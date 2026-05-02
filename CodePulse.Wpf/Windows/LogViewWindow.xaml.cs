using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using CodePulse.Wpf.ViewModels;

namespace CodePulse.Wpf.Windows;

public partial class LogViewWindow : Window
{
    private readonly MainShellViewModel _viewModel;

    public LogViewWindow(MainShellViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        ((INotifyCollectionChanged)_viewModel.LogEntries).CollectionChanged += LogEntries_OnCollectionChanged;
        Loaded += LogViewWindow_OnLoaded;
    }

    public void BringForward()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        ScrollLogToLatest();
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= LogViewWindow_OnLoaded;
        ((INotifyCollectionChanged)_viewModel.LogEntries).CollectionChanged -= LogEntries_OnCollectionChanged;
        base.OnClosed(e);
    }

    private void LogViewWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        ScrollLogToLatest();
    }

    private void LogEntries_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ScrollLogToLatest();
    }

    private void CopyLogButton_OnClick(object sender, RoutedEventArgs e)
    {
        var logText = _viewModel.BuildVisibleLogText();
        if (string.IsNullOrWhiteSpace(logText))
        {
            MessageBox.Show(
                this,
                "There is no log content to copy yet.",
                "Copy log",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Clipboard.SetText(logText);
    }

    private void ClearLogButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearLogs();
    }

    private void ScrollLogToLatest()
    {
        if (_viewModel.LogEntries.Count == 0)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(
            () => LogListBox.ScrollIntoView(_viewModel.LogEntries[^1]),
            DispatcherPriority.Background);
    }
}
