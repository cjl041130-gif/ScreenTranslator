using System.Buffers;
using System.Diagnostics;
using ScreenTranslator.Capture;
using ScreenTranslator.Services;
namespace ScreenTranslator.Vision;

// Single in-flight owned frame; admission drops frames while busy. Never retain capture-owned memory.
public sealed class VisionPipeline : IAsyncDisposable
{
    private readonly VisionSettings _settings;
    private readonly ITargetWindowTracker _windows;
    private readonly IPresentationDetector _detector;
    private readonly AppLogger _log;
    private Task _worker = Task.CompletedTask;
    private volatile bool _running;
    private int _generation;
    private long _next;
    private DetectionSnapshot? _latest;
    private long _completed;
    public DetectionSnapshot? Latest => Volatile.Read(ref _latest);
    public long CompletedDetections => Interlocked.Read(ref _completed);
    public bool IsProcessing => !_worker.IsCompleted;
    public VisionPipeline(VisionSettings settings, AppLogger log, ITargetWindowTracker? windows = null, IPresentationDetector? detector = null)
    {
        _settings = settings; settings.Normalize(); _log = log;
        _windows = windows ?? new TargetWindowTracker(settings); _detector = detector ?? new PresentationDetector(settings);
        _windows.Start();
    }
    public void Start() { _windows.Start(); _running = true; _next = 0; }
    public async Task PauseAsync() { _running = false; ++_generation; await _worker; }
    public async Task StopAsync()
    {
        _running = false; ++_generation; _windows.Stop(); await _worker;
        _detector.Reset(); Volatile.Write(ref _latest, null); _next = 0;
    }
    // Called from the existing preview consumer. Only the admitted frame is copied (2–4 times/sec).
    public void Submit(CapturedFrame source, MonitorInfo monitor)
    {
        if (!_running || !_worker.IsCompleted || Stopwatch.GetTimestamp() < _next) return;
        var fps = Latest?.Region is null ? _settings.DetectionFPS : _settings.StableDetectionFPS;
        _next = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency / fps);
        var owned = new CapturedFrame(source.Width, source.Height, source.MonitorId, ArrayPool<byte>.Shared);
        source.PixelData.Span.CopyTo(owned.Buffer);
        var generation = _generation;
        _worker = Task.Run(() =>
        {
            using (owned)
            {
                try
                {
                    var target = _windows.SelectTarget(monitor);
                    var result = _detector.Detect(owned, monitor, target);
                    Interlocked.Increment(ref _completed);
                    if (generation == Volatile.Read(ref _generation)) Volatile.Write(ref _latest, result);
                }
                catch (Exception ex)
                {
                    // Disable admission on failure, so a missing native dependency cannot flood logs.
                    _running = false; _detector.Reset(); _log.Error("Vision detection failed", ex);
                    if (generation == Volatile.Read(ref _generation)) Volatile.Write(ref _latest, new(null, System.Windows.Rect.Empty, [], null, 0, DateTimeOffset.UtcNow, ex.Message));
                }
            }
        });
    }
    public async ValueTask DisposeAsync() { await StopAsync(); _windows.Dispose(); }
}
