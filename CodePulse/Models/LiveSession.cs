using CodePulse.Enums;

namespace CodePulse.Models;

public sealed class LiveSession
{
    public ChannelProfile Channel { get; set; } = new();

    public string ChatLink { get; set; } = string.Empty;

    public string? PageTitle { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset LastNewMessageAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset LastDomHealthyAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset LastChatContainerSeenAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? LastNoMessagesAt { get; set; }

    public SessionState State { get; set; } = SessionState.LoadingChat;
}
