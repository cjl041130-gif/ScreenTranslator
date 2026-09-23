using ScreenTranslator.Models;

namespace ScreenTranslator.Recognition;

public sealed class RecognitionSettings
{
    public TranslationDisplayMode DisplayMode { get; set; } = TranslationDisplayMode.ChineseOverlay;
    public double TranslationBackgroundOpacity { get; set; } = .78;
    public double FontScale { get; set; } = 1;
    public bool OriginalTextVisibility { get; set; }
    public InferenceDevice InferenceDevice { get; set; } = InferenceDevice.Auto;
    public int SlideCacheCapacity { get; set; } = 64;
    public int StabilityMilliseconds { get; set; } = 350;
    public void Normalize()
    {
        TranslationBackgroundOpacity = Math.Clamp(TranslationBackgroundOpacity, .15, 1);
        FontScale = Math.Clamp(FontScale, .65, 1.8);
        SlideCacheCapacity = Math.Clamp(SlideCacheCapacity, 4, 256);
        StabilityMilliseconds = Math.Clamp(StabilityMilliseconds, 200, 1000);
        if (!Enum.IsDefined(DisplayMode)) DisplayMode = TranslationDisplayMode.ChineseOverlay;
        if (!Enum.IsDefined(InferenceDevice)) InferenceDevice = InferenceDevice.Auto;
    }
}
