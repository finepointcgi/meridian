using Meridian.Core.Model;

namespace Meridian.Core.Language;

public enum AutoDiagramStatus { New, Update }

/// <summary>What <see cref="AutoDiagrammer.Plan"/> looks at and how.</summary>
public sealed class AutoDiagramOptions
{
    /// <summary>A package (or the model root) to diagram; null = the whole model.</summary>
    public Element? Scope { get; init; }

    /// <summary>Also diagram elements already shown on a diagram of the same kind, and rebuild Meridian's own auto diagrams.</summary>
    public bool Regenerate { get; init; }

    /// <summary>Add «use» dependencies between packages whose elements reference each other (shown on the package overview).</summary>
    public bool DerivePackageDependencies { get; init; } = true;

    /// <summary>Largest diagram, in shapes; bigger groups are split into several diagrams.</summary>
    public int MaxNodes { get; init; } = AutoDiagrammer.DefaultMaxNodes;
}

/// <summary>One diagram the auto-diagrammer proposes to create (or to rebuild).</summary>
public sealed class PlannedDiagram
{
    public required DiagramType Type { get; init; }
    public required string Name { get; set; }
    public required Element Owner { get; init; }

    /// <summary>The elements to show. Nesting (packages, composite states), ports and nested parts are added by the engine.</summary>
    public required IReadOnlyList<Element> Elements { get; init; }

    /// <summary>An auto-generated diagram this plan rebuilds; null for a new diagram.</summary>
    public Diagram? Existing { get; init; }

    /// <summary>One line saying what the view is for; stored as the diagram's documentation (shown above it in reports).</summary>
    public string Purpose { get; set; } = "";

    /// <summary>Marks diagrams made by the auto-diagrammer (so later runs update rather than duplicate them).</summary>
    public string? Key { get; init; }

    /// <summary>Shapes the diagram will have (elements, ports, nested parts).</summary>
    public int NodeCount { get; internal set; }

    /// <summary>Run the layers of hierarchies and flows left to right instead of top to bottom (e.g. Mermaid "LR").</summary>
    public bool LeftToRight { get; init; }

    public bool Selected { get; set; } = true;
    public AutoDiagramStatus Status => Existing == null ? AutoDiagramStatus.New : AutoDiagramStatus.Update;
}

public sealed record PlannedDependency(Element Client, Element Supplier);

public sealed class AutoDiagramPlan
{
    public List<PlannedDiagram> Diagrams { get; } = new();

    /// <summary>«use» dependencies to add between packages (created only when a package diagram is created).</summary>
    public List<PlannedDependency> Dependencies { get; } = new();

    public bool IsEmpty => Diagrams.Count == 0;
}

public sealed record AutoDiagramResult(IReadOnlyList<Diagram> Created, IReadOnlyList<Diagram> Updated, int Shapes, int Dependencies)
{
    public IEnumerable<Diagram> All => Created.Concat(Updated);
    public Diagram? Overview => All.FirstOrDefault(d => d.DiagramType == DiagramType.Package);
    public int Count => Created.Count + Updated.Count;

    public string Summary =>
        Count == 0 ? "Nothing to diagram: every element is already on a diagram" :
        $"Auto-diagram: {Plural(Created.Count, "new diagram")}" + (Updated.Count > 0 ? $", {Updated.Count} updated" : "") +
        $" ({Plural(Shapes, "shape")}" + (Dependencies > 0 ? $", {Plural(Dependencies, "package dependency", "package dependencies")}" : "") + ") — ⌘Z undoes it";

    private static string Plural(int n, string one, string? many = null) => $"{n} {(n == 1 ? one : many ?? one + "s")}";
}

/// <summary>
/// Deterministic "diagram it all out": analyses the model and plans a set of readable diagrams (a package overview,
/// a BDD per package with blocks, an IBD per block with parts, a requirement diagram per requirements package, a
/// diagram per behavior that has none, use case diagrams), then builds them with a tidy layout: layered for
/// hierarchies and flows, a grid for loose sets, nesting for packages / composite states / IBD parts, ports on the
/// side facing their connections, no overlaps, orthogonal connector routing. Works without any AI; the assistant reuses
/// <see cref="Build"/> for the diagrams it proposes.
/// </summary>
public static class AutoDiagrammer
{
    public const int DefaultMaxNodes = 25;

    /// <summary>Diagram field marking a diagram made by the auto-diagrammer; the value groups it (type and owner).</summary>
    public const string MarkerKey = "autoDiagram";

    /// <summary>The package report templates are stored in (infrastructure, never diagrammed).</summary>
    private const string ReportTemplatesPackage = "Report Templates";

    /// <summary>Diagram types <see cref="Build"/> lays out well.</summary>
    public static readonly IReadOnlyList<DiagramType> SupportedTypes =
    [
        DiagramType.Package, DiagramType.BlockDefinition, DiagramType.Class, DiagramType.InternalBlock, DiagramType.Requirement,
        DiagramType.Activity, DiagramType.StateMachine, DiagramType.UseCase,
    ];

    private static readonly ElementKind[] BddKinds = [ElementKind.Block, ElementKind.InterfaceBlock, ElementKind.ValueType, ElementKind.ConstraintBlock, ElementKind.Enumeration];

    private static readonly ElementKind[] TraceKinds = [ElementKind.Satisfy, ElementKind.Verify, ElementKind.Refine, ElementKind.DeriveReqt, ElementKind.Trace, ElementKind.Copy];

    private static readonly ElementKind[] ReferenceKinds =
    [
        ElementKind.Generalization, ElementKind.Realization, ElementKind.Association, ElementKind.Composition, ElementKind.Aggregation,
        ElementKind.Satisfy, ElementKind.Verify, ElementKind.Refine, ElementKind.Trace, ElementKind.DeriveReqt, ElementKind.Copy,
        ElementKind.Allocate, ElementKind.Include, ElementKind.Extend, ElementKind.Dependency,
    ];

    // ================================================================== planning

