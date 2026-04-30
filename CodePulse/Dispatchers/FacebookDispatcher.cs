using System.Drawing;
using System.Text.RegularExpressions;
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
        var targetUrl = _settings.Dispatch.FacebookTargetUrl.Trim();

        return DesktopAutomationHelper.DispatchToWindowAsync(
            detectedEvent.Candidate.Value,
            _settings.Dispatch.PasteDelayMs,
            _settings.Dispatch.EnterAfterPaste,
            window => MatchesTarget(window, targetUrl),
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

    private static bool MatchesTarget(WindowHandleInfo window, string targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            return window.Title.Contains("Facebook", StringComparison.OrdinalIgnoreCase) ||
                   window.Title.Contains("Messenger", StringComparison.OrdinalIgnoreCase);
        }

        return DesktopAutomationHelper.TryGetBrowserAddress(window, out var address) &&
               IsSameMessengerTarget(address, targetUrl);
    }

    private static bool IsSameMessengerTarget(string currentAddress, string targetUrl)
    {
        var currentThreadId = ExtractMessengerThreadId(currentAddress);
        var targetThreadId = ExtractMessengerThreadId(targetUrl);
        if (!string.IsNullOrWhiteSpace(currentThreadId) && !string.IsNullOrWhiteSpace(targetThreadId))
        {
            return currentThreadId.Equals(targetThreadId, StringComparison.OrdinalIgnoreCase);
        }

        return NormalizeUrl(currentAddress).Contains(NormalizeUrl(targetUrl), StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractMessengerThreadId(string value)
    {
        var match = Regex.Match(value, @"facebook\.com/messages/t/([^/?#\s]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.TrimEnd('/') : string.Empty;
    }

    private static string NormalizeUrl(string value)
    {
        return value.Trim()
            .TrimEnd('/')
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
