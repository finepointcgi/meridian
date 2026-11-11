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

    /// <summary>On a code-sync root package: "true" once code sync made (or found) its package overview. An overview that
    /// is gone while this is set was deleted on purpose (or rejected in review), and is not made again.</summary>
    public const string OverviewMadeKey = "code.overview";

    /// <summary>
    /// Adds new elements to the diagrams they belong on and (optionally) diagrams new packages and blocks. With
    /// <paramref name="codeRoot"/> (the repository's root package) it also keeps the "&lt;root&gt; – package overview": made
    /// when there is none, <paramref name="createDiagrams"/> is on and there are at least two namespace packages, and kept in step (packages added and removed,
    /// the derived «use» dependencies, cycles in red) once it exists.
    /// </summary>
    public static DiagramRefreshResult Refresh(ModelRepository m, CodeSyncChanges changes, bool createDiagrams, Element? codeRoot = null)
    {
        var addedIds = changes.Added.ToHashSet(StringComparer.Ordinal);
        // Behaviour from code draws its own diagrams (CodeBehaviourMapper); its elements stay off the structure diagrams.
        var added = changes.Added.Select(m.Find).OfType<Element>().Where(e => !CodeBehaviourMapper.IsGenerated(e)).ToList();
        var shapes = added.Where(e => e is not Relationship && e is not Diagram && !Notation.IsBorderShape(e))
                          .OrderBy(e => e.AncestorsAndSelf().Count()).ToList();
        var relationships = added.OfType<Relationship>().ToList();
        var overviewKey = codeRoot is not null && m.Contains(codeRoot) ? AutoDiagrammer.KeyFor(DiagramType.Package, codeRoot) : null;
        // The overview is checked on every sync, so a project synced before it existed gets one too.
        var overview = overviewKey is not null ? RefreshOverview(m, codeRoot!, overviewKey, createDiagrams) : default;
        if (shapes.Count == 0 && relationships.Count == 0 && overview.Diagram is null) return DiagramRefreshResult.None;

        var updated = new List<Diagram>();
        var count = 0;
        if (overview is { Diagram: { } od, Created: false })
        {
            updated.Add(od);
            count += overview.Shapes;
        }
        // The overview is kept by RefreshOverview: the generic pass would nest deeper packages into it.
        foreach (var d in m.Diagrams.Where(d => !addedIds.Contains(d.Id) && !CodeBehaviourMapper.IsGenerated(d) && !(overviewKey is not null && ReferenceEquals(d.Owner, codeRoot) && d.Get(AutoDiagrammer.MarkerKey) == overviewKey)).ToList())
        {
            if (shapes.Count == 0 && relationships.Count == 0) break;
            // A diagram nobody moved since code sync laid it out stays "untouched" when code sync adds to it.
            var pristine = IsUntouched(d);
            var fresh = ShowNewElements(m, d, shapes);
            var edges = 0;
            foreach (var r in relationships.Concat(fresh.SelectMany(n => m.RelationshipsOf(n.Element))).Distinct())
                if (!d.Shows(r) && d.EdgeFor(r) is null && AutoDiagrammer.EdgeAllowed(d.DiagramType, r) && m.AddEdge(d, r) is not null) edges++;
            if (fresh.Count == 0 && edges == 0) continue;
            if (fresh.Count > 0) EdgeRouter.Invalidate(d);
            if (pristine) Stamp(m, d);
            updated.Add(d);
            count += fresh.Count;
        }

        var created = createDiagrams ? CreateDiagrams(m, shapes) : [];
        if (overview is { Diagram: { } nd, Created: true }) created.Insert(0, nd);
        foreach (var d in created) Stamp(m, d);
        return new DiagramRefreshResult(updated, created, count);
    }

    /// <summary>
    /// Where a structure diagram's shapes are, as a short hash per shape (element path and bounds), space separated. Code
    /// sync stores it in <see cref="CodeBehaviourMapper.LayoutKey"/> on the diagrams it makes; see <see cref="IsUntouched"/>.
    /// </summary>
    public static string LayoutStamp(Diagram d) =>
        string.Join(" ", d.Nodes.Select(n => Derivation.DeriveFields.Hash($"{ShapePath(d, n)} {n.Bounds.X:0.#} {n.Bounds.Y:0.#} {n.Bounds.Width:0.#} {n.Bounds.Height:0.#}")[..10])
            .Distinct().Order(StringComparer.Ordinal));

    /// <summary>
    /// A stamped diagram whose every shape is still where code sync put it: nothing moved or resized, nothing added by
    /// hand. Shapes that went with their element (code sync removing a class) do not count as a touch.
    /// </summary>
    public static bool IsUntouched(Diagram d)
    {
        if (d.Get(CodeBehaviourMapper.LayoutKey) is not { Length: > 0 } stamp) return false;
        var stored = stamp.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return LayoutStamp(d).Split(' ', StringSplitOptions.RemoveEmptyEntries).All(stored.Contains);
    }

    /// <summary>The element ids from the top-level shape down to <paramref name="n"/>, joined by '/'.</summary>
    private static string ShapePath(Diagram d, DiagramNode n)
    {
        var parts = new List<string>();
        for (DiagramNode? x = n; x != null && parts.Count < 16; x = d.ParentOf(x)) parts.Add(x.Element.Id);
        parts.Reverse();
        return string.Join("/", parts);
    }

    /// <summary>Records where code sync put the shapes of a diagram it made (or added to while nobody had moved anything).</summary>
    private static void Stamp(ModelRepository m, Diagram d)
    {
        var stamp = LayoutStamp(d);
        if (d.Get(CodeBehaviourMapper.LayoutKey) != stamp) m.Set(d, CodeBehaviourMapper.LayoutKey, stamp);
    }

    /// <summary>
    /// Removes the "internal structure" diagrams code sync made (stamped, or listed in <paramref name="synced"/>) for blocks of
    /// repository <paramref name="repoId"/> that no longer earn one (<see cref="WorthAnIbd"/>), when nobody touched them: every shape is a code-sync element where code
    /// sync put it (the stored <see cref="CodeBehaviourMapper.LayoutKey"/> hash, or for diagrams from before the hash the
    /// auto-diagrammer's own layout), no hand-made element, comment or relationship on it, its name and purpose as made,
    /// and nothing in the model (reports, relation maps, links, other diagrams) refers to it. Returns how many went.
    /// </summary>
    public static int RemoveTrivialIbds(ModelRepository m, string repoId, IReadOnlySet<string>? synced = null, References? references = null)
    {
        // Code sync's own: stamped when made, or (made before the stamp) listed in an earlier run of the repository.
        var candidates = m.Diagrams.Where(d => d.DiagramType == DiagramType.InternalBlock && d.Owner is { } block
                                               && (d.Get(CodeBehaviourMapper.LayoutKey).Length > 0 || synced?.Contains(d.Id) == true)
                                               && block.Get(CodeFieldKeys.Repo) == repoId && block.Get(CodeFieldKeys.Symbol).Length > 0
                                               && d.Get(AutoDiagrammer.MarkerKey) == AutoDiagrammer.KeyFor(DiagramType.InternalBlock, block)
                                               && !WorthAnIbd(m, block)).ToList();
        if (candidates.Count == 0) return 0;
        references ??= new References(m);
        var removed = 0;
        foreach (var d in candidates)
        {
            if (!m.Contains(d) || !IbdUntouched(m, d, d.Owner!, repoId) || references.Mentions(d)) continue;
            m.Delete(d);
            removed++;
        }
        return removed;
    }

    private static bool IbdUntouched(ModelRepository m, Diagram d, Element block, string repoId)
    {
        bool Ours(Element e) => e.Get(CodeFieldKeys.Repo) == repoId && e.Get(CodeFieldKeys.Symbol).Length > 0;
        if (!d.Name.EndsWith("internal structure", StringComparison.Ordinal)) return false;
        if (d.Documentation.Length > 0 && !d.Documentation.StartsWith("How the parts of ", StringComparison.Ordinal)) return false;
        if (d.Nodes.Any(n => !Ours(n.Element)) || d.Edges.Any(x => !Ours(x.Relationship))) return false;
        if (d.Get(CodeBehaviourMapper.LayoutKey).Length > 0) return IsUntouched(d);
        // Made before the hash: untouched when every shape is where the auto-diagrammer puts it today.
        var planned = AutoDiagrammer.PlannedLayout(m, DiagramType.InternalBlock, block, AutoDiagrammer.DefaultContent(m, DiagramType.InternalBlock, block));
        if (planned.Count != d.Nodes.Count) return false;
        foreach (var n in d.Nodes)
        {
            if (!planned.TryGetValue(ShapePath(d, n), out var b)) return false;
            if (Math.Abs(b.X - n.Bounds.X) > 1 || Math.Abs(b.Y - n.Bounds.Y) > 1 || Math.Abs(b.Width - n.Bounds.Width) > 1 || Math.Abs(b.Height - n.Bounds.Height) > 1) return false;
        }
        return true;
    }

    /// <summary>
    /// What the model refers to, to tell whether a diagram code sync would remove is used elsewhere: element ids in any
    /// field (report templates, relation maps, links) or node style, diagrams shown on other diagrams, and diagram names in
    /// the texts of elements people made (a report template naming a diagram).
    /// </summary>
    public sealed class References
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private readonly List<string> _texts = [];

        public References(ModelRepository m)
        {
            foreach (var e in m.AllElements)
            {
                var made = e is not Diagram && e.Get(CodeFieldKeys.Symbol).Length == 0 && !CodeBehaviourMapper.IsGenerated(e);
                foreach (var (k, v) in e.Fields)
                {
                    Ids(v);
                    if (made && k != FieldKeys.Name && v.Length > 3) _texts.Add(v);
                }
            }
            foreach (var d in m.Diagrams)
                foreach (var n in d.Nodes)
                {
                    if (n.Element is Diagram shown) _ids.Add(shown.Id);
                    foreach (var v in n.Style.Values) Ids(v);
                }
        }

        /// <summary>Element ids ("_" and 32 hex digits) in a text.</summary>
        private void Ids(string v)
        {
            for (var i = v.IndexOf('_'); i >= 0 && i + 33 <= v.Length; i = v.IndexOf('_', i + 1))
            {
                var j = i + 1;
                while (j < v.Length && j - i <= 32 && char.IsAsciiHexDigit(v[j])) j++;
                if (j - i == 33 && (j == v.Length || !char.IsAsciiHexDigit(v[j]))) _ids.Add(v.Substring(i, 33));
            }
        }

        /// <summary>Something refers to <paramref name="e"/> by id (or, for a diagram, by name in a hand-made text).</summary>
        public bool Mentions(Element e) =>
            _ids.Contains(e.Id) || e is Diagram && e.Name.Length > 0 && _texts.Any(t => t.Contains(e.Name, StringComparison.Ordinal));
    }

    /// <summary>
    /// The package overview of a code-sync root package: built with the auto-diagrammer's package overview (the packages of
    /// <see cref="CodeModelMapper.OverviewPackages"/>) when there is none yet and there are at least two namespace packages
    /// (on any sync, so projects synced before the overview existed get one; found again by its marker, never doubled); otherwise updated in
    /// place, without moving what is there: packages no longer shown are taken off, new ones placed in free space
    /// (inside their parent package when it is shown), dependencies drawn and cycles marked.
    /// </summary>
    private static (Diagram? Diagram, bool Created, int Shapes) RefreshOverview(ModelRepository m, Element root, string key, bool createDiagrams)
    {
        var existing = root.OwnedElements.OfType<Diagram>().FirstOrDefault(d => d.DiagramType == DiagramType.Package && d.Get(AutoDiagrammer.MarkerKey) == key);
        var packages = CodeModelMapper.OverviewPackages(root);
        if (existing is null)
        {
            if (!createDiagrams || packages.Count < 2 || root.Get(OverviewMadeKey) == "true") return default;
            Diagram d;
            using (m.History.Transaction("Code sync: package overview"))
            {
                d = AutoDiagrammer.Build(m, AutoDiagrammer.PlanPackageOverview(m, root, packages));
                MarkCycles(m, d);
                m.Set(root, OverviewMadeKey, "true");
            }
            return (d, true, d.Nodes.Count);
        }
        if (root.Get(OverviewMadeKey) != "true") m.Set(root, OverviewMadeKey, "true");

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
