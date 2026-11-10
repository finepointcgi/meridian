using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Web.Integrations;

/// <summary>
/// OSLC 2.0 resource representations of the Meridian provider: root services, service provider catalog, service
/// providers (Requirements Management and Architecture Management services), resource shapes, requirement and
/// architecture-element query results and resources, Compact (UI preview) documents and <c>oslc:Error</c>.
/// Everything is built as RDF/XML; <see cref="Turtle"/> and <see cref="JsonLd"/> convert it for other media types.
/// </summary>
public static class Oslc
{
    public static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    public static readonly XNamespace Rdfs = "http://www.w3.org/2000/01/rdf-schema#";
    public static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema#";
    public static readonly XNamespace Dcterms = "http://purl.org/dc/terms/";
    public static readonly XNamespace Core = "http://open-services.net/ns/core#";
    public static readonly XNamespace Rm = "http://open-services.net/ns/rm#";
    public static readonly XNamespace Am = "http://open-services.net/ns/am#";
    /// <summary>The RM 1.0 namespace Jazz root services documents use for <c>rmServiceProviders</c>.</summary>
    public static readonly XNamespace RmRoot = "http://open-services.net/xmlns/rm/1.0/";
    /// <summary>The AM 1.0 namespace Jazz root services documents use for <c>amServiceProviders</c>.</summary>
    public static readonly XNamespace AmRoot = "http://open-services.net/xmlns/am/1.0/";
    public static readonly XNamespace Jfs = "http://jazz.net/xmlns/prod/jazz/jfs/1.0/";
    /// <summary>Meridian-specific properties (element kind, stereotypes).</summary>
    public static readonly XNamespace Mrd = "https://meridian.dev/ns/oslc#";

    public const string RdfXml = "application/rdf+xml";
    public const string JsonLdType = "application/ld+json";
    public const string CompactXml = "application/x-oslc-compact+xml";

    /// <summary>Kept for callers that used the old name.</summary>
    public const string JsonLd = JsonLdType;

    /// <summary>URL layout under a public base URL (scheme://host/pathbase).</summary>
    public sealed record Urls(string Base)
    {
        public string RootServices => $"{Base}/oslc/rootservices";
        public string Catalog => $"{Base}/oslc/catalog";
        public string Shape(string name) => $"{Base}/oslc/shapes/{name}";
        public string Icon => $"{Base}/icons/icon-192.png";
        public string Provider(Guid p) => $"{Base}/oslc/projects/{p}/provider";
        public string Query(Guid p) => $"{Base}/oslc/projects/{p}/requirements";
        public string Requirement(Guid p, string id) => $"{Base}/oslc/projects/{p}/requirements/{Uri.EscapeDataString(id)}";
        public string Preview(Guid p, string id, string size = "small") => $"{Base}/oslc/projects/{p}/preview/{Uri.EscapeDataString(id)}?size={size}";
        public string Selector(Guid p) => $"{Base}/oslc/projects/{p}/selector";
        public string AmSelector(Guid p) => $"{Base}/oslc/projects/{p}/selector?type=resource";
        public string Creator(Guid p) => $"{Base}/oslc/projects/{p}/creator";
        public string AmQuery(Guid p) => $"{Base}/oslc/projects/{p}/resources";
        public string AmResource(Guid p, string id) => $"{Base}/oslc/projects/{p}/resources/{Uri.EscapeDataString(id)}";
        public string Element(Guid p, string id) => DeepLink.For(Base, p, id);

        /// <summary>The OSLC URL of any element: requirements are RM resources, everything else an AM resource.</summary>
        public string Resource(Guid p, Element e) => e.Kind == ElementKind.Requirement ? Requirement(p, e.Id) : AmResource(p, e.Id);
    }

    private static XElement RdfRoot(params object?[] content) => new(Rdf + "RDF",
        new XAttribute(XNamespace.Xmlns + "rdf", Rdf), new XAttribute(XNamespace.Xmlns + "rdfs", Rdfs), new XAttribute(XNamespace.Xmlns + "dcterms", Dcterms),
        new XAttribute(XNamespace.Xmlns + "oslc", Core), new XAttribute(XNamespace.Xmlns + "oslc_rm", Rm), new XAttribute(XNamespace.Xmlns + "oslc_am", Am),
        new XAttribute(XNamespace.Xmlns + "mrd", Mrd), content);

