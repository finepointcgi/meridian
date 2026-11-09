using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

/// <summary>The link columns of the traceability table, phrased from the requirement's point of view.</summary>
public enum TraceColumn { DerivedFrom, DerivedTo, RefinedBy, SatisfiedBy, VerifiedBy, AllocatedTo }

public enum TraceCoverage { None, Partial, Full }

public sealed record TraceLink(Relationship Relationship, Element Other, bool Suspect);

public sealed record TraceRow(Element Requirement, Element? Package, bool IsLeaf, IReadOnlyDictionary<TraceColumn, IReadOnlyList<TraceLink>> Links,
                              bool Satisfied, bool Verified)
{
    public string Id => Requirement.Get(FieldKeys.RequirementId);
    public string Name => Requirement.Name;
    public string Text => Requirement.Get(FieldKeys.Text);
    public string Status => Requirement.Get(ReqFields.Status);
    public IReadOnlyList<TraceLink> this[TraceColumn c] => Links[c];
    public bool HasSuspect => Links.Values.Any(l => l.Any(x => x.Suspect));
    public TraceCoverage Coverage => Satisfied && Verified ? TraceCoverage.Full : Satisfied || Verified ? TraceCoverage.Partial : TraceCoverage.None;
    /// <summary>Leaf requirements must be satisfied and verified; a missing one is a gap.</summary>
    public bool SatisfyGap => IsLeaf && !Satisfied && Status != "rejected";
    public bool VerifyGap => IsLeaf && !Verified && Status != "rejected";
}

public sealed record TraceFilter(bool NotSatisfied = false, bool NotVerified = false, bool SuspectOnly = false, Element? Package = null, string? Text = null);

public sealed record PackageCoverage(Element Package, int Total, int Satisfied, int Verified, int Suspect)
{
    public double SatisfiedRatio => Total == 0 ? 1 : (double)Satisfied / Total;
    public double VerifiedRatio => Total == 0 ? 1 : (double)Verified / Total;
}

/// <summary>One line of the multi-level view: need → requirement → derived → design → test.</summary>
public sealed record TraceChainRow(Element? Need, Element Requirement, Element? Derived, int Depth, Element? Design, Element? Test, string Verdict, bool Gap, bool Suspect);

/// <summary>
/// The requirements traceability table: one row per requirement with its derive / refine / satisfy / verify /
/// allocate links, coverage, gaps and suspect state; plus filters, the multi-level chain and per-package coverage.
/// </summary>
public static class TraceabilityTable
{
    public static readonly TraceColumn[] Columns = Enum.GetValues<TraceColumn>();

    public static string Title(TraceColumn c) => c switch
    {
        TraceColumn.DerivedFrom => "Derived from",
        TraceColumn.DerivedTo => "Derived to",
        TraceColumn.RefinedBy => "Refined by",
        TraceColumn.SatisfiedBy => "Satisfied by",
        TraceColumn.VerifiedBy => "Verified by",
        _ => "Allocated to",
    };

    /// <summary>Relationship kind, and whether the requirement is the source of it.</summary>
    public static (ElementKind Kind, bool RequirementIsSource) Shape(TraceColumn c) => c switch
    {
        TraceColumn.DerivedFrom => (ElementKind.DeriveReqt, true),
        TraceColumn.DerivedTo => (ElementKind.DeriveReqt, false),
        TraceColumn.RefinedBy => (ElementKind.Refine, false),
        TraceColumn.SatisfiedBy => (ElementKind.Satisfy, false),
        TraceColumn.VerifiedBy => (ElementKind.Verify, false),
        _ => (ElementKind.Allocate, true),
    };

