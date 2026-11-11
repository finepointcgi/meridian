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

    /// <summary>Adds new elements to the diagrams they belong on and (optionally) diagrams new packages and blocks.</summary>
    public static DiagramRefreshResult Refresh(ModelRepository m, CodeSyncChanges changes, bool createDiagrams)
    {
        var addedIds = changes.Added.ToHashSet(StringComparer.Ordinal);
        // Behaviour from code draws its own diagrams (CodeBehaviourMapper); its elements stay off the structure diagrams.
        var added = changes.Added.Select(m.Find).OfType<Element>().Where(e => !CodeBehaviourMapper.IsGenerated(e)).ToList();
        var shapes = added.Where(e => e is not Relationship && e is not Diagram && !Notation.IsBorderShape(e))
                          .OrderBy(e => e.AncestorsAndSelf().Count()).ToList();
        var relationships = added.OfType<Relationship>().ToList();
        if (shapes.Count == 0 && relationships.Count == 0) return DiagramRefreshResult.None;

        var updated = new List<Diagram>();
        var count = 0;
        foreach (var d in m.Diagrams.Where(d => !addedIds.Contains(d.Id) && !CodeBehaviourMapper.IsGenerated(d)).ToList())
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
        return new DiagramRefreshResult(updated, created, count);
    }

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
        foreach (var p in plan.Diagrams) p.Selected = p.Existing is null && owners.Contains(p.Owner.Id) && p.Type != DiagramType.Package;
        if (!plan.Diagrams.Any(p => p.Selected)) return [];
        return AutoDiagrammer.Apply(m, plan, addDependencies: false).Created.ToList();
    }
}
