namespace CodePulse.Models;

public sealed class CodeDetectedEvent
{
    public ChannelProfile Channel { get; set; } = new();

    public LiveSession Session { get; set; } = new();

    public CodeCandidate Candidate { get; set; } = new();

    public string SourceMessage { get; set; } = string.Empty;

    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.Now;

    public string? CapturedImagePath { get; set; }

    public bool IsOcrSource { get; set; }
}
