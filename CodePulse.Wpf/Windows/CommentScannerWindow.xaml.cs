using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CodePulse.Models;

namespace CodePulse.Wpf.Windows;

public partial class CommentScannerWindow : Window
{
    private readonly Func<ChannelProfile, bool> _isRunning;
    private readonly Func<ChannelProfile, string> _getLastVideoUrl;
    private readonly Func<ChannelProfile, string, TimeSpan, bool> _startScanner;
    private readonly Action<ChannelProfile> _stopScanner;

    public CommentScannerWindow(
        IEnumerable<ChannelProfile> channels,
        ChannelProfile? selectedChannel,
        Func<ChannelProfile, bool> isRunning,
        Func<ChannelProfile, string> getLastVideoUrl,
        Func<ChannelProfile, string, TimeSpan, bool> startScanner,
        Action<ChannelProfile> stopScanner)
    {
        InitializeComponent();

        _isRunning = isRunning;
        _getLastVideoUrl = getLastVideoUrl;
        _startScanner = startScanner;
        _stopScanner = stopScanner;

        var availableChannels = channels
            .OrderBy(static channel => channel.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ChannelComboBox.ItemsSource = availableChannels;
        ChannelComboBox.SelectedItem = availableChannels.FirstOrDefault(channel => channel.Id == selectedChannel?.Id)
            ?? availableChannels.FirstOrDefault();

        PollIntervalComboBox.ItemsSource = new[]
        {
            new PollIntervalOption("Burst 1 sec / 5 min", 1),
            new PollIntervalOption("5 sec", 5),
            new PollIntervalOption("10 sec", 10),
            new PollIntervalOption("20 sec", 20),
            new PollIntervalOption("30 sec", 30),
            new PollIntervalOption("60 sec", 60)
        };
        PollIntervalComboBox.SelectedValue = 1;
        LoadLastVideoUrlForSelectedChannel();

        UpdateButtons();
    }

    private ChannelProfile? SelectedChannel => ChannelComboBox.SelectedItem as ChannelProfile;

    private void StartButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedChannel is null)
        {
            return;
        }

        if (_startScanner(SelectedChannel, VideoUrlTextBox.Text, GetSelectedPollInterval()))
        {
            UpdateButtons();
        }
    }

    private void StopButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedChannel is null)
        {
            return;
        }

        _stopScanner(SelectedChannel);
        UpdateButtons();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ChannelComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LoadLastVideoUrlForSelectedChannel();
        UpdateButtons();
    }

    private void VideoUrlTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateButtons();
    }

    private void PollIntervalComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var hasChannel = SelectedChannel is not null;
        var hasVideo = !string.IsNullOrWhiteSpace(VideoUrlTextBox.Text);
        var isRunning = SelectedChannel is not null && _isRunning(SelectedChannel);

        StartButton.IsEnabled = hasChannel && hasVideo && !isRunning;
        StopButton.IsEnabled = hasChannel && isRunning;
        PollIntervalComboBox.IsEnabled = !isRunning;
        ChannelComboBox.IsEnabled = !isRunning;
        VideoUrlTextBox.IsEnabled = !isRunning;

        if (!hasChannel)
        {
            SelectedStatusTextBlock.Text = "Idle";
            SelectedStatusHintTextBlock.Text = "Choose a channel to load its last video link.";
            return;
        }

        if (isRunning)
        {
            SelectedStatusTextBlock.Text = "Running";
            SelectedStatusHintTextBlock.Text = IsBurstSelected()
                ? $"Burst polling every 1 sec for owner comments on {SelectedChannel!.Name}. Stops after 5 min or 400 requests."
                : $"Polling every {GetSelectedPollInterval().TotalSeconds:0} sec for owner comments on {SelectedChannel!.Name}.";
            return;
        }

        if (!hasVideo)
        {
            SelectedStatusTextBlock.Text = "Waiting";
            SelectedStatusHintTextBlock.Text = "Paste a YouTube link or video id to enable Start.";
            return;
        }

        SelectedStatusTextBlock.Text = "Ready";
        SelectedStatusHintTextBlock.Text = IsBurstSelected()
            ? $"Burst scanner is ready for {SelectedChannel!.Name}: 1 sec, 5 min, 400 request guard."
            : $"Scanner is ready to start for {SelectedChannel!.Name}.";
    }

    private TimeSpan GetSelectedPollInterval()
    {
        return PollIntervalComboBox.SelectedValue is int seconds
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(20);
    }

    private bool IsBurstSelected()
    {
        return PollIntervalComboBox.SelectedValue is int seconds && seconds <= 1;
    }

    private void LoadLastVideoUrlForSelectedChannel()
    {
        VideoUrlTextBox.Text = SelectedChannel is null
            ? string.Empty
            : _getLastVideoUrl(SelectedChannel);
    }

    private sealed record PollIntervalOption(string Label, int Seconds);
}
