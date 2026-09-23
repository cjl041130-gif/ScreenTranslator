using ScreenTranslator.Translation;

namespace ScreenTranslator.Document;

public interface IDocumentParser
{
    DocumentType DocumentType { get; }
    Task<TranslationDocument> ParseAsync(string path, IProgress<DocumentProgress>? progress, CancellationToken cancellationToken);
}

public interface IDocumentImportService
{
    Task<TranslationDocument> ImportAsync(string path, IProgress<DocumentProgress>? progress, CancellationToken cancellationToken);
}

public interface IDocumentTranslationOrchestrator
{
    Task TranslateAsync(TranslationDocument document, IPageTranslationEngine engine,
        IProgress<DocumentProgress>? progress, CancellationToken cancellationToken);
}

public interface IDocumentExportService
{
    bool Supports(DocumentType type);
    Task<DocumentExportResult> ExportAsync(TranslationDocument document, string outputPath,
        DocumentExportMode mode, IProgress<DocumentProgress>? progress, CancellationToken cancellationToken);
}

public sealed class DocumentUserException(string message, Exception? inner = null) : Exception(message, inner);

