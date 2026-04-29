namespace CodePulse.Services;

public sealed class ChannelDuplicateGuard
{
    private readonly Dictionary<Guid, HashSet<string>> _seenCodesByChannel = new();
    private readonly object _sync = new();

    public bool TryRegister(Guid channelId, string code)
    {
        lock (_sync)
        {
            if (!_seenCodesByChannel.TryGetValue(channelId, out var codes))
            {
                codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _seenCodesByChannel[channelId] = codes;
            }

            return codes.Add(code);
        }
    }

    public bool Contains(Guid channelId, string code)
    {
        lock (_sync)
        {
            return _seenCodesByChannel.TryGetValue(channelId, out var codes) &&
                   codes.Contains(code);
        }
    }

    public void Unregister(Guid channelId, string code)
    {
        lock (_sync)
        {
            if (!_seenCodesByChannel.TryGetValue(channelId, out var codes))
            {
                return;
            }

            codes.Remove(code);
            if (codes.Count == 0)
            {
                _seenCodesByChannel.Remove(channelId);
            }
        }
    }

    public void Reset(Guid channelId)
    {
        lock (_sync)
        {
            _seenCodesByChannel.Remove(channelId);
        }
    }
}
