using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;
using ScreenTranslator.Document;
using ScreenTranslator.Core;
using ScreenTranslator.Models;
using ScreenTranslator.OCR;
using ScreenTranslator.Services;
using ScreenTranslator.Translation;
using ScreenTranslator.ViewModels;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace ScreenTranslator.Tests;

internal static partial class Program
{
    private static async Task RunDocumentUnitTestsAsync(MainWindow window)
    {
        var vm = (MainViewModel)window.DataContext;
        window.Hide();
        var fixtures = Path.Combine(_output, "document-fixtures"); Directory.CreateDirectory(fixtures);
        var docxPath = Path.Combine(fixtures, "WordImportFixture.docx"); CreateDocx(docxPath);
        var pdfPath = Path.Combine(fixtures, "MixedPdfFixture.pdf"); CreateMixedPdf(pdfPath);
        var options = new DocumentTranslationOptions(); options.Normalize();
        var log = ((App)Application.Current).Log;
        var fixtureRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures"));
        var pptxPath = Path.Combine(fixtureRoot, "TestPresentation16x9.pptx");
        if (!File.Exists(pptxPath)) throw new FileNotFoundException("PPTX 测试文件缺失", pptxPath);
        var validation = new DocumentValidationService(options);
        Check(validation.Validate(docxPath) == DocumentType.Docx, "DOCX file signature and package parts are accepted");
        Check(validation.Validate(pdfPath) == DocumentType.Pdf, "PDF file signature is accepted");
        Check(validation.Validate(pptxPath) == DocumentType.Pptx, "PPTX file signature and package parts are accepted");
        var badPath = Path.Combine(fixtures, "invalid.pdf"); await File.WriteAllTextAsync(badPath, "not a PDF");
        try { validation.Validate(badPath); throw new InvalidOperationException("Invalid PDF was accepted"); }
        catch (DocumentUserException) { Check(true, "Invalid PDF signature is rejected with a user-facing error"); }

        var pptx = await new PptxDocumentParser().ParseAsync(pptxPath, null, CancellationToken.None);
        Check(pptx.PageCount > 0 && pptx.TextBlockCount > 0, $"Real PPTX fixture parses slides and paragraphs ({pptx.PageCount} slides, {pptx.TextBlockCount} blocks)");
        Check(pptx.Pages.SelectMany(x => x.TextBlocks).Select(x => x.StableId).Distinct().Count() == pptx.TextBlockCount,
            "PPTX stable IDs are unique and text runs are combined per paragraph");

        var word = await new DocxDocumentParser().ParseAsync(docxPath, null, CancellationToken.None);
        Check(word.PageCount == 1 && word.TextBlockCount == 4, $"DOCX parser reads paragraphs, list item, and table cell ({word.TextBlockCount} blocks)");
        Check(word.Pages[0].TextBlocks[1].OriginalText == "An entire sentence should stay together.", "DOCX multiple text runs combine into one complete sentence");
        Check(word.Pages[0].TextBlocks.Any(x => x.BlockType == DocumentBlockType.Title), "DOCX heading style is recognized");
        Check(word.Pages[0].TextBlocks.Any(x => x.BlockType == DocumentBlockType.TableCell), "DOCX table cell text is extracted");

        var fakeOcr = new EmptyOcrEngine();
        var pdfOptions = new DocumentTranslationOptions { EnableScannedPdfOcr = false };
        var pdf = await new PdfDocumentParser(fakeOcr, new PdfPageRenderer(), pdfOptions).ParseAsync(pdfPath, null, CancellationToken.None);
        Check(pdf.PageCount == 2 && pdf.TextBlockCount > 0, $"Real mixed PDF parses text layer ({pdf.PageCount} pages, {pdf.TextBlockCount} blocks)");
        Check(pdf.Pages[0].TextBlocks.Any(x => x.OriginalText.Contains("PDF text layer test", StringComparison.OrdinalIgnoreCase)), "PDF text extraction preserves text-layer content: " + string.Join(" | ", pdf.Pages[0].TextBlocks.Select(x => x.OriginalText)));
        Check(pdf.Pages[1].TextBlocks.Count == 0 && pdf.Pages[1].Warning is not null, "Scan-only PDF page is detected for local OCR without sending image data");
        var renderer = new PdfPageRenderer();
        var rendered = await renderer.RenderAsync(pdfPath, 1, CancellationToken.None);
        Check(rendered.Width > 0 && rendered.Height > 0 && rendered.PngBytes.Length > 1000, "PDF page renders locally to a preview image");

        foreach (var block in word.Pages.SelectMany(x => x.TextBlocks).Where(x => x.ShouldTranslate))
        { block.TranslatedText = "测试译文 " + block.OriginalText; block.TranslationStatus = DocumentBlockTranslationStatus.Completed; }
        var docxOut = Path.Combine(fixtures, "WordImportFixture_zh-CN.docx");
        var docxResult = await new DocxDocumentExportService().ExportAsync(word, docxOut, DocumentExportMode.Replace, null, CancellationToken.None);
        Check(docxResult.Valid && File.Exists(docxOut) && !Path.GetFullPath(docxOut).Equals(Path.GetFullPath(docxPath), StringComparison.OrdinalIgnoreCase), "DOCX export creates a new validated file and preserves the original: " + string.Join(" | ", docxResult.Warnings));
        using (var exported = WordprocessingDocument.Open(docxOut, false)) Check(!new OpenXmlValidator().Validate(exported).Any(), "Exported DOCX passes OpenXmlValidator");

        foreach (var block in pdf.Pages.SelectMany(x => x.TextBlocks).Where(x => x.ShouldTranslate)) block.TranslatedText = "测试译文";
        var pdfOut = Path.Combine(fixtures, "MixedPdfFixture_zh-CN.pdf");
        var pdfResult = await new PdfDocumentExportService().ExportAsync(pdf, pdfOut, DocumentExportMode.Bilingual, null, CancellationToken.None);
        Check(pdfResult.Valid && File.Exists(pdfOut), "PDF bilingual export creates a reopenable PDF");
        using (var exportedPdf = PdfPigDocument.Open(pdfOut))
        {
            var translatedPdfText = string.Join("\n", exportedPdf.GetPages().Select(page => page.Text));
            Check(translatedPdfText.Contains("测试译文", StringComparison.Ordinal), "PDF export embeds the bundled Chinese font and retains extractable Chinese text");
        }

        var importer = new DocumentImportService(validation,
            [new PptxDocumentParser(), new PdfDocumentParser(fakeOcr, renderer, options), new DocxDocumentParser()], options, log);
        var cache = new DocumentTranslationCache(options); cache.Clear();
        var documentVm = new DocumentViewModel(vm, importer, new DocumentTranslationOrchestrator(options, cache),
            [new PptxDocumentExportService(), new PdfDocumentExportService(), new DocxDocumentExportService()],
            new TestTranslationEngine(), options, cache, renderer, log);
        vm.CurrentPage = documentVm; await Task.Delay(150); SaveWindow(window, "document-empty.png");
        Check(documentVm.State == DocumentTranslationState.Empty && !documentVm.StartTranslationCommand.CanExecute(null) && vm.State == ApplicationState.Stopped,
            "Document UI starts empty and importing does not start screen capture");
        await documentVm.ImportPathAsync(docxPath); await Task.Delay(250);
        Check(documentVm.State == DocumentTranslationState.Ready && documentVm.StartTranslationCommand.CanExecute(null), "Successful DOCX import reaches Ready and enables explicit translation");
        Check(documentVm.ShowPageCanvas && documentVm.ShowPageList && documentVm.CurrentBlocks.All(x => !x.Bounds.IsEmpty), "DOCX opens as a page preview with positioned paragraphs");
        Check(documentVm.OpenOriginalCommand.CanExecute(null), "Imported document exposes a command to view its original layout in the local Office app");
        Check(vm.State == ApplicationState.Stopped, "Document import keeps screen capture stopped");
        SaveWindow(window, "document-docx-ready.png");
        documentVm.StartTranslationCommand.Execute(null);
        await Until(() => documentVm.State == DocumentTranslationState.Completed, "Document translation reaches completed state");
        Check(documentVm.ExportCommand.CanExecute(null), "Completed document enables export");
        SaveWindow(window, "document-docx-completed.png");
        documentVm.RemoveCommand.Execute(null); await Until(() => documentVm.State == DocumentTranslationState.Empty, "Document removal completes");
        Check(documentVm.State == DocumentTranslationState.Empty && !documentVm.ExportCommand.CanExecute(null), "Removing document returns UI to Empty");
        await documentVm.ImportPathAsync(pptxPath); await Task.Delay(200);
        Check(documentVm.IsOpenXmlDocument && documentVm.ShowPageCanvas && documentVm.ShowPageList && documentVm.CurrentBlocks.All(x => !x.Bounds.IsEmpty), "PPTX opens as a slide preview with positioned text");
        foreach (var block in documentVm.CurrentBlocks.Where(x => x.ShouldTranslate)) block.TranslatedText = "中文完整覆盖";
        SaveWindow(window, "document-pptx-overlay.png");
        documentVm.RemoveCommand.Execute(null); await Until(() => documentVm.State == DocumentTranslationState.Empty, "PPTX removal completes");
        await documentVm.ImportPathAsync(pdfPath);
        await Until(() => documentVm.HasPagePreview, "PDF half-page preview renders after import");
        foreach (var block in documentVm.CurrentBlocks.Where(x => x.ShouldTranslate)) block.TranslatedText = "中文覆盖预览";
        Check(documentVm.IsPdfDocument && !documentVm.ShowStructuredText && !documentVm.ShowPageList && documentVm.PreviewMode == DocumentPreviewMode.Chinese && documentVm.SelectedPdfHalfName == "上半页" && documentVm.PdfHalfHeight > 0,
            "PDF preview defaults to an expanded upper-half Chinese overlay rather than shrinking the full page");
        Check(documentVm.CurrentBlocks.Where(x => x.ShouldTranslate).All(x => x.HasTranslation && x.OverlayFontSize > 0),
            "Translated PDF blocks expose fitted overlay text for source-position coverage");
        SaveWindow(window, "document-pdf-overlay.png");
        documentVm.SelectedPdfHalfName = "下半页";
        Check(documentVm.SelectedPdfHalfName == "下半页" && documentVm.PdfHalfOffset == documentVm.PdfHalfHeight, "PDF lower-half selection shifts the crop by exactly half a page");
        SaveWindow(window, "document-pdf-lower-half.png");
        documentVm.SelectedPdfHalfName = "上半页";
        documentVm.RemoveCommand.Execute(null); await Until(() => documentVm.State == DocumentTranslationState.Empty, "PDF document removal completes");
        await documentVm.DisposeAsync(); cache.Clear();
        File.WriteAllText(Path.Combine(_output, "document-summary.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            Pptx = new { pptx.OriginalFileName, pptx.PageCount, pptx.TextBlockCount },
            Docx = new { word.OriginalFileName, word.PageCount, word.TextBlockCount },
            Pdf = new { pdf.OriginalFileName, pdf.PageCount, pdf.TextBlockCount, pdf.OcrPageCount },
            ExportedDocx = docxOut, ExportedPdf = pdfOut, ScreenCaptureState = vm.State.ToString()
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        await vm.DisposeAsync();
        window.Close();
        await Until(() => !window.IsLoaded, "Document test window closes after asynchronous service cleanup");
    }

    private static void CreateDocx(string path)
    {
        using var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var numbering = main.AddNewPart<NumberingDefinitionsPart>();
        var level = new W.Level { LevelIndex = 0 };
        level.Append(new W.StartNumberingValue { Val = 1 }, new W.NumberingFormat { Val = W.NumberFormatValues.Bullet }, new W.LevelText { Val = "•" }, new W.ParagraphProperties(new W.Indentation { Left = "720", Hanging = "360" }));
        numbering.Numbering = new W.Numbering(
            new W.AbstractNum(new W.MultiLevelType { Val = W.MultiLevelValues.SingleLevel }, level) { AbstractNumberId = 0 },
            new W.NumberingInstance(new W.AbstractNumId { Val = 0 }) { NumberID = 1 });
        var body = new W.Body();
        body.Append(new W.Paragraph(new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Title" }),
            new W.Run(new W.Text("Document translation fixture"))));
        body.Append(new W.Paragraph(new W.Run(new W.Text("An entire ") { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }),
            new W.Run(new W.RunProperties(new W.Bold()), new W.Text("sentence should stay together."))));
        body.Append(new W.Table(new W.TableProperties(), new W.TableGrid(new W.GridColumn { Width = "3000" }),
            new W.TableRow(new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Table cell content")))))));
        body.Append(new W.Paragraph(new W.ParagraphProperties(new W.NumberingProperties(new W.NumberingLevelReference { Val = 0 }, new W.NumberingId { Val = 1 })), new W.Run(new W.Text("A bullet item"))));
        main.Document = new W.Document(body); main.Document.Save();
    }

    private static void CreateMixedPdf(string path)
    {
        var imagePath = Path.ChangeExtension(path, ".png");
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1400, 900));
            var text = new FormattedText("LOCAL OCR SCANNED PAGE TEST", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Arial"), 70, Brushes.Black, 96);
            context.DrawText(text, new Point(90, 280));
            var second = new FormattedText("The complete sentence stays together.", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Arial"), 48, Brushes.Black, 96);
            context.DrawText(second, new Point(90, 400));
        }
        var bitmap = new RenderTargetBitmap(1400, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var output = File.Create(imagePath)) encoder.Save(output);
        using var pdf = new PdfDocument();
        var textPage = pdf.AddPage(); textPage.Size = PdfSharp.PageSize.Letter;
        using (var g = XGraphics.FromPdfPage(textPage))
        {
            g.DrawString("PDF text layer test: a complete English sentence for document import.", new XFont("Arial", 16), XBrushes.Black, new XPoint(50, 90));
            g.DrawString("This text remains local and is not uploaded as a document.", new XFont("Arial", 13), XBrushes.Black, new XPoint(50, 130));
            g.DrawString("Lower-half text verifies enlarged continuation.", new XFont("Arial", 16), XBrushes.Black, new XPoint(50, 440));
        }
        var scanPage = pdf.AddPage(); scanPage.Size = PdfSharp.PageSize.Letter;
        using (var g = XGraphics.FromPdfPage(scanPage))
        using (var image = XImage.FromFile(imagePath)) g.DrawImage(image, 24, 24, scanPage.Width.Point - 48, scanPage.Height.Point - 48);
        pdf.Save(path); File.Delete(imagePath);
    }

    private sealed class EmptyOcrEngine : IOcrEngine
    {
        public string Name => "Test OCR"; public bool IsInitialized { get; private set; }
        public Task InitializeAsync(InferenceDevice preferredDevice, CancellationToken cancellationToken) { IsInitialized = true; return Task.CompletedTask; }
        public Task<IReadOnlyList<OcrBlock>> RecognizeAsync(OcrInput input, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<OcrBlock>>([]);
        public ValueTask DisposeAsync() { IsInitialized = false; return ValueTask.CompletedTask; }
    }

    private sealed class TestTranslationEngine : ITranslationEngine, IPageTranslationEngine
    {
        public string Name => "Test translation"; public bool IsInitialized { get; private set; }
        public LanguagePackMetadata? LanguagePack => null;
        public Task InitializeAsync(InferenceDevice preferredDevice, CancellationToken cancellationToken) { IsInitialized = true; return Task.CompletedTask; }
        public Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken) => Task.FromResult(new TranslationResponse("测试译文", false, 0));
        public Task<IReadOnlyList<TranslationResponse>> TranslatePageAsync(IReadOnlyList<OcrBlock> blocks, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TranslationResponse>>(blocks.Select(x => new TranslationResponse("测试译文 " + x.OriginalText, false, 0)).ToArray());
        public ValueTask DisposeAsync() { IsInitialized = false; return ValueTask.CompletedTask; }
    }
}
