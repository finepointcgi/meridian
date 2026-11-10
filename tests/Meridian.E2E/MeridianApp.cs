using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

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

    public static async Task<MeridianApp> StartAsync(bool localMode, IDictionary<string, string?>? extra = null)
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
        };
        if (localMode) { settings["Meridian:Mode"] = "local"; settings["Meridian:LaunchToken"] = token; }
        if (extra != null) foreach (var kv in extra) settings[kv.Key] = kv.Value;

        var app = await Meridian.Web.MeridianServer.CreateAsync([], b =>
        {
            b.Configuration.AddInMemoryCollection(settings);
            b.WebHost.UseUrls("http://127.0.0.1:0");
            b.WebHost.UseStaticWebAssets();   // serves wwwroot + MudBlazor's _content from the build output
        }, new WebApplicationOptions
        {
            ApplicationName = "Meridian.Web",
            EnvironmentName = "E2E",
            ContentRootPath = WebProjectDir(),
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new MeridianApp(app, address.TrimEnd('/'), data, token);
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
