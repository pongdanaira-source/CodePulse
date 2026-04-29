using System.Drawing;
using CodePulse.Models;

namespace CodePulse.Dispatchers;

public sealed class LineDispatcher
{
    private readonly AppSettings _settings;

    public LineDispatcher(AppSettings settings)
    {
        _settings = settings;
    }

    public Task<DesktopDispatchResult> TryDispatchAsync(CodeDetectedEvent detectedEvent, CancellationToken cancellationToken)
    {
        return DesktopAutomationHelper.DispatchToWindowAsync(
            detectedEvent.Candidate.Value,
            _settings.Dispatch.PasteDelayMs,
            _settings.Dispatch.EnterAfterPaste,
            static window => string.Equals(window.ProcessName, "LINE", StringComparison.OrdinalIgnoreCase),
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
