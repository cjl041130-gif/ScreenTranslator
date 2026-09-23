using OpenCvSharp;
using WRect = System.Windows.Rect;
namespace ScreenTranslator.Vision;

public sealed class FullscreenPresentationDetector(VisionSettings settings)
{
    public WRect Detect(Mat image)
    {
        var w = image.Width; var h = image.Height;
        var corner = image.At<Vec4b>(0, 0);
        bool Uniform(bool column, int position)
        {
            var length = column ? h : w; var matches = 0; var count = 0;
            for (var t = 0; t < length; t += 3)
            {
                var p = image.At<Vec4b>(column ? t : position, column ? position : t);
                if (Math.Max(Math.Abs(p.Item0 - corner.Item0), Math.Max(Math.Abs(p.Item1 - corner.Item1), Math.Abs(p.Item2 - corner.Item2))) <= settings.UniformBorderTolerance) matches++;
                count++;
            }
            return matches / (double)count > .995;
        }
        int left = 0, right = 0, top = 0, bottom = 0;
        while (left < w / 3 && Uniform(true, left)) left++;
        while (right < w / 3 && Uniform(true, w - right - 1)) right++;
        while (top < h / 3 && Uniform(false, top)) top++;
        while (bottom < h / 3 && Uniform(false, h - bottom - 1)) bottom++;
        var ambiguousDark = Math.Max(corner.Item0, Math.Max(corner.Item1, corner.Item2)) <= settings.UniformBorderTolerance &&
            (left >= w / 3 || right >= w / 3) && (top >= h / 3 || bottom >= h / 3);
        if (ambiguousDark) return WRect.Empty;
        // Only symmetric, bounded opposite margins count as letter/pillarboxing.
        // All-uniform / same-colour slide edges are inherently ambiguous without document metadata.
        if (left < 2 || right < 2 || left >= w / 3 || right >= w / 3 || Math.Abs(left - right) > Math.Max(4, w * .015)) left = right = 0;
        if (top < 2 || bottom < 2 || top >= h / 3 || bottom >= h / 3 || Math.Abs(top - bottom) > Math.Max(4, h * .015)) top = bottom = 0;
        return new(left, top, w - left - right, h - top - bottom);
    }
}
