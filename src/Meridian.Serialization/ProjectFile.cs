using System.Buffers;
using System.Text;
using System.Text.Json;
using Meridian.Core.Libraries;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>
/// Native project format (*.meridian): a single, diff-friendly JSON document holding the
/// containment tree, relationships and diagram layout.
/// <para>
/// Written with <see cref="Utf8JsonWriter"/> straight from the model and read with <see cref="JsonDocument"/>, so no
/// intermediate JSON DOM is built (the output is byte-for-byte what the earlier JsonNode-based writer produced).
/// </para>
/// <para>
/// Used projects (libraries, docs/LIBRARIES.md) are never part of the file: their root packages are skipped when the model is
/// written, and references to their elements are written as plain ids. In memory and between the server and its tabs the model
/// travels with its libraries in an extra <c>"libraries"</c> section (<see cref="Serialize(ModelRepository, bool)"/>,
/// <see cref="WithLibraries"/>), which the reader mounts as read-only elements. References that don't resolve (a library
/// element removed by an update, or a library the reader can't see) load as <see cref="MissingReference"/> placeholders that
/// keep their ids, so saving again loses nothing.
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

    public static string Serialize(ModelRepository model) => Serialize(model, includeLibraries: false);

    /// <summary>The project as JSON; with <paramref name="includeLibraries"/>, its mounted libraries travel along (in-memory copies, tabs).</summary>
    public static string Serialize(ModelRepository model, bool includeLibraries)
    {
        var buffer = new ArrayBufferWriter<byte>(Math.Max(1 << 16, model.Count * 400));
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
            Write(w, model, includeLibraries);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>An independent in-memory copy of a model, libraries included (mounted read-only again).</summary>
    public static ModelRepository Copy(ModelRepository model) => Deserialize(Serialize(model, includeLibraries: true));

    /// <summary>One element and everything it owns, as the file writes it (a library's content; see <see cref="LibraryPayload"/>).</summary>
    public static string SerializeSubtree(Element e)
    {
        var buffer = new ArrayBufferWriter<byte>(1 << 14);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
            Write(w, e, null);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Adds a <c>"libraries"</c> section to a project document without re-writing the project's own JSON (the document must not
    /// carry one yet; a document that does is merged through the reader).
    /// </summary>
    public static string WithLibraries(string projectJson, IReadOnlyList<LibraryPayload> libraries)
    {
        if (libraries.Count == 0) return projectJson;
        if (projectJson.Contains("\"libraries\"", StringComparison.Ordinal))
            return Serialize(Deserialize(projectJson, libraries), includeLibraries: true);
        var buffer = new ArrayBufferWriter<byte>(1 << 14);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartArray();
            foreach (var l in libraries) WriteLibrary(w, l.Info, () => w.WriteRawValue(l.RootJson, skipInputValidation: true));
            w.WriteEndArray();
        }
        var end = projectJson.LastIndexOf('}');
        if (end < 0) throw new FormatException("Not a Meridian project file");
        return string.Concat(projectJson.AsSpan(0, end).TrimEnd(), ",\n  \"libraries\": ", Encoding.UTF8.GetString(buffer.WrittenSpan), "\n}");
    }

    /// <summary>The project as UTF-8 JSON bytes (no intermediate string).</summary>
    public static byte[] SerializeToUtf8Bytes(ModelRepository model)
    {
        var buffer = new ArrayBufferWriter<byte>(Math.Max(1 << 16, model.Count * 400));
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
            Write(w, model, includeLibraries: false);
        return buffer.WrittenSpan.ToArray();
    }

    private static void Write(Utf8JsonWriter w, ModelRepository model, bool includeLibraries = false)
    {
        // Library roots are skipped wherever they are mounted: the file never contains used projects' content.
        Func<Element, bool>? skip = model.HasLibraries ? model.IsLibraryRoot : null;
        w.WriteStartObject();
        w.WriteString("format", "meridian");
        w.WriteNumber("version", FormatVersion);
        w.WritePropertyName("root");
        Write(w, model.Root, skip);
        if (includeLibraries && model.HasLibraries)
        {
            w.WriteStartArray("libraries");
            foreach (var m in model.Libraries) WriteLibrary(w, m.Info, () => Write(w, m.Root, skip));
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private static void WriteLibrary(Utf8JsonWriter w, LibraryInfo i, Action writeRoot)
    {
        w.WriteStartObject();
        w.WriteString("usage", i.UsageId);
        w.WriteString("project", i.SourceProjectId);
        w.WriteString("projectName", i.SourceProjectName);
        w.WriteString("package", i.RootPackageId);
        w.WriteString("pinKind", LibraryPins.Code(i.PinKind));
        w.WriteString("pinValue", i.PinValue);
        w.WriteString("pinLabel", i.PinLabel);
        w.WriteString("mount", i.MountId);
        if (i.ViaUsageId is { } via) w.WriteString("via", via);
        if (i.Resolved is { } resolved) w.WriteString("resolved", resolved);
        w.WritePropertyName("root");
        writeRoot();
        w.WriteEndObject();
    }

    private static LibraryInfo ReadLibraryInfo(JsonElement o) => new(
        Str(o, "usage"), Guid.TryParse(OptStr(o, "project"), out var p) ? p : Guid.Empty, OptStr(o, "projectName") ?? "Library",
        Str(o, "package"), LibraryPins.Kind(OptStr(o, "pinKind")), OptStr(o, "pinValue") ?? "", OptStr(o, "pinLabel") ?? "",
        OptStr(o, "mount") ?? "", OptStr(o, "via"), OptStr(o, "resolved"));

    private static void Write(Utf8JsonWriter w, Element e, Func<Element, bool>? skip)
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
            foreach (var c in e.OwnedElements)
                if (skip is null || !skip(c)) Write(w, c, skip);
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    public static ModelRepository Deserialize(string json)
    {
        using var doc = JsonDocument.Parse(json, ReaderOptions);
        return Read(doc.RootElement, null);
    }

    /// <summary>Reads a project and mounts <paramref name="libraries"/> (after any the document carries) as read-only elements.</summary>
    public static ModelRepository Deserialize(string json, IReadOnlyList<LibraryPayload>? libraries)
    {
        using var doc = JsonDocument.Parse(json, ReaderOptions);
        return Read(doc.RootElement, libraries);
    }

    public static ModelRepository Deserialize(ReadOnlyMemory<byte> utf8Json)
    {
        using var doc = JsonDocument.Parse(utf8Json, ReaderOptions);
        return Read(doc.RootElement, null);
    }

    /// <summary>
    /// A library's content as a stand-alone, editable model (its root package below an empty model root), for comparing two pins
    /// of a library with <see cref="Core.Analysis.ModelDiff"/>.
    /// </summary>
    public static ModelRepository LibraryContent(LibraryPayload library) =>
        Deserialize("{\"format\":\"meridian\",\"version\":1,\"root\":{\"id\":\"_library_view_root\",\"kind\":\"Model\",\"fields\":{\"name\":"
                    + JsonSerializer.Serialize(library.Info.SourceProjectName) + "},\"children\":[" + library.RootJson + "]}}");

    private static readonly JsonDocumentOptions ReaderOptions = new() { MaxDepth = 4096 };

    private static JsonElement? Prop(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    private static string Str(JsonElement o, string name) =>
        Prop(o, name)?.GetString() ?? throw new FormatException($"Missing '{name}'");

    private static string? OptStr(JsonElement o, string name) => Prop(o, name)?.GetString();

    /// <summary>A library being read: its info, flattened elements, and the ids of its elements not loaded (already in the model).</summary>
    private sealed class LibraryRead(LibraryInfo info, List<(JsonElement dto, string id, ElementKind kind, string? ownerId)> dtos)
    {
        public LibraryInfo Info { get; } = info;
        public List<(JsonElement dto, string id, ElementKind kind, string? ownerId)> Dtos { get; } = dtos;
        public HashSet<string> Skipped { get; } = new(StringComparer.Ordinal);
        public Element? Root { get; set; }
    }

    private static ModelRepository Read(JsonElement doc, IReadOnlyList<LibraryPayload>? external)
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
        var pool = new StringPool();
        foreach (var (dto, id, kind, _) in dtos)
        {
            if (Core.Language.Metamodel.IsRelationship(kind)) continue;
            Element el = kind == ElementKind.Diagram
                ? new Diagram(Enum.Parse<DiagramType>(Str(dto, "diagramType")), id)
                : new Element(kind, id);
            ApplyFields(el, dto, pool);
            byId.Add(id, el);
        }

        // Libraries (used projects): the document's own section, then the ones passed in. Their elements are created next to
        // the project's so references resolve both ways; an id already loaded (a diamond, or a copy in the project) is skipped.
        var libraryDocs = new List<JsonDocument>();
        try
        {
            var libraries = new List<LibraryRead>();
            void AddLibrary(LibraryInfo info, JsonElement root)
            {
                if (root.ValueKind != JsonValueKind.Object) return;
                var list = new List<(JsonElement, string, ElementKind, string?)>();
                Flatten(root, null, list);
                libraries.Add(new LibraryRead(info, list));
            }
            if (Prop(doc, "libraries") is { ValueKind: JsonValueKind.Array } section)
                foreach (var l in section.EnumerateArray())
                    if (l.ValueKind == JsonValueKind.Object && Prop(l, "root") is { } r) AddLibrary(ReadLibraryInfo(l), r);
            foreach (var p in external ?? [])
            {
                var d = JsonDocument.Parse(p.RootJson, ReaderOptions);
                libraryDocs.Add(d);
                AddLibrary(p.Info, d.RootElement);
            }
            return Build(dtos, byId, libraries, pool);
        }
        finally
        {
            foreach (var d in libraryDocs) d.Dispose();
        }
    }

    private static ModelRepository Build(List<(JsonElement dto, string id, ElementKind kind, string? ownerId)> dtos, Dictionary<string, Element> byId,
                                         List<LibraryRead> libraries, StringPool pool)
    {
        var library = new Dictionary<string, Element>(StringComparer.Ordinal);
        foreach (var lib in libraries)
            foreach (var (dto, id, kind, ownerId) in lib.Dtos)
            {
                if (Core.Language.Metamodel.IsRelationship(kind)) continue;
                if ((ownerId != null && lib.Skipped.Contains(ownerId)) || byId.ContainsKey(id) || library.ContainsKey(id)) { lib.Skipped.Add(id); continue; }
                Element el = kind == ElementKind.Diagram
                    ? new Diagram(Enum.TryParse<DiagramType>(OptStr(dto, "diagramType"), out var dt) ? dt : DiagramType.Package, id)
                    : new Element(kind, id);
                ApplyFields(el, dto, pool);
                library.Add(id, el);
                if (ownerId == null) lib.Root = el;
            }

        // References that don't resolve become placeholders that keep the id (a removed or unavailable library element).
        var missing = new Dictionary<string, Element>(StringComparer.Ordinal);
        Element Missing(string id)
        {
            if (!missing.TryGetValue(id, out var m)) missing[id] = m = MissingReference.Element(id);
            return m;
        }
        Element? Known(string id) => byId.TryGetValue(id, out var e) ? e : library.GetValueOrDefault(id);

        // Pass 2: relationships, now that both ends exist.
        foreach (var (dto, id, kind, _) in dtos)
        {
            if (!Core.Language.Metamodel.IsRelationship(kind)) continue;
            var sourceId = Str(dto, "source");
            var targetId = Str(dto, "target");
            var s = Known(sourceId);
            var t = Known(targetId);
            var r = new Relationship(kind, s ?? Missing(sourceId), t ?? Missing(targetId), id);
            ApplyFields(r, dto, pool);
            byId.Add(id, r);
        }
        // A library's relationships to elements outside the package it shares are left out (read-only, nothing to keep).
        foreach (var lib in libraries)
            foreach (var (dto, id, kind, ownerId) in lib.Dtos)
            {
                if (!Core.Language.Metamodel.IsRelationship(kind)) continue;
                if ((ownerId != null && lib.Skipped.Contains(ownerId)) || byId.ContainsKey(id) || library.ContainsKey(id)
                    || !library.TryGetValue(OptStr(dto, "source") ?? "", out var s) || !library.TryGetValue(OptStr(dto, "target") ?? "", out var t))
                { lib.Skipped.Add(id); continue; }
                var r = new Relationship(kind, s, t, id);
                ApplyFields(r, dto, pool);
                library.Add(id, r);
            }

        // Pass 3: containment.
        Element? root = null;
        foreach (var (_, id, _, ownerId) in dtos)
        {
            var el = byId[id];
            if (ownerId == null) { root = el; continue; }
            var owner = byId[ownerId];
            el.Owner = owner;
            owner.ChildList.Add(el);
        }
        foreach (var lib in libraries)
            foreach (var (_, id, _, ownerId) in lib.Dtos)
            {
                if (ownerId == null || lib.Skipped.Contains(id) || !library.TryGetValue(id, out var el) || !library.TryGetValue(ownerId, out var owner)) continue;
                el.Owner = owner;
                owner.ChildList.Add(el);
            }
        var model = new ModelRepository(root ?? throw new FormatException("Empty project"));
        foreach (var m in missing.Values) model.RegisterMissing(m);

        // Mount each library below its mount point (a project package, or a package of a library mounted before it); a mount
        // point that is gone falls back to where the library that brought it in is mounted, then to the model root.
        var mounted = new Dictionary<string, LibraryMount>(StringComparer.Ordinal);
        foreach (var lib in libraries)
        {
            if (lib.Root is null) continue;   // its root is already in the model (loaded once)
            var at = model.Find(lib.Info.MountId);
            if (at is null || !Core.Language.Metamodel.CanOwn(at.Kind, lib.Root.Kind))
                at = lib.Info.ViaUsageId is { } via && mounted.TryGetValue(via, out var parent) ? parent.MountPoint : model.Root;
            mounted[lib.Info.UsageId] = model.MountLibraryForLoad(lib.Info, at, lib.Root);
        }

        // Pass 4: diagram content.
        foreach (var (dto, id, kind, _) in dtos)
            if (kind == ElementKind.Diagram && byId[id] is Diagram d) ReadDiagram(model, d, dto, Known, Missing);
        foreach (var lib in libraries)
            foreach (var (dto, id, kind, _) in lib.Dtos)
                if (kind == ElementKind.Diagram && !lib.Skipped.Contains(id) && library.TryGetValue(id, out var le) && le is Diagram d)
                    ReadDiagram(model, d, dto, x => library.GetValueOrDefault(x), null);
        foreach (var m in missing.Values) model.RegisterMissing(m);
        return model;
    }

    /// <param name="missing">Placeholders for elements that are gone (the project's diagrams keep such shapes); null drops them (a library's diagrams).</param>
    private static void ReadDiagram(ModelRepository model, Diagram d, JsonElement dto, Func<string, Element?> known, Func<string, Element>? missing)
    {
        var nodes = new Dictionary<string, DiagramNode>();
        if (Prop(dto, "nodes") is { ValueKind: JsonValueKind.Array } nodeArray)
            foreach (var n in nodeArray.EnumerateArray())
            {
                if (n.ValueKind != JsonValueKind.Object) continue;
                var elementId = Str(n, "element");
                Element? el;
                // A redacted view (package permissions) keeps hidden elements' shapes as nameless placeholders.
                if (elementId.StartsWith(Core.Permissions.ModelRedactor.PlaceholderPrefix, StringComparison.Ordinal))
                    el = model.Find(elementId) ?? Core.Permissions.ModelRedactor.Placeholder(elementId);
                else
                {
                    el = known(elementId) ?? missing?.Invoke(elementId);
                    if (el is null) continue;
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
                if (!nodes.TryGetValue(Str(x, "source"), out var s) || !nodes.TryGetValue(Str(x, "target"), out var t)) continue;
                var relId = Str(x, "relationship");
                Relationship r;
                if (known(relId) is Relationship rel) r = rel;
                else if (missing is not null)
                {
                    // A path of a relationship that is gone (a library relationship removed by an update) keeps its id.
                    r = MissingReference.Relationship(relId, s.Element, t.Element);
                    model.RegisterMissing(r);
                }
                else continue;
                var edge = new DiagramEdge(r, s, t, OptStr(x, "id"));
                if (Prop(x, "points") is { ValueKind: JsonValueKind.Array } pts)
                    ModelRepository.SetWaypointsForLoad(edge, pts.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Array)
                        .Select(p => new Pt(p[0].GetDouble(), p[1].GetDouble())));
                if (Prop(x, "style") is { ValueKind: JsonValueKind.Object } es)
                    foreach (var kv in es.EnumerateObject()) ModelRepository.SetEdgeStyleForLoad(edge, kv.Name, kv.Value.GetString()!);
                model.AddEdgeForLoad(d, edge);
            }
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

    private static void ApplyFields(Element e, JsonElement dto, StringPool pool)
    {
        if (Prop(dto, "fields") is not { ValueKind: JsonValueKind.Object } f) return;
        foreach (var kv in f.EnumerateObject())
            ModelRepository.SetFieldForLoad(e, pool.Key(kv.Name), kv.Value.ValueKind == JsonValueKind.Null ? null : pool.Value(kv.Value.GetString()));
    }

    /// <summary>
    /// Shares the strings that repeat across elements: field keys (the well-known ones are the interned constants, shared by
    /// every model in the process) and short values such as type references, multiplicities and enumerations. Every open tab
    /// holds its own replica of the model, so at 200,000 elements this saves tens of megabytes per tab.
    /// </summary>
    private sealed class StringPool
    {
        private const int MaxPooledValue = 48;
        private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);

        public string Key(string key) => string.IsInterned(key) ?? Share(key);

        public string? Value(string? value) => value is { Length: > 0 and <= MaxPooledValue } ? Share(value) : value;

        private string Share(string s)
        {
            if (_strings.TryGetValue(s, out var shared)) return shared;
            _strings[s] = s;
            return s;
        }
    }
}
