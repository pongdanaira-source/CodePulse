namespace CodePulse.Models;

public sealed class DispatchSettings
{
    public string TelegramBotToken { get; set; } = string.Empty;

    public string TelegramChatId { get; set; } = string.Empty;

    public bool EnableLine { get; set; }

    public bool EnableFacebook { get; set; }

    public bool EnableSound { get; set; } = true;

    public bool SkipIfWindowNotFound { get; set; } = true;

    public int PasteDelayMs { get; set; } = 300;

    public bool EnterAfterPaste { get; set; } = true;

    public bool EnableDesktopTargetVerification { get; set; }

    public string LineTargetTitleKeyword { get; set; } = "LINE";

    public string LineTargetWindowTitle { get; set; } = string.Empty;

    public string FacebookTargetTitleKeyword { get; set; } = "Messenger";

    public string FacebookTargetUrl { get; set; } = string.Empty;

    public bool EnableDryRun { get; set; }

    public bool SendManualCaptureImage { get; set; }

    public bool SaveManualCaptureImageToTempInDryRun { get; set; }
}
