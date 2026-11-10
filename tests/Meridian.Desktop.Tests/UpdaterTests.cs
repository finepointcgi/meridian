using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Meridian.Desktop;

namespace Meridian.Desktop.Tests;

public class UpdaterTests
{
    [Theory]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("v0.1.1", "0.1.0", true)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("0.10.0", "0.9.0", true)]      // numeric, not lexical
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("v0.1.0", "0.1.0.0", false)]   // .NET four-part version
    [InlineData("0.1.0", "0.2.0", false)]
    [InlineData("1.0.0", "1.0.0-rc.1", true)]  // a release beats its pre-release
    [InlineData("1.0.0-rc.1", "1.0.0", false)]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.1", true)]
    [InlineData("1.0.0-rc.10", "1.0.0-rc.9", true)]
    [InlineData("1.0.0-beta", "1.0.0-alpha", true)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha", true)]
    [InlineData("1.0.0+build.5", "1.0.0", false)] // build metadata is ignored
    [InlineData("nightly", "0.1.0", false)]
    [InlineData("", "0.1.0", false)]
    public void Compares_semantic_versions(string candidate, string current, bool newer) =>
        Assert.Equal(newer, Updater.IsNewer(candidate, current));

    [Theory]
    [InlineData("1.2.3-")]
    [InlineData("1.x.3")]
    [InlineData("-1.2.3")]
    [InlineData("1.2.3.4.5")]
    [InlineData("latest")]
    public void Rejects_malformed_versions(string text) => Assert.False(SemVersion.TryParse(text, out _));

    private static readonly ReleaseAsset[] Assets = new[]
    {
        "Meridian-0.2.0-macos-arm64.zip", "Meridian-0.2.0-macos-arm64.dmg", "Meridian-0.2.0-macos-x64.dmg", "Meridian-0.2.0-macos-x64.zip",
        "Meridian-0.2.0-windows-x64.zip", "Meridian-0.2.0-windows-x64-setup.exe",
        "Meridian-0.2.0-linux-x64.tar.gz", "Meridian-0.2.0-linux-x64.AppImage", "meridian_0.2.0_amd64.deb",
        "Meridian-Server-0.2.0.zip", "SHA256SUMS.txt",
    }.Select(n => new ReleaseAsset(n, "https://example.invalid/" + n, 1)).ToArray();

    [Theory]
    [InlineData("OSX", Architecture.Arm64, "Meridian-0.2.0-macos-arm64.dmg")]
    [InlineData("OSX", Architecture.X64, "Meridian-0.2.0-macos-x64.dmg")]
    [InlineData("WINDOWS", Architecture.X64, "Meridian-0.2.0-windows-x64-setup.exe")]
    [InlineData("LINUX", Architecture.X64, "Meridian-0.2.0-linux-x64.tar.gz")]   // not running as an AppImage: the tar.gz
    [InlineData("LINUX", Architecture.Arm64, null)]
    [InlineData("WINDOWS", Architecture.Arm64, null)]
    [InlineData("OSX", Architecture.X86, null)]
    public void Selects_the_installer_for_os_and_architecture(string os, Architecture arch, string? expected) =>
        Assert.Equal(expected, Updater.SelectAsset(Assets, OSPlatform.Create(os), arch)?.Name);

    [Fact]
    public void Falls_back_to_an_msi_on_windows()
    {
        var assets = new[] { new ReleaseAsset("Meridian-0.2.0-windows-x64.zip", "u", 1), new ReleaseAsset("Meridian-0.2.0-windows-x64.msi", "u", 1) };
        Assert.Equal("Meridian-0.2.0-windows-x64.msi", Updater.SelectAsset(assets, OSPlatform.Windows, Architecture.X64)?.Name);
    }

    [Fact]
    public void Parses_sha256sum_output()
    {
        var a = new string('a', 64);
        var b = new string('B', 64);
        var map = Updater.ParseChecksums($"{a}  Meridian-0.2.0-macos-arm64.dmg\r\n{b} *dist/Meridian-0.2.0-windows-x64-setup.exe\n# comment\nnot a line\n");
        Assert.Equal(a, map["Meridian-0.2.0-macos-arm64.dmg"]);
        Assert.Equal(new string('b', 64), map["Meridian-0.2.0-windows-x64-setup.exe"]);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void Checks_at_most_once_a_day()
    {
        var now = DateTimeOffset.Parse("2026-11-09T12:00:00Z");
        Assert.True(Updater.IsCheckDue(null, now));
        Assert.False(Updater.IsCheckDue(now.AddHours(-23), now));
        Assert.True(Updater.IsCheckDue(now.AddHours(-24), now));
        Assert.True(Updater.IsCheckDue(now.AddDays(3), now)); // clock moved backwards
    }


    // ------------------------------------------------------------------ packages for each OS

    /// <summary>What build/package.sh attaches when the CI is blocked: portable archives only, plus macOS dmg/zip and the checksums.</summary>
    private static readonly ReleaseAsset[] LocalBuild = new[]
    {
        "Meridian-0.6.0-macos-arm64.dmg", "Meridian-0.6.0-macos-arm64.zip", "Meridian-0.6.0-macos-x64.dmg", "Meridian-0.6.0-macos-x64.zip",
        "Meridian-0.6.0-windows-x64.zip", "Meridian-0.6.0-linux-x64.tar.gz", "Meridian-Server-0.6.0.zip", "SHA256SUMS.txt",
    }.Select(n => new ReleaseAsset(n, "https://example.invalid/" + n, 1)).ToArray();

    [Theory]
    [InlineData("WINDOWS", Architecture.X64, false, false, "Meridian-0.6.0-windows-x64.zip", PackageKind.Portable)]
    [InlineData("WINDOWS", Architecture.X64, true, false, "Meridian-0.6.0-windows-x64.zip", PackageKind.Portable)]
    [InlineData("LINUX", Architecture.X64, false, false, "Meridian-0.6.0-linux-x64.tar.gz", PackageKind.Portable)]
    [InlineData("LINUX", Architecture.X64, false, true, null, null)]                       // an AppImage needs an AppImage
    [InlineData("OSX", Architecture.Arm64, false, false, "Meridian-0.6.0-macos-arm64.dmg", PackageKind.Installer)]
    [InlineData("OSX", Architecture.Arm64, true, false, "Meridian-0.6.0-macos-arm64.zip", PackageKind.Portable)]
    [InlineData("OSX", Architecture.X64, true, false, "Meridian-0.6.0-macos-x64.zip", PackageKind.Portable)]
    [InlineData("LINUX", Architecture.Arm64, false, false, null, null)]
    [InlineData("WINDOWS", Architecture.Arm64, false, false, null, null)]
    public void Falls_back_to_the_portable_archive(string os, Architecture arch, bool preferPortable, bool appImage, string? name, PackageKind? kind)
    {
        var picked = Updater.SelectPackage(LocalBuild, new UpdatePlatform(OSPlatform.Create(os), arch, preferPortable, appImage));
        Assert.Equal(name, picked?.Asset.Name);
        Assert.Equal(kind, picked?.Kind);
    }

    [Fact]
    public void Prefers_the_portable_zip_for_a_portable_install_but_the_installer_otherwise()
    {
        Assert.Equal(PackageKind.Installer, Updater.SelectPackage(Assets, new UpdatePlatform(OSPlatform.Windows, Architecture.X64))!.Kind);
        Assert.Equal("Meridian-0.2.0-windows-x64.zip", Updater.SelectPackage(Assets, new UpdatePlatform(OSPlatform.Windows, Architecture.X64, PreferPortable: true))!.Asset.Name);
        Assert.Equal("Meridian-0.2.0-macos-arm64.dmg", Updater.SelectPackage(Assets.Where(a => !a.Name.EndsWith(".zip")), new UpdatePlatform(OSPlatform.OSX, Architecture.Arm64, PreferPortable: true))!.Asset.Name);
        Assert.Equal("Meridian-0.2.0-linux-x64.AppImage", Updater.SelectPackage(Assets, new UpdatePlatform(OSPlatform.Linux, Architecture.X64, AppImage: true))!.Asset.Name);
    }

    // ------------------------------------------------------------------ GitHub checks

    private static UpdatePlatform Mac => new(OSPlatform.OSX, Architecture.Arm64, PreferPortable: true);

    private static string ReleaseJson(string tag, bool prerelease = false, IEnumerable<ReleaseAsset>? assets = null) => $$"""
        { "tag_name": "{{tag}}", "html_url": "https://github.com/finepointcgi/meridian/releases/tag/{{tag}}", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [ {{string.Join(",", (assets ?? Assets).Select((a, i) => $$"""{ "name": "{{a.Name}}", "url": "https://api.github.com/repos/finepointcgi/meridian/releases/assets/{{i + 1}}", "browser_download_url": "{{a.Url}}", "size": 3 }"""))}} ] }
        """;

    private static Updater Make(HttpClient http, string version = "0.1.0", UpdateSource? source = null, UpdatePlatform? platform = null, List<string>? logs = null) =>
        new(http, version, logs == null ? null : new Action<string>(logs.Add), source: source, platform: platform ?? Mac);

    [Fact]
    public async Task A_private_repository_404_says_to_set_a_token_or_another_source()
    {
        var logs = new List<string>();
        using var http = new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var result = await Make(http, logs: logs).CheckAsync();
        Assert.Equal(UpdateStatus.PrivateRepository, result.Status);
        Assert.Null(result.Update);
        Assert.Contains("Couldn't reach GitHub: the repository is private", result.Message);
        Assert.Contains("set a token or another update source in Settings", result.Message);
        Assert.Contains(logs, l => l.Contains("404"));
    }

    [Fact]
    public async Task Offline_says_github_could_not_be_reached()
    {
        using var http = new HttpClient(new Stub(_ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known.")));
        var result = await Make(http).CheckAsync();
        Assert.Equal(UpdateStatus.Unreachable, result.Status);
        Assert.StartsWith("Couldn't reach GitHub:", result.Message);
    }

    [Fact]
    public async Task Rate_limits_and_bad_tokens_get_their_own_message()
    {
        using var limited = new HttpClient(new Stub(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
            r.Headers.Add("x-ratelimit-remaining", "0");
            return r;
        }));
        Assert.Contains("rate limit", (await Make(limited).CheckAsync()).Message);
        using var bad = new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var result = await Make(bad, source: new UpdateSource(UpdateSourceKind.GitHubToken, Token: "github_pat_" + new string('x', 30))).CheckAsync();
        Assert.Equal(UpdateStatus.Unauthorized, result.Status);
        Assert.Contains("rejected the token", result.Message);
        using var none = new HttpClient(new Stub(_ => throw new InvalidOperationException("must not be called")));
        Assert.Equal(UpdateStatus.NotConfigured, (await Make(none, source: new UpdateSource(UpdateSourceKind.GitHubToken)).CheckAsync()).Status);
    }

    [Fact]
    public async Task Finds_a_newer_release_for_the_machine()
    {
        using var http = new HttpClient(new Stub(r =>
        {
            Assert.Equal("/repos/finepointcgi/meridian/releases/latest", r.RequestUri!.AbsolutePath);
            Assert.NotEmpty(r.Headers.UserAgent);
            Assert.Null(r.Headers.Authorization);   // the public source never sends a token
            return Ok(ReleaseJson("v0.2.0"));
        }));
        var result = await Make(http, platform: new UpdatePlatform(OSPlatform.OSX, Architecture.Arm64)).CheckAsync();
        Assert.Equal(UpdateStatus.Available, result.Status);
        Assert.Equal("0.2.0 is available — Install and restart.", result.Message);
        var update = result.Update!;
        Assert.Equal("0.2.0", update.Version);
        Assert.Equal("Meridian-0.2.0-macos-arm64.dmg", update.Asset.Name);
        Assert.Equal(PackageKind.Installer, update.Kind);
        Assert.Equal("SHA256SUMS.txt", update.Checksums?.Name);
    }

    [Fact]
    public async Task Up_to_date_and_no_package_are_clear()
    {
        using var same = new HttpClient(new Stub(_ => Ok(ReleaseJson("v0.5.0"))));
        var result = await Make(same, "0.5.0").CheckAsync();
        Assert.Equal(UpdateStatus.UpToDate, result.Status);
        Assert.Equal("You're up to date (0.5.0).", result.Message);

        using var pre = new HttpClient(new Stub(_ => Ok(ReleaseJson("v0.6.0-rc.1", prerelease: true))));
        Assert.Equal(UpdateStatus.UpToDate, (await Make(pre, "0.5.0").CheckAsync()).Status);

        using var serverOnly = new HttpClient(new Stub(_ => Ok(ReleaseJson("v0.6.0", assets: [new ReleaseAsset("Meridian-Server-0.6.0.zip", "https://x/s", 1)]))));
        var none = await Make(serverOnly, "0.5.0").CheckAsync();
        Assert.Equal(UpdateStatus.NoPackage, none.Status);
        Assert.Equal("No package for this computer in release 0.6.0.", none.Message);
    }

    [Theory]
    [InlineData("WINDOWS", Architecture.X64, "Meridian-0.6.0-windows-x64.zip")]
    [InlineData("LINUX", Architecture.X64, "Meridian-0.6.0-linux-x64.tar.gz")]
    [InlineData("OSX", Architecture.Arm64, "Meridian-0.6.0-macos-arm64.dmg")]
    public async Task A_locally_built_release_offers_the_portable_archive(string os, Architecture arch, string expected)
    {
        using var http = new HttpClient(new Stub(_ => Ok(ReleaseJson("v0.6.0", assets: LocalBuild))));
        var result = await Make(http, "0.5.0", platform: new UpdatePlatform(OSPlatform.Create(os), arch)).CheckAsync();
        Assert.Equal(UpdateStatus.Available, result.Status);
        Assert.Equal(expected, result.Update!.Asset.Name);
    }

    // ------------------------------------------------------------------ tokens

    private const string Token = "github_pat_TESTTOKEN_0123456789abcdef";

    [Fact]
    public async Task The_token_is_sent_only_to_api_github_com_and_not_after_the_asset_redirect()
    {
        var payload = Encoding.UTF8.GetBytes("zip bytes");
        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var seen = new List<(Uri Url, string? Auth, string? Accept)>();
        using var http = new HttpClient(new Stub(r =>
        {
            var url = r.RequestUri!;
            seen.Add((url, r.Headers.Authorization?.ToString(), r.Headers.Accept.ToString()));
            if (url.AbsolutePath == "/repos/finepointcgi/meridian/releases/latest") return Ok(ReleaseJson("v0.6.0", assets: LocalBuild));
            if (url.Host == "api.github.com" && url.AbsolutePath.StartsWith("/repos/finepointcgi/meridian/releases/assets/"))
            {
                // GitHub answers an asset request with a redirect to its storage host.
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://objects.githubusercontent.com/github-production-release-asset/" + url.Segments[^1] + "?sig=abc");
                return redirect;
            }
            if (url.Host == "objects.githubusercontent.com")
            {
                var id = url.Segments[^1];
                var index = int.Parse(id) - 1;
                return LocalBuild[index].Name == "SHA256SUMS.txt"
                    ? Ok($"{hash}  Meridian-0.6.0-macos-arm64.zip\n")
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var updater = Make(http, "0.5.0", new UpdateSource(UpdateSourceKind.GitHubToken, Token: Token));
        var result = await updater.CheckAsync();
        Assert.Equal(UpdateStatus.Available, result.Status);
        Assert.Equal(UpdateSourceKind.GitHubToken, result.Update!.Source);
        var path = await updater.DownloadAsync(result.Update);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Directory.Delete(Path.GetDirectoryName(path)!, true);

        Assert.Contains(seen, s => s.Url.Host == "objects.githubusercontent.com");
        foreach (var (url, auth, accept) in seen)
        {
            if (url.Host == "api.github.com") Assert.Equal("Bearer " + Token, auth);
            else Assert.Null(auth);
            if (url.AbsolutePath.Contains("/releases/assets/")) Assert.Equal("application/octet-stream", accept);
        }
        Assert.DoesNotContain(seen, s => s.Url.Host == "example.invalid");   // browser_download_url is not used with a token
    }

    [Fact]
    public void Never_sends_the_token_elsewhere()
    {
        using var http = new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var updater = Make(http, source: new UpdateSource(UpdateSourceKind.GitHubToken, Token: Token));
        Assert.True(updater.SendsTokenTo(new Uri("https://api.github.com/repos/x/y/releases/latest")));
        Assert.False(updater.SendsTokenTo(new Uri("https://objects.githubusercontent.com/a")));
        Assert.False(updater.SendsTokenTo(new Uri("https://github.com/finepointcgi/meridian/releases/download/v1/x.zip")));
        Assert.False(updater.SendsTokenTo(new Uri("http://api.github.com/repos")));            // not over http
        Assert.False(updater.SendsTokenTo(new Uri("https://api.github.com.evil.example/repos")));
        Assert.False(Make(http).SendsTokenTo(new Uri("https://api.github.com/repos")));          // public source: no token at all
    }

    [Fact]
    public async Task Redirects_to_plain_http_are_refused()
    {
        using var http = new HttpClient(new Stub(r =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("http://downgrade.example/latest");
            return redirect;
        }));
        var result = await Make(http).CheckAsync();
        Assert.Equal(UpdateStatus.Unreachable, result.Status);
        Assert.Contains("https only", result.Message);
    }

    // ------------------------------------------------------------------ manifest

    private static readonly string Sha = new('c', 64);

    private static string ManifestJson(string version = "0.6.0") => $$"""
        { "version": "{{version}}", "notes": "https://updates.example.com/notes/{{version}}",
          "assets": [
            { "name": "Meridian-{{version}}-macos-arm64.zip", "url": "files/Meridian-{{version}}-macos-arm64.zip", "sha256": "{{Sha}}", "size": 10 },
            { "name": "Meridian-{{version}}-windows-x64.zip", "url": "https://cdn.example.com/w.zip", "sha256": "{{Sha}}" },
            { "name": "Meridian-{{version}}-linux-x64.tar.gz", "url": "https://cdn.example.com/l.tar.gz" },
            { "name": "Meridian-{{version}}-macos-x64.zip", "url": "http://cdn.example.com/insecure.zip", "sha256": "{{Sha}}" },
            { "name": "../evil.zip", "url": "https://cdn.example.com/e.zip", "sha256": "{{Sha}}" }
          ] }
        """;

    [Fact]
    public void Parses_the_manifest_and_drops_unverifiable_or_insecure_assets()
    {
        var m = UpdateManifest.Parse(ManifestJson(), new Uri("https://updates.example.com/api/desktop/latest"), allowLoopbackHttp: false);
        Assert.Equal("0.6.0", m.Version);
        Assert.Equal("https://updates.example.com/notes/0.6.0", m.NotesUrl);
        Assert.Equal(["Meridian-0.6.0-macos-arm64.zip", "Meridian-0.6.0-windows-x64.zip"], m.Assets.Select(a => a.Name));
        Assert.Equal("https://updates.example.com/api/desktop/files/Meridian-0.6.0-macos-arm64.zip", m.Assets[0].Url);   // relative to the manifest
        Assert.All(m.Assets, a => Assert.Equal(Sha, a.Sha256));
        Assert.Throws<FormatException>(() => UpdateManifest.Parse("""{ "assets": [] }""", new Uri("https://x.example/"), false));
    }

    [Fact]
    public async Task Checks_a_manifest_without_any_token()
    {
        using var http = new HttpClient(new Stub(r =>
        {
            Assert.Equal("https://updates.example.com/api/desktop/latest", r.RequestUri!.AbsoluteUri);
            Assert.Null(r.Headers.Authorization);
            return Ok(ManifestJson());
        }));
        var result = await Make(http, "0.5.0", new UpdateSource(UpdateSourceKind.Manifest, ManifestUrl: "https://updates.example.com/api/desktop/latest")).CheckAsync();
        Assert.Equal(UpdateStatus.Available, result.Status);
        Assert.Equal("Meridian-0.6.0-macos-arm64.zip", result.Update!.Asset.Name);
        Assert.Equal(PackageKind.Portable, result.Update.Kind);
        Assert.Equal(Sha, result.Update.Asset.Sha256);
        Assert.Equal(UpdateSourceKind.Manifest, result.Update.Source);
    }

    [Theory]
    [InlineData("http://updates.example.com/latest", false)]
    [InlineData("ftp://updates.example.com/latest", false)]
    [InlineData("https://user:pw@updates.example.com/latest", false)]
    [InlineData("not a url", false)]
    [InlineData("https://updates.example.com/latest", true)]
    [InlineData("http://localhost:5000/api/desktop/latest", true)]
    [InlineData("http://127.0.0.1:5000/api/desktop/latest", true)]
    public void Manifest_addresses_must_be_https_except_localhost(string url, bool ok) =>
        Assert.Equal(ok, UpdateSource.ParseManifestUrl(url, out _) is null);

    [Fact]
    public async Task Manifest_errors_are_explained()
    {
        var source = new UpdateSource(UpdateSourceKind.Manifest, ManifestUrl: "https://updates.example.com/api/desktop/latest");
        using var missing = new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Contains("Meridian:Desktop:UpdatesPath", (await Make(missing, source: source).CheckAsync()).Message);
        using var garbage = new HttpClient(new Stub(_ => Ok("<html>")));
        Assert.Equal(UpdateStatus.Invalid, (await Make(garbage, source: source).CheckAsync()).Status);
        using var none = new HttpClient(new Stub(_ => throw new InvalidOperationException("must not be called")));
        Assert.Equal(UpdateStatus.NotConfigured, (await Make(none, source: new UpdateSource(UpdateSourceKind.Manifest, ManifestUrl: "http://example.com/x")).CheckAsync()).Status);
    }

    [Fact]
    public async Task Manifest_downloads_are_verified_by_their_sha256()
    {
        var payload = Encoding.UTF8.GetBytes("portable zip");
        var good = Convert.ToHexStringLower(SHA256.HashData(payload));
        using var http = new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var updater = Make(http, source: new UpdateSource(UpdateSourceKind.Manifest, ManifestUrl: "https://u.example/latest"));
        var asset = new ReleaseAsset("Meridian-0.6.0-macos-arm64.zip", "https://u.example/files/m.zip", payload.Length, good);
        var update = new UpdateInfo("0.6.0", "0.6.0", "", asset, null, PackageKind.Portable, UpdateSourceKind.Manifest);
        var path = await updater.DownloadAsync(update);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Directory.Delete(Path.GetDirectoryName(path)!, true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => updater.DownloadAsync(update with { Asset = asset with { Sha256 = new string('0', 64) } }));
        Assert.Contains("Checksum mismatch", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => updater.DownloadAsync(update with { Asset = asset with { Sha256 = "abc" } }));
    }

    // ------------------------------------------------------------------ downloads

    [Fact]
    public async Task Download_is_verified_against_the_checksum_list()
    {
        var payload = Encoding.UTF8.GetBytes("installer bytes");
        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var asset = new ReleaseAsset("Meridian-0.2.0-macos-arm64.dmg", "https://example.invalid/dmg", payload.Length);
        var sums = new ReleaseAsset("SHA256SUMS.txt", "https://example.invalid/sums", 1);
        var update = new UpdateInfo("0.2.0", "v0.2.0", "", asset, sums);

        HttpClient Client(string sumsText) => new(new Stub(r => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = r.RequestUri!.AbsolutePath == "/sums" ? new StringContent(sumsText) : new ByteArrayContent(payload),
        }));

        using (var good = Client($"{hash}  {asset.Name}\n"))
        {
            var reports = new List<double>();
            var path = await Make(good).DownloadAsync(update, new SyncProgress(reports.Add));
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
            Assert.Equal(1.0, reports.Last());
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
        using (var bad = Client($"{new string('0', 64)}  {asset.Name}\n"))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Make(bad).DownloadAsync(update));
            Assert.Contains("Checksum mismatch", ex.Message);
        }
        using (var missing = Client($"{hash}  some-other-file\n"))
            await Assert.ThrowsAsync<InvalidOperationException>(() => Make(missing).DownloadAsync(update));
        using (var none = Client(""))
            await Assert.ThrowsAsync<InvalidOperationException>(() => Make(none).DownloadAsync(update with { Checksums = null }));
    }

    [Fact]
    public void Document_arguments_are_model_files_only()
    {
        var dir = Path.GetTempPath();
        var model = Path.Combine(dir, "Plant.meridian");
        var args = new[] { "--server", "https://x.example/a.sysml", "--local", "-psn_0_123", model, Path.Combine(dir, "notes.txt"), new Uri(Path.Combine(dir, "Car.sysml")).AbsoluteUri };
        Assert.Equal([Path.GetFullPath(model), Path.GetFullPath(Path.Combine(dir, "Car.sysml"))], Program.DocumentArgs(args));
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    internal sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
