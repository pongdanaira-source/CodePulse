using System.Collections.Specialized;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CodePulse.Wpf.Windows;
using CodePulse.Wpf.ViewModels;

namespace CodePulse.Wpf;

public partial class MainWindow : Window
{
    private static readonly TimeSpan CaptureMinimizeSettleDelay = TimeSpan.FromMilliseconds(180);
    private const double HeaderStackBreakpoint = 1280;
    private const double MainStackBreakpoint = 1200;
    private const double CompactBreakpoint = 880;
    private readonly MainShellViewModel _viewModel = new();
    private readonly DispatcherTimer _scanPreviewMonitorTimer;
    private bool _allowExit;
    private bool _hasResponsiveLayoutApplied;
    private bool _isHeaderStacked;
    private bool _isMainStacked;
    private bool _isCompactLayout;
    private QuickCaptureLauncherWindow? _quickCaptureLauncherWindow;
    private bool _quickCaptureLauncherRestoreMainWindowWhenClosed;
    private CommentScannerWindow? _commentScannerWindow;
    private LogViewWindow? _logViewWindow;
    private ScanRegionPreviewWindow? _scanRegionPreviewWindow;
    private Guid? _scanRegionPreviewChannelId;

    public MainWindow()
    {
        InitializeComponent();
        Title = BuildWindowTitle();
        DataContext = _viewModel;
        ((INotifyCollectionChanged)_viewModel.LogEntries).CollectionChanged += HandleLogEntriesChanged;
        _scanPreviewMonitorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _scanPreviewMonitorTimer.Tick += ScanPreviewMonitorTimer_OnTick;
        Loaded += (_, _) => ApplyResponsiveLayout();
    }

    private static string BuildWindowTitle()
    {
        var assembly = typeof(MainWindow).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        var version = informationalVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            version = assembly.GetName().Version?.ToString();
        }

        var metadataSeparatorIndex = version?.IndexOf('+') ?? -1;
        if (metadataSeparatorIndex > 0)
        {
            version = version![..metadataSeparatorIndex];
        }

