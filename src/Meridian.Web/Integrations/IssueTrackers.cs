using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Meridian.Web.Integrations;

/// <summary>Supported issue trackers.</summary>
public static class TrackerTypes
{
    public const string JiraCloud = "jira-cloud";       // Jira Cloud REST v3, email + API token (Basic)
    public const string JiraServer = "jira-server";     // Jira Server / Data Center REST v2, personal access token (Bearer)
    public const string AzureDevOps = "azure-devops";   // Azure DevOps work items, PAT (Basic with empty user)
    public const string GitHub = "github";              // GitHub Issues (github.com or Enterprise), token (Bearer)
    public const string GitLab = "gitlab";              // GitLab Issues (gitlab.com or self-managed), PRIVATE-TOKEN or OAuth Bearer

    public static readonly string[] All = [JiraCloud, JiraServer, AzureDevOps, GitHub, GitLab];

    public static string Display(string type) => type switch
    {
        JiraCloud => "Jira Cloud",
        JiraServer => "Jira Server / Data Center",
        AzureDevOps => "Azure DevOps",
        GitHub => "GitHub Issues",
        GitLab => "GitLab Issues",
        _ => type,
    };

    public static string ProjectLabel(string type) => type switch
    {
        AzureDevOps => "Project",
        GitHub => "Repository (owner/repo)",
        GitLab => "Project (group/project path or numeric id)",
        _ => "Project key",
    };

    public static string BaseUrlHint(string type) => type switch
    {
        JiraCloud => "https://your-site.atlassian.net",
        JiraServer => "https://jira.example.com",
        AzureDevOps => "https://dev.azure.com/your-organization",
        GitHub => "https://api.github.com (or https://ghe.example.com/api/v3)",
        GitLab => "https://gitlab.com (or https://gitlab.example.com)",
        _ => "",
    };

    public static bool NeedsUser(string type) => type == JiraCloud;
}

/// <summary>Non-secret settings of an issue tracker connection (stored as the integration's config JSON).</summary>
public sealed class IssueTrackerConfig
{
    public string Type { get; set; } = TrackerTypes.JiraCloud;
    public string BaseUrl { get; set; } = "";
    /// <summary>Jira project key, Azure DevOps project, GitHub "owner/repo", or GitLab "group/project" path or numeric id.</summary>
    public string Project { get; set; } = "";
    /// <summary>Jira Cloud account email (Basic auth user).</summary>
    public string? User { get; set; }
    /// <summary>Jira issue type / Azure DevOps work item type for created issues ("Task" by default). GitLab: optional
    /// <c>issue_type</c> (issue, incident, test_case or task; GitLab's default "issue" when empty).</summary>
    public string? IssueType { get; set; }
    /// <summary>GitLab: comma-separated labels added to created issues (e.g. "meridian, requirement").</summary>
    public string? Labels { get; set; }
    /// <summary>GitLab: send the token as <c>Authorization: Bearer</c> (an OAuth access token) instead of <c>PRIVATE-TOKEN</c>
    /// (personal, project or group access token).</summary>
    public bool? BearerAuth { get; set; }

    public static IssueTrackerConfig Parse(string json) =>
        JsonSerializer.Deserialize<IssueTrackerConfig>(json, IntegrationJson.Options) ?? new IssueTrackerConfig();

    public string ToJson() => JsonSerializer.Serialize(this, IntegrationJson.Options);

    /// <summary>Validates the settings; returns an error message or null.</summary>
    public string? Validate()
    {
        if (!TrackerTypes.All.Contains(Type)) return "Choose a tracker type.";
        if (!Uri.TryCreate(BaseUrl?.Trim(), UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) || u.Host.Length == 0)
            return "The base URL must be an absolute http:// or https:// URL.";
        if (string.IsNullOrWhiteSpace(Project)) return $"Enter the {TrackerTypes.ProjectLabel(Type).ToLowerInvariant()}.";
        if (Type == TrackerTypes.GitHub && Project.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries).Length != 2) return "The repository must look like owner/repo.";
        if (Type == TrackerTypes.GitLab && !GitLabTracker.IsProjectRef(Project.Trim())) return "The project must be a path like group/project (subgroups allowed) or a numeric project id.";
        if (Type == TrackerTypes.GitLab && IssueType is { Length: > 0 } it && !GitLabTracker.IssueTypes.Contains(it.Trim().ToLowerInvariant()))
            return $"GitLab issue types are {string.Join(", ", GitLabTracker.IssueTypes)}.";
        if (TrackerTypes.NeedsUser(Type) && string.IsNullOrWhiteSpace(User)) return "Enter the Atlassian account email.";
        return null;
    }
}

