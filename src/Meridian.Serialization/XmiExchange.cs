using System.Xml;
using System.Xml.Linq;
using Meridian.Core.CodeGen;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization;

public sealed record ImportReport(int Elements, int Relationships, IReadOnlyList<string> Warnings);

/// <summary>
/// XMI 2.5.1 interchange (UML 2.5.1 + SysML 1.6 profile) for moving models to and from
/// Cameo/MagicDraw, Enterprise Architect, Papyrus and Capella. Diagrams are not exchanged
/// (tools rarely agree on UML-DI); semantic content is.
/// </summary>
public static class XmiExchange
{
    public static readonly XNamespace Xmi = "http://www.omg.org/spec/XMI/20131001";
    public static readonly XNamespace Uml = "http://www.omg.org/spec/UML/20161101";
    public static readonly XNamespace SysML = "http://www.omg.org/spec/SysML/20181001/SysML";
    public static readonly XNamespace Meridian = "urn:meridian:extension";
    public static readonly XNamespace UmlDi = "http://www.omg.org/spec/UML/20161101/UMLDI";
    public static readonly XNamespace Dc = "http://www.omg.org/spec/DD/20131001/DC";
    private const string PrimitivesHref = "http://www.omg.org/spec/UML/20161101/PrimitiveTypes.xmi#";

    // ======================================================================= export

    public static void Export(ModelRepository model, string path)
    {
        using var w = XmlWriter.Create(path, new XmlWriterSettings { Indent = true });
        ToXml(model).Save(w);
    }

