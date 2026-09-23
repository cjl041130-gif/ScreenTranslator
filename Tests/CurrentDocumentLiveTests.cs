using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using ScreenTranslator.Capture;
using ScreenTranslator.Models;
using ScreenTranslator.OCR;
using ScreenTranslator.Services;
using ScreenTranslator.Vision;

namespace ScreenTranslator.Tests;

internal static partial class Program
{
    private static async Task RunCurrentDocumentLiveAsync(MainWindow window)
    {
        window.Hide();
        var log = new AppLogger(Path.Combine(_output, "current-document-logs"));
        var settings = AppSettings.Load(log);
        var monitor = new MonitorService(log).GetMonitors().OrderByDescending(x => x.IsPrimary).First();
        using var tracker = new TargetWindowTracker(settings.Vision);
        tracker.Start();
        ApplicationWindowInfo? target = null;
        var targetWait = Stopwatch.StartNew();
        while (targetWait.Elapsed < TimeSpan.FromSeconds(4))
        {
            target = tracker.SelectTarget(monitor);
            if (target?.Title.Contains("User Interview Transcript", StringComparison.OrdinalIgnoreCase) == true) break;
            await Task.Delay(150);
        }
        if (target is null) throw new InvalidOperationException("没有找到当前打开的浏览器、PDF 或演示窗口。");

        await using var capture = new ScreenCaptureService(log);
        await capture.StartAsync(monitor, 30);
        CapturedFrame? frame = null;
        var frameWait = Stopwatch.StartNew();
        while (frame is null && frameWait.Elapsed < TimeSpan.FromSeconds(6))
        { await Task.Delay(50); frame = capture.TakeLatestFrame(); }
        if (frame is null) throw new TimeoutException("显示器捕获超时。");

        using (frame)
        {
            SaveFrame(frame, Path.Combine(_output, "current-monitor.png"));
            var detector = new PresentationDetector(settings.Vision);
            var detection = detector.Detect(frame, monitor, target);
            File.WriteAllText(Path.Combine(_output, "current-detection.json"), JsonSerializer.Serialize(new
            {
                Target = new { target.ProcessName, target.Title, target.ApplicationType, target.WindowRect, target.ClientRect, target.IsForeground, target.Occlusions },
                detection.SearchRegion,
                Region = detection.Region is null ? null : new { detection.Region.X, detection.Region.Y, detection.Region.Width, detection.Region.Height,
                    detection.Region.Confidence, detection.Region.DetectionMethod, TargetWindowHandle = detection.Region.TargetWindowHandle.ToInt64() },
                detection.Error, detection.LatencyMs
            }, new JsonSerializerOptions { WriteIndented = true }));
            var bounds = detection.Region?.Bounds ?? throw new InvalidOperationException("当前文档没有生成 OCR 区域。");
            var crop = Clamp(bounds, frame.Width, frame.Height);
            var pixels = Crop(frame.PixelData.Span, frame.Stride, crop);
            var cropFrame = new CapturedFrame(crop.Width, crop.Height, "current-crop", ArrayPool<byte>.Shared);
            pixels.CopyTo(cropFrame.Buffer);
            using (cropFrame) SaveFrame(cropFrame, Path.Combine(_output, "current-ocr-input.png"));

            await using var ocr = new AdaptiveOcrEngine(settings.OCR, log);
            await ocr.InitializeAsync(InferenceDevice.Auto, CancellationToken.None);
            var ocrWatch = Stopwatch.StartNew();
            var raw = await ocr.RecognizeAsync(new OcrInput(pixels, crop.Width, crop.Height, crop.Width * 4), CancellationToken.None);
            ocrWatch.Stop();
            var reading = new RuleReadingOrderResolver();
            var ordered = reading.Resolve(new LocalOcrPostProcessor().Process(raw), crop.Width, crop.Height);
            var merged = reading.Resolve(new OcrParagraphMerger().Merge(ordered, crop.Width, crop.Height), crop.Width, crop.Height);

            var bytes = frame.PixelData.ToArray();
            using var image = OpenCvSharp.Mat.FromPixelData(frame.Height, frame.Width, OpenCvSharp.MatType.CV_8UC4, bytes);
            SaveAnnotated(image, crop, raw, merged, Path.Combine(_output, "current-ocr-annotated.png"));
            File.WriteAllText(Path.Combine(_output, "current-result.json"), JsonSerializer.Serialize(new
            {
                target.ProcessName, target.Title, target.ApplicationType,
                Monitor = new { frame.Width, frame.Height }, Crop = new { crop.X, crop.Y, crop.Width, crop.Height, AspectRatio = crop.Width / (double)crop.Height },
                Ocr = new { Milliseconds = ocrWatch.Elapsed.TotalMilliseconds, RawLines = raw.Count, TranslationBlocks = merged.Count,
                    AverageConfidence = raw.Select(x => x.Confidence).DefaultIfEmpty().Average(), Text = merged.Select(x => x.OriginalText) }
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        window.Close();
    }
}
