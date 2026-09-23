using System.Buffers;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenTranslator.Capture;
using ScreenTranslator.Models;
using ScreenTranslator.OCR;
using ScreenTranslator.Overlay;
using ScreenTranslator.Recognition;
using ScreenTranslator.Services;
using ScreenTranslator.Translation;
using ScreenTranslator.Vision;

namespace ScreenTranslator.Tests;

internal static partial class Program
{
    private static async Task RunPhase3UnitsAsync(MainWindow window)
    {
        window.Hide();
        await RunAiTranslationTestsAsync();
        await TestPaddleOcrSmokeAsync();
        await TestRealWindowsOcrAsync();
        await TestOfflineTranslationAsync();
        TestReadingOrderAndPostProcessing();
        TestOverlayTypography();
        TestSlideChangeAndCache();
        await TestSingleFinalFrameTriggersRecognitionAsync();
        await TestPageChangeCancelsRecognitionAsync();
        await TestStopCancelsRecognitionAsync();
        File.WriteAllText(Path.Combine(_output, "phase3-summary.txt"), string.Join(Environment.NewLine, Checks));
    }

    private static async Task TestRealWindowsOcrAsync()
    {
        const int width = 1200, height = 240;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var text = new FormattedText("Artificial Intelligence", CultureInfo.GetCultureInfo("en-US"),
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 72, Brushes.Black, 1);
            drawing.DrawText(text, new Point(55, 62));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        await using var engine = new WindowsOcrEngine();
        await engine.InitializeAsync(InferenceDevice.Cpu, CancellationToken.None);
        var result = await engine.RecognizeAsync(new(pixels, width, height, width * 4), CancellationToken.None);
        var recognized = string.Join(" ", result.Select(x => x.OriginalText));
        Check(recognized.Contains("Artificial Intelligence", StringComparison.OrdinalIgnoreCase), $"Real Windows OCR reads acceptance phrase: {recognized}");
        Check(result.All(x => x.BoundingBox.Width > 0 && x.BoundingBox.Height > 0), "OCR returns positioned text blocks");
    }

    private static async Task TestOfflineTranslationAsync()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();
        var privateBefore = process.PrivateMemorySize64 / 1048576d;
        var initializeWatch = System.Diagnostics.Stopwatch.StartNew();
        await using var engine = new MarianOnnxTranslationEngine();
        await engine.InitializeAsync(InferenceDevice.Cpu, CancellationToken.None);
        initializeWatch.Stop();
        process.Refresh();
        var privateAfterInitialize = process.PrivateMemorySize64 / 1048576d;
        var context = new TranslationContext("", [], new Dictionary<string, string>(), "");
        var result = await engine.TranslateAsync(new("en", "zh-CN", "Artificial Intelligence", context, OcrBlockType.Title), CancellationToken.None);
        Check(result.Text == "人工智能" && !result.IsPartial, "Acceptance translation: Artificial Intelligence → 人工智能");
        var samples = new[]
        {
            "The meeting starts at nine o'clock tomorrow morning.",
            "Please review the quarterly sales report before Friday.",
            "Our new system improves accuracy and reduces processing time.",
            "Students can access the course materials from any device.",
            "This chart shows the growth of renewable energy in Asia.",
            "Data privacy is essential for every organization.",
            "The project team completed all major tasks on schedule.",
            "Artificial intelligence helps people understand complex information quickly.",
            "Customer feedback helps us improve product quality.",
            "Turn off the computer after the presentation has finished."
        };
        var report = new StringBuilder();
        var timings = new List<double>();
        foreach (var sample in samples)
        {
            var translated = await engine.TranslateAsync(new("en", "zh-CN", sample, context, OcrBlockType.Body), CancellationToken.None);
            timings.Add(translated.ProcessingMilliseconds);
            report.AppendLine($"EN: {sample}").AppendLine($"ZH: {translated.Text}").AppendLine($"MS: {translated.ProcessingMilliseconds:F1}").AppendLine();
            Check(translated.Text != sample && translated.Text.Any(c => c is >= '\u3400' and <= '\u9fff') && !translated.IsPartial,
                $"Neural sentence translation produces complete Chinese: {sample}");
        }
        File.WriteAllText(Path.Combine(_output, "translation-acceptance.txt"), report.ToString());
        File.WriteAllText(Path.Combine(_output, "translation-engine-performance.txt"),
            FormattableString.Invariant($"ColdInitializationMs={initializeWatch.Elapsed.TotalMilliseconds:F1}\nPrivateMBBefore={privateBefore:F1}\nPrivateMBAfterInitialize={privateAfterInitialize:F1}\nMeanSentenceMs={timings.Average():F1}\nMinimumSentenceMs={timings.Min():F1}\nMaximumSentenceMs={timings.Max():F1}\n"));
        await TestRenderedSlideTranslationAsync(engine, context);
        var cached = await engine.TranslateAsync(new("en", "zh-CN", samples[0], context, OcrBlockType.Body), CancellationToken.None);
        Check(cached.ProcessingMilliseconds == 0 && engine.CacheHits > 0, "Repeated text uses bounded in-memory translation cache");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await ExpectCanceled(() => engine.TranslateAsync(new("en", "zh-CN", "A fresh sentence must observe cancellation.", context, OcrBlockType.Body), canceled.Token));
        Check(engine.LanguagePack is { Installed: true } pack && pack.ModelType.Contains("MarianMT", StringComparison.Ordinal), "Bundled neural ONNX language pack metadata validates");

