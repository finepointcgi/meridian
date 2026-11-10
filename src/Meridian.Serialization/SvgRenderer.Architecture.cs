using System.Text;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>An architecture violation between two model elements, drawn as a red dashed edge (docs/ARCHITECTURE-CHECKS.md).</summary>
public sealed record ViolationEdge(string SourceId, string TargetId, string Title);

public static partial class SvgRenderer
{
    /// <summary>
    /// Architecture violations over a rendered diagram: a dashed edge (class <c>arch-violation</c>, red in the theme)
    /// between the shapes of the offending elements. A shape shows an element when it is the element, or a part typed by
    /// it (so violations also appear on internal block diagrams). Pairs not both on the diagram are skipped.
    /// </summary>
    public static void RenderViolationEdges(StringBuilder sb, Diagram d, Func<DiagramNode, Bounds> bounds, IReadOnlyList<ViolationEdge> edges)
    {
        if (edges.Count == 0 || d.Nodes.Count == 0) return;
        var drawn = new HashSet<(string, string)>();
        var open = false;
        foreach (var edge in edges)
        {
            if (Shape(d, edge.SourceId) is not { } a || Shape(d, edge.TargetId) is not { } b || ReferenceEquals(a, b)) continue;
            if (!drawn.Add((a.Id, b.Id))) continue;
            if (!open)
            {
                sb.Append("<g class=\"arch-layer\" pointer-events=\"none\">");
                open = true;
            }
            var (ba, bb) = (bounds(a), bounds(b));
            var (ca, cb) = (Center(ba), Center(bb));
            var p = Clip(ba, ca, cb);
            var q = Clip(bb, cb, ca);
            sb.Append($"<g class=\"arch-violation\" data-source=\"{E(edge.SourceId)}\" data-target=\"{E(edge.TargetId)}\"><title>{E(edge.Title)}</title>");
            sb.Append($"<line x1=\"{F(p.X)}\" y1=\"{F(p.Y)}\" x2=\"{F(q.X)}\" y2=\"{F(q.Y)}\"/>");
            sb.Append($"<circle cx=\"{F(q.X)}\" cy=\"{F(q.Y)}\" r=\"4\"/></g>");
        }
        if (open) sb.Append("</g>");
    }

    private static DiagramNode? Shape(Diagram d, string id) =>
        d.Nodes.FirstOrDefault(n => n.Element.Id == id)
        ?? d.Nodes.FirstOrDefault(n => n.Element.Kind == ElementKind.Property && n.Element.TypeRef == id);

    private static Pt Center(Bounds b) => new(b.X + b.Width / 2, b.Y + b.Height / 2);

    /// <summary>Where the segment from the centre <paramref name="from"/> towards <paramref name="to"/> leaves the box.</summary>
    private static Pt Clip(Bounds b, Pt from, Pt to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return from;
        var tx = Math.Abs(dx) < 1e-9 ? double.MaxValue : b.Width / 2 / Math.Abs(dx);
        var ty = Math.Abs(dy) < 1e-9 ? double.MaxValue : b.Height / 2 / Math.Abs(dy);
        var t = Math.Min(1, Math.Min(tx, ty));
        return new Pt(from.X + dx * t, from.Y + dy * t);
    }
}
