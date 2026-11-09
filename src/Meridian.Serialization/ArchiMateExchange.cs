using System.Globalization;
using System.Xml.Linq;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>The Open Group ArchiMate Model Exchange File Format (3.x): elements, relationships and views.</summary>
public static class ArchiMateExchange
{
    private static readonly XNamespace A = "http://www.opengroup.org/xsd/archimate/3.0/";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private static string Pascal(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
    private static string Camel(string s) => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s[1..];
    private static string Id(string id) => char.IsLetter(id[0]) ? id : "id-" + id.TrimStart('_');
    private static string N(double v) => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);

    public static XDocument Export(ModelRepository m)
    {
        var elements = m.OfKind(ElementKind.ArchiElement).ToList();
        var relations = m.Relationships.Where(r => r.Kind == ElementKind.ArchiRelation).ToList();
        XElement Name(string n) => new(A + "name", new XAttribute(XNamespace.Xml + "lang", "en"), n);

        var views = m.Diagrams.Where(d => d.DiagramType == DiagramType.ArchiMate).Select(d =>
            new XElement(A + "view", new XAttribute("identifier", Id(d.Id)), new XAttribute(Xsi + "type", "Diagram"), Name(d.Name),
                d.Nodes.Where(n => n.Element.Kind == ElementKind.ArchiElement).Select(n => new XElement(A + "node", new XAttribute("identifier", Id(n.Id)),
                    new XAttribute("elementRef", Id(n.Element.Id)), new XAttribute(Xsi + "type", "Element"),
                    new XAttribute("x", N(n.Bounds.X)), new XAttribute("y", N(n.Bounds.Y)), new XAttribute("w", N(n.Bounds.Width)), new XAttribute("h", N(n.Bounds.Height)))),
                d.Edges.Where(e => e.Relationship.Kind == ElementKind.ArchiRelation).Select(e => new XElement(A + "connection", new XAttribute("identifier", Id(e.Id)),
                    new XAttribute("relationshipRef", Id(e.Relationship.Id)), new XAttribute(Xsi + "type", "Relationship"),
                    new XAttribute("source", Id(e.Source.Id)), new XAttribute("target", Id(e.Target.Id)))))).ToList();

        var model = new XElement(A + "model",
            new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName), new XAttribute("identifier", Id(m.Root.Id)), Name(m.Root.Name),
            new XElement(A + "elements", elements.Select(e => new XElement(A + "element", new XAttribute("identifier", Id(e.Id)),
                new XAttribute(Xsi + "type", Pascal(e.Get(FieldKeys.Subtype) is { Length: > 0 } s ? s : "businessObject")), Name(e.Name),
                string.IsNullOrEmpty(e.Documentation) ? null : new XElement(A + "documentation", e.Documentation)))),
            new XElement(A + "relationships", relations.Select(r => new XElement(A + "relationship", new XAttribute("identifier", Id(r.Id)),
                new XAttribute("source", Id(r.Source.Id)), new XAttribute("target", Id(r.Target.Id)),
                new XAttribute(Xsi + "type", Pascal(r.Get(FieldKeys.Subtype) is { Length: > 0 } s ? s : "association")),
                string.IsNullOrEmpty(r.Name) ? null : Name(r.Name)))));
        if (views.Count > 0) model.Add(new XElement(A + "views", new XElement(A + "diagrams", views)));
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), model);
    }

    /// <summary>Imports elements, relationships and views into <paramref name="target"/>. Returns (elements, relationships, views).</summary>
    public static (int Elements, int Relationships, int Views) Import(ModelRepository m, Element target, XDocument doc)
    {
        var root = doc.Root ?? throw new FormatException("Empty document");
        if (root.Name.LocalName != "model") throw new FormatException("Not an ArchiMate exchange file");
        XNamespace xsi = Xsi;
        string Type(XElement e) => (string?)e.Attribute(xsi + "type") ?? "";
        string NameOf(XElement e) => e.Elements().FirstOrDefault(c => c.Name.LocalName == "name")?.Value ?? "";
        var made = new Dictionary<string, Element>();
        var rels = new Dictionary<string, Relationship>();
        int views = 0;
        using (m.History.Transaction("Import ArchiMate"))
        {
            foreach (var x in root.Descendants().Where(e => e.Name.LocalName == "element"))
            {
                var sub = Camel(Type(x));
                if (!Subtypes.ArchiElements.Contains(sub)) sub = Subtypes.ArchiElements.FirstOrDefault(s => s.Equals(sub, StringComparison.OrdinalIgnoreCase)) ?? "businessObject";
                var e = m.Create(target, ElementKind.ArchiElement, NameOf(x) is { Length: > 0 } n ? n : Subtypes.Display(sub));
                m.Set(e, FieldKeys.Subtype, sub);
                m.Set(e, FieldKeys.Documentation, x.Elements().FirstOrDefault(c => c.Name.LocalName == "documentation")?.Value);
                made[(string)x.Attribute("identifier")!] = e;
            }
            foreach (var x in root.Descendants().Where(e => e.Name.LocalName == "relationship"))
            {
                if (!made.TryGetValue((string?)x.Attribute("source") ?? "", out var s) || !made.TryGetValue((string?)x.Attribute("target") ?? "", out var t)) continue;
                var r = m.Connect(ElementKind.ArchiRelation, s, t, target);
                var sub = Camel(Type(x));
                m.Set(r, FieldKeys.Subtype, Subtypes.ArchiRelations.Contains(sub) ? sub : "association");
                m.Set(r, FieldKeys.Name, NameOf(x) is { Length: > 0 } n ? n : null);
                rels[(string)x.Attribute("identifier")!] = r;
            }
            foreach (var v in root.Descendants().Where(e => e.Name.LocalName == "view"))
            {
                var d = m.CreateDiagram(target, DiagramType.ArchiMate, NameOf(v) is { Length: > 0 } n ? n : "View");
                views++;
                var nodes = new Dictionary<string, DiagramNode>();
                foreach (var nx in v.Descendants().Where(e => e.Name.LocalName == "node"))
                {
                    if (!made.TryGetValue((string?)nx.Attribute("elementRef") ?? "", out var el)) continue;
                    double G(string a) => double.TryParse((string?)nx.Attribute(a), NumberStyles.Float, CultureInfo.InvariantCulture, out var val) ? val : 0;
                    nodes[(string)nx.Attribute("identifier")!] = m.AddNode(d, el, new Bounds(G("x"), G("y"), Math.Max(40, G("w")), Math.Max(30, G("h"))), showRelationships: false);
                }
                foreach (var cx in v.Descendants().Where(e => e.Name.LocalName == "connection"))
                    if (rels.TryGetValue((string?)cx.Attribute("relationshipRef") ?? "", out var r)) m.AddEdge(d, r);
            }
        }
        return (made.Count, rels.Count, views);
    }
}
