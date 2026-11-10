using System.Text;
using System.Text.RegularExpressions;
using Meridian.Core.CodeSync;

namespace Meridian.Web.CodeSync.Hosts;

/// <summary>One <c>[submodule "name"]</c> section of a .gitmodules file.</summary>
public sealed record GitModuleEntry(string Name, string Path, string? Url, string? Branch, bool UpdateNone);

/// <summary>
/// A tolerant reader for .gitmodules (git-config syntax): sections, <c>key = value</c> lines, double-quoted values with
/// escapes, <c>#</c> and <c>;</c> comments, line continuations and case-insensitive names. Malformed lines are skipped,
/// never fatal: a broken .gitmodules must not fail the sync of the repository's own code.
/// </summary>
public static class GitModules
{
    public const string FileName = ".gitmodules";

    public static IReadOnlyList<GitModuleEntry> Parse(string? text)
    {
        var sections = new List<(string Name, Dictionary<string, string> Values)>();
        Dictionary<string, string>? current = null;
        foreach (var line in LogicalLines(text ?? ""))
        {
            var s = line.Trim();
            if (s.Length == 0 || s[0] is '#' or ';') continue;
            if (s[0] == '[')
            {
                current = null;
                var close = s.IndexOf(']');
                if (close < 0) continue;
                var header = s[1..close].Trim();
                string section, sub = "";
                var q = header.IndexOf('"');
                if (q >= 0)
                {
                    section = header[..q].Trim();
                    sub = Unquote(header[q..]);
                }
                else if (header.IndexOf('.') is var dot and > 0)
                {
                    section = header[..dot];   // deprecated [submodule.name] form
                    sub = header[(dot + 1)..];
                }
                else section = header;
                if (!section.Equals("submodule", StringComparison.OrdinalIgnoreCase)) continue;
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections.Add((sub, current));
                continue;
            }
            if (current is null) continue;
            var eq = s.IndexOf('=');
            var key = (eq < 0 ? s : s[..eq]).Trim();
            if (key.Length == 0 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) continue;
            current[key] = eq < 0 ? "true" : Value(s[(eq + 1)..]);
        }

        var list = new List<GitModuleEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, values) in sections)
        {
            var path = CodeRepoConfig.NormalizePath(values.GetValueOrDefault("path") ?? "");
            if (path.Length == 0 || !seen.Add(path)) continue;
            var url = values.GetValueOrDefault("url") is { Length: > 0 } u ? u.Trim() : null;
            var branch = values.GetValueOrDefault("branch") is { Length: > 0 } b ? b.Trim() : null;
            var update = values.GetValueOrDefault("update")?.Trim();
            list.Add(new GitModuleEntry(name, path, url, branch, string.Equals(update, "none", StringComparison.OrdinalIgnoreCase)));
        }
        return list;
    }

    /// <summary>Physical lines joined where one ends with an unescaped backslash.</summary>
    private static IEnumerable<string> LogicalLines(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.EndsWith('\\') && !raw.EndsWith("\\\\", StringComparison.Ordinal))
            {
                sb.Append(raw, 0, raw.Length - 1);
                continue;
            }
            sb.Append(raw);
            yield return sb.ToString();
            sb.Clear();
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>A value: quoted parts keep their spaces and comment characters; an unquoted # or ; starts a comment.</summary>
    private static string Value(string raw)
    {
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '"') { quoted = !quoted; continue; }
            if (!quoted && c is '#' or ';') break;
            if (c == '\\' && i + 1 < raw.Length)
            {
                var n = raw[++i];
                sb.Append(n switch { 'n' => '\n', 't' => '\t', 'b' => '\b', _ => n });
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.StartsWith('"')) s = s[1..];
        if (s.EndsWith('"')) s = s[..^1];
        return s.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
    }
}

/// <summary>
/// A repository on a git host, as a submodule URL names it: the web host ("github.com", "gitlab.example.com") and the
/// repository path ("owner/repo", "group/sub/project", "PROJECT/repo"). Compared case-insensitively.
/// </summary>
public sealed partial record GitRepoLocation(string Host, string Path)
{
    public string Key => $"{Host}/{Path}".ToLowerInvariant();