    public static XDocument ToXml(ModelRepository model)
    {
        var stereos = new List<XElement>();
        var ext = new XElement(Xmi + "Extension", new XAttribute("extender", "Meridian"));
        var umlModel = new XElement(Uml + "Model", Id(model.Root), new XAttribute("name", model.Root.Name));
        AddComments(umlModel, model.Root);
        foreach (var c in model.Root.OwnedElements) Emit(model, umlModel, c, stereos, ext);

        var root = new XElement(Xmi + "XMI",
            new XAttribute(XNamespace.Xmlns + "xmi", Xmi.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "uml", Uml.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "sysml", SysML.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "meridian", Meridian.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "umldi", UmlDi.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "dc", Dc.NamespaceName),
            new XElement(Xmi + "Documentation", new XElement(Xmi + "exporter", "Meridian"), new XElement(Xmi + "exporterVersion", "0.1")),
            umlModel);
        root.Add(stereos);
        root.Add(model.Diagrams.Select(DiagramToDi));
        if (ext.HasElements) root.Add(ext);
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    private static XAttribute Id(Element e) => new(Xmi + "id", e.Id);

    private static string Num(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>UML Diagram Interchange: shapes with bounds, edges with waypoints (plus Meridian nesting/colour).</summary>
    private static XElement DiagramToDi(Diagram d)
    {
        var x = new XElement(UmlDi + "UMLDiagram", new XAttribute(Xmi + "id", d.Id), new XAttribute("name", d.Name),
            new XAttribute("modelElement", d.Owner?.Id ?? ""), new XAttribute(Meridian + "diagramType", d.DiagramType.ToString()));
        if (!string.IsNullOrEmpty(d.Documentation)) x.Add(new XAttribute(Meridian + "documentation", d.Documentation));
        foreach (var n in d.Nodes)
        {
            var shape = new XElement("ownedElement", new XAttribute(Xmi + "type", "umldi:UMLShape"), new XAttribute(Xmi + "id", n.Id), new XAttribute("modelElement", n.Element.Id),
                new XElement("bounds", new XAttribute(Xmi + "type", "dc:Bounds"), new XAttribute("x", Num(n.Bounds.X)), new XAttribute("y", Num(n.Bounds.Y)),
                    new XAttribute("width", Num(n.Bounds.Width)), new XAttribute("height", Num(n.Bounds.Height))));
            if (n.ParentId != null) shape.Add(new XAttribute(Meridian + "parent", n.ParentId));
            if (n.Style.Count > 0) shape.Add(new XAttribute(Meridian + "style", string.Join(";", n.Style.Select(kv => $"{kv.Key}={kv.Value}"))));
            x.Add(shape);
        }
        foreach (var e in d.Edges)
            x.Add(new XElement("ownedElement", new XAttribute(Xmi + "type", "umldi:UMLEdge"), new XAttribute(Xmi + "id", e.Id), new XAttribute("modelElement", e.Relationship.Id),
                new XAttribute("source", e.Source.Id), new XAttribute("target", e.Target.Id),
                e.Waypoints.Select(p => new XElement("waypoint", new XAttribute(Xmi + "type", "dc:Point"), new XAttribute("x", Num(p.X)), new XAttribute("y", Num(p.Y))))));
        return x;
    }

    private static void ImportDiagrams(XElement root, ModelRepository model, Dictionary<string, Element> map, List<string> warnings)
    {
        double D(XElement? e, string a) => double.TryParse(e?.Attribute(a)?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        foreach (var dx in root.Descendants().Where(e => e.Name.LocalName == "UMLDiagram"))
        {
            var owner = map.GetValueOrDefault(Attr(dx, "modelElement") ?? "") ?? model.Root;
            var type = Enum.TryParse<DiagramType>(dx.Attribute(Meridian + "diagramType")?.Value, out var t) ? t : DiagramType.Class;
            if (!Metamodel.IsValidDiagramContext(type, owner.Kind))
            {
                warnings.Add($"Diagram '{Attr(dx, "name")}' moved: its owner can't hold a {Metamodel.DisplayName(type)}");
                owner = model.DefaultOwnerFor(owner, ElementKind.Package);
                type = DiagramType.Class;
            }
            var id = XmiId(dx);
            var d = new Diagram(type, id != null && model.Find(id) == null ? id : null);
            ModelRepository.SetFieldForLoad(d, FieldKeys.Name, Attr(dx, "name"));
            ModelRepository.SetFieldForLoad(d, FieldKeys.Documentation, dx.Attribute(Meridian + "documentation")?.Value);
            model.AttachForLoad(owner, d);
            var shapes = new Dictionary<string, DiagramNode>();
            foreach (var se in dx.Elements().Where(e => XmiType(e) == "UMLShape"))
            {
                if (!map.TryGetValue(Attr(se, "modelElement") ?? "", out var el)) continue;
                var b = se.Elements().FirstOrDefault(e => e.Name.LocalName == "bounds");
                var node = new DiagramNode(el, new Bounds(D(b, "x"), D(b, "y"), Math.Max(4, D(b, "width")), Math.Max(4, D(b, "height"))), XmiId(se));
                if (se.Attribute(Meridian + "parent")?.Value is { } parent) ModelRepository.SetNodeParentForLoad(node, parent);
                foreach (var kv in (se.Attribute(Meridian + "style")?.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                    if (kv.Split('=', 2) is [var k, var v]) ModelRepository.SetNodeStyleForLoad(node, k, v);
                model.AddNodeForLoad(d, node);
                shapes[node.Id] = node;
            }
            foreach (var ee in dx.Elements().Where(e => XmiType(e) == "UMLEdge"))
            {
                if (!map.TryGetValue(Attr(ee, "modelElement") ?? "", out var rel) || rel is not Relationship r) continue;
                if (!shapes.TryGetValue(Attr(ee, "source") ?? "", out var sn) || !shapes.TryGetValue(Attr(ee, "target") ?? "", out var tn)) continue;
                var edge = new DiagramEdge(r, sn, tn, XmiId(ee));
                ModelRepository.SetWaypointsForLoad(edge, ee.Elements().Where(w => w.Name.LocalName == "waypoint").Select(w => new Pt(D(w, "x"), D(w, "y"))));
                model.AddEdgeForLoad(d, edge);
            }
        }
    }

    /// <summary>Fields with no standard UML/SysML home; they round-trip through the Meridian XMI extension.</summary>
    private static readonly HashSet<string> ExtensionOnlyFields =
    [
        FieldKeys.Subtype, FieldKeys.PortKind, FieldKeys.ItemFlow, FieldKeys.FlowDirection, FieldKeys.SourcePart, FieldKeys.TargetPart,
        FieldKeys.Behavior, FieldKeys.Represents, FieldKeys.Entry, FieldKeys.Exit, FieldKeys.DoActivity, FieldKeys.Metaclass,
        FieldKeys.TagType, FieldKeys.TagValues, FieldKeys.Color, FieldKeys.SqlType, FieldKeys.PrimaryKey, FieldKeys.Nullable,
        FieldKeys.Unique, FieldKeys.FkColumns, FieldKeys.RefColumns,
        Core.Analysis.ViewFields.Query, Core.Analysis.ViewFields.Columns, Core.Analysis.ViewFields.Severity, Core.Analysis.ViewFields.Message,
        .. ExtFieldKeys.All,
    ];

    /// <summary>Kinds without a direct UML metaclass: exported as a UML element marked with meridian:kind.</summary>
    private static bool NeedsKindMarker(ElementKind k) => k is ElementKind.BpmnPool or ElementKind.BpmnLane or ElementKind.BpmnNode
        or ElementKind.ArchiElement or ElementKind.Table or ElementKind.Column or ElementKind.TagDefinition
        or ElementKind.BpmnFlow or ElementKind.ArchiRelation or ElementKind.ForeignKey or ElementKind.Binding
        or ElementKind.Shape or ElementKind.Line
        or ElementKind.TableView or ElementKind.QueryView or ElementKind.ValidationRule
        or ElementKind.Feature or ElementKind.Configuration or ElementKind.TradeStudy
        or ElementKind.FeatureDecomposition or ElementKind.Requires or ElementKind.Excludes;
    private static XAttribute Type(string t) => new(Xmi + "type", "uml:" + t);

    private static void Emit(ModelRepository m, XElement parent, Element e, List<XElement> st, XElement ext)
    {
        if (e.Kind == ElementKind.Diagram || e.Kind == ElementKind.Comment) { if (e.Kind == ElementKind.Comment) AddComment(parent, e); return; }

        // Custom stereotypes and tagged values survive a Meridian round trip through an extension.
        foreach (var kv in e.Fields.Where(f => f.Key == FieldKeys.Stereotypes || f.Key.StartsWith(FieldKeys.TagPrefix) || ExtensionOnlyFields.Contains(f.Key)))
            ext.Add(new XElement(Meridian + "field", new XAttribute("ref", e.Id), new XAttribute("key", kv.Key), new XAttribute("value", kv.Value)));

        if (e is Relationship r)
        {
            // Generalizations, includes and extends are owned by their source classifier in UML; they are emitted there.
            if (r.Kind is not (ElementKind.Generalization or ElementKind.Include or ElementKind.Extend)) EmitRelationship(parent, r, st);
            return;
        }

        var (tag, type) = e.Kind switch
        {
            ElementKind.Package => ("packagedElement", "Package"),
            ElementKind.Class or ElementKind.Block or ElementKind.Requirement or ElementKind.ConstraintBlock or ElementKind.InterfaceBlock => ("packagedElement", "Class"),
            ElementKind.Interface => ("packagedElement", "Interface"),
            ElementKind.DataType or ElementKind.ValueType => ("packagedElement", "DataType"),
            ElementKind.Enumeration => ("packagedElement", "Enumeration"),
            ElementKind.EnumerationLiteral => ("ownedLiteral", "EnumerationLiteral"),
            ElementKind.Component => ("packagedElement", "Component"),
            ElementKind.Actor => ("packagedElement", "Actor"),
            ElementKind.UseCase => ("packagedElement", "UseCase"),
            ElementKind.Activity or ElementKind.TestCase => ("packagedElement", "Activity"),
            ElementKind.StateMachine => ("packagedElement", "StateMachine"),
            ElementKind.Interaction => ("packagedElement", "Interaction"),
            ElementKind.Lifeline => ("lifeline", "Lifeline"),
            ElementKind.CombinedFragment => ("fragment", "CombinedFragment"),
            ElementKind.Partition => ("group", "ActivityPartition"),
            ElementKind.InputPin => ("input", "InputPin"),
            ElementKind.OutputPin => ("output", "OutputPin"),
            ElementKind.ObjectNode => ("node", "CentralBufferNode"),
            ElementKind.Profile => ("packagedElement", "Profile"),
            ElementKind.Stereotype => ("packagedElement", "Stereotype"),
            ElementKind.Column or ElementKind.TagDefinition => ("ownedAttribute", "Property"),
            ElementKind.Property => ("ownedAttribute", "Property"),
            ElementKind.Port => ("ownedAttribute", "Port"),
            ElementKind.Operation => ("ownedOperation", "Operation"),
            ElementKind.Action => ("node", "OpaqueAction"),
            ElementKind.InitialNode => ("node", "InitialNode"),
            ElementKind.ActivityFinalNode => ("node", "ActivityFinalNode"),
            ElementKind.DecisionNode => ("node", "DecisionNode"),
            ElementKind.ForkNode => ("node", "ForkNode"),
            ElementKind.State => ("subvertex", "State"),
            ElementKind.InitialState => ("subvertex", "Pseudostate"),
            ElementKind.FinalState => ("subvertex", "FinalState"),
            _ => ("packagedElement", "Class"),
        };
        // Behaviors nested in classifiers are ownedBehavior; classifiers nested in classifiers are nestedClassifier.
        if (tag == "packagedElement" && e.Owner != null && e.Owner.Kind is not (ElementKind.Model or ElementKind.Package))
            tag = Metamodel.IsBehavior(e.Kind) || e.Kind == ElementKind.TestCase ? "ownedBehavior" : "nestedClassifier";

        var x = new XElement(tag, Type(type), Id(e));
        if (NeedsKindMarker(e.Kind)) x.Add(new XAttribute(Meridian + "kind", e.Kind.ToString()));
        if (!string.IsNullOrEmpty(e.Name)) x.Add(new XAttribute("name", e.Name));
        if (!string.IsNullOrEmpty(e.Get(FieldKeys.Visibility))) x.Add(new XAttribute("visibility", e.Get(FieldKeys.Visibility)));
        if (e.IsAbstract) x.Add(new XAttribute("isAbstract", "true"));
        if (e.Kind == ElementKind.InitialState) x.Add(new XAttribute("kind", "initial"));
        if (e.Kind == ElementKind.CombinedFragment)
        {
            x.Add(new XAttribute("interactionOperator", e.Get(FieldKeys.Operator) is { Length: > 0 } op ? op : "alt"));
            if (e.Get(FieldKeys.Guard) is { Length: > 0 } fg) x.Add(new XAttribute(Meridian + "guard", fg));
        }
        if (e.Kind == ElementKind.Port && e.Get(FieldKeys.Direction) is { Length: > 0 } dir) x.Add(new XAttribute(Meridian + "direction", dir));
        AddComments(x, e);

        if (Metamodel.IsTypedFeature(e.Kind))
        {
            var t = m.ResolveType(e);
            if (t != null) x.Add(new XAttribute("type", t.Id));
            else if (Metamodel.PrimitiveTypes.Contains(e.TypeRef)) x.Add(new XElement("type", new XAttribute("href", PrimitivesHref + e.TypeRef)));
            if (e.Kind == ElementKind.Property && t?.Kind is ElementKind.Block or ElementKind.Class or ElementKind.Component) x.Add(new XAttribute("aggregation", "composite"));
            AddMultiplicity(x, e.Id, e.Multiplicity);
            if (e.Get(FieldKeys.DefaultValue) is { Length: > 0 } dv)
                x.Add(new XElement("defaultValue", new XAttribute(Xmi + "type", "uml:OpaqueExpression"), new XAttribute(Xmi + "id", e.Id + "_dv"), new XElement("body", dv)));
        }
        if (e.Kind == ElementKind.Operation) EmitParameters(m, x, e);
        if (e.Kind == ElementKind.Action && e.Get(FieldKeys.Body) is { Length: > 0 } body) x.Add(new XElement("body", body));
        if (e.Kind == ElementKind.ConstraintBlock && e.Get(FieldKeys.Body) is { Length: > 0 } cb)
            x.Add(new XElement("ownedRule", Type("Constraint"), new XAttribute(Xmi + "id", e.Id + "_c"),
                new XElement("specification", Type("OpaqueExpression"), new XAttribute(Xmi + "id", e.Id + "_cs"), new XElement("body", cb))));

        // Stereotype applications
        switch (e.Kind)
        {
            case ElementKind.Block: st.Add(new XElement(SysML + "Block", new XAttribute(Xmi + "id", e.Id + "_st"), new XAttribute("base_Class", e.Id))); break;
            case ElementKind.InterfaceBlock: st.Add(new XElement(SysML + "InterfaceBlock", new XAttribute(Xmi + "id", e.Id + "_st"), new XAttribute("base_Class", e.Id))); break;
            case ElementKind.ConstraintBlock: st.Add(new XElement(SysML + "ConstraintBlock", new XAttribute(Xmi + "id", e.Id + "_st"), new XAttribute("base_Class", e.Id))); break;
            case ElementKind.ValueType: st.Add(new XElement(SysML + "ValueType", new XAttribute(Xmi + "id", e.Id + "_st"), new XAttribute("base_DataType", e.Id))); break;
            case ElementKind.TestCase: st.Add(new XElement(SysML + "TestCase", new XAttribute(Xmi + "id", e.Id + "_st"), new XAttribute("base_Behavior", e.Id))); break;
            case ElementKind.Requirement:
                st.Add(new XElement(SysML + "Requirement", new XAttribute(Xmi + "id", e.Id + "_st"), new XAttribute("base_Class", e.Id),
                    new XAttribute("id", e.Get(FieldKeys.RequirementId)), new XAttribute("text", e.Get(FieldKeys.Text))));
                break;
        }

        // Children. State machine vertices and transitions go in a region.
        var container = x;
        if (e.Kind is ElementKind.StateMachine or ElementKind.State && e.OwnedElements.Any(c => c.Kind is not ElementKind.Diagram and not ElementKind.Comment))
        {
            container = new XElement("region", Type("Region"), new XAttribute(Xmi + "id", e.Id + "_region"));
            x.Add(container);
        }
        foreach (var c in e.OwnedElements) Emit(m, container, c, st, ext);
        foreach (var gr in m.Outgoing(e).Where(x => x.Kind is ElementKind.Generalization or ElementKind.Include or ElementKind.Extend))
            EmitRelationship(x, gr, st);
        parent.Add(x);
    }

    private static void EmitParameters(ModelRepository m, XElement op, Element e)
    {
        var sig = e.Get(FieldKeys.Signature);
        if (string.IsNullOrWhiteSpace(sig)) return;
        var open = sig.IndexOf('('); var close = sig.LastIndexOf(')');
        if (open < 0 || close < open) return;
        var i = 0;
        foreach (var p in sig[(open + 1)..close].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = p.Split(':', 2, StringSplitOptions.TrimEntries);
            var px = new XElement("ownedParameter", Type("Parameter"), new XAttribute(Xmi + "id", $"{e.Id}_p{i++}"), new XAttribute("name", parts[0]));
            if (parts.Length == 2) AddTypeRef(m, px, parts[1]);
            op.Add(px);
        }
        var rest = sig[(close + 1)..].Trim();
        if (rest.StartsWith(':'))
        {
            var rx = new XElement("ownedParameter", Type("Parameter"), new XAttribute(Xmi + "id", $"{e.Id}_ret"), new XAttribute("direction", "return"));
            AddTypeRef(m, rx, rest[1..].Trim());
            op.Add(rx);
        }
    }

    private static void AddTypeRef(ModelRepository m, XElement x, string typeName)
    {
        if (Metamodel.PrimitiveTypes.Contains(typeName)) { x.Add(new XElement("type", new XAttribute("href", PrimitivesHref + typeName))); return; }
        var t = m.AllElements.FirstOrDefault(c => Metamodel.IsClassifier(c.Kind) && c.Name == typeName);
        if (t != null) x.Add(new XAttribute("type", t.Id));
    }

    private static void AddMultiplicity(XElement x, string id, string mult)
    {
        if (string.IsNullOrEmpty(mult)) return;
        var (lo, hi) = ParseMultiplicity(mult);
        x.Add(new XElement("lowerValue", Type("LiteralInteger"), new XAttribute(Xmi + "id", id + "_lo"), new XAttribute("value", lo)));
        x.Add(new XElement("upperValue", Type("LiteralUnlimitedNatural"), new XAttribute(Xmi + "id", id + "_hi"), new XAttribute("value", hi)));
    }

    public static (string lo, string hi) ParseMultiplicity(string m)
    {
        var p = m.Split("..", StringSplitOptions.TrimEntries);
        if (p.Length == 2) return (p[0] == "*" ? "0" : p[0], p[1]);
        return p[0] == "*" ? ("0", "*") : (p[0], p[0]);
    }

    public static string FormatMultiplicity(string? lo, string? hi)
    {
        lo = string.IsNullOrEmpty(lo) ? "1" : lo;
        hi = string.IsNullOrEmpty(hi) ? "1" : hi == "-1" ? "*" : hi;
        if (lo == hi) return lo;
        if (lo == "0" && hi == "*") return "*";
        return $"{lo}..{hi}";
    }

    private static void EmitRelationship(XElement parent, Relationship r, List<XElement> st)
    {
        XElement x;
        switch (r.Kind)
        {
            case ElementKind.Generalization:
                x = new XElement("generalization", Type("Generalization"), Id(r), new XAttribute("general", r.Target.Id));
                break;
            case ElementKind.Include:
                x = new XElement("include", Type("Include"), Id(r), new XAttribute("addition", r.Target.Id));
                break;
            case ElementKind.Extend:
                x = new XElement("extend", Type("Extend"), Id(r), new XAttribute("extendedCase", r.Target.Id));
                break;
            case ElementKind.Transition:
                x = new XElement("transition", Type("Transition"), Id(r), new XAttribute("source", r.Source.Id), new XAttribute("target", r.Target.Id));
                if (r.Get(FieldKeys.Guard) is { Length: > 0 } g)
                    x.Add(new XElement("guard", Type("Constraint"), new XAttribute(Xmi + "id", r.Id + "_g"),
                        new XElement("specification", Type("OpaqueExpression"), new XAttribute(Xmi + "id", r.Id + "_gs"), new XElement("body", g))));
                if (r.Get(FieldKeys.Effect) is { Length: > 0 } fx)
                    x.Add(new XElement("effect", Type("OpaqueBehavior"), new XAttribute(Xmi + "id", r.Id + "_fx"), new XElement("body", fx)));
                break;
            case ElementKind.ControlFlow or ElementKind.ObjectFlow:
                x = new XElement("edge", Type(r.Kind.ToString()), Id(r), new XAttribute("source", r.Source.Id), new XAttribute("target", r.Target.Id));
                if (r.Get(FieldKeys.Guard) is { Length: > 0 } fg)
                    x.Add(new XElement("guard", Type("OpaqueExpression"), new XAttribute(Xmi + "id", r.Id + "_g"), new XElement("body", fg)));
                break;
            case ElementKind.Message:
                x = new XElement("message", Type("Message"), Id(r), new XAttribute(Meridian + "source", r.Source.Id), new XAttribute(Meridian + "target", r.Target.Id));
                if (r.Get(FieldKeys.MessageSort) is { Length: > 0 } sort) x.Add(new XAttribute("messageSort", sort));
                if (r.Get(FieldKeys.Signature) is { Length: > 0 } msig) x.Add(new XAttribute(Meridian + "signature", msig));
                break;
            case ElementKind.Binding:
                x = new XElement("ownedConnector", Type("Connector"), Id(r), new XAttribute(Meridian + "kind", "Binding"),
                    new XElement("end", Type("ConnectorEnd"), new XAttribute(Xmi + "id", r.Id + "_e1"), new XAttribute("role", r.Source.Id)),
                    new XElement("end", Type("ConnectorEnd"), new XAttribute(Xmi + "id", r.Id + "_e2"), new XAttribute("role", r.Target.Id)));
                st.Add(new XElement(SysML + "BindingConnector", new XAttribute(Xmi + "id", r.Id + "_st"), new XAttribute("base_Connector", r.Id)));
                break;
            case ElementKind.Connector:
                x = new XElement("ownedConnector", Type("Connector"), Id(r),
                    new XElement("end", Type("ConnectorEnd"), new XAttribute(Xmi + "id", r.Id + "_e1"), new XAttribute("role", r.Source.Id)),
                    new XElement("end", Type("ConnectorEnd"), new XAttribute(Xmi + "id", r.Id + "_e2"), new XAttribute("role", r.Target.Id)));
                break;
            case ElementKind.Association or ElementKind.Composition or ElementKind.Aggregation:
            {
                var srcEnd = r.Id + "_src";
                var tgtEnd = r.Id + "_tgt";
                x = new XElement("packagedElement", Type("Association"), Id(r), new XAttribute("memberEnd", $"{tgtEnd} {srcEnd}"), new XAttribute("navigableOwnedEnd", tgtEnd));
                var te = new XElement("ownedEnd", Type("Property"), new XAttribute(Xmi + "id", tgtEnd), new XAttribute("type", r.Target.Id), new XAttribute("association", r.Id));
                if (r.Kind != ElementKind.Association) te.Add(new XAttribute("aggregation", r.Kind == ElementKind.Composition ? "composite" : "shared"));
                AddMultiplicity(te, tgtEnd, string.IsNullOrEmpty(r.Multiplicity) ? "1" : r.Multiplicity);
                x.Add(te);
                x.Add(new XElement("ownedEnd", Type("Property"), new XAttribute(Xmi + "id", srcEnd), new XAttribute("type", r.Source.Id), new XAttribute("association", r.Id)));
                break;
            }
            default:
            {
                var umlType = r.Kind switch
                {
                    ElementKind.Dependency => "Dependency",
                    ElementKind.Realization => "Realization",
                    _ => "Abstraction",
                };
                x = new XElement("packagedElement", Type(umlType), Id(r), new XAttribute("client", r.Source.Id), new XAttribute("supplier", r.Target.Id));
                if (NeedsKindMarker(r.Kind)) x.Add(new XAttribute(Meridian + "kind", r.Kind.ToString()));
                var stereo = r.Kind switch
                {
                    ElementKind.Satisfy or ElementKind.Verify or ElementKind.Refine or ElementKind.Trace or ElementKind.DeriveReqt or ElementKind.Copy or ElementKind.Allocate => r.Kind.ToString(),
                    _ => null,
                };
                if (stereo != null) st.Add(new XElement(SysML + stereo, new XAttribute(Xmi + "id", r.Id + "_st"), new XAttribute("base_Abstraction", r.Id)));
                break;
            }
        }
        if (!string.IsNullOrEmpty(r.Name)) x.Add(new XAttribute("name", r.Name));
        parent.Add(x);
    }

    private static void AddComments(XElement x, Element e)
    {
        if (string.IsNullOrWhiteSpace(e.Documentation)) return;
        x.Add(new XElement("ownedComment", Type("Comment"), new XAttribute(Xmi + "id", e.Id + "_doc"),
            new XAttribute("annotatedElement", e.Id), new XElement("body", e.Documentation)));
    }

    private static void AddComment(XElement parent, Element c) =>
        parent.Add(new XElement("ownedComment", Type("Comment"), Id(c), new XElement("body", c.Get(FieldKeys.Body))));

    // ======================================================================= import

    public static (ModelRepository Model, ImportReport Report) Import(string path) => Import(XDocument.Load(path));

    public static (ModelRepository Model, ImportReport Report) Import(XDocument doc)
    {
        var warnings = new List<string>();
        var root = doc.Root ?? throw new FormatException("Empty document");

        var umlModel = root.Name.LocalName is "Model" or "Package" ? root
            : root.Elements().FirstOrDefault(x => x.Name.LocalName is "Model" or "Package")
              ?? throw new FormatException("No uml:Model found");

        // Stereotype applications are siblings of the model (any profile prefix: sysml, SysML1.4, MD_Customization…).
        var applied = new Dictionary<string, List<XElement>>(StringComparer.Ordinal);
        foreach (var s in root.Elements().Where(x => !ReferenceEquals(x, umlModel)))
            foreach (var a in s.Attributes().Where(a => a.Name.LocalName.StartsWith("base_", StringComparison.Ordinal)))
            {
                if (!applied.TryGetValue(a.Value, out var list)) applied[a.Value] = list = new();
                list.Add(s);
            }
        string? Stereo(string id, params string[] names) =>
            applied.TryGetValue(id, out var l) ? l.Select(x => x.Name.LocalName).FirstOrDefault(names.Contains) : null;
        XElement? StereoEl(string id, string name) => applied.TryGetValue(id, out var l) ? l.FirstOrDefault(x => x.Name.LocalName == name) : null;

        var model = new ModelRepository(Attr(umlModel, "name") ?? "Imported Model");
        var map = new Dictionary<string, Element>(StringComparer.Ordinal) { [XmiId(umlModel) ?? model.Root.Id] = model.Root };
        var pending = new List<(ElementKind kind, string? id, string? src, string? tgt, Element owner, XElement x)>();
        var typeRefs = new List<(Element feature, XElement x)>();
        var count = 0;

        void Walk(XElement x, Element owner)
        {
            foreach (var c in x.Elements())
            {
                var tag = c.Name.LocalName;
                var type = XmiType(c);
                var id = XmiId(c);

                if (tag == "ownedComment")
                {
                    var body = c.Element("body")?.Value ?? Attr(c, "body") ?? "";
                    var annotated = Attr(c, "annotatedElement");
                    if (annotated != null && annotated == XmiId(x) || annotated == null && x != umlModel && !string.IsNullOrEmpty(body) && owner.Kind != ElementKind.Package && owner.Kind != ElementKind.Model)
                        ModelRepository.SetFieldForLoad(owner, FieldKeys.Documentation, body);
                    else if (Metamodel.CanOwn(owner.Kind, ElementKind.Comment))
                    {
                        var cm = new Element(ElementKind.Comment, id);
                        ModelRepository.SetFieldForLoad(cm, FieldKeys.Body, body);
                        model.AttachForLoad(owner, cm);
                    }
                    continue;
                }
                if (tag == "region") { Walk(c, owner); continue; }
                if (tag is "generalization") { pending.Add((ElementKind.Generalization, id, XmiId(x), Attr(c, "general") ?? Href(c, "general"), owner, c)); continue; }
                if (tag is "interfaceRealization") { pending.Add((ElementKind.Realization, id, XmiId(x), Attr(c, "contract") ?? Attr(c, "supplier"), owner, c)); continue; }
                if (tag is "include" || type == "Include") { pending.Add((ElementKind.Include, id, Attr(c, "includingCase") ?? XmiId(x), Attr(c, "addition"), owner, c)); continue; }
                if (tag is "extend" || type == "Extend") { pending.Add((ElementKind.Extend, id, Attr(c, "extension") ?? XmiId(x), Attr(c, "extendedCase"), owner, c)); continue; }
                if (tag == "transition" || type == "Transition") { pending.Add((ElementKind.Transition, id, Attr(c, "source"), Attr(c, "target"), owner, c)); continue; }
                if (tag == "message" || type == "Message")
                {
                    pending.Add((ElementKind.Message, id, c.Attribute(Meridian + "source")?.Value ?? Attr(c, "sendEvent"), c.Attribute(Meridian + "target")?.Value ?? Attr(c, "receiveEvent"), owner, c));
                    continue;
                }
                if (tag == "edge" || type is "ControlFlow" or "ObjectFlow") { pending.Add((type == "ObjectFlow" ? ElementKind.ObjectFlow : ElementKind.ControlFlow, id, Attr(c, "source"), Attr(c, "target"), owner, c)); continue; }
                if (tag == "ownedConnector" || type == "Connector")
                {
                    var ends = c.Elements().Where(e => e.Name.LocalName == "end").Select(e => Attr(e, "role")).ToList();
                    var binding = c.Attribute(Meridian + "kind")?.Value == "Binding" || (id != null && Stereo(id, "BindingConnector") != null);
                    if (ends.Count == 2) pending.Add((binding ? ElementKind.Binding : ElementKind.Connector, id, ends[0], ends[1], owner, c));
                    continue;
                }
                if (type == "Association") { pending.Add((ElementKind.Association, id, null, null, owner, c)); continue; }
                if (type is "Dependency" or "Abstraction" or "Realization" or "Usage" or "Refine" or "Trace")
                {
                    var kind = id != null ? Stereo(id, "Satisfy", "Verify", "Refine", "Trace", "DeriveReqt", "Copy", "Allocate") : null;
                    var k = c.Attribute(Meridian + "kind")?.Value is { } mk && Enum.TryParse<ElementKind>(mk, out var marked) ? marked
                        : kind != null ? Enum.Parse<ElementKind>(kind)
                        : type == "Realization" ? ElementKind.Realization : type == "Refine" ? ElementKind.Refine : type == "Trace" ? ElementKind.Trace : ElementKind.Dependency;
                    pending.Add((k, id, Attr(c, "client") ?? c.Element("client")?.Attribute(Xmi + "idref")?.Value,
                                     Attr(c, "supplier") ?? c.Element("supplier")?.Attribute(Xmi + "idref")?.Value, owner, c));
                    continue;
                }

                var ek = c.Attribute(Meridian + "kind")?.Value is { } mkind && Enum.TryParse<ElementKind>(mkind, out var markedKind)
                    ? markedKind
                    : MapKind(tag, type, c, id is null ? null : (Func<string[], string?>)(n => Stereo(id, n)));
                if (ek == null)
                {
                    if (type != null && tag is "packagedElement" or "nestedClassifier" or "ownedBehavior" or "ownedAttribute" or "node" or "subvertex")
                        warnings.Add($"Skipped unsupported {type} '{Attr(c, "name")}'");
                    continue;
                }
                var parentForChild = Metamodel.CanOwn(owner.Kind, ek.Value) ? owner : model.DefaultOwnerFor(owner, ek.Value);
                if (!Metamodel.CanOwn(parentForChild.Kind, ek.Value)) { warnings.Add($"Could not place {ek} '{Attr(c, "name")}'"); continue; }

                var el = new Element(ek.Value, id is not null && !map.ContainsKey(id) ? id : null);
                ModelRepository.SetFieldForLoad(el, FieldKeys.Name, Attr(c, "name"));
                ModelRepository.SetFieldForLoad(el, FieldKeys.Visibility, Attr(c, "visibility") is "public" ? null : Attr(c, "visibility"));
                if (Attr(c, "isAbstract") == "true") ModelRepository.SetFieldForLoad(el, FieldKeys.IsAbstract, "true");
                ModelRepository.SetFieldForLoad(el, FieldKeys.Direction, c.Attribute(Meridian + "direction")?.Value);
                if (Metamodel.IsTypedFeature(ek.Value))
                {
                    var lo = c.Element("lowerValue"); var hi = c.Element("upperValue");
                    ModelRepository.SetFieldForLoad(el, FieldKeys.Multiplicity,
                        lo == null && hi == null ? (ek is ElementKind.Property or ElementKind.Port ? "1" : null) : FormatMultiplicity(lo == null ? "1" : Attr(lo, "value") ?? "0", hi == null ? "1" : Attr(hi, "value") ?? "0"));
                    ModelRepository.SetFieldForLoad(el, FieldKeys.DefaultValue, c.Element("defaultValue")?.Element("body")?.Value ?? (c.Element("defaultValue") is { } d ? Attr(d, "value") : null));
                    typeRefs.Add((el, c));
                }
                if (ek == ElementKind.Operation) ModelRepository.SetFieldForLoad(el, FieldKeys.Signature, ReadSignature(c, map));
                if (ek == ElementKind.Action) ModelRepository.SetFieldForLoad(el, FieldKeys.Body, c.Element("body")?.Value);
                if (ek == ElementKind.CombinedFragment)
                {
                    ModelRepository.SetFieldForLoad(el, FieldKeys.Operator, Attr(c, "interactionOperator") ?? "alt");
                    ModelRepository.SetFieldForLoad(el, FieldKeys.Guard, c.Attribute(Meridian + "guard")?.Value
                        ?? c.Descendants().FirstOrDefault(d => d.Name.LocalName == "guard")?.Descendants().FirstOrDefault(d => d.Name.LocalName == "body")?.Value);
                }
                if (ek == ElementKind.ConstraintBlock) ModelRepository.SetFieldForLoad(el, FieldKeys.Body, c.Element("ownedRule")?.Element("specification")?.Element("body")?.Value);
                if (ek == ElementKind.Requirement && id != null && StereoEl(id, "Requirement") is { } rq)
                {
                    ModelRepository.SetFieldForLoad(el, FieldKeys.RequirementId, Attr(rq, "id") ?? rq.Element("id")?.Value);
                    ModelRepository.SetFieldForLoad(el, FieldKeys.Text, Attr(rq, "text") ?? rq.Element("text")?.Value);
                }
                model.AttachForLoad(parentForChild, el);
                if (id != null) map[id] = el;
                count++;
                if (ek is not (ElementKind.Property or ElementKind.Port or ElementKind.Operation)) Walk(c, el);
                else foreach (var cm in c.Elements().Where(z => z.Name.LocalName == "ownedComment"))
                        ModelRepository.SetFieldForLoad(el, FieldKeys.Documentation, cm.Element("body")?.Value ?? Attr(cm, "body"));
            }
        }

        Walk(umlModel, model.Root);

        // Types
        foreach (var (f, x) in typeRefs)
        {
            var t = Attr(x, "type");
            var href = x.Element("type")?.Attribute("href")?.Value;
            if (t != null && map.TryGetValue(t, out var te)) ModelRepository.SetFieldForLoad(f, FieldKeys.Type, te.Id);
            else if (href != null) ModelRepository.SetFieldForLoad(f, FieldKeys.Type, PrimitiveFromHref(href));
            else if (t != null) ModelRepository.SetFieldForLoad(f, FieldKeys.Type, t);
        }

        // Relationships
        var rels = 0;
        Dictionary<string, XElement>? byXmiId = null;
        foreach (var (kind0, id, src0, tgt0, owner, x) in pending)
        {
            var kind = kind0; var src = src0; var tgt = tgt0;
            string? mult = null;
            if (kind == ElementKind.Association)
            {
                var memberEnds = (Attr(x, "memberEnd") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                memberEnds.AddRange(x.Elements().Where(e => e.Name.LocalName == "memberEnd").Select(e => e.Attribute(Xmi + "idref")?.Value).OfType<string>());
                // Index every xmi:id once (first occurrence in document order wins, as a linear search would find).
                if (byXmiId == null)
                {
                    byXmiId = new Dictionary<string, XElement>(StringComparer.Ordinal);
                    foreach (var d in doc.Descendants())
                        if (XmiId(d) is { } xid) byXmiId.TryAdd(xid, d);
                }
                var ends = memberEnds.Select(me => byXmiId.GetValueOrDefault(me)).OfType<XElement>().ToList();
                if (ends.Count != 2) { warnings.Add($"Association {id} skipped: needs two ends"); continue; }
                var whole = ends.FindIndex(e => Attr(e, "aggregation") is "composite" or "shared");
                var partEnd = whole >= 0 ? ends[whole] : ends[0];
                var otherEnd = whole >= 0 ? ends[1 - whole] : ends[1];
                kind = whole < 0 ? ElementKind.Association : Attr(partEnd, "aggregation") == "composite" ? ElementKind.Composition : ElementKind.Aggregation;
                // An end owned by a class (not by the association) is that class's attribute: the class is the source.
                src = Attr(otherEnd, "type");
                tgt = Attr(partEnd, "type");
                if (whole < 0 && ends.Any(e => e.Parent != x))
                {
                    var classOwned = ends.First(e => e.Parent != x);
                    tgt = Attr(classOwned, "type");
                    src = XmiId(classOwned.Parent!) ?? src;
                }
                mult = FormatMultiplicity(partEnd.Element("lowerValue") is { } l ? Attr(l, "value") ?? "0" : "1",
                                          partEnd.Element("upperValue") is { } u ? Attr(u, "value") ?? "0" : "1");
            }
            if (src == null || tgt == null || !map.TryGetValue(src, out var s) || !map.TryGetValue(tgt, out var t))
            {
                warnings.Add($"{kind} {id} skipped: unresolved end"); continue;
            }
            if (Metamodel.CheckEnds(kind, s.Kind, t.Kind) is { } problem)
            {
                if (kind is ElementKind.Satisfy or ElementKind.Verify or ElementKind.DeriveReqt or ElementKind.Copy) { warnings.Add($"{kind} {id}: {problem}; imported as Trace"); kind = ElementKind.Trace; }
                else if (kind is ElementKind.Composition or ElementKind.Aggregation or ElementKind.Association && !(Metamodel.IsClassifier(s.Kind) && Metamodel.IsClassifier(t.Kind))) { warnings.Add($"{kind} {id}: {problem}; skipped"); continue; }
                else if (kind is not (ElementKind.Dependency or ElementKind.Trace)) { warnings.Add($"{kind} {id}: {problem}; imported as Dependency"); kind = ElementKind.Dependency; }
            }
            var r = new Relationship(kind, s, t, id != null && !map.ContainsKey(id) ? id : null);
            ModelRepository.SetFieldForLoad(r, FieldKeys.Name, Attr(x, "name"));
            ModelRepository.SetFieldForLoad(r, FieldKeys.Multiplicity, mult);
            var guard = x.Element("guard");
            ModelRepository.SetFieldForLoad(r, FieldKeys.Guard, guard?.Element("specification")?.Element("body")?.Value ?? guard?.Element("body")?.Value);
            ModelRepository.SetFieldForLoad(r, FieldKeys.Effect, x.Element("effect")?.Element("body")?.Value);
            if (kind == ElementKind.Message)
            {
                ModelRepository.SetFieldForLoad(r, FieldKeys.MessageSort, Attr(x, "messageSort"));
                ModelRepository.SetFieldForLoad(r, FieldKeys.Signature, x.Attribute(Meridian + "signature")?.Value);
            }
            // Flows live with their nodes; relationships UML nests in the source classifier go to its namespace (as Connect() does).
            var relOwner = kind is ElementKind.Transition or ElementKind.ControlFlow or ElementKind.ObjectFlow or ElementKind.Connector or ElementKind.Message ? (s.Owner ?? owner)
                : x.Name.LocalName is "generalization" or "include" or "extend" or "interfaceRealization" ? model.DefaultOwnerFor(s.Owner ?? owner, kind)
                : owner;
            if (!Metamodel.CanOwn(relOwner.Kind, kind)) relOwner = model.DefaultOwnerFor(relOwner, kind);
            model.AttachForLoad(relOwner, r);
            if (id != null) map[id] = r;
            rels++;
        }

        // Meridian extension data (custom stereotypes, tags)
        foreach (var f in root.Descendants(Meridian + "field"))
            if (map.TryGetValue(f.Attribute("ref")?.Value ?? "", out var e))
                ModelRepository.SetFieldForLoad(e, f.Attribute("key")!.Value, f.Attribute("value")?.Value);

        ImportDiagrams(root, model, map, warnings);
        model.History.Clear();
        return (model, new ImportReport(count, rels, warnings));
    }

    private static ElementKind? MapKind(string tag, string? type, XElement x, Func<string[], string?>? stereo)
    {
        string? S(params string[] n) => stereo?.Invoke(n);
        return type switch
        {
            "Package" or "Model" => ElementKind.Package,
            "Profile" => ElementKind.Profile,
            "Stereotype" => ElementKind.Stereotype,
            "ActivityPartition" => ElementKind.Partition,
            "InputPin" => ElementKind.InputPin,
            "OutputPin" => ElementKind.OutputPin,
            "CentralBufferNode" or "DataStoreNode" => ElementKind.ObjectNode,
            "Class" => S("Requirement", "AbstractRequirement", "functionalRequirement", "performanceRequirement", "interfaceRequirement", "physicalRequirement", "designConstraint") != null ? ElementKind.Requirement
                     : S("InterfaceBlock") != null ? ElementKind.InterfaceBlock
                     : S("ConstraintBlock") != null ? ElementKind.ConstraintBlock
                     : S("Block") != null ? ElementKind.Block
                     : ElementKind.Class,
            "Interface" => ElementKind.Interface,
            "DataType" or "PrimitiveType" => S("ValueType") != null ? ElementKind.ValueType : ElementKind.DataType,
            "Enumeration" => ElementKind.Enumeration,
            "EnumerationLiteral" => ElementKind.EnumerationLiteral,
            "Component" => ElementKind.Component,
            "Actor" => ElementKind.Actor,
            "UseCase" => ElementKind.UseCase,
            "Activity" => S("TestCase") != null ? ElementKind.TestCase : ElementKind.Activity,
            "StateMachine" => ElementKind.StateMachine,
            "Interaction" => ElementKind.Interaction,
            "Lifeline" => ElementKind.Lifeline,
            "CombinedFragment" => ElementKind.CombinedFragment,
            "Property" => ElementKind.Property,
            "Port" => ElementKind.Port,
            "Operation" => ElementKind.Operation,
            "OpaqueAction" or "CallBehaviorAction" or "CallOperationAction" or "SendSignalAction" or "AcceptEventAction" => ElementKind.Action,
            "InitialNode" => ElementKind.InitialNode,
            "ActivityFinalNode" or "FlowFinalNode" => ElementKind.ActivityFinalNode,
            "DecisionNode" or "MergeNode" => ElementKind.DecisionNode,
            "ForkNode" or "JoinNode" => ElementKind.ForkNode,
            "State" => ElementKind.State,
            "FinalState" => ElementKind.FinalState,
            "Pseudostate" => (Attr(x, "kind") ?? "initial") == "initial" ? ElementKind.InitialState : null,
            _ => null,
        };
    }

    private static string? ReadSignature(XElement op, Dictionary<string, Element> map)
    {
        var ps = op.Elements().Where(e => e.Name.LocalName == "ownedParameter").ToList();
        if (ps.Count == 0) return null;
        string TypeName(XElement p)
        {
            var href = p.Element("type")?.Attribute("href")?.Value;
            if (href != null) return PrimitiveFromHref(href);
            var t = Attr(p, "type");
            return t != null && map.TryGetValue(t, out var e) ? e.Name : t ?? "";
        }
        var ins = ps.Where(p => Attr(p, "direction") != "return")
            .Select(p => { var tn = TypeName(p); return string.IsNullOrEmpty(tn) ? Attr(p, "name") ?? "arg" : $"{Attr(p, "name")} : {tn}"; });
        var ret = ps.FirstOrDefault(p => Attr(p, "direction") == "return");
        return $"({string.Join(", ", ins)})" + (ret != null ? $" : {TypeName(ret)}" : "");
    }

    private static string PrimitiveFromHref(string href)
    {
        var name = href[(href.LastIndexOf('#') + 1)..];
        return name switch { "Integer" or "Boolean" or "String" or "Real" or "UnlimitedNatural" => name, _ => CSharpGenerator.Identifier(name) };
    }

    private static string? Attr(XElement x, string name) => x.Attribute(name)?.Value;

    private static string? Href(XElement x, string child) => x.Element(child)?.Attribute("href")?.Value;

    private static string? XmiId(XElement x) =>
        x.Attributes().FirstOrDefault(a => a.Name.LocalName == "id" && a.Name.NamespaceName.Contains("XMI", StringComparison.OrdinalIgnoreCase))?.Value;

    private static string? XmiType(XElement x)
    {
        var t = x.Attributes().FirstOrDefault(a => a.Name.LocalName == "type" && a.Name.NamespaceName.Contains("XMI", StringComparison.OrdinalIgnoreCase))?.Value;
        if (t == null) return x.Name.NamespaceName.Contains("UML", StringComparison.OrdinalIgnoreCase) ? x.Name.LocalName : null;
        var i = t.IndexOf(':');
        return i >= 0 ? t[(i + 1)..] : t;
    }
}
