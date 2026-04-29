using CodePulse.Dispatchers;
using CodePulse.Models;

namespace CodePulse.Services;

public sealed class DispatchService
{
    private static readonly SemaphoreSlim DesktopDispatchSemaphore = new(1, 1);

    private readonly AppSettings _settings;
    private readonly SoundAlertService _soundAlertService;
    private readonly TelegramDispatcher _telegramDispatcher;
    private readonly LineDispatcher _lineDispatcher;
    private readonly FacebookDispatcher _facebookDispatcher;
    private Func<bool> _desktopDispatchEvaluator = static () => true;
    private Func<bool> _hasActiveBoost = static () => false;
    private Func<Guid, bool> _isBoostedChannel = static _ => false;

    public DispatchService(
        AppSettings settings,
        SoundAlertService soundAlertService,
        TelegramDispatcher telegramDispatcher,
        LineDispatcher lineDispatcher,
        FacebookDispatcher facebookDispatcher)
    {
        _settings = settings;
        _soundAlertService = soundAlertService;
        _telegramDispatcher = telegramDispatcher;
        _lineDispatcher = lineDispatcher;
        _facebookDispatcher = facebookDispatcher;

        _soundAlertService.LogEmitted += message => LogEmitted?.Invoke(message);
    }

    public event Action<string>? LogEmitted;

    public void SetDesktopDispatchEvaluator(Func<bool> evaluator)
    {
        _desktopDispatchEvaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    }

    public void SetBoostEvaluator(Func<bool> hasActiveBoost, Func<Guid, bool> isBoostedChannel)
    {
        _hasActiveBoost = hasActiveBoost ?? throw new ArgumentNullException(nameof(hasActiveBoost));
        _isBoostedChannel = isBoostedChannel ?? throw new ArgumentNullException(nameof(isBoostedChannel));
    }

    public async Task<bool> DispatchAsync(CodeDetectedEvent detectedEvent, CancellationToken cancellationToken)
    {
        return await DispatchCoreAsync(detectedEvent, cancellationToken, includeSound: true);
    }

    public async Task<bool> TestDispatchAsync(CancellationToken cancellationToken)
    {
        var dryRunWasEnabled = _settings.Dispatch.EnableDryRun;
        _settings.Dispatch.EnableDryRun = true;

        var detectedEvent = new CodeDetectedEvent
        {
            Channel = new ChannelProfile { Name = "ทดสอบระบบ" },
            Candidate = new CodeCandidate { Value = "TEST1234" },
            SourceMessage = "ข้อความทดสอบจาก CodePulse"
        };

        try
        {
            return await DispatchCoreAsync(detectedEvent, cancellationToken, includeSound: false);
        }
        finally
        {
            _settings.Dispatch.EnableDryRun = dryRunWasEnabled;
        }
    }

    private async Task<bool> DispatchCoreAsync(
        CodeDetectedEvent detectedEvent,
        CancellationToken cancellationToken,
        bool includeSound)
    {
        if (includeSound && !_settings.Dispatch.EnableDryRun)
        {
            if (_settings.Dispatch.EnableSound)
            {
                _ = RunSoundInBackgroundAsync(cancellationToken);
            }
        }

        var pendingTasks = new List<Task<bool>>
        {
            DispatchTelegramAsync(detectedEvent, cancellationToken),
            DispatchDesktopAsync(detectedEvent, cancellationToken)
        };

        var results = await Task.WhenAll(pendingTasks);
        return results.Any(static success => success);
    }

