using System.Text.Json;
using System.Text.Json.Nodes;
using Meridian.Core.Language;

namespace Meridian.Core.Model;

/// <summary>A drawing layer of a diagram. Shapes and paths belong to one layer; hidden layers are not drawn or exported, locked ones can't be picked.</summary>
public sealed record DiagramLayer(string Id, string Name, bool Visible = true, bool Locked = false, int Order = 0);

/// <summary>
/// Diagram extras: embedded images, hyperlinks on shapes and drawing layers.
/// <para>
/// Everything is stored in existing replicated, undoable and persisted slots, so it needs no new file format:
/// an image is a free <see cref="ElementKind.Shape"/> with subtype <c>image</c> whose <c>image</c> field holds a data URI;
/// a link is the <c>link</c> style of a shape; layer definitions are <c>layer:&lt;id&gt;</c> fields on the diagram
/// (one field per layer, so collaborators editing different layers don't overwrite each other) and membership is
/// the <c>layer</c> style of a shape or path.
/// </para>
/// </summary>
public static class DiagramExtras
{
    public const string ImageSubtype = "image";
    public const string ImageField = "image";
    public const string LinkStyle = "link";
    public const string LayerStyle = "layer";
    public const string LayerFieldPrefix = "layer:";
    public const string DefaultLayerId = "default";
    public const string InternalLinkPrefix = "element:";

    /// <summary>Largest embedded image (decoded bytes). The browser downscales bigger pictures before inserting them.</summary>
    public const int MaxImageBytes = 2 * 1024 * 1024;

    /// <summary>Upper bound for a data URI holding <see cref="MaxImageBytes"/> (base64 plus a short header).</summary>
    public const int MaxImageDataUriLength = (MaxImageBytes + 2) / 3 * 4 + 64;

    private static readonly string[] ImageMimes = ["image/png", "image/jpeg", "image/gif", "image/webp", "image/svg+xml"];

    // ================================================================ images

    public static bool IsImage(Element e) => e.Kind == ElementKind.Shape && e.Get(FieldKeys.Subtype) == ImageSubtype;

    public static bool IsImage(DiagramNode n) => IsImage(n.Element);

    /// <summary>True for a base64 data URI of a raster or SVG picture within the size cap (nothing that could carry markup into an attribute).</summary>
    public static bool IsSafeImageDataUri(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > MaxImageDataUriLength || !s.StartsWith("data:", StringComparison.Ordinal)) return false;
        var comma = s.IndexOf(',');
        if (comma < 0) return false;
        var header = s[5..comma];
        if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return false;
        var mime = header[..^7].ToLowerInvariant();
        if (!ImageMimes.Contains(mime)) return false;
        for (var i = comma + 1; i < s.Length; i++)
        {
            var c = s[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')) return false;
        }
        return s.Length > comma + 1;
    }

    /// <summary>Splits a data URI into its media type and bytes, or null when it isn't a safe image.</summary>
    public static (string Mime, byte[] Bytes)? DecodeImage(string? dataUri)
    {
        if (!IsSafeImageDataUri(dataUri)) return null;
        var comma = dataUri!.IndexOf(',');
        try { return (dataUri[5..(comma - 7)].ToLowerInvariant(), Convert.FromBase64String(dataUri[(comma + 1)..])); }
        catch (FormatException) { return null; }
    }

    /// <summary>Size for a newly inserted picture: its natural size, scaled down to fit <paramref name="max"/> on the long side.</summary>
    public static (double W, double H) FitImageSize(double naturalW, double naturalH, double max = 320)
    {
        if (naturalW <= 0 || naturalH <= 0) return (160, 120);
        var k = Math.Min(1, max / Math.Max(naturalW, naturalH));
        return (Math.Max(20, Math.Round(naturalW * k)), Math.Max(20, Math.Round(naturalH * k)));
    }

    /// <summary>
    /// Creates an image shape owned by <paramref name="owner"/> and shows it on <paramref name="d"/>. One undo step.
    /// </summary>
    public static DiagramNode AddImage(this ModelRepository m, Diagram d, Element owner, string dataUri, Bounds bounds, string? name = null)
    {
        if (!IsSafeImageDataUri(dataUri))
            throw new InvalidOperationException(dataUri.Length > MaxImageDataUriLength ? "That image is larger than 2 MB." : "That file isn't a supported image (PNG, JPEG, GIF, WebP or SVG).");
        using (m.History.Transaction("Insert image"))
        {
            var e = m.Create(owner, ElementKind.Shape, name ?? "");
            m.Set(e, FieldKeys.Subtype, ImageSubtype);
            m.Set(e, ImageField, dataUri);
            return m.AddNode(d, e, bounds);
        }
    }

