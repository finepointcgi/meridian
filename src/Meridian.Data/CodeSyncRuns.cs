using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meridian.Data;

/// <summary>Where a code-sync run stands. Stored as its name.</summary>
public enum CodeSyncRunStatus { Pending, NoChanges, AwaitingReview, Accepted, Rejected, Failed }

/// <summary>How an element changed in one code-sync run.</summary>
public enum CodeSyncChangeKind { Added, Changed, Removed }

/// <summary>Review state of one change of a run.</summary>
public enum CodeSyncChangeState { Pending, Accepted, Rejected }

/// <summary>One element a code-sync run added, changed or removed (relationships and generated diagrams included).</summary>
public sealed record CodeSyncRunChange(string ElementId, CodeSyncChangeKind Kind, string Name, string Path, string ElementKind,
                                       CodeSyncChangeState State = CodeSyncChangeState.Pending);

/// <summary>
/// One code sync: a push to a connected git repository applied to the project's review branch
/// (<c>code-sync/&lt;repo&gt;</c>). <see cref="ChangesJson"/> is a JSON array of <see cref="CodeSyncRunChange"/>
/// (see <see cref="CodeSyncRuns"/>). <see cref="BranchId"/> is null when the run failed before a branch existed.
/// </summary>
public sealed record CodeSyncRunRecord(
    Guid Id, Guid ProjectId, Guid ConnectionId, Guid? BranchId, string Repo, string Commit, string? CommitUrl,
    string? Author, string? Message, CodeSyncRunStatus Status, string Summary,
    int Added, int Changed, int Removed, string ChangesJson, DateTime CreatedAt, DateTime UpdatedAt)
{
    public IReadOnlyList<CodeSyncRunChange> Changes => CodeSyncRuns.ParseChanges(ChangesJson);
    public string ShortCommit => Commit.Length > 7 ? Commit[..7] : Commit;
    /// <summary>True while some of its changes wait for a decision.</summary>
    public bool IsOpen => Status == CodeSyncRunStatus.AwaitingReview;
}

/// <summary>Rules and JSON helpers shared by every store for code-sync run records.</summary>
public static class CodeSyncRuns
{
    /// <summary>Largest change list stored for one run (a huge first import keeps its counts but lists this many).</summary>
    public const int MaxChanges = 20_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string SerializeChanges(IEnumerable<CodeSyncRunChange> changes) => JsonSerializer.Serialize(changes.Take(MaxChanges).ToList(), Json);

    public static IReadOnlyList<CodeSyncRunChange> ParseChanges(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<CodeSyncRunChange>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>Checks that <paramref name="json"/> is a JSON array; returns it unchanged.</summary>
    public static string CheckChangesJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Code-sync changes must be a JSON array.");
        }
        catch (JsonException) { throw new InvalidOperationException("Code-sync changes must be valid JSON."); }
        return json;
    }

    public static CodeSyncRunStatus ParseStatus(string s) => Enum.TryParse<CodeSyncRunStatus>(s, out var v) ? v : CodeSyncRunStatus.Failed;

    /// <summary>Clips free text from git hosts (commit messages, authors) to a sane length.</summary>
    public static string? Clip(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
