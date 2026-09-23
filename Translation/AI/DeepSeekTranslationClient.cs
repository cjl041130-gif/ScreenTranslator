using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ScreenTranslator.Services;

namespace ScreenTranslator.Translation.AI;

public sealed class DeepSeekTranslationClient : IAiTranslationClient
{
    private readonly HttpClient _http;
    private readonly AiTranslationOptions _options;
    private readonly AppLogger _log;
    public DeepSeekTranslationClient(HttpClient http, AiTranslationOptions options, AppLogger log)
    { _http = http; _options = options; _log = log; }

    public async Task<AiClientResult> TranslatePageAsync(AiPageTranslationRequest request, string apiKey, CancellationToken cancellationToken)
    {
        var payload = BuildPayload(request);
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            message.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            var watch = Stopwatch.StartNew();
            _log.Info($"DeepSeek translation request started: model={request.Model}; blocks={request.Blocks.Count}; characters={request.Blocks.Sum(x => x.Text.Length)}; attempt={attempt + 1}");
            try
            {
                using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                var responseText = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                watch.Stop();
                if (!response.IsSuccessStatusCode)
                {
                    _log.Info($"DeepSeek translation request failed: status={(int)response.StatusCode}; elapsedMs={watch.Elapsed.TotalMilliseconds:F0}; attempt={attempt + 1}");
                    if (ShouldRetry(response.StatusCode) && attempt < _options.MaximumRetries)
                    { await Task.Delay(RetryDelay(attempt), cancellationToken).ConfigureAwait(false); continue; }
                    throw MapError(response.StatusCode, responseText);
                }
                var parsed = ParseResponse(responseText, watch.Elapsed.TotalMilliseconds, (int)response.StatusCode);
                _log.Info($"DeepSeek translation request completed: status={(int)response.StatusCode}; elapsedMs={watch.Elapsed.TotalMilliseconds:F0}; inputTokens={parsed.InputTokens}; outputTokens={parsed.OutputTokens}");
                return parsed;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new AiTranslationException(AiTranslationErrorKind.Timeout, "连接 DeepSeek 超时，请检查网络。"); }
            catch (HttpRequestException ex) when (attempt < _options.MaximumRetries)
            { _log.Info($"DeepSeek network retry: attempt={attempt + 1}; type={ex.GetType().Name}"); await Task.Delay(RetryDelay(attempt), cancellationToken).ConfigureAwait(false); }
            catch (HttpRequestException ex)
            { throw new AiTranslationException(AiTranslationErrorKind.Network, "无法连接 DeepSeek，请检查网络。", ex); }
        }
    }

    private string BuildPayload(AiPageTranslationRequest request)
    {
        var input = new
        {
            scene = request.Scene, source_language = request.SourceLanguage, target_language = request.TargetLanguage,
            style = request.Style.ToString(), blocks = request.Blocks.Select(x => new
            {
                id=x.Id, type=x.Type, order=x.Order, text=x.Text,
                bounding_box=new { x=Finite(x.X), y=Finite(x.Y), width=Finite(x.Width), height=Finite(x.Height) },
                is_title=x.Type.Equals("title",StringComparison.OrdinalIgnoreCase),
                is_list=x.Type.Equals("bullet",StringComparison.OrdinalIgnoreCase),
                is_code=x.Type.Equals("code",StringComparison.OrdinalIgnoreCase),
                is_formula=x.Type.Equals("formula",StringComparison.OrdinalIgnoreCase)
            })
        };
        var body = new
        {
            model = request.Model,
            messages = new object[]
            {
                new { role="system", content=SystemPrompt(request.IsRepairAttempt) },
                new { role="user", content=JsonSerializer.Serialize(input) }
            },
            thinking = new { type="disabled" },
            response_format = new { type="json_object" },
            stream = false
        };
        return JsonSerializer.Serialize(body);
    }

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;

    private static string SystemPrompt(bool repair) =>
        "你是一名专业的英文到简体中文翻译专家。内容来自PPT、PDF、Word、网页课件或屏幕文档。" +
        "必须完整翻译所有文字块并保持每个ID和输入顺序；不得遗漏、合并、拆分、解释、总结、扩写或虚构。" +
        "每个正文文字块已经按段落合并，必须把其中跨行的句子作为一个连续段落翻译，保持前后语义衔接。" +
        "标题简洁自然，正文忠实流畅，列表保持列表语气；代码、变量、URL、路径和公式保持原样，专有名词按语义处理。" +
        "只返回一个JSON对象，格式必须是{\"translations\":[{\"id\":\"原ID\",\"translated_text\":\"中文译文\"}]}，不得使用Markdown。" +
        (repair ? "上一次结果未通过完整性检查，本次必须逐项核对所有ID。" : "");

    private static AiClientResult ParseResponse(string json, double elapsed, int status)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string? output = null;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
                    output = content.GetString();
            }
            if (string.IsNullOrWhiteSpace(output)) throw new AiTranslationException(AiTranslationErrorKind.InvalidResponse, "DeepSeek 返回格式无效，本页暂未覆盖翻译。");
            var inputTokens = 0; var outputTokens = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var i)) inputTokens = i.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var o)) outputTokens = o.GetInt32();
            }
            return new(output, inputTokens, outputTokens, elapsed, status);
        }
        catch(JsonException ex){throw new AiTranslationException(AiTranslationErrorKind.InvalidResponse,"DeepSeek 返回格式无效，本页暂未覆盖翻译。",ex);}
    }

    private static bool ShouldRetry(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests || (int)status >= 500;
    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 500 + Random.Shared.Next(80, 220));
    private static AiTranslationException MapError(HttpStatusCode status, string body)
    {
        var message = TryReadError(body);
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(AiTranslationErrorKind.Authentication, "DeepSeek API Key 无效，请在设置中重新填写。"),
            HttpStatusCode.PaymentRequired => new(AiTranslationErrorKind.Quota, "DeepSeek 账户余额不足，请充值后重试。"),
            HttpStatusCode.TooManyRequests => new(AiTranslationErrorKind.Quota, "DeepSeek 请求过于频繁，请稍后重试。"),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity when message.Contains("model", StringComparison.OrdinalIgnoreCase) => new(AiTranslationErrorKind.Model, "当前 DeepSeek 账户无法使用所选模型，请检查模型名称。"),
            _ => new(AiTranslationErrorKind.Service, $"DeepSeek 服务暂时不可用（HTTP {(int)status}）。")
        };
    }
    private static string TryReadError(string body)
    {
        try { using var doc=JsonDocument.Parse(body); return doc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? ""; }
        catch { return ""; }
    }
}
