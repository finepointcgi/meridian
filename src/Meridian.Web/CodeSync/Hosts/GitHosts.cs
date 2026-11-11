using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Meridian.Web.CodeSync.Hosts;

/// <summary>A failure talking to a git host or reading its archive. The message never contains credentials.</summary>
public class GitHostException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

public enum CommitState { Pending, Success, Failure }

/// <summary>A commit as reported by the host.</summary>
public sealed record GitCommitInfo(string Sha, string? Message, string? Author, string? Url);

/// <summary>What Meridian needs from a git host.</summary>
public interface IGitHost
{
    /// <summary>"owner/repo" or the GitLab project path.</summary>
    string RepoDisplay { get; }
    /// <summary>Checks the token and that the repository and branch are reachable. Returns a short description.</summary>
    Task<string> TestConnectionAsync(CancellationToken ct = default);
    Task<GitCommitInfo> ResolveBranchHeadAsync(string branch, CancellationToken ct = default);
    /// <summary>Downloads the repository archive at <paramref name="commit"/> and returns every file inside
    /// <paramref name="paths"/> (empty = all) accepted by <paramref name="include"/>: a full snapshot.</summary>
    Task<CodeSnapshot> FetchSnapshotAsync(string commit, IReadOnlyList<string> paths, Func<string, bool> include, CancellationToken ct = default);
    Task PostCommitStatusAsync(string commit, CommitState state, string description, string? targetUrl, CancellationToken ct = default);
    /// <summary>Writes Meridian's comment on pull / merge request <paramref name="number"/>: updates comment
    /// <paramref name="commentId"/> in place when given (and still there), else adds one. Returns the comment's id.
    /// Hosts that cannot comment keep the default, which fails (the caller logs it and carries on).</summary>
    Task<string> UpsertPullRequestCommentAsync(int number, string? commentId, string body, CancellationToken ct = default) =>
        throw new GitHostException("This host cannot comment on pull requests.");
    /// <summary>As <see cref="FetchSnapshotAsync(string, IReadOnlyList{string}, Func{string, bool}, CancellationToken)"/>, counting
    /// against <paramref name="budget"/> so the limits cover the repository and its submodules together (see Submodules.cs).</summary>
    Task<CodeSnapshot> FetchSnapshotAsync(string commit, IReadOnlyList<string> paths, Func<string, bool> include, SnapshotBudget budget, CancellationToken ct = default) =>
        FetchSnapshotAsync(commit, paths, include, ct);
    /// <summary>The commit each submodule path is pinned to at <paramref name="commit"/> (its gitlink). Paths without a
    /// gitlink are left out. Hosts that cannot tell keep the default, which finds none.</summary>
    Task<IReadOnlyDictionary<string, string>> ResolveSubmoduleCommitsAsync(string commit, IReadOnlyList<string> paths, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.Ordinal));
}

/// <summary>Builds host clients over a supplied <see cref="HttpClient"/> (tests pass one with a fake handler).</summary>
public static class GitHostFactory
{
    /// <summary>The context of the code-sync status ("meridian/model").</summary>
    public const string ModelStatusContext = "meridian/model";

    private static readonly AsyncLocal<string?> CurrentContext = new();

    /// <summary>The context hosts post commit statuses under: <see cref="ModelStatusContext"/>, unless a caller
    /// switched it for its own status with <see cref="UseStatusContext"/> (the architecture check posts "meridian/architecture").</summary>
    public static string StatusContext => CurrentContext.Value ?? ModelStatusContext;

