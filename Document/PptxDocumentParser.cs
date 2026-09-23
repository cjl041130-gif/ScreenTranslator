using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using System.Windows;

namespace ScreenTranslator.Document;

public sealed class PptxDocumentParser : IDocumentParser
{
    public DocumentType DocumentType => DocumentType.Pptx;

    public async Task<TranslationDocument> ParseAsync(string path, IProgress<DocumentProgress>? progress,
        CancellationToken cancellationToken) => await Task.Run(async () =>
    {
        var hash = await DocumentImportService.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        var result = new TranslationDocument
        {
            DocumentId = Guid.NewGuid().ToString("N"), OriginalFileName = Path.GetFileName(path), SourcePath = path,
            ContentSha256 = hash, DocumentType = DocumentType.Pptx, FileSizeBytes = new FileInfo(path).Length,
            CreatedAt = DateTimeOffset.UtcNow
        };
        using var presentation = PresentationDocument.Open(path, false);
        var presentationPart = presentation.PresentationPart ?? throw new InvalidDataException("PPTX 缺少演示文稿主体。");
        var presentationBody = presentationPart.Presentation ?? throw new InvalidDataException("PPTX 缺少演示文稿数据。");
        var ids = presentationBody.SlideIdList?.Elements<P.SlideId>().ToArray() ?? [];
        var slideSize = presentationBody.SlideSize;
        var width = (slideSize?.Cx?.Value ?? 12_192_000L) / 12_700d;
        var height = (slideSize?.Cy?.Value ?? 6_858_000L) / 12_700d;
        for (var slideIndex = 0; slideIndex < ids.Length; slideIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rel = ids[slideIndex].RelationshipId?.Value ?? throw new InvalidDataException("PPTX 幻灯片关系无效。");
            var part = (SlidePart)presentationPart.GetPartById(rel);
            var page = new DocumentPage { PageIndex = slideIndex, DisplayName = $"幻灯片 {slideIndex + 1}", Width = width, Height = height, Status = DocumentPageStatus.Analyzing };
            foreach (var entry in PptxStructure.EnumerateParagraphs(part, slideIndex))
            {
                var text = string.Concat(entry.Paragraph.Descendants<A.Text>().Select(x => x.Text)).Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                var bounds = entry.Bounds.IsEmpty || entry.Bounds.Width <= 1 || entry.Bounds.Height <= 1
                    ? new Rect(width * .08, height * (.08 + Math.Min(entry.Order, 14) * .058), width * .84, height * .052)
                    : entry.Bounds;
                var firstRun = entry.Paragraph.Descendants<A.Run>().FirstOrDefault();
                var run = firstRun?.RunProperties;
                var typeface = run?.GetFirstChild<A.LatinFont>()?.Typeface?.Value ?? "";
                var fontSize = (run?.FontSize?.Value ?? 1800) / 100d;
                var color = run?.GetFirstChild<A.SolidFill>()?.RgbColorModelHex?.Val?.Value;
                page.TextBlocks.Add(new DocumentTextBlock
                {
                    StableId = entry.StableId, PageIndex = slideIndex, OriginalText = text,
                    SourceReference = entry.StableId, Bounds = bounds, ReadingOrder = entry.Order,
                    BlockType = entry.Type, FontSize = fontSize, FontFamily = typeface,
                    FontWeight = run?.Bold?.Value == true ? "SemiBold" : "Normal",
                    TextColor = string.IsNullOrWhiteSpace(color) ? "#FF202020" : "#FF" + color,
                    ParagraphLevel = entry.Paragraph.ParagraphProperties?.Level?.Value ?? 0,
                    BulletInformation = entry.Paragraph.ParagraphProperties?.GetFirstChild<A.CharacterBullet>()?.Char?.Value ?? ""
                });
            }
            page.Status = DocumentPageStatus.Ready;
            result.Pages.Add(page);
            progress?.Report(new("正在提取演示文稿文字", slideIndex + 1, ids.Length,
                result.TextBlockCount, result.TextBlockCount, ids.Length == 0 ? 1 : (slideIndex + 1d) / ids.Length));
        }
        return result;
    }, cancellationToken).ConfigureAwait(false);
}

