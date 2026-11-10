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
public enum GitHostKind { GitHub, GitLab }

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

/// <summary>
/// Non-secret settings of a code repository connection, stored as the integration's config JSON. The access token is
/// the connection's protected secret; the webhook secret is encrypted with the same protector and kept here.
/// </summary>
public sealed partial class CodeRepoConfig
{
    public const string GitHubApi = "https://api.github.com";
    public const string GitLabCom = "https://gitlab.com";

    public GitHostKind Host { get; set; } = GitHostKind.GitHub;
    /// <summary>GitHub API root (https://api.github.com or https://ghe.example.com/api/v3), or the GitLab server (https://gitlab.com).</summary>
    public string BaseUrl { get; set; } = GitHubApi;
    /// <summary>GitHub owner (user or organisation).</summary>
    public string Owner { get; set; } = "";
    /// <summary>GitHub repository name.</summary>
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
    /// <summary>Webhook secret, encrypted with the integrations protector. Never shown again after creation.</summary>
    public string? ProtectedWebhookSecret { get; set; }
    public CodeRepoLastSync? LastSync { get; set; }

    public static CodeRepoConfig Parse(string json) =>
        JsonSerializer.Deserialize<CodeRepoConfig>(json, IntegrationJson.Options) ?? new CodeRepoConfig();

    public string ToJson() => JsonSerializer.Serialize(this, IntegrationJson.Options);

    public CodeRepoConfig Clone() => Parse(ToJson());

    public static string DefaultBaseUrl(GitHostKind host) => host == GitHostKind.GitLab ? GitLabCom : GitHubApi;

    /// <summary>"owner/repo" or the GitLab project path.</summary>
    [JsonIgnore]
    public string Display => Host == GitHostKind.GitHub ? $"{Owner.Trim()}/{Repo.Trim()}" : Project.Trim();

    /// <summary>The repository name: the last part of the GitHub repo or the GitLab path.</summary>
    [JsonIgnore]
    public string RepoName => Host == GitHostKind.GitHub ? Repo.Trim() : Project.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

    [JsonIgnore]
    public string EffectiveRootPackage => RootPackage is { Length: > 0 } r && r.Trim().Length > 0 ? r.Trim() : RepoName is { Length: > 0 } n ? n : "Code";

    /// <summary>Trims fields and normalises the watched folders ("./src/" → "src").</summary>
    public void Normalize()
    {
        BaseUrl = (BaseUrl ?? "").Trim().TrimEnd('/');
        if (BaseUrl.Length == 0) BaseUrl = DefaultBaseUrl(Host);
        Owner = (Owner ?? "").Trim();
        Repo = (Repo ?? "").Trim();
        if (Repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) Repo = Repo[..^4];
        Project = (Project ?? "").Trim().Trim('/');
        Branch = (Branch ?? "").Trim();
        if (Branch.StartsWith("refs/heads/", StringComparison.Ordinal)) Branch = Branch["refs/heads/".Length..];
        if (Branch.Length == 0) Branch = "main";
        Paths = (Paths ?? []).Select(NormalizePath).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
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
        if (!Uri.TryCreate(BaseUrl?.Trim(), UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)))
            return "The server URL must be an absolute https:// URL.";
        if (!string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) return "The server URL must not have a query or fragment.";
        if (Host == GitHostKind.GitHub)
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
        if (!Meridian.Serialization.CodeSync.CodeReaders.Languages.Contains(Language)) return "The language must be auto, csharp, typescript, java or python.";
        return null;
    }
}
