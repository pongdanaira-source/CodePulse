using System.Drawing;

namespace CodePulse.Services;

public sealed class ScreenCaptureService
{
    private const string InvalidRegionMessage = "\u0e1e\u0e37\u0e49\u0e19\u0e17\u0e35\u0e48\u0e08\u0e31\u0e1a\u0e20\u0e32\u0e1e\u0e44\u0e21\u0e48\u0e16\u0e39\u0e01\u0e15\u0e49\u0e2d\u0e07";
    private const string CaptureUnavailableMessage = "\u0e44\u0e21\u0e48\u0e2a\u0e32\u0e21\u0e32\u0e23\u0e16\u0e08\u0e31\u0e1a\u0e20\u0e32\u0e1e\u0e2b\u0e19\u0e49\u0e32\u0e08\u0e2d\u0e44\u0e14\u0e49 \u0e2d\u0e32\u0e08\u0e21\u0e35\u0e01\u0e32\u0e23\u0e40\u0e1b\u0e25\u0e35\u0e48\u0e22\u0e19\u0e2b\u0e19\u0e49\u0e32\u0e08\u0e2d\u0e2b\u0e23\u0e37\u0e2d\u0e1e\u0e37\u0e49\u0e19\u0e17\u0e35\u0e48\u0e08\u0e31\u0e1a\u0e44\u0e21\u0e48\u0e1e\u0e23\u0e49\u0e2d\u0e21\u0e43\u0e0a\u0e49\u0e07\u0e32\u0e19";

    public Task<Bitmap> CaptureAsync(Rectangle region, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (region.Width <= 0 || region.Height <= 0)
            {
                throw new ArgumentException(InvalidRegionMessage, nameof(region));
            }

            try
            {
                var bitmap = new Bitmap(region.Width, region.Height);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.CopyFromScreen(region.Location, Point.Empty, region.Size);
                return bitmap;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            {
                throw new InvalidOperationException(CaptureUnavailableMessage, ex);
            }
        }, cancellationToken);
    }
}
