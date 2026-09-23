using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace ScreenTranslator.Document;

public enum DocumentType { Pdf, Pptx, Docx }
public enum DocumentPageStatus { Pending, Analyzing, Ready, Translating, Completed, Faulted }
public enum DocumentBlockType { Title, Body, Bullet, TableCell, Caption, Code, Formula, Header, Footer, Unsupported }
public enum DocumentBlockTranslationStatus { Pending, Skipped, Translating, Completed, Faulted }
public enum DocumentPreviewMode { Original, Chinese, Bilingual }
public enum DocumentExportMode { Replace, Bilingual }

public sealed class TranslationDocument
{
    public required string DocumentId { get; init; }
    public required string OriginalFileName { get; init; }
    public required string SourcePath { get; init; }
    public required string ContentSha256 { get; init; }
    public required DocumentType DocumentType { get; init; }
    public required long FileSizeBytes { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "zh-CN";
    public ObservableCollection<DocumentPage> Pages { get; } = [];
    public int PageCount => Pages.Count;
    public int TextBlockCount => Pages.Sum(p => p.TextBlocks.Count);
    public int OcrPageCount => Pages.Count(p => p.UsedOcr);
    public int TranslatableCharacterCount => Pages.SelectMany(p => p.TextBlocks).Where(b => b.ShouldTranslate).Sum(b => b.OriginalText.Length);
}

public sealed class DocumentPage
{
    public required int PageIndex { get; init; }
    public required string DisplayName { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public byte[]? PreviewImage { get; set; }
    public bool UsedOcr { get; set; }
    public string? Warning { get; set; }
    public DocumentPageStatus Status { get; set; } = DocumentPageStatus.Pending;
    public ObservableCollection<DocumentTextBlock> TextBlocks { get; } = [];
}

public sealed class DocumentTextBlock : INotifyPropertyChanged
{
    private string _translatedText = "";
    private DocumentBlockTranslationStatus _translationStatus = DocumentBlockTranslationStatus.Pending;
    public required string StableId { get; init; }
    public required int PageIndex { get; init; }
    public required string OriginalText { get; init; }
    public string TranslatedText { get => _translatedText; set { if (_translatedText == value) return; _translatedText = value; Changed(); Changed(nameof(HasTranslation)); Changed(nameof(OverlayFontSize)); } }
    public Rect Bounds { get; init; }
    public int ReadingOrder { get; init; }
    public DocumentBlockType BlockType { get; init; } = DocumentBlockType.Body;
    public double Confidence { get; init; } = 1;
    public double FontSize { get; init; } = 18;
    public string FontFamily { get; init; } = "";
    public string FontWeight { get; init; } = "Normal";
    public string TextColor { get; init; } = "#FF202020";
    public string BackgroundColor { get; init; } = "#00FFFFFF";
    public int ParagraphLevel { get; init; }
    public string BulletInformation { get; init; } = "";
    public required string SourceReference { get; init; }
    public DocumentBlockTranslationStatus TranslationStatus { get => _translationStatus; set { if (_translationStatus == value) return; _translationStatus = value; Changed(); } }
    public bool HasTranslation => !string.IsNullOrWhiteSpace(TranslatedText);
    public double OverlayFontSize
    {
        get
        {
            if (Bounds.IsEmpty || Bounds.Width <= 1 || Bounds.Height <= 1 || !HasTranslation) return Math.Clamp(FontSize * .82, 6, 28);
            var target = Math.Clamp(FontSize * .82, 6, 28);
            var charsPerLine = Math.Max(1, (int)(Bounds.Width / (target * .95)));
            var lines = Math.Max(1, (int)Math.Ceiling(TranslatedText.Length / (double)charsPerLine));
            return Math.Clamp(Math.Min(target, Bounds.Height / (lines * 1.12)), 4, target);
        }
    }
    public bool ShouldTranslate => BlockType is not (DocumentBlockType.Code or DocumentBlockType.Formula or DocumentBlockType.Unsupported)
        && OriginalText.Any(char.IsLetter) && !Uri.TryCreate(OriginalText.Trim(), UriKind.Absolute, out _);
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed record DocumentProgress(string Stage, int CompletedPages, int TotalPages,
    int CompletedBlocks, int TotalBlocks, double Percent, TimeSpan? EstimatedRemaining = null);
public sealed record DocumentExportResult(string OutputPath, bool Valid, IReadOnlyList<string> Warnings);
