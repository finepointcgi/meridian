using System.Security.Cryptography;
using System.Text;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Validation;

namespace Meridian.Core.Analysis;

/// <summary>
/// Suspect links: a trace relationship (satisfy, verify, refine, derive, trace, allocate) becomes suspect when a
/// requirement at either end changes its name or text after the link was last reviewed.
/// <para>
/// Each trace relationship stores, per requirement end, a fingerprint of that requirement
/// (<see cref="ReqFields.SourceFingerprint"/> / <see cref="ReqFields.TargetFingerprint"/>). A link is suspect when a
/// stored fingerprint no longer matches the requirement. <see cref="ModelRepository.Set"/> records the baseline
/// fingerprint just before a requirement's name or text changes, inside the same undo step, so undo restores the
/// link to "not suspect" and the fingerprints replicate and save like any other field.
/// </para>
/// </summary>
public static class SuspectLinks
{
    public static readonly ElementKind[] TraceKinds =
        [ElementKind.Satisfy, ElementKind.Verify, ElementKind.Refine, ElementKind.DeriveReqt, ElementKind.Trace, ElementKind.Allocate];

    public static bool IsTraceKind(ElementKind k) => Array.IndexOf(TraceKinds, k) >= 0;

    /// <summary>True when a change of <paramref name="key"/> on <paramref name="e"/> can make links suspect.</summary>
    public static bool Watches(Element e, string key) =>
        e.Kind == ElementKind.Requirement && key is FieldKeys.Name or FieldKeys.Text;

    /// <summary>Short stable hash of the parts of a requirement a reviewer signs off on: its name and text.</summary>
    public static string Fingerprint(Element requirement)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(requirement.Name + "\u001f" + requirement.Get(FieldKeys.Text)));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    private static IEnumerable<(Element End, string Key)> TrackedEnds(Relationship r)
    {
        if (!IsTraceKind(r.Kind)) yield break;
        if (r.Source.Kind == ElementKind.Requirement) yield return (r.Source, ReqFields.SourceFingerprint);
        if (r.Target.Kind == ElementKind.Requirement) yield return (r.Target, ReqFields.TargetFingerprint);
    }

    /// <summary>The requirement ends of <paramref name="r"/> that changed since the link was last reviewed.</summary>
    public static IEnumerable<Element> ChangedEnds(Relationship r) =>
        TrackedEnds(r).Where(t => r.Get(t.Key) is { Length: > 0 } fp && fp != Fingerprint(t.End)).Select(t => t.End);

    public static bool IsSuspect(Relationship r) => ChangedEnds(r).Any();

    public static IEnumerable<Relationship> All(ModelRepository model) => model.Relationships.Where(IsSuspect);

    /// <summary>Suspect links touching <paramref name="e"/> (or <paramref name="e"/> itself when it is a suspect link).</summary>
    public static IEnumerable<Relationship> Of(ModelRepository model, Element e) =>
        e is Relationship r ? (IsSuspect(r) ? [r] : []) : model.RelationshipsOf(e).Where(IsSuspect);

    /// <summary>
    /// Called by <see cref="ModelRepository.Set"/> right before a watched field of <paramref name="requirement"/>
    /// changes: links that have no baseline yet get one (the fingerprint before the change), which then makes them
    /// suspect once the change lands.
    /// </summary>
    internal static void Baseline(ModelRepository model, Element requirement)
    {
        var fp = Fingerprint(requirement);
        foreach (var r in model.RelationshipsOf(requirement).ToList())
            foreach (var (end, key) in TrackedEnds(r))
                if (ReferenceEquals(end, requirement) && r.Get(key).Length == 0)
                    model.Set(r, key, fp);
    }

    /// <summary>Marks a link as reviewed: its fingerprints are reset to the requirements' current state (undoable).</summary>
    public static void Clear(ModelRepository model, Relationship r)
    {
        using (model.History.Transaction("Clear suspect link"))
            foreach (var (end, key) in TrackedEnds(r))
                model.Set(r, key, Fingerprint(end));
    }

    /// <summary>Clears every suspect link (or those in <paramref name="links"/>) in one undo step. Returns the count.</summary>
    public static int ClearAll(ModelRepository model, IEnumerable<Relationship>? links = null)
    {
        var list = (links ?? All(model)).Where(IsSuspect).ToList();
        if (list.Count == 0) return 0;
        using (model.History.Transaction(list.Count == 1 ? "Clear suspect link" : $"Clear {list.Count} suspect links"))
            foreach (var r in list) Clear(model, r);
        return list.Count;
    }

    /// <summary>Human readable reason, e.g. "REQ-002 Acceleration changed since this satisfy link was reviewed".</summary>
    public static string Describe(Relationship r)
    {
        var ends = ChangedEnds(r).Select(e => $"{e.Get(FieldKeys.RequirementId)} {e.Name}".Trim()).ToList();
        if (ends.Count == 0) return "Not suspect";
        var kw = Metamodel.Style(r.Kind).Keyword ?? Metamodel.DisplayName(r.Kind);
        return $"{string.Join(" and ", ends)} changed since this «{kw}» link was reviewed";
    }
}

/// <summary>Validation rule TRC-001: suspect trace links need review.</summary>
public sealed class SuspectLinkRule : IValidationRule
{
    public string Id => "TRC-001";
    public string Description => "Trace links should be reviewed after the requirement changes";

    public IEnumerable<ValidationIssue> Check(ModelRepository model) =>
        SuspectLinks.All(model).Select(r => new ValidationIssue(Severity.Warning, Id,
            $"Suspect link: {r.Source.DisplayName} → {r.Target.DisplayName} — {SuspectLinks.Describe(r)}", r));
}
