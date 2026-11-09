using System.Xml.Linq;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>
/// ReqIF 1.2 (OMG Requirements Interchange Format) for exchanging requirements with DOORS, Polarion, Jama, Codebeamer…
/// Export writes one SPEC-OBJECT per requirement (ForeignID, Name, Text), the containment hierarchy as a SPECIFICATION
/// and derive/trace links as SPEC-RELATIONs. Import accepts STRING and XHTML attributes and nests by hierarchy.
/// </summary>
public static class ReqIfExchange
{
    private static readonly XNamespace R = "http://www.omg.org/spec/ReqIF/20110401/reqif.xsd";
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";

    public static XDocument Export(ModelRepository model, string title = "Meridian export")
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        XElement Str(string id, string name) => new(R + "ATTRIBUTE-DEFINITION-STRING", new XAttribute("IDENTIFIER", id), new XAttribute("LONG-NAME", name), new XAttribute("LAST-CHANGE", now),
            new XElement(R + "TYPE", new XElement(R + "DATATYPE-DEFINITION-STRING-REF", "DT-STRING")));
        XElement Val(string def, string value) => new(R + "ATTRIBUTE-VALUE-STRING", new XAttribute("THE-VALUE", value),
            new XElement(R + "DEFINITION", new XElement(R + "ATTRIBUTE-DEFINITION-STRING-REF", def)));

        var reqs = model.OfKind(ElementKind.Requirement).ToList();
        var objects = reqs.Select(r => new XElement(R + "SPEC-OBJECT", new XAttribute("IDENTIFIER", r.Id), new XAttribute("LAST-CHANGE", now),
            new XElement(R + "TYPE", new XElement(R + "SPEC-OBJECT-TYPE-REF", "SOT-REQ")),
            new XElement(R + "VALUES", Val("AD-ID", r.Get(FieldKeys.RequirementId)), Val("AD-NAME", r.Name), Val("AD-TEXT", r.Get(FieldKeys.Text)))));

        var relations = model.Relationships.Where(r => r.Kind is ElementKind.DeriveReqt or ElementKind.Trace or ElementKind.Copy or ElementKind.Refine
                                                       && r.Source.Kind == ElementKind.Requirement && r.Target.Kind == ElementKind.Requirement)
            .Select(r => new XElement(R + "SPEC-RELATION", new XAttribute("IDENTIFIER", r.Id), new XAttribute("LAST-CHANGE", now), new XAttribute("LONG-NAME", r.Kind.ToString()),
                new XElement(R + "TYPE", new XElement(R + "SPEC-RELATION-TYPE-REF", "SRT-" + r.Kind)),
                new XElement(R + "SOURCE", new XElement(R + "SPEC-OBJECT-REF", r.Source.Id)),
                new XElement(R + "TARGET", new XElement(R + "SPEC-OBJECT-REF", r.Target.Id)))).ToList();

        XElement Hierarchy(Element req) => new(R + "SPEC-HIERARCHY", new XAttribute("IDENTIFIER", "H-" + req.Id), new XAttribute("LAST-CHANGE", now),
            new XElement(R + "OBJECT", new XElement(R + "SPEC-OBJECT-REF", req.Id)),
            req.OwnedElements.Any(c => c.Kind == ElementKind.Requirement)
                ? new XElement(R + "CHILDREN", req.OwnedElements.Where(c => c.Kind == ElementKind.Requirement).Select(Hierarchy))
                : null);

        var topLevel = reqs.Where(r => r.Owner?.Kind != ElementKind.Requirement);
        var relTypes = relations.Select(r => (string)r.Attribute("LONG-NAME")!).Distinct()
            .Select(k => new XElement(R + "SPEC-RELATION-TYPE", new XAttribute("IDENTIFIER", "SRT-" + k), new XAttribute("LONG-NAME", k), new XAttribute("LAST-CHANGE", now)));

