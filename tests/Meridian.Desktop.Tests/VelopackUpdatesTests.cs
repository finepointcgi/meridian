using System.Runtime.InteropServices;
using Meridian.Web.Services;
using Velopack.Sources;

namespace Meridian.Desktop.Tests;

/// <summary>One-click updates through Velopack: channel names, the feed each update source maps to, and the packaging contract.</summary>
public sealed class VelopackUpdatesTests
{
    [Theory]
    [InlineData("osx", Architecture.Arm64, "osx-arm64")]
    [InlineData("osx", Architecture.X64, "osx-x64")]
    [InlineData("win", Architecture.X64, "win-x64")]
    [InlineData("linux", Architecture.X64, "linux-x64")]
    public void Channels_Are_Named_By_Runtime(string os, Architecture arch, string channel)
    {
        var platform = os switch { "osx" => OSPlatform.OSX, "win" => OSPlatform.Windows, _ => OSPlatform.Linux };
        Assert.Equal(channel, VelopackUpdates.Channel(new UpdatePlatform(platform, arch)));
    }

    [Fact]
    public void Each_Update_Source_Maps_To_A_Feed()
    {
        Assert.IsType<GithubSource>(VelopackUpdates.SourceFor(UpdateSource.Default));
        Assert.IsType<GithubSource>(VelopackUpdates.SourceFor(new UpdateSource(UpdateSourceKind.GitHubToken, Token: "t")));
        Assert.IsType<GitlabSource>(VelopackUpdates.SourceFor(new UpdateSource(UpdateSourceKind.GitLab, GitLabUrl: "https://gitlab.example.com", GitLabProject: "tools/meridian")));
        Assert.Null(VelopackUpdates.SourceFor(new UpdateSource(UpdateSourceKind.GitLab, GitLabUrl: "https://gitlab.example.com")));
        Assert.IsType<SimpleWebSource>(VelopackUpdates.SourceFor(new UpdateSource(UpdateSourceKind.Manifest, ManifestUrl: "https://updates.example.com/meridian/latest.json")));
        Assert.Null(VelopackUpdates.SourceFor(new UpdateSource(UpdateSourceKind.Manifest, ManifestUrl: "not a url")));
    }

    [Theory]
    [InlineData("https://meridian.example.com/api/desktop/latest", "https://meridian.example.com/api/desktop/files/")]
    [InlineData("https://meridian.example.com/sub/api/desktop/latest/", "https://meridian.example.com/sub/api/desktop/files/")]
    [InlineData("https://cdn.example.com/meridian/latest.json", "https://cdn.example.com/meridian/")]
    [InlineData("http://localhost:8765/latest.json", "http://localhost:8765/")]
    public void A_Manifest_Feed_Reads_Velopack_Files_Next_To_The_Packages(string manifest, string folder) =>
        Assert.Equal(folder, VelopackUpdates.FeedFolder(manifest)!.AbsoluteUri);

    [Fact]
    public void A_Test_Host_Is_Not_A_Velopack_Install() => Assert.False(VelopackUpdates.IsInstalled());

    [Fact]
    public void A_Team_Server_Serves_The_Velopack_Index_And_Packages_By_Name()
    {
        var dir = Directory.CreateTempSubdirectory("meridian-velopack ");
        try
        {
            foreach (var f in new[] { "releases.win-x64.json", "Meridian-0.11.0-win-x64-full.nupkg", "Meridian-0.11.0-win-x64-delta.nupkg" })
            {
                File.WriteAllText(Path.Combine(dir.FullName, f), "x");
                Assert.NotNull(DesktopUpdates.FilePath(dir.FullName, f));
            }
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Package_Script_Packs_Every_Platform_As_A_Velopack_Channel()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "build", "package.sh"))) root = Path.GetDirectoryName(root)!;
        var script = File.ReadAllText(Path.Combine(root, "build", "package.sh"));
        Assert.Contains("--channel \"$rid\"", script);
        Assert.Contains("releases.$rid.json", script);
        Assert.Contains("velopack_pack \"$rid\" \"[win]\"", script);
        Assert.Contains("velopack_pack \"$rid\" \"[linux]\"", script);
        Assert.Contains("--bundleId io.meridian.desktop", script);
        // The vpk version is pinned next to the Velopack library the app uses.
        var tools = File.ReadAllText(Path.Combine(root, ".config", "dotnet-tools.json"));
        var csproj = File.ReadAllText(Path.Combine(root, "src", "Meridian.Desktop", "Meridian.Desktop.csproj"));
        var version = System.Text.RegularExpressions.Regex.Match(csproj, "Include=\"Velopack\" Version=\"([^\"]+)\"").Groups[1].Value;
        Assert.Contains($"\"version\": \"{version}\"", tools);
    }
}
