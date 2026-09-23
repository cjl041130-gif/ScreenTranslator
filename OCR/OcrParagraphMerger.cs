using System.Text.RegularExpressions;
using System.Windows;
using ScreenTranslator.Models;

namespace ScreenTranslator.OCR;

/// <summary>
/// Reassembles OCR visual lines that belong to the same logical paragraph.
/// PaddleOCR deliberately recognizes one visual line at a time; translation,
/// however, needs the complete sentence to preserve grammar and context.
/// </summary>
public sealed partial class OcrParagraphMerger
{
    public IReadOnlyList<OcrBlock> Merge(IReadOnlyList<OcrBlock> orderedBlocks, int pageWidth, int pageHeight)
    {
        if (orderedBlocks.Count < 2) return Reindex(orderedBlocks);

        var medianHeight = Median(orderedBlocks.Where(x => x.Height > 0).Select(x => x.Height));
        if (medianHeight <= 0) medianHeight = Math.Max(12, pageHeight * .025);

        var merged = new List<OcrBlock>(orderedBlocks.Count);
        foreach (var current in orderedBlocks)
        {
            if (merged.Count > 0 && CanMerge(merged[^1], current, pageWidth, medianHeight))
                merged[^1] = Combine(merged[^1], current);
            else
                merged.Add(current);
        }
        return Reindex(merged);
    }

    private static bool CanMerge(OcrBlock previous, OcrBlock current, int pageWidth, double medianHeight)
    {
        if (!CompatibleTypes(previous, current)) return false;
        if (StartsListItem(current.OriginalText)) return false;

        var verticalGap = current.Y - (previous.Y + previous.Height);
        // Once several visual lines have been merged, previous.Height spans the whole
        // paragraph. Using that accumulated height as the next-line tolerance causes a
        // following paragraph to be swallowed. Cap it near the page's median line height.
        var previousLineHeight = Math.Min(previous.Height, medianHeight * 1.5);
        var lineHeight = Math.Max(medianHeight, Math.Max(previousLineHeight, current.Height));
        if (verticalGap < -lineHeight * .30) return false;

        // A wrapped line is normally close to the preceding baseline.  Completed
        // sentences get a tighter limit so separate paragraphs stay separate.
        var maxGap = EndsSentence(previous.OriginalText) ? lineHeight * .58 : lineHeight * .92;
        if (verticalGap > maxGap) return false;

        var minWidth = Math.Max(1, Math.Min(previous.Width, current.Width));
        var overlap = Math.Max(0, Math.Min(previous.X + previous.Width, current.X + current.Width) - Math.Max(previous.X, current.X));
        var overlapRatio = overlap / minWidth;
        var leftDelta = Math.Abs(previous.X - current.X);
        var aligned = overlapRatio >= .42 || leftDelta <= Math.Max(pageWidth * .035, medianHeight * 1.7);
        if (!aligned) return false;

        // A continuation of a bullet may be indented, but it must stay inside the
        // bullet's horizontal span.  Two independent bullets are never combined.
        if (previous.BlockType == OcrBlockType.Bullet)
            return current.BlockType is OcrBlockType.Body or OcrBlockType.Subtitle or OcrBlockType.Unknown && current.X >= previous.X - medianHeight &&
                   current.X <= previous.X + previous.Width * .55;

        if (current.BlockType == OcrBlockType.Bullet) return false;
        return true;
    }

    private static bool CompatibleTypes(OcrBlock previous, OcrBlock current)
    {
        var previousType = previous.BlockType;
        var currentType = current.BlockType;
        if (previousType is OcrBlockType.Code or OcrBlockType.Formula or OcrBlockType.Table or OcrBlockType.ChartLabel or OcrBlockType.Footer) return false;
        if (currentType is OcrBlockType.Code or OcrBlockType.Formula or OcrBlockType.Table or OcrBlockType.ChartLabel or OcrBlockType.Footer) return false;
        if (previousType == OcrBlockType.Bullet) return currentType is OcrBlockType.Body or OcrBlockType.Subtitle or OcrBlockType.Unknown;
        if (previousType is OcrBlockType.Title or OcrBlockType.Subtitle)
        {
            if (currentType == previousType) return true;
            // Large body text near the top of a slide can be labelled as Title or
            // Subtitle by a geometry-only classifier. A real title is normally
            // short; a long unfinished line followed immediately by another text
            // line is a wrapped paragraph and must retain its sentence context.
            return previous.OriginalText.Length >= 35 && !EndsSentence(previous.OriginalText) &&
                   currentType is OcrBlockType.Title or OcrBlockType.Subtitle or OcrBlockType.Body or OcrBlockType.Unknown;
        }
        return previousType is OcrBlockType.Body or OcrBlockType.Caption or OcrBlockType.Unknown &&
               currentType is OcrBlockType.Body or OcrBlockType.Caption or OcrBlockType.Unknown;
    }

    private static OcrBlock Combine(OcrBlock first, OcrBlock next)
    {
        var firstText = first.OriginalText.TrimEnd();
        var nextText = next.OriginalText.TrimStart();
        string text;
        if (firstText.EndsWith('-') && firstText.Length > 1 && char.IsLetter(firstText[^2]) && nextText.Length > 0 && char.IsLetter(nextText[0]))
            text = firstText[..^1] + nextText;
        else
            text = firstText + " " + nextText;

        var bounds = Rect.Union(first.BoundingBox, next.BoundingBox);
        var firstWeight = Math.Max(1, first.OriginalText.Length);
        var nextWeight = Math.Max(1, next.OriginalText.Length);
        var confidence = (first.Confidence * firstWeight + next.Confidence * nextWeight) / (firstWeight + nextWeight);
        var firstLines = Math.Max(1, first.VisualLineCount);
        var nextLines = Math.Max(1, next.VisualLineCount);
        var lineHeight = (first.EffectiveLineHeight * firstLines + next.EffectiveLineHeight * nextLines) /
                         (firstLines + nextLines);
        return first with
        {
            OriginalText = text,
            Confidence = confidence,
            BoundingBox = bounds,
            SourceLineHeight = lineHeight,
            VisualLineCount = firstLines + nextLines
        };
    }

    private static IReadOnlyList<OcrBlock> Reindex(IEnumerable<OcrBlock> blocks) => blocks.Select((block, index) =>
        block with { Id = $"ocr-{index + 1}", LineIndex = index, ReadingOrder = index }).ToArray();

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0) return 0;
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2 : ordered[middle];
    }

    private static bool EndsSentence(string text) => SentenceEnding().IsMatch(text.TrimEnd());
    private static bool StartsListItem(string text) => ListMarker().IsMatch(text.TrimStart());

    [GeneratedRegex("[.!?。！？:：;；][\\\"'’”)]?$", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceEnding();

    [GeneratedRegex("^(?:[•●▪◦*-]|\\d+[.)]|[A-Za-z][.)])\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex ListMarker();
}
