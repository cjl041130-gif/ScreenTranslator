using OpenCvSharp;
using CvRect = OpenCvSharp.Rect;
using WRect = System.Windows.Rect;

namespace ScreenTranslator.Vision;

/// <summary>
/// Finds the visible paper/page surface inside a browser or PDF viewport.  It has
/// no presentation aspect-ratio prior, so portrait documents and partially visible
/// pages are valid.  When no convincing page is present the caller keeps the whole
/// browser content region, which is required for ordinary web pages.
/// </summary>
public static class DocumentPageDetector
{
    public static WRect Detect(Mat bgra, WRect contentBounds, out double confidence)
    {
        confidence = 0;
        var source = Clamp(contentBounds, bgra.Width, bgra.Height);
        if (source.Width < 320 || source.Height < 240) return WRect.Empty;

        var scale = Math.Min(1d, 1200d / source.Width);
        using var roi = new Mat(bgra, new CvRect((int)source.X, (int)source.Y, (int)source.Width, (int)source.Height));
        using var working = new Mat();
        Cv2.Resize(roi, working, new Size(Math.Max(1, (int)Math.Round(roi.Width * scale)),
            Math.Max(1, (int)Math.Round(roi.Height * scale))), 0, 0, InterpolationFlags.Area);
        using var bgr = new Mat();
        Cv2.CvtColor(working, bgr, ColorConversionCodes.BGRA2BGR);

        PageCandidate? best = null;
        // Office Web, PDF viewers and Feishu use slightly different canvas greys.
        // Trying three conservative white levels is more reliable than one global
        // threshold and remains cheap at the bounded working resolution.
        foreach (var level in new[] { 252, 249, 246 })
        {
            using var mask = new Mat();
            Cv2.InRange(bgr, new Scalar(level, level, level), new Scalar(255, 255, 255), mask);
            var candidate = FindCentralPaper(mask);
            if (candidate is not null && (best is null || candidate.Score > best.Score)) best = candidate;
        }
        if (best is null || best.Score < .66) return WRect.Empty;

        var local = best.Bounds;
        var result = new WRect(source.X + local.X / scale, source.Y + local.Y / scale,
            local.Width / scale, local.Height / scale);
        result = Clamp(result, bgra.Width, bgra.Height);
        confidence = Math.Clamp(best.Score, .72, .97);
        return result;
    }

    private static PageCandidate? FindCentralPaper(Mat mask)
    {
        var width = mask.Width;
        var height = mask.Height;
        var sampleTop = Math.Clamp((int)Math.Round(height * .14), 0, height - 1);
        var sampleBottom = Math.Clamp((int)Math.Round(height * .96), sampleTop + 1, height);
        var sampleHeight = sampleBottom - sampleTop;
        var whiteFractions = new double[width];
        unsafe
        {
            for (var x = 0; x < width; x++)
            {
                var white = 0;
                for (var y = sampleTop; y < sampleBottom; y++)
                    if (mask.At<byte>(y, x) != 0) white++;
                whiteFractions[x] = white / (double)sampleHeight;
            }
        }

        PageCandidate? best = null;
        foreach (var fraction in new[] { .72, .62, .52 })
        {
            var runs = Runs(whiteFractions, fraction, Math.Max(6, width / 250));
            foreach (var (left, right) in runs)
            {
                var pageWidth = right - left;
                var widthRatio = pageWidth / (double)width;
                if (widthRatio < .28 || widthRatio > .86) continue;
                var centerError = Math.Abs((left + right) / 2d - width / 2d) / (width / 2d);
                if (centerError > .28) continue;

                var top = FindPageTop(mask, left, right);
                var bottom = FindPageBottom(mask, left, right, top);
                var pageHeight = bottom - top;
                if (pageHeight < height * .42) continue;
                var aspect = pageWidth / (double)pageHeight;
                if (aspect < .30 || aspect > 1.75) continue;

                var interior = Mean(whiteFractions, left, right);
                var outside = (Mean(whiteFractions, 0, Math.Max(1, left - 3)) +
                    Mean(whiteFractions, Math.Min(width, right + 3), width)) / 2;
                var contrast = Math.Clamp((interior - outside) / .55, 0, 1);
                var centerScore = 1 - Math.Clamp(centerError / .28, 0, 1);
                var sizeScore = Math.Clamp((pageHeight / (double)height - .42) / .45, 0, 1);
                var score = .34 * contrast + .25 * centerScore + .21 * Math.Clamp(interior, 0, 1) + .20 * sizeScore;
                var candidate = new PageCandidate(new CvRect(left, top, pageWidth, pageHeight), score);
                if (best is null || candidate.Score > best.Score) best = candidate;
            }
        }
        return best;
    }

