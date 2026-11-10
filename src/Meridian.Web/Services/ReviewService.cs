using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;

namespace Meridian.Web.Services;

/// <summary>
/// Review workflow, threaded comments, @mentions and notifications for one browser tab. Store writes go through
/// <see cref="IMeridianStore"/>; other tabs on the project refresh through the session's comment signal, and
/// notified users' tabs refresh through <see cref="CollaborationHub.NotifyUser"/>.
/// </summary>
public sealed class ReviewService(IMeridianStore db, CollaborationHub hub, Workspace ws)
{
    /// <summary>Element field naming the responsible user (username); they hear about comments on the element.</summary>
    public const string OwnerKey = "owner";

    private Guid? _membersFor;
    private DateTime _membersAt;
    private IReadOnlyList<ProjectMember> _members = [];

    public IMeridianStore Store => db;

    public async Task<IReadOnlyList<ProjectMember>> MembersAsync(bool refresh = false)
    {
        if (ws.Session is not { } s) return [];
        if (refresh || _membersFor != s.ProjectId || (DateTime.UtcNow - _membersAt).TotalSeconds > 30)
        {
            _members = await db.MembersAsync(s.ProjectId);
            _membersFor = s.ProjectId;
            _membersAt = DateTime.UtcNow;
        }
        return _members;
    }

    public async Task<IReadOnlyList<UserAccount>> PeopleAsync() => (await MembersAsync()).Select(m => m.User).ToList();

    /// <summary>Every tab on the project (this one included) reloads comments and reviews.</summary>
    private void Signal() => ws.Session?.NotifyComments();

    private async Task NotifyAsync(NotificationDraft n)
    {
        if (n.UserId == Guid.Empty || n.UserId == n.ActorId) return;
        await db.AddNotificationAsync(n);
        hub.NotifyUser(n.UserId);
    }

    private string Me => ws.User?.DisplayName ?? "Someone";

    // ---------------------------------------------------------------- reviews

    public async Task<long> CreateAsync(ReviewDraft draft)
    {
        if (ws.Session is not { } s || ws.User is not { } me) throw new InvalidOperationException("Open a project first.");
        var id = await db.CreateReviewAsync(s.ProjectId, draft, me.Id);
        foreach (var r in draft.Reviewers.Distinct())
            await NotifyAsync(new(r, NotificationKinds.ReviewRequested, $"{Me} requested your review: {draft.Title.Trim()}", s.ProjectId, draft.ElementIds.FirstOrDefault(), id, me.Id));
        Signal();
        return id;
    }

    public async Task<IReadOnlyList<ReviewInfo>> ListAsync() => ws.Session is { } s ? await db.ListReviewsAsync(s.ProjectId) : [];

    /// <summary>Records the current user's vote and moves the review to the state the votes imply. Returns the updated review.</summary>
    public async Task<ReviewInfo?> VoteAsync(ReviewInfo review, ReviewVote vote)
    {
        if (ws.User is not { } me || review.IsClosed) return review;
        await db.SetReviewVoteAsync(review.Id, me.Id, vote);
        var updated = await db.GetReviewAsync(review.Id);
        if (updated is null) return null;
        var verb = vote switch { ReviewVote.Approve => "approved", ReviewVote.RequestChanges => "requested changes on", _ => "withdrew their vote on" };
        await NotifyAsync(new(updated.Author.Id, NotificationKinds.ReviewVote, $"{Me} {verb} “{updated.Title}”", updated.ProjectId, null, updated.Id, me.Id));
        updated = await MoveToAsync(updated, ReviewRules.Compute(updated.State, updated.Reviewers));
        Signal();
        return updated;
    }

    /// <summary>Closes a review, or reopens it (back to the state its votes imply).</summary>
    public async Task<ReviewInfo?> SetClosedAsync(ReviewInfo review, bool closed)
    {
        var target = closed ? ReviewState.Closed : ReviewRules.Compute(ReviewState.Open, review.Reviewers);
        var updated = await MoveToAsync(review, target);
        Signal();
        return updated;
    }

    private async Task<ReviewInfo> MoveToAsync(ReviewInfo review, ReviewState state)
    {
        if (state == review.State) return review;
        await db.SetReviewStateAsync(review.Id, state);
        var text = $"“{review.Title}” is now {ReviewRules.Label(state).ToLowerInvariant()}";
        foreach (var u in review.Reviewers.Select(r => r.User.Id).Append(review.Author.Id).Distinct())
            await NotifyAsync(new(u, NotificationKinds.ReviewState, text, review.ProjectId, null, review.Id, ws.User?.Id));
        return review with { State = state };
    }

    public async Task DeleteAsync(ReviewInfo review)
    {
        await db.DeleteReviewAsync(review.Id);
        Signal();
    }

    public async Task SetCheckedAsync(ReviewInfo review, string elementId, bool done)
    {
        if (ws.User is not { } me) return;
        await db.SetReviewCheckAsync(review.Id, elementId, me.Id, done);
        Signal();
    }

