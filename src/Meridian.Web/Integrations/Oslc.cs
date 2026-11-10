using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Meridian.Core.Model;

namespace Meridian.Web.Integrations;

/// <summary>
/// OSLC Requirements Management 2.0 resource representations (read-only provider): root services, service provider
/// catalog, service providers, requirement query results and single requirements, as RDF/XML or JSON-LD.
/// </summary>
public static class Oslc
{
    public static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    public static readonly XNamespace Rdfs = "http://www.w3.org/2000/01/rdf-schema#";
    public static readonly XNamespace Dcterms = "http://purl.org/dc/terms/";
    public static readonly XNamespace Core = "http://open-services.net/ns/core#";
    public static readonly XNamespace Rm = "http://open-services.net/ns/rm#";
    /// <summary>The RM 1.0 namespace Jazz root services documents use for <c>rmServiceProviders</c>.</summary>
    public static readonly XNamespace RmRoot = "http://open-services.net/xmlns/rm/1.0/";
    public static readonly XNamespace Jfs = "http://jazz.net/xmlns/prod/jazz/jfs/1.0/";

    public const string RdfXml = "application/rdf+xml";
    public const string JsonLd = "application/ld+json";
    public const string CompactXml = "application/x-oslc-compact+xml";

    /// <summary>URL layout under a public base URL (scheme://host/pathbase).</summary>
    public sealed record Urls(string Base)
    {
        public string RootServices => $"{Base}/oslc/rootservices";
        public string Catalog => $"{Base}/oslc/catalog";
        public string Provider(Guid p) => $"{Base}/oslc/projects/{p}/provider";
        public string Query(Guid p) => $"{Base}/oslc/projects/{p}/requirements";
        public string Requirement(Guid p, string id) => $"{Base}/oslc/projects/{p}/requirements/{Uri.EscapeDataString(id)}";
        public string Selector(Guid p) => $"{Base}/oslc/projects/{p}/selector";
        public string Element(Guid p, string id) => DeepLink.For(Base, p, id);
    }

    private static XDocument Doc(params object[] content) => new(new XDeclaration("1.0", "utf-8", null), new XElement(Rdf + "RDF",
        new XAttribute(XNamespace.Xmlns + "rdf", Rdf), new XAttribute(XNamespace.Xmlns + "rdfs", Rdfs), new XAttribute(XNamespace.Xmlns + "dcterms", Dcterms),
        new XAttribute(XNamespace.Xmlns + "oslc", Core), new XAttribute(XNamespace.Xmlns + "oslc_rm", Rm), content));

    private static XAttribute About(string url) => new(Rdf + "about", url);
    private static XElement Ref(XName name, string url) => new(name, new XAttribute(Rdf + "resource", url));

    // ---------------------------------------------------------------- discovery

    public static XDocument RootServices(Urls u, string title) => new(new XDeclaration("1.0", "utf-8", null),
        new XElement(Rdf + "Description", About(u.RootServices),
            new XAttribute(XNamespace.Xmlns + "rdf", Rdf), new XAttribute(XNamespace.Xmlns + "dc", Dcterms),
            new XAttribute(XNamespace.Xmlns + "oslc_rm", RmRoot), new XAttribute(XNamespace.Xmlns + "jfs", Jfs),
            new XElement(Dcterms + "title", title),
            Ref(RmRoot + "rmServiceProviders", u.Catalog)));

    public static XDocument Catalog(Urls u, string title, IEnumerable<(Guid Id, string Name)> projects) => Doc(
        new XElement(Core + "ServiceProviderCatalog", About(u.Catalog),
            new XElement(Dcterms + "title", title),
            new XElement(Dcterms + "description", "Meridian projects as OSLC Requirements Management service providers"),
            Ref(Core + "domain", Rm.NamespaceName),
            projects.Select(p => new XElement(Core + "serviceProvider",
                new XElement(Core + "ServiceProvider", About(u.Provider(p.Id)),
                    new XElement(Dcterms + "title", p.Name))))));

