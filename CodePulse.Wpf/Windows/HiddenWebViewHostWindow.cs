using System.IO;
using System.Windows;
using CodePulse.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CodePulse.Wpf.Windows;

internal sealed class HiddenWebViewHostWindow : Window, IHiddenWebViewHost
{
    private static int _sequence;
    private readonly WebView2 _webView = new();
    private readonly SemaphoreSlim _navigationSemaphore = new(1, 1);
    private TaskCompletionSource<bool>? _readyCompletionSource;

    public HiddenWebViewHostWindow()
    {
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Left = -32000;
        Top = -32000;
        Width = 900;
        Height = 700;
        Opacity = 0.01d;
        Background = System.Windows.Media.Brushes.Black;
        Content = _webView;
    }

    public event Action<string>? ObserverMessageReceived;
    public event Action<string>? NavigationFailed;
    public event Action<string>? BrowserProcessFailed;
    public event Action<string>? DebugLogEmitted;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        DebugLogEmitted?.Invoke("host: initialize requested");
        await InvokeOnUiThreadAsync(async () =>
        {
            if (!IsVisible)
            {
                DebugLogEmitted?.Invoke("host: showing hidden window");
                Show();
            }

            if (_webView.CoreWebView2 is not null)
            {
                DebugLogEmitted?.Invoke("host: WebView2 already initialized");
                return;
            }

            DebugLogEmitted?.Invoke("host: EnsureCoreWebView2Async");
            await _webView.EnsureCoreWebView2Async();
            if (_webView.CoreWebView2 is null)
            {
                throw new InvalidOperationException("Cannot initialize WebView2.");
            }

            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            _webView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Image);
            _webView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Media);
            _webView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Font);
            _webView.CoreWebView2.WebResourceRequested += (_, args) =>
            {
                args.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    Stream.Null,
                    204,
                    "No Content",
                    string.Empty);
            };
            _webView.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                try
                {
                    var message = args.TryGetWebMessageAsString();
                    if (message.Contains("\"type\":\"ready\"", StringComparison.Ordinal))
                    {
                        DebugLogEmitted?.Invoke("host: observer ready received");
                        _readyCompletionSource?.TrySetResult(true);
                    }

                    ObserverMessageReceived?.Invoke(message);
                }
                catch
                {
                    // Ignore malformed observer messages.
                }
            };
            _webView.CoreWebView2.ProcessFailed += (_, args) =>
            {
                DebugLogEmitted?.Invoke($"host: process failed {args.ProcessFailedKind}");
                BrowserProcessFailed?.Invoke(args.ProcessFailedKind.ToString());
            };
            _webView.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess)
                {
                    DebugLogEmitted?.Invoke("host: navigation completed successfully");
                    return;
                }

                DebugLogEmitted?.Invoke($"host: navigation failed {args.WebErrorStatus}");
                NavigationFailed?.Invoke(args.WebErrorStatus.ToString());
                _readyCompletionSource?.TrySetResult(false);
            };

            DebugLogEmitted?.Invoke("host: injecting observer script");
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(LiveChatObserverScript.Get());
            DebugLogEmitted?.Invoke("host: initialize completed");
        });
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<bool> NavigateAndWaitUntilReadyAsync(string chatLink, CancellationToken cancellationToken)
    {
        DebugLogEmitted?.Invoke($"host: navigate requested {chatLink}");
        return await WaitForReadyAsync(
            cancellationToken,
            () =>
            {
                if (_webView.CoreWebView2 is null)
                {
                    throw new InvalidOperationException("WebView2 is not ready.");
                }

                _webView.CoreWebView2.Navigate(chatLink);
            });
    }

    public async Task<bool> ReloadAndWaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        DebugLogEmitted?.Invoke("host: reload requested");
        return await WaitForReadyAsync(
            cancellationToken,
            () =>
            {
                if (_webView.CoreWebView2 is null)
                {
                    throw new InvalidOperationException("WebView2 is not ready.");
                }

                _webView.CoreWebView2.Reload();
            });
    }

    public string? DocumentTitle => Dispatcher.CheckAccess()
        ? _webView.CoreWebView2?.DocumentTitle
        : Dispatcher.Invoke(() => _webView.CoreWebView2?.DocumentTitle);

    public async Task SetLowLatencyModeAsync(bool enabled, CancellationToken cancellationToken)
    {
        await InvokeOnUiThreadAsync(async () =>
        {
            if (_webView.CoreWebView2 is null)
            {
                return;
            }

            var enabledLiteral = enabled ? "true" : "false";
            await _webView.CoreWebView2.ExecuteScriptAsync(
                $"window.__codePulseSetLowLatencyMode?.({enabledLiteral});");
        });
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<bool> WaitForReadyAsync(CancellationToken cancellationToken, Action navigationAction)
    {
        await _navigationSemaphore.WaitAsync(cancellationToken);
        try
        {
            var sequence = Interlocked.Increment(ref _sequence);
            var readyCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _readyCompletionSource = readyCompletionSource;
            using var registration = cancellationToken.Register(() => readyCompletionSource.TrySetCanceled(cancellationToken));

            DebugLogEmitted?.Invoke($"host: wait-ready #{sequence} dispatching navigation");
            await InvokeOnUiThreadAsync(() =>
            {
                navigationAction();
                return Task.CompletedTask;
            });
            DebugLogEmitted?.Invoke($"host: wait-ready #{sequence} navigation dispatched");

            var completedTask = await Task.WhenAny(
                readyCompletionSource.Task,
                Task.Delay(TimeSpan.FromSeconds(20), cancellationToken));

            if (completedTask != readyCompletionSource.Task)
            {
                DebugLogEmitted?.Invoke($"host: wait-ready #{sequence} timed out");
                return false;
            }

            var ready = await readyCompletionSource.Task;
            DebugLogEmitted?.Invoke($"host: wait-ready #{sequence} completed ready={ready}");
            return ready;
        }
        finally
        {
            _readyCompletionSource = null;
            _navigationSemaphore.Release();
        }
    }

    public void DisposeHost()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DisposeHost);
            return;
        }

        try
        {
            DebugLogEmitted?.Invoke("host: disposing hidden window");
            Close();
        }
        catch
        {
            // Ignore disposal errors.
        }
    }

    private Task InvokeOnUiThreadAsync(Func<Task> action)
    {
        if (Dispatcher.CheckAccess())
        {
            return action();
        }

        return Dispatcher.InvokeAsync(action).Task.Unwrap();
    }
}
