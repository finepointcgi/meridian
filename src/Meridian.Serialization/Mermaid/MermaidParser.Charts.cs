using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Meridian.Serialization.Mermaid;

/// <summary>One item of an Ishikawa diagram (the effect, a category or a cause) or of a treemap (a section or a leaf).</summary>
internal sealed class MChartNode
{
    public string Label { get; set; } = "";
    /// <summary>A treemap leaf's value.</summary>
    public double? Value { get; set; }
    /// <summary>A treemap item's <c>:::class</c>.</summary>
    public string? Class { get; set; }
    public List<MChartNode> Children { get; } = new();
    public int Line { get; init; }

    public IEnumerable<MChartNode> Descendants() => Children.SelectMany(c => c.Descendants().Prepend(c));
}

internal sealed class MRadarCurve
{
    public required string Id { get; init; }
    public string Label { get; set; } = "";
    public int Line { get; init; }
    /// <summary>Values given in order (matched to the axes in their order).</summary>
    public List<double> Positional { get; } = new();
    /// <summary>Values given as <c>axis: value</c>.</summary>
    public Dictionary<string, double> Keyed { get; } = new(StringComparer.Ordinal);
}

/// <summary>A radar-beta chart: its axes, curves and options.</summary>
internal sealed class MRadar
{
    public List<(string Id, string Label)> Axes { get; } = new();
    public List<MRadarCurve> Curves { get; } = new();
    public double? Max { get; set; }
    public double? Min { get; set; }
    public int? Ticks { get; set; }
    public string? Graticule { get; set; }
    public bool? ShowLegend { get; set; }

    /// <summary>The value of <paramref name="curve"/> on the axis at <paramref name="index"/>, or null when it has none.</summary>
    public double? ValueOf(MRadarCurve curve, int index)
    {
        if (curve.Keyed.TryGetValue(Axes[index].Id, out var v)) return v;
        return index < curve.Positional.Count ? curve.Positional[index] : null;
    }
}

/// <summary>The data of a chart drawn on a whiteboard: an Ishikawa tree, a treemap forest or a radar chart.</summary>
internal sealed class MChart
{
    /// <summary>Ishikawa: the effect, holding categories and causes. Treemap: an unnamed root holding the top-level items.</summary>
    public MChartNode Root { get; } = new();
    public MRadar Radar { get; } = new();
    /// <summary>classDef styles (fill, stroke, color) by class name.</summary>
    public Dictionary<string, Dictionary<string, string>> Classes { get; } = new(StringComparer.Ordinal);
}

/// <summary>Ishikawa (fishbone), radar and treemap charts.</summary>
internal static partial class MermaidParser
{
    private static int IndentOf(string s)
    {
        var n = 0;
        foreach (var c in s)
        {
            if (c == ' ') n++;
            else if (c == '\t') n += 4;
            else break;
        }
        return n;
    }

    /// <summary>
    /// <c>ishikawa-beta</c>: the first line is the effect (the fish head); each following line is a cause, nested by
    /// indentation. The least indented causes are the categories (the big bones).
    /// </summary>
    private static void Ishikawa(MGraph g, MermaidBlock block)
    {
        var chart = g.Chart = new MChart();
        var stack = new List<(int Indent, MChartNode Node)>();
        var haveRoot = false;
        var count = 0;
        foreach (var line in block.Body)
        {
            var t = line.Text.Trim();
            if (t.Length == 0) continue;
            var label = MermaidSource.Clean(t);
            if (!haveRoot)
            {
                chart.Root.Label = label;
                haveRoot = true;
                g.Title ??= MermaidSource.Name(label);
                continue;
            }
            if (++count > MermaidLimits.MaxNodes)
            {
                g.Warn(line.No, $"more than {MermaidLimits.MaxNodes} causes; the rest were ignored.");
                break;
            }
            var indent = IndentOf(line.Text);
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            if (stack.Count >= MermaidLimits.MaxDepth)
            {
                g.WarnOnce("depth", line.No, $"causes nested deeper than {MermaidLimits.MaxDepth} levels were attached at that level.");
                stack.RemoveAt(stack.Count - 1);
            }
            var node = new MChartNode { Label = label, Line = line.No };
            (stack.Count > 0 ? stack[^1].Node : chart.Root).Children.Add(node);
            stack.Add((indent, node));
        }
        if (!haveRoot) g.Warn(0, "has no effect (the first line names the problem); nothing to draw.");
    }

