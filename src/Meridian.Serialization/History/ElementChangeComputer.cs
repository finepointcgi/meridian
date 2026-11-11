namespace Meridian.Serialization.History;

/// <summary>What a commit did to one element (stored as a small integer: keep the values).</summary>
public enum ElementChangeKind
{
    Added = 0,
    Changed = 1,
    Removed = 2,
    Moved = 3,
    /// <summary>The element was put on a diagram (<see cref="ElementChange.DiagramId"/>).</summary>
    AddedToDiagram = 4,
    /// <summary>The element was taken off a diagram (or the diagram was deleted).</summary>
    RemovedFromDiagram = 5,
}

/// <summary>
/// One row of the element change index: <paramref name="Kind"/> of <paramref name="ElementId"/> in a commit, with the keys of
/// the fields it changed (pseudo-keys <c>owner</c>, <c>source</c>, <c>target</c>, <c>layout</c> for a move, relationship ends and a
/// diagram's shapes). <paramref name="Label"/> is the element's name (or relationship label) in that version;
/// <paramref name="OwnerId"/>, <paramref name="SourceId"/> and <paramref name="TargetId"/> let history find connected
/// relationships and check what a viewer may see. Diagram rows name the diagram in <paramref name="DiagramId"/> /
/// <paramref name="DiagramName"/>.
/// </summary>
public sealed record ElementChange(string ElementId, ElementChangeKind Kind, IReadOnlyList<string> Fields, string ElementKind, string Label,
                                   string? OwnerId, string? SourceId, string? TargetId, string? DiagramId = null, string? DiagramName = null);

/// <summary>Pseudo field keys of the change index (not element fields).</summary>
public static class HistoryKeys
{
    public const string Owner = "owner";
    public const string Source = "source";
    public const string Target = "target";
    public const string Layout = "layout";

    public static bool IsPseudo(string key) => key is Owner or Source or Target or Layout;
}

/// <summary>Per-element changes between two versions (matched by id), from their <see cref="FlatModel"/>s.</summary>
public static class ElementChangeComputer
{
    /// <summary>
    /// The changes from <paramref name="before"/> to <paramref name="after"/>. A version without a parent (the first one of a
    /// project) gives no rows: history shows elements without an "added" entry as present since the first version.
    /// </summary>
    public static List<ElementChange> Compute(FlatModel? before, FlatModel after)
    {
        var result = new List<ElementChange>();
        if (before is null) return result;

        foreach (var e in after.Elements)
        {
            var old = before.Find(e.Id);
            if (old is null)
            {
                result.Add(Row(after, e, ElementChangeKind.Added, []));
                continue;
            }
            var keys = ChangedKeys(old, e);
            var moved = old.OwnerId != e.OwnerId;
            if (keys.Count > 0)
            {
                if (moved) keys.Add(HistoryKeys.Owner);
                result.Add(Row(after, e, ElementChangeKind.Changed, keys));
            }
            else if (moved) result.Add(Row(after, e, ElementChangeKind.Moved, [HistoryKeys.Owner]));
        }
        foreach (var e in before.Elements)
            if (!after.Contains(e.Id)) result.Add(Row(before, e, ElementChangeKind.Removed, []));

        // Diagram membership: which elements a commit put on or took off which diagram.
        foreach (var d in after.Elements)
        {
            if (d.Shown is null) continue;
            var was = before.Find(d.Id)?.Shown;
            foreach (var id in d.Shown)
                if (was is null || !was.Contains(id)) result.Add(Membership(after, id, d, ElementChangeKind.AddedToDiagram));
        }
        foreach (var d in before.Elements)
        {
            if (d.Shown is null) continue;
            var now = after.Find(d.Id)?.Shown;
            foreach (var id in d.Shown)
                if (now is null || !now.Contains(id)) result.Add(Membership(before, id, d, ElementChangeKind.RemovedFromDiagram));
        }
        return result;
    }

    /// <summary>The field keys (and pseudo-keys) whose values differ between two versions of an element.</summary>
    public static List<string> ChangedKeys(FlatElement a, FlatElement b)
    {
        var keys = new List<string>();
        int i = 0, j = 0;
        while (i < a.Keys.Length || j < b.Keys.Length)
        {
            var c = i >= a.Keys.Length ? 1 : j >= b.Keys.Length ? -1 : string.CompareOrdinal(a.Keys[i], b.Keys[j]);
            if (c < 0) keys.Add(a.Keys[i++]);
            else if (c > 0) keys.Add(b.Keys[j++]);
            else
            {
                if (a.Hashes[i] != b.Hashes[j]) keys.Add(a.Keys[i]);
                i++;
                j++;
            }
        }
        if (a.SourceId != b.SourceId) keys.Add(HistoryKeys.Source);
        if (a.TargetId != b.TargetId) keys.Add(HistoryKeys.Target);
        if ((a.IsDiagram || b.IsDiagram) && a.LayoutHash != b.LayoutHash) keys.Add(HistoryKeys.Layout);
        return keys;
    }

    private static ElementChange Row(FlatModel m, FlatElement e, ElementChangeKind kind, IReadOnlyList<string> keys) =>
        new(e.Id, kind, keys, e.Kind, m.Label(e), e.OwnerId, e.SourceId, e.TargetId);

    private static ElementChange Membership(FlatModel m, string elementId, FlatElement diagram, ElementChangeKind kind)
    {
        var e = m.Find(elementId);
        return new ElementChange(elementId, kind, [], e?.Kind ?? "", e is null ? elementId : m.Label(e), e?.OwnerId, null, null, diagram.Id, diagram.DisplayName);
    }
}
