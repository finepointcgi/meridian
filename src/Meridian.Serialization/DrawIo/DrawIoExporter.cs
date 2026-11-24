using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization.DrawIo;

/// <summary>
/// Writes diagrams as a draw.io / diagrams.net file (<c>.drawio</c>, uncompressed): one page per diagram, every shape at its
/// position, with draw.io's own UML shapes (class boxes with their members, actors, use cases, lifelines, start / end
/// states, frames, notes) so the file reads back into Meridian as the same kind of diagram. See docs/DRAWIO.md.
/// </summary>
public static class DrawIoExporter
{
    public static string Export(ModelRepository m, IReadOnlyList<Diagram> diagrams)
    {
        var file = new XElement("mxfile",
            new XAttribute("host", "Meridian"),
            new XAttribute("type", "device"),
            diagrams.Select((d, i) => Page(m, d, i)));
        return new XDocument(file).ToString(SaveOptions.None);
    }

    private static string F(double v) => Math.Round(v, 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Text as an HTML label (draw.io's html=1): escaped, new lines as &lt;br&gt;.</summary>
    private static string Html(string s) => WebUtility.HtmlEncode(s).Replace("\r", "").Replace("\n", "<br>");

    private static XElement Page(ModelRepository m, Diagram d, int index)
    {
        var cells = new List<XElement>
        {
            new("mxCell", new XAttribute("id", "0")),
            new("mxCell", new XAttribute("id", "1"), new XAttribute("parent", "0")),
        };
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);   // diagram node id → cell id
        var nodes = d.Nodes.ToList();
        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var n0 = 0;
        foreach (var n in nodes) ids[n.Id] = $"n{++n0}";

        // Parents first, so nested shapes (lanes, composite states, packages) can be placed inside them.
        int Depth(DiagramNode n)
        {
            var depth = 0;
            for (var p = n.ParentId; p != null && depth < 50 && byId.TryGetValue(p, out var pn); p = pn.ParentId) depth++;
            return depth;
        }
        foreach (var n in nodes.OrderBy(Depth))
        {
            var parent = n.ParentId != null && byId.TryGetValue(n.ParentId, out var pn) ? pn : null;
            var b = n.Bounds;
            var local = parent == null ? b : b.Offset(-parent.Bounds.X, -parent.Bounds.Y);
            var made = NodeCells(m, d, n, ids[n.Id], parent == null ? "1" : ids[parent.Id], local).ToList();
            // A hint draw.io ignores (and keeps), so the page reads back into Meridian as the same kind of element.
            if (made.FirstOrDefault(c => c.Attribute("id")?.Value == ids[n.Id]) is { } main)
            {
                main.SetAttributeValue("style", main.Attribute("style")!.Value + $"meridian={n.Element.Kind};");
                if (IsStructure(d)) StructureHints(m, d, n.Element, main);
            }
            cells.AddRange(made);
        }

        var e0 = 0;
        foreach (var e in d.Edges)
        {
            if (!ids.TryGetValue(e.Source.Id, out var s) || !ids.TryGetValue(e.Target.Id, out var t)) continue;
            cells.Add(EdgeCell(m, d, e, $"e{++e0}", s, t));
        }

        if (IsStructure(d) && !nodes.Any(n => n.Element.Kind is ElementKind.Property or ElementKind.Port))
        {
            // An empty internal block / parametric diagram still names its block.
            cells.Add(Vertex("ctx", "1", $"text;html=1;meridian=Context;meridianDiagram={d.DiagramType};meridianContext={Hint(d.Owner?.Name)};", Html(d.Owner?.Name ?? ""), new Bounds(20, 20, 160, 30)));
        }

        var right = nodes.Count > 0 ? nodes.Max(n => n.Bounds.Right) + 40 : 850;
        var bottom = nodes.Count > 0 ? nodes.Max(n => n.Bounds.Bottom) + 40 : 1100;
        var model = new XElement("mxGraphModel",
            new XAttribute("dx", "1000"), new XAttribute("dy", "800"), new XAttribute("grid", "1"), new XAttribute("gridSize", "10"),
            new XAttribute("guides", "1"), new XAttribute("tooltips", "1"), new XAttribute("connect", "1"), new XAttribute("arrows", "1"),
            new XAttribute("fold", "1"), new XAttribute("page", "1"), new XAttribute("pageScale", "1"),
            new XAttribute("pageWidth", F(Math.Max(850, right))), new XAttribute("pageHeight", F(Math.Max(1100, bottom))),
            new XAttribute("math", "0"), new XAttribute("shadow", "0"),
            new XElement("root", cells));
        return new XElement("diagram", new XAttribute("id", $"meridian-{index + 1}"), new XAttribute("name", d.Name.Length > 0 ? d.Name : $"Page-{index + 1}"), model);
    }

    private static XElement Vertex(string id, string parent, string style, string value, Bounds b) =>
        new("mxCell",
            new XAttribute("id", id), new XAttribute("value", value), new XAttribute("style", style),
            new XAttribute("vertex", "1"), new XAttribute("parent", parent),
            new XElement("mxGeometry", new XAttribute("x", F(b.X)), new XAttribute("y", F(b.Y)), new XAttribute("width", F(b.Width)), new XAttribute("height", F(b.Height)), new XAttribute("as", "geometry")));

    private static readonly ElementKind[] ClassKinds =
    [
        ElementKind.Block, ElementKind.Class, ElementKind.InterfaceBlock, ElementKind.Interface, ElementKind.ValueType, ElementKind.Enumeration,
        ElementKind.DataType, ElementKind.Component, ElementKind.ConstraintBlock,
    ];

    private static string Colours(DiagramNode n)
    {
        var sb = new StringBuilder();
        if (n.Style.TryGetValue("fill", out var fill) && SvgRenderer.SafeColor(fill) is { } f) sb.Append($"fillColor={f};");
        if (n.Style.TryGetValue("stroke", out var stroke) && SvgRenderer.SafeColor(stroke) is { } s) sb.Append($"strokeColor={s};");
        if (n.Style.TryGetValue("text", out var text) && SvgRenderer.SafeColor(text) is { } t) sb.Append($"fontColor={t};");
        return sb.ToString();
    }

    private static IEnumerable<XElement> NodeCells(ModelRepository m, Diagram d, DiagramNode n, string id, string parent, Bounds b)
    {
        var e = n.Element;
        var name = e.Name;
        var colours = Colours(n);
        const string basic = "whiteSpace=wrap;html=1;";

        if (ClassKinds.Contains(e.Kind))
        {
            foreach (var c in ClassCells(m, n, id, parent, b, colours)) yield return c;
            yield break;
        }
        switch (e.Kind)
        {
            case ElementKind.Actor:
                yield return Vertex(id, parent, "shape=umlActor;verticalLabelPosition=bottom;verticalAlign=top;html=1;outlineConnect=0;" + colours, Html(name),
                    new Bounds(b.X + b.Width / 2 - 15, b.Y, 30, Math.Max(40, b.Height - 30)));
                yield break;
            case ElementKind.UseCase:
                yield return Vertex(id, parent, "ellipse;" + basic + colours, Html(name), b);
                yield break;
            case ElementKind.InitialNode or ElementKind.InitialState:
                yield return Vertex(id, parent, "ellipse;html=1;shape=startState;fillColor=#000000;strokeColor=#000000;", "", b);
                yield break;
            case ElementKind.ActivityFinalNode or ElementKind.FinalState:
                yield return Vertex(id, parent, "ellipse;html=1;shape=endState;fillColor=#000000;strokeColor=#000000;", "", b);
                yield break;
            case ElementKind.DecisionNode:
                yield return Vertex(id, parent, "rhombus;" + basic + colours, Html(name), b);
                yield break;
            case ElementKind.ForkNode:
                yield return Vertex(id, parent, "rounded=0;html=1;fillColor=#000000;strokeColor=none;", "", b);
                yield break;
            case ElementKind.Pseudostate:
            {
                var sub = e.Get(FieldKeys.Subtype);
                if (sub is "fork" or "join") yield return Vertex(id, parent, "rounded=0;html=1;fillColor=#000000;strokeColor=none;", "", b);
                else if (sub is "choice" or "junction") yield return Vertex(id, parent, "rhombus;" + basic + colours, Html(name), b);
                else yield return Vertex(id, parent, "ellipse;" + basic + colours, Html(name.Length > 0 ? name : sub), b);
                yield break;
            }
            case ElementKind.State:
            {
                var lines = new List<string> { name };
                if (e.Get(FieldKeys.Entry) is { Length: > 0 } en) lines.Add("entry / " + en);
                if (e.Get(FieldKeys.DoActivity) is { Length: > 0 } da) lines.Add("do / " + da);
                if (e.Get(FieldKeys.Exit) is { Length: > 0 } ex) lines.Add("exit / " + ex);
                var nested = d.Nodes.Any(x => x.ParentId == n.Id);
                yield return Vertex(id, parent, "rounded=1;arcSize=" + (nested ? "8" : "40") + ";" + basic + (nested ? "verticalAlign=top;container=1;" : "") + colours, Html(string.Join("\n", lines)), b);
                yield break;
            }
            case ElementKind.Lifeline:
                yield return Vertex(id, parent, "shape=umlLifeline;perimeter=lifelinePerimeter;" + basic + "container=1;collapsible=0;recursiveResize=0;outlineConnect=0;size=40;" + colours, Html(name), b);
                yield break;
            case ElementKind.CombinedFragment:
            {
                var op = e.Get(FieldKeys.Operator) is { Length: > 0 } o ? o : "alt";
                yield return Vertex(id, parent, "shape=umlFrame;" + basic + "width=60;height=24;fillColor=none;" + colours, Html(op), b);
                var guards = new List<string>();
                if (e.Get(FieldKeys.Guard) is { Length: > 0 } g) guards.Add(g);
                guards.AddRange(e.OwnedElements.Where(x => x.Kind == ElementKind.InteractionOperand).Select(x => x.Get(FieldKeys.Guard)).Where(x => x.Length > 0));
                if (guards.Count > 0)
                    yield return Vertex(id + "g", parent, "text;html=1;align=left;verticalAlign=top;", Html($"[{guards[0]}]"), new Bounds(b.X + 66, b.Y + 2, Math.Max(60, b.Width - 72), 20));
                yield break;
            }
            case ElementKind.Comment:
                yield return Vertex(id, parent, "shape=note;" + basic + "size=14;align=left;verticalAlign=top;spacingLeft=4;" + colours, Html(e.Get(FieldKeys.Body)), b);
                yield break;
            case ElementKind.Package:
                yield return Vertex(id, parent, "shape=folder;fontStyle=1;tabWidth=80;tabHeight=20;tabPosition=left;" + basic + "verticalAlign=top;align=left;spacingLeft=6;spacingTop=18;" + colours, Html(name), b);
                yield break;
            case ElementKind.Partition:
                yield return Vertex(id, parent, "swimlane;" + basic + "startSize=30;container=1;" + colours, Html(name), b);
                yield break;
            case ElementKind.Requirement:
            {
                var text = $"«requirement»\n{name}";
                if (e.Get(FieldKeys.RequirementId) is { Length: > 0 } rid) text += $"\nid: {rid}";
                if (e.Get(FieldKeys.Text) is { Length: > 0 } rt) text += $"\n{rt}";
                yield return Vertex(id, parent, "rounded=0;" + basic + "verticalAlign=top;" + colours, Html(text), b);
                yield break;
            }
            case ElementKind.Shape:
                yield return FreeShape(e, id, parent, b, colours);
                yield break;
            case ElementKind.Action:
                yield return Vertex(id, parent, "rounded=1;arcSize=30;" + basic + colours, Html(name), b);
                yield break;
            default:
            {
                var label = e.Kind is ElementKind.Port ? name : $"«{Metamodel.DisplayName(e.Kind).ToLowerInvariant()}»\n{e.DisplayName}";
                yield return Vertex(id, parent, "rounded=0;" + basic + colours, Html(label), b);
                yield break;
            }
        }
    }

    private static XElement FreeShape(Element e, string id, string parent, Bounds b, string colours)
    {
        const string basic = "whiteSpace=wrap;html=1;";
        if (IconLibrary.IsIcon(e.Get(FieldKeys.Subtype))) return IconShape(e, id, parent, b, colours);
        var style = e.Get(FieldKeys.Subtype) switch
        {
            "rounded" => "rounded=1;" + basic,
            "ellipse" => "ellipse;" + basic,
            "diamond" => "rhombus;" + basic,
            "text" => "text;html=1;align=center;verticalAlign=middle;" + basic,
            "cylinder" => "shape=cylinder3;boundedLbl=1;backgroundOutline=1;size=15;" + basic,
            "hexagon" => "shape=hexagon;perimeter=hexagonPerimeter2;fixedSize=1;" + basic,
            "parallelogram" => "shape=parallelogram;perimeter=parallelogramPerimeter;fixedSize=1;" + basic,
            "triangle" => "triangle;direction=north;" + basic,
            "document" => "shape=document;boundedLbl=1;" + basic,
            "cloud" => "ellipse;shape=cloud;" + basic,
            "callout" => "shape=callout;perimeter=calloutPerimeter;" + basic,
            DiagramExtras.ImageSubtype => ImageStyle(e),
            _ => "rounded=0;" + basic,
        };
        return Vertex(id, parent, style + colours, Html(e.Name), b);
    }

    /// <summary>
    /// A cloud or network icon: the line drawing as an SVG picture with the name (and provider) under it; a group icon
    /// becomes a container frame with its name in the top-left corner.
    /// </summary>
    private static XElement IconShape(Element e, string id, string parent, Bounds b, string colours)
    {
        var icon = IconLibrary.Find(e.Get(FieldKeys.Subtype))!;
        var name = e.Name.Length > 0 ? e.Name : icon.Name;
        var provider = e.Get(IconLibrary.ProviderField).Trim();
        var label = provider.Length > 0 ? $"{name}\n{provider}" : name;
        if (icon.Container)
            return Vertex(id, parent, $"rounded=1;arcSize=4;container=1;whiteSpace=wrap;html=1;verticalAlign=top;align=left;spacingLeft=8;fontStyle=1;{(icon.Key is "region" or "zone" or "subnet" ? "dashed=1;" : "")}" + colours, Html(label), b);
        var svg = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(IconLibrary.Svg(icon, "#3a4458")));
        return Vertex(id, parent, $"shape=image;html=1;verticalLabelPosition=bottom;verticalAlign=top;imageAspect=0;aspect=fixed;image=data:image/svg+xml,{svg};", Html(label), b);
    }