    private static XDocument Doc(params object?[] content) => new(new XDeclaration("1.0", "utf-8", null), RdfRoot(content));

    private static XAttribute About(string url) => new(Rdf + "about", url);
    private static XElement Ref(XName name, string url) => new(name, new XAttribute(Rdf + "resource", url));
    private static XElement Typed(XName name, object value, string xsdType) => new(name, new XAttribute(Rdf + "datatype", Xsd.NamespaceName + xsdType), value);

    // ---------------------------------------------------------------- discovery

    public static XDocument RootServices(Urls u, string title) => new(new XDeclaration("1.0", "utf-8", null),
        new XElement(Rdf + "Description", About(u.RootServices),
            new XAttribute(XNamespace.Xmlns + "rdf", Rdf), new XAttribute(XNamespace.Xmlns + "dc", Dcterms),
            new XAttribute(XNamespace.Xmlns + "oslc_rm", RmRoot), new XAttribute(XNamespace.Xmlns + "oslc_am", AmRoot), new XAttribute(XNamespace.Xmlns + "jfs", Jfs),
            new XElement(Dcterms + "title", title),
            Ref(RmRoot + "rmServiceProviders", u.Catalog),
            Ref(AmRoot + "amServiceProviders", u.Catalog)));

    public static XDocument Catalog(Urls u, string title, IEnumerable<(Guid Id, string Name)> projects) => Doc(
        new XElement(Core + "ServiceProviderCatalog", About(u.Catalog),
            new XElement(Dcterms + "title", title),
            new XElement(Dcterms + "description", "Meridian projects as OSLC Requirements Management and Architecture Management service providers"),
            Ref(Core + "domain", Rm.NamespaceName),
            Ref(Core + "domain", Am.NamespaceName),
            projects.Select(p => new XElement(Core + "serviceProvider",
                new XElement(Core + "ServiceProvider", About(u.Provider(p.Id)),
                    new XElement(Dcterms + "title", p.Name))))));

    private static XElement Dialog(XName kind, string title, string label, string url, string width, string height, string resourceType) =>
        new(kind, new XElement(Core + "Dialog",
            new XElement(Dcterms + "title", title),
            new XElement(Core + "label", label),
            Ref(Core + "dialog", url),
            new XElement(Core + "hintWidth", width),
            new XElement(Core + "hintHeight", height),
            Ref(Core + "resourceType", resourceType),
            Ref(Core + "usage", Core.NamespaceName + "default")));

