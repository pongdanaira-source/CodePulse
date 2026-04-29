namespace CodePulse.Models;

public sealed class OwnerTextProcessingResult
{
    public OwnerTextProcessingStatus Status { get; init; }

    public string? Code { get; init; }

    public string? Message { get; init; }
}

public enum OwnerTextProcessingStatus
{
    NoText,
    TooShort,
    NoCode,
    LowConfidence,
    Ambiguous,
    Suspicious,
    AlreadySentToday,
    Duplicate,
    Dispatched,
    DispatchFailed
}
