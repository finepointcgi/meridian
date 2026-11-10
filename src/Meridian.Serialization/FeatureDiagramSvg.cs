using System.Globalization;
using System.Text;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>Feature model notation that is not a node or an edge: the group arcs under a parent feature
/// (hollow arc = alternative / xor group, filled sector = or group).</summary>
public static class FeatureDiagramSvg
{
    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public static void RenderGroupArcs(StringBuilder sb, Diagram d, Func<DiagramNode, Bounds> bounds,
                                       Func<DiagramEdge, IReadOnlyList<Pt>?>? waypoints, SvgTheme t)
    {
        foreach (var group in d.Edges.Where(e => e.Relationship.Kind == ElementKind.FeatureDecomposition)
                     .GroupBy(e => (e.Source, Kind: e.Relationship.Target.Get(ExtFieldKeys.FeatureKind)))
                     .Where(g => g.Key.Kind is "alternative" or "or" && g.Count() >= 2))
        {
            var routes = group.Select(e => Notation.RouteIn(d, e, bounds, waypoints?.Invoke(e))).Where(r => r.Count >= 2).ToList();
            if (routes.Count < 2) continue;
            var origin = new Pt(routes.Average(r => r[0].X), routes.Average(r => r[0].Y));
            const double radius = 22;
            var angles = routes.Select(r => Math.Atan2(r[1].Y - r[0].Y, r[1].X - r[0].X)).OrderBy(a => a).ToList();
            double a0 = angles[0], a1 = angles[^1];
            var p0 = new Pt(origin.X + radius * Math.Cos(a0), origin.Y + radius * Math.Sin(a0));
            var p1 = new Pt(origin.X + radius * Math.Cos(a1), origin.Y + radius * Math.Sin(a1));
            var large = a1 - a0 > Math.PI ? 1 : 0;
            if (group.Key.Kind == "or")
                sb.Append($"<path d=\"M{F(origin.X)},{F(origin.Y)} L{F(p0.X)},{F(p0.Y)} A{F(radius)},{F(radius)} 0 {large} 1 {F(p1.X)},{F(p1.Y)} Z\" fill=\"{t.Edge}\" stroke=\"{t.Edge}\" stroke-width=\"1\"/>");
            else
                sb.Append($"<path d=\"M{F(p0.X)},{F(p0.Y)} A{F(radius)},{F(radius)} 0 {large} 1 {F(p1.X)},{F(p1.Y)}\" fill=\"none\" stroke=\"{t.Edge}\" stroke-width=\"1.4\"/>");
        }
    }

    /// <summary>Small annotations at the bottom of rounded shapes (actions): duration, signals and presence condition.</summary>
    public static void RenderBadges(StringBuilder sb, Element e, Bounds b, SvgTheme t)
    {
        var parts = new List<string>();
        if (e.Get(ExtFieldKeys.Duration) is { Length: > 0 } d) parts.Add($"⏱ {d}");
        if (e.Get(ExtFieldKeys.AcceptEvent) is { Length: > 0 } a) parts.Add($"⚑ {a}");
        if (e.Get(ExtFieldKeys.SendSignal) is { Length: > 0 } s) parts.Add($"⚡ {s}");
        if (e.Get(ExtFieldKeys.PresenceCondition) is { Length: > 0 } pc) parts.Add($"{{{pc}}}");
        if (parts.Count == 0 || b.Height < 30) return;
        var text = string.Join("  ", parts);
        var max = (int)Math.Max(4, (b.Width - 10) / 5.6);
        if (text.Length > max) text = text[..(max - 1)] + "…";
        sb.Append($"<text x=\"{F(b.Center.X)}\" y=\"{F(b.Bottom - 5)}\" text-anchor=\"middle\" font-size=\"9.5\" fill=\"{t.Muted}\">{System.Net.WebUtility.HtmlEncode(text)}</text>");
    }
}
