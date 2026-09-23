using System.Text.Json;

namespace ScreenTranslator.Translation.AI;

public sealed class AiTranslationResultValidator
{
    public IReadOnlyList<AiTranslatedItem> Validate(AiPageTranslationRequest request, string output)
    {
        if (string.IsNullOrWhiteSpace(output)) throw Invalid("AI 返回了空结果。");
        var trimmed = output.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal)) throw Invalid("AI 返回了 Markdown，而不是结构化翻译。");
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (!document.RootElement.TryGetProperty("translations", out var values) || values.ValueKind != JsonValueKind.Array)
                throw Invalid("AI 返回内容缺少 translations。 ");
            var items = new List<AiTranslatedItem>();
            foreach (var value in values.EnumerateArray())
            {
                var id = value.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                var text = value.TryGetProperty("translated_text", out var textValue) ? textValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(text)) throw Invalid("AI 返回了空 ID 或空翻译。");
                items.Add(new(id, text.Trim()));
            }
            if (items.Count != request.Blocks.Count) throw Invalid("AI 返回的文字块数量不完整。");
            if (items.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != items.Count) throw Invalid("AI 返回了重复 ID。");
            var expected = request.Blocks.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            if (items.Any(x => !expected.Contains(x.Id))) throw Invalid("AI 返回了未知 ID。");
            var byId = items.ToDictionary(x => x.Id, StringComparer.Ordinal);
            if (request.Blocks.Any(x => !byId.ContainsKey(x.Id))) throw Invalid("AI 遗漏了文字块。");
            var substantialTextBlocks = 0;
            var suspiciousEnglishBlocks = 0;
            foreach (var block in request.Blocks)
            {
                var translated = byId[block.Id].TranslatedText;
                if (translated.Length > Math.Max(180, block.Text.Length * 6)) throw Invalid("AI 返回文本长度异常。");
                if (translated.StartsWith("以下是", StringComparison.OrdinalIgnoreCase) || translated.StartsWith("翻译如下", StringComparison.OrdinalIgnoreCase) ||
                    translated.StartsWith("here is", StringComparison.OrdinalIgnoreCase) || translated.Contains("作为AI", StringComparison.OrdinalIgnoreCase))
                    throw Invalid("AI 返回了额外说明。");
                if (!IsTechnicalBlock(block.Type))
                {
                    var latin=translated.Count(char.IsAsciiLetter);var cjk=translated.Count(c=>c is >= '\u3400' and <= '\u9fff');
                    var sourceLatin=block.Text.Count(char.IsAsciiLetter);
                    if(sourceLatin>=20&&HasSeveralWords(block.Text))substantialTextBlocks++;
                    if(sourceLatin>=20&&cjk==0&&latin>=20&&latin>=translated.Length*.6&&HasSeveralWords(translated))
                        suspiciousEnglishBlocks++;
                }
            }
            // A product name, course title or library name can legitimately remain
            // English.  Reject the response only when untranslated English affects
            // most substantive blocks; one label must never discard an otherwise
            // complete page or trigger a second multi-second whole-page request.
            var suspiciousLimit=substantialTextBlocks<=1?1:Math.Max(2,(int)Math.Ceiling(substantialTextBlocks*.6));
            if(suspiciousEnglishBlocks>=suspiciousLimit)
                throw Invalid("AI 返回的大部分正文仍为英文。");
            return request.Blocks.Select(x => byId[x.Id]).ToArray();
        }
        catch (JsonException ex) { throw new AiTranslationException(AiTranslationErrorKind.InvalidResponse, "AI 返回格式无效，本页暂未覆盖翻译。", ex); }
    }

    private static AiTranslationException Invalid(string detail) =>
        new(AiTranslationErrorKind.InvalidResponse, $"AI 返回格式无效，本页暂未覆盖翻译。{detail}");
    private static bool IsTechnicalBlock(string type)=>type.Equals("code",StringComparison.OrdinalIgnoreCase)||type.Equals("formula",StringComparison.OrdinalIgnoreCase);
    private static bool HasSeveralWords(string text)=>text.Split([' ','\t','\r','\n'],StringSplitOptions.RemoveEmptyEntries).Length>=3;
}
