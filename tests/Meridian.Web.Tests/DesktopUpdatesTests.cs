using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meridian.Web;
using Meridian.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Web.Tests;

/// <summary>GET /api/desktop/latest and /api/desktop/files/{name}: desktop updates hosted by a team server.</summary>
public sealed class DesktopUpdatesTests : IAsyncLifetime
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("meridian-desktop-updates-");
    private string Updates => Path.Combine(_dir.FullName, "release files");
    private readonly List<WebApplication> _apps = new();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Updates);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var app in _apps) { await app.StopAsync(); await app.DisposeAsync(); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { _dir.Delete(true); } catch (IOException) { }
    }

    private async Task<HttpClient> StartAsync(string? updatesPath, bool localMode = false)
    {
        var data = Path.Combine(_dir.FullName, "data-" + _apps.Count);
        var settings = new Dictionary<string, string?>
        {
            ["Meridian:Storage"] = "sqlite",
            ["Meridian:DataPath"] = data,
            ["Meridian:KeysPath"] = Path.Combine(data, "keys"),
            ["Logging:LogLevel:Default"] = "Warning",
        };
        if (updatesPath != null) settings[DesktopUpdates.PathKey] = updatesPath;
        if (localMode) { settings["Meridian:Mode"] = "local"; settings["Meridian:LaunchToken"] = "T"; }
        var app = await MeridianServer.CreateAsync([], b =>
        {
            b.Configuration.AddInMemoryCollection(settings);
            b.WebHost.UseUrls("http://127.0.0.1:0");
        }, new WebApplicationOptions { ApplicationName = "Meridian.Web", EnvironmentName = "Production", ContentRootPath = AppContext.BaseDirectory });
        await app.StartAsync();
        _apps.Add(app);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(Updates, name);
        File.WriteAllText(path, content);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    [Fact]
    public async Task Lists_the_newest_release_with_checksums_and_serves_the_files_anonymously()
    {
        Write("Meridian-0.5.0-windows-x64.zip", "old windows");
        var win = Write("Meridian-0.6.0-windows-x64.zip", "new windows");
        var linux = Write("Meridian-0.6.0-linux-x64.tar.gz", "new linux");
        Write("Meridian-0.6.0-rc.1-macos-arm64.zip", "release candidate");
        Write("Meridian-Server-0.6.0.zip", "server");
        Write("notes.txt", "not a package");
        // SHA256SUMS.txt wins over hashing (here it lists the macOS zip that is not in the folder: ignored).
        var mac = Write("Meridian-0.6.0-macos-arm64.zip", "mac");
        File.WriteAllText(Path.Combine(Updates, "SHA256SUMS.txt"), $"{mac}  Meridian-0.6.0-macos-arm64.zip\n{new string('a', 64)}  Meridian-0.6.0-macos-x64.zip\n");

        using var http = await StartAsync(Updates);
        using var res = await http.GetAsync(DesktopUpdates.LatestRoute);   // no cookie: anonymous
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("0.6.0", doc.RootElement.GetProperty("version").GetString());
        var assets = doc.RootElement.GetProperty("assets").EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!, a => a);
        Assert.Equal(["Meridian-0.6.0-linux-x64.tar.gz", "Meridian-0.6.0-macos-arm64.zip", "Meridian-0.6.0-windows-x64.zip"], assets.Keys.Order());
        Assert.Equal(win, assets["Meridian-0.6.0-windows-x64.zip"].GetProperty("sha256").GetString());
        Assert.Equal(linux, assets["Meridian-0.6.0-linux-x64.tar.gz"].GetProperty("sha256").GetString());
        Assert.Equal(mac, assets["Meridian-0.6.0-macos-arm64.zip"].GetProperty("sha256").GetString());
        var url = assets["Meridian-0.6.0-windows-x64.zip"].GetProperty("url").GetString()!;
        Assert.Equal("files/Meridian-0.6.0-windows-x64.zip", url);

        // The relative url resolves against the manifest's address.
        var file = new Uri(new Uri(http.BaseAddress!, DesktopUpdates.LatestRoute), url);
        Assert.Equal("new windows", await http.GetStringAsync(file));
    }

    [Fact]
    public async Task Serves_a_hand_written_manifest_as_is()
    {
        var json = """{ "version": "0.7.0", "notes": "https://example.invalid/notes", "assets": [] }""";
        File.WriteAllText(Path.Combine(Updates, DesktopUpdates.ManifestFile), json);
        Write("Meridian-0.6.0-windows-x64.zip", "ignored while latest.json exists");
        using var http = await StartAsync(Updates);
        Assert.Equal(json, await http.GetStringAsync(DesktopUpdates.LatestRoute));
    }

    [Fact]
    public async Task Never_serves_files_outside_the_folder()
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "secret.txt"), "secret");
        Write(".hidden", "hidden");
        Write("Meridian-0.6.0-windows-x64.zip", "ok");
        using var http = await StartAsync(Updates);
        foreach (var name in new[] { "..%2Fsecret.txt", "%2E%2E%2Fsecret.txt", "..%5Csecret.txt", ".hidden", "missing.zip", "SECRET.TXT" })
        {
            using var res = await http.GetAsync($"{DesktopUpdates.FilesRoute}/{name}");
            Assert.True(res.StatusCode == HttpStatusCode.NotFound, $"{name}: {res.StatusCode}");
            Assert.DoesNotContain("secret", await res.Content.ReadAsStringAsync());
        }
        Assert.Null(DesktopUpdates.FilePath(Updates, "../secret.txt"));
        Assert.Null(DesktopUpdates.FilePath(Updates, Path.Combine(_dir.FullName, "secret.txt")));
        Assert.NotNull(DesktopUpdates.FilePath(Updates, "Meridian-0.6.0-windows-x64.zip"));
    }

    [Fact]
    public async Task Answers_404_with_a_reason_when_not_configured_or_empty()
    {
        using var none = await StartAsync(null);
        using var res = await none.GetAsync(DesktopUpdates.LatestRoute);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Contains(DesktopUpdates.PathKey, await res.Content.ReadAsStringAsync());

        using var empty = await StartAsync(Updates);
        using var res2 = await empty.GetAsync(DesktopUpdates.LatestRoute);
        Assert.Equal(HttpStatusCode.NotFound, res2.StatusCode);
        Assert.Contains("No Meridian desktop packages", await res2.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Orders_versions_semantically()
    {
        Write("Meridian-0.9.0-linux-x64.tar.gz", "a");
        Write("Meridian-0.10.0-rc.1-linux-x64.tar.gz", "b");
        Write("Meridian-0.10.0-linux-x64.tar.gz", "c");
        Assert.Equal("0.10.0", DesktopUpdates.Build(Updates)!.Version);
    }
}
