using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Meridian.Desktop;

/// <summary>A downloadable file attached to a GitHub release.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>A newer release that matches this machine, with its installer and checksum list.</summary>
public sealed record UpdateInfo(string Version, string Tag, string PageUrl, ReleaseAsset Asset, ReleaseAsset? Checksums);

/// <summary>A semantic version (major.minor.patch[-prerelease][+build]); build metadata is ignored when comparing.</summary>
public readonly record struct SemVersion(int Major, int Minor, int Patch, string Prerelease) : IComparable<SemVersion>
{
    public static bool TryParse(string? text, out SemVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        var pre = dash >= 0 ? s[(dash + 1)..] : "";
        var core = (dash >= 0 ? s[..dash] : s).Split('.');
        if (core.Length is < 1 or > 4 || (dash >= 0 && pre.Length == 0)) return false;
        var n = new int[3];
        for (var i = 0; i < core.Length; i++)
        {
            if (!int.TryParse(core[i], NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return false;
            if (i < 3) n[i] = v; // a fourth .NET "revision" part is accepted and ignored
        }
        version = new SemVersion(n[0], n[1], n[2], pre);
        return true;
    }

    public int CompareTo(SemVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // A release sorts after its pre-releases: 1.0.0-rc.1 < 1.0.0.
        if (Prerelease.Length == 0 && other.Prerelease.Length == 0) return 0;
        if (Prerelease.Length == 0) return 1;
        if (other.Prerelease.Length == 0) return -1;
        var a = Prerelease.Split('.');
        var b = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ai);
            var bn = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bi);
            c = (an, bn) switch
            {
                (true, true) => ai.CompareTo(bi),
                (true, false) => -1, // numeric identifiers sort before alphanumeric ones
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (c != 0) return Math.Sign(c);
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Prerelease.Length > 0 ? "-" + Prerelease : "");
}

/// <summary>
/// Checks GitHub for a newer Meridian release, downloads the installer for this OS and architecture,
/// verifies it against the release's SHA256SUMS.txt and hands it to the OS.
/// </summary>
public sealed class Updater
{
    public const string Repository = "finepointcgi/meridian";
    public const string ChecksumsName = "SHA256SUMS.txt";
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly HttpClient _http;
    private readonly string _currentVersion;
    private readonly Action<string> _log;
    private readonly string _apiBase;

    public Updater(HttpClient http, string currentVersion, Action<string>? log = null, string apiBase = "https://api.github.com")
    {
        _http = http;
        _currentVersion = currentVersion;
        _log = log ?? (_ => { });
        _apiBase = apiBase.TrimEnd('/');
    }

    /// <summary>The running app's version (from the assembly's informational version, without any +commit suffix).</summary>
    public static string CurrentVersion()
    {
        var asm = typeof(Updater).Assembly;
        var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        return (info ?? asm.GetName().Version?.ToString(3) ?? "0.0.0").Split('+')[0];
    }

    /// <summary>At most one automatic check per <see cref="CheckInterval"/>; a last-check time in the future (clock change) allows a check.</summary>
    public static bool IsCheckDue(DateTimeOffset? lastCheck, DateTimeOffset now) =>
        lastCheck is not { } last || last > now || now - last >= CheckInterval;

    /// <summary>True when <paramref name="candidate"/> is a strictly newer version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string candidate, string current) =>
        SemVersion.TryParse(candidate, out var c) && SemVersion.TryParse(current, out var cur) && c > cur;

    /// <summary>Picks the installer for an OS and CPU: a .dmg on macOS, the setup .exe (or .msi) on Windows, the AppImage on Linux.</summary>
    public static ReleaseAsset? SelectAsset(IEnumerable<ReleaseAsset> assets, OSPlatform os, Architecture arch)
    {
        var list = assets.ToList();
        string[] archNames = arch switch
        {
            Architecture.Arm64 => ["arm64", "aarch64"],
            Architecture.X64 => ["x64", "x86_64", "amd64"],
            _ => [],
        };
        if (archNames.Length == 0) return null;
        (string Os, string[] Suffixes) want =
            os == OSPlatform.OSX ? ("macos", [".dmg"])
            : os == OSPlatform.Windows ? ("windows", ["-setup.exe", ".msi"])
            : os == OSPlatform.Linux ? ("linux", [".AppImage"])
            : ("", []);
        foreach (var suffix in want.Suffixes)
        {
            var hit = list.FirstOrDefault(a =>
                a.Name.StartsWith("Meridian-", StringComparison.OrdinalIgnoreCase) &&
                !a.Name.StartsWith("Meridian-Server", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                Tokens(a.Name).Contains(want.Os) &&
                Tokens(a.Name).Any(t => archNames.Contains(t)));
            if (hit != null) return hit;
        }
        return null;

        static string[] Tokens(string name) => name.ToLowerInvariant().Split('-', '.', '_');
    }

    public static ReleaseAsset? SelectAssetForThisMachine(IEnumerable<ReleaseAsset> assets)
    {
        var os = OperatingSystem.IsMacOS() ? OSPlatform.OSX : OperatingSystem.IsWindows() ? OSPlatform.Windows : OSPlatform.Linux;
        return SelectAsset(assets, os, RuntimeInformation.OSArchitecture);
    }

    /// <summary>Parses `sha256sum` output: "&lt;hex&gt;  name" or "&lt;hex&gt; *name" per line.</summary>
    public static Dictionary<string, string> ParseChecksums(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 66 || line.StartsWith('#')) continue;
            var hash = line[..64];
            if (!hash.All(Uri.IsHexDigit) || !char.IsWhiteSpace(line[64])) continue;
            var name = line[65..].TrimStart();
            if (name.StartsWith('*')) name = name[1..];
            name = name.Replace('\\', '/');
            name = name[(name.LastIndexOf('/') + 1)..];
            if (name.Length > 0) map[name] = hash.ToLowerInvariant();
        }
        return map;
    }

    /// <summary>Reads a GitHub "release" JSON object.</summary>
    public static (string Tag, string PageUrl, bool Draft, bool Prerelease, List<ReleaseAsset> Assets) ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var assets = new List<ReleaseAsset>();
        if (r.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
                if (a.TryGetProperty("name", out var n) && a.TryGetProperty("browser_download_url", out var u))
                    assets.Add(new ReleaseAsset(n.GetString() ?? "", u.GetString() ?? "", a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0));
        return (
            r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "",
            r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
            r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True,
            r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True,
            assets);
    }

    /// <summary>
    /// Asks GitHub for the latest release. Returns null when there is nothing newer, nothing for this machine, or GitHub cannot
    /// be reached (offline, rate limited, or a private repository, which answers 404 to anonymous requests). Never throws for those.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_apiBase}/repos/{Repository}/releases/latest");
            Headers(req);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var res = await _http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.NotFound) { _log("Update check: no public release found (404: the repository is private or has no releases)."); return null; }
            if (!res.IsSuccessStatusCode) { _log($"Update check: GitHub answered {(int)res.StatusCode}."); return null; }
            var release = ParseRelease(await res.Content.ReadAsStringAsync(ct));
            if (release.Draft || release.Prerelease) return null;
            if (!IsNewer(release.Tag, _currentVersion)) { _log($"Update check: {release.Tag} is not newer than {_currentVersion}."); return null; }
            var asset = SelectAssetForThisMachine(release.Assets);
            if (asset is null) { _log($"Update check: {release.Tag} has no installer for {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}."); return null; }
            SemVersion.TryParse(release.Tag, out var v);
            return new UpdateInfo(v.ToString(), release.Tag, release.PageUrl, asset, release.Assets.FirstOrDefault(a => a.Name == ChecksumsName));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _log($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Downloads the installer to a fresh temp folder and verifies its SHA-256 against the release's checksum list.</summary>
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (update.Checksums is null) throw new InvalidOperationException($"The release has no {ChecksumsName}, so the download cannot be verified.");
        var sums = ParseChecksums(await GetStringAsync(update.Checksums.Url, ct));
        if (!sums.TryGetValue(update.Asset.Name, out var expected)) throw new InvalidOperationException($"{ChecksumsName} has no entry for {update.Asset.Name}.");

        var dir = Directory.CreateTempSubdirectory("meridian-update-");
        var path = Path.Combine(dir.FullName, Path.GetFileName(update.Asset.Name));
        using var req = new HttpRequestMessage(HttpMethod.Get, update.Asset.Url);
        Headers(req);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? (update.Asset.Size > 0 ? update.Asset.Size : -1);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var src = await res.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(path))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                sha.AppendData(buffer, 0, read);
                done += read;
                if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total));
            }
        }
        var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(expected)))
        {
            try { dir.Delete(true); } catch (IOException) { }
            throw new InvalidOperationException($"Checksum mismatch for {update.Asset.Name}: the download was discarded.");
        }
        progress?.Report(1.0);
        return path;
    }

    /// <summary>Opens a verified installer: mounts the .dmg (macOS), runs the setup (Windows) or starts the new AppImage (Linux).</summary>
    public static void Launch(string path)
    {
        if (OperatingSystem.IsMacOS())
            Process.Start(new ProcessStartInfo("/usr/bin/open", [path]) { UseShellExecute = false });
        else if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = false });
        }
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        Headers(req);
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    private void Headers(HttpRequestMessage req)
    {
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Meridian", SemVersion.TryParse(_currentVersion, out var v) ? v.ToString() : "0.0.0"));
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }
}
