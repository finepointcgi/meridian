using Meridian.Core.Libraries;

namespace Meridian.Core.Model;

/// <summary>
/// Replicas for collaboration (docs/PERFORMANCE.md, "Session"). Every open tab edits its own replica of the session's model.
/// Building one by serializing and parsing the model costs ~0.5 s and ~130 MB at 200,000 elements; <see cref="CreateReplica"/>
/// copies the object graph instead and shares everything immutable with the model it was copied from:
/// <list type="bullet">
/// <item>ids, field keys and values (strings) are shared outright;</item>
/// <item>each element's field dictionary is shared copy-on-write (<see cref="Element"/>): the first write on either side takes a
/// private copy, so a tab only pays for the elements it (or a collaborator) changes;</item>
/// <item>only the skeleton is per replica: element objects with owner and children, diagram shapes and paths (which point at
/// the replica's own elements), and the id / kind / incidence indexes.</item>
/// </list>
/// The result is equivalent to <c>ProjectFile.Deserialize(ProjectFile.Serialize(model, includeLibraries: true))</c>: same
/// tree, the same index order, libraries mounted read-only, placeholders for missing references, and an empty undo history.
/// </summary>
public sealed partial class ModelRepository
{
    /// <summary>Builds a repository around an already-copied tree; the caller fills the indexes.</summary>
    private ModelRepository(Element root, int capacity)
    {
        Root = root;
        _index.EnsureCapacity(capacity);
    }

    /// <summary>
    /// An independent replica of this model that shares its immutable data (see the type's remarks). This model must not be
    /// changed while the copy is made (the session calls it under its lock); afterwards both may be changed independently, on
    /// different threads.
    /// </summary>
    public ModelRepository CreateReplica()
    {
        var relationships = new List<Relationship>();
        var diagrams = new List<(Diagram Original, Diagram Copy)>();
        var libraryRoots = _libraries.Count == 0 ? null : new Dictionary<Element, Element>(ReferenceEqualityComparer.Instance);
        var root = CopyTree(Root, relationships, diagrams, libraryRoots);
        var replica = new ModelRepository(root, _index.Count);

        // The id index in the order a freshly loaded model has: the project's tree first, then each library as it was mounted.
        if (libraryRoots is null) replica.IndexIds(root, null);
        else
        {
            var copiedRoots = new HashSet<Element>(libraryRoots.Values, ReferenceEqualityComparer.Instance);
            replica.IndexIds(root, copiedRoots.Contains);
            foreach (var m in _libraries)
                if (libraryRoots.TryGetValue(m.Root, out var copy)) replica.IndexIds(copy, copiedRoots.Contains);
        }

        // Elements outside the tree that something refers to: placeholders for missing references (and anything else detached).
        Dictionary<Element, Element>? detached = null;
        Element Resolve(Element original)
        {
            if (_index.TryGetValue(original.Id, out var known) && ReferenceEquals(known, original)) return replica._index[original.Id];
            detached ??= new Dictionary<Element, Element>(ReferenceEqualityComparer.Instance);
            if (detached.TryGetValue(original, out var copy)) return copy;
            detached[original] = copy = original.CopyForReplica();
            if (copy is Relationship r) { r.Source = Resolve(r.Source); r.Target = Resolve(r.Target); }
            return copy;
        }
        foreach (var r in relationships)
        {
            r.Source = Resolve(r.Source);
            r.Target = Resolve(r.Target);
        }
        if (_missing is not null)
            foreach (var m in _missing.Values) replica.RegisterMissing(Resolve(m));

        // Kind and incidence indexes, in index order (as IndexRecursive builds them).
        foreach (var e in replica._index.Values)
        {
            if (!replica._byKind.TryGetValue(e.Kind, out var byKind))
                replica._byKind[e.Kind] = byKind = new Dictionary<string, Element>(_byKind.TryGetValue(e.Kind, out var mine) ? mine.Count : 0, StringComparer.Ordinal);
            byKind[e.Id] = e;
            if (e is Relationship r)
            {
                replica.AddIncident(r.Source, r);
                if (!ReferenceEquals(r.Source, r.Target)) replica.AddIncident(r.Target, r);
            }
        }

        foreach (var (original, copy) in diagrams) CopyDiagramContent(original, copy, Resolve);

        if (_libraryOf is not null)
        {
            var mounts = new Dictionary<LibraryMount, LibraryMount>(ReferenceEqualityComparer.Instance);
            foreach (var m in _libraries)
            {
                var copy = new LibraryMount(m.Info, Resolve(m.Root), Resolve(m.MountPoint));
                mounts[m] = copy;
                replica._libraries.Add(copy);
            }
            replica._libraryOf = new Dictionary<Element, LibraryMount>(_libraryOf.Count, ReferenceEqualityComparer.Instance);
            foreach (var (e, m) in _libraryOf)
                if (mounts.TryGetValue(m, out var copy)) replica._libraryOf[Resolve(e)] = copy;
        }
        return replica;
    }

