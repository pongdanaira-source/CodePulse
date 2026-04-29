using System.Drawing;

namespace CodePulse.Models;

public sealed class CaptureRegion
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool IsValid => Width > 0 && Height > 0;

    public Rectangle ToRectangle()
    {
        return new Rectangle(X, Y, Width, Height);
    }

    public static CaptureRegion FromRectangle(Rectangle rectangle)
    {
        return new CaptureRegion
        {
            X = rectangle.X,
            Y = rectangle.Y,
            Width = rectangle.Width,
            Height = rectangle.Height
        };
    }
}
