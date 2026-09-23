using System.Buffers;

namespace ScreenTranslator.Capture;

/// <summary>Owns pooled BGRA32 pixels, tightly packed, top-down. Dispose exactly once after use.
/// PixelData is valid only until Dispose; do not retain it separately from its frame.</summary>
public sealed class CapturedFrame : IDisposable
{
    private byte[]? _pixels;
    private readonly ArrayPool<byte> _pool;
    public DateTimeOffset Timestamp { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride => checked(Width * 4);
    public string MonitorId { get; }
    public ReadOnlyMemory<byte> PixelData => (_pixels ?? throw new ObjectDisposedException(nameof(CapturedFrame))).AsMemory(0, checked(Stride * Height));
    internal byte[] Buffer => _pixels ?? throw new ObjectDisposedException(nameof(CapturedFrame));
    internal CapturedFrame(int width, int height, string monitorId, ArrayPool<byte> pool)
    {
        Width = width; Height = height; MonitorId = monitorId; Timestamp = DateTimeOffset.UtcNow;
        _pool = pool; _pixels = pool.Rent(checked(Stride * Height));
    }
    public void Dispose()
    {
        var pixels = Interlocked.Exchange(ref _pixels, null);
        if (pixels is not null) _pool.Return(pixels);
    }
}
