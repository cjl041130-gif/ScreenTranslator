using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using OpenCvSharp;
using ScreenTranslator.Capture;
using ScreenTranslator.Models;
using ScreenTranslator.OCR;
using ScreenTranslator.Overlay;
using ScreenTranslator.Services;
using ScreenTranslator.Translation;
using ScreenTranslator.Translation.AI;
using ScreenTranslator.Vision;

namespace ScreenTranslator.Recognition;

public sealed class RecognitionPipeline : IAsyncDisposable
{
    private readonly IOcrEngine _ocr;
    private readonly ITranslationEngine _translation;
    private readonly IReadingOrderResolver _readingOrder;
    private readonly LocalOcrPostProcessor _postProcessor;
    private readonly OcrParagraphMerger _paragraphMerger;
    private readonly ITranslationOverlay _overlay;
    private readonly SlideRecognitionCache _cache;
    private readonly SlideChangeDetector _changes;
    private readonly SlideChangeDetector _documentChanges = new(TimeSpan.FromMilliseconds(650), .065, .035);
    private readonly AppLogger _log;
    private readonly object _sync = new();
    private CancellationTokenSource? _session;
    private CancellationTokenSource? _page;
    private SlideFingerprint? _processingFingerprint;
    private PendingPage? _pendingPage;
    private long _pendingVersion;
    private Task _worker = Task.CompletedTask;
    private long _nextFingerprint;
    private int _generation;
    private DateTimeOffset _lastRegionSeen;
    private PresentationRegionInfo? _lastPresentation;
    private SlideRecognitionResult? _latest;
    private RecognitionRuntimeState _state = RecognitionRuntimeState.Idle;
    private string _status = "待机中";
    private long _ocrRuns, _cacheHits;
    private double _lastOcrMs, _lastTranslationMs, _lastTotalMs;
    public SlideRecognitionResult? Latest => Volatile.Read(ref _latest);
    public RecognitionRuntimeState State => _state;
    public string Status => _status;
    public bool IsInitialized => _ocr.IsInitialized && _translation.IsInitialized;
    public LanguagePackMetadata? LanguagePack => _translation.LanguagePack;
    public string OcrEngineName => _ocr.Name;
    public string TranslationEngineName => _translation.Name;
    public RecognitionStatistics Statistics => new(Interlocked.Read(ref _ocrRuns), Interlocked.Read(ref _cacheHits), _lastOcrMs, _lastTranslationMs, _lastTotalMs);
    public TranslationDisplayMode DisplayMode { get; set; } = TranslationDisplayMode.ChineseOverlay;
    public double BackgroundOpacity { get; set; } = .78;
    public double FontScale { get; set; } = 1;
    public bool ShowOriginalText { get; set; }
    public InferenceDevice PreferredDevice { get; set; } = InferenceDevice.Auto;

    public RecognitionPipeline(AppLogger log, IOcrEngine? ocr = null, ITranslationEngine? translation = null,
        IReadingOrderResolver? readingOrder = null, LocalOcrPostProcessor? postProcessor = null,
        ITranslationOverlay? overlay = null, SlideRecognitionCache? cache = null, SlideChangeDetector? changes = null,
        OcrParagraphMerger? paragraphMerger = null)
    {
        _log = log; _ocr = ocr ?? new WindowsOcrEngine(); _translation = translation ?? new MarianOnnxTranslationEngine();
        _readingOrder = readingOrder ?? new RuleReadingOrderResolver(); _postProcessor = postProcessor ?? new();
        _paragraphMerger = paragraphMerger ?? new();
        _overlay = overlay ?? new WpfTranslationOverlay(); _cache = cache ?? new(); _changes = changes ?? new();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (IsInitialized) return;
        SetState(RecognitionRuntimeState.Initializing, "正在初始化本地 OCR…");
        await _ocr.InitializeAsync(PreferredDevice, cancellationToken).ConfigureAwait(false);
        SetState(RecognitionRuntimeState.Initializing, "正在准备 AI 翻译…");
        await _translation.InitializeAsync(PreferredDevice, cancellationToken).ConfigureAwait(false);
        SetState(RecognitionRuntimeState.Idle, "本地 OCR 与 AI 翻译引擎已就绪");
        _log.Info($"Recognition engines initialized: OCR={_ocr.Name}; Translation={_translation.Name}; Device={PreferredDevice}");
    }

