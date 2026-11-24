using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Meridian.Serialization.DrawIo;

namespace Meridian.Serialization.Visio;

/// <summary>
/// Reads Visio drawings (<c>.vsdx</c>, <c>.vsdm</c>) as draw.io pages, so the draw.io importer turns them into diagrams: UML
/// stencils (actors, use cases, classes, lifelines, states, activity nodes, notes, packages) are recognised by their
/// master's name and become model elements; any other page becomes a whiteboard drawing with the same shapes, text,
/// positions, colours and glued connectors. Masters are inherited (size, geometry, text, colours), groups are flattened,
/// background pages are left out. The old binary <c>.vsd</c> is not read. See docs/VISIO.md.
/// </summary>
public static class VisioFile
{
    public static readonly IReadOnlyList<string> Extensions = [".vsdx", ".vsdm"];

    /// <summary>Most shapes read from one page (the rest are ignored with a warning).</summary>
    public const int MaxShapes = 20_000;

    /// <summary>Most pages read.</summary>
    public const int MaxPages = 100;

    /// <summary>Largest XML part read from the package.</summary>
    private const long MaxPartBytes = 64L * 1024 * 1024;

    /// <summary>Pixels per inch: Visio measures in inches.</summary>
    private const double Px = 96;

    private static readonly XNamespace V = "http://schemas.microsoft.com/office/visio/2012/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Pr = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static bool IsVisioFile(string? name) =>
        name != null && Extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>The pages of a Visio package. Throws <see cref="FormatException"/> with a readable message for anything it cannot read.</summary>
    public static IReadOnlyList<DrawIoPage> Read(string fileName, byte[] bytes)
    {
        if (fileName.EndsWith(".vsd", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("the old binary Visio format (.vsd) is not supported; open it in Visio and save it as .vsdx.");
        if (bytes.Length < 4 || bytes[0] != (byte)'P' || bytes[1] != (byte)'K')
            throw new FormatException("the file is not a Visio .vsdx package (it is not a zip archive).");
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            return new Package(zip).Pages();
        }
        catch (InvalidDataException ex) { throw new FormatException($"the Visio package could not be read: {ex.Message}"); }
        catch (XmlException ex) { throw new FormatException($"the Visio package holds broken XML: {ex.Message}"); }
    }

    private static XDocument Parse(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxPartBytes });
        return XDocument.Load(reader);
    }

    private static string F(double v) => Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);

    // ================================================================== the package

    private sealed class Package(ZipArchive zip)
    {
        /// <summary>Master id → its top shape (with the master's sub-shapes inside).</summary>
        private readonly Dictionary<string, (string Name, XElement Shape)> _masters = new(StringComparer.Ordinal);

        private XDocument? Part(string path)
        {
            var entry = zip.GetEntry(path) ?? zip.Entries.FirstOrDefault(e => e.FullName.Equals(path, StringComparison.OrdinalIgnoreCase));
            return entry == null ? null : Parse(SafeZip.ReadText(entry, MaxPartBytes));
        }

        /// <summary>The targets of a part's relationships (id → path in the package).</summary>
        private Dictionary<string, string> Rels(string partPath)
        {
            var dir = partPath.Contains('/') ? partPath[..partPath.LastIndexOf('/')] : "";
            var name = partPath[(partPath.LastIndexOf('/') + 1)..];
            var rels = Part($"{dir}/_rels/{name}.rels");
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (rels?.Root == null) return map;
            foreach (var r in rels.Root.Elements(Pr + "Relationship"))
            {
                var id = (string?)r.Attribute("Id");
                var target = (string?)r.Attribute("Target");
                if (id == null || target == null) continue;
                map[id] = Resolve(dir, target);
            }
            return map;
        }

        private static string Resolve(string dir, string target)
        {
            if (target.StartsWith('/')) return target.TrimStart('/');
            var parts = (dir.Length > 0 ? dir.Split('/') : []).ToList();
            foreach (var seg in target.Split('/'))
            {
                if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else if (seg != ".") parts.Add(seg);
            }
            return string.Join('/', parts);
        }

        public IReadOnlyList<DrawIoPage> Pages()
        {
            LoadMasters();
            var pagesDoc = Part("visio/pages/pages.xml") ?? throw new FormatException("the package has no Visio pages (visio/pages/pages.xml is missing).");
            var rels = Rels("visio/pages/pages.xml");
            var result = new List<DrawIoPage>();
            foreach (var page in pagesDoc.Root!.Elements(V + "Page"))
            {
                if ((string?)page.Attribute("Background") == "1") continue;
                if (result.Count >= MaxPages) break;
                var name = (string?)page.Attribute("Name") ?? (string?)page.Attribute("NameU") ?? $"Page {result.Count + 1}";
                var relId = (string?)page.Element(V + "Rel")?.Attribute(R + "id");
                if (relId == null || !rels.TryGetValue(relId, out var path) || Part(path) is not { Root: { } contents }) continue;
                var height = Num(page.Element(V + "PageSheet"), "PageHeight") ?? 11;
                result.Add(new PageConverter(this, name, height).Convert(contents));
            }
            if (result.Count == 0) throw new FormatException("the Visio file has no foreground pages.");
            return result;
        }

        private void LoadMasters()
        {
            if (Part("visio/masters/masters.xml") is not { Root: { } masters }) return;
            var rels = Rels("visio/masters/masters.xml");
            foreach (var m in masters.Elements(V + "Master"))
            {
                var id = (string?)m.Attribute("ID");
                var relId = (string?)m.Element(V + "Rel")?.Attribute(R + "id");
                if (id == null || relId == null || !rels.TryGetValue(relId, out var path)) continue;
                if (Part(path)?.Root?.Element(V + "Shapes")?.Elements(V + "Shape").FirstOrDefault() is not { } shape) continue;
                var name = (string?)m.Attribute("NameU") ?? (string?)m.Attribute("Name") ?? "";
                _masters[id] = (name, shape);
            }
        }

        public (string Name, XElement Shape)? Master(string? id) => id != null && _masters.TryGetValue(id, out var m) ? m : null;
    }

    // ================================================================== cells

    private static double? Num(XElement? sheet, string cell)
    {
        var c = sheet?.Elements(V + "Cell").FirstOrDefault(x => (string?)x.Attribute("N") == cell);
        return c != null && double.TryParse((string?)c.Attribute("V"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    }

    private static string? Str(XElement? sheet, string cell) =>
        (string?)sheet?.Elements(V + "Cell").FirstOrDefault(x => (string?)x.Attribute("N") == cell)?.Attribute("V");

    private static XElement? Section(XElement? sheet, string name) =>
        sheet?.Elements(V + "Section").FirstOrDefault(x => (string?)x.Attribute("N") == name);

    /// <summary>The text of a shape, paragraphs and line breaks kept; field and formatting markers dropped.</summary>
    private static string? TextOf(XElement? shape)
    {
        if (shape?.Element(V + "Text") is not { } t) return null;
        var sb = new StringBuilder();
        foreach (var n in t.DescendantNodes())
            if (n is XText x) sb.Append(x.Value);
        var s = sb.ToString().Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u2028', '\n').Replace('\u2029', '\n');
        return Regex.Replace(s, @"[ \t]+\n", "\n", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
    }

    /// <summary>A Visio colour cell ("#RRGGBB", "RGB(1,2,3)" or a palette index) as #rrggbb.</summary>
    private static string? Colour(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim();
        if (Regex.IsMatch(v, "^#[0-9a-fA-F]{6}$", RegexOptions.None, TimeSpan.FromSeconds(1))) return v.ToLowerInvariant();
        if (Regex.Match(v, @"^RGB\((\d+),\s*(\d+),\s*(\d+)\)$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) is { Success: true } m)
            return "#" + string.Concat(Enumerable.Range(1, 3).Select(i => Math.Clamp(int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture), 0, 255).ToString("x2", CultureInfo.InvariantCulture)));
        return v switch { "0" => "#000000", "1" => "#ffffff", "2" => "#ff0000", "3" => "#00ff00", "4" => "#0000ff", "5" => "#ffff00", "6" => "#ff00ff", "7" => "#00ffff", _ => null };
    }

    // ================================================================== one page

    private sealed class PageConverter(Package package, string name, double pageHeight)
    {
        private readonly List<XElement> _cells = [new XElement("mxCell", new XAttribute("id", "0")), new XElement("mxCell", new XAttribute("id", "1"), new XAttribute("parent", "0"))];
        private readonly List<string> _warnings = [];
        /// <summary>Visio sheet id → the draw.io cell that draws it (a sub-shape of a class box maps to the box).</summary>
        private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);
        private readonly List<(XElement Shape, Inherited Inh, double Ox, double Oy)> _connectors = [];
        private int _count, _rotated, _skipped;

        /// <summary>A shape with the master shape it inherits from.</summary>
        private readonly record struct Inherited(XElement Shape, XElement? MasterShape, string MasterName, XElement? MasterTop)
        {
            public double? Num(string cell) => VisioFile.Num(Shape, cell) ?? VisioFile.Num(MasterShape, cell);
            public string? Str(string cell) => VisioFile.Str(Shape, cell) ?? VisioFile.Str(MasterShape, cell);
            public XElement? Section(string name) => VisioFile.Section(Shape, name) ?? VisioFile.Section(MasterShape, name);
            public string? Text => TextOf(Shape) ?? TextOf(MasterShape);
            public string Type => (string?)Shape.Attribute("Type") ?? (string?)MasterShape?.Attribute("Type") ?? "Shape";
            public IEnumerable<XElement> Children => Shape.Element(V + "Shapes")?.Elements(V + "Shape") ?? [];
            public bool OneD => Num("BeginX") != null && Num("EndX") != null;
        }

        private Inherited Resolve(XElement shape, string? masterName, XElement? masterTop)
        {
            if ((string?)shape.Attribute("Master") is { } mid && package.Master(mid) is { } m)
                return new Inherited(shape, m.Shape, Clean(m.Name), m.Shape);
            XElement? ms = null;
            if ((string?)shape.Attribute("MasterShape") is { } msid && masterTop != null)
                ms = masterTop.DescendantsAndSelf(V + "Shape").FirstOrDefault(x => (string?)x.Attribute("ID") == msid);
            return new Inherited(shape, ms, masterName ?? "", masterTop);
        }

        /// <summary>"Use case.42" → "use case".</summary>
        private static string Clean(string masterName) => Regex.Replace(masterName, @"\.\d+$", "", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim().ToLowerInvariant();

        public DrawIoPage Convert(XElement contents)
        {
            foreach (var s in contents.Element(V + "Shapes")?.Elements(V + "Shape") ?? []) Visit(s, 0, 0, null, null, 0);
            var connects = contents.Element(V + "Connects")?.Elements(V + "Connect").ToList() ?? [];
            foreach (var (shape, inh, ox, oy) in _connectors) Connector(shape, inh, ox, oy, connects);
            if (_count >= MaxShapes) _warnings.Add($"Visio page “{name}”: more than {MaxShapes} shapes; the rest were ignored.");
            if (_rotated > 0) _warnings.Add($"Visio page “{name}”: {_rotated} rotated shape{(_rotated == 1 ? " was" : "s were")} placed without the rotation.");
            if (_skipped > 0) _warnings.Add($"Visio page “{name}”: {_skipped} shape{(_skipped == 1 ? "" : "s")} without text or outline (pictures, embedded objects) left out.");
            return new DrawIoPage(name, new XElement("mxGraphModel", new XElement("root", _cells))) { Warnings = _warnings };
        }

        /// <summary>Visits a shape whose parent's bottom-left corner is at (<paramref name="ox"/>, <paramref name="oy"/>) inches on the page.</summary>
        private void Visit(XElement shape, double ox, double oy, string? masterName, XElement? masterTop, int depth)
        {
            if (++_count > MaxShapes || depth > 32) return;
            var s = Resolve(shape, masterName, masterTop);
            var id = (string?)shape.Attribute("ID") ?? $"auto{_count}";
            if (s.OneD) { _connectors.Add((shape, s, ox, oy)); return; }
            var w = s.Num("Width") ?? 0;
            var h = s.Num("Height") ?? 0;
            var left = ox + (s.Num("PinX") ?? w / 2) - (s.Num("LocPinX") ?? w / 2);
            var bottom = oy + (s.Num("PinY") ?? h / 2) - (s.Num("LocPinY") ?? h / 2);
            if (Math.Abs(s.Num("Angle") ?? 0) > 0.01) _rotated++;
            var x = left * Px;
            var y = (pageHeight - bottom - h) * Px;
            var kind = s.MasterName;
            var children = s.Children.ToList();
            var text = s.Text ?? "";

            if (IsClassMaster(kind) && children.Count > 0)
            {
                Vertex("v" + id, ClassLabel(s, kind), "html=1;whiteSpace=wrap;align=center;" + Colours(s), x, y, w * Px, h * Px);
                Claim(shape, "v" + id);
                return;
            }
            var style = StyleFor(kind, s, text);
            if (s.Type == "Group" && style == null)
            {
                // A plain group: its parts are drawn one by one.
                foreach (var c in children) Visit(c, left, bottom, s.MasterName, s.MasterTop, depth + 1);
                return;
            }
            if (style == null)
            {
                if (text.Length == 0 && children.Count == 0) { _skipped++; return; }
                style = text.Length > 0 && !HasOutline(s) ? "text;" : "rounded=0;";
            }
            Vertex("v" + id, Html(text), style + "html=1;whiteSpace=wrap;" + (style.StartsWith("text;", StringComparison.Ordinal) ? "" : Colours(s)), x, y, w * Px, h * Px);
            _ids[id] = "v" + id;
            var whole = style.Contains("umlActor", StringComparison.Ordinal) || style.Contains("umlLifeline", StringComparison.Ordinal) || style.StartsWith("ellipse", StringComparison.Ordinal);
            foreach (var c in children)
            {
                if (whole) { Claim(c, "v" + id); continue; }
                Visit(c, left, bottom, s.MasterName, s.MasterTop, depth + 1);
            }
        }

        private void Claim(XElement shape, string cell)
        {
            foreach (var x in shape.DescendantsAndSelf(V + "Shape"))
                if ((string?)x.Attribute("ID") is { } sid) _ids.TryAdd(sid, cell);
        }

        private void Vertex(string id, string value, string style, double x, double y, double w, double h) =>
            _cells.Add(new XElement("mxCell", new XAttribute("id", id), new XAttribute("parent", "1"), new XAttribute("value", value),
                new XAttribute("style", style), new XAttribute("vertex", "1"),
                new XElement("mxGeometry", new XAttribute("x", F(x)), new XAttribute("y", F(y)), new XAttribute("width", F(Math.Max(1, w))), new XAttribute("height", F(Math.Max(1, h))), new XAttribute("as", "geometry"))));

        private static string Html(string text) => string.Join("<br>", text.Split('\n').Select(WebUtility.HtmlEncode));

        /// <summary>The shape draws a line or a fill (else it is a text block).</summary>
        private static bool HasOutline(Inherited s)
        {
            if (s.Section("Geometry") is not { } geo) return false;
            var noLine = Str(geo, "NoLine") == "1" || s.Str("LinePattern") == "0";
            var noFill = Str(geo, "NoFill") == "1" || s.Str("FillPattern") == "0";
            return !(noLine && noFill);
        }

        private static string Colours(Inherited s)
        {
            var sb = new StringBuilder();
            if (s.Str("FillPattern") == "0") sb.Append("fillColor=none;");
            else if (Colour(s.Str("FillForegnd")) is { } fill) sb.Append("fillColor=").Append(fill).Append(';');
            if (s.Str("LinePattern") == "0") sb.Append("strokeColor=none;");
            else if (Colour(s.Str("LineColor")) is { } line) sb.Append("strokeColor=").Append(line).Append(';');
            if (s.Section("Character")?.Elements(V + "Row").FirstOrDefault() is { } ch && Colour(Str(ch, "Color")) is { } font) sb.Append("fontColor=").Append(font).Append(';');
            return sb.ToString();
        }

        // ---------------------------------------------------------------- recognising stencils

        private static bool IsClassMaster(string kind) =>
            kind is "class" or "interface" or "enumeration" or "data type" or "datatype" or "abstract class" or "signal" || kind.EndsWith(" class", StringComparison.Ordinal);

        private static readonly Regex StartWord = new(@"^\s*(start|begin)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex EndWord = new(@"^\s*(end|stop|finish|done|exit)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        /// <summary>The draw.io style for a stencil (by master name), else by geometry; null for a plain group / unknown shape.</summary>
        private static string? StyleFor(string kind, Inherited s, string text)
        {
            if (kind.Length > 0)
            {
                if (kind.Contains("actor")) return "shape=umlActor;verticalLabelPosition=bottom;";
                if (kind.Contains("use case") || kind == "usecase") return "ellipse;";
                if (kind.Contains("lifeline")) return "shape=umlLifeline;";
                if (kind.Contains("note") || kind.Contains("comment") || kind.Contains("annotation")) return "shape=note;";
                if (kind.Contains("initial") || kind is "start state") return "shape=startState;";
                if (kind.Contains("final") || kind is "end state" or "stop state") return "shape=endState;";
                if (kind.Contains("decision") || kind.Contains("merge") || kind.Contains("choice")) return "rhombus;";
                if (kind.Contains("fork") || kind.Contains("join") || kind.Contains("synchroni")) return "shape=line;";
                if (kind.Contains("package")) return "shape=folder;";
                if (kind.Contains("fragment") || kind.Contains("frame")) return "shape=umlFrame;";
                if (kind.Contains("swimlane") || kind.Contains("functional band") || kind.Contains("lane")) return "swimlane;";
                if (kind.Contains("boundary") || kind.Contains("subject") || kind.Contains("container")) return "rounded=0;fillColor=none;";
                if (kind.Contains("state") || kind.Contains("action") || kind.Contains("activity")) return "rounded=1;";
                if (kind.Contains("start/end") || kind.Contains("terminator"))
                    return StartWord.IsMatch(text) ? "shape=startState;" : EndWord.IsMatch(text) ? "shape=endState;" : "rounded=1;arcSize=50;";
                if (kind.Contains("document")) return "shape=document;";
                if (kind.Contains("database") || kind.Contains("stored data") || kind.Contains("data store")) return "shape=cylinder3;";
                if (kind is "data" || kind.Contains("input/output")) return "shape=parallelogram;";
                if (kind.Contains("preparation")) return "shape=hexagon;";
                if (kind.Contains("reference") || kind.Contains("circle") || kind.Contains("ellipse")) return "ellipse;";
                if (kind.Contains("diamond")) return "rhombus;";
                if (kind.Contains("triangle")) return "triangle;";
                if (kind.Contains("hexagon")) return "shape=hexagon;";
                if (kind.Contains("cloud")) return "ellipse;shape=cloud;";
                if (kind.Contains("rounded")) return "rounded=1;";
                if (kind is "process" or "rectangle" or "square" or "box" or "subprocess" or "predefined process" or "component" or "node" or "object" or "block") return "rounded=0;";
                if (kind == "text" || kind.Contains("text block")) return "text;";
            }
            return ByGeometry(s);
        }

        /// <summary>A shape from its own geometry: an ellipse row, four corners in a diamond, a rounded rectangle, or a box.</summary>
        private static string? ByGeometry(Inherited s)
        {
            if (s.Section("Geometry") is not { } geo) return null;
            var rows = geo.Elements(V + "Row").ToList();
            if (rows.Any(r => (string?)r.Attribute("T") == "Ellipse")) return "ellipse;";
            var pts = rows.Where(r => (string?)r.Attribute("T") is "MoveTo" or "LineTo" or "RelMoveTo" or "RelLineTo")
                .Select(r => (X: Num(r, "X"), Y: Num(r, "Y"), Rel: ((string?)r.Attribute("T"))!.StartsWith("Rel", StringComparison.Ordinal))).ToList();
            var w = s.Num("Width") ?? 1;
            var h = s.Num("Height") ?? 1;
            if (pts.Count >= 4 && pts.All(p => p.X != null && p.Y != null))
            {
                var norm = pts.Select(p => p.Rel ? (p.X!.Value, p.Y!.Value) : (p.X!.Value / Math.Max(w, 1e-6), p.Y!.Value / Math.Max(h, 1e-6))).Distinct().ToList();
                bool Near(double a, double b) => Math.Abs(a - b) < 0.05;
                if (norm.Count == 4 && norm.Any(p => Near(p.Item1, 0.5) && Near(p.Item2, 0)) && norm.Any(p => Near(p.Item1, 0) && Near(p.Item2, 0.5))) return "rhombus;";
                if (norm.Count == 3) return "triangle;";
            }
            if (rows.Any(r => (string?)r.Attribute("T") is "ArcTo" or "EllipticalArcTo") || (s.Num("Rounding") ?? 0) > 0) return "rounded=1;";
            return "rounded=0;";
        }

        /// <summary>A UML class box as one HTML label: name, attributes and operations compartments split by &lt;hr&gt;.</summary>
        private string ClassLabel(Inherited s, string kind)
        {
            var parts = new List<string>();
            foreach (var c in s.Children.OrderByDescending(c => Resolve(c, s.MasterName, s.MasterTop).Num("PinY") ?? 0))
            {
                var lines = c.DescendantsAndSelf(V + "Shape")
                    .Select(x => (Y: Resolve(x, s.MasterName, s.MasterTop).Num("PinY") ?? 0, Depth: x.Ancestors(V + "Shape").Count(), Text: TextOf(x) ?? TextOf(Resolve(x, s.MasterName, s.MasterTop).MasterShape) ?? ""))
                    .Where(t => t.Text.Length > 0).ToList();
                // Member shapes sit inside a compartment shape: list them top to bottom.
                var text = string.Join("\n", lines.OrderBy(t => t.Depth).ThenByDescending(t => t.Y).Select(t => t.Text));
                if (text.Length > 0 || parts.Count > 0) parts.Add(Html(text));
            }
            if (parts.Count == 0) parts.Add(Html(s.Text ?? ""));
            var keyword = kind switch { "interface" => "«interface»", "enumeration" => "«enumeration»", "data type" or "datatype" => "«dataType»", _ => null };
            if (keyword != null && !parts[0].Contains(keyword[1..^1], StringComparison.OrdinalIgnoreCase)) parts[0] = $"<p>{keyword}</p>" + parts[0];
            if (parts.Count == 1) parts.Add("");
            return string.Join("<hr>", parts);
        }

        // ---------------------------------------------------------------- connectors

        private void Connector(XElement shape, Inherited s, double ox, double oy, List<XElement> connects)
        {
            var id = (string?)shape.Attribute("ID") ?? "";
            string? End(string cell) =>
                connects.FirstOrDefault(c => (string?)c.Attribute("FromSheet") == id && (string?)c.Attribute("FromCell") == cell) is { } c
                && (string?)c.Attribute("ToSheet") is { } to && _ids.TryGetValue(to, out var target) ? target : null;
            var source = End("BeginX");
            var target = End("EndX");
            var kind = s.MasterName;
            var style = new StringBuilder("html=1;");
            var dashed = (s.Num("LinePattern") ?? 1) > 1;
            var (start, startFill) = Arrow((int)(s.Num("BeginArrow") ?? 0));
            var (end, endFill) = Arrow((int)(s.Num("EndArrow") ?? 0));
            // UML connectors by name (their arrows vary between stencil versions).
            if (kind.Contains("generali") || kind.Contains("inherit")) (end, endFill, dashed) = ("block", false, false);
            else if (kind.Contains("realiz") || kind.Contains("implement")) (end, endFill, dashed) = ("block", false, true);
            else if (kind.Contains("composition")) (start, startFill) = ("diamond", true);
            else if (kind.Contains("aggregation")) (start, startFill) = ("diamond", false);
            else if (kind.Contains("include") || kind.Contains("extend") || kind.Contains("dependency")) (end, endFill, dashed) = ("open", false, true);
            style.Append("startArrow=").Append(start).Append(";startFill=").Append(startFill ? 1 : 0)
                 .Append(";endArrow=").Append(end).Append(";endFill=").Append(endFill ? 1 : 0).Append(';');
            if (dashed) style.Append("dashed=1;");
            if (Colour(s.Str("LineColor")) is { } stroke) style.Append("strokeColor=").Append(stroke).Append(';');
            var label = s.Text ?? "";
            if (label.Length == 0 && (kind.Contains("include") || kind.Contains("extend"))) label = kind.Contains("include") ? "«include»" : "«extend»";
            var p0 = ((ox + (s.Num("BeginX") ?? 0)) * Px, (pageHeight - oy - (s.Num("BeginY") ?? 0)) * Px);
            var p1 = ((ox + (s.Num("EndX") ?? 0)) * Px, (pageHeight - oy - (s.Num("EndY") ?? 0)) * Px);
            var geo = new XElement("mxGeometry", new XAttribute("relative", "1"), new XAttribute("as", "geometry"),
                new XElement("mxPoint", new XAttribute("x", F(p0.Item1)), new XAttribute("y", F(p0.Item2)), new XAttribute("as", "sourcePoint")),
                new XElement("mxPoint", new XAttribute("x", F(p1.Item1)), new XAttribute("y", F(p1.Item2)), new XAttribute("as", "targetPoint")));
            var cell = new XElement("mxCell", new XAttribute("id", "e" + id), new XAttribute("parent", "1"), new XAttribute("value", Html(label)),
                new XAttribute("style", style.ToString()), new XAttribute("edge", "1"), geo);
            if (source != null) cell.Add(new XAttribute("source", source));
            if (target != null) cell.Add(new XAttribute("target", target));
            _cells.Add(cell);
        }

        /// <summary>Visio arrow head numbers → draw.io arrow name and fill.</summary>
        private static (string Name, bool Fill) Arrow(int n) => n switch
        {
            0 => ("none", false),
            1 or 2 or 3 or 6 or 7 => ("open", false),
            9 or 10 or 11 or 12 => ("block", false),
            20 or 21 or 22 => ("oval", n == 21),
            23 or 24 => ("diamond", n == 24),
            _ => ("classic", true),
        };
    }
}
