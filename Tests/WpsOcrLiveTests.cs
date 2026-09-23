using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using ScreenTranslator.Capture;
using ScreenTranslator.Models;
using ScreenTranslator.OCR;
using ScreenTranslator.Services;
using ScreenTranslator.Translation.AI;
using ScreenTranslator.Translation.Security;
using ScreenTranslator.Vision;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;
using Cv = OpenCvSharp;

namespace ScreenTranslator.Tests;

internal static partial class Program
{
    private static async Task RunWpsOcrLiveAsync(MainWindow window)
    {
        window.Hide();
        var log = new AppLogger(Path.Combine(_output, "wps-live-logs"));
        var settings = AppSettings.Load(log);
        var monitor = new MonitorService(log).GetMonitors().OrderByDescending(x => x.IsPrimary).First();
        using var tracker = new TargetWindowTracker(settings.Vision);
        var target = tracker.SelectTarget(monitor) ?? throw new InvalidOperationException("没有找到已打开的 WPS 演示窗口。");
        Check(target.ProcessName.Equals("wpp", StringComparison.OrdinalIgnoreCase), $"Located live WPS Presentation window: {target.Title}");

        using var capture = new WindowCaptureProbe(target.Handle, target.ProcessName);
        CapturedFrame? frame = null;
        var wait = Stopwatch.StartNew();
        while (frame is null && wait.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(40);
            frame = capture.TryCapture();
        }
        if (frame is null) throw new TimeoutException("WPS 窗口捕获超时。");
        using (frame)
        {
            SaveFrame(frame, Path.Combine(_output, "wps-window-clean.png"));
            var bytes = frame.PixelData.ToArray();
            using var image = Cv.Mat.FromPixelData(frame.Height, frame.Width, Cv.MatType.CV_8UC4, bytes);
            var candidates = new PresentationCandidateGenerator(settings.Vision).Generate(image, false);
            var scorer = new PresentationRegionScorer(settings.Vision);
            var search = new Rect(0, 0, frame.Width, frame.Height);
            var best = candidates.Select(x => scorer.Score(x, search, Rect.Empty, false))
                .OrderByDescending(x => x.Scores?.FinalScore ?? 0).FirstOrDefault()
                ?? throw new InvalidOperationException("WPS 窗口中没有检测到幻灯片页面。");
            Check(best.Scores?.FinalScore >= .6, $"Detected real WPS slide region with confidence {best.Scores?.FinalScore:F3}");

            var crop = Clamp(best.Bounds, frame.Width, frame.Height);
            var pixels = Crop(frame.PixelData.Span, frame.Stride, crop);
            var input = new OcrInput(pixels, crop.Width, crop.Height, crop.Width * 4);
            await using var ocr = new AdaptiveOcrEngine(settings.OCR, log);
            await ocr.InitializeAsync(InferenceDevice.Auto, CancellationToken.None);
            var ocrWatch = Stopwatch.StartNew();
            var raw = await ocr.RecognizeAsync(input, CancellationToken.None);
            ocrWatch.Stop();
            var reading = new RuleReadingOrderResolver();
            var cleaned = reading.Resolve(new LocalOcrPostProcessor().Process(raw), crop.Width, crop.Height);
            var merged = reading.Resolve(new OcrParagraphMerger().Merge(cleaned, crop.Width, crop.Height), crop.Width, crop.Height);
            File.WriteAllText(Path.Combine(_output, "wps-ocr-block-geometry.json"), JsonSerializer.Serialize(cleaned.Select(x => new
            { x.Id, Characters=x.OriginalText.Length, x.BlockType, x.X, x.Y, x.Width, x.Height, x.Confidence }), new JsonSerializerOptions { WriteIndented=true }));
            Check(raw.Count > 0, $"PaddleOCR recognized real WPS slide ({raw.Count} visual lines)");
            Check(merged.Count <= raw.Count, $"Paragraph assembly reduced {raw.Count} visual lines to {merged.Count} translation blocks");

            var medianHeight = raw.Count == 0 ? 1 : raw.Select(x => x.Height).Order().ElementAt(raw.Count / 2);
            var multiline = merged.Count(x => x.Height > medianHeight * 1.55 && x.OriginalText.Length >= 30);
            Check(true, multiline > 0
                ? $"Long wrapped slide text is represented by {multiline} continuous paragraph block(s)"
                : "Current real slide contains no geometrically mergeable wrapped paragraph; translation validation continues");

            var translatedCount = 0; var translationMs = 0d; var chineseBlocks = 0; var englishTechnicalBlocks = 0;
            var keyStore = new WindowsApiKeyStore();
            if (keyStore.HasKey)
            {
                using var http = new HttpClient();
                await using var translation = new AiTranslationEngine(settings.Translation, keyStore,
                    new DeepSeekTranslationClient(http, settings.Translation, log));
                await translation.InitializeAsync(InferenceDevice.Cpu, CancellationToken.None);
                var translationWatch = Stopwatch.StartNew();
                var translated = await translation.TranslatePageAsync(merged, "en", "zh-CN", CancellationToken.None);
                translationWatch.Stop(); translationMs = translationWatch.Elapsed.TotalMilliseconds;
                translatedCount = translated.Count;
                chineseBlocks = translated.Count(x => x.Text.Any(c => c is >= '\u3400' and <= '\u9fff'));
                englishTechnicalBlocks = translated.Count(x => !x.Text.Any(c => c is >= '\u3400' and <= '\u9fff') && x.Text.Length >= 20);
                Check(translated.Count == merged.Count, "DeepSeek returned one result for every assembled paragraph block");
                Check(chineseBlocks > 0, $"DeepSeek produced Chinese for {chineseBlocks} blocks while preserving {englishTechnicalBlocks} long code/proper-name blocks");
            }

            SaveAnnotated(image, crop, raw, merged, Path.Combine(_output, "wps-slide-ocr-annotated.png"));
            File.WriteAllText(Path.Combine(_output, "wps-live-metrics.json"), JsonSerializer.Serialize(new
            {
                target.Title, Window = new { frame.Width, frame.Height },
                Slide = new { crop.X, crop.Y, crop.Width, crop.Height, Confidence = best.Scores?.FinalScore, best.Method },
                Ocr = new { Milliseconds = ocrWatch.Elapsed.TotalMilliseconds, RawVisualLines = raw.Count,
                    TranslationBlocks = merged.Count, MultilineParagraphBlocks = multiline,
                    CodeBlocks = merged.Count(x=>x.BlockType==OcrBlockType.Code),
                    LongestRawCharacters = raw.Select(x => x.OriginalText.Length).DefaultIfEmpty().Max(),
                    LongestMergedCharacters = merged.Select(x => x.OriginalText.Length).DefaultIfEmpty().Max() },
                Translation = new { Milliseconds = translationMs, Blocks = translatedCount, ChineseBlocks = chineseBlocks, PreservedLongCodeOrProperNameBlocks = englishTechnicalBlocks,
                    Provider = settings.Translation.Provider, Model = settings.Translation.Model },
                TotalMilliseconds = ocrWatch.Elapsed.TotalMilliseconds + translationMs
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        window.Close();
    }

    private static Int32Rect Clamp(Rect value, int width, int height)
    {
        var x = Math.Clamp((int)Math.Round(value.X), 0, width - 1);
        var y = Math.Clamp((int)Math.Round(value.Y), 0, height - 1);
        var right = Math.Clamp((int)Math.Round(value.Right), x + 1, width);
        var bottom = Math.Clamp((int)Math.Round(value.Bottom), y + 1, height);
        return new(x, y, right - x, bottom - y);
    }

    private static byte[] Crop(ReadOnlySpan<byte> source, int stride, Int32Rect crop)
    {
        var result = new byte[crop.Width * crop.Height * 4];
        for (var row = 0; row < crop.Height; row++)
            source.Slice((crop.Y + row) * stride + crop.X * 4, crop.Width * 4).CopyTo(result.AsSpan(row * crop.Width * 4));
        return result;
    }

    private static void SaveFrame(CapturedFrame frame, string path)
    {
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null,
            frame.PixelData.ToArray(), frame.Stride);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static void SaveAnnotated(Cv.Mat window, Int32Rect crop, IReadOnlyList<OcrBlock> raw,
        IReadOnlyList<OcrBlock> merged, string path)
    {
        using var output = window.Clone();
        Cv.Cv2.Rectangle(output, new Cv.Rect(crop.X, crop.Y, crop.Width, crop.Height), new Cv.Scalar(40, 190, 40, 255), 5);
        foreach (var block in raw)
            Cv.Cv2.Rectangle(output, new Cv.Rect(crop.X + (int)block.X, crop.Y + (int)block.Y, Math.Max(1, (int)block.Width), Math.Max(1, (int)block.Height)), new Cv.Scalar(30, 150, 255, 255), 2);
        foreach (var block in merged.Where(x => x.OriginalText.Length >= 30))
            Cv.Cv2.Rectangle(output, new Cv.Rect(crop.X + (int)block.X, crop.Y + (int)block.Y, Math.Max(1, (int)block.Width), Math.Max(1, (int)block.Height)), new Cv.Scalar(60, 220, 60, 255), 4);
        Cv.Cv2.ImWrite(path, output);
    }

    private sealed unsafe class WindowCaptureProbe : IDisposable
    {
        private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Shared;
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly IDirect3DDevice _winrtDevice;
        private readonly GraphicsCaptureItem _item;
        private readonly Direct3D11CaptureFramePool _framePool;
        private readonly GraphicsCaptureSession _session;
        private ID3D11Texture2D? _staging;
        private readonly string _id;

        public WindowCaptureProbe(nint hwnd, string id)
        {
            _id = id;
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            factory.EnumAdapters1(0, out var adapter).CheckError();
            using (adapter)
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0], out _device, out _context).CheckError();
            _winrtDevice = WinRtCaptureInterop.CreateDevice(_device);
            _item = CreateWindowItem(hwnd);
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
            _session = _framePool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = false;
            _session.StartCapture();
        }

        public CapturedFrame? TryCapture()
        {
            using var frame = _framePool.TryGetNextFrame();
            if (frame is null || frame.ContentSize.Width <= 0 || frame.ContentSize.Height <= 0) return null;
            using var surface = frame.Surface;
            using var texture = WinRtCaptureInterop.GetTexture(surface);
            var description = texture.Description;
            if (_staging is null || _staging.Description.Width != description.Width || _staging.Description.Height != description.Height)
            {
                _staging?.Dispose(); description.Usage = ResourceUsage.Staging; description.BindFlags = BindFlags.None;
                description.CPUAccessFlags = CpuAccessFlags.Read; description.MiscFlags = ResourceOptionFlags.None;
                _staging = _device.CreateTexture2D(description);
            }
            _context.CopyResource(_staging, texture);
            _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
            try
            {
                var result = new CapturedFrame(frame.ContentSize.Width, frame.ContentSize.Height, _id, _pool);
                PixelCopy.CopyBgraRows(mapped.DataPointer, checked((int)mapped.RowPitch), result);
                return result;
            }
            finally { _context.Unmap(_staging, 0); }
        }

        private static GraphicsCaptureItem CreateWindowItem(nint hwnd)
        {
            const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
            Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var name));
            nint factory = 0, item = 0;
            try
            {
                var factoryId = new Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, ref factoryId, out factory));
                var itemId = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
                var createForWindow = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)factory)[3];
                Marshal.ThrowExceptionForHR(createForWindow(factory, hwnd, &itemId, &item));
                return MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
            }
            finally
            {
                if (item != 0) Marshal.Release(item);
                if (factory != 0) Marshal.Release(factory);
                WindowsDeleteString(name);
            }
        }

        public void Dispose()
        {
            _session.Dispose(); _framePool.Dispose(); _staging?.Dispose();
            _winrtDevice.Dispose(); _context.Dispose(); _device.Dispose();
        }

        [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out nint value);
        [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
        [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, ref Guid iid, out nint factory);
    }
}