    /// <summary>Posts made in this async flow, until disposed, use <paramref name="context"/> as the status context.</summary>
    public static IDisposable UseStatusContext(string context)
    {
        var previous = CurrentContext.Value;
        CurrentContext.Value = context;
        return new Restore(() => CurrentContext.Value = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    public static IGitHost Create(CodeRepoConfig config, string token, HttpClient http, SnapshotLimits? limits = null) => config.Host switch
    {
        GitHostKind.GitHub => new GitHubHost(config, token, http, limits ?? SnapshotLimits.Default),
        GitHostKind.GitLab => new GitLabHost(config, token, http, limits ?? SnapshotLimits.Default),
        GitHostKind.Bitbucket => new BitbucketHost(config, token, http, limits ?? SnapshotLimits.Default),
        _ => throw new GitHostException($"Unknown git host '{config.Host}'."),
    };
}

/// <summary>Shared HTTP plumbing: authenticated JSON calls with credential-free errors, and the archive download.</summary>
public abstract class GitHostBase(HttpClient http, SnapshotLimits limits) : IGitHost
{
    /// <summary>Timeout for API calls (the archive download has its own, longer one).</summary>
    public static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    public abstract string RepoDisplay { get; }
    protected abstract string HostName { get; }
    protected abstract void Authorize(HttpRequestMessage request);
    protected abstract string ArchiveUrl(string commit);

    /// <summary>Accept values for the archive download. GitHub answers 415 to <c>application/x-gzip</c> and wants its API type.</summary>
    protected virtual string[] ArchiveAccept => ["application/octet-stream", "application/x-gzip"];

    public abstract Task<string> TestConnectionAsync(CancellationToken ct = default);
    public abstract Task<GitCommitInfo> ResolveBranchHeadAsync(string branch, CancellationToken ct = default);
    public abstract Task PostCommitStatusAsync(string commit, CommitState state, string description, string? targetUrl, CancellationToken ct = default);

    /// <summary>Adds a comment to a pull request; returns its id.</summary>
    protected abstract Task<string> CreateCommentAsync(int number, string body, CancellationToken ct);
    /// <summary>Replaces the text of an existing comment; returns its id.</summary>
    protected abstract Task<string> UpdateCommentAsync(int number, string commentId, string body, CancellationToken ct);

    /// <summary>Largest comment body sent (hosts cap comments at about 64 KB).</summary>
    public const int MaxCommentChars = 60_000;

    public async Task<string> UpsertPullRequestCommentAsync(int number, string? commentId, string body, CancellationToken ct = default)
    {
        if (number <= 0) throw new GitHostException("Not a pull request number.");
        body = Truncate(body, MaxCommentChars);
        if (commentId is { Length: > 0 and <= 40 } id && id.All(char.IsAsciiDigit))
        {
            try { return await UpdateCommentAsync(number, id, body, ct); }
            catch (GitHostException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone) { /* deleted by someone: add a new one */ }
        }
        return await CreateCommentAsync(number, body, ct);
    }

    /// <summary>The id of a created or updated comment (a number in every host's JSON).</summary>
    protected string CommentId(JsonNode? node) =>
        node?["id"] is JsonValue v && (v.TryGetValue<long>(out var n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : v.TryGetValue<string>(out var s) ? s : null) is { Length: > 0 } id
            ? id : throw new GitHostException($"{HostName} did not return the comment id.");

    protected HttpRequestMessage Request(HttpMethod method, string url, string accept)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Meridian-CodeSync", "1.0"));
        Authorize(request);
        return request;
    }

    protected async Task<JsonNode?> SendAsync(HttpMethod method, string url, JsonNode? body = null, CancellationToken ct = default)
    {
        using var request = Request(method, url, "application/json");
        if (body != null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);
        using var response = await Send(request, timeout.Token, HttpCompletionOption.ResponseContentRead, ct);
        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode) throw Error(response.StatusCode, text);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { throw new GitHostException($"{HostName} returned an unexpected response."); }
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token, HttpCompletionOption option, CancellationToken outer)
    {
        try { return await http.SendAsync(request, option, token); }
        catch (HttpRequestException ex) { throw new GitHostException($"Could not reach {request.RequestUri!.Host}: {ex.Message}"); }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested) { throw new GitHostException($"{request.RequestUri!.Host} did not answer in time."); }
    }

    /// <summary>The host's own error message in an error response body (GitHub and GitLab: <c>message</c>).</summary>
    protected virtual string? ErrorDetail(JsonNode? body) => body is JsonObject o && o["message"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>A readable error for a failed call; quotes the host's own message, never the request.</summary>
    protected GitHostException Error(HttpStatusCode status, string body)
    {
        string? detail = null;
        try { detail = ErrorDetail(JsonNode.Parse(body)); } catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { }
        if (detail is { Length: > 200 }) detail = detail[..200];
        var what = status switch
        {
            HttpStatusCode.Unauthorized => "the token was rejected",
            HttpStatusCode.Forbidden => "the token is not allowed to do this",
            HttpStatusCode.NotFound => "repository, branch or commit not found (or the token cannot see it)",
            (HttpStatusCode)422 => "the request was rejected",
            (HttpStatusCode)429 => "rate limited",
            _ => $"HTTP {(int)status}",
        };
        return new GitHostException($"{HostName}: {what}{(detail is { Length: > 0 } ? $" ({detail})" : "")}.", status);
    }

    public Task<CodeSnapshot> FetchSnapshotAsync(string commit, IReadOnlyList<string> paths, Func<string, bool> include, CancellationToken ct = default) =>
        FetchSnapshotAsync(commit, paths, include, new SnapshotBudget(limits), ct);

    public async Task<CodeSnapshot> FetchSnapshotAsync(string commit, IReadOnlyList<string> paths, Func<string, bool> include, SnapshotBudget budget, CancellationToken ct = default)
    {
        if (!IsSha(commit)) throw new GitHostException("Not a commit id.");
        using var request = Request(HttpMethod.Get, ArchiveUrl(commit), ArchiveAccept[0]);
        foreach (var extra in ArchiveAccept.Skip(1)) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(extra));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DownloadTimeout);
        using var response = await Send(request, timeout.Token, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw Error(response.StatusCode, await ReadSmallAsync(response, timeout.Token));
        if (response.Content.Headers.ContentLength > budget.RemainingArchiveBytes)
            throw new SnapshotLimitException($"The repository archive is larger than {SnapshotArchive.Mb(budget.Limits.MaxArchiveBytes)}.");
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await SnapshotArchive.ReadTarGzAsync(stream, paths.Select(CodeRepoConfig.NormalizePath).Where(p => p.Length > 0).ToList(), include, budget, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new GitHostException("Downloading the repository archive took too long."); }
        catch (HttpRequestException ex) { throw new GitHostException($"Downloading the repository archive failed: {ex.Message}"); }
        catch (IOException ex) { throw new GitHostException($"Downloading the repository archive failed: {ex.Message}"); }
    }