        var root = new XElement(R + "REQ-IF", new XAttribute(XNamespace.Xmlns + "xhtml", Xhtml.NamespaceName),
            new XElement(R + "THE-HEADER", new XElement(R + "REQ-IF-HEADER", new XAttribute("IDENTIFIER", "H-" + model.Root.Id),
                new XElement(R + "CREATION-TIME", now), new XElement(R + "REQ-IF-TOOL-ID", "Meridian"), new XElement(R + "REQ-IF-VERSION", "1.0"),
                new XElement(R + "SOURCE-TOOL-ID", "Meridian"), new XElement(R + "TITLE", title))),
            new XElement(R + "CORE-CONTENT", new XElement(R + "REQ-IF-CONTENT",
                new XElement(R + "DATATYPES", new XElement(R + "DATATYPE-DEFINITION-STRING", new XAttribute("IDENTIFIER", "DT-STRING"), new XAttribute("LONG-NAME", "String"),
                    new XAttribute("MAX-LENGTH", "100000"), new XAttribute("LAST-CHANGE", now))),
                new XElement(R + "SPEC-TYPES",
                    new XElement(R + "SPEC-OBJECT-TYPE", new XAttribute("IDENTIFIER", "SOT-REQ"), new XAttribute("LONG-NAME", "Requirement"), new XAttribute("LAST-CHANGE", now),
                        new XElement(R + "SPEC-ATTRIBUTES", Str("AD-ID", "ReqIF.ForeignID"), Str("AD-NAME", "ReqIF.Name"), Str("AD-TEXT", "ReqIF.Text"))),
                    new XElement(R + "SPECIFICATION-TYPE", new XAttribute("IDENTIFIER", "ST-SPEC"), new XAttribute("LONG-NAME", "Specification"), new XAttribute("LAST-CHANGE", now)),
                    relTypes),
                new XElement(R + "SPEC-OBJECTS", objects),
                new XElement(R + "SPEC-RELATIONS", relations),
                new XElement(R + "SPECIFICATIONS", new XElement(R + "SPECIFICATION", new XAttribute("IDENTIFIER", "SPEC-" + model.Root.Id), new XAttribute("LONG-NAME", model.Root.Name),
                    new XAttribute("LAST-CHANGE", now),
                    new XElement(R + "TYPE", new XElement(R + "SPECIFICATION-TYPE-REF", "ST-SPEC")),
                    new XElement(R + "CHILDREN", topLevel.Select(Hierarchy)))))));
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    /// <summary>Imports requirements into <paramref name="target"/> (one undoable transaction). Returns (created, updated, relations).</summary>
    public static (int Created, int Updated, int Relations) Import(ModelRepository model, Element target, XDocument doc)
    {
        var content = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "REQ-IF-CONTENT") ?? throw new FormatException("Not a ReqIF document (no REQ-IF-CONTENT)");
        string Local(XElement e) => e.Name.LocalName;

        // Attribute definitions: identifier -> long name.
        var defs = content.Descendants().Where(e => Local(e).StartsWith("ATTRIBUTE-DEFINITION-") && e.Attribute("IDENTIFIER") != null)
            .ToDictionary(e => (string)e.Attribute("IDENTIFIER")!, e => (string?)e.Attribute("LONG-NAME") ?? "");

        string Role(string longName)
        {
            var n = longName.ToLowerInvariant();
            if (n is "reqif.foreignid" or "id" or "identifier" or "req id" || n.EndsWith(".id") || n.Contains("absolute number")) return "id";
            if (n is "reqif.name" or "name" or "title" or "reqif.chaptername" or "heading" or "object heading") return "name";
            if (n is "reqif.text" or "text" or "description" or "object text" || n.Contains("text")) return "text";
            return "";
        }

        var created = 0; var updated = 0; var relCount = 0;
        var byReqIfId = new Dictionary<string, Element>(StringComparer.Ordinal);
        var byReqId = model.OfKind(ElementKind.Requirement).Where(r => r.Get(FieldKeys.RequirementId).Length > 0)
            .GroupBy(r => r.Get(FieldKeys.RequirementId)).ToDictionary(g => g.Key, g => g.First());