    public static XDocument Provider(Urls u, Guid projectId, string name) => Doc(
        new XElement(Core + "ServiceProvider", About(u.Provider(projectId)),
            new XElement(Dcterms + "title", name),
            new XElement(Dcterms + "description", $"Requirements of the Meridian project “{name}” (read-only)"),
            new XElement(Core + "service",
                new XElement(Core + "Service",
                    Ref(Core + "domain", Rm.NamespaceName),
                    new XElement(Core + "queryCapability",
                        new XElement(Core + "QueryCapability",
                            new XElement(Dcterms + "title", "Requirements"),
                            new XElement(Core + "label", "Requirements"),
                            Ref(Core + "queryBase", u.Query(projectId)),
                            Ref(Core + "resourceType", Rm.NamespaceName + "Requirement"),
                            Ref(Core + "usage", Core.NamespaceName + "default"))),
                    new XElement(Core + "selectionDialog",
                        new XElement(Core + "Dialog",
                            new XElement(Dcterms + "title", "Select Meridian requirements"),
                            new XElement(Core + "label", "Requirement"),
                            Ref(Core + "dialog", u.Selector(projectId)),
                            new XElement(Core + "hintWidth", "640px"),
                            new XElement(Core + "hintHeight", "520px"),
                            Ref(Core + "resourceType", Rm.NamespaceName + "Requirement"),
                            Ref(Core + "usage", Core.NamespaceName + "default")))))));

    // ---------------------------------------------------------------- requirements

    /// <summary>The OSLC view of one requirement.</summary>
    public sealed record Req(string Url, string Identifier, string Title, string Description, IReadOnlyList<string> SatisfiedBy, IReadOnlyList<string> ValidatedBy, string WebUrl);

    public static Req Describe(ModelRepository model, Urls u, Guid projectId, Element e)
    {
        var text = e.Get(FieldKeys.Text);
        var doc = e.Documentation;
        return new Req(u.Requirement(projectId, e.Id),
            e.Get(FieldKeys.RequirementId) is { Length: > 0 } rid ? rid : e.Id,
            e.DisplayName,
            string.Join("\n\n", new[] { text, doc }.Where(s => s.Length > 0)),
            model.Incoming(e, ElementKind.Satisfy).Select(r => u.Element(projectId, r.Source.Id)).ToList(),
            model.Incoming(e, ElementKind.Verify).Select(r => u.Element(projectId, r.Source.Id)).ToList(),
            u.Element(projectId, e.Id));
    }

    private static XElement RequirementXml(Req r, string providerUrl) => new(Rm + "Requirement", About(r.Url),
        new XElement(Dcterms + "identifier", r.Identifier),
        new XElement(Dcterms + "title", r.Title),
        r.Description.Length > 0 ? new XElement(Dcterms + "description", r.Description) : null,
        r.SatisfiedBy.Select(s => Ref(Rm + "satisfiedBy", s)),
        r.ValidatedBy.Select(s => Ref(Rm + "validatedBy", s)),
        Ref(Core + "serviceProvider", providerUrl));

    public static XDocument RequirementRdf(Req r, string providerUrl) => Doc(RequirementXml(r, providerUrl));

    public static XDocument QueryRdf(string queryBase, string requestUrl, IReadOnlyList<Req> page, int total, string? nextPage, string providerUrl) => Doc(
        new XElement(Rdf + "Description", About(queryBase), page.Select(r => Ref(Rdfs + "member", r.Url))),
        new XElement(Core + "ResponseInfo", About(requestUrl),
            new XElement(Core + "totalCount", total),
            nextPage != null ? Ref(Core + "nextPage", nextPage) : null),
        page.Select(r => RequirementXml(r, providerUrl)));

    public static XDocument Compact(Req r) => Doc(
        new XElement(Core + "Compact", About(r.Url),
            new XElement(Dcterms + "title", r.Title),
            new XElement(Core + "shortTitle", r.Identifier)));

    private static JsonObject Context() => new()
    {
        ["rdf"] = Rdf.NamespaceName, ["rdfs"] = Rdfs.NamespaceName, ["dcterms"] = Dcterms.NamespaceName,
        ["oslc"] = Core.NamespaceName, ["oslc_rm"] = Rm.NamespaceName,
    };

    private static JsonArray Ids(IEnumerable<string> urls) => new(urls.Select(s => (JsonNode)new JsonObject { ["@id"] = s }).ToArray());

