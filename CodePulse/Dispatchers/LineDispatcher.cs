using System.Drawing;
using CodePulse.Models;
using CodePulse.Services;

namespace CodePulse.Dispatchers;

public sealed class LineDispatcher
{
    private const int MaxLinePasteDelayMs = 120;

    private readonly AppSettings _settings;
    private readonly LineTargetWindowService _lineTargetWindowService;

    public LineDispatcher(AppSettings settings, LineTargetWindowService lineTargetWindowService)
    {
        _settings = settings;
        _lineTargetWindowService = lineTargetWindowService;
    }

    public Task<DesktopDispatchResult> TryDispatchAsync(CodeDetectedEvent detectedEvent, CancellationToken cancellationToken)
    {
        if (!_lineTargetWindowService.TryGetSelectedLiveWindow(out var selectedWindow))
        {
            var savedTitle = _settings.Dispatch.LineTargetWindowTitle.Trim();
            if (string.IsNullOrWhiteSpace(savedTitle) ||
                !_lineTargetWindowService.TryRestoreByTitle(savedTitle, out selectedWindow))
            {
                return Task.FromResult(new DesktopDispatchResult
                {
                    Success = false,
                    WindowFound = false,
                    TargetNotSelected = true
                });
            }
        }

        return DesktopAutomationHelper.DispatchToHandleAsync(
            detectedEvent.Candidate.Value,
            GetLinePasteDelayMs(),
            _settings.Dispatch.EnterAfterPaste,
            selectedWindow,
            cancellationToken,
            focusPoints:
            [
                new PointF(0.50f, 0.930f),
                new PointF(0.50f, 0.940f)
            ],
            typeFallback: false,
            moveCursorToBottomOnComplete: false,
            minimizeWindowOnComplete: true,
            dryRun: _settings.Dispatch.EnableDryRun,
            safePaste: _settings.Dispatch.EnableSafeDesktopPaste,
            sendEscapeBeforePaste: false);
    }

    private int GetLinePasteDelayMs()
    {
        return Math.Clamp(_settings.Dispatch.PasteDelayMs, 50, MaxLinePasteDelayMs);
    }
}
