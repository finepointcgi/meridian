using System.Buffers;
using System.Text;
using System.Text.Json;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Permissions;

namespace Meridian.Serialization.History;

/// <summary>
/// One element of a <see cref="FlatModel"/>: its identity, owner, relationship ends, name, and a hash of every field (the
/// values themselves only when they were asked for). Diagrams also carry a hash of their layout and the elements they show.
/// </summary>
public sealed class FlatElement
{
    internal FlatElement() { }

    public string Id { get; internal set; } = "";
    public string Kind { get; internal set; } = "";
    public FlatElement? Owner { get; internal set; }
    public string? OwnerId => Owner?.Id;
    public string? SourceId { get; internal set; }
    public string? TargetId { get; internal set; }
    public string Name { get; internal set; } = "";
    public string RequirementId { get; internal set; } = "";
    public bool IsRelationship => SourceId != null;

    /// <summary>Field keys in ordinal order, with a hash of each value at the same index.</summary>
    internal string[] Keys = [];
    internal ulong[] Hashes = [];
    internal string?[]? Values;

    public IReadOnlyList<string> FieldKeys => Keys;

    /// <summary>True when the field values were kept (<see cref="FlatModel.Parse(string, IReadOnlySet{string}?)"/>).</summary>
    public bool HasValues => Values != null;

    /// <summary>A field's value (null when absent or when values were not kept).</summary>
    public string? Get(string key)
    {
        if (Values is null) return null;
        var i = Array.BinarySearch(Keys, key, StringComparer.Ordinal);
        return i >= 0 ? Values[i] : null;
    }

    internal ulong? HashOf(string key)
    {
        var i = Array.BinarySearch(Keys, key, StringComparer.Ordinal);
        return i >= 0 ? Hashes[i] : null;
    }

    public string? DiagramType { get; internal set; }
    public bool IsDiagram => DiagramType != null;
    internal ulong NodesHash = FlatModel.Hash.Seed, EdgesHash = FlatModel.Hash.Seed;

    /// <summary>A hash of the diagram's shapes and paths (positions, nesting, waypoints, styles), in order.</summary>
    public ulong LayoutHash => FlatModel.Hash.Combine(NodesHash, EdgesHash);
    public int NodeCount { get; internal set; }
    public int EdgeCount { get; internal set; }

    /// <summary>The elements a diagram shows (null for other elements; placeholders of hidden elements are left out).</summary>
    public HashSet<string>? Shown { get; internal set; }

    public string DisplayName => Name.Length > 0 ? Name : $"<{KindDisplayName(Kind)}>";

    internal static string KindDisplayName(string kind) => Enum.TryParse<ElementKind>(kind, out var k) ? Metamodel.DisplayName(k) : kind;

    internal void SetFields(List<(string Key, ulong Hash, string? Value)> fields, bool keepValues)
    {
        if (fields.Count == 0) { Keys = []; Hashes = []; Values = keepValues ? [] : null; return; }
        // PostgreSQL's jsonb orders keys by length first: sort, so both stores compare alike.
        fields.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        Keys = new string[fields.Count];
        Hashes = new ulong[fields.Count];
        Values = keepValues ? new string?[fields.Count] : null;
        for (var i = 0; i < fields.Count; i++)
        {
            Keys[i] = fields[i].Key;
            Hashes[i] = fields[i].Hash;
            if (Values != null) Values[i] = fields[i].Value;
        }
    }
}

/// <summary>
/// A flat, read-only view of a stored version for element history (docs-site version control, "Element history"): every
/// element by id with its owner, ends and field hashes, read straight from the project JSON with a forward-only reader. It is
/// several times cheaper than deserializing a <see cref="ModelRepository"/> (no object graph, no indexes, no diagram geometry)
/// and comparing two of them gives the per-element changes of a commit (<see cref="ElementChangeComputer"/>). Field values are
/// kept only for the elements asked for (a diff being shown); names are always kept, to label rows and show references.
/// </summary>
public sealed class FlatModel
{
    private readonly Dictionary<string, FlatElement> _elements;

    private FlatModel(Dictionary<string, FlatElement> elements, FlatElement? root)
    {
        _elements = elements;
        Root = root;
    }

