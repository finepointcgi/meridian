using System.Net;
using Microsoft.Extensions.FileProviders;

namespace Meridian.Web.Hosting;

/// <summary>
/// The user guide (docs-site, built with DocFX) served by Meridian itself at <c>/guide</c>, so it works offline, in the
/// desktop app and on servers without public hosting. build/package.sh and the Dockerfile build the guide and put it in a
/// <c>guide</c> folder next to the app; <c>Meridian:GuidePath</c> points somewhere else. In Development a local build in
/// <c>docs-site/_site</c> is used when there is no bundled copy.
/// <para>
/// Access: the guide is public documentation (the same pages as docs-site), so it is served without signing in. The sign-in
/// page links to it, which only helps if people who are not signed in can open it; the desktop app opens it in the
/// system browser, which does not hold the window's sign-in either. It is a separate branch of the pipeline, so it never
/// reaches the app's pages, APIs or the desktop's launch-token gate. See docs/UX-AUDIT-0.7.md (decision 5).
/// </para>
/// </summary>
public static class UserGuide
{
    public const string PathKey = "Meridian:GuidePath";
    public const string Route = "/guide";

    /// <summary>The folder the guide is served from, or null when this build has none.</summary>
    public sealed record Files(string? Folder)
    {
        public bool Available => Folder != null;
    }

    /// <summary>The app-relative URL of a guide page, e.g. <c>Page("verification")</c> → "/guide/docs/verification.html".</summary>
    public static string Page(string? page = null, string? anchor = null) =>
        page is null or "" ? Route + "/" : $"{Route}/docs/{page}.html" + (anchor is { Length: > 0 } a ? "#" + a : "");

    /// <summary>Guide pages linked from the app (file names under docs-site/docs, without .html).</summary>
    public static class Pages
    {
        public const string GettingStarted = "getting-started";
        public const string Migration = "migration";
        public const string ImportExport = "import-export";
        public const string VersionControl = "version-control";
        public const string SpreadsheetImport = "spreadsheet-import";
        public const string Requirements = "requirements";
        public const string Verification = "verification";
        public const string Plugins = "plugins";
        public const string Administration = "administration";
        public const string CodeSync = "code-sync";
        public const string Database = "database";
        public const string Mermaid = "mermaid";
        public const string FindReplace = "find-replace";
        public const string Instances = "instances";
        public const string DeploymentDiagrams = "deployment-diagrams";
    }

    /// <summary>The guide folder: <c>Meridian:GuidePath</c>, else <c>guide</c> next to the content root or the app, else (Development) docs-site/_site.</summary>
    public static string? FindFolder(IConfiguration config, IWebHostEnvironment env)
    {
        static bool Has(string? dir) => dir is { Length: > 0 } && File.Exists(Path.Combine(dir, "index.html"));
        if (config[PathKey] is { Length: > 0 } configured) return Has(configured) ? Path.GetFullPath(configured) : null;
        foreach (var root in new[] { env.ContentRootPath, AppContext.BaseDirectory })
            if (Has(Path.Combine(root, "guide"))) return Path.GetFullPath(Path.Combine(root, "guide"));
        if (env.IsDevelopment())
            for (var dir = new DirectoryInfo(env.ContentRootPath); dir != null; dir = dir.Parent)
                if (Has(Path.Combine(dir.FullName, "docs-site", "_site"))) return Path.Combine(dir.FullName, "docs-site", "_site");
        return null;
    }

    public static void AddMeridianUserGuide(this WebApplicationBuilder builder) =>
        builder.Services.AddSingleton(new Files(FindFolder(builder.Configuration, builder.Environment)));

    /// <summary>Serves the guide at /guide (index.html for folders), or a short page saying how to build it.</summary>
    public static void UseMeridianUserGuide(this WebApplication app)
    {
        var folder = app.Services.GetRequiredService<Files>().Folder;
        app.Map(Route, branch =>
        {
            if (folder != null)
            {
                var files = new PhysicalFileProvider(folder);
                branch.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
                branch.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = files,
                    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "public, max-age=3600",
                });
            }
            branch.Run(async http =>
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                http.Response.ContentType = "text/html; charset=utf-8";
                var what = folder != null
                    ? "This page is not in the user guide."
                    : "This copy of Meridian was built without the user guide. Release builds include it (build/package.sh, the Docker image); " +
                      "to build it yourself, run <code>dotnet tool restore</code> and <code>dotnet docfx build docfx.json</code> in <code>docs-site</code>, " +
                      "or set <code>" + PathKey + "</code> to a folder with the built guide.";
                await http.Response.WriteAsync(
                    "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>User guide — Meridian</title>" +
                    "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"></head>" +
                    "<body style=\"font:15px/1.5 system-ui, sans-serif; max-width:640px; margin:48px auto; padding:0 16px\">" +
                    $"<h1 style=\"font-size:20px\">User guide</h1><p>{what}</p><p>" +
                    (folder != null ? $"<a href=\"{WebUtility.HtmlEncode(Route)}/\">Guide home</a> · " : "") +
                    "<a href=\"/\">Back to Meridian</a></p></body></html>");
            });
        });
    }
}
