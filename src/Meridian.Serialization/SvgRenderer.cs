using System.Globalization;
using System.Net;
using System.Text;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>
/// Renders diagrams as SVG using the shared <see cref="Notation"/> rules. The same body markup is used
/// by the interactive web editor and by file/report export, so on-screen and exported diagrams match.
/// </summary>
public sealed record SvgTheme(
    string Background, string NodeFill, string Stroke, string Divider, string Text, string Muted, string Edge, string LabelBg,
    string Ink, string FrameFill, string TraceTitle, string TraceText, string ShadowColor, double ShadowOpacity,
    Func<ElementKind, string> Header, Func<ElementKind, string> Fill, Func<ElementKind, string> Accent)
{
    public static readonly SvgTheme Light = new(
        "#ffffff", "#ffffff", "#5b6577", "#c9cfd9", "#1a2233", "#5a6476", "#4a5568", "#ffffff",
        "#3a4458", "#f6f7f9", "#2f6fec", "#24467f", "#1d2a44", 0.16,
        k => k switch
        {
            ElementKind.Requirement => "#dce8ff", ElementKind.TestCase => "#d9f1e1",
            ElementKind.Block or ElementKind.Class or ElementKind.Component => "#ffeec4",
            ElementKind.InterfaceBlock or ElementKind.Interface => "#ece2ff",
            ElementKind.ValueType or ElementKind.DataType or ElementKind.Enumeration => "#d8f0ef",
            ElementKind.ConstraintBlock => "#fbdede", ElementKind.Property or ElementKind.Port => "#fff4d6",
            ElementKind.Activity or ElementKind.StateMachine or ElementKind.Interaction => "#dcecfb", _ => "#eef0f4",
        },
        k => k switch
        {
            ElementKind.Comment => "#fff8d0", ElementKind.Package => "#f7f8fa", ElementKind.UseCase => "#fff6e2",
            ElementKind.Action or ElementKind.State => "#eef5ff", _ => "#ffffff",
        },
        k => k switch
        {
            ElementKind.Requirement => "#2f6fec", ElementKind.TestCase => "#2e8b57",
            ElementKind.Block or ElementKind.Class or ElementKind.Component or ElementKind.Property => "#c98a00",
            ElementKind.InterfaceBlock or ElementKind.Interface or ElementKind.Port => "#7b4fd6",
            ElementKind.ValueType or ElementKind.DataType or ElementKind.Enumeration => "#1d8a8a",
            ElementKind.ConstraintBlock => "#c0392b", ElementKind.Activity or ElementKind.StateMachine or ElementKind.Interaction => "#2a7fbf", _ => "#6b7586",
        });

    public static readonly SvgTheme Dark = new(
        "#0f1522", "#182133", "#3c4b67", "#2c3850", "#e8edf7", "#93a0b8", "#8796b0", "#0f1522",
        "#c9d3e6", "#182133", "#6ea8ff", "#b9d2ff", "#000000", 0.45,
        k => k switch
        {
            ElementKind.Requirement => "#1c335f", ElementKind.TestCase => "#173a2b",
            ElementKind.Block or ElementKind.Class or ElementKind.Component => "#3a2f14",
            ElementKind.InterfaceBlock or ElementKind.Interface => "#2d2453",
            ElementKind.ValueType or ElementKind.DataType or ElementKind.Enumeration => "#123c3c",
            ElementKind.ConstraintBlock => "#4a2222", ElementKind.Property or ElementKind.Port => "#33291a",
            ElementKind.Activity or ElementKind.StateMachine or ElementKind.Interaction => "#16304e", _ => "#232d42",
        },
        k => k switch
        {
            ElementKind.Comment => "#353020", ElementKind.Package => "#1a2335", ElementKind.UseCase => "#2b2517",
            ElementKind.Action or ElementKind.State => "#172640", _ => "#182133",
        },
        k => k switch
        {
            ElementKind.Requirement => "#5b8cff", ElementKind.TestCase => "#3fbf7f",
            ElementKind.Block or ElementKind.Class or ElementKind.Component or ElementKind.Property => "#f0b429",
            ElementKind.InterfaceBlock or ElementKind.Interface or ElementKind.Port => "#a283ff",
            ElementKind.ValueType or ElementKind.DataType or ElementKind.Enumeration => "#2ec4b6",
            ElementKind.ConstraintBlock => "#ff6b6b", ElementKind.Activity or ElementKind.StateMachine or ElementKind.Interaction => "#4aa3ff", _ => "#7f8ca3",
        });

    public string Defs =>
        $"<defs><filter id=\"msh\" x=\"-10%\" y=\"-10%\" width=\"125%\" height=\"135%\"><feDropShadow dx=\"0\" dy=\"2\" stdDeviation=\"2.5\" flood-color=\"{ShadowColor}\" flood-opacity=\"{ShadowOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"/></filter></defs>";
}

public static class SvgRenderer
{
    private const double Line = Notation.LineHeight;
    private const double CharWidth = 6.4; // approximate advance at 12px for truncation/wrapping
    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string E(string s) => WebUtility.HtmlEncode(s);

