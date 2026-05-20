using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private bool _isUpdatingTimePicker;

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
        HourComboBox.ItemsSource = Enumerable.Range(0, 24)
            .Select(static hour => hour.ToString("00", System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        MinuteComboBox.ItemsSource = Enumerable.Range(0, 60)
            .Select(static minute => minute.ToString("00", System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

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
        SyncTimePickerFromText();

        _refreshTimer.Interval = TimeSpan.FromSeconds(2);
        _refreshTimer.Tick += (_, _) => ReloadTimers(keepSelection: true);
        Loaded += (_, _) =>
        {
            _refreshTimer.Start();
            VideoUrlTextBox.Focus();
        };
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

        EmptyTimerListTextBlock.Visibility = _rows.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

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
        SyncTimePickerFromText();
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

    private void ClearEditor(bool preserveInput = false)
    {
        var preservedChannel = SelectedChannel;
        var preservedVideoUrl = VideoUrlTextBox.Text;
        var preservedDuration = DurationComboBox.SelectedValue;
        var preservedPollInterval = PollIntervalComboBox.SelectedValue;

        _isLoadingEditor = true;
        _editingTimerId = null;
        TimerListBox.SelectedItem = null;
        if (preserveInput && preservedChannel is not null)
        {
            ChannelComboBox.SelectedItem = preservedChannel;
        }

        StartTimeTextBox.Text = DateTime.Now.AddMinutes(1).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        SyncTimePickerFromText();
        DurationComboBox.SelectedValue = preserveInput && preservedDuration is int duration ? duration : 300;
        PollIntervalComboBox.SelectedValue = preserveInput && preservedPollInterval is int pollInterval ? pollInterval : 1;
        EnabledCheckBox.IsChecked = true;
        VideoUrlTextBox.Text = preserveInput ? preservedVideoUrl : string.Empty;
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

        var isAdding = _editingTimerId is null;
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
        if (isAdding)
        {
            _editingTimerId = null;
            ReloadTimers(keepSelection: false);
            StartTimeTextBox.Text = startTime;
            SyncTimePickerFromText();
            StatusTextBlock.Text = "Added";
            StatusHintTextBlock.Text = "Change time and press Enter to add another.";
            StartTimeTextBox.Focus();
            StartTimeTextBox.SelectAll();
            return;
        }

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

    private void DeleteAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        var timerIds = _getTimers()
            .Select(static timer => timer.Id)
            .ToList();
        if (timerIds.Count == 0)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            $"Delete all {timerIds.Count} comment timers?",
            "Delete all timers",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var timerId in timerIds)
        {
            _deleteTimer(timerId);
        }

        ClearEditor(preserveInput: true);
        ReloadTimers(keepSelection: false);
        StatusTextBlock.Text = "Deleted";
        StatusHintTextBlock.Text = $"Deleted {timerIds.Count} timer(s).";
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
        ClearEditor(preserveInput: true);
        StatusTextBlock.Text = "New";
        StatusHintTextBlock.Text = "Create another scheduled scan for this link.";
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (TimePickerPopup.IsOpen)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        if (e.Key == Key.S)
        {
            TrySaveFromKeyboard();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.N)
        {
            ClearEditor(preserveInput: true);
            StatusTextBlock.Text = "New";
            StatusHintTextBlock.Text = "Create another scheduled scan for this link.";
            StartTimeTextBox.Focus();
            StartTimeTextBox.SelectAll();
            e.Handled = true;
        }
    }

    private void EditorTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        TrySaveFromKeyboard();
        e.Handled = true;
    }

    private void TrySaveFromKeyboard()
    {
        if (!SaveButton.IsEnabled)
        {
            UpdateEditorState();
            return;
        }

        SaveButton_OnClick(SaveButton, new RoutedEventArgs());
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
        SyncTimePickerFromText();
        UpdateEditorState();
    }

    private void TimePickerButton_OnClick(object sender, RoutedEventArgs e)
    {
        SyncTimePickerFromText();
        TimePickerPopup.IsOpen = true;
    }

    private void TimePickerComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingTimePicker ||
            HourComboBox.SelectedItem is not string hour ||
            MinuteComboBox.SelectedItem is not string minute)
        {
            return;
        }

        StartTimeTextBox.Text = $"{hour}:{minute}";
        StartTimeTextBox.CaretIndex = StartTimeTextBox.Text.Length;
        UpdateEditorState();
    }

    private void QuickTimeButton_OnClick(object sender, RoutedEventArgs e)
    {
        var minutes = sender is Button { Tag: string tag } &&
                      int.TryParse(tag, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
        SetStartTime(minutes == 0
            ? DateTime.Now
            : GetStartTimeBase().AddMinutes(minutes));
        TimePickerPopup.IsOpen = false;
    }

    private void UpdateEditorState()
    {
        if (_isLoadingEditor ||
            VideoUrlTextBox is null ||
            StartTimeTextBox is null ||
            SaveButton is null ||
            DeleteButton is null ||
            DeleteAllButton is null ||
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
        SaveButton.Content = _editingTimerId is null ? "Add" : "Save";
        DeleteButton.IsEnabled = SelectedRow is not null;
        DeleteAllButton.IsEnabled = _rows.Count > 0;
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
        var trimmed = value.Trim();
        if (trimmed.Equals("now", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("n", StringComparison.OrdinalIgnoreCase))
        {
            return DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (TryParseRelativeMinutes(trimmed, out var relativeMinutes))
        {
            return DateTime.Now.AddMinutes(relativeMinutes).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        var digits = new string(trimmed.Where(char.IsAsciiDigit).ToArray());
        if (!trimmed.Contains(':', StringComparison.Ordinal) && digits.Length is >= 1 and <= 4)
        {
            var (hourText, minuteText) = digits.Length switch
            {
                <= 2 => (digits, "00"),
                3 => (digits[..1], digits[1..]),
                _ => (digits[..2], digits[2..])
            };

            if (int.TryParse(hourText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var hour) &&
                int.TryParse(minuteText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var minute) &&
                hour is >= 0 and <= 23 &&
                minute is >= 0 and <= 59)
            {
                return $"{hour:00}:{minute:00}";
            }
        }

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

    private static bool TryParseRelativeMinutes(string value, out int minutes)
    {
        minutes = 0;
        var trimmed = value.Trim();
        if (trimmed.StartsWith("+", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..].Trim();
        }
        else if (trimmed.StartsWith("now+", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[4..].Trim();
        }
        else
        {
            return false;
        }

        trimmed = trimmed.TrimEnd('m', 'M');
        return int.TryParse(
                   trimmed,
                   System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out minutes) &&
               minutes is >= 0 and <= 240;
    }

    private void SetStartTime(DateTime value)
    {
        StartTimeTextBox.Text = value.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        StartTimeTextBox.CaretIndex = StartTimeTextBox.Text.Length;
        SyncTimePickerFromText();
        UpdateEditorState();
    }

    private DateTime GetStartTimeBase()
    {
        var normalized = NormalizeStartTime(StartTimeTextBox.Text);
        if (string.IsNullOrWhiteSpace(normalized) ||
            !TimeOnly.TryParseExact(
                normalized,
                "HH:mm",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var time))
        {
            return DateTime.Now;
        }

        return DateTime.Today.Add(time.ToTimeSpan());
    }

    private void SyncTimePickerFromText()
    {
        if (HourComboBox is null || MinuteComboBox is null)
        {
            return;
        }

        var normalized = NormalizeStartTime(StartTimeTextBox.Text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        _isUpdatingTimePicker = true;
        HourComboBox.SelectedItem = normalized[..2];
        MinuteComboBox.SelectedItem = normalized[3..5];
        _isUpdatingTimePicker = false;
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
