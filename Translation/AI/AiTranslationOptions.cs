namespace ScreenTranslator.Translation.AI;

public enum AiTranslationStyle { Faithful, Natural, Presentation }

public sealed class AiTranslationOptions
{
    public string Mode { get; set; } = "Ai";
    public string Provider { get; set; } = "DeepL";
    public string Model { get; set; } = "deepseek-v4-pro";
    public int ProviderSelectionVersion { get; set; } = 1;
    public string Endpoint { get; set; } = "https://api.deepseek.com/chat/completions";
    public bool UploadImages { get; set; }
    public int TimeoutSeconds { get; set; } = 20;
    public int MaximumRetries { get; set; } = 2;
    public int CacheCapacity { get; set; } = 128;
    public AiTranslationStyle Style { get; set; } = AiTranslationStyle.Natural;

    public void Normalize()
    {
        Mode = "Ai";
        Provider = Provider.Equals("DeepSeek", StringComparison.OrdinalIgnoreCase) ? "DeepSeek" :
            Provider.Equals("DeepL", StringComparison.OrdinalIgnoreCase) ? "DeepL" : "DeepL";
        UploadImages = false;
        Model = Model.Equals("deepseek-flash", StringComparison.OrdinalIgnoreCase)
            ? "deepseek-flash"
            : Model.Equals("deepseek-v4-pro", StringComparison.OrdinalIgnoreCase)
                ? "deepseek-v4-pro"
                : "deepseek-v4-pro";
        ProviderSelectionVersion = 1;
        // The endpoint is intentionally fixed. This prevents a damaged or legacy user setting
        // from sending recognized screen text to an untrusted host.
        Endpoint = "https://api.deepseek.com/chat/completions";
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 5, 120);
        MaximumRetries = Math.Clamp(MaximumRetries, 0, 2);
        CacheCapacity = Math.Clamp(CacheCapacity, 8, 512);
        if (!Enum.IsDefined(Style)) Style = AiTranslationStyle.Natural;
    }
}
