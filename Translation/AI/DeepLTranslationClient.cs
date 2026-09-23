using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ScreenTranslator.Services;

namespace ScreenTranslator.Translation.AI;

public sealed class DeepLTranslationClient : IAiTranslationClient
{
    private const string FreeEndpoint = "https://api-free.deepl.com/v2/translate";
    private const string PaidEndpoint = "https://api.deepl.com/v2/translate";
    private readonly HttpClient _http;
    private readonly AiTranslationOptions _options;
    private readonly AppLogger _log;

    public DeepLTranslationClient(HttpClient http, AiTranslationOptions options, AppLogger log)
    { _http = http; _options = options; _log = log; }

    public async Task<AiClientResult> TranslatePageAsync(AiPageTranslationRequest request, string apiKey,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var translated = new List<object>(request.Blocks.Count);
        var lastStatus = 200;
        foreach (var batch in CreateBatches(request.Blocks))
        {
            var response = await TranslateBatchAsync(batch, request, apiKey, cancellationToken).ConfigureAwait(false);
            lastStatus = response.Status;
            for (var i = 0; i < batch.Count; i++)
                translated.Add(new { id = batch[i].Id, translated_text = response.Texts[i] });
        }
        watch.Stop();
        return new AiClientResult(JsonSerializer.Serialize(new { translations = translated }), 0, 0,
            watch.Elapsed.TotalMilliseconds, lastStatus);
    }

    private async Task<(IReadOnlyList<string> Texts, int Status)> TranslateBatchAsync(
        IReadOnlyList<AiTranslationBlock> blocks, AiPageTranslationRequest request, string apiKey,
        CancellationToken cancellationToken)
    {
        var endpoint = apiKey.EndsWith(":fx", StringComparison.OrdinalIgnoreCase) ? FreeEndpoint : PaidEndpoint;
        var payload = JsonSerializer.Serialize(new
        {
            text = blocks.Select(x => x.Text).ToArray(),
            source_lang = MapSourceLanguage(request.SourceLanguage),
            target_lang = MapTargetLanguage(request.TargetLanguage),
            preserve_formatting = true,
            split_sentences = "nonewlines"
        });
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
            message.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + apiKey);
            message.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            var requestWatch = Stopwatch.StartNew();
            _log.Info($"DeepL translation request started: blocks={blocks.Count}; characters={blocks.Sum(x => x.Text.Length)}; attempt={attempt + 1}");
            try
            {
                using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                requestWatch.Stop();
                if (!response.IsSuccessStatusCode)
                {
                    _log.Info($"DeepL translation request failed: status={(int)response.StatusCode}; elapsedMs={requestWatch.Elapsed.TotalMilliseconds:F0}; attempt={attempt + 1}");
                    if (ShouldRetry(response.StatusCode) && attempt < _options.MaximumRetries)
                    { await Task.Delay(RetryDelay(attempt), cancellationToken).ConfigureAwait(false); continue; }
                    throw MapError(response.StatusCode);
                }
                var texts = ParseTranslations(body, blocks.Count);
                _log.Info($"DeepL translation request completed: status={(int)response.StatusCode}; elapsedMs={requestWatch.Elapsed.TotalMilliseconds:F0}; blocks={texts.Count}");
                return (texts, (int)response.StatusCode);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new AiTranslationException(AiTranslationErrorKind.Timeout, "连接 DeepL 超时，请检查网络。"); }
            catch (HttpRequestException ex) when (attempt < _options.MaximumRetries)
            { _log.Info($"DeepL network retry: attempt={attempt + 1}; type={ex.GetType().Name}"); await Task.Delay(RetryDelay(attempt), cancellationToken).ConfigureAwait(false); }
            catch (HttpRequestException ex)
            { throw new AiTranslationException(AiTranslationErrorKind.Network, "无法连接 DeepL，请检查网络。", ex); }
        }
    }

    private static IReadOnlyList<string> ParseTranslations(string json, int expected)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var array = document.RootElement.GetProperty("translations");
            var values = array.EnumerateArray().Select(x => x.GetProperty("text").GetString()?.Trim() ?? "").ToArray();
            if (values.Length != expected || values.Any(string.IsNullOrWhiteSpace))
                throw new AiTranslationException(AiTranslationErrorKind.InvalidResponse, "DeepL 返回内容不完整，本页暂未覆盖翻译。");
            return values;
        }
        catch (JsonException ex)
        { throw new AiTranslationException(AiTranslationErrorKind.InvalidResponse, "DeepL 返回格式无效，本页暂未覆盖翻译。", ex); }
        catch (KeyNotFoundException ex)
        { throw new AiTranslationException(AiTranslationErrorKind.InvalidResponse, "DeepL 返回格式无效，本页暂未覆盖翻译。", ex); }
    }

    private static IEnumerable<IReadOnlyList<AiTranslationBlock>> CreateBatches(IReadOnlyList<AiTranslationBlock> blocks)
    {
        var batch = new List<AiTranslationBlock>(50);
        var characters = 0;
        foreach (var block in blocks)
        {
            if (batch.Count >= 50 || characters + block.Text.Length > 60000)
            { yield return batch.ToArray(); batch.Clear(); characters = 0; }
            batch.Add(block); characters += block.Text.Length;
        }
        if (batch.Count > 0) yield return batch.ToArray();
    }

    private static string MapSourceLanguage(string value) => value.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "EN" : value.ToUpperInvariant();
    private static string MapTargetLanguage(string value) => value.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "ZH-HANS" : value.ToUpperInvariant();
    private static bool ShouldRetry(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests || (int)status >= 500;
    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 500 + Random.Shared.Next(80, 220));
    private static AiTranslationException MapError(HttpStatusCode status) => (int)status switch
    {
        401 or 403 => new(AiTranslationErrorKind.Authentication, "DeepL API Key 无效，请在设置中重新填写。"),
        456 => new(AiTranslationErrorKind.Quota, "DeepL 字符额度已用完，请检查套餐或费用上限。"),
        429 => new(AiTranslationErrorKind.Quota, "DeepL 请求过于频繁，请稍后重试。"),
        _ => new(AiTranslationErrorKind.Service, $"DeepL 服务暂时不可用（HTTP {(int)status}）。")
    };
}
