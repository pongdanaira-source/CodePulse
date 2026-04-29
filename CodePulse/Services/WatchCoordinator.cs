using CodePulse.Enums;
using CodePulse.Models;

namespace CodePulse.Services;

public sealed class WatchCoordinator
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly ChannelWatcher _channelWatcher;

    public WatchCoordinator(
        AppSettings settings,
        SettingsStore settingsStore,
        ChannelWatcher channelWatcher)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _channelWatcher = channelWatcher;
        _channelWatcher.StatusChanged += HandleWatcherStatusChanged;
        _channelWatcher.CodeDispatched += HandleWatcherCodeDispatched;
    }

    public event Action? ChannelsChanged;
    public event Action<string>? LogEmitted;
    public event Action<CodeDetectedEvent>? CodeDispatched;

    public IReadOnlyList<ChannelProfile> Channels => _settings.Channels;

    public async Task StartWatchingChannelAsync(ChannelProfile channel, CancellationToken cancellationToken)
    {
        if (!channel.Enabled)
        {
            channel.Status = SessionState.Stopped;
            channel.LastStatusMessage = "ช่องนี้ถูกปิดใช้งาน";
            LogEmitted?.Invoke($"[{channel.Name}] ข้ามการเริ่มเฝ้าเพราะช่องถูกปิดใช้งาน");
            ChannelsChanged?.Invoke();
            return;
        }

        var watchSource = channel.ChatLink?.Trim() ?? string.Empty;
        LogEmitted?.Invoke($"[{channel.Name}] กำลัง resolve watch source: {DescribeWatchSource(watchSource)}");
        var resolution = await ChatLinkService.ResolveToChatLinkAsync(
            watchSource,
            _settings.YouTubeApiKey,
            cancellationToken);
        if (!resolution.Succeeded)
        {
            channel.Status = SessionState.Error;
            channel.LastStatusMessage = resolution.ErrorMessage;
            channel.LastCheckedAt = DateTimeOffset.Now;
            LogEmitted?.Invoke($"[{channel.Name}] {resolution.ErrorMessage}");
            ChannelsChanged?.Invoke();
            return;
        }

        var normalizedChatLink = resolution.NormalizedChatLink;
        LogEmitted?.Invoke($"[{channel.Name}] resolve สำเร็จ: {normalizedChatLink}");

        if (_channelWatcher.IsWatching(channel.Id))
        {
            LogEmitted?.Invoke($"[{channel.Name}] มีการเฝ้าดูอยู่แล้ว กำลังเริ่มใหม่");
        }

        channel.Status = SessionState.LoadingChat;
        channel.LastStatusMessage = "กำลังโหลดหน้าแชท";
        channel.LastCheckedAt = DateTimeOffset.Now;
        LogEmitted?.Invoke($"[{channel.Name}] เริ่มเฝ้าช่อง {channel.Name}");
        ChannelsChanged?.Invoke();

        try
        {
            var started = await _channelWatcher.StartAsync(channel, normalizedChatLink, cancellationToken);
            if (!started && channel.Status != SessionState.Error)
            {
                channel.Status = SessionState.Error;
                channel.LastStatusMessage = "โหลดหน้าแชทไม่สำเร็จ";
                ChannelsChanged?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            channel.Status = SessionState.Stopped;
            channel.LastStatusMessage = "หยุดแล้ว";
            ChannelsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            channel.Status = SessionState.Error;
            channel.LastStatusMessage = "โหลดหน้าแชทไม่สำเร็จ";
            LogEmitted?.Invoke($"[{channel.Name}] โหลดหน้าแชทไม่สำเร็จ: {Summarize(ex)}");
            ChannelsChanged?.Invoke();
        }
    }

    public async Task<OwnerTextProcessingResult> ProcessOwnerTextAsync(ChannelProfile channel, string text, CancellationToken cancellationToken)
    {
        return await _channelWatcher.ProcessExternalOwnerTextAsync(channel, text, cancellationToken);
    }

    public async Task<OwnerTextProcessingResult> ProcessDetectedCodeAsync(
        ChannelProfile channel,
        string code,
        string sourceMessage,
        string? capturedImagePath,
        CancellationToken cancellationToken)
    {
        return await _channelWatcher.ProcessExternalDetectedCodeAsync(channel, code, sourceMessage, capturedImagePath, cancellationToken);
    }

    public void StopChannel(ChannelProfile channel)
    {
        _channelWatcher.Stop(channel.Id);
        channel.Status = SessionState.Stopped;
        channel.LastStatusMessage = "หยุดแล้ว";
        channel.LastCheckedAt = DateTimeOffset.Now;
        ChannelsChanged?.Invoke();
        LogEmitted?.Invoke($"[{channel.Name}] หยุดการเฝ้าดู");
    }

    public void ShutdownAll()
    {
        foreach (var channel in _settings.Channels)
        {
            _channelWatcher.Stop(channel.Id);
            channel.EnableAutoScan = false;
            channel.Status = SessionState.Idle;
            channel.LastStatusMessage = "พร้อม";
            channel.LastCheckedAt = null;
        }

        _settingsStore.Save(_settings);
        ChannelsChanged?.Invoke();
    }

    public void AddOrUpdateChannel(ChannelProfile channel)
    {
        var existing = _settings.Channels.FirstOrDefault(x => x.Id == channel.Id);
        if (existing is null)
        {
            _settings.Channels.Add(channel);
        }
        else
        {
            existing.Name = channel.Name;
            existing.ChatLink = channel.ChatLink;
            existing.Enabled = channel.Enabled;
            existing.Prefixes = channel.Prefixes.ToList();
            existing.LastCaptureRegion = channel.LastCaptureRegion;
            existing.EnableAutoScan = channel.EnableAutoScan;
            existing.AutoScanIntervalMs = channel.AutoScanIntervalMs;
        }

        _settingsStore.Save(_settings);
        ChannelsChanged?.Invoke();
    }

    public void RemoveChannel(ChannelProfile channel)
    {
        StopChannel(channel);
        _settings.Channels.RemoveAll(x => x.Id == channel.Id);
        _settingsStore.Save(_settings);
        ChannelsChanged?.Invoke();
    }

    public void SaveSettings()
    {
        _settingsStore.Save(_settings);
        ChannelsChanged?.Invoke();
    }

    private void HandleWatcherStatusChanged(ChannelProfile channel, string message)
    {
        LogEmitted?.Invoke($"[{channel.Name}] {message}");
        ChannelsChanged?.Invoke();
    }

    private void HandleWatcherCodeDispatched(CodeDetectedEvent detectedEvent)
    {
        CodeDispatched?.Invoke(detectedEvent);
    }

    private static string Summarize(Exception ex)
    {
        var message = ex.Message.ReplaceLineEndings(" ").Trim();
        return string.IsNullOrWhiteSpace(message) ? "ไม่ทราบสาเหตุ" : message;
    }

    private static string DescribeWatchSource(string watchSource)
    {
        if (string.IsNullOrWhiteSpace(watchSource))
        {
            return "(ว่าง)";
        }

        if (watchSource.StartsWith("UC", StringComparison.OrdinalIgnoreCase))
        {
            return $"channel-id {watchSource}";
        }

        return watchSource;
    }
}
