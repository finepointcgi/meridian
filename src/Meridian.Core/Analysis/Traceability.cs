using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

public sealed record MatrixCell(Element Row, Element Column, IReadOnlyList<Relationship> Relationships)
{
    public bool IsEmpty => Relationships.Count == 0;
}

public sealed record TraceMatrix(IReadOnlyList<Element> Rows, IReadOnlyList<Element> Columns, IReadOnlyList<ElementKind> RelationshipKinds,
                                 Func<Element, Element, IReadOnlyList<Relationship>> Lookup)
{
    public MatrixCell Cell(Element row, Element col) => new(row, col, Lookup(row, col));
}

public sealed record RequirementCoverage(int Total, int Satisfied, int Verified, IReadOnlyList<Element> Unsatisfied, IReadOnlyList<Element> Unverified)
{
    public double SatisfiedRatio => Total == 0 ? 1 : (double)Satisfied / Total;
    public double VerifiedRatio => Total == 0 ? 1 : (double)Verified / Total;
}

/// <summary>Dependency matrices, impact analysis and requirement coverage.</summary>
public static class Traceability
{
    public static TraceMatrix BuildMatrix(ModelRepository model, IReadOnlyCollection<ElementKind> rowKinds, IReadOnlyCollection<ElementKind> columnKinds,
                                          IReadOnlyCollection<ElementKind> relationshipKinds, Element? scope = null)
    {
        bool inScope(Element e) => scope == null || ReferenceEquals(scope, e) || scope.IsAncestorOf(e);
        var rows = model.AllElements.Where(e => rowKinds.Contains(e.Kind) && inScope(e)).OrderBy(SortKey).ToList();
        var cols = model.AllElements.Where(e => columnKinds.Contains(e.Kind) && inScope(e)).OrderBy(SortKey).ToList();
        var kinds = relationshipKinds.ToList();

        // Either direction counts: a cell shows any relationship of the chosen kinds between row and column.
        var lookup = new Dictionary<(Element, Element), List<Relationship>>();
        foreach (var r in model.Relationships.Where(r => relationshipKinds.Contains(r.Kind)))
        {
            Add(r.Source, r.Target, r);
            Add(r.Target, r.Source, r);
        }

        return new TraceMatrix(rows, cols, kinds, (a, b) => lookup.TryGetValue((a, b), out var l) ? l : []);

        void Add(Element a, Element b, Relationship r)
        {
            if (!lookup.TryGetValue((a, b), out var l)) lookup[(a, b)] = l = new List<Relationship>();
            if (!l.Contains(r)) l.Add(r);
        }
    }

    private static string SortKey(Element e) =>
        e.Kind == ElementKind.Requirement ? e.Get(FieldKeys.RequirementId) + " " + e.Name : e.QualifiedName;

    public static RequirementCoverage Coverage(ModelRepository model)
    {
        var reqs = model.OfKind(ElementKind.Requirement).Where(r => !r.OwnedElements.Any(c => c.Kind == ElementKind.Requirement)).ToList();
        var unsat = reqs.Where(r => !model.Incoming(r, ElementKind.Satisfy).Any()).OrderBy(SortKey).ToList();
        var unver = reqs.Where(r => !model.Incoming(r, ElementKind.Verify).Any()).OrderBy(SortKey).ToList();
        return new RequirementCoverage(reqs.Count, reqs.Count - unsat.Count, reqs.Count - unver.Count, unsat, unver);
    }

    /// <summary>
    /// Impact analysis: everything that depends on <paramref name="changed"/>, transitively, following
    /// relationships backwards (who points at me) plus containment.
    /// </summary>
    public static IReadOnlyList<(Element Element, int Depth, Relationship? Via)> Impact(ModelRepository model, Element changed, int maxDepth = 6)
    {
        var result = new List<(Element, int, Relationship?)>();
        var seen = new HashSet<Element>(ReferenceEqualityComparer.Instance) { changed };
        var queue = new Queue<(Element e, int depth)>();
        queue.Enqueue((changed, 0));
        while (queue.Count > 0)
        {
            var (e, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;
            foreach (var r in model.Incoming(e))
            {
                if (r.Kind is ElementKind.Transition or ElementKind.ControlFlow or ElementKind.ObjectFlow) continue;
                if (seen.Add(r.Source)) { result.Add((r.Source, depth + 1, r)); queue.Enqueue((r.Source, depth + 1)); }
            }
            // Typed usages: properties typed by e are impacted
            foreach (var p in model.AllElements.Where(x => Metamodel.IsTypedFeature(x.Kind) && x.TypeRef == e.Id))
                if (seen.Add(p)) { result.Add((p, depth + 1, null)); queue.Enqueue((p, depth + 1)); }
        }
        return result;
    }
}