    public static XDocument Provider(Urls u, Guid projectId, string name) => Doc(
        new XElement(Core + "ServiceProvider", About(u.Provider(projectId)),
            new XElement(Dcterms + "title", name),
            new XElement(Dcterms + "description", $"Requirements and architecture elements of the Meridian project “{name}”"),
            Ref(Core + "details", $"{u.Base}/?project={projectId}"),
            new XElement(Core + "prefixDefinition", new XElement(Core + "PrefixDefinition",
                new XElement(Core + "prefix", "oslc_rm"), Ref(Core + "prefixBase", Rm.NamespaceName))),
            new XElement(Core + "prefixDefinition", new XElement(Core + "PrefixDefinition",
                new XElement(Core + "prefix", "oslc_am"), Ref(Core + "prefixBase", Am.NamespaceName))),
            new XElement(Core + "service",
                new XElement(Core + "Service",
                    Ref(Core + "domain", Rm.NamespaceName),
                    new XElement(Core + "queryCapability",
                        new XElement(Core + "QueryCapability",
                            new XElement(Dcterms + "title", "Requirements"),
                            new XElement(Core + "label", "Requirements"),
                            Ref(Core + "queryBase", u.Query(projectId)),
                            Ref(Core + "resourceShape", u.Shape("requirement")),
                            Ref(Core + "resourceType", Rm.NamespaceName + "Requirement"),
                            Ref(Core + "usage", Core.NamespaceName + "default"))),
                    new XElement(Core + "creationFactory",
                        new XElement(Core + "CreationFactory",
                            new XElement(Dcterms + "title", "New requirement"),
                            new XElement(Core + "label", "Requirement"),
                            Ref(Core + "creation", u.Query(projectId)),
                            Ref(Core + "resourceShape", u.Shape("requirement")),
                            Ref(Core + "resourceType", Rm.NamespaceName + "Requirement"),
                            Ref(Core + "usage", Core.NamespaceName + "default"))),
                    Dialog(Core + "selectionDialog", "Select Meridian requirements", "Requirement", u.Selector(projectId), "640px", "520px", Rm.NamespaceName + "Requirement"),
                    Dialog(Core + "creationDialog", "Create a Meridian requirement", "Requirement", u.Creator(projectId), "560px", "460px", Rm.NamespaceName + "Requirement"))),
            new XElement(Core + "service",
                new XElement(Core + "Service",
                    Ref(Core + "domain", Am.NamespaceName),
                    new XElement(Core + "queryCapability",
                        new XElement(Core + "QueryCapability",
                            new XElement(Dcterms + "title", "Architecture elements"),
                            new XElement(Core + "label", "Model elements"),
                            Ref(Core + "queryBase", u.AmQuery(projectId)),
                            Ref(Core + "resourceShape", u.Shape("resource")),
                            Ref(Core + "resourceType", Am.NamespaceName + "Resource"),
                            Ref(Core + "usage", Core.NamespaceName + "default"))),
                    Dialog(Core + "selectionDialog", "Select Meridian model elements", "Model element", u.AmSelector(projectId), "640px", "520px", Am.NamespaceName + "Resource")))));

    // ---------------------------------------------------------------- resource shapes

    private static XElement Prop(Urls u, string name, XNamespace ns, string occurs, string valueType, bool readOnly, string? representation = null, string? range = null, string? description = null) =>
        new(Core + "property", new XElement(Core + "Property",
            new XElement(Core + "name", name),
            Ref(Core + "propertyDefinition", ns.NamespaceName + name),
            Ref(Core + "occurs", Core.NamespaceName + occurs),
            Ref(Core + "valueType", valueType),
            Typed(Core + "readOnly", readOnly ? "true" : "false", "boolean"),
            representation != null ? Ref(Core + "representation", Core.NamespaceName + representation) : null,
            range != null ? Ref(Core + "range", range) : null,
            description != null ? new XElement(Dcterms + "description", description) : null));

    /// <summary>The shapes Meridian publishes: <c>requirement</c> (oslc_rm:Requirement) and <c>resource</c> (oslc_am:Resource).</summary>
    public static XDocument? Shape(Urls u, string name)
    {
        const string str = "http://www.w3.org/2001/XMLSchema#string";
        const string xml = "http://www.w3.org/1999/02/22-rdf-syntax-ns#XMLLiteral";
        var resource = Core.NamespaceName + "Resource";
        return name switch
        {
            "requirement" => Doc(new XElement(Core + "ResourceShape", About(u.Shape("requirement")),
                new XElement(Dcterms + "title", "Meridian requirement"),
                Ref(Core + "describes", Rm.NamespaceName + "Requirement"),
                Prop(u, "identifier", Dcterms, "Exactly-one", str, false, description: "Requirement id, e.g. REQ-002 (assigned when omitted on creation)"),
                Prop(u, "title", Dcterms, "Exactly-one", xml, false),
                Prop(u, "description", Dcterms, "Zero-or-one", xml, false, description: "Requirement text and documentation"),
                Prop(u, "satisfiedBy", Rm, "Zero-or-many", resource, true, "Reference", Am.NamespaceName + "Resource", "Sources of «satisfy» links"),
                Prop(u, "validatedBy", Rm, "Zero-or-many", resource, true, "Reference", null, "Sources of «verify» links (test cases)"),
                Prop(u, "elaboratedBy", Rm, "Zero-or-many", resource, true, "Reference", null, "Other ends of «trace» links"),
                Prop(u, "serviceProvider", Core, "Exactly-one", resource, true, "Reference"))),
            "resource" => Doc(new XElement(Core + "ResourceShape", About(u.Shape("resource")),
                new XElement(Dcterms + "title", "Meridian model element"),
                Ref(Core + "describes", Am.NamespaceName + "Resource"),
                Prop(u, "identifier", Dcterms, "Exactly-one", str, true),
                Prop(u, "title", Dcterms, "Exactly-one", xml, true),
                Prop(u, "description", Dcterms, "Zero-or-one", xml, true),
                Prop(u, "type", Dcterms, "Exactly-one", str, true, description: "SysML kind, e.g. Block, Part property, Test case"),
                Prop(u, "kind", Mrd, "Exactly-one", str, true, description: "Meridian element kind"),
                Prop(u, "satisfies", Mrd, "Zero-or-many", resource, true, "Reference", Rm.NamespaceName + "Requirement", "Targets of «satisfy» links"),
                Prop(u, "verifies", Mrd, "Zero-or-many", resource, true, "Reference", Rm.NamespaceName + "Requirement", "Targets of «verify» links"),
                Prop(u, "serviceProvider", Core, "Exactly-one", resource, true, "Reference"))),
            _ => null,
        };
    }

