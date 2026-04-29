using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using WpfPoint = System.Windows.Point;

namespace CodePulse.Wpf.Windows;

public partial class ScreenCaptureOverlayWindow : Window
{
    private readonly double _virtualLeft = SystemParameters.VirtualScreenLeft;
    private readonly double _virtualTop = SystemParameters.VirtualScreenTop;
    private bool _isSelecting;
    private WpfPoint _startPoint;
    private WpfPoint _currentPoint;

    public ScreenCaptureOverlayWindow(ImageSource? frozenScreen)
    {
        InitializeComponent();

        Left = _virtualLeft;
        Top = _virtualTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        FrozenScreenImage.Source = frozenScreen;
    }

    public DrawingRectangle? SelectedRegion { get; private set; }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Activate();
        Focus();
        Keyboard.Focus(RootCanvas);
    }

    public static ImageSource? CaptureFrozenScreen()
    {
        var screenWidth = (int)Math.Ceiling(SystemParameters.VirtualScreenWidth);
        var screenHeight = (int)Math.Ceiling(SystemParameters.VirtualScreenHeight);
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        if (screenWidth <= 0 || screenHeight <= 0)
        {
            return null;
        }

        using var bitmap = new DrawingBitmap(screenWidth, screenHeight, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                (int)Math.Round(virtualLeft),
                (int)Math.Round(virtualTop),
                0,
                0,
                bitmap.Size,
                CopyPixelOperation.SourceCopy);
        }

        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    private void Window_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isSelecting = true;
        _startPoint = e.GetPosition(this);
        _currentPoint = _startPoint;
        CaptureMouse();
        HintBadge.Visibility = Visibility.Collapsed;
        UpdateSelectionVisual();
    }

    private void Window_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSelecting)
        {
            return;
        }

        _currentPoint = e.GetPosition(this);
        UpdateSelectionVisual();
    }

    private void Window_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting)
        {
            return;
        }

        _isSelecting = false;
        ReleaseMouseCapture();
        _currentPoint = e.GetPosition(this);
        UpdateSelectionVisual();

        var region = GetSelectionRectangle();
        if (region.Width < 4 || region.Height < 4)
        {
            SelectedRegion = null;
            DialogResult = false;
            return;
        }

        SelectedRegion = region;
        DialogResult = true;
    }

    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        SelectedRegion = null;
        DialogResult = false;
    }

    private void Window_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isSelecting = false;
        ReleaseMouseCapture();
        SelectedRegion = null;
        DialogResult = false;
    }

    private void UpdateSelectionVisual()
    {
        var rect = GetLocalSelectionRectangle();
        var visibility = rect.Width > 0 && rect.Height > 0 ? Visibility.Visible : Visibility.Collapsed;

        SelectionFill.Visibility = visibility;
        SelectionBorder.Visibility = visibility;
        SelectionStartDot.Visibility = _isSelecting ? Visibility.Visible : Visibility.Collapsed;
        SelectionInfoBadge.Visibility = visibility;
        SelectionHandleTopLeft.Visibility = visibility;
        SelectionHandleTopRight.Visibility = visibility;
        SelectionHandleBottomLeft.Visibility = visibility;
        SelectionHandleBottomRight.Visibility = visibility;

        Canvas.SetLeft(SelectionFill, rect.X);
        Canvas.SetTop(SelectionFill, rect.Y);
        SelectionFill.Width = rect.Width;
        SelectionFill.Height = rect.Height;

        Canvas.SetLeft(SelectionBorder, rect.X);
        Canvas.SetTop(SelectionBorder, rect.Y);
        SelectionBorder.Width = rect.Width;
        SelectionBorder.Height = rect.Height;

        Canvas.SetLeft(SelectionStartDot, _startPoint.X - (SelectionStartDot.Width / 2));
        Canvas.SetTop(SelectionStartDot, _startPoint.Y - (SelectionStartDot.Height / 2));

        SelectionInfoText.Text = $"{Math.Max(0, (int)Math.Round(rect.Width))} x {Math.Max(0, (int)Math.Round(rect.Height))}";

        var badgeLeft = rect.X;
        var badgeTop = Math.Max(18, rect.Y - 38);
        Canvas.SetLeft(SelectionInfoBadge, badgeLeft);
        Canvas.SetTop(SelectionInfoBadge, badgeTop);

        PositionHandle(SelectionHandleTopLeft, rect.Left, rect.Top);
        PositionHandle(SelectionHandleTopRight, rect.Right, rect.Top);
        PositionHandle(SelectionHandleBottomLeft, rect.Left, rect.Bottom);
        PositionHandle(SelectionHandleBottomRight, rect.Right, rect.Bottom);
    }

    private Rect GetLocalSelectionRectangle()
    {
        var left = Math.Min(_startPoint.X, _currentPoint.X);
        var top = Math.Min(_startPoint.Y, _currentPoint.Y);
        var right = Math.Max(_startPoint.X, _currentPoint.X);
        var bottom = Math.Max(_startPoint.Y, _currentPoint.Y);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private DrawingRectangle GetSelectionRectangle()
    {
        var rect = GetLocalSelectionRectangle();
        var dpi = VisualTreeHelper.GetDpi(this);
        var left = (int)Math.Round((rect.Left + _virtualLeft) * dpi.DpiScaleX);
        var top = (int)Math.Round((rect.Top + _virtualTop) * dpi.DpiScaleY);
        var width = Math.Max(1, (int)Math.Round(rect.Width * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Round(rect.Height * dpi.DpiScaleY));
        return new DrawingRectangle(new DrawingPoint(left, top), new System.Drawing.Size(width, height));
    }

    private static void PositionHandle(FrameworkElement handle, double x, double y)
    {
        Canvas.SetLeft(handle, x - (handle.Width / 2));
        Canvas.SetTop(handle, y - (handle.Height / 2));
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
