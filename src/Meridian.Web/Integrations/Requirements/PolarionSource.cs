using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meridian.Web.Integrations.Requirements;

/// <summary>
/// Siemens Polarion over its REST API (<c>/polarion/rest/v1</c>, JSON:API) with a personal access token (Bearer):
/// projects, documents (found through the <c>module</c> relationship of work items), work items of one type (default
/// <c>requirement</c>) paged by <c>page[number]</c> / <c>links.next</c>, and link publishing as linked work items
/// (both ends in Polarion) or linked OSLC resources (the other end is a Meridian element).
/// </summary>
public sealed class PolarionSource : IRequirementsSource
{
    /// <summary>Items per page (Polarion allows up to 100).</summary>
    public int PageSize { get; init; } = 100;
    private const string Accept = "application/json";

    private readonly RequirementsToolConfig _config;
    private readonly ToolHttp _http;
    private readonly string _server;
    public int MaxPages { get; init; } = 500;

    public PolarionSource(RequirementsToolConfig config, string token, HttpClient http)
    {
        _config = config;
        var root = config.BaseUrl.Trim().TrimEnd('/');
        if (root.EndsWith("/rest/v1", StringComparison.OrdinalIgnoreCase)) root = root[..^"/rest/v1".Length];
        if (root.EndsWith("/polarion", StringComparison.OrdinalIgnoreCase)) root = root[..^"/polarion".Length];
        _server = root;
        _http = new ToolHttp(http, root, "Polarion") { Authorize = r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token) };
    }

    public string Api => _server + "/polarion/rest/v1";
    private string ProjectId => _config.Project.Trim();
    private string WorkItemType => _config.WorkItemType is { Length: > 0 } t ? t.Trim() : "requirement";

    /// <summary>The browser URL of a work item.</summary>
    public string WebUrl(string project, string workItemId) => $"{_server}/polarion/#/project/{Uri.EscapeDataString(project)}/workitem?id={Uri.EscapeDataString(workItemId)}";

    private async Task<JsonNode?> GetAsync(string url, CancellationToken ct)
    {
        var r = await _http.SendAsync(HttpMethod.Get, url, Accept, ct: ct);
        return Parse(r.Body);
    }

    private static JsonNode? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonNode.Parse(body); }
        catch (JsonException) { throw new RequirementsToolException("Polarion returned something that isn't JSON — check the server URL."); }
    }

    private static string Str(JsonNode? n) => n?.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n?.ToString() ?? "";

    /// <summary>Follows JSON:API paging: <c>page[number]</c> until <c>links.next</c> is absent or a page comes back short.</summary>
    private async IAsyncEnumerable<(JsonNode Item, JsonArray? Included)> PagesAsync(string url, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? next = url + (url.Contains('?') ? "&" : "?") + $"page%5Bsize%5D={PageSize}&page%5Bnumber%5D=1";
        var seen = new HashSet<string>();
        for (var page = 0; next != null && page < MaxPages; page++)
        {
            if (!seen.Add(next)) yield break;
            var doc = await GetAsync(next, ct);
            var data = doc?["data"] as JsonArray ?? [];
            var included = doc?["included"] as JsonArray;
            foreach (var item in data) if (item != null) yield return (item, included);
            var link = Str(doc?["links"]?["next"]);
            next = link.Length == 0 || data.Count < PageSize ? null
                : Uri.TryCreate(link, UriKind.Absolute, out _) ? link : _server + (link.StartsWith('/') ? "" : "/") + link;
        }
    }

    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        if (ProjectId.Length > 0)
        {
            var p = await GetAsync($"{Api}/projects/{Uri.EscapeDataString(ProjectId)}?fields%5Bprojects%5D=name", ct);
            var name = Str(p?["data"]?["attributes"]?["name"]);
            return $"Connected to Polarion project “{(name.Length > 0 ? name : ProjectId)}”";
        }
        var projects = await ProjectsAsync(ct);
        return $"Connected to Polarion — {projects.Count} project{(projects.Count == 1 ? "" : "s")} visible";
    }

    public async Task<IReadOnlyList<RemoteContainer>> ProjectsAsync(CancellationToken ct = default)
    {
        var list = new List<RemoteContainer>();
        await foreach (var (p, _) in PagesAsync($"{Api}/projects?fields%5Bprojects%5D=name", ct))
            list.Add(new RemoteContainer(Str(p["id"]), Str(p["attributes"]?["name"]) is { Length: > 0 } n ? n : Str(p["id"])));
        return list.OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Documents that hold work items of the configured type (the work items' <c>module</c> relationship, included).</summary>
    public async Task<IReadOnlyList<RemoteContainer>> ScopesAsync(string project, CancellationToken ct = default)
    {
        var docs = new Dictionary<string, string>();
        var url = $"{Api}/projects/{Uri.EscapeDataString(project.Trim())}/workitems?query={Uri.EscapeDataString(Lucene())}&fields%5Bworkitems%5D=module&fields%5Bdocuments%5D=title,moduleName,moduleFolder&include=module";
        await foreach (var (item, included) in PagesAsync(url, ct))
        {
            var id = Str(item["relationships"]?["module"]?["data"]?["id"]);
            if (id.Length == 0 || docs.ContainsKey(id)) continue;
            var doc = included?.FirstOrDefault(d => Str(d?["type"]) == "documents" && Str(d?["id"]) == id);
            var title = Str(doc?["attributes"]?["title"]);
            if (title.Length == 0) title = Str(doc?["attributes"]?["moduleName"]);
            docs[id] = title.Length > 0 ? $"{title} ({id.Split('/').Skip(1).FirstOrDefault()})" : id;
        }
        return docs.Select(kv => new RemoteContainer(kv.Key, kv.Value)).OrderBy(d => d.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private string Lucene() => $"type:{WorkItemType}" + (_config.Query is { Length: > 0 } q ? $" AND ({q})" : "");

    public async Task<IReadOnlyList<RemoteRequirement>> RequirementsAsync(CancellationToken ct = default)
    {
        var list = new List<RemoteRequirement>();
        var url = $"{Api}/projects/{Uri.EscapeDataString(ProjectId)}/workitems?query={Uri.EscapeDataString(Lucene())}&fields%5Bworkitems%5D=title,description,type,status,updated,module";
        await foreach (var (item, _) in PagesAsync(url, ct))
        {
            var full = Str(item["id"]);
            if (_config.Scope is { Length: > 0 } doc && Str(item["relationships"]?["module"]?["data"]?["id"]) != doc) continue;
            var a = item["attributes"];
            var shortId = full.Contains('/') ? full[(full.IndexOf('/') + 1)..] : full;
            var desc = a?["description"];
            var text = desc is JsonObject o ? Str(o["value"]) : Str(desc);
            if (desc is JsonObject d && Str(d["type"]).Contains("html", StringComparison.OrdinalIgnoreCase)) text = HtmlText.ToPlain(text);
            var title = Str(a?["title"]).Trim();
            list.Add(new RemoteRequirement(full, WebUrl(ProjectId, shortId), shortId, title.Length > 0 ? title : shortId, text.Trim(), Str(a?["updated"])));
        }
        return list;
    }

    public async Task<PublishOutcome> PublishLinkAsync(string externalId, OutboundLink link, CancellationToken ct = default)
    {
        var roles = _config.LinkRoles ?? new PolarionLinkRoles();
        var slash = externalId.IndexOf('/');
        if (slash <= 0) throw new RequirementsToolException($"“{externalId}” is not a Polarion work item id.");
        var (project, wi) = (externalId[..slash], externalId[(slash + 1)..]);
        var basePath = $"{Api}/projects/{Uri.EscapeDataString(project)}/workitems/{Uri.EscapeDataString(wi)}";
        JsonObject body;
        string url;
        if (link.TargetExternalId is { Length: > 0 } target)
        {
            url = basePath + "/linkedworkitems";
            var role = link.Kind switch { "satisfy" => roles.Satisfy, "verify" => roles.Verify, _ => roles.Trace };
            body = new JsonObject
            {
                ["data"] = new JsonArray(new JsonObject
                {
                    ["type"] = "linkedworkitems",
                    ["attributes"] = new JsonObject { ["role"] = role, ["suspect"] = false },
                    ["relationships"] = new JsonObject { ["workItem"] = new JsonObject { ["data"] = new JsonObject { ["type"] = "workitems", ["id"] = target } } },
                }),
            };
        }
        else
        {
            url = basePath + "/linkedoslcresources";
            var role = link.Kind switch { "satisfy" => roles.OslcSatisfy, "verify" => roles.OslcVerify, _ => roles.OslcTrace };
            body = new JsonObject
            {
                ["data"] = new JsonArray(new JsonObject
                {
                    ["type"] = "linkedoslcresources",
                    ["attributes"] = new JsonObject { ["role"] = role, ["uri"] = link.TargetUri, ["label"] = link.Label },
                }),
            };
        }
        var r = await _http.SendAsync(HttpMethod.Post, url, Accept, new ToolBody(body.ToJsonString(), "application/json"), ct: ct,
            allowStatus: s => s == HttpStatusCode.Conflict);
        return r.Status == HttpStatusCode.Conflict ? PublishOutcome.AlreadyPresent : PublishOutcome.Created;
    }
}
