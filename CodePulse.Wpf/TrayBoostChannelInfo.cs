namespace CodePulse.Wpf;

public sealed record TrayBoostChannelInfo(
    Guid Id,
    string Name,
    bool Enabled,
    bool IsBoosting,
    string StatusText);
