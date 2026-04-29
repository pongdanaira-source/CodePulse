using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CodePulse.Enums;
using CodePulse.Models;

namespace CodePulse.Wpf.Windows;

public partial class QuickCaptureLauncherWindow : Window
{
    public QuickCaptureLauncherWindow(IEnumerable<ChannelProfile> channels, ChannelProfile? selectedChannel)
    {
        InitializeComponent();

        var availableChannels = channels
            .OrderBy(static channel => channel.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ChannelComboBox.ItemsSource = availableChannels;
        SelectedChannel = availableChannels.FirstOrDefault(channel => channel.Id == selectedChannel?.Id)
            ?? availableChannels.FirstOrDefault();
        ChannelComboBox.SelectedItem = SelectedChannel;
        UpdateActionButtons();
    }

    public event EventHandler<QuickCaptureActionRequestedEventArgs>? ActionRequested;

    public ChannelProfile? SelectedChannel { get; private set; }

    public void SetInteractionEnabled(bool isEnabled)
    {
        ChannelComboBox.IsEnabled = isEnabled;
        CancelButton.IsEnabled = isEnabled;
        UpdateActionButtons(isEnabled);
    }

    private void CaptureButton_OnClick(object sender, RoutedEventArgs e)
    {
        RaiseActionRequested(QuickCaptureAction.Capture);
    }

    private void ScanButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedChannel?.Status is SessionState.OcrScanning or SessionState.OcrCooldown)
        {
            RaiseActionRequested(QuickCaptureAction.StopScan);
            return;
        }

        RaiseActionRequested(QuickCaptureAction.Scan);
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ChannelComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedChannel = ChannelComboBox.SelectedItem as ChannelProfile;
        UpdateActionButtons();
    }

    private void RaiseActionRequested(QuickCaptureAction action)
    {
        SelectedChannel = ChannelComboBox.SelectedItem as ChannelProfile;
        if (SelectedChannel is null)
        {
            return;
        }

        ActionRequested?.Invoke(this, new QuickCaptureActionRequestedEventArgs(action, SelectedChannel));
    }

    private void UpdateActionButtons(bool? interactionEnabledOverride = null)
    {
        var isEnabled = interactionEnabledOverride ?? true;
        var hasChannel = SelectedChannel is not null;

        CaptureButton.IsEnabled = isEnabled && hasChannel;
        ScanButton.IsEnabled = isEnabled && hasChannel;
        ScanButton.Content = SelectedChannel?.Status is SessionState.OcrScanning or SessionState.OcrCooldown
            ? "Stop scan"
            : "Scan";
    }
}

public enum QuickCaptureAction
{
    Capture,
    Scan,
    StopScan
}

public sealed class QuickCaptureActionRequestedEventArgs : EventArgs
{
    public QuickCaptureActionRequestedEventArgs(QuickCaptureAction action, ChannelProfile channel)
    {
        Action = action;
        Channel = channel;
    }

    public QuickCaptureAction Action { get; }

    public ChannelProfile Channel { get; }
}
