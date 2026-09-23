using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;
using ScreenTranslator.Capture;
using ScreenTranslator.Models;
using ScreenTranslator.OCR;
using ScreenTranslator.Services;
using ScreenTranslator.Vision;
using WRect = System.Windows.Rect;

namespace ScreenTranslator.Tests;

internal static partial class Program
{
    private static async Task RunDocumentPageFixtureAsync(MainWindow window)
    {
        window.Hide();
        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "test-results", "user-interview-transcript-current-2", "current-monitor.png"));
        if (!File.Exists(fixture)) throw new FileNotFoundException("缺少本机文档截图测试夹具。", fixture);
        using var source = Cv2.ImRead(fixture, ImreadModes.Color);
        using var bgra = new Mat();
        Cv2.CvtColor(source, bgra, ColorConversionCodes.BGR2BGRA);
        var target = new ApplicationWindowInfo(1, 1, "msedge", "User Interview Transcripts.docx - Microsoft Edge", "Chrome_WidgetWin_1",
            new WRect(0, 0, 2560, 1438), new WRect(0, 0, 2560, 1438), new WRect(0, 0, 2560, 1438),
            1.5, "fixture", true, true, false, ApplicationType.Browser, 1, false);
        var content = GeneralDocumentRegion.GetContentBounds(target, target.ClientRect);
        var page = DocumentPageDetector.Detect(bgra, content, out var confidence);
        Check(!page.IsEmpty, "Actual portrait online document page is detected");
        Check(page.Width / page.Height < 1.25, $"Actual document crop is portrait/near-square, ratio={page.Width / page.Height:F3}");
        Check(page.X > 500 && page.Right < 2000, $"Actual document crop excludes browser side workspace: {page}");

        var crop = Clamp(page, bgra.Width, bgra.Height);
        var pixels = new byte[crop.Width * crop.Height * 4];
        using (var view = new Mat(bgra, new OpenCvSharp.Rect(crop.X, crop.Y, crop.Width, crop.Height)))
        using (var contiguous = view.Clone())
            Marshal.Copy(contiguous.Data, pixels, 0, pixels.Length);
        using (var cropFrame = new CapturedFrame(crop.Width, crop.Height, "fixture", ArrayPool<byte>.Shared))
        {
            pixels.CopyTo(cropFrame.Buffer, 0);
            SaveFrame(cropFrame, Path.Combine(_output, "portrait-document-crop.png"));
        }

        var log = new AppLogger(Path.Combine(_output, "logs"));
        var settings = AppSettings.Load(log);
        await using var ocr = new AdaptiveOcrEngine(settings.OCR, log);
        await ocr.InitializeAsync(InferenceDevice.Auto, CancellationToken.None);
        var watch = Stopwatch.StartNew();
            var raw = await ocr.RecognizeAsync(new OcrInput(pixels, crop.Width, crop.Height, crop.Width * 4,OcrContentKind.WebDocument), CancellationToken.None);
        watch.Stop();
        var reading = new RuleReadingOrderResolver();
        var merged = reading.Resolve(new OcrParagraphMerger().Merge(
            reading.Resolve(new LocalOcrPostProcessor().Process(raw), crop.Width, crop.Height), crop.Width, crop.Height), crop.Width, crop.Height);
        Check(merged.Any(x => x.OriginalText.Contains("Interview", StringComparison.OrdinalIgnoreCase)), "Actual document English text is recognized");
        var completeText=string.Join(" ",merged.Select(x=>x.OriginalText));
        Check(watch.Elapsed<TimeSpan.FromSeconds(1.5),$"Actual web document OCR completes under 1.5 seconds ({watch.Elapsed.TotalMilliseconds:F1} ms)");
        Check(merged.First().OriginalText.StartsWith("Date:",StringComparison.OrdinalIgnoreCase),"Scrolled web document keeps strict top-to-bottom reading order");
        Check(completeText.Contains("While cleaning up last week",StringComparison.OrdinalIgnoreCase),"Wrapped web sentence remains connected across visual lines");
        Check(merged.Count<=12,$"Web paragraph merger sends bounded contextual blocks to AI ({merged.Count})");
        await using var windowsOcr = new WindowsOcrEngine();
        await windowsOcr.InitializeAsync(InferenceDevice.Cpu, CancellationToken.None);
        var windowsWatch = Stopwatch.StartNew();
        var windowsRaw = await windowsOcr.RecognizeAsync(new OcrInput(pixels, crop.Width, crop.Height, crop.Width * 4), CancellationToken.None);
        windowsWatch.Stop();
        var windowsMerged = reading.Resolve(new OcrParagraphMerger().Merge(
            reading.Resolve(new LocalOcrPostProcessor().Process(windowsRaw), crop.Width, crop.Height), crop.Width, crop.Height), crop.Width, crop.Height);
        File.WriteAllText(Path.Combine(_output, "portrait-document-result.json"), JsonSerializer.Serialize(new
        {
            Source = fixture, Content = content, Page = page, Confidence = confidence,
            AspectRatio = page.Width / page.Height, OcrMilliseconds = watch.Elapsed.TotalMilliseconds,
            RawLines = raw.Count, TranslationBlocks = merged.Count,
            AverageConfidence = raw.Select(x => x.Confidence).DefaultIfEmpty().Average(),
            Text = merged.Select(x => x.OriginalText),
            WindowsOcr = new { Milliseconds = windowsWatch.Elapsed.TotalMilliseconds, RawLines = windowsRaw.Count,
                TranslationBlocks = windowsMerged.Count, Text = windowsMerged.Select(x => x.OriginalText) }
        }, new JsonSerializerOptions { WriteIndented = true }));
        window.Close();
    }
}
