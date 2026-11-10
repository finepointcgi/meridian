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

    public static readonly string[] All = [JiraCloud, JiraServer, AzureDevOps, GitHub];

    public static string Display(string type) => type switch
    {
        JiraCloud => "Jira Cloud",
        JiraServer => "Jira Server / Data Center",
        AzureDevOps => "Azure DevOps",
        GitHub => "GitHub Issues",
        _ => type,
    };

    public static string ProjectLabel(string type) => type switch
    {
        AzureDevOps => "Project",
        GitHub => "Repository (owner/repo)",
        _ => "Project key",
    };

    public static string BaseUrlHint(string type) => type switch
    {
        JiraCloud => "https://your-site.atlassian.net",
        JiraServer => "https://jira.example.com",
        AzureDevOps => "https://dev.azure.com/your-organization",
        GitHub => "https://api.github.com (or https://ghe.example.com/api/v3)",
        _ => "",
    };

    public static bool NeedsUser(string type) => type == JiraCloud;
}

/// <summary>Non-secret settings of an issue tracker connection (stored as the integration's config JSON).</summary>
public sealed class IssueTrackerConfig
{
    public string Type { get; set; } = TrackerTypes.JiraCloud;
    public string BaseUrl { get; set; } = "";
    /// <summary>Jira project key, Azure DevOps project, or GitHub "owner/repo".</summary>
    public string Project { get; set; } = "";
    /// <summary>Jira Cloud account email (Basic auth user).</summary>
    public string? User { get; set; }
    /// <summary>Jira issue type / Azure DevOps work item type for created issues ("Task" by default).</summary>
    public string? IssueType { get; set; }

    public static IssueTrackerConfig Parse(string json) =>
        JsonSerializer.Deserialize<IssueTrackerConfig>(json, IntegrationJson.Options) ?? new IssueTrackerConfig();

    public string ToJson() => JsonSerializer.Serialize(this, IntegrationJson.Options);

    /// <summary>Validates the settings; returns an error message or null.</summary>
    public string? Validate()
    {
        if (!TrackerTypes.All.Contains(Type)) return "Choose a tracker type.";
        if (!Uri.TryCreate(BaseUrl?.Trim(), UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)))
            return "The base URL must be an absolute https:// URL.";
        if (string.IsNullOrWhiteSpace(Project)) return $"Enter the {TrackerTypes.ProjectLabel(Type).ToLowerInvariant()}.";
        if (Type == TrackerTypes.GitHub && Project.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries).Length != 2) return "The repository must look like owner/repo.";
        if (TrackerTypes.NeedsUser(Type) && string.IsNullOrWhiteSpace(User)) return "Enter the Atlassian account email.";
        return null;
    }
}

public static class IntegrationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
}

/// <summary>An issue as shown in Meridian: key, title, status and assignee, plus whether the tracker considers it done.</summary>
public sealed record IssueInfo(string Key, string Url, string Title, string Status, string? Assignee, bool Closed);

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
        _ => throw new IssueTrackerException($"Unknown tracker type '{config.Type}'."),
    };
}

/// <summary>Shared HTTP plumbing: authenticated JSON requests with credential-free error messages.</summary>
public abstract class TrackerBase(IssueTrackerConfig config, HttpClient http)
{
    protected IssueTrackerConfig Config { get; } = config;
    protected string Base { get; } = config.BaseUrl.Trim().TrimEnd('/');

    protected abstract void Authorize(HttpRequestMessage request);

    protected async Task<JsonNode?> SendAsync(HttpMethod method, string url, JsonNode? body = null, CancellationToken ct = default, string contentType = "application/json")
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
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return JsonNode.Parse(text); }
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
