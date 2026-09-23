using DocumentFormat.OpenXml.Packaging;
using System.Text;
using W = DocumentFormat.OpenXml.Wordprocessing;
using System.Windows;

namespace ScreenTranslator.Document;

public sealed class DocxDocumentParser : IDocumentParser
{
    public DocumentType DocumentType => DocumentType.Docx;

    public async Task<TranslationDocument> ParseAsync(string path, IProgress<DocumentProgress>? progress,
        CancellationToken cancellationToken) => await Task.Run(async () =>
    {
        var hash = await DocumentImportService.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        var result = new TranslationDocument
        {
            DocumentId = Guid.NewGuid().ToString("N"), OriginalFileName = Path.GetFileName(path), SourcePath = path,
            ContentSha256 = hash, DocumentType = DocumentType.Docx, FileSizeBytes = new FileInfo(path).Length,
            CreatedAt = DateTimeOffset.UtcNow
        };
        using var word = WordprocessingDocument.Open(path, false);
        var body = word.MainDocumentPart?.Document?.Body ?? throw new InvalidDataException("DOCX 缺少正文。");
        var page = NewPage(0);
        var paragraphIndex = 0;
        var cursorY = 72d;
        const double pageWidth = 595d;
        const double pageHeight = 842d;
        const double margin = 72d;
        const double bodyWidth = pageWidth - margin * 2;
        const double bodyBottom = pageHeight - margin;
        foreach (var child in body.ChildElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child is W.Paragraph paragraph)
            {
                var id = $"word-paragraph-{++paragraphIndex:000000}";
                var text = string.Concat(paragraph.Descendants<W.Text>().Select(x => x.Text)).Trim();
                if (text.Length > 0)
                {
                    var fontSize = GetFontSize(paragraph);
                    var lines = EstimateLineCount(text, bodyWidth, fontSize);
                    var blockHeight = Math.Max(fontSize * 1.5, lines * fontSize * 1.35 + 4);
                    if (cursorY + blockHeight > bodyBottom && page.TextBlocks.Count > 0)
                    {
                        page.Status = DocumentPageStatus.Ready;
                        result.Pages.Add(page);
                        page = NewPage(result.Pages.Count);
                        cursorY = margin;
                    }
                    AddParagraph(page, paragraph, id, paragraphIndex, bounds: new(margin, cursorY, bodyWidth, blockHeight));
                    cursorY += blockHeight + fontSize * .55;
                }
                if (paragraph.Descendants<W.Break>().Any(x => x.Type?.Value == W.BreakValues.Page) || paragraph.Descendants<W.LastRenderedPageBreak>().Any())
                {
                    if (page.TextBlocks.Count > 0) { page.Status = DocumentPageStatus.Ready; result.Pages.Add(page); page = NewPage(result.Pages.Count); cursorY = margin; }
                }
            }
            else if (child is W.Table table)
            {
                var tableIndex = body.Elements<W.Table>().TakeWhile(x => x != table).Count() + 1;
                var rows = table.Elements<W.TableRow>().ToArray();
                for (var r = 0; r < rows.Length; r++)
                {
                    var cells = rows[r].Elements<W.TableCell>().ToArray();
                    for (var c = 0; c < cells.Length; c++)
                    {
                        var paragraphs = cells[c].Elements<W.Paragraph>().ToArray();
                        for (var p = 0; p < paragraphs.Length; p++)
                        {
                            var cellText = string.Concat(paragraphs[p].Descendants<W.Text>().Select(x => x.Text)).Trim();
                            if (cellText.Length == 0) continue;
                            var fontSize = GetFontSize(paragraphs[p]);
                            var lines = EstimateLineCount(cellText, bodyWidth * .48, fontSize);
                            var blockHeight = Math.Max(fontSize * 1.5, lines * fontSize * 1.35 + 4);
                            if (cursorY + blockHeight > bodyBottom && page.TextBlocks.Count > 0)
                            {
                                page.Status = DocumentPageStatus.Ready;
                                result.Pages.Add(page);
                                page = NewPage(result.Pages.Count);
                                cursorY = margin;
                            }
                            AddParagraph(page, paragraphs[p], $"word-table-{tableIndex:0000}-row-{r + 1:0000}-cell-{c + 1:0000}-paragraph-{p + 1:0000}", ++paragraphIndex, DocumentBlockType.TableCell,
                                new(margin, cursorY, bodyWidth, blockHeight));
                            cursorY += blockHeight + fontSize * .55;
                        }
                    }
                }
            }
        }
        if (page.TextBlocks.Count > 0 || result.Pages.Count == 0) { page.Status = DocumentPageStatus.Ready; result.Pages.Add(page); }
        progress?.Report(new("正在提取 Word 文字", result.PageCount, result.PageCount, result.TextBlockCount, result.TextBlockCount, 1));
        return result;
    }, cancellationToken).ConfigureAwait(false);

    private static DocumentPage NewPage(int index) => new() { PageIndex = index, DisplayName = $"第 {index + 1} 页", Width = 595, Height = 842, Status = DocumentPageStatus.Analyzing };

    private static void AddParagraph(DocumentPage page, W.Paragraph paragraph, string id, int order,
        DocumentBlockType? forcedType = null, Rect? bounds = null)
    {
        var text = string.Concat(paragraph.Descendants<W.Text>().Select(x => x.Text)).Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        var runProps = paragraph.Descendants<W.RunProperties>().FirstOrDefault();
        var fontSize = double.TryParse(runProps?.FontSize?.Val?.Value, out var halfPoints) ? halfPoints / 2 : 11;
        var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
        var numbering = paragraph.ParagraphProperties?.NumberingProperties;
        var type = forcedType ?? (style.Contains("Title", StringComparison.OrdinalIgnoreCase) || style.Contains("Heading", StringComparison.OrdinalIgnoreCase)
            ? DocumentBlockType.Title : numbering is not null ? DocumentBlockType.Bullet : DocumentBlockType.Body);
        page.TextBlocks.Add(new DocumentTextBlock
        {
            StableId = id, PageIndex = page.PageIndex, OriginalText = text, SourceReference = id,
            Bounds = bounds ?? Rect.Empty, ReadingOrder = order, BlockType = type, FontSize = fontSize,
            FontFamily = runProps?.RunFonts?.Ascii?.Value ?? "", FontWeight = runProps?.Bold is not null ? "SemiBold" : "Normal",
            TextColor = "#FF" + (runProps?.Color?.Val?.Value ?? "202020"),
            ParagraphLevel = numbering?.NumberingLevelReference?.Val?.Value ?? 0,
            BulletInformation = numbering is null ? "" : "列表"
        });
    }

    private static double GetFontSize(W.Paragraph paragraph)
    {
        var runProps = paragraph.Descendants<W.RunProperties>().FirstOrDefault();
        return double.TryParse(runProps?.FontSize?.Val?.Value, out var halfPoints) ? halfPoints / 2 : 11;
    }

    private static int EstimateLineCount(string text, double width, double fontSize)
    {
        var capacity = Math.Max(1, (int)(width / Math.Max(4, fontSize * .52)));
        return Math.Max(1, (int)Math.Ceiling(text.EnumerateRunes().Sum(r => r.Value > 0x2E80 ? 1.8 : 1) / capacity));
    }
}
