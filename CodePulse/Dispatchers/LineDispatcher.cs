using System.Drawing;
using CodePulse.Models;
using CodePulse.Services;

namespace CodePulse.Dispatchers;

public sealed class LineDispatcher
{
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
            return Task.FromResult(new DesktopDispatchResult
            {
                Success = false,
                WindowFound = false,
                TargetNotSelected = true
            });
        }

        return DesktopAutomationHelper.DispatchToHandleAsync(
            detectedEvent.Candidate.Value,
            _settings.Dispatch.PasteDelayMs,
            _settings.Dispatch.EnterAfterPaste,
            selectedWindow,
            cancellationToken,
            focusPoints:
            [
                new PointF(0.50f, 0.915f),
                new PointF(0.62f, 0.930f),
                new PointF(0.42f, 0.930f),
                new PointF(0.50f, 0.940f)
            ],
            typeFallback: true,
            moveCursorToBottomOnComplete: true,
            completionCursorPoint: new PointF(0.92f, 0.985f),
            minimizeWindowOnComplete: true,
            dryRun: _settings.Dispatch.EnableDryRun);
    }
}
