using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Meridian.Web.Integrations;

// A deliberately small RDF toolkit (no package dependency): an in-memory triple list, an RDF/XML parser that covers
// what OSLC servers emit (typed nodes, rdf:about/ID/nodeID, rdf:resource, nested descriptions, parseType Resource /
// Literal / Collection, datatypes, xml:lang, xml:base, property attributes), and Turtle / JSON-LD writers. Meridian's
// OSLC provider builds RDF/XML and converts it with these writers; the DOORS Next connector parses RDF/XML with it.

public abstract record RdfNode;

public sealed record RdfIri(string Value) : RdfNode
{
    public override string ToString() => $"<{Value}>";
}

public sealed record RdfBlank(string Id) : RdfNode
{
    public override string ToString() => $"_:{Id}";
}

public sealed record RdfLiteral(string Value, string? Datatype = null, string? Lang = null) : RdfNode
{
    public const string XmlLiteral = "http://www.w3.org/1999/02/22-rdf-syntax-ns#XMLLiteral";
    public override string ToString() => $"\"{Value}\"";
}

public sealed record RdfTriple(RdfNode Subject, RdfIri Predicate, RdfNode Object);

/// <summary>An ordered list of triples with a few lookup helpers.</summary>
public sealed class RdfGraph
{
    public const string RdfNs = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    public const string RdfType = RdfNs + "type";

    private readonly List<RdfTriple> _triples = new();
    private readonly HashSet<RdfTriple> _set = new();

    public IReadOnlyList<RdfTriple> Triples => _triples;

    public void Add(RdfNode s, string predicate, RdfNode o)
    {
        var t = new RdfTriple(s, new RdfIri(predicate), o);
        if (_set.Add(t)) _triples.Add(t);
    }

    public IEnumerable<RdfNode> Objects(RdfNode subject, string predicate) =>
        _triples.Where(t => t.Subject.Equals(subject) && t.Predicate.Value == predicate).Select(t => t.Object);

    /// <summary>The first literal value (or IRI) of a property, or null.</summary>
    public string? Value(RdfNode subject, string predicate) => Objects(subject, predicate).Select(Text).FirstOrDefault();

    public IEnumerable<string> Iris(RdfNode subject, string predicate) => Objects(subject, predicate).OfType<RdfIri>().Select(i => i.Value);

    public IEnumerable<RdfNode> Subjects(string predicate, RdfNode obj) =>
        _triples.Where(t => t.Predicate.Value == predicate && t.Object.Equals(obj)).Select(t => t.Subject).Distinct();

    public IEnumerable<RdfNode> OfType(string typeIri) => Subjects(RdfType, new RdfIri(typeIri));

    public IEnumerable<RdfNode> SubjectNodes => _triples.Select(t => t.Subject).Distinct();

    public static string Text(RdfNode n) => n switch
    {
        RdfLiteral l => l.Value,
        RdfIri i => i.Value,
        RdfBlank b => "_:" + b.Id,
        _ => "",
    };
}

/// <summary>A failure reading RDF.</summary>
public sealed class RdfFormatException(string message) : Exception(message);

/// <summary>RDF/XML → triples (RDF 1.1 XML syntax, minus reification via rdf:ID on property elements).</summary>
public static class RdfXmlParser
{
    private static readonly XNamespace R = RdfGraph.RdfNs;
    private static readonly XNamespace XmlNs = XNamespace.Xml;

    public static RdfGraph Parse(string xml, string? baseUri = null)
    {
        XDocument doc;
        try
        {
            // DTDs are never processed (no entity expansion from a remote server).
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex) { throw new RdfFormatException($"Not RDF/XML: {ex.Message}"); }
        return Parse(doc, baseUri);
    }

    public static RdfGraph Parse(XDocument doc, string? baseUri = null)
    {
        var g = new RdfGraph();
        var p = new Parser(g);
        var root = doc.Root ?? throw new RdfFormatException("Empty document.");
        var b = Base(root, baseUri);
        if (root.Name == R + "RDF")
            foreach (var n in root.Elements()) p.Node(n, Base(n, b));
        else p.Node(root, b);
        return g;
    }

    private static string? Base(XElement e, string? inherited)
    {
        var xb = (string?)e.Attribute(XmlNs + "base");
        if (xb is null) return inherited;
        return inherited != null && Uri.TryCreate(new Uri(inherited), xb, out var u) ? u.ToString() : xb;
    }

    private static string Resolve(string? baseUri, string reference)
    {
        if (Uri.TryCreate(reference, UriKind.Absolute, out var abs)) return abs.OriginalString;
        if (baseUri != null && Uri.TryCreate(baseUri, UriKind.Absolute, out var b) && Uri.TryCreate(b, reference, out var r)) return r.ToString();
        return reference;
    }

