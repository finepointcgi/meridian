using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Meridian.Serialization.DrawIo;

/// <summary>One page of a draw.io file: its name and its <c>mxGraphModel</c>.</summary>
public sealed record DrawIoPage(string Name, XElement Model)
{
    /// <summary>What was lost reading the page (a Gliffy page converted to draw.io reports here).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Reads draw.io / diagrams.net files into pages: <c>.drawio</c> / <c>.dio</c> / <c>.xml</c> (an <c>mxfile</c> whose pages are
/// plain or compressed, or a bare <c>mxGraphModel</c>), and pictures with the diagram embedded: <c>.drawio.svg</c> (the
/// <c>content</c> attribute) and <c>.drawio.png</c> (the <c>mxfile</c> text chunk). Nothing here throws on bad input other
/// than <see cref="FormatException"/> with a readable message.
/// </summary>
public static class DrawIoFile
{
    /// <summary>Largest draw.io file read (they are XML, a few MB at most).</summary>
    public const long MaxBytes = 32L * 1024 * 1024;

    /// <summary>Largest XML a compressed page may inflate to.</summary>
    private const int MaxInflated = 64 * 1024 * 1024;

    /// <summary>draw.io files, and Gliffy files (<c>.gliffy</c>), which are read as draw.io pages (see <see cref="Gliffy.GliffyFile"/>).</summary>
    public static readonly IReadOnlyList<string> Extensions = [".drawio", ".dio", ".drawio.xml", ".drawio.svg", ".drawio.png", ".dio.svg", ".dio.png", .. Gliffy.GliffyFile.Extensions, .. Visio.VisioFile.Extensions];

    /// <summary>True for a draw.io file name. A plain <c>.xml</c> is only draw.io when its content says so (see <see cref="LooksLikeDrawIo(string)"/>).</summary>
    public static bool IsDrawIoFile(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var n = name.ToLowerInvariant();
        return Extensions.Any(n.EndsWith);
    }

    /// <summary>A binary container (PNG) rather than text.</summary>
    public static bool IsBinary(string? name) => name != null && (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || Visio.VisioFile.IsVisioFile(name));

    /// <summary>True when an XML text is a draw.io document.</summary>
    public static bool LooksLikeDrawIo(string text)
    {
        var head = text.Length > 4000 ? text[..4000] : text;
        return head.Contains("<mxfile", StringComparison.Ordinal) || head.Contains("<mxGraphModel", StringComparison.Ordinal);
    }

    /// <summary>Reads the pages of a file from its bytes (any supported container).</summary>
    public static IReadOnlyList<DrawIoPage> Read(string fileName, byte[] bytes)
    {
        if (bytes.Length > MaxBytes) throw new FormatException($"{fileName} is larger than {MaxBytes / (1024 * 1024)} MB.");
        if (Gliffy.GliffyFile.IsGliffyFile(fileName)) return Gliffy.GliffyFile.Read(fileName, bytes);
        if (Visio.VisioFile.IsVisioFile(fileName)) return Visio.VisioFile.Read(fileName, bytes);
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
            return ReadText(FromPng(bytes) ?? throw new FormatException("the PNG has no draw.io diagram embedded (save it from draw.io with \"Include a copy of my diagram\")."));
        return ReadText(Encoding.UTF8.GetString(bytes));
    }

