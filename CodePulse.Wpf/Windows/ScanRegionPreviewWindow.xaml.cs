using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CodePulse.Models;

namespace CodePulse.Wpf.Windows;

public partial class ScanRegionPreviewWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolwindow = 0x80;
    private const int WsExNoactivate = 0x08000000;

    private readonly CaptureRegion _region;

    public ScanRegionPreviewWindow(string channelName, CaptureRegion region)
    {
        InitializeComponent();
        _region = region;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, extendedStyle | WsExTransparent | WsExToolwindow | WsExNoactivate);
        PositionFromCaptureRegion(handle);
    }

    private void PositionFromCaptureRegion(nint handle)
    {
        var dpiScale = GetDpiScale(handle);
        Left = _region.X / dpiScale;
        Top = _region.Y / dpiScale;
        Width = Math.Max(1, _region.Width / dpiScale);
        Height = Math.Max(1, _region.Height / dpiScale);
    }

    private static double GetDpiScale(nint handle)
    {
        try
        {
            var dpi = GetDpiForWindow(handle);
            if (dpi > 0)
            {
                return dpi / 96d;
            }
        }
        catch
        {
            // Fall through to default scaling.
        }

        return 1d;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(nint hwnd);
}
