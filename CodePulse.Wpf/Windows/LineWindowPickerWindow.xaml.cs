using System.Windows;
using System.Windows.Input;
using CodePulse.Dispatchers;

namespace CodePulse.Wpf.Windows;

public partial class LineWindowPickerWindow : Window
{
    private readonly Func<IReadOnlyList<WindowHandleInfo>> _loadWindows;
    private readonly List<LineWindowItem> _items = new();

    public LineWindowPickerWindow(Func<IReadOnlyList<WindowHandleInfo>> loadWindows)
    {
        InitializeComponent();
        _loadWindows = loadWindows;
        RefreshWindows();
    }

    public WindowHandleInfo? SelectedWindow { get; private set; }

    private void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void UseSelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        UseSelectedWindow();
    }

    private void WindowsListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        UseSelectedWindow();
    }

    private void RefreshWindows()
    {
        _items.Clear();
        _items.AddRange(_loadWindows().Select(static window => new LineWindowItem(window)));
        WindowsListBox.ItemsSource = null;
        WindowsListBox.ItemsSource = _items;
        StatusTextBlock.Text = _items.Count == 0
            ? "No LINE windows found. Open the target LINE chat, then refresh."
            : $"{_items.Count} LINE window(s) found.";
        if (_items.Count > 0)
        {
            WindowsListBox.SelectedIndex = 0;
        }
    }

    private void UseSelectedWindow()
    {
        if (WindowsListBox.SelectedItem is not LineWindowItem item)
        {
            StatusTextBlock.Text = "Select a LINE window first.";
            return;
        }

        SelectedWindow = item.Window;
        DialogResult = true;
    }

    private sealed class LineWindowItem
    {
        public LineWindowItem(WindowHandleInfo window)
        {
            Window = window;
        }

        public WindowHandleInfo Window { get; }

        public string Title => Window.Title;

        public string Detail => $"{Window.ProcessName} | HWND {Window.Handle}";
    }
}
