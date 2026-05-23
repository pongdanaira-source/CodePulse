using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CodePulse.Dispatchers;
using CodePulse.Enums;
using CodePulse.Integrations;
using CodePulse.Models;
using CodePulse.Services;
using CodePulse.Wpf.Windows;

namespace CodePulse.Wpf.ViewModels;

public sealed class MainShellViewModel : INotifyPropertyChanged
{
    private const int MaxVisibleLogEntries = 250;
    private const int MinimumManualMessageLength = 8;

    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly AppLogService _appLogService;
    private readonly CodeExtractorService _codeExtractorService;
    private readonly WatchCoordinator _watchCoordinator;
    private readonly WatchWorkflowService _watchWorkflowService;
    private readonly CaptureWorkflowService _captureWorkflowService;
    private readonly OcrScanWorkflowService _ocrScanWorkflowService;
    private readonly YouTubeCommentScannerService _commentScannerService;
    private readonly ApiUsageTracker _apiUsageTracker;
    private readonly DispatchService _dispatchService;
    private readonly LineTargetWindowService _lineTargetWindowService;
    private readonly FacebookDispatcher _facebookDispatcher;
    private readonly ObservableCollection<ChannelProfile> _channels = new();
    private readonly ObservableCollection<AppLogEntry> _logEntries = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _boostTokens = new();
    private readonly Dictionary<Guid, Guid> _activeCommentTimerByChannel = new();
    private readonly DispatcherTimer _commentTimerSchedulerTimer = new();
    private readonly object _boostSync = new();
    private readonly string _dryRunLogsRootPath;
    private ChannelProfile? _selectedChannel;

    public MainShellViewModel()
    {
        var runtimeDataRootPath = ResolveRuntimeDataRootPath();
        _settingsStore = new SettingsStore(runtimeDataRootPath);
        _settings = _settingsStore.Load();
        _appLogService = new AppLogService();
        _dryRunLogsRootPath = Path.Combine(_settingsStore.AppFolderPath, "logs", "dry-run");
        _appLogService.ConfigureDryRunSessionLogging(_settings.Dispatch.EnableDryRun, _dryRunLogsRootPath);

        var telegramBotClient = new TelegramBotClient();
        var codeExtractorService = new CodeExtractorService();
        _codeExtractorService = codeExtractorService;
        var dailyCodeHistoryService = new DailyCodeHistoryService(_settingsStore.AppFolderPath);
        var duplicateGuard = new ChannelDuplicateGuard();
        var apiUsageTracker = new ApiUsageTracker(Path.Combine(_settingsStore.AppFolderPath, "usage-counters.json"));
        _apiUsageTracker = apiUsageTracker;
        var screenCaptureService = new ScreenCaptureService();
        var manualCaptureArtifactService = new ManualCaptureArtifactService();
        var ocrService = new OcrService(_settings, apiUsageTracker);
        var soundAlertService = new SoundAlertService();
        var telegramDispatcher = new TelegramDispatcher(_settings, telegramBotClient);
        _lineTargetWindowService = new LineTargetWindowService();
        var lineDispatcher = new LineDispatcher(_settings, _lineTargetWindowService);
        _facebookDispatcher = new FacebookDispatcher(_settings);
        _dispatchService = new DispatchService(
            _settings,
            soundAlertService,
            telegramDispatcher,
            lineDispatcher,
            _facebookDispatcher);
        var channelWatcher = new ChannelWatcher(
            _settings,
            codeExtractorService,
            dailyCodeHistoryService,
            duplicateGuard,
            _dispatchService,
            static () => new HiddenWebViewHostWindow());

        _watchCoordinator = new WatchCoordinator(_settings, _settingsStore, channelWatcher);
        var ocrWorkflowService = new OcrWorkflowService(
            _settings,
            ocrService,
            codeExtractorService,
            dailyCodeHistoryService,
            duplicateGuard,
            _watchCoordinator,
            telegramBotClient);
        _watchWorkflowService = new WatchWorkflowService(_appLogService, _watchCoordinator);
        _captureWorkflowService = new CaptureWorkflowService(
            _settings,
            _appLogService,
            _watchCoordinator,
            screenCaptureService,
            ocrWorkflowService,
            manualCaptureArtifactService);
        _ocrScanWorkflowService = new OcrScanWorkflowService(
            _settings,
            _appLogService,
            _watchCoordinator,
            screenCaptureService,
            ocrWorkflowService);
        _commentScannerService = new YouTubeCommentScannerService(
            _settings,
            _appLogService,
            _watchCoordinator,
            apiUsageTracker);

        _dispatchService.SetBoostEvaluator(HasActiveBoost, IsBoostedChannel);
        _watchCoordinator.LogEmitted += _appLogService.Write;
        _dispatchService.LogEmitted += _appLogService.Write;
        _watchCoordinator.ChannelsChanged += HandleChannelsChanged;
        _watchCoordinator.CodeDispatched += HandleCodeDispatched;
        _commentScannerService.ScannerCompleted += HandleCommentScannerCompleted;
        _appLogService.EntryEmitted += HandleLogEntryEmitted;
        _commentTimerSchedulerTimer.Interval = TimeSpan.FromSeconds(1);
        _commentTimerSchedulerTimer.Tick += CommentTimerSchedulerTimer_OnTick;
        _commentTimerSchedulerTimer.Start();

        Channels = new ReadOnlyObservableCollection<ChannelProfile>(_channels);
        LogEntries = new ReadOnlyObservableCollection<AppLogEntry>(_logEntries);

        RefreshChannels();
        LoadLogSnapshot();

        _appLogService.Write("WPF shell initialized");
        if (!string.IsNullOrWhiteSpace(_settingsStore.LastLoadFailureMessage))
        {
            _appLogService.Write(_settingsStore.LastLoadFailureMessage);
        }

        _appLogService.Write($"Settings file: {_settingsStore.SettingsPath}");
        _appLogService.Write($"Loaded channels: {_settings.Channels.Count}");
        RestoreSavedDesktopTargets();
        LogDryRunSessionLogPathIfEnabled();
        RunDailyYouTubeApiHealthCheckIfNeeded();
    }

