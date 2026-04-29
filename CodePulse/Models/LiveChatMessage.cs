namespace CodePulse.Models;

public sealed class LiveChatMessage
{
    public string MessageId { get; set; } = string.Empty;

    public string AuthorChannelId { get; set; } = string.Empty;

    public string AuthorDisplayName { get; set; } = string.Empty;

    public bool IsChatOwner { get; set; }

    public string DisplayMessage { get; set; } = string.Empty;

    public DateTimeOffset PublishedAt { get; set; }
}
