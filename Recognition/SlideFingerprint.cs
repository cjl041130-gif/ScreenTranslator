using System.Security.Cryptography;
using System.Windows;

namespace ScreenTranslator.Recognition;

public sealed record SlideFingerprint(string Key, byte[] Signature)
{
    public static double Distance(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length || a.Length == 0) return 1;
        long sum = 0; for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / (255d * a.Length);
    }
    public static SlideFingerprint Create(ReadOnlySpan<byte> pixels, int width, int height, int stride, Int32Rect region)
    {
        const int columns = 16, rows = 9; var signature = new byte[columns * rows];
        for (var gy = 0; gy < rows; gy++)
        for (var gx = 0; gx < columns; gx++)
        {
            long total = 0; var samples = 0;
            var x0 = region.X + gx * region.Width / columns; var x1 = region.X + (gx + 1) * region.Width / columns;
            var y0 = region.Y + gy * region.Height / rows; var y1 = region.Y + (gy + 1) * region.Height / rows;
            var stepX = Math.Max(1, (x1 - x0) / 8); var stepY = Math.Max(1, (y1 - y0) / 5);
            for (var y = y0; y < y1; y += stepY)
            for (var x = x0; x < x1; x += stepX)
            {
                var p = y * stride + x * 4; total += (pixels[p + 2] * 77 + pixels[p + 1] * 150 + pixels[p] * 29) >> 8; samples++;
            }
            signature[gy * columns + gx] = (byte)Math.Clamp((int)Math.Round((double)total / Math.Max(1, samples) / 8) * 8, 0, 255);
        }
        return new(Convert.ToHexString(SHA256.HashData(signature)), signature);
    }
}

public sealed class SlideChangeDetector(TimeSpan? stableDelay = null, double changeThreshold = .055, double similarityThreshold = .025)
{
    private readonly TimeSpan _stableDelay = stableDelay ?? TimeSpan.FromMilliseconds(350);
    private SlideFingerprint? _current, _candidate;
    private DateTimeOffset _candidateSince;
    private int _candidateCount;
    public SlideFingerprint? Observe(SlideFingerprint observed, DateTimeOffset now)
    {
        if (_current is not null && SlideFingerprint.Distance(_current.Signature, observed.Signature) <= changeThreshold)
        {
            // A caret blink, hover highlight or selection handle returned to the
            // current page. Discard the transient candidate before its delayed
            // confirmation can start another OCR pass.
            _candidate = null; _candidateCount = 0; return null;
        }
        if (_candidate is null || SlideFingerprint.Distance(_candidate.Signature, observed.Signature) > similarityThreshold)
        { _candidate = observed; _candidateSince = now; _candidateCount = 1; return null; }
        _candidate = observed; _candidateCount++;
        if (_candidateCount < 2 || now - _candidateSince < _stableDelay) return null;
        _current = observed; _candidate = null; _candidateCount = 0; return observed;
    }
    public bool TryGetPending(SlideFingerprint observed, DateTimeOffset now, out TimeSpan remaining)
    {
        if (_candidate is null || SlideFingerprint.Distance(_candidate.Signature, observed.Signature) > similarityThreshold)
        { remaining = TimeSpan.Zero; return false; }
        remaining = _stableDelay - (now - _candidateSince);
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        return true;
    }
    public SlideFingerprint? ConfirmPending(DateTimeOffset now)
    {
        if (_candidate is null || now - _candidateSince < _stableDelay) return null;
        _current = _candidate; _candidate = null; _candidateCount = 0; return _current;
    }
    public void Reset() { _current = null; _candidate = null; _candidateCount = 0; }
}
