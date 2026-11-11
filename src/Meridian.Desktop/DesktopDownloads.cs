namespace Meridian.Desktop;

/// <summary>
/// The save-file bridge for files the server streams (generated reports): the page sends the download URL, with a
/// single-use ticket, instead of the file's bytes, and the host copies the response to the file the user picked in
/// chunks. A 500 MB report never sits in memory or crosses the web-message bridge as base64.
/// </summary>
internal static class DesktopDownloads
{
    public const int ChunkSize = 81_920;

    /// <summary>
    /// Only report downloads from a server this window may show: the local server or a team server from the settings.
    /// A page can't make the host fetch anything else.
    /// </summary>
    public static bool IsAllowed(Uri url, IEnumerable<string?> origins)
    {
        if (url.Scheme is not ("http" or "https") || !url.AbsolutePath.Contains("/api/reports/", StringComparison.Ordinal)) return false;
        foreach (var o in origins)
            if (Uri.TryCreate(o, UriKind.Absolute, out var allowed) &&
                Uri.Compare(allowed, url, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
                return true;
        return false;
    }

    /// <summary>Copies the response to <paramref name="target"/> chunk by chunk (through a ".part" file, so a failed or
    /// cancelled download never leaves a truncated file under the chosen name). Returns the number of bytes written.</summary>
    public static async Task<long> SaveAsync(HttpClient http, Uri url, string target, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var part = target + ".part";
        long total = 0;
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize, useAsync: true))
            {
                var buffer = new byte[ChunkSize];
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    total += n;
                    progress?.Report(total);
                }
            }
            File.Move(part, target, overwrite: true);
            return total;
        }
        catch
        {
            try { File.Delete(part); } catch (IOException) { }
            throw;
        }
    }
}
