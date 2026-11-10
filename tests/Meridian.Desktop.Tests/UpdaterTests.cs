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
    [InlineData("LINUX", Architecture.X64, "Meridian-0.2.0-linux-x64.AppImage")]
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

    [Fact]
    public async Task A_private_repository_404_means_no_update()
    {
        var logs = new List<string>();
        using var http = new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(await new Updater(http, "0.1.0", logs.Add).CheckAsync());
        Assert.Contains(logs, l => l.Contains("404"));
    }

    [Fact]
    public async Task Offline_means_no_update()
    {
        using var http = new HttpClient(new Stub(_ => throw new HttpRequestException("offline")));
        Assert.Null(await new Updater(http, "0.1.0").CheckAsync());
    }

    private static string ReleaseJson(string tag, bool prerelease = false) => $$"""
        { "tag_name": "{{tag}}", "html_url": "https://github.com/finepointcgi/meridian/releases/tag/{{tag}}", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [ {{string.Join(",", Assets.Select(a => $$"""{ "name": "{{a.Name}}", "browser_download_url": "{{a.Url}}", "size": 3 }"""))}} ] }
        """;

    [Fact]
    public async Task Finds_a_newer_release_for_this_machine()
    {
        using var http = new HttpClient(new Stub(r =>
        {
            Assert.Equal("/repos/finepointcgi/meridian/releases/latest", r.RequestUri!.AbsolutePath);
            Assert.NotEmpty(r.Headers.UserAgent);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson("v0.2.0")) };
        }));
        var update = await new Updater(http, "0.1.0").CheckAsync();
        if (RuntimeInformation.OSArchitecture is not (Architecture.X64 or Architecture.Arm64) || (OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.Arm64))
            return; // no installer for this test machine
        Assert.NotNull(update);
        Assert.Equal("0.2.0", update!.Version);
        Assert.Equal("SHA256SUMS.txt", update.Checksums?.Name);
    }

    [Fact]
    public async Task Ignores_same_version_and_prereleases()
    {
        using var same = new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson("v0.1.0")) }));
        Assert.Null(await new Updater(same, "0.1.0").CheckAsync());
        using var pre = new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson("v0.2.0-rc.1", prerelease: true)) }));
        Assert.Null(await new Updater(pre, "0.1.0").CheckAsync());
    }

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
            var path = await new Updater(good, "0.1.0").DownloadAsync(update, new SyncProgress(reports.Add));
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
            Assert.Equal(1.0, reports.Last());
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
        using (var bad = Client($"{new string('0', 64)}  {asset.Name}\n"))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new Updater(bad, "0.1.0").DownloadAsync(update));
        using (var missing = Client($"{hash}  some-other-file\n"))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new Updater(missing, "0.1.0").DownloadAsync(update));
        using (var none = Client(""))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new Updater(none, "0.1.0").DownloadAsync(update with { Checksums = null }));
    }

    [Fact]
    public void Document_arguments_are_model_files_only()
    {
        var dir = Path.GetTempPath();
        var model = Path.Combine(dir, "Plant.meridian");
        var args = new[] { "--server", "https://x.example/a.sysml", "--local", "-psn_0_123", model, Path.Combine(dir, "notes.txt"), new Uri(Path.Combine(dir, "Car.sysml")).AbsoluteUri };
        Assert.Equal([Path.GetFullPath(model), Path.GetFullPath(Path.Combine(dir, "Car.sysml"))], Program.DocumentArgs(args));
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
