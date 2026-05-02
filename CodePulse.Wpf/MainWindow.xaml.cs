using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using CodePulse.Wpf.Windows;
using CodePulse.Wpf.ViewModels;

namespace CodePulse.Wpf;

public partial class MainWindow : Window
{
    private static readonly TimeSpan CaptureMinimizeSettleDelay = TimeSpan.FromMilliseconds(180);
    private readonly MainShellViewModel _viewModel = new();
    private readonly DispatcherTimer _scanPreviewMonitorTimer;
    private bool _allowExit;
    private QuickCaptureLauncherWindow? _quickCaptureLauncherWindow;
    private bool _quickCaptureLauncherRestoreMainWindowWhenClosed;
    private CommentScannerWindow? _commentScannerWindow;
    private LogViewWindow? _logViewWindow;
    private ScanRegionPreviewWindow? _scanRegionPreviewWindow;
    private Guid? _scanRegionPreviewChannelId;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        ((INotifyCollectionChanged)_viewModel.LogEntries).CollectionChanged += HandleLogEntriesChanged;
        _scanPreviewMonitorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _scanPreviewMonitorTimer.Tick += ScanPreviewMonitorTimer_OnTick;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowExit)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ((INotifyCollectionChanged)_viewModel.LogEntries).CollectionChanged -= HandleLogEntriesChanged;
        _scanPreviewMonitorTimer.Tick -= ScanPreviewMonitorTimer_OnTick;
        if (_logViewWindow is not null)
        {
            _logViewWindow.Closed -= LogViewWindow_OnClosed;
            _logViewWindow.Close();
            _logViewWindow = null;
        }

        HideScanRegionPreview();
        _viewModel.Shutdown();
        base.OnClosed(e);
    }

    private void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshView();
    }

    private void AddChannelButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ChannelEditorWindow(_viewModel.CreateNewChannelDraft())
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            _viewModel.SaveChannel(dialog.Result);
        }
    }

    private void EditChannelButton_OnClick(object sender, RoutedEventArgs e)
    {
        var draft = _viewModel.CreateSelectedChannelDraft();
        if (draft is null)
        {
            return;
        }

        var dialog = new ChannelEditorWindow(draft)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            _viewModel.SaveChannel(dialog.Result);
        }
    }

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(
            _viewModel.CreateSettingsDraft(),
            _viewModel.TestDispatchAsync,
            _viewModel.GetLineTargetWindows,
            _viewModel.SelectLineTargetWindow,
            _viewModel.ClearLineTargetWindow,
            () => _viewModel.LineTargetWindowText,
            _viewModel.GetApiUsageSnapshot,
            _viewModel.CheckYouTubeApiKeysNowAsync,
            _viewModel.ResetApiUsageCounters)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            _viewModel.SaveSettings(dialog.Result);
        }
    }

    private async void StartSelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        await _viewModel.StartSelectedWatchAsync();
    }

    private async void QuickCaptureButton_OnClick(object sender, RoutedEventArgs e)
    {
        await ShowQuickCaptureLauncherAsync(restoreMainWindowWhenFinished: true);
    }

    private void CommentScannerButton_OnClick(object sender, RoutedEventArgs e)
    {
        ShowCommentScannerWindow();
    }

    public void ShowCommentScannerWindow()
    {
        var channels = _viewModel.GetCommentScannerChannels();
        if (channels.Count == 0)
        {
            MessageBox.Show(
                "There are no enabled YouTube channel-id sources yet. Comment Scanner needs a channel with a UC... watch source.",
                "Comment scanner",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_commentScannerWindow is not null)
        {
            _commentScannerWindow.Activate();
            return;
        }

        _commentScannerWindow = new CommentScannerWindow(
            channels,
            _viewModel.SelectedChannel,
            _viewModel.IsCommentScannerRunning,
            _viewModel.GetLastCommentScannerVideoUrl,
            _viewModel.StartCommentScanner,
            _viewModel.StopCommentScanner);

        _commentScannerWindow.Closed += CommentScannerWindow_OnClosed;
        _commentScannerWindow.Show();
        _commentScannerWindow.Activate();
    }

    public async Task ShowQuickCaptureLauncherAsync(bool restoreMainWindowWhenFinished)
    {
        var availableChannels = _viewModel.GetQuickCaptureChannels();
        if (availableChannels.Count == 0)
        {
            MessageBox.Show(
                this,
                "There are no enabled capture channels yet. Quick Capture only shows channels without a chat link.",
                "Quick capture",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _quickCaptureLauncherRestoreMainWindowWhenClosed |= restoreMainWindowWhenFinished;
        if (_quickCaptureLauncherWindow is not null)
        {
            _quickCaptureLauncherWindow.Activate();
            return;
        }

        _quickCaptureLauncherWindow = new QuickCaptureLauncherWindow(availableChannels, _viewModel.SelectedChannel);
        _quickCaptureLauncherWindow.ActionRequested += QuickCaptureLauncherWindow_OnActionRequested;
        _quickCaptureLauncherWindow.Closed += QuickCaptureLauncherWindow_OnClosed;

        HideToTray();
        await Task.Delay(120);
        _quickCaptureLauncherWindow.Show();
        _quickCaptureLauncherWindow.Activate();
    }

    private void StopSelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel.StopSelectedWatch();
    }

    private async void CaptureSelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        var overlay = await ShowCaptureOverlayAsync(minimizeMainWindow: true);

        try
        {
            if (overlay.ShowDialog() != true || overlay.SelectedRegion is not System.Drawing.Rectangle selectedRegion)
            {
                return;
            }

            await _viewModel.RunSelectedCaptureAsync(selectedRegion);
        }
        finally
        {
            ShowFromTray();
        }
    }

    private async void StartSelectedOcrButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedChannel?.LastCaptureRegion?.IsValid != true)
        {
            var overlay = await ShowCaptureOverlayAsync(minimizeMainWindow: true);

            try
            {
                if (overlay.ShowDialog() != true || overlay.SelectedRegion is not System.Drawing.Rectangle selectedRegion)
                {
                    return;
                }

                _viewModel.SaveSelectedCaptureRegion(selectedRegion);
            }
            finally
            {
                ShowFromTray();
            }
        }

        var started = await _viewModel.StartSelectedOcrScanAsync();
        if (started && _viewModel.SelectedChannel?.LastCaptureRegion is { IsValid: true } region)
        {
            ShowScanRegionPreview(_viewModel.SelectedChannel.Name, _viewModel.SelectedChannel.Id, region);
        }
    }

    private void StopSelectedOcrButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel.StopSelectedOcrScan();
        HideScanRegionPreview();
    }

    private void StopAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel.StopAllWatches();
        HideScanRegionPreview();
    }

    private void BoostChannelButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CodePulse.Models.ChannelProfile channel })
        {
            _viewModel.ToggleBoost(channel);
        }
    }

    private void RemoveSelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedChannel is null)
        {
            return;
        }

        var confirmation = System.Windows.MessageBox.Show(
            this,
            $"Remove channel '{_viewModel.SelectedChannel.Name}'?",
            "Remove channel",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmation == MessageBoxResult.Yes)
        {
            _viewModel.RemoveSelectedChannel();
        }
    }

    private void CopyLogButton_OnClick(object sender, RoutedEventArgs e)
    {
        var logText = _viewModel.BuildVisibleLogText();
        if (string.IsNullOrWhiteSpace(logText))
        {
            MessageBox.Show(
                this,
                "There is no log content to copy yet.",
                "Copy log",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Clipboard.SetText(logText);
    }

    private void ClearLogButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearLogs();
    }

    private void ScrollLogToLatest()
    {
        if (_viewModel.LogEntries.Count == 0)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(
            () => LogListBox.ScrollIntoView(_viewModel.LogEntries[^1]),
            DispatcherPriority.Background);
    }

    private void HandleLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ScrollLogToLatest();
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void ShowLogViewWindow()
    {
        if (_logViewWindow is not null)
        {
            _logViewWindow.BringForward();
            return;
        }

        _logViewWindow = new LogViewWindow(_viewModel);
        _logViewWindow.Closed += LogViewWindow_OnClosed;
        _logViewWindow.Show();
        _logViewWindow.Activate();
    }

    public void HideToTray()
    {
        WindowState = WindowState.Minimized;
        Hide();
    }

    private async Task<ScreenCaptureOverlayWindow> ShowCaptureOverlayAsync(bool minimizeMainWindow)
    {
        if (minimizeMainWindow)
        {
            WindowState = WindowState.Minimized;
            await Task.Delay(CaptureMinimizeSettleDelay);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        return new ScreenCaptureOverlayWindow(null);
    }

    private async void QuickCaptureLauncherWindow_OnActionRequested(object? sender, QuickCaptureActionRequestedEventArgs e)
    {
        if (_quickCaptureLauncherWindow is null)
        {
            return;
        }

        if (e.Action == QuickCaptureAction.StopScan)
        {
            if (_viewModel.SelectedChannel?.Id != e.Channel.Id)
            {
                _viewModel.SelectedChannel = _viewModel.Channels.FirstOrDefault(channel => channel.Id == e.Channel.Id);
            }

            _viewModel.StopSelectedOcrScan();
            HideScanRegionPreview();
            _quickCaptureLauncherWindow.SetInteractionEnabled(true);
            return;
        }

        _quickCaptureLauncherWindow.SetInteractionEnabled(false);

        try
        {
            var overlay = await ShowCaptureOverlayAsync(minimizeMainWindow: false);
            if (overlay.ShowDialog() != true || overlay.SelectedRegion is not System.Drawing.Rectangle selectedRegion)
            {
                return;
            }

            if (_viewModel.SelectedChannel?.Id != e.Channel.Id)
            {
                _viewModel.SelectedChannel = _viewModel.Channels.FirstOrDefault(channel => channel.Id == e.Channel.Id);
            }

            switch (e.Action)
            {
                case QuickCaptureAction.Capture:
                    await _viewModel.RunCaptureAsync(e.Channel, selectedRegion, "quick capture launcher");
                    break;
                case QuickCaptureAction.Scan:
                    _viewModel.SaveSelectedCaptureRegion(selectedRegion);
                    var started = await _viewModel.StartSelectedOcrScanAsync();
                    if (started && _viewModel.SelectedChannel?.LastCaptureRegion is { IsValid: true } region)
                    {
                        ShowScanRegionPreview(_viewModel.SelectedChannel.Name, _viewModel.SelectedChannel.Id, region);
                    }
                    break;
            }
        }
        finally
        {
            _quickCaptureLauncherWindow?.SetInteractionEnabled(true);
            _quickCaptureLauncherWindow?.Activate();
        }
    }

    private void QuickCaptureLauncherWindow_OnClosed(object? sender, EventArgs e)
    {
        if (_quickCaptureLauncherWindow is not null)
        {
            _quickCaptureLauncherWindow.ActionRequested -= QuickCaptureLauncherWindow_OnActionRequested;
            _quickCaptureLauncherWindow.Closed -= QuickCaptureLauncherWindow_OnClosed;
            _quickCaptureLauncherWindow = null;
        }

        if (_quickCaptureLauncherRestoreMainWindowWhenClosed)
        {
            ShowFromTray();
        }

        _quickCaptureLauncherRestoreMainWindowWhenClosed = false;
    }

    private void CommentScannerWindow_OnClosed(object? sender, EventArgs e)
    {
        if (_commentScannerWindow is not null)
        {
            _commentScannerWindow.Closed -= CommentScannerWindow_OnClosed;
            _commentScannerWindow = null;
        }
    }

    private void LogViewWindow_OnClosed(object? sender, EventArgs e)
    {
        if (_logViewWindow is not null)
        {
            _logViewWindow.Closed -= LogViewWindow_OnClosed;
            _logViewWindow = null;
        }
    }

    private void ShowScanRegionPreview(string channelName, Guid channelId, CodePulse.Models.CaptureRegion region)
    {
        HideScanRegionPreview();

        _scanRegionPreviewChannelId = channelId;
        _scanRegionPreviewWindow = new ScanRegionPreviewWindow(channelName, region);
        _scanRegionPreviewWindow.Show();
        _scanPreviewMonitorTimer.Start();
    }

    private void HideScanRegionPreview()
    {
        _scanPreviewMonitorTimer.Stop();
        _scanRegionPreviewChannelId = null;

        if (_scanRegionPreviewWindow is null)
        {
            return;
        }

        _scanRegionPreviewWindow.Close();
        _scanRegionPreviewWindow = null;
    }

    private void ScanPreviewMonitorTimer_OnTick(object? sender, EventArgs e)
    {
        if (_scanRegionPreviewChannelId is null)
        {
            HideScanRegionPreview();
            return;
        }

        var channel = _viewModel.Channels.FirstOrDefault(item => item.Id == _scanRegionPreviewChannelId.Value);
        if (channel?.Status is not CodePulse.Enums.SessionState.OcrScanning and not CodePulse.Enums.SessionState.OcrCooldown)
        {
            HideScanRegionPreview();
        }
    }

    public void RequestExit()
    {
        _allowExit = true;
    }
}