    public void Start()
    {
        if (!IsInitialized) throw new InvalidOperationException("识别引擎尚未初始化。");
        lock (_sync)
        {
            _page?.Cancel(); _page?.Dispose(); _page = null;
            _session?.Cancel(); _session?.Dispose(); _session = new(); ++_generation;
            _changes.Reset(); _documentChanges.Reset(); ClearPendingLocked(); _nextFingerprint = 0; _lastPresentation = null; _lastRegionSeen = default; _processingFingerprint = null;
            SetState(RecognitionRuntimeState.Running, "等待屏幕内容稳定");
        }
    }
    public void Resume()
    {
        lock (_sync)
        {
            if (_session is null || _session.IsCancellationRequested)
            {
                _session?.Dispose();
                _session = new();
            }
            ++_generation;
            SetState(RecognitionRuntimeState.Running, "继续识别");
        }
    }
    public Task RefreshOverlayAsync(MonitorInfo monitor)
    {
        var result = Latest; var token = _session?.Token ?? CancellationToken.None;
        return result is null ? Task.CompletedTask : _overlay.ShowAsync(result, monitor, DisplayMode, BackgroundOpacity, FontScale, ShowOriginalText, token);
    }
    public async Task InvalidateTranslationsAsync()
    {
        CancellationTokenSource? page;
        lock (_sync)
        {
            page = _page; _page = null; _processingFingerprint = null; ++_generation;
            ClearPendingLocked(); _cache.Clear(); _changes.Reset(); _documentChanges.Reset(); _nextFingerprint = 0;
            Volatile.Write(ref _latest, null);
            if (_state is RecognitionRuntimeState.Running or RecognitionRuntimeState.Processing or RecognitionRuntimeState.RegionLost)
                SetState(RecognitionRuntimeState.Running, "翻译模式已切换，等待页面稳定");
        }
        page?.Cancel(); page?.Dispose();
        await _overlay.HideAsync().ConfigureAwait(false);
    }
    public async Task PauseAsync()
    {
        CancellationTokenSource? token, page; Task worker;
        lock (_sync) { token = _session; page = _page; _page = null; _processingFingerprint = null; ClearPendingLocked(); ++_generation; worker = _worker; SetState(RecognitionRuntimeState.Paused, "识别已暂停"); }
        page?.Cancel(); token?.Cancel(); await IgnoreCancellation(worker).ConfigureAwait(false); page?.Dispose();
    }
    public async Task StopAsync()
    {
        CancellationTokenSource? token, page; Task worker;
        lock (_sync) { token = _session; page = _page; _session = null; _page = null; _processingFingerprint = null; ClearPendingLocked(); ++_generation; worker = _worker; _nextFingerprint = 0; _changes.Reset(); _documentChanges.Reset(); }
        page?.Cancel(); token?.Cancel(); await IgnoreCancellation(worker).ConfigureAwait(false); page?.Dispose(); token?.Dispose();
        await _overlay.HideAsync().ConfigureAwait(false); Volatile.Write(ref _latest, null);
        SetState(RecognitionRuntimeState.Idle, "待机中");
    }

