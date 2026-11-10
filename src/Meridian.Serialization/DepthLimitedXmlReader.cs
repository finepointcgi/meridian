using System.Xml;

namespace Meridian.Serialization;

/// <summary>
/// An <see cref="XmlReader"/> that fails with an <see cref="XmlException"/> once elements are nested deeper than a limit.
/// Importers walk the XML tree recursively; without a limit a small crafted file overflows the stack, which ends the
/// whole server process (a stack overflow cannot be caught).
/// </summary>
public sealed class DepthLimitedXmlReader(XmlReader inner, int maxDepth) : XmlReader, IXmlLineInfo
{
    public const int DefaultMaxDepth = 256;

    public static XmlReader Create(Stream input, XmlReaderSettings settings, int maxDepth = DefaultMaxDepth) =>
        new DepthLimitedXmlReader(XmlReader.Create(input, settings), maxDepth);

    public override bool Read()
    {
        var ok = inner.Read();
        if (ok && inner.Depth > maxDepth)
            throw new XmlException($"Elements are nested more than {maxDepth} levels deep", null,
                inner is IXmlLineInfo li ? li.LineNumber : 0, inner is IXmlLineInfo lp ? lp.LinePosition : 0);
        return ok;
    }

    public override int AttributeCount => inner.AttributeCount;
    public override string BaseURI => inner.BaseURI;
    public override int Depth => inner.Depth;
    public override bool EOF => inner.EOF;
    public override bool IsEmptyElement => inner.IsEmptyElement;
    public override string LocalName => inner.LocalName;
    public override string Name => inner.Name;
    public override string NamespaceURI => inner.NamespaceURI;
    public override XmlNameTable NameTable => inner.NameTable;
    public override XmlNodeType NodeType => inner.NodeType;
    public override string Prefix => inner.Prefix;
    public override ReadState ReadState => inner.ReadState;
    public override string Value => inner.Value;
    public override bool HasValue => inner.HasValue;
    public override bool IsDefault => inner.IsDefault;
    public override string XmlLang => inner.XmlLang;
    public override XmlSpace XmlSpace => inner.XmlSpace;
    public override XmlReaderSettings? Settings => inner.Settings;
    public override string GetAttribute(int i) => inner.GetAttribute(i);
    public override string? GetAttribute(string name) => inner.GetAttribute(name);
    public override string? GetAttribute(string name, string? namespaceURI) => inner.GetAttribute(name, namespaceURI);
    public override string? LookupNamespace(string prefix) => inner.LookupNamespace(prefix);
    public override void MoveToAttribute(int i) => inner.MoveToAttribute(i);
    public override bool MoveToAttribute(string name) => inner.MoveToAttribute(name);
    public override bool MoveToAttribute(string name, string? ns) => inner.MoveToAttribute(name, ns);
    public override bool MoveToElement() => inner.MoveToElement();
    public override bool MoveToFirstAttribute() => inner.MoveToFirstAttribute();
    public override bool MoveToNextAttribute() => inner.MoveToNextAttribute();
    public override bool ReadAttributeValue() => inner.ReadAttributeValue();
    public override void ResolveEntity() => inner.ResolveEntity();
    public override void Close() => inner.Close();

    public bool HasLineInfo() => inner is IXmlLineInfo li && li.HasLineInfo();
    public int LineNumber => inner is IXmlLineInfo li ? li.LineNumber : 0;
    public int LinePosition => inner is IXmlLineInfo li ? li.LinePosition : 0;

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