    private static string Iri(XName name) => name.NamespaceName + name.LocalName;

    private static bool IsSyntaxAttribute(XAttribute a) =>
        a.IsNamespaceDeclaration || a.Name.Namespace == XmlNs
        || (a.Name.Namespace == R && a.Name.LocalName is "about" or "ID" or "nodeID" or "resource" or "parseType" or "datatype" or "type" or "bagID" or "aboutEach")
        || (a.Name.Namespace == XNamespace.None && a.Name.LocalName.StartsWith("xml", StringComparison.OrdinalIgnoreCase));

    private static string? Lang(XElement e)
    {
        for (var x = e; x != null; x = x.Parent)
            if ((string?)x.Attribute(XmlNs + "lang") is { } l) return l.Length == 0 ? null : l;
        return null;
    }

    private sealed class Parser(RdfGraph g)
    {
        private int _blank;
        private readonly Dictionary<string, RdfBlank> _named = new();

        private RdfBlank NewBlank() => new("b" + (++_blank).ToString(CultureInfo.InvariantCulture));
        private RdfBlank Named(string id) => _named.TryGetValue(id, out var b) ? b : _named[id] = new RdfBlank("n" + _named.Count.ToString(CultureInfo.InvariantCulture) + "_" + Regex.Replace(id, @"[^\w]", ""));

        public RdfNode Node(XElement e, string? baseUri)
        {
            RdfNode subject = e.Attribute(R + "about") is { } about ? new RdfIri(Resolve(baseUri, about.Value))
                : e.Attribute(R + "ID") is { } id ? new RdfIri(Resolve(baseUri, "#" + id.Value))
                : e.Attribute(R + "nodeID") is { } nid ? Named(nid.Value)
                : NewBlank();
            if (e.Name != R + "Description") g.Add(subject, RdfGraph.RdfType, new RdfIri(Iri(e.Name)));
            if (e.Attribute(R + "type") is { } t) g.Add(subject, RdfGraph.RdfType, new RdfIri(Resolve(baseUri, t.Value)));
            foreach (var a in e.Attributes().Where(a => !IsSyntaxAttribute(a)))
                g.Add(subject, Iri(a.Name), new RdfLiteral(a.Value, null, Lang(e)));
            var li = 0;
            foreach (var prop in e.Elements()) Property(subject, prop, Base(prop, baseUri), ref li);
            return subject;
        }

        private void Property(RdfNode subject, XElement p, string? baseUri, ref int li)
        {
            var predicate = p.Name == R + "li" ? RdfGraph.RdfNs + "_" + (++li).ToString(CultureInfo.InvariantCulture) : Iri(p.Name);
            var parseType = (string?)p.Attribute(R + "parseType");
            if (p.Attribute(R + "resource") is { } res)
            {
                var o = new RdfIri(Resolve(baseUri, res.Value));
                g.Add(subject, predicate, o);
                PropertyAttributes(o, p);
                return;
            }
            if (p.Attribute(R + "nodeID") is { } nid && parseType is null)
            {
                var o = Named(nid.Value);
                g.Add(subject, predicate, o);
                PropertyAttributes(o, p);
                return;
            }
            switch (parseType)
            {
                case "Resource":
                {
                    var o = NewBlank();
                    g.Add(subject, predicate, o);
                    var inner = 0;
                    foreach (var c in p.Elements()) Property(o, c, Base(c, baseUri), ref inner);
                    return;
                }
                case "Literal":
                    g.Add(subject, predicate, new RdfLiteral(string.Concat(p.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting))), RdfLiteral.XmlLiteral));
                    return;
                case "Collection":
                {
                    RdfNode head = new RdfIri(RdfGraph.RdfNs + "nil");
                    foreach (var item in p.Elements().Reverse())
                    {
                        var cell = NewBlank();
                        g.Add(cell, RdfGraph.RdfNs + "first", Node(item, Base(item, baseUri)));
                        g.Add(cell, RdfGraph.RdfNs + "rest", head);
                        head = cell;
                    }
                    g.Add(subject, predicate, head);
                    return;
                }
            }
            var children = p.Elements().ToList();
            if (children.Count > 0)
            {
                g.Add(subject, predicate, Node(children[0], Base(children[0], baseUri)));
                return;
            }
            if (p.Attributes().Any(a => !IsSyntaxAttribute(a)))
            {
                var o = NewBlank();
                g.Add(subject, predicate, o);
                PropertyAttributes(o, p);
                return;
            }
            var dt = (string?)p.Attribute(R + "datatype");
            g.Add(subject, predicate, new RdfLiteral(p.Value, dt is null ? null : Resolve(baseUri, dt), dt is null ? Lang(p) : null));
        }

        private void PropertyAttributes(RdfNode o, XElement p)
        {
            foreach (var a in p.Attributes().Where(a => !IsSyntaxAttribute(a)))
                g.Add(o, Iri(a.Name), new RdfLiteral(a.Value, null, Lang(p)));
        }
    }
}

