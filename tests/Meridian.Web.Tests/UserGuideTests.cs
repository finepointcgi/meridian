using System.Net;
using Meridian.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Web.Tests;

/// <summary>
/// The bundled user guide at /guide (Hosting/UserGuide.cs) and the packaging that builds it. The guide itself (DocFX) is
/// never built by the tests: a stand-in folder with an index.html is enough.
/// </summary>
public sealed class UserGuideTests : IAsyncLifetime
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("meridian-guide-");
    private readonly List<WebApplication> _apps = new();
    private string Guide => Path.Combine(_dir.FullName, "guide");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(Guide, "docs"));
        File.WriteAllText(Path.Combine(Guide, "index.html"), "<!doctype html><title>Meridian User Guide</title>");
        File.WriteAllText(Path.Combine(Guide, "docs", "verification.html"), "<!doctype html><title>Verification</title>");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var app in _apps) { await app.StopAsync(); await app.DisposeAsync(); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { _dir.Delete(true); } catch (IOException) { }
    }

    private async Task<HttpClient> StartAsync(string? guidePath, bool localMode = false)
    {
        var data = Path.Combine(_dir.FullName, "data-" + _apps.Count);
        var settings = new Dictionary<string, string?>
        {
            ["Meridian:Storage"] = "sqlite",
            ["Meridian:DataPath"] = data,
            ["Meridian:KeysPath"] = Path.Combine(data, "keys"),
            ["Logging:LogLevel:Default"] = "Warning",
            [UserGuide.PathKey] = guidePath ?? Path.Combine(_dir.FullName, "no-guide-here"),
        };
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

    [Fact]
    public async Task Serves_index_html_and_pages_without_signing_in()
    {
        using var http = await StartAsync(Guide);

        using var bare = await http.GetAsync("/guide");
        Assert.Contains(bare.StatusCode, new[] { HttpStatusCode.MovedPermanently, HttpStatusCode.Found });
        Assert.Equal("/guide/", bare.Headers.Location!.OriginalString.Replace(http.BaseAddress!.ToString().TrimEnd('/'), ""));

        using var index = await http.GetAsync("/guide/");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal("text/html", index.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Meridian User Guide", await index.Content.ReadAsStringAsync());

        using var page = await http.GetAsync(UserGuide.Page(UserGuide.Pages.Verification));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<title>Verification</title>", await page.Content.ReadAsStringAsync());

        using var missing = await http.GetAsync("/guide/docs/nope.html");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("not in the user guide", await missing.Content.ReadAsStringAsync());

        // The app itself still needs a sign-in; the guide is only its own branch.
        using var app = await http.GetAsync("/");
        Assert.True(app.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found, $"/ answered {app.StatusCode}");
        Assert.Contains("/login", app.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Desktop_local_mode_serves_the_guide_to_the_system_browser_but_keeps_the_workspace_private()
    {
        using var http = await StartAsync(Guide, localMode: true);
        using var guide = await http.GetAsync("/guide/");
        Assert.Equal(HttpStatusCode.OK, guide.StatusCode);
        using var workspace = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.Forbidden, workspace.StatusCode);
    }

    [Fact]
    public async Task Without_a_bundled_guide_says_how_to_build_it()
    {
        using var http = await StartAsync(guidePath: null);
        using var res = await http.GetAsync("/guide/");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("built without the user guide", body);
        Assert.Contains("docfx", body);
    }

    [Fact]
    public void Page_urls_point_into_the_docs_folder()
    {
        Assert.Equal("/guide/", UserGuide.Page());
        Assert.Equal("/guide/docs/migration.html", UserGuide.Page(UserGuide.Pages.Migration));
        Assert.Equal("/guide/docs/verification.html#criteria", UserGuide.Page("verification", "criteria"));
        // Every page the app links to exists in the docs-site sources.
        foreach (var f in typeof(UserGuide.Pages).GetFields())
        {
            var name = (string)f.GetRawConstantValue()!;
            Assert.True(File.Exists(Path.Combine(RepoRoot(), "docs-site", "docs", name + ".md")), $"docs-site/docs/{name}.md is missing");
        }
    }

    // ---------------------------------------------------------------- packaging

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Meridian.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("Meridian.slnx not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Package_script_builds_the_guide_with_docfx_and_puts_it_in_every_package()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "build", "package.sh"));
        Assert.Contains("dotnet docfx build docfx.json --warningsAsErrors", script);
        Assert.Contains("dotnet docfx metadata docfx.json", script);
        // Built once before the platform loop, then copied into the desktop folders and the server zip.
        var main = script[script.IndexOf("# ---------------------------------------------------------------- main", StringComparison.Ordinal)..];
        Assert.True(main.IndexOf("build_guide", StringComparison.Ordinal) < main.IndexOf("for rid in", StringComparison.Ordinal), "build_guide runs before the packages");
        var desktop = Section(script, "publish_desktop() {");
        Assert.Contains("add_guide \"$dir\"", desktop);
        var server = script[script.IndexOf("echo \"==> server\"", StringComparison.Ordinal)..];
        Assert.Contains("add_guide \"$srv\"", server);
        Assert.Contains("cp -R \"$GUIDE\" \"$1/guide\"", script);   // served from <app>/guide (UserGuide.FindFolder)
    }

    [Fact]
    public void Docker_image_builds_the_guide_into_app_guide()
    {
        var dockerfile = File.ReadAllText(Path.Combine(RepoRoot(), "Dockerfile"));
        Assert.Contains("COPY docs-site/ docs-site/", dockerfile);
        Assert.Contains("dotnet docfx build docfx.json --warningsAsErrors", dockerfile);
        Assert.Contains("cp -R _site /app/guide", dockerfile);
        var ignore = File.ReadAllText(Path.Combine(RepoRoot(), ".dockerignore"));
        Assert.DoesNotContain("\ndocs-site\n", "\n" + ignore + "\n");
    }

    [Fact]
    public void The_guide_is_not_built_by_the_test_projects()
    {
        foreach (var proj in Directory.GetFiles(Path.Combine(RepoRoot(), "tests"), "*.csproj", SearchOption.AllDirectories)
                     .Concat(Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories)))
            Assert.DoesNotContain("docfx", File.ReadAllText(proj), StringComparison.OrdinalIgnoreCase);
    }

    private static string Section(string script, string start)
    {
        var i = script.IndexOf(start, StringComparison.Ordinal);
        Assert.True(i >= 0, start + " not found");
        var end = script.IndexOf("\n}\n", i, StringComparison.Ordinal);
        return script[i..end];
    }
}
