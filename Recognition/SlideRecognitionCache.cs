using ScreenTranslator.Models;

namespace ScreenTranslator.Recognition;

public sealed class SlideRecognitionCache(int capacity = 64)
{
    private readonly int _capacity = Math.Max(4, capacity);
    private readonly LinkedList<Entry> _lru = new();
    private readonly object _sync = new();
    public int Count { get { lock (_sync) return _lru.Count; } }
    public bool TryGet(SlideFingerprint fingerprint, out SlideRecognitionResult result)
    {
        lock (_sync)
        {
            var match = _lru.FirstOrDefault(x => SlideFingerprint.Distance(x.Fingerprint.Signature, fingerprint.Signature) <= .018);
            if (match is null) { result = null!; return false; }
            _lru.Remove(match); _lru.AddFirst(match); result = match.Result; return true;
        }
    }
    public void Put(SlideFingerprint fingerprint, SlideRecognitionResult result)
    {
        lock (_sync)
        {
            var old = _lru.FirstOrDefault(x => x.Fingerprint.Key == fingerprint.Key); if (old is not null) _lru.Remove(old);
            _lru.AddFirst(new Entry(fingerprint, result)); while (_lru.Count > _capacity) _lru.RemoveLast();
        }
    }
    public void Clear() { lock (_sync) _lru.Clear(); }
    private sealed record Entry(SlideFingerprint Fingerprint, SlideRecognitionResult Result);
}