    private static string? ResolveRuntimeDataRootPath()
    {
#if DEBUG
        var workspaceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return Path.Combine(workspaceRoot, "runtime-debug");
#else
        return null;
#endif
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ReadOnlyObservableCollection<ChannelProfile> Channels { get; }

    public ReadOnlyObservableCollection<AppLogEntry> LogEntries { get; }

    public int LogEntryCount => _logEntries.Count;

    public ChannelProfile? SelectedChannel
    {
        get => _selectedChannel;
        set
        {
            if (ReferenceEquals(_selectedChannel, value))
            {
                return;
            }

            _selectedChannel = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedChannelPrefixes));
            OnPropertyChanged(nameof(SelectedChannelLink));
            OnPropertyChanged(nameof(SelectedChannelStatus));
            OnPropertyChanged(nameof(SelectedChannelCaptureText));
            OnPropertyChanged(nameof(SelectedChannelScanText));
            OnPropertyChanged(nameof(SelectedChannelName));
            OnPropertyChanged(nameof(HasSelectedChannel));
            OnPropertyChanged(nameof(IsSelectedChannelEnabled));
            OnPropertyChanged(nameof(IsSelectedChannelWatching));
            OnPropertyChanged(nameof(IsSelectedChannelScanning));
            OnPropertyChanged(nameof(CanEditSelectedChannel));
            OnPropertyChanged(nameof(CanRemoveSelectedChannel));
            OnPropertyChanged(nameof(CanStartSelectedWatch));
            OnPropertyChanged(nameof(CanStopSelectedWatch));
            OnPropertyChanged(nameof(CanCaptureSelected));
            OnPropertyChanged(nameof(CanStartSelectedOcrScan));
            OnPropertyChanged(nameof(CanStopSelectedOcrScan));
            OnPropertyChanged(nameof(SelectedChannelAvailabilityText));
        }
    }

    public string SettingsPath => _settingsStore.SettingsPath;

    public int TotalChannels => _channels.Count;

    public int EnabledChannels => _channels.Count(static channel => channel.Enabled);

    public int ReadyChannels => _channels.Count(channel => channel.Status == SessionState.Idle);

    public int WatchingChannels => _channels.Count(channel =>
        channel.Status is SessionState.LoadingChat or SessionState.Watching or SessionState.NoMessages);

    public string LineTargetWindowText => _lineTargetWindowService.SelectedWindowText;

    public string SelectedChannelName => SelectedChannel?.Name ?? "No channel selected";

    public bool HasSelectedChannel => SelectedChannel is not null;

    public bool IsSelectedChannelEnabled => SelectedChannel?.Enabled == true;

    public bool IsSelectedChannelWatching => SelectedChannel?.Status is SessionState.LoadingChat or SessionState.Watching or SessionState.NoMessages;

    public bool IsSelectedChannelScanning => SelectedChannel?.Status is SessionState.OcrScanning or SessionState.OcrCooldown;

    public bool CanEditSelectedChannel => HasSelectedChannel;

    public bool CanRemoveSelectedChannel => HasSelectedChannel;

    public bool CanStartSelectedWatch => SelectedChannel is { Enabled: true } && !IsSelectedChannelWatching;

    public bool CanStopSelectedWatch => IsSelectedChannelWatching;

    public bool CanCaptureSelected => HasSelectedChannel;

    public bool CanStartSelectedOcrScan => SelectedChannel is { Enabled: true } && !IsSelectedChannelScanning;

    public bool CanStopSelectedOcrScan => IsSelectedChannelScanning;

    public bool CanStopAnyWatch => _settings.Channels.Any(channel =>
        channel.Status is SessionState.LoadingChat or SessionState.Watching or SessionState.NoMessages or SessionState.OcrScanning or SessionState.OcrCooldown);

    public string SelectedChannelAvailabilityText => SelectedChannel switch
    {
        null => "Select a channel to unlock its actions.",
        { Enabled: false } => "This channel is disabled. You can still edit it, but watch and OCR actions stay off.",
        _ when IsSelectedChannelScanning => "OCR scan is running for this channel.",
        _ when IsSelectedChannelWatching => "This channel is actively being watched.",
        _ => "This channel is ready for watch, capture, and OCR actions."
    };

    public string SelectedChannelPrefixes => SelectedChannel?.PrefixDisplay is { Length: > 0 } prefixes
        ? prefixes
        : "-";

    public string SelectedChannelLink => string.IsNullOrWhiteSpace(SelectedChannel?.ChatLink)
        ? "-"
        : SelectedChannel.ChatLink;

    public string SelectedChannelStatus => SelectedChannel?.LastStatusMessage ?? "Select a channel to inspect";

    public string SelectedChannelCaptureText => SelectedChannel?.LastCaptureRegion?.IsValid == true
        ? $"{SelectedChannel.LastCaptureRegion.Width} x {SelectedChannel.LastCaptureRegion.Height}"
        : "Not configured";

    public string SelectedChannelScanText => SelectedChannel is null
        ? "-"
        : $"{SelectedChannel.AutoScanIntervalMs} ms";

    public void RefreshView()
    {
        RefreshChannelsOnUiThread();
    }

    public async Task StartSelectedWatchAsync()
    {
        if (SelectedChannel is null)
        {
            _appLogService.Write("Select a channel before starting watch");
            return;
        }

        await _watchWorkflowService.StartWatchingChannelsAsync(
            [SelectedChannel],
            RefreshChannelsOnUiThread,
            CancellationToken.None);
    }

    public IReadOnlyList<TrayWatchChannelInfo> GetTrayWatchChannels()
    {
        return _channels
            .Where(static channel => !string.IsNullOrWhiteSpace(channel.ChatLink))
            .Select(static channel => new TrayWatchChannelInfo(
                channel.Id,
                channel.Name,
                channel.Enabled,
                IsChatWatchActive(channel),
                channel.LastStatusMessage))
            .ToList();
    }

    public async Task StartWatchAsync(Guid channelId)
    {
        var channel = _settings.Channels.FirstOrDefault(item => item.Id == channelId);
        if (channel is null)
        {
            _appLogService.Write("Watch chat tray: channel not found");
            return;
        }

        if (!channel.Enabled)
        {
            _appLogService.Write($"[{channel.Name}] ข้ามการเฝ้าแชท เพราะช่องถูกปิดใช้งาน");
            return;
        }

        if (string.IsNullOrWhiteSpace(channel.ChatLink))
        {
            _appLogService.Write($"[{channel.Name}] ยังไม่มี watch source สำหรับเฝ้าแชท");
            return;
        }

        if (IsChatWatchActive(channel))
        {
            _appLogService.Write($"[{channel.Name}] กำลังเฝ้าแชทอยู่แล้ว");
            return;
        }

        SelectedChannel = _channels.FirstOrDefault(item => item.Id == channelId) ?? channel;
        await _watchWorkflowService.StartWatchingChannelsAsync(
            [channel],
            RefreshChannelsOnUiThread,
            CancellationToken.None);
    }

    public async Task<bool> StartSelectedOcrScanAsync()
    {
        if (SelectedChannel is null)
        {
            _appLogService.Write("Select a channel before starting OCR scan");
            return false;
        }

        return await _ocrScanWorkflowService.StartAsync(SelectedChannel, RefreshChannelsOnUiThread);
    }

    public void StopSelectedOcrScan()
    {
        if (SelectedChannel is null)
        {
            _appLogService.Write("Select a channel before stopping OCR scan");
            return;
        }

        _ocrScanWorkflowService.Stop(SelectedChannel, RefreshChannelsOnUiThread);
    }

    public async Task RunSelectedCaptureAsync(Rectangle selectedRegion)
    {
        if (SelectedChannel is null)
        {
            _appLogService.Write("Select a channel before capturing");
            return;
        }

        await RunCaptureAsync(SelectedChannel, selectedRegion, "WPF shell");
    }

    public void SaveSelectedCaptureRegion(Rectangle selectedRegion)
    {
        if (SelectedChannel is null)
        {
            _appLogService.Write("Select a channel before saving a capture region");
            return;
        }

        _captureWorkflowService.SaveCaptureRegion(SelectedChannel, selectedRegion, RefreshChannelsOnUiThread);
    }

    public async Task RunCaptureAsync(ChannelProfile channel, Rectangle selectedRegion, string sourceLabel)
    {
        _appLogService.Write($"[{channel.Name}] Start capture from {sourceLabel}");
        _captureWorkflowService.SaveCaptureRegion(channel, selectedRegion, RefreshChannelsOnUiThread);
        await _captureWorkflowService.ProcessCaptureAsync(channel, selectedRegion, CancellationToken.None);
    }

    public void StopSelectedWatch()
    {
        if (SelectedChannel is null)
        {
            _appLogService.Write("Select a channel before stopping watch");
            return;
        }

        _watchWorkflowService.StopChannels(
            [SelectedChannel],
            static _ => { },
            RefreshChannelsOnUiThread);
    }

    public void StopWatch(Guid channelId)
    {
        var channel = _settings.Channels.FirstOrDefault(item => item.Id == channelId);
        if (channel is null)
        {
            _appLogService.Write("Watch chat tray: channel not found");
            return;
        }

        _watchWorkflowService.StopChannels(
            [channel],
            static _ => { },
            RefreshChannelsOnUiThread);
    }

    public void StopAllChatWatches()
    {
        var activeWatchChannels = _settings.Channels
            .Where(IsChatWatchActive)
            .ToList();

        if (activeWatchChannels.Count == 0)
        {
            _appLogService.Write("ไม่มีช่องที่กำลังเฝ้าแชทอยู่");
            return;
        }

        _watchWorkflowService.StopChannels(
            activeWatchChannels,
            static _ => { },
            RefreshChannelsOnUiThread);
    }

    public IReadOnlyList<TrayBoostChannelInfo> GetTrayBoostChannels()
    {
        return _channels
            .Where(static channel => IsChatWatchActive(channel))
            .Select(static channel => new TrayBoostChannelInfo(
                channel.Id,
                channel.Name,
                channel.Enabled,
                channel.IsBoosting,
                BuildBoostStatusText(channel)))
            .ToList();
    }

    public void ToggleBoost(Guid channelId)
    {
        var channel = _settings.Channels.FirstOrDefault(item => item.Id == channelId);
        if (channel is null)
        {
            _appLogService.Write("Boost tray: channel not found");
            return;
        }

        if (!channel.Enabled && !channel.IsBoosting)
        {
            _appLogService.Write($"[{channel.Name}] ข้าม Boost เพราะช่องถูกปิดใช้งาน");
            return;
        }

        SelectedChannel = _channels.FirstOrDefault(item => item.Id == channelId) ?? channel;
        ToggleBoost(channel);
    }

    public void StopAllBoostsFromTray()
    {
        var activeBoostChannels = _settings.Channels
            .Where(static channel => channel.IsBoosting)
            .ToList();

        if (activeBoostChannels.Count == 0)
        {
            _appLogService.Write("ไม่มีช่องที่กำลัง Boost อยู่");
            return;
        }

        StopAllBoosts("หยุด Boost จาก tray");
    }

    public void StopAllWatches()
    {
        StopAllBoosts("หยุด Boost เพราะ Stop all");
        _watchWorkflowService.StopChannels(
            _settings.Channels,
            static _ => { },
            RefreshChannelsOnUiThread);
    }

    public ChannelProfile CreateNewChannelDraft()
    {
        return new ChannelProfile
        {
            Enabled = true,
            AutoScanIntervalMs = 500
        };
    }

    public IReadOnlyList<ChannelProfile> GetQuickCaptureChannels()
    {
        return _channels
            .Where(static channel => channel.Enabled)
            .Where(static channel => string.IsNullOrWhiteSpace(channel.ChatLink))
            .ToList();
    }

    public IReadOnlyList<ChannelProfile> GetCommentScannerChannels()
    {
        return _channels
            .Where(static channel => channel.Enabled)
            .ToList();
    }

    public IReadOnlyList<ChannelProfile> GetManualSendChannels()
    {
        return _channels
            .Where(static channel => channel.Enabled)
            .ToList();
    }

    public IReadOnlyList<CodeCandidate> ExtractManualCodeCandidates(ChannelProfile channel, string input)
    {
        return _codeExtractorService.ExtractCandidates(
            channel,
            input,
            includeGenericAmbiguousVariants: false);
    }

    public async Task<OwnerTextProcessingResult> SendManualCodeAsync(ChannelProfile channel, string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new OwnerTextProcessingResult { Status = OwnerTextProcessingStatus.NoText };
        }

        var trimmedInput = input.Trim();
        if (trimmedInput.Length < MinimumManualMessageLength)
        {
            return new OwnerTextProcessingResult { Status = OwnerTextProcessingStatus.TooShort };
        }

        _appLogService.Write($"[{channel.Name}] Manual send requested");
        var result = await SendManualCandidatesAsync(channel, trimmedInput);
        LogManualSendResult(channel, result);
        return result;
    }

    private async Task<OwnerTextProcessingResult> SendManualCandidatesAsync(ChannelProfile channel, string input)
    {
        var candidates = ExtractManualCodeCandidates(channel, input);
        if (candidates.Count == 0)
        {
            return new OwnerTextProcessingResult { Status = OwnerTextProcessingStatus.NoCode };
        }

        var dispatchedCodes = new List<string>();
        OwnerTextProcessingResult? firstSkippedResult = null;
        foreach (var candidate in candidates)
        {
            var result = await _watchCoordinator.ProcessDetectedCodeAsync(
                channel,
                candidate.Value,
                input,
                capturedImagePath: null,
                CancellationToken.None,
                reason: "manual-selected");

            if (result.Status == OwnerTextProcessingStatus.Dispatched)
            {
                dispatchedCodes.Add(result.Code ?? candidate.Value);
                continue;
            }

            firstSkippedResult ??= result;
        }

        if (dispatchedCodes.Count > 0)
        {
            return new OwnerTextProcessingResult
            {
                Status = OwnerTextProcessingStatus.Dispatched,
                Code = dispatchedCodes[0],
                Codes = dispatchedCodes
            };
        }

        return firstSkippedResult ?? new OwnerTextProcessingResult { Status = OwnerTextProcessingStatus.NoCode };
    }

    public string GetLastCommentScannerVideoUrl(ChannelProfile channel)
    {
        return _settings.CommentScannerLastVideoUrls.TryGetValue(channel.Id, out var value)
            ? value
            : string.Empty;
    }

    public bool IsCommentScannerRunning(ChannelProfile channel)
    {
        return _commentScannerService.IsRunning(channel.Id);
    }

    public ApiUsageSnapshot GetApiUsageSnapshot()
    {
        return _apiUsageTracker.Snapshot();
    }

    public async Task<YouTubeApiHealthCheckSummary> CheckYouTubeApiKeysNowAsync()
    {
        var summary = await _commentScannerService.CheckApiKeysAsync(CancellationToken.None);
        _settings.YouTubeApiHealthCheckLastRunDate = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        _settingsStore.Save(_settings);
        return summary;
    }

    public ApiUsageSnapshot ResetApiUsageCounters()
    {
        var snapshot = _apiUsageTracker.Reset();
        _appLogService.Write("API usage counters reset");
        return snapshot;
    }

    public bool StartCommentScanner(ChannelProfile channel, string videoUrlOrId, TimeSpan pollInterval)
    {
        _settings.CommentScannerLastVideoUrls[channel.Id] = videoUrlOrId.Trim();
        _settingsStore.Save(_settings);
        var started = _commentScannerService.Start(channel, videoUrlOrId, pollInterval, RefreshChannelsOnUiThread);
        RefreshChannelsOnUiThread();
        return started;
    }

    public void StopCommentScanner(ChannelProfile channel)
    {
        _commentScannerService.Stop(channel, RefreshChannelsOnUiThread);
        RefreshChannelsOnUiThread();
    }

    public IReadOnlyList<ChannelProfile> GetCommentTimerChannels()
    {
        return GetCommentScannerChannels();
    }

    public IReadOnlyList<CommentTimerProfile> GetCommentTimers()
    {
        return _settings.CommentTimers
            .Select(CloneCommentTimer)
            .ToList();
    }

    public void SaveCommentTimer(CommentTimerProfile timer)
    {
        var normalized = CloneCommentTimer(timer);
        normalized.Id = normalized.Id == Guid.Empty ? Guid.NewGuid() : normalized.Id;
        normalized.VideoUrl = normalized.VideoUrl.Trim();
        normalized.StartTime = NormalizeTimerStartTime(normalized.StartTime);
        normalized.DurationSeconds = Math.Clamp(normalized.DurationSeconds, 30, 3600);
        normalized.PollIntervalSeconds = Math.Clamp(normalized.PollIntervalSeconds, 1, 60);
        normalized.LastStatus = string.IsNullOrWhiteSpace(normalized.LastStatus)
            ? "Waiting"
            : normalized.LastStatus.Trim();

        var existing = _settings.CommentTimers.FirstOrDefault(item => item.Id == normalized.Id);
        if (existing is null)
        {
            _settings.CommentTimers.Add(normalized);
        }
        else
        {
            ApplyCommentTimer(normalized, existing);
        }

        _settingsStore.Save(_settings);
        _appLogService.Write("Comment Timer saved");
    }

    public void DeleteCommentTimer(Guid timerId)
    {
        StopCommentTimer(timerId);
        _settings.CommentTimers.RemoveAll(item => item.Id == timerId);
        _settingsStore.Save(_settings);
        _appLogService.Write("Comment Timer deleted");
    }

    public bool StartCommentTimerNow(Guid timerId)
    {
        var timer = _settings.CommentTimers.FirstOrDefault(item => item.Id == timerId);
        return timer is not null && StartCommentTimer(timer, manualStart: true);
    }

    public void StopCommentTimer(Guid timerId)
    {
        var activeChannel = _activeCommentTimerByChannel
            .FirstOrDefault(item => item.Value == timerId);
        if (activeChannel.Key == Guid.Empty)
        {
            return;
        }

        var channel = _settings.Channels.FirstOrDefault(item => item.Id == activeChannel.Key);
        if (channel is not null)
        {
            _commentScannerService.Stop(channel, RefreshChannelsOnUiThread);
        }

        _activeCommentTimerByChannel.Remove(activeChannel.Key);
        var timer = _settings.CommentTimers.FirstOrDefault(item => item.Id == timerId);
        if (timer is not null)
        {
            timer.LastStatus = "Stopped";
            _settingsStore.Save(_settings);
        }
    }

    public string GetCommentTimerStatus(CommentTimerProfile timer)
    {
        if (_activeCommentTimerByChannel.TryGetValue(timer.ChannelId, out var activeTimerId) &&
            activeTimerId == timer.Id &&
            _commentScannerService.IsRunning(timer.ChannelId))
        {
            return "Running";
        }

        if (!timer.Enabled)
        {
            return "Disabled";
        }

        var today = GetTodayKey();
        if (string.Equals(timer.LastTriggeredDate, today, StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(timer.LastStatus) ? "Done today" : timer.LastStatus;
        }

        return TryGetTimerStart(timer, DateTime.Now.Date, out var start)
            ? $"Next {start:HH:mm}"
            : "Invalid time";
    }

    public void ToggleBoost(ChannelProfile channel)
    {
        if (channel.IsBoosting)
        {
            StopBoost(channel, "หยุด Boost");
            return;
        }

        StartBoost(channel);
    }

    public ChannelProfile? CreateSelectedChannelDraft()
    {
        return SelectedChannel is null ? null : CloneChannel(SelectedChannel);
    }

    public void SaveChannel(ChannelProfile channel)
    {
        var isNewChannel = _settings.Channels.All(existing => existing.Id != channel.Id);
        _watchCoordinator.AddOrUpdateChannel(channel);
        RefreshChannelsOnUiThread();
        SelectedChannel = _channels.FirstOrDefault(existing => existing.Id == channel.Id);
        _appLogService.Write(isNewChannel
            ? $"Added channel: {channel.Name}"
            : $"Updated channel: {channel.Name}");
    }

    public void RemoveSelectedChannel()
    {
        if (SelectedChannel is null)
        {
            _appLogService.Write("Select a channel before removing it");
            return;
        }

        var removedChannelName = SelectedChannel.Name;
        StopBoost(SelectedChannel, "หยุด Boost เพราะลบช่อง");
        _watchCoordinator.RemoveChannel(SelectedChannel);
        RefreshChannelsOnUiThread();
        _appLogService.Write($"Removed channel: {removedChannelName}");
    }

    public AppSettings CreateSettingsDraft()
    {
        return CloneSettings(_settings);
    }

    public IReadOnlyList<WindowHandleInfo> GetLineTargetWindows()
    {
        return _lineTargetWindowService.GetLineWindows();
    }

    public void SelectLineTargetWindow(WindowHandleInfo window)
    {
        _lineTargetWindowService.Select(window);
        _settings.Dispatch.LineTargetWindowTitle = window.Title;
        _appLogService.Write($"[LINE] เลือกหน้าต่างเป้าหมาย: {window.Title}");
        OnPropertyChanged(nameof(LineTargetWindowText));
    }

    public void ClearLineTargetWindow()
    {
        _lineTargetWindowService.Clear();
        _settings.Dispatch.LineTargetWindowTitle = string.Empty;
        _appLogService.Write("[LINE] ล้างหน้าต่างเป้าหมายแล้ว");
        OnPropertyChanged(nameof(LineTargetWindowText));
    }

    private void RestoreSavedDesktopTargets()
    {
        var settingsChanged = false;

        var savedLineTitle = _settings.Dispatch.LineTargetWindowTitle.Trim();
        if (!string.IsNullOrWhiteSpace(savedLineTitle))
        {
            if (_lineTargetWindowService.TryRestoreByTitle(savedLineTitle, out var restoredLineWindow))
            {
                _appLogService.Write($"[LINE] กู้คืนหน้าต่างเป้าหมาย: {restoredLineWindow.Title}");
                OnPropertyChanged(nameof(LineTargetWindowText));
            }
            else
            {
                _settings.Dispatch.LineTargetWindowTitle = string.Empty;
                settingsChanged = true;
                _appLogService.Write($"[LINE] ไม่พบหน้าต่างที่เคยเลือกไว้ ล้างค่าแล้ว: {savedLineTitle}");
            }
        }

        var savedFacebookUrl = _settings.Dispatch.FacebookTargetUrl.Trim();
        if (!string.IsNullOrWhiteSpace(savedFacebookUrl) && !_facebookDispatcher.IsConfiguredTargetAvailable())
        {
            _settings.Dispatch.FacebookTargetUrl = string.Empty;
            settingsChanged = true;
            _appLogService.Write($"[Facebook] ไม่พบ Messenger target URL ที่เปิดอยู่ ล้างค่าแล้ว: {savedFacebookUrl}");
        }

        if (settingsChanged)
        {
            _watchCoordinator.SaveSettings();
        }
    }

    public void SaveSettings(AppSettings updatedSettings)
    {
        var dryRunWasEnabled = _settings.Dispatch.EnableDryRun;
        ApplySettings(updatedSettings, _settings);
        var dryRunStateChanged = _appLogService.ConfigureDryRunSessionLogging(_settings.Dispatch.EnableDryRun, _dryRunLogsRootPath);
        _watchCoordinator.SaveSettings();
        RefreshChannelsOnUiThread();
        _appLogService.Write("Saved settings from WPF shell");
        if (!dryRunWasEnabled && _settings.Dispatch.EnableDryRun && dryRunStateChanged)
        {
            LogDryRunSessionLogPathIfEnabled();
        }
        else if (dryRunWasEnabled && !_settings.Dispatch.EnableDryRun && dryRunStateChanged)
        {
            _appLogService.Write("Dry run session logging disabled");
        }
    }

    public async Task<bool> TestDispatchAsync(AppSettings draftSettings)
    {
        var backup = CloneSettings(_settings);
        ApplySettings(draftSettings, _settings);

        try
        {
            return await _dispatchService.TestDispatchAsync(CancellationToken.None);
        }
        finally
        {
            ApplySettings(backup, _settings);
        }
    }

    public void Shutdown()
    {
        _appLogService.Write("Shutting down WPF shell");
        _commentTimerSchedulerTimer.Stop();
        _commentTimerSchedulerTimer.Tick -= CommentTimerSchedulerTimer_OnTick;
        StopAllBoosts("หยุด Boost เพราะปิดโปรแกรม");
        _appLogService.ConfigureDryRunSessionLogging(false, _dryRunLogsRootPath);
        _commentScannerService.StopAll();
        _ocrScanWorkflowService.StopAll();
        _watchCoordinator.ShutdownAll();
        _watchCoordinator.ChannelsChanged -= HandleChannelsChanged;
        _watchCoordinator.CodeDispatched -= HandleCodeDispatched;
        _commentScannerService.ScannerCompleted -= HandleCommentScannerCompleted;
        _appLogService.EntryEmitted -= HandleLogEntryEmitted;
    }

    public string BuildVisibleLogText()
    {
        if (_logEntries.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var entry in _logEntries)
        {
            builder.Append('[')
                .Append(entry.Timestamp.ToString("HH:mm:ss"))
                .Append("] ")
                .AppendLine(entry.Message);
        }

        return builder.ToString().TrimEnd();
    }

    public void ClearLogs()
    {
        _appLogService.Clear();
        _logEntries.Clear();
        OnPropertyChanged(nameof(LogEntryCount));
        _appLogService.Write("Cleared live log");
    }

    private void LogManualSendResult(ChannelProfile channel, OwnerTextProcessingResult result)
    {
        var codeText = result.Codes.Count > 0
            ? string.Join(", ", result.Codes)
            : result.Code ?? "-";

        var message = result.Status switch
        {
            OwnerTextProcessingStatus.Dispatched => $"Manual send completed: {codeText}",
            OwnerTextProcessingStatus.AlreadySentToday => $"Manual send skipped, already sent today: {codeText}",
            OwnerTextProcessingStatus.Duplicate => $"Manual send skipped, duplicate in this session: {codeText}",
            OwnerTextProcessingStatus.NoCode => channel.PrefixOnly
                ? "Manual send skipped, no code matched this channel prefix-only rule"
                : "Manual send skipped, no code matched",
            OwnerTextProcessingStatus.TooShort => "Manual send skipped, text is too short",
            OwnerTextProcessingStatus.DispatchFailed => $"Manual send failed: {result.Message ?? codeText}",
            _ => $"Manual send finished: {result.Status}"
        };

        _appLogService.Write($"[{channel.Name}] {message}");
    }

    private void HandleChannelsChanged()
    {
        RefreshChannelsOnUiThread();
    }

    private void HandleCodeDispatched(CodeDetectedEvent detectedEvent)
    {
        if (!IsBoostedChannel(detectedEvent.Channel.Id))
        {
            return;
        }

        StopBoost(detectedEvent.Channel, $"Boost ส่งโค้ดสำเร็จ {detectedEvent.Candidate.Value}");
    }

    private void CommentTimerSchedulerTimer_OnTick(object? sender, EventArgs e)
    {
        if (_settings.CommentTimers.Count == 0)
        {
            return;
        }

        foreach (var active in _activeCommentTimerByChannel.ToList())
        {
            if (!_commentScannerService.IsRunning(active.Key))
            {
                _activeCommentTimerByChannel.Remove(active.Key);
            }
        }

        var now = DateTime.Now;
        var today = GetTodayKey();
        foreach (var timer in _settings.CommentTimers.Where(static item => item.Enabled).ToList())
        {
            if (string.Equals(timer.LastTriggeredDate, today, StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryGetTimerStart(timer, now.Date, out var start))
            {
                continue;
            }

            var duration = TimeSpan.FromSeconds(Math.Clamp(timer.DurationSeconds, 30, 3600));
            if (now < start || now >= start + duration)
            {
                continue;
            }

            StartCommentTimer(timer, manualStart: false);
        }
    }

    private bool StartCommentTimer(CommentTimerProfile timer, bool manualStart)
    {
        var channel = _settings.Channels.FirstOrDefault(item => item.Id == timer.ChannelId);
        if (channel is null)
        {
            timer.LastStatus = "Channel not found";
            timer.LastTriggeredDate = manualStart ? timer.LastTriggeredDate : GetTodayKey();
            _settingsStore.Save(_settings);
            _appLogService.Write("[Comment Timer] channel not found");
            return false;
        }

        if (string.IsNullOrWhiteSpace(timer.VideoUrl))
        {
            timer.LastStatus = "Missing video URL";
            timer.LastTriggeredDate = manualStart ? timer.LastTriggeredDate : GetTodayKey();
            _settingsStore.Save(_settings);
            _appLogService.Write($"[{channel.Name}] Comment Timer skipped: missing video URL");
            return false;
        }

        if (_commentScannerService.IsRunning(channel.Id))
        {
            timer.LastStatus = "Skipped, scanner already running";
            timer.LastTriggeredDate = manualStart ? timer.LastTriggeredDate : GetTodayKey();
            _settingsStore.Save(_settings);
            _appLogService.Write($"[{channel.Name}] Comment Timer skipped: scanner already running");
            return false;
        }

        var durationSeconds = Math.Clamp(timer.DurationSeconds, 30, 3600);
        var pollSeconds = Math.Clamp(timer.PollIntervalSeconds, 1, 60);
        timer.DurationSeconds = durationSeconds;
        timer.PollIntervalSeconds = pollSeconds;
        timer.LastTriggeredDate = GetTodayKey();
        timer.LastStatus = $"Running until {DateTime.Now.AddSeconds(durationSeconds):HH:mm:ss}";
        _settings.CommentScannerLastVideoUrls[channel.Id] = timer.VideoUrl.Trim();
        _settingsStore.Save(_settings);

        _activeCommentTimerByChannel[channel.Id] = timer.Id;
        var started = _commentScannerService.Start(
            channel,
            timer.VideoUrl,
            TimeSpan.FromSeconds(pollSeconds),
            RefreshChannelsOnUiThread,
            new CommentScannerStartOptions
            {
                MaxDuration = TimeSpan.FromSeconds(durationSeconds),
                StopOnFirstDispatchedCode = true,
                SourceLabel = "Comment Timer"
            });

        if (!started)
        {
            _activeCommentTimerByChannel.Remove(channel.Id);
            timer.LastStatus = "Start failed";
            _settingsStore.Save(_settings);
            return false;
        }

        _appLogService.Write($"[{channel.Name}] Comment Timer started: {pollSeconds}s polling for {durationSeconds / 60.0:0.#}m");
        RefreshChannelsOnUiThread();
        return true;
    }

    private void HandleCommentScannerCompleted(CommentScannerCompletedEvent completedEvent)
    {
        if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => HandleCommentScannerCompleted(completedEvent));
            return;
        }

        if (!string.Equals(completedEvent.SourceLabel, "Comment Timer", StringComparison.Ordinal))
        {
            return;
        }

        if (!_activeCommentTimerByChannel.TryGetValue(completedEvent.ChannelId, out var timerId))
        {
            return;
        }

        _activeCommentTimerByChannel.Remove(completedEvent.ChannelId);
        var timer = _settings.CommentTimers.FirstOrDefault(item => item.Id == timerId);
        if (timer is null)
        {
            return;
        }

        timer.LastStatus = completedEvent.DispatchedCodes.Count > 0
            ? $"Sent {string.Join(", ", completedEvent.DispatchedCodes.Distinct(StringComparer.OrdinalIgnoreCase))}"
            : NormalizeTimerStopReason(completedEvent.StopReason);
        _settingsStore.Save(_settings);
    }

    private void HandleLogEntryEmitted(AppLogEntry entry)
    {
        if (System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            AppendLogEntry(entry);
            return;
        }

        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => AppendLogEntry(entry));
    }

    private void RefreshChannels()
    {
        var selectedChannelId = SelectedChannel?.Id;
        var sortedChannels = _settings.Channels
            .OrderBy(static channel => NormalizeSortValue(channel.ChatLink), StringComparer.OrdinalIgnoreCase)
            .ThenBy(static channel => NormalizeSortValue(channel.Name), StringComparer.OrdinalIgnoreCase)
            .ToList();

        _channels.Clear();
        foreach (var channel in sortedChannels)
        {
            channel.IsCommentScanActive = _commentScannerService.IsRunning(channel.Id);
            _channels.Add(channel);
        }

        if (_channels.Count == 0)
        {
            SelectedChannel = null;
        }
        else
        {
            SelectedChannel = selectedChannelId.HasValue
                ? _channels.FirstOrDefault(channel => channel.Id == selectedChannelId.Value) ?? _channels[0]
                : _channels[0];
        }

        OnPropertyChanged(nameof(TotalChannels));
        OnPropertyChanged(nameof(EnabledChannels));
        OnPropertyChanged(nameof(ReadyChannels));
        OnPropertyChanged(nameof(WatchingChannels));
        OnPropertyChanged(nameof(CanStopAnyWatch));
    }

    private void StartBoost(ChannelProfile channel)
    {
        CancellationTokenSource source;
        lock (_boostSync)
        {
            if (_boostTokens.ContainsKey(channel.Id))
            {
                return;
            }

            source = new CancellationTokenSource();
            _boostTokens[channel.Id] = source;
        }

        foreach (var otherChannel in _settings.Channels.Where(item => item.Id != channel.Id).ToList())
        {
            if (_ocrScanWorkflowService.IsScanning(otherChannel.Id))
            {
                _ocrScanWorkflowService.Stop(otherChannel, RefreshChannelsOnUiThread, emitLog: false);
                _appLogService.Write($"[{otherChannel.Name}] หยุด OCR ชั่วคราวเพราะ Boost ช่อง {channel.Name}");
            }
        }

        channel.IsBoosting = true;
        channel.BoostExpiresAt = DateTimeOffset.Now.AddSeconds(GetBoostTimeoutSeconds());
        _watchCoordinator.SetBoostMode(channel.Id, true);
        _appLogService.Write($"[{channel.Name}] เริ่ม Boost {GetBoostTimeoutSeconds()} วิ");
        RefreshChannelsOnUiThread();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(GetBoostTimeoutSeconds()), source.Token);
                StopBoost(channel, "Boost timeout");
            }
            catch (OperationCanceledException)
            {
                // normal stop
            }
        });
    }

    private void StopBoost(ChannelProfile channel, string reason)
    {
        CancellationTokenSource? source;
        lock (_boostSync)
        {
            _boostTokens.TryGetValue(channel.Id, out source);
            _boostTokens.Remove(channel.Id);
        }

        source?.Cancel();
        source?.Dispose();

        if (!channel.IsBoosting && source is null)
        {
            return;
        }

        channel.IsBoosting = false;
        channel.BoostExpiresAt = null;
        _watchCoordinator.SetBoostMode(channel.Id, false);
        _appLogService.Write($"[{channel.Name}] {reason}");
        RefreshChannelsOnUiThread();
    }

    private void StopAllBoosts(string reason)
    {
        foreach (var channel in _settings.Channels.Where(static item => item.IsBoosting).ToList())
        {
            StopBoost(channel, reason);
        }
    }

    private bool HasActiveBoost()
    {
        lock (_boostSync)
        {
            return _boostTokens.Count > 0;
        }
    }

    private bool IsBoostedChannel(Guid channelId)
    {
        lock (_boostSync)
        {
            return _boostTokens.ContainsKey(channelId);
        }
    }

    private int GetBoostTimeoutSeconds()
    {
        return Math.Clamp(_settings.BoostTimeoutSeconds, 10, 300);
    }

    private void LoadLogSnapshot()
    {
        _logEntries.Clear();
        foreach (var entry in _appLogService.GetSnapshot().TakeLast(MaxVisibleLogEntries))
        {
            _logEntries.Add(entry);
        }

        OnPropertyChanged(nameof(LogEntryCount));
    }

    private void AppendLogEntry(AppLogEntry entry)
    {
        _logEntries.Add(entry);
        while (_logEntries.Count > MaxVisibleLogEntries)
        {
            _logEntries.RemoveAt(0);
        }

        OnPropertyChanged(nameof(LogEntryCount));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void RefreshChannelsOnUiThread()
    {
        if (System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            RefreshChannels();
            return;
        }

        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(RefreshChannels);
    }

    private void LogDryRunSessionLogPathIfEnabled()
    {
        if (!_settings.Dispatch.EnableDryRun || string.IsNullOrWhiteSpace(_appLogService.CurrentSessionLogPath))
        {
            return;
        }

        _appLogService.Write($"Dry run session log: {_appLogService.CurrentSessionLogPath}");
    }

    private void RunDailyYouTubeApiHealthCheckIfNeeded()
    {
        if (string.IsNullOrWhiteSpace(_settings.YouTubeApiKey) &&
            !_settings.YouTubeApiBackupKeys.Any(static key => !string.IsNullOrWhiteSpace(key)))
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (string.Equals(_settings.YouTubeApiHealthCheckLastRunDate, today, StringComparison.Ordinal))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await CheckYouTubeApiKeysNowAsync();
            }
            catch (Exception ex)
            {
                _appLogService.Write($"[Comment Scanner] YouTube API daily health check failed: {ex.Message}");
            }
        });
    }

    private static string NormalizeSortValue(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private static bool IsRawYouTubeChannelId(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.StartsWith("UC", StringComparison.OrdinalIgnoreCase) &&
               text.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
    }

    private static bool IsChatWatchActive(ChannelProfile channel)
    {
        return channel.Status is SessionState.LoadingChat or SessionState.Watching or SessionState.NoMessages;
    }

    private static string BuildBoostStatusText(ChannelProfile channel)
    {
        if (!channel.IsBoosting)
        {
            return channel.Enabled ? "Ready" : "Disabled";
        }

        if (channel.BoostExpiresAt is not DateTimeOffset expiresAt)
        {
            return "Boosting";
        }

        var remainingSeconds = Math.Max(0, (int)Math.Ceiling((expiresAt - DateTimeOffset.Now).TotalSeconds));
        return $"Boosting {remainingSeconds}s";
    }

    private static string GetTodayKey()
    {
        return DateOnly.FromDateTime(DateTime.Today).ToString(
            "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TryGetTimerStart(CommentTimerProfile timer, DateTime date, out DateTime start)
    {
        if (TimeOnly.TryParseExact(
                timer.StartTime,
                "HH:mm",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var time) ||
            TimeOnly.TryParse(timer.StartTime, out time))
        {
            start = date.Add(time.ToTimeSpan());
            return true;
        }

        start = date;
        return false;
    }

    private static string NormalizeTimerStartTime(string value)
    {
        if (TimeOnly.TryParseExact(
                value,
                "HH:mm",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var exactTime) ||
            TimeOnly.TryParse(value, out exactTime))
        {
            return exactTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        return DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string NormalizeTimerStopReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "Stopped";
        }

        return reason.Contains("duration reached", StringComparison.OrdinalIgnoreCase)
            ? "Timeout"
            : reason;
    }

    private static CommentTimerProfile CloneCommentTimer(CommentTimerProfile source)
    {
        return new CommentTimerProfile
        {
            Id = source.Id,
            ChannelId = source.ChannelId,
            VideoUrl = source.VideoUrl,
            StartTime = source.StartTime,
            DurationSeconds = source.DurationSeconds,
            PollIntervalSeconds = source.PollIntervalSeconds,
            Enabled = source.Enabled,
            LastTriggeredDate = source.LastTriggeredDate,
            LastStatus = source.LastStatus
        };
    }

    private static void ApplyCommentTimer(CommentTimerProfile source, CommentTimerProfile destination)
    {
        destination.ChannelId = source.ChannelId;
        destination.VideoUrl = source.VideoUrl;
        destination.StartTime = source.StartTime;
        destination.DurationSeconds = source.DurationSeconds;
        destination.PollIntervalSeconds = source.PollIntervalSeconds;
        destination.Enabled = source.Enabled;
        destination.LastTriggeredDate = source.LastTriggeredDate;
        destination.LastStatus = source.LastStatus;
    }

    private static ChannelProfile CloneChannel(ChannelProfile source)
    {
        return new ChannelProfile
        {
            Id = source.Id,
            Name = source.Name,
            ChatLink = source.ChatLink,
            Enabled = source.Enabled,
            Prefixes = source.Prefixes.ToList(),
            PrefixOnly = source.PrefixOnly,
            LastCaptureRegion = source.LastCaptureRegion,
            EnableAutoScan = source.EnableAutoScan,
            AutoScanIntervalMs = source.AutoScanIntervalMs,
            Status = source.Status,
            LastStatusMessage = source.LastStatusMessage,
            LastCheckedAt = source.LastCheckedAt
        };
    }

    private static AppSettings CloneSettings(AppSettings source)
    {
        return new AppSettings
        {
            Dispatch = new DispatchSettings
            {
                TelegramBotToken = source.Dispatch.TelegramBotToken,
                TelegramChatId = source.Dispatch.TelegramChatId,
                EnableLine = source.Dispatch.EnableLine,
                EnableFacebook = source.Dispatch.EnableFacebook,
                BlockDesktopDispatchForOcr = source.Dispatch.BlockDesktopDispatchForOcr,
                EnableSound = source.Dispatch.EnableSound,
                SkipIfWindowNotFound = source.Dispatch.SkipIfWindowNotFound,
                PasteDelayMs = source.Dispatch.PasteDelayMs,
                EnterAfterPaste = source.Dispatch.EnterAfterPaste,
                EnableSafeDesktopPaste = source.Dispatch.EnableSafeDesktopPaste,
                EnableDesktopTargetVerification = false,
                LineTargetTitleKeyword = source.Dispatch.LineTargetTitleKeyword,
                LineTargetWindowTitle = source.Dispatch.LineTargetWindowTitle,
                FacebookTargetTitleKeyword = source.Dispatch.FacebookTargetTitleKeyword,
                FacebookTargetUrl = source.Dispatch.FacebookTargetUrl,
                EnableDryRun = source.Dispatch.EnableDryRun,
                SendManualCaptureImage = source.Dispatch.SendManualCaptureImage,
                SaveManualCaptureImageToTempInDryRun = source.Dispatch.SaveManualCaptureImageToTempInDryRun
            },
            EnableOcrDebugLog = source.EnableOcrDebugLog,
            EnableOcrSpaceFallback = source.EnableOcrSpaceFallback,
            OcrSpaceApiKey = source.OcrSpaceApiKey,
            OcrSpaceLanguage = source.OcrSpaceLanguage,
            YouTubeApiKey = source.YouTubeApiKey,
            YouTubeApiBackupKeys = source.YouTubeApiBackupKeys.ToList(),
            YouTubeApiDailyQuotaGuardUnits = source.YouTubeApiDailyQuotaGuardUnits,
            YouTubeApiHealthCheckLastRunDate = source.YouTubeApiHealthCheckLastRunDate,
            OcrSpaceDailyRequestGuard = source.OcrSpaceDailyRequestGuard,
            OcrSpaceHourlyRequestGuard = source.OcrSpaceHourlyRequestGuard,
            BoostTimeoutSeconds = source.BoostTimeoutSeconds,
            CommentScannerLastVideoUrls = new Dictionary<Guid, string>(source.CommentScannerLastVideoUrls),
            CommentTimers = source.CommentTimers.Select(CloneCommentTimer).ToList(),
            Channels = source.Channels.Select(CloneChannel).ToList()
        };
    }

    private static void ApplySettings(AppSettings source, AppSettings destination)
    {
        destination.Dispatch.TelegramBotToken = source.Dispatch.TelegramBotToken;
        destination.Dispatch.TelegramChatId = source.Dispatch.TelegramChatId;
        destination.Dispatch.EnableLine = source.Dispatch.EnableLine;
        destination.Dispatch.EnableFacebook = source.Dispatch.EnableFacebook;
        destination.Dispatch.BlockDesktopDispatchForOcr = source.Dispatch.BlockDesktopDispatchForOcr;
        destination.Dispatch.EnableSound = source.Dispatch.EnableSound;
        destination.Dispatch.SkipIfWindowNotFound = source.Dispatch.SkipIfWindowNotFound;
        destination.Dispatch.PasteDelayMs = source.Dispatch.PasteDelayMs;
        destination.Dispatch.EnterAfterPaste = source.Dispatch.EnterAfterPaste;
        destination.Dispatch.EnableSafeDesktopPaste = source.Dispatch.EnableSafeDesktopPaste;
        destination.Dispatch.EnableDesktopTargetVerification = false;
        destination.Dispatch.LineTargetTitleKeyword = source.Dispatch.LineTargetTitleKeyword;
        destination.Dispatch.LineTargetWindowTitle = source.Dispatch.LineTargetWindowTitle;
        destination.Dispatch.FacebookTargetTitleKeyword = source.Dispatch.FacebookTargetTitleKeyword;
        destination.Dispatch.FacebookTargetUrl = source.Dispatch.FacebookTargetUrl;
        destination.Dispatch.EnableDryRun = source.Dispatch.EnableDryRun;
        destination.Dispatch.SendManualCaptureImage = source.Dispatch.SendManualCaptureImage;
        destination.Dispatch.SaveManualCaptureImageToTempInDryRun = source.Dispatch.SaveManualCaptureImageToTempInDryRun;
        destination.EnableOcrDebugLog = source.EnableOcrDebugLog;
        destination.EnableOcrSpaceFallback = source.EnableOcrSpaceFallback;
        destination.OcrSpaceApiKey = source.OcrSpaceApiKey;
        destination.OcrSpaceLanguage = source.OcrSpaceLanguage;
        destination.YouTubeApiKey = source.YouTubeApiKey;
        destination.YouTubeApiBackupKeys = source.YouTubeApiBackupKeys.ToList();
        destination.YouTubeApiDailyQuotaGuardUnits = source.YouTubeApiDailyQuotaGuardUnits;
        destination.YouTubeApiHealthCheckLastRunDate = source.YouTubeApiHealthCheckLastRunDate;
        destination.OcrSpaceDailyRequestGuard = source.OcrSpaceDailyRequestGuard;
        destination.OcrSpaceHourlyRequestGuard = source.OcrSpaceHourlyRequestGuard;
        destination.BoostTimeoutSeconds = source.BoostTimeoutSeconds;
        destination.CommentScannerLastVideoUrls = new Dictionary<Guid, string>(source.CommentScannerLastVideoUrls);
        destination.CommentTimers = source.CommentTimers.Select(CloneCommentTimer).ToList();
        ApplyChannels(source.Channels, destination.Channels);
    }

    private static void ApplyChannels(IReadOnlyList<ChannelProfile> sourceChannels, List<ChannelProfile> destinationChannels)
    {
        var importedChannelIds = sourceChannels
            .Select(static channel => channel.Id)
            .ToHashSet();

        destinationChannels.RemoveAll(channel => !importedChannelIds.Contains(channel.Id));
        foreach (var sourceChannel in sourceChannels)
        {
            var existing = destinationChannels.FirstOrDefault(channel => channel.Id == sourceChannel.Id);
            if (existing is null)
            {
                destinationChannels.Add(CloneChannel(sourceChannel));
                continue;
            }

            ApplyChannel(sourceChannel, existing);
        }
    }

    private static void ApplyChannel(ChannelProfile source, ChannelProfile destination)
    {
        destination.Name = source.Name;
        destination.ChatLink = source.ChatLink;
        destination.Enabled = source.Enabled;
        destination.Prefixes = source.Prefixes.ToList();
        destination.PrefixOnly = source.PrefixOnly;
        destination.LastCaptureRegion = source.LastCaptureRegion;
        destination.EnableAutoScan = source.EnableAutoScan;
        destination.AutoScanIntervalMs = source.AutoScanIntervalMs;
        destination.Status = source.Status;
        destination.LastStatusMessage = source.LastStatusMessage;
        destination.LastCheckedAt = source.LastCheckedAt;
    }
}