    private static async Task<string> ReadSmallAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var buffer = new byte[4096];
        await using var s = await response.Content.ReadAsStreamAsync(ct);
        var n = await s.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
        return Encoding.UTF8.GetString(buffer, 0, n);
    }

    /// <summary>Submodule gitlinks; each host overrides it (Submodules.Hosts.cs).</summary>
    public virtual Task<IReadOnlyDictionary<string, string>> ResolveSubmoduleCommitsAsync(string commit, IReadOnlyList<string> paths, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>A full or abbreviated hexadecimal commit id (SHA-1 or SHA-256).</summary>
    public static bool IsSha(string? s) => s is { Length: >= 7 and <= 64 } && s.All(Uri.IsHexDigit);

    protected static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    protected static string? SafeTargetUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.ToString() : null;

    protected static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}

/// <summary>GitHub (github.com or Enterprise Server) over the REST API, with a fine-grained or classic token.</summary>
public sealed partial class GitHubHost(CodeRepoConfig config, string token, HttpClient http, SnapshotLimits limits) : GitHostBase(http, limits)
{
    private readonly string _api = config.BaseUrl.Trim().TrimEnd('/');
    private string Repo => $"{_api}/repos/{Uri.EscapeDataString(config.Owner.Trim())}/{Uri.EscapeDataString(config.Repo.Trim())}";

    public override string RepoDisplay => $"{config.Owner.Trim()}/{config.Repo.Trim()}";
    protected override string HostName => "GitHub";

