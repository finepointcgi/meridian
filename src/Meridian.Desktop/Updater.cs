using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Meridian.Desktop;

/// <summary>
/// A downloadable file of a release. <paramref name="Url"/> is the public download address (GitHub's browser_download_url,
/// or a manifest's url); <paramref name="ApiUrl"/> is GitHub's asset API address, used with a token for private repositories;
/// <paramref name="Sha256"/> comes from a self-hosted manifest (GitHub releases are verified against SHA256SUMS.txt instead).
/// </summary>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256 = null, string? ApiUrl = null);

/// <summary>How an update is applied: an installer the OS opens (dmg, setup.exe, AppImage), or a portable archive the app unpacks and swaps in.</summary>
public enum PackageKind { Installer, Portable }

/// <summary>A newer release that matches this machine, with the package to install and the checksum list.</summary>
public sealed record UpdateInfo(string Version, string Tag, string PageUrl, ReleaseAsset Asset, ReleaseAsset? Checksums,
    PackageKind Kind = PackageKind.Installer, UpdateSourceKind Source = UpdateSourceKind.GitHub);

/// <summary>The package picked for a machine.</summary>
public sealed record SelectedPackage(ReleaseAsset Asset, PackageKind Kind);

/// <summary>The machine an update is for. Tests inject any OS and CPU.</summary>
/// <param name="PreferPortable">Try the portable archive before the installer (a writable .app bundle on macOS, a portable zip install on Windows, a tar.gz install on Linux).</param>
/// <param name="AppImage">Running as an AppImage on Linux: update with the new AppImage, not by unpacking a tar.gz.</param>
public sealed record UpdatePlatform(OSPlatform Os, Architecture Arch, bool PreferPortable = false, bool AppImage = false)
{
    public static OSPlatform CurrentOs => OperatingSystem.IsMacOS() ? OSPlatform.OSX : OperatingSystem.IsWindows() ? OSPlatform.Windows : OSPlatform.Linux;

    /// <summary>This computer, with the preference that matches how Meridian was installed.</summary>
    public static UpdatePlatform Current()
    {
        var os = CurrentOs;
        var appImage = os == OSPlatform.Linux && Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 };
        var location = InstallLocation.Detect(AppContext.BaseDirectory, os);
        var preferPortable =
            os == OSPlatform.OSX ? location != null && PortableInstaller.CheckWritable(location, DesktopSettings.Folder) is null
            : os == OSPlatform.Windows ? location is { HasInstaller: false }
            : !appImage;
        return new UpdatePlatform(os, RuntimeInformation.OSArchitecture, preferPortable, appImage);
    }
}

/// <summary>What an update check found.</summary>
public enum UpdateStatus { UpToDate, Available, NoPackage, PrivateRepository, Unauthorized, Unreachable, Invalid, NotConfigured }