    private static JsonObject RequirementJson(Req r, string providerUrl)
    {
        var o = new JsonObject
        {
            ["@id"] = r.Url, ["@type"] = "oslc_rm:Requirement",
            ["dcterms:identifier"] = r.Identifier, ["dcterms:title"] = r.Title,
        };
        if (r.Description.Length > 0) o["dcterms:description"] = r.Description;
        if (r.SatisfiedBy.Count > 0) o["oslc_rm:satisfiedBy"] = Ids(r.SatisfiedBy);
        if (r.ValidatedBy.Count > 0) o["oslc_rm:validatedBy"] = Ids(r.ValidatedBy);
        o["oslc:serviceProvider"] = new JsonObject { ["@id"] = providerUrl };
        return o;
    }

    public static JsonObject RequirementJsonLd(Req r, string providerUrl)
    {
        var o = RequirementJson(r, providerUrl);
        var result = new JsonObject { ["@context"] = Context() };
        foreach (var (k, v) in o) result[k] = v?.DeepClone();
        return result;
    }

    public static JsonObject QueryJsonLd(string queryBase, string requestUrl, IReadOnlyList<Req> page, int total, string? nextPage, string providerUrl)
    {
        var info = new JsonObject { ["@id"] = requestUrl, ["@type"] = "oslc:ResponseInfo", ["oslc:totalCount"] = total };
        if (nextPage != null) info["oslc:nextPage"] = new JsonObject { ["@id"] = nextPage };
        return new JsonObject
        {
            ["@context"] = Context(),
            ["@id"] = queryBase,
            ["rdfs:member"] = new JsonArray(page.Select(r => (JsonNode)RequirementJson(r, providerUrl)).ToArray()),
            ["oslc:responseInfo"] = info,
        };
    }

    // ---------------------------------------------------------------- query

    /// <summary>
    /// Applies the supported subset of the OSLC query syntax: <c>oslc.where</c> with <c>prop="value"</c> terms joined
    /// by <c>and</c> (dcterms:identifier, dcterms:title; also <c>in [...]</c> on dcterms:identifier), and
    /// <c>oslc.searchTerms</c> (case-insensitive text match on identifier, title and description).
    /// </summary>
    public static IEnumerable<Req> Filter(IEnumerable<Req> all, string? where, string? searchTerms)
    {
        var q = all;
        if (!string.IsNullOrWhiteSpace(where))
        {
            foreach (Match m in Regex.Matches(where, @"(dcterms:\w+)\s*=\s*""((?:[^""\\]|\\.)*)"""))
            {
                var (prop, value) = (m.Groups[1].Value, Regex.Unescape(m.Groups[2].Value));
                q = prop switch
                {
                    "dcterms:identifier" => q.Where(r => string.Equals(r.Identifier, value, StringComparison.OrdinalIgnoreCase)).ToList(),
                    "dcterms:title" => q.Where(r => string.Equals(r.Title, value, StringComparison.OrdinalIgnoreCase)).ToList(),
                    _ => q,
                };
            }
            var inMatch = Regex.Match(where, @"dcterms:identifier\s+in\s*\[([^\]]*)\]");
            if (inMatch.Success)
            {
                var ids = Regex.Matches(inMatch.Groups[1].Value, @"""([^""]*)""").Select(x => x.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                q = q.Where(r => ids.Contains(r.Identifier)).ToList();
            }
        }
        if (!string.IsNullOrWhiteSpace(searchTerms))
        {
            var terms = Regex.Matches(searchTerms, @"""([^""]+)""|(\S+)").Select(x => x.Groups[1].Success ? x.Groups[1].Value : x.Groups[2].Value).ToList();
            q = q.Where(r => terms.All(t => r.Identifier.Contains(t, StringComparison.OrdinalIgnoreCase) || r.Title.Contains(t, StringComparison.OrdinalIgnoreCase)
                                            || r.Description.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
        }
        return q;
    }

    /// <summary>All requirements of a model, in identifier order.</summary>
    public static List<Req> Requirements(ModelRepository model, Urls u, Guid projectId) =>
        model.OfKind(ElementKind.Requirement).Select(e => Describe(model, u, projectId, e))
            .OrderBy(r => r.Identifier, StringComparer.OrdinalIgnoreCase).ToList();
}