    // ---------------------------------------------------------------- requirements

    /// <summary>The OSLC view of one requirement.</summary>
    public sealed record Req(string Url, string Identifier, string Title, string Description, IReadOnlyList<string> SatisfiedBy, IReadOnlyList<string> ValidatedBy, string WebUrl)
    {
        public IReadOnlyList<string> ElaboratedBy { get; init; } = [];
        public string ElementId { get; init; } = "";
    }

    public static Req Describe(ModelRepository model, Urls u, Guid projectId, Element e)
    {
        var text = e.Get(FieldKeys.Text);
        var doc = e.Documentation;
        return new Req(u.Requirement(projectId, e.Id),
            e.Get(FieldKeys.RequirementId) is { Length: > 0 } rid ? rid : e.Id,
            e.DisplayName,
            string.Join("\n\n", new[] { text, doc }.Where(s => s.Length > 0)),
            model.Incoming(e, ElementKind.Satisfy).Select(r => u.Resource(projectId, r.Source)).Distinct().ToList(),
            model.Incoming(e, ElementKind.Verify).Select(r => u.Resource(projectId, r.Source)).Distinct().ToList(),
            u.Element(projectId, e.Id))
        {
            ElaboratedBy = model.RelationshipsOf(e).Where(r => r.Kind == ElementKind.Trace)
                .Select(r => u.Resource(projectId, ReferenceEquals(r.Source, e) ? r.Target : r.Source)).Distinct().ToList(),
            ElementId = e.Id,
        };
    }

    private static bool Selected(IReadOnlySet<string>? select, XName prop) => select is null || select.Contains(prop.NamespaceName + prop.LocalName);

    private static XElement RequirementXml(Req r, string providerUrl, IReadOnlySet<string>? select = null) => new(Rm + "Requirement", About(r.Url),
        Selected(select, Dcterms + "identifier") ? new XElement(Dcterms + "identifier", r.Identifier) : null,
        Selected(select, Dcterms + "title") ? new XElement(Dcterms + "title", r.Title) : null,
        Selected(select, Dcterms + "description") && r.Description.Length > 0 ? new XElement(Dcterms + "description", r.Description) : null,
        Selected(select, Rm + "satisfiedBy") ? r.SatisfiedBy.Select(s => Ref(Rm + "satisfiedBy", s)) : null,
        Selected(select, Rm + "validatedBy") ? r.ValidatedBy.Select(s => Ref(Rm + "validatedBy", s)) : null,
        Selected(select, Rm + "elaboratedBy") ? r.ElaboratedBy.Select(s => Ref(Rm + "elaboratedBy", s)) : null,
        Selected(select, Core + "serviceProvider") ? Ref(Core + "serviceProvider", providerUrl) : null);

    public static XDocument RequirementRdf(Req r, string providerUrl, IReadOnlySet<string>? select = null) => Doc(RequirementXml(r, providerUrl, select));

