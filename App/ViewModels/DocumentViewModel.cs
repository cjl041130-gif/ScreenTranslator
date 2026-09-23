using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Win32;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using ScreenTranslator.Document;
using ScreenTranslator.Models;
using ScreenTranslator.Services;
using ScreenTranslator.Translation;

namespace ScreenTranslator.ViewModels;

public sealed class DocumentViewModel : PageViewModel, INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IDocumentImportService _import;
    private readonly IDocumentTranslationOrchestrator _orchestrator;
    private readonly IReadOnlyList<IDocumentExportService> _exporters;
    private readonly ITranslationEngine _translation;
    private readonly DocumentTranslationOptions _options;
    private readonly DocumentTranslationCache _cache;
    private readonly PdfPageRenderer _previewRenderer;
    private readonly AppLogger _log;
    private CancellationTokenSource? _operation;
    private TranslationDocument? _document;
    private DocumentPage? _selectedPage;
    private DocumentTranslationState _state;
    private string _status = "等待导入文档", _error = "", _progressDetails = "";
    private double _progressPercent;
    private DocumentPreviewMode _previewMode = DocumentPreviewMode.Chinese;
    private DocumentExportMode _exportMode = DocumentExportMode.Replace;
    private int _selectedPdfHalfIndex;
    private BitmapImage? _currentPreviewImage;
    private CancellationTokenSource? _previewCancellation;

    public DocumentViewModel(MainViewModel capture, IDocumentImportService import,
        IDocumentTranslationOrchestrator orchestrator, IReadOnlyList<IDocumentExportService> exporters,
        ITranslationEngine translation, DocumentTranslationOptions options, DocumentTranslationCache cache, PdfPageRenderer previewRenderer,
        AppLogger log) : base(capture, "文档翻译", "\uE8A5")
    {
        _import = import; _orchestrator = orchestrator; _exporters = exporters; _translation = translation;
        _options = options; _cache = cache; _previewRenderer = previewRenderer; _log = log;
        SelectFileCommand = new(SelectFileAsync, () => !IsBusy, SetError);
        OpenOriginalCommand = new(() =>
        {
            if (Document is null) return Task.CompletedTask;
            Process.Start(new ProcessStartInfo(Document.SourcePath) { UseShellExecute = true });
            return Task.CompletedTask;
        }, () => HasDocument, SetError);
        StartTranslationCommand = new(StartTranslationAsync, () => CanStartTranslation, SetError);
        CancelCommand = new(CancelAsync, () => State == DocumentTranslationState.Translating, SetError);
        ExportCommand = new(ExportAsync, () => CanExport, SetError);
        RemoveCommand = new(RemoveAsync, () => HasDocument && !IsBusy, SetError);
        PreviousPageCommand = new(() => { MovePage(-1); return Task.CompletedTask; }, () => CanPreviousPage, SetError);
        NextPageCommand = new(() => { MovePage(1); return Task.CompletedTask; }, () => CanNextPage, SetError);
        ClearCacheCommand = new(() => { _cache.Clear(); Changed(nameof(CacheSizeText)); return Task.CompletedTask; }, () => !IsBusy && _cache.SizeBytes > 0, SetError);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public TranslationDocument? Document => _document;
    public bool HasDocument => _document is not null;
    public bool IsEmpty => _document is null;
    public bool IsBusy => State is DocumentTranslationState.Importing or DocumentTranslationState.Analyzing or DocumentTranslationState.Translating or DocumentTranslationState.Cancelling or DocumentTranslationState.Exporting;
    public DocumentTranslationState State { get => _state; private set { if (_state == value) return; if (!_state.CanMoveTo(value)) throw new InvalidOperationException($"非法文档状态转换：{_state} → {value}"); _state = value; Changed(); Changed(nameof(StateText)); Changed(nameof(IsBusy)); NotifyCommands(); } }
    public string StateText => State switch { DocumentTranslationState.Empty => "未导入", DocumentTranslationState.Importing => "正在导入", DocumentTranslationState.Analyzing => "正在分析", DocumentTranslationState.Ready => "等待翻译", DocumentTranslationState.Translating => "翻译中", DocumentTranslationState.Cancelling => "正在取消", DocumentTranslationState.Cancelled => "已取消，可继续", DocumentTranslationState.Completed => "翻译完成", DocumentTranslationState.Exporting => "正在导出", DocumentTranslationState.Exported => "已导出", _ => "处理失败" };
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public string Error { get => _error; private set { _error = value; Changed(); Changed(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public double ProgressPercent { get => _progressPercent; private set { _progressPercent = value; Changed(); } }
    public string ProgressDetails { get => _progressDetails; private set { _progressDetails = value; Changed(); } }
    public DocumentPage? SelectedPage { get => _selectedPage; set { if (_selectedPage == value) return; _selectedPage = value; SelectedPdfHalfIndex = 0; Changed(); Changed(nameof(PdfHalfHeight)); Changed(nameof(PdfHalfOffset)); Changed(nameof(PdfHalfTransform)); Changed(nameof(CurrentBlocks)); Changed(nameof(PagePosition)); Changed(nameof(CanPreviousPage)); Changed(nameof(CanNextPage)); PreviousPageCommand.Notify(); NextPageCommand.Notify(); _ = LoadPagePreviewAsync(value); } }
    public BitmapImage? CurrentPreviewImage { get => _currentPreviewImage; private set { _currentPreviewImage = value; Changed(); Changed(nameof(HasPagePreview)); } }
    public bool HasPagePreview => CurrentPreviewImage is not null;
    public bool IsPdfDocument => Document?.DocumentType == DocumentType.Pdf;
    public bool IsOpenXmlDocument => Document?.DocumentType is DocumentType.Pptx or DocumentType.Docx;
    public bool ShowPageCanvas => IsOpenXmlDocument;
    public bool ShowPageList => IsOpenXmlDocument;
    public bool ShowStructuredText => false;
    public IReadOnlyList<string> PdfHalfNames { get; } = ["上半页", "下半页"];
    public int SelectedPdfHalfIndex
    {
        get => _selectedPdfHalfIndex;
        set
        {
            var normalized = Math.Clamp(value, 0, 1);
            if (_selectedPdfHalfIndex == normalized) return;
            _selectedPdfHalfIndex = normalized;
            Changed(); Changed(nameof(SelectedPdfHalfName)); Changed(nameof(PdfHalfHeight)); Changed(nameof(PdfHalfOffset)); Changed(nameof(PdfHalfTransform));
        }
    }
    public string SelectedPdfHalfName { get => PdfHalfNames[SelectedPdfHalfIndex]; set { var index = Array.IndexOf(PdfHalfNames.ToArray(), value); if (index >= 0) SelectedPdfHalfIndex = index; } }
    public double PdfHalfHeight => (SelectedPage?.Height ?? 0) / 2d;
    public double PdfHalfOffset => SelectedPdfHalfIndex * PdfHalfHeight;
    public TranslateTransform PdfHalfTransform => new(0, -PdfHalfOffset);
    public IReadOnlyList<DocumentTextBlock> CurrentBlocks => SelectedPage?.TextBlocks ?? [];
    public string PagePosition => Document is null || SelectedPage is null ? "—" : $"{SelectedPage.PageIndex + 1} / {Document.PageCount}";
    public bool CanPreviousPage => SelectedPage is not null && SelectedPage.PageIndex > 0;
    public bool CanNextPage => Document is not null && SelectedPage is not null && SelectedPage.PageIndex + 1 < Document.PageCount;
    public IReadOnlyList<DocumentPreviewMode> PreviewModes { get; } = Enum.GetValues<DocumentPreviewMode>();
    public DocumentPreviewMode PreviewMode { get => _previewMode; set { _previewMode = value; Changed(); Changed(nameof(SelectedPreviewModeName)); Changed(nameof(ShowOriginal)); Changed(nameof(ShowChinese)); } }
    public bool ShowOriginal => PreviewMode is DocumentPreviewMode.Original or DocumentPreviewMode.Bilingual;
    public bool ShowChinese => PreviewMode is DocumentPreviewMode.Chinese or DocumentPreviewMode.Bilingual;
    public string[] PreviewModeNames { get; } = ["原文", "中文", "中英对照"];
    public string SelectedPreviewModeName { get => PreviewModeNames[(int)PreviewMode]; set { var index = Array.IndexOf(PreviewModeNames, value); if (index >= 0) PreviewMode = (DocumentPreviewMode)index; Changed(); } }
    public IReadOnlyList<DocumentExportMode> ExportModes { get; } = Enum.GetValues<DocumentExportMode>();
    public DocumentExportMode ExportMode { get => _exportMode; set { _exportMode = value; Changed(); Changed(nameof(SelectedExportModeName)); } }
    public string[] ExportModeNames { get; } = ["中文替换版", "中英对照版"];
    public string SelectedExportModeName { get => ExportModeNames[(int)ExportMode]; set { var index = Array.IndexOf(ExportModeNames, value); if (index >= 0) ExportMode = (DocumentExportMode)index; Changed(); } }
    public bool CanStartTranslation => HasDocument && State is DocumentTranslationState.Ready or DocumentTranslationState.Cancelled or DocumentTranslationState.Completed;
    public bool CanExport => HasDocument && State is DocumentTranslationState.Completed or DocumentTranslationState.Exported;
    public string FileName => Document?.OriginalFileName ?? "—";
    public string FileType => Document?.DocumentType.ToString().ToUpperInvariant() ?? "—";
    public string FileSize => Document is null ? "—" : $"{Document.FileSizeBytes / 1024d / 1024d:F1} MB";
    public string PageCount => Document?.PageCount.ToString() ?? "0";
    public string BlockCount => Document?.TextBlockCount.ToString() ?? "0";
    public string OcrPageCount => Document?.OcrPageCount.ToString() ?? "0";
    public string CharacterCount => Document?.TranslatableCharacterCount.ToString("N0") ?? "0";
    public string CurrentModel => Capture.SelectedTranslationMode;
    public string CacheSizeText => $"{_cache.SizeBytes / 1024d / 1024d:F1} MB";

    public AsyncCommand SelectFileCommand { get; }
    public AsyncCommand OpenOriginalCommand { get; }
    public AsyncCommand StartTranslationCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand ExportCommand { get; }
    public AsyncCommand RemoveCommand { get; }
    public AsyncCommand PreviousPageCommand { get; }
    public AsyncCommand NextPageCommand { get; }
    public AsyncCommand ClearCacheCommand { get; }

    private Task SelectFileAsync()
    {
        var dialog = new OpenFileDialog { Title = "选择要翻译的文档", Filter = "支持的文档 (*.pdf;*.pptx;*.docx)|*.pdf;*.pptx;*.docx|PDF (*.pdf)|*.pdf|PowerPoint (*.pptx)|*.pptx|Word (*.docx)|*.docx", Multiselect = false, CheckFileExists = true };
        return dialog.ShowDialog() == true ? ImportPathAsync(dialog.FileName) : Task.CompletedTask;
    }

    public async Task ImportPathAsync(string path)
    {
        if (IsBusy) return;
        if (State != DocumentTranslationState.Empty) State = DocumentTranslationState.Empty;
        _document = null; SelectedPage = null; CurrentPreviewImage = null; NotifyDocumentChanged();
        _operation?.Cancel(); _operation?.Dispose(); _operation = new();
        Error = ""; ProgressPercent = 0; ProgressDetails = ""; State = DocumentTranslationState.Importing; Status = "正在读取文档";
        try
        {
            State = DocumentTranslationState.Analyzing;
            var imported = await _import.ImportAsync(path, new Progress<DocumentProgress>(ApplyProgress), _operation.Token);
            _document = imported; SelectedPage = imported.Pages.FirstOrDefault();
            ExportMode = imported.DocumentType switch { DocumentType.Pdf => _options.DefaultPdfExportMode, DocumentType.Pptx => _options.DefaultPptxExportMode, _ => _options.DefaultDocxExportMode };
            State = DocumentTranslationState.Ready; Status = "文档分析完成，等待点击“开始翻译”"; ProgressPercent = 100; NotifyDocumentChanged();
        }
        catch (OperationCanceledException) { State = DocumentTranslationState.Faulted; Error = "文档导入已取消。"; }
        catch (Exception ex) { State = DocumentTranslationState.Faulted; SetError(ex); }
    }

    public Task HandleDroppedPathsAsync(IEnumerable<string> paths)
    {
        var files = paths.Where(File.Exists).ToArray();
        var supported = files.Where(x => Path.GetExtension(x).Equals(".pdf", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(x).Equals(".pptx", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(x).Equals(".docx", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (supported.Length == 0) { Error = "当前仅支持 PDF、PPTX 和 DOCX 文件。"; return Task.CompletedTask; }
        if (files.Length > 1) Error = "当前版本每次支持翻译一个文档，已读取第一个受支持文件。";
        return ImportPathAsync(supported[0]);
    }

    private async Task StartTranslationAsync()
    {
        if (Document is null) return;
        _operation?.Cancel(); _operation?.Dispose(); _operation = new();
        Error = ""; State = DocumentTranslationState.Translating; Status = $"正在使用 {CurrentModel} 翻译"; ProgressPercent = 0;
        try
        {
            await _translation.InitializeAsync(InferenceDevice.Cpu, _operation.Token);
            await _orchestrator.TranslateAsync(Document, (IPageTranslationEngine)_translation, new Progress<DocumentProgress>(ApplyProgress), _operation.Token);
            State = DocumentTranslationState.Completed; Status = "翻译完成，可以预览并导出新文件"; ProgressPercent = 100; Changed(nameof(CurrentBlocks));
        }
        catch (OperationCanceledException) { if (State == DocumentTranslationState.Cancelling) State = DocumentTranslationState.Cancelled; Status = "翻译已取消，已完成结果已经保留"; }
        catch (Exception ex) { State = DocumentTranslationState.Faulted; SetError(ex); }
    }

    private Task CancelAsync() { if (State == DocumentTranslationState.Translating) { State = DocumentTranslationState.Cancelling; Status = "正在取消翻译"; _operation?.Cancel(); } return Task.CompletedTask; }

    private async Task ExportAsync()
    {
        if (Document is null) return;
        var extension = Document.DocumentType switch { DocumentType.Pdf => ".pdf", DocumentType.Pptx => ".pptx", _ => ".docx" };
        var dialog = new SaveFileDialog { Title = "导出译文", FileName = Path.GetFileNameWithoutExtension(Document.OriginalFileName) + "_zh-CN" + extension, Filter = $"{FileType} (*{extension})|*{extension}", AddExtension = true, DefaultExt = extension };
        if (dialog.ShowDialog() != true) return;
        var exporter = _exporters.First(x => x.Supports(Document.DocumentType));
        _operation?.Cancel(); _operation?.Dispose(); _operation = new(); State = DocumentTranslationState.Exporting; Status = "正在生成导出文件"; Error = "";
        try
        {
            var result = await exporter.ExportAsync(Document, dialog.FileName, ExportMode, new Progress<DocumentProgress>(ApplyProgress), _operation.Token);
            if (!result.Valid) throw new DocumentUserException("导出文件验证失败，请查看日志后重试。");
            State = DocumentTranslationState.Exported; Status = result.Warnings.Count == 0 ? "译文已导出" : $"译文已导出 · {result.Warnings.Count} 项需要人工检查";
            _log.Info($"Document exported: type={Document.DocumentType}, mode={ExportMode}, warnings={result.Warnings.Count}");
        }
        catch (Exception ex) { State = DocumentTranslationState.Faulted; SetError(ex); }
    }

    private Task RemoveAsync() { _operation?.Cancel(); _previewCancellation?.Cancel(); _document = null; SelectedPage = null; CurrentPreviewImage = null; State = DocumentTranslationState.Empty; Status = "等待导入文档"; Error = ""; ProgressPercent = 0; ProgressDetails = ""; NotifyDocumentChanged(); return Task.CompletedTask; }
    private void MovePage(int offset) { if (Document is null || SelectedPage is null) return; SelectedPage = Document.Pages[Math.Clamp(SelectedPage.PageIndex + offset, 0, Document.PageCount - 1)]; }
    private void ApplyProgress(DocumentProgress value) { Status = value.Stage; ProgressPercent = Math.Clamp(value.Percent * 100, 0, 100); ProgressDetails = value.TotalBlocks > 0 ? $"已完成 {value.CompletedBlocks} / {value.TotalBlocks} 个文字块" : value.TotalPages > 0 ? $"正在处理第 {Math.Min(value.CompletedPages + 1, value.TotalPages)} / {value.TotalPages} 页" : ""; if (value.EstimatedRemaining is { } remaining && remaining.TotalSeconds >= 1) ProgressDetails += $" · 预计剩余约 {Math.Ceiling(remaining.TotalMinutes)} 分钟"; Changed(nameof(CurrentBlocks)); }
    private async Task LoadPagePreviewAsync(DocumentPage? page)
    {
        _previewCancellation?.Cancel(); _previewCancellation?.Dispose(); _previewCancellation = new();
        CurrentPreviewImage = null;
        if (page is null || Document?.DocumentType != DocumentType.Pdf) return;
        var token = _previewCancellation.Token;
        try
        {
            var rendered = await _previewRenderer.RenderAsync(Document.SourcePath, (uint)page.PageIndex, token);
            if (token.IsCancellationRequested || SelectedPage != page) return;
            using var stream = new MemoryStream(rendered.PngBytes, writable: false);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            CurrentPreviewImage = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Error("PDF preview render failed", ex); Error = "页面预览生成失败，但已提取的文字仍可查看。"; }
    }
    private void NotifyDocumentChanged() { foreach (var name in new[] { nameof(Document), nameof(HasDocument), nameof(IsEmpty), nameof(IsPdfDocument), nameof(IsOpenXmlDocument), nameof(ShowPageCanvas), nameof(ShowPageList), nameof(ShowStructuredText), nameof(FileName), nameof(FileType), nameof(FileSize), nameof(PageCount), nameof(BlockCount), nameof(OcrPageCount), nameof(CharacterCount), nameof(CurrentBlocks), nameof(PagePosition) }) Changed(name); NotifyCommands(); }
    private void NotifyCommands() { SelectFileCommand?.Notify(); OpenOriginalCommand?.Notify(); StartTranslationCommand?.Notify(); CancelCommand?.Notify(); ExportCommand?.Notify(); RemoveCommand?.Notify(); PreviousPageCommand?.Notify(); NextPageCommand?.Notify(); ClearCacheCommand?.Notify(); Changed(nameof(CanStartTranslation)); Changed(nameof(CanExport)); }
    private void SetError(Exception ex) { Error = ex is DocumentUserException ? ex.Message : "操作失败，请查看日志并重试。"; Status = "操作失败"; _log.Error("Document operation failed", ex); }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public ValueTask DisposeAsync() { _operation?.Cancel(); _operation?.Dispose(); _operation = null; _previewCancellation?.Cancel(); _previewCancellation?.Dispose(); _previewCancellation = null; return ValueTask.CompletedTask; }
}
