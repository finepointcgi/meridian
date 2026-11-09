using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

public enum DiffKind { Added, Removed, Changed, Moved }

public sealed record FieldChange(string Key, string? Before, string? After);

public sealed record ElementDiff(DiffKind Kind, string Id, ElementKind ElementKind, string Name, string Path, IReadOnlyList<FieldChange> Fields);

/// <summary>Element-level comparison of two versions of a model (matched by id).</summary>
public static class ModelDiff
{
    public static IReadOnlyList<ElementDiff> Compare(ModelRepository before, ModelRepository after)
    {
        var result = new List<ElementDiff>();
        var a = before.AllElements.ToDictionary(e => e.Id);
        var b = after.AllElements.ToDictionary(e => e.Id);

        foreach (var (id, e) in b)
            if (!a.ContainsKey(id)) result.Add(new ElementDiff(DiffKind.Added, id, e.Kind, Label(after, e), e.Owner?.QualifiedName ?? "", []));
        foreach (var (id, e) in a)
            if (!b.ContainsKey(id)) result.Add(new ElementDiff(DiffKind.Removed, id, e.Kind, Label(before, e), e.Owner?.QualifiedName ?? "", []));

        foreach (var (id, old) in a)
        {
            if (!b.TryGetValue(id, out var neu)) continue;
            var keys = old.Fields.Keys.Union(neu.Fields.Keys).OrderBy(k => k, StringComparer.Ordinal);
            var changes = keys.Select(k => new FieldChange(k, old.Fields.GetValueOrDefault(k), neu.Fields.GetValueOrDefault(k)))
                              .Where(c => c.Before != c.After).ToList();
            if (old is Relationship ro && neu is Relationship rn)
            {
                if (ro.Source.Id != rn.Source.Id) changes.Add(new FieldChange("source", ro.Source.DisplayName, rn.Source.DisplayName));
                if (ro.Target.Id != rn.Target.Id) changes.Add(new FieldChange("target", ro.Target.DisplayName, rn.Target.DisplayName));
            }
            if (old is Diagram od && neu is Diagram nd && LayoutSignature(od) != LayoutSignature(nd))
                changes.Add(new FieldChange("layout", $"{od.Nodes.Count} shapes, {od.Edges.Count} paths", $"{nd.Nodes.Count} shapes, {nd.Edges.Count} paths"));
            if (changes.Count > 0) result.Add(new ElementDiff(DiffKind.Changed, id, neu.Kind, Label(after, neu), neu.Owner?.QualifiedName ?? "", changes));
            else if (old.Owner?.Id != neu.Owner?.Id)
                result.Add(new ElementDiff(DiffKind.Moved, id, neu.Kind, Label(after, neu), $"{old.Owner?.QualifiedName} → {neu.Owner?.QualifiedName}", []));
        }
        return result.OrderBy(d => d.Kind).ThenBy(d => d.Path).ThenBy(d => d.Name).ToList();
    }

    private static string LayoutSignature(Diagram d) =>
        string.Join(";", d.Nodes.Select(n => $"{n.Element.Id}@{n.Bounds}")) + "|" + string.Join(";", d.Edges.Select(e => $"{e.Relationship.Id}:{string.Join(",", e.Waypoints)}"));

    private static string Label(ModelRepository m, Element e) => e switch
    {
        Relationship r => $"«{Metamodel.Style(r.Kind).Keyword ?? Metamodel.DisplayName(r.Kind)}» {r.Source.DisplayName} → {r.Target.DisplayName}",
        _ when e.Kind == ElementKind.Requirement => $"{e.Get(FieldKeys.RequirementId)} {e.Name}",
        _ => e.DisplayName,
    };
}
