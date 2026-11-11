using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.CodeSync;

/// <summary>What <see cref="CodeSyncDiagrams.Refresh"/> did to the diagrams of a model.</summary>
public sealed record DiagramRefreshResult(IReadOnlyList<Diagram> Updated, IReadOnlyList<Diagram> Created, int ShapesAdded)
{
    public static readonly DiagramRefreshResult None = new([], [], 0);
}

/// <summary>
/// Keeps diagrams in step with a code sync: existing diagrams that show the context of newly created elements get those
/// elements as new shapes, placed in free space next to what is already there (shapes people positioned never move), and
/// new namespaces / blocks can get diagrams of their own from the <see cref="AutoDiagrammer"/>.
/// </summary>
public static class CodeSyncDiagrams
{
    private const double Gap = 30;
    private const double RowGap = 40;
    private const double WrapWidth = 900;

    /// <summary>A block gets its own "internal structure" IBD from code sync only with at least this many parts typed by scanned classes.</summary>
    public const int MinIbdParts = 2;

    /// <summary>Stroke of a package dependency that is part of a cycle on the package overview.</summary>
    public const string CycleStroke = "#d32f2f";

    /// <summary>
    /// Adds new elements to the diagrams they belong on and (optionally) diagrams new packages and blocks. With
    /// <paramref name="codeRoot"/> (the repository's root package) it also keeps the "&lt;root&gt; – package overview": made
    /// when namespaces are added and <paramref name="createDiagrams"/> is on, and kept in step (packages added and removed,
    /// the derived «use» dependencies, cycles in red) once it exists.
    /// </summary>
    public static DiagramRefreshResult Refresh(ModelRepository m, CodeSyncChanges changes, bool createDiagrams, Element? codeRoot = null)
    {
        var addedIds = changes.Added.ToHashSet(StringComparer.Ordinal);
        var added = changes.Added.Select(m.Find).OfType<Element>().ToList();
        var shapes = added.Where(e => e is not Relationship && e is not Diagram && !Notation.IsBorderShape(e))
                          .OrderBy(e => e.AncestorsAndSelf().Count()).ToList();
        var relationships = added.OfType<Relationship>().ToList();
        var overviewKey = codeRoot is not null && m.Contains(codeRoot) ? AutoDiagrammer.KeyFor(DiagramType.Package, codeRoot) : null;
        var overview = overviewKey is not null && !changes.IsEmpty ? RefreshOverview(m, codeRoot!, overviewKey, added, createDiagrams) : default;
        if (shapes.Count == 0 && relationships.Count == 0 && overview.Diagram is null) return DiagramRefreshResult.None;

        var updated = new List<Diagram>();
        var count = 0;
        if (overview is { Diagram: { } od, Created: false })
        {
            updated.Add(od);
            count += overview.Shapes;
        }
        // The overview is kept by RefreshOverview: the generic pass would nest deeper packages into it.
        foreach (var d in m.Diagrams.Where(d => !addedIds.Contains(d.Id) && !(overviewKey is not null && ReferenceEquals(d.Owner, codeRoot) && d.Get(AutoDiagrammer.MarkerKey) == overviewKey)).ToList())
        {
            var fresh = ShowNewElements(m, d, shapes);
            var edges = 0;
            foreach (var r in relationships.Concat(fresh.SelectMany(n => m.RelationshipsOf(n.Element))).Distinct())
                if (!d.Shows(r) && d.EdgeFor(r) is null && AutoDiagrammer.EdgeAllowed(d.DiagramType, r) && m.AddEdge(d, r) is not null) edges++;
            if (fresh.Count == 0 && edges == 0) continue;
            if (fresh.Count > 0) EdgeRouter.Invalidate(d);
            updated.Add(d);
            count += fresh.Count;
        }

        var created = createDiagrams ? CreateDiagrams(m, shapes) : [];
        if (overview is { Diagram: { } nd, Created: true }) created.Insert(0, nd);
        return new DiagramRefreshResult(updated, created, count);
    }

