namespace ScreenTranslator.Translation;

internal sealed class TranslationMemoryCache(int capacity = 512)
{
    private readonly int _capacity = Math.Max(16, capacity);
    private readonly Dictionary<string, LinkedListNode<(string Key, TranslationResponse Value)>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, TranslationResponse Value)> _lru = new();
    private readonly object _sync = new();
    private long _hits;

    public long Hits => Interlocked.Read(ref _hits);

    public bool TryGet(string key, out TranslationResponse response)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                response = default!;
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            response = node.Value.Value with { ProcessingMilliseconds = 0 };
            Interlocked.Increment(ref _hits);
            return true;
        }
    }

    public void Put(string key, TranslationResponse response)
    {
        lock (_sync)
        {
            if (_entries.Remove(key, out var existing)) _lru.Remove(existing);
            var node = _lru.AddFirst((key, response));
            _entries[key] = node;
            while (_entries.Count > _capacity)
            {
                var last = _lru.Last!;
                _entries.Remove(last.Value.Key);
                _lru.RemoveLast();
            }
        }
    }

    public void Clear()
    {
        lock (_sync) { _entries.Clear(); _lru.Clear(); }
    }
}