    protected override void Authorize(HttpRequestMessage request)
    {
        // No token: anonymous access to a public submodule (Submodules.cs).
        if (token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    // Answers with a redirect to a short-lived codeload URL; the client follows it (through the outbound guard) and drops
    // the Authorization header on the way.
    protected override string ArchiveUrl(string commit) => $"{Repo}/tarball/{commit}";

    // GitHub rejects application/x-gzip on /tarball with 415 Unsupported Media Type.
    protected override string[] ArchiveAccept => ["application/vnd.github+json"];

    public override async Task<string> TestConnectionAsync(CancellationToken ct = default)
    {
        var repo = await SendAsync(HttpMethod.Get, Repo, ct: ct);
        var name = (string?)repo?["full_name"] ?? RepoDisplay;
        var head = await ResolveBranchHeadAsync(config.Branch, ct);
        var canStatus = repo?["permissions"]?["push"] is JsonValue v && v.TryGetValue<bool>(out var push) ? push : (bool?)null;
        var note = config.PostStatus && canStatus == false ? " The token cannot post commit statuses (needs Commit statuses: write)." : "";
        return $"Connected to {name}: {config.Branch} is at {Short(head.Sha)}.{note}";
    }

    public override async Task<GitCommitInfo> ResolveBranchHeadAsync(string branch, CancellationToken ct = default)
    {
        var c = await SendAsync(HttpMethod.Get, $"{Repo}/commits/{Uri.EscapeDataString(branch)}", ct: ct);
        var sha = (string?)c?["sha"];
        if (!IsSha(sha)) throw new GitHostException($"GitHub did not return a commit for {branch}.");
        return new GitCommitInfo(sha!, (string?)c?["commit"]?["message"], (string?)c?["commit"]?["author"]?["name"] ?? (string?)c?["author"]?["login"], (string?)c?["html_url"]);
    }

    public override async Task PostCommitStatusAsync(string commit, CommitState state, string description, string? targetUrl, CancellationToken ct = default)
    {
        if (!IsSha(commit)) throw new GitHostException("Not a commit id.");
        var body = new JsonObject
        {
            ["state"] = state switch { CommitState.Pending => "pending", CommitState.Success => "success", _ => "failure" },
            ["description"] = Truncate(description, 140),
            ["context"] = GitHostFactory.StatusContext,
        };
        if (SafeTargetUrl(targetUrl) is { } url) body["target_url"] = url;
        await SendAsync(HttpMethod.Post, $"{Repo}/statuses/{commit}", body, ct);
    }

    protected override async Task<string> CreateCommentAsync(int number, string body, CancellationToken ct) =>
        CommentId(await SendAsync(HttpMethod.Post, $"{Repo}/issues/{number}/comments", new JsonObject { ["body"] = body }, ct));

    protected override async Task<string> UpdateCommentAsync(int number, string commentId, string body, CancellationToken ct) =>
        CommentId(await SendAsync(HttpMethod.Patch, $"{Repo}/issues/comments/{commentId}", new JsonObject { ["body"] = body }, ct));
}

/// <summary>GitLab (gitlab.com or self-managed) over REST API v4, with a personal, project or group access token.</summary>
public sealed partial class GitLabHost(CodeRepoConfig config, string token, HttpClient http, SnapshotLimits limits) : GitHostBase(http, limits)
{
    private readonly string _api = ApiRoot(config.BaseUrl);
    private string Project => $"{_api}/projects/{Uri.EscapeDataString(config.Project.Trim().Trim('/'))}";

    /// <summary>"https://gitlab.example.com" → "https://gitlab.example.com/api/v4" (an explicit /api/v4 is kept).</summary>
    public static string ApiRoot(string baseUrl)
    {
        var b = baseUrl.Trim().TrimEnd('/');
        return b.EndsWith("/api/v4", StringComparison.OrdinalIgnoreCase) ? b : b + "/api/v4";
    }

    public override string RepoDisplay => config.Project.Trim().Trim('/');
    protected override string HostName => "GitLab";

    protected override void Authorize(HttpRequestMessage request)
    {
        if (token.Length > 0) request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);
    }

    protected override string ArchiveUrl(string commit) => $"{Project}/repository/archive.tar.gz?sha={commit}";

    public override async Task<string> TestConnectionAsync(CancellationToken ct = default)
    {
        var p = await SendAsync(HttpMethod.Get, Project, ct: ct);
        var name = (string?)p?["path_with_namespace"] ?? RepoDisplay;
        var head = await ResolveBranchHeadAsync(config.Branch, ct);
        return $"Connected to {name}: {config.Branch} is at {Short(head.Sha)}.";
    }

    public override async Task<GitCommitInfo> ResolveBranchHeadAsync(string branch, CancellationToken ct = default)
    {
        var b = await SendAsync(HttpMethod.Get, $"{Project}/repository/branches/{Uri.EscapeDataString(branch)}", ct: ct);
        var c = b?["commit"];
        var sha = (string?)c?["id"];
        if (!IsSha(sha)) throw new GitHostException($"GitLab did not return a commit for {branch}.");
        return new GitCommitInfo(sha!, (string?)c?["message"], (string?)c?["author_name"], (string?)c?["web_url"]);
    }

    public override async Task PostCommitStatusAsync(string commit, CommitState state, string description, string? targetUrl, CancellationToken ct = default)
    {
        if (!IsSha(commit)) throw new GitHostException("Not a commit id.");
        var body = new JsonObject
        {
            ["state"] = state switch { CommitState.Pending => "pending", CommitState.Success => "success", _ => "failed" },
            ["name"] = GitHostFactory.StatusContext,
            ["description"] = Truncate(description, 140),
        };
        if (SafeTargetUrl(targetUrl) is { } url) body["target_url"] = url;
        await SendAsync(HttpMethod.Post, $"{Project}/statuses/{commit}", body, ct);
    }

    protected override async Task<string> CreateCommentAsync(int number, string body, CancellationToken ct) =>
        CommentId(await SendAsync(HttpMethod.Post, $"{Project}/merge_requests/{number}/notes", new JsonObject { ["body"] = body }, ct));

    protected override async Task<string> UpdateCommentAsync(int number, string commentId, string body, CancellationToken ct) =>
        CommentId(await SendAsync(HttpMethod.Put, $"{Project}/merge_requests/{number}/notes/{commentId}", new JsonObject { ["body"] = body }, ct));
}
