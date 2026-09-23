using System.Diagnostics;
using OpenCvSharp;
using ScreenTranslator.Capture;
using WRect = System.Windows.Rect;
namespace ScreenTranslator.Vision;

public sealed class PresentationDetector : IPresentationDetector
{
    private readonly VisionSettings _settings;
    private readonly IPresentationCandidateGenerator _generator;
    private readonly PresentationRegionScorer _scorer;
    private readonly PresentationRegionTracker _tracker;
    private nint _target;
    private WRect _window = WRect.Empty;
    private string? _monitor;
    public PresentationDetector(VisionSettings settings)
    { _settings = settings; _generator = new PresentationCandidateGenerator(settings); _scorer = new(settings); _tracker = new(settings); }
    public void Reset() { _tracker.Reset(); _target = 0; _window = WRect.Empty; _monitor = null; }
    public unsafe DetectionSnapshot Detect(CapturedFrame frame, MonitorInfo monitor, ApplicationWindowInfo? target)
    {
        var watch = Stopwatch.StartNew();
        if (target is null) { Reset(); return new(null, WRect.Empty, [], null, watch.Elapsed.TotalMilliseconds, frame.Timestamp); }
        if (_target != target.Handle || _window != target.ClientRect || _monitor != monitor.Id)
        { _tracker.Reset(); _target = target.Handle; _window = target.ClientRect; _monitor = monitor.Id; }
        var search = CoordinateMapper.ScreenToFrame(target.ClientRect, monitor, frame.Width, frame.Height);
        if (search.IsEmpty || search.Width < 40 || search.Height < 40) { Reset(); return new(target, search, [], null, watch.Elapsed.TotalMilliseconds, frame.Timestamp); }

        // Web pages and PDF viewers do not necessarily contain a slide-shaped rectangle.
        // Recognize their visible document surface directly instead of forcing the PPT-only
        // candidate detector to invent a page boundary. This is also much faster than running
        // OpenCV contours on every browser frame.
        if (target.ApplicationType is ApplicationType.Browser or ApplicationType.PdfViewer)
        {
            _tracker.Reset();
            var content = GeneralDocumentRegion.GetContentBounds(target, search);
            if (content.IsEmpty || OccludedRatio(content, monitor, target.Occlusions) >= .35)
                return new(target, search, [], null, watch.Elapsed.TotalMilliseconds, frame.Timestamp);
            var page = WRect.Empty;
            var pageConfidence = 0d;
            fixed (byte* data = frame.Buffer)
            {
                using var full = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, (nint)data, frame.Stride);
                page = DocumentPageDetector.Detect(full, content, out pageConfidence);
            }
            var bounds = page.IsEmpty ? content : page;
            var method = page.IsEmpty
                ? target.ApplicationType == ApplicationType.PdfViewer ? "PDF visible content" : "Browser visible content"
                : target.ApplicationType == ApplicationType.PdfViewer ? "PDF document page" : "Browser document page";
            var confidence = page.IsEmpty ? Math.Clamp(Math.Max(.82, target.Confidence), 0, .96) : pageConfidence;
            var region = new PresentationRegion(bounds.X, bounds.Y, bounds.Width, bounds.Height,
                confidence, method, target.Handle, target.MonitorId, frame.Timestamp);
            return new(target, search, [], region, watch.Elapsed.TotalMilliseconds, frame.Timestamp);
        }
        var roi = new Rect((int)search.X, (int)search.Y, (int)search.Width, (int)search.Height);
        var scale = Math.Min(1, _settings.DetectionWorkingWidth / Math.Max(search.Width, search.Height));
        using var working = new Mat();
        fixed (byte* data = frame.Buffer)
        {
            using var full = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, (nint)data, frame.Stride);
            using var cropped = new Mat(full, roi);
            Cv2.Resize(cropped, working, new Size(Math.Max(1, (int)(roi.Width * scale)), Math.Max(1, (int)(roi.Height * scale))), 0, 0, InterpolationFlags.Area);
        }
        var candidates = _generator.Generate(working, target.IsFullscreen)
            .Select(c => c with { Bounds = CoordinateMapper.WorkingToFrame(c.Bounds, search, working.Width, working.Height) })
            .Where(c => !IsOccluded(c.Bounds, monitor, target.Occlusions))
            .Select(c => _scorer.Score(c, search, _tracker.Bounds, target.IsFullscreen)).OrderByDescending(c => c.Scores!.FinalScore).ToArray();
        // Do not retain a previously stable box over another application's pixels, even during lost grace.
        if (IsOccluded(_tracker.Bounds, monitor, target.Occlusions)) _tracker.Reset();
        var stable = _tracker.Update(candidates.FirstOrDefault(), target, frame.Timestamp);
        return new(target, search, candidates, stable, watch.Elapsed.TotalMilliseconds, frame.Timestamp);
    }
    internal static bool IsOccluded(WRect frameBounds, MonitorInfo monitor, IReadOnlyList<WRect> surfaces)
    {
        if (frameBounds.IsEmpty) return false;
        var screen = new WRect(frameBounds.X + monitor.Left, frameBounds.Y + monitor.Top, frameBounds.Width, frameBounds.Height);
        return surfaces.Any(surface => { var overlap = WRect.Intersect(screen, surface); return !overlap.IsEmpty && overlap.Width > 1 && overlap.Height > 1; });
    }
    internal static double OccludedRatio(WRect frameBounds, MonitorInfo monitor, IReadOnlyList<WRect> surfaces)
    {
        if (frameBounds.IsEmpty) return 0;
        var screen = new WRect(frameBounds.X + monitor.Left, frameBounds.Y + monitor.Top, frameBounds.Width, frameBounds.Height);
        var covered = surfaces.Sum(surface =>
        {
            var overlap = WRect.Intersect(screen, surface);
            return overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
        });
        return Math.Clamp(covered / Math.Max(1, screen.Width * screen.Height), 0, 1);
    }
}
