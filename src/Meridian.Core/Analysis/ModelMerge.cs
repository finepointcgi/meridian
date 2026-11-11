using System.Globalization;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

public enum MergeConflictKind
{
    /// <summary>The same field was changed to different values on both sides.</summary>
    Field,
    /// <summary>One side deleted an element (or a package containing it) that the other side changed, extended or referenced.</summary>
    DeleteModify,
    /// <summary>Both sides moved the element to different owners.</summary>
    Move,
    /// <summary>One side moved the element while the other side edited it in its old place.</summary>
    MoveEdit,
    /// <summary>A diagram shape was changed differently on both sides, or removed on one and changed on the other.</summary>
    DiagramNode,
    /// <summary>A diagram path was changed differently on both sides, or removed on one and changed on the other.</summary>
    DiagramEdge,
}

public enum MergeChoice { Ours, Theirs, Custom }

/// <summary>
/// One decision the merge could not make on its own. <see cref="Ours"/> / <see cref="Theirs"/> / <see cref="Base"/> are display
/// values; <see cref="Choice"/> (and <see cref="CustomValue"/> for fields) decide what <see cref="MergeResult.Resolve"/> builds.
/// </summary>
public sealed class MergeConflict
{
    internal MergeConflict(string key, MergeConflictKind kind, string elementId, ElementKind elementKind, string elementName, string path,
                           string subject, string? @base, string? ours, string? theirs, bool allowsCustom, string description)
    {
        Key = key; Kind = kind; ElementId = elementId; ElementKind = elementKind; ElementName = elementName; Path = path;
        Subject = subject; Base = @base; Ours = ours; Theirs = theirs; AllowsCustom = allowsCustom; Description = description;
    }

    /// <summary>Stable identity of the conflict (the same inputs always produce the same keys).</summary>
    public string Key { get; }
    public MergeConflictKind Kind { get; }
    public string ElementId { get; }
    public ElementKind ElementKind { get; }
    public string ElementName { get; }
    public string Path { get; }
    /// <summary>What is in conflict: a field key, "owner", "existence", "shape", "path".</summary>
    public string Subject { get; }
    public string? Base { get; }
    public string? Ours { get; }
    public string? Theirs { get; }
    public bool AllowsCustom { get; }
    public string Description { get; }

    public MergeChoice Choice { get; set; } = MergeChoice.Ours;

    /// <summary>Used when <see cref="Choice"/> is <see cref="MergeChoice.Custom"/> (field conflicts only). Empty removes the field.</summary>
    public string? CustomValue { get; set; }

    public override string ToString() => $"{Kind} {ElementName} [{Subject}]: ours={Ours ?? "∅"} theirs={Theirs ?? "∅"}";
}

/// <summary>Outcome of a three-way merge: the merged model (with every conflict at its current choice) and the conflicts.</summary>
public sealed class MergeResult
{
    private readonly ModelMerge.Snapshot _base, _ours, _theirs;

    internal MergeResult(ModelMerge.Snapshot b, ModelMerge.Snapshot o, ModelMerge.Snapshot t, ModelRepository model, IReadOnlyList<MergeConflict> conflicts)
    {
        _base = b; _ours = o; _theirs = t;
        Model = model;
        Conflicts = conflicts;
    }

    /// <summary>The merged model with every conflict resolved as "ours" (the target side).</summary>
    public ModelRepository Model { get; }
    public IReadOnlyList<MergeConflict> Conflicts { get; }
    public bool HasConflicts => Conflicts.Count > 0;

    /// <summary>Builds the merged model with the current <see cref="MergeConflict.Choice"/> of every conflict.</summary>
    public ModelRepository Resolve()
    {
        var choices = Conflicts.ToDictionary(c => c.Key, c => c);
        return new ModelMerge.Merger(_base, _ours, _theirs, choices).Run();
    }

    /// <summary>Sets every conflict to one side.</summary>
    public void ChooseAll(MergeChoice choice)
    {
        foreach (var c in Conflicts) c.Choice = choice == MergeChoice.Custom && !c.AllowsCustom ? MergeChoice.Ours : choice;
    }
}

/// <summary>
/// Three-way model merge at element/field granularity (elements matched by id). "Ours" is the target being merged into,
/// "theirs" the incoming side, "base" their common ancestor. Non-overlapping changes from both sides are combined;
/// overlapping ones become <see cref="MergeConflict"/>s whose default resolution is "ours".
/// Diagrams are merged per shape and per path (bounds, nesting, style, waypoints).
/// </summary>
public static class ModelMerge
{
    private const string SourceKey = "@source";
    private const string TargetKey = "@target";

    public static MergeResult Merge(ModelRepository @base, ModelRepository ours, ModelRepository theirs)
    {
        var b = Snapshot.Of(@base);
        var o = Snapshot.Of(ours);
        var t = Snapshot.Of(theirs);
        var merger = new Merger(b, o, t, null);
        var model = merger.Run();
        return new MergeResult(b, o, t, model, merger.Conflicts);
    }

