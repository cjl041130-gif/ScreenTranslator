using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Windows;
using ScreenTranslator.Models;
using ScreenTranslator.Services;
using ScreenTranslator.Translation.AI;
using ScreenTranslator.Translation.Security;

namespace ScreenTranslator.Tests;

internal static partial class Program
{
    private static async Task RunAiTranslationTestsAsync()
    {
        var legacyOptions = new AiTranslationOptions
        {
            Provider = "OpenAI", Model = "gpt-5-mini", Endpoint = "https://api.openai.com/v1/responses"
        };
        legacyOptions.Normalize();
        Check(legacyOptions.Provider == "DeepL" && legacyOptions.Model == "deepseek-v4-pro" &&
              legacyOptions.Endpoint == "https://api.deepseek.com/chat/completions",
            "Legacy unsupported provider settings migrate to the DeepL product default");
        legacyOptions.Endpoint = "https://example.invalid/collect";
        legacyOptions.Normalize();
        Check(legacyOptions.Endpoint == "https://api.deepseek.com/chat/completions",
            "DeepSeek endpoint is fixed so OCR text cannot be redirected by damaged settings");

        var blocks = new[]
        {
            new OcrBlock("ocr-1", "Introduction to Computer Vision", .97, new Rect(10, 20, 420, 52), 0, 0, OcrBlockType.Title),
            new OcrBlock("ocr-2", "Computer vision enables machines to understand images.", .94, new Rect(10, 90, 760, 44), 1, 1, OcrBlockType.Body)
        };
        const string valid = "{\"translations\":[{\"id\":\"ocr-1\",\"translated_text\":\"计算机视觉导论\"},{\"id\":\"ocr-2\",\"translated_text\":\"计算机视觉使机器能够理解图像。\"}]}";
        var options = new AiTranslationOptions { Provider = "DeepSeek", Model = "deepseek-flash", MaximumRetries = 2, CacheCapacity = 8 };
        var keyStore = new MemoryKeyStore("sk-sensitive-unit-test-DO-NOT-LOG");
        var client = new QueueAiClient(valid);
        await using (var engine = new AiTranslationEngine(options, keyStore, client))
        {
            await engine.InitializeAsync(InferenceDevice.Cpu, CancellationToken.None);
            var translated = await engine.TranslatePageAsync(blocks, "en", "zh-CN", CancellationToken.None);
            Check(client.CallCount == 1 && client.Requests.Single().Blocks.Count == 2,
                "AI translation sends one whole-page request containing every OCR block");
            Check(translated.Count == 2 && translated[0].Text == "计算机视觉导论" && translated[1].Text.Contains("理解图像"),
                "AI translation maps complete results back by stable block IDs");
            _ = await engine.TranslatePageAsync(blocks, "en", "zh-CN", CancellationToken.None);
            Check(client.CallCount == 1, "Identical stable page uses SHA256 LRU cache without another API request");
        }

        var mixedBlocks = new[]
        {
            new OcrBlock("body", "Explain the sample output", .95, new Rect(0,0,300,40), 0,0,OcrBlockType.Body),
            new OcrBlock("code", "for item in values: print(item)", .96, new Rect(0,50,400,40), 1,1,OcrBlockType.Code),
            new OcrBlock("formula", "x = y + 1", .96, new Rect(0,100,200,40), 2,2,OcrBlockType.Formula)
        };
        const string mixedOutput="{\"translations\":[{\"id\":\"body\",\"translated_text\":\"解释示例输出\"}]}";
        var mixedClient=new QueueAiClient(mixedOutput);
        await using(var mixedEngine=new AiTranslationEngine(options,keyStore,mixedClient))
        {
            await mixedEngine.InitializeAsync(InferenceDevice.Cpu,CancellationToken.None);
            var mixed=await mixedEngine.TranslatePageAsync(mixedBlocks,"en","zh-CN",CancellationToken.None);
            Check(mixedClient.Requests.Single().Blocks.Count==1&&mixedClient.Requests.Single().Blocks[0].Id=="body",
                "Code and formula blocks are excluded from the DeepSeek request");
            Check(mixed.Count==3&&mixed[0].Text=="解释示例输出"&&mixed[1].Text==""&&mixed[2].Text=="",
                "Technical blocks remain visible on the slide without translation overlay boxes");
        }

        var request = new AiPageTranslationRequest("presentation", "en", "zh-CN", AiTranslationStyle.Natural,
            "deepseek-flash", blocks.Select((x, i) => new AiTranslationBlock(x.Id, x.BlockType.ToString(), i,
                x.OriginalText, x.X, x.Y, x.Width, x.Height)).ToArray());
        var validator = new AiTranslationResultValidator();
        ExpectInvalidTranslation(() => validator.Validate(request, "not json"), "Non-JSON AI response is rejected");
        ExpectInvalidTranslation(() => validator.Validate(request, "```json\n" + valid + "\n```"), "Markdown-wrapped JSON is rejected");
        ExpectInvalidTranslation(() => validator.Validate(request, "{\"translations\":[]}"), "Empty result is rejected");
        ExpectInvalidTranslation(() => validator.Validate(request, "{\"translations\":[{\"id\":\"ocr-1\",\"translated_text\":\"一\"}]}"), "Missing block ID is rejected");
        ExpectInvalidTranslation(() => validator.Validate(request, "{\"translations\":[{\"id\":\"ocr-1\",\"translated_text\":\"一\"},{\"id\":\"ocr-1\",\"translated_text\":\"二\"}]}"), "Duplicate block ID is rejected");
        ExpectInvalidTranslation(() => validator.Validate(request, "{\"translations\":[{\"id\":\"ocr-1\",\"translated_text\":\"一\"},{\"id\":\"unknown\",\"translated_text\":\"二\"}]}"), "Unknown block ID is rejected");
        ExpectInvalidTranslation(() => validator.Validate(request, "{\"translations\":[{\"id\":\"ocr-1\",\"translated_text\":\"\"},{\"id\":\"ocr-2\",\"translated_text\":\"二\"}]}"), "Empty translated text is rejected");
        ExpectInvalidTranslation(() => validator.Validate(request, "{\"translations\":[{\"id\":\"ocr-1\",\"translated_text\":\"Introduction to Computer Vision\"},{\"id\":\"ocr-2\",\"translated_text\":\"Computer vision enables machines to understand images\"}]}"), "Abnormal untranslated English residue is rejected");
        const string properNoun = "{\"translations\":[{\"id\":\"ocr-1\",\"translated_text\":\"DeepSeek V4 Pro\"},{\"id\":\"ocr-2\",\"translated_text\":\"计算机视觉使机器能够理解图像。\"}]}";
        Check(validator.Validate(request, properNoun)[0].TranslatedText == "DeepSeek V4 Pro",
            "Short product names may remain English without forcing a second whole-page request");
        const string oneLongProperName = "{\"translations\":[{\"id\":\"ocr-1\",\"translated_text\":\"Natural Language Processing Toolkit\"},{\"id\":\"ocr-2\",\"translated_text\":\"计算机视觉使机器能够理解图像。\"}]}";
        Check(validator.Validate(request, oneLongProperName).Count == 2,
            "One long English course or product name cannot discard all translated page blocks");

        var repairClient = new QueueAiClient("{\"translations\":[]}", valid);
        await using (var repairEngine = new AiTranslationEngine(options, keyStore, repairClient))
        {
            await repairEngine.InitializeAsync(InferenceDevice.Cpu, CancellationToken.None);
            var repaired = await repairEngine.TranslatePageAsync(blocks, "en", "zh-CN", CancellationToken.None);
            Check(repairClient.CallCount == 2 && repairClient.Requests[1].IsRepairAttempt && repaired.Count == 2,
                "Invalid structured result receives one stricter repair request");
        }

        var failedClient = new QueueAiClient("not json", "still not json");
        await using (var failedEngine = new AiTranslationEngine(options, keyStore, failedClient))
        {
            await failedEngine.InitializeAsync(InferenceDevice.Cpu, CancellationToken.None);
            await ExpectAiErrorAsync(() => failedEngine.TranslatePageAsync(blocks, "en", "zh-CN", CancellationToken.None),
                AiTranslationErrorKind.InvalidResponse, "Second invalid response stops without a fallback translation");
            Check(failedClient.CallCount == 2, "Invalid response retry is limited to one repair attempt");
        }

        await using (var missingKeyEngine = new AiTranslationEngine(options, new MemoryKeyStore(null), new QueueAiClient(valid)))
        {
            await missingKeyEngine.InitializeAsync(InferenceDevice.Cpu, CancellationToken.None);
            await ExpectAiErrorAsync(() => missingKeyEngine.TranslatePageAsync(blocks, "en", "zh-CN", CancellationToken.None),
                AiTranslationErrorKind.NotConfigured, "Missing API key performs no network request and returns a clear status");
        }

        var cancelClient = new CancelingAiClient();
        await using (var cancelEngine = new AiTranslationEngine(options, keyStore, cancelClient))
        {
            await cancelEngine.InitializeAsync(InferenceDevice.Cpu, CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            var task = cancelEngine.TranslatePageAsync(blocks, "en", "zh-CN", cancellation.Token);
            cancellation.Cancel();
            var canceled = false;
            try { await task; } catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "AI page request observes CancellationToken for page changes, Pause and Stop");
        }

        await TestHttpErrorMappingAndSecretLoggingAsync();
        await TestDeepLClientAsync();
        TestWindowsCredentialStore();
    }

