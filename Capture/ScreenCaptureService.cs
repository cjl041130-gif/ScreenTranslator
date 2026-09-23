using System.Buffers;
using System.Diagnostics;
using ScreenTranslator.Core;
using ScreenTranslator.Services;

namespace ScreenTranslator.Capture;

public sealed class ScreenCaptureService(AppLogger log) : IScreenCaptureService
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Create(64 * 1024 * 1024, 3);
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private CapturedFrame? _latest;
    private volatile ApplicationState _state = ApplicationState.Stopped;
    private volatile string _status = "Stopped";
    private double _fps;
    private long _totalFrames;
    private bool _disposed;
    public ApplicationState State => _state;
    public string Status => _status;
    public double FramesPerSecond => Volatile.Read(ref _fps);
    public long TotalFrames => Interlocked.Read(ref _totalFrames);
    public CapturedFrame? TakeLatestFrame() => Interlocked.Exchange(ref _latest, null);

    public async Task StartAsync(MonitorInfo monitor, int targetFps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (targetFps is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(targetFps));
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopWorkerAsync();
            cancellationToken.ThrowIfCancellationRequested();
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cancellation.Token;
            Interlocked.Exchange(ref _totalFrames, 0);
            SetState(ApplicationState.Starting, "Starting capture…");
            // LongRunning gives the D3D context a single dedicated owner; no UI or thread-pool blocking.
            _worker = Task.Factory.StartNew(() => CaptureLoop(monitor, targetFps, token),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            log.Info($"Capture start: monitor={monitor.Id}, targetFPS={targetFps}");
        }
        finally { _lifecycle.Release(); }
    }

    public async Task PauseAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed || State is ApplicationState.Stopped or ApplicationState.Paused) return;
            await StopWorkerAsync();
            SetState(ApplicationState.Paused, "Paused · 点击 Start Capture 继续");
            log.Info("Capture paused; GPU session released");
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            await StopWorkerAsync();
            SetState(ApplicationState.Stopped, "Stopped");
            log.Info("Capture stopped; GPU session and pending frame released");
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StopWorkerAsync()
    {
        _cancellation?.Cancel();
        if (_worker is not null) await _worker.ConfigureAwait(false);
        _worker = null;
        _cancellation?.Dispose(); _cancellation = null;
        TakeLatestFrame()?.Dispose();
        Volatile.Write(ref _fps, 0);
    }

    private void CaptureLoop(MonitorInfo selected, int targetFps, CancellationToken token)
    {
        var failures = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var current = new MonitorService(log).GetMonitors().FirstOrDefault(m => m.Id == selected.Id)
                        ?? throw new InvalidOperationException("所选显示器已断开。请重新连接或停止后选择其他显示器。");
                    using var session = new WindowsGraphicsCaptureSession(current);
                    SetState(ApplicationState.Capturing, "Capturing · FPS 为实际获取的新帧数；静止桌面会降低");
                    var clock = Stopwatch.StartNew();
                    var next = 0.0; var sampleStart = 0.0; var loggedAt = 0.0; var frameCount = 0;
                    while (!token.IsCancellationRequested)
                    {
                        var frame = session.TryCapture(_pool);
                        if (frame is not null)
                        {
                            Interlocked.Exchange(ref _latest, frame)?.Dispose();
                            Interlocked.Increment(ref _totalFrames);
                            frameCount++; failures = 0;
                        }
                        var now = clock.Elapsed.TotalSeconds;
                        if (now - sampleStart >= 1)
                        {
                            Volatile.Write(ref _fps, frameCount / (now - sampleStart));
                            frameCount = 0; sampleStart = now;
                        }
                        if (now - loggedAt >= 10)
                        {
                            log.Info($"FPS={FramesPerSecond:F1}; totalFrames={TotalFrames}; monitor={current.Id}");
                            loggedAt = now;
                        }
                        next += 1.0 / targetFps;
                        var remaining = next - clock.Elapsed.TotalSeconds;
                        if (remaining > 0) token.WaitHandle.WaitOne(TimeSpan.FromSeconds(remaining));
                        else next = clock.Elapsed.TotalSeconds; // Do not accumulate a catch-up queue.
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    failures++;
                    var deviceLost = ex.HResult is unchecked((int)0x887A0026) or unchecked((int)0x887A0005) or unchecked((int)0x887A0007);
                    log.Error($"{(deviceLost ? "Device lost / access lost" : "Capture error")}; recovery {failures}/5", ex);
                    TakeLatestFrame()?.Dispose(); Volatile.Write(ref _fps, 0);
                    if (failures >= 5)
                    {
                        SetState(ApplicationState.Faulted, $"Capture error: {ex.Message} · 请 Stop 后 Refresh 并重试");
                        return;
                    }
                    SetState(ApplicationState.Recovering, $"Recovering {failures}/5 · {ex.Message}");
                    token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                }
            }
        }
        catch (Exception ex) { log.Error("Capture worker exception", ex); SetState(ApplicationState.Faulted, ex.Message); }
        finally { Volatile.Write(ref _fps, 0); }
    }

    private void SetState(ApplicationState state, string status) { _status = status; _state = state; }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopWorkerAsync();
            SetState(ApplicationState.Stopped, "Stopped");
        }
        finally { _lifecycle.Release(); }
    }
}
