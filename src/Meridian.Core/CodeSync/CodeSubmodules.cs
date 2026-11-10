namespace Meridian.Core.CodeSync;

/// <summary>
/// A git submodule of a synced repository at the synced commit: its path in the merged snapshot ("libs/core", nested
/// ones under their parent), the commit the parent pins it to, and a link template to compare two of its commits
/// ("{old}" and "{new}" are replaced) where the host has one. <see cref="Fetched"/> is false when its code could not be
/// read (no access, skipped): the pin still counts, so a bump shows in "What changed".
/// </summary>
public sealed record CodeSubmodulePin(string Path, string Sha, string? CompareUrl = null, bool Fetched = true)
{
    /// <summary>"libs/core@1a2b3c4…", as stamped in <see cref="CodeFieldKeys.Submodule"/>.</summary>
    public string Label => $"{Path}@{Sha}";
}

/// <summary>A submodule whose pinned commit changed between two syncs (null <see cref="OldSha"/>: added; null <see cref="NewSha"/>: removed).</summary>
public sealed record CodeSubmoduleBump(string Path, string? OldSha, string? NewSha, string? CompareUrl);

/// <summary>Reads and writes the submodule pins kept on the model root (<see cref="CodeFieldKeys.SubmodulePinsPrefix"/>).</summary>
public static class CodeSubmodulePins
{
    /// <summary>The model-root field holding a connection's pins.</summary>
    public static string FieldKey(string repoId) => CodeFieldKeys.SubmodulePinsPrefix + repoId;

    public static bool IsPinsField(string key) => key.StartsWith(CodeFieldKeys.SubmodulePinsPrefix, StringComparison.Ordinal);

    /// <summary>One line per submodule, sorted by path: "path@sha" or "path@sha &lt;compare-url&gt;". Null when there are none.</summary>
    public static string? Format(IEnumerable<CodeSubmodulePin> pins)
    {
        var lines = pins.Where(p => p.Path.Length > 0 && p.Sha.Length > 0 && !p.Path.Contains('\n'))
            .GroupBy(p => p.Path, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(p => p.Path, StringComparer.Ordinal)
            .Select(p => p.CompareUrl is { Length: > 0 } u && !u.Contains('>') && !u.Contains('\n') && !u.Contains(' ') ? $"{p.Label} <{u}>" : p.Label)
            .ToList();
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>Parses <see cref="Format"/>'s text (tolerant: blank and malformed lines are ignored).</summary>
    public static IReadOnlyList<CodeSubmodulePin> Parse(string? text)
    {
        var list = new List<CodeSubmodulePin>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            string? url = null;
            if (line.EndsWith('>') && line.LastIndexOf(" <", StringComparison.Ordinal) is var lt and > 0)
            {
                url = line[(lt + 2)..^1];
                line = line[..lt].TrimEnd();
            }
            var at = line.LastIndexOf('@');
            if (at <= 0 || at == line.Length - 1) continue;
            list.Add(new CodeSubmodulePin(line[..at], line[(at + 1)..], url));
        }
        return list;
    }

    /// <summary>The bumps between two pin texts, by path: added, removed, or moved to another commit (with a compare link
    /// when the host has one).</summary>
    public static IReadOnlyList<CodeSubmoduleBump> Bumps(string? before, string? after)
    {
        var old = Parse(before).GroupBy(p => p.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var neu = Parse(after).GroupBy(p => p.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var list = new List<CodeSubmoduleBump>();
        foreach (var path in old.Keys.Union(neu.Keys).OrderBy(p => p, StringComparer.Ordinal))
        {
            var o = old.GetValueOrDefault(path);
            var n = neu.GetValueOrDefault(path);
            if (o?.Sha == n?.Sha) continue;
            var template = n?.CompareUrl ?? o?.CompareUrl;
            var compare = o is not null && n is not null && template is { Length: > 0 }
                ? template.Replace("{old}", o.Sha, StringComparison.Ordinal).Replace("{new}", n.Sha, StringComparison.Ordinal) : null;
            list.Add(new CodeSubmoduleBump(path, o?.Sha, n?.Sha, compare));
        }
        return list;
    }

    /// <summary>The submodule a repository-relative file is in (the deepest one), or null.</summary>
    public static CodeSubmodulePin? Containing(IReadOnlyList<CodeSubmodulePin> pins, string? file)
    {
        if (string.IsNullOrEmpty(file) || pins.Count == 0) return null;
        CodeSubmodulePin? best = null;
        foreach (var p in pins)
            if (file.StartsWith(p.Path + "/", StringComparison.Ordinal) && (best is null || p.Path.Length > best.Path.Length)) best = p;
        return best;
    }
}
