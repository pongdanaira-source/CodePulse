namespace CodePulse.Models;

public sealed class LiveChatPage
{
    public List<LiveChatMessage> Messages { get; set; } = new();

    public string? NextPageToken { get; set; }

    public int PollingIntervalMilliseconds { get; set; } = 5000;
}
