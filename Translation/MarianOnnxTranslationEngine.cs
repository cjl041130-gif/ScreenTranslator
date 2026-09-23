using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ScreenTranslator.Models;

namespace ScreenTranslator.Translation;

public sealed partial class MarianOnnxTranslationEngine : ITranslationEngine
{
    private const int DecoderStartTokenId = 65000;
    private const int EosTokenId = 0;
    private const int VocabularySize = 65001;
    private readonly LocalLanguagePackService _packs;
    private readonly TranslationMemoryCache _cache = new();
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly Dictionary<string, string> _exactTranslations = new(StringComparer.OrdinalIgnoreCase);
    private InferenceSession? _encoder;
    private InferenceSession? _decoder;
    private MarianTokenizerAdapter? _tokenizer;
    private bool _disposed;

    public string Name => LanguagePack is null
        ? "English → 中文 · 本地神经翻译"
        : $"{LanguagePack.DisplayName} · {LanguagePack.Quality} · CPU";
    public bool IsInitialized { get; private set; }
    public LanguagePackMetadata? LanguagePack { get; private set; }
    public long CacheHits => _cache.Hits;

    public MarianOnnxTranslationEngine(LocalLanguagePackService? packs = null) => _packs = packs ?? new();

    public async Task InitializeAsync(InferenceDevice preferredDevice, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsInitialized) return;
        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsInitialized) return;
            var pack = _packs.Inspect("en-zh-neural")
                ?? throw new FileNotFoundException("未安装 English → 中文神经翻译语言包。", _packs.GetPackDirectory("en-zh-neural"));
            await Task.Run(() => Load(pack, cancellationToken), cancellationToken).ConfigureAwait(false);
            LanguagePack = pack;
            IsInitialized = true;
        }
        finally { _inferenceGate.Release(); }
    }

    public async Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        if (!IsInitialized || _encoder is null || _decoder is null || _tokenizer is null)
            throw new InvalidOperationException("翻译引擎尚未初始化。");
        cancellationToken.ThrowIfCancellationRequested();

        var source = NormalizeSource(request.Text);
        if (source.Length == 0 || !LatinText().IsMatch(source)) return new(source, false, 0);
        if (_exactTranslations.TryGetValue(source, out var exact)) return new(exact, false, 0);
        var cacheKey = $"{request.SourceLanguage}\u001f{request.TargetLanguage}\u001f{source}";
        if (_cache.TryGet(cacheKey, out var cached)) return cached;

        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGet(cacheKey, out cached)) return cached;
            var watch = Stopwatch.StartNew();
            var translated = new List<string>();
            var partial = false;
            foreach (var segment in TranslationTextSegmenter.Split(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = TranslateSegment(segment, cancellationToken);
                if (string.IsNullOrWhiteSpace(value)) { value = segment; partial = true; }
                translated.Add(RestoreTerminalPunctuation(segment, NormalizeTarget(value)));
            }
            watch.Stop();
            var response = new TranslationResponse(string.Concat(translated), partial, watch.Elapsed.TotalMilliseconds);
            _cache.Put(cacheKey, response);
            return response;
        }
        finally { _inferenceGate.Release(); }
    }

    private void Load(LanguagePackMetadata pack, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateManifest(pack.InstallationPath);
        LoadExactTranslations();
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
            EnableCpuMemArena = true,
            EnableMemoryPattern = true
        };
        try
        {
            _tokenizer = new MarianTokenizerAdapter(pack.InstallationPath);
            _encoder = new InferenceSession(Path.Combine(pack.InstallationPath, "onnx", "encoder_model_quantized.onnx"), options);
            _decoder = new InferenceSession(Path.Combine(pack.InstallationPath, "onnx", "decoder_model_quantized.onnx"), options);
        }
        catch
        {
            _encoder?.Dispose(); _encoder = null;
            _decoder?.Dispose(); _decoder = null;
            _tokenizer?.Dispose(); _tokenizer = null;
            throw;
        }
        finally { options.Dispose(); }
    }

    private string TranslateSegment(string source, CancellationToken cancellationToken)
    {
        var inputIds = _tokenizer!.Encode(source);
        if (inputIds.Length > 500) throw new InvalidDataException("单个翻译片段超过模型的 500 token 限制。");
        var attention = Enumerable.Repeat(1L, inputIds.Length).ToArray();
        var inputTensor = new DenseTensor<long>(inputIds, [1, inputIds.Length]);
        var attentionTensor = new DenseTensor<long>(attention, [1, attention.Length]);
        var inputValue = NamedOnnxValue.CreateFromTensor("input_ids", inputTensor);
        var attentionValue = NamedOnnxValue.CreateFromTensor("attention_mask", attentionTensor);
        using var encoded = _encoder!.Run([inputValue, attentionValue], ["last_hidden_state"]);
        var hidden = encoded[0].AsTensor<float>();

        var generated = new List<long>(Math.Min(160, inputIds.Length * 2 + 16)) { DecoderStartTokenId };
        var maxOutputTokens = Math.Clamp(inputIds.Length * 3 + 12, 20, 160);
        for (var step = 0; step < maxOutputTokens; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decoderTensor = new DenseTensor<long>(generated.ToArray(), [1, generated.Count]);
            var decoderValue = NamedOnnxValue.CreateFromTensor("input_ids", decoderTensor);
            var encoderMaskValue = NamedOnnxValue.CreateFromTensor("encoder_attention_mask", attentionTensor);
            var hiddenValue = NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden);
            using var decoded = _decoder!.Run([encoderMaskValue, decoderValue, hiddenValue], ["logits"]);
            var logits = decoded[0].AsTensor<float>();
            var next = ArgMax(logits, generated.Count - 1);
            if (next == EosTokenId) break;
            generated.Add(next);
        }

        return _tokenizer.Decode(generated.Skip(1).Select(x => (int)x));
    }

    private static int ArgMax(Tensor<float> logits, int position)
    {
        var offset = position * VocabularySize;
        var best = EosTokenId;
        var bestScore = float.NegativeInfinity;
        for (var token = 0; token < DecoderStartTokenId; token++)
        {
            var score = logits.GetValue(offset + token);
            if (score <= bestScore) continue;
            bestScore = score;
            best = token;
        }
        return best;
    }

    private void LoadExactTranslations()
    {
        _exactTranslations.Clear();
        var legacy = Path.Combine(_packs.GetPackDirectory("en-zh"), "model", "phrase-table.json");
        if (!File.Exists(legacy)) return;
        using var document = JsonDocument.Parse(File.ReadAllText(legacy));
        foreach (var group in new[] { "phrases", "terminology" })
        {
            if (!document.RootElement.TryGetProperty(group, out var values)) continue;
            foreach (var item in values.EnumerateObject()) _exactTranslations[item.Name.Trim()] = item.Value.GetString() ?? item.Name;
        }
    }

    private static void ValidateManifest(string directory)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("语言包完整性清单缺失。", manifestPath);
        var manifest = JsonSerializer.Deserialize<ModelManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("语言包完整性清单格式无效。");
        foreach (var item in manifest.Files)
        {
            var path = Path.GetFullPath(Path.Combine(directory, item.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new FileNotFoundException($"语言包文件缺失：{item.Path}", path);
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"语言包文件校验失败：{item.Path}");
        }
    }

    private static string NormalizeSource(string text) => Whitespace().Replace(text.Replace("\r", " ").Replace("\n", " "), " ").Trim();

    private static string NormalizeTarget(string text)
    {
        var value = SpaceBeforePunctuation().Replace(text.Trim(), "$1");
        value = ChineseDuplicateWithParticle().Replace(value, "${word}的");
        if (!value.Any(c => c is >= '\u3400' and <= '\u9fff')) return value;
        return value.Replace(",", "，").Replace(";", "；").Replace(":", "：");
    }

    private static string RestoreTerminalPunctuation(string source, string translated)
    {
        if (translated.Length == 0 || "。！？；：,.!?;:".Contains(translated[^1])) return translated;
        return source[^1] switch { '.' => translated + "。", '?' => translated + "？", '!' => translated + "！", ';' => translated + "；", ':' => translated + "：", _ => translated };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _inferenceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            IsInitialized = false;
            _encoder?.Dispose(); _encoder = null;
            _decoder?.Dispose(); _decoder = null;
            _tokenizer?.Dispose(); _tokenizer = null;
            _cache.Clear();
        }
        finally { _inferenceGate.Release(); _inferenceGate.Dispose(); }
    }

    private sealed record ModelManifest(IReadOnlyList<ModelManifestFile> Files);
    private sealed record ModelManifestFile(string Path, string Sha256, long SizeBytes);

    [GeneratedRegex("[A-Za-z]")]
    private static partial Regex LatinText();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
    [GeneratedRegex(@"\s+([，。！？；：,.!?;:])")]
    private static partial Regex SpaceBeforePunctuation();
    [GeneratedRegex(@"(?<word>[\u3400-\u9fff]{2,4}?)的\k<word>")]
    private static partial Regex ChineseDuplicateWithParticle();
}
