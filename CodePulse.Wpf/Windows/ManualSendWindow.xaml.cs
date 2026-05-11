using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CodePulse.Models;

namespace CodePulse.Wpf.Windows;

public partial class ManualSendWindow : Window
{
    private readonly Func<ChannelProfile, string, IReadOnlyList<CodeCandidate>> _extractCandidates;
    private readonly Func<ChannelProfile, string, Task<OwnerTextProcessingResult>> _sendManualCodeAsync;
    private IReadOnlyList<CodeCandidate> _lastCandidates = Array.Empty<CodeCandidate>();
    private bool _isSending;

    public ManualSendWindow(
        IEnumerable<ChannelProfile> channels,
        ChannelProfile? selectedChannel,
        Func<ChannelProfile, string, IReadOnlyList<CodeCandidate>> extractCandidates,
        Func<ChannelProfile, string, Task<OwnerTextProcessingResult>> sendManualCodeAsync)
    {
        InitializeComponent();

        _extractCandidates = extractCandidates;
        _sendManualCodeAsync = sendManualCodeAsync;

        var availableChannels = channels
            .OrderBy(static channel => channel.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ChannelComboBox.ItemsSource = availableChannels;
        ChannelComboBox.SelectedItem = availableChannels.FirstOrDefault(channel => channel.Id == selectedChannel?.Id)
            ?? availableChannels.FirstOrDefault();

        UpdateChannelDetails();
        UpdatePreview();
    }

    private ChannelProfile? SelectedChannel => ChannelComboBox.SelectedItem as ChannelProfile;

    private async void SendButton_OnClick(object sender, RoutedEventArgs e)
    {
        await SendCurrentAsync();
    }

    private async Task SendCurrentAsync()
    {
        if (SelectedChannel is not { } channel || string.IsNullOrWhiteSpace(InputTextBox.Text))
        {
            return;
        }

        SetSending(true);
        try
        {
            var result = await _sendManualCodeAsync(channel, InputTextBox.Text);
            StatusTextBlock.Text = BuildStatusTitle(result.Status);
            StatusHintTextBlock.Text = BuildStatusHint(result);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Failed";
            StatusHintTextBlock.Text = ex.Message;
        }
        finally
        {
            SetSending(false);
            UpdatePreview();
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void CopyPrefixButton_OnClick(object sender, RoutedEventArgs e)
    {
        var prefixText = SelectedPrefixTextBlock.Text.Trim();
        if (!string.IsNullOrWhiteSpace(prefixText) && prefixText != "-")
        {
            Clipboard.SetText(prefixText);
            StatusTextBlock.Text = "Copied";
            StatusHintTextBlock.Text = "Channel prefixes copied to clipboard.";
        }
    }

    private void ChannelComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateChannelDetails();
        UpdatePreview();
    }

    private void InputTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePreview();
    }

    private async void InputTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _isSending || !SendButton.IsEnabled)
        {
            return;
        }

        e.Handled = true;
        await SendCurrentAsync();
    }

    private void UpdateChannelDetails()
    {
        if (SelectedChannel is not { } channel)
        {
            SelectedPrefixTextBlock.Text = "-";
            SelectedPrefixModeTextBlock.Text = "No channel";
            return;
        }

        SelectedPrefixTextBlock.Text = string.IsNullOrWhiteSpace(channel.PrefixDisplay)
            ? "-"
            : channel.PrefixDisplay;

        if (channel.PrefixOnly)
        {
            SelectedPrefixModeTextBlock.Text = "Prefix only";
            return;
        }

        SelectedPrefixModeTextBlock.Text = channel.Prefixes.Count > 0
            ? "Prefix + generic"
            : "Generic only";
    }

    private void UpdatePreview()
    {
        if (SendButton is null)
        {
            return;
        }

        if (SelectedChannel is not { } channel)
        {
            _lastCandidates = Array.Empty<CodeCandidate>();
            PreviewTextBlock.Text = "Choose a channel first.";
            SendButton.IsEnabled = false;
            return;
        }

        var input = InputTextBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            _lastCandidates = Array.Empty<CodeCandidate>();
            PreviewTextBlock.Text = channel.PrefixOnly
                ? "Paste a code/message. Generic codes without this channel prefix will be blocked."
                : "Paste a code/message to preview what will be sent.";
            SendButton.IsEnabled = false;
            return;
        }

        _lastCandidates = _extractCandidates(channel, input);
        if (_lastCandidates.Count == 0)
        {
            PreviewTextBlock.Text = channel.PrefixOnly
                ? "No match for this channel prefix-only rule."
                : "No code matched this channel's rules.";
            SendButton.IsEnabled = false;
            return;
        }

        var previewCodes = _lastCandidates
            .Take(5)
            .Select(static candidate => candidate.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        PreviewTextBlock.Text = previewCodes.Count == _lastCandidates.Count
            ? $"Will send: {string.Join(", ", previewCodes)}"
            : $"Will send: {string.Join(", ", previewCodes)} + {_lastCandidates.Count - previewCodes.Count} more";
        SendButton.IsEnabled = !_isSending;
    }

    private void SetSending(bool isSending)
    {
        _isSending = isSending;
        ChannelComboBox.IsEnabled = !isSending;
        InputTextBox.IsEnabled = !isSending;
        SendButton.Content = isSending ? "Sending..." : "Send";
        SendButton.IsEnabled = !isSending && _lastCandidates.Count > 0;
    }

    private static string BuildStatusTitle(OwnerTextProcessingStatus status)
    {
        return status switch
        {
            OwnerTextProcessingStatus.Dispatched => "Sent",
            OwnerTextProcessingStatus.AlreadySentToday => "Skipped",
            OwnerTextProcessingStatus.Duplicate => "Skipped",
            OwnerTextProcessingStatus.DispatchFailed => "Failed",
            OwnerTextProcessingStatus.NoCode => "No match",
            OwnerTextProcessingStatus.TooShort => "Too short",
            OwnerTextProcessingStatus.NoText => "Empty",
            _ => status.ToString()
        };
    }

    private static string BuildStatusHint(OwnerTextProcessingResult result)
    {
        var codes = result.Codes.Count > 0
            ? string.Join(", ", result.Codes)
            : result.Code ?? string.Empty;

        return result.Status switch
        {
            OwnerTextProcessingStatus.Dispatched => $"Sent: {codes}",
            OwnerTextProcessingStatus.AlreadySentToday => $"Already sent today: {codes}",
            OwnerTextProcessingStatus.Duplicate => $"Duplicate in this session: {codes}",
            OwnerTextProcessingStatus.DispatchFailed => result.Message ?? "No destination reported success.",
            OwnerTextProcessingStatus.NoCode => "No code matched the selected channel rules.",
            OwnerTextProcessingStatus.TooShort => "Text is too short to contain a code.",
            OwnerTextProcessingStatus.NoText => "Paste a code or source message first.",
            _ => result.Message ?? result.Status.ToString()
        };
    }
}
