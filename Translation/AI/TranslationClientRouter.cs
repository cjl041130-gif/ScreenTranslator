namespace ScreenTranslator.Translation.AI;

public sealed class TranslationClientRouter(
    AiTranslationOptions options,
    IAiTranslationClient deepSeek,
    IAiTranslationClient deepL) : IAiTranslationClient
{
    public Task<AiClientResult> TranslatePageAsync(AiPageTranslationRequest request, string apiKey,
        CancellationToken cancellationToken) =>
        (options.Provider == "DeepL" ? deepL : deepSeek)
        .TranslatePageAsync(request, apiKey, cancellationToken);
}
