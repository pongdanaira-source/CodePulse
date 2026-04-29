using System.Text.Json.Serialization;
using CodePulse.Enums;

namespace CodePulse.Models;

public sealed class ChannelProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string ChatLink { get; set; } = string.Empty;

    [JsonPropertyName("youTubeChannelId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyYouTubeChannelId
    {
        get => null;
        set
        {
            if (string.IsNullOrWhiteSpace(ChatLink) && !string.IsNullOrWhiteSpace(value))
            {
                ChatLink = value;
            }
        }
    }

    public bool Enabled { get; set; } = true;

    public List<string> Prefixes { get; set; } = new();

    public CaptureRegion? LastCaptureRegion { get; set; }

    public bool EnableAutoScan { get; set; }

    public int AutoScanIntervalMs { get; set; } = 500;

    public SessionState Status { get; set; } = SessionState.Idle;

    public string LastStatusMessage { get; set; } = "พร้อม";

    public DateTimeOffset? LastCheckedAt { get; set; }

    public string PrefixDisplay => string.Join(", ", PrefixRule.ParseMany(Prefixes).Select(static rule => rule.DisplayText));

    [JsonIgnore]
    public bool IsBoosting { get; set; }

    [JsonIgnore]
    public DateTimeOffset? BoostExpiresAt { get; set; }

    [JsonIgnore]
    public string BoostButtonText => IsBoosting ? "Boosting" : "Boost";

    [JsonIgnore]
    public bool IsWatchActive => Status is SessionState.LoadingChat or SessionState.Watching or SessionState.NoMessages;

    [JsonIgnore]
    public string WatchBadgeText => Status switch
    {
        SessionState.LoadingChat => "Loading",
        SessionState.Watching => "Watching",
        SessionState.NoMessages => "Watching",
        _ => string.Empty
    };

    [JsonIgnore]
    public bool IsScanActive => Status is SessionState.OcrScanning or SessionState.OcrCooldown;

    [JsonIgnore]
    public string ScanBadgeText => Status switch
    {
        SessionState.OcrScanning => "Scanning",
        SessionState.OcrCooldown => "Cooldown",
        _ => string.Empty
    };

    [JsonIgnore]
    public bool IsCommentScanActive { get; set; }

    [JsonIgnore]
    public bool IsStatusBadgeVisible => Status is SessionState.Idle or SessionState.Stopped or SessionState.Ended or SessionState.Error;

    [JsonIgnore]
    public string StatusBadgeText => Status switch
    {
        SessionState.Idle => "Ready",
        SessionState.Stopped => "Stopped",
        SessionState.Ended => "Ended",
        SessionState.Error => "Error",
        _ => string.Empty
    };
}
