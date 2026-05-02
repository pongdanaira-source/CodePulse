using System.Windows;
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

    private void LogViewTrayMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current.MainWindow is MainWindow mainWindow)
        {
            mainWindow.ShowLogViewWindow();
        }
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
