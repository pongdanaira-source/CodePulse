namespace CodePulse.Models;

public sealed class AppLogEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public string Message { get; init; } = string.Empty;

    public bool UseAccentStyle =>
        Message.Equals("WPF shell initialized", StringComparison.Ordinal) ||
        Message.StartsWith("Settings file:", StringComparison.Ordinal) ||
        Message.StartsWith("Loaded channels:", StringComparison.Ordinal) ||
        Message.StartsWith("Dry run session log:", StringComparison.Ordinal) ||
        Message.Contains("Start capture from", StringComparison.Ordinal);
}
