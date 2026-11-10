using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Meridian.Web.CodeSync.Hosts;

/// <summary>A failure talking to a git host or reading its archive. The message never contains credentials.</summary>
public sealed class GitHostException(string message, HttpStatusCode? status = null) : Exception(message)
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
}

/// <summary>Builds host clients over a supplied <see cref="HttpClient"/> (tests pass one with a fake handler).</summary>
public static class GitHostFactory
{
    public const string StatusContext = "meridian/model";

    public static IGitHost Create(CodeRepoConfig config, string token, HttpClient http, SnapshotLimits? limits = null) => config.Host switch
    {
        GitHostKind.GitHub => new GitHubHost(config, token, http, limits ?? SnapshotLimits.Default),
        GitHostKind.GitLab => new GitLabHost(config, token, http, limits ?? SnapshotLimits.Default),
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

    public abstract Task<string> TestConnectionAsync(CancellationToken ct = default);
    public abstract Task<GitCommitInfo> ResolveBranchHeadAsync(string branch, CancellationToken ct = default);
    public abstract Task PostCommitStatusAsync(string commit, CommitState state, string description, string? targetUrl, CancellationToken ct = default);

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

    /// <summary>A readable error for a failed call; quotes the host's own message, never the request.</summary>
    protected GitHostException Error(HttpStatusCode status, string body)
    {
        string? detail = null;
        try { detail = (string?)JsonNode.Parse(body)?["message"]; } catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { }
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

    public async Task<CodeSnapshot> FetchSnapshotAsync(string commit, IReadOnlyList<string> paths, Func<string, bool> include, CancellationToken ct = default)
    {
        if (!IsSha(commit)) throw new GitHostException("Not a commit id.");
        using var request = Request(HttpMethod.Get, ArchiveUrl(commit), "application/octet-stream");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-gzip"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DownloadTimeout);
        using var response = await Send(request, timeout.Token, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw Error(response.StatusCode, await ReadSmallAsync(response, timeout.Token));
        if (response.Content.Headers.ContentLength > limits.MaxArchiveBytes)
            throw new GitHostException($"The repository archive is larger than {limits.MaxArchiveBytes / (1024 * 1024)} MB.");
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await SnapshotArchive.ReadTarGzAsync(stream, paths.Select(CodeRepoConfig.NormalizePath).Where(p => p.Length > 0).ToList(), include, limits, timeout.Token);
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

    /// <summary>A full or abbreviated hexadecimal commit id (SHA-1 or SHA-256).</summary>
    public static bool IsSha(string? s) => s is { Length: >= 7 and <= 64 } && s.All(Uri.IsHexDigit);

    protected static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    protected static string? SafeTargetUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.ToString() : null;

    protected static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}

/// <summary>GitHub (github.com or Enterprise Server) over the REST API, with a fine-grained or classic token.</summary>
public sealed class GitHubHost(CodeRepoConfig config, string token, HttpClient http, SnapshotLimits limits) : GitHostBase(http, limits)
{
    private readonly string _api = config.BaseUrl.Trim().TrimEnd('/');
    private string Repo => $"{_api}/repos/{Uri.EscapeDataString(config.Owner.Trim())}/{Uri.EscapeDataString(config.Repo.Trim())}";

    public override string RepoDisplay => $"{config.Owner.Trim()}/{config.Repo.Trim()}";
    protected override string HostName => "GitHub";

    protected override void Authorize(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    // Answers with a redirect to a short-lived codeload URL; the client follows it (through the outbound guard) and drops
    // the Authorization header on the way.
    protected override string ArchiveUrl(string commit) => $"{Repo}/tarball/{commit}";

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
}

/// <summary>GitLab (gitlab.com or self-managed) over REST API v4, with a personal, project or group access token.</summary>
public sealed class GitLabHost(CodeRepoConfig config, string token, HttpClient http, SnapshotLimits limits) : GitHostBase(http, limits)
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

    protected override void Authorize(HttpRequestMessage request) => request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);

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
}
