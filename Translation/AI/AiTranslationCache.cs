using System.Security.Cryptography;
using System.Text;

namespace ScreenTranslator.Translation.AI;

public sealed class AiTranslationCache
{
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<(string Key, string[] Values)>> _items = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, string[] Values)> _lru = new();
    private readonly object _gate = new();
    public long Hits { get; private set; }
    public AiTranslationCache(int capacity = 128) => _capacity = Math.Clamp(capacity, 8, 512);

    public string CreateKey(AiPageTranslationRequest request, string provider = "DeepSeek")
    {
        var material = new StringBuilder("provider=").Append(provider.ToLowerInvariant()).Append("\nprompt=ai-page-v2\n").Append(request.Model).Append('\n')
            .Append(request.SourceLanguage).Append('\n').Append(request.TargetLanguage).Append('\n').Append(request.Style).Append('\n');
        foreach (var block in request.Blocks) material.Append(block.Id).Append('\t').Append(block.Type).Append('\t').Append(block.Text).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())));
    }

    public bool TryGet(string key, out string[] values)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(key, out var node)) { values = []; return false; }
            _lru.Remove(node); _lru.AddFirst(node); Hits++; values = [.. node.Value.Values]; return true;
        }
    }

    public void Put(string key, IReadOnlyList<string> values)
    {
        lock (_gate)
        {
            if (_items.Remove(key, out var old)) _lru.Remove(old);
            var node = _lru.AddFirst((key, values.ToArray())); _items[key] = node;
            while (_items.Count > _capacity && _lru.Last is { } last) { _items.Remove(last.Value.Key); _lru.RemoveLast(); }
        }
    }
}
