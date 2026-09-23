using System.IO;
using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace ScreenTranslator.Translation;

internal sealed class MarianTokenizerAdapter : IDisposable
{
    private const int EosTokenId = 0;
    private const int UnknownTokenId = 1;
    private readonly FileStream _sourceStream;
    private readonly FileStream _targetStream;
    private readonly SentencePieceTokenizer _source;
    private readonly SentencePieceTokenizer _target;
    private readonly Dictionary<string, int> _modelVocabulary;
    private readonly Dictionary<int, string> _reverseVocabulary;
    private readonly Dictionary<string, int> _targetPieceIds;

    public MarianTokenizerAdapter(string packDirectory)
    {
        _sourceStream = File.OpenRead(Path.Combine(packDirectory, "source.spm"));
        _targetStream = File.OpenRead(Path.Combine(packDirectory, "target.spm"));
        _source = SentencePieceTokenizer.Create(_sourceStream, addBeginningOfSentence: false, addEndOfSentence: false);
        _target = SentencePieceTokenizer.Create(_targetStream, addBeginningOfSentence: false, addEndOfSentence: false);
        _modelVocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(
            File.ReadAllText(Path.Combine(packDirectory, "vocab.json")))
            ?? throw new InvalidDataException("语言包词表格式无效。");
        _reverseVocabulary = _modelVocabulary.ToDictionary(x => x.Value, x => x.Key);
        _targetPieceIds = _target.Vocabulary.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    }

    public long[] Encode(string text)
    {
        var pieces = _source.EncodeToTokens(text, out _, addBeginningOfSentence: false, addEndOfSentence: false);
        var ids = new long[pieces.Count + 1];
        for (var i = 0; i < pieces.Count; i++)
            ids[i] = _modelVocabulary.GetValueOrDefault(pieces[i].Value, UnknownTokenId);
        ids[^1] = EosTokenId;
        return ids;
    }

    public string Decode(IEnumerable<int> ids)
    {
        var internalIds = new List<int>();
        foreach (var id in ids)
        {
            if (id == EosTokenId || id == 65000) continue;
            if (!_reverseVocabulary.TryGetValue(id, out var piece)) continue;
            if (_targetPieceIds.TryGetValue(piece, out var internalId)) internalIds.Add(internalId);
        }
        return _target.Decode(internalIds).Trim();
    }

    public void Dispose()
    {
        _sourceStream.Dispose();
        _targetStream.Dispose();
    }
}
