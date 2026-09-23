using ScreenTranslator.Translation.AI;

namespace ScreenTranslator.Translation.Security;

/// <summary>Selects the credential for the currently active online translation provider.</summary>
public sealed class ProviderApiKeyStore(
    AiTranslationOptions options,
    IApiKeyStore deepSeek,
    IApiKeyStore deepL) : IApiKeyStore
{
    private IApiKeyStore Active => options.Provider == "DeepL" ? deepL : deepSeek;
    public bool HasKey => Active.HasKey;
    public string? Read() => Active.Read();
    public void Save(string apiKey) => Active.Save(apiKey);
    public void Delete() => Active.Delete();
}
