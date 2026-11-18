using System.Globalization;
using System.Text;

namespace Meridian.Core.Model;

/// <summary>
/// Freehand pen and highlighter strokes on diagrams (whiteboards).
/// <para>
/// A stroke is a free <see cref="ElementKind.Shape"/> with subtype <c>pen</c> or <c>highlighter</c>. Its points are kept
/// in the <c>points</c> field as fractions of the shape's bounds ("x,y x,y …", 0–1), so moving and resizing the shape
/// moves and stretches the stroke. Colour and width are the shape's ordinary <c>stroke</c> and <c>width</c> styles, and
/// the layer is its <c>layer</c> style, so strokes need no new file format: they persist, replicate, undo and export
/// like any other shape.
/// </para>
/// </summary>
public static class Strokes
{
    public const string PenSubtype = "pen";
    public const string HighlighterSubtype = "highlighter";

    /// <summary>Not a shape: the eraser tool of the shapes panel (click or drag over strokes to delete them).</summary>
    public const string EraserTool = "eraser";

    public const string PointsField = "points";

    /// <summary>Points kept per stroke after simplification (longer strokes are thinned evenly).</summary>
    public const int MaxPoints = 1500;

    public const string HighlighterColor = "#ffd400";
    public const double HighlighterOpacity = 0.4;
    public const double DefaultPenWidth = 2.5;

    /// <summary>A highlighter is this many times wider than a pen with the same "width" style.</summary>
    public const double HighlighterFactor = 4;

    /// <summary>Smallest side of a stroke's box.</summary>
    public const double MinBox = 32;

    public static bool IsStrokeSubtype(string? subtype) => subtype is PenSubtype or HighlighterSubtype;

    public static bool IsStroke(Element e) => e.Kind == ElementKind.Shape && IsStrokeSubtype(e.Get(FieldKeys.Subtype));

    public static bool IsStroke(DiagramNode n) => IsStroke(n.Element);

    public static bool IsHighlighter(Element e) => e.Kind == ElementKind.Shape && e.Get(FieldKeys.Subtype) == HighlighterSubtype;