    /// <summary>Loads the models the review compares: the base version and the head version (or the live model).</summary>
    public async Task<(ModelRepository? Before, ModelRepository After)> ModelsAsync(ReviewInfo review)
    {
        ModelRepository? before = null, after = ws.Model;
        if (review.BaseVersionId is { } b && await db.LoadVersionAsync(review.ProjectId, b) is { } bj) before = ProjectFile.Deserialize(bj);
        if (review.HeadVersionId is { } h && await db.LoadVersionAsync(review.ProjectId, h) is { } hj) after = ProjectFile.Deserialize(hj);
        return (before, after);
    }

    public async Task<IReadOnlyList<ElementDiff>?> DiffAsync(ReviewInfo review)
    {
        var (before, after) = await ModelsAsync(review);
        return before is null ? null : ReviewScope.Diff(before, after, review);
    }

    /// <summary>Moves the in-scope requirements' status to approved in the live model (one undoable step). Returns the count.</summary>
    public async Task<int> ApplyApprovedStatusAsync(ReviewInfo review)
    {
        if (ws.ReadOnly) throw new InvalidOperationException("Only editors can change requirement status.");
        var diff = review.ScopeKind == ReviewScopeKind.Versions ? await DiffAsync(review) : null;
        return ReviewScope.ApplyApprovedStatus(ws.Model, review, diff);
    }

    // ---------------------------------------------------------------- review comments

    public async Task<long> CommentOnReviewAsync(ReviewInfo review, string? elementId, long? parentId, string body)
    {
        if (ws.User is not { } me || string.IsNullOrWhiteSpace(body)) return 0;
        body = body.Trim();
        var id = await db.AddReviewCommentAsync(review.Id, elementId, parentId, me.Id, body);
        var mentioned = Mentions.Find(body, await PeopleAsync()).Select(u => u.Id).ToList();
        if (mentioned.Count > 0) await db.AddMentionsAsync(review.ProjectId, "review_comment", id, mentioned);
        var where = elementId != null && ws.Model.Find(elementId) is { } el ? $" on {el.DisplayName}" : "";
        foreach (var u in mentioned)
            await NotifyAsync(new(u, NotificationKinds.Mention, $"{Me} mentioned you in “{review.Title}”{where}: {Excerpt(body)}", review.ProjectId, elementId, review.Id, me.Id));
        var others = new HashSet<Guid>(review.Reviewers.Select(r => r.User.Id).Append(review.Author.Id));
        if (parentId != null && (await db.ListReviewCommentsAsync(review.Id)).FirstOrDefault(c => c.Id == parentId) is { } parent) others.Add(parent.Author.Id);
        foreach (var u in others.Except(mentioned))
            await NotifyAsync(new(u, NotificationKinds.Comment, $"{Me} commented on “{review.Title}”{where}: {Excerpt(body)}", review.ProjectId, elementId, review.Id, me.Id));
        Signal();
        return id;
    }

    public async Task DeleteReviewCommentAsync(ReviewCommentInfo c)
    {
        await db.DeleteReviewCommentAsync(c.Id);
        Signal();
    }

    // ---------------------------------------------------------------- element comments

    /// <summary>Posts a comment (or a reply) on an element, records its mentions and notifies the mentioned users,
    /// the element's owner and everyone who already commented on it.</summary>
    public async Task<long> CommentOnElementAsync(string elementId, string body, long? parentId = null)
    {
        if (ws.Session is not { } s || ws.User is not { } me || string.IsNullOrWhiteSpace(body)) return 0;
        body = body.Trim();
        var earlier = (await db.ListCommentsAsync(s.ProjectId)).Where(c => c.ElementId == elementId).ToList();
        var id = await db.AddCommentAsync(s.ProjectId, elementId, me.Id, body);
        if (parentId is { } p) await db.SetCommentParentAsync(id, p);
        var people = await PeopleAsync();
        var mentioned = Mentions.Find(body, people).Select(u => u.Id).ToList();
        if (mentioned.Count > 0) await db.AddMentionsAsync(s.ProjectId, "comment", id, mentioned);
        var element = ws.Model.Find(elementId);
        var name = element?.DisplayName ?? "an element";
        foreach (var u in mentioned)
            await NotifyAsync(new(u, NotificationKinds.Mention, $"{Me} mentioned you on {name}: {Excerpt(body)}", s.ProjectId, elementId, null, me.Id));
        var watchers = new HashSet<Guid>(earlier.Select(c => c.Author.Id));
        if (element?.Get(OwnerKey) is { Length: > 0 } owner && people.FirstOrDefault(u => string.Equals(u.Username, owner, StringComparison.OrdinalIgnoreCase)) is { } o)
            watchers.Add(o.Id);
        foreach (var u in watchers.Except(mentioned))
            await NotifyAsync(new(u, NotificationKinds.Comment, $"{Me} commented on {name}: {Excerpt(body)}", s.ProjectId, elementId, null, me.Id));
        s.NotifyComments();
        return id;
    }

    public static string Excerpt(string body) => body.Length > 90 ? body[..90].TrimEnd() + "…" : body;
}
