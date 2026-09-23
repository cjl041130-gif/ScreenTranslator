using System.Diagnostics;
using System.Windows;
using ScreenTranslator.Models;
using ScreenTranslator.Translation.Security;

namespace ScreenTranslator.Translation.AI;

public sealed class AiTranslationEngine : ITranslationEngine, IDocumentPageTranslationEngine
{
    private readonly AiTranslationOptions _options;
    private readonly IApiKeyStore _keyStore;
    private readonly IAiTranslationClient _client;
    private readonly AiTranslationResultValidator _validator;
    private readonly AiTranslationCache _cache;
    public string Name => _options.Provider == "DeepL" ? "DeepL 在线翻译" :
        _options.Model == "deepseek-v4-pro" ? "DeepSeek V4 Pro" : "DeepSeek Flash";
    public bool IsInitialized { get; private set; }
    public bool IsConfigured => _keyStore.HasKey;
    public LanguagePackMetadata? LanguagePack => null;
    public AiTranslationOptions Options => _options;

    public AiTranslationEngine(AiTranslationOptions options, IApiKeyStore keyStore, IAiTranslationClient client,
        AiTranslationResultValidator? validator = null, AiTranslationCache? cache = null)
    { _options=options; _keyStore=keyStore; _client=client; _validator=validator ?? new(); _cache=cache ?? new(options.CacheCapacity); }

    public Task InitializeAsync(InferenceDevice preferredDevice, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); _options.Normalize(); IsInitialized=true; return Task.CompletedTask; }

    public async Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        var block = new OcrBlock("single", request.Text, 1, Rect.Empty, 0, 0, request.BlockType);
        return (await TranslatePageAsync([block], request.SourceLanguage, request.TargetLanguage, cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<IReadOnlyList<TranslationResponse>> TranslatePageAsync(IReadOnlyList<OcrBlock> blocks,
        string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
        => await TranslateBatchAsync(blocks, sourceLanguage, targetLanguage, "presentation", cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<TranslationResponse>> TranslateDocumentBatchAsync(IReadOnlyList<OcrBlock> blocks,
        string sourceLanguage, string targetLanguage, string documentType, CancellationToken cancellationToken)
        => await TranslateBatchAsync(blocks, sourceLanguage, targetLanguage, documentType.ToLowerInvariant(), cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<TranslationResponse>> TranslateBatchAsync(IReadOnlyList<OcrBlock> blocks,
        string sourceLanguage, string targetLanguage, string scene, CancellationToken cancellationToken)
    {
        if (!IsInitialized) throw new InvalidOperationException("AI 翻译引擎尚未初始化。");
        if (blocks.Count == 0) return [];
        var completeRequest = CreateRequest(blocks, sourceLanguage, targetLanguage, false, scene);
        var key = _cache.CreateKey(completeRequest, _options.Provider);
        if (_cache.TryGet(key, out var cached)) return cached.Select(x => new TranslationResponse(x, false, 0)).ToArray();
        var translatable=blocks.Where(x=>!IsTechnicalBlock(x.BlockType)).ToArray();
        if(translatable.Length==0)
        {
            var empty=blocks.Select(_=>string.Empty).ToArray();_cache.Put(key,empty);
            return empty.Select(x=>new TranslationResponse(x,false,0)).ToArray();
        }
        var request = CreateRequest(translatable, sourceLanguage, targetLanguage, false, scene);
        var apiKey = _keyStore.Read();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new AiTranslationException(AiTranslationErrorKind.NotConfigured,
                $"{(_options.Provider == "DeepL" ? "DeepL" : "DeepSeek")} 翻译尚未配置，请前往“设置”填写 API Key。");
        var total = Stopwatch.StartNew();
        try
        {
            AiClientResult response; IReadOnlyList<AiTranslatedItem> translated;
            try
            {
                response = await _client.TranslatePageAsync(request, apiKey, cancellationToken).ConfigureAwait(false);
                translated = _validator.Validate(request, response.OutputText);
            }
            catch (AiTranslationException ex) when (ex.Kind == AiTranslationErrorKind.InvalidResponse && _options.Provider == "DeepSeek")
            {
                var repair = CreateRequest(translatable, sourceLanguage, targetLanguage, true, scene);
                response = await _client.TranslatePageAsync(repair, apiKey, cancellationToken).ConfigureAwait(false);
                translated = _validator.Validate(repair, response.OutputText);
            }
            total.Stop();
            var byId=translated.ToDictionary(x=>x.Id,x=>x.TranslatedText,StringComparer.Ordinal);
            var values = blocks.Select(x=>IsTechnicalBlock(x.BlockType)?string.Empty:byId[x.Id]).ToArray();
            _cache.Put(key, values);
            var perBlock = total.Elapsed.TotalMilliseconds / Math.Max(1, values.Length);
            return values.Select(x => new TranslationResponse(x, false, perBlock)).ToArray();
        }
        finally { apiKey = string.Empty; }
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        var wasInitialized = IsInitialized;
        if (!wasInitialized) await InitializeAsync(InferenceDevice.Cpu, cancellationToken).ConfigureAwait(false);
        var request = new TranslationRequest("en", "zh-CN", "Connection test", new("", [], new Dictionary<string,string>(), ""), OcrBlockType.Body);
        _ = await TranslateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private AiPageTranslationRequest CreateRequest(IReadOnlyList<OcrBlock> blocks, string source, string target, bool repair, string scene = "presentation") =>
        new(scene, source, target, _options.Style, _options.Model,
            blocks.Select((x,i) => new AiTranslationBlock(x.Id, x.BlockType.ToString().ToLowerInvariant(), i, x.OriginalText,
                x.X, x.Y, x.Width, x.Height)).ToArray(), repair);

    private static bool IsTechnicalBlock(OcrBlockType type)=>type is OcrBlockType.Code or OcrBlockType.Formula;

    public ValueTask DisposeAsync() { IsInitialized=false; return ValueTask.CompletedTask; }
}