    public static string Render(ModelRepository model, Diagram d, double margin = 24)
    {
        Func<DiagramNode, Bounds> bounds = n => Notation.EffectiveBounds(model, n, d.DiagramType);
        var nodes = d.Nodes;
        double minX = 0, minY = 0, maxX = 400, maxY = 300;
        if (nodes.Count > 0)
        {
            var pts = d.Edges.SelectMany(e => e.Waypoints).ToList();
            minX = Math.Min(nodes.Min(n => n.Bounds.X), pts.Count > 0 ? pts.Min(p => p.X) : double.MaxValue) - margin - (d.DiagramType == DiagramType.InternalBlock ? 40 : 0);
            minY = Math.Min(nodes.Min(n => n.Bounds.Y), pts.Count > 0 ? pts.Min(p => p.Y) : double.MaxValue) - margin - 30 - (d.DiagramType == DiagramType.InternalBlock ? 50 : 0);
            maxX = Math.Max(nodes.Max(n => n.Bounds.Right), pts.Count > 0 ? pts.Max(p => p.X) : double.MinValue) + margin + 60;
            maxY = Math.Max(nodes.Max(n => bounds(n).Bottom), pts.Count > 0 ? pts.Max(p => p.Y) : double.MinValue) + margin;
        }
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{F(minX)} {F(minY)} {F(maxX - minX)} {F(maxY - minY)}\" width=\"{F(maxX - minX)}\" height=\"{F(maxY - minY)}\" font-family=\"Inter, Helvetica, Arial, sans-serif\" font-size=\"12\">");
        var t = SvgTheme.Light;
        sb.Append(t.Defs);
        sb.Append($"<rect x=\"{F(minX)}\" y=\"{F(minY)}\" width=\"{F(maxX - minX)}\" height=\"{F(maxY - minY)}\" fill=\"{t.Background}\"/>");
        sb.Append($"<rect x=\"{F(minX + 4)}\" y=\"{F(minY + 4)}\" width=\"{F(maxX - minX - 8)}\" height=\"{F(maxY - minY - 8)}\" fill=\"none\" stroke=\"#8a93a3\"/>");
        RenderHeader(sb, d, minX + 4, minY + 4, t);
        RenderBody(sb, model, d, bounds, null, t);
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>The SysML diagram frame header ("bdd [Package] Structure [Vehicle Structure]").</summary>
    public static void RenderHeader(StringBuilder sb, Diagram d, double x, double y, SvgTheme? theme = null)
    {
        var t = theme ?? SvgTheme.Light;
        var header = HeaderText(d);
        var hw = header.Length * CharWidth + 16;
        sb.Append($"<path d=\"M{F(x)},{F(y)} H{F(x + hw + 10)} V{F(y + 12)} L{F(x + hw)},{F(y + 22)} H{F(x)} Z\" fill=\"{t.FrameFill}\" stroke=\"{t.Stroke}\"/>");
        sb.Append($"<text x=\"{F(x + 6)}\" y=\"{F(y + 15)}\" fill=\"{t.Text}\">{E(header)}</text>");
    }

    public static string HeaderText(Diagram d) =>
        $"{Metamodel.DiagramAbbreviation(d.DiagramType)} [{Metamodel.DisplayName(d.Owner?.Kind ?? ElementKind.Package)}] {d.Owner?.Name} [{d.Name}]";

    /// <summary>Edges then nodes, in model coordinates.</summary>
    public static void RenderBody(StringBuilder sb, ModelRepository model, Diagram d, Func<DiagramNode, Bounds> bounds,
                                  Func<DiagramEdge, IReadOnlyList<Pt>?>? waypoints = null, SvgTheme? theme = null, Bounds? viewport = null)
    {
        var t = theme ?? SvgTheme.Light;
        if (viewport is { } vp && d.Nodes.Count > 150)
        {
            // Large diagrams: draw only what intersects the (padded) viewport.
            var pad = new Bounds(vp.X - 300, vp.Y - 300, vp.Width + 600, vp.Height + 600);
            var visible = d.Nodes.Where(n => bounds(n).Intersects(pad)).ToHashSet();
            var culled = new Diagram(d.DiagramType, d.Id);
            foreach (var kv in d.Fields) culled.SetRaw(kv.Key, kv.Value);
            culled.Owner = d.Owner;
            culled.NodeList.AddRange(d.Nodes.Where(n => visible.Contains(n) || d.Nodes.Any(c => visible.Contains(c) && c.ParentId == n.Id)));
            culled.EdgeList.AddRange(d.Edges.Where(e => visible.Contains(e.Source) || visible.Contains(e.Target)));
            d = culled;
        }
        if (d.DiagramType == DiagramType.InternalBlock && d.Owner != null && d.Nodes.Count > 0) RenderContextFrame(sb, d, bounds, t);
        if (d.DiagramType == DiagramType.Sequence)
        {
            // Fragments behind, then lifelines, activation bars, messages, then everything else (comments) on top.
            foreach (var n in d.Nodes.Where(n => n.Element.Kind == ElementKind.CombinedFragment)) RenderNode(sb, model, d, n, bounds(n), t);
            foreach (var n in d.Nodes.Where(n => n.Element.Kind == ElementKind.Lifeline)) RenderNode(sb, model, d, n, bounds(n), t);
            foreach (var (ll, from, to) in Notation.Activations(d, waypoints))
            {
                var cx = bounds(ll).Center.X;
                sb.Append($"<rect x=\"{F(cx - Notation.ActivationWidth / 2)}\" y=\"{F(from)}\" width=\"{F(Notation.ActivationWidth)}\" height=\"{F(Math.Max(8, to - from))}\" rx=\"2\" fill=\"{t.Header(ElementKind.Block)}\" stroke=\"{t.Stroke}\"/>");
            }
            foreach (var e in d.Edges) RenderEdge(sb, d, e, bounds, waypoints?.Invoke(e), t);
            foreach (var n in d.Nodes.Where(n => n.Element.Kind is not (ElementKind.CombinedFragment or ElementKind.Lifeline))) RenderNode(sb, model, d, n, bounds(n), t);
            return;
        }
        // Containers (pools, swimlanes, composite states, parts with nested shapes) first, then paths, then contents by depth.
        var containers = d.Nodes.Where(n => IsContainer(d, n)).OrderBy(n => Depth(d, n)).ToList();
        foreach (var n in containers) RenderNode(sb, model, d, n, bounds(n), t);
        foreach (var e in d.Edges) RenderEdge(sb, d, e, bounds, waypoints?.Invoke(e), t);
        foreach (var n in d.Nodes.Except(containers).OrderBy(n => Depth(d, n))) RenderNode(sb, model, d, n, bounds(n), t);
        RenderLegend(sb, model, d, bounds, t);
    }

