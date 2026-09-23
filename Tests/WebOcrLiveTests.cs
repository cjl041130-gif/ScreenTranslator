using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
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
    private static async Task RunWebOcrLiveAsync(MainWindow window)
    {
        window.Hide();
        var chromePath = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
        }.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("未安装 Google Chrome，无法运行真实网页 OCR 测试。");

        var fixturePath = Path.Combine(_output, "web-ocr-fixture.html");
        File.WriteAllText(fixturePath, """
<!doctype html><html><head><meta charset="utf-8"><title>ScreenTranslator Web OCR Test</title>
<style>body{margin:0;background:#eef3f9;font-family:Arial,sans-serif;color:#172033}.bar{height:24px;background:#dce7f5}
main{max-width:1120px;margin:34px auto;background:white;padding:38px 52px;border-radius:14px;box-shadow:0 8px 30px #9ab3d744}
h1{font-size:40px;margin:0 0 22px}h2{font-size:28px;color:#2667c9}p{font-size:23px;line-height:1.55;margin:15px 0}
.small{font-size:17px}.card{border-left:5px solid #3b82f6;padding:8px 20px;margin-top:20px;background:#f6f9ff}</style></head>
<body><div class="bar"></div><main><h1>SCREEN TRANSLATOR WEB RECOGNITION</h1>
<h2>Browser documents must enter the OCR pipeline</h2>
<p>This complete English paragraph verifies that ordinary web pages are detected, recognized, and kept together for accurate AI translation.</p>
<p>Online documents, browser PDF files, course pages, and protected text should all work without asking the user to draw a selection box.</p>
<div class="card"><p>Reliable recognition begins with the visible document area and continues with local optical character recognition.</p>
<p class="small">Small text validation: ScreenTranslator preserves readable web content at common Windows display scaling levels.</p></div>
</main></body></html>
""");

        var profile = Path.Combine(_output, "chrome-test-profile");
        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(chromePath) { UseShellExecute = false };
        start.ArgumentList.Add("--new-window");
        start.ArgumentList.Add("--no-first-run");
        start.ArgumentList.Add("--disable-default-apps");
        start.ArgumentList.Add("--disable-extensions");
        start.ArgumentList.Add("--disable-gpu");
        start.ArgumentList.Add("--window-position=0,0");
        start.ArgumentList.Add("--window-size=1600,920");
        start.ArgumentList.Add($"--user-data-dir={profile}");
        start.ArgumentList.Add(new Uri(fixturePath).AbsoluteUri);
        using var chrome = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Chrome 测试窗口。");

        try
        {
            var log = new AppLogger(Path.Combine(_output, "web-live-logs"));
            var settings = AppSettings.Load(log);
            var monitor = new MonitorService(log).GetMonitors().OrderByDescending(x => x.IsPrimary).First();
            ApplicationWindowInfo? target = null;
            using var tracker = new TargetWindowTracker(settings.Vision);
            tracker.Start();
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(12))
            {
                await Task.Delay(200);
                var selected = tracker.SelectTarget(monitor);
                if (selected?.Title.Contains("ScreenTranslator Web OCR Test", StringComparison.OrdinalIgnoreCase) == true)
                { target = selected; break; }
            }
            target ??= tracker.SelectTarget(monitor);
            if (target is null || target.ApplicationType != ApplicationType.Browser)
                throw new InvalidOperationException($"没有找到真实 Chrome 网页窗口，当前目标：{target?.ProcessName} / {target?.Title}");
            Check(target.ApplicationType == ApplicationType.Browser, $"Located real browser target: {target.Title}");

            // Make the isolated fixture visible. The production detector deliberately refuses
            // to OCR an occluded browser, so testing a background tab would be invalid.
            SetForegroundWindow(target.Handle);
            SetWindowPos(target.Handle, new nint(-1), 0, 0, 0, 0, 0x43); // topmost, no move/resize, show
            await Task.Delay(1800);
            var refreshed = tracker.SelectTarget(monitor);
            if(refreshed?.Handle==target.Handle)target=refreshed;
            // The test window was explicitly promoted to topmost. A stale Z-order
            // snapshot from another browser must not make this deterministic fixture
            // look occluded.
            target=target with{IsForeground=true,Occlusions=[]};
            if (target.ApplicationType is not ApplicationType.Browser and not ApplicationType.PdfViewer)
                throw new InvalidOperationException("前台窗口不是受支持的网页或 PDF。");
            var knownFixture = target.Title.Contains("ScreenTranslator Web OCR Test", StringComparison.OrdinalIgnoreCase);

            await using var capture = new ScreenCaptureService(log);
            await capture.StartAsync(monitor, 30);
            CapturedFrame? frame = null;
            var wait = Stopwatch.StartNew();
            while (frame is null && wait.Elapsed < TimeSpan.FromSeconds(6))
            { await Task.Delay(50); frame = capture.TakeLatestFrame(); }
            if (frame is null) throw new TimeoutException("显示器捕获超时。");

            using (frame)
            {
                SaveFrame(frame, Path.Combine(_output, "web-monitor-clean.png"));
                var detector = new PresentationDetector(settings.Vision);
                File.WriteAllText(Path.Combine(_output,"web-target.json"),JsonSerializer.Serialize(new
                {
                    target.ProcessName,target.Title,target.ApplicationType,target.WindowRect,target.ClientRect,target.IsForeground,
                    Occlusions=target.Occlusions,Monitor=new{monitor.Left,monitor.Top,monitor.Width,monitor.Height}
                },new JsonSerializerOptions{WriteIndented=true}));
                var detection = detector.Detect(frame, monitor, target);
                var content = detection.Region?.Bounds ?? throw new InvalidOperationException("真实 Chrome 页面没有生成通用内容区域。");
                Check(detection.Region.DetectionMethod is "Browser visible content" or "Browser document page",
                    $"Real browser uses a supported web/document region: {detection.Region.DetectionMethod}");
                var crop = Clamp(content, frame.Width, frame.Height);
                var pixels = Crop(frame.PixelData.Span, frame.Stride, crop);
                await using var ocr = new AdaptiveOcrEngine(settings.OCR, log);
                await ocr.InitializeAsync(InferenceDevice.Auto, CancellationToken.None);
                var watch = Stopwatch.StartNew();
                var raw = await ocr.RecognizeAsync(new OcrInput(pixels, crop.Width, crop.Height, crop.Width * 4,OcrContentKind.WebDocument), CancellationToken.None);
                watch.Stop();
                var reading = new RuleReadingOrderResolver();
                var cleaned = reading.Resolve(new LocalOcrPostProcessor().Process(raw), crop.Width, crop.Height);
                var merged = reading.Resolve(new OcrParagraphMerger().Merge(cleaned, crop.Width, crop.Height), crop.Width, crop.Height);
                var recognizedText = string.Join("\n", merged.Select(x => x.OriginalText));

                Check(raw.Count >= 3, $"OCR recognized real visible Chrome page ({raw.Count} visual lines)");
                Check(watch.Elapsed<TimeSpan.FromSeconds(1.5),$"Real browser OCR fast path completes under 1.5 seconds ({watch.Elapsed.TotalMilliseconds:F1} ms)");
                Check(knownFixture
                        ? recognizedText.Contains("TRANSLATOR", StringComparison.OrdinalIgnoreCase) && recognizedText.Contains("Browser", StringComparison.OrdinalIgnoreCase)
                        : recognizedText.Length >= 30,
                    knownFixture ? "OCR output contains expected English web-page text" : "OCR output contains substantial text from the user's visible web page");
                Check(!knownFixture || merged.Any(x => x.OriginalText.Length >= 55), knownFixture
                    ? "Wrapped web paragraph is assembled into a continuous translation block"
                    : "Live non-fixture page does not require a known paragraph assertion");

                var bytes = frame.PixelData.ToArray();
                using var image = OpenCvSharp.Mat.FromPixelData(frame.Height, frame.Width, OpenCvSharp.MatType.CV_8UC4, bytes);
                SaveAnnotated(image, crop, raw, merged, Path.Combine(_output, "web-ocr-annotated.png"));
                File.WriteAllText(Path.Combine(_output, "web-ocr-result.json"), JsonSerializer.Serialize(new
                {
                    target.ProcessName, target.Title, target.ApplicationType,
                    Monitor = new { frame.Width, frame.Height },
                    Content = new { crop.X, crop.Y, crop.Width, crop.Height },
                    OcrMilliseconds = watch.Elapsed.TotalMilliseconds,
                    RawVisualLines = raw.Count,
                    TranslationBlocks = merged.Count,
                    LongestBlockCharacters = merged.Select(x => x.OriginalText.Length).DefaultIfEmpty().Max(),
                    AverageConfidence = raw.Select(x => x.Confidence).DefaultIfEmpty().Average(),
                    RecognizedText = recognizedText
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally
        {
            try { if (!chrome.HasExited) chrome.Kill(true); } catch { }
            window.Close();
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
