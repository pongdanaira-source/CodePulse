using System.Drawing;
using CodePulse.Models;

namespace CodePulse.Dispatchers;

public sealed class FacebookDispatcher
{
    private readonly AppSettings _settings;

    public FacebookDispatcher(AppSettings settings)
    {
        _settings = settings;
    }

    public Task<DesktopDispatchResult> TryDispatchAsync(CodeDetectedEvent detectedEvent, CancellationToken cancellationToken)
    {
        return DesktopAutomationHelper.DispatchToWindowAsync(
            detectedEvent.Candidate.Value,
            _settings.Dispatch.PasteDelayMs,
            _settings.Dispatch.EnterAfterPaste,
            static window =>
                window.Title.Contains("Facebook", StringComparison.OrdinalIgnoreCase) ||
                window.Title.Contains("Messenger", StringComparison.OrdinalIgnoreCase),
            cancellationToken,
            focusPoints:
            [
                new PointF(0.50f, 0.975f),
                new PointF(0.58f, 0.975f),
                new PointF(0.42f, 0.975f),
                new PointF(0.50f, 0.94f)
            ],
            typeFallback: true,
            moveCursorToBottomOnComplete: true,
            completionCursorPoint: new PointF(0.92f, 0.985f),
            minimizeWindowOnComplete: true,
            dryRun: _settings.Dispatch.EnableDryRun);
    }
}