    public static XDocument QueryRdf(string queryBase, string requestUrl, IReadOnlyList<Req> page, int total, string? nextPage, string providerUrl, IReadOnlySet<string>? select = null) => Doc(
        new XElement(Rdf + "Description", About(queryBase), page.Select(r => Ref(Rdfs + "member", r.Url))),
        new XElement(Core + "ResponseInfo", About(requestUrl),
            new XElement(Core + "totalCount", total),
            nextPage != null ? Ref(Core + "nextPage", nextPage) : null),
        page.Select(r => RequirementXml(r, providerUrl, select)));

    /// <summary>OSLC Compact (UI preview) for link hovers: title, short title, icon and small/large preview pages.</summary>
    public static XDocument Compact(Req r, string? smallPreview = null, string? largePreview = null, string? icon = null) =>
        Compact(r.Url, r.Title, r.Identifier, smallPreview, largePreview, icon);

    public static XDocument Compact(string about, string title, string shortTitle, string? smallPreview, string? largePreview, string? icon) => Doc(
        new XElement(Core + "Compact", About(about),
            new XElement(Dcterms + "title", title),
            new XElement(Core + "shortTitle", shortTitle),
            icon != null ? Ref(Core + "icon", icon) : null,
            smallPreview != null ? new XElement(Core + "smallPreview", new XElement(Core + "Preview",
                Ref(Core + "document", smallPreview), new XElement(Core + "hintWidth", "400px"), new XElement(Core + "hintHeight", "200px"))) : null,
            largePreview != null ? new XElement(Core + "largePreview", new XElement(Core + "Preview",
                Ref(Core + "document", largePreview), new XElement(Core + "hintWidth", "600px"), new XElement(Core + "hintHeight", "420px"))) : null));

    private static JsonObject Context() => new()
    {
        ["rdf"] = Rdf.NamespaceName, ["rdfs"] = Rdfs.NamespaceName, ["dcterms"] = Dcterms.NamespaceName,
        ["oslc"] = Core.NamespaceName, ["oslc_rm"] = Rm.NamespaceName,
    };

    private static JsonArray Ids(IEnumerable<string> urls) => new(urls.Select(s => (JsonNode)new JsonObject { ["@id"] = s }).ToArray());

    private static JsonObject RequirementJson(Req r, string providerUrl, IReadOnlySet<string>? select)
    {
        var o = new JsonObject { ["@id"] = r.Url, ["@type"] = "oslc_rm:Requirement" };
        if (Selected(select, Dcterms + "identifier")) o["dcterms:identifier"] = r.Identifier;
        if (Selected(select, Dcterms + "title")) o["dcterms:title"] = r.Title;
        if (Selected(select, Dcterms + "description") && r.Description.Length > 0) o["dcterms:description"] = r.Description;
        if (Selected(select, Rm + "satisfiedBy") && r.SatisfiedBy.Count > 0) o["oslc_rm:satisfiedBy"] = Ids(r.SatisfiedBy);
        if (Selected(select, Rm + "validatedBy") && r.ValidatedBy.Count > 0) o["oslc_rm:validatedBy"] = Ids(r.ValidatedBy);
        if (Selected(select, Rm + "elaboratedBy") && r.ElaboratedBy.Count > 0) o["oslc_rm:elaboratedBy"] = Ids(r.ElaboratedBy);
        if (Selected(select, Core + "serviceProvider")) o["oslc:serviceProvider"] = new JsonObject { ["@id"] = providerUrl };
        return o;
    }

    public static JsonObject RequirementJsonLd(Req r, string providerUrl, IReadOnlySet<string>? select = null)
    {
        var o = RequirementJson(r, providerUrl, select);
        var result = new JsonObject { ["@context"] = Context() };
        foreach (var (k, v) in o) result[k] = v?.DeepClone();
        return result;
    }

