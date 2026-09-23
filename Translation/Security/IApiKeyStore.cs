namespace ScreenTranslator.Translation.Security;

public interface IApiKeyStore
{
    bool HasKey { get; }
    string? Read();
    void Save(string apiKey);
    void Delete();
}
