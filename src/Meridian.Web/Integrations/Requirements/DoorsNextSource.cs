using System.Net;
using System.Xml.Linq;

namespace Meridian.Web.Integrations.Requirements;

/// <summary>
/// IBM DOORS Next (ELM / Jazz RM) over OSLC RM 2.0: root services discovery, the service provider catalog (project
/// areas), modules, the requirement query capability (paged by <c>oslc:nextPage</c>) and link publishing by
/// GET + PUT with <c>If-Match</c>. Signs in with Jazz form login (session cookies kept in this instance only),
/// HTTP Basic, or a Jazz Authorization Server app password (sent as Basic).
/// </summary>
public sealed class DoorsNextSource : IRequirementsSource
{
    public const string AuthMsgHeader = "X-com-ibm-team-repository-web-auth-msg";
    private const string Accept = "application/rdf+xml";

    public const string JazzRm = "http://jazz.net/ns/rm#";
    public const string ModuleType = JazzRm + "Module";
    public static readonly string RmNs = Oslc.Rm.NamespaceName;
    private static readonly string Dc = Oslc.Dcterms.NamespaceName;
    private static readonly string CoreNs = Oslc.Core.NamespaceName;
    private static readonly string RdfsMember = Oslc.Rdfs.NamespaceName + "member";

    /// <summary>OSLC RM properties Meridian publishes its link kinds as.</summary>
    public static string LinkProperty(string kind) => kind switch
    {
        "satisfy" => RmNs + "satisfiedBy",
        "verify" => RmNs + "validatedBy",
        _ => RmNs + "elaboratedBy",
    };

    private readonly RequirementsToolConfig _config;
    private readonly ToolHttp _http;
    private readonly string _root;
    private readonly string _secret;
    public int MaxPages { get; init; } = 500;

    public DoorsNextSource(RequirementsToolConfig config, string secret, HttpClient http)
    {
        _config = config;
        _secret = secret;
        _root = config.BaseUrl.Trim().TrimEnd('/');
        _http = new ToolHttp(http, _root, "DOORS Next");
        if (config.Auth == JazzAuth.Form)
        {
            _http.NeedsLogin = r => r.Status == HttpStatusCode.Unauthorized
                || (r.Headers.TryGetValues(AuthMsgHeader, out var v) && v.Any(x => x.Equals("authrequired", StringComparison.OrdinalIgnoreCase)));
            _http.LoginAsync = FormLoginAsync;
        }
        else _http.Authorize = r => r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", ToolHttp.BasicAuth(config.User?.Trim() ?? "", secret));
    }

    /// <summary>The cookie jar of this instance (tests check that it stays private).</summary>
    internal CookieContainer Cookies => _http.Cookies;

    private Dictionary<string, string> Headers()
    {
        var h = new Dictionary<string, string> { ["OSLC-Core-Version"] = "2.0" };
        if (_config.ConfigurationContext is { Length: > 0 } cc) h["Configuration-Context"] = cc;
        return h;
    }

    private async Task FormLoginAsync(CancellationToken ct)
    {
        var authBase = (_config.AuthUrl is { Length: > 0 } a ? a : _root).TrimEnd('/');
        var form = $"j_username={Uri.EscapeDataString(_config.User?.Trim() ?? "")}&j_password={Uri.EscapeDataString(_secret)}";
        var r = await _http.SendRawAsync(HttpMethod.Post, authBase + "/j_security_check", "text/html,*/*", new ToolBody(form, "application/x-www-form-urlencoded"), ct: ct);
        if (r.Headers.TryGetValues(AuthMsgHeader, out var v) && v.Any(x => x.Equals("authfailed", StringComparison.OrdinalIgnoreCase)) || r.Status == HttpStatusCode.Unauthorized)
            throw new RequirementsToolException("DOORS Next rejected the username or password.", HttpStatusCode.Unauthorized);
    }

    private async Task<RdfGraph> GetRdfAsync(string url, CancellationToken ct)
    {
        var r = await _http.SendAsync(HttpMethod.Get, url, Accept, headers: Headers(), ct: ct);
        try { return RdfXmlParser.Parse(r.Body, r.FinalUrl); }
        catch (RdfFormatException) { throw new RequirementsToolException($"DOORS Next returned something that isn't RDF/XML from {new Uri(url).AbsolutePath} — check the server URL."); }
    }

    // ---------------------------------------------------------------- discovery

