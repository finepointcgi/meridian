using System.Text;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>How an element differs in a code-sync review: the CSS class suffix of its highlight.</summary>
public enum ChangeHighlight { Added, Changed, Removed }

public static partial class SvgRenderer
{
    /// <summary>CSS class of a highlight ("chg-added", "chg-changed", "chg-removed"); styled by the workbench's app.css.</summary>
    public static string ChangeClass(ChangeHighlight k) => k switch
    {
        ChangeHighlight.Added => "chg-added",
        ChangeHighlight.Changed => "chg-changed",
        _ => "chg-removed",
    };

    /// <summary>
    /// Code-change highlights over a rendered diagram: an outline around the shapes and a halo along the paths of changed
    /// elements, classed <c>chg-added</c> / <c>chg-changed</c> / <c>chg-removed</c> so the theme decides the colours. When
    /// <paramref name="ghostSource"/> (the same diagram on main) is given, removed elements that this diagram no longer
    /// shows are drawn as faded, dashed ghosts at the place main has them.
    /// </summary>
    public static void RenderChangeHighlights(StringBuilder sb, Diagram d, Func<DiagramNode, Bounds> bounds,
        Func<DiagramEdge, IReadOnlyList<Pt>?>? waypoints, IReadOnlyDictionary<string, ChangeHighlight> changes, Diagram? ghostSource = null)
    {
        if (changes.Count == 0) return;
        sb.Append("<g class=\"chg-layer\" pointer-events=\"none\">");
        foreach (var e in d.Edges)
        {
            if (!changes.TryGetValue(e.Relationship.Id, out var k)) continue;
            var pts = Notation.RouteIn(d, e, bounds, waypoints?.Invoke(e) ?? e.Waypoints);
            sb.Append($"<polyline class=\"chg-edge {ChangeClass(k)}\" data-element=\"{E(e.Relationship.Id)}\" points=\"{Points(pts)}\" fill=\"none\"/>");
        }
        foreach (var n in d.Nodes)
        {
            if (!changes.TryGetValue(n.Element.Id, out var k)) continue;
            var b = bounds(n);
            sb.Append($"<rect class=\"chg-outline {ChangeClass(k)}\" data-element=\"{E(n.Element.Id)}\" x=\"{F(b.X - 8)}\" y=\"{F(b.Y - 8)}\" width=\"{F(b.Width + 16)}\" height=\"{F(b.Height + 16)}\" rx=\"10\" fill=\"none\"/>");
        }
        if (ghostSource is not null)
        {
            var shown = d.Nodes.Select(n => n.Element.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var e in ghostSource.Edges)
            {
                if (!changes.TryGetValue(e.Relationship.Id, out var k) || k != ChangeHighlight.Removed || d.Edges.Any(x => x.Relationship.Id == e.Relationship.Id)) continue;
                var pts = Notation.RouteIn(ghostSource, e, n => n.Bounds, e.Waypoints);
                sb.Append($"<polyline class=\"chg-ghost chg-removed\" data-element=\"{E(e.Relationship.Id)}\" points=\"{Points(pts)}\" fill=\"none\"/>");
            }
            foreach (var n in ghostSource.Nodes)
            {
                if (!changes.TryGetValue(n.Element.Id, out var k) || k != ChangeHighlight.Removed || shown.Contains(n.Element.Id)) continue;
                var b = n.Bounds;
                sb.Append($"<g class=\"chg-ghost chg-removed\" data-element=\"{E(n.Element.Id)}\"><title>Removed: {E(n.Element.DisplayName)}</title>");
                sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" rx=\"6\"/>");
                var label = n.Element.DisplayName;
                var max = Math.Max(3, (int)(b.Width / CharWidth) - 1);
                if (label.Length > max) label = label[..(max - 1)] + "…";
                sb.Append($"<text x=\"{F(b.X + b.Width / 2)}\" y=\"{F(b.Y + Math.Min(22, b.Height / 2 + 4))}\" text-anchor=\"middle\">{E(label)}</text></g>");
            }
        }
        sb.Append("</g>");
    }
}
