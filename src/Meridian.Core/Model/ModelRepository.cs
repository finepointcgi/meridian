using Meridian.Core.Commands;
using Meridian.Core.Language;
using Meridian.Core.Libraries;

namespace Meridian.Core.Model;

public enum ChangeKind { Added, Removed, Updated, Moved, DiagramChanged, Reset }

public sealed record ModelChange(ChangeKind Kind, Element? Element, string? Field = null);

/// <summary>
/// The model repository: owns the containment tree, an id index, an incidence index for relationships
/// and the undo history. Every public mutator is undoable and raises <see cref="Changed"/>.
/// </summary>
public sealed partial class ModelRepository
{
    private readonly Dictionary<string, Element> _index = new(StringComparer.Ordinal);
    private readonly Dictionary<Element, List<Relationship>> _incident = new(ReferenceEqualityComparer.Instance);

    public ModelRepository(string name = "Model") : this(CreateRoot(name)) { }

    /// <summary>Wraps an already-built tree (used by loaders). Relationship ends must already be resolved.</summary>
    public ModelRepository(Element root)
    {
        if (root.Kind != ElementKind.Model) throw new ArgumentException("Root must be a Model element", nameof(root));
        Root = root;
        IndexRecursive(root);
    }

    private static Element CreateRoot(string name)
    {
        var root = new Element(ElementKind.Model);
        root.SetRaw(FieldKeys.Name, name);
        return root;
    }

    public Element Root { get; }
    public CommandStack History { get; } = new();
    public string? FilePath { get; set; }

    public event EventHandler<ModelChange>? Changed;

    // ------------------------------------------------------------------ queries

    public Element? Find(string? id) => id != null && _index.TryGetValue(id, out var e) ? e : null;
    public bool Contains(Element e) => _index.TryGetValue(e.Id, out var x) && ReferenceEquals(x, e);
    public IEnumerable<Element> AllElements => _index.Values;
    public IEnumerable<Relationship> Relationships => _index.Values.OfType<Relationship>();
    public IEnumerable<Diagram> Diagrams => OfKind(ElementKind.Diagram).Cast<Diagram>();

    /// <summary>Elements of one kind, from a per-kind index (no scan of the whole model).</summary>
    public IEnumerable<Element> OfKind(ElementKind k) =>
        _byKind.TryGetValue(k, out var d) ? d.Values : Array.Empty<Element>();

    /// <summary>Number of elements in the model (O(1)).</summary>
    public int Count => _index.Count;

    public IReadOnlyList<Relationship> RelationshipsOf(Element e) =>
        _incident.TryGetValue(e, out var l) ? l : Array.Empty<Relationship>();

    public IEnumerable<Relationship> Outgoing(Element e, ElementKind? kind = null) =>
        RelationshipsOf(e).Where(r => ReferenceEquals(r.Source, e) && (kind == null || r.Kind == kind));

    public IEnumerable<Relationship> Incoming(Element e, ElementKind? kind = null) =>
        RelationshipsOf(e).Where(r => ReferenceEquals(r.Target, e) && (kind == null || r.Kind == kind));

    /// <summary>Resolves a typed feature's type: an element id, or null for primitives/unset.</summary>
    public Element? ResolveType(Element feature) => Find(feature.TypeRef);

    public string TypeDisplayName(Element feature)
    {
        var t = ResolveType(feature);
        return t != null ? t.DisplayName : feature.TypeRef;
    }

    public IEnumerable<Element> General(Element classifier) =>
        Outgoing(classifier, ElementKind.Generalization).Select(r => r.Target);

    public IEnumerable<Element> Search(string text) =>
        _index.Values.Where(e => e.Kind != ElementKind.Model &&
            (e.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
             e.Get(FieldKeys.RequirementId).Contains(text, StringComparison.OrdinalIgnoreCase) ||
             e.Get(FieldKeys.Text).Contains(text, StringComparison.OrdinalIgnoreCase)));

    // ------------------------------------------------------------------ raw ops (no undo)

    private void IndexRecursive(Element e)
    {
        _index[e.Id] = e;
        if (!_byKind.TryGetValue(e.Kind, out var byKind)) _byKind[e.Kind] = byKind = new Dictionary<string, Element>(StringComparer.Ordinal);
        byKind[e.Id] = e;
        if (e.Kind == ElementKind.Requirement && _maxRequirementNumber is { } max)
            _maxRequirementNumber = Math.Max(max, RequirementNumber(e));
        if (e is Relationship r)
        {
            AddIncident(r.Source, r);
            if (!ReferenceEquals(r.Source, r.Target)) AddIncident(r.Target, r);
        }
        foreach (var c in e.Children) IndexRecursive(c);
    }

    private void UnindexRecursive(Element e)
    {
        foreach (var c in e.Children) UnindexRecursive(c);
        _index.Remove(e.Id);
        if (_byKind.TryGetValue(e.Kind, out var byKind)) byKind.Remove(e.Id);
        if (e.Kind == ElementKind.Requirement) _maxRequirementNumber = null;
        if (e is Relationship r)
        {
            RemoveIncident(r.Source, r);
            RemoveIncident(r.Target, r);
        }
    }

    private void AddIncident(Element e, Relationship r)
    {
        if (!_incident.TryGetValue(e, out var l)) _incident[e] = l = new List<Relationship>();
        l.Add(r);
    }

    private void RemoveIncident(Element e, Relationship r)
    {
        if (_incident.TryGetValue(e, out var l) && l.Remove(r) && l.Count == 0) _incident.Remove(e);
    }

    // Primitives. Each one is tolerant of state changed by other collaborators (missing targets are skipped) and emits a
    // replication op unless it is itself applying a remote op. A local primitive and the remote application of the op it
    // emits must give the same model: that is what keeps every replica equal to the server (docs/COLLABORATION.md). So a
    // local primitive skips exactly when the server would (and then emits nothing), and the cascades (paths of a removed
    // shape; views and relationships of a detached element) are the same on both sides.
    // While a journal is recording (ReplicaSync, collaboration), each primitive also logs its exact inverse.

