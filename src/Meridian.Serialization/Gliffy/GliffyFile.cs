using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Meridian.Serialization.DrawIo;

namespace Meridian.Serialization.Gliffy;

/// <summary>
/// Reads Gliffy diagrams (<c>.gliffy</c>, the JSON that Gliffy for Confluence and Gliffy Online save) and turns each page
/// into a draw.io page, so the draw.io importer does the rest: UML pages (class, use case, sequence, flowcharts with start
/// and end shapes) become model elements, other pages whiteboard drawings with the same geometry, colours, text and
/// lines. Groups are flattened (their shapes keep their place). Nothing here throws on bad input other than
/// <see cref="FormatException"/> with a readable message. See docs/GLIFFY.md.
/// </summary>
public static class GliffyFile
{
    public static readonly IReadOnlyList<string> Extensions = [".gliffy"];

    /// <summary>Most objects read per page (a hostile file cannot exhaust the server).</summary>
    public const int MaxObjects = 20_000;

    /// <summary>Deepest nesting of groups read.</summary>
    public const int MaxDepth = 64;

    public static bool IsGliffyFile(string? name) => name != null && name.EndsWith(".gliffy", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a text looks like a Gliffy document (JSON with a Gliffy content type, a stage or Gliffy shapes).</summary>
    public static bool LooksLikeGliffy(string text)
    {
        var t = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (!t.StartsWith('{')) return false;
        var head = t.Length > 4000 ? t[..4000] : t;
        return head.Contains("gliffy", StringComparison.OrdinalIgnoreCase) || head.Contains("\"stage\"", StringComparison.Ordinal);
    }

    public static IReadOnlyList<DrawIoPage> Read(string fileName, byte[] bytes)
    {
        if (bytes.Length > DrawIoFile.MaxBytes) throw new FormatException($"{fileName} is larger than {DrawIoFile.MaxBytes / (1024 * 1024)} MB.");
        return ReadText(Encoding.UTF8.GetString(bytes));
    }

    /// <summary>The pages of a Gliffy document as draw.io pages.</summary>
    public static IReadOnlyList<DrawIoPage> ReadText(string text)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text.TrimStart('﻿'), new JsonDocumentOptions { MaxDepth = 256, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new FormatException($"the Gliffy file is not valid JSON ({ex.Message.Split(". ")[0].TrimEnd('.')}).", ex);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("this is not a Gliffy diagram (expected a JSON object with a stage).");
            var pages = new List<DrawIoPage>();
            if (root.TryGetProperty("pages", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                var n = 0;
                foreach (var page in list.EnumerateArray())
                {
                    n++;
                    if (page.ValueKind != JsonValueKind.Object) continue;
                    var name = Str(page, "title") ?? Str(page, "name") ?? $"Page-{n}";
                    var scene = Obj(page, "scene") ?? Obj(page, "stage") ?? page;
                    pages.Add(new Converter().Convert(name.Trim().Length > 0 ? name.Trim() : $"Page-{n}", scene));
                }
            }
            else if (Obj(root, "stage") is { } stage) pages.Add(new Converter().Convert("Page-1", stage));
            else throw new FormatException("this is not a Gliffy diagram (it has no stage or pages).");
            return pages;
        }
    }

    // ================================================================== JSON helpers

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        } : null;

    private static double Num(JsonElement e, string name, double fallback = 0) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? Num(v, fallback) : fallback;

