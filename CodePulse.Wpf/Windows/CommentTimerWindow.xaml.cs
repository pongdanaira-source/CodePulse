using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CodePulse.Models;

namespace CodePulse.Wpf.Windows;

public partial class CommentTimerWindow : Window
{
    private readonly List<ChannelProfile> _channels;
    private readonly Func<IReadOnlyList<CommentTimerProfile>> _getTimers;
    private readonly Action<CommentTimerProfile> _saveTimer;
    private readonly Action<Guid> _deleteTimer;
    private readonly Func<Guid, bool> _startNow;
    private readonly Action<Guid> _stopTimer;
    private readonly Func<CommentTimerProfile, string> _getStatus;
    private readonly ObservableCollection<CommentTimerRow> _rows = new();
    private readonly DispatcherTimer _refreshTimer = new();
    private Guid? _editingTimerId;
    private bool _isLoadingEditor;

    public CommentTimerWindow(
        IEnumerable<ChannelProfile> channels,
        ChannelProfile? selectedChannel,
        Func<IReadOnlyList<CommentTimerProfile>> getTimers,
        Action<CommentTimerProfile> saveTimer,
        Action<Guid> deleteTimer,
        Func<Guid, bool> startNow,
        Action<Guid> stopTimer,
        Func<CommentTimerProfile, string> getStatus)
    {
        InitializeComponent();

        _channels = channels
            .OrderBy(static channel => channel.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _getTimers = getTimers;
        _saveTimer = saveTimer;
        _deleteTimer = deleteTimer;
        _startNow = startNow;
        _stopTimer = stopTimer;
        _getStatus = getStatus;

        ChannelComboBox.ItemsSource = _channels;
        ChannelComboBox.SelectedItem = _channels.FirstOrDefault(channel => channel.Id == selectedChannel?.Id)
            ?? _channels.FirstOrDefault();

        DurationComboBox.ItemsSource = new[]
        {
            new TimeOption("3 min", 180),
            new TimeOption("5 min", 300),
            new TimeOption("10 min", 600),
            new TimeOption("15 min", 900)
        };
        DurationComboBox.SelectedValue = 300;

        PollIntervalComboBox.ItemsSource = new[]
        {
            new TimeOption("1 sec", 1),
            new TimeOption("2 sec", 2),
            new TimeOption("5 sec", 5),
            new TimeOption("10 sec", 10)
        };
        PollIntervalComboBox.SelectedValue = 1;

        TimerListBox.ItemsSource = _rows;
        StartTimeTextBox.Text = DateTime.Now.AddMinutes(1).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        _refreshTimer.Interval = TimeSpan.FromSeconds(2);
        _refreshTimer.Tick += (_, _) => ReloadTimers(keepSelection: true);
        Loaded += (_, _) => _refreshTimer.Start();
        Closed += (_, _) => _refreshTimer.Stop();

        ReloadTimers(keepSelection: false);
        UpdateEditorState();
    }

    private ChannelProfile? SelectedChannel => ChannelComboBox.SelectedItem as ChannelProfile;

    private CommentTimerRow? SelectedRow => TimerListBox.SelectedItem as CommentTimerRow;

    private void ReloadTimers(bool keepSelection)
    {
        var selectedId = keepSelection ? SelectedRow?.Timer.Id ?? _editingTimerId : null;
        var timers = _getTimers();

        _rows.Clear();
        foreach (var timer in timers.OrderBy(static item => item.StartTime, StringComparer.Ordinal))
        {
            var channel = _channels.FirstOrDefault(item => item.Id == timer.ChannelId);
            _rows.Add(new CommentTimerRow(
                timer,
                channel?.Name ?? "(missing channel)",
                timer.StartTime,
                $"{Math.Clamp(timer.DurationSeconds, 30, 3600) / 60.0:0.#}m",
                $"{Math.Clamp(timer.PollIntervalSeconds, 1, 60)}s",
                timer.Enabled ? "On" : "Off",
                _getStatus(timer)));
        }

        if (selectedId is { } timerId)
        {
            TimerListBox.SelectedItem = _rows.FirstOrDefault(row => row.Timer.Id == timerId);
        }

        UpdateEditorState();
    }

    private void LoadTimerIntoEditor(CommentTimerProfile timer)
    {
        _isLoadingEditor = true;
        _editingTimerId = timer.Id;
        ChannelComboBox.SelectedItem = _channels.FirstOrDefault(channel => channel.Id == timer.ChannelId)
            ?? _channels.FirstOrDefault();
        StartTimeTextBox.Text = timer.StartTime;
        DurationComboBox.SelectedValue = Math.Clamp(timer.DurationSeconds, 30, 3600);
        if (DurationComboBox.SelectedItem is null)
        {
            DurationComboBox.SelectedValue = 300;
        }

        PollIntervalComboBox.SelectedValue = Math.Clamp(timer.PollIntervalSeconds, 1, 60);
        if (PollIntervalComboBox.SelectedItem is null)
        {
            PollIntervalComboBox.SelectedValue = 1;
        }

        EnabledCheckBox.IsChecked = timer.Enabled;
        VideoUrlTextBox.Text = timer.VideoUrl;
        _isLoadingEditor = false;
        UpdateEditorState();
    }

    private void ClearEditor()
    {
        _isLoadingEditor = true;
        _editingTimerId = null;
        TimerListBox.SelectedItem = null;
        StartTimeTextBox.Text = DateTime.Now.AddMinutes(1).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        DurationComboBox.SelectedValue = 300;
        PollIntervalComboBox.SelectedValue = 1;
        EnabledCheckBox.IsChecked = true;
        VideoUrlTextBox.Text = string.Empty;
        _isLoadingEditor = false;
        UpdateEditorState();
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedChannel is null)
        {
            return;
        }

        var startTime = NormalizeStartTime(StartTimeTextBox.Text);
        if (string.IsNullOrWhiteSpace(startTime))
        {
            StatusTextBlock.Text = "Invalid";
            StatusHintTextBlock.Text = "Use HH:mm, for example 20:30.";
            return;
        }

        var existing = SelectedRow?.Timer;
        var timer = new CommentTimerProfile
        {
            Id = _editingTimerId ?? Guid.NewGuid(),
            ChannelId = SelectedChannel.Id,
            VideoUrl = VideoUrlTextBox.Text.Trim(),
            StartTime = startTime,
            DurationSeconds = DurationComboBox.SelectedValue is int duration ? duration : 300,
            PollIntervalSeconds = PollIntervalComboBox.SelectedValue is int poll ? poll : 1,
            Enabled = EnabledCheckBox.IsChecked == true,
            LastTriggeredDate = existing?.LastTriggeredDate ?? string.Empty,
            LastStatus = existing?.LastStatus ?? "Waiting"
        };

        _saveTimer(timer);
        _editingTimerId = timer.Id;
        ReloadTimers(keepSelection: true);
        StatusTextBlock.Text = "Saved";
        StatusHintTextBlock.Text = "Timer settings updated.";
    }

