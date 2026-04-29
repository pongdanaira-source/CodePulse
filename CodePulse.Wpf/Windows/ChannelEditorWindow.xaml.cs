using System.Windows;
using CodePulse.Models;
using CodePulse.Services;

namespace CodePulse.Wpf.Windows;

public partial class ChannelEditorWindow : Window
{
    private readonly ChannelProfile _draft;

    public ChannelEditorWindow(ChannelProfile draft)
    {
        InitializeComponent();
        _draft = draft;

        Title = draft.Name.Length == 0 ? "Add channel" : $"Edit channel - {draft.Name}";
        NameTextBox.Text = draft.Name;
        ChatLinkTextBox.Text = draft.ChatLink;
        PrefixesTextBox.Text = string.Join(Environment.NewLine, PrefixRule.ParseMany(draft.Prefixes).Select(static rule => rule.DisplayText));
        EnabledCheckBox.IsChecked = draft.Enabled;
        EnableAutoScanCheckBox.IsChecked = draft.EnableAutoScan;
        AutoScanIntervalTextBox.Text = draft.AutoScanIntervalMs.ToString();
    }

    public ChannelProfile? Result { get; private set; }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryBuildResult(out var result, out var validationMessage))
        {
            ValidationTextBlock.Text = validationMessage;
            ValidationBorder.Visibility = Visibility.Visible;
            return;
        }

        ValidationBorder.Visibility = Visibility.Collapsed;
        Result = result;
        DialogResult = true;
    }

    private bool TryBuildResult(out ChannelProfile result, out string validationMessage)
    {
        result = _draft;
        validationMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            validationMessage = "Please enter a channel name.";
            return false;
        }

        if (!int.TryParse(AutoScanIntervalTextBox.Text.Trim(), out var autoScanIntervalMs) || autoScanIntervalMs < 200 || autoScanIntervalMs > 10000)
        {
            validationMessage = "Auto-scan interval must be between 200 and 10000 ms.";
            return false;
        }

        var watchSource = ChatLinkTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(watchSource) && !ChatLinkService.TryNormalize(watchSource, out _))
        {
            validationMessage = "Watch source must be a YouTube live_chat link, a watch URL, or a channel ID that starts with UC.";
            return false;
        }

        var rawPrefixes = PrefixesTextBox.Text
            .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var invalidPrefix = rawPrefixes.FirstOrDefault(static value => !PrefixRule.TryParse(value, out _));
        if (invalidPrefix is not null)
        {
            validationMessage = $"Invalid prefix format: {invalidPrefix}";
            return false;
        }

        result.Name = NameTextBox.Text.Trim();
        result.ChatLink = watchSource;
        result.Prefixes = PrefixRule.ParseMany(rawPrefixes)
            .Select(static rule => rule.DisplayText)
            .ToList();
        result.Enabled = EnabledCheckBox.IsChecked == true;
        result.EnableAutoScan = EnableAutoScanCheckBox.IsChecked == true;
        result.AutoScanIntervalMs = autoScanIntervalMs;

        return true;
    }
}
