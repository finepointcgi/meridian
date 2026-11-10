using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Web.Services;

/// <summary>A relationship kind that can link every selected element with one target, and which way it points.</summary>
public sealed record BulkLinkOption(ElementKind Kind, bool SelectionIsSource)
{
    public string Keyword => Metamodel.Style(Kind).Keyword is { } kw ? $"«{kw}»" : Metamodel.DisplayName(Kind);
}

/// <summary>Editing several selected shapes and connectors at once: stereotypes, shared fields and bulk linking. Each is one undo step.</summary>
public sealed partial class DiagramController
{
    /// <summary>Relationship kinds offered by "Link to…", in menu order.</summary>
    public static readonly IReadOnlyList<ElementKind> BulkLinkKinds =
    [
        ElementKind.Association, ElementKind.Include, ElementKind.Extend, ElementKind.Satisfy, ElementKind.Verify, ElementKind.Refine,
        ElementKind.Trace, ElementKind.DeriveReqt, ElementKind.Allocate, ElementKind.Dependency, ElementKind.Generalization,
        ElementKind.Composition, ElementKind.Aggregation, ElementKind.Realization,
    ];

    /// <summary>The selected elements that can be linked (the shapes' elements; connectors are left out).</summary>
    public IReadOnlyList<Element> LinkableSelection => _selNodes.Select(n => n.Element)
        .Where(e => e.Kind is not (ElementKind.Shape or ElementKind.Comment) && e is not Meridian.Core.Model.Diagram).Distinct().ToList();

    /// <summary>
    /// Relationship kinds valid between every linkable selected element and <paramref name="target"/>. The selection is the
    /// source when that is valid (use case «include» target, design element «satisfy» requirement), otherwise the target.
    /// </summary>
    public IReadOnlyList<BulkLinkOption> BulkLinkOptions(Element target)
    {
        var sel = LinkableSelection.Where(e => !ReferenceEquals(e, target)).ToList();
        if (sel.Count == 0 || target is Relationship or Meridian.Core.Model.Diagram) return [];
        var options = new List<BulkLinkOption>();
        // Structural relationships make no sense with requirements: offer traceability only (as dropping onto a shape does).
        var requirements = target.Kind == ElementKind.Requirement || sel.Any(e => e.Kind == ElementKind.Requirement);
        foreach (var kind in BulkLinkKinds)
        {
            if (requirements && kind is ElementKind.Association or ElementKind.Composition or ElementKind.Aggregation or ElementKind.Generalization or ElementKind.Realization) continue;
            if (sel.All(e => Metamodel.CheckEnds(kind, e.Kind, target.Kind) == null)) options.Add(new BulkLinkOption(kind, true));
            else if (sel.All(e => Metamodel.CheckEnds(kind, target.Kind, e.Kind) == null)) options.Add(new BulkLinkOption(kind, false));
        }
        // Most fitting first, by the same rules a drawn line uses (use case → actor: association, → use case: «include»,
        // → requirement: «satisfy», test case → requirement: «verify», action → block: «allocate»…).
        var ranked = sel.Select(e => e.Kind).Distinct()
            .Select(k => ConnectorRules.Candidates(Diagram.DiagramType, k, target.Kind)).ToList();
        int Rank(BulkLinkOption o) => ranked.Sum(list =>
        {
            var i = list.ToList().FindIndex(c => c.Kind == o.Kind && c.Reverse != o.SelectionIsSource);
            return i < 0 ? 1000 : i;
        });
        return options.OrderBy(Rank).ToList();   // stable: ties keep the menu order
    }

    /// <summary>The relationship "Link to…" preselects for <paramref name="target"/>: the most fitting valid one.</summary>
    public BulkLinkOption? DefaultBulkLink(Element target) => BulkLinkOptions(target).FirstOrDefault();

    /// <summary>Elements "Link to…" can point at: anything that some relationship can connect to the whole selection.</summary>
    public IEnumerable<Element> BulkLinkCandidates(string? query, bool requirementsOnly = false)
    {
        var sel = LinkableSelection.ToHashSet();
        var q = query?.Trim() ?? "";
        return Model.AllElements
            .Where(x => x is not Relationship && x is not Meridian.Core.Model.Diagram && x.Kind is not (ElementKind.Model or ElementKind.Package or ElementKind.Profile or ElementKind.Shape or ElementKind.Comment) && !sel.Contains(x))
            .Where(x => !requirementsOnly || x.Kind == ElementKind.Requirement)
            .Where(x => q.Length == 0 || x.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) || x.Get(FieldKeys.RequirementId).Contains(q, StringComparison.OrdinalIgnoreCase))
            .Where(x => BulkLinkOptions(x).Count > 0)
            .OrderBy(x => Diagram.Shows(x) ? 0 : 1).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates one <paramref name="kind"/> relationship between every linkable selected element and <paramref name="target"/>,
    /// draws them (showing the target beside the selection if it isn't on the diagram yet), in one undo step.
    /// Pairs that are already linked that way are skipped. Returns the relationships created.
    /// </summary>
    public IReadOnlyList<Relationship> BulkLink(ElementKind kind, Element target, bool? selectionIsSource = null)
    {
        if (ReadOnly) return [];
        var sel = LinkableSelection.Where(e => !ReferenceEquals(e, target)).ToList();
        var option = BulkLinkOptions(target).FirstOrDefault(o => o.Kind == kind && (selectionIsSource == null || o.SelectionIsSource == selectionIsSource));
        if (sel.Count == 0 || option == null)
        {
            _ws.SetStatus($"{Metamodel.DisplayName(kind)} can't link the selection to {target.DisplayName}");
            return [];
        }
        var created = new List<Relationship>();
        try
        {
            using (Model.History.Transaction($"Link {sel.Count} elements {option.Keyword} {target.DisplayName}"))
            {
                if (Diagram.NodeFor(target) == null) PlaceBeside(target, sel);
                foreach (var e in sel)
                {
                    var (s, t) = option.SelectionIsSource ? (e, target) : (target, e);
                    // Already linked that way? Associations have no direction, so either way round counts.
                    var undirected = kind is ElementKind.Association;
                    if (Model.Outgoing(s, kind).Any(r => ReferenceEquals(r.Target, t)) || undirected && Model.Outgoing(t, kind).Any(r => ReferenceEquals(r.Target, s))) continue;
                    var r = Model.Connect(kind, s, t);
                    Model.AddEdge(Diagram, r);
                    created.Add(r);
                }
            }
            var skipped = sel.Count - created.Count;
            _ws.SetStatus($"Linked {created.Count} element{(created.Count == 1 ? "" : "s")} {option.Keyword} {target.DisplayName}" + (skipped > 0 ? $" ({skipped} already linked)" : ""));
        }
        catch (InvalidOperationException ex) { _ws.SetStatus(ex.Message); }
        Notify();
        return created;
    }

