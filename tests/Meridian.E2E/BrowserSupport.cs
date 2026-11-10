using System.Runtime.InteropServices;
using System.Text.Json;

namespace Meridian.E2E;

/// <summary>
/// Finds out whether the Playwright Chromium build this package needs is installed, so the suite can skip
/// (instead of failing) on machines and CI jobs that never ran <c>playwright install chromium</c>.
/// Set <c>MERIDIAN_E2E=require</c> to turn a missing browser into a failure (the dedicated CI job does).
/// </summary>
public static class BrowserSupport
{
    private static readonly Lazy<string?> _missing = new(FindProblem);

    /// <summary>Null when Chromium is installed; otherwise why the browser tests cannot run.</summary>
    public static string? MissingReason => _missing.Value;

    public static bool Required => string.Equals(Environment.GetEnvironmentVariable("MERIDIAN_E2E"), "require", StringComparison.OrdinalIgnoreCase);

    private static string? FindProblem()
    {
        var manifest = Path.Combine(AppContext.BaseDirectory, ".playwright", "package", "browsers.json");
        if (!File.Exists(manifest)) return $"Playwright driver not found at {manifest}. Build tests/Meridian.E2E first.";
        string? revision = null, shellRevision = null;
        using (var doc = JsonDocument.Parse(File.ReadAllText(manifest)))
        {
            foreach (var b in doc.RootElement.GetProperty("browsers").EnumerateArray())
            {
                var name = b.GetProperty("name").GetString();
                if (name == "chromium") revision = b.GetProperty("revision").GetString();
                if (name == "chromium-headless-shell") shellRevision = b.GetProperty("revision").GetString();
            }
        }
        var root = BrowsersPath();
        bool Has(string folder, string? rev) => rev != null && Directory.Exists(Path.Combine(root, $"{folder}-{rev}"));
        if (Has("chromium_headless_shell", shellRevision) || Has("chromium", revision)) return null;
        return $"Playwright Chromium (revision {shellRevision ?? revision}) is not installed in {root}. " +
               "Run tests/Meridian.E2E/install-browsers.sh (see tests/Meridian.E2E/README.md) to enable the browser tests.";
    }

    private static string BrowsersPath()
    {
        if (Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH") is { Length: > 0 } p && p != "0") return p;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return Path.Combine(home, "Library", "Caches", "ms-playwright");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright");
        return Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".cache"), "ms-playwright");
    }
}

/// <summary>A browser test: skipped with a clear message when Playwright's Chromium is not installed.</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (BrowserSupport.MissingReason is { } why && !BrowserSupport.Required) Skip = why;
    }
}
