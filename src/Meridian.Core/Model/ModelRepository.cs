using Meridian.Core.Commands;
using Meridian.Core.Language;

namespace Meridian.Core.Model;

public enum ChangeKind { Added, Removed, Updated, Moved, DiagramChanged, Reset }

public sealed record ModelChange(ChangeKind Kind, Element? Element, string? Field = null);

/// <summary>
/// The model repository: owns the containment tree, an id index, an incidence index for relationships
/// and the undo history. Every public mutator is undoable and raises <see cref="Changed"/>.
/// </summary>
public sealed class ModelRepository
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
    public IEnumerable<Diagram> Diagrams => _index.Values.OfType<Diagram>();
    public IEnumerable<Element> OfKind(ElementKind k) => _index.Values.Where(e => e.Kind == k);

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

    // Primitives. Each one is tolerant of state changed by other collaborators (missing targets are skipped)
    // and emits a replication op unless it is itself applying a remote op.

    private void AttachRaw(Element owner, Element e, int index)
    {
        if (_index.ContainsKey(e.Id) || !Contains(owner)) return;
        e.Owner = owner;
        if (index < 0 || index > owner.Children.Count) owner.Children.Add(e);
        else owner.Children.Insert(index, e);
        IndexRecursive(e);
        Raise(ChangeKind.Added, e);
        Emit(() => new AttachOp(owner.Id, owner.Children.IndexOf(e), ElementSnap.Of(e)));
    }

    private int DetachRaw(Element e)
    {
        if (e.Owner is not { } owner || !Contains(e)) return -1;
        var i = owner.Children.IndexOf(e);
        owner.Children.RemoveAt(i);
        UnindexRecursive(e);
        e.Owner = null;
        Raise(ChangeKind.Removed, e);
        Emit(() => new DetachOp(e.Id));
        return i;
    }

    private void SetFieldRaw(Element e, string key, string? value)
    {
        if (!Contains(e)) return;
        e.SetRaw(key, value);
        Raise(ChangeKind.Updated, e, key);
        Emit(() => new SetFieldOp(e.Id, key, value));
    }

    private void MoveRaw(Element e, Element newOwner, int index)
    {
        if (!Contains(e) || !Contains(newOwner) || e.Owner == null || ReferenceEquals(e, newOwner) || e.IsAncestorOf(newOwner)) return;
        e.Owner.Children.Remove(e);
        if (index < 0 || index > newOwner.Children.Count) newOwner.Children.Add(e);
        else newOwner.Children.Insert(index, e);
        e.Owner = newOwner;
        Raise(ChangeKind.Moved, e);
        Emit(() => new MoveOp(e.Id, newOwner.Id, newOwner.Children.IndexOf(e)));
    }

    private void NodeInsertRaw(Diagram d, int index, DiagramNode n)
    {
        if (!Contains(d) || d.NodeList.Contains(n) || d.NodeList.Any(x => x.Id == n.Id)) return;
        if (index < 0 || index > d.NodeList.Count) d.NodeList.Add(n); else d.NodeList.Insert(index, n);
        Raise(ChangeKind.DiagramChanged, d);
        Emit(() => new NodeInsertOp(d.Id, d.NodeList.IndexOf(n), n.Id, n.Element.Id, n.Bounds, n.ParentId, n.Style.ToArray()));
    }

    private void NodeRemoveRaw(Diagram d, DiagramNode n)
    {
        if (!d.NodeList.Remove(n)) return;
        Raise(ChangeKind.DiagramChanged, d);
        Emit(() => new NodeRemoveOp(d.Id, n.Id));
    }

    private void EdgeInsertRaw(Diagram d, int index, DiagramEdge x)
    {
        if (!Contains(d) || d.EdgeList.Contains(x) || d.EdgeList.Any(e => e.Id == x.Id)) return;
        if (!d.NodeList.Contains(x.Source) || !d.NodeList.Contains(x.Target)) return;
        if (index < 0 || index > d.EdgeList.Count) d.EdgeList.Add(x); else d.EdgeList.Insert(index, x);
        Raise(ChangeKind.DiagramChanged, d);
        Emit(() => new EdgeInsertOp(d.Id, d.EdgeList.IndexOf(x), x.Id, x.Relationship.Id, x.Source.Id, x.Target.Id, x.Waypoints.ToArray(), x.Style.ToArray()));
    }

    private void EdgeRemoveRaw(Diagram d, DiagramEdge x)
    {
        if (!d.EdgeList.Remove(x)) return;
        Raise(ChangeKind.DiagramChanged, d);
        Emit(() => new EdgeRemoveOp(d.Id, x.Id));
    }

    private void NodeStyleRaw(Diagram d, DiagramNode n, string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) n.Style.Remove(key); else n.Style[key] = value;
        Raise(ChangeKind.DiagramChanged, d);
        Emit(() => new NodeStyleOp(d.Id, n.Id, key, value));
    }

    private void EdgeStyleRaw(Diagram d, DiagramEdge x, string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) x.Style.Remove(key); else x.Style[key] = value;
        Raise(ChangeKind.DiagramChanged, d);
        Emit(() => new EdgeStyleOp(d.Id, x.Id, key, value));
    }

    private void BoundsRaw(Diagram d, DiagramNode n, Bounds b)
    {
        n.Bounds = b;
        Raise(ChangeKind.DiagramChanged, d);
        Emit(() => new NodeBoundsOp(d.Id, n.Id, b));
    }

    private void WaypointsRaw(Diagram d, DiagramEdge x, List<Pt> pts)
    {
        x.Waypoints = pts;
        Raise(ChangeKind.DiagramChanged, d);
        Emit(() => new EdgeWaypointsOp(d.Id, x.Id, pts.ToArray()));
    }

    // ------------------------------------------------------------------ replication

    private bool _applyingRemote;

    /// <summary>Raised for every local mutation (including undo/redo), for replication to collaborators.</summary>
    public event Action<ModelOp>? OpEmitted;

    private void Emit(Func<ModelOp> op)
    {
        if (!_applyingRemote && OpEmitted != null) OpEmitted(op());
    }

    /// <summary>Applies an operation made by someone else. It is not undoable here and is not re-emitted.</summary>
    public void ApplyRemote(ModelOp op)
    {
        _applyingRemote = true;
        try
        {
            switch (op)
            {
                case AttachOp a when Find(a.OwnerId) is { } owner && Find(a.Snap.Id) == null:
                    AttachRaw(owner, Materialize(a.Snap), a.Index);
                    break;
                case DetachOp dt when Find(dt.ElementId) is { Owner: not null } e:
                    // Views of the element on other diagrams go with it.
                    foreach (var d in Diagrams.ToList())
                    {
                        foreach (var x in d.EdgeList.Where(x => ReferenceEquals(x.Relationship, e) || ReferenceEquals(x.Source.Element, e) || ReferenceEquals(x.Target.Element, e)).ToList()) EdgeRemoveRaw(d, x);
                        foreach (var n in d.NodeList.Where(n => ReferenceEquals(n.Element, e)).ToList()) NodeRemoveRaw(d, n);
                    }
                    DetachRaw(e);
                    break;
                case SetFieldOp sf when Find(sf.ElementId) is { } e:
                    SetFieldRaw(e, sf.Key, sf.Value);
                    break;
                case MoveOp mv when Find(mv.ElementId) is { } e && Find(mv.NewOwnerId) is { } no:
                    MoveRaw(e, no, mv.Index);
                    break;
                case NodeInsertOp ni when Find(ni.DiagramId) is Diagram d && Find(ni.ElementId) is { } el:
                    NodeInsertRaw(d, ni.Index, new DiagramNode(el, ni.Bounds, ni.NodeId)
                    {
                        ParentId = ni.ParentId,
                        Style = (ni.Style ?? []).ToDictionary(kv => kv.Key, kv => kv.Value),
                    });
                    break;
                case NodeStyleOp ns when Find(ns.DiagramId) is Diagram d && d.FindNode(ns.NodeId) is { } n:
                    NodeStyleRaw(d, n, ns.Key, ns.Value);
                    break;
                case NodeRemoveOp nr when Find(nr.DiagramId) is Diagram d && d.FindNode(nr.NodeId) is { } n:
                    foreach (var x in d.EdgesOf(n).ToList()) EdgeRemoveRaw(d, x);
                    NodeRemoveRaw(d, n);
                    break;
                case NodeBoundsOp nb when Find(nb.DiagramId) is Diagram d && d.FindNode(nb.NodeId) is { } n:
                    BoundsRaw(d, n, nb.Bounds);
                    break;
                case EdgeInsertOp ei when Find(ei.DiagramId) is Diagram d && Find(ei.RelationshipId) is Relationship r
                                         && d.FindNode(ei.SourceNodeId) is { } s && d.FindNode(ei.TargetNodeId) is { } t:
                    var edge = new DiagramEdge(r, s, t, ei.EdgeId) { Waypoints = ei.Waypoints.ToList(), Style = (ei.Style ?? []).ToDictionary(kv => kv.Key, kv => kv.Value) };
                    EdgeInsertRaw(d, ei.Index, edge);
                    break;
                case EdgeRemoveOp er when Find(er.DiagramId) is Diagram d && d.EdgeList.FirstOrDefault(x => x.Id == er.EdgeId) is { } x:
                    EdgeRemoveRaw(d, x);
                    break;
                case EdgeStyleOp es when Find(es.DiagramId) is Diagram d && d.EdgeList.FirstOrDefault(x => x.Id == es.EdgeId) is { } x:
                    EdgeStyleRaw(d, x, es.Key, es.Value);
                    break;
                case EdgeWaypointsOp ew when Find(ew.DiagramId) is Diagram d && d.EdgeList.FirstOrDefault(x => x.Id == ew.EdgeId) is { } x:
                    WaypointsRaw(d, x, ew.Waypoints.ToList());
                    break;
            }
        }
        finally { _applyingRemote = false; }
    }

    /// <summary>Builds a detached element tree from a snapshot, resolving references inside the snapshot or in this model.</summary>
    private Element Materialize(ElementSnap root)
    {
        var made = new Dictionary<string, Element>(StringComparer.Ordinal);
        var all = root.SelfAndDescendants().ToList();
        Element? Resolve(string? id) => id == null ? null : made.TryGetValue(id, out var m) ? m : Find(id);

        foreach (var s in all.Where(s => !Metamodel.IsRelationship(s.Kind)))
        {
            Element e = s.Kind == ElementKind.Diagram ? new Diagram(s.DiagramType ?? DiagramType.Package, s.Id) : new Element(s.Kind, s.Id);
            foreach (var (k, v) in s.Fields) e.SetRaw(k, v);
            made[s.Id] = e;
        }
        foreach (var s in all.Where(s => Metamodel.IsRelationship(s.Kind)))
        {
            if (Resolve(s.SourceId) is not { } src || Resolve(s.TargetId) is not { } tgt) continue;
            var r = new Relationship(s.Kind, src, tgt, s.Id);
            foreach (var (k, v) in s.Fields) r.SetRaw(k, v);
            made[s.Id] = r;
        }
        void Link(ElementSnap s)
        {
            if (!made.TryGetValue(s.Id, out var e)) return;
            foreach (var c in s.Children)
            {
                if (!made.TryGetValue(c.Id, out var ce)) continue;
                ce.Owner = e;
                e.Children.Add(ce);
                Link(c);
            }
        }
        Link(root);
        foreach (var s in all.Where(s => s.Kind == ElementKind.Diagram))
        {
            var d = (Diagram)made[s.Id];
            foreach (var n in s.Nodes)
                if (Resolve(n.ElementId) is { } el)
                    d.NodeList.Add(new DiagramNode(el, n.Bounds, n.Id) { ParentId = n.ParentId, Style = (n.Style ?? []).ToDictionary(kv => kv.Key, kv => kv.Value) });
            foreach (var x in s.Edges)
                if (Resolve(x.RelationshipId) is Relationship r && d.FindNode(x.SourceNodeId) is { } sn && d.FindNode(x.TargetNodeId) is { } tn)
                    d.EdgeList.Add(new DiagramEdge(r, sn, tn, x.Id) { Waypoints = x.Waypoints.ToList(), Style = (x.Style ?? []).ToDictionary(kv => kv.Key, kv => kv.Value) });
        }
        return made[root.Id];
    }

    private void Raise(ChangeKind k, Element? e, string? field = null) => Changed?.Invoke(this, new ModelChange(k, e, field));

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
            owner = source.Owner ?? Root;   // flows live with their nodes
        return Add(owner, r);
    }

    public Diagram CreateDiagram(Element owner, DiagramType type, string? name = null)
    {
        if (!Metamodel.IsValidDiagramContext(type, owner.Kind))
            throw new InvalidOperationException($"A {Metamodel.DisplayName(type)} cannot be owned by a {Metamodel.DisplayName(owner.Kind)}");
        var d = new Diagram(type);
        d.SetRaw(FieldKeys.Name, name ?? $"{owner.DisplayName} {Metamodel.DiagramAbbreviation(type)}");
        return Add(owner, d);
    }

    public void Set(Element e, string key, string? value)
    {
        value = string.IsNullOrEmpty(value) ? null : value;
        var old = e.Fields.TryGetValue(key, out var o) ? o : null;
        if (old == value) return;
        History.Execute(new DelegateCommand($"Set {key}",
            () => SetFieldRaw(e, key, value),
            () => SetFieldRaw(e, key, old)));
    }

    public void Rename(Element e, string name) => Set(e, FieldKeys.Name, name);

    public void Move(Element e, Element newOwner, int index = -1)
    {
        if (e.Owner == null) throw new InvalidOperationException("Cannot move the root");
        if (ReferenceEquals(e, newOwner) || e.IsAncestorOf(newOwner)) throw new InvalidOperationException("Cannot move an element into itself");
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

        var subtree = new HashSet<Element>(ReferenceEqualityComparer.Instance) { e };
        foreach (var d in e.Descendants()) subtree.Add(d);
        var externalRels = subtree.SelectMany(RelationshipsOf).Where(r => !subtree.Contains(r)).Distinct().ToList();
        var gone = new HashSet<Element>(subtree, ReferenceEqualityComparer.Instance);
        foreach (var r in externalRels) gone.Add(r);

        // Detach order: external relationships first, then the element. Record positions for undo.
        var detached = new List<(Element el, Element owner, int index)>();
        var views = Diagrams.Where(d => !gone.Contains(d)).Select(d => (
            diagram: d,
            edges: d.EdgeList.Select((x, i) => (x, i)).Where(t => gone.Contains(t.x.Relationship) || gone.Contains(t.x.Source.Element) || gone.Contains(t.x.Target.Element)
                || d.NodeList.Any(p => gone.Contains(p.Element) && (d.DescendantsOf(p).Contains(t.x.Source) || d.DescendantsOf(p).Contains(t.x.Target)))).ToList(),
            nodes: d.NodeList.Select((x, i) => (x, i)).Where(t => gone.Contains(t.x.Element) || d.NodeList.Any(p => gone.Contains(p.Element) && d.DescendantsOf(p).Contains(t.x))).ToList()))
            .Where(v => v.edges.Count > 0 || v.nodes.Count > 0).ToList();

        History.Execute(new DelegateCommand($"Delete {e.DisplayName}",
            () =>
            {
                foreach (var v in views)
                {
                    foreach (var (x, _) in v.edges) EdgeRemoveRaw(v.diagram, x);
                    foreach (var (n, _) in v.nodes) NodeRemoveRaw(v.diagram, n);
                }
                detached.Clear();
                foreach (var r in externalRels.Where(Contains))
                {
                    var owner = r.Owner!;
                    detached.Add((r, owner, DetachRaw(r)));
                }
                var eo = e.Owner!;
                detached.Add((e, eo, DetachRaw(e)));
            },
            () =>
            {
                for (var k = detached.Count - 1; k >= 0; k--) AttachRaw(detached[k].owner, detached[k].el, detached[k].index);
                foreach (var v in views)
                {
                    foreach (var (n, i) in v.nodes) NodeInsertRaw(v.diagram, i, n);
                    foreach (var (x, i) in v.edges) EdgeInsertRaw(v.diagram, i, x);
                }
            }));
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
            foreach (var child in src.Children.Where(x => x is not Relationship && x is not Diagram))
            {
                var cc = CloneTree(child);
                cc.Owner = c;
                c.Children.Add(cc);
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
        var max = OfKind(ElementKind.Requirement)
            .Select(r => r.Get(FieldKeys.RequirementId))
            .Select(id => int.TryParse(new string(id.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();
        return $"REQ-{max + 1:000}";
    }

    // ------------------------------------------------------------------ diagram API

    public DiagramNode AddNode(Diagram d, Element e, Bounds b, bool showRelationships = true, DiagramNode? parent = null)
    {
        var existing = parent == null ? d.NodeFor(e) : d.Nodes.FirstOrDefault(n => ReferenceEquals(n.Element, e) && n.ParentId == parent.Id);
        if (existing != null) return existing;
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
        var old = n.Style.TryGetValue(key, out var o) ? o : null;
        value = string.IsNullOrEmpty(value) ? null : value;
        if (old == value) return;
        History.Execute(new DelegateCommand("Change appearance", () => NodeStyleRaw(d, n, key, value), () => NodeStyleRaw(d, n, key, old)));
    }

    public void SetEdgeStyle(Diagram d, DiagramEdge x, string key, string? value)
    {
        var old = x.Style.TryGetValue(key, out var o) ? o : null;
        value = string.IsNullOrEmpty(value) ? null : value;
        if (old == value) return;
        History.Execute(new DelegateCommand("Change appearance", () => EdgeStyleRaw(d, x, key, value), () => EdgeStyleRaw(d, x, key, old)));
    }

    public void SetBounds(Diagram d, IReadOnlyDictionary<DiagramNode, Bounds> newBounds, string description = "Move")
    {
        var changes = newBounds.Where(kv => kv.Key.Bounds != kv.Value).Select(kv => (node: kv.Key, from: kv.Key.Bounds, to: kv.Value)).ToList();
        if (changes.Count == 0) return;
        History.Execute(new DelegateCommand(description,
            () => { foreach (var c in changes) BoundsRaw(d, c.node, c.to); },
            () => { foreach (var c in changes) BoundsRaw(d, c.node, c.from); }));
    }

    public void SetWaypoints(Diagram d, DiagramEdge edge, IReadOnlyList<Pt> points)
    {
        var old = edge.Waypoints;
        var neu = points.ToList();
        History.Execute(new DelegateCommand("Reroute",
            () => WaypointsRaw(d, edge, neu),
            () => WaypointsRaw(d, edge, old)));
    }

    // ------------------------------------------------------------------ loader support

    /// <summary>Directly attaches a pre-built element during loading/import (not undoable).</summary>
    public void AttachForLoad(Element owner, Element e) => AttachRaw(owner, e, -1);

    public void AddNodeForLoad(Diagram d, DiagramNode n) => d.NodeList.Add(n);
    public void AddEdgeForLoad(Diagram d, DiagramEdge e) => d.EdgeList.Add(e);
    public static void SetFieldForLoad(Element e, string key, string? value) => e.SetRaw(key, value);
    public static void SetWaypointsForLoad(DiagramEdge e, IEnumerable<Pt> pts) => e.Waypoints = pts.ToList();
    public static void SetNodeParentForLoad(DiagramNode n, string? parentId) => n.ParentId = parentId;
    public static void SetNodeStyleForLoad(DiagramNode n, string key, string value) => n.Style[key] = value;
    public static void SetEdgeStyleForLoad(DiagramEdge x, string key, string value) => x.Style[key] = value;
}
