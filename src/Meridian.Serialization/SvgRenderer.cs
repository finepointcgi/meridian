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
            ElementKind.Block or ElementKind.Class or ElementKind.Component or ElementKind.CommunicationLifeline => "#ffeec4",
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
            ElementKind.Block or ElementKind.Class or ElementKind.Component or ElementKind.CommunicationLifeline => "#3a2f14",
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

    /// <summary>Maps an embedded image's data URI to the URL drawn (the editor serves cached copies so the canvas markup stays small).</summary>
    public Func<string, string>? ImageHref { get; init; }

    /// <summary>Wraps shapes that carry a web link in an SVG anchor (exported files), so the link works in a browser.</summary>
    public bool LinkAnchors { get; init; }

    public string Defs =>
        $"<defs><filter id=\"msh\" x=\"-10%\" y=\"-10%\" width=\"125%\" height=\"135%\"><feDropShadow dx=\"0\" dy=\"2\" stdDeviation=\"2.5\" flood-color=\"{ShadowColor}\" flood-opacity=\"{ShadowOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"/></filter></defs>";
}

public static partial class SvgRenderer
{
    private const double Line = Notation.LineHeight;
    private const double CharWidth = 6.4; // approximate advance at 12px for truncation/wrapping
    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string E(string s) => WebUtility.HtmlEncode(s);

    /// <summary>
    /// A CSS colour from the model (shape style, stereotype colour) that is safe to write into an SVG attribute, or null.
    /// Model values come from collaborators and imported files, and the editor inserts this markup into the page, so
    /// anything other than a plain colour (hex, a named colour, rgb()/hsl()) is dropped rather than escaped.
    /// </summary>
    public static string? SafeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        return v.Length <= 64 && SafeColorPattern().IsMatch(v) ? v : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:#[0-9a-fA-F]{3,8}|[a-zA-Z]{1,32}|(?:rgb|rgba|hsl|hsla)\([0-9.,%\s/+-]{1,48}\))$")]
    private static partial System.Text.RegularExpressions.Regex SafeColorPattern();

    /// <summary>Text scale of the shape being drawn (its "fontSize" style relative to the 12 px default); 0 means 1.</summary>
    [ThreadStatic] private static double _fontScale;

    /// <summary>Line pattern a "dash" style asks for (dashed, dotted), or null (solid, or the notation's own).</summary>
    public static string? DashArray(string? dash) => dash switch { "dashed" => "6,4", "dotted" => "2,3", _ => null };