    public bool SameHost(GitRepoLocation other) => string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Host}/{Path}";

    /// <summary>The well-known public hosts, where a public submodule can be read without a token.</summary>
    public static readonly IReadOnlyDictionary<string, GitHostKind> PublicHosts = new Dictionary<string, GitHostKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["github.com"] = GitHostKind.GitHub,
        ["gitlab.com"] = GitHostKind.GitLab,
        ["bitbucket.org"] = GitHostKind.Bitbucket,
    };

    /// <summary>Aliases of the public hosts (API, SSH and www names) → the web host.</summary>
    private static string CanonicalHost(string host)
    {
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        return h switch
        {
            "www.github.com" or "api.github.com" or "ssh.github.com" => "github.com",
            "www.gitlab.com" or "altssh.gitlab.com" => "gitlab.com",
            "www.bitbucket.org" or "api.bitbucket.org" or "altssh.bitbucket.org" => "bitbucket.org",
            _ => h,
        };
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+$")]
    private static partial Regex Segment();

    /// <summary>The location of a connection's repository; null when it has no path (a numeric GitLab project id).</summary>
    public static GitRepoLocation? Of(CodeRepoConfig c)
    {
        if (!Uri.TryCreate(c.BaseUrl?.Trim(), UriKind.Absolute, out var u)) return null;
        var path = c.Host == GitHostKind.GitLab ? c.Project.Trim().Trim('/') : $"{c.Owner.Trim()}/{c.Repo.Trim()}";
        if (c.Host == GitHostKind.GitLab && path.All(char.IsAsciiDigit)) return null;
        return Make(CanonicalHost(u.Host), path);
    }

    private static GitRepoLocation? Make(string host, string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count > 0 && segments[^1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)) segments[^1] = segments[^1][..^4];
        if (host.Length == 0 || segments.Count < 2 || segments.Any(s => s is "." or ".." || !Segment().IsMatch(s))) return null;
        return new GitRepoLocation(host, string.Join('/', segments));
    }

    /// <summary>
    /// Resolves a submodule URL: absolute https / http / ssh / git URLs, scp-like <c>git@host:group/repo.git</c>, and
    /// relative <c>../lib.git</c> or <c>./lib</c> against <paramref name="parent"/> (git's rule: each "../" drops one
    /// segment of the parent's path). User names and passwords in the URL are ignored. Null for local paths and URLs
    /// that do not name a repository.
    /// </summary>
    public static GitRepoLocation? Resolve(string? url, GitRepoLocation? parent)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0) return null;
        if (u.StartsWith("./", StringComparison.Ordinal) || u.StartsWith("../", StringComparison.Ordinal))
        {
            if (parent is null) return null;
            var segments = parent.Path.Split('/').ToList();
            var rest = u;
            while (true)
            {
                if (rest.StartsWith("./", StringComparison.Ordinal)) rest = rest[2..];
                else if (rest.StartsWith("../", StringComparison.Ordinal))
                {
                    if (segments.Count == 0) return null;
                    segments.RemoveAt(segments.Count - 1);
                    rest = rest[3..];
                }
                else break;
            }
            if (rest.Length == 0) return null;
            return Make(parent.Host, string.Join('/', segments.Append(rest)));
        }
        if (u.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(u, UriKind.Absolute, out var abs) || abs.Scheme is not ("https" or "http" or "ssh" or "git" or "git+ssh")) return null;
            var path = Uri.UnescapeDataString(abs.AbsolutePath).Trim('/');
            // Bitbucket Data Center serves https clones at /scm/PROJECT/repo.git.
            var parts = path.Split('/');
            if (parts.Length == 3 && parts[0].Equals("scm", StringComparison.OrdinalIgnoreCase)) path = $"{parts[1]}/{parts[2]}";
            return Make(CanonicalHost(abs.Host), path);
        }
        var scp = ScpLike().Match(u);
        return scp.Success ? Make(CanonicalHost(scp.Groups["host"].Value), scp.Groups["path"].Value.TrimStart('/')) : null;
    }

    [GeneratedRegex(@"^(?:[^@/\s]+@)?(?<host>[A-Za-z0-9.\-]+):(?<path>[^\s]+)$")]
    private static partial Regex ScpLike();

    /// <summary>A URL as shown to people: without user name and password.</summary>
    public static string Redact(string? url)
    {
        var u = (url ?? "").Trim();
        if (Uri.TryCreate(u, UriKind.Absolute, out var abs) && abs.UserInfo.Length > 0 && u.Contains("://", StringComparison.Ordinal))
        {
            // Keep "git@" style user names (not secrets); drop anything with a password.
            if (abs.UserInfo.Contains(':')) return new UriBuilder(abs) { UserName = "", Password = "" }.Uri.ToString();
        }
        return u.Length > 500 ? u[..500] : u;
    }
}