        using (model.History.Transaction("Import ReqIF"))
        {
            foreach (var so in content.Descendants().Where(e => Local(e) == "SPEC-OBJECT"))
            {
                var ident = (string?)so.Attribute("IDENTIFIER") ?? Guid.NewGuid().ToString();
                string? id = null, name = (string?)so.Attribute("LONG-NAME"), text = null;
                foreach (var v in so.Descendants().Where(e => Local(e).StartsWith("ATTRIBUTE-VALUE-")))
                {
                    var defRef = v.Descendants().FirstOrDefault(e => Local(e).StartsWith("ATTRIBUTE-DEFINITION-") && Local(e).EndsWith("-REF"))?.Value;
                    var role = defRef != null && defs.TryGetValue(defRef, out var ln) ? Role(ln) : "";
                    var value = (string?)v.Attribute("THE-VALUE") ?? string.Concat(v.Descendants().Where(e => Local(e) == "THE-VALUE").SelectMany(t => t.DescendantNodes().OfType<XText>()).Select(t => t.Value)).Trim();
                    switch (role)
                    {
                        case "id": id = value; break;
                        case "name": name = value; break;
                        case "text": text = value; break;
                    }
                }
                id ??= ident;
                if (byReqId.TryGetValue(id, out var existing))
                {
                    if (name != null) model.Set(existing, FieldKeys.Name, name);
                    if (text != null) model.Set(existing, FieldKeys.Text, text);
                    byReqIfId[ident] = existing;
                    updated++;
                    continue;
                }
                var req = model.Create(target, ElementKind.Requirement, string.IsNullOrWhiteSpace(name) ? Short(text) ?? id : name);
                model.Set(req, FieldKeys.RequirementId, id);
                model.Set(req, FieldKeys.Text, text);
                byReqIfId[ident] = req;
                byReqId[id] = req;
                created++;
            }

            // Hierarchy → requirement containment.
            foreach (var h in content.Descendants().Where(e => Local(e) == "SPEC-HIERARCHY"))
            {
                var child = h.Elements().FirstOrDefault(e => Local(e) == "OBJECT")?.Value.Trim();
                var parentH = h.Parent?.Parent;
                var parent = parentH != null && Local(parentH) == "SPEC-HIERARCHY" ? parentH.Elements().FirstOrDefault(e => Local(e) == "OBJECT")?.Value.Trim() : null;
                if (child != null && parent != null && byReqIfId.TryGetValue(child, out var c) && byReqIfId.TryGetValue(parent, out var p) && !ReferenceEquals(c.Owner, p) && !c.IsAncestorOf(p))
                    model.Move(c, p);
            }

            foreach (var rel in content.Descendants().Where(e => Local(e) == "SPEC-RELATION"))
            {
                var s = rel.Elements().FirstOrDefault(e => Local(e) == "SOURCE")?.Value.Trim();
                var t = rel.Elements().FirstOrDefault(e => Local(e) == "TARGET")?.Value.Trim();
                if (s == null || t == null || !byReqIfId.TryGetValue(s, out var se) || !byReqIfId.TryGetValue(t, out var te)) continue;
                var kindName = ((string?)rel.Attribute("LONG-NAME") ?? rel.Descendants().FirstOrDefault(e => Local(e) == "SPEC-RELATION-TYPE-REF")?.Value ?? "").ToLowerInvariant();
                var kind = kindName.Contains("deriv") ? ElementKind.DeriveReqt : kindName.Contains("refine") ? ElementKind.Refine : kindName.Contains("copy") ? ElementKind.Copy : ElementKind.Trace;
                if (model.Outgoing(se, kind).Any(r => ReferenceEquals(r.Target, te))) continue;
                model.Connect(kind, se, te);
                relCount++;
            }
        }
        return (created, updated, relCount);
    }

    private static string? Short(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Length <= 60 ? text : text[..57] + "…";
}