        return string.IsNullOrWhiteSpace(version)
            ? "CodePulse Next"
            : $"CodePulse Next v{version}";
    }

    private void MainWindow_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout();
    }

    private void ShellScrollViewer_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollShellPageIfStacked(e);
    }

    private void PageScrollChild_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollShellPageIfStacked(e);
    }

    private void ScrollShellPageIfStacked(MouseWheelEventArgs e)
    {
        if (!_isMainStacked)
        {
            return;
        }

        e.Handled = true;
        ShellScrollViewer.ScrollToVerticalOffset(ShellScrollViewer.VerticalOffset - e.Delta);
    }

    private void ApplyResponsiveLayout()
    {
        if (!IsInitialized || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var width = ActualWidth;
        var compactLayout = width < CompactBreakpoint;

        OuterChrome.Padding = compactLayout ? new Thickness(10) : new Thickness(18);
        HeroPanel.Padding = compactLayout ? new Thickness(14) : new Thickness(16);

        var chromeWidth = OuterChrome.ActualWidth > 0
            ? OuterChrome.ActualWidth
            : width;
        var availableContentWidth = Math.Max(
            320,
            chromeWidth - OuterChrome.Padding.Left - OuterChrome.Padding.Right);
        var headerStacked = availableContentWidth < HeaderStackBreakpoint;
        var mainStacked = availableContentWidth < MainStackBreakpoint;
        var shellWidthReserve = mainStacked ? SystemParameters.VerticalScrollBarWidth + 2 : 0;
        RootLayout.Width = Math.Max(320, availableContentWidth - shellWidthReserve);
        RootLayout.HorizontalAlignment = HorizontalAlignment.Stretch;
        SettingsPathTextBlock.Width = headerStacked
            ? Math.Max(220, availableContentWidth - 120)
            : 290;
        var headerContentWidth = Math.Max(
            280,
            availableContentWidth
            - HeroPanel.Padding.Left
            - HeroPanel.Padding.Right
            - 72);
        HeaderStatsPanel.Width = headerStacked ? headerContentWidth : double.NaN;
        HeaderActionsWrap.Width = headerStacked ? headerContentWidth : double.NaN;

        if (!_hasResponsiveLayoutApplied || _isHeaderStacked != headerStacked)
        {
            ApplyHeaderLayout(headerStacked);
            _isHeaderStacked = headerStacked;
        }

        if (!_hasResponsiveLayoutApplied || _isMainStacked != mainStacked)
        {
            ApplyMainContentLayout(mainStacked);
            _isMainStacked = mainStacked;
        }

        if (!_hasResponsiveLayoutApplied || _isCompactLayout != compactLayout)
        {
            ApplyCompactLayout(compactLayout);
            _isCompactLayout = compactLayout;
        }

        ApplyHeaderSummaryVisibility(headerStacked);
        _hasResponsiveLayoutApplied = true;
    }

    private void ApplyHeaderLayout(bool stacked)
    {
        if (stacked)
        {
            HeaderTitleColumn.Width = new GridLength(1, GridUnitType.Star);
            HeaderTitleGapColumn.Width = new GridLength(0);
            HeaderStatsColumn.Width = new GridLength(0);
            HeaderActionsGapColumn.Width = new GridLength(0);
            HeaderActionsColumn.Width = new GridLength(0);
            HeaderFirstGapRow.Height = new GridLength(10);
            HeaderStatsRow.Height = GridLength.Auto;
            HeaderSecondGapRow.Height = new GridLength(10);
            HeaderActionsRow.Height = GridLength.Auto;

            Grid.SetRow(HeaderTitlePanel, 0);
            Grid.SetColumn(HeaderTitlePanel, 0);
            Grid.SetColumnSpan(HeaderTitlePanel, 5);
            Grid.SetRow(HeaderStatsPanel, 2);
            Grid.SetColumn(HeaderStatsPanel, 0);
            Grid.SetColumnSpan(HeaderStatsPanel, 5);
            Grid.SetRow(HeaderActionsPanel, 4);
            Grid.SetColumn(HeaderActionsPanel, 0);
            Grid.SetColumnSpan(HeaderActionsPanel, 5);

            HeaderStatsPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            HeaderActionsPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            HeaderActionsWrap.HorizontalAlignment = HorizontalAlignment.Left;
            SettingsPathTextBlock.HorizontalAlignment = HorizontalAlignment.Left;
            StatsGrid.Columns = 2;
            StatsGrid.Rows = 2;
            return;
        }

        HeaderTitleColumn.Width = GridLength.Auto;
        HeaderTitleGapColumn.Width = new GridLength(18);
        HeaderStatsColumn.Width = new GridLength(1, GridUnitType.Star);
        HeaderActionsGapColumn.Width = new GridLength(18);
        HeaderActionsColumn.Width = GridLength.Auto;
        HeaderFirstGapRow.Height = new GridLength(0);
        HeaderStatsRow.Height = new GridLength(0);
        HeaderSecondGapRow.Height = new GridLength(0);
        HeaderActionsRow.Height = new GridLength(0);

        Grid.SetRow(HeaderTitlePanel, 0);
        Grid.SetColumn(HeaderTitlePanel, 0);
        Grid.SetColumnSpan(HeaderTitlePanel, 1);
        Grid.SetRow(HeaderStatsPanel, 0);
        Grid.SetColumn(HeaderStatsPanel, 2);
        Grid.SetColumnSpan(HeaderStatsPanel, 1);
        Grid.SetRow(HeaderActionsPanel, 0);
        Grid.SetColumn(HeaderActionsPanel, 4);
        Grid.SetColumnSpan(HeaderActionsPanel, 1);

        HeaderStatsPanel.HorizontalAlignment = HorizontalAlignment.Center;
        HeaderActionsPanel.HorizontalAlignment = HorizontalAlignment.Right;
        HeaderActionsWrap.HorizontalAlignment = HorizontalAlignment.Right;
        SettingsPathTextBlock.HorizontalAlignment = HorizontalAlignment.Right;
        StatsGrid.Columns = 4;
        StatsGrid.Rows = 1;
    }

    private void ApplyMainContentLayout(bool stacked)
    {
        if (stacked)
        {
            ShellScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            MainContentScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            ScrollViewer.SetVerticalScrollBarVisibility(ChannelListView, ScrollBarVisibility.Disabled);
            ScrollViewer.SetCanContentScroll(ChannelListView, false);
            RootMainRow.Height = GridLength.Auto;
            MainLeftColumn.Width = new GridLength(1, GridUnitType.Star);
            MainGapColumn.Width = new GridLength(0);
            MainRightColumn.Width = new GridLength(0);
            MainTopRow.Height = GridLength.Auto;
            MainStackGapRow.Height = new GridLength(14);
            MainBottomRow.Height = GridLength.Auto;
            ChannelsListRow.Height = GridLength.Auto;
            SelectedChannelAvailabilityTextBlock.Visibility = Visibility.Collapsed;
            SelectedChannelDetailsGrid.Visibility = Visibility.Collapsed;
            SideSecondGapRow.Height = new GridLength(14);
            SideLogRow.Height = GridLength.Auto;
            LiveLogPanel.Visibility = Visibility.Collapsed;
            LogViewShortcutPanel.Visibility = Visibility.Visible;
            LogListBox.MaxHeight = 280;
            ApplyActionButtonLayout(stacked: true);

            Grid.SetRow(ChannelsPanel, 0);
            Grid.SetColumn(ChannelsPanel, 0);
            Grid.SetColumnSpan(ChannelsPanel, 1);
            Grid.SetRow(SidePanelGrid, 2);
            Grid.SetColumn(SidePanelGrid, 0);
            Grid.SetColumnSpan(SidePanelGrid, 1);
            ApplyChannelHeaderLayout(stacked: true);
            return;
        }

        ShellScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        MainContentScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        ScrollViewer.SetVerticalScrollBarVisibility(ChannelListView, ScrollBarVisibility.Auto);
        ScrollViewer.SetCanContentScroll(ChannelListView, true);
        RootMainRow.Height = new GridLength(1, GridUnitType.Star);
        MainLeftColumn.Width = new GridLength(2, GridUnitType.Star);
        MainGapColumn.Width = new GridLength(16);
        MainRightColumn.Width = new GridLength(1.08, GridUnitType.Star);
        MainTopRow.Height = new GridLength(1, GridUnitType.Star);
        MainStackGapRow.Height = new GridLength(0);
        MainBottomRow.Height = new GridLength(0);
        ChannelsListRow.Height = new GridLength(1, GridUnitType.Star);
        SelectedChannelAvailabilityTextBlock.Visibility = Visibility.Visible;
        SelectedChannelDetailsGrid.Visibility = Visibility.Visible;
        SideSecondGapRow.Height = new GridLength(14);
        SideLogRow.Height = new GridLength(1, GridUnitType.Star);
        LiveLogPanel.Visibility = Visibility.Visible;
        LogViewShortcutPanel.Visibility = Visibility.Collapsed;
        LogListBox.MaxHeight = double.PositiveInfinity;
        ApplyActionButtonLayout(stacked: false);

        Grid.SetRow(ChannelsPanel, 0);
        Grid.SetColumn(ChannelsPanel, 0);
        Grid.SetColumnSpan(ChannelsPanel, 1);
        Grid.SetRow(SidePanelGrid, 0);
        Grid.SetColumn(SidePanelGrid, 2);
        Grid.SetColumnSpan(SidePanelGrid, 1);
        ApplyChannelHeaderLayout(stacked: false);
    }

    private void ApplyChannelHeaderLayout(bool stacked)
    {
        if (stacked)
        {
            ChannelHeaderActionsColumn.Width = new GridLength(0);
            ChannelHeaderGapRow.Height = new GridLength(8);
            ChannelHeaderActionsRow.Height = GridLength.Auto;
            Grid.SetColumnSpan(ChannelHeaderTitlePanel, 2);
            Grid.SetRow(ChannelHeaderActionsPanel, 2);
            Grid.SetColumn(ChannelHeaderActionsPanel, 0);
            Grid.SetColumnSpan(ChannelHeaderActionsPanel, 2);
            ChannelHeaderActionsPanel.HorizontalAlignment = HorizontalAlignment.Left;
            return;
        }

        ChannelHeaderActionsColumn.Width = GridLength.Auto;
        ChannelHeaderGapRow.Height = new GridLength(0);
        ChannelHeaderActionsRow.Height = new GridLength(0);
        Grid.SetColumnSpan(ChannelHeaderTitlePanel, 1);
        Grid.SetRow(ChannelHeaderActionsPanel, 0);
        Grid.SetColumn(ChannelHeaderActionsPanel, 1);
        Grid.SetColumnSpan(ChannelHeaderActionsPanel, 1);
        ChannelHeaderActionsPanel.HorizontalAlignment = HorizontalAlignment.Right;
    }

    private void ApplyCompactLayout(bool compact)
    {
        if (_isMainStacked)
        {
            ApplyActionButtonLayout(stacked: true);
        }
    }

    private void ApplyHeaderSummaryVisibility(bool hidden)
    {
        HeaderStatsPanel.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;

        if (!_isHeaderStacked)
        {
            return;
        }

        HeaderFirstGapRow.Height = hidden ? new GridLength(0) : new GridLength(10);
        HeaderStatsRow.Height = hidden ? new GridLength(0) : GridLength.Auto;
        HeaderSecondGapRow.Height = new GridLength(10);
    }

    private void ApplyActionButtonLayout(bool stacked)
    {
        ActionsGrid.Columns = stacked ? 1 : 2;
        ActionsGrid.Width = double.NaN;

        var desktopMargins = new[]
        {
            new Thickness(0, 0, 8, 8),
            new Thickness(8, 0, 0, 8),
            new Thickness(0, 8, 8, 0),
            new Thickness(8, 8, 0, 0),
            new Thickness(0, 8, 8, 0),
            new Thickness(8, 8, 0, 0)
        };

        var index = 0;
        foreach (var child in ActionsGrid.Children)
        {
            if (child is not Button button)
            {
                continue;
            }

            button.Margin = stacked
                ? new Thickness(0, 0, 0, 10)
                : desktopMargins[Math.Min(index, desktopMargins.Length - 1)];
            index++;
        }
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
        ShowSettingsWindow();
    }

    public void ShowSettingsWindow()
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
        { };

        if (IsVisible)
        {
            dialog.Owner = this;
        }

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

    private void OpenLogViewButton_OnClick(object sender, RoutedEventArgs e)
    {
        ShowLogViewWindow();
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

    public IReadOnlyList<TrayWatchChannelInfo> GetTrayWatchChannels()
    {
        return _viewModel.GetTrayWatchChannels();
    }

    public async Task StartWatchFromTrayAsync(Guid channelId)
    {
        await _viewModel.StartWatchAsync(channelId);
    }

    public void StopWatchFromTray(Guid channelId)
    {
        _viewModel.StopWatch(channelId);
    }

    public void StopAllChatWatchesFromTray()
    {
        _viewModel.StopAllChatWatches();
    }

    public IReadOnlyList<TrayBoostChannelInfo> GetTrayBoostChannels()
    {
        return _viewModel.GetTrayBoostChannels();
    }

    public void ToggleBoostFromTray(Guid channelId)
    {
        _viewModel.ToggleBoost(channelId);
    }

    public void StopAllBoostsFromTray()
    {
        _viewModel.StopAllBoostsFromTray();
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
