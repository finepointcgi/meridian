using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;

namespace Meridian.Web.Services;

/// <summary>
/// Desktop local mode only: the desktop host asks the in-process server to import a file from disk as a new project
/// (a document double-clicked in Finder / Explorer, or picked on the start screen). Guarded three ways: the server must be in
/// local mode, the request must come from loopback and carry this launch's token, and the file must have a model extension.
/// </summary>
public static class LocalFileImport
{
    public const string Route = "/desktop/import";
    public const string TokenHeader = "X-Meridian-Launch";
    public const long MaxBytes = 64 * 1024 * 1024;

    /// <summary>Files the desktop app can open as a project (the same formats as "Open a file as a new project").</summary>
    public static readonly IReadOnlyList<string> Extensions = [".meridian", ".sysml", ".xmi", ".qea", ".qeax", ".mdzip", ".mdxml"];

    public static bool IsSupported(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public sealed record Request(string? Path);
    public sealed record Response(Guid ProjectId, string Name, int Warnings);

    /// <summary>Adds the endpoint (as middleware, ahead of the local sign-in gate). Does nothing unless this is a local-mode host with a launch token.</summary>
    public static void UseLocalFileImport(this WebApplication app, MeridianHostInfo host, UserAccount? localUser)
    {
        if (!host.LocalMode || localUser is null || host.LaunchToken is not { Length: > 0 } token) return;
        var expected = Encoding.UTF8.GetBytes(token);
        app.Use(async (http, next) =>
        {
            if (!http.Request.Path.Equals(Route, StringComparison.OrdinalIgnoreCase)) { await next(); return; }
            // Every answer has a body so status-code pages do not re-execute the request elsewhere.
            if (!HttpMethods.IsPost(http.Request.Method)) { await Problem(http, 405, "Use POST."); return; }
            var given = Encoding.UTF8.GetBytes(http.Request.Headers[TokenHeader].ToString());
            if (http.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip) || !CryptographicOperations.FixedTimeEquals(given, expected))
            {
                await Problem(http, 403, "Forbidden.");
                return;
            }
            Request? body;
            try { body = await JsonSerializer.DeserializeAsync<Request>(http.Request.Body, JsonSerializerOptions.Web, http.RequestAborted); }
            catch (JsonException) { body = null; }
            var path = body?.Path;
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !IsSupported(path))
            {
                await Problem(http, 400, "Only full paths to .meridian, .sysml, .xmi, .qea or .mdzip/.mdxml files can be opened.");
                return;
            }
            var file = new FileInfo(path);
            if (!file.Exists) { await Problem(http, 404, $"{file.Name} was not found."); return; }
            if (file.Length > MaxBytes) { await Problem(http, 413, $"{file.Name} is larger than 64 MB."); return; }
            try
            {
                var (model, report) = Import(file.FullName);
                var name = Path.GetFileNameWithoutExtension(file.Name);
                var db = http.RequestServices.GetRequiredService<IMeridianStore>();
                var id = await db.CreateProjectAsync(name, ProjectFile.Serialize(model), localUser.Id);
                await http.Response.WriteAsJsonAsync(new Response(id, name, report?.Warnings.Count ?? 0), JsonSerializerOptions.Web);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Xml.XmlException or JsonException
                or InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
            {
                await Problem(http, 422, $"Could not open {file.Name}: {ex.Message}");
            }
        });
    }

    /// <summary>Reads a model file by extension.</summary>
    public static (ModelRepository Model, ImportReport? Report) Import(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".qea" or ".qeax":
                // EA repositories are SQLite databases: read a private copy so the user's file is never locked or modified.
                var dir = Directory.CreateTempSubdirectory("meridian-ea-");
                try
                {
                    var tmp = Path.Combine(dir.FullName, "model.qea");
                    File.Copy(path, tmp);
                    return EaImporter.Import(tmp);
                }
                finally
                {
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    try { dir.Delete(true); } catch (IOException) { }
                }
            case ".xmi":
                return XmiExchange.Import(System.Xml.Linq.XDocument.Parse(File.ReadAllText(path)));
            case ".sysml":
                return SysMLv2Text.Import(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));
            case ".meridian":
                return (ProjectFile.Deserialize(File.ReadAllText(path)), null);
            case ".mdzip" or ".mdxml":
            {
                var (md, report) = MagicDrawImporter.Import(path);
                return (md, report.ToImportReport());
            }
            default:
                throw new ArgumentException("Unsupported file type.");
        }
    }

    private static Task Problem(HttpContext http, int status, string message)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new { error = message });
    }
}
