using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Meridian.Desktop;

/// <summary>Where the desktop app looks for updates (Settings ▸ Desktop ▸ Update source).</summary>
public enum UpdateSourceKind
{
    /// <summary>GitHub releases of <see cref="Updater.Repository"/> through the public API (no token).</summary>
    GitHub,
    /// <summary>GitHub releases with a personal access token, for a private repository (assets through the asset API).</summary>
    GitHubToken,
    /// <summary>A JSON manifest at a custom https:// address, for example a Meridian team server's /api/desktop/latest.</summary>
    Manifest,
}

/// <summary>The update source in effect. The token is decrypted only for the check and never written anywhere in clear.</summary>
public sealed record UpdateSource(UpdateSourceKind Kind, string Repository = Updater.Repository, string? Token = null, string? ManifestUrl = null)
{
    public static readonly UpdateSource Default = new(UpdateSourceKind.GitHub);

    /// <summary>The manifest address when it is https, or http://localhost (testing); otherwise null.</summary>
    public Uri? ManifestUri() =>
        Kind == UpdateSourceKind.Manifest && ParseManifestUrl(ManifestUrl, out var uri) is null ? uri : null;

    /// <summary>An http://localhost manifest was configured explicitly: its assets may also be fetched from http://localhost.</summary>
    public bool AllowLoopbackHttp => ManifestUri() is { Scheme: "http" };

    /// <summary>Validates a manifest address typed in Settings; returns an error sentence, or null when it is usable.</summary>
    public static string? ParseManifestUrl(string? text, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(text)) return "Enter the manifest's address, for example https://meridian.example.com/api/desktop/latest.";
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return "That is not a web address.";
        if (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback) { uri = u; return null; }
        if (u.Scheme != Uri.UriSchemeHttps) return "Use an https:// address (plain http is only accepted for localhost, for testing).";
        if (!string.IsNullOrEmpty(u.UserInfo)) return "Don't put a user name or password in the address.";
        uri = u;
        return null;
    }
}

/// <summary>
/// The self-hosted update manifest:
/// <code>
/// { "version": "0.6.0", "notes": "https://…/release-notes",
///   "assets": [ { "name": "Meridian-0.6.0-windows-x64.zip", "url": "https://…/Meridian-0.6.0-windows-x64.zip", "sha256": "64 hex digits", "size": 123 } ] }
/// </code>
/// <c>url</c> may be relative to the manifest's own address. Every asset must carry a sha256; one without (or with an address
/// that is not https) is ignored, so nothing from a manifest can be installed unverified.
/// </summary>
public sealed record UpdateManifest(string Version, string? NotesUrl, List<ReleaseAsset> Assets)
{
    public static bool IsSha256(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);

    public static UpdateManifest Parse(string json, Uri manifestUrl, bool allowLoopbackHttp)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) throw new FormatException("the manifest is not a JSON object.");
        var version = r.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        if (!SemVersion.TryParse(version, out _)) throw new FormatException("the manifest has no valid \"version\".");
        string? notes = null;
        if (r.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String && Uri.TryCreate(manifestUrl, n.GetString(), out var nu) && Updater.IsAllowedUrl(nu, allowLoopbackHttp))
            notes = nu.AbsoluteUri;
        var assets = new List<ReleaseAsset>();
        if (r.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                var name = Str(a, "name");
                var url = Str(a, "url");
                var sha = Str(a, "sha256");
                if (name is null || url is null || !IsSha256(sha)) continue;
                if (name.Contains('/') || name.Contains('\\') || name is "." or "..") continue;
                if (!Uri.TryCreate(manifestUrl, url, out var au) || !Updater.IsAllowedUrl(au, allowLoopbackHttp)) continue;
                var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) && sz > 0 ? sz : 0;
                assets.Add(new ReleaseAsset(name, au.AbsoluteUri, size, sha!.ToLowerInvariant()));
            }
        return new UpdateManifest(version, notes, assets);

        static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var x) && x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s ? s : null;
    }
}

/// <summary>
/// Encrypts the GitHub token at rest with ASP.NET Core Data Protection, like the AI assistant's API key: keys live in the
/// desktop data folder (DPAPI-protected on Windows), so a copied desktop.json is useless on another machine.
/// </summary>
public sealed class UpdateTokenProtector
{
    private readonly IDataProtector _protector;

    public UpdateTokenProtector(string keysFolder)
    {
        Directory.CreateDirectory(keysFolder);
        _protector = DataProtectionProvider.Create(new DirectoryInfo(keysFolder), b => b.SetApplicationName("Meridian.Desktop"))
            .CreateProtector("Meridian.Desktop.UpdateToken.v1");
    }

    public static string DefaultKeysFolder => Path.Combine(DesktopSettings.Folder, "desktop-keys");

    public string Protect(string token) => _protector.Protect(token);

    /// <summary>The token, or null when there is none or it cannot be decrypted (keys rotated, file copied from another machine).</summary>
    public string? Unprotect(string? protectedToken)
    {
        if (string.IsNullOrEmpty(protectedToken)) return null;
        try { return _protector.Unprotect(protectedToken); }
        catch (CryptographicException) { return null; }
    }

    /// <summary>Checks a pasted token: GitHub tokens are printable ASCII without spaces.</summary>
    public static string? Validate(string? token)
    {
        var t = token?.Trim() ?? "";
        if (t.Length < 20) return "That is too short to be a GitHub token.";
        if (t.Length > 400 || t.Any(c => c <= ' ' || c > '~')) return "That does not look like a GitHub token.";
        return null;
    }
}