    /// <summary>
    /// The package overview of a code-sync root package: built with the auto-diagrammer's package overview (the packages of
    /// <see cref="CodeModelMapper.OverviewPackages"/>) when namespaces were added and there is none yet; otherwise updated in
    /// place, without moving what is there: packages no longer shown are taken off, new ones placed in free space
    /// (inside their parent package when it is shown), dependencies drawn and cycles marked.
    /// </summary>
    private static (Diagram? Diagram, bool Created, int Shapes) RefreshOverview(ModelRepository m, Element root, string key, IReadOnlyList<Element> added, bool createDiagrams)
    {
        var existing = root.OwnedElements.OfType<Diagram>().FirstOrDefault(d => d.DiagramType == DiagramType.Package && d.Get(AutoDiagrammer.MarkerKey) == key);
        var packages = CodeModelMapper.OverviewPackages(root);
        if (existing is null)
        {
            if (!createDiagrams || packages.Count < 2 || !added.Any(e => CodeModelMapper.IsNamespacePackage(e) && root.IsAncestorOf(e))) return default;
            Diagram d;
            using (m.History.Transaction("Code sync: package overview"))
            {
                d = AutoDiagrammer.Build(m, AutoDiagrammer.PlanPackageOverview(m, root, packages));
                MarkCycles(m, d);
            }
            return (d, true, d.Nodes.Count);
        }

        var want = packages.ToHashSet(ReferenceEqualityComparer.Instance);
        var stale = existing.Nodes.Where(n => CodeModelMapper.IsNamespacePackage(n.Element) && !want.Contains(n.Element)).ToList();
        if (stale.Count > 0) m.RemoveFromDiagram(existing, stale);
        var fresh = new List<DiagramNode>();
        var placer = new Placer(existing, fresh);
        foreach (var p in packages.Where(p => !existing.Shows(p)))
        {
            var parent = p.Owner is { } owner && want.Contains(owner) ? existing.NodeFor(owner) : null;
            var (w, h) = Notation.DefaultSizeOf(p);
            fresh.Add(m.AddNode(existing, p, placer.Next(parent, w, h), showRelationships: false, parent));
        }
        var nested = fresh.Where(n => n.ParentId != null).ToList();
        if (nested.Count > 0 && ContainerLayout.GrowToFit(existing, nested, n => n.Bounds) is { Count: > 0 } grow) m.SetBounds(existing, grow, "Code sync: fit containers");
        var edges = 0;
        foreach (var r in existing.Nodes.Select(n => n.Element).Distinct().SelectMany(m.RelationshipsOf).Distinct().ToList())
            if (existing.EdgeFor(r) is null && AutoDiagrammer.EdgeAllowed(DiagramType.Package, r) && m.AddEdge(existing, r) is not null) edges++;
        var marked = MarkCycles(m, existing);
        if (fresh.Count > 0) EdgeRouter.Invalidate(existing);
        return stale.Count + fresh.Count + edges + marked > 0 ? (existing, false, fresh.Count) : default;
    }

    /// <summary>Draws the package dependencies that are part of a cycle in <see cref="CycleStroke"/> (and the others as usual).</summary>
    private static int MarkCycles(ModelRepository m, Diagram d)
    {
        var changed = 0;
        foreach (var x in d.Edges.Where(x => x.Relationship.Get(CodeFieldKeys.Symbol).StartsWith(CodeModelMapper.PackageDependencyPrefix, StringComparison.Ordinal)).ToList())
        {
            var cycle = x.Relationship.Get(CodeModelMapper.CycleKey) == "true";
            var stroke = x.Style.TryGetValue("stroke", out var s) ? s : null;
            if (cycle && stroke is null) { m.SetEdgeStyle(d, x, "stroke", CycleStroke); changed++; }
            else if (!cycle && stroke == CycleStroke) { m.SetEdgeStyle(d, x, "stroke", null); changed++; }
        }
        return changed;
    }

    /// <summary>
    /// Whether a code block earns its own internal block diagram: at least <see cref="MinIbdParts"/> part properties typed
    /// by scanned classes. A block whose only part is one other class (or whose members are all framework, collection-of-
    /// value or interface typed, which are values and references, not parts) gets none: its BDD already shows that.
    /// </summary>
    public static bool WorthAnIbd(ModelRepository m, Element block) =>
        AutoDiagrammer.Parts(m, block).Count(p => m.ResolveType(p) is { } t && t.Get(CodeFieldKeys.Symbol).Length > 0) >= MinIbdParts;

    /// <summary>
    /// Whether <paramref name="e"/> belongs on <paramref name="d"/>: it is a child of the diagram's context (a new block in
    /// the package a BDD shows, a new part of the block an IBD shows), or a child of a package / block drawn on it as a
    /// container. Returns the parent shape to nest it in (null = top level), or false when it does not belong.
    /// </summary>
    public static bool Belongs(ModelRepository m, Diagram d, Element e, out DiagramNode? parent)
    {
        parent = null;
        if (e.Owner is not { } owner || d.Owner is not { } context) return false;
        if (d.DiagramType == DiagramType.InternalBlock)
            return ReferenceEquals(owner, context) && e.Kind == ElementKind.Property && AutoDiagrammer.Parts(m, context).Contains(e);
        if (AutoDiagrammer.CheckShape(d.DiagramType, context, e) is not null) return false;
        if (d.NodeFor(owner) is { } ownerNode && ContainerLayout.IsContainer(d, ownerNode))
        {
            parent = ownerNode;
            return true;
        }
        return ReferenceEquals(owner, context);
    }

