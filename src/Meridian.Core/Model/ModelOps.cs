namespace Meridian.Core.Model;

/// <summary>
/// Replication operations. Every low-level mutation of a <see cref="ModelRepository"/> is described by one of these,
/// addressed purely by ids (no object references), so it can be applied to another replica of the same model.
/// Undo/redo and cascades go through the same primitives and therefore replicate too.
/// </summary>
public abstract record ModelOp;

public sealed record AttachOp(string OwnerId, int Index, ElementSnap Snap) : ModelOp;
public sealed record DetachOp(string ElementId) : ModelOp;
public sealed record SetFieldOp(string ElementId, string Key, string? Value) : ModelOp;
public sealed record MoveOp(string ElementId, string NewOwnerId, int Index) : ModelOp;
public sealed record NodeInsertOp(string DiagramId, int Index, string NodeId, string ElementId, Bounds Bounds, string? ParentId = null, KeyValuePair<string, string>[]? Style = null) : ModelOp;
public sealed record NodeStyleOp(string DiagramId, string NodeId, string Key, string? Value) : ModelOp;
public sealed record NodeRemoveOp(string DiagramId, string NodeId) : ModelOp;
public sealed record NodeBoundsOp(string DiagramId, string NodeId, Bounds Bounds) : ModelOp;
public sealed record EdgeInsertOp(string DiagramId, int Index, string EdgeId, string RelationshipId, string SourceNodeId, string TargetNodeId, Pt[] Waypoints, KeyValuePair<string, string>[]? Style = null) : ModelOp;
public sealed record EdgeStyleOp(string DiagramId, string EdgeId, string Key, string? Value) : ModelOp;
public sealed record EdgeRemoveOp(string DiagramId, string EdgeId) : ModelOp;
public sealed record EdgeWaypointsOp(string DiagramId, string EdgeId, Pt[] Waypoints) : ModelOp;

public sealed record NodeSnap(string Id, string ElementId, Bounds Bounds, string? ParentId = null, KeyValuePair<string, string>[]? Style = null);
public sealed record EdgeSnap(string Id, string RelationshipId, string SourceNodeId, string TargetNodeId, Pt[] Waypoints, KeyValuePair<string, string>[]? Style = null);

/// <summary>Immutable copy of an element subtree (fields, children, relationship ends, diagram content).</summary>
public sealed record ElementSnap(
    string Id, ElementKind Kind, KeyValuePair<string, string>[] Fields, string? SourceId, string? TargetId,
    DiagramType? DiagramType, NodeSnap[] Nodes, EdgeSnap[] Edges, ElementSnap[] Children)
{
    public static ElementSnap Of(Element e) => new(
        e.Id, e.Kind, e.Fields.ToArray(),
        (e as Relationship)?.Source.Id, (e as Relationship)?.Target.Id,
        (e as Diagram)?.DiagramType,
        e is Diagram d ? d.Nodes.Select(n => new NodeSnap(n.Id, n.Element.Id, n.Bounds, n.ParentId, n.Style.ToArray())).ToArray() : [],
        e is Diagram d2 ? d2.Edges.Select(x => new EdgeSnap(x.Id, x.Relationship.Id, x.Source.Id, x.Target.Id, x.Waypoints.ToArray(), x.Style.ToArray())).ToArray() : [],
        e.OwnedElements.Select(Of).ToArray());

    public IEnumerable<ElementSnap> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.SelfAndDescendants()) yield return d;
    }
}
