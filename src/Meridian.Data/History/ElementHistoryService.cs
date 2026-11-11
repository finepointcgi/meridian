using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Serialization;
using Meridian.Serialization.History;

namespace Meridian.Data.History;

/// <summary>
/// One entry of an element's history: what <see cref="Version"/> did to the element (or to a relationship connected to it, or to
/// its place on a diagram). <see cref="ParentVersionId"/> is the version it is compared with. A <see cref="PresentSinceFirst"/>
/// entry stands for "already in the first version" (the first version of a project has no change rows).
/// </summary>
public sealed record ElementHistoryEntry(HistoryVersion Version, string ElementId, string Label, string ElementKind, ElementChangeKind Change,
                                         IReadOnlyList<string> Fields, bool IsConnected, string? DiagramId, string? DiagramName, long? ParentVersionId,
                                         bool PresentSinceFirst = false)
{
    public bool IsDiagramRow => Change is ElementChangeKind.AddedToDiagram or ElementChangeKind.RemovedFromDiagram;
}

/// <summary>An element's history as one viewer may see it, newest first.</summary>
public sealed record ElementHistory(IReadOnlyList<ElementHistoryEntry> Entries, IndexProgress Progress, HistoryVersion? Head, HistoryVersion? First)
{
    public static readonly ElementHistory Empty = new([], IndexProgress.Empty, null, null);
}

/// <summary>What changed on an element since the last commit of its line (<see cref="ElementHistoryService.UncommittedAsync"/>).</summary>
public sealed record UncommittedChange(ElementChangeKind Change, IReadOnlyList<string> Fields);

/// <summary>A field-by-field comparison of one element in two states (versions, or a version and the current model).</summary>
public sealed record ElementDiff(IReadOnlyList<FieldDiffRow> Rows, FlatModel? Before, FlatModel? After, string? Note = null);

/// <summary>
/// Element history ("who changed this and when"), read from the change index without loading stored versions; field values are
/// read from the two versions only when a diff is opened (<see cref="VersionReader"/>). Everything is filtered for the viewer:
/// history is only shown for elements in the viewer's (redacted) model, rows from when an element or relationship was somewhere the
/// viewer can't see are left out, and diffs come from versions redacted for the viewer (docs/PERMISSIONS.md).
/// </summary>
public static class ElementHistoryService
{
    /// <summary>
    /// The history of <paramref name="element"/> (an element of the viewer's model <paramref name="visible"/>) on the line
    /// <paramref name="branchId"/> (null = main), or on every line with <paramref name="allBranches"/>.
    /// </summary>
    public static async Task<ElementHistory> LoadAsync(IMeridianStore db, Guid projectId, Guid? branchId, Element element, ModelRepository visible,
                                                       AccessView view, bool includeRelationships = false, bool allBranches = false,
                                                       int inlineLimit = 3, CancellationToken ct = default)
    {
        if (!visible.Contains(element) || view.IsHidden(element)) return ElementHistory.Empty;
        var progress = await ElementHistoryIndexer.For(db).EnsureIndexedAsync(projectId, inlineLimit, ct);
        var versions = await db.ListHistoryVersionsAsync(projectId);
        var byId = versions.ToDictionary(v => v.Id);
        var (head, line) = await LineAsync(db, projectId, branchId, versions);
        var first = line.Count > 0 ? line[^1] : null;
        var inLine = line.Select(v => v.Id).ToHashSet();

        var rows = await db.ListElementChangesAsync(projectId, element.Id, includeRelationships);
        var entries = new List<ElementHistoryEntry>(rows.Count + 1);
        foreach (var r in rows)
        {
            if (!byId.TryGetValue(r.VersionId, out var v)) continue;
            if (!allBranches && !inLine.Contains(v.Id)) continue;
            if (!MaySee(r, element.Id, visible, view)) continue;
            entries.Add(new ElementHistoryEntry(v, r.ElementId, r.Label, r.ElementKind, r.Change, r.Fields, r.ElementId != element.Id,
                                                r.DiagramId, r.DiagramName, v.ParentVersionId));
        }
        entries = entries.OrderByDescending(e => e.Version.Number).ToList();

        // The first version of a project has no rows: an element nothing ever added was already in it.
        var added = entries.Any(e => !e.IsConnected && e.Change == ElementChangeKind.Added);
        if (!added && first != null && progress.Complete)
            entries.Add(new ElementHistoryEntry(first, element.Id, element.DisplayName, element.Kind.ToString(), ElementChangeKind.Added, [], false,
                                                null, null, null, PresentSinceFirst: true));
        return new ElementHistory(entries, progress, head, first);
    }

    /// <summary>
    /// The newest version of a line and its ancestry (newest first): the line's own commits back to where it started, then the line
    /// it was made from. A branch without commits yet stands on the version it was made from.
    /// </summary>
    public static async Task<(HistoryVersion? Head, List<HistoryVersion> Line)> LineAsync(IMeridianStore db, Guid projectId, Guid? branchId,
                                                                                             IReadOnlyList<HistoryVersion> versions)
    {
        var byId = versions.ToDictionary(v => v.Id);
        IReadOnlyList<BranchInfo>? branches = null;
        var head = versions.Where(v => v.BranchId == branchId).MaxBy(v => v.Number);
        if (head is null && branchId is { } b && await db.GetBranchAsync(b) is { } branch)
            head = branch.BaseVersionId is { } baseId && byId.TryGetValue(baseId, out var based) ? based
                 : versions.Where(v => v.BranchId is null && v.CreatedAt <= branch.CreatedAt).MaxBy(v => v.Number);
        var line = new List<HistoryVersion>();
        var seen = new HashSet<long>();
        for (var cur = head; cur != null && seen.Add(cur.Id);)
        {
            line.Add(cur);
            if (!cur.Indexed && cur.BranchId != null) branches ??= await db.ListBranchesAsync(projectId);
            var parent = ElementHistoryIndexer.ParentOf(cur, versions, branches);
            cur = parent is { } p && byId.TryGetValue(p, out var x) ? x : null;
        }
        return (head, line);
    }