/// <summary>A code-repo connection of the project whose credentials a submodule can use (its settings and token).</summary>
public sealed record SubmoduleCredential(string Name, CodeRepoConfig Config, string Token);

/// <summary>The merged snapshot: the repository's files plus its submodules' under their paths.</summary>
public sealed record SubmoduleSnapshot(CodeSnapshot Snapshot, IReadOnlyList<CodeRepoSubmodule> Submodules, IReadOnlyList<CodeSubmodulePin> Pins)
{
    /// <summary>Submodules whose code should have been read but was not (no access, not found).</summary>
    public int Missing => Submodules.Count(s => s.Status is CodeRepoSubmodule.NoAccess or CodeRepoSubmodule.NotFound);
}

/// <summary>
/// Fetches a repository snapshot with its git submodules. Archives leave submodules out, so after reading the
/// repository's archive this reads its .gitmodules, asks the host for the commit each submodule is pinned to (the
/// gitlink), fetches each submodule's archive at that commit and merges its files in under the submodule path,
/// recursively to <see cref="MaxDepth"/>, with cycles cut. One <see cref="SnapshotBudget"/> covers every archive.
/// <para>
/// Credentials, in order: a code-repo connection of the project for exactly that repository; the connection's own
/// token when the submodule is on the same host; anonymous access on github.com, gitlab.com and bitbucket.org. Each is
/// used only against the API address of the connection it belongs to — a URL in .gitmodules never chooses where a token
/// goes, nor any address at all: requests only go to configured connections and the fixed public API addresses.
/// A submodule that cannot be read is recorded with a warning; the sync continues without it.
/// </para>
/// </summary>
public sealed class SubmoduleFetcher(Func<CodeRepoConfig, string, IGitHost> hostFactory, ILogger? log = null)
{
    /// <summary>Submodules of submodules are followed this deep (1 = the repository's own).</summary>
    public const int MaxDepth = 3;
    /// <summary>Most submodules looked at in one snapshot (each costs API calls and an archive).</summary>
    public const int MaxSubmodules = 50;

    private sealed record Access(IGitHost Host, CodeRepoConfig Config, string Via);

    private sealed class State
    {
        public required CodeRepoConfig Root;
        public required string RootToken;
        public required GitRepoLocation? RootLocation;
        public required IReadOnlyList<SubmoduleCredential> Others;
        public required Func<string, bool> Include;
        public required SnapshotBudget Budget;
        public readonly List<CodeFile> Files = [];
        public readonly List<string> Skipped = [];
        public readonly List<CodeRepoSubmodule> Submodules = [];
        public readonly List<CodeSubmodulePin> Pins = [];
        public int Looked;
    }