    public static JsonObject QueryJsonLd(string queryBase, string requestUrl, IReadOnlyList<Req> page, int total, string? nextPage, string providerUrl, IReadOnlySet<string>? select = null)
    {
        var info = new JsonObject { ["@id"] = requestUrl, ["@type"] = "oslc:ResponseInfo", ["oslc:totalCount"] = total };
        if (nextPage != null) info["oslc:nextPage"] = new JsonObject { ["@id"] = nextPage };
        return new JsonObject
        {
            ["@context"] = Context(),
            ["@id"] = queryBase,
            ["rdfs:member"] = new JsonArray(page.Select(r => (JsonNode)RequirementJson(r, providerUrl, select)).ToArray()),
            ["oslc:responseInfo"] = info,
        };
    }

    // ---------------------------------------------------------------- architecture elements (oslc_am:Resource)

    /// <summary>The OSLC AM view of a model element that is not a requirement, relationship or diagram.</summary>
    public sealed record AmRes(string Url, string Identifier, string Title, string Type, string Kind, string Description, IReadOnlyList<string> Satisfies, IReadOnlyList<string> Verifies, string WebUrl);

    public static bool IsAmResource(ModelRepository model, Element e) =>
        e is not Relationship && e.Kind is not (ElementKind.Requirement or ElementKind.Diagram) && !ReferenceEquals(e, model.Root) && e.Name.Length > 0;

    public static AmRes DescribeAm(ModelRepository model, Urls u, Guid projectId, Element e) => new(
        u.AmResource(projectId, e.Id), e.Id, e.DisplayName, Metamodel.DisplayName(e.Kind), e.Kind.ToString(), e.Documentation,
        model.Outgoing(e, ElementKind.Satisfy).Select(r => u.Resource(projectId, r.Target)).Distinct().ToList(),
        model.Outgoing(e, ElementKind.Verify).Select(r => u.Resource(projectId, r.Target)).Distinct().ToList(),
        u.Element(projectId, e.Id));

    public static List<AmRes> AmResources(ModelRepository model, Urls u, Guid projectId) =>
        model.AllElements.Where(e => IsAmResource(model, e)).Select(e => DescribeAm(model, u, projectId, e))
            .OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();

    private static XElement AmXml(AmRes r, string providerUrl, IReadOnlySet<string>? select) => new(Am + "Resource", About(r.Url),
        Selected(select, Dcterms + "identifier") ? new XElement(Dcterms + "identifier", r.Identifier) : null,
        Selected(select, Dcterms + "title") ? new XElement(Dcterms + "title", r.Title) : null,
        Selected(select, Dcterms + "description") && r.Description.Length > 0 ? new XElement(Dcterms + "description", r.Description) : null,
        Selected(select, Dcterms + "type") ? new XElement(Dcterms + "type", r.Type) : null,
        Selected(select, Mrd + "kind") ? new XElement(Mrd + "kind", r.Kind) : null,
        Selected(select, Mrd + "satisfies") ? r.Satisfies.Select(s => Ref(Mrd + "satisfies", s)) : null,
        Selected(select, Mrd + "verifies") ? r.Verifies.Select(s => Ref(Mrd + "verifies", s)) : null,
        Selected(select, Core + "serviceProvider") ? Ref(Core + "serviceProvider", providerUrl) : null);

    public static XDocument AmRdf(AmRes r, string providerUrl, IReadOnlySet<string>? select = null) => Doc(AmXml(r, providerUrl, select));

    public static XDocument AmQueryRdf(string queryBase, string requestUrl, IReadOnlyList<AmRes> page, int total, string? nextPage, string providerUrl, IReadOnlySet<string>? select = null) => Doc(
        new XElement(Rdf + "Description", About(queryBase), page.Select(r => Ref(Rdfs + "member", r.Url))),
        new XElement(Core + "ResponseInfo", About(requestUrl),
            new XElement(Core + "totalCount", total),
            nextPage != null ? Ref(Core + "nextPage", nextPage) : null),
        page.Select(r => AmXml(r, providerUrl, select)));

    // ---------------------------------------------------------------- errors

    public static XDocument Error(int status, string message) => Doc(
        new XElement(Core + "Error",
            new XElement(Core + "statusCode", status),
            new XElement(Core + "message", message)));

    // ---------------------------------------------------------------- query

