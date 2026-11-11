using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Meridian.Web.Integrations;

namespace Meridian.Web.CodeSync.Hosts;

/// <summary>Integration kind of a code repository connection.</summary>
public static class CodeRepoKinds
{
    public const string CodeRepo = "code-repo";
}

[JsonConverter(typeof(JsonStringEnumConverter<GitHostKind>))]
public enum GitHostKind { GitHub, GitLab, Bitbucket }

/// <summary>What happened in the most recent sync of a connection (kept in the connection's config).</summary>
public sealed class CodeRepoLastSync
{
    public DateTime At { get; set; }
    public string Commit { get; set; } = "";
    /// <summary>"queued", "running", "no-changes", "awaiting-review", "accepted", "failed".</summary>
    public string Status { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? ReviewUrl { get; set; }
}

/// <summary>What happened to a git submodule in the most recent sync of the followed branch (kept in the connection's config).</summary>
public sealed class CodeRepoSubmodule
{
    public const string Fetched = "fetched";
    public const string Skipped = "skipped";
    public const string NoAccess = "no-access";
    public const string NotFound = "not-found";

    /// <summary>Path in the merged snapshot ("libs/core"; nested ones under their parent's path).</summary>
    public string Path { get; set; } = "";
    /// <summary>The URL from .gitmodules, without any user name or password it carried.</summary>
    public string Url { get; set; } = "";
    /// <summary>The commit the parent pins it to; empty when unknown.</summary>
    public string Sha { get; set; } = "";
    /// <summary><see cref="Fetched"/>, <see cref="Skipped"/>, <see cref="NoAccess"/> or <see cref="NotFound"/>.</summary>
    public string Status { get; set; } = "";
    /// <summary>Why it was skipped or not fetched, and whose credentials read it ("anonymous", another connection).</summary>
    public string? Message { get; set; }
    /// <summary>1 for a submodule of the repository, 2–3 for nested ones.</summary>
    public int Depth { get; set; } = 1;
    /// <summary>Source files it added to the snapshot.</summary>
    public int Files { get; set; }
}

/// <summary>An open pull / merge request with a preview branch (kept in the connection's config).</summary>
public sealed class CodeRepoPullRequest
{
    public int Number { get; set; }
    public string? Title { get; set; }
    public string? Url { get; set; }
    /// <summary>The head commit last synced (or queued).</summary>
    public string Head { get; set; } = "";
    /// <summary>As <see cref="CodeRepoLastSync.Status"/>.</summary>
    public string Status { get; set; } = "";
    public string Summary { get; set; } = "";
    /// <summary>The preview branch in Meridian.</summary>
    public string? ReviewUrl { get; set; }
    /// <summary>Id of Meridian's comment on the pull request, updated in place on later syncs.</summary>
    public string? CommentId { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Non-secret settings of a code repository connection, stored as the integration's config JSON. The access token is
/// the connection's protected secret; the webhook secret is encrypted with the same protector and kept here.
/// </summary>
public sealed partial class CodeRepoConfig
{
    public const string GitHubApi = "https://api.github.com";
    public const string GitLabCom = "https://gitlab.com";
    public const string BitbucketCloudApi = "https://api.bitbucket.org/2.0";

    /// <summary>Most open pull requests remembered per connection (the oldest are forgotten).</summary>
    public const int MaxPullRequests = 100;

    public GitHostKind Host { get; set; } = GitHostKind.GitHub;
    /// <summary>GitHub API root (https://api.github.com or https://ghe.example.com/api/v3), the GitLab server
    /// (https://gitlab.com), or Bitbucket: https://api.bitbucket.org/2.0 (Cloud) or the Data Center / Server address.</summary>
    public string BaseUrl { get; set; } = GitHubApi;
    /// <summary>GitHub owner (user or organisation); Bitbucket Cloud workspace or Data Center project key.</summary>
    public string Owner { get; set; } = "";
    /// <summary>GitHub repository name; Bitbucket repository slug.</summary>
    public string Repo { get; set; } = "";
    /// <summary>GitLab project path ("group/sub/project") or numeric id.</summary>
    public string Project { get; set; } = "";
    public string Branch { get; set; } = "main";
    /// <summary>Folders to include (repository-relative); empty means the whole repository.</summary>
    public List<string> Paths { get; set; } = [];
    /// <summary>Package that receives the code; empty means the repository name.</summary>
    public string? RootPackage { get; set; }
    /// <summary>"auto" (every supported language, picked by file extension), "csharp", "typescript", "java" or "python".</summary>
    public string Language { get; set; } = "auto";
    /// <summary>Sync on every push to <see cref="Branch"/> (webhook); otherwise only with "Sync now".</summary>
    public bool AutoSync { get; set; } = true;
    /// <summary>Post a "meridian/model" commit status back to the host.</summary>
    public bool PostStatus { get; set; } = true;
    /// <summary>Sync pull / merge requests into the branch into preview branches (<c>code-sync/&lt;repo&gt;/pr-&lt;n&gt;</c>).</summary>
    public bool PreviewPullRequests { get; set; } = true;
    /// <summary>Keep one Meridian comment on each previewed pull request with the model changes (updated in place).</summary>
    public bool CommentOnPullRequests { get; set; } = true;
    /// <summary>Create diagrams for new packages and blocks. The model root's <c>codeSync.autoDiagrams</c> field overrides it when set.</summary>
    public bool AutoDiagrams { get; set; } = true;
    /// <summary>Keep deployment artifacts that «manifest» the synced code (one for the repository, or one per top-level
    /// package), so the code can be deployed to nodes. Off by default.</summary>
    public Meridian.Core.CodeSync.CodeArtifactMode DeploymentArtifacts { get; set; } = Meridian.Core.CodeSync.CodeArtifactMode.None;
    /// <summary>Also read the code of git submodules (archives leave it out): each is fetched at the commit the
    /// repository pins it to and merged in under its path (docs/CODE-SYNC.md, "Submodules").</summary>
    public bool IncludeSubmodules { get; set; } = true;
    /// <summary>Submodule paths not to fetch (repository-relative; a nested one by its full path).</summary>
    public List<string> SkipSubmodules { get; set; } = [];
    /// <summary>The submodules found by the most recent sync of <see cref="Branch"/>.</summary>
    public List<CodeRepoSubmodule> Submodules { get; set; } = [];
    /// <summary>Open pull requests with a preview branch, by number.</summary>
    public List<CodeRepoPullRequest> PullRequests { get; set; } = [];
    /// <summary>Webhook secret, encrypted with the integrations protector. Never shown again after creation.</summary>
    public string? ProtectedWebhookSecret { get; set; }
    public CodeRepoLastSync? LastSync { get; set; }

    public static CodeRepoConfig Parse(string json) =>
        JsonSerializer.Deserialize<CodeRepoConfig>(json, IntegrationJson.Options) ?? new CodeRepoConfig();

    public string ToJson() => JsonSerializer.Serialize(this, IntegrationJson.Options);

    public CodeRepoConfig Clone() => Parse(ToJson());

    public static string DefaultBaseUrl(GitHostKind host) => host switch
    {
        GitHostKind.GitLab => GitLabCom,
        GitHostKind.Bitbucket => BitbucketCloudApi,
        _ => GitHubApi,
    };

    /// <summary>The host's display name ("Bitbucket Cloud", "Bitbucket Data Center").</summary>
    [JsonIgnore]
    public string HostLabel => Host switch
    {
        GitHostKind.GitLab => "GitLab",
        GitHostKind.Bitbucket => IsBitbucketCloud ? "Bitbucket Cloud" : "Bitbucket Data Center",
        _ => "GitHub",
    };

    /// <summary>A Bitbucket connection to bitbucket.org (Cloud) rather than a Data Center / Server instance.</summary>
    [JsonIgnore]
    public bool IsBitbucketCloud => Host == GitHostKind.Bitbucket && IsBitbucketCloudUrl(BaseUrl);

    public static bool IsBitbucketCloudUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && (u.Host.Equals("api.bitbucket.org", StringComparison.OrdinalIgnoreCase) || u.Host.Equals("bitbucket.org", StringComparison.OrdinalIgnoreCase));

    /// <summary>"owner/repo", the GitLab project path, or "workspace/repo" / "PROJECT/repo" for Bitbucket.</summary>
    [JsonIgnore]
    public string Display => Host == GitHostKind.GitLab ? Project.Trim() : $"{Owner.Trim()}/{Repo.Trim()}";

    /// <summary>The repository name: the last part of the GitHub / Bitbucket repo or the GitLab path.</summary>
    [JsonIgnore]
    public string RepoName => Host == GitHostKind.GitLab ? Project.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "" : Repo.Trim();

    [JsonIgnore]
    public string EffectiveRootPackage => RootPackage is { Length: > 0 } r && r.Trim().Length > 0 ? r.Trim() : RepoName is { Length: > 0 } n ? n : "Code";

    /// <summary>Trims fields and normalises the watched folders ("./src/" → "src").</summary>
    public void Normalize()
    {
        BaseUrl = (BaseUrl ?? "").Trim().TrimEnd('/');
        // Empty, or another host's default (the host was switched): this host's default.
        if (BaseUrl.Length == 0 || Enum.GetValues<GitHostKind>().Any(h => h != Host && string.Equals(BaseUrl, DefaultBaseUrl(h), StringComparison.OrdinalIgnoreCase)))
            BaseUrl = DefaultBaseUrl(Host);
        if (Host == GitHostKind.Bitbucket && IsBitbucketCloudUrl(BaseUrl)) BaseUrl = BitbucketCloudApi;   // bitbucket.org → its API root
        Owner = (Owner ?? "").Trim();
        Repo = (Repo ?? "").Trim();
        if (Repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) Repo = Repo[..^4];
        Project = (Project ?? "").Trim().Trim('/');
        Branch = (Branch ?? "").Trim();
        if (Branch.StartsWith("refs/heads/", StringComparison.Ordinal)) Branch = Branch["refs/heads/".Length..];
        if (Branch.Length == 0) Branch = "main";
        Paths = (Paths ?? []).Select(NormalizePath).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        SkipSubmodules = (SkipSubmodules ?? []).Select(NormalizePath).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        RootPackage = RootPackage?.Trim() is { Length: > 0 } r ? r : null;
        Language = string.IsNullOrWhiteSpace(Language) ? "auto" : Language.Trim().ToLowerInvariant();
    }

    public static string NormalizePath(string p)
    {
        var s = (p ?? "").Trim().Replace('\\', '/');
        while (s.StartsWith("./", StringComparison.Ordinal)) s = s[2..];
        return s.Trim('/');
    }

    /// <summary>True when <paramref name="path"/> (repository-relative) is inside one of the watched folders.</summary>
    public static bool InPaths(string path, IReadOnlyList<string> paths) =>
        paths.Count == 0 || paths.Any(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal));

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+$")]
    private static partial Regex NamePart();

    /// <summary>Validates the settings; returns an error message or null.</summary>
    public string? Validate()
    {
        if (!Uri.TryCreate(BaseUrl?.Trim(), UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) || u.Host.Length == 0)
            return "The server URL must be an absolute http:// or https:// URL.";
        if (!string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) return "The server URL must not have a query or fragment.";
        if (Host == GitHostKind.Bitbucket)
        {
            if (!NamePart().IsMatch(Owner?.Trim() ?? "")) return IsBitbucketCloud ? "Enter the Bitbucket workspace." : "Enter the Bitbucket project key.";
            if (!NamePart().IsMatch(Repo?.Trim() ?? "") || (Repo ?? "").Trim() is "." or "..") return "Enter the repository slug.";
        }
        else if (Host == GitHostKind.GitHub)
        {
            if (!NamePart().IsMatch(Owner?.Trim() ?? "")) return "Enter the repository owner (user or organisation).";
            if (!NamePart().IsMatch(Repo?.Trim() ?? "") || (Repo ?? "").Trim() is "." or "..") return "Enter the repository name.";
        }
        else
        {
            var parts = (Project ?? "").Trim().Trim('/').Split('/');
            if (parts.Length == 0 || parts.Any(p => !NamePart().IsMatch(p) || p is "." or "..")) return "Enter the GitLab project path (group/project) or numeric id.";
        }
        var b = Branch?.Trim() ?? "";
        if (b.Length == 0 || b.Length > 250 || b.Contains("..", StringComparison.Ordinal) || b.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
            return "Enter a valid branch name.";
        foreach (var p in Paths ?? [])
        {
            var n = NormalizePath(p);
            if (n.Split('/').Any(s => s is ".." or ".") || n.Contains(':')) return $"“{p}” is not a valid folder.";
        }
        foreach (var p in SkipSubmodules ?? [])
        {
            var n = NormalizePath(p);
            if (n.Split('/').Any(s => s is ".." or ".") || n.Contains(':')) return $"“{p}” is not a valid submodule path.";
        }
        if (!Meridian.Serialization.CodeSync.CodeReaders.Languages.Contains(Language)) return "The language must be auto, csharp, typescript, java or python.";
        return null;
    }
}
