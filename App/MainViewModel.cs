using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenTranslator.Capture;
using ScreenTranslator.Core;
using ScreenTranslator.Services;
using ScreenTranslator.ViewModels;
using ScreenTranslator.Vision;
using ScreenTranslator.Models;
using ScreenTranslator.Recognition;
using ScreenTranslator.OCR;
using ScreenTranslator.Translation.AI;
using ScreenTranslator.Translation.Security;
using ScreenTranslator.Document;

namespace ScreenTranslator;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly AppLogger _log;
    private readonly IScreenCaptureService _capture;
    private readonly VisionPipeline _vision;
    private readonly RecognitionPipeline _recognition;
    private readonly WindowsApiKeyStore _deepSeekApiKeyStore;
    private readonly WindowsApiKeyStore _deepLApiKeyStore;
    private readonly ProviderApiKeyStore _providerApiKeyStore;
    private readonly AiTranslationEngine _aiTranslation;
    private readonly IOcrEngine _ocr;
    private readonly DocumentViewModel _documentViewModel;
    private readonly HttpClient _aiHttpClient;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _startCancellation;
    public DetectionSnapshot? Detection => _vision.Latest;
    public long VisionCompletedDetections => _vision.CompletedDetections;
    public SlideRecognitionResult? RecognitionResult => _recognition.Latest;
    public IReadOnlyList<TranslatedBlock> TranslatedBlocks => RecognitionResult?.Blocks ?? [];
    public RecognitionStatistics RecognitionStatistics => _recognition.Statistics;
    public string ProcessingStatus => _recognition.Status;
    public string OcrStatus => _recognition.IsInitialized ? _recognition.OcrEngineName : _recognition.State == RecognitionRuntimeState.Initializing ? "正在加载" : "本地高精度 · English";
    public string TranslationStatus => SelectedTranslationMode switch
    {
        "DeepL" => "DeepL 在线翻译",
        "DeepSeek V4 Pro" => "DeepSeek V4 Pro",
        _ => "DeepSeek Flash"
    };
    public string LatestRecognitionSummary => RecognitionResult is null ? "暂无识别内容" : RecognitionResult.FromCache ? $"已从缓存恢复 · {RecognitionResult.Blocks.Count} 个文字块" : $"本页已识别 · {RecognitionResult.Blocks.Count} 个文字块";
    public string PerformanceText => RecognitionResult is null
        ? "尚无本页性能数据"
        : $"OCR {_recognition.Statistics.LastOcrMilliseconds:F0} ms · 翻译 {_recognition.Statistics.LastTranslationMilliseconds:F0} ms · 总计 {_recognition.Statistics.LastTotalMilliseconds:F0} ms · 缓存命中 {_recognition.Statistics.CacheHits}";
    public IReadOnlyList<TranslationDisplayMode> DisplayModes { get; } = Enum.GetValues<TranslationDisplayMode>();
    public IReadOnlyList<InferenceDevice> InferenceDevices { get; } = Enum.GetValues<InferenceDevice>();
    public TranslationDisplayMode DisplayMode { get => _settings.Recognition.DisplayMode; set { if (_settings.Recognition.DisplayMode == value) return; _settings.Recognition.DisplayMode = value; _recognition.DisplayMode = value; SaveRecognitionSettings(); Changed(nameof(IsChineseOverlay)); Changed(nameof(IsBilingual)); Changed(nameof(IsSidebar)); } }
    public bool IsChineseOverlay { get => DisplayMode == TranslationDisplayMode.ChineseOverlay; set { if (value) DisplayMode = TranslationDisplayMode.ChineseOverlay; } }
    public bool IsBilingual { get => DisplayMode == TranslationDisplayMode.Bilingual; set { if (value) DisplayMode = TranslationDisplayMode.Bilingual; } }
    public bool IsSidebar { get => DisplayMode == TranslationDisplayMode.Sidebar; set { if (value) DisplayMode = TranslationDisplayMode.Sidebar; } }
    public double TranslationBackgroundOpacity { get => _settings.Recognition.TranslationBackgroundOpacity; set { _settings.Recognition.TranslationBackgroundOpacity = Math.Clamp(value, .15, 1); _recognition.BackgroundOpacity = _settings.Recognition.TranslationBackgroundOpacity; SaveRecognitionSettings(); } }
    public double TranslationFontScale { get => _settings.Recognition.FontScale; set { _settings.Recognition.FontScale = Math.Clamp(value, .65, 1.8); _recognition.FontScale = _settings.Recognition.FontScale; SaveRecognitionSettings(); } }
    public bool OriginalTextVisibility { get => _settings.Recognition.OriginalTextVisibility; set { _settings.Recognition.OriginalTextVisibility = value; _recognition.ShowOriginalText = value; SaveRecognitionSettings(); } }
    public InferenceDevice SelectedInferenceDevice { get => _settings.Recognition.InferenceDevice; set { if (State != ApplicationState.Stopped) return; _settings.Recognition.InferenceDevice = value; _recognition.PreferredDevice = value; SaveRecognitionSettings(); } }
    public string DisplayModeLabel => DisplayMode switch { TranslationDisplayMode.ChineseOverlay => "中文覆盖", TranslationDisplayMode.Bilingual => "中英对照", _ => "侧边栏" };
    public string LanguagePackPath => _recognition.LanguagePack?.InstallationPath ?? Path.Combine(AppContext.BaseDirectory, "ModelsData", "Translation", "en-zh-neural");
    public string LanguagePackDetails => _recognition.LanguagePack is { } p ? $"已安装 · 完全离线 · {p.Version} · {p.Quality} · {p.SizeBytes / 1024d / 1024d:F1} MB" : "神经翻译语言包随程序提供 · 首次开始时校验并加载";
    public AsyncCommand OpenLanguagePackFolderCommand { get; }
    private string _aiApiKeyInput = "";
    private string _aiConnectionStatus = "";
    public string AiApiKeyInput { get => _aiApiKeyInput; set { if(_aiApiKeyInput==value)return;_aiApiKeyInput=value;Changed();SaveAiKeyCommand.Notify(); } }
    public IReadOnlyList<string> TranslationModes { get; } = ["DeepL", "DeepSeek V4 Pro", "DeepSeek Flash"];
    public IReadOnlyList<string> TranslationProviders { get; } = ["DeepL", "DeepSeek"];
    public IReadOnlyList<string> DeepSeekModels { get; } = ["DeepSeek V4 Pro", "DeepSeek Flash"];
    public string SelectedTranslationProvider
    {
        get => _settings.Translation.Provider == "DeepL" ? "DeepL" : "DeepSeek";
        set
        {
            if (!TranslationProviders.Contains(value) || SelectedTranslationProvider == value) return;
            SelectedTranslationMode = value == "DeepL" ? "DeepL" : SelectedDeepSeekModel;
        }
    }
    public bool IsDeepSeekProvider => SelectedTranslationProvider == "DeepSeek";
    public string SelectedTranslationMode
    {
        get => _settings.Translation.Provider == "DeepL" ? "DeepL" :
            _settings.Translation.Model == "deepseek-v4-pro" ? "DeepSeek V4 Pro" : "DeepSeek Flash";
        set
        {
            if (!TranslationModes.Contains(value) || SelectedTranslationMode == value) return;
            if (value == "DeepL") _settings.Translation.Provider = "DeepL";
            else
            {
                _settings.Translation.Provider = "DeepSeek";
                _settings.Translation.Model = value == "DeepSeek V4 Pro" ? "deepseek-v4-pro" : "deepseek-flash";
            }
            _settings.Translation.Normalize(); _settings.Save(_log); _aiConnectionStatus = "";
            Changed(); Changed(nameof(SelectedTranslationProvider)); Changed(nameof(IsDeepSeekProvider));
            Changed(nameof(SelectedDeepSeekModel)); Changed(nameof(TranslationStatus));
            Changed(nameof(ActiveTranslationStatus)); NotifyCommands();
            _ = _recognition.InvalidateTranslationsAsync();
        }
    }
    public string SelectedDeepSeekModel
    {
        get => _settings.Translation.Model == "deepseek-v4-pro" ? "DeepSeek V4 Pro" : "DeepSeek Flash";
        set
        {
            if (!DeepSeekModels.Contains(value)) return;
            var model = value == "DeepSeek V4 Pro" ? "deepseek-v4-pro" : "deepseek-flash";
            if (_settings.Translation.Model == model) return;
            _settings.Translation.Model = model; _settings.Translation.Normalize(); _settings.Save(_log);
            Changed(); Changed(nameof(SelectedTranslationMode)); Changed(nameof(TranslationStatus));
            _ = _recognition.InvalidateTranslationsAsync();
        }
    }
    public IReadOnlyList<string> AiStyles { get; }=["忠实准确","自然流畅","课件简洁"];
    public string SelectedAiStyle { get=>_settings.Translation.Style switch{AiTranslationStyle.Faithful=>"忠实准确",AiTranslationStyle.Presentation=>"课件简洁",_=>"自然流畅"}; set { var style=value switch{"忠实准确"=>AiTranslationStyle.Faithful,"课件简洁"=>AiTranslationStyle.Presentation,_=>AiTranslationStyle.Natural};if(_settings.Translation.Style==style)return;_settings.Translation.Style=style;_settings.Save(_log);Changed(); } }
    public string AiTranslationStatus => _deepSeekApiKeyStore.HasKey ? "DeepSeek 已配置" : "未配置 DeepSeek API Key";
    public string DeepLTranslationStatus => _deepLApiKeyStore.HasKey ? "DeepL 已配置" : "未配置 DeepL API Key";
    public string ActiveTranslationStatus => !string.IsNullOrWhiteSpace(_aiConnectionStatus) ? _aiConnectionStatus :
        _providerApiKeyStore.HasKey ? $"{SelectedTranslationMode} 已配置 · 等待手动开始识别" : $"{SelectedTranslationMode} 未配置 API Key";
    public AsyncCommand SaveAiKeyCommand { get; }
    public AsyncCommand DeleteAiKeyCommand { get; }
    private string _deepLApiKeyInput = "";
    public string DeepLApiKeyInput { get => _deepLApiKeyInput; set { if (_deepLApiKeyInput == value) return; _deepLApiKeyInput = value; Changed(); SaveDeepLKeyCommand.Notify(); } }
    public AsyncCommand SaveDeepLKeyCommand { get; }
    public AsyncCommand DeleteDeepLKeyCommand { get; }
    public AsyncCommand TestAiConnectionCommand { get; }
    public string DetectionLabel => State == ApplicationState.Stopped ? "未启动" : Detection?.Error is not null ? "区域检测异常，请查看日志" : Detection?.Region is { } region ? $"内容区域已识别 · 置信度 {region.Confidence:P0}" : Detection?.Target?.Occlusions.Count > 0 ? "请保持内容窗口可见，移开遮挡窗口" : "正在查找网页、PDF 或演示文稿…";
    public string TargetApplication => Detection?.Target?.ApplicationType switch { ApplicationType.PowerPoint => "Microsoft PowerPoint", ApplicationType.WpsPresentation => "WPS 演示", ApplicationType.PdfViewer => "PDF 阅读器 / 浏览器 PDF", ApplicationType.Browser => "网页 / 在线文档", _ => "尚未发现可识别窗口" };
    public string TargetWindowTitle => Detection?.Target?.Title ?? "—";
    public string VisionDebugInfo => Detection is not { } d ? "未启动视觉处理" :
        $"窗口: {d.Target?.ClassName} / {d.Target?.ProcessName} / HWND {d.Target?.Handle}\n搜索: {d.SearchRegion}\n区域: {d.Region?.Bounds.ToString() ?? "—"} · 比例 {d.Region?.AspectRatio:F3}\n检测耗时: {d.LatencyMs:F1} ms\n" +
        string.Join("\n", d.Candidates.Take(8).Select((c, i) => $"{i + 1}. {c.Method}: {c.Scores?.FinalScore:F3} · Area {c.Scores?.AreaScore:F2} Ratio {c.Scores?.AspectRatioScore:F2} Edge {c.Edge:F2} Stability {c.Scores?.TemporalStabilityScore:F2}"));
    public DebugOverlaySettings VisionDebug => _settings.Vision.DebugOverlay;
    public bool ShowSearchRegion { get => VisionDebug.ShowSearchRegion; set { VisionDebug.ShowSearchRegion = value; SaveVisionDebug(); } }
    public bool ShowCandidates { get => VisionDebug.ShowCandidates; set { VisionDebug.ShowCandidates = value; SaveVisionDebug(); } }
    public bool ShowCandidateScore { get => VisionDebug.ShowCandidateScore; set { VisionDebug.ShowCandidateScore = value; SaveVisionDebug(); } }
    public bool ShowStableRegion { get => VisionDebug.ShowStableRegion; set { VisionDebug.ShowStableRegion = value; SaveVisionDebug(); } }
    public bool ShowWindowInformation { get => VisionDebug.ShowWindowInformation; set { VisionDebug.ShowWindowInformation = value; SaveVisionDebug(); } }
    private void SaveVisionDebug([CallerMemberName] string? name = null) { _settings.Save(_log); Changed(name); }
    private void SaveRecognitionSettings([CallerMemberName] string? name = null)
    {
        _settings.Save(_log); Changed(name); Changed(nameof(DisplayModeLabel));
        if (SelectedMonitor is not null) _ = _recognition.RefreshOverlayAsync(SelectedMonitor);
    }
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _statisticsTimer;
    private bool _busy, _closing;
    private ApplicationState _observedState;
    private string? _commandError;
    public ObservableCollection<string> RecentActivity { get; } = new() { "应用已启动", "等待用户点击开始识别" };
    public bool AutoStart => false;
    public string StateLabel => _commandError is not null || _recognition.State == RecognitionRuntimeState.Faulted ? "运行异常" : _recognition.State switch
    {
        RecognitionRuntimeState.Initializing => "正在初始化",
        RecognitionRuntimeState.Running or RecognitionRuntimeState.Processing or RecognitionRuntimeState.RegionLost => "识别中",
        RecognitionRuntimeState.Paused => "已暂停",
        _ => State is ApplicationState.Recovering or ApplicationState.Faulted ? "运行异常" : "待机中"
    };
    public string StateTone => _commandError is not null || _recognition.State == RecognitionRuntimeState.Faulted ? "Faulted" : _recognition.State switch
    {
        RecognitionRuntimeState.Initializing or RecognitionRuntimeState.Running or RecognitionRuntimeState.Processing or RecognitionRuntimeState.RegionLost => "Running",
        RecognitionRuntimeState.Paused => "Paused",
        _ => State is ApplicationState.Recovering or ApplicationState.Faulted ? "Faulted" : "Idle"
    };
    public string StartLabel => State == ApplicationState.Paused ? "继续识别" : "开始识别";
    public string RecognitionSummary => _recognition.State switch { RecognitionRuntimeState.Initializing => "正在初始化本地 OCR", RecognitionRuntimeState.Processing => "正在识别或进行在线翻译", RecognitionRuntimeState.RegionLost => "等待网页、PDF 或演示文稿", RecognitionRuntimeState.Running => "识别中", RecognitionRuntimeState.Paused => "已暂停", RecognitionRuntimeState.Faulted => "运行异常", _ => "未启动" };
    public string PreviewLabel => State switch { ApplicationState.Capturing => "实时预览", ApplicationState.Paused => "画面已暂停", ApplicationState.Starting => "正在准备", _ => "全屏预览" };
    public IReadOnlyList<int> AvailableFrameRates { get; } = new[] { 20, 30, 60 };
    public int TargetFps
    {
        get => _settings.TargetFPS;
        set { if (!CanSelectMonitor || !AvailableFrameRates.Contains(value)) return; _settings.TargetFPS = value; _settings.Save(_log); Changed(); Changed(nameof(FpsText)); }
    }
    public bool DebugMode { get => _settings.DebugMode; set { _settings.DebugMode = value; _settings.Save(_log); Changed(); } }
    public AsyncCommand OpenLogsCommand { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }
    private PageViewModel _currentPage = null!;
    public PageViewModel CurrentPage { get => _currentPage; set { if (_currentPage == value) return; _currentPage = value; Changed(); } }
    public ObservableCollection<MonitorInfo> Monitors { get; } = new();
    private MonitorInfo? _selectedMonitor;
    public MonitorInfo? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (_selectedMonitor == value || !CanSelectMonitor) return;
            _selectedMonitor = value; _settings.SelectedMonitor = value?.Id;
            _settings.Save(_log); Changed(); NotifyCommands();
        }
    }
    private WriteableBitmap? _preview;
    public WriteableBitmap? Preview { get => _preview; private set { _preview = value; Changed(); } }
    private string _resolutionText = "Screen Resolution: —";
    public string ResolutionText { get => _resolutionText; private set { _resolutionText = value; Changed(); Changed(nameof(ResolutionValue)); } }
    public string ResolutionValue => _resolutionText.Replace("Screen Resolution: ", "");
    public string FpsText => $"FPS: {_capture.FramesPerSecond:F1} / {_settings.TargetFPS}";
    public string StatusText => _commandError ?? _capture.Status;
    public bool CanSelectMonitor => !_busy && !_closing && _capture.State is ApplicationState.Stopped or ApplicationState.Faulted;
    public ApplicationState State => _capture.State;
    public long PresentedFrames { get; private set; }
    public AsyncCommand StartCommand { get; }
    public AsyncCommand PauseCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public MainViewModel(AppLogger log, IScreenCaptureService? capture = null, AppSettings? settings = null)
    {
        _log = log; _settings = settings ?? AppSettings.Load(log);
        _capture = capture ?? new ScreenCaptureService(log);
        _vision = new VisionPipeline(_settings.Vision, log);
        _deepSeekApiKeyStore=new WindowsApiKeyStore();
        _deepLApiKeyStore=new WindowsApiKeyStore("ScreenTranslator/DeepL");
        _providerApiKeyStore=new ProviderApiKeyStore(_settings.Translation,_deepSeekApiKeyStore,_deepLApiKeyStore);
        _aiHttpClient=new HttpClient();
        var deepSeekClient=new DeepSeekTranslationClient(_aiHttpClient,_settings.Translation,log);
        var deepLClient=new DeepLTranslationClient(_aiHttpClient,_settings.Translation,log);
        var aiClient=new TranslationClientRouter(_settings.Translation,deepSeekClient,deepLClient);
        _aiTranslation=new AiTranslationEngine(_settings.Translation,_providerApiKeyStore,aiClient);
        _ocr=new AdaptiveOcrEngine(_settings.OCR,log);
        _recognition = new RecognitionPipeline(log,_ocr,_aiTranslation,cache: new SlideRecognitionCache(_settings.Recognition.SlideCacheCapacity), changes: new SlideChangeDetector(TimeSpan.FromMilliseconds(_settings.Recognition.StabilityMilliseconds)))
        { DisplayMode = _settings.Recognition.DisplayMode, BackgroundOpacity = _settings.Recognition.TranslationBackgroundOpacity, FontScale = _settings.Recognition.FontScale, ShowOriginalText = _settings.Recognition.OriginalTextVisibility, PreferredDevice = _settings.Recognition.InferenceDevice };
        OpenLogsCommand = new(() => { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_log.DirectoryPath) { UseShellExecute = true }); return Task.CompletedTask; }, () => !_closing, ShowError);
        OpenLanguagePackFolderCommand = new(() => { Directory.CreateDirectory(LanguagePackPath); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LanguagePackPath) { UseShellExecute = true }); return Task.CompletedTask; }, () => !_closing, ShowError);
        SaveAiKeyCommand=new(SaveAiKeyAsync,()=>!_closing&&!string.IsNullOrWhiteSpace(AiApiKeyInput),ShowError);
        DeleteAiKeyCommand=new(DeleteAiKeyAsync,()=>!_closing&&_deepSeekApiKeyStore.HasKey,ShowError);
        SaveDeepLKeyCommand=new(SaveDeepLKeyAsync,()=>!_closing&&!string.IsNullOrWhiteSpace(DeepLApiKeyInput),ShowError);
        DeleteDeepLKeyCommand=new(DeleteDeepLKeyAsync,()=>!_closing&&_deepLApiKeyStore.HasKey,ShowError);
        TestAiConnectionCommand=new(TestAiConnectionAsync,()=>!_closing&&_providerApiKeyStore.HasKey,ShowError);
        var documentOptions = _settings.DocumentTranslation;
        var validation = new DocumentValidationService(documentOptions);
        var renderer = new PdfPageRenderer();
        IDocumentParser[] parsers = [new PptxDocumentParser(), new PdfDocumentParser(_ocr, renderer, documentOptions), new DocxDocumentParser()];
        var import = new DocumentImportService(validation, parsers, documentOptions, log);
        var documentCache = new DocumentTranslationCache(documentOptions);
        IDocumentExportService[] exporters = [new PptxDocumentExportService(), new PdfDocumentExportService(), new DocxDocumentExportService()];
        _documentViewModel = new DocumentViewModel(this, import, new DocumentTranslationOrchestrator(documentOptions, documentCache), exporters, _aiTranslation, documentOptions, documentCache, renderer, log);
        Pages = new PageViewModel[] { new HomeViewModel(this), new RecognitionViewModel(this), _documentViewModel, new LanguagePackViewModel(this), new SettingsViewModel(this) };
        CurrentPage = Pages[0];
        StartCommand = new(() => BusyAsync(StartAsync), () => !_busy && !_closing && SelectedMonitor is not null && State is ApplicationState.Stopped or ApplicationState.Paused or ApplicationState.Faulted, ShowError);
        PauseCommand = new(() => BusyAsync(PauseAsync), () => !_busy && !_closing && State is ApplicationState.Capturing or ApplicationState.Starting or ApplicationState.Recovering, ShowError);
        StopCommand = new(StopRequestedAsync, () => !_closing && (State != ApplicationState.Stopped || _recognition.State != RecognitionRuntimeState.Idle || _busy), ShowError);
        RefreshCommand = new(() => BusyAsync(RefreshAsync), () => CanSelectMonitor, ShowError);
        LoadMonitors(new MonitorService(log).GetMonitors());
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
        _previewTimer.Tick += PreviewTick;
        _statisticsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _statisticsTimer.Tick += StatisticsTick;
        _previewTimer.Start(); _statisticsTimer.Start();
    }

    private Task SaveAiKeyAsync()
    {
        _deepSeekApiKeyStore.Save(AiApiKeyInput); AiApiKeyInput=""; _aiConnectionStatus="DeepSeek 密钥已安全保存 · 尚未测试连接";
        Changed(nameof(AiTranslationStatus));Changed(nameof(ActiveTranslationStatus));DeleteAiKeyCommand.Notify();TestAiConnectionCommand.Notify();return Task.CompletedTask;
    }
    private Task DeleteAiKeyAsync()
    {
        _deepSeekApiKeyStore.Delete();AiApiKeyInput="";_aiConnectionStatus="未配置 · 已删除 DeepSeek API Key";
        Changed(nameof(AiTranslationStatus));Changed(nameof(ActiveTranslationStatus));DeleteAiKeyCommand.Notify();TestAiConnectionCommand.Notify();return Task.CompletedTask;
    }
    private Task SaveDeepLKeyAsync()
    {
        _deepLApiKeyStore.Save(DeepLApiKeyInput); DeepLApiKeyInput=""; _aiConnectionStatus="DeepL 密钥已安全保存 · 尚未测试连接";
        Changed(nameof(DeepLTranslationStatus));Changed(nameof(ActiveTranslationStatus));DeleteDeepLKeyCommand.Notify();TestAiConnectionCommand.Notify();return Task.CompletedTask;
    }
    private Task DeleteDeepLKeyAsync()
    {
        _deepLApiKeyStore.Delete();DeepLApiKeyInput="";_aiConnectionStatus="未配置 · 已删除 DeepL API Key";
        Changed(nameof(DeepLTranslationStatus));Changed(nameof(ActiveTranslationStatus));DeleteDeepLKeyCommand.Notify();TestAiConnectionCommand.Notify();return Task.CompletedTask;
    }
    private async Task TestAiConnectionAsync()
    {
        _aiConnectionStatus="正在测试连接…";Changed(nameof(AiTranslationStatus));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try{await _aiTranslation.TestConnectionAsync(timeout.Token);_aiConnectionStatus=$"{SelectedTranslationMode} 可以使用 · 连接测试成功";}
        catch(AiTranslationException ex){_aiConnectionStatus=ex.Message;}
        catch(OperationCanceledException){_aiConnectionStatus=$"连接 {SelectedTranslationMode} 超时，请检查 VPN、代理或网络后重试";}
        catch(Exception ex){_log.Error("Translation connection test failed",ex);_aiConnectionStatus=$"{SelectedTranslationMode} 连接测试失败，请查看日志";}
        finally{Changed(nameof(ActiveTranslationStatus));}
    }

    private async Task BusyAsync(Func<Task> action)
    {
        _busy = true; _commandError = null; NotifyCommands();
        try { await action(); }
        finally { _busy = false; NotifyCommands(); RefreshVisualState(); Changed(nameof(StatusText)); Changed(nameof(FpsText)); }
    }
    private async Task StartAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (State == ApplicationState.Paused)
            {
                _startCancellation?.Dispose(); _startCancellation = new();
                _recognition.Resume(); await _capture.StartAsync(SelectedMonitor!, _settings.TargetFPS); _vision.Start();
                return;
            }
            _startCancellation?.Cancel(); _startCancellation?.Dispose(); _startCancellation = new();
            var token = _startCancellation.Token;
            _settings.SelectedMonitor = SelectedMonitor!.Id; _settings.Save(_log);
            await _recognition.InitializeAsync(token);
            token.ThrowIfCancellationRequested();
            await _capture.StartAsync(SelectedMonitor, _settings.TargetFPS);
            token.ThrowIfCancellationRequested();
            if (State is ApplicationState.Starting or ApplicationState.Capturing) { _vision.Start(); _recognition.Start(); }
        }
        catch (OperationCanceledException) when (_startCancellation?.IsCancellationRequested == true) { }
        catch
        {
            await _recognition.StopAsync(); await _vision.StopAsync(); await _capture.StopAsync(); throw;
        }
        finally { _lifecycle.Release(); }
    }
    private async Task PauseAsync() { await _recognition.PauseAsync(); await _vision.PauseAsync(); await _capture.PauseAsync(); }
    private async Task StopRequestedAsync()
    {
        _startCancellation?.Cancel();
        await _lifecycle.WaitAsync();
        try { await StopAsync(); }
        finally { _lifecycle.Release(); _busy = false; NotifyCommands(); RefreshVisualState(); }
    }
    private async Task StopAsync()
    {
        await _recognition.StopAsync();
        await _vision.StopAsync();
        await _capture.StopAsync();
        Preview = null; ResolutionText = "Screen Resolution: —";
    }
    private async Task RefreshAsync()
    {
        await _recognition.StopAsync(); await _vision.StopAsync();
        await _capture.StopAsync();
        Preview = null; ResolutionText = "Screen Resolution: —";
        LoadMonitors(await Task.Run(() => new MonitorService(_log).GetMonitors()));
    }
    private void LoadMonitors(IReadOnlyList<MonitorInfo> monitors)
    {
        var selectedId = _settings.SelectedMonitor;
        Monitors.Clear();
        foreach (var monitor in monitors) Monitors.Add(monitor);
        _selectedMonitor = Monitors.FirstOrDefault(m => m.Id == selectedId) ?? Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();
        Changed(nameof(SelectedMonitor));
        if (_selectedMonitor is null) _commandError = "未检测到显示器，请连接显示器后点击 Refresh。";
        NotifyCommands();
    }
    private void PreviewTick(object? sender, EventArgs e)
    {
        using var frame = _capture.TakeLatestFrame();
        if (frame is null) return;
        if (State is ApplicationState.Stopped or ApplicationState.Paused or ApplicationState.Faulted) return;
        var resolution = $"Screen Resolution: {frame.Width} × {frame.Height}";
        if (_resolutionText != resolution) ResolutionText = resolution;
        if (Preview is null || Preview.PixelWidth != frame.Width || Preview.PixelHeight != frame.Height)
            Preview = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
        // One pooled CPU readback + one WPF upload. No per-frame Bitmap/PNG/Dispatcher queue.
        Preview.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Buffer, frame.Stride, 0);
        if (SelectedMonitor is not null) { _vision.Submit(frame, SelectedMonitor); _recognition.Submit(frame, SelectedMonitor, _vision.Latest); }
        PresentedFrames++;
    }
    private void StatisticsTick(object? sender, EventArgs e)
    {
        Changed(nameof(FpsText)); Changed(nameof(StatusText));
        Changed(nameof(Detection)); Changed(nameof(DetectionLabel)); Changed(nameof(TargetApplication)); Changed(nameof(TargetWindowTitle)); Changed(nameof(VisionDebugInfo));
        Changed(nameof(RecognitionResult)); Changed(nameof(TranslatedBlocks)); Changed(nameof(RecognitionStatistics)); Changed(nameof(ProcessingStatus));
        Changed(nameof(OcrStatus)); Changed(nameof(TranslationStatus)); Changed(nameof(LatestRecognitionSummary)); Changed(nameof(PerformanceText)); Changed(nameof(LanguagePackDetails)); Changed(nameof(LanguagePackPath));
        Changed(nameof(AiTranslationStatus)); Changed(nameof(DeepLTranslationStatus)); Changed(nameof(ActiveTranslationStatus));
        RefreshVisualState();
    }
    private void RefreshVisualState()
    {
        if (_observedState != State)
        {
            _observedState = State;
            var message = State switch
            {
                ApplicationState.Starting => "正在准备屏幕捕获",
                ApplicationState.Capturing => $"开始屏幕捕获 · 本地 OCR 与 {SelectedTranslationMode} 已启用",
                ApplicationState.Paused => "屏幕捕获已暂停",
                ApplicationState.Stopped => "屏幕捕获已停止 · 等待手动开始",
                ApplicationState.Recovering => "捕获暂时中断，正在尝试恢复",
                _ => "捕获异常，请在设置中查看日志"
            };
            RecentActivity.Insert(0, $"{DateTime.Now:HH:mm}  {message}");
            while (RecentActivity.Count > 3) RecentActivity.RemoveAt(RecentActivity.Count - 1);
            NotifyCommands(); Changed(nameof(State));
        }
        Changed(nameof(StateLabel)); Changed(nameof(StateTone)); Changed(nameof(StartLabel)); Changed(nameof(RecognitionSummary)); Changed(nameof(PreviewLabel)); Changed(nameof(ProcessingStatus));
    }
    private void NotifyCommands()
    {
        Changed(nameof(CanSelectMonitor));
        StartCommand?.Notify(); PauseCommand?.Notify(); StopCommand?.Notify(); RefreshCommand?.Notify();SaveAiKeyCommand?.Notify();DeleteAiKeyCommand?.Notify();SaveDeepLKeyCommand?.Notify();DeleteDeepLKeyCommand?.Notify();TestAiConnectionCommand?.Notify();
    }
    private void ShowError(Exception ex) { _log.Error("Control command failed", ex); _commandError = ex.Message; Changed(nameof(StatusText)); RefreshVisualState(); }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public async ValueTask DisposeAsync()
    {
        if (_closing) return;
        _closing = true; NotifyCommands();
        _previewTimer.Stop(); _previewTimer.Tick -= PreviewTick;
        _statisticsTimer.Stop(); _statisticsTimer.Tick -= StatisticsTick;
        await _documentViewModel.DisposeAsync();
        _startCancellation?.Cancel(); _startCancellation?.Dispose();
        await _recognition.DisposeAsync(); await _vision.DisposeAsync();
        await _capture.DisposeAsync();
        _aiHttpClient.Dispose();
        Preview = null; _settings.Save(_log);
        _lifecycle.Dispose();
    }
}