    /// <summary>Values of one property IRI of a requirement, for oslc.where and oslc.orderBy.</summary>
    public static IReadOnlyList<string> Values(Req r, string property) => property switch
    {
        _ when property == Dcterms.NamespaceName + "identifier" => [r.Identifier],
        _ when property == Dcterms.NamespaceName + "title" => [r.Title],
        _ when property == Dcterms.NamespaceName + "description" => [r.Description],
        _ when property == Rm.NamespaceName + "satisfiedBy" => r.SatisfiedBy,
        _ when property == Rm.NamespaceName + "validatedBy" => r.ValidatedBy,
        _ when property == Rm.NamespaceName + "elaboratedBy" => r.ElaboratedBy,
        _ when property == Rdf.NamespaceName + "type" => [Rm.NamespaceName + "Requirement"],
        _ when property == Rdf.NamespaceName + "about" => [r.Url],
        _ => [],
    };

    public static IReadOnlyList<string> Values(AmRes r, string property) => property switch
    {
        _ when property == Dcterms.NamespaceName + "identifier" => [r.Identifier],
        _ when property == Dcterms.NamespaceName + "title" => [r.Title],
        _ when property == Dcterms.NamespaceName + "description" => [r.Description],
        _ when property == Dcterms.NamespaceName + "type" => [r.Type],
        _ when property == Mrd.NamespaceName + "kind" => [r.Kind],
        _ when property == Mrd.NamespaceName + "satisfies" => r.Satisfies,
        _ when property == Mrd.NamespaceName + "verifies" => r.Verifies,
        _ when property == Rdf.NamespaceName + "type" => [Am.NamespaceName + "Resource"],
        _ when property == Rdf.NamespaceName + "about" => [r.Url],
        _ => [],
    };

