using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meridian.Desktop;

namespace Meridian.Desktop.Tests;

/// <summary>Updates from GitLab releases (gitlab.com and self-managed) over a fake handler: no network access.</summary>
public sealed class GitLabUpdaterTests
{
    private const string Token = "glpat-UPDATE_s3cr3t_0123456789";
    private static UpdatePlatform Mac => new(OSPlatform.OSX, Architecture.Arm64, PreferPortable: true);
    private static UpdatePlatform Windows => new(OSPlatform.Windows, Architecture.X64);

    private static readonly string[] Files =
    [
        "Meridian-0.7.0-macos-arm64.dmg", "Meridian-0.7.0-macos-arm64.zip", "Meridian-0.7.0-windows-x64-setup.exe", "Meridian-0.7.0-windows-x64.zip",
        "Meridian-0.7.0-linux-x64.tar.gz", "Meridian-0.7.0-linux-x64.AppImage", "Meridian-Server-0.7.0.zip", "SHA256SUMS.txt",
    ];

    /// <summary>A release as GET /projects/:id/releases/:tag returns it: assets are links into the generic package registry.</summary>
    private static string ReleaseJson(string tag, string host = "https://gitlab.com", string project = "acme/meridian", int projectId = 4711,
        bool upcoming = false, IEnumerable<string>? files = null, string? linkHost = null) => new JsonObject
    {
        ["name"] = $"Meridian {tag}", ["tag_name"] = tag, ["description"] = "Release notes",
        ["created_at"] = "2026-11-09T10:00:00.000Z", ["released_at"] = upcoming ? "2099-01-01T00:00:00.000Z" : "2026-11-09T10:00:00.000Z",
        ["upcoming_release"] = upcoming,
        ["author"] = new JsonObject { ["username"] = "release-bot" },
        ["commit"] = new JsonObject { ["id"] = "0123456789abcdef" },
        ["assets"] = new JsonObject
        {
            ["count"] = (files ?? Files).Count() + 4,
            ["sources"] = new JsonArray(new JsonObject { ["format"] = "zip", ["url"] = $"{host}/{project}/-/archive/{tag}/meridian-{tag}.zip" }),
            ["links"] = new JsonArray((files ?? Files).Select((f, i) => (JsonNode)new JsonObject
            {
                ["id"] = i + 1, ["name"] = f,
                ["url"] = $"{linkHost ?? host}/api/v4/projects/{projectId}/packages/generic/meridian/{tag.TrimStart('v')}/{f}",
                ["direct_asset_url"] = $"{host}/{project}/-/releases/{tag}/downloads/{f}",
                ["link_type"] = f.EndsWith(".txt") ? "other" : "package",
            }).ToArray()),
        },
        ["_links"] = new JsonObject { ["self"] = $"{host}/{project}/-/releases/{tag}" },
    }.ToJsonString();

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Redirect(string to)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(to);
        return r;
    }

    private static Updater Make(HttpClient http, UpdateSource source, string version = "0.6.0", UpdatePlatform? platform = null) =>
        new(http, version, source: source, platform: platform ?? Mac);

    private static UpdateSource GitLab(string? url = null, string project = "acme/meridian", string? token = null) =>
        new(UpdateSourceKind.GitLab, Token: token, GitLabUrl: url, GitLabProject: project);

    [Fact]
    public async Task Finds_the_latest_release_through_the_permalink_redirect()
    {
        var seen = new List<Uri>();
        using var http = new HttpClient(new UpdaterTests.Stub(r =>
        {
            seen.Add(r.RequestUri!);
            return r.RequestUri!.AbsolutePath switch
            {
                "/api/v4/projects/acme%2Fmeridian/releases/permalink/latest" => Redirect("https://gitlab.com/api/v4/projects/acme%2Fmeridian/releases/v0.7.0"),
                "/api/v4/projects/acme%2Fmeridian/releases/v0.7.0" => Ok(ReleaseJson("v0.7.0")),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }));
        var result = await Make(http, GitLab()).CheckAsync();

        Assert.Equal(UpdateStatus.Available, result.Status);
        var update = result.Update!;
        Assert.Equal(("0.7.0", "v0.7.0", UpdateSourceKind.GitLab, PackageKind.Portable), (update.Version, update.Tag, update.Source, update.Kind));
        Assert.Equal("Meridian-0.7.0-macos-arm64.zip", update.Asset.Name);
        Assert.Equal("https://gitlab.com/api/v4/projects/4711/packages/generic/meridian/0.7.0/Meridian-0.7.0-macos-arm64.zip", update.Asset.Url);
        Assert.Equal("SHA256SUMS.txt", update.Checksums!.Name);
        Assert.Equal("https://gitlab.com/acme/meridian/-/releases/v0.7.0", update.PageUrl);
        Assert.Equal("https://gitlab.com/api/v4/projects/acme%2Fmeridian/releases/permalink/latest", seen[0].AbsoluteUri);
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public async Task Falls_back_to_listing_on_older_gitlab_and_skips_upcoming_and_pre_releases()
    {
        using var http = new HttpClient(new UpdaterTests.Stub(r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path.EndsWith("/permalink/latest")) return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"404 Not Found"}""") };
            if (path == "/gitlab/api/v4/projects/42/releases")
            {
                Assert.Contains("per_page=50", r.RequestUri.Query);
                return Ok("[" + string.Join(",", ReleaseJson("v0.9.0", upcoming: true), ReleaseJson("v0.8.0-rc.1"), ReleaseJson("v0.7.1"), ReleaseJson("v0.7.0"), """{"tag_name":"nightly"}""") + "]");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var result = await Make(http, GitLab("https://git.acme.test/gitlab/", "42"), platform: Windows).CheckAsync();

        Assert.Equal(UpdateStatus.Available, result.Status);
        Assert.Equal("0.7.1", result.Update!.Version);
        Assert.Equal("Meridian-0.7.0-windows-x64-setup.exe", result.Update.Asset.Name);   // the fake release reuses the file list
    }

    [Fact]
    public async Task A_pre_release_permalink_also_falls_back_to_the_list()
    {
        using var http = new HttpClient(new UpdaterTests.Stub(r => r.RequestUri!.AbsolutePath.EndsWith("/permalink/latest")
            ? Ok(ReleaseJson("v0.8.0-beta.2"))
            : Ok("[" + ReleaseJson("v0.8.0-beta.2") + "," + ReleaseJson("v0.6.0") + "]")));
        var result = await Make(http, GitLab()).CheckAsync();
        Assert.Equal(UpdateStatus.UpToDate, result.Status);
        Assert.Equal("0.6.0", result.LatestVersion);
    }

    [Fact]
    public async Task Downloads_from_the_package_registry_and_verifies_sha256sums_with_the_token_only_on_the_gitlab_host()
    {
        var payload = Encoding.UTF8.GetBytes("mac portable zip");
        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var seen = new List<(Uri Url, string? PrivateToken, string? Auth)>();
        using var http = new HttpClient(new UpdaterTests.Stub(r =>
        {
            var url = r.RequestUri!;
            seen.Add((url, r.Headers.TryGetValues("PRIVATE-TOKEN", out var t) ? t.Single() : null, r.Headers.Authorization?.ToString()));
            if (url.AbsolutePath.EndsWith("/permalink/latest")) return Redirect("https://gitlab.acme.test/api/v4/projects/acme%2Fmeridian/releases/v0.7.0");
            if (url.AbsolutePath.EndsWith("/releases/v0.7.0")) return Ok(ReleaseJson("v0.7.0", host: "https://gitlab.acme.test"));
            if (url.Host == "gitlab.acme.test" && url.AbsolutePath.Contains("/packages/generic/"))
                // Object storage: GitLab redirects package downloads to a pre-signed address on another host.
                return Redirect("https://objects.acme-storage.test/bucket/" + url.Segments[^1] + "?X-Amz-Signature=abc");
            if (url.Host == "objects.acme-storage.test")
                return url.Segments[^1] == "SHA256SUMS.txt"
                    ? Ok($"{hash}  Meridian-0.7.0-macos-arm64.zip\n{new string('0', 64)}  Meridian-0.7.0-windows-x64.zip\n")
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var updater = Make(http, GitLab("https://gitlab.acme.test", token: Token));
        var result = await updater.CheckAsync();
        Assert.Equal(UpdateStatus.Available, result.Status);
        var path = await updater.DownloadAsync(result.Update!);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Directory.Delete(Path.GetDirectoryName(path)!, true);

        Assert.Contains(seen, s => s.Url.Host == "objects.acme-storage.test");
        foreach (var (url, privateToken, auth) in seen)
        {
            Assert.Null(auth);   // GitLab uses PRIVATE-TOKEN, never a Bearer header here
            if (url.Host == "gitlab.acme.test") Assert.Equal(Token, privateToken);
            else Assert.Null(privateToken);
        }
    }

    [Fact]
    public async Task A_checksum_mismatch_discards_the_download()
    {
        using var http = new HttpClient(new UpdaterTests.Stub(r =>
        {
            var p = r.RequestUri!.AbsolutePath;
            if (p.EndsWith("/permalink/latest")) return Ok(ReleaseJson("v0.7.0"));
            if (p.EndsWith("/SHA256SUMS.txt")) return Ok($"{new string('a', 64)}  Meridian-0.7.0-macos-arm64.zip\n");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        }));
        var updater = Make(http, GitLab());
        var result = await updater.CheckAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => updater.DownloadAsync(result.Update!));
        Assert.Contains("Checksum mismatch", ex.Message);
    }

    [Fact]
    public void The_token_is_scoped_to_the_configured_gitlab_instance()
    {
        using var http = new HttpClient(new UpdaterTests.Stub(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var selfManaged = Make(http, GitLab("https://code.acme.test/gitlab", token: Token));
        Assert.True(selfManaged.SendsTokenTo(new Uri("https://code.acme.test/gitlab/api/v4/projects/1/releases")));
        Assert.False(selfManaged.SendsTokenTo(new Uri("https://code.acme.test/other-app/api/v4/projects/1")));   // same host, outside the GitLab path
        Assert.False(selfManaged.SendsTokenTo(new Uri("http://code.acme.test/gitlab/api/v4/projects/1")));      // not over http
        Assert.False(selfManaged.SendsTokenTo(new Uri("https://code.acme.test:8443/gitlab/api/v4/projects/1"))); // other port
        Assert.False(selfManaged.SendsTokenTo(new Uri("https://code.acme.test.evil.example/gitlab/api/v4")));
        Assert.False(selfManaged.SendsTokenTo(new Uri("https://gitlab.com/api/v4/projects/1")));
        Assert.False(selfManaged.SendsTokenTo(new Uri("https://api.github.com/repos/x/y")));

        var dotCom = Make(http, GitLab(token: Token));
        Assert.True(dotCom.SendsTokenTo(new Uri("https://gitlab.com/api/v4/projects/acme%2Fmeridian/releases")));
        Assert.False(Make(http, GitLab()).SendsTokenTo(new Uri("https://gitlab.com/api/v4/projects/1")));   // no token saved
        // A GitHub token is never sent to GitLab, and the GitLab mode never sends to api.github.com.
        Assert.False(Make(http, new UpdateSource(UpdateSourceKind.GitHubToken, Token: Token)).SendsTokenTo(new Uri("https://gitlab.com/api/v4/projects/1")));
    }

    [Fact]
    public async Task Redirects_to_another_host_do_not_carry_the_token_and_http_downgrades_are_refused()
    {
        var seen = new List<(Uri Url, bool Token)>();
        using var http = new HttpClient(new UpdaterTests.Stub(r =>
        {
            seen.Add((r.RequestUri!, r.Headers.Contains("PRIVATE-TOKEN")));
            return r.RequestUri!.Host == "gitlab.com"
                ? Redirect("https://mirror.evil.example/api/v4/projects/1/releases/v9")
                : Redirect("http://mirror.evil.example/plain");
        }));
        var result = await Make(http, GitLab(token: Token)).CheckAsync();
        Assert.Equal(UpdateStatus.Unreachable, result.Status);
        Assert.Contains("https only", result.Message);
        Assert.StartsWith("Couldn't reach GitLab", result.Message);
        Assert.Equal([true, false], seen.Select(s => s.Token));
        Assert.DoesNotContain(seen, s => s.Url.Scheme == "http");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, UpdateStatus.Unauthorized, "rejected the token", true)]
    [InlineData(HttpStatusCode.Forbidden, UpdateStatus.Unreachable, "read_api", true)]
    [InlineData(HttpStatusCode.NotFound, UpdateStatus.PrivateRepository, "add a token", false)]
    [InlineData(HttpStatusCode.NotFound, UpdateStatus.PrivateRepository, "token can't read it", true)]
    [InlineData(HttpStatusCode.TooManyRequests, UpdateStatus.Unreachable, "rate limit", false)]
    [InlineData(HttpStatusCode.BadGateway, UpdateStatus.Unreachable, "502", false)]
    public async Task Gitlab_errors_become_clear_sentences(HttpStatusCode status, UpdateStatus expected, string text, bool withToken)
    {
        using var http = new HttpClient(new UpdaterTests.Stub(_ => new HttpResponseMessage(status) { Content = new StringContent("""{"message":"error"}""") }));
        var result = await Make(http, GitLab(token: withToken ? Token : null)).CheckAsync();
        Assert.Equal(expected, result.Status);
        Assert.Contains(text, result.Message);
        Assert.DoesNotContain(Token, result.Message);
    }

    [Fact]
    public async Task Garbage_is_reported_as_invalid_and_missing_settings_as_not_configured()
    {
        using var html = new HttpClient(new UpdaterTests.Stub(_ => Ok("<html>sign in</html>")));
        var bad = await Make(html, GitLab()).CheckAsync();
        Assert.Equal(UpdateStatus.Invalid, bad.Status);
        Assert.StartsWith("GitLab's answer could not be read", bad.Message);

        using var none = new HttpClient(new UpdaterTests.Stub(_ => throw new InvalidOperationException("no request expected")));
        Assert.Equal(UpdateStatus.NotConfigured, (await Make(none, GitLab(project: "")).CheckAsync()).Status);
        Assert.Equal(UpdateStatus.NotConfigured, (await Make(none, GitLab(project: "solo")).CheckAsync()).Status);
        Assert.Equal(UpdateStatus.NotConfigured, (await Make(none, GitLab("http://gitlab.example.com")).CheckAsync()).Status);
    }

    [Theory]
    [InlineData(null, "https://gitlab.com/")]
    [InlineData("", "https://gitlab.com/")]
    [InlineData("https://gitlab.example.com/", "https://gitlab.example.com/")]
    [InlineData("https://gitlab.example.com/api/v4", "https://gitlab.example.com/")]
    [InlineData("https://example.com/gitlab", "https://example.com/gitlab/")]
    [InlineData("http://localhost:8929", "http://localhost:8929/")]
    [InlineData("http://gitlab.example.com", null)]
    [InlineData("https://user:pw@gitlab.example.com", null)]
    [InlineData("https://gitlab.example.com/?x=1", null)]
    [InlineData("not a url", null)]
    public void Parses_gitlab_addresses(string? text, string? expected)
    {
        var error = UpdateSource.ParseGitLabUrl(text, out var uri);
        Assert.Equal(expected, uri?.AbsoluteUri);
        Assert.Equal(expected is null, error is not null);
    }

    [Theory]
    [InlineData("acme/meridian", true)]
    [InlineData("acme/tools/meridian.desktop", true)]
    [InlineData("12345", true)]
    [InlineData("meridian", false)]
    [InlineData("acme//meridian", false)]
    [InlineData("acme/../x", false)]
    [InlineData("", false)]
    public void Validates_gitlab_projects(string project, bool ok) => Assert.Equal(ok, UpdateSource.ValidateGitLabProject(project) is null);

    [Fact]
    public void The_gitlab_token_is_stored_encrypted_and_resolved_with_the_project()
    {
        var dir = Directory.CreateTempSubdirectory("meridian-gitlab-source ");
        try
        {
            var protector = new UpdateTokenProtector(Path.Combine(dir.FullName, "desktop-keys"));
            var settings = new DesktopSettings
            {
                UpdateSource = UpdateSourceKind.GitLab, UpdateGitLabUrl = "https://gitlab.acme.test", UpdateGitLabProject = "acme/meridian",
                ProtectedGitLabToken = protector.Protect(Token),
            };
            var json = JsonSerializer.Serialize(settings);
            Assert.DoesNotContain("s3cr3t", json);
            var source = JsonSerializer.Deserialize<DesktopSettings>(json)!.ResolveUpdateSource(protector);
            Assert.Equal((UpdateSourceKind.GitLab, Token, "acme/meridian"), (source.Kind, source.Token, source.GitLabProject));
            Assert.Equal(new Uri("https://gitlab.acme.test/"), source.GitLabBase());
            Assert.Null(UpdateTokenProtector.Validate(Token, "GitLab"));
            Assert.Contains("GitLab", UpdateTokenProtector.Validate("glpat-short", "GitLab"));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Release_links_outside_the_gitlab_api_are_downloaded_through_their_direct_address()
    {
        using var doc = JsonDocument.Parse(ReleaseJson("v0.7.0", linkHost: "https://downloads.example.org"));
        var (tag, page, upcoming, assets) = Updater.ParseGitLabRelease(doc.RootElement, new Uri("https://gitlab.com/"));
        Assert.Equal(("v0.7.0", "https://gitlab.com/acme/meridian/-/releases/v0.7.0", false), (tag, page, upcoming));
        Assert.Equal(Files.Length, assets.Count);
        Assert.All(assets, a => Assert.StartsWith("https://gitlab.com/acme/meridian/-/releases/v0.7.0/downloads/", a.Url));
    }
}
