using System.Globalization;
using System.Xml.Linq;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>BPMN 2.0 XML (with BPMN DI) for one BPMN diagram: export, and import into a package.</summary>
public static class BpmnExchange
{
    private static readonly XNamespace B = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    private static readonly XNamespace Di = "http://www.omg.org/spec/BPMN/20100524/DI";
    private static readonly XNamespace Dc = "http://www.omg.org/spec/DD/20100524/DC";
    private static readonly XNamespace DdDi = "http://www.omg.org/spec/DD/20100524/DI";

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Id(string id) => char.IsLetter(id[0]) || id[0] == '_' ? id : "_" + id;

    private static string Tag(string subtype) => subtype switch
    {
        "startEvent" => "startEvent",
        "endEvent" => "endEvent",
        "intermediateEvent" or "timerEvent" or "messageEvent" => "intermediateCatchEvent",
        "userTask" => "userTask",
        "serviceTask" => "serviceTask",
        "scriptTask" => "scriptTask",
        "subProcess" => "subProcess",
        "exclusiveGateway" => "exclusiveGateway",
        "parallelGateway" => "parallelGateway",
        "inclusiveGateway" => "inclusiveGateway",
        "eventGateway" => "eventBasedGateway",
        "dataObject" => "dataObjectReference",
        "dataStore" => "dataStoreReference",
        _ => "task",
    };