    // ================================================================ hyperlinks

    public static string? LinkOf(DiagramNode n) => n.Style.TryGetValue(LinkStyle, out var l) && l.Length > 0 ? l : null;

    /// <summary>
    /// Cleans up a link typed by a user: <c>element:&lt;id&gt;</c> for internal links, otherwise an absolute
    /// http(s) or mailto URL ("example.com" becomes "https://example.com"). Returns null for empty or unsafe input.
    /// </summary>
    public static string? NormalizeLink(string? raw)
    {
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.StartsWith(InternalLinkPrefix, StringComparison.Ordinal)) return s.Length > InternalLinkPrefix.Length && !s.Any(char.IsWhiteSpace) ? s : null;
        if (s.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return s.Length > 7 && !s.Any(char.IsWhiteSpace) ? s : null;
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && !string.IsNullOrEmpty(u.Host) ? u.AbsoluteUri : null;
    }

    public static bool IsExternalLink(string? link) => NormalizeLink(link) is { } l && !l.StartsWith(InternalLinkPrefix, StringComparison.Ordinal);

    /// <summary>The element id of an internal link, or null.</summary>
    public static string? InternalTarget(string? link) =>
        link != null && link.StartsWith(InternalLinkPrefix, StringComparison.Ordinal) && link.Length > InternalLinkPrefix.Length ? link[InternalLinkPrefix.Length..] : null;

    public static string InternalLink(Element target) => InternalLinkPrefix + target.Id;

    /// <summary>Sets (or clears) the hyperlink of shapes. One undo step.</summary>
    public static void SetLink(this ModelRepository m, Diagram d, IEnumerable<DiagramNode> nodes, string? link)
    {
        var normalized = NormalizeLink(link);
        if (!string.IsNullOrWhiteSpace(link) && normalized == null) throw new InvalidOperationException("Enter a web address (https://…) or pick an element.");
        using (m.History.Transaction(normalized == null ? "Remove link" : "Set link"))
            foreach (var n in nodes.ToList()) m.SetNodeStyle(d, n, LinkStyle, normalized);
    }

    /// <summary>Where the small link icon of a shape is drawn (bottom-right corner, inside the shape).</summary>
    public static Bounds LinkBadgeBounds(Bounds b)
    {
        const double s = 14;
        return new Bounds(b.Right - s - 3, b.Bottom - s - 3, s, s);
    }

    // ================================================================ layers

    /// <summary>Every layer of a diagram, bottom-most first. The default layer always exists.</summary>
    public static IReadOnlyList<DiagramLayer> Layers(Diagram d)
    {
        var list = new List<DiagramLayer>();
        foreach (var (key, value) in d.Fields)
        {
            if (!key.StartsWith(LayerFieldPrefix, StringComparison.Ordinal)) continue;
            var id = key[LayerFieldPrefix.Length..];
            if (id.Length == 0) continue;
            list.Add(ParseLayer(id, value));
        }
        if (!list.Any(l => l.Id == DefaultLayerId)) list.Add(new DiagramLayer(DefaultLayerId, "Default"));
        return list.OrderBy(l => l.Order).ThenBy(l => l.Id == DefaultLayerId ? 0 : 1).ThenBy(l => l.Id, StringComparer.Ordinal).ToList();
    }

