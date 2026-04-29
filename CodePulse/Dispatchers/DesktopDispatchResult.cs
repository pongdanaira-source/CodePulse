namespace CodePulse.Dispatchers;

public sealed class DesktopDispatchResult
{
    public bool Success { get; init; }

    public bool WindowFound { get; init; }

    public string? WindowTitle { get; init; }

    public string? WindowProcessName { get; init; }

    public string? ErrorMessage { get; init; }

    public bool VerificationSkipped { get; init; }

    public bool DryRun { get; init; }
}
