namespace ScreenTranslator.Vision;

public sealed class VisionSettings
{
    public double DetectionFPS { get; set; } = 4;
    public double StableDetectionFPS { get; set; } = 2;
    public double MinimumConfidence { get; set; } = .72;
    public double AspectPriorStrength { get; set; } = .35;
    public double SwitchConfidenceMargin { get; set; } = .12;
    public double StableIoUThreshold { get; set; } = .85;
    public int StableFramesRequired { get; set; } = 3;
    public double ImmediateConfidence { get; set; } = .88;
    public int DetectionWorkingWidth { get; set; } = 1200;
    public double SmoothingAlpha { get; set; } = .65;
    public double LostRegionSeconds { get; set; } = 1;
    public double ExternalApplicationGraceSeconds { get; set; } = 1.5;
    public double MinimumCandidateArea { get; set; } = .045;
    public double MaximumEditCandidateArea { get; set; } = .88;
    public double CannyLow { get; set; } = 35;
    public double CannyHigh { get; set; } = 110;
    public double UniformBorderTolerance { get; set; } = 2;
    public int MaximumCandidates { get; set; } = 80;
    public CandidateWeights CandidateWeights { get; set; } = new();
    public DebugOverlaySettings DebugOverlay { get; set; } = new();
    public void Normalize()
    {
        DetectionFPS = Clamp(DetectionFPS, 1, 5, 4); StableDetectionFPS = Clamp(StableDetectionFPS, 1, DetectionFPS, 2);
        MinimumConfidence = Clamp(MinimumConfidence, .4, .99, .72); StableIoUThreshold = Clamp(StableIoUThreshold, .5, .99, .85);
        ImmediateConfidence = Clamp(ImmediateConfidence, MinimumConfidence, .99, .88);
        AspectPriorStrength = Clamp(AspectPriorStrength, 0, .6, .35);
        SwitchConfidenceMargin = Clamp(SwitchConfidenceMargin, 0, .5, .12); SmoothingAlpha = Clamp(SmoothingAlpha, .1, 1, .65);
        StableFramesRequired = Math.Clamp(StableFramesRequired, 2, 20); DetectionWorkingWidth = Math.Clamp(DetectionWorkingWidth, 800, 1400);
        LostRegionSeconds = Clamp(LostRegionSeconds, .25, 5, 1); ExternalApplicationGraceSeconds = Clamp(ExternalApplicationGraceSeconds, .1, 5, 1.5);
        MinimumCandidateArea = Clamp(MinimumCandidateArea, .01, .2, .045); MaximumEditCandidateArea = Clamp(MaximumEditCandidateArea, .5, .98, .88);
        CannyLow = Clamp(CannyLow, 1, 200, 35); CannyHigh = Clamp(CannyHigh, CannyLow, 255, 110);
        UniformBorderTolerance = Clamp(UniformBorderTolerance, 1, 40, 12); MaximumCandidates = Math.Clamp(MaximumCandidates, 10, 150);
        CandidateWeights ??= new(); CandidateWeights.Normalize(); DebugOverlay ??= new();
    }
    internal static double Clamp(double x, double lo, double hi, double fallback) => double.IsFinite(x) ? Math.Clamp(x, lo, hi) : fallback;
}
public sealed class CandidateWeights
{
    public double Area { get; set; } = .16;
    public double AspectRatio { get; set; } = .10;
    public double RectangleCompleteness { get; set; } = .18;
    public double Edge { get; set; } = .22;
    public double Center { get; set; } = .07;
    public double WindowContainment { get; set; } = .08;
    public double Interior { get; set; } = .09;
    public double TemporalStability { get; set; } = .10;
    internal double[] Values => [Area, AspectRatio, RectangleCompleteness, Edge, Center, WindowContainment, Interior, TemporalStability];
    internal void Normalize()
    {
        Area = VisionSettings.Clamp(Area, 0, 1, .16); AspectRatio = VisionSettings.Clamp(AspectRatio, 0, 1, .10);
        RectangleCompleteness = VisionSettings.Clamp(RectangleCompleteness, 0, 1, .18); Edge = VisionSettings.Clamp(Edge, 0, 1, .22);
        Center = VisionSettings.Clamp(Center, 0, 1, .07); WindowContainment = VisionSettings.Clamp(WindowContainment, 0, 1, .08);
        Interior = VisionSettings.Clamp(Interior, 0, 1, .09); TemporalStability = VisionSettings.Clamp(TemporalStability, 0, 1, .10);
        if (Values.Sum() < .001) Edge = 1;
    }
}
public sealed class DebugOverlaySettings
{
    public bool ShowSearchRegion { get; set; }
    public bool ShowCandidates { get; set; }
    public bool ShowCandidateScore { get; set; }
    public bool ShowStableRegion { get; set; } = true;
    public bool ShowWindowInformation { get; set; }
}
