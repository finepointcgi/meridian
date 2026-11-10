using System.Text.Json;

namespace Meridian.Desktop;

/// <summary>Start-screen choices, kept in the user's application-data folder next to the local database.</summary>
internal sealed class DesktopSettings
{
    public static string Folder => Environment.GetEnvironmentVariable("MERIDIAN_DATA") is { Length: > 0 } custom ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "Meridian");

    private static string FilePath => Path.Combine(Folder, "desktop.json");

    /// <summary>"local" or "server".</summary>
    public string? LastMode { get; set; }

    /// <summary>Open the last choice directly on launch instead of showing the start screen.</summary>
    public bool AutoOpen { get; set; }

    /// <summary>Recently used team servers, most recent first.</summary>
    public List<string> Servers { get; set; } = new();

    /// <summary>Files opened with Meridian (double-click, "Open with", drag onto the app), most recent first.</summary>
    public List<RecentFile> RecentFiles { get; set; } = new();

    /// <summary>Look for a newer release on GitHub at most once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;

    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>The last check's result, as shown in Settings ▸ Desktop ("You're up to date (0.5.0)").</summary>
    public string? LastUpdateMessage { get; set; }
    public UpdateStatus? LastUpdateStatus { get; set; }

    /// <summary>The newer release found by the last check, shown again on later launches until installed.</summary>
    public UpdateInfo? AvailableUpdate { get; set; }

    /// <summary>Where updates come from: GitHub releases (default), GitHub with a token, a custom manifest address, or GitLab releases.</summary>
    public UpdateSourceKind UpdateSource { get; set; } = UpdateSourceKind.GitHub;

    /// <summary>The manifest address for <see cref="UpdateSourceKind.Manifest"/> (https://, or http://localhost for testing).</summary>
    public string? UpdateManifestUrl { get; set; }

    /// <summary>The GitHub token for <see cref="UpdateSourceKind.GitHubToken"/>, encrypted with Data Protection (<see cref="UpdateTokenProtector"/>).</summary>
    public string? ProtectedGitHubToken { get; set; }

    /// <summary>The GitLab instance for <see cref="UpdateSourceKind.GitLab"/> (https://gitlab.com when empty).</summary>
    public string? UpdateGitLabUrl { get; set; }

    /// <summary>The GitLab project (group/project path or numeric id) for <see cref="UpdateSourceKind.GitLab"/>.</summary>
    public string? UpdateGitLabProject { get; set; }

    /// <summary>The optional GitLab token (read_api) for a private project, encrypted like the GitHub token; sent only to that GitLab host.</summary>
    public string? ProtectedGitLabToken { get; set; }

    /// <summary>The update source to check, with the token decrypted.</summary>
    public UpdateSource ResolveUpdateSource(UpdateTokenProtector? tokens) => UpdateSource switch
    {
        UpdateSourceKind.GitHubToken => new UpdateSource(UpdateSourceKind.GitHubToken, Token: tokens?.Unprotect(ProtectedGitHubToken)),
        UpdateSourceKind.Manifest => new UpdateSource(UpdateSourceKind.Manifest, ManifestUrl: UpdateManifestUrl),
        UpdateSourceKind.GitLab => new UpdateSource(UpdateSourceKind.GitLab, Token: tokens?.Unprotect(ProtectedGitLabToken),
            GitLabUrl: UpdateGitLabUrl, GitLabProject: UpdateGitLabProject),
        _ => Meridian.Desktop.UpdateSource.Default,
    };

    /// <summary>Moves a file to the top of the recent list.</summary>
    public RecentFile Remember(string path, Guid? projectId, DateTime fileTimeUtc)
    {
        RecentFiles.RemoveAll(r => string.Equals(r.Path, path, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
        var entry = new RecentFile { Path = path, ProjectId = projectId, FileTimeUtc = fileTimeUtc, OpenedAt = DateTimeOffset.Now };
        RecentFiles.Insert(0, entry);
        return entry;
    }

    public static DesktopSettings Load()
    {
        try { return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            if (Servers.Count > 8) Servers.RemoveRange(8, Servers.Count - 8);
            if (RecentFiles.Count > 10) RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* settings are a convenience */ }
    }
}

/// <summary>A file opened in Meridian and the project it was imported as.</summary>
internal sealed class RecentFile
{
    public string Path { get; set; } = "";
    public Guid? ProjectId { get; set; }
    /// <summary>The file's last-write time when it was imported; a newer file is imported again.</summary>
    public DateTime FileTimeUtc { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
}
