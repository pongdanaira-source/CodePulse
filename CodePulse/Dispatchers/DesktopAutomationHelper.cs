using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace CodePulse.Dispatchers;

internal static class DesktopAutomationHelper
{
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const int MinimumActivationDelayMs = 15;
    private const int FocusPointDelayMs = 25;
    private const int TypeFallbackStepDelayMs = 15;
    private const int CompletionStepDelayMs = 10;

    public static Task<DesktopDispatchResult> DispatchToWindowAsync(
        string payload,
        int pasteDelayMs,
        bool enterAfterPaste,
        Func<WindowHandleInfo, bool> matchWindow,
        CancellationToken cancellationToken,
        IReadOnlyList<PointF>? focusPoints = null,
        bool typeFallback = false,
        bool moveCursorToBottomOnComplete = false,
        PointF? completionCursorPoint = null,
        bool minimizeWindowOnComplete = false,
        bool dryRun = false,
        Func<WindowHandleInfo, bool>? verificationPredicate = null)
    {
        var completionSource = new TaskCompletionSource<DesktopDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            ClipboardBackup? clipboardBackup = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var window = FindWindow(matchWindow);
                if (window is null)
                {
                    completionSource.TrySetResult(new DesktopDispatchResult
                    {
                        Success = false,
                        WindowFound = false
                    });
                    return;
                }

                if (verificationPredicate is not null && !verificationPredicate(window.Value))
                {
                    completionSource.TrySetResult(new DesktopDispatchResult
                    {
                        Success = false,
                        WindowFound = true,
                        WindowTitle = window.Value.Title,
                        WindowProcessName = window.Value.ProcessName,
                        VerificationSkipped = true
                    });
                    return;
                }

                if (dryRun)
                {
                    completionSource.TrySetResult(new DesktopDispatchResult
                    {
                        Success = true,
                        WindowFound = true,
                        WindowTitle = window.Value.Title,
                        WindowProcessName = window.Value.ProcessName,
                        DryRun = true
                    });
                    return;
                }

                ActivateWindow(window.Value.Handle);
                Thread.Sleep(Math.Max(MinimumActivationDelayMs, pasteDelayMs));
                cancellationToken.ThrowIfCancellationRequested();

                clipboardBackup = TryBackupClipboard();

                if (focusPoints is not null)
                {
                    foreach (var focusPoint in focusPoints)
                    {
                        ClickInsideWindow(window.Value.Handle, focusPoint);
                        Thread.Sleep(FocusPointDelayMs);
                    }
                }

                SetClipboardText(payload.Trim());
                SendKeys.SendWait("^v");

                if (typeFallback)
                {
                    Thread.Sleep(TypeFallbackStepDelayMs);
                    SendKeys.SendWait("^a");
                    Thread.Sleep(TypeFallbackStepDelayMs);
                    SendKeys.SendWait(EscapeSendKeys(payload.Trim()));
                }

                if (enterAfterPaste)
                {
                    Thread.Sleep(TypeFallbackStepDelayMs);
                    SendKeys.SendWait("{ENTER}");
                }

                if (moveCursorToBottomOnComplete)
                {
                    Thread.Sleep(CompletionStepDelayMs);
                    MoveCursorInsideWindow(window.Value.Handle, completionCursorPoint ?? new PointF(0.90f, 0.985f));
                }

                if (minimizeWindowOnComplete)
                {
                    Thread.Sleep(CompletionStepDelayMs);
                    ShowWindow(window.Value.Handle, 6);
                }

                completionSource.TrySetResult(new DesktopDispatchResult
                {
                    Success = true,
                    WindowFound = true,
                    WindowTitle = window.Value.Title,
                    WindowProcessName = window.Value.ProcessName
                });
            }
            catch (OperationCanceledException)
            {
                completionSource.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completionSource.TrySetResult(new DesktopDispatchResult
                {
                    Success = false,
                    WindowFound = true,
                    ErrorMessage = ex.Message
                });
            }
            finally
            {
                if (clipboardBackup.HasValue)
                {
                    RestoreClipboard(clipboardBackup.Value);
                }
            }
        })
        {
            IsBackground = true,
            Name = "CodePulseDesktopDispatch"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completionSource.Task;
    }

    public static Task<DesktopDispatchResult> DispatchToHandleAsync(
        string payload,
        int pasteDelayMs,
        bool enterAfterPaste,
        WindowHandleInfo window,
        CancellationToken cancellationToken,
        IReadOnlyList<PointF>? focusPoints = null,
        bool typeFallback = false,
        bool moveCursorToBottomOnComplete = false,
        PointF? completionCursorPoint = null,
        bool minimizeWindowOnComplete = false,
        bool dryRun = false)
    {
        return DispatchToWindowAsync(
            payload,
            pasteDelayMs,
            enterAfterPaste,
            candidate => candidate.Handle == window.Handle,
            cancellationToken,
            focusPoints,
            typeFallback,
            moveCursorToBottomOnComplete,
            completionCursorPoint,
            minimizeWindowOnComplete,
            dryRun);
    }

    public static bool WindowContainsText(IntPtr handle, string keyword, int maxElements = 250)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return true;
        }

        try
        {
            var root = AutomationElement.FromHandle(handle);
            if (root is null)
            {
                return false;
            }

            var walker = TreeWalker.ControlViewWalker;
            var queue = new Queue<AutomationElement>();
            queue.Enqueue(root);
            var visited = 0;

            while (queue.Count > 0 && visited++ < maxElements)
            {
                var element = queue.Dequeue();
                if (ContainsKeyword(element.Current.Name, keyword) ||
                    ContainsKeyword(element.Current.AutomationId, keyword) ||
                    ContainsKeyword(element.Current.HelpText, keyword))
                {
                    return true;
                }

                var child = walker.GetFirstChild(element);
                while (child is not null)
                {
                    queue.Enqueue(child);
                    child = walker.GetNextSibling(child);
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    public static bool TryGetBrowserAddress(WindowHandleInfo window, out string address)
    {
        address = string.Empty;
        if (!IsBrowserProcess(window.ProcessName))
        {
            return false;
        }

        try
        {
            var root = AutomationElement.FromHandle(window.Handle);
            if (root is null)
            {
                return false;
            }

            var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            foreach (AutomationElement edit in edits)
            {
                if (!edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ||
                    pattern is not ValuePattern valuePattern)
                {
                    continue;
                }

                var value = valuePattern.Current.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(value) && IsLikelyWebAddress(value))
                {
                    address = value;
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static WindowHandleInfo? FindWindow(Func<WindowHandleInfo, bool> matchWindow)
    {
        WindowHandleInfo? result = null;

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
            {
                return true;
            }

            var title = GetWindowTitle(handle);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            GetWindowThreadProcessId(handle, out var processId);
            string processName;
            try
            {
                processName = Process.GetProcessById((int)processId).ProcessName;
            }
            catch
            {
                processName = string.Empty;
            }

            var info = new WindowHandleInfo(handle, title, processName);
            if (!matchWindow(info))
            {
                return true;
            }

            result = info;
            return false;
        }, IntPtr.Zero);

        return result;
    }

    private static bool ContainsKeyword(string? value, string keyword)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBrowserProcess(string processName)
    {
        return processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("opera", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyWebAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("facebook.com/messages/t/", StringComparison.OrdinalIgnoreCase);
    }

    private static void ActivateWindow(IntPtr handle)
    {
        if (IsIconic(handle))
        {
            ShowWindow(handle, 9);
        }
        else
        {
            ShowWindow(handle, 5);
        }

        SetForegroundWindow(handle);
    }

    private static void ClickInsideWindow(IntPtr handle, PointF ratio)
    {
        if (!TryGetClientScreenRect(handle, out var rect))
        {
            return;
        }

        GetCursorPos(out var originalPoint);

        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        var x = rect.Left + (int)(width * ratio.X);
        var y = rect.Top + (int)(height * ratio.Y);

        SetCursorPos(x, y);
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(CompletionStepDelayMs);
        SetCursorPos(originalPoint.X, originalPoint.Y);
    }

    private static void MoveCursorInsideWindow(IntPtr handle, PointF ratio)
    {
        if (!TryGetClientScreenRect(handle, out var rect))
        {
            return;
        }

        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        var x = rect.Left + (int)(width * ratio.X);
        var y = rect.Top + (int)(height * ratio.Y);
        SetCursorPos(x, y);
    }

    private static bool TryGetClientScreenRect(IntPtr handle, out Rect rect)
    {
        rect = default;
        if (!GetClientRect(handle, out var clientRect))
        {
            return false;
        }

        var topLeft = new WinPoint { X = clientRect.Left, Y = clientRect.Top };
        var bottomRight = new WinPoint { X = clientRect.Right, Y = clientRect.Bottom };

        if (!ClientToScreen(handle, ref topLeft) || !ClientToScreen(handle, ref bottomRight))
        {
            return false;
        }

        rect = new Rect
        {
            Left = topLeft.X,
            Top = topLeft.Y,
            Right = bottomRight.X,
            Bottom = bottomRight.Y
        };
        return true;
    }

    private static void SetClipboardText(string text)
    {
        Clipboard.Clear();
        Clipboard.SetText(text);
    }

    private static ClipboardBackup TryBackupClipboard()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                return new ClipboardBackup(true, Clipboard.GetText());
            }
        }
        catch
        {
            // Ignore clipboard access errors.
        }

        return new ClipboardBackup(false, string.Empty);
    }

    private static void RestoreClipboard(ClipboardBackup backup)
    {
        try
        {
            if (!backup.HasText)
            {
                return;
            }

            Clipboard.Clear();
            Clipboard.SetText(backup.Text);
        }
        catch
        {
            // Ignore clipboard restore errors.
        }
    }

    private static string EscapeSendKeys(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '+' or '^' or '%' or '~' or '(' or ')' or '[' or ']' or '{' or '}')
            {
                builder.Append('{').Append(character).Append('}');
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref WinPoint lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out WinPoint lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint
    {
        public int X;
        public int Y;
    }

    private readonly record struct ClipboardBackup(bool HasText, string Text);
}

public readonly record struct WindowHandleInfo(IntPtr Handle, string Title, string ProcessName);
