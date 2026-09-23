using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenTranslator.Models;

namespace ScreenTranslator.Translation;

public sealed partial class LocalPhraseTranslationEngine : ITranslationEngine
{
    private readonly LocalLanguagePackService _packs;
    private readonly TerminologyManager _terminology;
    private Dictionary<string, string> _phrases = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _words = new(StringComparer.OrdinalIgnoreCase);
    public string Name => LanguagePack is null ? "English → Chinese 离线语言包" : $"{LanguagePack.DisplayName} · {LanguagePack.Quality}";
    public bool IsInitialized { get; private set; }
    public LanguagePackMetadata? LanguagePack { get; private set; }
    public LocalPhraseTranslationEngine(LocalLanguagePackService? packs = null, TerminologyManager? terminology = null)
    { _packs = packs ?? new(); _terminology = terminology ?? new(); }

    public Task InitializeAsync(InferenceDevice preferredDevice, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LanguagePack = _packs.Inspect("en-zh") ?? throw new FileNotFoundException("未安装 English → Chinese 离线语言包。", _packs.GetPackDirectory("en-zh"));
        var modelPath = Path.Combine(LanguagePack.InstallationPath, "model", "phrase-table.json");
        if (!File.Exists(modelPath)) throw new FileNotFoundException("语言包模型文件缺失。", modelPath);
        var model = JsonSerializer.Deserialize<ModelFile>(File.ReadAllText(modelPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("语言包模型格式无效。");
        _phrases = new(model.Phrases, StringComparer.OrdinalIgnoreCase);
        _words = new(model.Words, StringComparer.OrdinalIgnoreCase);
        _terminology.Load(model.Terminology);
        IsInitialized = true;
        return Task.CompletedTask;
    }

    public Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        if (!IsInitialized) throw new InvalidOperationException("翻译引擎尚未初始化。");
        cancellationToken.ThrowIfCancellationRequested(); var watch = Stopwatch.StartNew();
        var source = request.Text.Trim();
        if (_phrases.TryGetValue(source, out var exact)) return Task.FromResult(new TranslationResponse(exact, false, watch.Elapsed.TotalMilliseconds));
        var value = _terminology.Apply(source, out var termChanged);
        var translatedWords = 0; var totalWords = 0;
        value = WordToken().Replace(value, match =>
        {
            totalWords++;
            if (!_words.TryGetValue(match.Value, out var translated)) return match.Value;
            translatedWords++; return translated;
        });
        value = CleanupChineseSpacing(value);
        var changed = termChanged || translatedWords > 0;
        return Task.FromResult(new TranslationResponse(changed ? value : source, !changed || translatedWords < totalWords, watch.Elapsed.TotalMilliseconds));
    }

    private static string CleanupChineseSpacing(string text)
    {
        var value = Regex.Replace(text, @"(?<=[\u4e00-\u9fff])\s+(?=[\u4e00-\u9fff])", "");
        value = Regex.Replace(value, @"\s+([，。；：！？])", "$1");
        return value;
    }
    [GeneratedRegex("[A-Za-z]+(?:'[A-Za-z]+)?")]
    private static partial Regex WordToken();
    public ValueTask DisposeAsync() { IsInitialized = false; _phrases.Clear(); _words.Clear(); return ValueTask.CompletedTask; }
    private sealed record ModelFile(Dictionary<string, string> Phrases, Dictionary<string, string> Words, Dictionary<string, string> Terminology);
}
