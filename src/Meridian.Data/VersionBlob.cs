using System.IO.Compression;
using System.Text;

namespace Meridian.Data;

/// <summary>
/// Compressed storage of committed versions in the SQLite store. A version is an immutable copy of the whole model, so a
/// 200,000-element project (~80 MB of JSON) grew the database file by ~64 MB per commit. Versions are now written as a
/// Brotli blob behind a short magic header (about a tenth of the size); rows written as JSON text by earlier releases are
/// read as before. PostgreSQL keeps <c>jsonb</c> (it already compresses large values with TOAST).
/// </summary>
public static class VersionBlob
{
    private static readonly byte[] Magic = "MRDNBR1\n"u8.ToArray();

    /// <summary>Versions smaller than this stay JSON text (not worth compressing; keeps small databases readable).</summary>
    public const int MinCompressedLength = 64 * 1024;

    /// <summary>What to store in the <c>model</c> column: the JSON text, or a compressed blob for large models.</summary>
    public static object Encode(string json)
    {
        if (json.Length < MinCompressedLength) return json;
        var utf8 = Encoding.UTF8.GetBytes(json);
        using var output = new MemoryStream(utf8.Length / 8 + Magic.Length);
        output.Write(Magic);
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true)) brotli.Write(utf8);
        return output.ToArray();
    }

    /// <summary>The JSON of a stored version (text from any release, or a blob written by <see cref="Encode"/>).</summary>
    public static string? Decode(object? stored) => stored switch
    {
        null or DBNull => null,
        string s => s,
        byte[] b when b.AsSpan().StartsWith(Magic) => Decompress(b),
        byte[] b => Encoding.UTF8.GetString(b),
        _ => stored.ToString(),
    };

    private static string Decompress(byte[] b)
    {
        using var input = new MemoryStream(b, Magic.Length, b.Length - Magic.Length, writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(brotli, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