    private void DeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is null)
        {
            return;
        }

        _deleteTimer(SelectedRow.Timer.Id);
        ClearEditor();
        ReloadTimers(keepSelection: false);
        StatusTextBlock.Text = "Deleted";
        StatusHintTextBlock.Text = "Timer removed.";
    }

    private void StartNowButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is null)
        {
            return;
        }

        var started = _startNow(SelectedRow.Timer.Id);
        ReloadTimers(keepSelection: true);
        StatusTextBlock.Text = started ? "Running" : "Not started";
        StatusHintTextBlock.Text = started
            ? "Timer is scanning now."
            : "Check log for the reason.";
    }

    private void StopButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is null)
        {
            return;
        }

        _stopTimer(SelectedRow.Timer.Id);
        ReloadTimers(keepSelection: true);
        StatusTextBlock.Text = "Stopped";
        StatusHintTextBlock.Text = "Timer scan stopped.";
    }

    private void NewButton_OnClick(object sender, RoutedEventArgs e)
    {
        ClearEditor();
        StatusTextBlock.Text = "New";
        StatusHintTextBlock.Text = "Create a scheduled scan.";
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TimerListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingEditor || SelectedRow is null)
        {
            UpdateEditorState();
            return;
        }

        LoadTimerIntoEditor(SelectedRow.Timer);
    }

    private void Editor_OnChanged(object sender, RoutedEventArgs e)
    {
        UpdateEditorState();
    }

    private void Editor_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateEditorState();
    }

    private void UpdateEditorState()
    {
        if (_isLoadingEditor ||
            VideoUrlTextBox is null ||
            StartTimeTextBox is null ||
            SaveButton is null ||
            DeleteButton is null ||
            StartNowButton is null ||
            StopButton is null ||
            StatusTextBlock is null ||
            StatusHintTextBlock is null)
        {
            return;
        }

        var hasChannel = SelectedChannel is not null;
        var hasVideo = !string.IsNullOrWhiteSpace(VideoUrlTextBox.Text);
        var hasValidTime = !string.IsNullOrWhiteSpace(NormalizeStartTime(StartTimeTextBox.Text));
        SaveButton.IsEnabled = hasChannel && hasVideo && hasValidTime;
        DeleteButton.IsEnabled = SelectedRow is not null;
        StartNowButton.IsEnabled = SelectedRow is not null;
        StopButton.IsEnabled = SelectedRow is not null;

        if (!hasVideo)
        {
            StatusTextBlock.Text = "Waiting";
            StatusHintTextBlock.Text = "Paste a YouTube link or video id.";
            return;
        }

        if (!hasValidTime)
        {
            StatusTextBlock.Text = "Invalid";
            StatusHintTextBlock.Text = "Use HH:mm start time.";
            return;
        }

        StatusTextBlock.Text = "Ready";
        StatusHintTextBlock.Text = _editingTimerId is null ? "Save to add timer." : "Save to update timer.";
    }

    private static string NormalizeStartTime(string value)
    {
        if (TimeOnly.TryParseExact(
                value,
                "HH:mm",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var time) ||
            TimeOnly.TryParse(value, out time))
        {
            return time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        return string.Empty;
    }

    public sealed record TimeOption(string Label, int Seconds);

    public sealed record CommentTimerRow(
        CommentTimerProfile Timer,
        string ChannelName,
        string StartTime,
        string DurationText,
        string PollText,
        string EnabledText,
        string Status);
}
