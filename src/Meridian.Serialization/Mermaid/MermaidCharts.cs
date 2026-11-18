using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization.Mermaid;

/// <summary>
/// Draws Mermaid charts that have no SysML equivalent on whiteboards: an Ishikawa (fishbone) diagram as a spine with an
/// effect head, category bones and causes; a radar chart as rings, axes, one closed line per curve and a legend; a treemap
/// as nested rectangles sized by value (squarified). Every shape that carries data remembers its role, so
/// <see cref="Export"/> writes the chart back as Mermaid with the current labels (and values, for radar and treemap).
/// See docs/MERMAID.md.
/// </summary>
internal sealed class MermaidCharts(ModelRepository m, Element target, string source)
{
    private const string ChartField = MermaidChartFields.ChartField, RoleField = MermaidChartFields.RoleField, ParentField = MermaidChartFields.ParentField,
        OrderField = MermaidChartFields.OrderField, IdField = MermaidChartFields.IdField, AxisField = MermaidChartFields.AxisField, ValueTag = MermaidChartFields.ValueTag;

    private static readonly string[] Palette = ["#4e79a7", "#f28e2b", "#e15759", "#76b7b2", "#59a14f", "#edc948", "#b07aa1", "#ff9da7", "#9c755f", "#bab0ac"];
    private const string Ink = "#1f2328";
    private const string Grid = "#c8ccd2";
    private const string Axis = "#8a9099";

    private Diagram _d = null!;

    public Diagram? Build(MGraph g, string name)
    {
        if (g.Chart is not { } chart) return null;
        _d = m.CreateDiagram(target, DiagramType.Whiteboard, name);
        m.Set(_d, FieldKeys.Documentation, $"Imported from {source} ({g.Keyword}).");
        switch (g.Kind)
        {
            case MermaidDiagramKind.Ishikawa: m.Set(_d, ChartField, "ishikawa"); Fishbone(chart); break;
            case MermaidDiagramKind.Radar: m.Set(_d, ChartField, "radar"); Radar(g, chart.Radar); break;
            case MermaidDiagramKind.Treemap: m.Set(_d, ChartField, "treemap"); Treemap(g, chart); break;
        }
        EdgeRouter.Invalidate(_d);
        return _d;
    }

    // ================================================================== drawing helpers

    private Element Shape(string subtype, string? name, Bounds b, string? fill = null, string? stroke = null, string? text = null, string? role = null)
    {
        // A blank (one space) name keeps decoration shapes from showing a "<Shape>" placeholder.
        var e = m.Create(target, ElementKind.Shape, string.IsNullOrEmpty(name) ? " " : name);
        m.Set(e, FieldKeys.Subtype, subtype);
        if (role != null) m.Set(e, RoleField, role);
        var n = m.AddNode(_d, e, new Bounds(Math.Round(b.X, 1), Math.Round(b.Y, 1), Math.Max(4, Math.Round(b.Width, 1)), Math.Max(4, Math.Round(b.Height, 1))), showRelationships: false);
        if (fill != null) m.SetNodeStyle(_d, n, "fill", fill);
        if (stroke != null) m.SetNodeStyle(_d, n, "stroke", stroke);
        if (text != null) m.SetNodeStyle(_d, n, "text", text);
        else if (fill is { Length: > 0 } and not "none" && Luminance(fill) is { } l) m.SetNodeStyle(_d, n, "text", l > 0.5 ? Ink : "#ffffff");
        return e;
    }

    private Element Dot(Pt c, double size, string fill, string stroke, string role) =>
        Shape("ellipse", null, new Bounds(c.X - size / 2, c.Y - size / 2, size, size), fill, stroke, role: role);

    private void Line(Element a, Element b, string subtype, string stroke, double width)
    {
        var r = m.Connect(ElementKind.Line, a, b, _d.Owner);
        m.Set(r, FieldKeys.Subtype, subtype);
        if (m.AddEdge(_d, r) is not { } edge) return;
        m.SetEdgeStyle(_d, edge, "stroke", stroke);
        if (Math.Abs(width - 1.3) > 0.01) m.SetEdgeStyle(_d, edge, "width", width.ToString(CultureInfo.InvariantCulture));
    }

    private void Mark(Element e, Element? parent, int order)
    {
        if (parent != null) m.Set(e, ParentField, parent.Id);
        m.Set(e, OrderField, order.ToString(CultureInfo.InvariantCulture));
    }