    public static AutoDiagramPlan Plan(ModelRepository m, AutoDiagramOptions? options = null)
    {
        var o = options ?? new AutoDiagramOptions();
        var scope = o.Scope is { } s && m.Contains(s) && s.Kind is ElementKind.Package or ElementKind.Model ? s : m.Root;
        var cap = Math.Max(4, o.MaxNodes);
        var ctx = new PlanContext(m, o, scope, cap);
        var plan = new AutoDiagramPlan();

        if (o.DerivePackageDependencies) plan.Dependencies.AddRange(DeriveDependencies(m, scope));
        PlanPackages(ctx, plan, scope, scope.Kind == ElementKind.Model ? "Package overview" : $"{scope.Name} – package overview", plan.Dependencies);

        var content = scope.Descendants().Prepend(scope).Where(e => e is not Relationship && e is not Diagram && !Excluded(e)).ToList();

        // A BDD per package holding blocks.
        foreach (var g in content.Where(e => BddKinds.Contains(e.Kind)).GroupBy(NearestPackage).Where(g => g.Key != null))
        {
            var pkg = g.Key!;
            ctx.PlanGroup(plan, DiagramType.BlockDefinition, pkg, pkg.Kind == ElementKind.Model ? "Structure overview" : $"{pkg.Name} – structure",
                $"The blocks and value types of {pkg.DisplayName} and how they relate: generalization, composition and association.",
                g.ToList(), whole: false);
        }

        // An IBD per block with parts.
        foreach (var block in content.Where(e => e.Kind == ElementKind.Block))
        {
            var parts = Parts(m, block).ToList();
            if (parts.Count == 0) continue;
            ctx.PlanGroup(plan, DiagramType.InternalBlock, block, $"{block.Name} – internal structure",
                $"How the parts of {block.DisplayName} are connected through their ports.", parts.Concat(BoundaryPorts(m, block)).ToList(), whole: true,
                isPrimary: e => e.Kind == ElementKind.Property);
        }

        // A requirement diagram per requirements package.
        foreach (var g in content.Where(e => e.Kind == ElementKind.Requirement).GroupBy(NearestPackage).Where(g => g.Key != null))
        {
            var pkg = g.Key!;
            ctx.PlanGroup(plan, DiagramType.Requirement, pkg, $"{(pkg.Kind == ElementKind.Model ? "Model" : pkg.Name)} – requirements",
                $"The requirements of {pkg.DisplayName} and the design elements and tests that satisfy, verify or refine them.",
                g.ToList(), whole: false, context: reqs => RequirementContext(m, reqs));
        }

        // An activity / state machine diagram per behavior that has nodes but no diagram.
        foreach (var b in content.Where(e => e.Kind is ElementKind.Activity or ElementKind.StateMachine))
        {
            var activity = b.Kind == ElementKind.Activity;
            var nodes = BehaviorNodes(b).ToList();
            if (nodes.Count == 0) continue;
            ctx.PlanGroup(plan, activity ? DiagramType.Activity : DiagramType.StateMachine, b, $"{b.Name} – {(activity ? "flow" : "states")}",
                activity ? $"The flow of {b.DisplayName}: its actions, decisions and the order they run in." : $"The states of {b.DisplayName} and the transitions between them.",
                nodes, whole: true);
        }

        // Use case diagrams.
        foreach (var g in content.Where(e => e.Kind is ElementKind.Actor or ElementKind.UseCase).GroupBy(NearestPackage).Where(g => g.Key != null))
        {
            var pkg = g.Key!;
            if (!g.Any(e => e.Kind == ElementKind.UseCase)) continue;   // actors alone tell no story
            ctx.PlanGroup(plan, DiagramType.UseCase, pkg, $"{(pkg.Kind == ElementKind.Model ? "Model" : pkg.Name)} – use cases",
                $"Who uses the system and for what ({pkg.DisplayName}).", g.ToList(), whole: false, context: items => UseCaseContext(m, items));
        }

        foreach (var p in plan.Diagrams) p.NodeCount = Count(Collect(m, p.Type, p.Owner, p.Elements));
        if (!plan.Diagrams.Any(d => d.Type == DiagramType.Package)) plan.Dependencies.Clear();
        return plan;
    }

    private sealed class PlanContext(ModelRepository m, AutoDiagramOptions o, Element scope, int cap)
    {
        public ModelRepository M => m;
        public AutoDiagramOptions O => o;
        public Element Scope => scope;
        public int Cap => cap;
        private readonly Dictionary<DiagramType, HashSet<Element>> _shown = new();

        public bool Shown(DiagramType t, Element e)
        {
            if (!_shown.TryGetValue(t, out var set))
                _shown[t] = set = m.Diagrams.Where(d => d.DiagramType == t).SelectMany(d => d.Nodes.Select(n => n.Element)).ToHashSet();
            return set.Contains(e);
        }

        /// <summary>
        /// Plans the diagrams of one group (a type for one owner). <paramref name="whole"/>: the diagram always shows the
        /// whole group (behaviors, IBDs) rather than only what no diagram shows yet.
        /// </summary>
        public void PlanGroup(AutoDiagramPlan plan, DiagramType type, Element owner, string name, string purpose, List<Element> items, bool whole,
                              Func<List<Element>, List<Element>>? context = null, Func<Element, bool>? isPrimary = null)
        {
            if (!Metamodel.IsValidDiagramContext(type, owner.Kind)) return;
            isPrimary ??= _ => true;
            var key = KeyFor(type, owner);
            var autos = owner.OwnedElements.OfType<Diagram>().Where(d => d.DiagramType == type && d.Get(MarkerKey) == key).ToList();
            var fresh = items.Where(e => isPrimary(e) && (o.Regenerate || !Shown(type, e))).ToList();
            if (fresh.Count == 0) return;
            context ??= _ => [];

            if (whole)
            {
                foreach (var (chunk, i, n) in Chunk(items, context, cap))
                    Add(plan, type, owner, Suffix(name, i, n), purpose, chunk, autos.ElementAtOrDefault(i), key);
                return;
            }

            if (!o.Regenerate && autos.Count > 0)
            {
                // New elements join the last auto diagram of the group while it has room.
                var target = autos[^1];
                var current = items.Where(target.Shows).ToList();
                var merged = current.Concat(fresh).Distinct().ToList();
                if (merged.Count + context(merged).Count <= cap)
                {
                    Add(plan, type, owner, target.Name, purpose, merged, target, key);
                    return;
                }
                var extra = Chunk(fresh, context, cap).ToList();
                foreach (var (chunk, i, _) in extra) Add(plan, type, owner, $"{name} ({autos.Count + i + 1})", purpose, chunk, null, key);
                return;
            }

            foreach (var (chunk, i, n) in Chunk(fresh, context, cap))
                Add(plan, type, owner, Suffix(name, i, n), purpose, chunk, o.Regenerate ? autos.ElementAtOrDefault(i) : null, key);
        }