    public void Submit(CapturedFrame frame, MonitorInfo monitor, DetectionSnapshot? detection)
    {
        CancellationToken token; int generation;
        lock (_sync)
        {
            if (_session is null || _session.IsCancellationRequested || _state is RecognitionRuntimeState.Idle or RecognitionRuntimeState.Initializing or RecognitionRuntimeState.Paused or RecognitionRuntimeState.Faulted) return;
            token = _session.Token; generation = _generation;
        }
        if (detection?.Region is not { } region)
        {
            if (_lastRegionSeen != default && DateTimeOffset.UtcNow - _lastRegionSeen > TimeSpan.FromSeconds(1))
            { SetState(RecognitionRuntimeState.RegionLost, "可识别内容已离开，翻译已隐藏"); _ = _overlay.HideAsync(); }
            return;
        }
        _lastRegionSeen = DateTimeOffset.UtcNow;
        var presentation = new PresentationRegionInfo(region.X, region.Y, region.Width, region.Height, detection.Target?.DpiScale ?? monitor.Scale, monitor.Id);
        if (_lastPresentation is not null && CoordinateMapper.IoU(_lastPresentation.Bounds, presentation.Bounds) < .9)
        {
            lock(_sync){_changes.Reset();_documentChanges.Reset();ClearPendingLocked();}
        }
        _lastPresentation = presentation;
        if (Latest is not null) _ = _overlay.UpdateRegionAsync(presentation, monitor, token);
        if (Stopwatch.GetTimestamp() < Interlocked.Read(ref _nextFingerprint)) return;
        Interlocked.Exchange(ref _nextFingerprint, Stopwatch.GetTimestamp() + Stopwatch.Frequency / 8);
        var crop = ToLocalCrop(region.Bounds, monitor, frame.Width, frame.Height); if (crop.Width < 80 || crop.Height < 50) return;
        var fingerprint = SlideFingerprint.Create(frame.PixelData.Span, frame.Width, frame.Height, frame.Stride, crop);
        var continuousDocument = detection.Target?.ApplicationType is ApplicationType.Browser or ApplicationType.PdfViewer;
        var changeDetector = continuousDocument ? _documentChanges : _changes;
        CancellationTokenSource? changedPage = null;
        lock (_sync)
        {
            // Slides should react immediately to a real page turn. Browser documents,
            // however, contain blinking carets, selection handles and scrollbars. Let
            // SlideChangeDetector confirm a stable new document image before replacing
            // in-flight OCR/AI work; otherwise those harmless pixels can starve output.
            if (!continuousDocument && _processingFingerprint is not null && SlideFingerprint.Distance(_processingFingerprint.Signature, fingerprint.Signature) > .055)
            {
                changedPage = _page; _page = null; _processingFingerprint = null; ++_generation; generation = _generation;
                SetState(RecognitionRuntimeState.Running, "页面已经变化，旧翻译已取消");
            }
        }
        if (changedPage is not null)
        {
            changedPage.Cancel(); changedPage.Dispose(); Volatile.Write(ref _latest, null); _ = _overlay.HideAsync();
            _log.Info("Page changed while recognition was active; old OCR/AI request canceled");
        }
        SlideFingerprint? stable; TimeSpan remaining;
        lock(_sync)
        {
            stable = changeDetector.Observe(fingerprint, frame.Timestamp);
            if(stable is null)
            {
                if(changeDetector.TryGetPending(fingerprint,frame.Timestamp,out remaining))
                    QueuePendingLocked(frame,crop,fingerprint,presentation,monitor,generation,token,remaining,changeDetector);
                return;
            }
            ClearPendingLocked();
        }
        var owned = CopyCrop(frame.PixelData.Span, frame.Stride, crop);
        AdmitStable(stable,owned,crop.Width,crop.Height,presentation,monitor,generation,token,
            continuousDocument ? OcrContentKind.WebDocument : OcrContentKind.Presentation);
    }

    private void AdmitStable(SlideFingerprint stable,byte[] owned,int width,int height,PresentationRegionInfo presentation,
        MonitorInfo monitor,int generation,CancellationToken token,OcrContentKind contentKind)
    {
        if (_cache.TryGet(stable, out var cached))
        {
            ArrayPool<byte>.Shared.Return(owned);
            Interlocked.Increment(ref _cacheHits); var hit = cached with { Presentation = presentation, FromCache = true, CompletedAt = DateTimeOffset.UtcNow };
            _log.Info($"Slide cache hit: fingerprint={stable.Key[..12]}");
            Publish(hit, monitor, generation, token, "已从页面缓存恢复翻译"); return;
        }
        lock (_sync)
        {
            if(generation!=_generation||token.IsCancellationRequested){ArrayPool<byte>.Shared.Return(owned);return;}
            var previous = _worker;
            _page?.Cancel(); _page?.Dispose();
            _page = CancellationTokenSource.CreateLinkedTokenSource(token);
            _processingFingerprint = stable;
            var pageToken = _page.Token;
            SetState(RecognitionRuntimeState.Processing, "页面已稳定，正在识别英文…");
            _worker = Task.Run(async () =>
            {
                await IgnoreCancellation(previous).ConfigureAwait(false);
                if (pageToken.IsCancellationRequested) { ArrayPool<byte>.Shared.Return(owned); return; }
                await ProcessAsync(owned, width, height, stable, presentation, monitor, generation, pageToken,contentKind).ConfigureAwait(false);
            });
        }
    }

