using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace CodePulse.Wpf;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Global\\CodePulse.Next.SingleInstance";
    private const string ShowMainWindowEventName = "Global\\CodePulse.Next.ShowMainWindow";

    private TaskbarIcon? _trayIcon;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showMainWindowEvent;
    private Thread? _showMainWindowListenerThread;
    private bool _ownsSingleInstance;
    private bool _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out _ownsSingleInstance);
        if (!_ownsSingleInstance)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        StartExistingInstanceSignalListener();
        base.OnStartup(e);
        _trayIcon = (TaskbarIcon?)FindResource("TrayIcon");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isExiting = true;
        _showMainWindowEvent?.Set();
        _showMainWindowEvent?.Dispose();
        _trayIcon?.Dispose();

        if (_ownsSingleInstance)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var showMainWindowEvent = EventWaitHandle.OpenExisting(ShowMainWindowEventName);
            showMainWindowEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first process may still be starting. The mutex is enough to prevent a duplicate.
        }
    }

    private void StartExistingInstanceSignalListener()
    {
        _showMainWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowMainWindowEventName);
        _showMainWindowListenerThread = new Thread(() =>
        {
            while (!_isExiting)
            {
                _showMainWindowEvent.WaitOne();
                if (_isExiting)
                {
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    if (Current.MainWindow is MainWindow mainWindow)
                    {
                        mainWindow.ShowFromTray();
                    }
                });
            }
        })
        {
            IsBackground = true,
            Name = "CodePulse single-instance listener"
        };
        _showMainWindowListenerThread.Start();
    }

    private async void QuickCaptureTrayMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current.MainWindow is MainWindow mainWindow)
        {
            await mainWindow.ShowQuickCaptureLauncherAsync(restoreMainWindowWhenFinished: false);
        }
    }

    private void CommentsTrayMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current.MainWindow is MainWindow mainWindow)
        {
            mainWindow.ShowCommentScannerWindow();
        }
    }

    private void SendCodeTrayMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current.MainWindow is MainWindow mainWindow)
        {
            mainWindow.ShowManualSendWindow();
        }
    }

    private void LogViewTrayMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current.MainWindow is MainWindow mainWindow)
        {
            mainWindow.ShowLogViewWindow();
        }
    }

    private void SettingsTrayMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current.MainWindow is MainWindow mainWindow)
        {
            mainWindow.ShowSettingsWindow();
        }
    }

    private void TrayContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu)
        {
            return;
        }

        var watchMenu = FindTaggedMenuItem(contextMenu, "WatchChat");
        if (watchMenu is null)
        {
            return;
        }

        BuildWatchChatTrayMenu(watchMenu);

        var boostMenu = FindTaggedMenuItem(contextMenu, "Boost");
        if (boostMenu is not null)
        {
            BuildBoostTrayMenu(boostMenu);
        }
    }

    private void BuildWatchChatTrayMenu(MenuItem watchMenu)
    {
        watchMenu.Items.Clear();

        if (Current.MainWindow is not MainWindow mainWindow)
        {
            watchMenu.Items.Add(new MenuItem
            {
                Header = "Main window is not ready",
                IsEnabled = false
            });
            return;
        }

        var channels = mainWindow.GetTrayWatchChannels();
        var activeCount = channels.Count(static channel => channel.IsWatching);

        watchMenu.Items.Add(new MenuItem
        {
            Header = $"Watching {activeCount}/{channels.Count}",
            IsEnabled = false
        });
        watchMenu.Items.Add(new Separator());

        if (channels.Count == 0)
        {
            watchMenu.Items.Add(new MenuItem
            {
                Header = "No watch channels",
                IsEnabled = false
            });
            return;
        }

        foreach (var channel in channels)
        {
            var channelInfo = channel;
            var action = channelInfo.IsWatching ? "Stop" : "Start";
            var status = channelInfo.IsWatching
                ? NormalizeTrayStatus(channelInfo.StatusText, "Watching")
                : channelInfo.Enabled ? "Ready" : "Disabled";
            var item = new MenuItem
            {
                Header = $"{action} {channelInfo.Name} ({status})",
                IsEnabled = channelInfo.Enabled || channelInfo.IsWatching
            };

            if (channelInfo.IsWatching)
            {
                item.Click += (_, _) =>
                {
                    if (Current.MainWindow is MainWindow currentMainWindow)
                    {
                        currentMainWindow.StopWatchFromTray(channelInfo.Id);
                    }
                };
            }
            else
            {
                item.Click += async (_, _) =>
                {
                    if (Current.MainWindow is MainWindow currentMainWindow)
                    {
                        await currentMainWindow.StartWatchFromTrayAsync(channelInfo.Id);
                    }
                };
            }

            watchMenu.Items.Add(item);
        }

        watchMenu.Items.Add(new Separator());
        var stopAllItem = new MenuItem
        {
            Header = "Stop all watch",
            IsEnabled = activeCount > 0
        };
        stopAllItem.Click += (_, _) =>
        {
            if (Current.MainWindow is MainWindow currentMainWindow)
            {
                currentMainWindow.StopAllChatWatchesFromTray();
            }
        };
        watchMenu.Items.Add(stopAllItem);
    }

    private void BuildBoostTrayMenu(MenuItem boostMenu)
    {
        boostMenu.Items.Clear();

        if (Current.MainWindow is not MainWindow mainWindow)
        {
            boostMenu.Items.Add(new MenuItem
            {
                Header = "Main window is not ready",
                IsEnabled = false
            });
            return;
        }

        var channels = mainWindow.GetTrayBoostChannels();
        var activeCount = channels.Count(static channel => channel.IsBoosting);

        boostMenu.Items.Add(new MenuItem
        {
            Header = $"Boosting {activeCount}/{channels.Count}",
            IsEnabled = false
        });
        boostMenu.Items.Add(new Separator());

        if (channels.Count == 0)
        {
            boostMenu.Items.Add(new MenuItem
            {
                Header = "No active watch channels",
                IsEnabled = false
            });
            return;
        }

        foreach (var channel in channels)
        {
            var channelInfo = channel;
            var action = channelInfo.IsBoosting ? "Stop Boost" : "Start Boost";
            var item = new MenuItem
            {
                Header = $"{action} {channelInfo.Name} ({NormalizeTrayStatus(channelInfo.StatusText, "Ready")})",
                IsEnabled = channelInfo.Enabled || channelInfo.IsBoosting
            };
            item.Click += (_, _) =>
            {
                if (Current.MainWindow is MainWindow currentMainWindow)
                {
                    currentMainWindow.ToggleBoostFromTray(channelInfo.Id);
                }
            };

            boostMenu.Items.Add(item);
        }

        boostMenu.Items.Add(new Separator());
        var stopAllItem = new MenuItem
        {
            Header = "Stop all boost",
            IsEnabled = activeCount > 0
        };
        stopAllItem.Click += (_, _) =>
        {
            if (Current.MainWindow is MainWindow currentMainWindow)
            {
                currentMainWindow.StopAllBoostsFromTray();
            }
        };
        boostMenu.Items.Add(stopAllItem);
    }

    private static MenuItem? FindTaggedMenuItem(ItemsControl parent, string tag)
    {
        foreach (var item in parent.Items)
        {
            if (item is MenuItem menuItem)
            {
                if (menuItem.Tag is string itemTag &&
                    string.Equals(itemTag, tag, StringComparison.Ordinal))
                {
                    return menuItem;
                }

                var child = FindTaggedMenuItem(menuItem, tag);
                if (child is not null)
                {
                    return child;
                }
            }
        }

        return null;
    }

    private static string NormalizeTrayStatus(string? statusText, string fallback)
    {
        var status = statusText?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(status) ? fallback : status;
    }

    private void ShowMainWindowTrayMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current.MainWindow is MainWindow mainWindow)
        {
            mainWindow.ShowFromTray();
        }
    }

    private void ExitTrayMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current.MainWindow is MainWindow mainWindow)
        {
            mainWindow.RequestExit();
        }

        Shutdown();
    }

    private void TrayIcon_OnTrayLeftMouseUp(object sender, RoutedEventArgs e)
    {
        if (Current.MainWindow is MainWindow mainWindow)
        {
            mainWindow.ShowFromTray();
        }
    }
}
