using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Meridian.Core.Model;

namespace Meridian.Web.Services;

/// <summary>
/// Serves images embedded in diagrams to the editor by URL. Images live in the model as data URIs (so they persist,
/// replicate and export), but inlining megabytes of base64 into the canvas markup would resend them on every render.
/// The canvas instead draws <c>/_img/&lt;sha256&gt;</c>; entries are content-addressed, so a URL can only be produced
/// by someone who already has the picture.
/// </summary>
public sealed class ImageCache
{
    public const string Route = "/_img/";
    private const long MaxBytes = 256L * 1024 * 1024;

    private readonly ConcurrentDictionary<string, (string Mime, byte[] Bytes)> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();
    private readonly ConditionalWeakTable<string, string> _hashes = new();
    private long _size;

    /// <summary>The URL the canvas should draw for <paramref name="dataUri"/>; the data URI itself when it isn't a valid image.</summary>
    public string Href(string dataUri)
    {
        // Field values are immutable strings, so hash each distinct instance once.
        if (!_hashes.TryGetValue(dataUri, out var hash))
        {
            if (DiagramExtras.DecodeImage(dataUri) is not { } img) return dataUri;
            hash = Convert.ToHexStringLower(SHA256.HashData(img.Bytes));
            _hashes.AddOrUpdate(dataUri, hash);
            Store(hash, img);
        }
        else if (!_entries.ContainsKey(hash) && DiagramExtras.DecodeImage(dataUri) is { } img)
            Store(hash, img);   // evicted meanwhile
        return Route.TrimStart('/') + hash;
    }

    private void Store(string hash, (string Mime, byte[] Bytes) img)
    {
        if (!_entries.TryAdd(hash, img)) return;
        _order.Enqueue(hash);
        Interlocked.Add(ref _size, img.Bytes.Length);
        while (Interlocked.Read(ref _size) > MaxBytes && _order.TryDequeue(out var old) && old != hash)
            if (_entries.TryRemove(old, out var gone)) Interlocked.Add(ref _size, -gone.Bytes.Length);
    }

    public (string Mime, byte[] Bytes)? Get(string hash) => _entries.TryGetValue(hash, out var e) ? e : null;

    /// <summary>Maps the image route (signed-in users only).</summary>
    public static void Map(WebApplication app) =>
        app.MapGet(Route + "{hash}", (string hash, ImageCache cache, HttpContext http) =>
        {
            if (cache.Get(hash) is not { } img) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            // SVG pictures are only ever shown through <image>, where scripts don't run; forbid them anyway when opened directly.
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(img.Bytes, img.Mime);
        }).RequireAuthorization();
}