public static class IntegrationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
}

/// <summary>An issue as shown in Meridian: key, title, status and assignee, plus whether the tracker considers it done.
/// <see cref="Labels"/> is filled by trackers that report labels (GitLab); null otherwise.</summary>
public sealed record IssueInfo(string Key, string Url, string Title, string Status, string? Assignee, bool Closed)
{
    public string[]? Labels { get; init; }
}

/// <summary>A failure talking to an issue tracker. The message never contains credentials.</summary>
public sealed class IssueTrackerException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>What Meridian needs from an issue tracker.</summary>
public interface IIssueTracker
{
    /// <summary>Checks credentials and that the project/repository is reachable. Returns a short description.</summary>
    Task<string> TestAsync(CancellationToken ct = default);
    Task<IReadOnlyList<IssueInfo>> SearchAsync(string text, CancellationToken ct = default);
    Task<IssueInfo> GetAsync(string key, CancellationToken ct = default);
    Task<IssueInfo> CreateAsync(string title, string description, string backLink, CancellationToken ct = default);
    /// <summary>Extracts an issue key from a pasted key or browser URL, or null if it isn't one of this tracker's.</summary>
    string? ParseKey(string keyOrUrl);
}

/// <summary>Builds tracker clients over a supplied <see cref="HttpClient"/> (tests pass one with a fake handler).</summary>
public static class IssueTrackerFactory
{
    public static IIssueTracker Create(IssueTrackerConfig config, string secret, HttpClient http) => config.Type switch
    {
        TrackerTypes.JiraCloud => new JiraTracker(config, secret, http, cloud: true),
        TrackerTypes.JiraServer => new JiraTracker(config, secret, http, cloud: false),
        TrackerTypes.AzureDevOps => new AzureDevOpsTracker(config, secret, http),
        TrackerTypes.GitHub => new GitHubTracker(config, secret, http),
        TrackerTypes.GitLab => new GitLabTracker(config, secret, http),
        _ => throw new IssueTrackerException($"Unknown tracker type '{config.Type}'."),
    };
}

/// <summary>Shared HTTP plumbing: authenticated JSON requests with credential-free error messages.</summary>
public abstract class TrackerBase(IssueTrackerConfig config, HttpClient http)
{
    protected IssueTrackerConfig Config { get; } = config;
    protected string Base { get; } = config.BaseUrl.Trim().TrimEnd('/');

    protected abstract void Authorize(HttpRequestMessage request);

    protected async Task<JsonNode?> SendAsync(HttpMethod method, string url, JsonNode? body = null, CancellationToken ct = default, string contentType = "application/json") =>
        (await SendWithHeadersAsync(method, url, body, ct, contentType)).Json;

    /// <summary>Like <see cref="SendAsync"/>, also returning the response headers (for paging).</summary>
    protected async Task<(JsonNode? Json, HttpResponseHeaders Headers)> SendWithHeadersAsync(HttpMethod method, string url, JsonNode? body = null, CancellationToken ct = default, string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Meridian", "1.0"));
        Authorize(request);
        if (body != null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType) { CharSet = "utf-8" };
        }
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (HttpRequestException ex) { throw new IssueTrackerException($"Could not reach {new Uri(url).Host}: {ex.Message}"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new IssueTrackerException($"{new Uri(url).Host} did not answer in time."); }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var hint = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "the credentials were rejected",
                    HttpStatusCode.Forbidden => "the token lacks permission",
                    HttpStatusCode.NotFound => "not found",
                    _ => Summarize(text),
                };
                throw new IssueTrackerException($"{(int)response.StatusCode} {response.ReasonPhrase}: {hint}", response.StatusCode);
            }
            if (string.IsNullOrWhiteSpace(text)) return (null, response.Headers);
            try { return (JsonNode.Parse(text), response.Headers); }
            catch (JsonException) { throw new IssueTrackerException("The tracker returned something that isn't JSON — check the base URL."); }
        }
    }

    private static string Summarize(string text)
    {
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 200 ? text[..200] + "…" : text;
    }

    protected static string Str(JsonNode? n) => n?.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n?.ToString() ?? "";

    protected static string BasicAuth(string user, string secret) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}"));
}