    private async Task RunSoundInBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _soundAlertService.PlayAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // ปล่อยผ่านเมื่อถูกยกเลิก
        }
        catch (Exception ex)
        {
            LogEmitted?.Invoke($"เล่นเสียงแจ้งเตือนไม่สำเร็จ: {ex.Message}");
        }
    }

    private async Task<bool> DispatchTelegramAsync(CodeDetectedEvent detectedEvent, CancellationToken cancellationToken)
    {
        if (_settings.Dispatch.EnableDryRun)
        {
            if (_settings.Dispatch.SendManualCaptureImage &&
                !string.IsNullOrWhiteSpace(detectedEvent.CapturedImagePath))
            {
                LogEmitted?.Invoke($"[Telegram] DryRun: จะส่ง {detectedEvent.Candidate.Value} พร้อมภาพ {detectedEvent.CapturedImagePath}");
                return true;
            }

            LogEmitted?.Invoke($"[Telegram] DryRun: จะส่ง {detectedEvent.Candidate.Value}");
            return true;
        }

        try
        {
            await _telegramDispatcher.DispatchAsync(detectedEvent, cancellationToken);
            LogEmitted?.Invoke("ส่ง Telegram สำเร็จ");
            return true;
        }
        catch (Exception ex)
        {
            LogEmitted?.Invoke($"ส่ง Telegram ล้มเหลว: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> DispatchDesktopAsync(CodeDetectedEvent detectedEvent, CancellationToken cancellationToken)
    {
        if (!_desktopDispatchEvaluator())
        {
            return false;
        }

        if (_hasActiveBoost() && !_isBoostedChannel(detectedEvent.Channel.Id))
        {
            await Task.Delay(750, cancellationToken);
        }

        await DesktopDispatchSemaphore.WaitAsync(cancellationToken);
        try
        {
            var anySuccess = false;

            if (_settings.Dispatch.EnableLine)
            {
                var result = await _lineDispatcher.TryDispatchAsync(detectedEvent, cancellationToken);
                anySuccess |= HandleDesktopResult(
                    result,
                    targetName: "LINE",
                    notFoundMessage: "[LINE] ไม่พบ LINE ที่เปิดอยู่");
            }

            if (_settings.Dispatch.EnableFacebook)
            {
                var result = await _facebookDispatcher.TryDispatchAsync(detectedEvent, cancellationToken);
                anySuccess |= HandleDesktopResult(
                    result,
                    targetName: "Facebook",
                    notFoundMessage: "[Facebook] ไม่พบ Facebook/Messenger ที่เปิดอยู่");
            }

            return anySuccess;
        }
        finally
        {
            DesktopDispatchSemaphore.Release();
        }
    }

    private bool HandleDesktopResult(
        DesktopDispatchResult result,
        string targetName,
        string notFoundMessage)
    {
        if (result.WindowFound && !string.IsNullOrWhiteSpace(result.WindowTitle))
        {
            var processName = string.IsNullOrWhiteSpace(result.WindowProcessName)
                ? string.Empty
                : $" ({result.WindowProcessName})";
            LogEmitted?.Invoke($"[{targetName}] พบหน้าต่าง: {result.WindowTitle}{processName}");
        }

        if (result.VerificationSkipped)
        {
            LogEmitted?.Invoke($"[{targetName}] ข้ามการส่ง เพราะหน้าต่างไม่ตรง");
            return false;
        }

        if (!result.WindowFound)
        {
            if (_settings.Dispatch.SkipIfWindowNotFound)
            {
                LogEmitted?.Invoke(notFoundMessage);
                return false;
            }

            LogEmitted?.Invoke($"{notFoundMessage} และไม่ได้ตั้งค่าให้ข้าม");
            return false;
        }

        if (result.DryRun)
        {
            LogEmitted?.Invoke($"[{targetName}] DryRun: จะส่งโค้ด");
            return true;
        }

        if (result.Success)
        {
            LogEmitted?.Invoke($"[{targetName}] ส่งสำเร็จ");
            return true;
        }

        LogEmitted?.Invoke($"[{targetName}] ส่งล้มเหลว{(string.IsNullOrWhiteSpace(result.ErrorMessage) ? string.Empty : $": {result.ErrorMessage}")}");
        return false;
    }
}
