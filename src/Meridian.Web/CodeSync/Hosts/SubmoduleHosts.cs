using System.Net;
using System.Text.Json.Nodes;

namespace Meridian.Web.CodeSync.Hosts;

// Submodule gitlinks per host: the commit a repository pins each submodule path to, read through the host's API at the
// parent commit (archives only hold an empty folder there). Every call goes to the connection's own API host with its
// own token, like the rest of the host client. A path the host does not report as a submodule is left out.

internal static class SubmoduleApi
{
    /// <summary>Most listing pages read for one folder.</summary>
    public const int MaxPages = 20;

    public static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>"libs/core lib" → "libs/core%20lib" (each segment escaped, slashes kept).</summary>
    public static string EscapePath(string path) => string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    /// <summary>The folder holding a path ("libs/core" → "libs", "core" → "").</summary>
    public static string Folder(string path) => path.LastIndexOf('/') is var i and > 0 ? path[..i] : "";

    public static Dictionary<string, string> NewResult() => new(StringComparer.Ordinal);

    public static bool SameCommit(string a, string b) =>
        a.Length >= 7 && b.Length >= 7 && (a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase));
}

/// <summary>GitHub: the contents API at a submodule path answers <c>{"type":"submodule","sha":…}</c>.</summary>
public sealed partial class GitHubHost
{
    public override async Task<IReadOnlyDictionary<string, string>> ResolveSubmoduleCommitsAsync(string commit, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (!IsSha(commit)) throw new GitHostException("Not a commit id.");
        var result = SubmoduleApi.NewResult();
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            JsonNode? node;
            try { node = await SendAsync(HttpMethod.Get, $"{Repo}/contents/{SubmoduleApi.EscapePath(path)}?ref={commit}", ct: ct); }
            catch (GitHostException ex) when (ex.Status == HttpStatusCode.NotFound) { continue; }
            if (node is JsonObject o && SubmoduleApi.Str(o["type"]) == "submodule" && SubmoduleApi.Str(o["sha"]) is { } sha && IsSha(sha)) result[path] = sha;
        }
        return result;
    }
}

/// <summary>GitLab: the repository tree of the submodule's folder lists it as <c>{"type":"commit","id":…}</c>.</summary>
public sealed partial class GitLabHost
{
    public override async Task<IReadOnlyDictionary<string, string>> ResolveSubmoduleCommitsAsync(string commit, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (!IsSha(commit)) throw new GitHostException("Not a commit id.");
        var result = SubmoduleApi.NewResult();
        foreach (var folder in paths.Distinct(StringComparer.Ordinal).GroupBy(SubmoduleApi.Folder))
        {
            var wanted = folder.ToHashSet(StringComparer.Ordinal);
            for (var page = 1; page <= SubmoduleApi.MaxPages && wanted.Count > 0; page++)
            {
                var url = $"{Project}/repository/tree?ref={commit}&per_page=100&page={page}" + (folder.Key.Length > 0 ? $"&path={Uri.EscapeDataString(folder.Key)}" : "");
                JsonNode? node;
                try { node = await SendAsync(HttpMethod.Get, url, ct: ct); }
                catch (GitHostException ex) when (ex.Status == HttpStatusCode.NotFound) { break; }
                if (node is not JsonArray items) break;
                foreach (var item in items)
                    if (SubmoduleApi.Str(item?["type"]) == "commit" && SubmoduleApi.Str(item?["path"]) is { } p && wanted.Remove(p) && SubmoduleApi.Str(item?["id"]) is { } id && IsSha(id))
                        result[p] = id;
                if (items.Count < 100) break;
            }
        }
        return result;
    }
}

