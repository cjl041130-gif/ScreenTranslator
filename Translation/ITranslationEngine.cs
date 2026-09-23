using ScreenTranslator.Models;

namespace ScreenTranslator.Translation;

public sealed record TranslationContext(string SlideTitle, IReadOnlyList<string> PreviousBlocks,
    IReadOnlyDictionary<string, string> KnownTerms, string CurrentSlideTopic);
public sealed record TranslationRequest(string SourceLanguage, string TargetLanguage, string Text,
    TranslationContext Context, OcrBlockType BlockType);
public sealed record TranslationResponse(string Text, bool IsPartial, double ProcessingMilliseconds);

public interface ITranslationEngine : IAsyncDisposable
{
    string Name { get; }
    bool IsInitialized { get; }
    LanguagePackMetadata? LanguagePack { get; }
    Task InitializeAsync(InferenceDevice preferredDevice, CancellationToken cancellationToken);
    Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken);
}

public interface IPageTranslationEngine
{
    Task<IReadOnlyList<TranslationResponse>> TranslatePageAsync(
        IReadOnlyList<OcrBlock> blocks, string sourceLanguage, string targetLanguage,
        CancellationToken cancellationToken);
}

public interface IDocumentPageTranslationEngine : IPageTranslationEngine
{
    Task<IReadOnlyList<TranslationResponse>> TranslateDocumentBatchAsync(
        IReadOnlyList<OcrBlock> blocks, string sourceLanguage, string targetLanguage,
        string documentType, CancellationToken cancellationToken);
}

public sealed record LanguagePackMetadata(string Id, string SourceLanguage, string TargetLanguage,
    string DisplayName, string Version, string Quality, string Runtime, string ModelType,
    long SizeBytes, string InstallationPath, bool Installed);
