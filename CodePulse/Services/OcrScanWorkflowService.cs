using CodePulse.Enums;
using CodePulse.Models;
using System.Drawing;
using System.Numerics;

namespace CodePulse.Services;

public sealed class OcrScanWorkflowService
{
    private static readonly TimeSpan IdleProgressLogInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UnchangedFrameRetryInterval = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan ScanOcrSpaceFallbackCooldown = TimeSpan.FromSeconds(12);
    private const int UnchangedFrameLogEvery = 20;
    private const int SameFrameHashDistance = 2;

    private readonly AppSettings _settings;
    private readonly AppLogService _appLogService;
    private readonly WatchCoordinator _watchCoordinator;
    private readonly ScreenCaptureService _screenCaptureService;
    private readonly OcrWorkflowService _ocrWorkflowService;
    private readonly Dictionary<Guid, CancellationTokenSource> _scanTokens = new();
    private readonly object _sync = new();

    public OcrScanWorkflowService(
        AppSettings settings,
        AppLogService appLogService,
        WatchCoordinator watchCoordinator,
        ScreenCaptureService screenCaptureService,
        OcrWorkflowService ocrWorkflowService)
    {
        _settings = settings;
        _appLogService = appLogService;
        _watchCoordinator = watchCoordinator;
        _screenCaptureService = screenCaptureService;
        _ocrWorkflowService = ocrWorkflowService;
    }

    public bool IsScanning(Guid channelId)
    {
        lock (_sync)
        {
            return _scanTokens.ContainsKey(channelId);
        }
    }

