using Meridian.Core.Model;

namespace Meridian.Web.CodeSync;

/// <summary>
/// Puts chosen elements of a model back to how another version of the model has them: elements the reference lacks are
/// deleted, elements only the reference has are re-created (same ids, fields, subtree and diagram shapes), and fields
/// of elements both have are copied from the reference. Used to reject code changes (back to main's version) and to
/// accept only some of them (the rest go back to the merge base before merging).
/// </summary>
public static class CodeSyncRevert
{
    /// <summary>Reverts <paramref name="ids"/> in <paramref name="target"/> to <paramref name="reference"/>. Returns how many elements changed.</summary>
    public static int Revert(ModelRepository target, ModelRepository reference, IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.Ordinal);
        var count = 0;

        // 1. Elements the reference does not have.
        var extra = set.Select(target.Find).OfType<Element>().Where(e => e.Owner != null && reference.Find(e.Id) is null).ToList();
        extra = extra.Where(e => !extra.Any(o => !ReferenceEquals(o, e) && o.IsAncestorOf(e))).ToList();
        if (extra.Count > 0)
        {
            count += extra.Count;
            target.Delete(extra);
        }

        // 2. Elements only the reference has (with their contents), owners before what they own, relationships last.
        var missing = set.Select(reference.Find).OfType<Element>().Where(e => e.Owner != null && target.Find(e.Id) is null)
            .SelectMany(e => e.Descendants().Prepend(e)).Where(e => target.Find(e.Id) is null)
            .Distinct().OrderBy(e => e is Relationship ? 1 : 0).ThenBy(e => e.AncestorsAndSelf().Count()).ToList();
        var restored = new List<Element>();
        foreach (var x in missing.OrderBy(x => x is Diagram ? 1 : 0))
        {
            if (target.Find(x.Id) is not null || target.Find(x.Owner!.Id) is not { } owner) continue;
            Element copy;
            if (x is Diagram xd) copy = new Diagram(xd.DiagramType, xd.Id);   // its shapes follow in RestoreShapes
            else if (x is Relationship r)
            {
                if (target.Find(r.Source.Id) is not { } s || target.Find(r.Target.Id) is not { } t) continue;
                copy = new Relationship(r.Kind, s, t, r.Id);
            }
            else copy = new Element(x.Kind, x.Id);
            foreach (var (k, v) in x.Fields) ModelRepository.SetFieldForLoad(copy, k, v);
            try { target.Add(owner, copy, Math.Min(IndexIn(x), owner.OwnedElements.Count)); }
            catch (InvalidOperationException) { continue; }
            restored.Add(copy);
            count++;
        }
        RestoreShapes(target, reference, restored);

        // 3. Elements both have: the reference's fields (and owner).
        foreach (var id in set)
        {
            if (target.Find(id) is not { } e || reference.Find(id) is not { } x || e is Diagram || restored.Contains(e)) continue;
            var changed = false;
            foreach (var key in e.Fields.Keys.Union(x.Fields.Keys).ToList())
            {
                var want = x.Fields.GetValueOrDefault(key);
                if (e.Fields.GetValueOrDefault(key) == want) continue;
                target.Set(e, key, want);
                changed = true;
            }
            if (x.Owner is { } xo && e.Owner is { } eo && xo.Id != eo.Id && target.Find(xo.Id) is { } newOwner && !e.IsAncestorOf(newOwner))
            {
                try { target.Move(e, newOwner); changed = true; }
                catch (InvalidOperationException) { }
            }
            if (changed) count++;
        }
        return count;
    }

    private static int IndexIn(Element x) => x.Owner is { } o ? Math.Max(0, o.OwnedElements.ToList().IndexOf(x)) : -1;

    /// <summary>Re-adds the shapes and paths that the reference's diagrams draw for re-created elements, with the same ids and places.</summary>
    private static void RestoreShapes(ModelRepository target, ModelRepository reference, IReadOnlyList<Element> restored)
    {
        if (restored.Count == 0) return;
        var ids = restored.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var rd in reference.Diagrams)
        {
            if (target.Find(rd.Id) is not Diagram td) continue;
            // A re-created diagram gets all its shapes back (of elements that exist); others only those of re-created elements.
            var whole = ids.Contains(rd.Id);
            foreach (var rn in rd.Nodes.Where(n => whole || ids.Contains(n.Element.Id)))
            {
                if (td.FindNode(rn.Id) is not null || target.Find(rn.Element.Id) is not { } e) continue;
                if (rn.ParentId is { } pid && td.FindNode(pid) is null) continue;
                var node = new DiagramNode(e, rn.Bounds, rn.Id);
                ModelRepository.SetNodeParentForLoad(node, rn.ParentId);
                foreach (var (k, v) in rn.Style) ModelRepository.SetNodeStyleForLoad(node, k, v);
                target.AddNodeForLoad(td, node);
            }
            foreach (var re in rd.Edges)
            {
                if (td.FindNode(re.Id) is not null || td.Edges.Any(x => x.Id == re.Id)) continue;
                if (!whole && !ids.Contains(re.Relationship.Id) && !ids.Contains(re.Source.Element.Id) && !ids.Contains(re.Target.Element.Id)) continue;
                if (target.Find(re.Relationship.Id) is not Relationship r || td.FindNode(re.Source.Id) is not { } s || td.FindNode(re.Target.Id) is not { } t) continue;
                if (td.EdgeFor(r) is not null) continue;
                var edge = new DiagramEdge(r, s, t, re.Id);
                ModelRepository.SetWaypointsForLoad(edge, re.Waypoints);
                foreach (var (k, v) in re.Style) ModelRepository.SetEdgeStyleForLoad(edge, k, v);
                target.AddEdgeForLoad(td, edge);
            }
        }
    }
}
