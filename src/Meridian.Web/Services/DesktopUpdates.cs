using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Meridian.Web.Services;

/// <summary>
/// Desktop updates hosted by a Meridian team server. With <c>Meridian:Desktop:UpdatesPath</c> set to a folder, the server answers
/// <c>GET /api/desktop/latest</c> with the update manifest the desktop app's "Custom URL" source reads, and serves the files at
/// <c>GET /api/desktop/files/{name}</c>. The folder holds either a hand-written <c>latest.json</c> (served as is) or the release
/// files themselves as build/package.sh names them (<c>Meridian-0.6.0-windows-x64.zip</c>, …): the newest version is
/// listed with each file's SHA-256 (from the folder's SHA256SUMS.txt, else computed once and cached).
/// Both endpoints are anonymous (the desktop updater has no session) and answer 404 when the folder is not configured.
/// </summary>
public static partial class DesktopUpdates
{
    public const string LatestRoute = "/api/desktop/latest";
    public const string FilesRoute = "/api/desktop/files";
    public const string PathKey = "Meridian:Desktop:UpdatesPath";
    public const string NotesKey = "Meridian:Desktop:NotesUrl";
    public const string ManifestFile = "latest.json";
    public const string ChecksumsFile = "SHA256SUMS.txt";

    public sealed record ManifestAsset(string Name, string Url, string Sha256, long Size);
    public sealed record Manifest(string Version, string? Notes, IReadOnlyList<ManifestAsset> Assets);

    [GeneratedRegex(@"^Meridian-(?<v>\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)-(?:macos|windows|linux)-[0-9A-Za-z_]+(?:-setup)?\.(?:dmg|zip|exe|msi|AppImage|tar\.gz|deb)$")]
    private static partial Regex PackageName();

    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Time), string> HashCache = new();

    public static void MapDesktopUpdates(this WebApplication app, MeridianHostInfo host)
    {
        if (host.LocalMode) return;   // a team-server feature; the desktop's own local server never serves updates
        app.MapGet(LatestRoute, (IConfiguration config) =>
        {
            if (Folder(config) is not { } folder) return NotConfigured();
            var hand = Path.Combine(folder, ManifestFile);
            if (File.Exists(hand)) return Results.File(hand, "application/json");
            var manifest = Build(folder, config[NotesKey]);
            return manifest is null
                ? Results.Json(new { error = "No Meridian desktop packages in the updates folder." }, statusCode: StatusCodes.Status404NotFound)
                : Results.Json(manifest, JsonSerializerOptions.Web);
        }).AllowAnonymous();

        app.MapGet(FilesRoute + "/{name}", (string name, IConfiguration config) =>
        {
            if (Folder(config) is not { } folder) return NotConfigured();
            if (FilePath(folder, name) is not { } path) return Results.Json(new { error = "No such file." }, statusCode: StatusCodes.Status404NotFound);
            return Results.File(path, "application/octet-stream", Path.GetFileName(path), enableRangeProcessing: true);
        }).AllowAnonymous();
    }

    private static IResult NotConfigured() =>
        Results.Json(new { error = $"Desktop updates are not hosted here (set {PathKey})." }, statusCode: StatusCodes.Status404NotFound);

    private static string? Folder(IConfiguration config) =>
        config[PathKey] is { Length: > 0 } p && Directory.Exists(p) ? Path.GetFullPath(p) : null;

    /// <summary>A file directly inside <paramref name="folder"/> by its plain name; null for anything else (paths, hidden files, missing).</summary>
    public static string? FilePath(string folder, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.') || name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\') || name.Contains('\0')) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var full = Path.GetFullPath(Path.Combine(root, name));
        if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.Ordinal) || !File.Exists(full)) return null;
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return null;   // no symlinks out of the folder
        return full;
    }

    /// <summary>The manifest for the newest version in a folder of release files, or null when it holds none.</summary>
    public static Manifest? Build(string folder, string? notesUrl = null)
    {
        var packages = Directory.EnumerateFiles(folder)
            .Select(p => (Path: p, Name: Path.GetFileName(p), Match: PackageName().Match(Path.GetFileName(p))))
            .Where(x => x.Match.Success && FilePath(folder, x.Name) != null)
            .Select(x => (x.Path, x.Name, Version: x.Match.Groups["v"].Value))
            .ToList();
        if (packages.Count == 0) return null;
        var newest = packages.Select(p => p.Version).Distinct().OrderByDescending(v => v, VersionComparer.Instance).First();
        var sumsPath = Path.Combine(folder, ChecksumsFile);
        var listed = File.Exists(sumsPath) ? ParseChecksums(File.ReadAllText(sumsPath)) : new Dictionary<string, string>();
        var assets = packages.Where(p => p.Version == newest).OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p =>
            {
                var info = new FileInfo(p.Path);
                var sha = listed.TryGetValue(p.Name, out var s) ? s : HashCache.GetOrAdd((p.Path, info.Length, info.LastWriteTimeUtc), _ => Hash(p.Path));
                return new ManifestAsset(p.Name, "files/" + Uri.EscapeDataString(p.Name), sha, info.Length);
            })
            .ToList();
        return new Manifest(newest, notesUrl is { Length: > 0 } ? notesUrl : null, assets);
    }

    private static string Hash(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    /// <summary>`sha256sum` output: "&lt;hex&gt;  name" (or " *name") per line.</summary>
    public static Dictionary<string, string> ParseChecksums(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 66 || !line[..64].All(Uri.IsHexDigit) || !char.IsWhiteSpace(line[64])) continue;
            var name = line[65..].TrimStart().TrimStart('*').Replace('\\', '/');
            name = name[(name.LastIndexOf('/') + 1)..];
            if (name.Length > 0) map[name] = line[..64].ToLowerInvariant();
        }
        return map;
    }

    /// <summary>Semantic-version order: numeric major.minor.patch, then a release after its pre-releases.</summary>
    private sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? a, string? b)
        {
            var (an, ap) = Split(a ?? "");
            var (bn, bp) = Split(b ?? "");
            for (var i = 0; i < 3; i++)
                if (an[i] != bn[i]) return an[i].CompareTo(bn[i]);
            if (ap.Length == 0 || bp.Length == 0) return bp.Length.CompareTo(ap.Length) switch { > 0 => 1, < 0 => -1, _ => 0 };
            return string.CompareOrdinal(ap, bp);
        }

        private static (long[] Numbers, string Pre) Split(string v)
        {
            var dash = v.IndexOf('-');
            var core = (dash >= 0 ? v[..dash] : v).Split('.');
            var n = new long[3];
            for (var i = 0; i < 3 && i < core.Length; i++) long.TryParse(core[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i]);
            return (n, dash >= 0 ? v[(dash + 1)..] : "");
        }
    }
}