    private void QueuePendingLocked(CapturedFrame frame,Int32Rect crop,SlideFingerprint fingerprint,
        PresentationRegionInfo presentation,MonitorInfo monitor,int generation,CancellationToken token,TimeSpan remaining,
        SlideChangeDetector detector)
    {
        var owned=CopyCrop(frame.PixelData.Span,frame.Stride,crop);
        var same=_pendingPage is not null&&SlideFingerprint.Distance(_pendingPage.Fingerprint.Signature,fingerprint.Signature)<=.025;
        if(_pendingPage is not null)ArrayPool<byte>.Shared.Return(_pendingPage.Pixels);
        var version=same?_pendingVersion:++_pendingVersion;
        var kind=detector==_documentChanges?OcrContentKind.WebDocument:OcrContentKind.Presentation;
        _pendingPage=new(fingerprint,owned,crop.Width,crop.Height,presentation,monitor,generation,detector,kind);
        if(!same)_=ConfirmPendingAsync(version,remaining,token);
    }

    private async Task ConfirmPendingAsync(long version,TimeSpan remaining,CancellationToken token)
    {
        try
        {
            if(remaining>TimeSpan.Zero)await Task.Delay(remaining+TimeSpan.FromMilliseconds(20),token).ConfigureAwait(false);
            PendingPage? pending;SlideFingerprint? stable;
            lock(_sync)
            {
                if(version!=_pendingVersion||_pendingPage is null||token.IsCancellationRequested)return;
                pending=_pendingPage;
                stable=pending.Detector.ConfirmPending(DateTimeOffset.UtcNow);
                if(stable is null){ClearPendingLocked();return;}
                _pendingPage=null;++_pendingVersion;
            }
            _log.Info($"Stable page confirmed without duplicate desktop frame: fingerprint={stable.Key[..12]}");
            AdmitStable(stable,pending.Pixels,pending.Width,pending.Height,pending.Presentation,pending.Monitor,pending.Generation,token,pending.ContentKind);
        }
        catch(OperationCanceledException){}
    }

    private void ClearPendingLocked()
    {
        if(_pendingPage is not null)ArrayPool<byte>.Shared.Return(_pendingPage.Pixels);
        _pendingPage=null;++_pendingVersion;
    }

    private sealed record PendingPage(SlideFingerprint Fingerprint,byte[] Pixels,int Width,int Height,
        PresentationRegionInfo Presentation,MonitorInfo Monitor,int Generation,SlideChangeDetector Detector,OcrContentKind ContentKind);