// ==================================================================================== Jira

public sealed class JiraTracker(IssueTrackerConfig config, string secret, HttpClient http, bool cloud) : TrackerBase(config, http), IIssueTracker
{
    private string Api => $"{Base}/rest/api/{(cloud ? "3" : "2")}";
    private string ProjectKey => Config.Project.Trim();
    private const string Fields = "summary,status,assignee";

    protected override void Authorize(HttpRequestMessage request) =>
        request.Headers.Authorization = cloud
            ? new AuthenticationHeaderValue("Basic", BasicAuth(Config.User?.Trim() ?? "", secret))
            : new AuthenticationHeaderValue("Bearer", secret);

    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var p = await SendAsync(HttpMethod.Get, $"{Api}/project/{Uri.EscapeDataString(ProjectKey)}", ct: ct);
        return $"Connected to Jira project {Str(p?["key"])} — {Str(p?["name"])}";
    }

    public async Task<IReadOnlyList<IssueInfo>> SearchAsync(string text, CancellationToken ct = default)
    {
        text = text.Trim();
        var key = ParseKey(text);
        var jql = key != null
            ? $"key = \"{key}\""
            : $"project = \"{Escape(ProjectKey)}\"{(text.Length > 0 ? $" AND text ~ \"{Escape(text)}\"" : "")} ORDER BY updated DESC";
        // Cloud: /search/jql (the classic /search was retired); Server/DC: /search.
        var url = $"{Api}/{(cloud ? "search/jql" : "search")}?jql={Uri.EscapeDataString(jql)}&fields={Fields}&maxResults=20";
        try
        {
            var r = await SendAsync(HttpMethod.Get, url, ct: ct);
            return (r?["issues"]?.AsArray() ?? []).Select(Read).ToList();
        }
        catch (IssueTrackerException ex) when (key != null && ex.Status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            return [];
        }
    }

    public async Task<IssueInfo> GetAsync(string key, CancellationToken ct = default) =>
        Read(await SendAsync(HttpMethod.Get, $"{Api}/issue/{Uri.EscapeDataString(key)}?fields={Fields}", ct: ct));

    public async Task<IssueInfo> CreateAsync(string title, string description, string backLink, CancellationToken ct = default)
    {
        JsonNode desc = cloud
            ? new JsonObject   // Atlassian Document Format
            {
                ["type"] = "doc", ["version"] = 1,
                ["content"] = new JsonArray(Paragraphs(description).Select(p => (JsonNode)new JsonObject
                {
                    ["type"] = "paragraph", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = p }),
                }).Append(new JsonObject
                {
                    ["type"] = "paragraph",
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text", ["text"] = "Open in Meridian",
                        ["marks"] = new JsonArray(new JsonObject { ["type"] = "link", ["attrs"] = new JsonObject { ["href"] = backLink } }),
                    }),
                }).ToArray()),
            }
            : JsonValue.Create($"{description}\n\n[Open in Meridian|{backLink}]")!;
        var body = new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["project"] = new JsonObject { ["key"] = ProjectKey },
                ["summary"] = Truncate(title, 250),
                ["issuetype"] = new JsonObject { ["name"] = string.IsNullOrWhiteSpace(Config.IssueType) ? "Task" : Config.IssueType.Trim() },
                ["description"] = desc,
            },
        };
        var created = await SendAsync(HttpMethod.Post, $"{Api}/issue", body, ct);
        var key = Str(created?["key"]);
        if (key.Length == 0) throw new IssueTrackerException("Jira did not return the new issue's key.");
        try { return await GetAsync(key, ct); }
        catch (IssueTrackerException) { return new IssueInfo(key, BrowseUrl(key), title, "Created", null, false); }
    }

    public string? ParseKey(string keyOrUrl)
    {
        var m = Regex.Match(keyOrUrl.Trim(), @"(?:/browse/|selectedIssue=|^)([A-Za-z][A-Za-z0-9_]*-\d+)(?:$|[/?#&])");
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    private string BrowseUrl(string key) => $"{Base}/browse/{key}";

    private IssueInfo Read(JsonNode? issue)
    {
        var key = Str(issue?["key"]);
        var f = issue?["fields"];
        return new IssueInfo(key, BrowseUrl(key), Str(f?["summary"]), Str(f?["status"]?["name"]),
            f?["assignee"]?["displayName"] is { } a ? Str(a) : null,
            Str(f?["status"]?["statusCategory"]?["key"]) == "done");
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    internal static IEnumerable<string> Paragraphs(string text) =>
        text.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).DefaultIfEmpty("(no description)");
    internal static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}