    /// <summary>A numeric style ("width", "fontSize") clamped to sane bounds, or null when unset or not a number.</summary>
    public static double? StyleNumber(IReadOnlyDictionary<string, string> style, string key, double min, double max) =>
        style.TryGetValue(key, out var v) && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) && double.IsFinite(x)
            ? Math.Clamp(x, min, max) : null;

    private static double FontScaleOf(IReadOnlyDictionary<string, string> style) => StyleNumber(style, "fontSize", 6, 48) is { } fs ? fs / 12 : 1;

    /// <summary>Line width and pattern of a shape, inherited by the outline parts that don't set their own.</summary>
    private static string LineStyleAttrs(IReadOnlyDictionary<string, string> style)
    {
        var a = "";
        if (StyleNumber(style, "width", 0.5, 12) is { } w) a += $" stroke-width=\"{F(w)}\"";
        if (DashArray(style.GetValueOrDefault("dash")) is { } da) a += $" stroke-dasharray=\"{da}\"";
        return a;
    }

    /// <summary>A colour style of a shape (fill, stroke, text), sanitised by <see cref="SafeColor"/>.</summary>
    private static string? StyleColor(DiagramNode n, string key) => n.Style.TryGetValue(key, out var v) ? SafeColor(v) : null;

    /// <param name="theme">Colours (light by default).</param>
    /// <param name="transparent">Leave the background unpainted (for PNG export onto slides and documents).</param>
    public static string Render(ModelRepository model, Diagram d, double margin = 24, SvgTheme? theme = null, bool transparent = false)
    {
        Func<DiagramNode, Bounds> bounds = n => Notation.EffectiveBounds(model, n, d.DiagramType);
        var view = DiagramExtras.VisibleView(d);   // hidden layers are not exported
        var nodes = view.Nodes;
        double minX = 0, minY = 0, maxX = 400, maxY = 300;
        if (nodes.Count > 0)
        {
            // Routed connectors may detour around shapes: keep their whole path inside the picture.
            var pts = EdgeRouter.RouteAll(view, bounds).Values.SelectMany(p => p).ToList();
            minX = Math.Min(nodes.Min(n => n.Bounds.X), pts.Count > 0 ? pts.Min(p => p.X) : double.MaxValue) - margin - (d.DiagramType == DiagramType.InternalBlock ? 40 : 0);
            minY = Math.Min(nodes.Min(n => n.Bounds.Y), pts.Count > 0 ? pts.Min(p => p.Y) : double.MaxValue) - margin - 30 - (d.DiagramType == DiagramType.InternalBlock ? 50 : 0);
            maxX = Math.Max(nodes.Max(n => n.Bounds.Right), pts.Count > 0 ? pts.Max(p => p.X) : double.MinValue) + margin + 60;
            maxY = Math.Max(nodes.Max(n => bounds(n).Bottom), pts.Count > 0 ? pts.Max(p => p.Y) : double.MinValue) + margin;
        }
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{F(minX)} {F(minY)} {F(maxX - minX)} {F(maxY - minY)}\" width=\"{F(maxX - minX)}\" height=\"{F(maxY - minY)}\" font-family=\"Inter, Helvetica, Arial, sans-serif\" font-size=\"12\">");
        var t = (theme ?? SvgTheme.Light) with { LinkAnchors = true };
        sb.Append(t.Defs);
        if (!transparent) sb.Append($"<rect x=\"{F(minX)}\" y=\"{F(minY)}\" width=\"{F(maxX - minX)}\" height=\"{F(maxY - minY)}\" fill=\"{t.Background}\"/>");
        // Whiteboards are free drawing: no SysML diagram frame.
        if (d.DiagramType != DiagramType.Whiteboard)
            sb.Append($"<rect x=\"{F(minX + 4)}\" y=\"{F(minY + 4)}\" width=\"{F(maxX - minX - 8)}\" height=\"{F(maxY - minY - 8)}\" fill=\"none\" stroke=\"#8a93a3\"/>");
        RenderHeader(sb, d, minX + 4, minY + 4, t);
        RenderBody(sb, model, d, bounds, null, t);
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>The SysML diagram frame header ("bdd [Package] Structure [Vehicle Structure]").</summary>
    public static void RenderHeader(StringBuilder sb, Diagram d, double x, double y, SvgTheme? theme = null)
    {
        if (d.DiagramType == DiagramType.Whiteboard) return;   // free drawing: no SysML frame header
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
        d = DiagramExtras.VisibleView(d);   // layers: hidden ones are skipped, the rest drawn bottom layer first
        // Shape bounds are asked for many times per render (routing, containers, edges): compute each once.
        var raw = bounds;
        var memo = new Dictionary<DiagramNode, Bounds>(d.Nodes.Count, ReferenceEqualityComparer.Instance);
        bounds = n => memo.TryGetValue(n, out var mb) ? mb : memo[n] = raw(n);
        // Routes are computed on the whole (visible) diagram, before culling, so they don't depend on the viewport.
        var routes = EdgeRouter.RouteAll(d, bounds, waypoints);
        var jumps = EdgeRouter.LineJumps(d) ? new List<(Pt A, Pt B)>() : null;
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
            foreach (var e in d.Edges) RenderEdge(sb, d, e, routes[e], t, jumps);
            foreach (var n in d.Nodes.Where(n => n.Element.Kind is not (ElementKind.CombinedFragment or ElementKind.Lifeline))) RenderNode(sb, model, d, n, bounds(n), t);
            return;
        }
        // Containers (pools, swimlanes, composite states, parts with nested shapes) first, then paths, then contents by depth.
        var containers = d.Nodes.Where(n => IsContainer(d, n)).OrderBy(n => Depth(d, n)).ToList();
        foreach (var n in containers) RenderNode(sb, model, d, n, bounds(n), t);
        foreach (var e in d.Edges) RenderEdge(sb, d, e, routes[e], t, jumps);
        if (d.DiagramType == DiagramType.FeatureModel) FeatureDiagramSvg.RenderGroupArcs(sb, d, bounds, waypoints, t);
        foreach (var n in d.Nodes.Except(containers).OrderBy(n => Depth(d, n))) RenderNode(sb, model, d, n, bounds(n), t);
        if (d.DiagramType == DiagramType.Timing) RenderTimeAxis(sb, d, bounds, t);
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
        if (StyleColor(n, "fill") is { } f) return f;
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
            if (m.OfKind(ElementKind.Stereotype).FirstOrDefault(s => s.Name == name)?.Get(FieldKeys.Color) is { Length: > 0 } c && SafeColor(c) is { } safe) return safe;
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

    private static void RenderEdge(StringBuilder sb, Diagram d, DiagramEdge e, IReadOnlyList<Pt> pts, SvgTheme t, List<(Pt A, Pt B)>? jumps)
    {
        var style = Notation.StyleOf(e.Relationship);
        if (e.Style.TryGetValue("stroke", out var ec) && SafeColor(ec) is { } edgeColor) t = t with { Edge = edgeColor };
        var width = StyleNumber(e.Style, "width", 0.5, 12) ?? 1.3;
        var labelSize = StyleNumber(e.Style, "fontSize", 6, 48) ?? 11;
        if (e.Style.TryGetValue("text", out var etc) && SafeColor(etc) is { } labelColor) t = t with { Muted = labelColor };
        if (e.Relationship.Kind == ElementKind.Message)
            style = e.Relationship.Get(FieldKeys.MessageSort) switch
            {
                "reply" => style with { Dashed = true, TargetHead = ArrowHead.Open },
                "asynchCall" or "asynchSignal" => style with { TargetHead = ArrowHead.Open },
                _ => style,
            };
        var dash = style.Dotted ? " stroke-dasharray=\"2,3\"" : style.Dashed ? " stroke-dasharray=\"6,4\"" : "";
        if (e.Style.GetValueOrDefault("dash") is { } ds) dash = DashArray(ds) is { } da ? $" stroke-dasharray=\"{da}\"" : ds == "solid" ? "" : dash;
        var hops = jumps is { Count: > 0 } ? EdgeRouter.Hops(pts, jumps) : null;
        if (hops is { Count: > 0 })
            sb.Append($"<path d=\"{JumpPath(pts, hops)}\" fill=\"none\" stroke=\"{t.Edge}\" stroke-width=\"{F(width)}\"{dash}/>");
        else
            sb.Append($"<polyline points=\"{Points(pts)}\" fill=\"none\" stroke=\"{t.Edge}\" stroke-width=\"{F(width)}\"{dash}/>");
        jumps?.AddRange(EdgeRouter.Segments(pts));
        if (e.Relationship.Kind == ElementKind.CommunicationLink) RenderLinkMessages(sb, e.Relationship, pts, t);
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
            sb.Append($"<text x=\"{F(pts.Max(p => p.X) + 6)}\" y=\"{F(pts[0].Y + 15)}\" font-size=\"{F(labelSize)}\" font-style=\"italic\" fill=\"{t.Muted}\">{E(label)}</text>");
        }
        else if (label != null)
        {
            var ls = labelSize / 11;
            var w = label.Length * 5.9 * ls + 6;
            sb.Append($"<rect x=\"{F(mid.X - w / 2)}\" y=\"{F(mid.Y - 3 - 14 * ls)}\" width=\"{F(w)}\" height=\"{F(14 * ls)}\" rx=\"3\" fill=\"{t.LabelBg}\" fill-opacity=\"0.85\"/>");
            sb.Append($"<text x=\"{F(mid.X)}\" y=\"{F(mid.Y - 6)}\" text-anchor=\"middle\" font-size=\"{F(labelSize)}\" font-style=\"italic\" fill=\"{t.Muted}\">{E(label)}</text>");
        }
        if (e.Relationship.Kind is ElementKind.Association or ElementKind.Composition or ElementKind.Aggregation && pts.Count >= 2 && !string.IsNullOrEmpty(e.Relationship.Multiplicity))
            sb.Append($"<text x=\"{F(pts[^1].X + 6)}\" y=\"{F(pts[^1].Y - 6)}\" font-size=\"11\" fill=\"{t.Muted}\">{E(e.Relationship.Multiplicity)}</text>");
        // The source end's multiplicity, next to where the path leaves its source.
        if (e.Relationship.Kind is ElementKind.Association or ElementKind.Composition or ElementKind.Aggregation && pts.Count >= 2 && e.Relationship.Get(FieldKeys.SourceMultiplicity) is { Length: > 0 } sm)
            sb.Append($"<text class=\"source-mult\" x=\"{F(pts[0].X + 6)}\" y=\"{F(pts[0].Y - 6)}\" font-size=\"11\" fill=\"{t.Muted}\">{E(sm)}</text>");
    }

    /// <summary>SVG path for a polyline with small semicircular hops at the given crossings.</summary>
    public static string JumpPath(IReadOnlyList<Pt> pts, IReadOnlyList<(int Segment, Pt At)> hops, double r = 5)
    {
        var p = new StringBuilder($"M{F(pts[0].X)},{F(pts[0].Y)}");
        var k = 0;
        for (var i = 1; i < pts.Count; i++)
        {
            Pt a = pts[i - 1], b = pts[i];
            var len = Notation.Dist(a, b);
            var ux = len > 0 ? (b.X - a.X) / len : 0; var uy = len > 0 ? (b.Y - a.Y) / len : 0;
            var sweep = ux + uy > 0 ? 1 : 0;   // hops bulge up on horizontal runs, right on vertical ones
            for (; k < hops.Count && hops[k].Segment == i; k++)
            {
                var c = hops[k].At;
                p.Append($" L{F(c.X - ux * r)},{F(c.Y - uy * r)} A{F(r)},{F(r)} 0 0 {sweep} {F(c.X + ux * r)},{F(c.Y + uy * r)}");
            }
            p.Append($" L{F(b.X)},{F(b.Y)}");
        }
        return p.ToString();
    }

    /// <summary>Communication diagrams: numbered messages beside a link, each with a small arrow showing its direction.</summary>
    private static void RenderLinkMessages(StringBuilder sb, Relationship link, IReadOnlyList<Pt> pts, SvgTheme t)
    {
        var msgs = InteractionNotation.LinkMessages(link);
        if (msgs.Count == 0 || pts.Count < 2) return;
        var m = Notation.Midpoint(pts);
        var seg = Enumerable.Range(1, pts.Count - 1).Select(i => (pts[i - 1], pts[i])).OrderBy(sg => Notation.DistanceToSegment(m, sg.Item1, sg.Item2)).First();
        var len = Notation.Dist(seg.Item1, seg.Item2);
        if (len < 1) return;
        var ux = (seg.Item2.X - seg.Item1.X) / len; var uy = (seg.Item2.Y - seg.Item1.Y) / len;
        // Stack the messages on one side of the link (above it, or left of a vertical one).
        var nx = -uy; var ny = ux;
        if (ny > 0 || Math.Abs(ny) < 1e-6 && nx > 0) { nx = -nx; ny = -ny; }
        var accent = t.Accent(ElementKind.Interaction);
        var horizontal = Math.Abs(ux) >= Math.Abs(uy);
        for (var i = 0; i < msgs.Count; i++)
        {
            var (text, forward) = msgs[i];
            var tw = text.Length * 6;
            // Beside a horizontal link the messages stack upwards, centred on it; beside a vertical one they stack downwards on its left.
            var c = horizontal
                ? new Pt(m.X - tw / 2 - 16, m.Y + ny * (12 + i * 16))
                : new Pt(m.X + nx * 14, m.Y - (msgs.Count - 1) * 8 + i * 16);
            var dx = forward ? ux : -ux; var dy = forward ? uy : -uy;
            var tail = new Pt(c.X - dx * 9, c.Y - dy * 6 - (horizontal ? 0 : 0)); var tip = new Pt(c.X + dx * 9, c.Y + dy * 6);
            sb.Append($"<line x1=\"{F(tail.X)}\" y1=\"{F(tail.Y)}\" x2=\"{F(tip.X)}\" y2=\"{F(tip.Y)}\" stroke=\"{accent}\" stroke-width=\"1.3\"/>");
            sb.Append($"<polygon points=\"{Points(Notation.ArrowPolygon(tail, tip, ArrowHead.Filled, 6))}\" fill=\"{accent}\"/>");
            if (horizontal) Text(sb, c.X + 14, c.Y + 4, text, false, "start", size: 11, color: t.Text);
            else Text(sb, c.X + (nx < 0 ? -8 : 8), c.Y + 4, text, false, nx < 0 ? "end" : "start", size: 11, color: t.Text);
        }
    }

    /// <summary>Timing diagrams: a time axis under the lowest lifeline, aligned with its plot area.</summary>
    private static void RenderTimeAxis(StringBuilder sb, Diagram d, Func<DiagramNode, Bounds> bounds, SvgTheme t)
    {
        var lifelines = d.Nodes.Where(n => n.Element.Kind == ElementKind.TimingLifeline).ToList();
        if (lifelines.Count == 0) return;
        var b = lifelines.Select(bounds).MaxBy(x => x.Bottom);
        var (t0, t1) = InteractionNotation.TimeRange(d);
        var lw = Math.Min(InteractionNotation.TimingLabelWidth, b.Width / 2);
        double x0 = b.X + lw + 10, x1 = b.Right - 10, y = b.Bottom + 14;
        if (x1 <= x0) return;
        sb.Append($"<line x1=\"{F(x0)}\" y1=\"{F(y)}\" x2=\"{F(x1)}\" y2=\"{F(y)}\" stroke=\"{t.Stroke}\" stroke-width=\"1.2\"/>");
        var step = InteractionNotation.TickStep(t1 - t0);
        var unit = d.Get(InteractionNotation.TimeUnit);
        for (var v = Math.Ceiling(t0 / step) * step; v <= t1 + 1e-9; v += step)
        {
            var x = x0 + (v - t0) / (t1 - t0) * (x1 - x0);
            sb.Append($"<line x1=\"{F(x)}\" y1=\"{F(y - 4)}\" x2=\"{F(x)}\" y2=\"{F(y + 4)}\" stroke=\"{t.Stroke}\"/>");
            Text(sb, x, y + 17, v.ToString("0.##", CultureInfo.InvariantCulture), false, "middle", size: 10.5, color: t.Muted);
        }
        Text(sb, x1, y + 31, unit.Length > 0 ? $"time ({unit})" : "time", false, "end", italic: true, size: 10.5, color: t.Muted);
    }

    public static string Points(IEnumerable<Pt> pts) => string.Join(" ", pts.Select(p => $"{F(p.X)},{F(p.Y)}"));

    private static void Head(StringBuilder sb, Pt from, Pt tip, ArrowHead head, SvgTheme t)
    {
        if (head is ArrowHead.Dot or ArrowHead.CrowsFoot or ArrowHead.Bar or ArrowHead.HollowDot)
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
                case ArrowHead.HollowDot:
                    sb.Append($"<circle cx=\"{F(tip.X - ux * 5)}\" cy=\"{F(tip.Y - uy * 5)}\" r=\"4.5\" fill=\"{t.Background}\" stroke=\"{t.Edge}\" stroke-width=\"1.3\"/>");
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
        var link = DiagramExtras.LinkOf(n);
        // Only the normalised http(s)/mailto form is written: the raw value could be any scheme (e.g. javascript:).
        var href = t.LinkAnchors && DiagramExtras.IsExternalLink(link) ? DiagramExtras.NormalizeLink(link) : null;
        var anchor = href != null;
        if (anchor) sb.Append($"<a href=\"{E(href!)}\" target=\"_blank\" rel=\"noopener noreferrer\">");
        var scale = _fontScale;
        _fontScale = FontScaleOf(n.Style);
        try { RenderNodeCore(sb, m, d, n, b, t); }
        finally { _fontScale = scale; }
        if (link != null) LinkBadge(sb, b, link, t);
        if (anchor) sb.Append("</a>");
    }

    /// <summary>The small chain-link icon in the bottom-right corner of a shape that has a hyperlink.</summary>
    private static void LinkBadge(StringBuilder sb, Bounds b, string link, SvgTheme t)
    {
        var r = DiagramExtras.LinkBadgeBounds(b);
        var accent = t.TraceTitle;
        double x = r.X, y = r.Y, s = r.Width;
        sb.Append($"<g class=\"link-badge\"><title>{E(link)}</title>");
        sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(s)}\" height=\"{F(s)}\" rx=\"3.5\" fill=\"{t.NodeFill}\" stroke=\"{accent}\" stroke-width=\"0.9\"/>");
        // Two interlocking chain links, drawn diagonally.
        sb.Append($"<path d=\"M{F(x + s * .44)},{F(y + s * .64)} L{F(x + s * .30)},{F(y + s * .78)} A{F(s * .14)},{F(s * .14)} 0 0 1 {F(x + s * .22)},{F(y + s * .70)} L{F(x + s * .36)},{F(y + s * .56)} M{F(x + s * .56)},{F(y + s * .36)} L{F(x + s * .70)},{F(y + s * .22)} A{F(s * .14)},{F(s * .14)} 0 0 1 {F(x + s * .78)},{F(y + s * .30)} L{F(x + s * .64)},{F(y + s * .44)} M{F(x + s * .40)},{F(y + s * .60)} L{F(x + s * .60)},{F(y + s * .40)}\" fill=\"none\" stroke=\"{accent}\" stroke-width=\"1.4\" stroke-linecap=\"round\"/>");
        sb.Append("</g>");
    }

    private static void RenderNodeCore(StringBuilder sb, ModelRepository m, Diagram d, DiagramNode n, Bounds b, SvgTheme t)
    {
        var c = Notation.Content(m, n.Element, d.DiagramType);
        var shape = Notation.ShapeOf(n.Element);
        // Per-shape overrides from the format bar: line and text colour apply to every notation.
        if (n.Element.Kind != ElementKind.Shape)
        {
            if (StyleColor(n, "stroke") is { } sc) t = t with { Stroke = sc, Ink = sc };
            if (StyleColor(n, "text") is { } tc) t = t with { Text = tc, Muted = tc };
        }
        // Compartment boxes take a custom fill on their header band; simple outlines (states, actions, use cases…) on the body.
        var fill = shape != NodeShape.Rectangle && StyleColor(n, "fill") is { } bf ? bf : t.Fill(n.Element.Kind);
        var header = HeaderColor(m, n, t);
        var stroke = t.Stroke;
        var ink = t.Ink;
        sb.Append($"<g data-node=\"{E(n.Id)}\"{LineStyleAttrs(n.Style)}>");
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
            case NodeShape.Pseudostate:
                RenderPseudostate(sb, n.Element, b, t);
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
                // UML package: a tabbed folder. With nested contents the name sits in the tab (UML 2.5 §12.3.5),
                // otherwise in the body; the keyword («profile», stereotypes) goes with it.
                const double tabH = 20;
                var nested = ContainerLayout.IsContainer(d, n);
                var name = nested ? Trunc(c.Name, b.Width * 0.7 - 16) : Trunc(c.Name, b.Width - 12);
                var tab = nested ? Math.Clamp(name.Length * CharWidth + 20, 50, b.Width * 0.7) : Math.Clamp(b.Width * 0.38, 30, 80);
                sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y + tabH)}\" width=\"{F(b.Width)}\" height=\"{F(Math.Max(0, b.Height - tabH))}\" fill=\"{fill}\" stroke=\"{stroke}\" filter=\"url(#msh)\"/>");
                sb.Append($"<path d=\"M{F(b.X)},{F(b.Y + tabH)} V{F(b.Y + 3)} Q{F(b.X)},{F(b.Y)} {F(b.X + 3)},{F(b.Y)} H{F(b.X + tab - 3)} Q{F(b.X + tab)},{F(b.Y)} {F(b.X + tab)},{F(b.Y + 3)} V{F(b.Y + tabH)} Z\" fill=\"{header}\" stroke=\"{stroke}\"/>");
                if (nested)
                {
                    Text(sb, b.X + 10, b.Y + 14.5, name, true, "start", color: t.Text);
                    if (c.Keyword != null) Text(sb, b.X + 10, b.Y + tabH + 16, Trunc(c.Keyword, b.Width - 20), false, "start", size: 11, color: t.Muted);
                }
                else
                {
                    var cy = b.Y + tabH + (b.Height - tabH) / 2;
                    if (c.Keyword != null)
                    {
                        Text(sb, b.Center.X, cy - 4, Trunc(c.Keyword, b.Width - 10), false, "middle", size: 11, color: t.Muted);
                        Text(sb, b.Center.X, cy + 13, name, true, "middle", color: t.Text);
                    }
                    else Text(sb, b.Center.X, cy + 5, name, true, "middle", color: t.Text);
                }
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
                // Operands: a dashed separator above every operand but the first, each with its guard below the line.
                var bands = InteractionOperands.Layout(n, b);
                foreach (var band in bands)
                {
                    var guard = band.Operand?.Get(FieldKeys.Guard) ?? n.Element.Get(FieldKeys.Guard);
                    if (band.Index > 0)
                        sb.Append($"<line class=\"operand-sep\" data-operand=\"{E(band.Operand!.Id)}\" x1=\"{F(b.X)}\" y1=\"{F(band.Top)}\" x2=\"{F(b.Right)}\" y2=\"{F(band.Top)}\" stroke=\"{t.Accent(ElementKind.Interaction)}\" stroke-width=\"1.2\" stroke-dasharray=\"7,4\"/>");
                    if (guard.Length == 0) continue;
                    var gx = band.Index == 0 ? b.X + tagW + 6 : b.X + 10;
                    var gy = band.Index == 0 ? b.Y + 14 : band.Top + 15;
                    Text(sb, gx, gy, Trunc($"[{guard}]", b.Right - gx - 4), false, "start", italic: true, size: 11.5, color: t.Muted);
                }
                break;
            }
            case NodeShape.Timeline:
                RenderTimeline(sb, d, n.Element, b, c.Name, t);
                break;
            case NodeShape.DurationMarker:
            {
                var y = b.Center.Y + 6;
                var ac = t.Accent(ElementKind.Interaction);
                sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" fill=\"transparent\"/>");
                sb.Append($"<path d=\"M{F(b.X)},{F(y - 8)} V{F(y + 8)} M{F(b.Right)},{F(y - 8)} V{F(y + 8)} M{F(b.X)},{F(y)} H{F(b.Right)}\" stroke=\"{ac}\" stroke-width=\"1.3\" fill=\"none\"/>");
                sb.Append($"<polygon points=\"{Points(Notation.ArrowPolygon(new Pt(b.Center.X, y), new Pt(b.X, y), ArrowHead.Filled, 7))}\" fill=\"{ac}\"/>");
                sb.Append($"<polygon points=\"{Points(Notation.ArrowPolygon(new Pt(b.Center.X, y), new Pt(b.Right, y), ArrowHead.Filled, 7))}\" fill=\"{ac}\"/>");
                Text(sb, b.Center.X, y - 6, Trunc(c.Name, b.Width), false, "middle", size: 11.5, color: t.Text);
                break;
            }
            case NodeShape.TimeMarker:
            {
                var ac = t.Accent(ElementKind.Interaction);
                sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" fill=\"transparent\"/>");
                sb.Append($"<line x1=\"{F(b.Center.X)}\" y1=\"{F(b.Y + 20)}\" x2=\"{F(b.Center.X)}\" y2=\"{F(b.Bottom)}\" stroke=\"{ac}\" stroke-width=\"1.3\" stroke-dasharray=\"4,3\"/>");
                sb.Append($"<path d=\"M{F(b.Center.X - 6)},{F(b.Y + 20)} H{F(b.Center.X + 6)}\" stroke=\"{ac}\" stroke-width=\"1.3\"/>");
                Text(sb, b.Center.X, b.Y + 13, Trunc(c.Name, Math.Max(b.Width, 60)), false, "middle", size: 11.5, color: t.Text);
                break;
            }
            case NodeShape.InteractionUse or NodeShape.InlineInteraction:
            {
                var isRef = shape == NodeShape.InteractionUse;
                var tag = isRef ? "ref" : "sd " + n.Element.Name;
                var tagW = Math.Min(b.Width - 4, tag.Length * 7 + 20);
                var ac = t.Accent(ElementKind.Interaction);
                sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" rx=\"3\" fill=\"{fill}\" stroke=\"{ac}\" stroke-width=\"1.3\" filter=\"url(#msh)\"/>");
                sb.Append($"<path d=\"M{F(b.X)},{F(b.Y)} H{F(b.X + tagW)} V{F(b.Y + 12)} L{F(b.X + tagW - 8)},{F(b.Y + 20)} H{F(b.X)} Z\" fill=\"{t.Header(ElementKind.Interaction)}\" stroke=\"{ac}\" stroke-width=\"1.3\"/>");
                Text(sb, b.X + 6, b.Y + 14, Trunc(tag, tagW - 10), true, "start", size: 11.5, color: t.Text);
                if (isRef) WrappedCentered(sb, b.Center.X, b.Center.Y + 6, b.Width - 16, c.Name, t.Text);
                else RenderInlineSequence(sb, n.Element, new Bounds(b.X + 6, b.Y + 24, b.Width - 12, b.Height - 28), t);
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
                FeatureDiagramSvg.RenderBadges(sb, n.Element, b, t);
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

    /// <summary>History (H / H*), entry point (circle), exit point (circle with a cross) and terminate (cross) pseudostates, named beside them.</summary>
    private static void RenderPseudostate(StringBuilder sb, Element e, Bounds b, SvgTheme t)
    {
        var kind = Pseudostates.KindOf(e);
        var cx = b.Center.X; var cy = b.Center.Y;
        var r = Math.Min(b.Width, b.Height) / 2;
        var ink = t.Ink;
        switch (kind)
        {
            case Pseudostates.Terminate:
            {
                var k = r * 0.8;
                sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" fill=\"transparent\"/>");
                sb.Append($"<path d=\"M{F(cx - k)},{F(cy - k)} L{F(cx + k)},{F(cy + k)} M{F(cx + k)},{F(cy - k)} L{F(cx - k)},{F(cy + k)}\" stroke=\"{ink}\" stroke-width=\"2\" fill=\"none\"/>");
                break;
            }
            case Pseudostates.ShallowHistory or Pseudostates.DeepHistory:
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\" fill=\"{t.Background}\" stroke=\"{ink}\"/>");
                sb.Append($"<text x=\"{F(cx)}\" y=\"{F(cy + r * 0.38)}\" text-anchor=\"middle\" font-size=\"{F(r * 1.05)}\" font-weight=\"700\" fill=\"{ink}\">{(kind == Pseudostates.DeepHistory ? "H*" : "H")}</text>");
                break;
            default:   // entry / exit point
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\" fill=\"{t.Background}\" stroke=\"{ink}\" stroke-width=\"1.3\"/>");
                if (kind == Pseudostates.ExitPoint)
                {
                    var k = r * 0.68;
                    sb.Append($"<path d=\"M{F(cx - k)},{F(cy - k)} L{F(cx + k)},{F(cy + k)} M{F(cx + k)},{F(cy - k)} L{F(cx - k)},{F(cy + k)}\" stroke=\"{ink}\" stroke-width=\"1.3\" fill=\"none\"/>");
                }
                break;
        }
        if (e.Name.Length > 0) Text(sb, b.Right + 4, b.Y - 3, e.Name, false, "start", size: 11.5, color: t.Text);
    }

    /// <summary>Timing lifeline: name and state labels on the left, the state timeline as a step line over the diagram's time range.</summary>
    private static void RenderTimeline(StringBuilder sb, Diagram d, Element e, Bounds b, string name, SvgTheme t)
    {
        var lw = Math.Min(InteractionNotation.TimingLabelWidth, b.Width / 2);
        var ac = t.Accent(ElementKind.Interaction);
        sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" rx=\"3\" fill=\"{t.NodeFill}\" stroke=\"{t.Stroke}\" filter=\"url(#msh)\"/>");
        sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(lw)}\" height=\"{F(b.Height)}\" rx=\"3\" fill=\"{t.Header(ElementKind.Interaction)}\" stroke=\"{t.Stroke}\"/>");
        Text(sb, b.X + 8, b.Y + 17, Trunc(name, lw - 12), true, "start", size: 12, color: t.Text);
        var states = InteractionNotation.StatesOf(e);
        double top = b.Y + 26, bottom = b.Bottom - 8;
        var rowH = (bottom - top) / Math.Max(1, states.Count);
        double Row(string s) => top + (IndexOf(states, s) + 0.5) * rowH;
        double px0 = b.X + lw + 10, px1 = b.Right - 10;
        foreach (var s in states)
        {
            var y = Row(s);
            Text(sb, b.X + lw - 6, y + 4, Trunc(s, lw - 14), false, "end", size: 11, color: t.Muted);
            sb.Append($"<line x1=\"{F(px0)}\" y1=\"{F(y)}\" x2=\"{F(px1)}\" y2=\"{F(y)}\" stroke=\"{t.Divider}\" stroke-dasharray=\"2,4\"/>");
        }
        var line = InteractionNotation.TimelineOf(e);
        if (line.Count == 0 || px1 <= px0) return;
        var (t0, t1) = InteractionNotation.TimeRange(d);
        double X(double time) => px0 + (time - t0) / (t1 - t0) * (px1 - px0);
        var pts = new List<Pt>();
        foreach (var (time, state) in line)
        {
            var x = Math.Clamp(X(time), px0, px1);
            if (pts.Count > 0) pts.Add(new Pt(x, pts[^1].Y));
            pts.Add(new Pt(x, Row(state)));
        }
        pts.Add(new Pt(px1, pts[^1].Y));
        sb.Append($"<polyline points=\"{Points(pts)}\" fill=\"none\" stroke=\"{ac}\" stroke-width=\"2\" stroke-linejoin=\"round\"/>");
    }

    private static int IndexOf(IReadOnlyList<string> list, string s)
    {
        for (var i = 0; i < list.Count; i++) if (list[i] == s) return i;
        return 0;
    }

    /// <summary>Interaction overview: the inline sequence of an "sd" frame as miniature lifelines and messages.</summary>
    private static void RenderInlineSequence(StringBuilder sb, Element e, Bounds area, SvgTheme t)
    {
        var parts = InteractionNotation.InlineParticipants(e);
        var msgs = InteractionNotation.InlineMessages(e);
        if (parts.Count == 0 || area.Width < 20 || area.Height < 30) return;
        var colW = area.Width / parts.Count;
        double X(string p) => area.X + (IndexOf(parts, p) + 0.5) * colW;
        const double headH = 18;
        foreach (var p in parts)
        {
            var x = X(p); var w = Math.Min(colW - 8, Math.Max(40, p.Length * 6.5 + 10));
            sb.Append($"<rect x=\"{F(x - w / 2)}\" y=\"{F(area.Y)}\" width=\"{F(w)}\" height=\"{F(headH)}\" rx=\"3\" fill=\"{t.Header(ElementKind.Block)}\" stroke=\"{t.Stroke}\"/>");
            Text(sb, x, area.Y + 13, Trunc(p, w - 4), false, "middle", size: 10.5, color: t.Text);
            sb.Append($"<line x1=\"{F(x)}\" y1=\"{F(area.Y + headH)}\" x2=\"{F(x)}\" y2=\"{F(area.Bottom)}\" stroke=\"{t.Stroke}\" stroke-dasharray=\"4,4\"/>");
        }
        var gap = Math.Min(22, (area.Height - headH - 6) / Math.Max(1, msgs.Count));
        var y = area.Y + headH + gap * 0.8;
        foreach (var (from, to, text) in msgs)
        {
            if (y > area.Bottom - 2) break;
            double x0 = X(from), x1 = X(to);
            var self = Math.Abs(x1 - x0) < 1;
            if (self)
                sb.Append($"<polyline points=\"{F(x0)},{F(y - 6)} {F(x0 + 18)},{F(y - 6)} {F(x0 + 18)},{F(y + 2)} {F(x0 + 2)},{F(y + 2)}\" fill=\"none\" stroke=\"{t.Edge}\"/>");
            else
            {
                sb.Append($"<line x1=\"{F(x0)}\" y1=\"{F(y)}\" x2=\"{F(x1)}\" y2=\"{F(y)}\" stroke=\"{t.Edge}\"/>");
                sb.Append($"<polygon points=\"{Points(Notation.ArrowPolygon(new Pt(x0, y), new Pt(x1, y), ArrowHead.Filled, 7))}\" fill=\"{t.Edge}\"/>");
            }
            if (text.Length > 0)
                Text(sb, self ? x0 + 24 : (x0 + x1) / 2, y - 3, Trunc(text, Math.Max(40, Math.Abs(x1 - x0) - 6)), false, self ? "start" : "middle", size: 10, color: t.Text);
            y += gap;
        }
    }

    /// <summary>General drawing shapes: a plain outline with centred, wrapped text (draw.io style).</summary>
    private static void RenderFreeShape(StringBuilder sb, DiagramNode n, Bounds b, string text, SvgTheme t)
    {
        var fill = StyleColor(n, "fill") ?? t.NodeFill;
        var stroke = StyleColor(n, "stroke") ?? t.Stroke;
        var attrs = $"fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"1.4\" filter=\"url(#msh)\"";
        double x = b.X, y = b.Y, r = b.Right, bo = b.Bottom, cx = b.Center.X, cy = b.Center.Y, w = b.Width, h = b.Height;
        var textY = cy;
        var textW = w - 14;
        switch (n.Element.Get(FieldKeys.Subtype))
        {
            case DiagramExtras.ImageSubtype:
            {
                var data = n.Element.Get(DiagramExtras.ImageField);
                if (DiagramExtras.IsSafeImageDataUri(data))
                {
                    var href = t.ImageHref?.Invoke(data) ?? data;
                    sb.Append($"<image href=\"{E(href)}\" x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" preserveAspectRatio=\"xMidYMid meet\"/>");
                    if (StyleColor(n, "stroke") is { } border)
                        sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" fill=\"none\" stroke=\"{E(border)}\" stroke-width=\"1.4\"/>");
                }
                else
                {
                    // Placeholder: a dashed frame with a little landscape.
                    sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" rx=\"4\" fill=\"{t.FrameFill}\" stroke=\"{t.Muted}\" stroke-dasharray=\"5,4\"/>");
                    sb.Append($"<path d=\"M{F(cx - 14)},{F(cy + 8)} l9,-11 l6,7 l4,-4 l9,8 Z\" fill=\"{t.Muted}\" fill-opacity=\"0.5\"/><circle cx=\"{F(cx + 7)}\" cy=\"{F(cy - 8)}\" r=\"3\" fill=\"{t.Muted}\" fill-opacity=\"0.5\"/>");
                }
                // A caption goes under the picture.
                if (!string.IsNullOrEmpty(text))
                    WrappedCentered(sb, cx, bo + 14, Math.Max(40, w), text, StyleColor(n, "text") ?? t.Text);
                return;
            }
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
        var ink = StyleColor(n, "text") ?? t.Text;
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
        sb.Append($"<text x=\"{F(x)}\" y=\"{F(y)}\" text-anchor=\"{anchor}\" font-size=\"{F(size * (_fontScale > 0 ? _fontScale : 1))}\" fill=\"{color}\"{(bold ? " font-weight=\"600\"" : "")}{(italic ? " font-style=\"italic\"" : "")}>{E(s)}</text>");

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