    private async Task ProcessAsync(byte[] pixels, int width, int height, SlideFingerprint fingerprint,
        PresentationRegionInfo presentation, MonitorInfo monitor, int generation, CancellationToken token,OcrContentKind contentKind)
    {
        var total = Stopwatch.StartNew();
        IReadOnlyList<OcrBlock> blocks = [];
        try
        {
            var input = new OcrInput(pixels.AsMemory(0, width * height * 4), width, height, width * 4,contentKind);
            var ocrWatch = Stopwatch.StartNew(); blocks = await _ocr.RecognizeAsync(input, token).ConfigureAwait(false); ocrWatch.Stop();
            blocks = _readingOrder.Resolve(_postProcessor.Process(blocks), width, height);
            blocks = _readingOrder.Resolve(_paragraphMerger.Merge(blocks, width, height), width, height);
            Interlocked.Increment(ref _ocrRuns); _lastOcrMs = ocrWatch.Elapsed.TotalMilliseconds;
            if (blocks.Count == 0)
            {
                total.Stop(); _lastTranslationMs=0; _lastTotalMs=total.Elapsed.TotalMilliseconds;
                var empty=new SlideRecognitionResult(fingerprint.Key,presentation,[],false,_lastOcrMs,0,_lastTotalMs,DateTimeOffset.UtcNow);
                _cache.Put(fingerprint,empty); Publish(empty,monitor,generation,token,"当前页未识别到英文文字"); return;
            }
            var translated = new List<TranslatedBlock>(blocks.Count); var translationWatch = Stopwatch.StartNew();
            SetState(RecognitionRuntimeState.Processing, "英文识别完成，正在进行 AI 翻译…");
            if (_translation is IPageTranslationEngine pageEngine)
            {
                var responses=await pageEngine.TranslatePageAsync(blocks,"en","zh-CN",token).ConfigureAwait(false);
                if(responses.Count!=blocks.Count)throw new InvalidDataException("翻译引擎返回的文字块数量不完整。");
                for(var i=0;i<blocks.Count;i++)translated.Add(new(blocks[i],responses[i].Text,responses[i].IsPartial,responses[i].ProcessingMilliseconds));
            }
            else
            {
                var title = blocks.FirstOrDefault(b => b.BlockType == OcrBlockType.Title)?.OriginalText ?? "";
                foreach (var block in blocks)
                {
                    token.ThrowIfCancellationRequested();
                    var context = new TranslationContext(title, translated.TakeLast(3).Select(x => x.Source.OriginalText).ToArray(), new Dictionary<string, string>(), title);
                    var response = await _translation.TranslateAsync(new("en", "zh-CN", block.OriginalText, context, block.BlockType), token).ConfigureAwait(false);
                    translated.Add(new(block, response.Text, response.IsPartial, response.ProcessingMilliseconds));
                }
            }
            translationWatch.Stop(); total.Stop(); _lastTranslationMs = translationWatch.Elapsed.TotalMilliseconds; _lastTotalMs = total.Elapsed.TotalMilliseconds;
            var result = new SlideRecognitionResult(fingerprint.Key, presentation, translated, false, _lastOcrMs, _lastTranslationMs, _lastTotalMs, DateTimeOffset.UtcNow);
            _cache.Put(fingerprint, result); Publish(result, monitor, generation, token, $"AI 翻译完成 · {translated.Count} 个文字块");
            _log.Info($"Slide processed: blocks={translated.Count}, ocr={_lastOcrMs:F1}ms, translation={_lastTranslationMs:F1}ms, total={_lastTotalMs:F1}ms, fingerprint={fingerprint.Key[..12]}");
        }
        catch (OperationCanceledException) { }
        catch (AiTranslationException ex)
        {
            total.Stop(); _lastTranslationMs=0; _lastTotalMs=total.Elapsed.TotalMilliseconds;
            _log.Info($"AI translation unavailable: kind={ex.Kind}; message={ex.Message}");
            var sourceOnly=new SlideRecognitionResult(fingerprint.Key,presentation,
                blocks.Select(block => new TranslatedBlock(block, string.Empty, true, 0)).ToArray(),false,_lastOcrMs,0,_lastTotalMs,DateTimeOffset.UtcNow);
            lock(_sync)if(generation==_generation&&!token.IsCancellationRequested){Volatile.Write(ref _latest,sourceOnly);SetState(RecognitionRuntimeState.Running,ex.Message);}
            await _overlay.HideAsync(token).ConfigureAwait(false);
        }
        catch (OutOfMemoryException ex) { Fail("内存不足，已停止当前页面处理。", ex); }
        catch (Exception ex) { Fail("识别或 AI 翻译失败，请查看日志。", ex); }
        finally
        {
            lock(_sync)if(generation==_generation)_processingFingerprint=null;
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }
    private void Publish(SlideRecognitionResult result, MonitorInfo monitor, int generation, CancellationToken token, string status)
    {
        lock (_sync) if (generation != _generation || token.IsCancellationRequested) return;
        Volatile.Write(ref _latest, result); SetState(RecognitionRuntimeState.Running, status);
        _ = _overlay.ShowAsync(result, monitor, DisplayMode, BackgroundOpacity, FontScale, ShowOriginalText, token);
    }
    private void Fail(string message, Exception ex) { _log.Error(message, ex); SetState(RecognitionRuntimeState.Faulted, message); _ = _overlay.HideAsync(); }
    private void SetState(RecognitionRuntimeState state, string status) { _state = state; _status = status; }
    private static Int32Rect ToLocalCrop(System.Windows.Rect absolute, MonitorInfo monitor, int width, int height)
    {
        var x = Math.Clamp((int)Math.Round(absolute.X - monitor.Left), 0, width); var y = Math.Clamp((int)Math.Round(absolute.Y - monitor.Top), 0, height);
        var right = Math.Clamp((int)Math.Round(absolute.Right - monitor.Left), x, width); var bottom = Math.Clamp((int)Math.Round(absolute.Bottom - monitor.Top), y, height);
        return new(x, y, right - x, bottom - y);
    }
    private static byte[] CopyCrop(ReadOnlySpan<byte> source, int sourceStride, Int32Rect crop)
    {
        var stride = checked(crop.Width * 4); var result = ArrayPool<byte>.Shared.Rent(checked(stride * crop.Height));
        for (var y = 0; y < crop.Height; y++) source.Slice((crop.Y + y) * sourceStride + crop.X * 4, stride).CopyTo(result.AsSpan(y * stride, stride));
        return result;
    }
    private static async Task IgnoreCancellation(Task task) { try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { } }
    public async ValueTask DisposeAsync() { await StopAsync(); await _overlay.DisposeAsync(); await _ocr.DisposeAsync(); await _translation.DisposeAsync(); }
}
