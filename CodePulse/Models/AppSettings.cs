namespace CodePulse.Models;

public sealed class AppSettings
{
    public DispatchSettings Dispatch { get; set; } = new();

    public bool EnableOcrDebugLog { get; set; }

    public bool EnableOcrSpaceFallback { get; set; }

    public string OcrSpaceApiKey { get; set; } = string.Empty;

    public string OcrSpaceLanguage { get; set; } = "eng";

    public string YouTubeApiKey { get; set; } = string.Empty;

    public List<string> YouTubeApiBackupKeys { get; set; } = new();

    public int YouTubeApiDailyQuotaGuardUnits { get; set; } = 9000;

    public string YouTubeApiHealthCheckLastRunDate { get; set; } = string.Empty;

    public int OcrSpaceDailyRequestGuard { get; set; } = 100;

    public int OcrSpaceHourlyRequestGuard { get; set; } = 20;

    public int BoostTimeoutSeconds { get; set; } = 60;

    public Dictionary<Guid, string> CommentScannerLastVideoUrls { get; set; } = new();

    public List<CommentTimerProfile> CommentTimers { get; set; } = new();

    public List<ChannelProfile> Channels { get; set; } = new();
}