internal sealed record PptxParagraphEntry(string StableId, A.Paragraph Paragraph, Rect Bounds,
    int Order, DocumentBlockType Type);

internal static class PptxStructure
{
    public static IEnumerable<PptxParagraphEntry> EnumerateParagraphs(SlidePart part, int slideIndex)
    {
        var order = 0;
        var tree = part.Slide?.CommonSlideData?.ShapeTree;
        if (tree is null) yield break;
        foreach (var element in Flatten(tree.ChildElements))
        {
            if (element is P.Shape shape)
            {
                var id = shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Id?.Value ?? 0;
                var bounds = GetBounds(shape.ShapeProperties?.Transform2D);
                var placeholder = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.GetFirstChild<P.PlaceholderShape>()?.Type?.Value;
                var type = placeholder == P.PlaceholderValues.Title || placeholder == P.PlaceholderValues.CenteredTitle ? DocumentBlockType.Title : DocumentBlockType.Body;
                var paragraphs = shape.TextBody?.Elements<A.Paragraph>().ToArray() ?? [];
                for (var p = 0; p < paragraphs.Length; p++)
                {
                    var effective = paragraphs[p].ParagraphProperties?.GetFirstChild<A.CharacterBullet>() is not null ? DocumentBlockType.Bullet : type;
                    var paragraphBounds = bounds;
                    if (!bounds.IsEmpty && paragraphs.Length > 1 && bounds.Height > 0)
                        paragraphBounds = new(bounds.X, bounds.Y + bounds.Height * p / paragraphs.Length,
                            bounds.Width, bounds.Height / paragraphs.Length);
                    yield return new($"slide-{slideIndex + 1:0000}-shape-{id:0000}-paragraph-{p + 1:0000}", paragraphs[p], paragraphBounds, order++, effective);
                }
            }
            else if (element is P.GraphicFrame frame)
            {
                var table = frame.Graphic?.GraphicData?.GetFirstChild<A.Table>();
                if (table is null) continue;
                var frameId = frame.NonVisualGraphicFrameProperties?.NonVisualDrawingProperties?.Id?.Value ?? 0;
                var bounds = GetBounds(frame.Transform);
                var rows = table.Elements<A.TableRow>().ToArray();
                for (var r = 0; r < rows.Length; r++)
                {
                    var cells = rows[r].Elements<A.TableCell>().ToArray();
                    for (var c = 0; c < cells.Length; c++)
                    {
                        var cellBounds = bounds.IsEmpty || rows.Length == 0 || cells.Length == 0 ? bounds :
                            new Rect(bounds.X + bounds.Width * c / cells.Length, bounds.Y + bounds.Height * r / rows.Length,
                                bounds.Width / cells.Length, bounds.Height / rows.Length);
                        var paragraphs = cells[c].TextBody?.Elements<A.Paragraph>().ToArray() ?? [];
                        for (var p = 0; p < paragraphs.Length; p++)
                            yield return new($"slide-{slideIndex + 1:0000}-table-{frameId:0000}-row-{r + 1:0000}-cell-{c + 1:0000}-paragraph-{p + 1:0000}", paragraphs[p], cellBounds, order++, DocumentBlockType.TableCell);
                    }
                }
            }
        }
    }

    private static IEnumerable<OpenXmlElement> Flatten(IEnumerable<OpenXmlElement> elements)
    {
        foreach (var element in elements)
        {
            yield return element;
            if (element is P.GroupShape group)
                foreach (var child in Flatten(group.ChildElements)) yield return child;
        }
    }

    private static Rect GetBounds(A.Transform2D? transform) => transform is null ? Rect.Empty :
        new((transform.Offset?.X?.Value ?? 0) / 12_700d, (transform.Offset?.Y?.Value ?? 0) / 12_700d,
            (transform.Extents?.Cx?.Value ?? 0) / 12_700d, (transform.Extents?.Cy?.Value ?? 0) / 12_700d);

    private static Rect GetBounds(P.Transform? transform) => transform is null ? Rect.Empty :
        new((transform.Offset?.X?.Value ?? 0) / 12_700d, (transform.Offset?.Y?.Value ?? 0) / 12_700d,
            (transform.Extents?.Cx?.Value ?? 0) / 12_700d, (transform.Extents?.Cy?.Value ?? 0) / 12_700d);
}