    private void AttachRaw(Element owner, Element e, int index, bool normalize = true)
    {
        if (_index.ContainsKey(e.Id) || !Contains(owner)) { SkippedLocal(); return; }
        // A subtree whose references no longer resolve here (an undo after someone deleted a relationship end) is attached
        // the way every other replica will build it from the emitted snapshot (only matters when something replicates).
        if (normalize && !_applyingRemote && OpEmitted != null && !IsConsistent(e))
        {
            if (Materialize(ElementSnap.Of(e)) is not { } rebuilt) { SkippedLocal(); return; }
            e = rebuilt;
        }
        e.Owner = owner;
        if (index < 0 || index > owner.Children.Count) owner.ChildList.Add(e);
        else owner.ChildList.Insert(index, e);
        IndexRecursive(e);
        if (Journal is { } j)
        {
            j.Inverses.Add(() => DetachRaw(e, cascade: false));
            j.Elements[e.Id] = e;
        }
        Raise(ChangeKind.Added, e);
        Emit(() => new AttachOp(owner.Id, owner.Children.IndexOf(e), ElementSnap.Of(e)));
    }

    /// <summary>
    /// Detaches <paramref name="e"/>. With <paramref name="cascade"/>, first removes what would otherwise dangle: the diagram
    /// views of its subtree and the relationships from elsewhere that touch it (the same set <see cref="Delete(Element)"/> removes).
    /// </summary>
    private int DetachRaw(Element e, bool cascade = true)
    {
        if (e.Owner is null || !Contains(e)) { SkippedLocal(); return -1; }
        if (cascade)
        {
            var (rels, views) = CascadeOf(e);
            RemoveViews(views);
            foreach (var r in rels)
                if (Contains(r) && r.Owner != null && !IsLibraryElement(r)) DetachRaw(r, cascade: false);
            if (e.Owner is null || !Contains(e)) return -1;
        }
        var owner = e.Owner;
        var i = owner.Children.IndexOf(e);
        owner.ChildList.RemoveAt(i);
        UnindexRecursive(e);
        e.Owner = null;
        Journal?.Inverses.Add(() => AttachRaw(owner, e, i, normalize: false));
        Raise(ChangeKind.Removed, e);
        Emit(() => new DetachOp(e.Id));
        return i;
    }

    /// <summary>Removes a subtree without undo, events or replication (building a redacted copy; see <see cref="Permissions.ModelRedactor"/>).</summary>
    internal void DetachForRedaction(Element e)
    {
        if (e.Owner is not { } owner || !Contains(e)) return;
        owner.ChildList.Remove(e);
        UnindexRecursive(e);
        e.Owner = null;
    }

    private void SetFieldRaw(Element e, string key, string? value)
    {
        if (!Contains(e)) { SkippedLocal(); return; }
        if (Journal is { } j)
        {
            var old = e.Fields.TryGetValue(key, out var o) ? o : null;
            j.Inverses.Add(() => SetFieldRaw(e, key, old));
        }
        e.SetRaw(key, value);
        Raise(ChangeKind.Updated, e, key);
        Emit(() => new SetFieldOp(e.Id, key, value));
    }

    private void MoveRaw(Element e, Element newOwner, int index)
    {
        if (!Contains(e) || !Contains(newOwner) || e.Owner == null || ReferenceEquals(e, newOwner) || e.IsAncestorOf(newOwner)) { SkippedLocal(); return; }
        var oldOwner = e.Owner;
        var oldIndex = oldOwner.Children.IndexOf(e);
        oldOwner.ChildList.RemoveAt(oldIndex);
        if (index < 0 || index > newOwner.Children.Count) newOwner.ChildList.Add(e);
        else newOwner.ChildList.Insert(index, e);
        e.Owner = newOwner;
        Journal?.Inverses.Add(() => MoveRaw(e, oldOwner, oldIndex));
        Raise(ChangeKind.Moved, e);
        Emit(() => new MoveOp(e.Id, newOwner.Id, newOwner.Children.IndexOf(e)));
    }

    private void NodeInsertRaw(Diagram d, int index, DiagramNode n)
    {
        if (!Contains(d) || !Contains(n.Element) || d.NodeList.Contains(n) || d.FindNode(n.Id) != null) { SkippedLocal(); return; }
        if (index < 0 || index > d.NodeList.Count) d.NodeList.Add(n); else d.NodeList.Insert(index, n);
        if (Journal is { } j)
        {
            j.Inverses.Add(() => NodeRemoveCore(d, n));
            j.Nodes[n.Id] = n;
        }
        Raise(ChangeKind.DiagramChanged, d);
        Emit(() => new NodeInsertOp(d.Id, d.NodeList.IndexOf(n), n.Id, n.Element.Id, n.Bounds, n.ParentId, n.Style.ToArray()));
    }

    /// <summary>Removes a shape and the paths attached to it (as the remote <see cref="NodeRemoveOp"/> does).</summary>
    private void NodeRemoveRaw(Diagram d, DiagramNode n)
    {
        // Only a shape of the model's own diagram: an undo step may still hold a copy of the diagram that was deleted and
        // brought back (as a new object) by someone else meanwhile; the server would remove the live shape by id instead.
        if (HostOf(d, n) is not { } host) { SkippedLocal(); return; }
        foreach (var x in host.EdgesOf(n).ToList()) EdgeRemoveRaw(host, x);
        NodeRemoveCore(host, n);
    }

    private void NodeRemoveCore(Diagram d, DiagramNode n)
    {
        var i = d.NodeList.IndexOf(n);
        if (i < 0) return;
        d.NodeList.RemoveAt(i);
        Journal?.Inverses.Add(() => NodeInsertRaw(d, i, n));
        Raise(ChangeKind.DiagramChanged, d);
        Emit(() => new NodeRemoveOp(d.Id, n.Id));
    }

    private void EdgeInsertRaw(Diagram d, int index, DiagramEdge x)
    {
        if (!Contains(d) || !Contains(x.Relationship) || d.EdgeList.Contains(x) || d.EdgeList.Any(e => e.Id == x.Id)) { SkippedLocal(); return; }
        if (!d.NodeList.Contains(x.Source) || !d.NodeList.Contains(x.Target)) { SkippedLocal(); return; }
        if (index < 0 || index > d.EdgeList.Count) d.EdgeList.Add(x); else d.EdgeList.Insert(index, x);
        if (Journal is { } j)
        {
            j.Inverses.Add(() => EdgeRemoveRaw(d, x));
            j.Edges[x.Id] = x;
        }
        Raise(ChangeKind.DiagramChanged, d);
        Emit(() => new EdgeInsertOp(d.Id, d.EdgeList.IndexOf(x), x.Id, x.Relationship.Id, x.Source.Id, x.Target.Id, x.Waypoints.ToArray(), x.Style.ToArray()));
    }

    private void EdgeRemoveRaw(Diagram d, DiagramEdge x)
    {
        if (HostOf(d, x) is not { } host) { SkippedLocal(); return; }
        d = host;
        var i = d.EdgeList.IndexOf(x);
        d.EdgeList.RemoveAt(i);
        Journal?.Inverses.Add(() => EdgeInsertRaw(d, i, x));
        Raise(ChangeKind.DiagramChanged, d);
        Emit(() => new EdgeRemoveOp(d.Id, x.Id));
    }