    public FlatElement? Root { get; }
    public int Count => _elements.Count;
    public IEnumerable<FlatElement> Elements => _elements.Values;
    public FlatElement? Find(string? id) => id != null && _elements.TryGetValue(id, out var e) ? e : null;
    public bool Contains(string id) => _elements.ContainsKey(id);

    /// <summary>The name shown for an element id, or null when the id is not an element of this model.</summary>
    public string? NameOf(string id) => _elements.TryGetValue(id, out var e) ? e.DisplayName : null;

    /// <summary>The label of an element in history lists (a relationship by its kind and ends, a requirement with its id).</summary>
    public string Label(FlatElement e)
    {
        if (e.IsRelationship)
        {
            var keyword = Enum.TryParse<ElementKind>(e.Kind, out var k) ? Metamodel.Style(k).Keyword ?? Metamodel.DisplayName(k) : e.Kind;
            return $"«{keyword}» {NameOf(e.SourceId!) ?? "?"} → {NameOf(e.TargetId!) ?? "?"}";
        }
        if (e.Kind == nameof(ElementKind.Requirement) && e.RequirementId.Length > 0) return $"{e.RequirementId} {e.Name}".Trim();
        return e.DisplayName;
    }

    // ================================================================= reading the project JSON

    private static readonly JsonReaderOptions ReaderOptions = new() { MaxDepth = 4096, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>Reads a project document. Field values are kept for <paramref name="keepValuesFor"/> (null: none).</summary>
    public static FlatModel Parse(string json, IReadOnlySet<string>? keepValuesFor = null)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(json));
        try
        {
            var n = Encoding.UTF8.GetBytes(json, bytes);
            return Parse(bytes.AsSpan(0, n), keepValuesFor);
        }
        finally { ArrayPool<byte>.Shared.Return(bytes); }
    }

    /// <summary>Reads a project document from UTF-8.</summary>
    public static FlatModel Parse(ReadOnlySpan<byte> utf8, IReadOnlySet<string>? keepValuesFor = null)
    {
        var p = new Parser(keepValuesFor);
        var r = new Utf8JsonReader(utf8, ReaderOptions);
        if (!r.Read() || r.TokenType != JsonTokenType.StartObject) throw new FormatException("Not a Meridian project document.");
        FlatElement? root = null;
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("root"u8))
            {
                r.Read();
                root = p.ReadElement(ref r, null);
            }
            else
            {
                r.Read();
                r.Skip();   // format, version, libraries (never part of a stored version)
            }
        }
        return new FlatModel(p.Elements, root);
    }

    private sealed class Parser(IReadOnlySet<string>? keep)
    {
        public readonly Dictionary<string, FlatElement> Elements = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _pool = new(StringComparer.Ordinal);
        private readonly List<(string, ulong, string?)> _scratch = new();   // one element's fields at a time (copied into arrays)

        public FlatElement ReadElement(ref Utf8JsonReader r, FlatElement? owner)
        {
            if (r.TokenType != JsonTokenType.StartObject) throw new FormatException("An element must be a JSON object.");
            var e = new FlatElement { Owner = owner };
            var hasFields = false;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                if (r.ValueTextEquals("id"u8)) { r.Read(); e.Id = r.GetString() ?? ""; }
                else if (r.ValueTextEquals("kind"u8)) { r.Read(); e.Kind = Pooled(ref r); }
                else if (r.ValueTextEquals("source"u8)) { r.Read(); e.SourceId = r.GetString(); }
                else if (r.ValueTextEquals("target"u8)) { r.Read(); e.TargetId = r.GetString(); }
                else if (r.ValueTextEquals("diagramType"u8)) { r.Read(); e.DiagramType = Pooled(ref r); }
                else if (r.ValueTextEquals("fields"u8))
                {
                    r.Read();
                    // "id" comes first in both stores' documents; an unknown id keeps the values (correct, just a little more memory).
                    var keepValues = keep != null && (e.Id.Length == 0 || keep.Contains(e.Id));
                    e.SetFields(ReadFields(ref r, e, keepValues), keepValues);   // before the children reuse the scratch list
                    hasFields = true;
                }
                else if (r.ValueTextEquals("nodes"u8)) { r.Read(); ReadNodes(ref r, e); }
                else if (r.ValueTextEquals("edges"u8)) { r.Read(); ReadEdges(ref r, e); }
                else if (r.ValueTextEquals("children"u8))
                {
                    r.Read();
                    if (r.TokenType == JsonTokenType.StartArray)
                        while (r.Read() && r.TokenType != JsonTokenType.EndArray) ReadElement(ref r, e);
                    else r.Skip();
                }
                else { r.Read(); r.Skip(); }
            }
            if (e.DiagramType != null) e.Shown ??= new HashSet<string>(StringComparer.Ordinal);
            if (!hasFields)
            {
                _scratch.Clear();
                e.SetFields(_scratch, keep != null && keep.Contains(e.Id));
            }
            else if (e.Values != null && keep != null && !keep.Contains(e.Id)) e.Values = null;
            if (e.Id.Length > 0) Elements[e.Id] = e;
            return e;
        }

        private List<(string, ulong, string?)> ReadFields(ref Utf8JsonReader r, FlatElement e, bool keepValues)
        {
            var list = _scratch;
            list.Clear();
            if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); return list; }
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                var key = Pooled(ref r);
                r.Read();
                if (r.TokenType != JsonTokenType.String) { r.Skip(); continue; }
                var hash = Hash.Of(ref r);
                string? value = null;
                if (key == FieldKeys.Name) e.Name = value = r.GetString() ?? "";
                else if (key == FieldKeys.RequirementId) e.RequirementId = value = r.GetString() ?? "";
                if (keepValues) value ??= r.GetString();
                list.Add((key, hash, keepValues ? value : null));
            }
            return list;
        }

        private void ReadNodes(ref Utf8JsonReader r, FlatElement d)
        {
            if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); return; }
            var layout = Hash.Seed;
            d.Shown ??= new HashSet<string>(StringComparer.Ordinal);
            while (r.Read() && r.TokenType == JsonTokenType.StartObject)
            {
                string? element = null, parent = null;
                double x = 0, y = 0, w = 0, h = 0;
                ulong style = 0;
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    if (r.ValueTextEquals("element"u8)) { r.Read(); element = r.GetString(); }
                    else if (r.ValueTextEquals("parent"u8)) { r.Read(); parent = r.TokenType == JsonTokenType.String ? r.GetString() : null; }
                    else if (r.ValueTextEquals("x"u8)) { r.Read(); x = r.GetDouble(); }
                    else if (r.ValueTextEquals("y"u8)) { r.Read(); y = r.GetDouble(); }
                    else if (r.ValueTextEquals("w"u8)) { r.Read(); w = r.GetDouble(); }
                    else if (r.ValueTextEquals("h"u8)) { r.Read(); h = r.GetDouble(); }
                    else if (r.ValueTextEquals("style"u8)) { r.Read(); style = ReadStyle(ref r); }
                    else { r.Read(); r.Skip(); }
                }
                layout = Hash.Combine(layout, Hash.Node(element, x, y, w, h, parent, style));
                d.NodeCount++;
                if (element != null && !element.StartsWith(ModelRedactor.PlaceholderPrefix, StringComparison.Ordinal)) d.Shown.Add(element);
            }
            d.NodesHash = layout;
        }

        private void ReadEdges(ref Utf8JsonReader r, FlatElement d)
        {
            if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); return; }
            var edges = Hash.Seed;
            var points = new List<double>();
            while (r.Read() && r.TokenType == JsonTokenType.StartObject)
            {
                string? relationship = null;
                ulong style = 0;
                points.Clear();
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    if (r.ValueTextEquals("relationship"u8)) { r.Read(); relationship = r.GetString(); }
                    else if (r.ValueTextEquals("style"u8)) { r.Read(); style = ReadStyle(ref r); }
                    else if (r.ValueTextEquals("points"u8))
                    {
                        r.Read();
                        if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); continue; }
                        while (r.Read() && r.TokenType == JsonTokenType.StartArray)
                            while (r.Read() && r.TokenType == JsonTokenType.Number) points.Add(r.GetDouble());
                    }
                    else { r.Read(); r.Skip(); }
                }
                edges = Hash.Combine(edges, Hash.Edge(relationship, points, style));
                d.EdgeCount++;
            }
            // Nodes and edges are hashed separately, so the documents' key order (jsonb puts edges first) doesn't matter.
            d.EdgesHash = edges;
        }

        private static ulong ReadStyle(ref Utf8JsonReader r)
        {
            if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); return 0; }
            ulong sum = 0;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                var k = Hash.Of(ref r);
                r.Read();
                var v = r.TokenType == JsonTokenType.String ? Hash.Of(ref r) : 0;
                if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) r.Skip();
                sum += Hash.Combine(k, v);   // order-independent
            }
            return sum;
        }

        private string Pooled(ref Utf8JsonReader r)
        {
            var s = r.GetString() ?? "";
            if (_pool.TryGetValue(s, out var pooled)) return pooled;
            if (_pool.Count < 4096) _pool[s] = s;
            return s;
        }
    }

    // ================================================================= flattening a model in memory

    /// <summary>Flattens a model (e.g. a stored version redacted for a viewer). Field values are kept for <paramref name="keepValuesFor"/>.</summary>
    public static FlatModel From(ModelRepository model, IReadOnlySet<string>? keepValuesFor = null)
    {
        var map = new Dictionary<string, FlatElement>(model.Count, StringComparer.Ordinal);
        var root = Flatten(model.Root, null, map, keepValuesFor, model.HasLibraries ? model.IsLibraryRoot : null);
        return new FlatModel(map, root);
    }

    /// <summary>A model of one live element (and the names of everything it refers to), for comparing a version with the current model.</summary>
    public static FlatModel Of(ModelRepository model, Element e)
    {
        var map = new Dictionary<string, FlatElement>(StringComparer.Ordinal);
        var owner = e.Owner is { } o ? Shallow(o, null, keep: false) : null;
        var flat = Shallow(e, owner, keep: true);
        if (owner != null) map[owner.Id] = owner;
        map[flat.Id] = flat;
        foreach (var id in ReferencedIds(flat))
            if (!map.ContainsKey(id) && model.Find(id) is { } x) map[id] = Shallow(x, null, keep: false);
        return new FlatModel(map, null);
    }

    /// <summary>Ids a flattened element refers to (relationship ends, owner, and field values that look like element ids).</summary>
    public static IEnumerable<string> ReferencedIds(FlatElement e)
    {
        if (e.SourceId != null) yield return e.SourceId;
        if (e.TargetId != null) yield return e.TargetId;
        if (e.Values is null) yield break;
        foreach (var v in e.Values)
            if (v != null && v.StartsWith('_'))
                foreach (var part in v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    if (part.StartsWith('_')) yield return part;
    }

    private static FlatElement? Flatten(Element e, FlatElement? owner, Dictionary<string, FlatElement> map, IReadOnlySet<string>? keep, Func<Element, bool>? skip)
    {
        // Iterative: models can nest deeply.
        var stack = new Stack<(Element E, FlatElement? Owner)>();
        stack.Push((e, owner));
        FlatElement? root = null;
        while (stack.Count > 0)
        {
            var (x, o) = stack.Pop();
            var flat = Shallow(x, o, keep != null && keep.Contains(x.Id));
            map[flat.Id] = flat;
            root ??= flat;
            for (var i = x.OwnedElements.Count - 1; i >= 0; i--)
                if (skip is null || !skip(x.OwnedElements[i])) stack.Push((x.OwnedElements[i], flat));
        }
        return root;
    }

    private static FlatElement Shallow(Element e, FlatElement? owner, bool keep)
    {
        var flat = new FlatElement
        {
            Id = e.Id, Kind = e.Kind.ToString(), Owner = owner, Name = e.Name, RequirementId = e.Get(FieldKeys.RequirementId),
            SourceId = (e as Relationship)?.Source.Id, TargetId = (e as Relationship)?.Target.Id,
        };
        var fields = new List<(string, ulong, string?)>(e.Fields.Count);
        foreach (var (k, v) in e.Fields) fields.Add((k, Hash.Of(v), keep ? v : null));
        flat.SetFields(fields, keep);
        if (owner is null && e.Owner is { } o) flat.Owner = new FlatElement { Id = o.Id, Kind = o.Kind.ToString(), Name = o.Name };
        if (e is Diagram d)
        {
            flat.DiagramType = d.DiagramType.ToString();
            flat.Shown = new HashSet<string>(StringComparer.Ordinal);
            var layout = Hash.Seed;
            foreach (var n in d.Nodes)
            {
                layout = Hash.Combine(layout, Hash.Node(n.Element.Id, Math.Round(n.Bounds.X, 2), Math.Round(n.Bounds.Y, 2), Math.Round(n.Bounds.Width, 2),
                                                        Math.Round(n.Bounds.Height, 2), n.ParentId, StyleHash(n.Style)));
                if (!ModelRedactor.IsPlaceholder(n.Element)) flat.Shown.Add(n.Element.Id);
            }
            var edges = Hash.Seed;
            foreach (var x in d.Edges)
                edges = Hash.Combine(edges, Hash.Edge(x.Relationship.Id, x.Waypoints.SelectMany(p => new[] { Math.Round(p.X, 2), Math.Round(p.Y, 2) }).ToList(), StyleHash(x.Style)));
            flat.NodesHash = layout;
            flat.EdgesHash = edges;
            flat.NodeCount = d.Nodes.Count;
            flat.EdgeCount = d.Edges.Count;
        }
        return flat;
    }

    private static ulong StyleHash(Dictionary<string, string> style)
    {
        ulong sum = 0;
        foreach (var (k, v) in style) sum += Hash.Combine(Hash.Of(k), Hash.Of(v));
        return sum;
    }

    // ================================================================= hashing (identical for the reader and the in-memory path)

    internal static class Hash
    {
        public const ulong Seed = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        public static ulong Of(ReadOnlySpan<byte> utf8)
        {
            var h = Seed;
            foreach (var b in utf8) h = (h ^ b) * Prime;
            return Mix(h ^ (ulong)utf8.Length);
        }

        public static ulong Of(string? s)
        {
            if (s is null) return 0;
            var max = Encoding.UTF8.GetMaxByteCount(s.Length);
            if (max <= 512)
            {
                Span<byte> buf = stackalloc byte[max];
                return Of(buf[..Encoding.UTF8.GetBytes(s, buf)]);
            }
            var rented = ArrayPool<byte>.Shared.Rent(max);
            try { return Of(rented.AsSpan(0, Encoding.UTF8.GetBytes(s, rented))); }
            finally { ArrayPool<byte>.Shared.Return(rented); }
        }

        /// <summary>The hash of the current string token's unescaped UTF-8.</summary>
        public static ulong Of(ref Utf8JsonReader r)
        {
            if (!r.ValueIsEscaped) return Of(r.ValueSpan);   // always a single span: the reader runs over one buffer
            var rented = ArrayPool<byte>.Shared.Rent(Math.Max(r.ValueSpan.Length, 16));
            try { return Of(rented.AsSpan(0, r.CopyString(rented))); }
            finally { ArrayPool<byte>.Shared.Return(rented); }
        }

        public static ulong Combine(ulong a, ulong b) => Mix(a ^ (b + 0x9E3779B97F4A7C15UL + (a << 6) + (a >> 2)));

        public static ulong Number(double d) => Mix((ulong)BitConverter.DoubleToInt64Bits(d == 0 ? 0 : d));   // -0 == 0

        public static ulong Node(string? element, double x, double y, double w, double h, string? parent, ulong style)
        {
            var n = Combine(Of(element), Number(x));
            n = Combine(n, Number(y));
            n = Combine(n, Number(w));
            n = Combine(n, Number(h));
            n = Combine(n, parent is null ? 1 : Of(parent));
            return Combine(n, style);
        }

        public static ulong Edge(string? relationship, List<double> points, ulong style)
        {
            var n = Of(relationship);
            foreach (var p in points) n = Combine(n, Number(p));
            return Combine(n, style);
        }

        private static ulong Mix(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
