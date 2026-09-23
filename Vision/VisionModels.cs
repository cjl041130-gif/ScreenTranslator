using System.Windows;
using ScreenTranslator.Capture;

namespace ScreenTranslator.Vision;

public enum ApplicationType { Unknown, PowerPoint, WpsPresentation, PdfViewer, Browser }
// All Rect values in the detection pipeline are physical pixels, never WPF DIP.
public sealed record ApplicationWindowInfo(nint Handle, int ProcessId, string ProcessName,
    string Title, string ClassName, Rect WindowRect, Rect ClientRect, Rect PhysicalPixelRect,
    double DpiScale, string MonitorId, bool IsForeground, bool IsLastExternalForeground,
    bool IsMinimized, ApplicationType ApplicationType, double Confidence, bool IsFullscreen)
{
    // Visible top-level surfaces above this window, in physical screen coordinates.
    public IReadOnlyList<Rect> Occlusions { get; init; } = [];
}
public sealed record CandidateScores(double AreaScore, double AspectRatioScore,
    double RectangleCompletenessScore, double EdgeScore, double CenterScore,
    double WindowContainmentScore, double InteriorScore, double TemporalStabilityScore, double FinalScore);
public sealed record PresentationCandidate(Rect Bounds, string Method, double Completeness,
    double Edge, double Interior, CandidateScores? Scores = null);
public sealed record PresentationRegion(double X, double Y, double Width, double Height,
    double Confidence, string DetectionMethod, nint TargetWindowHandle, string MonitorId, DateTimeOffset Timestamp)
{
    public Rect Bounds => new(X, Y, Width, Height);
    public double AspectRatio => Width / Math.Max(1, Height);
}
public sealed record DetectionSnapshot(ApplicationWindowInfo? Target, Rect SearchRegion,
    IReadOnlyList<PresentationCandidate> Candidates, PresentationRegion? Region, double LatencyMs,
    DateTimeOffset Timestamp, string? Error = null);
public interface ITargetWindowTracker : IDisposable
{
    nint LastExternalForegroundWindow { get; }
    void Start();
    void Stop();
    ApplicationWindowInfo? SelectTarget(MonitorInfo monitor);
}
public interface IPresentationCandidateGenerator
{
    IReadOnlyList<PresentationCandidate> Generate(OpenCvSharp.Mat workingImage, bool fullscreen);
}
public interface IPresentationDetector
{
    DetectionSnapshot Detect(CapturedFrame frame, MonitorInfo monitor, ApplicationWindowInfo? target);
    void Reset();
}