    private static readonly Regex TreemapItem = new(
        @"^(?<name>""[^""]*""|'[^']*')\s*(?:[:,]\s*(?<value>-?[0-9_.,]+))?\s*(?::::\s*(?<cls>[A-Za-z_][A-Za-z0-9_-]*))?\s*;?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex ClassDefLine = new(@"^classDef\s+(?<name>[A-Za-z_][A-Za-z0-9_-]*)(?:\s+(?<styles>[^;]*))?;?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary><c>treemap-beta</c>: quoted names, nested by indentation; <c>"Name": value</c> is a leaf, a bare name a section.</summary>
    private static void Treemap(MGraph g, MermaidBlock block)
    {
        var chart = g.Chart = new MChart();
        var stack = new List<(int Indent, MChartNode Node)>();
        var count = 0;
        foreach (var line in block.Body)
        {
            var t = line.Text.Trim();
            if (t.Length == 0) continue;
            if (Word(t, "title")) { if (MermaidSource.Name(t[5..]) is { Length: > 0 } title) g.Title ??= title; continue; }
            if (Word(t, "classDef"))
            {
                if (ClassDefLine.Match(t) is { Success: true } cd) chart.Classes[cd.Groups["name"].Value] = StyleOf(cd.Groups["styles"].Value);
                else g.Warn(line.No, "could not read this classDef; skipped.");
                continue;
            }
            var mm = TreemapItem.Match(t);
            if (!mm.Success)
            {
                g.Warn(line.No, $"“{Short(t)}” is not a treemap item (write \"Name\" for a section or \"Name\": 10 for a value); skipped.");
                continue;
            }
            if (++count > MermaidLimits.MaxNodes)
            {
                g.Warn(line.No, $"more than {MermaidLimits.MaxNodes} items; the rest were ignored.");
                break;
            }
            var node = new MChartNode { Label = MermaidSource.Clean(mm.Groups["name"].Value), Line = line.No, Class = mm.Groups["cls"].Success ? mm.Groups["cls"].Value : null };
            if (mm.Groups["value"].Success)
            {
                var raw = mm.Groups["value"].Value.Replace("_", "").Replace(",", "");
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)) node.Value = v;
                else g.Warn(line.No, $"“{mm.Groups["value"].Value}” is not a number; “{node.Label}” has no value.");
            }
            var indent = IndentOf(line.Text);
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            if (stack.Count >= MermaidLimits.MaxDepth)
            {
                g.WarnOnce("depth", line.No, $"items nested deeper than {MermaidLimits.MaxDepth} levels were attached at that level.");
                stack.RemoveAt(stack.Count - 1);
            }
            var parent = stack.Count > 0 ? stack[^1].Node : chart.Root;
            if (parent.Value != null && parent.Children.Count == 0)
                g.Warn(parent.Line, $"“{parent.Label}” has a value and items inside it; its size is the sum of its items.");
            parent.Children.Add(node);
            stack.Add((indent, node));
        }
        if (chart.Root.Children.Count == 0) g.Warn(0, "has no items; nothing to draw.");
    }

    /// <summary>"fill:#f96,stroke:#333,color:#fff" → {fill, stroke, color}.</summary>
    private static Dictionary<string, string> StyleOf(string styles)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in styles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var c = part.IndexOf(':');
            if (c > 0) d[part[..c].Trim()] = part[(c + 1)..].Trim();
        }
        return d;
    }

    private static string Short(string s) => s.Length > 40 ? s[..37] + "…" : s;

    private static readonly Regex RadarAxis = new(@"^(?<id>[A-Za-z_][\w-]*)\s*(?:\[\s*(?<label>""[^""]*""|'[^']*')\s*\])?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex RadarCurve = new(@"^(?<id>[A-Za-z_][\w-]*)\s*(?:\[\s*(?<label>""[^""]*""|'[^']*')\s*\])?\s*\{(?<body>[^{}]*)\}$", RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
    private static readonly Regex RadarKeyed = new(@"^(?<axis>[A-Za-z_][\w-]*)\s*:?\s*(?<value>-?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex RadarOption = new(@"^(?<name>showLegend|ticks|max|min|graticule)\s+(?<value>\S+)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary><c>radar-beta</c>: axes, curves (values in axis order or as <c>axis: value</c>) and options.</summary>
    private static void Radar(MGraph g, MermaidBlock block)
    {
        var chart = g.Chart = new MChart();
        var radar = chart.Radar;
        var axisIds = new HashSet<string>(StringComparer.Ordinal);
        var curveIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in RadarStatements(g, block))
        {
            var t = s.Text;
            if (Word(t, "title")) { if (MermaidSource.Name(t[5..]) is { Length: > 0 } title) g.Title ??= title; continue; }
            if (Word(t, "axis"))
            {
                foreach (var part in MermaidSource.SplitTop(t[4..], ','))
                {
                    var p = part.Trim();
                    if (p.Length == 0) continue;
                    var a = RadarAxis.Match(p);
                    if (!a.Success) { g.Warn(s.No, $"“{Short(p)}” is not an axis (write id or id[\"Label\"]); skipped."); continue; }
                    var id = a.Groups["id"].Value;
                    if (!axisIds.Add(id)) { g.Warn(s.No, $"axis “{id}” is defined twice; the second was skipped."); continue; }
                    if (radar.Axes.Count >= MermaidLimits.MaxNodes) { g.WarnOnce("axes", s.No, $"more than {MermaidLimits.MaxNodes} axes; the rest were ignored."); break; }
                    radar.Axes.Add((id, a.Groups["label"].Success ? MermaidSource.Clean(a.Groups["label"].Value) : id));
                }
                continue;
            }
            if (Word(t, "curve"))
            {
                foreach (var part in MermaidSource.SplitTop(t[5..], ','))
                {
                    var p = part.Trim();
                    if (p.Length == 0) continue;
                    var c = RadarCurve.Match(p);
                    if (!c.Success) { g.Warn(s.No, $"“{Short(p)}” is not a curve (write id[\"Label\"]{{1, 2, 3}}); skipped."); continue; }
                    var id = c.Groups["id"].Value;
                    if (!curveIds.Add(id)) { g.Warn(s.No, $"curve “{id}” is defined twice; the second was skipped."); continue; }
                    if (radar.Curves.Count >= MermaidLimits.MaxEdges) { g.WarnOnce("curves", s.No, $"more than {MermaidLimits.MaxEdges} curves; the rest were ignored."); break; }
                    var curve = new MRadarCurve { Id = id, Label = c.Groups["label"].Success ? MermaidSource.Clean(c.Groups["label"].Value) : id, Line = s.No };
                    foreach (var entry in c.Groups["body"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (double.TryParse(entry, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)) curve.Positional.Add(v);
                        else if (RadarKeyed.Match(entry) is { Success: true } k
                                 && double.TryParse(k.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var kv) && double.IsFinite(kv))
                            curve.Keyed[k.Groups["axis"].Value] = kv;
                        else g.Warn(s.No, $"curve “{id}”: “{Short(entry)}” is not a value; skipped.");
                    }
                    if (curve.Positional.Count > 0 && curve.Keyed.Count > 0)
                        g.Warn(s.No, $"curve “{id}” mixes plain values and axis: value pairs; the pairs win where both give a value.");
                    radar.Curves.Add(curve);
                }
                continue;
            }
            var any = false;
            foreach (var part in MermaidSource.SplitTop(t, ','))
            {
                var o = RadarOption.Match(part.Trim());
                if (!o.Success) continue;
                any = true;
                var v = o.Groups["value"].Value;
                var num = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : (double?)null;
                switch (o.Groups["name"].Value)
                {
                    case "showLegend": radar.ShowLegend = !v.Equals("false", StringComparison.OrdinalIgnoreCase); break;
                    case "graticule": radar.Graticule = v.Equals("polygon", StringComparison.OrdinalIgnoreCase) ? "polygon" : "circle"; break;
                    case "max" when num != null: radar.Max = num; break;
                    case "min" when num != null: radar.Min = num; break;
                    case "ticks" when num is > 0: radar.Ticks = (int)Math.Min(50, Math.Round(num.Value)); break;
                    default: g.Warn(s.No, $"“{Short(part.Trim())}” has a value Meridian cannot read; skipped."); break;
                }
            }
            if (!any) g.Warn(s.No, $"“{Short(t)}” is not a radar statement; skipped.");
        }

        // Check the values against the axes.
        foreach (var c in radar.Curves)
        {
            if (c.Positional.Count > radar.Axes.Count)
                g.Warn(c.Line, $"curve “{c.Id}” has {c.Positional.Count} values for {radar.Axes.Count} axes; the extra values were ignored.");
            foreach (var k in c.Keyed.Keys.Where(k => !axisIds.Contains(k)).ToList())
            {
                g.Warn(c.Line, $"curve “{c.Id}” gives a value for “{k}”, which is not an axis; skipped.");
                c.Keyed.Remove(k);
            }
            var missing = Enumerable.Range(0, radar.Axes.Count).Count(i => radar.ValueOf(c, i) == null);
            if (missing > 0) g.Warn(c.Line, $"curve “{c.Id}” has no value for {missing} ax{(missing == 1 ? "is" : "es")}; drawn at the minimum.");
        }
        if (radar.Axes.Count == 0) g.Warn(0, "has no axes; nothing to draw.");
        else if (radar.Axes.Count < 3) g.WarnOnce("fewaxes", 0, "has fewer than three axes, so the curves are lines rather than shapes.");
    }

    /// <summary>The statements of a radar chart: a curve's values may run over several lines up to the closing brace.</summary>
    private static IEnumerable<MLine> RadarStatements(MGraph g, MermaidBlock block)
    {
        var sb = new StringBuilder();
        var start = 0;
        var depth = 0;
        var count = 0;
        foreach (var line in block.Body)
        {
            var t = line.Text.Trim();
            if (t.Length == 0) continue;
            if (sb.Length == 0) start = line.No;
            else sb.Append(' ');
            sb.Append(t);
            depth += t.Count(c => c == '{') - t.Count(c => c == '}');
            if (depth > 0 && sb.Length < MermaidLimits.MaxLineLength) continue;
            depth = 0;
            if (++count > MermaidLimits.MaxStatements)
            {
                g.Warn(line.No, $"more than {MermaidLimits.MaxStatements} statements; the rest were ignored.");
                yield break;
            }
            yield return new MLine(start, sb.ToString());
            sb.Clear();
        }
        if (sb.Length > 0)
        {
            g.Warn(start, "a curve's { is never closed; it was read up to the end.");
            yield return new MLine(start, sb.Append('}').ToString());
        }
    }
}
