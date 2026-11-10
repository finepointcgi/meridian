using Meridian.Core.Model;

namespace Meridian.Core.Permissions;

/// <summary>What to do with someone else's operation for a participant with a filtered view.</summary>
public enum RelayDecision
{
    /// <summary>Send it as is: it only touches what the participant can see.</summary>
    Relay,
    /// <summary>Don't send it: it only touches what the participant can't see.</summary>
    Drop,
    /// <summary>It changes what the participant can see (something moved in or out of view): send a fresh filtered model instead.</summary>
    Resync,
}

/// <summary>What <see cref="PermissionCheck.Decide"/> needs to know about the model before an operation was applied.</summary>
public readonly record struct RelayProbe(bool HiddenBefore, bool ShownAsPlaceholder);

/// <summary>
/// Package permission rules for model operations: <see cref="Check"/> decides whether a user may apply an operation (the
/// server's single write check), <see cref="CheckReplace"/> does the same for a whole-model replacement (merges, version
/// restore, code-sync decisions), and <see cref="Probe"/>/<see cref="Decide"/> filter what others' operations a participant
/// with hidden packages receives.
/// </summary>
public static class PermissionCheck
{
    // ---------------------------------------------------------------- writes

    /// <summary>Null when <paramref name="view"/> may apply <paramref name="op"/> to <paramref name="model"/> (before it is applied), else why not.</summary>
    public static string? Check(ModelRepository model, AccessView view, ModelOp op)
    {
        if (view.IsOwner || (!view.HasRules && view.Default >= PackageAccess.Edit)) return null;
        switch (op)
        {
            case SetFieldOp sf:
                return model.Find(sf.ElementId) is { } e ? Edit(view, e) : null;
            case AttachOp a:
                return model.Find(a.OwnerId) is { } owner ? Attach(model, view, owner, a.Snap) : null;
            case DetachOp d:
                return model.Find(d.ElementId) is { } gone ? Subtree(view, gone) : null;
            case MoveOp mv:
                if (model.Find(mv.ElementId) is not { } moved || model.Find(mv.NewOwnerId) is not { } to) return null;
                return Subtree(view, moved)
                    ?? (moved.Owner is { } from ? Contents(view, from, moved.Id) : null)   // moving out changes the old container
                    ?? Contents(view, to, moved.Id);                                         // and moving in, the new one
            case NodeInsertOp ni:
                return OnDiagram(model, view, ni.DiagramId)
                    ?? (model.Find(ni.ElementId) is { } shown && view.IsHidden(shown) ? view.DeniedMessage(shown) : null);
            case EdgeInsertOp ei:
                return OnDiagram(model, view, ei.DiagramId)
                    ?? (model.Find(ei.RelationshipId) is { } rel && view.IsHidden(rel) ? view.DeniedMessage(rel) : null);
            case NodeStyleOp x: return OnDiagram(model, view, x.DiagramId);
            case NodeRemoveOp x: return OnDiagram(model, view, x.DiagramId);
            case NodeBoundsOp x: return OnDiagram(model, view, x.DiagramId);
            case EdgeStyleOp x: return OnDiagram(model, view, x.DiagramId);
            case EdgeRemoveOp x: return OnDiagram(model, view, x.DiagramId);
            case EdgeWaypointsOp x: return OnDiagram(model, view, x.DiagramId);
            default:
                return null;
        }
    }

    private static string? Edit(AccessView v, Element e) => v.CanEdit(e) ? null : v.DeniedMessage(e);

    /// <summary>The element and everything it contains (deleting or moving it takes them along).</summary>
    private static string? Subtree(AccessView v, Element e)
    {
        if (Edit(v, e) is { } why) return why;
        if (!v.HasRules) return null;
        foreach (var d in e.Descendants())
            if (!v.CanEdit(d)) return v.DeniedMessage(d);
        return null;
    }

    /// <summary>Adding or removing a child <paramref name="childId"/> of <paramref name="container"/>.</summary>
    private static string? Contents(AccessView v, Element container, string childId) =>
        v.CanSeeContents(container) && v.LevelUnder(container, childId) >= PackageAccess.Edit ? null : v.ContentsDeniedMessage(container);

    private static string? OnDiagram(ModelRepository m, AccessView v, string diagramId) =>
        m.Find(diagramId) is Diagram d ? Edit(v, d) : null;

