using ScreenTranslator.Capture;
using ScreenTranslator.Models;

namespace ScreenTranslator.Overlay;

public interface ITranslationOverlay : IAsyncDisposable
{
    bool IsVisible { get; }
    Task ShowAsync(SlideRecognitionResult result, MonitorInfo monitor, TranslationDisplayMode mode,
        double backgroundOpacity, double fontScale, bool showOriginalText, CancellationToken cancellationToken);
    Task UpdateRegionAsync(PresentationRegionInfo region, MonitorInfo monitor, CancellationToken cancellationToken);
    Task HideAsync(CancellationToken cancellationToken = default);
}

public sealed class NullTranslationOverlay : ITranslationOverlay
{
    public bool IsVisible { get; private set; }
    public Task ShowAsync(SlideRecognitionResult result, MonitorInfo monitor, TranslationDisplayMode mode, double backgroundOpacity, double fontScale, bool showOriginalText, CancellationToken cancellationToken) { IsVisible = true; return Task.CompletedTask; }
    public Task UpdateRegionAsync(PresentationRegionInfo region, MonitorInfo monitor, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task HideAsync(CancellationToken cancellationToken = default) { IsVisible = false; return Task.CompletedTask; }
    public ValueTask DisposeAsync() { IsVisible = false; return ValueTask.CompletedTask; }
}
