using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace CodePulse.Wpf;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _trayIcon = (TaskbarIcon?)FindResource("TrayIcon");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
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
