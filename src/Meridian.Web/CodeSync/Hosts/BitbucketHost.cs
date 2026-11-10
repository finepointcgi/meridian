using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Meridian.Web.CodeSync.Hosts;

/// <summary>
/// Bitbucket Cloud (REST API 2.0 at https://api.bitbucket.org/2.0) or Bitbucket Data Center / Server (REST API 1.0 at
/// <c>&lt;server&gt;/rest/api/1.0</c>), chosen by the server URL. <see cref="CodeRepoConfig.Owner"/> is the Cloud
/// workspace or the Data Center project key, <see cref="CodeRepoConfig.Repo"/> the repository slug.
/// The token is a Cloud repository / workspace access token or a Data Center HTTP access token (sent as a bearer token),
/// or <c>username:app-password</c> (sent with basic authentication).
/// </summary>
public sealed partial class BitbucketHost(CodeRepoConfig config, string token, HttpClient http, SnapshotLimits limits) : GitHostBase(http, limits)
{
    /// <summary>Build-status key of the current <see cref="GitHostFactory.StatusContext"/> ("meridian/model" → "meridian-model").
    /// Bitbucket identifies a status by its key (at most 40 characters), so each context keeps its own status.</summary>
    public static string StatusKey => GitHostFactory.StatusContext.Replace('/', '-') is var k && k.Length > 40 ? k[..40] : k;

    private readonly bool _cloud = CodeRepoConfig.IsBitbucketCloudUrl(config.BaseUrl);
    private readonly string _base = ServerRoot(config.BaseUrl);
    private string Workspace => Uri.EscapeDataString(config.Owner.Trim());
    private string Slug => Uri.EscapeDataString(config.Repo.Trim());

