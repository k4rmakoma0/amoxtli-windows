namespace Amoxtli.Editing;

/// <summary>
/// Pairs scroll offsets of two views of the same document block by block, since a table, image or
/// code block takes very different heights on each side. Points run from (0, 0) to both ends and
/// grow on both sides; positions between two blocks are interpolated linearly.
/// </summary>
public sealed class ScrollMap
{
    private readonly List<(double A, double B)> _points;

    private ScrollMap(List<(double A, double B)> points) => _points = points;

    public IReadOnlyList<(double A, double B)> Points => _points;

    /// <param name="blocks">Where each block starts in view A and in view B, in document order.</param>
    /// <param name="endA">The largest scroll offset of view A.</param>
    /// <param name="endB">The largest scroll offset of view B.</param>
    /// <param name="sameBlocks">False when the views do not show the same blocks: then only the ends line up.</param>
    public static ScrollMap Create(IEnumerable<(double A, double B)> blocks, double endA, double endB, bool sameBlocks = true)
    {
        endA = Math.Max(0, endA); endB = Math.Max(0, endB);
        var points = new List<(double A, double B)> { (0, 0) };
        if (sameBlocks)
            foreach (var (a, b) in blocks)
            {
                // Near the end a block can no longer reach the top of its view; the end leads there.
                if (a >= endA || b >= endB) continue;
                if (a > points[^1].A && b > points[^1].B) points.Add((a, b));
            }
        if (endA > points[^1].A && endB > points[^1].B) points.Add((endA, endB));
        return new ScrollMap(points);
    }

    /// <summary>The offset in view B that matches <paramref name="a"/> in view A.</summary>
    public double Forward(double a) => Interpolate(a, true);

    /// <summary>The offset in view A that matches <paramref name="b"/> in view B.</summary>
    public double Backward(double b) => Interpolate(b, false);

    private double Interpolate(double value, bool forward)
    {
        double From((double A, double B) p) => forward ? p.A : p.B;
        double To((double A, double B) p) => forward ? p.B : p.A;
        if (_points.Count == 1 || value <= From(_points[0])) return To(_points[0]);
        if (value >= From(_points[^1])) return To(_points[^1]);
        for (int i = 1; i < _points.Count; i++)
        {
            if (value > From(_points[i])) continue;
            var (start, end) = (_points[i - 1], _points[i]);
            double span = From(end) - From(start);
            return To(start) + (span <= 0 ? 0 : (value - From(start)) / span * (To(end) - To(start)));
        }
        return To(_points[^1]);
    }
}