    /// <summary>
    /// The model's own diagram when <paramref name="n"/> is one of its shapes (callers may pass a drawn view of a diagram, which
    /// shares the shape objects; see <see cref="DiagramExtras.VisibleView"/>). Null when the shape isn't in the model.
    /// </summary>
    private Diagram? HostOf(Diagram d, DiagramNode n) => Find(d.Id) is Diagram real && real.NodeList.Contains(n) ? real : null;
    private Diagram? HostOf(Diagram d, DiagramEdge x) => Find(d.Id) is Diagram real && real.EdgeList.Contains(x) ? real : null;

    private void NodeStyleRaw(Diagram d, DiagramNode n, string key, string? value)
    {
        if (HostOf(d, n) is not { } host) { SkippedLocal(); return; }
        if (Journal is { } j)
        {
            var old = n.Style.TryGetValue(key, out var o) ? o : null;
            j.Inverses.Add(() => NodeStyleRaw(host, n, key, old));
        }
        if (string.IsNullOrEmpty(value)) n.Style.Remove(key); else n.Style[key] = value;
        Raise(ChangeKind.DiagramChanged, host);
        Emit(() => new NodeStyleOp(host.Id, n.Id, key, value));
    }

    private void EdgeStyleRaw(Diagram d, DiagramEdge x, string key, string? value)
    {
        if (HostOf(d, x) is not { } host) { SkippedLocal(); return; }
        if (Journal is { } j)
        {
            var old = x.Style.TryGetValue(key, out var o) ? o : null;
            j.Inverses.Add(() => EdgeStyleRaw(host, x, key, old));
        }
        if (string.IsNullOrEmpty(value)) x.Style.Remove(key); else x.Style[key] = value;
        Raise(ChangeKind.DiagramChanged, host);
        Emit(() => new EdgeStyleOp(host.Id, x.Id, key, value));
    }

    private void BoundsRaw(Diagram d, DiagramNode n, Bounds b)
    {
        if (HostOf(d, n) is not { } host) { SkippedLocal(); return; }
        if (Journal is { } j)
        {
            var old = n.Bounds;
            j.Inverses.Add(() => BoundsRaw(host, n, old));
        }
        n.Bounds = b;
        Raise(ChangeKind.DiagramChanged, host);
        Emit(() => new NodeBoundsOp(host.Id, n.Id, b));
    }

    private void WaypointsRaw(Diagram d, DiagramEdge x, List<Pt> pts)
    {
        if (HostOf(d, x) is not { } host) { SkippedLocal(); return; }
        if (Journal is { } j)
        {
            var old = x.Waypoints;
            j.Inverses.Add(() => WaypointsRaw(host, x, old));
        }
        x.Waypoints = pts;
        Raise(ChangeKind.DiagramChanged, host);
        Emit(() => new EdgeWaypointsOp(host.Id, x.Id, pts.ToArray()));
    }

    /// <summary>
    /// What detaching <paramref name="e"/> takes along: relationships owned elsewhere that touch its subtree, and every diagram
    /// view (shapes, nested shapes, paths) of the subtree and of those relationships, with positions for undo.
    /// </summary>
    private (List<Relationship> Rels, List<(Diagram diagram, List<(DiagramEdge x, int i)> edges, List<(DiagramNode x, int i)> nodes)> Views) CascadeOf(Element e)
    {
        var subtree = new List<Element> { e };
        subtree.AddRange(e.Descendants());
        var gone = new HashSet<Element>(subtree, ReferenceEqualityComparer.Instance);
        var rels = new List<Relationship>();
        foreach (var x in subtree)
            foreach (var r in RelationshipsOf(x))
                if (gone.Add(r))
                {
                    rels.Add(r);
                    foreach (var c in r.Descendants()) gone.Add(c);
                }
        var views = Diagrams.Where(d => !gone.Contains(d)).Select(d => ViewsOf(d, gone))
            .Where(v => v.edges.Count > 0 || v.nodes.Count > 0).ToList();
        return (rels, views);
    }

    private void RemoveViews(List<(Diagram diagram, List<(DiagramEdge x, int i)> edges, List<(DiagramNode x, int i)> nodes)> views)
    {
        foreach (var v in views)
        {
            foreach (var (x, _) in v.edges) EdgeRemoveRaw(v.diagram, x);
            foreach (var (n, _) in v.nodes) NodeRemoveRaw(v.diagram, n);
        }
    }

    /// <summary>
    /// True when <paramref name="root"/> (a detached subtree) can be attached as is: none of its ids is in the model, and every
    /// reference out of it (relationship ends, shapes' elements, paths' relationships) is to the very object the model holds
    /// for that id — exactly what <see cref="Materialize"/> would rebuild from its snapshot.
    /// </summary>
    private bool IsConsistent(Element root)
    {
        if (root.Children.Count == 0 && root is not Relationship && root is not Diagram) return !_index.ContainsKey(root.Id);
        var inside = new HashSet<Element>(ReferenceEqualityComparer.Instance) { root };
        foreach (var d in root.Descendants()) inside.Add(d);
        bool Resolves(Element target) => inside.Contains(target) || ReferenceEquals(FindOrMissing(target.Id), target);
        foreach (var x in inside)
        {
            if (_index.ContainsKey(x.Id)) return false;
            if (x is Relationship r && (!Resolves(r.Source) || !Resolves(r.Target))) return false;
            if (x is Diagram d)
            {
                foreach (var n in d.NodeList) if (!Resolves(n.Element)) return false;
                foreach (var ed in d.EdgeList) if (!Resolves(ed.Relationship)) return false;
            }
        }
        return true;
    }

    // ------------------------------------------------------------------ replication

    private bool _applyingRemote;
    private int _rebasing;
    private int _skippedLocal;
    private RawJournal? _reuse;

    /// <summary>True while an operation made by someone else is being applied (see <see cref="ApplyRemote"/>).</summary>
    public bool IsApplyingRemote => _applyingRemote;

    /// <summary>
    /// True while collaboration takes this replica's unconfirmed edits back, applies someone else's earlier operations and
    /// re-applies the edits (<see cref="Collaboration.ReplicaSync"/>). Listeners should not react to transient removals then.
    /// </summary>
    public bool IsRebasing => _rebasing > 0;