        private static void Add(AutoDiagramPlan plan, DiagramType type, Element owner, string name, string purpose, List<Element> primaries, Diagram? existing, string key) =>
            plan.Diagrams.Add(new PlannedDiagram { Type = type, Owner = owner, Name = name, Purpose = purpose, Elements = primaries, Existing = existing, Key = key });
    }

    private static string Suffix(string name, int i, int n) => n > 1 ? $"{name} ({i + 1}/{n})" : name;

    public static string KeyFor(DiagramType type, Element owner) => $"{Metamodel.DiagramAbbreviation(type)}:{owner.Id}";

    /// <summary>Splits primaries into groups whose shapes (with their context) stay within the cap; context is added to each group.</summary>
    private static IEnumerable<(List<Element> Items, int Index, int Count)> Chunk(List<Element> primaries, Func<List<Element>, List<Element>> context, int cap)
    {
        var chunks = new List<List<Element>>();
        var cur = new List<Element>();
        foreach (var e in primaries)
        {
            var trial = cur.Append(e).ToList();
            if (cur.Count > 0 && trial.Count + context(trial).Count > cap)
            {
                chunks.Add(cur);
                cur = [e];
            }
            else cur = trial;
        }
        if (cur.Count > 0) chunks.Add(cur);
        for (var i = 0; i < chunks.Count; i++)
        {
            var ctx = context(chunks[i]).Where(c => !chunks[i].Contains(c));
            yield return (chunks[i].Concat(ctx).ToList(), i, chunks.Count);
        }
    }

    /// <summary>
    /// Package overviews: every package under <paramref name="root"/>, nested, down to the depth that fits the cap.
    /// Packages below that depth get an overview of their own, so every package ends up on a package diagram.
    /// </summary>
    private static void PlanPackages(PlanContext ctx, AutoDiagramPlan plan, Element root, string name, List<PlannedDependency> deps)
    {
        var all = PackagesUnder(root).ToList();
        if (all.Count == 0) return;
        int Depth(Element p) => p.AncestorsAndSelf().TakeWhile(a => !ReferenceEquals(a, root)).Count();   // 1 = direct child
        var maxDepth = all.Max(Depth);
        var depth = 1;
        while (depth < maxDepth && all.Count(p => Depth(p) <= depth + 1) <= ctx.Cap) depth++;
        var shown = all.Where(p => Depth(p) <= depth).ToList();
        var key = KeyFor(DiagramType.Package, root);
        var autos = root.OwnedElements.OfType<Diagram>().Where(d => d.DiagramType == DiagramType.Package && d.Get(MarkerKey) == key).ToList();
        var newDeps = deps.Any(x => shown.Contains(x.Client) && shown.Contains(x.Supplier));
        var trigger = ctx.O.Regenerate || newDeps || shown.Any(p => !ctx.Shown(DiagramType.Package, p));
        const string purpose = "How the model is organised into packages and how the packages depend on each other.";
        if (trigger)
        {
            var chunks = shown.Count <= ctx.Cap ? [shown] : shown.Chunk(ctx.Cap).Select(c => c.ToList()).ToList();
            for (var i = 0; i < chunks.Count; i++)
                AddOverview(plan, root, Suffix(name, i, chunks.Count), purpose, chunks[i], autos.ElementAtOrDefault(i), key);
        }
        foreach (var p in shown.Where(p => Depth(p) == depth && PackagesUnder(p).Any()))
            PlanPackages(ctx, plan, p, $"{p.Name} – packages", deps);
    }

    private static void AddOverview(AutoDiagramPlan plan, Element owner, string name, string purpose, List<Element> items, Diagram? existing, string key) =>
        plan.Diagrams.Add(new PlannedDiagram { Type = DiagramType.Package, Owner = owner, Name = name, Purpose = purpose, Elements = items, Existing = existing, Key = key });

    private static IEnumerable<Element> PackagesUnder(Element root)
    {
        foreach (var c in root.OwnedElements)
        {
            if (c.Kind == ElementKind.Package && !Excluded(c))
            {
                yield return c;
                foreach (var d in PackagesUnder(c)) yield return d;
            }
            else if (c.Kind == ElementKind.Profile) yield return c;   // shown as a folder; its stereotypes are not packages
        }
    }

    /// <summary>Inside a profile, or the report templates package: infrastructure that is never diagrammed.</summary>
    private static bool Excluded(Element e) => e.AncestorsAndSelf().Any(a =>
        a.Kind == ElementKind.Profile && !ReferenceEquals(a, e) || a.Kind == ElementKind.Package && a.Name == ReportTemplatesPackage && a.Owner?.Kind == ElementKind.Model);

    public static Element? NearestPackage(Element e) =>
        e.AncestorsAndSelf().Skip(1).FirstOrDefault(a => a.Kind is ElementKind.Package or ElementKind.Model or ElementKind.Profile);

    /// <summary>Part properties of a block: properties typed by a block (not value or constraint properties).</summary>
    public static IEnumerable<Element> Parts(ModelRepository m, Element block) =>
        block.OwnedElements.Where(p => p.Kind == ElementKind.Property && m.ResolveType(p) is { Kind: ElementKind.Block or ElementKind.Component or ElementKind.Class });

    /// <summary>The block's own ports that its connectors use (drawn as boundary ports on its IBD).</summary>
    private static IEnumerable<Element> BoundaryPorts(ModelRepository m, Element block) =>
        block.OwnedElements.Where(p => p.Kind == ElementKind.Port && m.RelationshipsOf(p).Any(r => r.Kind == ElementKind.Connector));

