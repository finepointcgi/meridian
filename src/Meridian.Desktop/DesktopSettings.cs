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
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* settings are a convenience */ }
    }
}