    /// <summary>draw.io keeps pictures in the style as "image=data:image/png,BASE64" (';' would end the style).</summary>
    private static string ImageStyle(Element e)
    {
        if (DiagramExtras.DecodeImage(e.Get(DiagramExtras.ImageField)) is not { } img) return "rounded=0;whiteSpace=wrap;html=1;dashed=1;";
        return $"shape=image;html=1;verticalLabelPosition=bottom;verticalAlign=top;imageAspect=0;aspect=fixed;image=data:{img.Mime},{Convert.ToBase64String(img.Bytes)};";
    }

    /// <summary>A UML class box: a stack-layout swimlane with one row per member, as draw.io's own "Class" shape.</summary>
    private static IEnumerable<XElement> ClassCells(ModelRepository m, DiagramNode n, string id, string parent, Bounds b, string colours)
    {
        var e = n.Element;
        var keyword = e.Kind switch
        {
            ElementKind.Block => "block",
            ElementKind.InterfaceBlock => "interfaceBlock",
            ElementKind.Interface => "interface",
            ElementKind.ValueType => "valueType",
            ElementKind.Enumeration => "enumeration",
            ElementKind.DataType => "dataType",
            ElementKind.Component => "component",
            ElementKind.ConstraintBlock => "constraint",
            _ => "",
        };
        var title = keyword.Length > 0 ? $"«{keyword}»\n{e.Name}" : e.Name;
        const int header = 40, row = 22;
        var fontStyle = e.IsAbstract ? 3 : 1;
        yield return Vertex(id, parent,
            $"swimlane;fontStyle={fontStyle};align=center;verticalAlign=top;childLayout=stackLayout;horizontal=1;startSize={header};horizontalStack=0;resizeParent=1;resizeParentMax=0;resizeLast=0;collapsible=1;marginBottom=0;whiteSpace=wrap;html=1;" + colours,
            Html(title), b);

        string TypeName(string t) => t.Length == 0 ? "" : m.Find(t)?.Name ?? t;
        var attrs = new List<(string Text, bool Port)>();
        var ops = new List<string>();
        foreach (var c in e.OwnedElements)
        {
            var vis = c.Get(FieldKeys.Visibility) switch { "private" => "- ", "protected" => "# ", "package" => "~ ", "public" => "+ ", _ => "" };
            switch (c.Kind)
            {
                case ElementKind.Property or ElementKind.Port:
                {
                    var s = vis + c.Name;
                    if (TypeName(c.TypeRef) is { Length: > 0 } t) s += ": " + t;
                    if (c.Get(FieldKeys.Multiplicity) is { Length: > 0 } mult && mult != "1") s += $" [{mult}]";
                    if (c.Get(FieldKeys.DefaultValue) is { Length: > 0 } dv) s += " = " + dv;
                    attrs.Add((s, c.Kind == ElementKind.Port));
                    break;
                }
                case ElementKind.EnumerationLiteral:
                    attrs.Add((c.Name, false));
                    break;
                case ElementKind.Operation:
                {
                    var sig = c.Get(FieldKeys.Signature);
                    ops.Add(vis + c.Name + (sig.StartsWith('(') ? sig : "()"));
                    break;
                }
            }
        }
        var y = (double)header;
        var i = 0;
        var rowStyle = "text;strokeColor=none;fillColor=none;align=left;verticalAlign=top;spacingLeft=4;spacingRight=4;overflow=hidden;rotatable=0;points=[[0,0.5],[1,0.5]];portConstraint=eastwest;whiteSpace=wrap;html=1;";
        foreach (var (a, port) in attrs)
        {
            // Ports are rows like properties; the hint keeps them ports when the page is read back.
            yield return Vertex($"{id}a{++i}", id, rowStyle + (port ? "meridian=Port;" : ""), Html(a), new Bounds(0, y, b.Width, row));
            y += row;
        }
        if (ops.Count > 0)
        {
            yield return Vertex($"{id}s", id, "line;strokeWidth=1;fillColor=none;align=left;verticalAlign=middle;spacingTop=-1;spacingLeft=3;spacingRight=3;rotatable=0;labelPosition=right;points=[];portConstraint=eastwest;", "", new Bounds(0, y, b.Width, 8));
            y += 8;
            foreach (var o in ops)
            {
                yield return Vertex($"{id}o{++i}", id, rowStyle, Html(o), new Bounds(0, y, b.Width, row));
                y += row;
            }
        }
    }

