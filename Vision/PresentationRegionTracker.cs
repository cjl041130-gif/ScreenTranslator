using System.Windows;
namespace ScreenTranslator.Vision;

public sealed class PresentationRegionTracker(VisionSettings settings)
{
    private PresentationRegion? _stable;
    private Rect _pending = Rect.Empty;
    private int _confirmations;
    private DateTimeOffset _lastSeen;
    private readonly Queue<Rect> _history = new();
    public Rect Bounds => _stable?.Bounds ?? Rect.Empty;
    public void Reset() { _stable = null; _pending = Rect.Empty; _confirmations = 0; _history.Clear(); }
    public PresentationRegion? Update(PresentationCandidate? candidate, ApplicationWindowInfo target, DateTimeOffset now)
    {
        if (candidate?.Scores is not { } scores || scores.FinalScore < settings.MinimumConfidence)
        {
            if ((now - _lastSeen).TotalSeconds >= settings.LostRegionSeconds) Reset();
            return _stable;
        }
        var rect = candidate.Bounds;
        if (_stable is not null && CoordinateMapper.IoU(rect, _stable.Bounds) >= settings.StableIoUThreshold)
        {
            var a = settings.SmoothingAlpha; var old = _stable.Bounds;
            rect = new(old.X + a * (rect.X - old.X), old.Y + a * (rect.Y - old.Y), old.Width + a * (rect.Width - old.Width), old.Height + a * (rect.Height - old.Height));
            _pending = Rect.Empty; _confirmations = 0;
        }
        else
        {
            _confirmations = CoordinateMapper.IoU(rect, _pending) >= settings.StableIoUThreshold ? _confirmations + 1 : 1;
            _pending = rect;
            var required = _stable is null && scores.FinalScore >= settings.ImmediateConfidence ? 1 :
                _stable is not null && scores.FinalScore > _stable.Confidence + settings.SwitchConfidenceMargin ? 2 : settings.StableFramesRequired;
            if (_confirmations < required)
            {
                if ((now - _lastSeen).TotalSeconds >= settings.LostRegionSeconds) _stable = null;
                return _stable;
            }
            _history.Clear();
        }
        _lastSeen = now; _history.Enqueue(rect); while (_history.Count > 15) _history.Dequeue();
        _stable = new(rect.X, rect.Y, rect.Width, rect.Height, scores.FinalScore, candidate.Method, target.Handle, target.MonitorId, now);
        return _stable;
    }
}
