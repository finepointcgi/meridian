using System.Buffers;
using System.Text;
using System.Text.Json;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>
/// Native project format (*.meridian): a single, diff-friendly JSON document holding the
/// containment tree, relationships and diagram layout.
/// <para>
/// Written with <see cref="Utf8JsonWriter"/> straight from the model and read with <see cref="JsonDocument"/>, so no
/// intermediate JSON DOM is built (the output is byte-for-byte what the earlier JsonNode-based writer produced).
/// </para>
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".meridian";
    public const int FormatVersion = 1;

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    public static void Save(ModelRepository model, string path)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var w = new Utf8JsonWriter(fs, WriterOptions))
            Write(w, model);
        File.Move(tmp, path, overwrite: true);
        model.FilePath = path;
        model.History.MarkSaved();
    }

    public static ModelRepository Load(string path)
    {
        var m = Deserialize(File.ReadAllBytes(path));
        m.FilePath = path;
        return m;
    }

    public static string Serialize(ModelRepository model)
    {
        var buffer = new ArrayBufferWriter<byte>(Math.Max(1 << 16, model.Count * 400));
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
            Write(w, model);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The project as UTF-8 JSON bytes (no intermediate string).</summary>
    public static byte[] SerializeToUtf8Bytes(ModelRepository model)
    {
        var buffer = new ArrayBufferWriter<byte>(Math.Max(1 << 16, model.Count * 400));
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
            Write(w, model);
        return buffer.WrittenSpan.ToArray();
    }

    private static void Write(Utf8JsonWriter w, ModelRepository model)
    {
        w.WriteStartObject();
        w.WriteString("format", "meridian");
        w.WriteNumber("version", FormatVersion);
        w.WritePropertyName("root");
        Write(w, model.Root);
        w.WriteEndObject();
    }

    private static void Write(Utf8JsonWriter w, Element e)
    {
        w.WriteStartObject();
        w.WriteString("id", e.Id);
        w.WriteString("kind", e.Kind.ToString());
        if (e is Relationship r)
        {
            w.WriteString("source", r.Source.Id);
            w.WriteString("target", r.Target.Id);
        }
        if (e.Fields.Count > 0)
        {
            w.WriteStartObject("fields");
            foreach (var kv in e.Fields.OrderBy(k => k.Key, StringComparer.Ordinal)) w.WriteString(kv.Key, kv.Value);
            w.WriteEndObject();
        }
        if (e is Diagram d)
        {
            w.WriteString("diagramType", d.DiagramType.ToString());
            w.WriteStartArray("nodes");
            foreach (var n in d.Nodes)
            {
                w.WriteStartObject();
                w.WriteString("id", n.Id);
                w.WriteString("element", n.Element.Id);
                w.WriteNumber("x", Math.Round(n.Bounds.X, 2));
                w.WriteNumber("y", Math.Round(n.Bounds.Y, 2));
                w.WriteNumber("w", Math.Round(n.Bounds.Width, 2));
                w.WriteNumber("h", Math.Round(n.Bounds.Height, 2));
                if (n.ParentId is null) w.WriteNull("parent"); else w.WriteString("parent", n.ParentId);
                if (n.Style.Count == 0) w.WriteNull("style");
                else
                {
                    w.WriteStartObject("style");
                    foreach (var kv in n.Style.OrderBy(kv => kv.Key)) w.WriteString(kv.Key, kv.Value);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("edges");
            foreach (var x in d.Edges)
            {
                w.WriteStartObject();
                w.WriteString("id", x.Id);
                w.WriteString("relationship", x.Relationship.Id);
                w.WriteString("source", x.Source.Id);
                w.WriteString("target", x.Target.Id);
                if (x.Waypoints.Count > 0)
                {
                    w.WriteStartArray("points");
                    foreach (var p in x.Waypoints)
                    {
                        w.WriteStartArray();
                        w.WriteNumberValue(Math.Round(p.X, 2));
                        w.WriteNumberValue(Math.Round(p.Y, 2));
                        w.WriteEndArray();
                    }
                    w.WriteEndArray();
                }
                if (x.Style.Count > 0)
                {
                    w.WriteStartObject("style");
                    foreach (var kv in x.Style.OrderBy(kv => kv.Key)) w.WriteString(kv.Key, kv.Value);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        if (e.OwnedElements.Count > 0)
        {
            w.WriteStartArray("children");
            foreach (var c in e.OwnedElements) Write(w, c);
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    public static ModelRepository Deserialize(string json)
    {
        using var doc = JsonDocument.Parse(json, ReaderOptions);
        return Read(doc.RootElement);
    }

    public static ModelRepository Deserialize(ReadOnlyMemory<byte> utf8Json)
    {
        using var doc = JsonDocument.Parse(utf8Json, ReaderOptions);
        return Read(doc.RootElement);
    }

    private static readonly JsonDocumentOptions ReaderOptions = new() { MaxDepth = 4096 };

    private static JsonElement? Prop(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    private static string Str(JsonElement o, string name) =>
        Prop(o, name)?.GetString() ?? throw new FormatException($"Missing '{name}'");

    private static string? OptStr(JsonElement o, string name) => Prop(o, name)?.GetString();

    private static ModelRepository Read(JsonElement doc)
    {
        if (doc.ValueKind != JsonValueKind.Object) throw new FormatException("Not a JSON object");
        if (OptStr(doc, "format") != "meridian") throw new FormatException("Not a Meridian project file");
        var version = Prop(doc, "version")?.GetInt32() ?? 0;
        if (version > FormatVersion) throw new FormatException($"Project was written by a newer version (format {version})");
        if (Prop(doc, "root") is not { ValueKind: JsonValueKind.Object } rootDto) throw new FormatException("Missing root");

        // Pass 1: flatten DTOs and create non-relationship elements.
        var dtos = new List<(JsonElement dto, string id, ElementKind kind, string? ownerId)>();
        Flatten(rootDto, null, dtos);
        var byId = new Dictionary<string, Element>(dtos.Count, StringComparer.Ordinal);
        foreach (var (dto, id, kind, _) in dtos)
        {
            if (Core.Language.Metamodel.IsRelationship(kind)) continue;
            Element el = kind == ElementKind.Diagram
                ? new Diagram(Enum.Parse<DiagramType>(Str(dto, "diagramType")), id)
                : new Element(kind, id);
            ApplyFields(el, dto);
            byId.Add(id, el);
        }

        // Pass 2: relationships, now that both ends exist.
        foreach (var (dto, id, kind, _) in dtos)
        {
            if (!Core.Language.Metamodel.IsRelationship(kind)) continue;
            if (!byId.TryGetValue(Str(dto, "source"), out var s) || !byId.TryGetValue(Str(dto, "target"), out var t))
                throw new FormatException($"Relationship {id} has a dangling end");
            var r = new Relationship(kind, s, t, id);
            ApplyFields(r, dto);
            byId.Add(id, r);
        }

        // Pass 3: containment.
        Element? root = null;
        foreach (var (_, id, _, ownerId) in dtos)
        {
            var el = byId[id];
            if (ownerId == null) { root = el; continue; }
            var owner = byId[ownerId];
            el.Owner = owner;
            owner.Children.Add(el);
        }
        var model = new ModelRepository(root ?? throw new FormatException("Empty project"));

        // Pass 4: diagram content.
        foreach (var (dto, id, kind, _) in dtos)
        {
            if (kind != ElementKind.Diagram || byId[id] is not Diagram d) continue;
            var nodes = new Dictionary<string, DiagramNode>();
            if (Prop(dto, "nodes") is { ValueKind: JsonValueKind.Array } nodeArray)
                foreach (var n in nodeArray.EnumerateArray())
                {
                    if (n.ValueKind != JsonValueKind.Object) continue;
                    var elementId = Str(n, "element");
                    // A redacted view (package permissions) keeps hidden elements' shapes as nameless placeholders.
                    if (!byId.TryGetValue(elementId, out var el))
                    {
                        if (!elementId.StartsWith(Core.Permissions.ModelRedactor.PlaceholderPrefix, StringComparison.Ordinal)) continue;
                        el = Core.Permissions.ModelRedactor.Placeholder(elementId);
                    }
                    var node = new DiagramNode(el, new Bounds(Num(n, "x"), Num(n, "y"), Num(n, "w"), Num(n, "h")), OptStr(n, "id"));
                    ModelRepository.SetNodeParentForLoad(node, OptStr(n, "parent"));
                    if (Prop(n, "style") is { ValueKind: JsonValueKind.Object } st)
                        foreach (var kv in st.EnumerateObject()) node.Style[kv.Name] = kv.Value.GetString()!;
                    nodes[node.Id] = node;
                    model.AddNodeForLoad(d, node);
                }
            if (Prop(dto, "edges") is { ValueKind: JsonValueKind.Array } edgeArray)
                foreach (var x in edgeArray.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.Object) continue;
                    if (!byId.TryGetValue(Str(x, "relationship"), out var rel) || rel is not Relationship r) continue;
                    if (!nodes.TryGetValue(Str(x, "source"), out var s) || !nodes.TryGetValue(Str(x, "target"), out var t)) continue;
                    var edge = new DiagramEdge(r, s, t, OptStr(x, "id"));
                    if (Prop(x, "points") is { ValueKind: JsonValueKind.Array } pts)
                        ModelRepository.SetWaypointsForLoad(edge, pts.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Array)
                            .Select(p => new Pt(p[0].GetDouble(), p[1].GetDouble())));
                    if (Prop(x, "style") is { ValueKind: JsonValueKind.Object } es)
                        foreach (var kv in es.EnumerateObject()) ModelRepository.SetEdgeStyleForLoad(edge, kv.Name, kv.Value.GetString()!);
                    model.AddEdgeForLoad(d, edge);
                }
        }
        return model;
    }

    private static double Num(JsonElement o, string name) =>
        Prop(o, name)?.GetDouble() ?? throw new FormatException($"Missing '{name}'");

    private static void Flatten(JsonElement dto, string? ownerId, List<(JsonElement, string, ElementKind, string?)> into)
    {
        var id = Str(dto, "id");
        into.Add((dto, id, Enum.Parse<ElementKind>(Str(dto, "kind")), ownerId));
        if (Prop(dto, "children") is { ValueKind: JsonValueKind.Array } children)
            foreach (var c in children.EnumerateArray())
                if (c.ValueKind == JsonValueKind.Object) Flatten(c, id, into);
    }

    private static void ApplyFields(Element e, JsonElement dto)
    {
        if (Prop(dto, "fields") is not { ValueKind: JsonValueKind.Object } f) return;
        foreach (var kv in f.EnumerateObject())
            ModelRepository.SetFieldForLoad(e, kv.Name, kv.Value.ValueKind == JsonValueKind.Null ? null : kv.Value.GetString());
    }
}
