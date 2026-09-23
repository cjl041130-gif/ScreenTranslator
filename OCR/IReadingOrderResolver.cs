using ScreenTranslator.Models;

namespace ScreenTranslator.OCR;

public interface IReadingOrderResolver
{
    IReadOnlyList<OcrBlock> Resolve(IReadOnlyList<OcrBlock> blocks, int pageWidth, int pageHeight);
}

public sealed class RuleReadingOrderResolver : IReadingOrderResolver
{
    public IReadOnlyList<OcrBlock> Resolve(IReadOnlyList<OcrBlock> blocks, int pageWidth, int pageHeight)
    {
        if (blocks.Count == 0) return [];
        // Width alone is not a header signal on web pages: ordinary article lines often
        // span most of the viewport. Promoting those lines ahead of their short wrapped
        // continuations breaks sentences before translation.
        var header = blocks.Where(b => b.BlockType is OcrBlockType.Title or OcrBlockType.Subtitle)
            .OrderBy(b => b.Y).ThenBy(b => b.X).ToList();
        var body = blocks.Except(header).ToList();
        var centers = body.Select(b => b.CenterX).Order().ToArray();
        var split = -1d;
        if (centers.Length >= 4)
        {
            var gaps = centers.Zip(centers.Skip(1), (a, b) => (At: (a + b) / 2, Gap: b - a)).OrderByDescending(x => x.Gap).First();
            if (gaps.Gap > pageWidth * .18 && body.Count(b => b.CenterX < gaps.At) >= 2 && body.Count(b => b.CenterX >= gaps.At) >= 2) split = gaps.At;
        }
        IEnumerable<OcrBlock> orderedBody = split < 0
            ? body.OrderBy(b => b.Y).ThenBy(b => b.X)
            : body.Where(b => b.CenterX < split).OrderBy(b => b.Y).ThenBy(b => b.X)
                .Concat(body.Where(b => b.CenterX >= split).OrderBy(b => b.Y).ThenBy(b => b.X));
        return header.Concat(orderedBody).Select((b, i) => b with { ReadingOrder = i }).ToArray();
    }
}