/// <summary>The outcome of a check, with a sentence Settings ▸ Desktop shows as is.</summary>
public sealed record UpdateCheckResult(UpdateStatus Status, string Message, UpdateInfo? Update = null, string? LatestVersion = null);

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
/// Checks an update source (GitHub releases, GitHub with a token, or a self-hosted manifest) for a newer Meridian, picks the
/// package for this OS and architecture (installer, or the portable archive as a fallback), downloads it and verifies its SHA-256
/// (SHA256SUMS.txt for GitHub releases, the manifest's sha256 otherwise). Nothing unverified is ever returned.
/// </summary>
public sealed class Updater
{
    public const string Repository = "finepointcgi/meridian";
    public const string ChecksumsName = "SHA256SUMS.txt";
    public const string GitHubApi = "https://api.github.com";
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);
    /// <summary>Largest package accepted (the desktop archives are ~100–200 MB).</summary>
    public const long MaxDownloadBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxRedirects = 5;

    private readonly HttpClient _http;
    private readonly string _currentVersion;
    private readonly Action<string> _log;
    private readonly Uri _api;
    private readonly UpdateSource _source;
    private readonly UpdatePlatform _platform;

    /// <param name="http">Should not follow redirects itself (<see cref="CreateHttpClient"/>): the updater follows them so a token never leaves the GitHub API host.</param>
    public Updater(HttpClient http, string currentVersion, Action<string>? log = null, string apiBase = GitHubApi,
        UpdateSource? source = null, UpdatePlatform? platform = null)
    {
        _http = http;
        _currentVersion = currentVersion;
        _log = log ?? (_ => { });
        _api = new Uri(apiBase.TrimEnd('/') + "/");
        _source = source ?? UpdateSource.Default;
        _platform = platform ?? UpdatePlatform.Current();
    }

    /// <summary>An HttpClient for the updater: redirects are not followed automatically (the updater follows them without the token).</summary>
    public static HttpClient CreateHttpClient(TimeSpan timeout) =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = timeout };

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

    /// <summary>Picks the installer for an OS and CPU, falling back to the portable archive (see <see cref="SelectPackage"/>).</summary>
    public static ReleaseAsset? SelectAsset(IEnumerable<ReleaseAsset> assets, OSPlatform os, Architecture arch) =>
        SelectPackage(assets, new UpdatePlatform(os, arch))?.Asset;

    /// <summary>
    /// The package for a machine. Installers: .dmg (macOS), -setup.exe or .msi (Windows), .AppImage (Linux, only when running as
    /// an AppImage). Portable archives: macOS .zip (Meridian.app inside), windows .zip, linux .tar.gz (not for an AppImage).
    /// The installer comes first unless the platform prefers the portable archive; either way the other kind is the fallback.
    /// </summary>
    public static SelectedPackage? SelectPackage(IEnumerable<ReleaseAsset> assets, UpdatePlatform platform)
    {
        var list = assets.ToList();
        string[] archNames = platform.Arch switch
        {
            Architecture.Arm64 => ["arm64", "aarch64"],
            Architecture.X64 => ["x64", "x86_64", "amd64"],
            _ => [],
        };
        if (archNames.Length == 0) return null;
        var os = platform.Os;
        (string Os, string[] Installer, string[] Portable) want =
            os == OSPlatform.OSX ? ("macos", [".dmg"], [".zip"])
            : os == OSPlatform.Windows ? ("windows", ["-setup.exe", ".msi"], [".zip"])
            : os == OSPlatform.Linux ? ("linux", platform.AppImage ? [".AppImage"] : [], platform.AppImage ? [] : [".tar.gz"])
            : ("", [], []);
        var installers = want.Installer.Select(s => (Suffix: s, Kind: PackageKind.Installer));
        var portables = want.Portable.Select(s => (Suffix: s, Kind: PackageKind.Portable));
        foreach (var (suffix, kind) in platform.PreferPortable ? portables.Concat(installers) : installers.Concat(portables))
        {
            var hit = list.FirstOrDefault(a =>
                a.Name.StartsWith("Meridian-", StringComparison.OrdinalIgnoreCase) &&
                !a.Name.StartsWith("Meridian-Server", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                Tokens(a.Name).Contains(want.Os) &&
                Tokens(a.Name).Any(t => archNames.Contains(t)));
            if (hit != null) return new SelectedPackage(hit, kind);
        }
        return null;

        static string[] Tokens(string name) => name.ToLowerInvariant().Split('-', '.', '_');
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
                    assets.Add(new ReleaseAsset(n.GetString() ?? "", u.GetString() ?? "",
                        a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0,
                        ApiUrl: a.TryGetProperty("url", out var api) && api.ValueKind == JsonValueKind.String ? api.GetString() : null));
        return (
            r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "",
            r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
            r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True,
            r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True,
            assets);
    }

    /// <summary>
    /// Reads a GitLab "release" JSON object (GET /projects/:id/releases/…). Assets are the release's links: a link into the
    /// project's API on the same GitLab host (the generic package registry) is used as is, so a token works for a private
    /// project; any other link is downloaded through its <c>direct_asset_url</c> (or <c>url</c>) without the token.
    /// </summary>
    public static (string Tag, string PageUrl, bool Upcoming, List<ReleaseAsset> Assets) ParseGitLabRelease(JsonElement r, Uri gitLabBase)
    {
        var assets = new List<ReleaseAsset>();
        if (r.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
            foreach (var l in links.EnumerateArray())
            {
                var name = Str(l, "name");
                var url = Str(l, "url");
                var direct = Str(l, "direct_asset_url");
                if (name is null || (url is null && direct is null)) continue;
                if (name.Contains('/') || name.Contains('\\') || name is "." or "..") continue;
                var apiOnHost = url != null && Uri.TryCreate(url, UriKind.Absolute, out var u) && SameOrigin(u, gitLabBase)
                    && u.AbsolutePath.StartsWith(gitLabBase.AbsolutePath.TrimEnd('/') + "/api/v4/", StringComparison.OrdinalIgnoreCase);
                assets.Add(new ReleaseAsset(name, apiOnHost ? url! : direct ?? url!, 0));
            }
        var page = r.TryGetProperty("_links", out var lk) && lk.ValueKind == JsonValueKind.Object ? Str(lk, "self") : null;
        return (Str(r, "tag_name") ?? "", page ?? "", r.TryGetProperty("upcoming_release", out var up) && up.ValueKind == JsonValueKind.True, assets);

        static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var x) && x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s ? s : null;
    }

    internal static bool SameOrigin(Uri a, Uri b) =>
        a.Scheme == b.Scheme && a.Port == b.Port && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);

    private bool IsManifest => _source.Kind == UpdateSourceKind.Manifest;
    private bool IsGitLab => _source.Kind == UpdateSourceKind.GitLab;
    private string SourceName => IsManifest ? "the update server" : IsGitLab ? "GitLab" : "GitHub";

    /// <summary>
    /// Asks the update source for the latest release. Never throws for network, HTTP or format problems: the result says what
    /// happened in a sentence the Settings dialog shows as is.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            return IsManifest ? await CheckManifestAsync(ct) : IsGitLab ? await CheckGitLabAsync(ct) : await CheckGitHubAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _log($"Update check failed: {ex.Message}");
            return new(UpdateStatus.Unreachable, $"Couldn't reach {SourceName}: {Reason(ex)}");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or UriFormatException)
        {
            _log($"Update check failed: {ex.Message}");
            return new(UpdateStatus.Invalid, $"{(IsManifest ? "The update manifest" : IsGitLab ? "GitLab's answer" : "GitHub's answer")} could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// GitLab: <c>/api/v4/projects/:id/releases/permalink/latest</c> (it redirects to the latest release), falling back to
    /// listing the releases when that address is unknown (GitLab before 15.4), the latest is an upcoming release or a pre-release.
    /// </summary>
    private async Task<UpdateCheckResult> CheckGitLabAsync(CancellationToken ct)
    {
        if (_source.GitLabBase() is not { } gitLab)
            return new(UpdateStatus.NotConfigured, "The GitLab address is not valid — enter an https:// address in Settings ▸ Desktop.");
        if (UpdateSource.ValidateGitLabProject(_source.GitLabProject) is { } problem) return new(UpdateStatus.NotConfigured, problem);
        var project = _source.GitLabProject!.Trim().Trim('/');
        var releases = new Uri(gitLab, $"api/v4/projects/{Uri.EscapeDataString(project)}/releases");
        var withToken = !string.IsNullOrEmpty(_source.Token);

        (string Tag, string PageUrl, bool Upcoming, List<ReleaseAsset> Assets)? chosen = null;
        using (var res = await SendAsync(new Uri(releases.AbsoluteUri + "/permalink/latest"), "application/json", ct))
        {
            if (GitLabFailure(res, project, withToken, listing: false) is { } failure) return failure;
            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await ReadLimitedStringAsync(res, ct));
                var latest = ParseGitLabRelease(doc.RootElement, gitLab);
                if (!latest.Upcoming && SemVersion.TryParse(latest.Tag, out var lv) && lv.Prerelease.Length == 0) chosen = latest;
            }
        }
        if (chosen is null)
        {
            using var res = await SendAsync(new Uri(releases.AbsoluteUri + "?per_page=50&order_by=released_at&sort=desc"), "application/json", ct);
            if (GitLabFailure(res, project, withToken, listing: true) is { } failure) return failure;
            if (!res.IsSuccessStatusCode) return new(UpdateStatus.Unreachable, $"Couldn't reach GitLab: it answered {(int)res.StatusCode}.");
            using var doc = JsonDocument.Parse(await ReadLimitedStringAsync(res, ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("the release list is not a JSON array.");
            // Like GitHub's "latest": the highest published version that is not a pre-release.
            chosen = doc.RootElement.EnumerateArray().Select(e => ParseGitLabRelease(e, gitLab))
                .Where(x => !x.Upcoming && SemVersion.TryParse(x.Tag, out var v) && v.Prerelease.Length == 0)
                .OrderByDescending(x => { SemVersion.TryParse(x.Tag, out var v); return v; })
                .Cast<(string, string, bool, List<ReleaseAsset>)?>()
                .FirstOrDefault();
            if (chosen is null)
            {
                _log("Update check: no published GitLab release.");
                return new(UpdateStatus.UpToDate, $"You're up to date ({Display(_currentVersion)}).");
            }
        }
        var (tag, page, _, assets) = chosen.Value;
        return Evaluate(tag, page, assets, assets.FirstOrDefault(a => a.Name == ChecksumsName));
    }

    private UpdateCheckResult? GitLabFailure(HttpResponseMessage res, string project, bool withToken, bool listing)
    {
        switch (res.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                return new(UpdateStatus.Unauthorized, "GitLab rejected the token (401) — it may have expired or been revoked. Set a new one in Settings ▸ Desktop.");
            case HttpStatusCode.Forbidden:
                return new(UpdateStatus.Unreachable, $"GitLab refused the request (403){(withToken ? " — the token needs the read_api scope and at least Reporter access" : "")}.");
            case HttpStatusCode.TooManyRequests:
                return new(UpdateStatus.Unreachable, "GitLab's rate limit for update checks is used up — try again later.");
            case HttpStatusCode.NotFound when listing && !withToken:
                _log("Update check: 404 from GitLab (the project is private or does not exist).");
                return new(UpdateStatus.PrivateRepository, $"Couldn't reach GitLab: the project {project} is private or does not exist — add a token in Settings ▸ Desktop.");
            case HttpStatusCode.NotFound when listing:
                return new(UpdateStatus.PrivateRepository, $"GitLab found no project {project}, or the token can't read it — check the project path and the token's access.");
        }
        return null;
    }

    private static string Reason(Exception ex) => ex switch
    {
        TaskCanceledException => "the request timed out.",
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => "the address could not be found (are you offline?).",
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } => "no connection (are you offline?).",
        _ => ex.Message.TrimEnd('.') + ".",
    };

    private async Task<UpdateCheckResult> CheckGitHubAsync(CancellationToken ct)
    {
        var withToken = _source.Kind == UpdateSourceKind.GitHubToken;
        if (withToken && string.IsNullOrEmpty(_source.Token))
            return new(UpdateStatus.NotConfigured, "No GitHub token is saved — add one in Settings ▸ Desktop, or choose another update source.");
        var repo = _source.Repository is { Length: > 0 } r ? r : Repository;
        using var res = await SendAsync(new Uri(_api, $"repos/{repo}/releases/latest"), "application/vnd.github+json", ct);
        switch (res.StatusCode)
        {
            case HttpStatusCode.NotFound when !withToken:
                _log("Update check: 404 from GitHub (the repository is private or has no releases).");
                return new(UpdateStatus.PrivateRepository, "Couldn't reach GitHub: the repository is private (or has no releases yet) — set a token or another update source in Settings.");
            case HttpStatusCode.NotFound:
                _log("Update check: 404 from GitHub with a token.");
                return new(UpdateStatus.PrivateRepository, $"GitHub found no published release in {repo}, or the token can't read that repository — check the token's repository access.");
            case HttpStatusCode.Unauthorized:
                return new(UpdateStatus.Unauthorized, "GitHub rejected the token (401) — it may have expired. Set a new one in Settings ▸ Desktop.");
            case HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests:
                var limited = res.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0";
                return new(UpdateStatus.Unreachable, limited
                    ? "GitHub's rate limit for update checks is used up — try again in an hour, or set a token."
                    : $"GitHub refused the request ({(int)res.StatusCode}){(withToken ? " — the token may not have access to the repository's contents" : "")}.");
        }
        if (!res.IsSuccessStatusCode) return new(UpdateStatus.Unreachable, $"Couldn't reach GitHub: it answered {(int)res.StatusCode}.");
        var release = ParseRelease(await ReadLimitedStringAsync(res, ct));
        if (release.Draft || release.Prerelease) return new(UpdateStatus.UpToDate, $"You're up to date ({Display(_currentVersion)}).");
        return Evaluate(release.Tag, release.PageUrl, release.Assets, release.Assets.FirstOrDefault(a => a.Name == ChecksumsName));
    }

    private async Task<UpdateCheckResult> CheckManifestAsync(CancellationToken ct)
    {
        if (_source.ManifestUri() is not { } url)
            return new(UpdateStatus.NotConfigured, "No update manifest address is set — enter an https:// address in Settings ▸ Desktop.");
        using var res = await SendAsync(url, "application/json", ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
            return new(UpdateStatus.Unreachable, $"Couldn't reach the update server: {url.Host} has no manifest at {url.AbsolutePath} (404). A Meridian server needs Meridian:Desktop:UpdatesPath.");
        if (!res.IsSuccessStatusCode) return new(UpdateStatus.Unreachable, $"Couldn't reach the update server: {url.Host} answered {(int)res.StatusCode}.");
        var manifest = UpdateManifest.Parse(await ReadLimitedStringAsync(res, ct), url, _source.AllowLoopbackHttp);
        return Evaluate(manifest.Version, manifest.NotesUrl ?? "", manifest.Assets, null);
    }

    private UpdateCheckResult Evaluate(string tag, string pageUrl, List<ReleaseAsset> assets, ReleaseAsset? checksums)
    {
        if (!SemVersion.TryParse(tag, out var v)) throw new FormatException($"\"{tag}\" is not a version number.");
        if (!(SemVersion.TryParse(_currentVersion, out var cur) && v > cur))
        {
            _log($"Update check: {tag} is not newer than {_currentVersion}.");
            return new(UpdateStatus.UpToDate, $"You're up to date ({Display(_currentVersion)}).", LatestVersion: v.ToString());
        }
        if (SelectPackage(assets, _platform) is not { } package)
        {
            _log($"Update check: {tag} has no package for {_platform.Os} {_platform.Arch}.");
            return new(UpdateStatus.NoPackage, $"No package for this computer in release {v}.", LatestVersion: v.ToString());
        }
        var update = new UpdateInfo(v.ToString(), tag, pageUrl, package.Asset, checksums, package.Kind, _source.Kind);
        return new(UpdateStatus.Available, $"{v} is available — Install and restart.", update, v.ToString());
    }

    private static string Display(string version) => SemVersion.TryParse(version, out var v) ? v.ToString() : version;

    /// <summary>
    /// Downloads the package to a fresh temp folder and verifies its SHA-256: against the manifest's value, or the release's
    /// SHA256SUMS.txt. A mismatch deletes the file and throws; there is no way to get an unverified file.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string expected;
        if (update.Asset.Sha256 is { } manifestHash)
        {
            if (!UpdateManifest.IsSha256(manifestHash)) throw new InvalidOperationException($"The manifest's sha256 for {update.Asset.Name} is not valid.");
            expected = manifestHash.ToLowerInvariant();
        }
        else
        {
            if (update.Checksums is null) throw new InvalidOperationException($"The release has no {ChecksumsName}, so the download cannot be verified.");
            using var sumsRes = await SendAsync(AssetUri(update.Checksums), "application/octet-stream", ct);
            if (!sumsRes.IsSuccessStatusCode) throw new HttpRequestException($"{ChecksumsName} could not be downloaded ({(int)sumsRes.StatusCode}).");
            var sums = ParseChecksums(await ReadLimitedStringAsync(sumsRes, ct));
            if (!sums.TryGetValue(update.Asset.Name, out var listed)) throw new InvalidOperationException($"{ChecksumsName} has no entry for {update.Asset.Name}.");
            expected = listed;
        }

        var dir = Directory.CreateTempSubdirectory("meridian-update-");
        var path = Path.Combine(dir.FullName, Path.GetFileName(update.Asset.Name));
        try
        {
            using var res = await SendAsync(AssetUri(update.Asset), "application/octet-stream", ct);
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{update.Asset.Name} could not be downloaded ({(int)res.StatusCode}).");
            var total = res.Content.Headers.ContentLength ?? (update.Asset.Size > 0 ? update.Asset.Size : -1);
            if (total > MaxDownloadBytes) throw new InvalidOperationException($"{update.Asset.Name} is larger than {MaxDownloadBytes / (1024 * 1024)} MB.");
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var src = await res.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(path))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    done += read;
                    if (done > MaxDownloadBytes) throw new InvalidOperationException($"{update.Asset.Name} is larger than {MaxDownloadBytes / (1024 * 1024)} MB.");
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha.AppendData(buffer, 0, read);
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total));
                }
            }
            var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(expected)))
                throw new InvalidOperationException($"Checksum mismatch for {update.Asset.Name}: the download was discarded.");
        }
        catch
        {
            try { dir.Delete(true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
        progress?.Report(1.0);
        return path;
    }

    /// <summary>With a token, GitHub assets are fetched through the asset API (the browser address needs a GitHub login for a private repository).</summary>
    private Uri AssetUri(ReleaseAsset asset) =>
        new(_source.Kind == UpdateSourceKind.GitHubToken && asset.ApiUrl is { Length: > 0 } api ? api : asset.Url);

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

    /// <summary>
    /// An address the updater may contact: https, or http to localhost only when the configured source itself is an
    /// http://localhost address (for testing).
    /// </summary>
    public static bool IsAllowedUrl(Uri url, bool allowLoopbackHttp) =>
        url.IsAbsoluteUri && (url.Scheme == Uri.UriSchemeHttps || (allowLoopbackHttp && url.Scheme == Uri.UriSchemeHttp && url.IsLoopback));

    private bool AllowLoopbackHttp => _source.AllowLoopbackHttp || (_api.Scheme == Uri.UriSchemeHttp && _api.IsLoopback)
        || _source.GitLabBase() is { Scheme: "http" };

    /// <summary>
    /// The token goes only to the GitHub API host (api.github.com) in GitHub-with-token mode, or only to the configured GitLab
    /// instance (same scheme, host and port, under its path) in GitLab mode.
    /// </summary>
    internal bool SendsTokenTo(Uri url)
    {
        if (string.IsNullOrEmpty(_source.Token)) return false;
        return _source.Kind switch
        {
            UpdateSourceKind.GitHubToken => SameOrigin(url, _api),
            UpdateSourceKind.GitLab => _source.GitLabBase() is { } gl && SameOrigin(url, gl)
                && url.AbsolutePath.StartsWith(gl.AbsolutePath, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>
    /// GET with redirects followed by hand: every hop must be an allowed address, and the token goes only to the GitHub API
    /// host. GitHub answers an asset request with a redirect to objects.githubusercontent.com, which is followed without it.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Uri url, string accept, CancellationToken ct)
    {
        for (var hop = 0; ; hop++)
        {
            if (!IsAllowedUrl(url, AllowLoopbackHttp)) throw new HttpRequestException($"refusing to connect to {url.Scheme}://{url.Host} (updates are downloaded over https only).");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Meridian", SemVersion.TryParse(_currentVersion, out var v) ? v.ToString() : "0.0.0"));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            if (string.Equals(url.Host, _api.Host, StringComparison.OrdinalIgnoreCase)) req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            if (SendsTokenTo(url))
            {
                if (IsGitLab) req.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", _source.Token);
                else req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _source.Token);
            }
            var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)res.StatusCode is 301 or 302 or 303 or 307 or 308 && res.Headers.Location is { } location)
            {
                res.Dispose();
                if (hop >= MaxRedirects) throw new HttpRequestException("too many redirects.");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            return res;
        }
    }

    private static async Task<string> ReadLimitedStringAsync(HttpResponseMessage res, CancellationToken ct, long max = 4 * 1024 * 1024)
    {
        await using var s = new Meridian.Serialization.SafeZip.LimitedStream(await res.Content.ReadAsStreamAsync(ct), max, "The response");
        using var reader = new StreamReader(s);
        try { return await reader.ReadToEndAsync(ct); }
        catch (InvalidDataException) { throw new InvalidOperationException($"the answer is larger than {max / (1024 * 1024)} MB."); }
    }
}

