using ScreenTranslator.Models;

namespace ScreenTranslator.OCR;

public enum OcrContentKind { Presentation, WebDocument }

public sealed record OcrInput(ReadOnlyMemory<byte> BgraPixels, int Width, int Height, int Stride,
    OcrContentKind ContentKind = OcrContentKind.Presentation);

public interface IOcrEngine : IAsyncDisposable
{
    string Name { get; }
    bool IsInitialized { get; }
    Task InitializeAsync(InferenceDevice preferredDevice, CancellationToken cancellationToken);
    Task<IReadOnlyList<OcrBlock>> RecognizeAsync(OcrInput input, CancellationToken cancellationToken);
}
