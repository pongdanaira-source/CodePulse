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

    public int BoostTimeoutSeconds { get; set; } = 60;

    public Dictionary<Guid, string> CommentScannerLastVideoUrls { get; set; } = new();

    public List<ChannelProfile> Channels { get; set; } = new();
}