// ==================================================================================== Azure DevOps

public sealed class AzureDevOpsTracker(IssueTrackerConfig config, string secret, HttpClient http) : TrackerBase(config, http), IIssueTracker
{
    private const string Version = "api-version=7.1";
    private string Project => Uri.EscapeDataString(Config.Project.Trim());
    private static readonly string[] ClosedStates = ["Closed", "Done", "Removed", "Resolved", "Completed"];

    protected override void Authorize(HttpRequestMessage request) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuth("", secret));

    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var p = await SendAsync(HttpMethod.Get, $"{Base}/_apis/projects/{Project}?{Version}", ct: ct);
        return $"Connected to Azure DevOps project {Str(p?["name"])}";
    }

    public async Task<IReadOnlyList<IssueInfo>> SearchAsync(string text, CancellationToken ct = default)
    {
        text = text.Trim();
        if (ParseKey(text) is { } id)
        {
            try { return [await GetAsync(id, ct)]; }
            catch (IssueTrackerException ex) when (ex.Status == HttpStatusCode.NotFound) { return []; }
        }
        var where = "[System.TeamProject] = @project" + (text.Length > 0 ? $" AND [System.Title] CONTAINS '{text.Replace("'", "''")}'" : "");
        var wiql = new JsonObject { ["query"] = $"SELECT [System.Id] FROM WorkItems WHERE {where} ORDER BY [System.ChangedDate] DESC" };
        var r = await SendAsync(HttpMethod.Post, $"{Base}/{Project}/_apis/wit/wiql?$top=20&{Version}", wiql, ct);
        var ids = (r?["workItems"]?.AsArray() ?? []).Select(w => Str(w?["id"])).Where(s => s.Length > 0).Take(20).ToList();
        if (ids.Count == 0) return [];
        var items = await SendAsync(HttpMethod.Get,
            $"{Base}/{Project}/_apis/wit/workitems?ids={string.Join(",", ids)}&fields=System.Id,System.Title,System.State,System.AssignedTo&{Version}", ct: ct);
        return (items?["value"]?.AsArray() ?? []).Select(Read).ToList();
    }

    public async Task<IssueInfo> GetAsync(string key, CancellationToken ct = default) =>
        Read(await SendAsync(HttpMethod.Get, $"{Base}/{Project}/_apis/wit/workitems/{Uri.EscapeDataString(key)}?{Version}", ct: ct));

    public async Task<IssueInfo> CreateAsync(string title, string description, string backLink, CancellationToken ct = default)
    {
        var html = string.Join("", JiraTracker.Paragraphs(description).Select(p => $"<p>{WebUtility.HtmlEncode(p)}</p>"))
                   + $"<p><a href=\"{WebUtility.HtmlEncode(backLink)}\">Open in Meridian</a></p>";
        var patch = new JsonArray(
            new JsonObject { ["op"] = "add", ["path"] = "/fields/System.Title", ["value"] = JiraTracker.Truncate(title, 255) },
            new JsonObject { ["op"] = "add", ["path"] = "/fields/System.Description", ["value"] = html },
            new JsonObject { ["op"] = "add", ["path"] = "/relations/-", ["value"] = new JsonObject { ["rel"] = "Hyperlink", ["url"] = backLink, ["attributes"] = new JsonObject { ["comment"] = "Meridian model element" } } });
        var type = Uri.EscapeDataString(string.IsNullOrWhiteSpace(Config.IssueType) ? "Task" : Config.IssueType.Trim());
        return Read(await SendAsync(HttpMethod.Post, $"{Base}/{Project}/_apis/wit/workitems/${type}?{Version}", patch, ct, "application/json-patch+json"));
    }

    public string? ParseKey(string keyOrUrl)
    {
        var s = keyOrUrl.Trim().TrimStart('#');
        if (Regex.IsMatch(s, @"^\d+$")) return s;
        var m = Regex.Match(s, @"(?:_workitems/edit/|workItems/|workitems/)(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private IssueInfo Read(JsonNode? item)
    {
        var id = Str(item?["id"]);
        var f = item?["fields"];
        var state = Str(f?["System.State"]);
        var assignee = f?["System.AssignedTo"] is { } a ? (a is JsonObject o ? Str(o["displayName"]) : Str(a)) : null;
        return new IssueInfo(id, $"{Base}/{Project}/_workitems/edit/{id}", Str(f?["System.Title"]), state, assignee,
            ClosedStates.Contains(state, StringComparer.OrdinalIgnoreCase));
    }
}

// ==================================================================================== GitHub Issues

public sealed class GitHubTracker(IssueTrackerConfig config, string secret, HttpClient http) : TrackerBase(config, http), IIssueTracker
{
    private string Repo => Config.Project.Trim().Trim('/');

    protected override void Authorize(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Get, $"{Base}/repos/{Repo}", ct: ct);
        return $"Connected to GitHub repository {Str(r?["full_name"])}{(r?["has_issues"]?.GetValue<bool>() == false ? " (issues are disabled!)" : "")}";
    }

    public async Task<IReadOnlyList<IssueInfo>> SearchAsync(string text, CancellationToken ct = default)
    {
        text = text.Trim();
        if (ParseKey(text) is { } n)
        {
            try { return [await GetAsync(n, ct)]; }
            catch (IssueTrackerException ex) when (ex.Status == HttpStatusCode.NotFound) { return []; }
        }
        var q = $"{text} repo:{Repo} is:issue".Trim();
        var r = await SendAsync(HttpMethod.Get, $"{Base}/search/issues?q={Uri.EscapeDataString(q)}&per_page=20&sort=updated", ct: ct);
        return (r?["items"]?.AsArray() ?? []).Select(Read).ToList();
    }

    public async Task<IssueInfo> GetAsync(string key, CancellationToken ct = default) =>
        Read(await SendAsync(HttpMethod.Get, $"{Base}/repos/{Repo}/issues/{Uri.EscapeDataString(key.TrimStart('#'))}", ct: ct));

    public async Task<IssueInfo> CreateAsync(string title, string description, string backLink, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["title"] = JiraTracker.Truncate(title, 250),
            ["body"] = $"{description.Trim()}\n\n[Open in Meridian]({backLink})",
        };
        return Read(await SendAsync(HttpMethod.Post, $"{Base}/repos/{Repo}/issues", body, ct));
    }

    public string? ParseKey(string keyOrUrl)
    {
        var s = keyOrUrl.Trim();
        if (Regex.IsMatch(s, @"^#?\d+$")) return s.TrimStart('#');
        var m = Regex.Match(s, @"github[^/]*/" + Regex.Escape(Repo) + @"/issues/(\d+)", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(s, @"/repos/" + Regex.Escape(Repo) + @"/issues/(\d+)", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(s, "^https?://[^/]+/" + Regex.Escape(Repo) + @"/issues/(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static IssueInfo Read(JsonNode? issue)
    {
        var state = Str(issue?["state"]);
        return new IssueInfo("#" + Str(issue?["number"]), Str(issue?["html_url"]), Str(issue?["title"]),
            state, issue?["assignee"]?["login"] is { } a ? Str(a) : null, state == "closed");
    }
}

// ==================================================================================== GitLab Issues

/// <summary>
/// GitLab Issues through REST v4, on gitlab.com or a self-managed instance. The base URL is the instance root
/// (<c>https://gitlab.com</c>) or its API root (<c>…/api/v4</c>); the project is a full path (<c>group/sub/project</c>) or a
/// numeric id. The token is a personal, project or group access token with the <c>api</c> scope sent as <c>PRIVATE-TOKEN</c>,
/// or an OAuth access token sent as <c>Authorization: Bearer</c> (<see cref="IssueTrackerConfig.BearerAuth"/>).
/// Lists are paged with GitLab's <c>x-next-page</c> header (or a same-host <c>Link: rel="next"</c>).
/// </summary>
public sealed class GitLabTracker(IssueTrackerConfig config, string secret, HttpClient http) : TrackerBase(config, http), IIssueTracker
{
    public static readonly string[] IssueTypes = ["issue", "incident", "test_case", "task"];
    /// <summary>Most issues a search returns (followed over pages of <see cref="PageSize"/>).</summary>
    public const int MaxSearchResults = 50;
    public const int PageSize = 20;
    private const int MaxPages = 10;

    private string Api => Base.EndsWith("/api/v4", StringComparison.OrdinalIgnoreCase) ? Base : Base + "/api/v4";
    private string WebRoot => Base.EndsWith("/api/v4", StringComparison.OrdinalIgnoreCase) ? Base[..^"/api/v4".Length] : Base;
    private string ProjectRef => Config.Project.Trim().Trim('/');
    private string ProjectId => Uri.EscapeDataString(ProjectRef);
    private bool NumericProject => ProjectRef.All(char.IsAsciiDigit);

    /// <summary>A numeric project id, or a namespace path with at least two segments of GitLab's allowed characters.</summary>
    public static bool IsProjectRef(string project)
    {
        var p = project.Trim().Trim('/');
        if (p.Length > 0 && p.All(char.IsAsciiDigit)) return true;
        var parts = p.Split('/');
        return parts.Length >= 2 && parts.All(s => Regex.IsMatch(s, @"^[A-Za-z0-9_][A-Za-z0-9_.\-]*$"));
    }

    protected override void Authorize(HttpRequestMessage request)
    {
        if (Config.BearerAuth == true) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        else request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", secret);
    }

    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var p = await SendAsync(HttpMethod.Get, $"{Api}/projects/{ProjectId}", ct: ct);
        var disabled = p?["issues_enabled"]?.GetValueKind() == JsonValueKind.False || Str(p?["issues_access_level"]) == "disabled";
        return $"Connected to GitLab project {Str(p?["path_with_namespace"])}{(disabled ? " (issues are disabled!)" : "")}";
    }

    public async Task<IReadOnlyList<IssueInfo>> SearchAsync(string text, CancellationToken ct = default)
    {
        text = text.Trim();
        if (ParseKey(text) is { } iid)
        {
            try { return [await GetAsync(iid, ct)]; }
            catch (IssueTrackerException ex) when (ex.Status == HttpStatusCode.NotFound) { return []; }
        }
        var url = $"{Api}/projects/{ProjectId}/issues?scope=all&order_by=updated_at&sort=desc&per_page={PageSize}"
                  + (text.Length > 0 ? $"&search={Uri.EscapeDataString(text)}&in=title,description" : "");
        return (await GetPagesAsync(url, MaxSearchResults, ct)).Select(Read).ToList();
    }

    public async Task<IssueInfo> GetAsync(string key, CancellationToken ct = default) =>
        Read(await SendAsync(HttpMethod.Get, $"{Api}/projects/{ProjectId}/issues/{Uri.EscapeDataString(key.Trim().TrimStart('#'))}", ct: ct));

    public async Task<IssueInfo> CreateAsync(string title, string description, string backLink, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["title"] = JiraTracker.Truncate(title, 255),
            ["description"] = $"{description.Trim()}\n\n[Open in Meridian]({backLink})",
        };
        var labels = (Config.Labels ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (labels.Length > 0) body["labels"] = string.Join(",", labels);
        if (Config.IssueType is { Length: > 0 } type) body["issue_type"] = type.Trim().ToLowerInvariant();
        return Read(await SendAsync(HttpMethod.Post, $"{Api}/projects/{ProjectId}/issues", body, ct));
    }

    public string? ParseKey(string keyOrUrl)
    {
        var s = keyOrUrl.Trim();
        if (Regex.IsMatch(s, @"^#?\d+$")) return s.TrimStart('#');
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || !Uri.TryCreate(WebRoot, UriKind.Absolute, out var root)
            || !string.Equals(u.Host, root.Host, StringComparison.OrdinalIgnoreCase)) return null;
        var path = Uri.UnescapeDataString(u.AbsolutePath).TrimEnd('/');
        // API address: /api/v4/projects/{id or url-encoded path}/issues/{iid}
        var api = Regex.Match(path, @"/api/v4/projects/(.+)/issues/(\d+)$", RegexOptions.IgnoreCase);
        if (api.Success) return string.Equals(api.Groups[1].Value, ProjectRef, StringComparison.OrdinalIgnoreCase) ? api.Groups[2].Value : null;
        // Browser address: /{group}/{project}/-/issues/{iid} (also /-/work_items/{iid} and the old /{group}/{project}/issues/{iid}).
        var web = Regex.Match(path, @"^/(.+?)(?:/-)?/(?:issues|work_items)/(\d+)$", RegexOptions.IgnoreCase);
        if (!web.Success) return null;
        // With a numeric project id the path can't be compared, so any issue address on the same GitLab host is accepted.
        return NumericProject || string.Equals(web.Groups[1].Value, ProjectRef, StringComparison.OrdinalIgnoreCase) ? web.Groups[2].Value : null;
    }

    /// <summary>GETs a JSON array and the pages after it, up to <paramref name="max"/> items.</summary>
    private async Task<List<JsonNode?>> GetPagesAsync(string url, int max, CancellationToken ct)
    {
        var items = new List<JsonNode?>();
        string? next = url;
        for (var page = 0; page < MaxPages && next != null && items.Count < max; page++)
        {
            var (json, headers) = await SendWithHeadersAsync(HttpMethod.Get, next, ct: ct);
            if (json is not JsonArray arr) throw new IssueTrackerException("GitLab returned something other than a list — check the base URL.");
            items.AddRange(arr.Take(max - items.Count));
            next = NextPage(next, headers);
        }
        return items;
    }

    /// <summary>
    /// The next page: GitLab's <c>x-next-page</c> number applied to the current address, else a <c>Link: &lt;…&gt;; rel="next"</c>
    /// address on the same scheme, host and port as the API (the token is never sent to another host). Null on the last page.
    /// </summary>
    internal string? NextPage(string current, HttpResponseHeaders headers)
    {
        if (headers.TryGetValues("x-next-page", out var next))
        {
            var n = next.FirstOrDefault()?.Trim();
            if (string.IsNullOrEmpty(n)) return null;   // GitLab sends an empty x-next-page on the last page
            if (!int.TryParse(n, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) || number < 1) return null;
            var withoutPage = Regex.Replace(current, @"([?&])page=\d+&?", "$1").TrimEnd('&', '?');
            return withoutPage + (withoutPage.Contains('?') ? "&" : "?") + "page=" + number;
        }
        if (!headers.TryGetValues("Link", out var links)) return null;
        foreach (var part in string.Join(",", links).Split(','))
        {
            var m = Regex.Match(part, @"<([^>]+)>\s*;\s*rel=""?next""?", RegexOptions.IgnoreCase);
            if (!m.Success || !Uri.TryCreate(m.Groups[1].Value, UriKind.Absolute, out var u) || !Uri.TryCreate(Api, UriKind.Absolute, out var api)) continue;
            if (u.Scheme == api.Scheme && u.Port == api.Port && string.Equals(u.Host, api.Host, StringComparison.OrdinalIgnoreCase)) return u.AbsoluteUri;
        }
        return null;
    }

    private static IssueInfo Read(JsonNode? issue)
    {
        var state = Str(issue?["state"]);
        var assignees = (issue?["assignees"] as JsonArray ?? [])
            .Select(a => Str(a?["username"])).Where(n => n.Length > 0).ToList();
        if (assignees.Count == 0 && issue?["assignee"]?["username"] is { } single) assignees.Add(Str(single));
        var labels = (issue?["labels"] as JsonArray ?? [])
            .Select(l => l is JsonObject o ? Str(o["name"]) : Str(l)).Where(l => l.Length > 0).ToArray();
        return new IssueInfo("#" + Str(issue?["iid"]), Str(issue?["web_url"]), Str(issue?["title"]), state,
            assignees.Count > 0 ? string.Join(", ", assignees) : null, state == "closed") { Labels = labels };
    }
}