    private static string? Attach(ModelRepository m, AccessView v, Element owner, ElementSnap snap)
    {
        if (Contents(v, owner, snap.Id) is { } why) return why;
        // Levels inside the new subtree (a re-attached package may carry its own rule), and relationship ends.
        var inSnap = snap.SelfAndDescendants().ToDictionary(s => s.Id, StringComparer.Ordinal);
        string? Walk(ElementSnap s, PackageAccess level)
        {
            level = v.ExplicitAt(s.Id) ?? level;
            if (level < PackageAccess.Edit) return v.DeniedMessage(owner);
            foreach (var endId in new[] { s.SourceId, s.TargetId })
            {
                if (endId is null || inSnap.ContainsKey(endId)) continue;
                if (m.Find(endId) is { } end && !(v.Level(end) >= PackageAccess.Edit && !v.IsHidden(end))) return v.DeniedMessage(end);
            }
            foreach (var n in s.Nodes)
                if (!inSnap.ContainsKey(n.ElementId) && m.Find(n.ElementId) is { } shown && v.IsHidden(shown)) return v.DeniedMessage(shown);
            foreach (var c in s.Children)
                if (Walk(c, level) is { } w) return w;
            return null;
        }
        return Walk(snap, v.Level(owner));
    }

    /// <summary>
    /// Null when <paramref name="view"/> may turn <paramref name="before"/> into <paramref name="after"/> (a merge result, a restored
    /// version, accepted or rejected code changes), else why not: every element added, removed, changed or moved must be editable
    /// by the user where it was and where it ends up.
    /// </summary>
    public static string? CheckReplace(ModelRepository before, ModelRepository after, AccessView view)
    {
        if (view.IsOwner || (!view.HasRules && view.Default >= PackageAccess.Edit)) return null;
        foreach (var b in before.AllElements)
        {
            var a = after.Find(b.Id);
            if (a is null) { if (!view.CanEdit(b)) return view.DeniedMessage(b); continue; }
            if (!Same(b, a)) { if (!view.CanEdit(b)) return view.DeniedMessage(b); if (!view.CanEdit(a)) return view.DeniedMessage(a); }
        }
        foreach (var a in after.AllElements)
            if (before.Find(a.Id) is null && !view.CanEdit(a)) return view.DeniedMessage(a);
        return null;
    }

    private static bool Same(Element b, Element a)
    {
        if (b.Kind != a.Kind || b.Owner?.Id != a.Owner?.Id || b.Fields.Count != a.Fields.Count) return false;
        foreach (var (k, v) in b.Fields)
            if (!a.Fields.TryGetValue(k, out var av) || av != v) return false;
        if (b is Relationship rb && a is Relationship ra && (rb.Source.Id != ra.Source.Id || rb.Target.Id != ra.Target.Id)) return false;
        if (b is Diagram db && a is Diagram da)
        {
            if (db.Nodes.Count != da.Nodes.Count || db.Edges.Count != da.Edges.Count) return false;
            for (var i = 0; i < db.Nodes.Count; i++)
            {
                var (x, y) = (db.Nodes[i], da.Nodes[i]);
                if (x.Id != y.Id || x.Element.Id != y.Element.Id || x.Bounds != y.Bounds || x.ParentId != y.ParentId
                    || x.Style.Count != y.Style.Count || x.Style.Any(kv => y.Style.GetValueOrDefault(kv.Key) != kv.Value)) return false;
            }
            for (var i = 0; i < db.Edges.Count; i++)
            {
                var (x, y) = (db.Edges[i], da.Edges[i]);
                if (x.Id != y.Id || x.Relationship.Id != y.Relationship.Id || x.Source.Id != y.Source.Id || x.Target.Id != y.Target.Id
                    || !x.Waypoints.SequenceEqual(y.Waypoints) || x.Style.Count != y.Style.Count
                    || x.Style.Any(kv => y.Style.GetValueOrDefault(kv.Key) != kv.Value)) return false;
            }
        }
        return true;
    }

    // ---------------------------------------------------------------- relaying others' operations to a filtered view