    /// <summary>Copies an element subtree (fields shared copy-on-write); relationship ends still point at the originals.</summary>
    private Element CopyTree(Element original, List<Relationship> relationships, List<(Diagram, Diagram)> diagrams,
                             Dictionary<Element, Element>? libraryRoots)
    {
        // Iterative: containment can be thousands of levels deep.
        var root = original.CopyForReplica();
        Collect(original, root);
        var stack = new Stack<(Element Original, Element Copy)>();
        stack.Push((original, root));
        while (stack.Count > 0)
        {
            var (o, c) = stack.Pop();
            var children = o.Children;
            if (children.Count == 0) continue;
            c.ChildList.Capacity = children.Count;
            foreach (var child in children)
            {
                var copy = child.CopyForReplica();
                copy.SetOwnerForReplica(c);
                c.ChildList.Add(copy);
                Collect(child, copy);
                if (child.Children.Count > 0) stack.Push((child, copy));
            }
        }
        return root;

        void Collect(Element o, Element c)
        {
            if (c is Relationship r) relationships.Add(r);
            else if (c is Diagram d) diagrams.Add(((Diagram)o, d));
            if (libraryRoots is not null && IsLibraryRoot(o)) libraryRoots[o] = c;
        }
    }

    /// <summary>Adds a copied subtree to the id index in pre-order (what <see cref="IndexRecursive"/> does), skipping some subtrees.</summary>
    private void IndexIds(Element root, Predicate<Element>? skip)
    {
        var stack = new Stack<Element>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            _index[e.Id] = e;
            var children = e.Children;
            for (var i = children.Count - 1; i >= 0; i--)
            {
                var child = children[i];
                if (skip is null || !skip(child)) stack.Push(child);
            }
        }
    }

    private static void CopyDiagramContent(Diagram original, Diagram copy, Func<Element, Element> resolve)
    {
        var nodes = original.NodeList;
        if (nodes.Count == 0) return;
        copy.NodeList.Capacity = nodes.Count;
        var byNode = new Dictionary<DiagramNode, DiagramNode>(nodes.Count, ReferenceEqualityComparer.Instance);
        foreach (var n in nodes)
        {
            var c = new DiagramNode(resolve(n.Element), n.Bounds, n.Id)
            {
                ParentId = n.ParentId,
                Style = n.Style.Count == 0 ? new() : new Dictionary<string, string>(n.Style),
            };
            byNode[n] = c;
            copy.NodeList.Add(c);
        }
        copy.EdgeList.Capacity = original.EdgeList.Count;
        foreach (var x in original.EdgeList)
        {
            if (!byNode.TryGetValue(x.Source, out var s) || !byNode.TryGetValue(x.Target, out var t)) continue;
            copy.EdgeList.Add(new DiagramEdge((Relationship)resolve(x.Relationship), s, t, x.Id)
            {
                Waypoints = new List<Pt>(x.Waypoints),
                Style = x.Style.Count == 0 ? new() : new Dictionary<string, string>(x.Style),
            });
        }
    }
}