/// <summary>Prefixes used when writing Turtle and JSON-LD.</summary>
public static class RdfPrefixes
{
    public static readonly IReadOnlyList<(string Prefix, string Ns)> Default =
    [
        ("rdf", RdfGraph.RdfNs),
        ("rdfs", "http://www.w3.org/2000/01/rdf-schema#"),
        ("xsd", "http://www.w3.org/2001/XMLSchema#"),
        ("dcterms", "http://purl.org/dc/terms/"),
        ("oslc", "http://open-services.net/ns/core#"),
        ("oslc_rm", "http://open-services.net/ns/rm#"),
        ("oslc_am", "http://open-services.net/ns/am#"),
        ("oslc_rm1", "http://open-services.net/xmlns/rm/1.0/"),
        ("oslc_am1", "http://open-services.net/xmlns/am/1.0/"),
        ("jfs", "http://jazz.net/xmlns/prod/jazz/jfs/1.0/"),
        ("mrd", "https://meridian.dev/ns/oslc#"),
    ];

    private static readonly Regex LocalName = new(@"^[A-Za-z_][A-Za-z0-9_\-]*$", RegexOptions.Compiled);

    /// <summary>prefix:local when <paramref name="iri"/> is in a known namespace and the local part is a plain name.</summary>
    public static string? Compact(string iri, IEnumerable<(string Prefix, string Ns)> prefixes)
    {
        foreach (var (prefix, ns) in prefixes)
            if (iri.StartsWith(ns, StringComparison.Ordinal) && LocalName.IsMatch(iri[ns.Length..])) return prefix + ":" + iri[ns.Length..];
        return null;
    }
}

/// <summary>Writes a graph as Turtle (subjects grouped, rdf:type as <c>a</c>, known prefixes declared when used).</summary>
public static class Turtle
{
    public const string MediaType = "text/turtle";

    public static string Write(RdfGraph g, IEnumerable<(string Prefix, string Ns)>? prefixes = null)
    {
        var pfx = (prefixes ?? RdfPrefixes.Default).ToList();
        var used = new HashSet<string>();
        string Term(RdfNode n)
        {
            switch (n)
            {
                case RdfIri i:
                    if (RdfPrefixes.Compact(i.Value, pfx) is { } c) { used.Add(c[..c.IndexOf(':')]); return c; }
                    return "<" + EscapeIri(i.Value) + ">";
                case RdfBlank b:
                    return "_:" + b.Id;
                case RdfLiteral l:
                    var s = Quote(l.Value);
                    if (l.Lang is { Length: > 0 } lang) return s + "@" + lang;
                    if (l.Datatype is { Length: > 0 } dt) return s + "^^" + Term(new RdfIri(dt));
                    return s;
                default:
                    return "";
            }
        }

        var body = new StringBuilder();
        foreach (var group in g.Triples.GroupBy(t => t.Subject))
        {
            body.Append(Term(group.Key));
            var first = true;
            foreach (var byPred in group.GroupBy(t => t.Predicate.Value))
            {
                body.Append(first ? "\n    " : " ;\n    ");
                first = false;
                body.Append(byPred.Key == RdfGraph.RdfType ? "a" : Term(new RdfIri(byPred.Key)));
                body.Append(' ');
                body.Append(string.Join(" , ", byPred.Select(t => Term(t.Object))));
            }
            body.Append(" .\n\n");
        }
        var head = new StringBuilder();
        foreach (var (prefix, ns) in pfx.Where(p => used.Contains(p.Prefix)))
            head.Append("@prefix ").Append(prefix).Append(": <").Append(ns).Append("> .\n");
        if (head.Length > 0) head.Append('\n');
        return head.Append(body).ToString().TrimEnd() + "\n";
    }

    private static string EscapeIri(string iri)
    {
        var sb = new StringBuilder();
        foreach (var ch in iri)
            sb.Append(ch is '<' or '>' or '"' or '{' or '}' or '|' or '^' or '`' or '\\' || ch <= ' ' ? $"\\u{(int)ch:X4}" : ch.ToString());
        return sb.ToString();
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in s)
            sb.Append(ch switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when ch < ' ' => $"\\u{(int)ch:X4}",
                _ => ch.ToString(),
            });
        return sb.Append('"').ToString();
    }
}