    /// <summary>Cloud: https://api.bitbucket.org/2.0 (any bitbucket.org URL). Data Center: the server URL without a
    /// trailing <c>/rest/api/1.0</c>.</summary>
    public static string ServerRoot(string baseUrl)
    {
        var b = (baseUrl ?? "").Trim().TrimEnd('/');
        if (CodeRepoConfig.IsBitbucketCloudUrl(b)) return CodeRepoConfig.BitbucketCloudApi;
        foreach (var suffix in new[] { "/rest/api/1.0", "/rest/api/latest" })
            if (b.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return b[..^suffix.Length];
        return b;
    }

    /// <summary>The repository's API URL.</summary>
    private string Repo => _cloud ? $"{_base}/repositories/{Workspace}/{Slug}" : $"{_base}/rest/api/1.0/projects/{Workspace}/repos/{Slug}";

    /// <summary>The repository's web page (Cloud: bitbucket.org, not the API host).</summary>
    private string WebRepo => _cloud ? $"https://bitbucket.org/{Workspace}/{Slug}" : $"{_base}/projects/{Workspace}/repos/{Slug}";

    private string CommitWebUrl(string sha) => $"{WebRepo}/commits/{sha}";

    public override string RepoDisplay => $"{config.Owner.Trim()}/{config.Repo.Trim()}";
    protected override string HostName => _cloud ? "Bitbucket" : "Bitbucket Data Center";

    protected override void Authorize(HttpRequestMessage request)
    {
        var t = token.Trim();
        if (t.Length == 0) return;   // anonymous access to a public submodule
        request.Headers.Authorization = t.Contains(':')
            ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(t)))
            : new AuthenticationHeaderValue("Bearer", t);
    }

    // Cloud serves archives from the web host (a .tar.gz with one top-level folder). Data Center builds the archive in
    // the REST API; the prefix gives it the top-level folder the snapshot reader strips.
    protected override string ArchiveUrl(string commit) => _cloud
        ? $"{WebRepo}/get/{commit}.tar.gz"
        : $"{Repo}/archive?at={commit}&format=tar.gz&prefix={Slug}%2F";

    /// <summary>Cloud: <c>{"error":{"message":…}}</c>; Data Center: <c>{"errors":[{"message":…}]}</c>.</summary>
    protected override string? ErrorDetail(JsonNode? body)
    {
        if (body is not JsonObject o) return null;
        if (o["error"]?["message"] is JsonValue c && c.TryGetValue<string>(out var cloud)) return cloud;
        if (o["errors"] is JsonArray errors && errors.FirstOrDefault()?["message"] is JsonValue s && s.TryGetValue<string>(out var server)) return server;
        return base.ErrorDetail(body);
    }

    public override async Task<string> TestConnectionAsync(CancellationToken ct = default)
    {
        var repo = await SendAsync(HttpMethod.Get, Repo, ct: ct);
        var name = _cloud
            ? (string?)repo?["full_name"] ?? RepoDisplay
            : (string?)repo?["project"]?["key"] is { } key && (string?)repo?["slug"] is { } slug ? $"{key}/{slug}" : RepoDisplay;
        var head = await ResolveBranchHeadAsync(config.Branch, ct);
        return $"Connected to {name} on {HostName}: {config.Branch} is at {Short(head.Sha)}.";
    }

    public override async Task<GitCommitInfo> ResolveBranchHeadAsync(string branch, CancellationToken ct = default)
    {
        if (_cloud)
        {
            var b = await SendAsync(HttpMethod.Get, $"{Repo}/refs/branches/{Uri.EscapeDataString(branch)}", ct: ct);
            var t = b?["target"];
            var sha = (string?)t?["hash"];
            if (!IsSha(sha)) throw new GitHostException($"Bitbucket did not return a commit for {branch}.");
            return new GitCommitInfo(sha!, (string?)t?["message"], (string?)t?["author"]?["user"]?["display_name"] ?? (string?)t?["author"]?["raw"],
                (string?)t?["links"]?["html"]?["href"] ?? CommitWebUrl(sha!));
        }
        var page = await SendAsync(HttpMethod.Get, $"{Repo}/commits?until={Uri.EscapeDataString("refs/heads/" + branch)}&limit=1", ct: ct);
        var c = (page?["values"] as JsonArray)?.FirstOrDefault();
        var id = (string?)c?["id"];
        if (!IsSha(id)) throw new GitHostException($"Bitbucket Data Center did not return a commit for {branch}.");
        return new GitCommitInfo(id!, (string?)c?["message"], (string?)c?["author"]?["displayName"] ?? (string?)c?["author"]?["name"], CommitWebUrl(id!));
    }

    public override async Task PostCommitStatusAsync(string commit, CommitState state, string description, string? targetUrl, CancellationToken ct = default)
    {
        if (!IsSha(commit)) throw new GitHostException("Not a commit id.");
        var body = new JsonObject
        {
            ["state"] = state switch { CommitState.Pending => "INPROGRESS", CommitState.Success => "SUCCESSFUL", _ => "FAILED" },
            ["key"] = StatusKey,
            ["name"] = GitHostFactory.StatusContext,
            // Bitbucket requires a link: the review in Meridian, else the commit itself.
            ["url"] = SafeTargetUrl(targetUrl) ?? CommitWebUrl(commit),
            ["description"] = Truncate(description, 255),
        };
        var url = _cloud ? $"{Repo}/commit/{commit}/statuses/build" : $"{_base}/rest/build-status/1.0/commits/{commit}";
        await SendAsync(HttpMethod.Post, url, body, ct);
    }

    protected override async Task<string> CreateCommentAsync(int number, string body, CancellationToken ct) => _cloud
        ? CommentId(await SendAsync(HttpMethod.Post, $"{Repo}/pullrequests/{number}/comments", new JsonObject { ["content"] = new JsonObject { ["raw"] = body } }, ct))
        : CommentId(await SendAsync(HttpMethod.Post, $"{Repo}/pull-requests/{number}/comments", new JsonObject { ["text"] = body }, ct));

    protected override async Task<string> UpdateCommentAsync(int number, string commentId, string body, CancellationToken ct)
    {
        if (_cloud)
            return CommentId(await SendAsync(HttpMethod.Put, $"{Repo}/pullrequests/{number}/comments/{commentId}", new JsonObject { ["content"] = new JsonObject { ["raw"] = body } }, ct));
        // Data Center updates are optimistic: send the version the comment is at.
        var url = $"{Repo}/pull-requests/{number}/comments/{commentId}";
        var current = await SendAsync(HttpMethod.Get, url, ct: ct);
        var version = current?["version"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
        return CommentId(await SendAsync(HttpMethod.Put, url, new JsonObject { ["text"] = body, ["version"] = version }, ct));
    }
}
