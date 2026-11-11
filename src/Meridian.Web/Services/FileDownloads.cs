using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.JSInterop;

namespace Meridian.Web.Services;

/// <summary>
/// Hands large generated files (whole-model documents, reports) to the browser by URL instead of pushing them through one
/// base64 JS-interop string. The generator writes into <see cref="CreateFile"/>; <see cref="Publish"/> returns a
/// one-time URL (<c>/_dl/&lt;random token&gt;</c>, signed-in users only) that streams the file once and deletes it.
/// Unclaimed files expire after <see cref="Lifetime"/>.
/// </summary>
public sealed class FileDownloads : IDisposable
{
    public const string Route = "/_dl/";

    /// <summary>Files at or below this size still go through the direct interop download (one round trip).</summary>
    public int InlineLimit { get; init; } = 4 * 1024 * 1024;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private sealed record Entry(string Path, string Name, string Mime, DateTime Expires);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly string _dir;
    private readonly Timer _sweep;

    public FileDownloads() : this(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meridian-downloads-" + Environment.ProcessId)) { }

    public FileDownloads(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(_dir);
        _sweep = new Timer(_ => Sweep(DateTime.UtcNow), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>A new, empty, seekable temp file to generate into. Dispose it before <see cref="Publish"/>; on failure call <see cref="Discard"/>.</summary>
    public FileStream CreateFile() =>
        new(System.IO.Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".part"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024);

    /// <summary>Deletes a file made by <see cref="CreateFile"/> that will not be published.</summary>
    public static void Discard(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Registers a finished file and returns its relative one-time URL (no leading slash).</summary>
    public string Publish(string path, string fileName, string mime)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _entries[token] = new Entry(path, fileName, mime, DateTime.UtcNow + Lifetime);
        return Route.TrimStart('/') + token;
    }

    /// <summary>Takes the file for <paramref name="token"/> (each URL works once). The stream deletes the file when closed.</summary>
    public (FileStream Stream, string Name, string Mime)? Take(string token)
    {
        if (!_entries.TryRemove(token, out var e)) return null;
        if (e.Expires < DateTime.UtcNow) { Discard(e.Path); return null; }
        try
        {
            return (new FileStream(e.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024,
                                   FileOptions.DeleteOnClose | FileOptions.SequentialScan | FileOptions.Asynchronous), e.Name, e.Mime);
        }
        catch (IOException) { return null; }
    }

    /// <summary>Deletes the files of expired, unclaimed downloads.</summary>
    public void Sweep(DateTime now)
    {
        foreach (var (token, e) in _entries)
            if (e.Expires < now && _entries.TryRemove(token, out _)) Discard(e.Path);
    }

    public void Dispose()
    {
        _sweep.Dispose();
        foreach (var e in _entries.Values) Discard(e.Path);
        _entries.Clear();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Maps the download route (signed-in users only; the token is the capability).</summary>
    public static void Map(WebApplication app) =>
        app.MapGet(Route + "{token}", (string token, FileDownloads downloads, HttpContext http) =>
        {
            if (token.Length != 64 || downloads.Take(token) is not { } f) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            // Always an attachment; if a browser shows it anyway, generated HTML must not run script on this origin.
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; sandbox";
            return Results.File(f.Stream, f.Mime, f.Name);
        }).RequireAuthorization();
}

/// <summary>Writes a generated file and hands it to the browser (or the desktop host's save dialog): small files inline
/// through <c>meridian.download</c>, large ones through a <see cref="FileDownloads"/> one-time URL.</summary>
public static class GeneratedDownload
{
    /// <summary>Generous: the desktop host receives a large file in chunks inside this one call.</summary>
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Runs <paramref name="write"/> into a temp file (or memory when <see cref="FileDownloads"/> isn't registered)
    /// and delivers it; returns the file's size in bytes.</summary>
    public static async Task<long> DeliverAsync(IServiceProvider services, IJSRuntime js, string name, string mime, Func<Stream, Task> write)
    {
        if (services.GetService(typeof(FileDownloads)) is not FileDownloads downloads)
        {
            using var ms = new MemoryStream();
            await write(ms);
            await js.InvokeVoidAsync("meridian.download", name, mime, Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length));
            return ms.Length;
        }
        var file = downloads.CreateFile();
        var path = file.Name;
        var published = false;
        try
        {
            long length;
            await using (file)
            {
                await write(file);
                length = file.Length;
            }
            if (length <= downloads.InlineLimit)
            {
                var bytes = await File.ReadAllBytesAsync(path);
                await js.InvokeVoidAsync("meridian.download", name, mime, Convert.ToBase64String(bytes));
                return length;
            }
            var url = downloads.Publish(path, name, mime);
            published = true;
            using var timeout = new CancellationTokenSource(TransferTimeout);
            await js.InvokeVoidAsync("meridian.downloadUrl", timeout.Token, name, url);
            return length;
        }
        finally
        {
            if (!published) FileDownloads.Discard(path);   // a published file is deleted once served, or by the sweep
        }
    }
}