    private static IEnumerable<Element> BehaviorNodes(Element behavior) => behavior.Kind == ElementKind.Activity
        ? behavior.OwnedElements.Where(n => Metamodel.IsActivityNode(n.Kind) && !Metamodel.IsPin(n.Kind) && n.Kind != ElementKind.Partition)
        : behavior.Descendants().Where(n => Metamodel.IsVertex(n.Kind));

    private static List<Element> RequirementContext(ModelRepository m, List<Element> reqs) =>
        reqs.SelectMany(r => m.RelationshipsOf(r).Where(x => TraceKinds.Contains(x.Kind)).Select(x => ReferenceEquals(x.Source, r) ? x.Target : x.Source))
            .Where(e => !reqs.Contains(e) && e is not Relationship && e is not Diagram).Distinct().ToList();

    private static List<Element> UseCaseContext(ModelRepository m, List<Element> items) =>
        items.Where(e => e.Kind == ElementKind.UseCase)
             .SelectMany(u => m.RelationshipsOf(u).Where(x => x.Kind is ElementKind.Association or ElementKind.Include or ElementKind.Extend)
                                                  .Select(x => ReferenceEquals(x.Source, u) ? x.Target : x.Source))
             .Where(e => e.Kind is ElementKind.Actor or ElementKind.UseCase && !items.Contains(e)).Distinct().ToList();

    /// <summary>
    /// «use» dependencies implied by references between packages (typed features, trace links, generalizations…), lifted to
    /// the two sibling packages under the packages' common owner. Existing package dependencies are not repeated.
    /// </summary>
    public static List<PlannedDependency> DeriveDependencies(ModelRepository m, Element? scope = null)
    {
        scope ??= m.Root;
        var found = new HashSet<(Element, Element)>();
        bool InScope(Element e) => ReferenceEquals(scope, e) || scope.IsAncestorOf(e);

        void Note(Element a, Element b)
        {
            if (NearestPackage(a) is not { } pa || NearestPackage(b) is not { } pb || ReferenceEquals(pa, pb)) return;
            if (pa.IsAncestorOf(pb) || pb.IsAncestorOf(pa)) return;
            var chainA = pa.AncestorsAndSelf().Reverse().ToList();
            var chainB = pb.AncestorsAndSelf().Reverse().ToList();
            var i = 0;
            while (i < chainA.Count && i < chainB.Count && ReferenceEquals(chainA[i], chainB[i])) i++;
            if (i >= chainA.Count || i >= chainB.Count) return;
            var (ca, cb) = (chainA[i], chainB[i]);
            if (ca.Kind != ElementKind.Package || cb.Kind != ElementKind.Package || Excluded(ca) || Excluded(cb)) return;
            if (!InScope(ca) || !InScope(cb) || ReferenceEquals(ca, scope) || ReferenceEquals(cb, scope)) return;
            found.Add((ca, cb));
        }

        foreach (var e in scope.Descendants())
        {
            if (e is Relationship r)
            {
                if (ReferenceKinds.Contains(r.Kind) && !(r.Source.Kind == ElementKind.Package && r.Target.Kind == ElementKind.Package)) Note(r.Source, r.Target);
            }
            else if (Metamodel.IsTypedFeature(e.Kind) && m.ResolveType(e) is { } t) Note(e, t);
        }
        // Relationships owned outside the scope but starting inside it.
        foreach (var r in m.Relationships.Where(r => ReferenceKinds.Contains(r.Kind) && InScope(r.Source) && !InScope(r)))
            Note(r.Source, r.Target);

        return found.Where(p => !m.Outgoing(p.Item1, ElementKind.Dependency).Any(d => ReferenceEquals(d.Target, p.Item2)))
                    .OrderBy(p => p.Item1.QualifiedName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Item2.QualifiedName, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new PlannedDependency(p.Item1, p.Item2)).ToList();
    }

    // ================================================================== content (also used to validate assistant proposals)

    /// <summary>What a diagram of this type for this owner would show by default (the assistant's create_diagrams without element ids).</summary>
    public static List<Element> DefaultContent(ModelRepository m, DiagramType type, Element owner)
    {
        bool Mine(Element e) => ReferenceEquals(NearestPackage(e), owner) && !Excluded(e);
        return type switch
        {
            DiagramType.Package => PackagesUnder(owner).ToList(),
            DiagramType.BlockDefinition or DiagramType.Class => owner.Descendants().Where(e => (BddKinds.Contains(e.Kind) || e.Kind is ElementKind.Class or ElementKind.Interface or ElementKind.DataType or ElementKind.Component) && Mine(e)).ToList(),
            DiagramType.InternalBlock => Parts(m, owner).Concat(BoundaryPorts(m, owner)).ToList(),
            DiagramType.Requirement => owner.Descendants().Where(e => e.Kind == ElementKind.Requirement && Mine(e)).ToList() is var reqs ? reqs.Concat(RequirementContext(m, reqs)).ToList() : [],
            DiagramType.Activity or DiagramType.StateMachine => BehaviorNodes(owner).ToList(),
            DiagramType.UseCase => owner.Descendants().Where(e => e.Kind is ElementKind.Actor or ElementKind.UseCase && Mine(e)).ToList() is var uc ? uc.Concat(UseCaseContext(m, uc)).ToList() : [],
            _ => [],
        };
    }

    /// <summary>Null when <paramref name="e"/> can be a shape on a <paramref name="type"/> diagram owned by <paramref name="owner"/>, else why not.</summary>
    public static string? CheckShape(DiagramType type, Element owner, Element e)
    {
        if (e is Relationship || e is Diagram || e.Kind == ElementKind.Model) return $"{e.DisplayName} cannot be a shape; relationships are drawn automatically.";
        if (e.Kind == ElementKind.Comment) return null;
        return type switch
        {
            DiagramType.InternalBlock => e.Kind is ElementKind.Property or ElementKind.Port && ReferenceEquals(e.Owner, owner) ? null
                : $"An internal block diagram of {owner.DisplayName} shows its own parts (and ports); {e.DisplayName} is not one of them.",
            DiagramType.Activity => Metamodel.IsActivityNode(e.Kind) && owner.IsAncestorOf(e) ? null
                : $"{e.DisplayName} is not a node of the activity {owner.DisplayName}.",
            DiagramType.StateMachine => Metamodel.IsVertex(e.Kind) && owner.IsAncestorOf(e) ? null
                : $"{e.DisplayName} is not a state of {owner.DisplayName}.",
            DiagramType.Package => e.Kind is ElementKind.Package or ElementKind.Profile || Metamodel.IsClassifier(e.Kind) ? null
                : $"A package diagram shows packages (and classifiers), not a {Metamodel.DisplayName(e.Kind)}.",
            _ when SupportedTypes.Contains(type) => Metamodel.IsClassifier(e.Kind) || e.Kind is ElementKind.Package or ElementKind.Requirement or ElementKind.TestCase ? null
                : $"A {Metamodel.DisplayName(type)} does not show a {Metamodel.DisplayName(e.Kind)}.",
            _ => $"{Metamodel.DisplayName(type)} is not laid out automatically; supported: {string.Join(", ", SupportedTypes)}.",
        };
    }

