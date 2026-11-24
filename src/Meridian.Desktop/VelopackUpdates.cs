using Velopack;
using Velopack.Sources;

namespace Meridian.Desktop;

/// <summary>
/// One-click updates for a copy of Meridian installed from a Velopack package (the Windows setup, the macOS .dmg / .zip and
/// the Linux AppImage since 0.11): downloads the new release (a small delta package when the previous one is on the feed),
/// checks its SHA-256 and size against the feed's release index, then quits Meridian, swaps the files and starts the new
/// version. The feed is the update source chosen in Settings ▸ Desktop, read in Velopack's format (releases.&lt;channel&gt;.json
/// next to the packages). Copies installed otherwise (a development build, the .deb, the tar.gz, installs from 0.10.x and
/// earlier) keep using <see cref="Updater"/> and <see cref="PortableInstaller"/>.
/// </summary>
public static class VelopackUpdates
{
    /// <summary>The release channel for this OS and CPU ("osx-arm64", "win-x64", "linux-x64"), as build/package.sh packs them.</summary>
    public static string Channel(UpdatePlatform platform)
    {
        var os = platform.Os == System.Runtime.InteropServices.OSPlatform.OSX ? "osx"
            : platform.Os == System.Runtime.InteropServices.OSPlatform.Windows ? "win" : "linux";
        var arch = platform.Arch == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        return $"{os}-{arch}";
    }

    /// <summary>True when this copy was installed from a Velopack package and can update itself that way.</summary>
    public static bool IsInstalled()
    {
        try { return new UpdateManager(new SimpleFileSource(new DirectoryInfo(AppContext.BaseDirectory))).IsInstalled; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// The Velopack feed for an update source: GitHub releases (with the token for a private repository), a GitLab project's
    /// releases, or the folder a manifest lives in (a team server's /api/desktop/latest reads its files from /api/desktop/files).
    /// Null when the source has no usable address.
    /// </summary>
    public static IUpdateSource? SourceFor(UpdateSource source)
    {
        switch (source.Kind)
        {
            case UpdateSourceKind.GitHub or UpdateSourceKind.GitHubToken:
                return new GithubSource($"https://github.com/{source.Repository}", source.Kind == UpdateSourceKind.GitHubToken ? source.Token : null, prerelease: false);
            case UpdateSourceKind.GitLab:
                if (source.GitLabBase() is not { } gitLab || string.IsNullOrWhiteSpace(source.GitLabProject)) return null;
                return new GitlabSource(new Uri(gitLab, "api/v4/projects/" + Uri.EscapeDataString(source.GitLabProject.Trim().Trim('/'))).AbsoluteUri, source.Token ?? "", upcomingRelease: false);
            case UpdateSourceKind.Manifest:
                return FeedFolder(source.ManifestUrl) is { } folder ? new SimpleWebSource(folder) : null;
            default:
                return null;
        }
    }

    /// <summary>The folder holding a manifest's packages: a team server's files route, else the manifest's own folder.</summary>
    public static Uri? FeedFolder(string? manifestUrl)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var url)) return null;
        var path = url.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/api/desktop/latest", StringComparison.OrdinalIgnoreCase))
            return new Uri(url, path[..^"latest".Length] + "files/");
        return new Uri(url, "./");
    }

    /// <summary>
    /// Looks for a newer release on the feed, downloads it (reporting 0–100) and verifies it. Returns the release ready to
    /// apply, or null when the feed has nothing newer for this channel.
    /// </summary>
    public static async Task<(UpdateManager Manager, VelopackAsset Release)?> DownloadAsync(IUpdateSource source, Action<int> progress, CancellationToken ct = default)
    {
        var manager = new UpdateManager(source);
        var info = await manager.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);
        if (info is null) return null;
        await manager.DownloadUpdatesAsync(info, progress, ct).ConfigureAwait(false);
        return (manager, info.TargetFullRelease);
    }

    /// <summary>Starts Velopack's updater, which waits for Meridian to quit, installs <paramref name="release"/> and starts it.</summary>
    public static void ApplyAfterExit(UpdateManager manager, VelopackAsset release) =>
        manager.WaitExitThenApplyUpdates(release, silent: false, restart: true);
}