    /// <summary>Line width a stroke is drawn with: the "width" style (pen), four times it for a highlighter.</summary>
    public static double WidthOf(IReadOnlyDictionary<string, string> style, bool highlighter)
    {
        double? w = style.TryGetValue("width", out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.IsFinite(x)
            ? Math.Clamp(x, 0.5, 12) : null;
        return highlighter ? (w ?? 3.5) * HighlighterFactor : w ?? DefaultPenWidth;
    }

    public static double WidthOf(DiagramNode n) => WidthOf(n.Style, IsHighlighter(n.Element));

    // ================================================================ simplification

    /// <summary>
    /// Ramer–Douglas–Peucker: drops points closer than <paramref name="tolerance"/> to the line through their
    /// neighbours. Keeps the first and last point; iterative, so long strokes can't overflow the stack.
    /// </summary>
    public static IReadOnlyList<Pt> Simplify(IReadOnlyList<Pt> points, double tolerance)
    {
        // Drop exact repeats first (a pointer that didn't move).
        var pts = new List<Pt>(points.Count);
        foreach (var p in points)
            if (double.IsFinite(p.X) && double.IsFinite(p.Y) && (pts.Count == 0 || pts[^1] != p)) pts.Add(p);
        if (pts.Count <= 2) return pts;
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int A, int B)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b - a < 2) continue;
            var best = -1;
            var bestD = tolerance;
            for (var i = a + 1; i < b; i++)
            {
                var d = DistanceToSegment(pts[i], pts[a], pts[b]);
                if (d > bestD) { bestD = d; best = i; }
            }
            if (best < 0) continue;
            keep[best] = true;
            stack.Push((a, best));
            stack.Push((best, b));
        }
        var result = new List<Pt>();
        for (var i = 0; i < pts.Count; i++) if (keep[i]) result.Add(pts[i]);
        if (result.Count > MaxPoints)
        {
            var step = (double)(result.Count - 1) / (MaxPoints - 1);
            result = Enumerable.Range(0, MaxPoints).Select(i => result[(int)Math.Round(i * step)]).ToList();
        }
        return result;
    }

    public static double DistanceToSegment(Pt p, Pt a, Pt b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 <= 1e-12) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        double qx = a.X + t * dx - p.X, qy = a.Y + t * dy - p.Y;
        return Math.Sqrt(qx * qx + qy * qy);
    }

    // ================================================================ storage

    /// <summary>
    /// The bounds of a stroke through <paramref name="points"/> (padded by <paramref name="pad"/> on every side) and the
    /// points as fractions of those bounds, ready for the <see cref="PointsField"/>.
    /// </summary>
    public static (Bounds Bounds, string Points) Encode(IReadOnlyList<Pt> points, double pad)
    {
        if (points.Count == 0) throw new ArgumentException("A stroke needs at least one point.", nameof(points));
        pad = Math.Max(1, pad);
        double x0 = points.Min(p => p.X) - pad, y0 = points.Min(p => p.Y) - pad;
        double x1 = points.Max(p => p.X) + pad, y1 = points.Max(p => p.Y) + pad;
        // Plain shapes are never drawn shorter than one line of text, so a flat stroke gets a box at least that tall
        // (the ink stays centred in it) instead of being stretched when drawn.
        if (x1 - x0 < MinBox) { var g = (MinBox - (x1 - x0)) / 2; x0 -= g; x1 += g; }
        if (y1 - y0 < MinBox) { var g = (MinBox - (y1 - y0)) / 2; y0 -= g; y1 += g; }
        var b = new Bounds(x0, y0, x1 - x0, y1 - y0);
        var sb = new StringBuilder(points.Count * 14);
        foreach (var p in points)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(Frac((p.X - x0) / b.Width)).Append(',').Append(Frac((p.Y - y0) / b.Height));
        }
        return (b, sb.ToString());
    }

    private static string Frac(double v) => Math.Clamp(v, 0, 1).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>The points of a stroke in diagram coordinates, for the shape drawn at <paramref name="b"/>. Bad input gives no points.</summary>
    public static IReadOnlyList<Pt> Decode(string? points, Bounds b)
    {
        var list = new List<Pt>();
        if (string.IsNullOrWhiteSpace(points)) return list;
        foreach (var pair in points.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (list.Count >= MaxPoints * 2) break;
            var comma = pair.IndexOf(',');
            if (comma <= 0
                || !double.TryParse(pair.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out var fx)
                || !double.TryParse(pair.AsSpan(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var fy)
                || !double.IsFinite(fx) || !double.IsFinite(fy)) continue;
            list.Add(new Pt(b.X + Math.Clamp(fx, 0, 1) * b.Width, b.Y + Math.Clamp(fy, 0, 1) * b.Height));
        }
        return list;
    }

    public static IReadOnlyList<Pt> PointsOf(DiagramNode n, Bounds b) => Decode(n.Element.Get(PointsField), b);

    // ================================================================ drawing

    /// <summary>
    /// A smooth SVG path through <paramref name="pts"/>: a Catmull–Rom spline written as cubic Béziers. One point
    /// becomes a dot (a zero-length line, drawn with round caps).
    /// </summary>
    public static string SmoothPath(IReadOnlyList<Pt> pts)
    {
        if (pts.Count == 0) return "";
        var sb = new StringBuilder(pts.Count * 40);
        sb.Append('M').Append(F(pts[0].X)).Append(',').Append(F(pts[0].Y));
        if (pts.Count == 1) return sb.Append(" L").Append(F(pts[0].X + 0.01)).Append(',').Append(F(pts[0].Y)).ToString();
        if (pts.Count == 2) return sb.Append(" L").Append(F(pts[1].X)).Append(',').Append(F(pts[1].Y)).ToString();
        for (var i = 0; i < pts.Count - 1; i++)
        {
            var p0 = pts[Math.Max(0, i - 1)];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = pts[Math.Min(pts.Count - 1, i + 2)];
            var c1 = new Pt(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Pt(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
            sb.Append(" C").Append(F(c1.X)).Append(',').Append(F(c1.Y)).Append(' ')
              .Append(F(c2.X)).Append(',').Append(F(c2.Y)).Append(' ')
              .Append(F(p2.X)).Append(',').Append(F(p2.Y));
        }
        return sb.ToString();
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Distance from <paramref name="p"/> to the stroke drawn at <paramref name="b"/> (its centre line).</summary>
    public static double Distance(DiagramNode n, Bounds b, Pt p)
    {
        var pts = PointsOf(n, b);
        if (pts.Count == 0) return double.MaxValue;
        if (pts.Count == 1) return DistanceToSegment(p, pts[0], pts[0]);
        var best = double.MaxValue;
        for (var i = 1; i < pts.Count; i++) best = Math.Min(best, DistanceToSegment(p, pts[i - 1], pts[i]));
        return best;
    }

    // ================================================================ editing

    /// <summary>
    /// Adds a stroke through <paramref name="raw"/> (diagram coordinates, as the pointer moved) to <paramref name="d"/>:
    /// simplified with <paramref name="tolerance"/>, owned by <paramref name="owner"/>. One undo step.
    /// </summary>
    public static DiagramNode AddStroke(this ModelRepository m, Diagram d, Element owner, IReadOnlyList<Pt> raw, bool highlighter = false,
        double tolerance = 0.8, IReadOnlyDictionary<string, string>? style = null)
    {
        var pts = Simplify(raw, tolerance);
        if (pts.Count == 0) throw new InvalidOperationException("A stroke needs at least one point.");
        var width = WidthOf(style ?? new Dictionary<string, string>(), highlighter);
        var (bounds, encoded) = Encode(pts, width / 2 + 2);
        using (m.History.Transaction(highlighter ? "Highlight" : "Draw"))
        {
            var e = m.Create(owner, ElementKind.Shape, "");
            m.Set(e, FieldKeys.Subtype, highlighter ? HighlighterSubtype : PenSubtype);
            m.Set(e, PointsField, encoded);
            var node = m.AddNode(d, e, bounds, showRelationships: false);
            if (style != null)
                foreach (var key in new[] { "stroke", "width", "dash", DiagramExtras.LayerStyle })
                    if (style.TryGetValue(key, out var v) && v.Length > 0) m.SetNodeStyle(d, node, key, v);
            return node;
        }
    }
}
