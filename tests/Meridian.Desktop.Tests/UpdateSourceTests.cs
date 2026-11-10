using System.Runtime.InteropServices;
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

namespace Meridian.Desktop.Tests;

public sealed class UpdateSourceTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("meridian-update-source ");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { _dir.Delete(true); } catch (IOException) { }
    }

    [Fact]
    public void The_token_is_stored_encrypted_and_never_in_clear()
    {
        const string token = "github_pat_SECRET_abcdefghijklmnopqrstuvwxyz";
        var protector = new UpdateTokenProtector(Path.Combine(_dir.FullName, "desktop-keys"));
        var settings = new DesktopSettings { UpdateSource = UpdateSourceKind.GitHubToken, ProtectedGitHubToken = protector.Protect(token) };
        var json = JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("SECRET", json);
        var back = JsonSerializer.Deserialize<DesktopSettings>(json)!;
        var source = back.ResolveUpdateSource(new UpdateTokenProtector(Path.Combine(_dir.FullName, "desktop-keys")));
        Assert.Equal(UpdateSourceKind.GitHubToken, source.Kind);
        Assert.Equal(token, source.Token);
        // Keys from another machine (another key folder) cannot decrypt it.
        Assert.Null(new UpdateTokenProtector(Path.Combine(_dir.FullName, "other-keys")).Unprotect(back.ProtectedGitHubToken));
        Assert.Null(protector.Unprotect("garbage"));
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("github_pat_has a space in it_0123456789", false)]
    [InlineData("ghp_0123456789abcdefghijklmnopqrstuvwxyz", true)]
    [InlineData("github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyz", true)]
    public void Validates_pasted_tokens(string token, bool ok) => Assert.Equal(ok, UpdateTokenProtector.Validate(token) is null);

    [Fact]
    public void Older_settings_files_default_to_github()
    {
        var old = JsonSerializer.Deserialize<DesktopSettings>("""{ "CheckForUpdates": true, "AvailableUpdate": { "Version": "0.2.0", "Tag": "v0.2.0", "PageUrl": "", "Asset": { "Name": "a.dmg", "Url": "https://x/a.dmg", "Size": 1 }, "Checksums": null } }""")!;
        Assert.Equal(UpdateSourceKind.GitHub, old.UpdateSource);
        Assert.Equal(PackageKind.Installer, old.AvailableUpdate!.Kind);
        Assert.Equal(UpdateSource.Default, old.ResolveUpdateSource(null));
        old.UpdateSource = UpdateSourceKind.Manifest;
        old.UpdateManifestUrl = "https://updates.example.com/api/desktop/latest";
        Assert.Equal(new Uri("https://updates.example.com/api/desktop/latest"), old.ResolveUpdateSource(null).ManifestUri());
    }

    /// <summary>
    /// End to end on loopback only: a Meridian team server hosts a folder of release files; the desktop updater, pointed at
    /// http://127.0.0.1/api/desktop/latest (allowed because it is explicitly configured), finds, downloads and verifies the zip.
    /// </summary>
    [Fact]
    public async Task Updates_from_a_meridian_server_manifest()
    {
        var updates = Path.Combine(_dir.FullName, "release files");
        Directory.CreateDirectory(updates);
        var payload = Encoding.UTF8.GetBytes("windows portable zip");
        await File.WriteAllBytesAsync(Path.Combine(updates, "Meridian-0.6.0-windows-x64.zip"), payload);
        await File.WriteAllTextAsync(Path.Combine(updates, "Meridian-0.6.0-linux-x64.tar.gz"), "linux");
        var data = Path.Combine(_dir.FullName, "data");
        var app = await MeridianServer.CreateAsync([], b =>
        {
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Meridian:Storage"] = "sqlite",
                ["Meridian:DataPath"] = data,
                ["Meridian:KeysPath"] = Path.Combine(data, "keys"),
                [DesktopUpdates.PathKey] = updates,
                ["Logging:LogLevel:Default"] = "Warning",
            });
            b.WebHost.UseUrls("http://127.0.0.1:0");
        }, new WebApplicationOptions { ApplicationName = "Meridian.Web", EnvironmentName = "Production", ContentRootPath = AppContext.BaseDirectory });
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var source = new UpdateSource(UpdateSourceKind.Manifest, ManifestUrl: address.TrimEnd('/') + DesktopUpdates.LatestRoute);
            Assert.True(source.AllowLoopbackHttp);
            using var http = Updater.CreateHttpClient(TimeSpan.FromSeconds(20));
            var updater = new Updater(http, "0.5.0", source: source, platform: new UpdatePlatform(OSPlatform.Windows, Architecture.X64, PreferPortable: true));
            var result = await updater.CheckAsync();
            Assert.Equal(UpdateStatus.Available, result.Status);
            Assert.Equal("Meridian-0.6.0-windows-x64.zip", result.Update!.Asset.Name);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), result.Update.Asset.Sha256);
            var path = await updater.DownloadAsync(result.Update);
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
            Directory.Delete(Path.GetDirectoryName(path)!, true);

            // The same server, same version: up to date.
            Assert.Equal("You're up to date (0.6.0).", (await new Updater(http, "0.6.0", source: source, platform: new UpdatePlatform(OSPlatform.Windows, Architecture.X64)).CheckAsync()).Message);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
