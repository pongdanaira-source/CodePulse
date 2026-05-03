namespace CodePulse.Wpf;

public sealed record TrayWatchChannelInfo(
    Guid Id,
    string Name,
    bool Enabled,
    bool IsWatching,
    string StatusText);