    /// <summary>
    /// Whether a viewer with hidden packages may see a row. The element itself is visible (checked by the caller); a row of it from
    /// when it sat in a package the viewer can't see now (or one that is gone) is left out. A connected relationship is shown when it
    /// is in the viewer's model, or — removed since — when both its ends and its owner are. Diagram rows need the diagram.
    /// </summary>
    public static bool MaySee(ElementChangeRow r, string focusId, ModelRepository visible, AccessView view)
    {
        if (!view.HidesAnything) return true;
        if (r.DiagramId != null && (visible.Find(r.DiagramId) is not { } d || view.IsHidden(d))) return false;
        if (r.ElementId == focusId) return r.OwnerId is null || OwnerVisible(r.OwnerId, visible, view);
        if (visible.Find(r.ElementId) is { } rel) return !view.IsHidden(rel);
        return r.SourceId != null && r.TargetId != null && visible.Find(r.SourceId) is { } s && !view.IsHidden(s)
               && visible.Find(r.TargetId) is { } t && !view.IsHidden(t) && (r.OwnerId is null || OwnerVisible(r.OwnerId, visible, view));
    }

    private static bool OwnerVisible(string ownerId, ModelRepository visible, AccessView view) =>
        visible.Find(ownerId) is { } o && view.CanSeeContents(o);

    /// <summary>
    /// What changed on <paramref name="live"/> (an element of the viewer's current model) since <paramref name="head"/>, the last commit
    /// of the line (null: nothing committed yet), or null when nothing did.
    /// </summary>
    public static async Task<UncommittedChange?> UncommittedAsync(VersionReader reader, HistoryVersion? head, Element live, ModelRepository liveModel)
    {
        if (head is null) return new UncommittedChange(ElementChangeKind.Added, []);
        var version = await reader.LoadAsync(head.Id, live.Id);
        var old = version?.Find(live.Id);
        if (old is null) return new UncommittedChange(ElementChangeKind.Added, []);
        var now = FlatModel.Of(liveModel, live).Find(live.Id)!;
        var keys = ElementChangeComputer.ChangedKeys(old, now);
        var moved = old.OwnerId != now.OwnerId;
        if (keys.Count == 0) return moved ? new UncommittedChange(ElementChangeKind.Moved, [HistoryKeys.Owner]) : null;
        if (moved) keys.Add(HistoryKeys.Owner);
        return new UncommittedChange(ElementChangeKind.Changed, keys);
    }

    /// <summary>The field-by-field diff of an entry: the element in the entry's parent version against the entry's version.</summary>
    public static async Task<ElementDiff> DiffAsync(VersionReader reader, ElementHistoryEntry entry)
    {
        var id = entry.ElementId;
        var before = !entry.PresentSinceFirst && entry.ParentVersionId is { } p ? await reader.LoadAsync(p, id) : null;
        var after = entry.Change != ElementChangeKind.Removed ? await reader.LoadAsync(entry.Version.Id, id) : null;
        var a = before?.Find(id);
        var b = after?.Find(id);
        if (entry.Change == ElementChangeKind.Removed ? a is null : b is null)
            return new ElementDiff([], before, after, "Not visible to you in this version.");
        return new ElementDiff(FieldDiff.Compare(a, before, b, after), before, after);
    }

    /// <summary>The element at <paramref name="versionId"/> against the current model ("Compare with current").</summary>
    public static async Task<ElementDiff> CompareWithCurrentAsync(VersionReader reader, long versionId, string elementId, ModelRepository liveModel)
    {
        var then = await reader.LoadAsync(versionId, elementId);
        var live = liveModel.Find(elementId);
        var now = live is null ? null : FlatModel.Of(liveModel, live);
        return new ElementDiff(FieldDiff.Compare(then?.Find(elementId), then, now?.Find(elementId), now), then, now,
                               then?.Find(elementId) is null ? "Not in that version (or not visible to you there)." : live is null ? "No longer in the model." : null);
    }
}

/// <summary>
/// Reads stored versions for one viewer, redacted for them (packages hidden from them removed, references to hidden elements
/// cleared), keeping the field values of the element being compared. Keeps the last few it read.
/// </summary>
public sealed class VersionReader(IMeridianStore db, Guid projectId, AccessView view)
{
    private readonly List<(long Version, string Element, FlatModel Model)> _cache = [];

    public async Task<FlatModel?> LoadAsync(long versionId, string elementId)
    {
        lock (_cache)
            if (_cache.FirstOrDefault(c => c.Version == versionId && c.Element == elementId) is { Model: { } hit }) return hit;
        var json = await db.LoadVersionAsync(projectId, versionId);
        if (json is null) return null;
        var keep = new HashSet<string>(StringComparer.Ordinal) { elementId };
        var flat = await Task.Run(() =>
        {
            if (!view.HidesAnything) return FlatModel.Parse(json, keep);
            var model = ProjectFile.Deserialize(json);
            ModelRedactor.Redact(model, view);
            return FlatModel.From(model, keep);
        });
        lock (_cache)
        {
            _cache.Add((versionId, elementId, flat));
            if (_cache.Count > 4) _cache.RemoveAt(0);
        }
        return flat;
    }
}
