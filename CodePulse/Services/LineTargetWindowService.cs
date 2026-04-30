using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using CodePulse.Dispatchers;

namespace CodePulse.Services;

public sealed class LineTargetWindowService
{
    private readonly object _sync = new();
    private WindowHandleInfo? _selectedWindow;

    public WindowHandleInfo? SelectedWindow
    {
        get
        {
            lock (_sync)
            {
                return _selectedWindow;
            }
        }
    }

    public string SelectedWindowText
    {
        get
        {
            var window = SelectedWindow;
            return window is null
                ? "Not selected"
                : $"{window.Value.Title} ({window.Value.ProcessName})";
        }
    }

    public IReadOnlyList<WindowHandleInfo> GetLineWindows()
    {
        var windows = new List<WindowHandleInfo>();

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
            {
                return true;
            }

            var title = GetBestWindowTitle(handle);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            GetWindowThreadProcessId(handle, out var processId);
            var processName = GetProcessName(processId);
            if (!processName.Equals("LINE", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            windows.Add(new WindowHandleInfo(handle, title, processName));
            return true;
        }, IntPtr.Zero);

        return windows
            .OrderBy(static window => window.Title.Equals("LINE", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(static window => window.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Select(WindowHandleInfo window)
    {
        lock (_sync)
        {
            _selectedWindow = window;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _selectedWindow = null;
        }
    }

    public bool TryGetSelectedLiveWindow(out WindowHandleInfo window)
    {
        window = default;
        var selected = SelectedWindow;
        if (selected is null)
        {
            return false;
        }

        if (!IsWindow(selected.Value.Handle) || !IsWindowVisible(selected.Value.Handle))
        {
            Clear();
            return false;
        }

        var currentProcessName = GetWindowProcessName(selected.Value.Handle);
        if (!currentProcessName.Equals("LINE", StringComparison.OrdinalIgnoreCase))
        {
            Clear();
            return false;
        }

        var currentTitle = GetBestWindowTitle(selected.Value.Handle);
        window = new WindowHandleInfo(selected.Value.Handle, currentTitle, currentProcessName);
        return true;
    }

    private static string GetWindowProcessName(IntPtr handle)
    {
        GetWindowThreadProcessId(handle, out var processId);
        return GetProcessName(processId);
    }

    private static string GetProcessName(uint processId)
    {
        try
        {
            return Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            return string.Empty;
        }
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

    private static string GetBestWindowTitle(IntPtr handle)
    {
        var automationTitle = GetAutomationWindowTitle(handle);
        if (!string.IsNullOrWhiteSpace(automationTitle) && !LooksGarbled(automationTitle))
        {
            return automationTitle;
        }

        var win32Title = GetWindowTitle(handle);
        return !string.IsNullOrWhiteSpace(win32Title) ? win32Title : automationTitle;
    }

    private static string GetAutomationWindowTitle(IntPtr handle)
    {
        try
        {
            var element = AutomationElement.FromHandle(handle);
            return element?.Current.Name?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool LooksGarbled(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var questionMarks = value.Count(static character => character == '?');
        return questionMarks >= 3 && questionMarks >= value.Length / 3;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