    /// <summary>Case-insensitive match of every search term against identifier, title and description.</summary>
    public static bool MatchesSearch(string? searchTerms, params string[] fields)
    {
        if (string.IsNullOrWhiteSpace(searchTerms)) return true;
        var terms = Regex.Matches(searchTerms, @"""([^""]+)""|(\S+)").Select(x => x.Groups[1].Success ? x.Groups[1].Value : x.Groups[2].Value);
        return terms.All(t => fields.Any(f => f.Contains(t, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Applies <c>oslc.where</c> (see <see cref="OslcQuery"/>) and <c>oslc.searchTerms</c> (case-insensitive text
    /// match on identifier, title and description). Throws <see cref="OslcQueryException"/> for unsupported syntax.
    /// </summary>
    public static IEnumerable<Req> Filter(IEnumerable<Req> all, string? where, string? searchTerms, string? prefix = null, string? orderBy = null)
    {
        var prefixes = OslcQuery.Prefixes(prefix);
        var q = OslcQuery.Apply(all, OslcQuery.ParseWhere(where, prefixes), OslcQuery.ParseOrderBy(orderBy, prefixes), Values);
        return q.Where(r => MatchesSearch(searchTerms, r.Identifier, r.Title, r.Description)).ToList();
    }

    public static IEnumerable<AmRes> FilterAm(IEnumerable<AmRes> all, string? where, string? searchTerms, string? prefix = null, string? orderBy = null)
    {
        var prefixes = OslcQuery.Prefixes(prefix);
        var q = OslcQuery.Apply(all, OslcQuery.ParseWhere(where, prefixes), OslcQuery.ParseOrderBy(orderBy, prefixes), Values);
        return q.Where(r => MatchesSearch(searchTerms, r.Identifier, r.Title, r.Description, r.Type)).ToList();
    }

    /// <summary>All requirements of a model, in identifier order.</summary>
    public static List<Req> Requirements(ModelRepository model, Urls u, Guid projectId) =>
        model.OfKind(ElementKind.Requirement).Select(e => Describe(model, u, projectId, e))
            .OrderBy(r => r.Identifier, StringComparer.OrdinalIgnoreCase).ToList();

    // ---------------------------------------------------------------- creation (factory and dialog)

    /// <summary>What a client asked to create: title is required, identifier and description optional.</summary>
    public sealed record NewRequirement(string Title, string? Identifier, string? Description);

    /// <summary>Reads an oslc_rm:Requirement from an RDF/XML or JSON(-LD) creation request body.</summary>
    public static NewRequirement ParseCreation(string body, string? contentType, string baseUri)
    {
        string? title, id, description;
        if (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            JsonNode? n;
            try { n = JsonNode.Parse(body); }
            catch (System.Text.Json.JsonException ex) { throw new RdfFormatException($"Not JSON: {ex.Message}"); }
            if (n?["@graph"] is JsonArray graph) n = graph.FirstOrDefault();
            static string? Str(JsonNode? v) => v switch
            {
                JsonValue jv => jv.ToString(),
                JsonObject o when o["@value"] is { } val => val.ToString(),
                JsonArray a => Str(a.FirstOrDefault()),
                _ => null,
            };
            title = Str(n?["dcterms:title"] ?? n?[Dcterms.NamespaceName + "title"] ?? n?["title"]);
            id = Str(n?["dcterms:identifier"] ?? n?[Dcterms.NamespaceName + "identifier"] ?? n?["identifier"]);
            description = Str(n?["dcterms:description"] ?? n?[Dcterms.NamespaceName + "description"] ?? n?["description"]);
        }
        else
        {
            var g = RdfXmlParser.Parse(body, baseUri);
            var subject = g.OfType(Rm.NamespaceName + "Requirement").FirstOrDefault() ?? g.SubjectNodes.FirstOrDefault()
                          ?? throw new RdfFormatException("The request body has no oslc_rm:Requirement.");
            title = g.Value(subject, Dcterms.NamespaceName + "title");
            id = g.Value(subject, Dcterms.NamespaceName + "identifier");
            description = g.Value(subject, Dcterms.NamespaceName + "description");
        }
        title = HtmlText.ToPlain(title ?? "").Trim();
        if (title.Length == 0) throw new RdfFormatException("dcterms:title is required.");
        return new NewRequirement(title, string.IsNullOrWhiteSpace(id) ? null : id.Trim(), description is null ? null : HtmlText.ToPlain(description).Trim());
    }

    /// <summary>The package new requirements go to: <paramref name="packageId"/> if it is a package or the model, else the first package named "Requirements", else the root.</summary>
    public static Element CreationTarget(ModelRepository model, string? packageId) =>
        model.Find(packageId) is { } p && (p.Kind == ElementKind.Package || ReferenceEquals(p, model.Root)) ? p
        : model.OfKind(ElementKind.Package).FirstOrDefault(p => string.Equals(p.Name, "Requirements", StringComparison.OrdinalIgnoreCase)) ?? model.Root;

    /// <summary>Creates the requirement (one undo step). An identifier already in use is refused.</summary>
    public static Element Create(ModelRepository model, NewRequirement r, string? packageId)
    {
        if (r.Identifier is { } id && model.OfKind(ElementKind.Requirement).Any(e => string.Equals(e.Get(FieldKeys.RequirementId), id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A requirement with id {id} already exists.");
        using (model.History.Transaction("Create requirement (OSLC)"))
        {
            var e = model.Create(CreationTarget(model, packageId), ElementKind.Requirement, r.Title);
            if (r.Identifier is { } rid) model.Set(e, FieldKeys.RequirementId, rid);
            if (r.Description is { Length: > 0 } text) model.Set(e, FieldKeys.Text, text);
            return e;
        }
    }
}

/// <summary>Plain text from the XHTML that requirements tools store (DOORS Next primary text, Polarion descriptions).</summary>
public static partial class HtmlText
{
    public static string ToPlain(string html)
    {
        if (html.IndexOf('<') < 0 && html.IndexOf('&') < 0) return html.Trim();
        var s = BlockEnd().Replace(html, "\n");
        s = Tag().Replace(s, "");
        s = System.Net.WebUtility.HtmlDecode(s).Replace(' ', ' ');
        s = Spaces().Replace(s, " ");
        s = BlankLines().Replace(s, "\n\n");
        return string.Join("\n", s.Split('\n').Select(l => l.Trim())).Trim();
    }

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h[1-6]|/tr)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n\s*\n+")]
    private static partial Regex BlankLines();
}
