using OpenCvSharp;
using WRect = System.Windows.Rect;
namespace ScreenTranslator.Vision;

public sealed class PresentationCandidateGenerator(VisionSettings settings) : IPresentationCandidateGenerator
{
    public IReadOnlyList<PresentationCandidate> Generate(Mat image, bool fullscreen)
    {
        using var gray = new Mat(); using var blur = new Mat(); using var edges = new Mat(); using var linked = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGRA2GRAY);
        Cv2.GaussianBlur(gray, blur, new Size(3, 3), 0);
        Cv2.Canny(blur, edges, settings.CannyLow, settings.CannyHigh);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.MorphologyEx(edges, linked, MorphTypes.Close, kernel);
        // A pastel slide can have almost the same luminance as the editor canvas while still
        // having a strong colour boundary. Lab chroma edges recover that page without OCR or UI coordinates.
        using var bgr = new Mat(); using var lab = new Mat(); using var colorEdges = new Mat(image.Size(), MatType.CV_8UC1, Scalar.All(0));
        Cv2.CvtColor(image, bgr, ColorConversionCodes.BGRA2BGR); Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
        var channels = Cv2.Split(lab);
        try
        {
            foreach (var channel in channels.Skip(1))
            {
                using var channelBlur = new Mat(); using var channelEdge = new Mat();
                Cv2.GaussianBlur(channel, channelBlur, new Size(3, 3), 0);
                Cv2.Canny(channelBlur, channelEdge, Math.Max(4, settings.CannyLow * .25), Math.Max(12, settings.CannyHigh * .35));
                Cv2.BitwiseOr(colorEdges, channelEdge, colorEdges);
            }
        }
        finally { foreach (var channel in channels) channel.Dispose(); }
        using var colorLinked = new Mat(); Cv2.MorphologyEx(colorEdges, colorLinked, MorphTypes.Close, kernel);
        using var wideGray = new Mat(); using var wideColor = new Mat(); using var supportEdges = new Mat();
        Cv2.Dilate(edges, wideGray, kernel); Cv2.Dilate(colorEdges, wideColor, kernel); Cv2.BitwiseOr(wideGray, wideColor, supportEdges);
        var candidates = new List<PresentationCandidate>();
        if (fullscreen)
        {
            var full = new FullscreenPresentationDetector(settings).Detect(image);
            if (full.IsEmpty) return candidates;
            candidates.Add(new(full, "Fullscreen uniform-border scan", 1, 1, Interior(gray, full)));
            return candidates;
        }
        AddContours(linked, "Closed edge contour");
        AddContours(colorLinked, "Colour contrast contour");
        using var threshold = new Mat();
        Cv2.Threshold(blur, threshold, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        AddContours(threshold, "Luminance region");
        Cv2.BitwiseNot(threshold, threshold); AddContours(threshold, "Inverse luminance region");
        // Line intersections recover a page border fragmented by slide content or an editing handle.
        // Keep line intersection on luminance edges; chroma UI creates many decorative lines and is used only for closed rectangles.
        var lines = Cv2.HoughLinesP(edges, 1, Math.PI / 180, 70, Math.Min(image.Width, image.Height) * .18, 12);
        var horizontal = lines.Where(l => Math.Abs(l.P1.Y - l.P2.Y) <= 2).OrderByDescending(l => Math.Abs(l.P1.X - l.P2.X))
            .Select(l => (l.P1.Y + l.P2.Y) / 2).DistinctBy(v => v / 4).Take(12).Order().ToArray();
        var vertical = lines.Where(l => Math.Abs(l.P1.X - l.P2.X) <= 2).OrderByDescending(l => Math.Abs(l.P1.Y - l.P2.Y))
            .Select(l => (l.P1.X + l.P2.X) / 2).DistinctBy(v => v / 4).Take(12).Order().ToArray();
        for (var a = 0; a < horizontal.Length; a++) for (var b = a + 1; b < horizontal.Length; b++)
        for (var c = 0; c < vertical.Length; c++) for (var d = c + 1; d < vertical.Length; d++)
            Add(new Rect(vertical[c], horizontal[a], vertical[d] - vertical[c], horizontal[b] - horizontal[a]), "Line intersection", 1);
        return candidates.OrderByDescending(c => c.Edge * c.Completeness).Take(settings.MaximumCandidates).ToArray();

        void AddContours(Mat mask, string method)
        {
            Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
            foreach (var contour in contours)
            {
                var r = Cv2.BoundingRect(contour);
                var completeness = Math.Clamp(Math.Abs(Cv2.ContourArea(contour)) / Math.Max(1, r.Width * r.Height), 0, 1);
                if (completeness > .65) Add(r, method, completeness);
            }
        }
        void Add(Rect r, string method, double completeness)
        {
            var area = r.Width * (double)r.Height / (image.Width * image.Height);
            if (r.Width < 40 || r.Height < 40 || area < settings.MinimumCandidateArea || area > settings.MaximumEditCandidateArea) return;
            var bounds = new WRect(r.X, r.Y, r.Width, r.Height);
            if (candidates.Any(c => CoordinateMapper.IoU(c.Bounds, bounds) > .97)) return;
            var edge = EdgeSupport(supportEdges, r);
            if (edge < .48) return;
            candidates.Add(new(bounds, method, completeness, edge, Interior(gray, bounds)));
        }
    }
    private static double EdgeSupport(Mat edges, Rect r)
    {
        double Sample(bool horizontal, int fixedPos, int from, int to)
        {
            var hits = 0; var n = 0;
            for (var t = from + 2; t < to - 2; t += 2)
            {
                var found = false;
                for (var offset = -2; offset <= 2; offset++)
                {
                    var x = horizontal ? t : fixedPos + offset; var y = horizontal ? fixedPos + offset : t;
                    if (x >= 0 && y >= 0 && x < edges.Width && y < edges.Height && edges.At<byte>(y, x) != 0) { found = true; break; }
                }
                if (found) hits++; n++;
            }
            return hits / (double)Math.Max(1, n);
        }
        var sides = new[] { Sample(true, r.Top, r.Left, r.Right), Sample(true, r.Bottom - 1, r.Left, r.Right),
            Sample(false, r.Left, r.Top, r.Bottom), Sample(false, r.Right - 1, r.Top, r.Bottom) };
        return .55 * sides.Average() + .45 * sides.Min();
    }
    internal static double Interior(Mat gray, WRect r)
    {
        var roi = new Rect((int)r.X, (int)r.Y, Math.Max(1, (int)r.Width), Math.Max(1, (int)r.Height));
        using var crop = new Mat(gray, roi);
        Cv2.MeanStdDev(crop, out _, out Scalar deviation);
        // Blank slides remain candidates, but receive less interior evidence.
        return .25 + .75 * Math.Clamp(deviation.Val0 / 45, 0, 1);
    }
}
