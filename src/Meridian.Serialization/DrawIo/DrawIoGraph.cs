using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Meridian.Core.Model;

namespace Meridian.Serialization.DrawIo;

/// <summary>A draw.io cell with its style parsed, its label as plain text and its bounds in page coordinates.</summary>
internal sealed class DCell
{
    public required string Id { get; init; }
    public string? Parent { get; init; }
    public string? Source { get; set; }
    public string? Target { get; set; }
    public bool Vertex { get; init; }
    public bool Edge { get; init; }
    /// <summary>The label as plain text (HTML removed, line breaks kept).</summary>
    public string Text { get; set; } = "";
    /// <summary>The label as stored (may be HTML).</summary>
    public string RawLabel { get; init; } = "";
    public Dictionary<string, string> Style { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The first style token when it is a bare word ("ellipse", "rhombus", "swimlane", "text"…).</summary>
    public string BaseShape { get; init; } = "";
    /// <summary>Bounds relative to the parent cell (vertices).</summary>
    public Bounds Local { get; init; }
    /// <summary>Bounds in page coordinates (vertices).</summary>
    public Bounds Abs { get; set; }
    /// <summary>Edge points in page coordinates: free source / target points and waypoints.</summary>
    public Pt? SourcePoint { get; set; }
    public Pt? TargetPoint { get; set; }
    public List<Pt> Points { get; } = new();
    public List<DCell> Children { get; } = new();
    /// <summary>Position along the parent edge (-1 source … 1 target) of a label that is a child of an edge.</summary>
    public double RelX { get; init; }
    /// <summary>Labels placed on this edge as child cells, with their position along it.</summary>
    public List<(string Text, double RelX)> EdgeLabels { get; } = new();

    /// <summary>The edge's own label followed by the labels placed in its middle.</summary>
    public string FullText => string.Join(" ", new[] { Text }.Concat(EdgeLabels.Where(l => Math.Abs(l.RelX) < 0.5).Select(l => l.Text)).Where(t => t.Length > 0));
    /// <summary>Extra properties of an &lt;object&gt; / &lt;UserObject&gt; wrapper (tooltip, link, custom data).</summary>
    public Dictionary<string, string> Data { get; } = new(StringComparer.Ordinal);

    public string S(string key) => Style.TryGetValue(key, out var v) ? v : "";
    public bool Has(string key) => Style.ContainsKey(key);
    public bool Flag(string key) => S(key) == "1";

    /// <summary>The shape name: the "shape" style, else the bare first token.</summary>
    public string Shape => S("shape") is { Length: > 0 } s ? s : BaseShape;

    public bool IsLayer => Parent is null or "0" && !Vertex && !Edge;

    public override string ToString() => $"{Id} {Shape} \"{Text}\"";
}

/// <summary>The cells of one page, linked into a tree, with page coordinates resolved.</summary>
internal sealed class DGraph
{
    public required string Name { get; init; }
    public Dictionary<string, DCell> ById { get; } = new(StringComparer.Ordinal);
    public List<DCell> Vertices { get; } = new();
    public List<DCell> Edges { get; } = new();
    public List<string> Warnings { get; } = new();

    public const int MaxCells = 20000;

