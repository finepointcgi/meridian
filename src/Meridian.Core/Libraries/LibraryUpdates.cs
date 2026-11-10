using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Validation;

namespace Meridian.Core.Libraries;

/// <summary>A project element that refers to a library element: how (typed by, a relationship end, a shape on a diagram).</summary>
public sealed record LibraryReference(Element Referrer, Element Target, string How);

/// <summary>A field of a project element changed by a library update (for undo).</summary>
public sealed record LibraryMark(string ElementId, string? Before, string After);

/// <summary>
/// What a library means for the project using it: which project elements refer to its elements, and what a new pin changes
/// (compared with <see cref="ModelDiff"/>) and makes suspect.
/// </summary>
public static class LibraryUpdates
{
    /// <summary>Project elements that refer to elements of <paramref name="mount"/> (all mounted libraries when null).</summary>
    public static IReadOnlyList<LibraryReference> References(ModelRepository model, LibraryMount? mount = null)
    {
        var list = new List<LibraryReference>();
        if (!model.HasLibraries) return list;
        bool In(Element e) => model.LibraryOf(e) is { } m && (mount is null || ReferenceEquals(m, mount));
        foreach (var e in model.OwnElements)
        {
            if (e is Relationship r)
            {
                if (In(r.Source)) list.Add(new LibraryReference(r, r.Source, Keyword(r) + " source"));
                if (In(r.Target)) list.Add(new LibraryReference(r, r.Target, Keyword(r) + " target"));
            }
            else if (Metamodel.IsTypedFeature(e.Kind) && e.TypeRef.Length > 0 && model.Find(e.TypeRef) is { } t && In(t))
                list.Add(new LibraryReference(e, t, "typed by"));
            else if (e is Diagram d)
                foreach (var n in d.Nodes)
                    if (In(n.Element)) list.Add(new LibraryReference(d, n.Element, "shown on diagram"));
        }
        return list;
    }

    private static string Keyword(Relationship r) => "«" + (Metamodel.Style(r.Kind).Keyword ?? Metamodel.DisplayName(r.Kind)) + "»";

    /// <summary>
    /// The ids of library elements that changed between two pins: every added, removed or changed element and, because a
    /// change inside a block changes the block, all their library ancestors (diagram layout changes don't count).
    /// </summary>
    public static (HashSet<string> Changed, HashSet<string> Removed) ChangedIds(ModelRepository before, ModelRepository after, IReadOnlyList<ElementDiff> diffs)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var removed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in diffs)
        {
            if (d.ElementKind == ElementKind.Diagram) continue;
            if (d.Kind == DiffKind.Removed) removed.Add(d.Id);
            changed.Add(d.Id);
            foreach (var m in new[] { before, after })
                if (m.Find(d.Id) is { } e)
                    foreach (var a in e.AncestorsAndSelf().Skip(1))
                        if (a.Owner != null) changed.Add(a.Id);   // not the stand-in model root
        }
        return (changed, removed);
    }

    /// <summary>
    /// Marks the project's references to changed library elements as suspect (one undo step; trace links show in the suspect
    /// link lists, other elements in validation TRC-001 and the inspector). <paramref name="model"/> already has the new pin
    /// mounted. Returns the fields it set, for undoing the update.
    /// </summary>
    public static IReadOnlyList<LibraryMark> MarkSuspects(ModelRepository model, string libraryName, string fromLabel, string toLabel,
                                                          IReadOnlySet<string> changed, IReadOnlySet<string> removed, IReadOnlyDictionary<string, string> names)
    {
        var marks = new List<LibraryMark>();
        if (changed.Count == 0) return marks;
        string Name(string id) => names.TryGetValue(id, out var n) ? n : model.Find(id)?.DisplayName ?? id;
        string Why(string id) => removed.Contains(id)
            ? $"{Name(id)} was removed from {libraryName} ({fromLabel} → {toLabel})"
            : $"{Name(id)} changed in {libraryName} ({fromLabel} → {toLabel})";

        var targets = new List<(Element Referrer, string TargetId)>();
        foreach (var e in model.OwnElements)
        {
            if (e is Relationship r)
            {
                if (changed.Contains(r.Source.Id)) targets.Add((r, r.Source.Id));
                else if (changed.Contains(r.Target.Id)) targets.Add((r, r.Target.Id));
            }
            else if (Metamodel.IsTypedFeature(e.Kind) && changed.Contains(e.TypeRef)) targets.Add((e, e.TypeRef));
        }
        if (targets.Count == 0) return marks;
        using (model.History.Transaction($"Library update: {targets.Count} suspect reference{(targets.Count == 1 ? "" : "s")}"))
            foreach (var (referrer, id) in targets)
            {
                var before = referrer.Fields.GetValueOrDefault(ReqFields.LibraryChange);
                var why = Why(id);
                SuspectLinks.MarkLibraryChange(model, referrer, why);
                marks.Add(new LibraryMark(referrer.Id, before, why));
            }
        return marks;
    }

    /// <summary>Undoes <see cref="MarkSuspects"/> (fields changed since are left alone).</summary>
    public static void UnmarkSuspects(ModelRepository model, IEnumerable<LibraryMark> marks)
    {
        var list = marks.ToList();
        if (list.Count == 0) return;
        using (model.History.Transaction("Undo library update"))
            foreach (var m in list)
                if (model.Find(m.ElementId) is { } e && e.Get(ReqFields.LibraryChange) == m.After)
                    model.Set(e, ReqFields.LibraryChange, m.Before);
    }
}

/// <summary>
/// Validation rule LIB-001: references to elements that are not in the model — library elements removed by an update (or of a
/// library that is no longer used or that the user can't read). They are kept, not dropped, until someone fixes them.
/// </summary>
public sealed class MissingReferenceRule : IValidationRule
{
    public string Id => "LIB-001";
    public string Description => "References to used-project (library) elements must resolve";

    public IEnumerable<ValidationIssue> Check(ModelRepository model)
    {
        if (!model.MissingReferences.Any()) yield break;
        foreach (var r in model.Relationships)
        {
            if (MissingReference.Is(r.Source) || MissingReference.Is(r.Target))
            {
                var end = MissingReference.Is(r.Target) ? r.Target : r.Source;
                var other = ReferenceEquals(end, r.Target) ? r.Source : r.Target;
                yield return new ValidationIssue(Severity.Error, Id,
                    $"Missing reference: {Metamodel.DisplayName(r.Kind)} of {other.DisplayName} refers to an element that no longer exists ({end.Id})", r);
            }
        }
        foreach (var d in model.Diagrams)
        {
            if (model.IsLibraryElement(d)) continue;
            var shapes = d.Nodes.Count(n => MissingReference.Is(n.Element));
            var paths = d.Edges.Count(x => MissingReference.Is(x.Relationship));
            if (shapes + paths > 0)
                yield return new ValidationIssue(Severity.Error, Id,
                    $"Missing reference: {d.DisplayName} shows {shapes + paths} element{(shapes + paths == 1 ? "" : "s")} that no longer exist{(shapes + paths == 1 ? "s" : "")}", d);
        }
    }
}