    /// <summary>
    /// Fetches <paramref name="commit"/> of the connection <paramref name="config"/> (through <paramref name="host"/>,
    /// whose token is <paramref name="token"/>) with its submodules. <paramref name="others"/> are the project's other
    /// code-repo connections. Limit errors (<see cref="SnapshotLimitException"/>) and failures of the repository's own
    /// archive propagate; a submodule's failure does not.
    /// </summary>
    public async Task<SubmoduleSnapshot> FetchAsync(IGitHost host, CodeRepoConfig config, string token, string commit, Func<string, bool> include,
                                                    IReadOnlyList<SubmoduleCredential> others, SnapshotBudget budget, CancellationToken ct = default)
    {
        var paths = config.Paths.Select(CodeRepoConfig.NormalizePath).Where(p => p.Length > 0).ToList();
        var main = await host.FetchSnapshotAsync(commit, WithGitModules(paths), WithGitModules(include), budget, ct);
        var state = new State
        {
            Root = config, RootToken = token, RootLocation = GitRepoLocation.Of(config), Others = others, Include = include, Budget = budget,
        };
        var gitmodules = Take(main, state.Files, state.Skipped, "");
        if (gitmodules is not null)
        {
            var root = new Access(host, config, "");
            await VisitAsync(state, root, state.RootLocation, commit, gitmodules, "", 1, [state.RootLocation?.Key ?? ""], ct);
        }
        if (state.Files.Count > budget.Limits.MaxFiles)
            throw new SnapshotLimitException($"More than {budget.Limits.MaxFiles:N0} source files in the watched folders. Narrow the folders to sync.");
        state.Files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new SubmoduleSnapshot(new CodeSnapshot(state.Files, state.Skipped), state.Submodules, state.Pins);
    }

    private static IReadOnlyList<string> WithGitModules(IReadOnlyList<string> paths) => paths.Count == 0 ? paths : [.. paths, GitModules.FileName];
    private static Func<string, bool> WithGitModules(Func<string, bool> include) => p => p == GitModules.FileName || include(p);

    /// <summary>Copies a snapshot's files (under <paramref name="prefix"/>) leaving .gitmodules out; returns its text.</summary>
    private static string? Take(CodeSnapshot snapshot, List<CodeFile> files, List<string> skipped, string prefix)
    {
        string? gitmodules = null;
        foreach (var f in snapshot.Files)
        {
            if (f.Path == GitModules.FileName) { gitmodules = f.Content; continue; }
            files.Add(prefix.Length == 0 ? f : f with { Path = prefix + f.Path });
        }
        skipped.AddRange(snapshot.Skipped.Select(s => prefix + s));
        return gitmodules;
    }

    /// <summary>
    /// The folders to read inside a submodule at <paramref name="full"/> for the connection's folders to include:
    /// empty = all of it; null = none (the submodule is outside every included folder).
    /// </summary>
    public static IReadOnlyList<string>? PathsInside(string full, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return [];
        var inside = new List<string>();
        foreach (var p in paths.Select(CodeRepoConfig.NormalizePath).Where(p => p.Length > 0))
        {
            if (p == full || full.StartsWith(p + "/", StringComparison.Ordinal)) return [];   // the whole submodule is included
            if (p.StartsWith(full + "/", StringComparison.Ordinal)) inside.Add(p[(full.Length + 1)..]);
        }
        return inside.Count == 0 ? null : inside;
    }

