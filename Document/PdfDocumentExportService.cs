using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ScreenTranslator.Document;

public sealed class PdfDocumentExportService : IDocumentExportService
{
    public bool Supports(DocumentType type) => type == DocumentType.Pdf;

    public async Task<DocumentExportResult> ExportAsync(TranslationDocument document, string outputPath,
        DocumentExportMode mode, IProgress<DocumentProgress>? progress, CancellationToken cancellationToken) => await Task.Run(() =>
    {
        PdfChineseFontResolver.Configure();
        PptxDocumentExportService.EnsureDifferentPath(document.SourcePath, outputPath);
        var warnings = new List<string>();
        using var source = PdfReader.Open(document.SourcePath, PdfDocumentOpenMode.Import);
        using var output = new PdfDocument();
        for (var i = 0; i < source.PageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = output.AddPage(source.Pages[i]);
            var model = document.Pages[i];
            if (mode == DocumentExportMode.Replace)
            {
                using var graphics = XGraphics.FromPdfPage(original, XGraphicsPdfPageOptions.Append);
                foreach (var block in model.TextBlocks.Where(x => !string.IsNullOrWhiteSpace(x.TranslatedText)))
                {
                    if (block.Bounds.IsEmpty || block.Bounds.Width < 1 || block.Bounds.Height < 1) { warnings.Add($"第 {i + 1} 页有文字块无法原位覆盖。"); continue; }
                    var rectangle = new XRect(block.Bounds.X, block.Bounds.Y, block.Bounds.Width, Math.Max(block.Bounds.Height, block.FontSize * 2.2));
                    graphics.DrawRectangle(XBrushes.White, rectangle);
                    DrawText(graphics, block.TranslatedText, rectangle, Math.Clamp(block.FontSize * .85, 7, 28));
                }
                warnings.Add($"第 {i + 1} 页使用白色背景覆盖文字；复杂背景请人工检查。");
            }
            else
            {
                var translated = output.AddPage(); translated.Width = original.Width; translated.Height = original.Height;
                using var graphics = XGraphics.FromPdfPage(translated);
                graphics.DrawString($"第 {i + 1} 页 · 中文译文", new XFont("Noto Sans CJK SC", 16, XFontStyleEx.Bold), XBrushes.DarkSlateGray,
                    new XRect(36, 28, translated.Width.Point - 72, 32), XStringFormats.TopLeft);
                var y = 72d;
                foreach (var block in model.TextBlocks.Where(x => !string.IsNullOrWhiteSpace(x.TranslatedText)))
                {
                    var height = Math.Max(34, EstimateHeight(block.TranslatedText, translated.Width.Point - 72, 11));
                    if (y + height > translated.Height.Point - 36)
                    {
                        warnings.Add($"第 {i + 1} 页译文较长，末尾可能需要人工调整。"); break;
                    }
                    DrawText(graphics, block.TranslatedText, new XRect(36, y, translated.Width.Point - 72, height), 11);
                    y += height + 8;
                }
            }
            progress?.Report(new("正在生成 PDF", i + 1, source.PageCount, 0, document.TextBlockCount,
                (i + 1d) / Math.Max(1, source.PageCount)));
        }
        output.Save(outputPath);
        using var verify = PdfReader.Open(outputPath, PdfDocumentOpenMode.Import);
        var valid = verify.PageCount == (mode == DocumentExportMode.Bilingual ? source.PageCount * 2 : source.PageCount);
        if (!valid) warnings.Add("导出的 PDF 页数验证失败。");
        return new DocumentExportResult(outputPath, valid, warnings.Distinct().ToArray());
    }, cancellationToken).ConfigureAwait(false);

    private static void DrawText(XGraphics graphics, string text, XRect bounds, double size)
    {
        var font = new XFont("Noto Sans CJK SC", size, XFontStyleEx.Regular,
            new XPdfFontOptions(PdfFontEncoding.Unicode, PdfFontEmbedding.TryComputeSubset));
        var formatter = new XTextFormatter(graphics) { Alignment = XParagraphAlignment.Left };
        formatter.DrawString(text, font, XBrushes.Black, bounds, XStringFormats.TopLeft);
    }

    private static double EstimateHeight(string text, double width, double fontSize)
    {
        var charsPerLine = Math.Max(1, (int)(width / (fontSize * .95)));
        return Math.Ceiling(text.Length / (double)charsPerLine) * fontSize * 1.7;
    }
}