    internal static double? Luminance(string colour)
    {
        if (Hex(colour) is not { } rgb) return null;
        return (0.2126 * rgb.R + 0.7152 * rgb.G + 0.0722 * rgb.B) / 255.0;
    }

    private static (int R, int G, int B)? Hex(string colour)
    {
        var h = colour.Trim().TrimStart('#');
        if (h.Length is 3 or 4) h = string.Concat(h[..3].Select(c => $"{c}{c}"));
        if (h.Length is not (6 or 8) || !int.TryParse(h[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
        return ((v >> 16) & 255, (v >> 8) & 255, v & 255);
    }

    /// <summary>The colour mixed with white: 0 keeps it, 1 is white.</summary>
    private static string Tint(string colour, double amount)
    {
        if (Hex(colour) is not { } c) return colour;
        int Mix(int x) => (int)Math.Round(x + (255 - x) * amount);
        return $"#{Mix(c.R):x2}{Mix(c.G):x2}{Mix(c.B):x2}";
    }

    /// <summary>A colour Meridian can store from a Mermaid style value (#rgb, #rrggbb or a few names), else null.</summary>
    private static string? SafeColour(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (Regex.IsMatch(v, "^#[0-9a-fA-F]{3}([0-9a-fA-F]{3})?$", RegexOptions.None, TimeSpan.FromSeconds(1))) return v;
        return v.ToLowerInvariant() switch
        {
            "white" => "#ffffff", "black" => "#000000", "red" => "#ff0000", "green" => "#008000", "blue" => "#0000ff",
            "yellow" => "#ffff00", "orange" => "#ffa500", "gray" or "grey" => "#808080", "none" or "transparent" => "none",
            _ => null,
        };
    }

    // ================================================================== Ishikawa (fishbone)

    private const double RowH = 30, BoxW = 150, BoxH = 40, CauseH = 24;

    /// <summary>Where one cause goes: its label, the point its arrow ends on (the bone, or a joint on its parent's arrow) and the joints its own causes end on.</summary>
    private sealed record CausePlace(MChartNode Node, MChartNode Parent, Bounds Text, Pt Anchor, IReadOnlyList<Pt> Joints, int Depth);

    private static double TextWidth(string label) =>
        Math.Clamp(label.Split('\n').Max(l => l.Length) * 6.6 + 14, 40, 220);

    /// <summary>
    /// Lays out the causes of one category: one row each, from the category box towards the spine. A main cause's arrow
    /// ends on the bone; a sub-cause's arrow ends on a joint along its parent's arrow.
    /// </summary>
    private static List<CausePlace> PlaceCauses(MChartNode category, double sx, double dx, double h, double spineY, bool up)
    {
        var list = new List<CausePlace>();
        var row = 0;
        double RowY(int i) => up ? spineY - h + 35 + i * RowH : spineY + h - 35 - i * RowH;
        double BoneX(double y) => sx - dx * Math.Abs(y - spineY) / h;
        void Place(MChartNode node, MChartNode parent, Pt? anchor, int depth)
        {
            var y = RowY(row++);
            var a = anchor ?? new Pt(BoneX(y), y);
            var n = node.Children.Count;
            var right = a.X - 34 - 44 * n;
            var w = TextWidth(node.Label);
            var text = new Bounds(right - w, y - CauseH / 2, w, CauseH);
            var joints = Enumerable.Range(1, n).Select(k => new Pt(right + (a.X - right) * k / (n + 1.0), y + (a.Y - y) * k / (n + 1.0))).ToList();
            list.Add(new CausePlace(node, parent, text, a, joints, depth));
            if (depth >= MermaidLimits.MaxDepth) return;
            for (var k = 0; k < n; k++) Place(node.Children[k], node, joints[k], depth + 1);
        }
        foreach (var c in category.Children) Place(c, category, null, 1);
        return list;
    }

    private void Fishbone(MChart chart)
    {
        var effect = chart.Root;
        var cats = effect.Children;
        double Height(int i) => 50 + Math.Max(1, cats[i].Descendants().Count()) * RowH;
        var top = Enumerable.Range(0, cats.Count).Where(i => i % 2 == 0).ToList();
        var bottom = Enumerable.Range(0, cats.Count).Where(i => i % 2 == 1).ToList();
        var hTop = top.Count > 0 ? top.Max(Height) : 0;
        var hBottom = bottom.Count > 0 ? bottom.Max(Height) : 0;
        var spineY = 40 + (top.Count > 0 ? hTop + BoxH : 60);
        // How far left of its spine joint each category reaches (its box and its causes), measured with the joint at 0.
        var reach = 60.0;
        for (var i = 0; i < cats.Count; i++)
        {
            var up = i % 2 == 0;
            var h = up ? hTop : hBottom;
            var left = Math.Min(-h * 0.45 - BoxW / 2, PlaceCauses(cats[i], 0, h * 0.45, h, spineY, up).Select(p => p.Text.X).DefaultIfEmpty(0).Min());
            reach = Math.Max(reach, -left);
        }
        var columns = (cats.Count + 1) / 2;
        var spineXs = Enumerable.Range(0, columns).Select(j => 40 + 30 + reach + j * (reach + 40)).ToList();
        var headX = (columns > 0 ? spineXs[^1] : 240) + 70;

        const string bone = "#41484f";
        var tail = Dot(new Pt(40, spineY), 8, bone, bone, "joint");
        var joints = spineXs.Select(x => Dot(new Pt(x, spineY), 8, bone, bone, "joint")).ToList();
        var head = Shape("rounded", effect.Label, new Bounds(headX, spineY - 40, 190, 80), "#e15759", "#b5363a", role: "effect");
        // The spine: tail → one joint per column → head.
        var spine = joints.Prepend(tail).Append(head).ToList();
        for (var i = 0; i + 1 < spine.Count; i++) Line(spine[i], spine[i + 1], i + 2 == spine.Count ? "arrow" : "line", bone, 3);

        for (var i = 0; i < cats.Count; i++)
        {
            var up = i % 2 == 0;
            var h = up ? hTop : hBottom;
            var dx = h * 0.45;
            var sx = spineXs[i / 2];
            var colour = Palette[i % Palette.Length];
            var boxY = up ? spineY - h - BoxH : spineY + h;
            var cat = Shape("rectangle", cats[i].Label, new Bounds(sx - dx - BoxW / 2, boxY, BoxW, BoxH), Tint(colour, 0.75), colour, Ink, "category");
            Mark(cat, null, i);

            var made = new Dictionary<MChartNode, Element> { [cats[i]] = cat };
            var jointsOf = new Dictionary<MChartNode, List<Element>>();
            var endOf = new Dictionary<MChartNode, Element>();
            var order = new Dictionary<Element, int>();
            var previous = cat;
            var places = PlaceCauses(cats[i], sx, dx, h, spineY, up);
            foreach (var p in places)
            {
                var owner = made[p.Parent];
                var cause = Shape("text", p.Node.Label, p.Text, role: "cause");
                var n = order.GetValueOrDefault(owner);
                order[owner] = n + 1;
                Mark(cause, owner, n);
                made[p.Node] = cause;
                jointsOf[p.Node] = p.Joints.Select(j => Dot(j, 5, Axis, Axis, "joint")).ToList();
                // A main cause's arrow ends on a new joint on the bone; a sub-cause's on its joint along the parent's arrow.
                if (p.Depth == 1)
                {
                    var end = Dot(p.Anchor, 6, colour, colour, "joint");
                    Line(previous, end, "line", colour, 2);
                    previous = endOf[p.Node] = end;
                }
                else endOf[p.Node] = jointsOf[p.Parent][n];
            }
            // Each cause's arrow runs from its label through the joints of its own causes to where it ends.
            foreach (var p in places)
            {
                var chain = jointsOf[p.Node].Prepend(made[p.Node]).Append(endOf[p.Node]).ToList();
                for (var k = 0; k + 1 < chain.Count; k++) Line(chain[k], chain[k + 1], k + 2 == chain.Count ? "arrow" : "line", Axis, 1.3);
            }
            Line(previous, joints[i / 2], "arrow", colour, 2);
        }
    }

    // ================================================================== radar

    private const double RadarR = 200;

    private void Radar(MGraph g, MRadar radar)
    {
        if (radar.Max is { } mx) m.Set(_d, "mermaid.max", Num(mx));
        if (radar.Min is { } mn) m.Set(_d, "mermaid.min", Num(mn));
        if (radar.Ticks is { } tk) m.Set(_d, "mermaid.ticks", tk.ToString(CultureInfo.InvariantCulture));
        if (radar.Graticule is { } gr) m.Set(_d, "mermaid.graticule", gr);
        if (radar.ShowLegend is { } sl) m.Set(_d, "mermaid.showLegend", sl ? "true" : "false");
        var n = radar.Axes.Count;
        if (n == 0) return;
        var cx = 40 + 150 + RadarR;
        var cy = 40 + 50 + RadarR;
        var ticks = radar.Ticks ?? 5;
        for (var k = ticks; k >= 1; k--)
        {
            var r = RadarR * k / ticks;
            Shape("ellipse", null, new Bounds(cx - r, cy - r, 2 * r, 2 * r), "none", Grid, role: "ring");
        }
        if (radar.Graticule == "polygon") g.WarnOnce("graticule", g.Line, "the polygon grid is drawn as circles.");
        var centre = Dot(new Pt(cx, cy), 6, Axis, Axis, "centre");
        Pt At(int i, double r)
        {
            var a = -Math.PI / 2 + 2 * Math.PI * i / n;
            return new Pt(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }
        var axes = new List<Element>();
        for (var i = 0; i < n; i++)
        {
            var end = Dot(At(i, RadarR), 4, Axis, Axis, "axisEnd");
            Line(centre, end, "line", Axis, 1);
            var p = At(i, RadarR + 34);
            var a = Math.Cos(-Math.PI / 2 + 2 * Math.PI * i / n);
            var label = Shape("text", radar.Axes[i].Label, new Bounds(p.X - 70 + a * 50, p.Y - 15, 140, 30), role: "axis");
            m.Set(label, IdField, radar.Axes[i].Id);
            Mark(label, null, i);
            axes.Add(label);
        }

        var values = radar.Curves.SelectMany(c => Enumerable.Range(0, n).Select(i => radar.ValueOf(c, i))).OfType<double>().ToList();
        var lo = radar.Min ?? 0;
        var hi = radar.Max ?? (values.Count > 0 ? values.Max() : lo + 1);
        if (hi <= lo) hi = lo + 1;
        var showLegend = radar.ShowLegend ?? true;
        for (var c = 0; c < radar.Curves.Count; c++)
        {
            var curve = radar.Curves[c];
            var colour = Palette[c % Palette.Length];
            if (showLegend)
            {
                var entry = Shape("rounded", curve.Label, new Bounds(cx + RadarR + 130, cy - RadarR + c * 34, 150, 26), colour, colour, role: "curve");
                m.Set(entry, IdField, curve.Id);
                Mark(entry, null, c);
            }
            var points = new List<Element>();
            for (var i = 0; i < n; i++)
            {
                var v = radar.ValueOf(curve, i);
                var r = RadarR * Math.Clamp(((v ?? lo) - lo) / (hi - lo), 0, 1);
                var dot = Dot(At(i, r), 10, colour, colour, "point");
                m.Set(dot, ParentField, curve.Id);
                m.Set(dot, "mermaid.curveLabel", curve.Label);
                m.Set(dot, OrderField, c.ToString(CultureInfo.InvariantCulture));
                m.Set(dot, AxisField, radar.Axes[i].Id);
                if (v is { } value) m.Set(dot, ValueTag, Num(value));
                points.Add(dot);
            }
            for (var i = 0; i + 1 < points.Count; i++) Line(points[i], points[i + 1], "line", colour, 2);
            if (points.Count >= 3) Line(points[^1], points[0], "line", colour, 2);
        }
    }

    private static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);

    // ================================================================== treemap

    private void Treemap(MGraph g, MChart chart)
    {
        var sizes = new Dictionary<MChartNode, double>();
        var skipped = 0;
        double Size(MChartNode node)
        {
            double v;
            if (node.Children.Count > 0) v = node.Children.Sum(Size);
            else
            {
                v = node.Value ?? 0;
                if (v <= 0) { skipped++; v = 0; }
            }
            return sizes[node] = v;
        }
        Size(chart.Root);
        if (skipped > 0) g.Warn(0, $"{skipped} item{(skipped == 1 ? " has" : "s have")} no positive value and {(skipped == 1 ? "is" : "are")} drawn as a thin strip.");
        var tops = chart.Root.Children;
        var area = new Bounds(40, 40, 960, 600);
        var placed = Squarify(tops, sizes, area);
        for (var i = 0; i < tops.Count; i++) DrawItem(tops[i], placed[i], null, i, Palette[i % Palette.Length], 0, sizes, chart);
    }

    private void DrawItem(MChartNode node, Bounds b, Element? parent, int order, string colour, int depth, Dictionary<MChartNode, double> sizes, MChart chart)
    {
        var style = node.Class != null && chart.Classes.TryGetValue(node.Class, out var s) ? s : null;
        var fill = SafeColour(style?.GetValueOrDefault("fill"));
        var stroke = SafeColour(style?.GetValueOrDefault("stroke"));
        var text = SafeColour(style?.GetValueOrDefault("color"));
        var inner = new Bounds(b.X + 2, b.Y + 2, Math.Max(4, b.Width - 4), Math.Max(4, b.Height - 4));
        if (node.Children.Count == 0)
        {
            var leaf = Shape("rectangle", node.Label + "\n" + Display(node.Value ?? 0), inner, fill ?? Tint(colour, Math.Min(0.6, depth * 0.15)), stroke ?? "#ffffff", text, "leaf");
            Mark(leaf, parent, order);
            if (node.Value is { } v) m.Set(leaf, ValueTag, Num(v));
            return;
        }
        Shape("rectangle", null, inner, fill ?? Tint(colour, 0.82 - Math.Min(0.3, depth * 0.1)), stroke ?? colour, role: "frame");
        var header = Shape("text", node.Label, new Bounds(inner.X + 4, inner.Y + 2, Math.Max(4, inner.Width - 8), 22), text: text ?? Ink, role: "section");
        Mark(header, parent, order);
        var content = new Bounds(inner.X + 4, inner.Y + 26, Math.Max(4, inner.Width - 8), Math.Max(4, inner.Height - 30));
        var placed = Squarify(node.Children, sizes, content);
        for (var i = 0; i < node.Children.Count; i++) DrawItem(node.Children[i], placed[i], header, i, colour, depth + 1, sizes, chart);
    }

    private static string Display(double v) => v.ToString(Math.Abs(v) >= 1000 ? "#,0.##" : "0.##", CultureInfo.InvariantCulture);

    /// <summary>Squarified treemap (Bruls, Huizing, van Wijk): rectangles for <paramref name="items"/> (in their order) filling <paramref name="r"/>.</summary>
    internal static List<Bounds> Squarify(IReadOnlyList<MChartNode> items, IReadOnlyDictionary<MChartNode, double> sizes, Bounds r)
    {
        var result = new Bounds[items.Count];
        var total = items.Sum(i => sizes[i]);
        // Items without a value get a thin strip so they stay visible (and editable).
        var order = Enumerable.Range(0, items.Count).OrderByDescending(i => sizes[items[i]]).ToList();
        var zero = order.Where(i => sizes[items[i]] <= 0).ToList();
        var positive = order.Where(i => sizes[items[i]] > 0).ToList();
        var rect = r;
        if (zero.Count > 0)
        {
            var strip = Math.Min(r.Height / 2, 18 * zero.Count);
            var w = r.Width / zero.Count;
            for (var k = 0; k < zero.Count; k++) result[zero[k]] = new Bounds(r.X + k * w, r.Bottom - strip, w, strip);
            rect = new Bounds(r.X, r.Y, r.Width, Math.Max(1, r.Height - strip));
        }
        if (positive.Count == 0 || total <= 0) return result.ToList();
        var scale = rect.Width * rect.Height / total;
        var queue = new Queue<int>(positive);
        var row = new List<int>();
        double Area(int i) => sizes[items[i]] * scale;
        double Worst(List<int> rw, double side)
        {
            if (rw.Count == 0) return double.MaxValue;
            var sum = rw.Sum(Area);
            var max = rw.Max(Area);
            var min = rw.Min(Area);
            return Math.Max(side * side * max / (sum * sum), sum * sum / (side * side * min));
        }
        while (queue.Count > 0)
        {
            var side = Math.Min(rect.Width, rect.Height);
            var next = queue.Peek();
            var with = new List<int>(row) { next };
            if (row.Count == 0 || Worst(with, side) <= Worst(row, side))
            {
                row.Add(queue.Dequeue());
                continue;
            }
            rect = LayRow(row, rect, Area, result);
            row.Clear();
        }
        if (row.Count > 0) LayRow(row, rect, Area, result);
        return result.ToList();
    }

    private static Bounds LayRow(List<int> row, Bounds rect, Func<int, double> area, Bounds[] result)
    {
        var sum = row.Sum(area);
        if (rect.Width >= rect.Height)
        {
            // A column on the left.
            var w = rect.Height > 0 ? sum / rect.Height : 0;
            var y = rect.Y;
            foreach (var i in row)
            {
                var h = w > 0 ? area(i) / w : 0;
                result[i] = new Bounds(rect.X, y, w, h);
                y += h;
            }
            return new Bounds(rect.X + w, rect.Y, Math.Max(0, rect.Width - w), rect.Height);
        }
        else
        {
            // A row along the top.
            var h = rect.Width > 0 ? sum / rect.Width : 0;
            var x = rect.X;
            foreach (var i in row)
            {
                var w = h > 0 ? area(i) / h : 0;
                result[i] = new Bounds(x, rect.Y, w, h);
                x += w;
            }
            return new Bounds(rect.X, rect.Y + h, rect.Width, Math.Max(0, rect.Height - h));
        }
    }

    // ================================================================== export

    /// <summary>Writes a chart drawn by the import back as Mermaid, with the shapes' current labels and values.</summary>
    public static void Export(Diagram d, StringBuilder sb)
    {
        var shapes = d.Nodes.Select(n => n.Element).Where(e => e.Get(RoleField).Length > 0).Distinct().ToList();
        static int Order(Element e) => int.TryParse(e.Get(OrderField), NumberStyles.Integer, CultureInfo.InvariantCulture, out var o) ? o : int.MaxValue;
        IEnumerable<Element> Children(string role, string? parentId) => shapes
            .Where(e => e.Get(RoleField) == role && (parentId == null ? e.Get(ParentField).Length == 0 : e.Get(ParentField) == parentId))
            .OrderBy(Order).ThenBy(e => d.NodeFor(e)?.Bounds.Y ?? 0);
        switch (d.Get(ChartField))
        {
            case "ishikawa":
            {
                sb.Append("ishikawa-beta\n");
                var effect = shapes.FirstOrDefault(e => e.Get(RoleField) == "effect");
                sb.Append("    ").Append(Line(effect?.Name ?? d.Name)).Append('\n');
                void Causes(Element parent, int depth)
                {
                    foreach (var c in Children("cause", parent.Id))
                    {
                        sb.Append(new string(' ', 4 * depth)).Append(Line(c.Name)).Append('\n');
                        if (depth < MermaidLimits.MaxDepth) Causes(c, depth + 1);
                    }
                }
                foreach (var cat in Children("category", null))
                {
                    sb.Append("    ").Append(Line(cat.Name)).Append('\n');
                    Causes(cat, 2);
                }
                break;
            }
            case "radar":
            {
                sb.Append("radar-beta\n");
                var axes = Children("axis", null).ToList();
                var used = new HashSet<string>(StringComparer.Ordinal);
                var axisIds = axes.Select(a => Id(a, used)).ToList();
                if (axes.Count > 0)
                    sb.Append("    axis ").Append(string.Join(", ", axes.Select((a, i) => $"{axisIds[i]}[{Quote(a.Name)}]"))).Append('\n');
                var points = shapes.Where(e => e.Get(RoleField) == "point").ToList();
                var legend = Children("curve", null).ToList();
                var curves = legend.Select(l => (Id: l.Get(IdField), Label: l.Name)).ToList();
                foreach (var p in points.OrderBy(Order))
                    if (!curves.Any(c => c.Id == p.Get(ParentField)))
                        curves.Add((p.Get(ParentField), p.Get("mermaid.curveLabel") is { Length: > 0 } cl ? cl : p.Get(ParentField)));
                foreach (var (id, label) in curves)
                {
                    var mine = points.Where(p => p.Get(ParentField) == id).ToList();
                    var vals = axes.Select(a => mine.FirstOrDefault(p => p.Get(AxisField) == a.Get(IdField))?.Get(ValueTag) is { Length: > 0 } v
                        && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : (double?)null).ToList();
                    var curveId = Regex.IsMatch(id, @"^[A-Za-z_][\w-]*$", RegexOptions.None, TimeSpan.FromSeconds(1)) ? id : MermaidSource.IdFrom(label, used, "c");
                    var body = vals.All(v => v != null)
                        ? string.Join(", ", vals.Select(v => Num(v!.Value)))
                        : string.Join(", ", vals.Select((v, i) => v is { } x ? $"{axisIds[i]}: {Num(x)}" : null).OfType<string>());
                    sb.Append("    curve ").Append(curveId).Append('[').Append(Quote(label)).Append("]{").Append(body).Append("}\n");
                }
                foreach (var (key, word) in new[] { ("mermaid.max", "max"), ("mermaid.min", "min"), ("mermaid.ticks", "ticks"), ("mermaid.graticule", "graticule"), ("mermaid.showLegend", "showLegend") })
                    if (d.Get(key) is { Length: > 0 } v) sb.Append("    ").Append(word).Append(' ').Append(v).Append('\n');
                break;
            }
            case "treemap":
            {
                sb.Append("treemap-beta\n");
                void Items(string? parentId, int depth)
                {
                    var items = shapes.Where(e => e.Get(RoleField) is "section" or "leaf"
                            && (parentId == null ? e.Get(ParentField).Length == 0 : e.Get(ParentField) == parentId))
                        .OrderBy(Order).ToList();
                    foreach (var e in items)
                    {
                        sb.Append(new string(' ', 4 * depth));
                        if (e.Get(RoleField) == "section")
                        {
                            sb.Append(Quote(Line(e.Name))).Append('\n');
                            if (depth < MermaidLimits.MaxDepth) Items(e.Id, depth + 1);
                        }
                        else
                        {
                            var (label, value) = MermaidChartFields.LeafText(e);
                            sb.Append(Quote(label)).Append(": ").Append(Num(value)).Append('\n');
                        }
                    }
                }
                Items(null, 0);
                break;
            }
        }
    }

    private static string Id(Element axis, HashSet<string> used)
    {
        var id = axis.Get(IdField);
        if (Regex.IsMatch(id, @"^[A-Za-z_][\w-]*$", RegexOptions.None, TimeSpan.FromSeconds(1)) && used.Add(id)) return id;
        return MermaidSource.IdFrom(axis.Name, used, "a");
    }

    private static string Line(string s) => Regex.Replace(s, @"\s*\r?\n\s*", " ").Trim();

    private static string Quote(string s) => "\"" + Line(s).Replace('"', '\'') + "\"";
}

/// <summary>The fields a whiteboard drawn from a Mermaid Ishikawa, radar or treemap chart keeps on its shapes (see <see cref="MermaidCharts"/>).</summary>
public static class MermaidChartFields
{
    /// <summary>Diagram field: "ishikawa", "radar" or "treemap" on a whiteboard drawn from a Mermaid chart.</summary>
    public const string ChartField = "mermaid.chart";
    /// <summary>Shape field: what the shape stands for ("effect", "category", "cause", "axis", "curve", "point", "section", "leaf"…).</summary>
    public const string RoleField = "mermaid.role";
    /// <summary>Shape field: the id of the shape this one belongs to (the parent cause or section, the curve of a point).</summary>
    public const string ParentField = "mermaid.parent";
    /// <summary>Shape field: position among its siblings.</summary>
    public const string OrderField = "mermaid.order";
    /// <summary>Shape field: the Mermaid id of a radar axis or curve.</summary>
    public const string IdField = "mermaid.id";
    /// <summary>Shape field: the axis id of a radar point.</summary>
    public const string AxisField = "mermaid.axis";
    /// <summary>Tagged value holding a radar point's value (editable in the properties).</summary>
    public const string ValueTag = FieldKeys.TagPrefix + "value";

    public static bool IsChart(Diagram? d) => d is { DiagramType: DiagramType.Whiteboard } && d.Get(ChartField) is "ishikawa" or "radar" or "treemap";

    /// <summary>A treemap leaf's label and value: the value is the last line of its text when that is a number, else the stored value.</summary>
    public static (string Label, double Value) LeafText(Element e)
    {
        var lines = e.Name.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count > 1 && double.TryParse(lines[^1].Replace(",", "").Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v))
            return (string.Join(" ", lines.Take(lines.Count - 1)), v);
        var stored = double.TryParse(e.Get(ValueTag), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
        return (string.Join(" ", lines), stored);
    }

    /// <summary>Squarified treemap rectangles for <paramref name="values"/> (in their order) filling <paramref name="area"/>.</summary>
    public static List<Bounds> Squarify(IReadOnlyList<double> values, Bounds area)
    {
        var items = values.Select(v => new MChartNode { Value = v }).ToList();
        return MermaidCharts.Squarify(items, items.ToDictionary(i => i, i => i.Value ?? 0), area);
    }
}
