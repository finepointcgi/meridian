using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>
/// Imports MagicDraw / Cameo Systems Modeler projects (.mdzip, and the plain-XMI export .mdxml) including diagrams.
/// <para>
/// Format (MagicDraw 17.0.1 and later): an .mdzip is a zip of resources. The UML model is XMI (UML 2.5 + SysML 1.x profile
/// applications as siblings of <c>uml:Model</c>) in entries ending with <c>com.nomagic.magicdraw.uml_model.model</c> and
/// <c>…shared_model</c>. Each diagram is a <c>uml:Diagram</c> under <c>xmi:Extension/modelExtension/ownedDiagram</c> of its
/// owner; its <c>DiagramRepresentationObject</c> names the diagram type, and its symbols (<c>mdElement</c> views with
/// <c>elementID</c>, <c>geometry</c>, <c>mdOwnedViews</c>, <c>linkFirstEndID</c>/<c>linkSecondEndID</c>) are either inline
/// (.mdxml) or in the zip entry named by <c>binaryObject/@streamContentID</c>.
/// </para>
/// The importer is defensive: anything it does not understand is skipped and reported, never fatal. The model XMI is
/// streamed: packages are read incrementally and only one non-package element subtree is materialised at a time.
/// </summary>
public static partial class MagicDrawImporter
{
    public static readonly IReadOnlyList<string> Extensions = [".mdzip", ".mdxml"];

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Custom field recording a property's UML aggregation (composite / shared / none) for blocks' part vs reference properties.</summary>
    public const string AggregationField = "aggregation";

    // ======================================================================= entry points

    public static (ModelRepository Model, MagicDrawImportReport Report) Import(string path)
    {
        using var fs = File.OpenRead(path);
        return Import(fs, Path.GetFileName(path));
    }

    /// <summary>Reads a project from a stream; <paramref name="fileName"/> decides between .mdzip and .mdxml (content sniffing otherwise).</summary>
    public static (ModelRepository Model, MagicDrawImportReport Report) Import(Stream stream, string fileName)
    {
        try
        {
            if (!stream.CanSeek)
            {
                // Zip needs random access: spool to a private temp file.
                var tmp = Path.Combine(Directory.CreateTempSubdirectory("meridian-md-").FullName, "project.bin");
                try
                {
                    using (var f = File.Create(tmp)) stream.CopyTo(f);
                    using var fs = File.OpenRead(tmp);
                    return ImportSeekable(fs, fileName);
                }
                finally { try { Directory.Delete(Path.GetDirectoryName(tmp)!, true); } catch (IOException) { } }
            }
            return ImportSeekable(stream, fileName);
        }
        catch (InvalidDataException ex) { throw new FormatException($"Not a readable MagicDraw project ({ex.Message})", ex); }
        catch (XmlException ex) { throw new FormatException($"The project's model XML is malformed ({ex.Message})", ex); }
    }

    private static (ModelRepository, MagicDrawImportReport) ImportSeekable(Stream s, string fileName)
    {
        var head = new byte[4];
        var read = s.Read(head, 0, 4);
        s.Position = 0;
        var isZip = read >= 2 && head[0] == (byte)'P' && head[1] == (byte)'K';
        if (isZip)
        {
            using var zip = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true);
            return FromZip(zip);
        }
        if (Path.GetExtension(fileName).Equals(".mdzip", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("This .mdzip is not a zip archive.");
        var source = new ProjectSource([() => new NonClosingStream(Rewind(s))], _ => null, []);
        return new Builder(source).Run();
    }

    private static Stream Rewind(Stream s) { s.Position = 0; return s; }

    private static (ModelRepository, MagicDrawImportReport) FromZip(ZipArchive zip)
    {
        var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        var models = entries.Where(e => e.FullName.EndsWith("uml_model.model", StringComparison.OrdinalIgnoreCase)).ToList();
        models.AddRange(entries.Where(e => e.FullName.EndsWith("uml_model.shared_model", StringComparison.OrdinalIgnoreCase)));
        if (models.Count == 0)
            // Older projects (and hand-made zips) hold one .mdxml / .xmi / .xml.
            models = entries.Where(e => Path.GetExtension(e.Name).ToLowerInvariant() is ".mdxml" or ".xmi" or ".xml" or ".uml")
                            .OrderBy(e => Path.GetExtension(e.Name).Equals(".mdxml", StringComparison.OrdinalIgnoreCase) ? 0 : 1).Take(1).ToList();
        if (models.Count == 0) throw new FormatException("No UML model found in the .mdzip (expected a com.nomagic.magicdraw.uml_model.model entry).");

        var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) { byName.TryAdd(e.FullName, e); byName.TryAdd(e.Name, e); }
        Stream? OpenBinary(string id)
        {
            if (byName.TryGetValue(id, out var e)) return e.Open();
            var hit = entries.FirstOrDefault(x => x.FullName.EndsWith(id, StringComparison.OrdinalIgnoreCase));
            return hit?.Open();
        }
        var other = entries.Where(e => !models.Contains(e)).Select(e => e.FullName).ToList();
        var source = new ProjectSource(models.Select(m => (Func<Stream>)m.Open).ToList(), OpenBinary, other);
        return new Builder(source).Run();
    }

