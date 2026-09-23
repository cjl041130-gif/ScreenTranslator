using System.Windows;
using ScreenTranslator.Capture;
namespace ScreenTranslator.Vision;

public static class CoordinateMapper
{
    public static Rect ScreenToFrame(Rect screen, MonitorInfo monitor, int width, int height)
    {
        screen.Offset(-monitor.Left, -monitor.Top);
        screen.Intersect(new Rect(0, 0, width, height));
        return screen;
    }
    public static Rect WorkingToFrame(Rect working, Rect search, int workingWidth, int workingHeight) =>
        new(search.X + working.X * search.Width / workingWidth, search.Y + working.Y * search.Height / workingHeight,
            working.Width * search.Width / workingWidth, working.Height * search.Height / workingHeight);
    public static Rect PhysicalToDip(Rect rect, double dpiScale) => new(rect.X / dpiScale, rect.Y / dpiScale, rect.Width / dpiScale, rect.Height / dpiScale);
    public static double IoU(Rect a, Rect b)
    {
        if (a.IsEmpty || b.IsEmpty) return 0;
        var overlap = Rect.Intersect(a, b);
        var area = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
        return Math.Clamp(area / Math.Max(1, a.Width * a.Height + b.Width * b.Height - area), 0, 1);
    }
}
public static class PreviewCoordinateMapper
{
    public static Rect Map(Rect frameRect, double frameWidth, double frameHeight, double viewWidth, double viewHeight)
    {
        if (frameRect.IsEmpty || frameWidth <= 0 || frameHeight <= 0 || viewWidth <= 0 || viewHeight <= 0) return Rect.Empty;
        var scale = Math.Min(viewWidth / frameWidth, viewHeight / frameHeight);
        return new Rect((viewWidth - frameWidth * scale) / 2 + frameRect.X * scale,
            (viewHeight - frameHeight * scale) / 2 + frameRect.Y * scale, frameRect.Width * scale, frameRect.Height * scale);
    }
}