/// <summary>
/// Bitbucket Cloud: the source listing of the submodule's folder (<c>src/{commit}/{folder}/</c>) has an entry for the
/// path whose type names a submodule, or a directory entry whose commit is not the listed commit (the gitlink).
/// Data Center: the browse API lists it as <c>{"type":"SUBMODULE","contentId":…}</c>.
/// </summary>
public sealed partial class BitbucketHost
{
    public override async Task<IReadOnlyDictionary<string, string>> ResolveSubmoduleCommitsAsync(string commit, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (!IsSha(commit)) throw new GitHostException("Not a commit id.");
        var result = SubmoduleApi.NewResult();
        foreach (var folder in paths.Distinct(StringComparer.Ordinal).GroupBy(SubmoduleApi.Folder))
        {
            var wanted = folder.ToHashSet(StringComparer.Ordinal);
            if (_cloud) await CloudAsync(commit, folder.Key, wanted, result, ct);
            else await DataCenterAsync(commit, folder.Key, wanted, result, ct);
        }
        return result;
    }

    private async Task CloudAsync(string commit, string folder, HashSet<string> wanted, Dictionary<string, string> result, CancellationToken ct)
    {
        string? url = $"{Repo}/src/{commit}/{(folder.Length > 0 ? SubmoduleApi.EscapePath(folder) + "/" : "")}?pagelen=100";
        for (var page = 0; page < SubmoduleApi.MaxPages && url is not null && wanted.Count > 0; page++)
        {
            JsonNode? node;
            try { node = await SendAsync(HttpMethod.Get, url, ct: ct); }
            catch (GitHostException ex) when (ex.Status == HttpStatusCode.NotFound) { return; }
            foreach (var item in node?["values"] as JsonArray ?? [])
            {
                if (SubmoduleApi.Str(item?["path"]) is not { } p || !wanted.Contains(p)) continue;
                var type = SubmoduleApi.Str(item?["type"]) ?? "";
                var hash = SubmoduleApi.Str(item?["commit"]?["hash"]);
                if (hash is null || !IsSha(hash) || type == "commit_file") continue;
                if (type.Contains("submodule", StringComparison.OrdinalIgnoreCase) || !SubmoduleApi.SameCommit(hash, commit))
                {
                    result[p] = hash;
                    wanted.Remove(p);
                }
            }
            // The token only goes back to the API host it belongs to.
            url = SubmoduleApi.Str(node?["next"]) is { } next && next.StartsWith(_base + "/", StringComparison.Ordinal) ? next : null;
        }
    }

    private async Task DataCenterAsync(string commit, string folder, HashSet<string> wanted, Dictionary<string, string> result, CancellationToken ct)
    {
        var start = 0;
        for (var page = 0; page < SubmoduleApi.MaxPages && wanted.Count > 0; page++)
        {
            var url = $"{Repo}/browse{(folder.Length > 0 ? "/" + SubmoduleApi.EscapePath(folder) : "")}?at={commit}&limit=500&start={start}";
            JsonNode? node;
            try { node = await SendAsync(HttpMethod.Get, url, ct: ct); }
            catch (GitHostException ex) when (ex.Status == HttpStatusCode.NotFound) { return; }
            var children = node?["children"];
            foreach (var item in children?["values"] as JsonArray ?? [])
            {
                if (SubmoduleApi.Str(item?["type"]) != "SUBMODULE") continue;
                var name = SubmoduleApi.Str(item?["path"]?["toString"]) ?? SubmoduleApi.Str(item?["path"]?["name"]);
                if (name is null) continue;
                var p = folder.Length > 0 && !name.StartsWith(folder + "/", StringComparison.Ordinal) ? $"{folder}/{name}" : name;
                if (!wanted.Remove(p)) continue;
                if ((SubmoduleApi.Str(item?["contentId"]) ?? SubmoduleApi.Str(item?["node"])) is { } sha && IsSha(sha)) result[p] = sha;
            }
            if (children?["isLastPage"] is JsonValue last && last.TryGetValue<bool>(out var isLast) && isLast) return;
            if (children?["nextPageStart"] is not JsonValue n || !n.TryGetValue<int>(out var next) || next <= start) return;
            start = next;
        }
    }
}
