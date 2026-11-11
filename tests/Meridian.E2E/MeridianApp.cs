using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Meridian.E2E;

/// <summary>
/// The real Meridian web app, hosted in-process on a free loopback port with SQLite in a temp folder.
/// </summary>
public sealed class MeridianApp : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string BaseUrl { get; }
    public string DataPath { get; }
    public string? LaunchToken { get; }

    private MeridianApp(WebApplication app, string baseUrl, string dataPath, string? launchToken)
    {
        _app = app; BaseUrl = baseUrl; DataPath = dataPath; LaunchToken = launchToken;
    }

    public IServiceProvider Services => _app.Services;

    /// <summary>Server-side errors (log level Error and above), so tests also fail on exceptions the browser never sees.</summary>
    public ServerErrorLog ServerErrors { get; } = new();

    /// <param name="services">Test-only registrations, applied before the app's own (which use TryAdd for seams such as the code-sync mapper).</param>
    public static async Task<MeridianApp> StartAsync(bool localMode, IDictionary<string, string?>? extra = null, Action<IServiceCollection>? services = null)
    {
        var data = Path.Combine(Path.GetTempPath(), "meridian-e2e-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(data);
        var token = localMode ? Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)) : null;

        var settings = new Dictionary<string, string?>
        {
            ["Meridian:Storage"] = "sqlite",
            ["Meridian:DataPath"] = data,
            ["Meridian:KeysPath"] = Path.Combine(data, "keys"),
            ["Meridian:Security:LoginAttemptsPerMinute"] = "1000",
            ["Logging:LogLevel:Default"] = "Warning",
            ["DetailedErrors"] = "true",   // circuit exceptions reach the browser console with their message
        };
        // A stand-in for the bundled user guide (packaging builds the real one with DocFX; tests never run DocFX).
        var guide = Path.Combine(data, "guide");
        Directory.CreateDirectory(Path.Combine(guide, "docs"));
        File.WriteAllText(Path.Combine(guide, "index.html"), "<!doctype html><title>Meridian test guide</title><h1>User guide</h1>");
        foreach (var page in new[] { "getting-started", "migration", "verification", "import-export", "requirements", "plugins" })
            File.WriteAllText(Path.Combine(guide, "docs", page + ".html"), $"<!doctype html><title>{page} — Meridian test guide</title><h1>{page}</h1>");
        settings[Meridian.Web.Hosting.UserGuide.PathKey] = guide;
        if (localMode) { settings["Meridian:Mode"] = "local"; settings["Meridian:LaunchToken"] = token; }
        if (extra != null) foreach (var kv in extra) settings[kv.Key] = kv.Value;

        var errors = new ServerErrorLog();
        var app = await Meridian.Web.MeridianServer.CreateAsync([], b =>
        {
            b.Logging.AddProvider(errors);
            b.Configuration.AddInMemoryCollection(settings);
            b.WebHost.UseUrls("http://127.0.0.1:0");
            b.WebHost.UseStaticWebAssets();   // serves wwwroot + MudBlazor's _content from the build output
            services?.Invoke(b.Services);
        }, new WebApplicationOptions
        {
            ApplicationName = "Meridian.Web",
            EnvironmentName = "E2E",
            ContentRootPath = WebProjectDir(),
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var started = new MeridianApp(app, address.TrimEnd('/'), data, token);
        started.ServerErrors.Attach(errors);
        return started;
    }

    /// <summary>src/Meridian.Web, found by walking up from the test output folder.</summary>
    public static string WebProjectDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Meridian.Web");
            if (File.Exists(Path.Combine(candidate, "Meridian.Web.csproj"))) return candidate;
        }
        throw new DirectoryNotFoundException("Could not find src/Meridian.Web above " + AppContext.BaseDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _app.StopAsync(); } catch { }
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataPath, true); } catch { }
    }
}

/// <summary>Collects error-level log entries from the in-process server.</summary>
public sealed class ServerErrorLog : Microsoft.Extensions.Logging.ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _entries = new();
    private ServerErrorLog? _source;

    internal void Attach(ServerErrorLog source) => _source = source;

    /// <summary>All entries so far; take <see cref="Count"/> before a test and read the rest after it.</summary>
    public IReadOnlyList<string> Entries => (_source ?? this)._entries.ToList();
    public int Count => (_source ?? this)._entries.Count;

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Category(this, categoryName);
    public void Dispose() { }

    private sealed class Category(ServerErrorLog log, string name) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Error;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            log._entries.Enqueue($"{logLevel} {name}: {formatter(state, exception)}{(exception != null ? "\n" + exception : "")}");
        }
    }
}