        var missingRoot = Path.Combine(_output, "missing-translation-pack");
        Directory.CreateDirectory(missingRoot);
        await using var missing = new MarianOnnxTranslationEngine(new LocalLanguagePackService(missingRoot));
        var missingRejected = false;
        try { await missing.InitializeAsync(InferenceDevice.Cpu, CancellationToken.None); }
        catch (FileNotFoundException) { missingRejected = true; }
        Check(missingRejected, "Missing neural language pack fails with a clear initialization error");

        var corruptRoot = Path.Combine(_output, "corrupt-translation-pack");
        var corruptPack = Path.Combine(corruptRoot, "en-zh-neural");
        Directory.CreateDirectory(corruptPack);
        File.WriteAllText(Path.Combine(corruptPack, "metadata.json"), "{\"id\":\"en-zh-neural\",\"sourceLanguage\":\"en\",\"targetLanguage\":\"zh-CN\",\"displayName\":\"Test\",\"version\":\"1\",\"quality\":\"Test\",\"runtime\":\"ONNX\",\"modelType\":\"MarianMT\"}");
        File.WriteAllText(Path.Combine(corruptPack, "config.json"), "{}");
        File.WriteAllText(Path.Combine(corruptPack, "manifest.json"), "{\"files\":[{\"path\":\"config.json\",\"sha256\":\"0000000000000000000000000000000000000000000000000000000000000000\",\"sizeBytes\":2}]}");
        await using var corrupt = new MarianOnnxTranslationEngine(new LocalLanguagePackService(corruptRoot));
        var corruptionRejected = false;
        try { await corrupt.InitializeAsync(InferenceDevice.Cpu, CancellationToken.None); }
        catch (InvalidDataException) { corruptionRejected = true; }
        Check(corruptionRejected, "Corrupted neural language pack is rejected by SHA256 validation");
    }

    private static async Task TestRenderedSlideTranslationAsync(MarianOnnxTranslationEngine translation, TranslationContext context)
    {
        const int width = 1400, height = 620;
        var lines = new[]
        {
            "Building Reliable Offline Software",
            "The system processes every image on this computer.",
            "Clear information helps teams make better decisions."
        };
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (var i = 0; i < lines.Length; i++)
            {
                var text = new FormattedText(lines[i], CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), i == 0 ? 58 : 43, Brushes.Black, 1);
                drawing.DrawText(text, new Point(55, 65 + i * 155));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        await using var ocr = new WindowsOcrEngine();
        await ocr.InitializeAsync(InferenceDevice.Cpu, CancellationToken.None);
        var blocks = await ocr.RecognizeAsync(new(pixels, width, height, width * 4), CancellationToken.None);
        var translated = new List<string>();
        foreach (var block in blocks.Where(x => x.OriginalText.Any(char.IsLetter)))
        {
            var response = await translation.TranslateAsync(new("en", "zh-CN", block.OriginalText, context, block.BlockType), CancellationToken.None);
            translated.Add($"{block.OriginalText} => {response.Text}");
            Check(response.Text.Any(c => c is >= '\u3400' and <= '\u9fff'), $"Rendered slide OCR block receives complete neural translation: {block.OriginalText}");
        }
        Check(translated.Count >= 3, $"Rendered slide OCR finds all three English text lines ({translated.Count} blocks)");
        File.WriteAllLines(Path.Combine(_output, "rendered-slide-ocr-translation.txt"), translated);
    }

    private static async Task ExpectCanceled(Func<Task<TranslationResponse>> action)
    {
        var canceled = false;
        try { await action(); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "Neural translation observes cancellation");
    }

    private static void TestReadingOrderAndPostProcessing()
    {
        var blocks = new[]
        {
            Block("right-2", 650, 330), Block("title", 80, 30, 1040, OcrBlockType.Title),
            Block("left-2", 80, 330), Block("right-1", 650, 180), Block("left-1", 80, 180)
        };
        var ordered = new RuleReadingOrderResolver().Resolve(blocks, 1200, 700).Select(x => x.OriginalText).ToArray();
        Check(ordered.SequenceEqual(new[] { "title", "left-1", "left-2", "right-1", "right-2" }), "Title and two-column reading order");
        var cleaned = new LocalOcrPostProcessor().Clean("Artificia1   lntelligence ,  Machine-\nLearning");
        Check(cleaned == "Artificial Intelligence, Machine Learning", "Local OCR correction and dehyphenation");

        var merger = new OcrParagraphMerger();
        var wrapped = new[]
        {
            BlockAt("This is a long sentence that continues", 90, 180, 720, 32, OcrBlockType.Body),
            BlockAt("onto the next visual line without losing", 92, 220, 690, 32, OcrBlockType.Body),
            BlockAt("its meaning in the translated paragraph.", 91, 260, 650, 32, OcrBlockType.Body)
        };
        var merged = merger.Merge(wrapped, 1200, 700);
        Check(merged.Count == 1 && merged[0].OriginalText.Contains("continues onto") && merged[0].OriginalText.EndsWith("paragraph."),
            "Wrapped OCR lines merge into one complete sentence before translation");
        Check(merged[0].VisualLineCount == 3 && Math.Abs(merged[0].EffectiveLineHeight - 32) < .01,
            "Paragraph merge preserves original per-line typography metadata");

        var columns = new[]
        {
            BlockAt("Left column line", 80, 180, 420, 32, OcrBlockType.Body),
            BlockAt("Left continuation", 82, 220, 390, 32, OcrBlockType.Body),
            BlockAt("Right column line", 680, 180, 400, 32, OcrBlockType.Body),
            BlockAt("Right continuation", 682, 220, 390, 32, OcrBlockType.Body)
        };
        var columnOrder = new RuleReadingOrderResolver().Resolve(columns, 1200, 700);
        var mergedColumns = merger.Merge(columnOrder, 1200, 700);
        Check(mergedColumns.Count == 2 && mergedColumns[0].OriginalText.Contains("Left continuation") && mergedColumns[1].OriginalText.Contains("Right continuation"),
            "Paragraph merge preserves two-column boundaries");

        var bullets = new[]
        {
            BlockAt("• First item begins here", 90, 180, 650, 32, OcrBlockType.Bullet),
            BlockAt("and continues on the next line", 125, 220, 570, 32, OcrBlockType.Body),
            BlockAt("• Second item stays separate", 90, 265, 640, 32, OcrBlockType.Bullet)
        };
        var mergedBullets = merger.Merge(bullets, 1200, 700);
        Check(mergedBullets.Count == 2 && mergedBullets[0].OriginalText.Contains("continues") && mergedBullets[1].OriginalText.StartsWith("• Second"),
            "Bullet continuation merges while the next bullet stays separate");

        var separated = new[]
        {
            BlockAt("First paragraph ends here.", 90, 180, 620, 32, OcrBlockType.Body),
            BlockAt("Second paragraph begins here.", 90, 240, 620, 32, OcrBlockType.Body)
        };
        Check(merger.Merge(separated, 1200, 700).Count == 2, "Paragraph spacing and sentence boundary prevent unrelated text merging");

        var misclassifiedTopBody = new[]
        {
            BlockAt("Tokenization is the process of segmenting a chunk of continuous text", 120, 145, 890, 50, OcrBlockType.Title),
            BlockAt("into separate entities called tokens.", 155, 200, 650, 44, OcrBlockType.Body)
        };
        Check(merger.Merge(misclassifiedTopBody, 1200, 700).Count == 1,
            "Long top-of-slide body text merges even when geometry misclassifies its first line as a title");

        var webLines = new[]
        {
            BlockAt("A wide browser paragraph continues across most of the visible document", 90, 180, 930, 32, OcrBlockType.Body),
            BlockAt("and finishes on a shorter wrapped line.", 90, 220, 480, 32, OcrBlockType.Body),
            BlockAt("The next web paragraph begins lower on the page.", 90, 285, 720, 32, OcrBlockType.Body)
        };
        var orderedWeb = new RuleReadingOrderResolver().Resolve(webLines, 1200, 700);
        var mergedWeb = merger.Merge(orderedWeb, 1200, 700);
        Check(mergedWeb.Count == 2 && mergedWeb[0].OriginalText.Contains("document and finishes"),
            $"Wide browser lines keep vertical order and merge with shorter sentence continuations: {string.Join(" | ", mergedWeb.Select(x=>x.OriginalText))}");

        var ocrSettings = new OcrSettings(); ocrSettings.Normalize();
        Check(ocrSettings.TileActivationLongSide == 3000, "2K presentation pages use one PaddleOCR detector pass; 4K pages retain tiling");
    }

    private static OcrBlock BlockAt(string text,double x,double y,double width,double height,OcrBlockType type) =>
        new(text,text,.95,new Rect(x,y,width,height),0,0,type){ConfidenceSource=OcrConfidenceSource.Model};

    private static void TestOverlayTypography()
    {
        static OcrBlock TypographyBlock(string id, double lineHeight, OcrBlockType type, double totalHeight = 0, int lines = 1) =>
            new(id, id, .95, new Rect(0, 0, 600, totalHeight > 0 ? totalHeight : lineHeight), 0, 0, type)
            { SourceLineHeight = lineHeight, VisualLineCount = lines };

        const double dpi = 1.5;
        var title = TranslationOverlayWindow.CalculateTranslatedFontSize(TypographyBlock("title", 66, OcrBlockType.Title), dpi, 1);
        var body = TranslationOverlayWindow.CalculateTranslatedFontSize(TypographyBlock("body", 36, OcrBlockType.Body), dpi, 1);
        var caption = TranslationOverlayWindow.CalculateTranslatedFontSize(TypographyBlock("caption", 20, OcrBlockType.Caption), dpi, 1);
        Check(title > body && body > caption,
            $"Overlay follows source text hierarchy after DPI conversion (title={title:F1}, body={body:F1}, caption={caption:F1})");

        var mergedParagraph = TypographyBlock("merged", 36, OcrBlockType.Body, 240, 6);
        var mergedSize = TranslationOverlayWindow.CalculateTranslatedFontSize(mergedParagraph, dpi, 1);
        Check(Math.Abs(mergedSize - body) < .01,
            $"Merged multi-line paragraph keeps body font size instead of using total block height ({mergedSize:F1})");

        var scaled = TranslationOverlayWindow.CalculateTranslatedFontSize(TypographyBlock("scaled", 36, OcrBlockType.Body), 1, 1.25);
        Check(scaled > body && scaled <= 46,
            $"User font scale remains effective within readable overlay bounds ({scaled:F1})");
    }

    private static void TestSlideChangeAndCache()
    {
        var a = new SlideFingerprint(new string('A', 64), Enumerable.Repeat((byte)40, 144).ToArray());
        var b = new SlideFingerprint(new string('B', 64), Enumerable.Repeat((byte)220, 144).ToArray());
        var detector = new SlideChangeDetector(TimeSpan.FromMilliseconds(300)); var now = DateTimeOffset.UtcNow;
        Check(detector.Observe(a, now) is null && detector.Observe(a, now.AddMilliseconds(150)) is null && detector.Observe(a, now.AddMilliseconds(360)) is not null, "Initial slide waits for stability before OCR");
        Check(detector.Observe(a, now.AddSeconds(1)) is null, "Static slide does not repeat OCR");
        detector.Observe(b, now.AddSeconds(1.1));
        detector.Observe(a, now.AddSeconds(1.2));
        Check(detector.ConfirmPending(now.AddSeconds(2)) is null, "Transient caret/hover change is discarded when the current page returns");
        Check(detector.Observe(b, now.AddSeconds(2)) is null && detector.Observe(b, now.AddSeconds(2.4)) is not null, "Meaningful slide change triggers after debounce");
        var cache = new SlideRecognitionCache(8); var presentation = new PresentationRegionInfo(0, 0, 1200, 700, 1, "test");
        var slide = new SlideRecognitionResult(a.Key, presentation, [], false, 20, 1, 21, now); cache.Put(a, slide);
        Check(cache.TryGet(a, out var hit) && hit.Fingerprint == a.Key, "Returning to a page restores cached OCR/translation");
    }

    private static async Task TestSingleFinalFrameTriggersRecognitionAsync()
    {
        var ocr=new ImmediateOcrEngine();var overlay=new TrackingOverlay();
        var log=new AppLogger(Path.Combine(_output,"single-frame-recognition-logs"));
        await using var pipeline=new RecognitionPipeline(log,ocr,new FakeTranslationEngine(),overlay:overlay,
            changes:new SlideChangeDetector(TimeSpan.FromMilliseconds(45)));
        await pipeline.InitializeAsync(CancellationToken.None);pipeline.Start();
        var monitor=new MonitorInfo("test","test",256,144,0,0,1,true,0,1);
        var region=new PresentationRegion(0,0,256,144,.95,"test",1,monitor.Id,DateTimeOffset.UtcNow);
        var detection=new DetectionSnapshot(null,new Rect(0,0,256,144),[],region,1,DateTimeOffset.UtcNow);
        using(var onlyFrame=new CapturedFrame(256,144,monitor.Id,ArrayPool<byte>.Shared))
        {Array.Fill(onlyFrame.Buffer,(byte)235);pipeline.Submit(onlyFrame,monitor,detection);}
        await Until(()=>pipeline.Latest is not null,"One final WGC frame is confirmed after debounce without mouse movement");
        Check(ocr.Runs==1&&overlay.IsVisible,"Single stable final frame runs OCR and publishes translation exactly once");
        await pipeline.StopAsync();
    }

    private static async Task TestStopCancelsRecognitionAsync()
    {
        var ocr = new BlockingOcrEngine();
        var translation = new FakeTranslationEngine();
        var overlay = new TrackingOverlay();
        var log = new AppLogger(Path.Combine(_output, "recognition-pipeline-logs"));
        await using var pipeline = new RecognitionPipeline(log, ocr, translation, overlay: overlay,
            changes: new SlideChangeDetector(TimeSpan.FromMilliseconds(40)));
        await pipeline.InitializeAsync(CancellationToken.None);
        pipeline.Start();
        var monitor = new MonitorInfo("test", "test", 256, 144, 0, 0, 1, true, 0, 1);
        var region = new PresentationRegion(0, 0, 256, 144, .95, "test", 1, monitor.Id, DateTimeOffset.UtcNow);
        var detection = new DetectionSnapshot(null, new Rect(0, 0, 256, 144), [], region, 1, DateTimeOffset.UtcNow);
        using (var firstFrame = new CapturedFrame(256, 144, monitor.Id, ArrayPool<byte>.Shared))
        {
            Array.Fill(firstFrame.Buffer, (byte)245);
            pipeline.Submit(firstFrame, monitor, detection);
        }
        await Task.Delay(150);
        using (var stableFrame = new CapturedFrame(256, 144, monitor.Id, ArrayPool<byte>.Shared))
        {
            Array.Fill(stableFrame.Buffer, (byte)245);
            pipeline.Submit(stableFrame, monitor, detection);
        }
        await Until(() => ocr.Entered, "Recognition worker starts after slide stability debounce");
        await pipeline.StopAsync();
        Check(ocr.CancellationObserved && pipeline.State == RecognitionRuntimeState.Idle && pipeline.Latest is null,
            "Stop cancels active OCR and discards late recognition output");
        Check(!overlay.IsVisible && overlay.HideCalls > 0, "Stop hides translation overlay");
    }

    private static async Task TestPageChangeCancelsRecognitionAsync()
    {
        var ocr = new BlockingOcrEngine();
        var overlay = new TrackingOverlay();
        var log = new AppLogger(Path.Combine(_output, "page-change-cancel-logs"));
        await using var pipeline = new RecognitionPipeline(log, ocr, new FakeTranslationEngine(), overlay: overlay,
            changes: new SlideChangeDetector(TimeSpan.FromMilliseconds(40)));
        await pipeline.InitializeAsync(CancellationToken.None); pipeline.Start();
        var monitor = new MonitorInfo("test", "test", 256, 144, 0, 0, 1, true, 0, 1);
        var region = new PresentationRegion(0, 0, 256, 144, .95, "test", 1, monitor.Id, DateTimeOffset.UtcNow);
        var detection = new DetectionSnapshot(null, new Rect(0, 0, 256, 144), [], region, 1, DateTimeOffset.UtcNow);
        using (var first = new CapturedFrame(256,144,monitor.Id,ArrayPool<byte>.Shared))
        { Array.Fill(first.Buffer,(byte)245);pipeline.Submit(first,monitor,detection); }
        await Task.Delay(150);
        using (var stable = new CapturedFrame(256,144,monitor.Id,ArrayPool<byte>.Shared))
        { Array.Fill(stable.Buffer,(byte)245);pipeline.Submit(stable,monitor,detection); }
        await Until(()=>ocr.Entered,"Recognition worker starts before page-change cancellation");
        await Task.Delay(150);
        using (var changed = new CapturedFrame(256,144,monitor.Id,ArrayPool<byte>.Shared))
        { Array.Fill(changed.Buffer,(byte)20);pipeline.Submit(changed,monitor,detection); }
        await Until(()=>ocr.CancellationObserved,"Page change immediately cancels active OCR/AI work");
        Check(pipeline.Latest is null && !overlay.IsVisible,"Canceled old page cannot publish or retain a stale overlay");
        await pipeline.StopAsync();
    }

    private static OcrBlock Block(string text, double x, double y, double width = 300, OcrBlockType type = OcrBlockType.Body) =>
        new(text, text, .92, new Rect(x, y, width, 42), 0, 0, type);

    private sealed class BlockingOcrEngine : IOcrEngine
    {
        public string Name => "Blocking test OCR";
        public bool IsInitialized { get; private set; }
        public volatile bool Entered;
        public volatile bool CancellationObserved;
        public Task InitializeAsync(InferenceDevice preferredDevice, CancellationToken cancellationToken)
        { IsInitialized = true; return Task.CompletedTask; }
        public async Task<IReadOnlyList<OcrBlock>> RecognizeAsync(OcrInput input, CancellationToken cancellationToken)
        {
            Entered = true;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { CancellationObserved = true; throw; }
            return [];
        }
        public ValueTask DisposeAsync() { IsInitialized = false; return ValueTask.CompletedTask; }
    }

    private sealed class ImmediateOcrEngine : IOcrEngine
    {
        public string Name=>"Immediate test OCR";public bool IsInitialized{get;private set;}public int Runs;
        public Task InitializeAsync(InferenceDevice preferredDevice,CancellationToken cancellationToken){IsInitialized=true;return Task.CompletedTask;}
        public Task<IReadOnlyList<OcrBlock>> RecognizeAsync(OcrInput input,CancellationToken cancellationToken)
        {Interlocked.Increment(ref Runs);return Task.FromResult<IReadOnlyList<OcrBlock>>([Block("A complete English sentence for testing.",20,20,200)]);}
        public ValueTask DisposeAsync(){IsInitialized=false;return ValueTask.CompletedTask;}
    }

    private sealed class FakeTranslationEngine : ITranslationEngine
    {
        public string Name => "Test translation";
        public bool IsInitialized { get; private set; }
        public LanguagePackMetadata? LanguagePack => new("test", "en", "zh-CN", "Test", "1", "Fast", "CPU", "Test", 1, _output, true);
        public Task InitializeAsync(InferenceDevice preferredDevice, CancellationToken cancellationToken)
        { IsInitialized = true; return Task.CompletedTask; }
        public Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new TranslationResponse(request.Text, false, 0));
        public ValueTask DisposeAsync() { IsInitialized = false; return ValueTask.CompletedTask; }
    }

    private sealed class TrackingOverlay : ITranslationOverlay
    {
        public bool IsVisible { get; private set; }
        public int HideCalls { get; private set; }
        public Task ShowAsync(SlideRecognitionResult result, MonitorInfo monitor, TranslationDisplayMode mode,
            double backgroundOpacity, double fontScale, bool showOriginalText, CancellationToken cancellationToken)
        { IsVisible = true; return Task.CompletedTask; }
        public Task UpdateRegionAsync(PresentationRegionInfo region, MonitorInfo monitor, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task HideAsync(CancellationToken cancellationToken = default)
        { IsVisible = false; HideCalls++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { IsVisible = false; return ValueTask.CompletedTask; }
    }
}
