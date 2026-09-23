using System.Windows;

namespace ScreenTranslator.Models;

public enum OcrBlockType { Title, Subtitle, Body, Bullet, Table, ChartLabel, Caption, Footer, Code, Formula, Unknown }
public enum OcrConfidenceSource { Heuristic, Model }
public enum TranslationDisplayMode { ChineseOverlay, Bilingual, Sidebar }
public enum InferenceDevice { Auto, Cpu, Gpu }
public enum RecognitionRuntimeState { Idle, Initializing, Running, Processing, Paused, RegionLost, Faulted }

public sealed record OcrBlock(string Id, string OriginalText, double Confidence, Rect BoundingBox,
    int LineIndex, int ReadingOrder, OcrBlockType BlockType)
{
    public OcrConfidenceSource ConfidenceSource { get; init; } = OcrConfidenceSource.Heuristic;
    // Typography metadata is kept separate from BoundingBox because a merged paragraph's
    // bounds can span many visual lines. Overlay text must follow the original line size,
    // rather than treating the full paragraph height as a single large font.
    public double SourceLineHeight { get; init; }
    public int VisualLineCount { get; init; } = 1;
    public double EffectiveLineHeight => SourceLineHeight > 0
        ? SourceLineHeight
        : Height / Math.Max(1, VisualLineCount);
    public double X => BoundingBox.X;
    public double Y => BoundingBox.Y;
    public double Width => BoundingBox.Width;
    public double Height => BoundingBox.Height;
    public double CenterX => BoundingBox.X + BoundingBox.Width / 2;
    public double CenterY => BoundingBox.Y + BoundingBox.Height / 2;
}

public sealed record TranslatedBlock(OcrBlock Source, string TranslatedText, bool IsPartial, double TranslationMilliseconds);

public sealed record SlideRecognitionResult(string Fingerprint, PresentationRegionInfo Presentation,
    IReadOnlyList<TranslatedBlock> Blocks, bool FromCache, double OcrMilliseconds,
    double TranslationMilliseconds, double TotalMilliseconds, DateTimeOffset CompletedAt)
{
    public string SlideTitle => Blocks.FirstOrDefault(b => b.Source.BlockType == OcrBlockType.Title)?.Source.OriginalText ?? "";
}

public sealed record PresentationRegionInfo(double X, double Y, double Width, double Height,
    double DpiScale, string MonitorId)
{
    public Rect Bounds => new(X, Y, Width, Height);
}

public sealed record RecognitionStatistics(long OcrRuns, long CacheHits, double LastOcrMilliseconds,
    double LastTranslationMilliseconds, double LastTotalMilliseconds);