/// <summary>Writes a graph as compacted JSON-LD: an <c>@context</c> of the prefixes used and a <c>@graph</c> of node objects.</summary>
public static class JsonLd
{
    public const string MediaType = "application/ld+json";

    public static JsonObject Write(RdfGraph g, IEnumerable<(string Prefix, string Ns)>? prefixes = null)
    {
        var pfx = (prefixes ?? RdfPrefixes.Default).ToList();
        var used = new HashSet<string>();
        string Key(string iri)
        {
            if (RdfPrefixes.Compact(iri, pfx) is { } c) { used.Add(c[..c.IndexOf(':')]); return c; }
            return iri;
        }
        string Id(RdfNode n) => n is RdfBlank b ? "_:" + b.Id : ((RdfIri)n).Value;

        var nodes = new JsonArray();
        foreach (var group in g.Triples.GroupBy(t => t.Subject))
        {
            var o = new JsonObject { ["@id"] = Id(group.Key) };
            var types = group.Where(t => t.Predicate.Value == RdfGraph.RdfType && t.Object is RdfIri).Select(t => (JsonNode)Key(((RdfIri)t.Object).Value)!).ToArray();
            if (types.Length == 1) o["@type"] = types[0];
            else if (types.Length > 1) o["@type"] = new JsonArray(types);
            foreach (var byPred in group.Where(t => !(t.Predicate.Value == RdfGraph.RdfType && t.Object is RdfIri)).GroupBy(t => t.Predicate.Value))
            {
                var values = byPred.Select(t => Value(t.Object, Key)).ToArray();
                o[Key(byPred.Key)] = values.Length == 1 ? values[0] : new JsonArray(values);
            }
            nodes.Add(o);
        }
        var context = new JsonObject();
        foreach (var (prefix, ns) in pfx.Where(p => used.Contains(p.Prefix))) context[prefix] = ns;
        return new JsonObject { ["@context"] = context, ["@graph"] = nodes };
    }

    private static JsonNode Value(RdfNode n, Func<string, string> key) => n switch
    {
        RdfIri i => new JsonObject { ["@id"] = i.Value },
        RdfBlank b => new JsonObject { ["@id"] = "_:" + b.Id },
        RdfLiteral { Lang: { Length: > 0 } lang } l => new JsonObject { ["@value"] = l.Value, ["@language"] = lang },
        RdfLiteral { Datatype: { Length: > 0 } dt } l => new JsonObject { ["@value"] = l.Value, ["@type"] = key(dt) },
        RdfLiteral l => JsonValue.Create(l.Value)!,
        _ => JsonValue.Create("")!,
    };
}

/// <summary>The RDF serialization a client asked for in its <c>Accept</c> header.</summary>
public enum RdfFormat { RdfXml, Turtle, JsonLd, CompactXml, Html }

public static class RdfNegotiation
{
    /// <summary>
    /// Picks the best supported format by q-value (ties go to the order listed in the header). Anything else, or no
    /// header, gives RDF/XML. <c>text/html</c> is only chosen when <paramref name="allowHtml"/> is set.
    /// </summary>
    public static RdfFormat Choose(string? accept, bool allowHtml = false, bool allowCompact = false)
    {
        if (string.IsNullOrWhiteSpace(accept)) return RdfFormat.RdfXml;
        var best = (Format: RdfFormat.RdfXml, Q: -1.0);
        foreach (var part in accept.Split(','))
        {
            var bits = part.Split(';');
            var type = bits[0].Trim().ToLowerInvariant();
            var q = 1.0;
            foreach (var param in bits.Skip(1))
            {
                var kv = param.Split('=', 2);
                if (kv.Length == 2 && kv[0].Trim() == "q" && double.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) q = v;
            }
            RdfFormat? f = type switch
            {
                "application/rdf+xml" or "application/xml" or "text/xml" => RdfFormat.RdfXml,
                "text/turtle" or "application/x-turtle" => RdfFormat.Turtle,
                "application/ld+json" or "application/json" => RdfFormat.JsonLd,
                Oslc.CompactXml when allowCompact => RdfFormat.CompactXml,
                "text/html" or "application/xhtml+xml" when allowHtml => RdfFormat.Html,
                _ => null,
            };
            if (f is { } fmt && q > 0 && q > best.Q) best = (fmt, q);
        }
        return best.Q < 0 ? RdfFormat.RdfXml : best.Format;
    }
}
