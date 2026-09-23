using System.IO;
using System.Text.Json;

namespace ScreenTranslator.Translation;

public sealed class LocalLanguagePackService
{
    public string RootDirectory { get; }
    public LocalLanguagePackService(string? rootDirectory = null) => RootDirectory = rootDirectory ?? Path.Combine(AppContext.BaseDirectory, "ModelsData", "Translation");
    public string GetPackDirectory(string id) => Path.Combine(RootDirectory, id);
    public LanguagePackMetadata? Inspect(string id)
    {
        var directory = GetPackDirectory(id); var metadataPath = Path.Combine(directory, "metadata.json");
        if (!File.Exists(metadataPath)) return null;
        try
        {
            var file = JsonSerializer.Deserialize<MetadataFile>(File.ReadAllText(metadataPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (file is null) return null;
            var bytes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
            return new(file.Id, file.SourceLanguage, file.TargetLanguage, file.DisplayName, file.Version,
                file.Quality, file.Runtime, file.ModelType, bytes, directory, true);
        }
        catch { return null; }
    }
    private sealed record MetadataFile(string Id, string SourceLanguage, string TargetLanguage,
        string DisplayName, string Version, string Quality, string Runtime, string ModelType);
}