    /// <summary>Internal block and parametric diagrams: parts, ports and parameters are written as "name : Type", with hints
    /// (diagram kind, the block the diagram belongs to, the type and its kind) so the page reads back as the same parts.</summary>
    private static bool IsStructure(Diagram d) => d.DiagramType is DiagramType.InternalBlock or DiagramType.Parametric;

    /// <summary>A style value draw.io keeps as is (';' and '=' would break the style).</summary>
    internal static string Hint(string? s) => Uri.EscapeDataString(s ?? "");

    private static void StructureHints(ModelRepository m, Diagram d, Element e, XElement cell)
    {
        var style = cell.Attribute("style")!.Value + $"meridianDiagram={d.DiagramType};meridianContext={Hint(d.Owner?.Name)};";
        if (e.Kind is ElementKind.Property or ElementKind.Port)
        {
            var type = e.TypeRef.Length > 0 ? m.Find(e.TypeRef) : null;
            var typeName = type?.Name ?? e.TypeRef;
            if (typeName.Length > 0) style += $"meridianType={Hint(typeName)};";
            if (type != null) style += $"meridianTypeKind={type.Kind};";
            if (e.Get(FieldKeys.Multiplicity) is { Length: > 0 } mult && mult != "1") style += $"meridianMult={Hint(mult)};";
            cell.SetAttributeValue("value", Html(typeName.Length > 0 ? $"{e.Name} : {typeName}" : e.Name));
            if (e.Kind == ElementKind.Port) style += "labelPosition=center;verticalLabelPosition=top;verticalAlign=bottom;";
        }
        cell.SetAttributeValue("style", style);
    }

