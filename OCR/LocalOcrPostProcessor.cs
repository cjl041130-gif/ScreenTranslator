using System.Text.RegularExpressions;
using ScreenTranslator.Models;

namespace ScreenTranslator.OCR;

public sealed partial class LocalOcrPostProcessor
{
    private static readonly IReadOnlyDictionary<string, string> ObviousWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Artificia1"] = "Artificial", ["lntelligence"] = "Intelligence", ["MachineLearning"] = "Machine Learning",
        ["Neura1"] = "Neural", ["Netvvork"] = "Network"
    };
    public IReadOnlyList<OcrBlock> Process(IReadOnlyList<OcrBlock> blocks) => blocks.Select(b => b with { OriginalText = Clean(b.OriginalText) }).ToArray();
    public string Clean(string text)
    {
        var value = HyphenBreak().Replace(text, "$1$2");
        value = WhiteSpace().Replace(value, " ").Trim();
        value = SpaceBeforePunctuation().Replace(value, "$1");
        foreach (var pair in ObviousWords) value = Regex.Replace(value, $"\\b{Regex.Escape(pair.Key)}\\b", pair.Value, RegexOptions.IgnoreCase);
        return value;
    }
    [GeneratedRegex("([A-Za-z])-\\s*\\r?\\n\\s*([A-Za-z])")]
    private static partial Regex HyphenBreak();
    [GeneratedRegex("\\s+")]
    private static partial Regex WhiteSpace();
    [GeneratedRegex("\\s+([,.;:!?])")]
    private static partial Regex SpaceBeforePunctuation();
}