    /// <summary>Reads the pages of a draw.io XML or .drawio.svg text.</summary>
    public static IReadOnlyList<DrawIoPage> ReadText(string text)
    {
        var doc = Parse(text);
        var root = doc.Root ?? throw new FormatException("the file is empty.");
        if (root.Name.LocalName == "svg")
        {
            var content = root.Attribute("content")?.Value;
            if (string.IsNullOrWhiteSpace(content)) throw new FormatException("the SVG has no draw.io diagram embedded (export it from draw.io with \"Include a copy of my diagram\").");
            // Older exports URL-encode or compress the embedded document.
            if (!content.TrimStart().StartsWith('<')) content = Inflate(content) ?? Uri.UnescapeDataString(content);
            return ReadText(content);
        }
        if (root.Name.LocalName == "mxGraphModel") return [new DrawIoPage("Page-1", root)];
        if (root.Name.LocalName != "mxfile") throw new FormatException("this is not a draw.io file (no mxfile or mxGraphModel).");
        var pages = new List<DrawIoPage>();
        var n = 0;
        foreach (var d in root.Elements("diagram"))
        {
            n++;
            var name = d.Attribute("name")?.Value is { Length: > 0 } nm ? nm : $"Page-{n}";
            var model = d.Element("mxGraphModel");
            if (model == null)
            {
                var payload = d.Value.Trim();
                if (payload.Length == 0) continue;
                var xml = payload.StartsWith('<') ? payload : Inflate(payload) ?? throw new FormatException($"page \"{name}\" could not be decompressed.");
                model = Parse(xml).Root;
            }
            if (model?.Name.LocalName == "mxGraphModel") pages.Add(new DrawIoPage(name, model));
        }
        return pages;
    }

    private static XDocument Parse(string text)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxInflated });
            return XDocument.Load(reader);
        }
        catch (XmlException ex) { throw new FormatException("the XML could not be read: " + ex.Message, ex); }
    }

    /// <summary>draw.io's page compression: base64 of raw deflate of the URL-encoded XML. Null when the text isn't that.</summary>
    internal static string? Inflate(string payload)
    {
        byte[] data;
        try { data = Convert.FromBase64String(payload.Trim()); }
        catch (FormatException) { return null; }
        try
        {
            using var input = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > MaxInflated) throw new FormatException("a compressed page is too large.");
            }
            var s = Encoding.UTF8.GetString(output.ToArray());
            return s.StartsWith('%') || s.StartsWith("%3C", StringComparison.OrdinalIgnoreCase) || !s.TrimStart().StartsWith('<') ? Uri.UnescapeDataString(s) : s;
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>The <c>mxfile</c> tEXt / zTXt / iTXt chunk of a PNG exported from draw.io.</summary>
    internal static string? FromPng(byte[] png)
    {
        var pos = 8;
        while (pos + 12 <= png.Length)
        {
            var len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            if (len < 0 || pos + 12 + len > png.Length) return null;
            var type = Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = new ReadOnlySpan<byte>(png, pos + 8, len);
            if (type is "tEXt" or "zTXt" or "iTXt")
            {
                var zero = data.IndexOf((byte)0);
                if (zero > 0 && Encoding.Latin1.GetString(data[..zero]) == "mxfile")
                {
                    var rest = data[(zero + 1)..];
                    string? value = null;
                    if (type == "tEXt") value = Encoding.Latin1.GetString(rest);
                    else if (type == "zTXt" && rest.Length > 1) value = Encoding.Latin1.GetString(Zlib(rest[1..].ToArray()));
                    else if (type == "iTXt" && rest.Length > 2)
                    {
                        var compressed = rest[0] == 1;
                        var after = rest[2..];
                        // language tag \0 translated keyword \0 text
                        var z1 = after.IndexOf((byte)0);
                        if (z1 < 0) return null;
                        var z2 = after[(z1 + 1)..].IndexOf((byte)0);
                        if (z2 < 0) return null;
                        var text = after[(z1 + 1 + z2 + 1)..].ToArray();
                        value = Encoding.UTF8.GetString(compressed ? Zlib(text) : text);
                    }
                    if (value == null) return null;
                    return value.TrimStart().StartsWith('<') ? value : Uri.UnescapeDataString(value);
                }
            }
            if (type == "IEND") break;
            pos += 12 + len;
        }
        return null;
    }

    private static byte[] Zlib(byte[] data)
    {
        using var input = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > MaxInflated) throw new FormatException("the embedded diagram is too large.");
        }
        return output.ToArray();
    }
}