    private async Task VisitAsync(State state, Access parent, GitRepoLocation? parentLocation, string commit, string gitmodules, string prefix,
                                  int depth, IReadOnlyList<string> ancestors, CancellationToken ct)
    {
        var entries = GitModules.Parse(gitmodules);
        if (entries.Count == 0) return;
        if (!state.Root.IncludeSubmodules)
        {
            // Listed, but not even their pins are asked for.
            foreach (var e in entries)
                state.Submodules.Add(new CodeRepoSubmodule
                {
                    Path = prefix + e.Path, Url = GitRepoLocation.Redact(e.Url), Depth = depth,
                    Status = CodeRepoSubmodule.Skipped, Message = "Submodules are turned off for this connection.",
                });
            return;
        }

        // First pass: what to look at, and what is skipped without asking the host.
        var todo = new List<(GitModuleEntry Entry, CodeRepoSubmodule Info, IReadOnlyList<string>? Inside)>();
        foreach (var e in entries)
        {
            var info = new CodeRepoSubmodule { Path = prefix + e.Path, Url = GitRepoLocation.Redact(e.Url), Depth = depth };
            state.Submodules.Add(info);
            if (e.Path.Split('/').Any(s => s is "." or "..") || e.Path.Contains(':') || e.Path.Contains('\\'))
            {
                (info.Status, info.Message) = (CodeRepoSubmodule.NotFound, "Unsafe path in .gitmodules.");
                continue;
            }
            if (depth > MaxDepth)
            {
                (info.Status, info.Message) = (CodeRepoSubmodule.Skipped, $"Nested deeper than {MaxDepth} levels.");
                continue;
            }
            if (++state.Looked > MaxSubmodules)
            {
                (info.Status, info.Message) = (CodeRepoSubmodule.Skipped, $"More than {MaxSubmodules} submodules.");
                continue;
            }
            todo.Add((e, info, PathsInside(info.Path, state.Root.Paths)));
        }
        if (todo.Count == 0) return;

        IReadOnlyDictionary<string, string> shas;
        try { shas = await parent.Host.ResolveSubmoduleCommitsAsync(commit, todo.Select(t => t.Entry.Path).ToList(), ct); }
        catch (GitHostException ex)
        {
            log?.LogWarning("Reading the submodule commits of {Repo} failed: {Error}", parent.Host.RepoDisplay, ex.Message);
            foreach (var (_, info, _) in todo)
                (info.Status, info.Message) = (CodeRepoSubmodule.NotFound, $"Could not read the pinned commit: {ex.Message}");
            return;
        }

        foreach (var (entry, info, inside) in todo)
        {
            ct.ThrowIfCancellationRequested();
            var location = GitRepoLocation.Resolve(entry.Url, parentLocation);
            if (!shas.TryGetValue(entry.Path, out var sha))
            {
                (info.Status, info.Message) = (CodeRepoSubmodule.NotFound, "No submodule at this path in the commit (.gitmodules is out of date?).");
                continue;
            }
            info.Sha = sha;
            var compare = location is null ? null : CompareUrl(location, KindOf(location, state));
            var pin = new CodeSubmodulePin(info.Path, sha, compare, Fetched: false);

            string? skip = null;
            if (entry.UpdateNone) skip = "update = none in .gitmodules.";
            else if (state.Root.SkipSubmodules.Any(s => info.Path == s || info.Path.StartsWith(s + "/", StringComparison.Ordinal))) skip = "Skipped in the connection's settings.";
            else if (inside is null) skip = "Outside the folders to include.";
            if (skip is not null)
            {
                (info.Status, info.Message) = (CodeRepoSubmodule.Skipped, skip);
                state.Pins.Add(pin);
                continue;
            }
            if (location is null)
            {
                (info.Status, info.Message) = (CodeRepoSubmodule.NotFound, entry.Url is null ? "No url in .gitmodules." : "The submodule URL does not name a repository Meridian can read.");
                state.Pins.Add(pin);
                continue;
            }
            if (ancestors.Contains(location.Key, StringComparer.Ordinal))
            {
                (info.Status, info.Message) = (CodeRepoSubmodule.Skipped, "Cycle: the repository includes itself.");
                state.Pins.Add(pin);
                continue;
            }

            var fetched = await FetchOneAsync(state, location, sha, inside!, info, ct);
            state.Pins.Add(pin with { Fetched = fetched is not null });
            if (fetched is null) continue;
            var (access, snapshot) = fetched.Value;
            var before = state.Files.Count;
            var nested = Take(snapshot, state.Files, state.Skipped, info.Path + "/");
            info.Files = state.Files.Count - before;
            info.Status = CodeRepoSubmodule.Fetched;
            if (nested is not null)
                await VisitAsync(state, access, location, sha, nested, info.Path + "/", depth + 1, [.. ancestors, location.Key], ct);
        }
    }

