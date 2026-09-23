namespace ScreenTranslator.Translation;

public sealed class TerminologyManager
{
    private readonly Dictionary<string, string> _terms = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> Terms => _terms;
    public void Load(IEnumerable<KeyValuePair<string, string>> terms)
    {
        foreach (var term in terms.OrderByDescending(t => t.Key.Length)) _terms[term.Key] = term.Value;
    }
    public string Apply(string text, out bool changed)
    {
        changed = false; var value = text;
        foreach (var pair in _terms.OrderByDescending(x => x.Key.Length))
        {
            var replaced = System.Text.RegularExpressions.Regex.Replace(value, $"\\b{System.Text.RegularExpressions.Regex.Escape(pair.Key)}\\b", pair.Value, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (replaced != value) changed = true;
            value = replaced;
        }
        return value;
    }
}
