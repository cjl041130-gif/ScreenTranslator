using System.IO;
using System.Text.Json;

namespace ScreenTranslator.Services;

public sealed class AppSettings
{
    public ScreenTranslator.Vision.VisionSettings Vision { get; set; } = new();
    public ScreenTranslator.Recognition.RecognitionSettings Recognition { get; set; } = new();
    public ScreenTranslator.Translation.AI.AiTranslationOptions Translation { get; set; } = new();
    public ScreenTranslator.OCR.OcrSettings OCR { get; set; } = new();
    public ScreenTranslator.Document.DocumentTranslationOptions DocumentTranslation { get; set; } = new();
    public string? SelectedMonitor { get; set; }
    public int TargetFPS { get; set; } = 30;
    public bool DebugMode { get; set; } = true;
    // Manual-only product policy: even legacy/malformed intent cannot enable auto capture.
    public bool AutoStart { get => false; set { } }

    public static string DataDirectory => Environment.GetEnvironmentVariable("SCREEN_TRANSLATOR_DATA_DIR") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenTranslator");
    public static string UserPath => Path.Combine(DataDirectory, "appsettings.json");

    public static AppSettings Load(AppLogger log)
    {
        var path = File.Exists(UserPath) ? UserPath : Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new();
            // v0.3.9 changes the product default from DeepSeek to DeepL. Existing settings
            // written before provider selection was versioned are migrated exactly once.
            using (var document = JsonDocument.Parse(json))
            {
                if (document.RootElement.TryGetProperty("Translation", out var translation) &&
                    !translation.TryGetProperty("ProviderSelectionVersion", out _))
                {
                    settings.Translation ??= new();
                    settings.Translation.Provider = "DeepL";
                    settings.Translation.Model = "deepseek-v4-pro";
                }
            }
            settings.TargetFPS = Math.Clamp(settings.TargetFPS, 1, 60);
            settings.Vision ??= new(); settings.Vision.Normalize();
            settings.Recognition ??= new(); settings.Recognition.Normalize();
            settings.Translation ??= new(); settings.Translation.Normalize();
            settings.OCR ??= new(); settings.OCR.Normalize();
            settings.DocumentTranslation ??= new(); settings.DocumentTranslation.Normalize();
            return settings;
        }
        catch (Exception ex) { log.Error("Settings load failed; using defaults", ex); return new(); }
    }

    public void Save(AppLogger log)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var temporary = UserPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, UserPath, true);
        }
        catch (Exception ex) { log.Error("Settings save failed", ex); }
    }
}
