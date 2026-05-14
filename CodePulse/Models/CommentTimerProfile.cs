namespace CodePulse.Models;

public sealed class CommentTimerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ChannelId { get; set; }

    public string VideoUrl { get; set; } = string.Empty;

    public string StartTime { get; set; } = "20:00";

    public int DurationSeconds { get; set; } = 300;

    public int PollIntervalSeconds { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public string LastTriggeredDate { get; set; } = string.Empty;

    public string LastStatus { get; set; } = "Waiting";
}