    private static DiagramLayer ParseLayer(string id, string json)
    {
        try
        {
            if (JsonNode.Parse(json) is JsonObject o)
                return new DiagramLayer(id,
                    (string?)o["name"] is { Length: > 0 } n ? n : id == DefaultLayerId ? "Default" : "Layer",
                    (bool?)o["visible"] ?? true, (bool?)o["locked"] ?? false, (int?)o["order"] ?? 0);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
        return new DiagramLayer(id, id == DefaultLayerId ? "Default" : "Layer");
    }

    private static string SerializeLayer(DiagramLayer l) =>
        new JsonObject { ["name"] = l.Name, ["visible"] = l.Visible, ["locked"] = l.Locked, ["order"] = l.Order }.ToJsonString();

    private static bool HasLayers(Diagram d) => d.Fields.Keys.Any(k => k.StartsWith(LayerFieldPrefix, StringComparison.Ordinal));

    public static string LayerIdOf(DiagramNode n) => n.Style.TryGetValue(LayerStyle, out var l) && l.Length > 0 ? l : DefaultLayerId;
    public static string LayerIdOf(DiagramEdge e) => e.Style.TryGetValue(LayerStyle, out var l) && l.Length > 0 ? l : DefaultLayerId;

    /// <summary>Visibility / lock state of a diagram's layers, computed once for a batch of queries.</summary>
    public sealed class LayerState
    {
        private readonly Diagram _d;
        private readonly Dictionary<string, DiagramLayer> _layers;
        private readonly DiagramLayer _default;

        internal LayerState(Diagram d)
        {
            _d = d;
            var all = Layers(d);
            _layers = all.ToDictionary(l => l.Id, StringComparer.Ordinal);
            _default = _layers[DefaultLayerId];
            Ordered = all;
            Trivial = all.All(l => l.Visible && !l.Locked) && all.Count == 1;
        }

        public IReadOnlyList<DiagramLayer> Ordered { get; }

        /// <summary>Only the default layer, visible and unlocked: nothing to filter or reorder.</summary>
        public bool Trivial { get; }

        /// <summary>The layer a shape is on; shapes naming a deleted layer fall back to the default one.</summary>
        public DiagramLayer Of(DiagramNode n) => _layers.TryGetValue(LayerIdOf(n), out var l) ? l : _default;
        public DiagramLayer Of(DiagramEdge e) => _layers.TryGetValue(LayerIdOf(e), out var l) ? l : _default;

        public int OrderOf(DiagramNode n) => Ordered.ToList().IndexOf(Of(n));

        /// <summary>Hidden when its layer, or the layer of a shape containing it, is hidden.</summary>
        public bool IsHidden(DiagramNode n)
        {
            var depth = 0;
            for (var x = n; x != null && depth++ < 64; x = _d.ParentOf(x))
                if (!Of(x).Visible) return true;
            return false;
        }

        public bool IsLocked(DiagramNode n)
        {
            var depth = 0;
            for (var x = n; x != null && depth++ < 64; x = _d.ParentOf(x))
                if (Of(x).Locked) return true;
            return false;
        }

        public bool IsHidden(DiagramEdge e) => !Of(e).Visible || IsHidden(e.Source) || IsHidden(e.Target);
        public bool IsLocked(DiagramEdge e) => Of(e).Locked;
    }

    public static LayerState StateOf(Diagram d) => new(d);

    public static bool IsHidden(Diagram d, DiagramNode n) => HasLayers(d) && StateOf(d).IsHidden(n);
    public static bool IsLocked(Diagram d, DiagramNode n) => HasLayers(d) && StateOf(d).IsLocked(n);
    public static bool IsHidden(Diagram d, DiagramEdge e) => HasLayers(d) && StateOf(d).IsHidden(e);
    public static bool IsLocked(Diagram d, DiagramEdge e) => HasLayers(d) && StateOf(d).IsLocked(e);

    /// <summary>
    /// What gets drawn: <paramref name="d"/> itself when it has no layers, otherwise a detached copy holding only
    /// shapes and paths on visible layers, in layer order (bottom first). The copy shares the node and edge objects.
    /// </summary>
    public static Diagram VisibleView(Diagram d) => SubjectAreaView(LayerView(d));

    /// <summary>Database diagrams filtered to a subject area: only its tables (and other shapes), and paths between what is shown.</summary>
    private static Diagram SubjectAreaView(Diagram d)
    {
        if (d.DiagramType != DiagramType.Database || d.Get(DataModeling.DbFields.GroupFilter).Length == 0) return d;
        var view = new Diagram(d.DiagramType, d.Id) { Owner = d.Owner };
        foreach (var kv in d.Fields) view.SetRaw(kv.Key, kv.Value);
        view.NodeList.AddRange(d.Nodes.Where(n => !DataModeling.DbModel.FilteredOut(d, n)));
        var shown = view.NodeList.ToHashSet();
        view.EdgeList.AddRange(d.Edges.Where(e => shown.Contains(e.Source) && shown.Contains(e.Target)));
        return view;
    }

    private static Diagram LayerView(Diagram d)
    {
        if (!HasLayers(d)) return d;
        var st = StateOf(d);
        if (st.Trivial) return d;
        var order = st.Ordered.Select((l, i) => (l.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        var view = new Diagram(d.DiagramType, d.Id) { Owner = d.Owner };
        foreach (var kv in d.Fields) view.SetRaw(kv.Key, kv.Value);
        // Children are ordered with (just after) nothing special: a stable sort keeps the original order within a layer.
        view.NodeList.AddRange(d.Nodes.Where(n => !st.IsHidden(n)).OrderBy(n => order[st.Of(n).Id]));
        view.EdgeList.AddRange(d.Edges.Where(e => !st.IsHidden(e)).OrderBy(e => order[st.Of(e).Id]));
        return view;
    }

    /// <summary>Adds a layer on top of the others and returns it. Undoable and replicated (a diagram field).</summary>
    public static DiagramLayer AddLayer(this ModelRepository m, Diagram d, string? name = null)
    {
        var layers = Layers(d);
        var id = "L" + Guid.NewGuid().ToString("N")[..8];
        var layer = new DiagramLayer(id, string.IsNullOrWhiteSpace(name) ? $"Layer {layers.Count + 1}" : name.Trim(), Order: layers.Max(l => l.Order) + 1);
        m.Set(d, LayerFieldPrefix + id, SerializeLayer(layer));
        return layer;
    }

    /// <summary>Stores a changed layer (name, visibility, lock, order).</summary>
    public static void UpdateLayer(this ModelRepository m, Diagram d, DiagramLayer layer)
    {
        var name = string.IsNullOrWhiteSpace(layer.Name) ? "Layer" : layer.Name.Trim();
        m.Set(d, LayerFieldPrefix + layer.Id, SerializeLayer(layer with { Name = name }));
    }

    public static DiagramLayer? FindLayer(Diagram d, string id) => Layers(d).FirstOrDefault(l => l.Id == id);

    /// <summary>Deletes a layer; its shapes and paths move to the default layer. The default layer can't be deleted.</summary>
    public static void RemoveLayer(this ModelRepository m, Diagram d, string id)
    {
        if (id == DefaultLayerId) throw new InvalidOperationException("The default layer can't be deleted.");
        using (m.History.Transaction("Delete layer"))
        {
            foreach (var n in d.Nodes.Where(n => LayerIdOf(n) == id).ToList()) m.SetNodeStyle(d, n, LayerStyle, null);
            foreach (var e in d.Edges.Where(e => LayerIdOf(e) == id).ToList()) m.SetEdgeStyle(d, e, LayerStyle, null);
            m.Set(d, LayerFieldPrefix + id, null);
        }
    }

    /// <summary>Moves a layer up (towards the front, <paramref name="delta"/> &gt; 0) or down in the stacking order.</summary>
    public static void ReorderLayer(this ModelRepository m, Diagram d, string id, int delta)
    {
        var layers = Layers(d).ToList();
        var i = layers.FindIndex(l => l.Id == id);
        var j = i + Math.Sign(delta);
        if (i < 0 || j < 0 || j >= layers.Count) return;
        (layers[i], layers[j]) = (layers[j], layers[i]);
        using (m.History.Transaction("Reorder layers"))
            for (var k = 0; k < layers.Count; k++)
                if (layers[k].Order != k || !d.Fields.ContainsKey(LayerFieldPrefix + layers[k].Id))
                    m.Set(d, LayerFieldPrefix + layers[k].Id, SerializeLayer(layers[k] with { Order = k }));
    }

    /// <summary>Puts shapes (with what they contain) and paths on a layer. One undo step.</summary>
    public static void MoveToLayer(this ModelRepository m, Diagram d, IEnumerable<DiagramNode> nodes, IEnumerable<DiagramEdge> edges, string layerId)
    {
        var value = layerId == DefaultLayerId ? null : layerId;
        if (value != null && FindLayer(d, layerId) == null) throw new InvalidOperationException("That layer no longer exists.");
        using (m.History.Transaction("Move to layer"))
        {
            foreach (var n in nodes.SelectMany(n => d.DescendantsOf(n).Prepend(n)).Distinct().ToList()) m.SetNodeStyle(d, n, LayerStyle, value);
            foreach (var e in edges.ToList()) m.SetEdgeStyle(d, e, LayerStyle, value);
        }
    }
}