    public async Task<bool> StartAsync(ChannelProfile channel, Action refreshUi)
    {
        if (channel.LastCaptureRegion?.IsValid != true)
        {
            _appLogService.Write($"[{channel.Name}] ยังไม่มีพื้นที่จับล่าสุด กรุณากดจับภาพก่อน");
            return false;
        }

        CancellationTokenSource source;
        lock (_sync)
        {
            if (_scanTokens.ContainsKey(channel.Id))
            {
                _appLogService.Write($"[{channel.Name}] กำลังสแกน OCR อยู่แล้ว");
                return false;
            }

            source = new CancellationTokenSource();
            _scanTokens[channel.Id] = source;
        }

        channel.EnableAutoScan = true;
        channel.Status = SessionState.OcrScanning;
        channel.LastStatusMessage = "กำลังสแกน OCR";
        _watchCoordinator.SaveSettings();
        refreshUi();
        _appLogService.Write($"[{channel.Name}] เริ่มสแกน OCR");

        _ = Task.Run(async () =>
        {
            var lastIdleProgressLogAt = DateTimeOffset.MinValue;
            var lastOcrAttemptAt = DateTimeOffset.MinValue;
            var unchangedFrameSkips = 0;
            FrameFingerprint? lastFrameFingerprint = null;

            try
            {
                while (IsActive(channel.Id, source))
                {
                    try
                    {
                        var region = channel.LastCaptureRegion;
                        if (region?.IsValid != true)
                        {
                            _appLogService.Write($"[{channel.Name}] ยังไม่มีพื้นที่จับล่าสุด กรุณากดจับภาพก่อน");
                            break;
                        }

                        using var bitmap = await _screenCaptureService.CaptureAsync(region.ToRectangle(), source.Token);
                        if (!IsActive(channel.Id, source))
                        {
                            break;
                        }

                        var now = DateTimeOffset.Now;
                        var currentFrameFingerprint = CreateFrameFingerprint(bitmap);
                        var isUnchangedFrame = lastFrameFingerprint is { } previousFingerprint &&
                                               IsSameFrame(previousFingerprint, currentFrameFingerprint);
                        var shouldRunOcr = !isUnchangedFrame ||
                                           now - lastOcrAttemptAt >= UnchangedFrameRetryInterval;

                        if (!shouldRunOcr)
                        {
                            unchangedFrameSkips++;
                            if (_settings.EnableOcrDebugLog && unchangedFrameSkips % UnchangedFrameLogEvery == 0)
                            {
                                _appLogService.Write($"[{channel.Name}] OCR scan skipped unchanged frame x{unchangedFrameSkips}");
                            }
                        }
                        else
                        {
                            lastFrameFingerprint = currentFrameFingerprint;
                            lastOcrAttemptAt = now;
                            unchangedFrameSkips = 0;

                            var result = await _ocrWorkflowService.ProcessCapturedBitmapAsync(
                                channel,
                                bitmap,
                                source.Token,
                                _appLogService.Write,
                                logStart: false,
                                suppressNoChangeLogs: !_settings.EnableOcrDebugLog,
                                allowOcrSpaceFallback: true,
                                useOcrSpaceFallbackForNoCode: false,
                                ocrSpaceFallbackCooldown: ScanOcrSpaceFallbackCooldown);

                            if (!IsActive(channel.Id, source))
                            {
                                break;
                            }

                            if (result.Status == OwnerTextProcessingStatus.Dispatched)
                            {
                                lastIdleProgressLogAt = DateTimeOffset.MinValue;
                                channel.Status = SessionState.OcrCooldown;
                                channel.LastStatusMessage = "พัก OCR";
                                refreshUi();
                                await Task.Delay(1500, source.Token);
                            }
                            else if (ShouldLogIdleProgress(result.Status))
                            {
                                now = DateTimeOffset.Now;
                                if (now - lastIdleProgressLogAt >= IdleProgressLogInterval)
                                {
                                    _appLogService.Write($"[{channel.Name}] สแกนอยู่ ยังไม่พบโค้ด");
                                    lastIdleProgressLogAt = now;
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (!IsActive(channel.Id, source))
                        {
                            break;
                        }

                        _appLogService.Write($"[{channel.Name}] OCR ล้มเหลว: {Summarize(ex)}");
                        channel.Status = SessionState.OcrScanning;
                        channel.LastStatusMessage = "กำลังสแกน OCR";
                        refreshUi();
                    }

                    if (!IsActive(channel.Id, source))
                    {
                        break;
                    }

                    channel.Status = SessionState.OcrScanning;
                    channel.LastStatusMessage = "กำลังสแกน OCR";
                    refreshUi();
                    await Task.Delay(Math.Max(200, channel.AutoScanIntervalMs), source.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // normal stop
            }
            finally
            {
                lock (_sync)
                {
                    if (_scanTokens.TryGetValue(channel.Id, out var activeSource) && ReferenceEquals(activeSource, source))
                    {
                        _scanTokens.Remove(channel.Id);
                    }
                }

                if (!channel.EnableAutoScan)
                {
                    channel.Status = SessionState.Stopped;
                    channel.LastStatusMessage = "หยุดสแกน";
                    refreshUi();
                }
            }
        }, source.Token);

        await Task.CompletedTask;
        return true;
    }

    public void Stop(ChannelProfile channel, Action refreshUi, bool emitLog = true)
    {
        var stopped = false;
        lock (_sync)
        {
            if (_scanTokens.TryGetValue(channel.Id, out var source))
            {
                source.Cancel();
                _scanTokens.Remove(channel.Id);
                stopped = true;
            }
        }

        channel.EnableAutoScan = false;
        channel.Status = SessionState.Stopped;
        channel.LastStatusMessage = "หยุดสแกน";
        _watchCoordinator.SaveSettings();
        refreshUi();
        if (emitLog && stopped)
        {
            _appLogService.Write($"[{channel.Name}] หยุดสแกน OCR");
        }
    }

    public void StopAll()
    {
        lock (_sync)
        {
            foreach (var source in _scanTokens.Values)
            {
                source.Cancel();
            }

            _scanTokens.Clear();
        }
    }

    private static FrameFingerprint CreateFrameFingerprint(Bitmap bitmap)
    {
        const int sampleColumns = 9;
        const int sampleRows = 8;
        Span<byte> luminance = stackalloc byte[sampleColumns * sampleRows];
        var totalLuminance = 0;
        var index = 0;

        for (var row = 0; row < sampleRows; row++)
        {
            var y = Math.Clamp((int)Math.Round((row + 0.5d) * bitmap.Height / sampleRows), 0, bitmap.Height - 1);
            for (var column = 0; column < sampleColumns; column++)
            {
                var x = Math.Clamp((int)Math.Round((column + 0.5d) * bitmap.Width / sampleColumns), 0, bitmap.Width - 1);
                var pixel = bitmap.GetPixel(x, y);
                var value = (byte)((pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000);
                luminance[index++] = value;
                totalLuminance += value;
            }
        }

        var hash = 0UL;
        var bitIndex = 0;
        for (var row = 0; row < sampleRows; row++)
        {
            var rowOffset = row * sampleColumns;
            for (var column = 0; column < sampleColumns - 1; column++)
            {
                if (luminance[rowOffset + column] > luminance[rowOffset + column + 1])
                {
                    hash |= 1UL << bitIndex;
                }

                bitIndex++;
            }
        }

        return new FrameFingerprint(hash, totalLuminance / luminance.Length);
    }

    private static bool IsSameFrame(FrameFingerprint previous, FrameFingerprint current)
    {
        var hashDistance = BitOperations.PopCount(previous.Hash ^ current.Hash);
        return hashDistance <= SameFrameHashDistance &&
               Math.Abs(previous.AverageLuminance - current.AverageLuminance) <= 8;
    }

    private bool IsActive(Guid channelId, CancellationTokenSource source)
    {
        if (source.IsCancellationRequested)
        {
            return false;
        }

        lock (_sync)
        {
            return _scanTokens.TryGetValue(channelId, out var activeSource) && ReferenceEquals(activeSource, source);
        }
    }

    private static bool ShouldLogIdleProgress(OwnerTextProcessingStatus status)
    {
        return status is OwnerTextProcessingStatus.NoText
            or OwnerTextProcessingStatus.TooShort
            or OwnerTextProcessingStatus.NoCode
            or OwnerTextProcessingStatus.LowConfidence
            or OwnerTextProcessingStatus.Ambiguous
            or OwnerTextProcessingStatus.Duplicate
            or OwnerTextProcessingStatus.AlreadySentToday;
    }

    private static string Summarize(Exception ex)
    {
        var message = ex.Message.Trim();
        return string.IsNullOrWhiteSpace(message) ? "ไม่ทราบสาเหตุ" : message;
    }

    private readonly record struct FrameFingerprint(ulong Hash, int AverageLuminance);
}