    private static List<DiagramNode> ShowNewElements(ModelRepository m, Diagram d, IReadOnlyList<Element> shapes)
    {
        var fresh = new List<DiagramNode>();
        var placer = new Placer(d, fresh);
        foreach (var e in shapes)
        {
            if (d.Shows(e) || !Belongs(m, d, e, out var parent)) continue;
            var (w, h) = Notation.DefaultSizeOf(e);
            fresh.Add(m.AddNode(d, e, placer.Next(parent, w, h), showRelationships: false, parent));
        }
        // Containers that received new children grow to hold them (their position stays put).
        var nested = fresh.Where(n => n.ParentId != null).ToList();
        if (nested.Count > 0)
        {
            var grow = ContainerLayout.GrowToFit(d, nested, n => n.Bounds);
            if (grow.Count > 0) m.SetBounds(d, grow, "Code sync: fit containers");
        }
        return fresh;
    }

    /// <summary>
    /// Places new shapes without moving existing ones: in rows below what a container (or the diagram) already holds,
    /// left-aligned with it and wrapping at the existing content's width.
    /// </summary>
    public sealed class Placer(Diagram d, IReadOnlyCollection<DiagramNode> placed)
    {
        private readonly Dictionary<string, (double X, double Y, double Left, double Right, double RowBottom)> _cursors = new(StringComparer.Ordinal);

        public Bounds Next(DiagramNode? parent, double w, double h)
        {
            var key = parent?.Id ?? "";
            if (!_cursors.TryGetValue(key, out var c))
            {
                var existing = d.Nodes.Where(n => n.ParentId == parent?.Id && !placed.Contains(n) && !Notation.IsBorderShape(n.Element)).Select(n => n.Bounds).ToList();
                double left, top, right;
                if (existing.Count > 0)
                {
                    left = existing.Min(b => b.X);
                    top = existing.Max(b => b.Bottom) + RowGap;
                    right = Math.Max(existing.Max(b => b.Right), left + (parent is null ? WrapWidth : 0));
                }
                else if (parent is { } p)
                {
                    var (hl, ht) = ContainerLayout.Header(p);
                    (left, top) = (p.Bounds.X + hl, p.Bounds.Y + ht);
                    right = Math.Max(p.Bounds.Right - ContainerLayout.Padding, left + w);
                }
                else
                {
                    // An empty diagram: below everything else drawn on it (or at the usual origin).
                    var all = d.Nodes.Where(n => !placed.Contains(n)).Select(n => n.Bounds).ToList();
                    (left, top) = all.Count > 0 ? (all.Min(b => b.X), all.Max(b => b.Bottom) + RowGap) : (40, 60);
                    right = left + WrapWidth;
                }
                c = (left, top, left, right, top);
            }
            if (c.X > c.Left && c.X + w > c.Right) c = (c.Left, c.RowBottom + Gap, c.Left, c.Right, c.RowBottom + Gap);
            var b = new Bounds(c.X, c.Y, w, h);
            _cursors[key] = (c.X + w + Gap, c.Y, c.Left, c.Right, Math.Max(c.RowBottom, b.Bottom));
            return b;
        }
    }

    /// <summary>Diagrams for new packages and blocks (a structure view per new namespace, an IBD per new block with parts).</summary>
    private static List<Diagram> CreateDiagrams(ModelRepository m, IReadOnlyList<Element> shapes)
    {
        var owners = shapes.Where(e => e.Kind is ElementKind.Package or ElementKind.Block).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        if (owners.Count == 0) return [];
        var plan = AutoDiagrammer.Plan(m, new AutoDiagramOptions { DerivePackageDependencies = false });
        foreach (var p in plan.Diagrams)
            p.Selected = p.Existing is null && owners.Contains(p.Owner.Id) && p.Type != DiagramType.Package
                         && (p.Type != DiagramType.InternalBlock || p.Owner.Get(CodeFieldKeys.Symbol).Length == 0 || WorthAnIbd(m, p.Owner));
        if (!plan.Diagrams.Any(p => p.Selected)) return [];
        return AutoDiagrammer.Apply(m, plan, addDependencies: false).Created.ToList();
    }
}