    /// <summary>Shapes a diagram with these elements would have (elements, ports, nested parts).</summary>
    public static int ShapeCount(ModelRepository m, DiagramType type, Element owner, IReadOnlyList<Element> elements) =>
        Count(Collect(m, type, owner, elements));

    // ================================================================== applying

    /// <summary>Creates (or rebuilds) every selected diagram of the plan as ONE undoable step. Rolls back completely on failure.</summary>
    public static AutoDiagramResult Apply(ModelRepository m, AutoDiagramPlan plan, bool addDependencies = true)
    {
        var chosen = plan.Diagrams.Where(p => p.Selected && m.Contains(p.Owner) && (p.Existing == null || m.Contains(p.Existing))).ToList();
        if (chosen.Count == 0) return new AutoDiagramResult([], [], 0, 0);
        var created = new List<Diagram>();
        var updated = new List<Diagram>();
        var deps = 0;
        var description = $"Auto-diagram: {chosen.Count} diagram{(chosen.Count == 1 ? "" : "s")}";
        try
        {
            using (m.History.Transaction(description))
            {
                if (addDependencies && chosen.Any(p => p.Type == DiagramType.Package))
                    foreach (var d in plan.Dependencies.Where(d => m.Contains(d.Client) && m.Contains(d.Supplier)))
                    {
                        if (m.Outgoing(d.Client, ElementKind.Dependency).Any(r => ReferenceEquals(r.Target, d.Supplier))) continue;
                        var r = m.Connect(ElementKind.Dependency, d.Client, d.Supplier, d.Client);
                        m.Set(r, FieldKeys.Stereotypes, "use");
                        deps++;
                    }
                foreach (var p in chosen) (p.Existing == null ? created : updated).Add(Build(m, p));
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            if (ReferenceEquals(m.History.UndoDescription, description)) m.History.Undo();
            throw;
        }
        return new AutoDiagramResult(created, updated, created.Concat(updated).Sum(d => d.Nodes.Count), deps);
    }

    /// <summary>
    /// Creates the diagram (or clears and refills an existing one), adds the shapes with a tidy layout and draws the
    /// relationships between them. Call inside a transaction to make it part of a larger undo step.
    /// </summary>
    public static Diagram Build(ModelRepository m, PlannedDiagram p)
    {
        Diagram d;
        if (p.Existing is { } ex && m.Contains(ex))
        {
            d = ex;
            if (d.Nodes.Count > 0 || d.Edges.Count > 0) m.RemoveFromDiagram(d, d.Nodes.ToList(), d.Edges.ToList());
            if (d.Name != p.Name && p.Name.Length > 0) m.Rename(d, p.Name);
        }
        else d = m.CreateDiagram(p.Owner, p.Type, string.IsNullOrWhiteSpace(p.Name) ? null : p.Name);
        if (p.Key != null && d.Get(MarkerKey) != p.Key) m.Set(d, MarkerKey, p.Key);
        if (p.Purpose.Length > 0 && (p.Existing == null || d.Documentation.Length == 0 || d.Get(MarkerKey).Length > 0)) m.Set(d, FieldKeys.Documentation, p.Purpose);
        if (d.Get(EdgeRouter.DiagramKey).Length == 0) m.Set(d, EdgeRouter.DiagramKey, EdgeRouter.Format(EdgeRouter.DefaultFor(d.DiagramType)));

        var roots = Collect(m, p.Type, p.Owner, p.Elements);
        var links = Links(m, p.Type, roots);
        Layout(m, p.Type, roots, links, p.LeftToRight);
        foreach (var n in roots) Materialize(m, d, n, null);
        foreach (var e in d.Nodes.Select(n => n.Element).Distinct().ToList())
            foreach (var r in m.RelationshipsOf(e).ToList())
                if (EdgeAllowed(p.Type, r)) m.AddEdge(d, r);
        EdgeRouter.Invalidate(d);
        return d;
    }

    private static void Materialize(ModelRepository m, Diagram d, LNode n, DiagramNode? parent)
    {
        var dn = m.AddNode(d, n.E, new Bounds(n.X, n.Y, n.W, n.H), showRelationships: false, parent);
        foreach (var port in n.Ports) m.AddNode(d, port.E, new Bounds(port.X, port.Y, port.W, port.H), showRelationships: false, dn);
        foreach (var k in n.Kids) Materialize(m, d, k, dn);
    }

    public static bool EdgeAllowed(DiagramType type, Relationship r) =>
        Metamodel.PaletteEdges(type).Contains(r.Kind)
        || r.Kind is ElementKind.Dependency or ElementKind.Trace && type is DiagramType.BlockDefinition or DiagramType.Class or DiagramType.Package or DiagramType.Requirement or DiagramType.UseCase;

    // ================================================================== layout

    private sealed class LNode
    {
        public required Element E;
        public double X, Y, W, H;
        public LNode? Parent;
        public bool IsPort;
        public readonly List<LNode> Kids = new();
        public readonly List<LNode> Ports = new();

        public IEnumerable<LNode> SelfAndDescendants() => Kids.SelectMany(k => k.SelfAndDescendants()).Concat(Ports).Prepend(this);
        public Pt Center => new(X + W / 2, Y + H / 2);
    }

    private sealed record Link(LNode A, LNode B, Relationship R);

    private static int Count(List<LNode> roots) => roots.Sum(r => r.SelfAndDescendants().Count());

    /// <summary>The shape tree: top-level shapes with their nested shapes and border ports.</summary>
    private static List<LNode> Collect(ModelRepository m, DiagramType type, Element owner, IReadOnlyList<Element> elements)
    {
        var list = elements.Where(e => m.Contains(e) && e is not Relationship && e is not Diagram && e.Kind != ElementKind.Model).Distinct().ToList();
        var roots = new List<LNode>();
        if (type == DiagramType.InternalBlock)
        {
            foreach (var e in list)
            {
                if (e.Kind == ElementKind.Property) roots.Add(Part(m, e, null, 0, [owner]));
                else roots.Add(new LNode { E = e, IsPort = e.Kind == ElementKind.Port });
            }
        }
        else
        {
            if (type == DiagramType.Activity) list.RemoveAll(e => e.Kind == ElementKind.Partition);
            var set = list.ToHashSet();
            var nodes = list.ToDictionary(e => e, e => new LNode { E = e });
            foreach (var e in list)
            {
                var n = nodes[e];
                Element? parent = null;
                if (Notation.IsBorderShape(e)) parent = e.Owner != null && set.Contains(e.Owner) ? e.Owner : null;
                else if (type == DiagramType.Package)
                    parent = e.AncestorsAndSelf().Skip(1).FirstOrDefault(a => set.Contains(a) && a.Kind is ElementKind.Package or ElementKind.Profile);
                else if (type == DiagramType.StateMachine)
                    parent = e.AncestorsAndSelf().Skip(1).FirstOrDefault(a => set.Contains(a) && a.Kind == ElementKind.State);
                if (parent == null)
                {
                    if (Notation.IsBorderShape(e)) continue;   // a pin without its action
                    roots.Add(n);
                    continue;
                }
                var pn = nodes[parent];
                n.Parent = pn;
                if (Notation.IsBorderShape(e)) { n.IsPort = true; pn.Ports.Add(n); }
                else pn.Kids.Add(n);
            }
        }
        foreach (var n in roots.SelectMany(r => r.SelfAndDescendants())) (n.W, n.H) = SizeOf(m, type, n);
        return roots;
    }

    /// <summary>A part with the ports of its type on its border and (one level down) the parts of its type nested inside.</summary>
    private static LNode Part(ModelRepository m, Element part, LNode? parent, int depth, HashSet<Element> path)
    {
        var n = new LNode { E = part, Parent = parent };
        if (m.ResolveType(part) is not { } type) return n;
        foreach (var port in type.OwnedElements.Where(p => p.Kind == ElementKind.Port))
            n.Ports.Add(new LNode { E = port, Parent = n, IsPort = true });
        if (depth == 0 && !path.Contains(type))
            foreach (var q in Parts(m, type))
                n.Kids.Add(Part(m, q, n, depth + 1, [.. path, type]));
        return n;
    }

    private static (double W, double H) SizeOf(ModelRepository m, DiagramType type, LNode n)
    {
        var e = n.E;
        if (n.IsPort) return (16, 16);
        var (w, h) = Notation.DefaultSizeOf(e);
        var shape = Notation.ShapeOf(e);
        switch (shape)
        {
            case NodeShape.Rectangle:
            {
                var c = Notation.Content(m, e, type);
                if (e.Kind == ElementKind.Requirement) w = 240;
                else
                {
                    var longest = c.Compartments.SelectMany(x => x.Title is { } t ? x.Lines.Append(t) : x.Lines).Append(c.Name).Append(c.Keyword ?? "").Max(s => s.Length);
                    w = Math.Clamp(longest * 6.8 + 28, w, 340);
                }
                var wrap = e.Kind == ElementKind.Requirement ? Math.Max(8, (int)((w - 12) / 6.4)) : 0;
                h = Math.Max(h, Notation.PreferredHeight(c, shape, Notation.LineHeight, wrap) + 4);
                if (type == DiagramType.InternalBlock) h = Math.Max(Math.Min(h, 90), 70);
                break;
            }
            case NodeShape.Folder:
                w = Math.Clamp(e.DisplayName.Length * 7.6 + 44, 160, 320);
                h = 90;
                break;
            case NodeShape.Rounded:
            {
                w = Math.Clamp(e.DisplayName.Length * 7.2 + 40, 130, 300);
                var lines = new[] { FieldKeys.Entry, FieldKeys.DoActivity, FieldKeys.Exit }.Count(k => e.Get(k).Length > 0) + (e.Kind == ElementKind.Action && e.Get(FieldKeys.Body).Length > 0 ? 1 : 0);
                h = Math.Max(h, 44 + lines * Notation.LineHeight);
                break;
            }
            case NodeShape.Ellipse:
                w = Math.Clamp(e.DisplayName.Length * 7 + 56, 150, 300);
                break;
        }
        if (n.Ports.Count > 0)
        {
            var perSide = (n.Ports.Count + 1) / 2;
            h = Math.Max(h, (perSide + 1) * 24);
            w = Math.Max(w, 170);
        }
        return (Ceil10(w), Ceil10(h));
    }

    private static double Ceil10(double v) => Math.Ceiling(v / 10) * 10;

    /// <summary>Relationships between shapes of the tree, with the shape at each end (a port's end is the port on the right part).</summary>
    private static List<Link> Links(ModelRepository m, DiagramType type, List<LNode> roots)
    {
        var all = roots.SelectMany(r => r.SelfAndDescendants()).ToList();
        var byElement = all.GroupBy(n => n.E).ToDictionary(g => (Element)g.Key!, g => g.ToList());
        LNode? End(Element e, string partId) =>
            byElement.TryGetValue(e, out var ns) ? ns.FirstOrDefault(n => partId.Length > 0 && n.Parent?.E.Id == partId) ?? ns[0] : null;
        var links = new List<Link>();
        var seen = new HashSet<Relationship>();
        foreach (var e in byElement.Keys)
            foreach (var r in m.RelationshipsOf(e))
                if (EdgeAllowed(type, r) && seen.Add(r) && End(r.Source, r.Get(FieldKeys.SourcePart)) is { } a && End(r.Target, r.Get(FieldKeys.TargetPart)) is { } b && !ReferenceEquals(a, b))
                    links.Add(new Link(a, b, r));
        return links;
    }

    private static void Layout(ModelRepository m, DiagramType type, List<LNode> roots, List<Link> links, bool leftToRight = false)
    {
        Arrange(type, roots, links, topLevel: true, leftToRight);
        foreach (var r in roots) Offset(r, 40, 40);
        foreach (var n in roots.SelectMany(r => r.SelfAndDescendants()).Where(n => n.Ports.Count > 0).ToList()) PlacePorts(n, links);
    }

    private static void Offset(LNode n, double dx, double dy)
    {
        n.X += dx;
        n.Y += dy;
        foreach (var k in n.Kids) Offset(k, n.X, n.Y);
    }

    /// <summary>Arranges one level of siblings (children first, so containers know their size). Positions are relative to the level's origin.</summary>
    private static void Arrange(DiagramType type, List<LNode> level, List<Link> links, bool topLevel, bool leftToRight = false)
    {
        foreach (var n in level.Where(n => n.Kids.Count > 0))
        {
            Arrange(type, n.Kids, links, topLevel: false, leftToRight);
            var (left, top) = ContainerLayout.Header(new DiagramNode(n.E, default));
            var right = n.Kids.Max(k => k.X + k.W);
            var bottom = n.Kids.Max(k => k.Y + k.H);
            foreach (var k in n.Kids) { k.X += left; k.Y += top; }
            n.W = Ceil10(Math.Max(n.W, left + right + ContainerLayout.Padding + 6));
            n.H = Ceil10(Math.Max(n.H, top + bottom + ContainerLayout.Padding + 6));
        }

        // Relationships lifted to this level: each end is the sibling that is (or contains) it.
        var index = level.Select((n, i) => (n, i)).ToDictionary(t => t.n, t => t.i);
        LNode? Lift(LNode x)
        {
            for (var c = x; c != null; c = c.Parent) if (index.ContainsKey(c)) return c;
            return null;
        }
        var pairs = new List<(LNode Up, LNode Down)>();
        foreach (var l in links)
        {
            if (Lift(l.A) is not { } a || Lift(l.B) is not { } b || ReferenceEquals(a, b)) continue;
            if (l.R.Kind is ElementKind.Requires or ElementKind.Excludes) continue;
            var st = Notation.StyleOf(l.R);
            var targetAbove = st.TargetHead == ArrowHead.HollowTriangle || st.Dashed && l.R.Kind is not ElementKind.ObjectFlow;
            pairs.Add(targetAbove ? (b, a) : (a, b));
        }

        var (hGap, vGap, perRow) = type switch
        {
            DiagramType.Package => (50.0, 60.0, 5),
            DiagramType.Requirement => (40.0, 80.0, 5),
            DiagramType.Activity => (50.0, 46.0, 4),
            DiagramType.StateMachine => (70.0, 70.0, 4),
            DiagramType.InternalBlock => (120.0, 50.0, 4),
            DiagramType.UseCase => (60.0, 36.0, 3),
            _ => (60.0, 80.0, 6),
        };
        if (!topLevel) { hGap = Math.Min(hGap, 40); vGap = Math.Min(vGap, 40); perRow = Math.Min(perRow, 4); }

        if (type == DiagramType.UseCase && topLevel && level.Any(n => n.E.Kind == ElementKind.Actor) && level.Any(n => n.E.Kind != ElementKind.Actor))
        {
            // Actors in a column on the left, use cases in a grid to their right.
            var actors = level.Where(n => n.E.Kind == ElementKind.Actor).ToList();
            var rest = level.Where(n => n.E.Kind != ElementKind.Actor).ToList();
            Grid(rest, hGap, vGap, perRow);
            var restH = rest.Max(n => n.Y + n.H);
            var y = Math.Max(0, (restH - (actors.Sum(a => a.H) + vGap * 2 * (actors.Count - 1))) / 2);
            foreach (var a in actors) { a.X = 0; a.Y = Snap(y); y += a.H + vGap * 2; }
            var shift = actors.Max(a => a.W) + 140;
            foreach (var r in rest) r.X += shift;
            return;
        }

        if (pairs.Count == 0) Grid(level, hGap, vGap, type == DiagramType.Package && topLevel ? 4 : perRow);
        else if (type == DiagramType.InternalBlock) Layered(level, pairs, index, vGap, hGap, perRow, horizontal: true);
        else if (leftToRight) Layered(level, pairs, index, vGap, hGap + 20, perRow, horizontal: true);
        else Layered(level, pairs, index, hGap, vGap, perRow, horizontal: false);
    }

    private static double Snap(double v) => Math.Round(v / 10) * 10;

    private static void Grid(List<LNode> level, double hGap, double vGap, int maxCols)
    {
        if (level.Count == 0) return;
        var cols = Math.Clamp((int)Math.Ceiling(Math.Sqrt(level.Count * 1.4)), 1, maxCols);
        var colW = new double[cols];
        for (var i = 0; i < level.Count; i++) colW[i % cols] = Math.Max(colW[i % cols], level[i].W);
        var y = 0.0;
        for (var row = 0; row * cols < level.Count; row++)
        {
            var items = level.Skip(row * cols).Take(cols).ToList();
            var x = 0.0;
            for (var c = 0; c < items.Count; c++)
            {
                items[c].X = Snap(x + (colW[c] - items[c].W) / 2);
                items[c].Y = Snap(y);
                x += colW[c] + hGap;
            }
            y += items.Max(n => n.H) + vGap;
        }
    }

    /// <summary>
    /// Layered (Sugiyama-style) placement: back edges of cycles are ignored, layers by longest path, order by barycentre,
    /// wide layers wrap into several rows. <paramref name="horizontal"/> runs the layers left to right.
    /// </summary>
    private static void Layered(List<LNode> level, List<(LNode Up, LNode Down)> pairs, Dictionary<LNode, int> index, double gap, double layerGap, int perRow, bool horizontal)
    {
        var n = level.Count;
        var outs = level.ToDictionary(x => x, _ => new List<LNode>());
        foreach (var (u, d) in pairs) if (!outs[u].Contains(d)) outs[u].Add(d);

        // Drop back edges (DFS from nodes without incoming edges first, in model order).
        var state = new Dictionary<LNode, int>();   // 1 = on stack, 2 = done
        var dag = new List<(LNode, LNode)>();
        var hasIn = pairs.Select(p => p.Down).ToHashSet();
        foreach (var start in level.OrderBy(x => hasIn.Contains(x) ? 1 : 0).ThenBy(x => index[x]))
        {
            if (state.ContainsKey(start)) continue;
            var stack = new Stack<(LNode Node, int Next)>();
            stack.Push((start, 0));
            state[start] = 1;
            while (stack.Count > 0)
            {
                var (v, i) = stack.Pop();
                if (i < outs[v].Count)
                {
                    stack.Push((v, i + 1));
                    var w = outs[v][i];
                    if (!state.TryGetValue(w, out var s)) { dag.Add((v, w)); state[w] = 1; stack.Push((w, 0)); }
                    else if (s == 2) dag.Add((v, w));   // forward / cross edge; s == 1 is a back edge
                }
                else state[v] = 2;
            }
        }

        var layer = level.ToDictionary(x => x, _ => 0);
        for (var iter = 0; iter <= n; iter++)
        {
            var changed = false;
            foreach (var (u, d) in dag) if (layer[d] < layer[u] + 1) { layer[d] = layer[u] + 1; changed = true; }
            if (!changed) break;
        }
        var layers = level.GroupBy(x => layer[x]).OrderBy(g => g.Key).Select(g => g.OrderBy(x => index[x]).ToList()).ToList();
        var order = new Dictionary<LNode, double>();
        foreach (var l in layers) for (var i = 0; i < l.Count; i++) order[l[i]] = i;
        for (var sweep = 0; sweep < 4; sweep++)
            foreach (var l in sweep % 2 == 0 ? layers.Skip(1) : layers.AsEnumerable().Reverse().Skip(1))
            {
                double Bary(LNode x)
                {
                    var ns = sweep % 2 == 0 ? dag.Where(p => ReferenceEquals(p.Item2, x)).Select(p => order[p.Item1]).ToList()
                                            : dag.Where(p => ReferenceEquals(p.Item1, x)).Select(p => order[p.Item2]).ToList();
                    return ns.Count == 0 ? order[x] : ns.Average();
                }
                var keyed = l.Select(x => (x, k: Bary(x))).OrderBy(t => t.k).ThenBy(t => index[t.x]).Select(t => t.x).ToList();
                l.Clear();
                l.AddRange(keyed);
                for (var i = 0; i < l.Count; i++) order[l[i]] = i;
            }

        // Wrap wide layers.
        var rows = layers.SelectMany(l => l.Chunk(perRow).Select(c => c.ToList())).ToList();
        double Main(LNode x) => horizontal ? x.H : x.W;    // extent along a row
        double Cross(LNode x) => horizontal ? x.W : x.H;   // extent across rows
        var widest = rows.Max(r => r.Sum(Main) + gap * (r.Count - 1));
        var pos = 0.0;
        foreach (var r in rows)
        {
            var len = r.Sum(Main) + gap * (r.Count - 1);
            var along = (widest - len) / 2;
            var thick = r.Max(Cross);
            foreach (var x in r)
            {
                var a = along;
                var c = pos + (thick - Cross(x)) / 2;
                if (horizontal) { x.X = Snap(c); x.Y = Snap(a); }
                else { x.X = Snap(a); x.Y = Snap(c); }
                along += Main(x) + gap;
            }
            pos += thick + layerGap;
        }
    }

    /// <summary>Ports go on the side of their part that faces what they connect to, spread evenly along it.</summary>
    private static void PlacePorts(LNode part, List<Link> links)
    {
        Pt Anchor(LNode x) => x.IsPort && x.Parent != null ? x.Parent.Center : x.Center;
        var c = part.Center;
        var sides = new List<LNode>[4];   // 0 left, 1 right, 2 top, 3 bottom
        for (var i = 0; i < 4; i++) sides[i] = new List<LNode>();
        int Capacity(int side) => Math.Max(1, (int)((side < 2 ? part.H : part.W) / 22) - 1);
        foreach (var port in part.Ports)
        {
            var peers = links.Where(l => ReferenceEquals(l.A, port) || ReferenceEquals(l.B, port)).Select(l => Anchor(ReferenceEquals(l.A, port) ? l.B : l.A)).ToList();
            int[] prefs;
            if (peers.Count > 0)
            {
                var dx = peers.Average(p => p.X) - c.X;
                var dy = peers.Average(p => p.Y) - c.Y;
                prefs = Math.Abs(dx) >= Math.Abs(dy)
                    ? dx >= 0 ? [1, dy >= 0 ? 3 : 2, dy >= 0 ? 2 : 3, 0] : [0, dy >= 0 ? 3 : 2, dy >= 0 ? 2 : 3, 1]
                    : dy >= 0 ? [3, dx >= 0 ? 1 : 0, dx >= 0 ? 0 : 1, 2] : [2, dx >= 0 ? 1 : 0, dx >= 0 ? 0 : 1, 3];
            }
            else prefs = port.E.Get(FieldKeys.Direction) switch { "in" => [0, 3, 2, 1], "out" => [1, 3, 2, 0], _ => [3, 1, 0, 2] };
            var side = prefs.FirstOrDefault(s => sides[s].Count < Capacity(s), prefs[0]);
            sides[side].Add(port);
        }
        for (var s = 0; s < 4; s++)
        {
            var list = sides[s];
            for (var i = 0; i < list.Count; i++)
            {
                var t = (i + 1.0) / (list.Count + 1);
                var p = list[i];
                var (px, py) = s switch
                {
                    0 => (part.X, part.Y + part.H * t),
                    1 => (part.X + part.W, part.Y + part.H * t),
                    2 => (part.X + part.W * t, part.Y),
                    _ => (part.X + part.W * t, part.Y + part.H),
                };
                p.X = Math.Round(px - p.W / 2);
                p.Y = Math.Round(py - p.H / 2);
            }
        }
    }
}