    /// <summary>"Trace to requirement…": the fitting link to it (design elements «satisfy», test cases «verify», requirements «deriveReqt»).</summary>
    public IReadOnlyList<Relationship> TraceToRequirement(Element requirement)
    {
        var pick = DefaultBulkLink(requirement);
        return pick == null ? [] : BulkLink(pick.Kind, requirement, pick.SelectionIsSource);
    }

    /// <summary>Shows <paramref name="e"/> to the right of the given elements' shapes, vertically centred on them.</summary>
    private void PlaceBeside(Element e, IReadOnlyList<Element> near)
    {
        var (w, h) = Notation.DefaultSizeOf(e);
        var bs = near.Select(Diagram.NodeFor).OfType<DiagramNode>().Select(BoundsOf).ToList();
        var x = bs.Count > 0 ? bs.Max(b => b.Right) + 160 : NextFreeSpot(w, h).X;
        var y = bs.Count > 0 ? (bs.Min(b => b.Y) + bs.Max(b => b.Bottom)) / 2 - h / 2 : NextFreeSpot(w, h).Y;
        var node = Model.AddNode(Diagram, e, new Bounds(Snap(x), Snap(y), w, h));
        PutOnActiveLayer(node);
    }

    // ---------------------------------------------------------------- stereotypes & shared fields

    /// <summary>Stereotypes applied to every selected element, and those applied to only some.</summary>
    public (IReadOnlyList<string> All, IReadOnlyList<string> Some) SelectionStereotypes()
    {
        var els = SelectedElements;
        if (els.Count == 0) return ([], []);
        var all = els.Select(e => (IEnumerable<string>)e.Stereotypes).Aggregate((a, b) => a.Intersect(b)).ToList();
        var some = els.SelectMany(e => e.Stereotypes).Distinct().Except(all).ToList();
        return (all, some);
    }

    /// <summary>Applies a stereotype to every selected element (one undo step).</summary>
    public void ApplyStereotype(string name) => EditStereotypes(name, add: true);

    /// <summary>Removes a stereotype from every selected element that has it (one undo step).</summary>
    public void RemoveStereotype(string name) => EditStereotypes(name, add: false);

    private void EditStereotypes(string name, bool add)
    {
        name = name.Trim().Trim('«', '»').Trim();
        if (ReadOnly || name.Length == 0) return;
        var els = SelectedElements;
        try
        {
            using (Model.History.Transaction($"{(add ? "Apply" : "Remove")} «{name}» on {els.Count} elements"))
                foreach (var e in els)
                {
                    var current = e.Stereotypes.ToList();
                    if (add == current.Contains(name)) continue;
                    var next = add ? current.Append(name) : current.Where(x => x != name);
                    Model.Set(e, FieldKeys.Stereotypes, string.Join(", ", next));
                }
        }
        catch (InvalidOperationException ex) { _ws.SetStatus(ex.Message); }
    }

    /// <summary>The element kind shared by the whole selection, or null when it mixes kinds.</summary>
    public ElementKind? CommonKind => SelectedElements.Select(e => e.Kind).Distinct().ToList() is [var k] ? k : null;

    /// <summary>Fields every selected element (all of one kind) can edit together. The name is left out: names are per element.</summary>
    public IReadOnlyList<string> SharedFields() =>
        CommonKind is { } k ? Metamodel.EditableFields(k).Where(f => f is not (FieldKeys.Name or FieldKeys.Stereotypes or FieldKeys.RequirementId)).ToList() : [];

    /// <summary>The value a field has on every selected element, or null when they differ ("Mixed").</summary>
    public string? SharedValue(string key) => SelectedElements.Select(e => e.Get(key)).Distinct().ToList() is [var v] ? v : null;

    /// <summary>Sets a field on every selected element (one undo step).</summary>
    public void SetSharedField(string key, string? value)
    {
        if (ReadOnly) return;
        var els = SelectedElements;
        try
        {
            using (Model.History.Transaction($"Edit {els.Count} elements"))
                foreach (var e in els.Where(e => e.Get(key) != (value ?? ""))) Model.Set(e, key, value);
        }
        catch (InvalidOperationException ex) { _ws.SetStatus(ex.Message); }
    }
}
