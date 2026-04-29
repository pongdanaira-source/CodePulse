namespace CodePulse.Models;

public sealed class CodeCandidate
{
    public string Value { get; set; } = string.Empty;

    public int Score { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string SourceMessage { get; set; } = string.Empty;
}
