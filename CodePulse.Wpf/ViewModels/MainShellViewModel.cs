using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
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

    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly AppLogService _appLogService;
    private readonly WatchCoordinator _watchCoordinator;
    private readonly WatchWorkflowService _watchWorkflowService;
    private readonly CaptureWorkflowService _captureWorkflowService;
    private readonly OcrScanWorkflowService _ocrScanWorkflowService;
    private readonly YouTubeCommentScannerService _commentScannerService;
    private readonly DispatchService _dispatchService;
    private readonly LineTargetWindowService _lineTargetWindowService;
    private readonly ObservableCollection<ChannelProfile> _channels = new();
    private readonly ObservableCollection<AppLogEntry> _logEntries = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _boostTokens = new();
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
        var dailyCodeHistoryService = new DailyCodeHistoryService(_settingsStore.AppFolderPath);
        var duplicateGuard = new ChannelDuplicateGuard();
        var screenCaptureService = new ScreenCaptureService();
        var manualCaptureArtifactService = new ManualCaptureArtifactService();
        var ocrService = new OcrService(_settings);
        var soundAlertService = new SoundAlertService();
        var telegramDispatcher = new TelegramDispatcher(_settings, telegramBotClient);
        _lineTargetWindowService = new LineTargetWindowService();
        var lineDispatcher = new LineDispatcher(_settings, _lineTargetWindowService);
        var facebookDispatcher = new FacebookDispatcher(_settings);
        _dispatchService = new DispatchService(
            _settings,
            soundAlertService,
            telegramDispatcher,
            lineDispatcher,
            facebookDispatcher);
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
            _watchCoordinator);

        _dispatchService.SetBoostEvaluator(HasActiveBoost, IsBoostedChannel);
        _watchCoordinator.LogEmitted += _appLogService.Write;
        _dispatchService.LogEmitted += _appLogService.Write;
        _watchCoordinator.ChannelsChanged += HandleChannelsChanged;
        _watchCoordinator.CodeDispatched += HandleCodeDispatched;
        _appLogService.EntryEmitted += HandleLogEntryEmitted;

        Channels = new ReadOnlyObservableCollection<ChannelProfile>(_channels);
        LogEntries = new ReadOnlyObservableCollection<AppLogEntry>(_logEntries);

        RefreshChannels();
        LoadLogSnapshot();

        _appLogService.Write("WPF shell initialized");
        _appLogService.Write($"Settings file: {_settingsStore.SettingsPath}");
        _appLogService.Write($"Loaded channels: {_settings.Channels.Count}");
        LogDryRunSessionLogPathIfEnabled();
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
        _appLogService.Write($"[LINE] เลือกหน้าต่างเป้าหมาย: {window.Title}");
        OnPropertyChanged(nameof(LineTargetWindowText));
    }

    public void ClearLineTargetWindow()
    {
        _lineTargetWindowService.Clear();
        _appLogService.Write("[LINE] ล้างหน้าต่างเป้าหมายแล้ว");
        OnPropertyChanged(nameof(LineTargetWindowText));
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
        StopAllBoosts("หยุด Boost เพราะปิดโปรแกรม");
        _appLogService.ConfigureDryRunSessionLogging(false, _dryRunLogsRootPath);
        _commentScannerService.StopAll();
        _ocrScanWorkflowService.StopAll();
        _watchCoordinator.ShutdownAll();
        _watchCoordinator.ChannelsChanged -= HandleChannelsChanged;
        _watchCoordinator.CodeDispatched -= HandleCodeDispatched;
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

    private static ChannelProfile CloneChannel(ChannelProfile source)
    {
        return new ChannelProfile
        {
            Id = source.Id,
            Name = source.Name,
            ChatLink = source.ChatLink,
            Enabled = source.Enabled,
            Prefixes = source.Prefixes.ToList(),
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
                EnableSound = source.Dispatch.EnableSound,
                SkipIfWindowNotFound = source.Dispatch.SkipIfWindowNotFound,
                PasteDelayMs = source.Dispatch.PasteDelayMs,
                EnterAfterPaste = source.Dispatch.EnterAfterPaste,
                EnableDesktopTargetVerification = false,
                LineTargetTitleKeyword = source.Dispatch.LineTargetTitleKeyword,
                FacebookTargetTitleKeyword = source.Dispatch.FacebookTargetTitleKeyword,
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
            BoostTimeoutSeconds = source.BoostTimeoutSeconds,
            CommentScannerLastVideoUrls = new Dictionary<Guid, string>(source.CommentScannerLastVideoUrls),
            Channels = source.Channels.Select(CloneChannel).ToList()
        };
    }

    private static void ApplySettings(AppSettings source, AppSettings destination)
    {
        destination.Dispatch.TelegramBotToken = source.Dispatch.TelegramBotToken;
        destination.Dispatch.TelegramChatId = source.Dispatch.TelegramChatId;
        destination.Dispatch.EnableLine = source.Dispatch.EnableLine;
        destination.Dispatch.EnableFacebook = source.Dispatch.EnableFacebook;
        destination.Dispatch.EnableSound = source.Dispatch.EnableSound;
        destination.Dispatch.SkipIfWindowNotFound = source.Dispatch.SkipIfWindowNotFound;
        destination.Dispatch.PasteDelayMs = source.Dispatch.PasteDelayMs;
        destination.Dispatch.EnterAfterPaste = source.Dispatch.EnterAfterPaste;
        destination.Dispatch.EnableDesktopTargetVerification = false;
        destination.Dispatch.LineTargetTitleKeyword = source.Dispatch.LineTargetTitleKeyword;
        destination.Dispatch.FacebookTargetTitleKeyword = source.Dispatch.FacebookTargetTitleKeyword;
        destination.Dispatch.EnableDryRun = source.Dispatch.EnableDryRun;
        destination.Dispatch.SendManualCaptureImage = source.Dispatch.SendManualCaptureImage;
        destination.Dispatch.SaveManualCaptureImageToTempInDryRun = source.Dispatch.SaveManualCaptureImageToTempInDryRun;
        destination.EnableOcrDebugLog = source.EnableOcrDebugLog;
        destination.EnableOcrSpaceFallback = source.EnableOcrSpaceFallback;
        destination.OcrSpaceApiKey = source.OcrSpaceApiKey;
        destination.OcrSpaceLanguage = source.OcrSpaceLanguage;
        destination.YouTubeApiKey = source.YouTubeApiKey;
        destination.YouTubeApiBackupKeys = source.YouTubeApiBackupKeys.ToList();
        destination.BoostTimeoutSeconds = source.BoostTimeoutSeconds;
        destination.CommentScannerLastVideoUrls = new Dictionary<Guid, string>(source.CommentScannerLastVideoUrls);
    }
}