    private static async Task TestHttpErrorMappingAndSecretLoggingAsync()
    {
        var logDirectory = Path.Combine(_output, "ai-http-logs");
        var log = new AppLogger(logDirectory);
        const string secret = "sk-sensitive-unit-test-DO-NOT-LOG";
        var successBody = System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "{\"translations\":[{\"id\":\"ocr-1\",\"translated_text\":\"连接测试\"}]}" } } },
            usage = new { prompt_tokens = 12, completion_tokens = 8 }
        });
        var success = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(successBody, Encoding.UTF8, "application/json") }));
        var deepSeek = new DeepSeekTranslationClient(new HttpClient(success), new AiTranslationOptions(), log);
        var successResult = await deepSeek.TranslatePageAsync(SingleRequest(), secret, CancellationToken.None);
        Check(successResult.OutputText.Contains("连接测试", StringComparison.Ordinal) && successResult.InputTokens == 12 && successResult.OutputTokens == 8,
            "DeepSeek Chat Completions response content and token usage are parsed");
        var nonFiniteBounds = new AiPageTranslationRequest("presentation", "en", "zh-CN", AiTranslationStyle.Natural,
            "deepseek-flash", [new("ocr-1", "body", 0, "Connection test", double.PositiveInfinity, double.NaN, double.NegativeInfinity, 0)]);
        _ = await deepSeek.TranslatePageAsync(nonFiniteBounds, secret, CancellationToken.None);
        using (var sanitized = System.Text.Json.JsonDocument.Parse(success.LastRequestBody!))
        {
            var userJson = sanitized.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            using var userPayload = System.Text.Json.JsonDocument.Parse(userJson);
            var bounds = userPayload.RootElement.GetProperty("blocks")[0].GetProperty("bounding_box");
            Check(bounds.GetProperty("x").GetDouble() == 0 && bounds.GetProperty("y").GetDouble() == 0 && bounds.GetProperty("width").GetDouble() == 0,
                "DeepSeek payload sanitizes non-finite test and OCR coordinates before JSON serialization");
        }
        Check(success.LastRequestUri?.AbsoluteUri == "https://api.deepseek.com/chat/completions",
            "DeepSeek request uses the official Chat Completions endpoint");
        using (var payload = System.Text.Json.JsonDocument.Parse(success.LastRequestBody!))
        {
            var root = payload.RootElement;
            Check(root.GetProperty("model").GetString() == "deepseek-flash" &&
                  root.GetProperty("thinking").GetProperty("type").GetString() == "disabled" &&
                  root.GetProperty("response_format").GetProperty("type").GetString() == "json_object" &&
                  root.GetProperty("messages").GetArrayLength() == 2,
                "DeepSeek request uses non-thinking JSON mode with system and whole-page user messages");
        }
        await TestHttpStatusAsync(HttpStatusCode.Unauthorized, AiTranslationErrorKind.Authentication, 1, log, secret);
        await TestHttpStatusAsync(HttpStatusCode.PaymentRequired, AiTranslationErrorKind.Quota, 1, log, secret);
        await TestHttpStatusAsync(HttpStatusCode.TooManyRequests, AiTranslationErrorKind.Quota, 3, log, secret);
        await TestHttpStatusAsync(HttpStatusCode.InternalServerError, AiTranslationErrorKind.Service, 3, log, secret);
        var timeout = new StubHttpHandler((_, _) => throw new TaskCanceledException("simulated timeout"));
        var timeoutClient = new DeepSeekTranslationClient(new HttpClient(timeout), new AiTranslationOptions(), log);
        await ExpectAiErrorAsync(() => timeoutClient.TranslatePageAsync(SingleRequest(), secret, CancellationToken.None),
            AiTranslationErrorKind.Timeout, "HTTP timeout is converted to a user-readable AI timeout error");
        var logText = string.Join("\n", Directory.EnumerateFiles(logDirectory, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Check(!logText.Contains(secret, StringComparison.Ordinal), "API key never enters AI request logs");
        Check(!logText.Contains("Introduction to Computer Vision", StringComparison.Ordinal), "OCR source text never enters AI request logs");
    }

    private static void TestWindowsCredentialStore()
    {
        var target="ScreenTranslator.Tests/"+Guid.NewGuid().ToString("N");
        var store=new WindowsApiKeyStore(target);
        try
        {
            store.Save("sk-unit-test-credential");
            Check(store.HasKey&&store.Read()=="sk-unit-test-credential","API key round-trips through Windows Credential Manager");
            store.Delete();
            Check(!store.HasKey&&store.Read() is null,"API key deletion removes the Windows credential");
        }
        finally{store.Delete();}
    }

    private static async Task TestDeepLClientAsync()
    {
        var logDirectory = Path.Combine(_output, "deepl-http-logs");
        var log = new AppLogger(logDirectory);
        var responseBody = "{\"translations\":[{\"detected_source_language\":\"EN\",\"text\":\"计算机视觉导论\"},{\"detected_source_language\":\"EN\",\"text\":\"计算机视觉使机器理解图像。\"}]}";
        var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") }));
        var client = new DeepLTranslationClient(new HttpClient(handler), new AiTranslationOptions(), log);
        var blocks = new[]
        {
            new AiTranslationBlock("ocr-1", "title", 0, "Introduction to Computer Vision", 0, 0, 400, 50),
            new AiTranslationBlock("ocr-2", "body", 1, "Computer vision enables machines to understand images.", 0, 60, 700, 40)
        };
        var request = new AiPageTranslationRequest("presentation", "en", "zh-CN", AiTranslationStyle.Natural,
            "deepseek-v4-pro", blocks);
        const string secret = "unit-test-deepl-key:fx";
        var result = await client.TranslatePageAsync(request, secret, CancellationToken.None);
        var validated = new AiTranslationResultValidator().Validate(request, result.OutputText);
        Check(handler.LastRequestUri?.AbsoluteUri == "https://api-free.deepl.com/v2/translate",
            "DeepL Free/Developer key uses the official API Free endpoint");
        Check(validated.Count == 2 && validated[0].TranslatedText == "计算机视觉导论",
            "DeepL array response maps back to stable OCR block IDs");
        using (var payload = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!))
        {
            Check(payload.RootElement.GetProperty("text").GetArrayLength() == 2 &&
                  payload.RootElement.GetProperty("source_lang").GetString() == "EN" &&
                  payload.RootElement.GetProperty("target_lang").GetString() == "ZH-HANS",
                "DeepL request batches OCR blocks with explicit English to Simplified Chinese direction");
        }
        var logText = string.Join("\n", Directory.EnumerateFiles(logDirectory, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Check(!logText.Contains(secret, StringComparison.Ordinal) && !logText.Contains(blocks[0].Text, StringComparison.Ordinal),
            "DeepL logs contain neither API keys nor OCR source text");
    }

    private static async Task TestHttpStatusAsync(HttpStatusCode status, AiTranslationErrorKind kind, int calls,
        AppLogger log, string secret)
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        { Content = new StringContent("{\"error\":{\"message\":\"simulated\"}}", Encoding.UTF8, "application/json") }));
        var client = new DeepSeekTranslationClient(new HttpClient(handler), new AiTranslationOptions { MaximumRetries = 2 }, log);
        await ExpectAiErrorAsync(() => client.TranslatePageAsync(SingleRequest(), secret, CancellationToken.None), kind,
            $"HTTP {(int)status} maps to {kind} without uncontrolled retries");
        Check(handler.CallCount == calls, $"HTTP {(int)status} request count is bounded ({calls})");
    }

    private static AiPageTranslationRequest SingleRequest() => new("presentation", "en", "zh-CN",
        AiTranslationStyle.Natural, "deepseek-flash", [new("ocr-1", "title", 0, "Introduction to Computer Vision", 0, 0, 100, 20)]);

    private static void ExpectInvalidTranslation(Action action, string description)
    {
        var rejected = false;
        try { action(); } catch (AiTranslationException ex) when (ex.Kind == AiTranslationErrorKind.InvalidResponse) { rejected = true; }
        Check(rejected, description);
    }

    private static async Task ExpectAiErrorAsync(Func<Task> action, AiTranslationErrorKind kind, string description)
    {
        var matched = false;
        try { await action(); } catch (AiTranslationException ex) when (ex.Kind == kind) { matched = true; }
        Check(matched, description);
    }

    private sealed class MemoryKeyStore(string? key) : IApiKeyStore
    {
        private string? _key = key;
        public bool HasKey => !string.IsNullOrWhiteSpace(_key);
        public string? Read() => _key;
        public void Save(string apiKey) => _key = apiKey;
        public void Delete() => _key = null;
    }

    private sealed class QueueAiClient(params string[] outputs) : IAiTranslationClient
    {
        private readonly Queue<string> _outputs = new(outputs);
        public List<AiPageTranslationRequest> Requests { get; } = [];
        public int CallCount => Requests.Count;
        public Task<AiClientResult> TranslatePageAsync(AiPageTranslationRequest request, string apiKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request);
            return Task.FromResult(new AiClientResult(_outputs.Dequeue(), 10, 10, 1, 200));
        }
    }

    private sealed class CancelingAiClient : IAiTranslationClient
    {
        public async Task<AiClientResult> TranslatePageAsync(AiPageTranslationRequest request, string apiKey, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException(); }
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public string? LastRequestBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await response(request, cancellationToken);
        }
    }
}
