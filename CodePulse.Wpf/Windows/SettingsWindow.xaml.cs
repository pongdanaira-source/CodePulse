using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using CodePulse.Dispatchers;
using CodePulse.Models;
using CodePulse.Enums;
using CodePulse.Services;

namespace CodePulse.Wpf.Windows;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _draft;
    private readonly Func<AppSettings, Task<bool>> _testDispatchAsync;
    private readonly Func<IReadOnlyList<WindowHandleInfo>> _getLineTargetWindows;
    private readonly Action<WindowHandleInfo> _selectLineTargetWindow;
    private readonly Action _clearLineTargetWindow;
    private readonly Func<string> _getLineTargetWindowText;
    private readonly List<TextBox> _youTubeBackupKeyTextBoxes = new();

    public SettingsWindow(
        AppSettings draft,
        Func<AppSettings, Task<bool>> testDispatchAsync,
        Func<IReadOnlyList<WindowHandleInfo>> getLineTargetWindows,
        Action<WindowHandleInfo> selectLineTargetWindow,
        Action clearLineTargetWindow,
        Func<string> getLineTargetWindowText)
    {
        InitializeComponent();
        _draft = draft;
        _testDispatchAsync = testDispatchAsync;
        _getLineTargetWindows = getLineTargetWindows;
        _selectLineTargetWindow = selectLineTargetWindow;
        _clearLineTargetWindow = clearLineTargetWindow;
        _getLineTargetWindowText = getLineTargetWindowText;

        BindFromSettings(_draft);
    }

    public AppSettings? Result { get; private set; }

    private void BindFromSettings(AppSettings settings)
    {
        TelegramBotTokenTextBox.Text = settings.Dispatch.TelegramBotToken;
        TelegramChatIdTextBox.Text = settings.Dispatch.TelegramChatId;
        EnableSoundCheckBox.IsChecked = settings.Dispatch.EnableSound;
        EnableLineCheckBox.IsChecked = settings.Dispatch.EnableLine;
        EnableFacebookCheckBox.IsChecked = settings.Dispatch.EnableFacebook;
        SkipIfWindowNotFoundCheckBox.IsChecked = settings.Dispatch.SkipIfWindowNotFound;
        EnterAfterPasteCheckBox.IsChecked = settings.Dispatch.EnterAfterPaste;
        EnableDryRunCheckBox.IsChecked = settings.Dispatch.EnableDryRun;
        SendManualCaptureImageCheckBox.IsChecked = settings.Dispatch.SendManualCaptureImage;
        SaveManualCaptureImageToTempInDryRunCheckBox.IsChecked = settings.Dispatch.SaveManualCaptureImageToTempInDryRun;
        PasteDelayTextBox.Text = settings.Dispatch.PasteDelayMs.ToString();
        FacebookTargetUrlTextBox.Text = settings.Dispatch.FacebookTargetUrl;
        RefreshLineTargetText();
        EnableOcrDebugLogCheckBox.IsChecked = settings.EnableOcrDebugLog;
        EnableOcrSpaceFallbackCheckBox.IsChecked = settings.EnableOcrSpaceFallback;
        OcrSpaceApiKeyTextBox.Text = settings.OcrSpaceApiKey;
        OcrSpaceLanguageTextBox.Text = settings.OcrSpaceLanguage;
        YouTubeApiKeyTextBox.Text = settings.YouTubeApiKey;
        BoostTimeoutTextBox.Text = settings.BoostTimeoutSeconds.ToString();
        BindYouTubeBackupKeys(settings.YouTubeApiBackupKeys);
    }

    private void BindYouTubeBackupKeys(IReadOnlyList<string> backupKeys)
    {
        YouTubeBackupKeysGrid.Children.Clear();
        _youTubeBackupKeyTextBoxes.Clear();

        for (var index = 0; index < 10; index++)
        {
            var textBox = new TextBox
            {
                Margin = index % 2 == 0
                    ? new Thickness(0, 0, 8, 8)
                    : new Thickness(8, 0, 0, 8),
                Text = index < backupKeys.Count ? backupKeys[index] : string.Empty
            };

            _youTubeBackupKeyTextBoxes.Add(textBox);
            YouTubeBackupKeysGrid.Children.Add(textBox);
        }
    }

    private static void CopySettings(AppSettings source, AppSettings destination)
    {
        destination.Dispatch.TelegramBotToken = source.Dispatch.TelegramBotToken;
        destination.Dispatch.TelegramChatId = source.Dispatch.TelegramChatId;
        destination.Dispatch.EnableSound = source.Dispatch.EnableSound;
        destination.Dispatch.EnableLine = source.Dispatch.EnableLine;
        destination.Dispatch.EnableFacebook = source.Dispatch.EnableFacebook;
        destination.Dispatch.SkipIfWindowNotFound = source.Dispatch.SkipIfWindowNotFound;
        destination.Dispatch.EnterAfterPaste = source.Dispatch.EnterAfterPaste;
        destination.Dispatch.EnableDesktopTargetVerification = false;
        destination.Dispatch.EnableDryRun = source.Dispatch.EnableDryRun;
        destination.Dispatch.SendManualCaptureImage = source.Dispatch.SendManualCaptureImage;
        destination.Dispatch.SaveManualCaptureImageToTempInDryRun = source.Dispatch.SaveManualCaptureImageToTempInDryRun;
        destination.Dispatch.PasteDelayMs = source.Dispatch.PasteDelayMs;
        destination.Dispatch.LineTargetTitleKeyword = source.Dispatch.LineTargetTitleKeyword;
        destination.Dispatch.FacebookTargetTitleKeyword = source.Dispatch.FacebookTargetTitleKeyword;
        destination.Dispatch.FacebookTargetUrl = source.Dispatch.FacebookTargetUrl;
        destination.EnableOcrDebugLog = source.EnableOcrDebugLog;
        destination.EnableOcrSpaceFallback = source.EnableOcrSpaceFallback;
        destination.OcrSpaceApiKey = source.OcrSpaceApiKey;
        destination.OcrSpaceLanguage = source.OcrSpaceLanguage;
        destination.YouTubeApiKey = source.YouTubeApiKey;
        destination.YouTubeApiBackupKeys = source.YouTubeApiBackupKeys.ToList();
        destination.BoostTimeoutSeconds = source.BoostTimeoutSeconds;
        destination.CommentScannerLastVideoUrls = new Dictionary<Guid, string>(source.CommentScannerLastVideoUrls);
        destination.Channels = source.Channels.Select(CloneChannel).ToList();
    }

    private static ChannelProfile CloneChannel(ChannelProfile source)
    {
        return new ChannelProfile
        {
            Id = source.Id,
            Name = source.Name,
            ChatLink = source.ChatLink,
            Enabled = source.Enabled,
            Prefixes = source.Prefixes.ToList(),
            LastCaptureRegion = source.LastCaptureRegion,
            EnableAutoScan = false,
            AutoScanIntervalMs = source.AutoScanIntervalMs,
            Status = SessionState.Idle,
            LastStatusMessage = "พร้อม",
            LastCheckedAt = null
        };
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void PickLineWindowButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new LineWindowPickerWindow(_getLineTargetWindows)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.SelectedWindow is WindowHandleInfo selectedWindow)
        {
            _selectLineTargetWindow(selectedWindow);
            RefreshLineTargetText();
        }
    }

    private void ClearLineWindowButton_OnClick(object sender, RoutedEventArgs e)
    {
        _clearLineTargetWindow();
        RefreshLineTargetText();
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryBuildResult(out var result, out var validationMessage))
        {
            ShowStatus(validationMessage, MessageBoxImage.Warning);
            return;
        }

        Result = result;
        DialogResult = true;
    }

    private async void TestDispatchButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryBuildResult(out var result, out var validationMessage))
        {
            ShowStatus(validationMessage, MessageBoxImage.Warning);
            return;
        }

        StatusTextBlock.Text = "Testing dispatch...";
        var success = await _testDispatchAsync(result);
        ShowStatus(
            success
                ? "Dry-run dispatch test succeeded. No real message was sent."
                : "Dry-run dispatch test failed. Check the live log for details.",
            success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void ImportButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import settings",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(dialog.FileName);
            if (!SettingsStore.TryDeserialize(json, out var imported))
            {
                ShowStatus("Import failed: invalid settings file.", MessageBoxImage.Warning);
                return;
            }

            CopySettings(imported, _draft);
            BindFromSettings(_draft);
            ShowStatus($"Imported settings from {dialog.FileName}", MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowStatus($"Import failed: {ex.Message}", MessageBoxImage.Error);
        }
    }

    private void ExportButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryBuildResult(out var result, out var validationMessage))
        {
            ShowStatus(validationMessage, MessageBoxImage.Warning);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export settings",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = $"codepulse-settings-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            AddExtension = true,
            DefaultExt = ".json",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var json = SettingsStore.Serialize(result);
            File.WriteAllText(dialog.FileName, json);
            ShowStatus($"Exported settings to {dialog.FileName}", MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowStatus($"Export failed: {ex.Message}", MessageBoxImage.Error);
        }
    }

    private void ShowStatus(string message, MessageBoxImage icon)
    {
        StatusTextBlock.Text = message;
        MessageBox.Show(this, message, "Settings", MessageBoxButton.OK, icon);
    }

    private void RefreshLineTargetText()
    {
        LineTargetWindowTextBlock.Text = _getLineTargetWindowText();
    }

    private bool TryBuildResult(out AppSettings result, out string validationMessage)
    {
        result = _draft;
        validationMessage = string.Empty;

        if (!int.TryParse(PasteDelayTextBox.Text.Trim(), out var pasteDelayMs) || pasteDelayMs < 50 || pasteDelayMs > 5000)
        {
            validationMessage = "Paste delay must be between 50 and 5000 ms.";
            return false;
        }

        if (!int.TryParse(BoostTimeoutTextBox.Text.Trim(), out var boostTimeoutSeconds) || boostTimeoutSeconds < 10 || boostTimeoutSeconds > 300)
        {
            validationMessage = "Boost timeout must be between 10 and 300 seconds.";
            return false;
        }

        result.Dispatch.TelegramBotToken = TelegramBotTokenTextBox.Text.Trim();
        result.Dispatch.TelegramChatId = TelegramChatIdTextBox.Text.Trim();
        result.Dispatch.EnableSound = EnableSoundCheckBox.IsChecked == true;
        result.Dispatch.EnableLine = EnableLineCheckBox.IsChecked == true;
        result.Dispatch.EnableFacebook = EnableFacebookCheckBox.IsChecked == true;
        result.Dispatch.SkipIfWindowNotFound = SkipIfWindowNotFoundCheckBox.IsChecked == true;
        result.Dispatch.EnterAfterPaste = EnterAfterPasteCheckBox.IsChecked == true;
        result.Dispatch.EnableDesktopTargetVerification = false;
        result.Dispatch.EnableDryRun = EnableDryRunCheckBox.IsChecked == true;
        result.Dispatch.SendManualCaptureImage = SendManualCaptureImageCheckBox.IsChecked == true;
        result.Dispatch.SaveManualCaptureImageToTempInDryRun = SaveManualCaptureImageToTempInDryRunCheckBox.IsChecked == true;
        result.Dispatch.PasteDelayMs = pasteDelayMs;
        result.Dispatch.FacebookTargetUrl = FacebookTargetUrlTextBox.Text.Trim();
        result.EnableOcrDebugLog = EnableOcrDebugLogCheckBox.IsChecked == true;
        result.EnableOcrSpaceFallback = EnableOcrSpaceFallbackCheckBox.IsChecked == true;
        result.OcrSpaceApiKey = OcrSpaceApiKeyTextBox.Text.Trim();
        result.OcrSpaceLanguage = string.IsNullOrWhiteSpace(OcrSpaceLanguageTextBox.Text)
            ? "eng"
            : OcrSpaceLanguageTextBox.Text.Trim();
        result.YouTubeApiKey = YouTubeApiKeyTextBox.Text.Trim();
        result.YouTubeApiBackupKeys = _youTubeBackupKeyTextBoxes
            .Select(static textBox => textBox.Text.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Take(10)
            .ToList();
        result.BoostTimeoutSeconds = boostTimeoutSeconds;

        return true;
    }
}