    /// <summary>The RM service provider catalog URL from <c>{root}/rootservices</c>.</summary>
    public async Task<string> CatalogUrlAsync(CancellationToken ct = default)
    {
        var g = await GetRdfAsync(_root + "/rootservices", ct);
        return g.Triples.FirstOrDefault(t => t.Predicate.Value == Oslc.RmRoot.NamespaceName + "rmServiceProviders" && t.Object is RdfIri)?.Object is RdfIri i
            ? i.Value
            : throw new RequirementsToolException("The root services document has no oslc_rm:rmServiceProviders — is the URL the DOORS Next (/rm) application?");
    }

    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var projects = await ProjectsAsync(ct);
        var current = projects.FirstOrDefault(p => p.Id == _config.Project.Trim());
        return current != null
            ? $"Connected to DOORS Next — project “{current.Title}”"
            : $"Connected to DOORS Next — {projects.Count} project{(projects.Count == 1 ? "" : "s")} visible";
    }

    public async Task<IReadOnlyList<RemoteContainer>> ProjectsAsync(CancellationToken ct = default)
    {
        var catalogUrl = await CatalogUrlAsync(ct);
        var g = await GetRdfAsync(catalogUrl, ct);
        var result = g.OfType(CoreNs + "ServiceProvider").OfType<RdfIri>()
            .Select(p => new RemoteContainer(p.Value, g.Value(p, Dc + "title") ?? p.Value)).ToList();
        // Catalogs may nest catalogs (one level is enough for Jazz).
        foreach (var nested in g.Triples.Where(t => t.Predicate.Value == CoreNs + "serviceProviderCatalog" && t.Object is RdfIri).Select(t => ((RdfIri)t.Object).Value).Distinct().Take(20))
        {
            var ng = await GetRdfAsync(nested, ct);
            result.AddRange(ng.OfType(CoreNs + "ServiceProvider").OfType<RdfIri>().Select(p => new RemoteContainer(p.Value, ng.Value(p, Dc + "title") ?? p.Value)));
        }
        return result.DistinctBy(p => p.Id).OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The query base of the provider's capability for <paramref name="resourceType"/> (falls back to the requirement one).</summary>
    private async Task<string> QueryBaseAsync(string provider, string resourceType, CancellationToken ct)
    {
        var g = await GetRdfAsync(provider, ct);
        var caps = g.Triples.Where(t => t.Predicate.Value == CoreNs + "queryCapability").Select(t => t.Object).ToList();
        string? Base(string type) => caps.Where(c => g.Iris(c, CoreNs + "resourceType").Contains(type)).Select(c => g.Iris(c, CoreNs + "queryBase").FirstOrDefault()).FirstOrDefault(b => b != null);
        return Base(resourceType) ?? Base(RmNs + "Requirement") ?? caps.Select(c => g.Iris(c, CoreNs + "queryBase").FirstOrDefault()).FirstOrDefault(b => b != null)
               ?? throw new RequirementsToolException("The DOORS Next project has no OSLC query capability.");
    }

    public async Task<IReadOnlyList<RemoteContainer>> ScopesAsync(string project, CancellationToken ct = default)
    {
        var queryBase = await QueryBaseAsync(project, RmNs + "RequirementCollection", ct);
        var modules = new List<RemoteContainer>();
        await foreach (var (g, node) in QueryAsync(queryBase, $"rdf:type=<{ModuleType}>", "dcterms:title", ct))
            if (node is RdfIri i) modules.Add(new RemoteContainer(i.Value, g.Value(node, Dc + "title") ?? i.Value));
        return modules.DistinctBy(m => m.Id).OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------------------------------------------------------------- requirements

    /// <summary>Pages through a query capability; yields each member resource with the page graph it came in.</summary>
    private async IAsyncEnumerable<(RdfGraph Graph, RdfNode Node)> QueryAsync(string queryBase, string? where, string select, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var sep = queryBase.Contains('?') ? "&" : "?";
        var url = $"{queryBase}{sep}oslc.paging=true&oslc.pageSize=100"
                  + $"&oslc.prefix={Uri.EscapeDataString($"dcterms=<{Dc}>,oslc_rm=<{RmNs}>,rdf=<{RdfGraph.RdfNs}>,jazz_rm=<{JazzRm}>")}"
                  + $"&oslc.select={Uri.EscapeDataString(select)}"
                  + (string.IsNullOrWhiteSpace(where) ? "" : $"&oslc.where={Uri.EscapeDataString(where)}");
        var seen = new HashSet<string>();
        for (var page = 0; url != null && page < MaxPages; page++)
        {
            if (!seen.Add(url)) yield break;   // a server that returns the same next page forever
            var g = await GetRdfAsync(url, ct);
            var members = g.Triples.Where(t => t.Predicate.Value == RdfsMember).Select(t => t.Object).Distinct().ToList();
            if (members.Count == 0) members = g.OfType(RmNs + "Requirement").Concat(g.OfType(ModuleType)).Distinct().ToList();   // servers that list results without rdfs:member
            foreach (var m in members) yield return (g, m);
            url = g.Triples.FirstOrDefault(t => t.Predicate.Value == CoreNs + "nextPage" && t.Object is RdfIri)?.Object is RdfIri next ? next.Value : null;
        }
    }

    private static RemoteRequirement? Read(RdfGraph g, RdfNode node)
    {
        if (node is not RdfIri iri) return null;
        var types = g.Iris(node, RdfGraph.RdfType).ToList();
        if (types.Contains(ModuleType) || types.Contains(RmNs + "RequirementCollection")) return null;
        var primary = g.Objects(node, JazzRm + "primaryText").Select(RdfGraph.Text).FirstOrDefault();
        var text = HtmlText.ToPlain(primary is { Length: > 0 } ? primary : g.Value(node, Dc + "description") ?? "");
        var title = HtmlText.ToPlain(g.Value(node, Dc + "title") ?? "");
        var id = g.Value(node, Dc + "identifier") ?? "";
        return new RemoteRequirement(iri.Value, iri.Value, id, title.Length > 0 ? title : id, text, g.Value(node, Dc + "modified") ?? "");
    }

    public async Task<IReadOnlyList<RemoteRequirement>> RequirementsAsync(CancellationToken ct = default)
    {
        var list = new List<RemoteRequirement>();
        if (_config.Scope is { Length: > 0 } module)
        {
            // A module lists its requirements with oslc_rm:uses; each is read on its own (4 at a time).
            var mg = await GetRdfAsync(module, ct);
            var uses = mg.Iris(new RdfIri(module), RmNs + "uses").Distinct().ToList();
            if (uses.Count == 0) uses = mg.Triples.Where(t => t.Predicate.Value == RmNs + "uses" && t.Object is RdfIri).Select(t => ((RdfIri)t.Object).Value).Distinct().ToList();
            var results = new RemoteRequirement?[uses.Count];
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(uses.Select(async (u, i) =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var g = await GetRdfAsync(u, ct);
                    results[i] = Read(g, new RdfIri(u));
                }
                finally { gate.Release(); }
            }));
            list.AddRange(results.Where(r => r != null)!);
            return list;
        }
        var queryBase = await QueryBaseAsync(_config.Project.Trim(), RmNs + "Requirement", ct);
        await foreach (var (g, node) in QueryAsync(queryBase, _config.Query, "dcterms:identifier,dcterms:title,dcterms:description,dcterms:modified,jazz_rm:primaryText,rdf:type", ct))
            if (Read(g, node) is { } r) list.Add(r);
        return list.DistinctBy(r => r.ExternalId).ToList();
    }

    // ---------------------------------------------------------------- links

    public async Task<PublishOutcome> PublishLinkAsync(string externalId, OutboundLink link, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var current = await _http.SendAsync(HttpMethod.Get, externalId, Accept, headers: Headers(), ct: ct);
            XDocument doc;
            try { doc = XDocument.Parse(current.Body); }
            catch (System.Xml.XmlException) { throw new RequirementsToolException("DOORS Next returned a requirement that isn't RDF/XML."); }
            var property = LinkProperty(link.Kind);
            var g = RdfXmlParser.Parse(doc, current.FinalUrl);
            if (g.Iris(new RdfIri(externalId), property).Contains(link.TargetUri)) return PublishOutcome.AlreadyPresent;

            var subject = doc.Descendants().FirstOrDefault(e => (string?)e.Attribute(Oslc.Rdf + "about") == externalId)
                          ?? throw new RequirementsToolException("The DOORS Next requirement document does not describe the requirement.");
            subject.Add(new XElement(Oslc.Rm + property[RmNs.Length..], new XAttribute(Oslc.Rdf + "resource", link.TargetUri)));
            if (subject.Document?.Root is { } root && root.GetNamespaceOfPrefix("oslc_rm") is null && root.GetPrefixOfNamespace(Oslc.Rm) is null)
                root.Add(new XAttribute(XNamespace.Xmlns + "oslc_rm", Oslc.Rm.NamespaceName));

            var headers = Headers();
            if (current.ETag is { Length: > 0 } etag) headers["If-Match"] = etag;
            var put = await _http.SendAsync(HttpMethod.Put, externalId, Accept, new ToolBody(doc.ToString(SaveOptions.DisableFormatting), "application/rdf+xml"), headers, ct,
                allowStatus: s => s == HttpStatusCode.PreconditionFailed && attempt == 0);
            if (put.Status == HttpStatusCode.PreconditionFailed) continue;   // edited meanwhile: read it again once
            return PublishOutcome.Created;
        }
    }
}