    public static XDocument Export(ModelRepository m, Diagram d)
    {
        var processId = Id("P" + d.Id);
        var process = new XElement(B + "process", new XAttribute("id", processId), new XAttribute("name", d.Name), new XAttribute("isExecutable", "false"));
        var lanes = d.Nodes.Where(n => n.Element.Kind == ElementKind.BpmnLane).ToList();
        if (lanes.Count > 0)
            process.Add(new XElement(B + "laneSet", new XAttribute("id", Id("LS" + d.Id)),
                lanes.Select(l => new XElement(B + "lane", new XAttribute("id", Id(l.Element.Id)), new XAttribute("name", l.Element.Name),
                    d.Nodes.Where(n => n.Element.Kind == ElementKind.BpmnNode && l.Bounds.Contains(n.Bounds.Center)).Select(n => new XElement(B + "flowNodeRef", Id(n.Element.Id)))))));
        foreach (var n in d.Nodes.Where(n => n.Element.Kind == ElementKind.BpmnNode))
        {
            var sub = n.Element.Get(FieldKeys.Subtype);
            var x = new XElement(B + Tag(sub), new XAttribute("id", Id(n.Element.Id)), new XAttribute("name", n.Element.Name));
            if (sub == "timerEvent") x.Add(new XElement(B + "timerEventDefinition", new XAttribute("id", Id("T" + n.Element.Id))));
            if (sub == "messageEvent") x.Add(new XElement(B + "messageEventDefinition", new XAttribute("id", Id("M" + n.Element.Id))));
            if (!string.IsNullOrEmpty(n.Element.Documentation)) x.Add(new XElement(B + "documentation", n.Element.Documentation));
            process.Add(x);
        }
        var flows = d.Edges.Where(e => e.Relationship.Kind == ElementKind.BpmnFlow).ToList();
        foreach (var e in flows.Where(e => e.Relationship.Get(FieldKeys.Subtype) is "" or "sequence"))
        {
            var x = new XElement(B + "sequenceFlow", new XAttribute("id", Id(e.Relationship.Id)), new XAttribute("sourceRef", Id(e.Source.Element.Id)), new XAttribute("targetRef", Id(e.Target.Element.Id)));
            if (!string.IsNullOrEmpty(e.Relationship.Name)) x.Add(new XAttribute("name", e.Relationship.Name));
            process.Add(x);
        }

        var defs = new XElement(B + "definitions",
            new XAttribute(XNamespace.Xmlns + "bpmn", B.NamespaceName), new XAttribute(XNamespace.Xmlns + "bpmndi", Di.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "dc", Dc.NamespaceName), new XAttribute(XNamespace.Xmlns + "di", DdDi.NamespaceName),
            new XAttribute("id", Id("D" + d.Id)), new XAttribute("targetNamespace", "urn:meridian"), new XAttribute("exporter", "Meridian"));

        var pools = d.Nodes.Where(n => n.Element.Kind == ElementKind.BpmnPool).ToList();
        var planeOwner = processId;
        if (pools.Count > 0)
        {
            var collab = new XElement(B + "collaboration", new XAttribute("id", Id("C" + d.Id)),
                pools.Select(p => new XElement(B + "participant", new XAttribute("id", Id(p.Element.Id)), new XAttribute("name", p.Element.Name), new XAttribute("processRef", processId))),
                flows.Where(e => e.Relationship.Get(FieldKeys.Subtype) == "message").Select(e => new XElement(B + "messageFlow", new XAttribute("id", Id(e.Relationship.Id)),
                    new XAttribute("sourceRef", Id(e.Source.Element.Id)), new XAttribute("targetRef", Id(e.Target.Element.Id)))));
            defs.Add(collab);
            planeOwner = Id("C" + d.Id);
        }
        defs.Add(process);

        var plane = new XElement(Di + "BPMNPlane", new XAttribute("id", Id("PL" + d.Id)), new XAttribute("bpmnElement", planeOwner));
        foreach (var n in d.Nodes.Where(n => n.Element.Kind is ElementKind.BpmnNode or ElementKind.BpmnPool or ElementKind.BpmnLane))
            plane.Add(new XElement(Di + "BPMNShape", new XAttribute("id", Id("S" + n.Id)), new XAttribute("bpmnElement", Id(n.Element.Id)),
                n.Element.Kind is ElementKind.BpmnPool or ElementKind.BpmnLane ? new XAttribute("isHorizontal", "true") : null,
                new XElement(Dc + "Bounds", new XAttribute("x", N(n.Bounds.X)), new XAttribute("y", N(n.Bounds.Y)), new XAttribute("width", N(n.Bounds.Width)), new XAttribute("height", N(n.Bounds.Height)))));
        foreach (var e in flows)
        {
            var pts = Notation.RouteIn(d, e);
            plane.Add(new XElement(Di + "BPMNEdge", new XAttribute("id", Id("E" + e.Id)), new XAttribute("bpmnElement", Id(e.Relationship.Id)),
                pts.Select(p => new XElement(DdDi + "waypoint", new XAttribute("x", N(p.X)), new XAttribute("y", N(p.Y))))));
        }
        defs.Add(new XElement(Di + "BPMNDiagram", new XAttribute("id", Id("BD" + d.Id)), new XAttribute("name", d.Name), plane));
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), defs);
    }

    private static string? Subtype(string tag, XElement x) => tag switch
    {
        "startEvent" => "startEvent",
        "endEvent" => "endEvent",
        "intermediateCatchEvent" or "intermediateThrowEvent" or "boundaryEvent" =>
            x.Elements().Any(c => c.Name.LocalName == "timerEventDefinition") ? "timerEvent" : x.Elements().Any(c => c.Name.LocalName == "messageEventDefinition") ? "messageEvent" : "intermediateEvent",
        "task" or "manualTask" or "businessRuleTask" or "sendTask" or "receiveTask" or "callActivity" => "task",
        "userTask" => "userTask",
        "serviceTask" => "serviceTask",
        "scriptTask" => "scriptTask",
        "subProcess" or "transaction" => "subProcess",
        "exclusiveGateway" => "exclusiveGateway",
        "parallelGateway" => "parallelGateway",
        "inclusiveGateway" or "complexGateway" => "inclusiveGateway",
        "eventBasedGateway" => "eventGateway",
        "dataObjectReference" or "dataObject" => "dataObject",
        "dataStoreReference" => "dataStore",
        _ => null,
    };

    /// <summary>Imports processes (flow nodes, sequence/message flows, pools, lanes, layout) as a BPMN diagram in <paramref name="target"/>.</summary>
    public static Diagram Import(ModelRepository m, Element target, XDocument doc, string? name = null)
    {
        var root = doc.Root ?? throw new FormatException("Empty BPMN document");
        if (root.Name.LocalName != "definitions") throw new FormatException("Not a BPMN 2.0 document");
        double D(XElement? e, string a) => double.TryParse(e?.Attribute(a)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        var shapes = root.Descendants().Where(e => e.Name.LocalName == "BPMNShape").ToDictionary(e => (string)e.Attribute("bpmnElement")!, e => e.Elements().First(b => b.Name.LocalName == "Bounds"));
        var made = new Dictionary<string, Element>();
        Diagram diagram;
        using (m.History.Transaction("Import BPMN"))
        {
            diagram = m.CreateDiagram(target, DiagramType.Bpmn, name ?? (string?)root.Descendants().FirstOrDefault(e => e.Name.LocalName == "process")?.Attribute("name") ?? "Imported process");
            void Place(Element e, string id)
            {
                var (w, h) = Notation.DefaultSizeOf(e);
                var b = shapes.TryGetValue(id, out var sb) ? new Bounds(D(sb, "x"), D(sb, "y"), D(sb, "width"), D(sb, "height")) : new Bounds(40 + made.Count * 30, 40 + made.Count * 20, w, h);
                m.AddNode(diagram, e, b, showRelationships: false);
            }
            foreach (var p in root.Descendants().Where(e => e.Name.LocalName == "participant"))
            {
                var e = m.Create(target, ElementKind.BpmnPool, (string?)p.Attribute("name") ?? "Pool");
                made[(string)p.Attribute("id")!] = e;
                Place(e, (string)p.Attribute("id")!);
            }
            foreach (var l in root.Descendants().Where(e => e.Name.LocalName == "lane"))
            {
                var e = m.Create(target, ElementKind.BpmnLane, (string?)l.Attribute("name") ?? "Lane");
                made[(string)l.Attribute("id")!] = e;
                Place(e, (string)l.Attribute("id")!);
            }
            foreach (var proc in root.Descendants().Where(e => e.Name.LocalName is "process" or "subProcess"))
                foreach (var x in proc.Elements())
                {
                    var sub = Subtype(x.Name.LocalName, x);
                    if (sub is null || x.Attribute("id") is null) continue;
                    var nm = (string?)x.Attribute("name");
                    var e = m.Create(target, ElementKind.BpmnNode, string.IsNullOrEmpty(nm) && Subtypes.BpmnShape(sub) is NodeShape.Event or NodeShape.Gateway ? "" : nm ?? Subtypes.Display(sub));
                    m.Set(e, FieldKeys.Subtype, sub);
                    m.Set(e, FieldKeys.Documentation, x.Elements().FirstOrDefault(c => c.Name.LocalName == "documentation")?.Value);
                    made[(string)x.Attribute("id")!] = e;
                    Place(e, (string)x.Attribute("id")!);
                }
            foreach (var f in root.Descendants().Where(e => e.Name.LocalName is "sequenceFlow" or "messageFlow"))
            {
                if (!made.TryGetValue((string?)f.Attribute("sourceRef") ?? "", out var s) || !made.TryGetValue((string?)f.Attribute("targetRef") ?? "", out var t)) continue;
                var r = m.Connect(ElementKind.BpmnFlow, s, t, target);
                m.Set(r, FieldKeys.Subtype, f.Name.LocalName == "messageFlow" ? "message" : "sequence");
                m.Set(r, FieldKeys.Name, (string?)f.Attribute("name"));
                m.AddEdge(diagram, r);
            }
        }
        return diagram;
    }
}