    private static int FindPageTop(Mat mask, int left, int right)
    {
        var width = right - left;
        var limit = Math.Max(1, (int)(mask.Height * .32));
        var fractions = new double[limit];
        for (var y = 0; y < limit; y++)
        {
            var white = 0;
            for (var x = left; x < right; x += 2) if (mask.At<byte>(y, x) != 0) white++;
            fractions[y] = white / (double)Math.Max(1, (width + 1) / 2);
        }
        // The browser-specific content inset may already start inside a clipped or
        // scrolled page. In that case keep the first visible text row instead of
        // manufacturing another top margin.
        if (fractions.Take(Math.Min(8, fractions.Length)).Average() >= .68) return 0;
        // The strongest sustained transition into the paper surface normally lies
        // below the browser/editor ribbon.  Requiring several following rows rejects
        // text baselines and thin toolbar separators.
        var minimum = Math.Max(6, (int)(mask.Height * .025));
        for (var y = minimum; y < limit - 5; y++)
        {
            var after = fractions.Skip(y).Take(5).Average();
            var before = fractions.Skip(Math.Max(0, y - 5)).Take(5).Average();
            if (after >= .68 && (before < .48 || after - before > .22)) return y;
        }
        return minimum;
    }

    private static int FindPageBottom(Mat mask, int left, int right, int top)
    {
        var width = right - left;
        var height = mask.Height;
        var lastGood = height;
        var bad = 0;
        for (var y = Math.Max(top + 20, (int)(height * .45)); y < height; y++)
        {
            var white = 0;
            for (var x = left; x < right; x += 3) if (mask.At<byte>(y, x) != 0) white++;
            var fraction = white / (double)Math.Max(1, (width + 2) / 3);
            if (fraction < .38) bad++; else { bad = 0; lastGood = y + 1; }
            if (bad >= 8) return Math.Max(top + 1, lastGood);
        }
        return height;
    }

    private static IEnumerable<(int Left, int Right)> Runs(double[] values, double threshold, int bridge)
    {
        var start = -1;
        var gap = 0;
        for (var i = 0; i <= values.Length; i++)
        {
            var on = i < values.Length && values[i] >= threshold;
            if (on)
            {
                if (start < 0) start = i;
                gap = 0;
            }
            else if (start >= 0 && ++gap > bridge)
            {
                yield return (start, i - gap + 1);
                start = -1;
                gap = 0;
            }
        }
        if (start >= 0) yield return (start, values.Length);
    }

    private static double Mean(double[] values, int start, int end)
    {
        start = Math.Clamp(start, 0, values.Length);
        end = Math.Clamp(end, start, values.Length);
        if (end <= start) return 0;
        double sum = 0;
        for (var i = start; i < end; i++) sum += values[i];
        return sum / (end - start);
    }

    private static WRect Clamp(WRect value, int width, int height)
    {
        var x = Math.Clamp(value.X, 0, width);
        var y = Math.Clamp(value.Y, 0, height);
        var right = Math.Clamp(value.Right, x, width);
        var bottom = Math.Clamp(value.Bottom, y, height);
        return new WRect(x, y, right - x, bottom - y);
    }

    private sealed record PageCandidate(CvRect Bounds, double Score);
}