    internal sealed record ProjectSource(IReadOnlyList<Func<Stream>> ModelParts, Func<string, Stream?> OpenBinary, IReadOnlyList<string> OtherEntries);

    // ======================================================================= helpers shared by the builder and diagrams

    internal static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = true,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = true,
    };

    internal static string? Attr(XElement x, string name) => x.Attribute(name)?.Value is { Length: > 0 } v ? v : null;

    private static bool IsXmiNs(XNamespace ns) => ns.NamespaceName.Contains("XMI", StringComparison.OrdinalIgnoreCase);

    internal static string? XmiId(XElement x) =>
        x.Attributes().FirstOrDefault(a => a.Name.LocalName == "id" && IsXmiNs(a.Name.Namespace))?.Value;

    internal static string? XmiType(XElement x)
    {
        var t = x.Attributes().FirstOrDefault(a => a.Name.LocalName == "type" && (IsXmiNs(a.Name.Namespace) || a.Name.NamespaceName.Contains("XMLSchema-instance")))?.Value;
        if (t == null) return null;
        var i = t.IndexOf(':');
        return i >= 0 ? t[(i + 1)..] : t;
    }

    /// <summary>The id a reference element (<c>&lt;x xmi:idref="…"/&gt;</c> or <c>&lt;x href="file#id"/&gt;</c>) points to.</summary>
    internal static string? IdRef(XElement r)
    {
        var idref = r.Attributes().FirstOrDefault(a => a.Name.LocalName == "idref")?.Value;
        if (!string.IsNullOrEmpty(idref)) return idref;
        var href = r.Attribute("href")?.Value;
        if (href == null) return null;
        var hash = href.LastIndexOf('#');
        return hash >= 0 ? href[(hash + 1)..] : href;
    }

    /// <summary>All ids a reference feature holds: a space-separated attribute and/or child reference elements.</summary>
    internal static List<string> Refs(XElement x, string name)
    {
        var list = new List<string>();
        if (x.Attribute(name)?.Value is { } a) list.AddRange(a.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var c in x.Elements().Where(c => c.Name.LocalName == name))
            if (IdRef(c) is { } id) list.Add(id);
        return list;
    }

    internal static string? Ref(XElement x, string name) => Refs(x, name).FirstOrDefault();

    internal static XElement? Child(XElement x, string localName) => x.Elements().FirstOrDefault(c => c.Name.LocalName == localName);

    internal static double Num(string s) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

    /// <summary>Comment bodies and requirement text may be HTML (MagicDraw's rich text): reduce to plain text.</summary>
    internal static string? PlainText(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (!s.Contains('<')) return s.Trim();
        var t = Regex.Replace(s, @"<\s*(br|/p|/div|/li|/h\d)\s*/?>", "\n", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"<head>.*?</head>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        t = Regex.Replace(t, "<[^>]+>", "");
        t = WebUtility.HtmlDecode(t);
        var lines = t.Split('\n').Select(l => Regex.Replace(l, @"[ \t\r]+", " ").Trim()).Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }

    /// <summary>Text of a ValueSpecification: literal value, opaque expression body, or name.</summary>
    internal static string? ValueText(XElement? v)
    {
        if (v == null) return null;
        if (Attr(v, "value") is { } val) return val;
        if (Child(v, "body") is { } b && b.Value.Length > 0) return b.Value;
        if (Attr(v, "body") is { } ba) return ba;
        if (Child(v, "specification") is { } spec) return ValueText(spec);
        if (Child(v, "expr") is { } expr) return ValueText(expr);
        var type = XmiType(v);
        if (type == "LiteralBoolean") return "false";
        if (type is "LiteralInteger" or "LiteralReal" or "LiteralUnlimitedNatural") return "0";
        if (type == "LiteralNull") return "null";
        return Attr(v, "name");
    }

    /// <summary>Wraps a stream that must stay open across several readers (plain .mdxml read twice).</summary>
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