    public static DGraph From(DrawIoPage page)
    {
        var g = new DGraph { Name = page.Name };
        var root = page.Model.Element("root");
        if (root == null) return g;
        var all = new List<DCell>();
        foreach (var x in root.Elements())
        {
            if (all.Count >= MaxCells) { g.Warnings.Add($"page \"{page.Name}\": more than {MaxCells} cells; the rest were ignored."); break; }
            XElement cell;
            var data = new Dictionary<string, string>(StringComparer.Ordinal);
            string? id, label;
            if (x.Name.LocalName == "mxCell") { cell = x; id = x.Attribute("id")?.Value; label = x.Attribute("value")?.Value; }
            else if (x.Element("mxCell") is { } inner)
            {
                // <object label="…" id="…" placeholders="1" custom="…"><mxCell …/></object>
                cell = inner;
                id = x.Attribute("id")?.Value ?? inner.Attribute("id")?.Value;
                label = x.Attribute("label")?.Value ?? inner.Attribute("value")?.Value;
                foreach (var a in x.Attributes())
                    if (a.Name.LocalName is not ("id" or "label" or "placeholders")) data[a.Name.LocalName] = a.Value;
            }
            else continue;
            if (string.IsNullOrEmpty(id)) continue;
            var styleText = cell.Attribute("style")?.Value ?? "";
            var (style, baseShape) = ParseStyle(styleText);
            var geo = cell.Element("mxGeometry");
            var c = new DCell
            {
                Id = id,
                Parent = cell.Attribute("parent")?.Value,
                Source = cell.Attribute("source")?.Value,
                Target = cell.Attribute("target")?.Value,
                Vertex = cell.Attribute("vertex")?.Value == "1",
                Edge = cell.Attribute("edge")?.Value == "1",
                RawLabel = label ?? "",
                Text = PlainText(label ?? "", style.TryGetValue("html", out var html) && html == "1"),
                Style = style,
                BaseShape = baseShape,
                Local = geo != null && geo.Attribute("relative")?.Value != "1"
                    ? new Bounds(Num(geo, "x"), Num(geo, "y"), Num(geo, "width"), Num(geo, "height"))
                    : default,
                RelX = geo != null && geo.Attribute("relative")?.Value == "1" ? Num(geo, "x") : 0,
            };
            foreach (var (k, v) in data) c.Data[k] = v;
            if (c.Edge && geo != null)
            {
                foreach (var p in geo.Elements("mxPoint"))
                {
                    var pt = new Pt(Num(p, "x"), Num(p, "y"));
                    if (p.Attribute("as")?.Value == "sourcePoint") c.SourcePoint = pt;
                    else if (p.Attribute("as")?.Value == "targetPoint") c.TargetPoint = pt;
                }
                if (geo.Elements("Array").FirstOrDefault(a => a.Attribute("as")?.Value == "points") is { } arr)
                    foreach (var p in arr.Elements("mxPoint")) c.Points.Add(new Pt(Num(p, "x"), Num(p, "y")));
            }
            all.Add(c);
            g.ById.TryAdd(c.Id, c);
        }
        foreach (var c in all)
            if (c.Parent != null && g.ById.TryGetValue(c.Parent, out var p) && !ReferenceEquals(p, c)) p.Children.Add(c);

        // Page coordinates: a vertex inside another vertex is placed relative to it; layers add nothing.
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        Pt Offset(DCell c)
        {
            if (c.Parent == null || !g.ById.TryGetValue(c.Parent, out var p) || !p.Vertex || !visiting.Add(c.Id)) return new Pt(0, 0);
            var o = Offset(p);
            visiting.Remove(c.Id);
            return new Pt(o.X + p.Local.X, o.Y + p.Local.Y);
        }
        foreach (var c in all)
        {
            var o = Offset(c);
            if (c.Vertex) { c.Abs = c.Local.Offset(o.X, o.Y); g.Vertices.Add(c); }
            else if (c.Edge)
            {
                if (c.SourcePoint is { } sp) c.SourcePoint = new Pt(sp.X + o.X, sp.Y + o.Y);
                if (c.TargetPoint is { } tp) c.TargetPoint = new Pt(tp.X + o.X, tp.Y + o.Y);
                for (var i = 0; i < c.Points.Count; i++) c.Points[i] = new Pt(c.Points[i].X + o.X, c.Points[i].Y + o.Y);
                g.Edges.Add(c);
            }
        }
        // Edge labels are child vertices of the edge (style "edgeLabel"): fold them into the edge's text.
        foreach (var e in g.Edges)
        {
            foreach (var ch in e.Children.Where(ch => ch.Vertex && ch.Text.Length > 0)) e.EdgeLabels.Add((ch.Text, ch.RelX));
        }
        g.Vertices.RemoveAll(v => v.Parent != null && g.ById.TryGetValue(v.Parent, out var p) && p.Edge);
        return g;
    }

    private static double Num(XElement e, string name) =>
        double.TryParse(e.Attribute(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : 0;

    /// <summary>"ellipse;whiteSpace=wrap;html=1;fillColor=#fff" → ({ellipse:"", whiteSpace:"wrap", …}, "ellipse").</summary>
    internal static (Dictionary<string, string> Style, string BaseShape) ParseStyle(string style)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var first = "";
        var i = 0;
        foreach (var part in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                var token = part.Trim();
                if (i == 0) first = token;
                d.TryAdd(token, "");
            }
            else d[part[..eq].Trim()] = part[(eq + 1)..].Trim();
            i++;
        }
        return (d, first);
    }

    private static readonly Regex Breaks = new(@"<\s*br\s*/?\s*>|</\s*(div|p|li|tr|h[1-6])\s*>|<\s*hr[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Tags = new(@"<[^>]*>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Spaces = new(@"[ \t ]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The label as plain text: HTML tags removed, &lt;br&gt; / block ends as new lines, entities decoded.</summary>
    internal static string PlainText(string label, bool html)
    {
        if (label.Length == 0) return "";
        var s = label;
        if (html || s.Contains('<'))
        {
            s = Breaks.Replace(s, "\n");
            s = Tags.Replace(s, "");
            s = WebUtility.HtmlDecode(s);
        }
        var lines = s.Replace("\r", "").Split('\n').Select(l => Spaces.Replace(l, " ").Trim()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        return string.Join("\n", lines);
    }

    /// <summary>The label split into parts at horizontal rules (UML class boxes drawn as one HTML label).</summary>
    internal static List<string> Compartments(string rawLabel)
    {
        var parts = Regex.Split(rawLabel, @"<\s*hr[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return parts.Select(p => PlainText(p, true)).ToList();
    }

    /// <summary>The cell an edge end attaches to, climbing out of decorations (activation bars, labels) to a known cell.</summary>
    public DCell? End(string? id, Func<DCell, bool> accept)
    {
        for (var depth = 0; id != null && depth < 20; depth++)
        {
            if (!ById.TryGetValue(id, out var c)) return null;
            if (accept(c)) return c;
            id = c.Parent;
        }
        return null;
    }
}