    public static IReadOnlyList<TraceLink> LinksOf(ModelRepository model, Element req, TraceColumn c)
    {
        var (kind, outgoing) = Shape(c);
        return (outgoing ? model.Outgoing(req, kind).Select(r => new TraceLink(r, r.Target, SuspectLinks.IsSuspect(r)))
                         : model.Incoming(req, kind).Select(r => new TraceLink(r, r.Source, SuspectLinks.IsSuspect(r))))
            .OrderBy(l => Label(l.Other), StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string Label(Element e) =>
        e.Kind == ElementKind.Requirement ? $"{e.Get(FieldKeys.RequirementId)} {e.Name}".Trim() : e.DisplayName;

    public static Element? PackageOf(Element e) => e.AncestorsAndSelf().Skip(1).FirstOrDefault(a => a.Kind is ElementKind.Package or ElementKind.Model);

    private static IEnumerable<Element> Decomposition(ModelRepository model, Element req) =>
        req.OwnedElements.Where(c => c.Kind == ElementKind.Requirement)
           .Concat(model.Incoming(req, ElementKind.DeriveReqt).Select(r => r.Source))
           .Distinct();

    public static IReadOnlyList<TraceRow> Build(ModelRepository model)
    {
        var sat = new Dictionary<Element, bool>(ReferenceEqualityComparer.Instance);
        var ver = new Dictionary<Element, bool>(ReferenceEqualityComparer.Instance);

        // A requirement is covered directly, or through its whole decomposition (children / derived requirements).
        bool Covered(Element r, ElementKind kind, Dictionary<Element, bool> memo, HashSet<Element> path)
        {
            if (memo.TryGetValue(r, out var v)) return v;
            if (model.Incoming(r, kind).Any()) return memo[r] = true;
            if (!path.Add(r)) return false;
            var parts = Decomposition(model, r).ToList();
            var result = parts.Count > 0 && parts.All(p => Covered(p, kind, memo, path));
            path.Remove(r);
            return memo[r] = result;
        }

        return RequirementIds.InDocumentOrder(model).Select(r =>
        {
            var links = Columns.ToDictionary(c => c, c => LinksOf(model, r, c));
            var leaf = !Decomposition(model, r).Any();
            return new TraceRow(r, PackageOf(r), leaf, links,
                Covered(r, ElementKind.Satisfy, sat, new(ReferenceEqualityComparer.Instance)),
                Covered(r, ElementKind.Verify, ver, new(ReferenceEqualityComparer.Instance)));
        }).ToList();
    }

    public static IEnumerable<TraceRow> Apply(IEnumerable<TraceRow> rows, TraceFilter f)
    {
        var q = f.Text?.Trim() ?? "";
        return rows.Where(r =>
            (!f.NotSatisfied || r.SatisfyGap) &&
            (!f.NotVerified || r.VerifyGap) &&
            (!f.SuspectOnly || r.HasSuspect) &&
            (f.Package == null || f.Package.IsAncestorOf(r.Requirement)) &&
            (q.Length == 0 || Matches(r, q)));
    }

    private static bool Matches(TraceRow r, string q) =>
        r.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        r.Text.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Status.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        r.Links.Values.Any(l => l.Any(x => Label(x.Other).Contains(q, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Design elements (blocks) that no requirement traces to: nothing satisfies, refines, traces or allocates them to a requirement.</summary>
    public static IReadOnlyList<Element> OrphanDesignElements(ModelRepository model) =>
        model.AllElements.Where(e => e.Kind is ElementKind.Block or ElementKind.InterfaceBlock or ElementKind.ConstraintBlock)
             .Where(b => !model.RelationshipsOf(b).Any(r => SuspectLinks.IsTraceKind(r.Kind) &&
                                                           (ReferenceEquals(r.Source, b) ? r.Target : r.Source).Kind == ElementKind.Requirement))
             .OrderBy(b => b.QualifiedName, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Valid, not-yet-linked elements for a cell's link picker.</summary>
    public static IEnumerable<Element> Candidates(ModelRepository model, Element req, TraceColumn c)
    {
        var (kind, outgoing) = Shape(c);
        var existing = LinksOf(model, req, c).Select(l => l.Other).ToHashSet(ReferenceEqualityComparer.Instance);
        bool preferred(Element x) => c switch
        {
            TraceColumn.DerivedFrom or TraceColumn.DerivedTo => x.Kind == ElementKind.Requirement,
            TraceColumn.VerifiedBy => x.Kind == ElementKind.TestCase,
            TraceColumn.SatisfiedBy => x.Kind is ElementKind.Block or ElementKind.InterfaceBlock or ElementKind.Property or ElementKind.Component or ElementKind.Class,
            TraceColumn.RefinedBy => x.Kind is ElementKind.UseCase or ElementKind.Activity or ElementKind.StateMachine or ElementKind.Interaction,
            _ => x.Kind is ElementKind.Block or ElementKind.Property or ElementKind.Activity or ElementKind.Action,
        };
        return model.AllElements
            .Where(x => x is not Relationship && x is not Diagram && x.Kind is not (ElementKind.Model or ElementKind.Package or ElementKind.Comment) && !ReferenceEquals(x, req) && !existing.Contains(x))
            .Where(x => outgoing ? Metamodel.CheckEnds(kind, req.Kind, x.Kind) == null : Metamodel.CheckEnds(kind, x.Kind, req.Kind) == null)
            .OrderBy(x => preferred(x) ? 0 : 1).ThenBy(Label, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Creates the link for a cell (undoable, replicated). Throws <see cref="InvalidOperationException"/> when invalid.</summary>
    public static Relationship Link(ModelRepository model, Element req, TraceColumn c, Element other)
    {
        var (kind, outgoing) = Shape(c);
        return outgoing ? model.Connect(kind, req, other) : model.Connect(kind, other, req);
    }

    public static IReadOnlyList<PackageCoverage> CoverageByPackage(IEnumerable<TraceRow> rows) =>
        rows.Where(r => r.IsLeaf && r.Package != null && r.Status != "rejected").GroupBy(r => r.Package!)
            .Select(g => new PackageCoverage(g.Key, g.Count(), g.Count(r => r.Satisfied), g.Count(r => r.Verified), g.Count(r => r.HasSuspect)))
            .OrderBy(p => p.Package.QualifiedName, StringComparer.OrdinalIgnoreCase).ToList();

    public static string Verdict(Element? testCase) => testCase?.Get(ReqFields.Verdict) ?? "";

    /// <summary>
    /// Multi-level view. Top requirements (not derived from or contained in another requirement) are expanded through
    /// their decomposition (contained and derived requirements); each level lists its design (satisfy) and test
    /// (verify) elements. Needs are use cases / actors linked to the top requirement by refine or trace.
    /// </summary>
    public static IReadOnlyList<TraceChainRow> Chains(ModelRepository model)
    {
        var result = new List<TraceChainRow>();
        var tops = RequirementIds.InDocumentOrder(model)
            .Where(r => r.Owner?.Kind != ElementKind.Requirement && !model.Outgoing(r, ElementKind.DeriveReqt).Any()).ToList();
        foreach (var top in tops)
        {
            var needs = model.RelationshipsOf(top)
                .Where(r => r.Kind is ElementKind.Refine or ElementKind.Trace)
                .Select(r => ReferenceEquals(r.Source, top) ? r.Target : r.Source)
                .Where(x => x.Kind is ElementKind.UseCase or ElementKind.Actor).Distinct().ToList();
            var first = true;
            var seen = new HashSet<Element>(ReferenceEqualityComparer.Instance);
            Walk(top, 0);

            void Walk(Element req, int depth)
            {
                if (!seen.Add(req)) return;
                var designLinks = model.Incoming(req, ElementKind.Satisfy).ToList();
                var testLinks = model.Incoming(req, ElementKind.Verify).ToList();
                var parts = Decomposition(model, req).ToList();
                var leaf = parts.Count == 0;
                var n = Math.Max(1, Math.Max(designLinks.Count, testLinks.Count));
                for (var i = 0; i < n; i++)
                {
                    var d = i < designLinks.Count ? designLinks[i] : null;
                    var t = i < testLinks.Count ? testLinks[i] : null;
                    var gap = leaf && (designLinks.Count == 0 || testLinks.Count == 0) && req.Get(ReqFields.Status) != "rejected";
                    result.Add(new TraceChainRow(first ? needs.FirstOrDefault() : null, top, ReferenceEquals(req, top) ? null : req, depth,
                        d?.Source, t?.Source, Verdict(t?.Source), gap,
                        (d != null && SuspectLinks.IsSuspect(d)) || (t != null && SuspectLinks.IsSuspect(t))));
                    first = false;
                }
                foreach (var p in parts.OrderBy(Label, StringComparer.OrdinalIgnoreCase)) Walk(p, depth + 1);
            }
        }
        return result;
    }
}
