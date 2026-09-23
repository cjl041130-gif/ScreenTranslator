using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using ScreenTranslator.Models;

namespace ScreenTranslator.OCR;

public sealed class WindowsOcrEngine : IOcrEngine
{
    private OcrEngine? _engine;
    public string Name => "Windows OCR · en-US · CPU";
    public bool IsInitialized => _engine is not null;

    public Task InitializeAsync(InferenceDevice preferredDevice, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
            ?? throw new InvalidOperationException("未安装 English (United States) 本地 OCR 语言资源。请在 Windows 语言设置中安装英文 OCR。 ");
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<OcrBlock>> RecognizeAsync(OcrInput input, CancellationToken cancellationToken)
    {
        var engine = _engine ?? throw new InvalidOperationException("OCR 引擎尚未初始化。");
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Stride != input.Width * 4) throw new ArgumentException("OCR 输入必须是紧密排列的 BGRA32。", nameof(input));
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, input.Width, input.Height, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(input.BgraPixels.ToArray().AsBuffer());
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
        var raw = new List<(string Text, Rect Box, int Line)>();
        for (var lineIndex = 0; lineIndex < result.Lines.Count; lineIndex++)
        {
            var line = result.Lines[lineIndex];
            if (string.IsNullOrWhiteSpace(line.Text) || line.Words.Count == 0) continue;
            var left = line.Words.Min(w => w.BoundingRect.X);
            var top = line.Words.Min(w => w.BoundingRect.Y);
            var right = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
            var bottom = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
            raw.Add((line.Text.Trim(), new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top)), lineIndex));
        }
        var medianHeight = raw.Count == 0 ? 0 : raw.Select(x => x.Box.Height).Order().ElementAt(raw.Count / 2);
        return raw.Select((x, i) => new OcrBlock($"ocr-{i + 1}", x.Text, EstimateConfidence(x.Text), x.Box,
            x.Line, i, Classify(x.Text, x.Box, input.Width, input.Height, medianHeight,input.ContentKind))
            { ConfidenceSource = OcrConfidenceSource.Heuristic, SourceLineHeight = x.Box.Height, VisualLineCount = 1 }).ToArray();
    }

    private static double EstimateConfidence(string text)
    {
        // Windows.Media.Ocr does not expose per-line confidence. This documented quality indicator
        // penalizes replacement characters and symbol-heavy output; it is not a model probability.
        if (text.Length == 0) return 0;
        var suspicious = text.Count(c => c == '\uFFFD' || (char.IsSymbol(c) && c is not '$' and not '%' and not '+'));
        return Math.Clamp(0.92 - suspicious * 0.08 / Math.Max(1, text.Length), 0.5, 0.92);
    }

    private static OcrBlockType Classify(string text, Rect box, int width, int height, double medianHeight,OcrContentKind contentKind)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('•') || trimmed.StartsWith("- ") || trimmed.StartsWith("* ")) return OcrBlockType.Bullet;
        if (box.Y > height * 0.9) return OcrBlockType.Footer;
        if (text.Contains("=>") || text.Contains("();") || text.Contains("{") || text.Contains("}") || text.StartsWith("def ")) return OcrBlockType.Code;
        if (text.Any(c => c is '∑' or '√' or '≈' or '∞')) return OcrBlockType.Formula;
        // On long scrolling documents, vertical position is not a title signal: a
        // line near the top may simply be the middle of a paragraph after scrolling.
        // Keeping these lines as body text preserves strict top-to-bottom order.
        if(contentKind==OcrContentKind.WebDocument)return OcrBlockType.Body;
        if (box.Y < height * 0.28 && box.Height >= Math.Max(18, medianHeight * 1.25)) return OcrBlockType.Title;
        if (box.Y < height * 0.38 && box.Height >= medianHeight * 1.05) return OcrBlockType.Subtitle;
        return OcrBlockType.Body;
    }

    public ValueTask DisposeAsync() { _engine = null; return ValueTask.CompletedTask; }
}
