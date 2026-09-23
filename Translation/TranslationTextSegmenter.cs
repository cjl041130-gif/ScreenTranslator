using System.Text;
using System.Text.RegularExpressions;

namespace ScreenTranslator.Translation;

internal static partial class TranslationTextSegmenter
{
    private const int MaxCharacters = 320;

    public static IReadOnlyList<string> Split(string text)
    {
        var normalized = Whitespace().Replace(text.Replace("\r", " ").Replace("\n", " "), " ").Trim();
        if (normalized.Length <= MaxCharacters) return normalized.Length == 0 ? [] : [normalized];

        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var sentence in SentenceBoundary().Split(normalized).Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var value = sentence.Trim();
            if (current.Length > 0 && current.Length + value.Length + 1 > MaxCharacters)
            {
                result.Add(current.ToString());
                current.Clear();
            }

            while (value.Length > MaxCharacters)
            {
                var split = value.LastIndexOf(' ', MaxCharacters);
                if (split < MaxCharacters / 2) split = MaxCharacters;
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                result.Add(value[..split].Trim());
                value = value[split..].Trim();
            }

            if (value.Length == 0) continue;
            if (current.Length > 0) current.Append(' ');
            current.Append(value);
        }

        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?<=[.!?;:])\s+")]
    private static partial Regex SentenceBoundary();
}
