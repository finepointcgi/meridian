using System.Text.Json;
using System.Text.RegularExpressions;

namespace Meridian.Data;

/// <summary>Lifecycle of a review request: open → approved / changes requested → closed.</summary>
public enum ReviewState { Open = 0, Approved = 1, ChangesRequested = 2, Closed = 3 }

public enum ReviewVote { Pending = 0, Approve = 1, RequestChanges = 2 }

/// <summary>What a review covers: an explicit set of elements, a package (and everything it owns) or the changes between two versions.</summary>
public enum ReviewScopeKind { Elements = 0, Package = 1, Versions = 2 }

public sealed record ReviewerInfo(UserAccount User, ReviewVote Vote, DateTime? VotedAt);

/// <summary>A review request. <see cref="ElementIds"/> holds the chosen elements (Elements) or the package id (Package).
/// <see cref="BaseVersionId"/> is the version the diff is taken from; <see cref="HeadVersionId"/> null means the live model.</summary>
public sealed record ReviewInfo(
    long Id, Guid ProjectId, string Title, string Description, UserAccount Author, ReviewState State,
    ReviewScopeKind ScopeKind, IReadOnlyList<string> ElementIds, long? BaseVersionId, long? HeadVersionId, bool ApplyStatus,
    DateTime CreatedAt, DateTime UpdatedAt, IReadOnlyList<ReviewerInfo> Reviewers)
{
    public bool IsClosed => State == ReviewState.Closed;
    public bool IsReviewer(Guid userId) => Reviewers.Any(r => r.User.Id == userId);
    public ReviewVote VoteOf(Guid userId) => Reviewers.FirstOrDefault(r => r.User.Id == userId)?.Vote ?? ReviewVote.Pending;
    /// <summary>Open reviews where this user still has to vote.</summary>
    public bool WaitingFor(Guid userId) => State is ReviewState.Open or ReviewState.ChangesRequested && IsReviewer(userId) && VoteOf(userId) == ReviewVote.Pending;
}

/// <summary>Everything needed to create a review.</summary>
public sealed record ReviewDraft(
    string Title, string Description, ReviewScopeKind ScopeKind, IReadOnlyList<string> ElementIds,
    long? BaseVersionId, long? HeadVersionId, bool ApplyStatus, IReadOnlyList<Guid> Reviewers);

/// <summary>A comment inside a review, optionally on one in-scope element, optionally a reply to another comment.</summary>
public sealed record ReviewCommentInfo(long Id, long ReviewId, string? ElementId, long? ParentId, UserAccount Author, string Body, DateTime CreatedAt);

/// <summary>An element a reviewer has ticked off as reviewed (reviews double as checklists).</summary>
public sealed record ReviewCheck(string ElementId, Guid UserId);

public static class NotificationKinds
{
    public const string Mention = "mention";
    public const string ReviewRequested = "review_requested";
    public const string ReviewVote = "review_vote";
    public const string ReviewState = "review_state";
    public const string Comment = "comment";
    /// <summary>A used project's source moved ahead of the pin (docs/LIBRARIES.md); the element id carries the usage id ("N" format).</summary>
    public const string LibraryUpdate = "library_update";
}

public sealed record NotificationDraft(Guid UserId, string Kind, string Text, Guid? ProjectId = null, string? ElementId = null, long? ReviewId = null, Guid? ActorId = null);

public sealed record NotificationInfo(long Id, Guid UserId, string Kind, Guid? ProjectId, string? ProjectName, string? ElementId, long? ReviewId,
    string Text, bool Read, DateTime CreatedAt, UserAccount? Actor);

/// <summary>Review rules shared by every store and the UI.</summary>
public static class ReviewRules
{
    /// <summary>The state implied by the votes: any "request changes" wins, every reviewer approving approves; closed stays closed.</summary>
    public static ReviewState Compute(ReviewState current, IReadOnlyCollection<ReviewerInfo> reviewers)
    {
        if (current == ReviewState.Closed) return ReviewState.Closed;
        if (reviewers.Any(r => r.Vote == ReviewVote.RequestChanges)) return ReviewState.ChangesRequested;
        if (reviewers.Count > 0 && reviewers.All(r => r.Vote == ReviewVote.Approve)) return ReviewState.Approved;
        return ReviewState.Open;
    }

    public static string Label(ReviewState s) => s switch
    {
        ReviewState.Open => "Open",
        ReviewState.Approved => "Approved",
        ReviewState.ChangesRequested => "Changes requested",
        _ => "Closed",
    };

    internal static string EncodeIds(IReadOnlyList<string> ids) => JsonSerializer.Serialize(ids);

    internal static IReadOnlyList<string> DecodeIds(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    internal static void Check(ReviewDraft d)
    {
        if (string.IsNullOrWhiteSpace(d.Title)) throw new InvalidOperationException("A review needs a title.");
        if (d.ScopeKind != ReviewScopeKind.Versions && d.ElementIds.Count == 0) throw new InvalidOperationException("Choose at least one element or a package to review.");
        if (d.ScopeKind == ReviewScopeKind.Versions && d.BaseVersionId is null) throw new InvalidOperationException("Choose the version the review starts from.");
    }
}

/// <summary>@mentions in comment text: <c>@username</c> tokens that name a project member.</summary>
public static partial class Mentions
{
    [GeneratedRegex(@"(?<![\w@])@([A-Za-z0-9._-]{2,40})")]
    private static partial Regex Token();

    /// <summary>A run of comment text: plain text, or a mention of <see cref="User"/>.</summary>
    public sealed record Segment(string Text, UserAccount? User);

    /// <summary>Splits text into plain runs and mentions of known users (trailing '.', '-' or '_' is punctuation unless part of a username).</summary>
    public static IReadOnlyList<Segment> Split(string text, IEnumerable<UserAccount> people)
    {
        var byName = people.GroupBy(p => p.Username, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<Segment>();
        var at = 0;
        foreach (Match m in Token().Matches(text))
        {
            var name = m.Groups[1].Value;
            UserAccount? user = null;
            while (name.Length >= 2 && !byName.TryGetValue(name, out user) && name[^1] is '.' or '-' or '_') name = name[..^1];
            if (user is null) continue;
            if (m.Index > at) result.Add(new Segment(text[at..m.Index], null));
            result.Add(new Segment("@" + name, user));
            at = m.Index + 1 + name.Length;
        }
        if (at < text.Length) result.Add(new Segment(text[at..], null));
        return result;
    }

    /// <summary>The distinct users mentioned in the text.</summary>
    public static IReadOnlyList<UserAccount> Find(string text, IEnumerable<UserAccount> people) =>
        Split(text, people).Where(s => s.User != null).Select(s => s.User!).DistinctBy(u => u.Id).ToList();
}
