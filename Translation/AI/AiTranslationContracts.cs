using ScreenTranslator.Models;

namespace ScreenTranslator.Translation.AI;

public sealed record AiTranslationBlock(string Id, string Type, int Order, string Text,
    double X, double Y, double Width, double Height);

public sealed record AiPageTranslationRequest(string Scene, string SourceLanguage, string TargetLanguage,
    AiTranslationStyle Style, string Model, IReadOnlyList<AiTranslationBlock> Blocks, bool IsRepairAttempt = false);

public sealed record AiClientResult(string OutputText, int InputTokens, int OutputTokens,
    double ProcessingMilliseconds, int HttpStatusCode);

public sealed record AiTranslatedItem(string Id, string TranslatedText);

public interface IAiTranslationClient
{
    Task<AiClientResult> TranslatePageAsync(AiPageTranslationRequest request, string apiKey,
        CancellationToken cancellationToken);
}

public enum AiTranslationErrorKind { NotConfigured, Authentication, Quota, Model, Timeout, Network, InvalidResponse, Service }

public sealed class AiTranslationException : Exception
{
    public AiTranslationErrorKind Kind { get; }
    public AiTranslationException(AiTranslationErrorKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;
}
