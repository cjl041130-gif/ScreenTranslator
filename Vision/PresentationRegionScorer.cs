using System.Windows;
namespace ScreenTranslator.Vision;

public sealed class PresentationRegionScorer(VisionSettings settings)
{
    public PresentationCandidate Score(PresentationCandidate c, Rect search, Rect previous, bool fullscreen)
    {
        var r = c.Bounds; var fraction = r.Width * r.Height / Math.Max(1, search.Width * search.Height);
        var area = fullscreen ? 1 : Math.Min(1, fraction / .28) * Math.Min(1, (1 - fraction) / .18);
        var ratio = r.Width / Math.Max(1, r.Height);
        var ratioScore = .45 + .55 * Math.Exp(-5 * Math.Min(Math.Abs(Math.Log(ratio / (16d / 9))), Math.Abs(Math.Log(ratio / (4d / 3)))));
        var dx = (r.X + r.Width / 2 - (search.X + search.Width / 2)) / search.Width;
        var dy = (r.Y + r.Height / 2 - (search.Y + search.Height / 2)) / search.Height;
        var center = Math.Clamp(1 - Math.Sqrt(dx * dx + dy * dy), 0, 1);
        var overlap = Rect.Intersect(r, search);
        var containment = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height / (r.Width * r.Height);
        var stability = previous.IsEmpty ? .5 : CoordinateMapper.IoU(r, previous);
        double[] values = [area, ratioScore, c.Completeness, c.Edge, center, containment, c.Interior, stability];
        var weights = settings.CandidateWeights.Values;
        var final = values.Zip(weights, (v, weight) => v * weight).Sum() / weights.Sum();
        // Soft shape prior suppresses long ribbon strips without rejecting custom slide sizes.
        final *= 1 - settings.AspectPriorStrength * (1 - ratioScore);
        return c with { Scores = new(area, ratioScore, c.Completeness, c.Edge, center, Math.Clamp(containment, 0, 1), c.Interior, stability, Math.Clamp(final, 0, 1)) };
    }
}
