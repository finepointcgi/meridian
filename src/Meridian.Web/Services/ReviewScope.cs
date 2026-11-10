using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Data;

namespace Meridian.Web.Services;

/// <summary>Resolves what a review covers in a concrete model: the listed items, every id in scope and the in-scope diff.</summary>
public static class ReviewScope
{
    /// <summary>Requirement field moved to "approved" when an approving review applies status.</summary>
    public const string StatusKey = "status";
    public const string Approved = "approved";

    /// <summary>The elements a reviewer works through (the checklist).</summary>
    public static IReadOnlyList<Element> Items(ModelRepository model, ReviewInfo review, IReadOnlyList<ElementDiff>? diff)
    {
        switch (review.ScopeKind)
        {
            case ReviewScopeKind.Elements:
                return review.ElementIds.Select(model.Find).OfType<Element>().ToList();
            case ReviewScopeKind.Package:
                if (model.Find(review.ElementIds.FirstOrDefault()) is not { } pkg) return [];
                return pkg.Descendants().Where(e => e is not Relationship && e is not Diagram).Prepend(pkg).ToList();
            default:
                return (diff ?? []).Where(d => d.Kind != DiffKind.Removed).Select(d => model.Find(d.Id))
                    .OfType<Element>().Where(e => e is not Diagram).DistinctBy(e => e.Id).ToList();
        }
    }

    /// <summary>Every element id the review covers in the given model (chosen elements and everything they own).</summary>
    public static HashSet<string> Ids(ModelRepository model, ReviewInfo review)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in review.ElementIds)
        {
            set.Add(id);
            if (model.Find(id) is { } e) foreach (var d in e.Descendants()) set.Add(d.Id);
        }
        return set;
    }

    /// <summary>The changes between two models restricted to the review's scope (everything, for a version-range review).</summary>
    public static IReadOnlyList<ElementDiff> Diff(ModelRepository before, ModelRepository after, ReviewInfo review)
    {
        // Suspect-link fingerprints are bookkeeping written alongside requirement edits, not changes to review.
        var all = ModelDiff.Compare(before, after)
            .Select(d => d with { Fields = d.Fields.Where(f => f.Key is not (ReqFields.SourceFingerprint or ReqFields.TargetFingerprint)).ToList() })
            .Where(d => d.Kind != DiffKind.Changed || d.Fields.Count > 0)
            .ToList();
        if (review.ScopeKind == ReviewScopeKind.Versions) return all;
        var ids = Ids(before, review);
        ids.UnionWith(Ids(after, review));
        return all.Where(d => ids.Contains(d.Id)).ToList();
    }

    /// <summary>The requirements an approval moves to "approved": in-scope items and the requirements they own.</summary>
    public static IReadOnlyList<Element> Requirements(ModelRepository model, ReviewInfo review, IReadOnlyList<ElementDiff>? diff) =>
        Items(model, review, diff).SelectMany(e => e.Descendants().Prepend(e))
            .Where(e => e.Kind == ElementKind.Requirement).DistinctBy(e => e.Id).ToList();

    /// <summary>Sets status = approved on the in-scope requirements as one undoable step. Returns how many changed.</summary>
    public static int ApplyApprovedStatus(ModelRepository model, ReviewInfo review, IReadOnlyList<ElementDiff>? diff)
    {
        var targets = Requirements(model, review, diff).Where(r => r.Get(StatusKey) != Approved).ToList();
        if (targets.Count == 0) return 0;
        using (model.History.Transaction($"Approve requirements (review #{review.Id})"))
            foreach (var r in targets) model.Set(r, StatusKey, Approved);
        return targets.Count;
    }
}
