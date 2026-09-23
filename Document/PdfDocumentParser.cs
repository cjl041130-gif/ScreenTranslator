using System.Windows;
using ScreenTranslator.Models;
using ScreenTranslator.OCR;
using UglyToad.PdfPig;

namespace ScreenTranslator.Document;

public sealed class PdfDocumentParser(IOcrEngine ocr, PdfPageRenderer renderer,
    DocumentTranslationOptions options) : IDocumentParser
{
    public DocumentType DocumentType => DocumentType.Pdf;

    public async Task<TranslationDocument> ParseAsync(string path, IProgress<DocumentProgress>? progress,
        CancellationToken cancellationToken) => await Task.Run(async () =>
    {
        var hash = await DocumentImportService.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        var result = new TranslationDocument
        {
            DocumentId = Guid.NewGuid().ToString("N"), OriginalFileName = Path.GetFileName(path), SourcePath = path,
            ContentSha256 = hash, DocumentType = DocumentType.Pdf, FileSizeBytes = new FileInfo(path).Length,
            CreatedAt = DateTimeOffset.UtcNow
        };
        using var pdf = PdfDocument.Open(path);
        var total = pdf.NumberOfPages;
        for (var index = 0; index < total; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = pdf.GetPage(index + 1);
            var page = new DocumentPage { PageIndex = index, DisplayName = $"第 {index + 1} 页", Width = source.Width, Height = source.Height, Status = DocumentPageStatus.Analyzing };
            var words = source.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToArray();
            if (HasUsableTextLayer(words.Select(w => w.Text)))
            {
                var lines = BuildLines(words);
                var ordered = OrderColumns(lines, source.Width);
                var order = 0;
                foreach (var line in ordered)
                {
                    var text = string.Join(" ", line.Words.Select(w => w.Text)).Trim();
                    if (string.IsNullOrWhiteSpace(text) || IsPageNumber(text, line.Bottom, source.Height)) continue;
                    var height = Math.Max(1, line.Top - line.Bottom);
                    page.TextBlocks.Add(new DocumentTextBlock
                    {
                        StableId = $"pdf-page-{index + 1:0000}-line-{++order:0000}", PageIndex = index,
                        OriginalText = text, SourceReference = $"pdf:{index + 1}:line:{order}", ReadingOrder = order,
                        Bounds = new Rect(line.Left, source.Height - line.Top, Math.Max(1, line.Right - line.Left), height),
                        FontSize = height, BlockType = height > source.Height * .035 ? DocumentBlockType.Title :
                            text.StartsWith('•') || text.StartsWith("- ") ? DocumentBlockType.Bullet : DocumentBlockType.Body
                    });
                }
            }
            else if (options.EnableScannedPdfOcr)
            {
                progress?.Report(new("正在执行本地 OCR", index, total, result.TextBlockCount, result.TextBlockCount, total == 0 ? 0 : index / (double)total));
                if (!ocr.IsInitialized) await ocr.InitializeAsync(InferenceDevice.Cpu, cancellationToken).ConfigureAwait(false);
                var rendered = await renderer.RenderAsync(path, (uint)index, cancellationToken).ConfigureAwait(false);
                var blocks = await ocr.RecognizeAsync(new OcrInput(rendered.BgraPixels, rendered.Width, rendered.Height, rendered.Stride, OcrContentKind.WebDocument), cancellationToken).ConfigureAwait(false);
                foreach (var block in blocks.OrderBy(x => x.ReadingOrder))
                    page.TextBlocks.Add(new DocumentTextBlock
                    {
                        StableId = $"pdf-page-{index + 1:0000}-ocr-{block.ReadingOrder + 1:0000}", PageIndex = index,
                        OriginalText = block.OriginalText, SourceReference = $"pdf:{index + 1}:ocr:{block.ReadingOrder + 1}",
                        ReadingOrder = block.ReadingOrder, Bounds = new Rect(block.X * source.Width / rendered.Width,
                            block.Y * source.Height / rendered.Height, block.Width * source.Width / rendered.Width,
                            block.Height * source.Height / rendered.Height), Confidence = block.Confidence,
                        FontSize = block.EffectiveLineHeight * source.Height / rendered.Height,
                        BlockType = block.BlockType switch { OcrBlockType.Title => DocumentBlockType.Title, OcrBlockType.Bullet => DocumentBlockType.Bullet, OcrBlockType.Table => DocumentBlockType.TableCell, OcrBlockType.Code => DocumentBlockType.Code, OcrBlockType.Formula => DocumentBlockType.Formula, _ => DocumentBlockType.Body }
                    });
                page.UsedOcr = true;
            }
            else page.Warning = "此页没有可用文本层，扫描页 OCR 已关闭。";
            page.Status = DocumentPageStatus.Ready;
            result.Pages.Add(page);
            progress?.Report(new("正在分析 PDF", index + 1, total, result.TextBlockCount, result.TextBlockCount,
                total == 0 ? 1 : (index + 1d) / total));
        }
        return result;
    }, cancellationToken).ConfigureAwait(false);

    private static bool HasUsableTextLayer(IEnumerable<string> values)
    {
        var text = string.Join(" ", values).Trim();
        if (text.Length < 24) return false;
        var printable = text.Count(c => !char.IsControl(c)) / (double)Math.Max(1, text.Length);
        var letters = text.Count(char.IsLetter);
        var replacement = text.Count(c => c == '\uFFFD');
        return printable >= .92 && letters >= 12 && replacement <= Math.Max(1, text.Length / 50);
    }

    private static bool IsPageNumber(string text, double bottom, double pageHeight) =>
        bottom < pageHeight * .05 && text.Length <= 8 && text.All(c => char.IsDigit(c) || char.IsWhiteSpace(c) || c is '-' or '–');

    private static PdfLine[] BuildLines(UglyToad.PdfPig.Content.Word[] words)
    {
        if (words.Length == 0) return [];
        var medianHeight = words.Select(w => Math.Max(1, w.BoundingBox.Height)).Order().ElementAt(words.Length / 2);
        var tolerance = Math.Clamp(medianHeight * .48, 1.5, 8);
        var groups = new List<List<UglyToad.PdfPig.Content.Word>>();
        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
        {
            var group = groups.FirstOrDefault(g => Math.Abs(g.Average(x => x.BoundingBox.Bottom) - word.BoundingBox.Bottom) <= tolerance);
            if (group is null) groups.Add([word]); else group.Add(word);
        }
        return groups.Select(g => new PdfLine(
                g.OrderBy(w => w.BoundingBox.Left).Select(w => new PdfWord(w.Text, w.BoundingBox.Left)).ToArray(),
                g.Average(w => w.BoundingBox.Bottom), g.Min(w => w.BoundingBox.Left),
                g.Max(w => w.BoundingBox.Right), g.Max(w => w.BoundingBox.Top)))
            .OrderByDescending(x => x.Bottom).ThenBy(x => x.Left).ToArray();
    }

    private static IEnumerable<PdfLine> OrderColumns(PdfLine[] lines, double width)
    {
        var left = lines.Where(x => x.Right <= width * .56).ToArray();
        var right = lines.Where(x => x.Left >= width * .44).ToArray();
        var spanning = lines.Where(x => x.Right > width * .56 && x.Left < width * .44).ToArray();
        var hasColumns = left.Length >= 3 && right.Length >= 3;
        return hasColumns ? spanning.OrderByDescending(x => x.Bottom).Concat(left.OrderByDescending(x => x.Bottom)).Concat(right.OrderByDescending(x => x.Bottom)) : lines;
    }

    private sealed record PdfWord(string Text, double Left);
    private sealed record PdfLine(PdfWord[] Words, double Bottom, double Left, double Right, double Top);
}