    /// <summary>Auto-generated legend (element kinds, stereotypes and custom colours used on the diagram).</summary>
    public static void RenderLegend(StringBuilder sb, ModelRepository m, Diagram d, Func<DiagramNode, Bounds> bounds, SvgTheme t)
    {
        if (d.Get(FieldKeys.Legend) != "true" || d.Nodes.Count == 0) return;
        var entries = new List<(string Color, string Label)>();
        foreach (var g in d.Nodes.GroupBy(n => HeaderColor(m, n, t)))
        {
            var n = g.First();
            var label = n.Style.ContainsKey("fill") ? "Highlighted"
                : StereotypeColor(m, n.Element) != null ? "«" + n.Element.Stereotypes.First() + "»"
                : n.Element.Kind == ElementKind.ArchiElement ? Subtypes.Layer(n.Element.Get(FieldKeys.Subtype)) + " layer"
                : Metamodel.DisplayName(n.Element.Kind);
            if (!entries.Any(e => e.Label == label)) entries.Add((g.Key, label));
        }
        var all = d.Nodes.Select(bounds).ToList();
        var x = all.Max(b => b.Right) + 40; var y = all.Min(b => b.Y);
        var h = 30 + entries.Count * 20;
        sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"190\" height=\"{F(h)}\" rx=\"6\" fill=\"{t.NodeFill}\" stroke=\"{t.Stroke}\"/>");
        Text(sb, x + 10, y + 18, "Legend", true, "start", size: 12, color: t.Text);
        var yy = y + 34;
        foreach (var (color, label) in entries)
        {
            sb.Append($"<rect x=\"{F(x + 10)}\" y=\"{F(yy - 10)}\" width=\"18\" height=\"12\" rx=\"2\" fill=\"{color}\" stroke=\"{t.Stroke}\"/>");
            Text(sb, x + 36, yy, Trunc(label, 140), false, "start", size: 11.5, color: t.Text);
            yy += 20;
        }
    }

    private static bool IsContainer(Diagram d, DiagramNode n) =>
        Notation.ShapeOf(n.Element) is NodeShape.Swimlane or NodeShape.Pool || d.Nodes.Any(c => c.ParentId == n.Id && !Notation.IsBorderShape(c.Element));

    private static int Depth(Diagram d, DiagramNode n)
    {
        var depth = 0;
        for (var p = d.ParentOf(n); p != null && depth < 32; p = d.ParentOf(p)) depth++;
        return depth;
    }

    /// <summary>Header tint for a shape: per-shape override, stereotype colour, ArchiMate layer, then the theme default.</summary>
    public static string HeaderColor(ModelRepository m, DiagramNode n, SvgTheme t)
    {
        if (n.Style.TryGetValue("fill", out var f) && f.Length > 0) return f;
        if (StereotypeColor(m, n.Element) is { } sc) return sc;
        if (n.Element.Kind == ElementKind.ArchiElement)
        {
            var (light, dark) = Subtypes.LayerColor(Subtypes.Layer(n.Element.Get(FieldKeys.Subtype)));
            return ReferenceEquals(t, SvgTheme.Dark) ? dark : light;
        }
        return t.Header(n.Element.Kind);
    }

    public static string? StereotypeColor(ModelRepository m, Element e)
    {
        foreach (var name in e.Stereotypes)
            if (m.OfKind(ElementKind.Stereotype).FirstOrDefault(s => s.Name == name)?.Get(FieldKeys.Color) is { Length: > 0 } c) return c;
        return null;
    }

    /// <summary>IBD: the enclosing block whose parts and ports are shown inside it.</summary>
    private static void RenderContextFrame(StringBuilder sb, Diagram d, Func<DiagramNode, Bounds> bounds, SvgTheme t)
    {
        var bs = d.Nodes.Select(bounds).ToList();
        double x0 = bs.Min(b => b.X) - 30, y0 = bs.Min(b => b.Y) - 44, x1 = bs.Max(b => b.Right) + 30, y1 = bs.Max(b => b.Bottom) + 30;
        var k = d.Owner!.Kind;
        sb.Append($"<rect x=\"{F(x0)}\" y=\"{F(y0)}\" width=\"{F(x1 - x0)}\" height=\"{F(y1 - y0)}\" rx=\"8\" fill=\"{t.Fill(k)}\" fill-opacity=\"0.55\" stroke=\"{t.Accent(k)}\" stroke-width=\"1.5\"/>");
        sb.Append($"<path d=\"M{F(x0)},{F(y0 + 26)} V{F(y0 + 8)} Q{F(x0)},{F(y0)} {F(x0 + 8)},{F(y0)} H{F(x1 - 8)} Q{F(x1)},{F(y0)} {F(x1)},{F(y0 + 8)} V{F(y0 + 26)} Z\" fill=\"{t.Header(k)}\"/>");
        Text(sb, x0 + 12, y0 + 18, $"«block» {d.Owner.Name}", true, "start", size: 12.5, color: t.Text);
    }

    private static void RenderEdge(StringBuilder sb, Diagram d, DiagramEdge e, Func<DiagramNode, Bounds> bounds, IReadOnlyList<Pt>? wps, SvgTheme t)
    {
        var pts = Notation.RouteIn(d, e, bounds, wps);
        var style = Notation.StyleOf(e.Relationship);
        if (e.Style.TryGetValue("stroke", out var ec) && ec.Length > 0) t = t with { Edge = ec };
        var width = e.Style.TryGetValue("width", out var ew) && double.TryParse(ew, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wv) ? wv : 1.3;
        if (e.Relationship.Kind == ElementKind.Message)
            style = e.Relationship.Get(FieldKeys.MessageSort) switch
            {
                "reply" => style with { Dashed = true, TargetHead = ArrowHead.Open },
                "asynchCall" or "asynchSignal" => style with { TargetHead = ArrowHead.Open },
                _ => style,
            };
        var dash = style.Dotted ? " stroke-dasharray=\"2,3\"" : style.Dashed ? " stroke-dasharray=\"6,4\"" : "";
        sb.Append($"<polyline points=\"{Points(pts)}\" fill=\"none\" stroke=\"{t.Edge}\" stroke-width=\"{F(width)}\"{dash}/>");
        if (e.Relationship.Kind == ElementKind.Connector && e.Relationship.Get(FieldKeys.ItemFlow) is { Length: > 0 } item && pts.Count >= 2)
        {
            // SysML item flow: a filled triangle on the connector pointing in the flow direction, plus the conveyed item.
            var m = Notation.Midpoint(pts);
            var seg = Enumerable.Range(1, pts.Count - 1).Select(i => (pts[i - 1], pts[i])).OrderBy(sg => Notation.DistanceToSegment(m, sg.Item1, sg.Item2)).First();
            var reverse = e.Relationship.Get(FieldKeys.FlowDirection) == "reverse";
            var (a, z) = reverse ? (seg.Item2, seg.Item1) : (seg.Item1, seg.Item2);
            var len = Notation.Dist(a, z);
            if (len > 1)
            {
                var ux = (z.X - a.X) / len; var uy = (z.Y - a.Y) / len;
                var tip = new Pt(m.X + ux * 7, m.Y + uy * 7);
                var b1 = new Pt(m.X - ux * 7 - uy * 6, m.Y - uy * 7 + ux * 6);
                var b2 = new Pt(m.X - ux * 7 + uy * 6, m.Y - uy * 7 - ux * 6);
                sb.Append($"<polygon points=\"{Points([tip, b1, b2])}\" fill=\"{t.Accent(ElementKind.Block)}\" stroke=\"none\"/>");
            }
            sb.Append($"<text x=\"{F(m.X + 10)}\" y=\"{F(m.Y + 16)}\" font-size=\"11\" fill=\"{t.Accent(ElementKind.Block)}\">«flow» {E(item)}</text>");
        }
        if (pts.Count >= 2)
        {
            Head(sb, pts[^2], pts[^1], style.TargetHead, t);
            Head(sb, pts[1], pts[0], style.SourceHead, t);
        }
        var label = Notation.EdgeLabel(e.Relationship);
        var mid = Notation.Midpoint(pts);
        if (label != null && ReferenceEquals(e.Source, e.Target) && pts.Count >= 2)
        {
            // Self loops: label beside the loop, not on top of it.
            sb.Append($"<text x=\"{F(pts.Max(p => p.X) + 6)}\" y=\"{F(pts[0].Y + 15)}\" font-size=\"11\" font-style=\"italic\" fill=\"{t.Muted}\">{E(label)}</text>");
        }
        else if (label != null)
        {
            var w = label.Length * 5.9 + 6;
            sb.Append($"<rect x=\"{F(mid.X - w / 2)}\" y=\"{F(mid.Y - 17)}\" width=\"{F(w)}\" height=\"14\" rx=\"3\" fill=\"{t.LabelBg}\" fill-opacity=\"0.85\"/>");
            sb.Append($"<text x=\"{F(mid.X)}\" y=\"{F(mid.Y - 6)}\" text-anchor=\"middle\" font-size=\"11\" font-style=\"italic\" fill=\"{t.Muted}\">{E(label)}</text>");
        }
        if (e.Relationship.Kind is ElementKind.Association or ElementKind.Composition or ElementKind.Aggregation && pts.Count >= 2 && !string.IsNullOrEmpty(e.Relationship.Multiplicity))
            sb.Append($"<text x=\"{F(pts[^1].X + 6)}\" y=\"{F(pts[^1].Y - 6)}\" font-size=\"11\" fill=\"{t.Muted}\">{E(e.Relationship.Multiplicity)}</text>");
    }

    public static string Points(IEnumerable<Pt> pts) => string.Join(" ", pts.Select(p => $"{F(p.X)},{F(p.Y)}"));

    private static void Head(StringBuilder sb, Pt from, Pt tip, ArrowHead head, SvgTheme t)
    {
        if (head is ArrowHead.Dot or ArrowHead.CrowsFoot or ArrowHead.Bar)
        {
            var len = Notation.Dist(from, tip);
            if (len < 1e-6) return;
            var ux = (tip.X - from.X) / len; var uy = (tip.Y - from.Y) / len;
            var px = -uy; var py = ux;
            switch (head)
            {
                case ArrowHead.Dot:
                    sb.Append($"<circle cx=\"{F(tip.X - ux * 4)}\" cy=\"{F(tip.Y - uy * 4)}\" r=\"3.5\" fill=\"{t.Edge}\"/>");
                    break;
                case ArrowHead.Bar:
                    sb.Append($"<line x1=\"{F(tip.X - ux * 10 + px * 7)}\" y1=\"{F(tip.Y - uy * 10 + py * 7)}\" x2=\"{F(tip.X - ux * 10 - px * 7)}\" y2=\"{F(tip.Y - uy * 10 - py * 7)}\" stroke=\"{t.Edge}\" stroke-width=\"1.3\"/>");
                    break;
                case ArrowHead.CrowsFoot:
                    var b = new Pt(tip.X - ux * 14, tip.Y - uy * 14);
                    foreach (var off in new[] { -8.0, 0, 8 })
                        sb.Append($"<line x1=\"{F(b.X)}\" y1=\"{F(b.Y)}\" x2=\"{F(tip.X + px * off)}\" y2=\"{F(tip.Y + py * off)}\" stroke=\"{t.Edge}\" stroke-width=\"1.3\"/>");
                    break;
            }
            return;
        }
        var poly = Notation.ArrowPolygon(from, tip, head);
        if (poly.Length == 0) return;
        var (tag, fill) = head switch
        {
            ArrowHead.Open => ("polyline", "none"),
            ArrowHead.HollowTriangle or ArrowHead.HollowDiamond => ("polygon", t.Background),
            _ => ("polygon", t.Edge),
        };
        sb.Append($"<{tag} points=\"{Points(poly)}\" fill=\"{fill}\" stroke=\"{t.Edge}\" stroke-width=\"1.3\" stroke-linejoin=\"round\"/>");
    }

    private static void RenderNode(StringBuilder sb, ModelRepository m, Diagram d, DiagramNode n, Bounds b, SvgTheme t)
    {
        var c = Notation.Content(m, n.Element, d.DiagramType);
        var shape = Notation.ShapeOf(n.Element);
        // Per-shape overrides from the format bar: line and text colour apply to every notation.
        if (n.Element.Kind != ElementKind.Shape)
        {
            if (n.Style.TryGetValue("stroke", out var sc) && sc.Length > 0) t = t with { Stroke = sc, Ink = sc };
            if (n.Style.TryGetValue("text", out var tc) && tc.Length > 0) t = t with { Text = tc, Muted = tc };
        }
        // Compartment boxes take a custom fill on their header band; simple outlines (states, actions, use cases…) on the body.
        var fill = shape != NodeShape.Rectangle && n.Style.TryGetValue("fill", out var bf) && bf.Length > 0 ? bf : t.Fill(n.Element.Kind);
        var header = HeaderColor(m, n, t);
        var stroke = t.Stroke;
        var ink = t.Ink;
        sb.Append($"<g data-node=\"{n.Id}\">");
        if (n.Element.Kind == ElementKind.Shape)
        {
            RenderFreeShape(sb, n, b, c.Name, t);
            sb.Append("</g>");
            return;
        }
        switch (shape)
        {
            case NodeShape.StickFigure:
            {
                var cx = b.X + b.Width / 2; var top = b.Y;
                var s = Math.Min(b.Width / 60, b.Height / 90);
                sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" fill=\"transparent\"/>");
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(top + 10 * s)}\" r=\"{F(10 * s)}\" fill=\"{fill}\" stroke=\"{stroke}\"/>");
                sb.Append($"<path d=\"M{F(cx)},{F(top + 20 * s)} V{F(top + 50 * s)} M{F(cx - 18 * s)},{F(top + 30 * s)} H{F(cx + 18 * s)} M{F(cx)},{F(top + 50 * s)} L{F(cx - 15 * s)},{F(top + 70 * s)} M{F(cx)},{F(top + 50 * s)} L{F(cx + 15 * s)},{F(top + 70 * s)}\" stroke=\"{stroke}\" fill=\"none\"/>");
                Text(sb, cx, b.Bottom - 2, c.Name, false, "middle", color: t.Text);
                break;
            }
            case NodeShape.Ellipse:
                sb.Append($"<ellipse cx=\"{F(b.Center.X)}\" cy=\"{F(b.Center.Y)}\" rx=\"{F(b.Width / 2)}\" ry=\"{F(b.Height / 2)}\" fill=\"{fill}\" stroke=\"{stroke}\" filter=\"url(#msh)\"/>");
                WrappedCentered(sb, b.Center.X, b.Center.Y, b.Width * 0.8, c.Name, t.Text);
                break;
            case NodeShape.FilledCircle:
                sb.Append($"<circle cx=\"{F(b.Center.X)}\" cy=\"{F(b.Center.Y)}\" r=\"{F(Math.Min(b.Width, b.Height) / 2)}\" fill=\"{ink}\"/>");
                break;
            case NodeShape.BullsEye:
                sb.Append($"<circle cx=\"{F(b.Center.X)}\" cy=\"{F(b.Center.Y)}\" r=\"{F(Math.Min(b.Width, b.Height) / 2)}\" fill=\"{t.Background}\" stroke=\"{ink}\"/>");
                sb.Append($"<circle cx=\"{F(b.Center.X)}\" cy=\"{F(b.Center.Y)}\" r=\"{F(Math.Max(2, Math.Min(b.Width, b.Height) / 2 - 5))}\" fill=\"{ink}\"/>");
                break;
            case NodeShape.Diamond:
                sb.Append($"<polygon points=\"{F(b.Center.X)},{F(b.Y)} {F(b.Right)},{F(b.Center.Y)} {F(b.Center.X)},{F(b.Bottom)} {F(b.X)},{F(b.Center.Y)}\" fill=\"{fill}\" stroke=\"{stroke}\"/>");
                break;
            case NodeShape.Bar:
                sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" fill=\"{ink}\"/>");
                break;
            case NodeShape.SmallSquare:
                sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" fill=\"{t.Background}\" stroke=\"{ink}\"/>");
                Text(sb, b.Right + 4, b.Y - 4, c.Name, false, "start", color: t.Text);
                break;
            case NodeShape.Folder:
            {
                var tab = Math.Min(70, b.Width * 0.45);
                sb.Append($"<path d=\"M{F(b.X)},{F(b.Y)} h{F(tab)} v18 H{F(b.Right)} V{F(b.Bottom)} H{F(b.X)} Z M{F(b.X)},{F(b.Y + 18)} h{F(tab)}\" fill=\"{fill}\" stroke=\"{stroke}\"/>");
                Text(sb, b.Center.X, b.Y + 40, Trunc(c.Name, b.Width - 10), true, "middle", color: t.Text);
                break;
            }
            case NodeShape.Note:
                sb.Append($"<path d=\"M{F(b.X)},{F(b.Y)} H{F(b.Right - 12)} L{F(b.Right)},{F(b.Y + 12)} V{F(b.Bottom)} H{F(b.X)} Z\" fill=\"{fill}\" stroke=\"{stroke}\" filter=\"url(#msh)\"/>");
                Wrapped(sb, b.X + 6, b.Y + 16, b.Width - 14, b.Bottom - 4, c.Name, t.Text);
                break;
            case NodeShape.Lifeline:
            {
                var hh = Math.Min(Notation.LifelineHeader, b.Height);
                sb.Append($"<line x1=\"{F(b.Center.X)}\" y1=\"{F(b.Y + hh)}\" x2=\"{F(b.Center.X)}\" y2=\"{F(b.Bottom)}\" stroke=\"{t.Stroke}\" stroke-width=\"1.3\" stroke-dasharray=\"6,5\"/>");
                sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(hh)}\" rx=\"5\" fill=\"{t.Header(ElementKind.Block)}\" stroke=\"{stroke}\" filter=\"url(#msh)\"/>");
                sb.Append($"<rect x=\"{F(b.X + 1)}\" y=\"{F(b.Y + 1)}\" width=\"{F(Math.Max(0, b.Width - 2))}\" height=\"3\" rx=\"1.5\" fill=\"{t.Accent(ElementKind.Block)}\"/>");
                Text(sb, b.Center.X, b.Y + hh / 2 + 5, Trunc(c.Name, b.Width - 10), true, "middle", color: t.Text);
                break;
            }
            case NodeShape.Swimlane:
                sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" fill=\"{header}\" fill-opacity=\"0.18\" stroke=\"{stroke}\"/>");
                sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"30\" fill=\"{header}\" stroke=\"{stroke}\"/>");
                Text(sb, b.Center.X, b.Y + 20, Trunc(c.Name, b.Width - 10), true, "middle", color: t.Text);
                break;
            case NodeShape.Pool:
            {
                var band = 30.0;
                sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" fill=\"{t.Fill(ElementKind.Package)}\" fill-opacity=\"0.6\" stroke=\"{stroke}\"/>");
                sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(band)}\" height=\"{F(b.Height)}\" fill=\"{header}\" stroke=\"{stroke}\"/>");
                sb.Append($"<text transform=\"translate({F(b.X + band / 2 + 5)},{F(b.Center.Y)}) rotate(-90)\" text-anchor=\"middle\" font-size=\"12.5\" font-weight=\"600\" fill=\"{t.Text}\">{E(Trunc(c.Name, b.Height - 10))}</text>");
                break;
            }
            case NodeShape.Event:
            {
                var sub = n.Element.Get(FieldKeys.Subtype);
                var r = Math.Min(b.Width, b.Height) / 2;
                var sw = sub == "endEvent" ? 3.2 : 1.4;
                sb.Append($"<circle cx=\"{F(b.Center.X)}\" cy=\"{F(b.Center.Y)}\" r=\"{F(r)}\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"{F(sw)}\"/>");
                if (sub is "intermediateEvent" or "timerEvent" or "messageEvent")
                    sb.Append($"<circle cx=\"{F(b.Center.X)}\" cy=\"{F(b.Center.Y)}\" r=\"{F(r - 3.5)}\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"1.2\"/>");
                if (sub == "timerEvent")
                    sb.Append($"<path d=\"M{F(b.Center.X)},{F(b.Center.Y - r + 7)} V{F(b.Center.Y)} L{F(b.Center.X + r * .4)},{F(b.Center.Y + r * .3)}\" stroke=\"{stroke}\" fill=\"none\" stroke-width=\"1.4\"/>");
                if (sub == "messageEvent")
                    sb.Append($"<path d=\"M{F(b.Center.X - r * .45)},{F(b.Center.Y - r * .3)} h{F(r * .9)} v{F(r * .6)} h-{F(r * .9)} Z l{F(r * .45)},{F(r * .3)} l{F(r * .45)},-{F(r * .3)}\" stroke=\"{stroke}\" fill=\"none\"/>");
                if (!string.IsNullOrEmpty(c.Name) && !c.Name.StartsWith('<')) Text(sb, b.Center.X, b.Bottom + 14, c.Name, false, "middle", size: 11.5, color: t.Text);
                break;
            }
            case NodeShape.Gateway:
            {
                var sub = n.Element.Get(FieldKeys.Subtype);
                sb.Append($"<polygon points=\"{F(b.Center.X)},{F(b.Y)} {F(b.Right)},{F(b.Center.Y)} {F(b.Center.X)},{F(b.Bottom)} {F(b.X)},{F(b.Center.Y)}\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"1.4\"/>");
                var k = Math.Min(b.Width, b.Height) * 0.2;
                var cx = b.Center.X; var cy = b.Center.Y;
                var marker = sub switch
                {
                    "parallelGateway" => $"M{F(cx - k)},{F(cy)} H{F(cx + k)} M{F(cx)},{F(cy - k)} V{F(cy + k)}",
                    "exclusiveGateway" => $"M{F(cx - k * .8)},{F(cy - k * .8)} L{F(cx + k * .8)},{F(cy + k * .8)} M{F(cx + k * .8)},{F(cy - k * .8)} L{F(cx - k * .8)},{F(cy + k * .8)}",
                    _ => "",
                };
                if (marker.Length > 0) sb.Append($"<path d=\"{marker}\" stroke=\"{stroke}\" stroke-width=\"2.6\" fill=\"none\"/>");
                if (sub == "inclusiveGateway") sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(k)}\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"2.2\"/>");
                if (!string.IsNullOrEmpty(c.Name) && !c.Name.StartsWith('<')) Text(sb, b.Center.X, b.Bottom + 14, c.Name, false, "middle", size: 11.5, color: t.Text);
                break;
            }
            case NodeShape.DataObject:
            {
                if (n.Element.Get(FieldKeys.Subtype) == "dataStore")
                {
                    var ry = 6;
                    sb.Append($"<path d=\"M{F(b.X)},{F(b.Y + ry)} A{F(b.Width / 2)},{ry} 0 0 1 {F(b.Right)},{F(b.Y + ry)} V{F(b.Bottom - ry)} A{F(b.Width / 2)},{ry} 0 0 1 {F(b.X)},{F(b.Bottom - ry)} Z\" fill=\"{fill}\" stroke=\"{stroke}\"/>");
                    sb.Append($"<path d=\"M{F(b.X)},{F(b.Y + ry)} A{F(b.Width / 2)},{ry} 0 0 0 {F(b.Right)},{F(b.Y + ry)}\" fill=\"none\" stroke=\"{stroke}\"/>");
                }
                else
                {
                    sb.Append($"<path d=\"M{F(b.X)},{F(b.Y)} H{F(b.Right - 10)} L{F(b.Right)},{F(b.Y + 10)} V{F(b.Bottom)} H{F(b.X)} Z M{F(b.Right - 10)},{F(b.Y)} V{F(b.Y + 10)} H{F(b.Right)}\" fill=\"{fill}\" stroke=\"{stroke}\"/>");
                }
                Text(sb, b.Center.X, b.Bottom + 14, c.Name, false, "middle", size: 11.5, color: t.Text);
                break;
            }
            case NodeShape.Fragment:
            {
                var op = c.Name;
                var tagW = op.Length * 7.5 + 22;
                sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" rx=\"3\" fill=\"{t.Accent(ElementKind.Interaction)}\" fill-opacity=\"0.05\" stroke=\"{t.Accent(ElementKind.Interaction)}\" stroke-width=\"1.3\"/>");
                sb.Append($"<path d=\"M{F(b.X)},{F(b.Y)} H{F(b.X + tagW)} V{F(b.Y + 12)} L{F(b.X + tagW - 8)},{F(b.Y + 20)} H{F(b.X)} Z\" fill=\"{t.Header(ElementKind.Interaction)}\" stroke=\"{t.Accent(ElementKind.Interaction)}\" stroke-width=\"1.3\"/>");
                Text(sb, b.X + 7, b.Y + 14, op, true, "start", size: 11.5, color: t.Text);
                if (n.Element.Get(FieldKeys.Guard) is { Length: > 0 } guard)
                    Text(sb, b.X + tagW + 6, b.Y + 14, $"[{guard}]", false, "start", italic: true, size: 11.5, color: t.Muted);
                break;
            }
            case NodeShape.Rounded:
                sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" rx=\"12\" fill=\"{fill}\" stroke=\"{stroke}\" filter=\"url(#msh)\"/>");
                if (n.Element.Kind == ElementKind.State)
                {
                    Text(sb, b.Center.X, b.Y + 18, Trunc(c.Name, b.Width - 12), true, "middle", color: t.Text);
                    sb.Append($"<line x1=\"{F(b.X)}\" y1=\"{F(b.Y + 26)}\" x2=\"{F(b.Right)}\" y2=\"{F(b.Y + 26)}\" stroke=\"{stroke}\"/>");
                    var ly = b.Y + 42;
                    foreach (var line in c.Compartments.SelectMany(cc => cc.Lines))
                    {
                        if (ly > b.Bottom - 4) break;
                        Text(sb, b.X + 10, ly, Trunc(line, b.Width - 16), false, "start", size: 11.5, color: t.Text);
                        ly += Line;
                    }
                }
                else WrappedCentered(sb, b.Center.X, b.Center.Y, b.Width - 14, c.Name, t.Text);
                break;
            default:
                if (n.Element.Kind == ElementKind.ArchiElement)
                {
                    sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" rx=\"{(n.Element.Get(FieldKeys.Subtype).EndsWith("Service") ? 18 : 4)}\" fill=\"{header}\" stroke=\"{stroke}\" filter=\"url(#msh)\"/>");
                    Text(sb, b.Right - 8, b.Y + 16, Subtypes.ArchiGlyph(n.Element.Get(FieldKeys.Subtype)), false, "end", size: 13, color: t.Text);
                    WrappedCentered(sb, b.Center.X, b.Center.Y + 4, b.Width - 22, c.Name, t.Text);
                    break;
                }
                RenderCompartments(sb, b, c, fill, n.Element.Kind, t, header);
                break;
        }
        sb.Append("</g>");
    }

    /// <summary>General drawing shapes: a plain outline with centred, wrapped text (draw.io style).</summary>
    private static void RenderFreeShape(StringBuilder sb, DiagramNode n, Bounds b, string text, SvgTheme t)
    {
        var fill = n.Style.TryGetValue("fill", out var f) && f.Length > 0 ? f : t.NodeFill;
        var stroke = n.Style.TryGetValue("stroke", out var st) && st.Length > 0 ? st : t.Stroke;
        var attrs = $"fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"1.4\" filter=\"url(#msh)\"";
        double x = b.X, y = b.Y, r = b.Right, bo = b.Bottom, cx = b.Center.X, cy = b.Center.Y, w = b.Width, h = b.Height;
        var textY = cy;
        var textW = w - 14;
        switch (n.Element.Get(FieldKeys.Subtype))
        {
            case "text":
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" fill=\"transparent\"/>");
                break;
            case "rounded":
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" rx=\"{F(Math.Min(14, h / 3))}\" {attrs}/>");
                break;
            case "ellipse":
                sb.Append($"<ellipse cx=\"{F(cx)}\" cy=\"{F(cy)}\" rx=\"{F(w / 2)}\" ry=\"{F(h / 2)}\" {attrs}/>");
                textW = w * 0.75;
                break;
            case "diamond":
                sb.Append($"<polygon points=\"{F(cx)},{F(y)} {F(r)},{F(cy)} {F(cx)},{F(bo)} {F(x)},{F(cy)}\" {attrs}/>");
                textW = w * 0.6;
                break;
            case "triangle":
                sb.Append($"<polygon points=\"{F(cx)},{F(y)} {F(r)},{F(bo)} {F(x)},{F(bo)}\" {attrs}/>");
                textY = y + h * 0.66; textW = w * 0.55;
                break;
            case "hexagon":
            {
                var k = Math.Min(w * 0.22, h / 2);
                sb.Append($"<polygon points=\"{F(x + k)},{F(y)} {F(r - k)},{F(y)} {F(r)},{F(cy)} {F(r - k)},{F(bo)} {F(x + k)},{F(bo)} {F(x)},{F(cy)}\" {attrs}/>");
                textW = w - 2 * k;
                break;
            }
            case "parallelogram":
            {
                var k = Math.Min(w * 0.2, 30);
                sb.Append($"<polygon points=\"{F(x + k)},{F(y)} {F(r)},{F(y)} {F(r - k)},{F(bo)} {F(x)},{F(bo)}\" {attrs}/>");
                textW = w - 2 * k;
                break;
            }
            case "cylinder":
            {
                var ry = Math.Min(12, h / 6);
                sb.Append($"<path d=\"M{F(x)},{F(y + ry)} A{F(w / 2)},{F(ry)} 0 0 1 {F(r)},{F(y + ry)} V{F(bo - ry)} A{F(w / 2)},{F(ry)} 0 0 1 {F(x)},{F(bo - ry)} Z\" {attrs}/>");
                sb.Append($"<path d=\"M{F(x)},{F(y + ry)} A{F(w / 2)},{F(ry)} 0 0 0 {F(r)},{F(y + ry)}\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"1.4\"/>");
                textY = cy + ry / 2;
                break;
            }
            case "document":
            {
                var wv = Math.Min(12, h / 6);
                sb.Append($"<path d=\"M{F(x)},{F(y)} H{F(r)} V{F(bo - wv)} Q{F(x + w * 0.75)},{F(bo - 2.5 * wv)} {F(cx)},{F(bo - wv)} T{F(x)},{F(bo - wv)} Z\" {attrs}/>");
                textY = cy - wv / 2;
                break;
            }
            case "cloud":
            {
                sb.Append($"<path d=\"M{F(x + w * .25)},{F(y + h * .8)} C{F(x - w * .05)},{F(y + h * .8)} {F(x - w * .02)},{F(y + h * .35)} {F(x + w * .22)},{F(y + h * .38)} C{F(x + w * .2)},{F(y + h * .02)} {F(x + w * .6)},{F(y - h * .05)} {F(x + w * .64)},{F(y + h * .22)} C{F(x + w * .8)},{F(y + h * .05)} {F(x + w * 1.02)},{F(y + h * .3)} {F(x + w * .88)},{F(y + h * .48)} C{F(x + w * 1.06)},{F(y + h * .62)} {F(x + w * .95)},{F(y + h * .88)} {F(x + w * .75)},{F(y + h * .8)} Z\" {attrs}/>");
                textW = w * 0.62; textY = cy + h * 0.06;
                break;
            }
            case "callout":
            {
                var tail = Math.Min(18, h / 4);
                var body = bo - tail;
                sb.Append($"<path d=\"M{F(x)},{F(y)} H{F(r)} V{F(body)} H{F(x + w * .4)} L{F(x + w * .22)},{F(bo)} L{F(x + w * .25)},{F(body)} H{F(x)} Z\" {attrs}/>");
                textY = y + (body - y) / 2;
                break;
            }
            default:
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" rx=\"2\" {attrs}/>");
                break;
        }
        var ink = n.Style.TryGetValue("text", out var tc) && tc.Length > 0 ? tc : t.Text;
        if (!string.IsNullOrEmpty(text)) WrappedCentered(sb, cx, textY, Math.Max(20, textW), text, ink);
    }

    private static void RenderCompartments(StringBuilder sb, Bounds b, NodeContent c, string fill, ElementKind kind, SvgTheme t, string headerColor)
    {
        var stroke = t.Stroke;
        var headerH = (c.Keyword != null ? Line : 0) + Line + 12;
        sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" rx=\"4\" fill=\"{fill}\" stroke=\"{stroke}\" filter=\"url(#msh)\"/>");
        sb.Append($"<path d=\"M{F(b.X + 0.5)},{F(b.Y + headerH)} V{F(b.Y + 4)} Q{F(b.X + 0.5)},{F(b.Y + 0.5)} {F(b.X + 4)},{F(b.Y + 0.5)} H{F(b.Right - 4)} Q{F(b.Right - 0.5)},{F(b.Y + 0.5)} {F(b.Right - 0.5)},{F(b.Y + 4)} V{F(b.Y + headerH)} Z\" fill=\"{headerColor}\"/>");
        sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" rx=\"4\" fill=\"none\" stroke=\"{stroke}\"/>");
        sb.Append($"<rect x=\"{F(b.X + 1)}\" y=\"{F(b.Y + 1)}\" width=\"{F(Math.Max(0, b.Width - 2))}\" height=\"3\" rx=\"1.5\" fill=\"{t.Accent(kind)}\"/>");
        var y = b.Y + Line + 2;
        if (c.Keyword != null) { Text(sb, b.Center.X, y, Trunc(c.Keyword, b.Width - 8), false, "middle", size: 11, color: t.Muted); y += Line; }
        Text(sb, b.Center.X, y, Trunc(c.Name, b.Width - 8, 7.4), true, "middle", c.Italic, size: 13.5, color: t.Text);
        y = b.Y + headerH;
        foreach (var comp in c.Compartments)
        {
            if (y > b.Bottom - 6) break;
            sb.Append($"<line x1=\"{F(b.X)}\" y1=\"{F(y)}\" x2=\"{F(b.Right)}\" y2=\"{F(y)}\" stroke=\"{t.Divider}\"/>");
            y += Line;
            var traceSlot = comp.Title is "satisfiedBy" or "verifiedBy" or "refinedBy" or "derivedFrom" or "derived" or "tracedTo" or "satisfies" or "verifies" or "refines" or "allocatedTo" or "allocatedFrom";
            if (comp.Title != null) { Text(sb, b.Center.X, y - 2, comp.Title, false, "middle", italic: true, size: 10.5, color: traceSlot ? t.TraceTitle : t.Muted); y += Line; }
            foreach (var l in comp.Lines)
            {
                if (y > b.Bottom - 2) break;
                if (kind == ElementKind.Requirement && comp.Title == null)
                {
                    foreach (var wl in Wrap(l, Math.Max(8, (int)((b.Width - 12) / CharWidth))))
                    {
                        if (y > b.Bottom - 2) break;
                        Text(sb, b.X + 7, y - 2, wl, false, "start", size: 11.5, color: t.Text);
                        y += Line;
                    }
                    continue;
                }
                Text(sb, b.X + 7, y - 2, Trunc(l, b.Width - 14), false, "start", size: 11.5, color: traceSlot ? t.TraceText : t.Text);
                y += Line;
            }
            if (comp.Lines.Count == 0) y += Line;
            y -= Line - 8;
        }
    }

    private static string Trunc(string s, double width, double charWidth = CharWidth)
    {
        var max = Math.Max(1, (int)(width / charWidth));
        return s.Length <= max ? s : s[..Math.Max(1, max - 1)] + "…";
    }

    private static void Text(StringBuilder sb, double x, double y, string s, bool bold, string anchor, bool italic = false, double size = 12, string color = "#25303f") =>
        sb.Append($"<text x=\"{F(x)}\" y=\"{F(y)}\" text-anchor=\"{anchor}\" font-size=\"{F(size)}\" fill=\"{color}\"{(bold ? " font-weight=\"600\"" : "")}{(italic ? " font-style=\"italic\"" : "")}>{E(s)}</text>");

    private static void Wrapped(StringBuilder sb, double x, double y, double width, double bottom, string s, string color)
    {
        foreach (var line in Wrap(s, Math.Max(8, (int)(width / CharWidth))))
        {
            if (y > bottom) break;
            Text(sb, x, y, line, false, "start", size: 11.5, color: color);
            y += Line;
        }
    }

    private static void WrappedCentered(StringBuilder sb, double cx, double cy, double width, string s, string color)
    {
        var lines = Wrap(s, Math.Max(6, (int)(width / CharWidth))).Take(3).ToList();
        var y = cy - (lines.Count - 1) * Line / 2 + 4;
        foreach (var l in lines) { Text(sb, cx, y, l, false, "middle", color: color); y += Line; }
    }

    public static IEnumerable<string> Wrap(string s, int perLine)
    {
        foreach (var para in s.Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var w in para.Split(' '))
            {
                if (line.Length + w.Length + 1 > perLine && line.Length > 0) { yield return line.ToString(); line.Clear(); }
                if (line.Length > 0) line.Append(' ');
                line.Append(w);
            }
            yield return line.ToString();
        }
    }
}
