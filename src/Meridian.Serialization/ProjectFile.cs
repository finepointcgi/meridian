using System.Text.Json;
using System.Text.Json.Nodes;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>
/// Native project format (*.meridian): a single, diff-friendly JSON document holding the
/// containment tree, relationships and diagram layout.
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".meridian";
    public const int FormatVersion = 1;

    public static void Save(ModelRepository model, string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(model));
        File.Move(tmp, path, overwrite: true);
        model.FilePath = path;
        model.History.MarkSaved();
    }

    public static ModelRepository Load(string path)
    {
        var m = Deserialize(File.ReadAllText(path));
        m.FilePath = path;
        return m;
    }

    public static string Serialize(ModelRepository model)
    {
        var doc = new JsonObject
        {
            ["format"] = "meridian",
            ["version"] = FormatVersion,
            ["root"] = Write(model.Root),
        };
        return doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject Write(Element e)
    {
        var o = new JsonObject { ["id"] = e.Id, ["kind"] = e.Kind.ToString() };
        if (e is Relationship r)
        {
            o["source"] = r.Source.Id;
            o["target"] = r.Target.Id;
        }
        if (e.Fields.Count > 0)
        {
            var f = new JsonObject();
            foreach (var kv in e.Fields.OrderBy(k => k.Key, StringComparer.Ordinal)) f[kv.Key] = kv.Value;
            o["fields"] = f;
        }
        if (e is Diagram d)
        {
            o["diagramType"] = d.DiagramType.ToString();
            o["nodes"] = new JsonArray(d.Nodes.Select(n => (JsonNode)new JsonObject
            {
                ["id"] = n.Id, ["element"] = n.Element.Id,
                ["x"] = Math.Round(n.Bounds.X, 2), ["y"] = Math.Round(n.Bounds.Y, 2),
                ["w"] = Math.Round(n.Bounds.Width, 2), ["h"] = Math.Round(n.Bounds.Height, 2),
                ["parent"] = n.ParentId,
                ["style"] = n.Style.Count == 0 ? null : new JsonObject(n.Style.OrderBy(kv => kv.Key).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            }).ToArray());
            o["edges"] = new JsonArray(d.Edges.Select(x =>
            {
                var eo = new JsonObject { ["id"] = x.Id, ["relationship"] = x.Relationship.Id, ["source"] = x.Source.Id, ["target"] = x.Target.Id };
                if (x.Waypoints.Count > 0)
                    eo["points"] = new JsonArray(x.Waypoints.Select(p => (JsonNode)new JsonArray(Math.Round(p.X, 2), Math.Round(p.Y, 2))).ToArray());
                if (x.Style.Count > 0)
                    eo["style"] = new JsonObject(x.Style.OrderBy(kv => kv.Key).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
                return (JsonNode)eo;
            }).ToArray());
        }
        if (e.OwnedElements.Count > 0)
            o["children"] = new JsonArray(e.OwnedElements.Select(c => (JsonNode)Write(c)).ToArray());
        return o;
    }

    public static ModelRepository Deserialize(string json)
    {
        var doc = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("Not a JSON object");
        if ((string?)doc["format"] != "meridian") throw new FormatException("Not a Meridian project file");
        var version = (int?)doc["version"] ?? 0;
        if (version > FormatVersion) throw new FormatException($"Project was written by a newer version (format {version})");
        var rootDto = doc["root"] as JsonObject ?? throw new FormatException("Missing root");

        // Pass 1: flatten DTOs and create non-relationship elements.
        var dtos = new List<(JsonObject dto, string? ownerId)>();
        Flatten(rootDto, null, dtos);
        var byId = new Dictionary<string, Element>(StringComparer.Ordinal);
        foreach (var (dto, _) in dtos)
        {
            var kind = Enum.Parse<ElementKind>((string)dto["kind"]!);
            var id = (string)dto["id"]!;
            if (Core.Language.Metamodel.IsRelationship(kind)) continue;
            Element el = kind == ElementKind.Diagram
                ? new Diagram(Enum.Parse<DiagramType>((string)dto["diagramType"]!), id)
                : new Element(kind, id);
            ApplyFields(el, dto);
            byId.Add(id, el);
        }

        // Pass 2: relationships, now that both ends exist.
        foreach (var (dto, _) in dtos)
        {
            var kind = Enum.Parse<ElementKind>((string)dto["kind"]!);
            if (!Core.Language.Metamodel.IsRelationship(kind)) continue;
            var id = (string)dto["id"]!;
            if (!byId.TryGetValue((string)dto["source"]!, out var s) || !byId.TryGetValue((string)dto["target"]!, out var t))
                throw new FormatException($"Relationship {id} has a dangling end");
            var r = new Relationship(kind, s, t, id);
            ApplyFields(r, dto);
            byId.Add(id, r);
        }

        // Pass 3: containment.
        Element? root = null;
        foreach (var (dto, ownerId) in dtos)
        {
            var el = byId[(string)dto["id"]!];
            if (ownerId == null) { root = el; continue; }
            var owner = byId[ownerId];
            el.Owner = owner;
            owner.Children.Add(el);
        }
        var model = new ModelRepository(root ?? throw new FormatException("Empty project"));

        // Pass 4: diagram content.
        foreach (var (dto, _) in dtos)
        {
            if (byId[(string)dto["id"]!] is not Diagram d) continue;
            var nodes = new Dictionary<string, DiagramNode>();
            foreach (var n in (dto["nodes"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (!byId.TryGetValue((string)n["element"]!, out var el)) continue;
                var node = new DiagramNode(el, new Bounds((double)n["x"]!, (double)n["y"]!, (double)n["w"]!, (double)n["h"]!), (string?)n["id"]);
                node.ParentId = (string?)n["parent"];
                if (n["style"] is JsonObject st) foreach (var kv in st) node.Style[kv.Key] = (string)kv.Value!;
                nodes[node.Id] = node;
                model.AddNodeForLoad(d, node);
            }
            foreach (var x in (dto["edges"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (!byId.TryGetValue((string)x["relationship"]!, out var rel) || rel is not Relationship r) continue;
                if (!nodes.TryGetValue((string)x["source"]!, out var s) || !nodes.TryGetValue((string)x["target"]!, out var t)) continue;
                var edge = new DiagramEdge(r, s, t, (string?)x["id"]);
                if (x["points"] is JsonArray pts)
                    ModelRepository.SetWaypointsForLoad(edge, pts.OfType<JsonArray>().Select(p => new Pt((double)p[0]!, (double)p[1]!)));
                if (x["style"] is JsonObject es) foreach (var kv in es) ModelRepository.SetEdgeStyleForLoad(edge, kv.Key, (string)kv.Value!);
                model.AddEdgeForLoad(d, edge);
            }
        }
        return model;
    }

    private static void Flatten(JsonObject dto, string? ownerId, List<(JsonObject, string?)> into)
    {
        into.Add((dto, ownerId));
        var id = (string)dto["id"]!;
        foreach (var c in (dto["children"] as JsonArray ?? []).OfType<JsonObject>()) Flatten(c, id, into);
    }

    private static void ApplyFields(Element e, JsonObject dto)
    {
        if (dto["fields"] is not JsonObject f) return;
        foreach (var kv in f) e.SetRaw(kv.Key, (string?)kv.Value);
    }
}