    /// <summary>
    /// Local edits (commands, undo, redo) that found their target gone (deleted by someone else) and were skipped. A UI
    /// compares it before and after an action to say so.
    /// </summary>
    public int SkippedEdits => _skippedLocal;

    private void SkippedLocal()
    {
        if (!_applyingRemote) _skippedLocal++;
    }

    /// <summary>Raised for every local mutation (including undo/redo), for replication to collaborators.</summary>
    public event Action<ModelOp>? OpEmitted;

    private void Emit(Func<ModelOp> op)
    {
        if (!_applyingRemote && OpEmitted != null) OpEmitted(op());
    }

    /// <summary>
    /// Where primitives log their inverses (see <see cref="RawJournal"/>); null when nothing records. Remote operations are
    /// never recorded, except while re-applying this replica's own pending edits.
    /// </summary>
    internal RawJournal? Journal { get; set; }

    /// <summary>Applies an operation made by someone else. It is not undoable here and is not re-emitted.</summary>
    public void ApplyRemote(ModelOp op) => TryApplyRemote(op);

    /// <summary>
    /// Applies an operation made by someone else (not undoable here, not re-emitted). False when it was dropped: its target,
    /// owner or an end is gone, it would create an ownership cycle, or it touches a library element.
    /// </summary>
    public bool TryApplyRemote(ModelOp op)
    {
        var journal = Journal;
        Journal = null;
        try { return ApplyCore(op); }
        finally { Journal = journal; }
    }

    /// <summary>
    /// Re-applies one of this replica's own operations during a rebase, as the server applied it, logging inverses into
    /// <paramref name="into"/>; objects the operation created the first time are taken from <paramref name="reuse"/> when
    /// they still fit, so undo history, selection and open tabs keep pointing at live objects.
    /// </summary>
    internal bool Reapply(ModelOp op, RawJournal? reuse, RawJournal into)
    {
        var (journal, pool) = (Journal, _reuse);
        (Journal, _reuse) = (into, reuse);
        try { return ApplyCore(op); }
        finally { (Journal, _reuse) = (journal, pool); }
    }

    /// <summary>Takes back everything <paramref name="journal"/> recorded, newest first (no replication, nothing recorded).</summary>
    internal void Revert(RawJournal journal)
    {
        var (was, j) = (_applyingRemote, Journal);
        (_applyingRemote, Journal) = (true, null);
        try
        {
            for (var i = journal.Inverses.Count - 1; i >= 0; i--) journal.Inverses[i]();
        }
        finally { (_applyingRemote, Journal) = (was, j); }
    }

    internal void BeginRebase() => _rebasing++;
    internal void EndRebase() => _rebasing--;

    private bool ApplyCore(ModelOp op)
    {
        // Library elements are read-only everywhere (the server refuses such operations; a replica ignores them too).
        if (_libraries.Count > 0 && LibraryGuard.Check(this, op) != null) return false;
        var was = _applyingRemote;
        _applyingRemote = true;
        try
        {
            switch (op)
            {
                case AttachOp a:
                {
                    if (Find(a.OwnerId) is not { } owner || Find(a.Snap.Id) != null) return false;
                    if ((Reusable(a.Snap) ?? Materialize(a.Snap)) is not { } made) return false;
                    AttachRaw(owner, made, a.Index, normalize: false);
                    return true;
                }
                case DetachOp dt when Find(dt.ElementId) is { Owner: not null } e:
                    DetachRaw(e);   // with its views and the relationships that would dangle
                    return true;
                case SetFieldOp sf when Find(sf.ElementId) is { } e:
                    SetFieldRaw(e, sf.Key, sf.Value);
                    return true;
                case MoveOp mv when Find(mv.ElementId) is { Owner: not null } e && Find(mv.NewOwnerId) is { } no:
                    if (ReferenceEquals(e, no) || e.IsAncestorOf(no)) return false;
                    MoveRaw(e, no, mv.Index);
                    return true;
                case NodeInsertOp ni when Find(ni.DiagramId) is Diagram d && Find(ni.ElementId) is { } el:
                {
                    if (d.FindNode(ni.NodeId) != null) return false;
                    var node = _reuse != null && _reuse.Nodes.TryGetValue(ni.NodeId, out var old) && ReferenceEquals(old.Element, el)
                               && new NodeSnap(ni.NodeId, ni.ElementId, ni.Bounds, ni.ParentId, ni.Style).Describes(old) ? old
                        : new DiagramNode(el, ni.Bounds, ni.NodeId)
                        {
                            ParentId = ni.ParentId,
                            Style = (ni.Style ?? []).ToDictionary(kv => kv.Key, kv => kv.Value),
                        };
                    NodeInsertRaw(d, ni.Index, node);
                    return true;
                }
                case NodeStyleOp ns when Find(ns.DiagramId) is Diagram d && d.FindNode(ns.NodeId) is { } n:
                    NodeStyleRaw(d, n, ns.Key, ns.Value);
                    return true;
                case NodeRemoveOp nr when Find(nr.DiagramId) is Diagram d && d.FindNode(nr.NodeId) is { } n:
                    NodeRemoveRaw(d, n);
                    return true;
                case NodeBoundsOp nb when Find(nb.DiagramId) is Diagram d && d.FindNode(nb.NodeId) is { } n:
                    BoundsRaw(d, n, nb.Bounds);
                    return true;
                case EdgeInsertOp ei when Find(ei.DiagramId) is Diagram d && Find(ei.RelationshipId) is Relationship r
                                         && d.FindNode(ei.SourceNodeId) is { } s && d.FindNode(ei.TargetNodeId) is { } t:
                {
                    if (d.EdgeList.Any(x => x.Id == ei.EdgeId)) return false;
                    var edge = _reuse != null && _reuse.Edges.TryGetValue(ei.EdgeId, out var old)
                               && ReferenceEquals(old.Relationship, r) && ReferenceEquals(old.Source, s) && ReferenceEquals(old.Target, t)
                               && new EdgeSnap(ei.EdgeId, ei.RelationshipId, ei.SourceNodeId, ei.TargetNodeId, ei.Waypoints, ei.Style).Describes(old)
                        ? old
                        : new DiagramEdge(r, s, t, ei.EdgeId) { Waypoints = ei.Waypoints.ToList(), Style = (ei.Style ?? []).ToDictionary(kv => kv.Key, kv => kv.Value) };
                    EdgeInsertRaw(d, ei.Index, edge);
                    return true;
                }
                case EdgeRemoveOp er when Find(er.DiagramId) is Diagram d && d.EdgeList.FirstOrDefault(x => x.Id == er.EdgeId) is { } x:
                    EdgeRemoveRaw(d, x);
                    return true;
                case EdgeStyleOp es when Find(es.DiagramId) is Diagram d && d.EdgeList.FirstOrDefault(x => x.Id == es.EdgeId) is { } x:
                    EdgeStyleRaw(d, x, es.Key, es.Value);
                    return true;
                case EdgeWaypointsOp ew when Find(ew.DiagramId) is Diagram d && d.EdgeList.FirstOrDefault(x => x.Id == ew.EdgeId) is { } x:
                    WaypointsRaw(d, x, ew.Waypoints.ToList());
                    return true;
                default:
                    return false;
            }
        }
        finally { _applyingRemote = was; }
    }

