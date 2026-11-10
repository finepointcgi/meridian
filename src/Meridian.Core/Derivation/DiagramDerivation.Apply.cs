using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Derivation;

public static partial class DiagramDerivation
{
    /// <summary>
    /// The items that will really be applied: ticked ones, minus additions that depend on an addition left out (a flow to an
    /// unticked action) and "only if used" items nothing uses.
    /// </summary>
    public static HashSet<DerivedItem> Effective(DerivationPlan plan)
    {
        var byKey = plan.Items.ToDictionary(i => i.Key);
        var on = plan.Items.Where(i => i.Change is DerivedChange.Same or DerivedChange.Reuse || i.Selected && i.Listed).ToHashSet();
        IEnumerable<string> Refs(DerivedItem i)
        {
            foreach (var r in new[] { i.Owner, i.Source, i.Target })
                if (r is { Key: { } k }) yield return k;
            foreach (var k in i.FieldRefs.Values) yield return k;
        }
        bool Present(string key) => byKey.TryGetValue(key, out var x) && (x.Change is DerivedChange.Same or DerivedChange.Reuse or DerivedChange.Change || x.Change == DerivedChange.Add && on.Contains(x));
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var i in on.Where(i => i.Change == DerivedChange.Add).ToList())
                if (Refs(i).Any(k => !Present(k)))
                {
                    on.Remove(i);
                    changed = true;
                }
            foreach (var i in on.Where(i => i.Change == DerivedChange.Add && i.OnlyIfUsed).ToList())
                if (!on.Any(x => x.Change == DerivedChange.Add && !x.IsDiagram && !ReferenceEquals(x, i) && Refs(x).Contains(i.Key)))
                {
                    on.Remove(i);
                    changed = true;
                }
        }
        // A new diagram whose shapes all dropped out is not worth creating.
        foreach (var d in on.Where(i => i.IsDiagram && i.Change == DerivedChange.Add).ToList())
            if (!on.Any(x => x.Shape?.DiagramKey == d.Key && x.Change == DerivedChange.Add)) on.Remove(d);
        return on;
    }

    /// <summary>
    /// Applies the ticked items of a plan as ONE undo step: removals, new elements, changed fields, new relationships, then the
    /// diagrams (new ones laid out; existing ones only get the new shapes, placed where nothing is, so what the user moved
    /// stays put). Every derived item gets the derivation's metadata. Rolls back completely on failure.
    /// </summary>
    public static DerivationResult Apply(ModelRepository m, DerivationPlan plan)
    {
        var on = Effective(plan);
        var created = new Dictionary<string, Element>();
        var createdIds = new List<string>();
        var changedIds = new List<string>();
        var removedIds = new List<string>();
        var diagrams = new List<Diagram>();
        var description = plan.IsUpdate ? $"Update {DerivationKinds.Product(plan.Kind)} from {plan.Source.DisplayName}" : $"{DerivationKinds.Label(plan.Kind)} {plan.Source.DisplayName}";
        Element Resolve(ItemRef r) => r.Element ?? (created.TryGetValue(r.Key!, out var e) ? e : throw new KeyNotFoundException($"Planned item {r.Key} was not created"));
        try
        {
            using (m.History.Transaction(description))
            {
                foreach (var i in plan.Items.Where(i => i.Existing != null && i.Change is DerivedChange.Same or DerivedChange.Change or DerivedChange.Reuse))
                    created[i.Key] = i.Existing!;

                // 1. Removals (relationships first, so cascades don't hit them twice).
                foreach (var i in plan.Items.Where(i => i.Change == DerivedChange.Remove && on.Contains(i)).OrderBy(i => i.IsRelationship ? 0 : 1))
                    if (i.Existing is { } e && m.Contains(e))
                    {
                        removedIds.Add(e.Id);
                        m.Delete(e);
                    }

                // 2. New elements, in plan order (owners first).
                foreach (var i in plan.Items.Where(i => i.Change == DerivedChange.Add && on.Contains(i) && !i.IsRelationship && !i.IsDiagram))
                {
                    var owner = Resolve(i.Owner ?? m.Root);
                    var e = m.Create(owner, i.Kind, i.Fields.TryGetValue(FieldKeys.Name, out var n) && n.Length > 0 ? n : null);
                    created[i.Key] = e;
                    createdIds.Add(e.Id);
                }
                foreach (var i in plan.Items.Where(i => i.Change == DerivedChange.Add && on.Contains(i) && !i.IsRelationship && !i.IsDiagram))
                    Write(m, created[i.Key], i, plan, Resolve);

                // 3. Changes to earlier derived elements.
                foreach (var i in plan.Items.Where(i => i.Change == DerivedChange.Change && on.Contains(i)))
                {
                    Write(m, i.Existing!, i, plan, Resolve);
                    changedIds.Add(i.Existing!.Id);
                }

                // 4. New relationships.
                foreach (var i in plan.Items.Where(i => i.Change == DerivedChange.Add && on.Contains(i) && i.IsRelationship))
                {
                    var r = m.Connect(i.Kind, Resolve(i.Source!.Value), Resolve(i.Target!.Value), i.Owner is { } o ? Resolve(o) : null);
                    created[i.Key] = r;
                    createdIds.Add(r.Id);
                    Write(m, r, i, plan, Resolve);
                }

                // 5. Diagrams.
                foreach (var d in plan.Items.Where(i => i.IsDiagram && on.Contains(i) && i.Change != DerivedChange.Remove))
                {
                    var shapes = plan.Items.Where(x => x.Shape?.DiagramKey == d.Key && x.Change != DerivedChange.Remove && created.ContainsKey(x.Key)).ToList();
                    Diagram diagram;
                    if (d.Change == DerivedChange.Add)
                    {
                        diagram = Create(m, d, shapes, plan, on, Resolve, created);
                        created[d.Key] = diagram;
                        createdIds.Add(diagram.Id);
                        Stamp(m, diagram, d, plan);
                    }
                    else
                    {
                        diagram = (Diagram)d.Existing!;
                        AddNewShapes(m, diagram, shapes.Where(x => x.Change == DerivedChange.Add).ToList(), plan, on, created);
                    }
                    diagrams.Add(diagram);
                }

                // 6. Everything derived now carries the current source fingerprint.
                foreach (var i in plan.Items.Where(i => on.Contains(i) && i.Change is DerivedChange.Same or DerivedChange.Change && i.Existing != null && m.Contains(i.Existing)))
                {
                    var e = i.Existing!;
                    var unedited = !DeriveFields.IsEdited(e);
                    m.Set(e, DeriveFields.Fingerprint, plan.Fingerprint);
                    if (unedited) m.Set(e, DeriveFields.ContentHash, DeriveFields.ContentOf(e));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            if (m.History.UndoDescription == description && createdIds.Count + changedIds.Count + removedIds.Count > 0) m.History.Undo();
            throw;
        }
        var added = plan.Items.Count(i => i.Change == DerivedChange.Add && on.Contains(i) && !i.IsDiagram);
        return new DerivationResult(plan, diagrams.Where(x => m.Contains(x)).ToList(), added, changedIds.Count, removedIds.Count, createdIds, changedIds, removedIds);
    }

    /// <summary>Writes the planned fields (references resolved) and the derivation metadata.</summary>
    private static void Write(ModelRepository m, Element e, DerivedItem i, DerivationPlan plan, Func<ItemRef, Element> resolve)
    {
        foreach (var (k, v) in i.Fields)
            if (!(k == FieldKeys.Name && e.Name == v)) m.Set(e, k, v);
        foreach (var (k, key) in i.FieldRefs) m.Set(e, k, resolve(ItemRef.To(key)).Id);
        Stamp(m, e, i, plan);
    }

    private static void Stamp(ModelRepository m, Element e, DerivedItem i, DerivationPlan plan)
    {
        m.Set(e, DeriveFields.Source, plan.Source.Id);
        m.Set(e, DeriveFields.Kind, DerivationKinds.Id(plan.Kind));
        m.Set(e, DeriveFields.Key, i.Key);
        m.Set(e, DeriveFields.Target, plan.Target?.Id);
        m.Set(e, DeriveFields.Fingerprint, plan.Fingerprint);
        m.Set(e, DeriveFields.ContentHash, DeriveFields.ContentOf(e));
    }

    private static Diagram Create(ModelRepository m, DerivedItem d, List<DerivedItem> shapes, DerivationPlan plan, HashSet<DerivedItem> on,
                                  Func<ItemRef, Element> resolve, Dictionary<string, Element> created)
    {
        var owner = resolve(d.Owner!.Value);
        var purpose = d.Fields.GetValueOrDefault(FieldKeys.Documentation, "");
        if (d.AutoLayout)
        {
            var elements = shapes.Where(x => x.Shape!.ParentKey == null).Select(x => created[x.Key]).ToList();
            return AutoDiagrammer.Build(m, new PlannedDiagram { Type = d.DiagramType!.Value, Owner = owner, Name = d.Name, Purpose = purpose, Elements = elements });
        }
        var diagram = m.CreateDiagram(owner, d.DiagramType!.Value, d.Name);
        if (purpose.Length > 0) m.Set(diagram, FieldKeys.Documentation, purpose);
        var nodes = new Dictionary<string, DiagramNode>();
        foreach (var s in shapes)
        {
            var e = created[s.Key];
            var b = s.Shape!.Bounds ?? Fallback(diagram, e);
            var parent = s.Shape.ParentKey is { } pk && nodes.TryGetValue(pk, out var pn) ? pn : null;
            nodes[s.Key] = m.AddNode(diagram, e, b, showRelationships: false, parent);
            Separate(m, diagram, nodes[s.Key], s, created);
        }
        DrawEdges(m, diagram, plan.Items.Where(x => x.IsRelationship && created.ContainsKey(x.Key) && x.Change != DerivedChange.Remove), created, null);
        EdgeRouter.Invalidate(diagram);
        return diagram;
    }

    /// <summary>Paths for the plan's relationships whose ends are shown; messages at their planned (or next free) height.</summary>
    private static void DrawEdges(ModelRepository m, Diagram d, IEnumerable<DerivedItem> rels, Dictionary<string, Element> created, double? nextMessageY)
    {
        foreach (var r in rels)
        {
            if (created[r.Key] is not Relationship rel || !AutoDiagrammer.EdgeAllowed(d.DiagramType, rel)) continue;
            if (d.EdgeFor(rel) != null) continue;
            var edge = m.AddEdge(d, rel);
            if (edge == null || rel.Kind != ElementKind.Message) continue;
            var y = nextMessageY ?? r.MessageY ?? 100;
            if (nextMessageY != null) nextMessageY += ActivityToSequence.MessageGap;
            m.SetWaypoints(d, edge, [new Pt(0, y)]);
        }
    }

    /// <summary>Stores where each later operand of a planned fragment starts (the separators of its shape).</summary>
    private static void Separate(ModelRepository m, Diagram d, DiagramNode node, DerivedItem item, Dictionary<string, Element> created)
    {
        foreach (var (key, offset) in item.Separators ?? [])
            if (created.TryGetValue(key, out var operand))
                m.SetNodeStyle(d, node, InteractionOperands.StyleKey(operand), InteractionOperands.Format(offset));
    }

    private static Bounds Fallback(Diagram d, Element e)
    {
        var (w, h) = Notation.DefaultSizeOf(e);
        var bottom = d.Nodes.Count == 0 ? 20 : d.Nodes.Where(n => n.ParentId == null).DefaultIfEmpty().Max(n => n?.Bounds.Bottom ?? 20);
        return new Bounds(40, bottom + 30, w, h);
    }

    /// <summary>
    /// Update: draws only the new elements. Existing shapes are never moved (lanes may grow to hold new actions). A new shape
    /// goes to its planned place when that is free, else below what is there.
    /// </summary>
    private static void AddNewShapes(ModelRepository m, Diagram d, List<DerivedItem> fresh, DerivationPlan plan, HashSet<DerivedItem> on,
                                     Dictionary<string, Element> created)
    {
        var added = new List<DiagramNode>();
        var autoX = 40.0;
        var autoY = (d.Nodes.Count == 0 ? 0 : d.Nodes.Max(n => n.Bounds.Bottom)) + 40;
        foreach (var s in fresh)
        {
            var e = created[s.Key];
            if (d.NodeFor(e) != null) continue;
            DiagramNode? parent = null;
            if (s.Shape!.ParentKey is { } pk && created.TryGetValue(pk, out var pe)) parent = d.NodeFor(pe);
            Bounds b;
            if (s.Shape.Bounds is { } planned)
            {
                b = planned;
                if (parent != null && !planned.Intersects(parent.Bounds))
                {
                    // The lane moved: keep the shape's place relative to it.
                    var laneItem = plan[s.Shape.ParentKey!];
                    if (laneItem?.Shape?.Bounds is { } lb) b = planned.Offset(parent.Bounds.X - lb.X, parent.Bounds.Y - lb.Y);
                }
                b = FreeSpot(d, b, parent);
            }
            else
            {
                var (w, h) = Notation.DefaultSizeOf(e);
                if (e.Kind is ElementKind.Property && d.DiagramType == DiagramType.InternalBlock) (w, h) = (170, 70);
                b = new Bounds(autoX, autoY, w, h);
                autoX += w + 40;
            }
            var node = m.AddNode(d, e, b, showRelationships: false, parent);
            Separate(m, d, node, s, created);
            added.Add(node);
        }
        if (added.Count > 0)
        {
            var grow = ContainerLayout.GrowToFit(d, added, n => n.Bounds);
            if (grow.Count > 0) m.SetBounds(d, grow, "Grow to fit");
        }
        var nextY = d.DiagramType == DiagramType.Sequence
            ? d.Edges.Where(x => x.Relationship.Kind == ElementKind.Message).Select(x => Notation.MessageY(x)).DefaultIfEmpty(ActivityToSequence.FirstMessageY - ActivityToSequence.MessageGap).Max() + ActivityToSequence.MessageGap
            : (double?)null;
        DrawEdges(m, d, plan.Items.Where(x => x.IsRelationship && x.Change == DerivedChange.Add && on.Contains(x) && created.ContainsKey(x.Key)), created, nextY);
        EdgeRouter.Invalidate(d);
    }

    /// <summary><paramref name="b"/>, moved down until it overlaps no shape other than its container (and the container's ancestors).</summary>
    private static Bounds FreeSpot(Diagram d, Bounds b, DiagramNode? parent)
    {
        var containers = new HashSet<DiagramNode>();
        for (var p = parent; p != null; p = d.ParentOf(p)) containers.Add(p);
        var others = d.Nodes.Where(n => !containers.Contains(n) && !ContainerLayout.IsContainer(d, n) && n.Element.Kind is not (ElementKind.Partition or ElementKind.Lifeline or ElementKind.CombinedFragment)).ToList();
        for (var guard = 0; guard < 200; guard++)
        {
            var hit = others.FirstOrDefault(n => n.Bounds.Intersects(b));
            if (hit == null) return b;
            b = b with { Y = Math.Ceiling((hit.Bounds.Bottom + 24) / 10) * 10 };
        }
        return b;
    }
}
