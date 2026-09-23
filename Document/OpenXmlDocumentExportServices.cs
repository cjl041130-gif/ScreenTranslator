using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace ScreenTranslator.Document;

public sealed class PptxDocumentExportService : IDocumentExportService
{
    public bool Supports(DocumentType type) => type == DocumentType.Pptx;

    public async Task<DocumentExportResult> ExportAsync(TranslationDocument document, string outputPath,
        DocumentExportMode mode, IProgress<DocumentProgress>? progress, CancellationToken cancellationToken) => await Task.Run(() =>
    {
        EnsureDifferentPath(document.SourcePath, outputPath);
        File.Copy(document.SourcePath, outputPath, true);
        var byId = document.Pages.SelectMany(x => x.TextBlocks).Where(x => !string.IsNullOrWhiteSpace(x.TranslatedText)).ToDictionary(x => x.StableId);
        using (var presentation = PresentationDocument.Open(outputPath, true))
        {
            var part = presentation.PresentationPart ?? throw new InvalidDataException("PPTX 缺少演示文稿主体。");
            var presentationBody = part.Presentation ?? throw new InvalidDataException("PPTX 缺少演示文稿数据。");
            var ids = presentationBody.SlideIdList?.Elements<P.SlideId>().ToArray() ?? [];
            for (var i = 0; i < ids.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slide = part.GetPartById(ids[i].RelationshipId!.Value!) as SlidePart ?? throw new InvalidDataException("PPTX 幻灯片关系无效。");
                foreach (var entry in PptxStructure.EnumerateParagraphs(slide, i))
                    if (byId.TryGetValue(entry.StableId, out var block)) ReplaceDrawingParagraph(entry.Paragraph, block, mode);
                slide.Slide?.Save();
                progress?.Report(new("正在生成 PPTX", i + 1, ids.Length, 0, byId.Count, (i + 1d) / Math.Max(1, ids.Length)));
            }
        }
        using var verify = PresentationDocument.Open(outputPath, false);
        var errors = new OpenXmlValidator().Validate(verify).Take(20).Select(x => $"{x.Path?.XPath}: {x.Description}").ToArray();
        if (errors.Length > 0) return new DocumentExportResult(outputPath, false, errors);
        return new DocumentExportResult(outputPath, true, []);
    }, cancellationToken).ConfigureAwait(false);

    private static void ReplaceDrawingParagraph(A.Paragraph paragraph, DocumentTextBlock block, DocumentExportMode mode)
    {
        var texts = paragraph.Descendants<A.Text>().ToArray();
        if (texts.Length == 0)
        {
            var run = new A.Run(new A.RunProperties { Language = "zh-CN" }, new A.Text());
            paragraph.Append(run); texts = [run.Text!];
        }
        texts[0].Text = mode == DocumentExportMode.Bilingual ? block.OriginalText + "\n" + block.TranslatedText : block.TranslatedText;
        foreach (var text in texts.Skip(1)) text.Text = "";
        var runProperties = paragraph.Descendants<A.RunProperties>().FirstOrDefault();
        if (runProperties is not null)
        {
            runProperties.Language = "zh-CN";
            var latin = runProperties.GetFirstChild<A.LatinFont>();
            if (latin is null) runProperties.Append(new A.LatinFont { Typeface = "Microsoft YaHei UI" });
            else latin.Typeface = "Microsoft YaHei UI";
            if (runProperties.FontSize?.Value is int size) runProperties.FontSize = Math.Max(700, (int)Math.Round(size * .85));
        }
    }

    internal static void EnsureDifferentPath(string source, string output)
    {
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new DocumentUserException("导出文件不能覆盖原文档，请选择新的文件名。");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    }
}

public sealed class DocxDocumentExportService : IDocumentExportService
{
    public bool Supports(DocumentType type) => type == DocumentType.Docx;

    public async Task<DocumentExportResult> ExportAsync(TranslationDocument document, string outputPath,
        DocumentExportMode mode, IProgress<DocumentProgress>? progress, CancellationToken cancellationToken) => await Task.Run(() =>
    {
        PptxDocumentExportService.EnsureDifferentPath(document.SourcePath, outputPath);
        File.Copy(document.SourcePath, outputPath, true);
        var byId = document.Pages.SelectMany(x => x.TextBlocks).Where(x => !string.IsNullOrWhiteSpace(x.TranslatedText)).ToDictionary(x => x.StableId);
        using (var word = WordprocessingDocument.Open(outputPath, true))
        {
            var body = word.MainDocumentPart?.Document?.Body ?? throw new InvalidDataException("DOCX 缺少正文。");
            var paragraphIndex = 0;
            foreach (var child in body.ChildElements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (child is W.Paragraph paragraph)
                {
                    var id = $"word-paragraph-{++paragraphIndex:000000}";
                    if (byId.TryGetValue(id, out var block)) ReplaceWordParagraph(paragraph, block, mode);
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
                                var id = $"word-table-{tableIndex:0000}-row-{r + 1:0000}-cell-{c + 1:0000}-paragraph-{p + 1:0000}";
                                paragraphIndex++;
                                if (byId.TryGetValue(id, out var block)) ReplaceWordParagraph(paragraphs[p], block, mode);
                            }
                        }
                    }
                }
            }
            word.MainDocumentPart.Document.Save();
        }
        using var verify = WordprocessingDocument.Open(outputPath, false);
        var errors = new OpenXmlValidator().Validate(verify).Take(20).Select(x => $"{x.Path?.XPath}: {x.Description}").ToArray();
        progress?.Report(new("正在生成 DOCX", document.PageCount, document.PageCount, document.TextBlockCount, document.TextBlockCount, 1));
        return new DocumentExportResult(outputPath, errors.Length == 0, errors);
    }, cancellationToken).ConfigureAwait(false);

    private static void ReplaceWordParagraph(W.Paragraph paragraph, DocumentTextBlock block, DocumentExportMode mode)
    {
        var texts = paragraph.Descendants<W.Text>().ToArray();
        if (texts.Length == 0)
        {
            var run = new W.Run(new W.RunProperties(new W.RunFonts { Ascii = "Microsoft YaHei UI", EastAsia = "Microsoft YaHei UI" }), new W.Text());
            paragraph.Append(run); texts = [run.GetFirstChild<W.Text>()!];
        }
        texts[0].Text = mode == DocumentExportMode.Bilingual ? block.OriginalText + "\n" + block.TranslatedText : block.TranslatedText;
        texts[0].Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve;
        foreach (var text in texts.Skip(1)) text.Text = "";
        var props = paragraph.Descendants<W.RunProperties>().FirstOrDefault();
        if (props is not null)
        {
            props.RunFonts ??= new W.RunFonts(); props.RunFonts.Ascii = "Microsoft YaHei UI"; props.RunFonts.EastAsia = "Microsoft YaHei UI";
            if (double.TryParse(props.FontSize?.Val?.Value, out var halfPoints)) props.FontSize!.Val = Math.Max(14, Math.Round(halfPoints * .85)).ToString("0");
        }
    }
}