    // ================================================================ flat snapshots

    internal sealed record FNode(string Id, string ElementId, Bounds Bounds, string? ParentId, IReadOnlyDictionary<string, string> Style);
    internal sealed record FEdge(string Id, string RelationshipId, string SourceNodeId, string TargetNodeId, IReadOnlyList<Pt> Waypoints, IReadOnlyDictionary<string, string> Style);

    internal sealed class FElement
    {
        public required string Id { get; init; }
        public required ElementKind Kind { get; init; }
        public string? Owner { get; init; }
        public required Dictionary<string, string> Fields { get; init; }   // includes @source / @target for relationships
        public DiagramType? DiagramType { get; init; }
        public List<string> Children { get; } = new();
        public Dictionary<string, FNode> Nodes { get; } = new(StringComparer.Ordinal);
        public List<string> NodeOrder { get; } = new();
        public Dictionary<string, FEdge> Edges { get; } = new(StringComparer.Ordinal);
        public List<string> EdgeOrder { get; } = new();
        public required string Label { get; init; }
        public required string Path { get; init; }

        private string? _sig;
        /// <summary>Everything about this element except its children.</summary>
        public string Signature => _sig ??= string.Join("\u001f",
            new[] { Kind.ToString(), Owner ?? "", DiagramType?.ToString() ?? "" }
                .Concat(Fields.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Key + "=" + f.Value))
                .Concat(NodeOrder.Select(n => NodeSig(Nodes[n])))
                .Concat(EdgeOrder.Select(x => EdgeSig(Edges[x]))));
    }

    internal sealed class Snapshot
    {
        public required string RootId { get; init; }
        public required Dictionary<string, FElement> Elements { get; init; }

        /// <summary>
        /// Ids the model refers to but doesn't contain: used-project (library) elements and missing references
        /// (docs/LIBRARIES.md). References to them survive the merge instead of being dropped as dangling.
        /// </summary>
        public HashSet<string> External { get; } = new(StringComparer.Ordinal);

        public FElement? this[string? id] => id != null && Elements.TryGetValue(id, out var e) ? e : null;

        public static Snapshot Of(ModelRepository m)
        {
            var map = new Dictionary<string, FElement>(m.Count, StringComparer.Ordinal);
            var external = new HashSet<string>(StringComparer.Ordinal);
            foreach (var x in m.MissingReferences) external.Add(x.Id);
            void Walk(Element e)
            {
                if (m.HasLibraries && m.IsLibraryRoot(e))
                {
                    // A used project's content is not the project's to merge: only references to it are.
                    external.Add(e.Id);
                    foreach (var inside in e.Descendants()) external.Add(inside.Id);
                    return;
                }
                var fields = new Dictionary<string, string>(e.Fields, StringComparer.Ordinal);
                if (e is Relationship r) { fields[SourceKey] = r.Source.Id; fields[TargetKey] = r.Target.Id; }
                var f = new FElement
                {
                    Id = e.Id, Kind = e.Kind, Owner = e.Owner?.Id, Fields = fields, DiagramType = (e as Diagram)?.DiagramType,
                    Label = Label(e), Path = e.Owner?.QualifiedName ?? "",
                };
                foreach (var c in e.OwnedElements)
                    if (!m.HasLibraries || !m.IsLibraryRoot(c)) f.Children.Add(c.Id);
                if (e is Diagram d)
                {
                    foreach (var n in d.Nodes)
                    {
                        f.Nodes[n.Id] = new FNode(n.Id, n.Element.Id, n.Bounds, n.ParentId, new Dictionary<string, string>(n.Style));
                        f.NodeOrder.Add(n.Id);
                    }
                    foreach (var x in d.Edges)
                    {
                        f.Edges[x.Id] = new FEdge(x.Id, x.Relationship.Id, x.Source.Id, x.Target.Id, x.Waypoints.ToList(), new Dictionary<string, string>(x.Style));
                        f.EdgeOrder.Add(x.Id);
                    }
                }
                map[e.Id] = f;
                foreach (var c in e.OwnedElements) Walk(c);
            }
            Walk(m.Root);
            var snapshot = new Snapshot { RootId = m.Root.Id, Elements = map };
            snapshot.External.UnionWith(external);
            return snapshot;
        }

        public bool IsInSubtree(string id, string rootId)
        {
            for (var e = this[id]; e != null; e = this[e.Owner])
                if (e.Id == rootId) return true;
            return false;
        }

        /// <summary>Pre-order (an element, then each child's subtree in order), without nested iterators (O(depth) per item).</summary>
        public IEnumerable<string> Subtree(string id)
        {
            var stack = new Stack<string>();
            stack.Push(id);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (this[cur] is not { } e) continue;
                yield return cur;
                for (var i = e.Children.Count - 1; i >= 0; i--) stack.Push(e.Children[i]);
            }
        }
    }

    private static string Label(Element e) => e switch
    {
        Relationship r => $"«{Metamodel.Style(r.Kind).Keyword ?? Metamodel.DisplayName(r.Kind)}» {r.Source.DisplayName} → {r.Target.DisplayName}",
        _ when e.Kind == ElementKind.Requirement && e.Get(FieldKeys.RequirementId) is { Length: > 0 } rid => $"{rid} {e.Name}",
        _ => e.DisplayName,
    };

    private static string StyleSig(IReadOnlyDictionary<string, string> s) =>
        string.Join(",", s.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + ":" + k.Value));

    internal static string NodeSig(FNode n) => $"N{n.Id}|{n.ElementId}|{FormatBounds(n.Bounds)}|{n.ParentId}|{StyleSig(n.Style)}";
    internal static string EdgeSig(FEdge x) => $"E{x.Id}|{x.RelationshipId}|{x.SourceNodeId}|{x.TargetNodeId}|{FormatPoints(x.Waypoints)}|{StyleSig(x.Style)}";

    public static string FormatBounds(Bounds b) =>
        string.Create(CultureInfo.InvariantCulture, $"({b.X:0.#}, {b.Y:0.#}) {b.Width:0.#}×{b.Height:0.#}");

    private static string FormatPoints(IReadOnlyList<Pt> pts) =>
        pts.Count == 0 ? "straight" : string.Join(" ", pts.Select(p => string.Create(CultureInfo.InvariantCulture, $"({p.X:0.#},{p.Y:0.#})")));

    // ================================================================ the merge itself

    internal sealed class Merger(Snapshot b, Snapshot o, Snapshot t, IReadOnlyDictionary<string, MergeConflict>? choices)
    {
        private readonly Dictionary<string, MergeConflict> _conflicts = new(StringComparer.Ordinal);
        private readonly List<MergeConflict> _ordered = new();

        public IReadOnlyList<MergeConflict> Conflicts => _ordered;

        private sealed class Merged
        {
            public required string Id { get; init; }
            public required ElementKind Kind { get; init; }
            public required string? Owner { get; set; }
            public required Dictionary<string, string> Fields { get; init; }
            public DiagramType? DiagramType { get; init; }
            public string? BaseOwner { get; init; }
            public List<FNode> Nodes { get; } = new();
            public List<FEdge> Edges { get; } = new();
        }

        private readonly Dictionary<string, Merged> _out = new(StringComparer.Ordinal);

        /// <summary>Registers a conflict (or finds the caller's previous decision) and returns the chosen side.</summary>
        private MergeConflict Conflict(string key, MergeConflictKind kind, FElement info, string subject, string? bv, string? ov, string? tv, bool allowsCustom, string description)
        {
            if (_conflicts.TryGetValue(key, out var existing)) return existing;
            var c = new MergeConflict(key, kind, info.Id, info.Kind, info.Label, info.Path, subject, bv, ov, tv, allowsCustom, description);
            if (choices != null && choices.TryGetValue(key, out var prior))
            {
                c.Choice = prior.Choice == MergeChoice.Custom && !allowsCustom ? MergeChoice.Ours : prior.Choice;
                c.CustomValue = prior.CustomValue;
            }
            _conflicts[key] = c;
            _ordered.Add(c);
            return c;
        }

        private string Name(Snapshot s, string? id) => s[id] is { } e ? (e.Path.Length > 0 ? e.Path + "::" : "") + e.Label : id ?? "∅";

        public ModelRepository Run()
        {
            // Sized up front: at 200,000 elements, rehashing these sets was a good part of the merge.
            var ids = new List<string>(o.Elements.Count + 64);
            var seen = new HashSet<string>(o.Elements.Count + 64, StringComparer.Ordinal);
            _out.EnsureCapacity(o.Elements.Count + 64);
            // Deterministic order: ours tree order, then theirs-only, then base-only.
            foreach (var s in new[] { o, t, b })
                foreach (var id in s.Subtree(s.RootId))
                    if (seen.Add(id)) ids.Add(id);

            var keep = DecideExistence(ids);
            foreach (var id in ids.Where(keep.Contains)) MergeElement(id);
            ResolveOwners();
            return Build();
        }

        // ------------------------------------------------------------ existence (adds, deletes, delete/modify)

        private HashSet<string> DecideExistence(List<string> ids)
        {
            var keep = new HashSet<string>(ids.Count, StringComparer.Ordinal);
            var decided = new Dictionary<string, bool>(StringComparer.Ordinal);   // deletion root → kept?
            foreach (var id in ids)
            {
                var (be, oe, te) = (b[id], o[id], t[id]);
                if (be is null) { keep.Add(id); continue; }                 // added on one or both sides
                if (oe != null && te != null) { keep.Add(id); continue; }    // present everywhere
                if (oe is null && te is null) continue;                      // deleted on both sides
                // Deleted on exactly one side.
                var (deleter, other, deleterIsOurs) = oe is null ? (o, t, true) : (t, o, false);
                var root = DeletionRoot(id, deleter, other);
                if (!decided.TryGetValue(root, out var kept))
                {
                    var touch = Touch(root, other);
                    if (touch is null) kept = false;
                    else
                    {
                        var info = b[root]!;
                        var c = Conflict($"delete:{root}", MergeConflictKind.DeleteModify, info, "existence", "present",
                            deleterIsOurs ? "deleted" : $"kept · {touch}", deleterIsOurs ? $"kept · {touch}" : "deleted", false,
                            $"{(deleterIsOurs ? "Ours" : "Theirs")} deleted “{info.Label}” but {(deleterIsOurs ? "theirs" : "ours")} {touch}.");
                        kept = deleterIsOurs ? c.Choice == MergeChoice.Theirs : c.Choice != MergeChoice.Theirs;
                    }
                    decided[root] = kept;
                }
                if (kept) keep.Add(id);
            }
            return keep;
        }

        /// <summary>Topmost base ancestor that the deleting side removed together with <paramref name="id"/> (and the other side still has).</summary>
        private string DeletionRoot(string id, Snapshot deleter, Snapshot other)
        {
            var root = id;
            for (var owner = b[id]?.Owner; owner != null; owner = b[owner]?.Owner)
            {
                if (deleter[owner] != null || other[owner] == null) break;
                root = owner;
            }
            return root;
        }

        /// <summary>Describes how <paramref name="other"/> changed the base subtree rooted at <paramref name="root"/>; null when it did not.</summary>
        private string? Touch(string root, Snapshot other)
        {
            var baseIds = b.Subtree(root).ToHashSet(StringComparer.Ordinal);
            foreach (var id in baseIds)
            {
                var oe = other[id];
                if (oe is null) continue;   // the other side deleted that part too
                if (oe.Signature != b[id]!.Signature)
                    return id == root ? "changed it" : $"changed “{oe.Label}” inside it";
            }
            foreach (var id in other.Subtree(root))
                if (!baseIds.Contains(id)) return $"added “{other[id]!.Label}” inside it";
            foreach (var id in baseIds)
                if (other[id] != null && !other.IsInSubtree(id, root)) return $"moved “{other[id]!.Label}” out of it";
            // New or re-pointed relationships and diagram shapes that reference the deleted elements.
            foreach (var (e, referenced, isShape) in NewReferences(other))
            {
                if (baseIds.Contains(e.Id) || !baseIds.Contains(referenced)) continue;
                return isShape ? $"showed it on diagram “{e.Label}”" : $"added “{e.Label}” referencing it";
            }
            return null;
        }

        private readonly Dictionary<Snapshot, List<(FElement Element, string Referenced, bool IsShape)>> _newRefs = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Every reference <paramref name="other"/> has that the base lacks: relationship ends that are new or re-pointed and
        /// diagram shapes that are new, in snapshot order. Computed once per side, so <see cref="Touch"/> doesn't rescan the model
        /// for every deletion.
        /// </summary>
        private List<(FElement Element, string Referenced, bool IsShape)> NewReferences(Snapshot other)
        {
            if (_newRefs.TryGetValue(other, out var list)) return list;
            list = new();
            foreach (var e in other.Elements.Values)
            {
                var be = b[e.Id];
                foreach (var key in EndKeys)
                    if (e.Fields.TryGetValue(key, out var end) && be?.Fields.GetValueOrDefault(key) != end)
                        list.Add((e, end, false));
                foreach (var n in e.Nodes.Values)
                    if (be?.Nodes.ContainsKey(n.Id) != true)
                        list.Add((e, n.ElementId, true));
            }
            return _newRefs[other] = list;
        }

        private static readonly string[] EndKeys = [SourceKey, TargetKey];

        // ------------------------------------------------------------ element content

        private void MergeElement(string id)
        {
            var (be, oe, te) = (b[id], o[id], t[id]);
            var any = oe ?? te ?? be!;
            Dictionary<string, string> fields;
            string? owner;
            if (oe != null && te != null)
            {
                var baseFields = be?.Fields ?? new Dictionary<string, string>();
                fields = MergeFields(any, baseFields, oe.Fields, te.Fields);
                owner = MergeOwner(any, be, oe, te);
            }
            else
            {
                // Present on one side only: an addition, or a deletion overruled by a delete/modify resolution.
                var src = oe ?? te!;
                fields = new Dictionary<string, string>(src.Fields, StringComparer.Ordinal);
                owner = src.Owner;
            }
            var m = new Merged
            {
                Id = id, Kind = any.Kind, Owner = owner, Fields = fields,
                DiagramType = oe?.DiagramType ?? te?.DiagramType ?? be?.DiagramType, BaseOwner = be?.Owner,
            };
            if (any.Kind == ElementKind.Diagram) MergeDiagram(m, be, oe, te);
            _out[id] = m;
        }

        private Dictionary<string, string> MergeFields(FElement info, IReadOnlyDictionary<string, string> bf, Dictionary<string, string> of, Dictionary<string, string> tf)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (SameFields(of, tf))
            {
                // Both sides agree (the common case): no conflicts are possible, take ours.
                foreach (var key in of.Keys.OrderBy(k => k, StringComparer.Ordinal))
                    if (!string.IsNullOrEmpty(of[key])) result[key] = of[key];
                return result;
            }
            foreach (var key in bf.Keys.Union(of.Keys).Union(tf.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                var bv = bf.GetValueOrDefault(key);
                var ov = of.GetValueOrDefault(key);
                var tv = tf.GetValueOrDefault(key);
                string? v;
                if (ov == tv) v = ov;
                else if (ov == bv) v = tv;
                else if (tv == bv) v = ov;
                else
                {
                    var isEnd = key is SourceKey or TargetKey;
                    var label = isEnd ? key[1..] : key;
                    var c = Conflict($"field:{info.Id}:{key}", MergeConflictKind.Field, info, label,
                        isEnd ? Name(b, bv) : bv, isEnd ? Name(o, ov) : ov, isEnd ? Name(t, tv) : tv, !isEnd,
                        $"Both sides changed {label} of “{info.Label}”.");
                    v = c.Choice switch
                    {
                        MergeChoice.Theirs => tv,
                        MergeChoice.Custom => string.IsNullOrEmpty(c.CustomValue) ? null : c.CustomValue,
                        _ => ov,
                    };
                }
                if (!string.IsNullOrEmpty(v)) result[key] = v;
            }
            return result;
        }

        private static bool SameFields(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var (k, v) in a)
                if (!b.TryGetValue(k, out var w) || v != w) return false;
            return true;
        }

        private string? MergeOwner(FElement info, FElement? be, FElement oe, FElement te)
        {
            var (bo, oo, to) = (be?.Owner, oe.Owner, te.Owner);
            if (oo == to) return oo;
            if (be != null && oo != bo && to != bo)
            {
                var c = Conflict($"move:{info.Id}", MergeConflictKind.Move, info, "owner", Name(b, bo), Name(o, oo), Name(t, to), false,
                    $"Both sides moved “{info.Label}” to different places.");
                return c.Choice == MergeChoice.Theirs ? to : oo;
            }
            if (be is null) // added with the same id on both sides, in different places
            {
                var c = Conflict($"move:{info.Id}", MergeConflictKind.Move, info, "owner", null, Name(o, oo), Name(t, to), false,
                    $"Both sides added “{info.Label}” in different places.");
                return c.Choice == MergeChoice.Theirs ? to : oo;
            }
            // Moved on exactly one side: a conflict when the other side edited the element in its old place.
            var oursMoved = oo != bo;
            var editor = oursMoved ? te : oe;
            var edited = editor.Signature != be.Signature;   // the editor kept the base owner, so only content differs
            if (!edited) return oursMoved ? oo : to;
            var mc = Conflict($"moveedit:{info.Id}", MergeConflictKind.MoveEdit, info, "owner", Name(b, bo),
                Name(o, oo) + (oursMoved ? " (moved)" : " (edited here)"), Name(t, to) + (oursMoved ? " (edited here)" : " (moved)"), false,
                $"{(oursMoved ? "Ours" : "Theirs")} moved “{info.Label}” while {(oursMoved ? "theirs" : "ours")} edited it in its old place; both edits are kept — pick where it lives.");
            return mc.Choice == MergeChoice.Theirs ? to : oo;
        }

        // ------------------------------------------------------------ diagrams

        private void MergeDiagram(Merged m, FElement? be, FElement? oe, FElement? te)
        {
            if (oe is null || te is null)
            {
                var src = oe ?? te!;
                m.Nodes.AddRange(src.NodeOrder.Select(n => src.Nodes[n]));
                m.Edges.AddRange(src.EdgeOrder.Select(x => src.Edges[x]));
                return;
            }
            var info = oe;
            var bn = be?.Nodes ?? new Dictionary<string, FNode>();
            foreach (var id in Order(be?.NodeOrder ?? [], oe.NodeOrder, te.NodeOrder))
            {
                var n = MergeItem(id, bn.GetValueOrDefault(id), oe.Nodes.GetValueOrDefault(id), te.Nodes.GetValueOrDefault(id), NodeSig,
                    MergeConflictKind.DiagramNode, info, "shape", x => $"{ShapeName(x.ElementId)} {FormatBounds(x.Bounds)}", x => ShapeName(x.ElementId), MergeNode);
                if (n != null) m.Nodes.Add(n);
            }
            var bx = be?.Edges ?? new Dictionary<string, FEdge>();
            foreach (var id in Order(be?.EdgeOrder ?? [], oe.EdgeOrder, te.EdgeOrder))
            {
                var x = MergeItem(id, bx.GetValueOrDefault(id), oe.Edges.GetValueOrDefault(id), te.Edges.GetValueOrDefault(id), EdgeSig,
                    MergeConflictKind.DiagramEdge, info, "path", e => $"{ShapeName(e.RelationshipId)} {FormatPoints(e.Waypoints)}", e => ShapeName(e.RelationshipId), MergeEdge);
                if (x != null) m.Edges.Add(x);
            }
        }

        private string ShapeName(string elementId) => (o[elementId] ?? t[elementId] ?? b[elementId])?.Label ?? elementId;

        /// <summary>Three-way merge of one shape or path; property-level merge when both sides kept it.</summary>
        private T? MergeItem<T>(string id, T? bi, T? oi, T? ti, Func<T, string> sig, MergeConflictKind kind, FElement diagram, string what,
                                Func<T, string> show, Func<T, string> subject, Func<string, T?, T, T, FElement, Func<T, string>, T> both) where T : class
        {
            if (oi is null && ti is null) return null;
            if (bi is null) return oi != null && ti != null ? both(id, null, oi, ti, diagram, show) : oi ?? ti;
            if (oi != null && ti != null) return both(id, bi, oi, ti, diagram, show);
            var survivor = oi ?? ti!;
            if (sig(survivor) == sig(bi)) return null;   // removed on one side, untouched on the other
            var oursRemoved = oi is null;
            var c = Conflict($"{what}:{diagram.Id}:{id}", kind, diagram, what, show(bi),
                oursRemoved ? "removed" : show(survivor), oursRemoved ? show(survivor) : "removed", false,
                $"{(oursRemoved ? "Ours" : "Theirs")} removed the {what} of “{subject(survivor)}” from “{diagram.Label}”, which {(oursRemoved ? "theirs" : "ours")} changed.");
            var keepIt = oursRemoved ? c.Choice == MergeChoice.Theirs : c.Choice != MergeChoice.Theirs;
            return keepIt ? survivor : null;
        }

        private static T Pick3<T>(bool hasBase, T bv, T ov, T tv, bool preferTheirs, ref bool conflicted)
        {
            var eq = EqualityComparer<T>.Default;
            if (eq.Equals(ov, tv)) return ov;
            if (hasBase && eq.Equals(ov, bv)) return tv;
            if (hasBase && eq.Equals(tv, bv)) return ov;
            conflicted = true;
            return preferTheirs ? tv : ov;
        }

        private FNode MergeNode(string id, FNode? bn, FNode on, FNode tn, FElement diagram, Func<FNode, string> show)
        {
            FNode Combine(bool preferTheirs, out bool conflicted)
            {
                conflicted = false;
                var hasBase = bn != null;
                var bounds = Pick3(hasBase, bn?.Bounds ?? default, on.Bounds, tn.Bounds, preferTheirs, ref conflicted);
                var parent = Pick3(hasBase, bn?.ParentId, on.ParentId, tn.ParentId, preferTheirs, ref conflicted);
                var style = MergeStyle(bn?.Style, on.Style, tn.Style, preferTheirs, ref conflicted);
                return new FNode(id, on.ElementId, bounds, parent, style);
            }
            var merged = Combine(false, out var conflict);
            if (!conflict) return merged;
            var c = Conflict($"shape:{diagram.Id}:{id}", MergeConflictKind.DiagramNode, diagram, "shape", bn is null ? null : show(bn), show(on), show(tn), false,
                $"Both sides changed the shape of “{ShapeName(on.ElementId)}” on “{diagram.Label}” differently.");
            return c.Choice == MergeChoice.Theirs ? Combine(true, out _) : merged;
        }

        private FEdge MergeEdge(string id, FEdge? bx, FEdge ox, FEdge tx, FElement diagram, Func<FEdge, string> show)
        {
            FEdge Combine(bool preferTheirs, out bool conflicted)
            {
                conflicted = false;
                var hasBase = bx != null;
                var pts = Pick3(hasBase, bx is null ? "" : FormatPoints(bx.Waypoints), FormatPoints(ox.Waypoints), FormatPoints(tx.Waypoints), preferTheirs, ref conflicted);
                var points = pts == FormatPoints(ox.Waypoints) ? ox.Waypoints : tx.Waypoints;
                var style = MergeStyle(bx?.Style, ox.Style, tx.Style, preferTheirs, ref conflicted);
                return ox with { Waypoints = points, Style = style };
            }
            var merged = Combine(false, out var conflict);
            if (!conflict) return merged;
            var c = Conflict($"path:{diagram.Id}:{id}", MergeConflictKind.DiagramEdge, diagram, "path", bx is null ? null : show(bx), show(ox), show(tx), false,
                $"Both sides changed the path of “{ShapeName(ox.RelationshipId)}” on “{diagram.Label}” differently.");
            return c.Choice == MergeChoice.Theirs ? Combine(true, out _) : merged;
        }

        private static Dictionary<string, string> MergeStyle(IReadOnlyDictionary<string, string>? bs, IReadOnlyDictionary<string, string> os, IReadOnlyDictionary<string, string> ts, bool preferTheirs, ref bool conflicted)
        {
            var result = new Dictionary<string, string>();
            bs ??= new Dictionary<string, string>();
            foreach (var k in bs.Keys.Union(os.Keys).Union(ts.Keys))
            {
                var (bv, ov, tv) = (bs.GetValueOrDefault(k), os.GetValueOrDefault(k), ts.GetValueOrDefault(k));
                string? v;
                if (ov == tv) v = ov;
                else if (ov == bv) v = tv;
                else if (tv == bv) v = ov;
                else { conflicted = true; v = preferTheirs ? tv : ov; }
                if (v != null) result[k] = v;
            }
            return result;
        }

        // ------------------------------------------------------------ containment & ordering

        private void ResolveOwners()
        {
            // An owner that no longer exists, or a cycle created by two crossing moves, falls back to the base owner (then the root).
            foreach (var m in _out.Values)
            {
                if (m.Owner is null || m.Id == o.RootId) continue;
                // Moved into a container that is gone: back to where it was; new elements there go with the container.
                if (!_out.ContainsKey(m.Owner)) m.Owner = m.BaseOwner != null && _out.ContainsKey(m.BaseOwner) ? m.BaseOwner : null;
            }
            foreach (var m in _out.Values)
            {
                if (m.Owner is null) continue;
                var visited = new HashSet<string>(StringComparer.Ordinal) { m.Id };
                for (var cur = m.Owner; cur != null; cur = _out.GetValueOrDefault(cur)?.Owner)
                {
                    if (visited.Add(cur)) continue;
                    if (cur != m.Id) break;   // a cycle further up; fixed when its own members are visited
                    m.Owner = m.BaseOwner != null && _out.ContainsKey(m.BaseOwner) && m.BaseOwner != m.Id ? m.BaseOwner : o.RootId;
                    break;
                }
            }
        }

        /// <summary>Order of children / shapes: the side that reordered wins (ours if both did); the other side's additions slot in after their predecessor.</summary>
        private static List<string> Order(IReadOnlyList<string> bl, IReadOnlyList<string> ol, IReadOnlyList<string> tl)
        {
            static List<string> Common(IReadOnlyList<string> list, ISet<string> keep) => list.Where(keep.Contains).ToList();
            var all = bl.Concat(ol).Concat(tl).ToHashSet(StringComparer.Ordinal);
            var inBase = bl.ToHashSet(StringComparer.Ordinal);
            var both = ol.Intersect(tl).Intersect(inBase).ToHashSet(StringComparer.Ordinal);
            var oursReordered = !Common(ol, both).SequenceEqual(Common(bl, both));
            var theirsReordered = !Common(tl, both).SequenceEqual(Common(bl, both));
            var (primary, secondary) = !oursReordered && theirsReordered ? (tl, ol) : (ol, tl);
            var result = primary.ToList();
            var placed = result.ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < secondary.Count; i++)
            {
                if (placed.Contains(secondary[i])) continue;
                var at = 0;
                for (var j = i - 1; j >= 0; j--)
                    if (result.IndexOf(secondary[j]) is var p and >= 0) { at = p + 1; break; }
                result.Insert(at, secondary[i]);
                placed.Add(secondary[i]);
            }
            foreach (var id in bl.Where(all.Contains)) if (placed.Add(id)) result.Add(id);
            return result;
        }

        // ------------------------------------------------------------ materialise

        private ModelRepository Build()
        {
            var rootId = o.RootId;
            var childrenOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var m in _out.Values.Where(m => m.Owner != null))
            {
                if (!childrenOf.TryGetValue(m.Owner!, out var list)) childrenOf[m.Owner!] = list = new();
                list.Add(m.Id);
            }
            foreach (var (owner, list) in childrenOf.ToList())
            {
                var set = list.ToHashSet(StringComparer.Ordinal);
                var ordered = Order(b[owner]?.Children ?? [], o[owner]?.Children ?? [], t[owner]?.Children ?? []).Where(set.Contains).ToList();
                var inOrder = ordered.ToHashSet(StringComparer.Ordinal);
                ordered.AddRange(list.Where(x => !inOrder.Contains(x)));
                childrenOf[owner] = ordered;
            }

            // References to used-project elements and missing references are kept (docs/LIBRARIES.md).
            var external = new HashSet<string>(b.External, StringComparer.Ordinal);
            external.UnionWith(o.External);
            external.UnionWith(t.External);
            external.ExceptWith(_out.Keys);
            var stand = new Dictionary<string, Element>(StringComparer.Ordinal);
            Element? Stand(string id) => !external.Contains(id) ? null
                : stand.TryGetValue(id, out var x) ? x : stand[id] = Libraries.MissingReference.Element(id);

            // Reachable from the root; relationships only when both ends survive (repeat until stable, since ends may be relationships).
            var alive = new HashSet<string>(StringComparer.Ordinal);
            bool changed;
            do
            {
                changed = false;
                var stack = new Stack<string>([rootId]);
                var reach = new HashSet<string>(_out.Count, StringComparer.Ordinal);
                while (stack.Count > 0)
                {
                    var id = stack.Pop();
                    if (!_out.TryGetValue(id, out var m) || !reach.Add(id)) continue;
                    if (Metamodel.IsRelationship(m.Kind) &&
                        (!m.Fields.TryGetValue(SourceKey, out var s) || !m.Fields.TryGetValue(TargetKey, out var tg) ||
                         (alive.Count > 0 && ((!alive.Contains(s) && !external.Contains(s)) || (!alive.Contains(tg) && !external.Contains(tg))))
                         || (!_out.ContainsKey(s) && !external.Contains(s)) || (!_out.ContainsKey(tg) && !external.Contains(tg))))
                    { reach.Remove(id); continue; }
                    foreach (var c in childrenOf.GetValueOrDefault(id) ?? []) stack.Push(c);
                }
                if (!reach.SetEquals(alive)) { alive = reach; changed = true; }
            } while (changed);

            var made = new Dictionary<string, Element>(alive.Count, StringComparer.Ordinal);
            foreach (var id in alive)
            {
                var m = _out[id];
                if (Metamodel.IsRelationship(m.Kind)) continue;
                var e = m.Kind == ElementKind.Diagram ? new Diagram(m.DiagramType ?? DiagramType.Package, id) : new Element(m.Kind, id);
                foreach (var (k, v) in m.Fields) e.SetRaw(k, v);
                made[id] = e;
            }
            var pending = alive.Where(id => !made.ContainsKey(id)).ToList();
            while (pending.Count > 0)
            {
                var remaining = new List<string>();
                foreach (var id in pending)
                {
                    var m = _out[id];
                    var s = made.GetValueOrDefault(m.Fields[SourceKey]) ?? Stand(m.Fields[SourceKey]);
                    var tg = made.GetValueOrDefault(m.Fields[TargetKey]) ?? Stand(m.Fields[TargetKey]);
                    if (s is null || tg is null) { remaining.Add(id); continue; }
                    var r = new Relationship(m.Kind, s, tg, id);
                    foreach (var (k, v) in m.Fields) if (k is not (SourceKey or TargetKey)) r.SetRaw(k, v);
                    made[id] = r;
                }
                if (remaining.Count == pending.Count) break;   // ends that never materialise (cyclic relationship chains): drop
                pending = remaining;
            }

            void Link(Element e)
            {
                foreach (var cid in childrenOf.GetValueOrDefault(e.Id) ?? [])
                {
                    if (!made.TryGetValue(cid, out var c)) continue;
                    c.Owner = e;
                    e.ChildList.Add(c);
                    Link(c);
                }
            }
            var root = made[rootId];
            Link(root);
            var model = new ModelRepository(root);

            foreach (var d in model.Diagrams)
            {
                var m = _out[d.Id];
                var nodes = new Dictionary<string, DiagramNode>(StringComparer.Ordinal);
                foreach (var n in m.Nodes)
                {
                    if ((model.Find(n.ElementId) ?? Stand(n.ElementId)) is not { } el) continue;
                    var node = new DiagramNode(el, n.Bounds, n.Id);
                    foreach (var (k, v) in n.Style) node.Style[k] = v;
                    nodes[n.Id] = node;
                    model.AddNodeForLoad(d, node);
                }
                foreach (var n in m.Nodes)
                    if (nodes.TryGetValue(n.Id, out var node) && n.ParentId != null && nodes.ContainsKey(n.ParentId)) node.ParentId = n.ParentId;
                foreach (var x in m.Edges)
                {
                    if (!nodes.TryGetValue(x.SourceNodeId, out var sn) || !nodes.TryGetValue(x.TargetNodeId, out var tn)) continue;
                    Relationship r;
                    if (model.Find(x.RelationshipId) is Relationship known) r = known;
                    else if (external.Contains(x.RelationshipId)) stand[x.RelationshipId] = r = Libraries.MissingReference.Relationship(x.RelationshipId, sn.Element, tn.Element);
                    else continue;
                    var edge = new DiagramEdge(r, sn, tn, x.Id) { Waypoints = x.Waypoints.ToList() };
                    foreach (var (k, v) in x.Style) edge.Style[k] = v;
                    model.AddEdgeForLoad(d, edge);
                }
            }
            foreach (var x in stand.Values) model.RegisterMissing(x);
            return model;
        }
    }
}
