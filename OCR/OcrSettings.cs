namespace ScreenTranslator.OCR;

public sealed class OcrSettings
{
    public string Engine { get; set; } = "PaddleOCR";
    public string AccuracyMode { get; set; } = "High";
    public double LowConfidenceThreshold { get; set; } = .78;
    public bool EnableWindowsFallback { get; set; } = true;
    public bool EnableAdaptivePreprocessing { get; set; } = true;
    public bool EnableTileRecognition { get; set; } = true;
    public int TileSize { get; set; } = 1408;
    public int TileOverlap { get; set; } = 96;
    public int TileActivationLongSide { get; set; } = 3000;
    public void Normalize()
    {
        Engine = "PaddleOCR";
        AccuracyMode = "High";
        LowConfidenceThreshold = Math.Clamp(LowConfidenceThreshold, .4, .98);
        TileSize = Math.Clamp(TileSize, 768, 2048);
        TileOverlap = Math.Clamp(TileOverlap, 32, Math.Min(256, TileSize / 3));
        TileActivationLongSide = Math.Clamp(TileActivationLongSide, 2048, 4096);
    }
}