    private static double Num(JsonElement v, double fallback = 0)
    {
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && double.IsFinite(s)) return s;
        return fallback;
    }

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
            : [];

    private static string F(double v) => Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);

    // ================================================================== one page

    private sealed class Converter
    {
        private readonly List<XElement> _cells = new();
        private readonly List<string> _warnings = new();
        /// <summary>Gliffy object id → draw.io cell id (the parts of a class box or a UML shape all map to the shape).</summary>
        private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);
        private readonly List<(JsonElement Line, double X, double Y, string Layer)> _lines = new();
        private readonly Dictionary<string, string> _layers = new(StringComparer.Ordinal);
        private readonly HashSet<string> _hiddenLayers = new(StringComparer.Ordinal);
        private string _defaultLayer = "1";
        private int _count, _rotated, _hidden, _next;

        public DrawIoPage Convert(string name, JsonElement scene)
        {
            _cells.Add(new XElement("mxCell", new XAttribute("id", "0")));
            var layers = Array(scene, "layers").OrderBy(l => Num(l, "order")).ToList();
            foreach (var l in layers)
            {
                var guid = Str(l, "guid") ?? $"layer{_layers.Count}";
                var id = "layer-" + guid;
                if (_layers.ContainsKey(guid)) continue;
                _layers[guid] = id;
                if (Str(l, "visible") == "false") _hiddenLayers.Add(guid);
                _cells.Add(new XElement("mxCell", new XAttribute("id", id), new XAttribute("parent", "0"), new XAttribute("value", Str(l, "name") ?? "")));
            }
            if (_layers.Count == 0) _cells.Add(new XElement("mxCell", new XAttribute("id", "1"), new XAttribute("parent", "0")));
            else _defaultLayer = _layers.Values.First();

            var objects = Array(scene, "objects").Select((o, i) => (o, i)).OrderBy(x => Order(x.o, x.i)).ThenBy(x => x.i).Select(x => x.o).ToList();
            foreach (var o in objects) Visit(o, 0, 0, null, 0);
            foreach (var (line, x, y, layer) in _lines) Line(line, x, y, layer);

            if (_count >= MaxObjects) _warnings.Add($"Gliffy page “{name}”: more than {MaxObjects} objects; the rest were ignored.");
            if (_hidden > 0) _warnings.Add($"Gliffy page “{name}”: {_hidden} hidden object{(_hidden == 1 ? " was" : "s were")} left out.");
            if (_rotated > 0) _warnings.Add($"Gliffy page “{name}”: {_rotated} rotated shape{(_rotated == 1 ? " was" : "s were")} placed without the rotation.");
            var model = new XElement("mxGraphModel", new XElement("root", _cells));
            return new DrawIoPage(name, model) { Warnings = _warnings };
        }

        private static double Order(JsonElement o, int index) => Str(o, "order") is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : index;

        private string IdOf(JsonElement o) => Str(o, "id") is { Length: > 0 } id ? id : "auto" + _next++;

        private static string Uid(JsonElement o) => (Str(o, "uid") ?? "").ToLowerInvariant();

        private static string? GraphicType(JsonElement o) => Obj(o, "graphic") is { } g ? Str(g, "type") : null;

        private static JsonElement? Graphic(JsonElement o) => Obj(o, "graphic") is { } g && Str(g, "type") is { } t ? Obj(g, t) : null;

        /// <summary>Registers the object and all its parts as drawn by <paramref name="cell"/>.</summary>
        private void Claim(JsonElement o, string cell, int depth = 0)
        {
            _ids.TryAdd(IdOf(o), cell);
            if (depth < MaxDepth) foreach (var c in Array(o, "children")) Claim(c, cell, depth + 1);
        }

        private void Visit(JsonElement o, double ox, double oy, string? parentLayer, int depth)
        {
            if (++_count > MaxObjects || depth > MaxDepth) return;
            var layer = Str(o, "layerId") is { } lid && _layers.ContainsKey(lid) ? lid : parentLayer;
            if (Str(o, "hidden") == "true" || layer != null && _hiddenLayers.Contains(layer)) { _hidden++; return; }
            var layerCell = layer != null ? _layers[layer] : _defaultLayer;
            var x = ox + Num(o, "x");
            var y = oy + Num(o, "y");
            var type = GraphicType(o);
            var uid = Uid(o);
            var children = Array(o, "children").ToList();
            if (type == "Line") { _lines.Add((o, ox, oy, layer ?? "")); return; }
            if (type == null && (uid.Length == 0 || uid.Contains(".group") || uid.Contains("selection")) && children.Count > 0 && !(uid.Length == 0 && GraphicType(children[0]) == "Text"))
            {
                // An invisible group: its children are placed relative to it.
                foreach (var c in children.Select((c, i) => (c, i)).OrderBy(t => Order(t.c, t.i)).Select(t => t.c)) Visit(c, x, y, layer, depth + 1);
                return;
            }
            if (Math.Abs(Num(o, "rotation") % 360) > 0.5) _rotated++;
            var w = Math.Max(1, Num(o, "width"));
            var h = Math.Max(1, Num(o, "height"));
            var id = "g" + IdOf(o);
            if (_ids.ContainsKey(IdOf(o))) id = "g" + IdOf(o) + "_" + _next++;

            if (IsClass(uid))
            {
                Vertex(id, layerCell, ClassLabel(o, uid), "html=1;whiteSpace=wrap;align=center;" + Colours(o), x, y, w, h);
                Claim(o, id);
                return;
            }
            if (type == "Text")
            {
                Vertex(id, layerCell, TextHtml(o) ?? "", "text;html=1;whiteSpace=wrap;", x, y, w, h);
                Claim(o, id);
                return;
            }
            if (type == "Image")
            {
                var url = Graphic(o) is { } img ? Str(img, "url") ?? "" : "";
                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    // draw.io writes "data:image/png,BASE64": a style value cannot hold ';'.
                    var comma = url.IndexOf(',');
                    if (comma > 0) url = url[..comma].Replace(";base64", "", StringComparison.OrdinalIgnoreCase) + url[comma..];
                }
                Vertex(id, layerCell, FirstText(o) ?? "", "shape=image;html=1;image=" + url.Replace(";", "%3B"), x, y, w, h);
                Claim(o, id);
                return;
            }

            var label = FirstText(o, uid) ?? "";
            Vertex(id, layerCell, label, ShapeStyle(uid, Graphic(o) ?? FirstChildGraphic(o), label) + "html=1;whiteSpace=wrap;" + Colours(o), x, y, w, h);
            _ids.TryAdd(IdOf(o), id);
            // A UML shape without a graphic of its own is drawn by its children; other shapes may hold more shapes.
            var whole = type == null || uid.Contains(".uml.");
            foreach (var c in children)
            {
                if (whole || GraphicType(c) == "Text") { Claim(c, id); continue; }
                Visit(c, x, y, layer, depth + 1);
            }
        }

        private void Vertex(string id, string parent, string value, string style, double x, double y, double w, double h) =>
            _cells.Add(new XElement("mxCell", new XAttribute("id", id), new XAttribute("parent", parent), new XAttribute("value", value),
                new XAttribute("style", style), new XAttribute("vertex", "1"),
                new XElement("mxGeometry", new XAttribute("x", F(x)), new XAttribute("y", F(y)), new XAttribute("width", F(w)), new XAttribute("height", F(h)), new XAttribute("as", "geometry"))));

        private static JsonElement? FirstChildGraphic(JsonElement o) =>
            Array(o, "children").Select(c => Graphic(c)).FirstOrDefault(g => g is { } x && x.TryGetProperty("fillColor", out _));

        /// <summary>The HTML of a Text object.</summary>
        private static string? TextHtml(JsonElement o) => Graphic(o) is { } t && GraphicType(o) == "Text" ? Str(t, "html") : null;

        /// <summary>The first text found in the object or its children (Gliffy keeps a shape's label as a Text child).</summary>
        private static string? FirstText(JsonElement o, string uid = "", int depth = 0)
        {
            if (TextHtml(o) is { } own) return own;
            if (depth > 8) return null;
            foreach (var c in Array(o, "children"))
            {
                if (GraphicType(c) == "Line") continue;
                if (FirstText(c, uid, depth + 1) is { } t) return t;
            }
            return null;
        }

        private static string Colours(JsonElement o)
        {
            var g = Graphic(o) ?? FirstChildGraphic(o);
            if (g is not { } s) return "";
            var sb = new StringBuilder();
            if (Colour(Str(s, "fillColor")) is { } fill) sb.Append("fillColor=").Append(fill).Append(';');
            if (Colour(Str(s, "strokeColor")) is { } stroke) sb.Append("strokeColor=").Append(stroke).Append(';');
            return sb.ToString();
        }

        private static readonly Regex Rgb = new(@"^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        private static string? Colour(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var v = value.Trim();
            if (v.Equals("none", StringComparison.OrdinalIgnoreCase) || v.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return "none";
            if (Regex.IsMatch(v, "^#[0-9a-fA-F]{3}([0-9a-fA-F]{3})?$", RegexOptions.None, TimeSpan.FromSeconds(1))) return v;
            if (Rgb.Match(v) is { Success: true } m)
                return "#" + string.Concat(Enumerable.Range(1, 3).Select(i => Math.Clamp(int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture), 0, 255).ToString("x2", CultureInfo.InvariantCulture)));
            return null;
        }

        // ---------------------------------------------------------------- UML class boxes

        private static bool IsClass(string uid) =>
            uid.Contains(".uml.") && Last(uid) is "class" or "class2" or "simple_class" or "abstract_class" or "interface" or "enumeration" or "object" or "data_type";

        private static string Last(string uid) => uid[(uid.LastIndexOf('.') + 1)..];

        /// <summary>A class box as one HTML label with its compartments (name, attributes, operations) split by &lt;hr&gt;.</summary>
        private static string ClassLabel(JsonElement o, string uid)
        {
            var parts = Array(o, "children").OrderBy(c => Num(c, "y")).Select(c => FirstText(c) ?? "").ToList();
            // "class2" adds a responsibilities compartment under the operations: it is not a member, so it is left out.
            if (Last(uid) == "class2" && parts.Count > 3) parts.RemoveRange(3, parts.Count - 3);
            if (parts.Count == 0) parts.Add(FirstText(o) ?? "");
            var keyword = Last(uid) switch { "interface" => "«interface»", "enumeration" => "«enumeration»", "data_type" => "«dataType»", _ => null };
            if (keyword != null && !parts[0].Contains(keyword[1..^1], StringComparison.OrdinalIgnoreCase)) parts[0] = $"<p>{keyword}</p>" + parts[0];
            if (parts.Count == 1) parts.Add("");
            return string.Join("<hr>", parts);
        }

        // ---------------------------------------------------------------- shapes

        private static readonly Regex StartWord = new(@"^\s*(start|begin)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex EndWord = new(@"^\s*(end|stop|finish|done|exit)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        /// <summary>The draw.io style for a Gliffy shape id ("com.gliffy.shape.flowchart.flowchart_v1.default.decision" → "rhombus;"); a bare shape word comes first, as draw.io reads it.</summary>
        internal static string ShapeStyle(string uid, JsonElement? graphic, string labelHtml)
        {
            var last = uid.Length > 0 ? Last(uid) : "";
            var tid = graphic is { } g ? (Str(g, "tid") ?? "").ToLowerInvariant() : "";
            if (uid.Contains(".uml."))
            {
                if (last == "actor" || last.EndsWith("_actor", StringComparison.Ordinal)) return "shape=umlActor;verticalLabelPosition=bottom;";
                if (last.Contains("use_case") || last == "usecase") return "ellipse;";
                if (last.Contains("lifeline") || last.Contains("timeline")) return "shape=umlLifeline;";
                if (last.Contains("note") || last.Contains("comment")) return "shape=note;";
                if (last.Contains("initial") || last is "start" or "start_state") return "shape=startState;";
                if (last.Contains("final") || last.Contains("end_state") || last.Contains("flow_final")) return "shape=endState;";
                if (last.Contains("decision") || last.Contains("choice") || last.Contains("merge")) return "rhombus;";
                if (last.Contains("fork") || last.Contains("join") || last.Contains("synch")) return "shape=line;";
                if (last.Contains("package")) return "shape=folder;";
                if (last.Contains("frame") || last.Contains("fragment")) return "shape=umlFrame;";
                if (last.Contains("state") || last.Contains("activity") || last.Contains("action")) return "rounded=1;";
                return "rounded=0;";   // components, nodes, artifacts, objects: boxes
            }
            if (uid.Contains(".flowchart."))
            {
                var text = DGraphText(labelHtml);
                if (last.Contains("start_end") || last.Contains("terminator") || last.Contains("terminal"))
                    return StartWord.IsMatch(text) ? "shape=startState;" : EndWord.IsMatch(text) ? "shape=endState;" : "rounded=1;arcSize=50;";
                if (last.Contains("decision")) return "rhombus;";
                if (last.Contains("document")) return "shape=document;";
                if (last.Contains("database") || last.Contains("stored_data") || last.Contains("data_storage") || last.Contains("disk")) return "shape=cylinder3;";
                if (last.Contains("input_output") || last == "data") return "shape=parallelogram;";
                if (last.Contains("preparation")) return "shape=hexagon;";
                if (last.Contains("connector") || last.Contains("off_page")) return "ellipse;";
                if (last.Contains("note") || last.Contains("annotation") || last.Contains("comment")) return "shape=note;";
                return "rounded=0;";
            }
            if (uid.Contains("swimlane")) return "swimlane;";
            var name = last.Length > 0 && uid.Contains(".basic.") ? last : tid;
            if (name.Contains("round_rect") || name.Contains("rounded")) return "rounded=1;";
            if (name.Contains("rectangle") || name is "square" or "default") return "rounded=0;";
            if (name.Contains("ellipse") || name.Contains("circle") || name.Contains("oval")) return "ellipse;";
            if (name.Contains("diamond") || name.Contains("rhombus")) return "rhombus;";
            if (name.Contains("triangle")) return "triangle;";
            if (name.Contains("hexagon")) return "shape=hexagon;";
            if (name.Contains("cylinder")) return "shape=cylinder3;";
            if (name.Contains("cloud")) return "ellipse;shape=cloud;";
            if (name.Contains("parallelogram")) return "shape=parallelogram;";
            if (name.Contains("note") || name.Contains("sticky")) return "shape=note;";
            if (name == "text") return "text;";
            // A library icon (network, AWS, UI mock-ups…): drawn as a rectangle with its label.
            return uid.Length > 0 && !uid.Contains(".basic.") ? "shape=mxgraph.gliffy;" : "rounded=0;";
        }

        /// <summary>"8.0,2.0" → "8 2".</summary>
        private static string Dash(string s) => string.Join(" ", s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(d => double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? F(v) : "4"));

        private static string DGraphText(string html) => Regex.Replace(Regex.Replace(html, "<[^>]*>", " ", RegexOptions.None, TimeSpan.FromSeconds(1)), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();

        // ---------------------------------------------------------------- lines

        /// <summary>Gliffy arrow numbers → draw.io arrow name and fill.</summary>
        internal static (string Name, bool Fill) Arrow(int n) => n switch
        {
            0 => ("none", false),
            1 or 6 => ("open", false),
            2 or 17 => ("block", true),
            3 or 4 => ("block", false),
            5 => ("diamond", false),
            7 => ("diamond", true),
            9 => ("ERzeroToMany", true),
            10 => ("ERoneToMany", true),
            11 => ("ERmandOne", true),
            12 => ("ERone", true),
            13 => ("ERzeroToOne", true),
            14 => ("ERmany", true),
            15 => ("oval", false),
            16 => ("dash", false),
            19 => ("openAsync", false),
            _ => ("classic", true),
        };

        private void Line(JsonElement o, double ox, double oy, string layer)
        {
            var g = Graphic(o);
            var x = ox + Num(o, "x");
            var y = oy + Num(o, "y");
            var points = new List<(double X, double Y)>();
            if (g is { } line && line.TryGetProperty("controlPath", out var path) && path.ValueKind == JsonValueKind.Array)
                foreach (var p in path.EnumerateArray())
                    if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2)
                        points.Add((x + Num(p[0]), y + Num(p[1])));
            if (points.Count == 0) points.AddRange([(x, y), (x + Num(o, "width"), y + Num(o, "height"))]);
            var constraints = Obj(o, "constraints");
            string? End(string which, string kind) =>
                constraints is { } c && Obj(c, which) is { } end && Obj(end, kind) is { } pos && Str(pos, "nodeId") is { } node && _ids.TryGetValue(node, out var cell) ? cell : null;
            var source = End("startConstraint", "StartPositionConstraint");
            var target = End("endConstraint", "EndPositionConstraint");

            var style = new StringBuilder("html=1;");
            if (g is { } l)
            {
                var (start, startFill) = Arrow((int)Num(l, "startArrow"));
                var (end, endFill) = Arrow((int)Num(l, "endArrow"));
                style.Append("startArrow=").Append(start).Append(";startFill=").Append(startFill ? 1 : 0)
                     .Append(";endArrow=").Append(end).Append(";endFill=").Append(endFill ? 1 : 0).Append(';');
                if (Str(l, "dashStyle") is { Length: > 0 } dash && dash != "null")
                    style.Append("dashed=1;dashPattern=").Append(Dash(dash)).Append(';');
                if (Colour(Str(l, "strokeColor")) is { } stroke) style.Append("strokeColor=").Append(stroke).Append(';');
            }
            var label = string.Join("<br>", Array(o, "children").Select(c => FirstText(c)).OfType<string>());
            var id = "g" + IdOf(o);
            var geo = new XElement("mxGeometry", new XAttribute("relative", "1"), new XAttribute("as", "geometry"),
                new XElement("mxPoint", new XAttribute("x", F(points[0].X)), new XAttribute("y", F(points[0].Y)), new XAttribute("as", "sourcePoint")),
                new XElement("mxPoint", new XAttribute("x", F(points[^1].X)), new XAttribute("y", F(points[^1].Y)), new XAttribute("as", "targetPoint")));
            if (points.Count > 2)
                geo.Add(new XElement("Array", new XAttribute("as", "points"),
                    points.Skip(1).Take(points.Count - 2).Select(p => new XElement("mxPoint", new XAttribute("x", F(p.X)), new XAttribute("y", F(p.Y))))));
            var cell = new XElement("mxCell", new XAttribute("id", id), new XAttribute("parent", layer.Length > 0 && _layers.TryGetValue(layer, out var lc) ? lc : _defaultLayer),
                new XAttribute("value", label), new XAttribute("style", style.ToString()), new XAttribute("edge", "1"), geo);
            if (source != null) cell.Add(new XAttribute("source", source));
            if (target != null) cell.Add(new XAttribute("target", target));
            _cells.Add(cell);
        }
    }
}