    /// <summary>Looks at the model before <paramref name="op"/> is applied (what it removes or moves may be gone afterwards).</summary>
    public static RelayProbe Probe(ModelRepository model, AccessView view, ModelOp op)
    {
        switch (op)
        {
            case DetachOp d when model.Find(d.ElementId) is { } e:
                var hidden = view.IsHidden(e);
                return new RelayProbe(hidden, hidden && ShownOnVisibleDiagram(model, view, e));
            case MoveOp mv when model.Find(mv.ElementId) is { } e:
                return new RelayProbe(view.IsHidden(e), false);
            case SetFieldOp sf when model.Find(sf.ElementId) is { } e:
                return new RelayProbe(view.IsHidden(e), false);
            case EdgeRemoveOp er: return new RelayProbe(EdgeInvisible(model, view, er.DiagramId, er.EdgeId), false);
            case EdgeStyleOp es: return new RelayProbe(EdgeInvisible(model, view, es.DiagramId, es.EdgeId), false);
            case EdgeWaypointsOp ew: return new RelayProbe(EdgeInvisible(model, view, ew.DiagramId, ew.EdgeId), false);
            default:
                return new RelayProbe(true, false);   // "unknown": decided from the model after the operation
        }
    }

    /// <summary>What a participant with <paramref name="view"/> gets for <paramref name="op"/>, once it has been applied to <paramref name="model"/>.</summary>
    public static RelayDecision Decide(ModelRepository model, AccessView view, ModelOp op, RelayProbe probe)
    {
        if (!view.HidesAnything) return RelayDecision.Relay;
        switch (op)
        {
            case SetFieldOp sf:
                return model.Find(sf.ElementId) is null || probe.HiddenBefore ? RelayDecision.Drop : RelayDecision.Relay;
            case AttachOp a:
            {
                if (model.Find(a.Snap.Id) is not { } root) return RelayDecision.Drop;
                if (view.IsHidden(root)) return RelayDecision.Drop;
                foreach (var s in a.Snap.SelfAndDescendants())
                {
                    if (model.Find(s.Id) is { } x && view.IsHidden(x)) return RelayDecision.Resync;
                    if (s.Nodes.Any(n => model.Find(n.ElementId) is { } shown && view.IsHidden(shown))) return RelayDecision.Resync;
                }
                return RelayDecision.Relay;
            }
            case DetachOp:
                return probe.HiddenBefore ? probe.ShownAsPlaceholder ? RelayDecision.Resync : RelayDecision.Drop : RelayDecision.Relay;
            case MoveOp mv:
            {
                if (model.Find(mv.ElementId) is not { } e) return RelayDecision.Drop;
                var after = view.IsHidden(e);
                return after == probe.HiddenBefore ? after ? RelayDecision.Drop : RelayDecision.Relay : RelayDecision.Resync;
            }
            case NodeInsertOp ni:
                if (DiagramHidden(model, view, ni.DiagramId)) return RelayDecision.Drop;
                return model.Find(ni.ElementId) is { } el && view.IsHidden(el) ? RelayDecision.Resync : RelayDecision.Relay;
            case NodeStyleOp x: return DiagramHidden(model, view, x.DiagramId) ? RelayDecision.Drop : RelayDecision.Relay;
            case NodeRemoveOp x: return DiagramHidden(model, view, x.DiagramId) ? RelayDecision.Drop : RelayDecision.Relay;
            case NodeBoundsOp x: return DiagramHidden(model, view, x.DiagramId) ? RelayDecision.Drop : RelayDecision.Relay;
            case EdgeInsertOp ei:
                return EdgeInvisible(model, view, ei.DiagramId, ei.EdgeId) ? RelayDecision.Drop : RelayDecision.Relay;
            case EdgeRemoveOp or EdgeStyleOp or EdgeWaypointsOp:
                return probe.HiddenBefore ? RelayDecision.Drop : RelayDecision.Relay;
            default:
                return RelayDecision.Resync;
        }
    }

    private static bool DiagramHidden(ModelRepository m, AccessView v, string diagramId) =>
        m.Find(diagramId) is not Diagram d || v.IsHidden(d);

    private static bool EdgeInvisible(ModelRepository m, AccessView v, string diagramId, string edgeId)
    {
        if (m.Find(diagramId) is not Diagram d || v.IsHidden(d)) return true;
        if (d.Edges.FirstOrDefault(x => x.Id == edgeId) is not { } edge) return true;
        return v.IsHidden(edge.Relationship) || v.IsHidden(edge.Source.Element) || v.IsHidden(edge.Target.Element);
    }

    private static bool ShownOnVisibleDiagram(ModelRepository m, AccessView v, Element e)
    {
        var subtree = new HashSet<Element>(e.Descendants().Prepend(e), ReferenceEqualityComparer.Instance);
        foreach (var d in m.Diagrams)
            if (!subtree.Contains(d) && d.Nodes.Any(n => subtree.Contains(n.Element)) && !v.IsHidden(d)) return true;
        return false;
    }
}