    private static XElement EdgeCell(ModelRepository m, Diagram d, DiagramEdge e, string id, string source, string target)
    {
        var r = e.Relationship;
        var label = r.Name;
        string style;
        switch (r.Kind)
        {
            case ElementKind.Generalization: style = "endArrow=block;endFill=0;endSize=12;"; break;
            case ElementKind.Realization: style = "endArrow=block;endFill=0;endSize=12;dashed=1;"; break;
            case ElementKind.Composition: style = "startArrow=diamondThin;startFill=1;startSize=14;endArrow=none;"; break;
            case ElementKind.Aggregation: style = "startArrow=diamondThin;startFill=0;startSize=14;endArrow=none;"; break;
            case ElementKind.Association: style = "endArrow=none;"; break;
            case ElementKind.ControlFlow or ElementKind.ObjectFlow:
                style = "endArrow=classic;";
                if (r.Get(FieldKeys.Guard) is { Length: > 0 } guard) label = $"[{guard}]";
                break;
            case ElementKind.Transition:
            {
                style = "endArrow=classic;";
                var t = r.Name;
                if (r.Get(FieldKeys.Guard) is { Length: > 0 } g) t += $" [{g}]";
                if (r.Get(FieldKeys.Effect) is { Length: > 0 } ef) t += $" / {ef}";
                label = t.Trim();
                break;
            }
            case ElementKind.Message:
                style = r.Get(FieldKeys.MessageSort) switch
                {
                    "reply" => "endArrow=open;dashed=1;",
                    "asynchCall" => "endArrow=open;",
                    _ => "endArrow=block;endFill=1;",
                };
                break;
            case ElementKind.Line:
                style = r.Get(FieldKeys.Subtype) switch
                {
                    "line" => "endArrow=none;",
                    "dashed" => "endArrow=none;dashed=1;",
                    "dashedArrow" => "endArrow=classic;dashed=1;",
                    "dotted" => "endArrow=classic;dashed=1;dashPattern=1 2;",
                    "bidirectional" => "startArrow=classic;endArrow=classic;",
                    _ => "endArrow=classic;",
                };
                break;
            case ElementKind.Include or ElementKind.Extend or ElementKind.Dependency or ElementKind.Trace or ElementKind.Satisfy or ElementKind.Verify
                or ElementKind.Refine or ElementKind.DeriveReqt or ElementKind.Allocate:
                style = "endArrow=open;endSize=12;dashed=1;";
                if (r.Kind != ElementKind.Dependency) label = $"«{Metamodel.DisplayName(r.Kind).ToLowerInvariant()}»" + (r.Name.Length > 0 ? " " + r.Name : "");
                break;
            default: style = "endArrow=none;"; break;
        }
        style += "html=1;rounded=0;";
        if (IsStructure(d))
        {
            style += $"meridian={r.Kind};";
            if (r.Get(FieldKeys.ItemFlow) is { Length: > 0 } item)
            {
                var itemName = m.Find(item)?.Name ?? item;
                style += $"meridianItem={Hint(itemName)};endArrow=classic;";
                if (r.Get(FieldKeys.FlowDirection) == "reverse") style += "meridianFlow=reverse;startArrow=classic;";
                if (label.Length == 0) label = itemName;
            }
        }
        if (e.Style.TryGetValue("stroke", out var stroke) && SvgRenderer.SafeColor(stroke) is { } sc) style += $"strokeColor={sc};";

        var geo = new XElement("mxGeometry", new XAttribute("relative", "1"), new XAttribute("as", "geometry"));
        if (r.Kind == ElementKind.Message && e.Waypoints.Count > 0)
        {
            // Sequence messages sit at a height on the lifelines: attach both ends there.
            var y = e.Waypoints[0].Y;
            double Rel(DiagramNode n) => n.Bounds.Height > 0 ? Math.Clamp((y - n.Bounds.Y) / n.Bounds.Height, 0, 1) : 0.5;
            style += $"exitX=0.5;exitY={F(Rel(e.Source))};exitDx=0;exitDy=0;entryX=0.5;entryY={F(Rel(e.Target))};entryDx=0;entryDy=0;";
            if (ReferenceEquals(e.Source, e.Target))
            {
                var x = e.Source.Bounds.Center.X;
                geo.Add(new XElement("Array", new XAttribute("as", "points"),
                    new XElement("mxPoint", new XAttribute("x", F(x + 50)), new XAttribute("y", F(y))),
                    new XElement("mxPoint", new XAttribute("x", F(x + 50)), new XAttribute("y", F(y + 24)))));
            }
        }
        else if (e.Waypoints.Count > 0)
        {
            geo.Add(new XElement("Array", new XAttribute("as", "points"),
                e.Waypoints.Select(p => new XElement("mxPoint", new XAttribute("x", F(p.X)), new XAttribute("y", F(p.Y))))));
        }
        var cell = new XElement("mxCell",
            new XAttribute("id", id), new XAttribute("value", Html(label)), new XAttribute("style", style),
            new XAttribute("edge", "1"), new XAttribute("parent", "1"), new XAttribute("source", source), new XAttribute("target", target), geo);
        return cell;
    }
}
