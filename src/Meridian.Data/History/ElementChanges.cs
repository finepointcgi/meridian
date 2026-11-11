using Meridian.Serialization.History;

namespace Meridian.Data.History;

/// <summary>What kind of commit a version is, for labelling history (stored as a small integer: keep the values).</summary>
public enum HistoryVersionKind
{
    Commit = 0,
    /// <summary>A merge commit; the changes it brings are attributed to it, with the source branch.</summary>
    Merge = 1,
    /// <summary>A code-sync commit (a push applied to the review branch, or a review decision).</summary>
    CodeSync = 2,
    /// <summary>Written by the system (no author), e.g. a library update applied to a closed line.</summary>
    System = 3,
}

/// <summary>
/// A committed version with what the element change index knows about it: its parent (the version it was compared with),
/// its kind, and whether its changes are indexed yet.
/// </summary>
public sealed record HistoryVersion(long Id, int Number, string Message, Guid? AuthorId, string Author, DateTime CreatedAt, Guid? BranchId,
                                    string BranchName, bool Indexed, long? ParentVersionId, HistoryVersionKind Kind, string? MergeSource,
                                    bool KindRecorded);

/// <summary>A stored row of the element change index (see <see cref="ElementChange"/>).</summary>
public sealed record ElementChangeRow(long VersionId, string ElementId, ElementChangeKind Change, IReadOnlyList<string> Fields, string ElementKind,
                                      string Label, string? OwnerId, string? SourceId, string? TargetId, string? DiagramId, string? DiagramName);

/// <summary>Column encodings shared by both stores.</summary>
public static class ElementChangeCodes
{
    /// <summary>Changed field keys are stored in one text column, one per line.</summary>
    public static string JoinFields(IReadOnlyList<string> keys) => string.Join('\n', keys);

    public static IReadOnlyList<string> SplitFields(string? s) => string.IsNullOrEmpty(s) ? [] : s.Split('\n');

    /// <summary>Rows for very large commits are capped per text column; ids and keys are short, labels are not.</summary>
    public static string Clip(string s, int max = 400) => s.Length <= max ? s : s[..max] + "…";
}
