using ScreenTranslator.Core;

namespace ScreenTranslator.Capture;

public interface IScreenCaptureService : IAsyncDisposable
{
    ApplicationState State { get; }
    string Status { get; }
    double FramesPerSecond { get; }
    long TotalFrames { get; }
    Task StartAsync(MonitorInfo monitor, int targetFps, CancellationToken cancellationToken = default);
    Task PauseAsync();
    Task StopAsync();
    /// <summary>Transfers ownership of the latest frame to the caller, who must Dispose it.
    /// Only one consumer is supported; superseded frames are disposed automatically.</summary>
    CapturedFrame? TakeLatestFrame();
}