    /// <summary>
    /// During a rebase: the detached subtree this replica made for <paramref name="snap"/> the first time, if it still holds
    /// exactly the snapshot (operations applied while the rewind had it attached may have changed it) and its references still
    /// resolve to the model's objects — then attaching it gives the same model as building the snapshot afresh.
    /// </summary>
    private Element? Reusable(ElementSnap snap) =>
        _reuse != null && _reuse.Elements.TryGetValue(snap.Id, out var e) && e.Owner is null && snap.Describes(e) && IsConsistent(e) ? e : null;

    /// <summary>
    /// Builds a detached element tree from a snapshot, resolving references inside the snapshot or in this model. Parts that
    /// can't be built here are left out, deterministically on every replica: elements whose id is already in the model (ids stay
    /// unique), relationships with an end that no longer exists (and what they own), shapes of missing elements and paths of
    /// missing relationships. Null when the root itself can't be built.
    /// </summary>
    private Element? Materialize(ElementSnap root)
    {
        // Leaving a part out can strand what refers to it (a relationship whose end sat under an element whose id already
        // exists); such relationships are left out too, until nothing in the result refers outside it or the model.
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var built = Build(root, excluded, out var bad);
            if (built is null || bad.Count == 0) return built;
            excluded.UnionWith(bad);
        }
    }

    private Element? Build(ElementSnap root, HashSet<string> excluded, out List<string> bad)
    {
        bad = [];
        var byId = new Dictionary<string, ElementSnap>(StringComparer.Ordinal);
        void Walk(ElementSnap s)
        {
            if (_index.ContainsKey(s.Id) || excluded.Contains(s.Id) || !byId.TryAdd(s.Id, s)) return;
            foreach (var c in s.Children) Walk(c);
        }
        Walk(root);
        if (!byId.ContainsKey(root.Id)) return null;

        var made = new Dictionary<string, Element>(StringComparer.Ordinal);
        var making = new HashSet<string>(StringComparer.Ordinal);
        Element? Get(string? id)
        {
            if (id is null) return null;
            if (made.TryGetValue(id, out var m)) return m;
            if (!byId.TryGetValue(id, out var s)) return FindOrMissing(id);
            if (!making.Add(id)) return null;   // relationships whose ends refer to each other
            Element e;
            if (Metamodel.IsRelationship(s.Kind))
            {
                if (Get(s.SourceId) is not { } src || Get(s.TargetId) is not { } tgt) return null;
                e = new Relationship(s.Kind, src, tgt, s.Id);
            }
            else e = s.Kind == ElementKind.Diagram ? new Diagram(s.DiagramType ?? DiagramType.Package, s.Id) : new Element(s.Kind, s.Id);
            foreach (var (k, v) in s.Fields) e.SetRaw(k, v);
            return made[id] = e;
        }
        Element? Link(ElementSnap s)
        {
            if (Get(s.Id) is not { } e) return null;
            foreach (var c in s.Children)
                if (byId.ContainsKey(c.Id) && Link(c) is { } ce)
                {
                    ce.Owner = e;
                    e.ChildList.Add(ce);
                }
            return e;
        }
        if (Link(root) is not { } top) return null;

        var linked = new HashSet<Element>(ReferenceEqualityComparer.Instance) { top };
        foreach (var d in top.Descendants()) linked.Add(d);
        bool Holds(Element x) => linked.Contains(x) || ReferenceEquals(FindOrMissing(x.Id), x);
        foreach (var e in linked)
            if (e is Relationship r && (!Holds(r.Source) || !Holds(r.Target))) bad.Add(r.Id);
        if (bad.Count > 0) return top;

        Element? Resolve(string id) => made.TryGetValue(id, out var m) ? linked.Contains(m) ? m : null : FindOrMissing(id);
        foreach (var d in linked.OfType<Diagram>())
        {
            var s = byId[d.Id];
            foreach (var n in s.Nodes)
                if (Resolve(n.ElementId) is { } el)
                    d.NodeList.Add(new DiagramNode(el, n.Bounds, n.Id) { ParentId = n.ParentId, Style = (n.Style ?? []).ToDictionary(kv => kv.Key, kv => kv.Value) });
            foreach (var x in s.Edges)
                if (Resolve(x.RelationshipId) is Relationship r && d.FindNode(x.SourceNodeId) is { } sn && d.FindNode(x.TargetNodeId) is { } tn)
                    d.EdgeList.Add(new DiagramEdge(r, sn, tn, x.Id) { Waypoints = x.Waypoints.ToList(), Style = (x.Style ?? []).ToDictionary(kv => kv.Key, kv => kv.Value) });
        }
        return top;
    }

    private void Raise(ChangeKind k, Element? e, string? field = null)
    {
        Revision++;
        Changed?.Invoke(this, new ModelChange(k, e, field));
    }

    /// <summary>Increases with every change notification: a cheap version for caches of derived output (e.g. rendered diagrams in reports).</summary>
    public long Revision { get; private set; }

    /// <summary>Tells listeners the whole model should be re-read (after load/import).</summary>
    public void RaiseReset() => Raise(ChangeKind.Reset, Root);

    // ------------------------------------------------------------------ element API

    public Element DefaultOwnerFor(Element context, ElementKind kind)
    {
        foreach (var a in context.AncestorsAndSelf())
            if (Metamodel.CanOwn(a.Kind, kind)) return a;
        return Root;
    }

    public string UniqueName(Element owner, ElementKind kind)
    {
        var baseName = Metamodel.DisplayName(kind).Replace(" ", "").Replace("/", "");
        var names = owner.Children.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = 1; ; i++)
            if (!names.Contains(baseName + i)) return baseName + i;
    }

    public Element Create(Element owner, ElementKind kind, string? name = null, int index = -1)
    {
        if (Metamodel.IsRelationship(kind)) throw new ArgumentException("Use Connect() for relationships");
        if (kind == ElementKind.Diagram) throw new ArgumentException("Use CreateDiagram() for diagrams");
        var e = new Element(kind);
        e.SetRaw(FieldKeys.Name, name ?? (Metamodel.NeedsName(kind) ? UniqueName(owner, kind) : null));
        if (kind is ElementKind.Property or ElementKind.Port) e.SetRaw(FieldKeys.Multiplicity, "1");
        if (kind == ElementKind.CombinedFragment) e.SetRaw(FieldKeys.Operator, "alt");
        if (kind == ElementKind.Requirement && string.IsNullOrEmpty(e.Get(FieldKeys.RequirementId)))
            e.SetRaw(FieldKeys.RequirementId, NextRequirementId());
        return Add(owner, e, index);
    }

    public T Add<T>(Element owner, T element, int index = -1) where T : Element
    {
        if (!Contains(owner)) throw new InvalidOperationException("Owner is not part of this model");
        EnsureEditable(owner);
        if (!Metamodel.CanOwn(owner.Kind, element.Kind))
            throw new InvalidOperationException($"{Metamodel.DisplayName(owner.Kind)} cannot own {Metamodel.DisplayName(element.Kind)}");
        History.Execute(new DelegateCommand($"Add {Metamodel.DisplayName(element.Kind)}",
            () => AttachRaw(owner, element, index),
            () => DetachRaw(element)));
        return element;
    }

    public Relationship Connect(ElementKind kind, Element source, Element target, Element? owner = null)
    {
        var problem = Metamodel.CheckEnds(kind, source.Kind, target.Kind);
        if (problem != null) throw new InvalidOperationException(problem);
        var r = new Relationship(kind, source, target);
        if (kind is ElementKind.Association or ElementKind.Composition or ElementKind.Aggregation)
            r.SetRaw(FieldKeys.Multiplicity, kind == ElementKind.Composition ? "1" : "*");
        if (kind == ElementKind.Message) r.SetRaw(FieldKeys.MessageSort, "synchCall");
        owner ??= DefaultOwnerFor(source.Owner ?? Root, kind);
        if (kind is ElementKind.Transition or ElementKind.ControlFlow or ElementKind.ObjectFlow or ElementKind.Connector or ElementKind.Message)
            // flows live with their nodes; a pin's flows live with the activity that owns its action
            owner = (Metamodel.IsPin(source.Kind) && source.Owner?.Kind == ElementKind.Action ? source.Owner.Owner : source.Owner) ?? Root;
        if (_libraries.Count > 0) owner = EditableOwnerFor(owner, target, kind);   // links to library elements belong to the project
        return Add(owner, r);
    }

    public Diagram CreateDiagram(Element owner, DiagramType type, string? name = null)
    {
        EnsureEditable(owner);
        if (!Metamodel.IsValidDiagramContext(type, owner.Kind))
            throw new InvalidOperationException($"A {Metamodel.DisplayName(type)} cannot be owned by a {Metamodel.DisplayName(owner.Kind)}");
        var d = new Diagram(type);
        d.SetRaw(FieldKeys.Name, name ?? $"{owner.DisplayName} {Metamodel.DiagramAbbreviation(type)}");
        // New diagrams route their connectors (existing diagrams without the field keep the classic look).
        d.SetRaw(EdgeRouter.DiagramKey, EdgeRouter.Format(EdgeRouter.DefaultFor(type)));
        return Add(owner, d);
    }

    public void Set(Element e, string key, string? value)
    {
        value = string.IsNullOrEmpty(value) ? null : value;
        var old = e.Fields.TryGetValue(key, out var o) ? o : null;
        if (old == value) return;
        EnsureEditable(e);
        // Requirement name/text edits make their trace links suspect (baseline recorded in the same undo step).
        using var suspectStep = Analysis.SuspectLinks.Watches(e, key) ? History.Transaction($"Set {key}") : null;
        if (suspectStep != null) Analysis.SuspectLinks.Baseline(this, e);
        // Renaming a column renames it in the foreign keys, indexes and constraints that list it (same undo step).
        using var renameStep = DataModeling.DbRename.Watches(e, key, old, value) ? History.Transaction($"Rename {old}") : null;
        History.Execute(new DelegateCommand($"Set {key}",
            () => SetFieldRaw(e, key, value),
            () => SetFieldRaw(e, key, old)));
        if (renameStep != null) DataModeling.DbRename.Propagate(this, e, old!, value!);
    }

    public void Rename(Element e, string name) => Set(e, FieldKeys.Name, name);

    public void Move(Element e, Element newOwner, int index = -1)
    {
        if (e.Owner == null) throw new InvalidOperationException("Cannot move the root");
        if (ReferenceEquals(e, newOwner) || e.IsAncestorOf(newOwner)) throw new InvalidOperationException("Cannot move an element into itself");
        EnsureEditable(e);
        EnsureEditable(newOwner);
        if (!Metamodel.CanOwn(newOwner.Kind, e.Kind))
            throw new InvalidOperationException($"{Metamodel.DisplayName(newOwner.Kind)} cannot own {Metamodel.DisplayName(e.Kind)}");
        var oldOwner = e.Owner;
        var oldIndex = oldOwner.Children.IndexOf(e);
        History.Execute(new DelegateCommand("Move",
            () => MoveRaw(e, newOwner, index),
            () => MoveRaw(e, oldOwner, oldIndex)));
    }

    public void Delete(IEnumerable<Element> elements)
    {
        var list = elements.ToList();
        using (History.Transaction(list.Count == 1 ? $"Delete {list[0].DisplayName}" : $"Delete {list.Count} elements"))
            foreach (var e in list)
                if (Contains(e) && e.Owner != null) Delete(e);
    }

    /// <summary>
    /// Deletes an element, its subtree, every relationship touching any of them, and every diagram view of
    /// any of them. Fully undoable.
    /// </summary>
    public void Delete(Element e)
    {
        if (e.Owner == null) throw new InvalidOperationException("Cannot delete the model root");
        EnsureEditable(e);
        EnsureNoMountInside(e);

        // What goes along is worked out each time the delete runs (a redo may find relationships or views collaborators added
        // meanwhile) — the same cascade a replica applies for the DetachOp (DetachRaw). Positions are recorded for undo.
        var detached = new List<(Element el, Element owner, int index)>();
        List<(Diagram diagram, List<(DiagramEdge x, int i)> edges, List<(DiagramNode x, int i)> nodes)> views = [];

        History.Execute(new DelegateCommand($"Delete {e.DisplayName}",
            () =>
            {
                detached.Clear();
                views = [];
                if (e.Owner is null || !Contains(e)) { SkippedLocal(); return; }
                var (rels, found) = CascadeOf(e);
                views = found;
                RemoveViews(views);
                // Detach order: external relationships first, then the element.
                foreach (var r in rels)
                    if (Contains(r) && r.Owner is { } owner && !IsLibraryElement(r)) detached.Add((r, owner, DetachRaw(r, cascade: false)));
                var eo = e.Owner!;
                detached.Add((e, eo, DetachRaw(e, cascade: false)));
            },
            () =>
            {
                for (var k = detached.Count - 1; k >= 0; k--)
                    if (detached[k].index >= 0) AttachRaw(detached[k].owner, detached[k].el, detached[k].index);
                foreach (var v in views)
                {
                    foreach (var (n, i) in v.nodes) NodeInsertRaw(v.diagram, i, n);
                    foreach (var (x, i) in v.edges) EdgeInsertRaw(v.diagram, i, x);
                }
            }));
    }

    /// <summary>
    /// The shapes and paths of <paramref name="d"/> that go with <paramref name="gone"/>: shapes of gone elements and every shape
    /// nested in them, and paths of gone relationships or touching any of those shapes (with their positions, for undo).
    /// Linear in the diagram's size: deleting one element of a model with a thousand diagrams must not cost seconds.
    /// </summary>
    private static (Diagram diagram, List<(DiagramEdge x, int i)> edges, List<(DiagramNode x, int i)> nodes) ViewsOf(Diagram d, HashSet<Element> gone)
    {
        var removedRoots = d.NodeList.Where(p => gone.Contains(p.Element)).ToList();
        HashSet<DiagramNode>? inside = null;
        if (removedRoots.Count > 0)
        {
            inside = new HashSet<DiagramNode>(ReferenceEqualityComparer.Instance);
            foreach (var p in removedRoots) inside.UnionWith(d.DescendantsOf(p));
        }
        var edges = d.EdgeList.Select((x, i) => (x, i)).Where(t => gone.Contains(t.x.Relationship) || gone.Contains(t.x.Source.Element) || gone.Contains(t.x.Target.Element)
            || (inside != null && (inside.Contains(t.x.Source) || inside.Contains(t.x.Target)))).ToList();
        var nodes = removedRoots.Count == 0 ? [] : d.NodeList.Select((x, i) => (x, i)).Where(t => gone.Contains(t.x.Element) || inside!.Contains(t.x)).ToList();
        return (d, edges, nodes);
    }

    /// <summary>
    /// Deep-copies elements (with their owned features and behaviors, not diagrams) into <paramref name="owner"/>,
    /// then re-creates relationships whose ends were both copied. Typed references to copied elements are remapped.
    /// Returns the map from original to copy. One undo step.
    /// </summary>
    public Dictionary<Element, Element> Duplicate(IEnumerable<Element> originals, Element owner, string suffix = " copy")
    {
        var roots = originals.Where(e => e is not Relationship && e is not Diagram && e.Owner != null).Distinct().ToList();
        // Skip elements whose ancestor is also being copied (they come along with it).
        roots = roots.Where(e => !roots.Any(r => !ReferenceEquals(r, e) && r.IsAncestorOf(e))).ToList();
        var map = new Dictionary<Element, Element>(ReferenceEqualityComparer.Instance);

        Element CloneTree(Element src)
        {
            var c = new Element(src.Kind);
            foreach (var (k, v) in src.Fields) c.SetRaw(k, v);
            foreach (var child in src.Children.Where(x => x is not Relationship && x is not Diagram && !IsLibraryRoot(x)))
            {
                var cc = CloneTree(child);
                cc.Owner = c;
                c.ChildList.Add(cc);
            }
            map[src] = c;
            return c;
        }

        using (History.Transaction(roots.Count == 1 ? $"Duplicate {roots[0].DisplayName}" : $"Duplicate {roots.Count} elements"))
        {
            foreach (var r in roots)
            {
                var target = Metamodel.CanOwn(owner.Kind, r.Kind) ? owner : DefaultOwnerFor(r.Owner!, r.Kind);
                var copy = CloneTree(r);
                if (Metamodel.NeedsName(r.Kind) && target.Children.Any(x => x.Name == r.Name))
                    copy.SetRaw(FieldKeys.Name, r.Name + suffix);
                if (r.Kind == ElementKind.Requirement) copy.SetRaw(FieldKeys.RequirementId, NextRequirementId());
                Add(target, copy);
            }
            // Remap typed references inside the copies.
            foreach (var c in map.Values.Where(x => Metamodel.IsTypedFeature(x.Kind)))
                if (Find(c.TypeRef) is { } t && map.TryGetValue(t, out var tc)) Set(c, FieldKeys.Type, tc.Id);
            // Relationships fully inside the copied set (including ones owned by copied elements, e.g. flows).
            foreach (var rel in map.Keys.SelectMany(RelationshipsOf).Distinct().ToList())
                if (map.TryGetValue(rel.Source, out var s) && map.TryGetValue(rel.Target, out var t))
                {
                    var nr = Connect(rel.Kind, s, t);
                    foreach (var (k, v) in rel.Fields) Set(nr, k, v);
                }
        }
        return map;
    }

    public string NextRequirementId()
    {
        // The highest number is cached; adding requirements raises it in place, removing one or changing any
        // requirement id (RequirementIdGeneration) makes it recompute.
        var generation = Element.RequirementIdGeneration;
        if (_maxRequirementNumber == null || _maxRequirementGeneration != generation)
        {
            _maxRequirementNumber = OfKind(ElementKind.Requirement).Select(RequirementNumber).DefaultIfEmpty(0).Max();
            _maxRequirementGeneration = generation;
        }
        return Analysis.RequirementIds.Format(Analysis.RequirementIds.Prefix(this), _maxRequirementNumber.Value + 1);
    }

    private int? _maxRequirementNumber;
    private int _maxRequirementGeneration;
    private readonly Dictionary<ElementKind, Dictionary<string, Element>> _byKind = new();

    /// <summary>The first run of digits of a requirement id ("REQ-012" → 12), 0 when there is none.</summary>
    private static int RequirementNumber(Element r)
    {
        var id = r.Get(FieldKeys.RequirementId);
        var start = 0;
        while (start < id.Length && !char.IsDigit(id[start])) start++;
        var end = start;
        while (end < id.Length && char.IsDigit(id[end])) end++;
        return int.TryParse(id.AsSpan(start, end - start), out var n) ? n : 0;
    }

    // ------------------------------------------------------------------ diagram API

    public DiagramNode AddNode(Diagram d, Element e, Bounds b, bool showRelationships = true, DiagramNode? parent = null)
    {
        var existing = parent == null ? d.NodeFor(e) : d.Nodes.FirstOrDefault(n => ReferenceEquals(n.Element, e) && n.ParentId == parent.Id);
        if (existing != null) return existing;
        EnsureEditable(d);
        var node = new DiagramNode(e, b) { ParentId = parent?.Id };
        using (History.Transaction($"Show {e.DisplayName}"))
        {
            History.Execute(new DelegateCommand("Add shape",
                () => NodeInsertRaw(d, -1, node),
                () => { foreach (var x in d.EdgesOf(node).ToList()) EdgeRemoveRaw(d, x); NodeRemoveRaw(d, node); }));
            if (showRelationships)
                foreach (var r in RelationshipsOf(e)) AddEdge(d, r);
        }
        return node;
    }

    /// <summary>Adds a path for <paramref name="r"/> if both ends are shown and it isn't shown already.</summary>
    public DiagramEdge? AddEdge(Diagram d, Relationship r)
    {
        var existing = d.EdgeFor(r);
        if (existing != null) return existing;
        EnsureEditable(d);
        var s = EndNode(d, r.Source, r.Get(FieldKeys.SourcePart));
        var t = EndNode(d, r.Target, r.Get(FieldKeys.TargetPart));
        if (s == null || t == null) return null;
        var edge = new DiagramEdge(r, s, t);
        History.Execute(new DelegateCommand("Add path",
            () => EdgeInsertRaw(d, -1, edge),
            () => EdgeRemoveRaw(d, edge)));
        return edge;
    }

    /// <summary>The shape showing <paramref name="e"/>; for a port shown on several parts, the one on <paramref name="partId"/>.</summary>
    public static DiagramNode? EndNode(Diagram d, Element e, string? partId)
    {
        if (!string.IsNullOrEmpty(partId))
        {
            var onPart = d.Nodes.FirstOrDefault(n => ReferenceEquals(n.Element, e) && d.ParentOf(n)?.Element.Id == partId);
            if (onPart != null) return onPart;
        }
        return d.NodeFor(e);
    }

    /// <summary>Removes shapes (and their paths) from a diagram without deleting model elements.</summary>
    public void RemoveFromDiagram(Diagram d, IEnumerable<DiagramNode> nodes, IEnumerable<DiagramEdge>? edges = null)
    {
        var nodeSet = nodes.SelectMany(n => d.DescendantsOf(n).Prepend(n)).ToHashSet();
        var edgeSet = (edges ?? []).Concat(d.EdgeList.Where(x => nodeSet.Contains(x.Source) || nodeSet.Contains(x.Target))).ToHashSet();
        if (nodeSet.Count == 0 && edgeSet.Count == 0) return;
        EnsureEditable(d);
        var removedNodes = d.NodeList.Select((n, i) => (n, i)).Where(t => nodeSet.Contains(t.n)).ToList();
        var removedEdges = d.EdgeList.Select((x, i) => (x, i)).Where(t => edgeSet.Contains(t.x)).ToList();
        History.Execute(new DelegateCommand("Remove from diagram",
            () =>
            {
                foreach (var (x, _) in removedEdges) EdgeRemoveRaw(d, x);
                foreach (var (n, _) in removedNodes) NodeRemoveRaw(d, n);
            },
            () =>
            {
                foreach (var (n, i) in removedNodes) NodeInsertRaw(d, i, n);
                foreach (var (x, i) in removedEdges) EdgeInsertRaw(d, i, x);
            }));
    }

    /// <summary>Sets (or clears, with null) a per-shape appearance override such as "fill".</summary>
    public void SetNodeStyle(Diagram d, DiagramNode n, string key, string? value)
    {
        EnsureEditable(d);
        var old = n.Style.TryGetValue(key, out var o) ? o : null;
        value = string.IsNullOrEmpty(value) ? null : value;
        if (old == value) return;
        History.Execute(new DelegateCommand("Change appearance", () => NodeStyleRaw(d, n, key, value), () => NodeStyleRaw(d, n, key, old)));
    }

    public void SetEdgeStyle(Diagram d, DiagramEdge x, string key, string? value)
    {
        EnsureEditable(d);
        var old = x.Style.TryGetValue(key, out var o) ? o : null;
        value = string.IsNullOrEmpty(value) ? null : value;
        if (old == value) return;
        History.Execute(new DelegateCommand("Change appearance", () => EdgeStyleRaw(d, x, key, value), () => EdgeStyleRaw(d, x, key, old)));
    }

    public void SetBounds(Diagram d, IReadOnlyDictionary<DiagramNode, Bounds> newBounds, string description = "Move")
    {
        var changes = newBounds.Where(kv => kv.Key.Bounds != kv.Value).Select(kv => (node: kv.Key, from: kv.Key.Bounds, to: kv.Value)).ToList();
        if (changes.Count == 0) return;
        EnsureEditable(d);
        History.Execute(new DelegateCommand(description,
            () => { foreach (var c in changes) BoundsRaw(d, c.node, c.to); },
            () => { foreach (var c in changes) BoundsRaw(d, c.node, c.from); }));
    }

    public void SetWaypoints(Diagram d, DiagramEdge edge, IReadOnlyList<Pt> points)
    {
        EnsureEditable(d);
        var old = edge.Waypoints;
        var neu = points.ToList();
        History.Execute(new DelegateCommand("Reroute",
            () => WaypointsRaw(d, edge, neu),
            () => WaypointsRaw(d, edge, old)));
    }

    // ------------------------------------------------------------------ loader support

    /// <summary>Directly attaches a pre-built element during loading/import (not undoable).</summary>
    public void AttachForLoad(Element owner, Element e) => AttachRaw(owner, e, -1, normalize: false);

    public void AddNodeForLoad(Diagram d, DiagramNode n) => d.NodeList.Add(n);
    public void AddEdgeForLoad(Diagram d, DiagramEdge e) => d.EdgeList.Add(e);
    public static void SetFieldForLoad(Element e, string key, string? value) => e.SetRaw(key, value);
    public static void SetWaypointsForLoad(DiagramEdge e, IEnumerable<Pt> pts) => e.Waypoints = pts.ToList();
    public static void SetNodeParentForLoad(DiagramNode n, string? parentId) => n.ParentId = parentId;
    public static void SetNodeStyleForLoad(DiagramNode n, string key, string value) => n.Style[key] = value;
    public static void SetEdgeStyleForLoad(DiagramEdge x, string key, string value) => x.Style[key] = value;
}