    /// <summary>Tries each way in to the submodule's repository; null (and a warning on <paramref name="info"/>) when none works.</summary>
    private async Task<(Access, CodeSnapshot)?> FetchOneAsync(State state, GitRepoLocation location, string sha, IReadOnlyList<string> inside,
                                                              CodeRepoSubmodule info, CancellationToken ct)
    {
        var candidates = Candidates(state, location).ToList();
        if (candidates.Count == 0)
        {
            (info.Status, info.Message) = (CodeRepoSubmodule.NoAccess,
                $"{location.Host} is not this connection's host and no connection of the project is for {location}. Add one to read it.");
            log?.LogWarning("Submodule {Path}: no credentials for {Repo}", info.Path, location);
            return null;
        }
        string? lastError = null;
        foreach (var access in candidates)
        {
            try
            {
                var snapshot = await access.Host.FetchSnapshotAsync(sha, WithGitModules(inside), WithGitModules(state.Include), state.Budget, ct);
                if (access.Via.Length > 0) info.Message = access.Via;
                return (access, snapshot);
            }
            catch (SnapshotLimitException) { throw; }
            catch (GitHostException ex)
            {
                lastError = ex.Message;
                log?.LogInformation("Submodule {Path} ({Repo}) {Via}: {Error}", info.Path, location, access.Via.Length > 0 ? access.Via : "with the connection's token", ex.Message);
            }
        }
        (info.Status, info.Message) = (CodeRepoSubmodule.NoAccess, $"Could not read {location}: {lastError}");
        log?.LogWarning("Submodule {Path}: could not read {Repo}: {Error}", info.Path, location, lastError);
        return null;
    }

    private IEnumerable<Access> Candidates(State state, GitRepoLocation location)
    {
        // 1. A connection of the project for exactly this repository: its own settings and token.
        foreach (var other in state.Others)
            if (GitRepoLocation.Of(other.Config)?.Key == location.Key && other.Token.Length > 0)
                yield return new Access(hostFactory(other.Config, other.Token), other.Config, $"Read with the credentials of “{other.Name}”.");
        // 2. Same host as this connection: its token, against its API address.
        if (state.RootLocation is { } root && root.SameHost(location) && For(state.Root, location) is { } same)
            yield return new Access(hostFactory(same, state.RootToken), same, "");
        // 3. A public host: anonymous, against the host's public API.
        if (GitRepoLocation.PublicHosts.TryGetValue(location.Host, out var kind)
            && For(new CodeRepoConfig { Host = kind, BaseUrl = CodeRepoConfig.DefaultBaseUrl(kind) }, location) is { } anonymous)
            yield return new Access(hostFactory(anonymous, ""), anonymous, "Read anonymously (public repository).");
    }

    /// <summary><paramref name="template"/>'s host settings pointed at the repository <paramref name="location"/>; null when
    /// the path does not fit the host (GitHub and Bitbucket need owner/repo).</summary>
    private static CodeRepoConfig? For(CodeRepoConfig template, GitRepoLocation location)
    {
        var c = new CodeRepoConfig { Host = template.Host, BaseUrl = template.BaseUrl, Branch = template.Branch, Language = template.Language };
        var parts = location.Path.Split('/');
        if (template.Host == GitHostKind.GitLab) c.Project = location.Path;
        else if (parts.Length == 2) (c.Owner, c.Repo) = (parts[0], parts[1]);
        else return null;
        c.Normalize();
        return c.Validate() is null ? c : null;
    }

    private static GitHostKind? KindOf(GitRepoLocation location, State state)
    {
        if (state.Others.FirstOrDefault(o => GitRepoLocation.Of(o.Config)?.Key == location.Key) is { } other) return other.Config.Host;
        if (state.RootLocation is { } root && root.SameHost(location)) return state.Root.Host;
        return GitRepoLocation.PublicHosts.TryGetValue(location.Host, out var k) ? k : null;
    }

    /// <summary>A link comparing two commits of the repository ("{old}" and "{new}" stand for them); null when unknown.</summary>
    public static string? CompareUrl(GitRepoLocation location, GitHostKind? kind)
    {
        var web = $"https://{location.Host}/{location.Path}";
        return kind switch
        {
            GitHostKind.GitHub => web + "/compare/{old}...{new}",
            GitHostKind.GitLab => web + "/-/compare/{old}...{new}",
            GitHostKind.Bitbucket when location.Host == "bitbucket.org" => web + "/branches/compare/{new}%0D{old}",
            GitHostKind.Bitbucket when location.Path.Split('/') is [var project, var repo] =>
                $"https://{location.Host}/projects/{project}/repos/{repo}/compare/commits?sourceBranch={{new}}&targetBranch={{old}}",
            _ => null,
        };
    }
}
