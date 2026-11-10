using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>Reviews, threaded replies, mentions and notifications in the SQLite store (same schema as PostgreSQL).</summary>
public sealed class ReviewStoreTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-review-test-{Guid.NewGuid():N}.db");
    private SqliteStore _store = default!;

    public async Task InitializeAsync()
    {
        _store = new SqliteStore(_path);
        await _store.MigrateAsync();
        await _store.MigrateAsync(); // idempotent
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Reviews_Votes_Comments_And_Checks_Round_Trip()
    {
        var ann = await _store.CreateUserAsync("ann", "Ann Lee", "correct horse");
        var ben = await _store.CreateUserAsync("ben", "Ben Ito", "battery staple");
        var cat = await _store.CreateUserAsync("cat", "Cat Ruiz", "staple horse1");
        var json = ProjectFile.Serialize(SampleModels.Vehicle());
        var pid = await _store.CreateProjectAsync("Vehicle", json, ann.Id);
        await _store.CommitVersionAsync(pid, "baseline", json, ann.Id);
        var baseId = (await _store.ListVersionsAsync(pid)).Single().Id;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.CreateReviewAsync(pid, new ReviewDraft(" ", "", ReviewScopeKind.Elements, ["x"], null, null, false, []), ann.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.CreateReviewAsync(pid, new ReviewDraft("Empty", "", ReviewScopeKind.Elements, [], null, null, false, []), ann.Id));

        var id = await _store.CreateReviewAsync(pid, new ReviewDraft("Braking reqs", "Check REQ-004", ReviewScopeKind.Elements, ["e1", "e2"], baseId, null, true, [ben.Id, cat.Id, ben.Id]), ann.Id);
        var review = (await _store.ListReviewsAsync(pid)).Single();
        Assert.Equal(id, review.Id);
        Assert.Equal("Braking reqs", review.Title);
        Assert.Equal(ReviewState.Open, review.State);
        Assert.Equal(["e1", "e2"], review.ElementIds);
        Assert.Equal(baseId, review.BaseVersionId);
        Assert.Null(review.HeadVersionId);
        Assert.True(review.ApplyStatus);
        Assert.Equal("ann", review.Author.Username);
        Assert.Equal(2, review.Reviewers.Count);
        Assert.True(review.WaitingFor(ben.Id));
        Assert.False(review.WaitingFor(ann.Id));

        await _store.SetReviewVoteAsync(id, ben.Id, ReviewVote.Approve);
        review = (await _store.GetReviewAsync(id))!;
        Assert.Equal(ReviewVote.Approve, review.VoteOf(ben.Id));
        Assert.NotNull(review.Reviewers.Single(r => r.User.Id == ben.Id).VotedAt);
        Assert.False(review.WaitingFor(ben.Id));
        Assert.Equal(ReviewState.Open, ReviewRules.Compute(review.State, review.Reviewers));
        await _store.SetReviewVoteAsync(id, cat.Id, ReviewVote.Approve);
        review = (await _store.GetReviewAsync(id))!;
        Assert.Equal(ReviewState.Approved, ReviewRules.Compute(review.State, review.Reviewers));
        await _store.SetReviewStateAsync(id, ReviewState.Approved);
        Assert.Equal(ReviewState.Approved, (await _store.GetReviewAsync(id))!.State);

        var c1 = await _store.AddReviewCommentAsync(id, null, null, ben.Id, "Looks good @ann");
        var c2 = await _store.AddReviewCommentAsync(id, "e1", c1, ann.Id, "Thanks");
        var comments = await _store.ListReviewCommentsAsync(id);
        Assert.Equal([c1, c2], comments.Select(c => c.Id));
        Assert.Equal(c1, comments[1].ParentId);
        Assert.Equal("e1", comments[1].ElementId);
        await _store.DeleteReviewCommentAsync(c1);   // replies go with their parent
        Assert.Empty(await _store.ListReviewCommentsAsync(id));

        await _store.SetReviewCheckAsync(id, "e1", ben.Id, true);
        await _store.SetReviewCheckAsync(id, "e1", ben.Id, true);
        await _store.SetReviewCheckAsync(id, "e2", ben.Id, true);
        await _store.SetReviewCheckAsync(id, "e2", ben.Id, false);
        Assert.Equal(["e1"], (await _store.ListReviewChecksAsync(id)).Select(c => c.ElementId));

        await _store.DeleteReviewAsync(id);
        Assert.Empty(await _store.ListReviewsAsync(pid));
        Assert.Null(await _store.GetReviewAsync(id));
    }

    [Fact]
    public async Task Replies_Mentions_And_Notifications_Round_Trip()
    {
        var ann = await _store.CreateUserAsync("ann", "Ann Lee", "correct horse");
        var ben = await _store.CreateUserAsync("ben", "Ben Ito", "battery staple");
        var pid = await _store.CreateProjectAsync("Vehicle", ProjectFile.Serialize(SampleModels.Vehicle()), ann.Id);

        var root = await _store.AddCommentAsync(pid, "el", ann.Id, "Is this right, @ben?");
        var reply = await _store.AddCommentAsync(pid, "el", ben.Id, "Yes");
        await _store.SetCommentParentAsync(reply, root);
        Assert.Equal(root, (await _store.ListCommentParentsAsync(pid))[reply]);
        await _store.AddMentionsAsync(pid, "comment", root, [ben.Id, ben.Id]);
        Assert.Equal([ben.Id], await _store.ListMentionsAsync("comment", root));

        await _store.AddNotificationAsync(new NotificationDraft(ben.Id, NotificationKinds.Mention, "Ann mentioned you", pid, "el", null, ann.Id));
        await _store.AddNotificationAsync(new NotificationDraft(ben.Id, NotificationKinds.Comment, "Ann commented", pid));
        await _store.AddNotificationAsync(new NotificationDraft(ann.Id, NotificationKinds.Comment, "Ben replied", pid));
        Assert.Equal(2, await _store.UnreadNotificationCountAsync(ben.Id));
        var inbox = await _store.ListNotificationsAsync(ben.Id);
        Assert.Equal(2, inbox.Count);
        var mention = inbox.Single(n => n.Kind == NotificationKinds.Mention);
        Assert.Equal("Vehicle", mention.ProjectName);
        Assert.Equal("el", mention.ElementId);
        Assert.Equal("ann", mention.Actor!.Username);
        Assert.Null(inbox.Single(n => n.Kind == NotificationKinds.Comment).Actor);

        await _store.MarkNotificationReadAsync(mention.Id, ann.Id);     // someone else's: no effect
        Assert.Equal(2, await _store.UnreadNotificationCountAsync(ben.Id));
        await _store.MarkNotificationReadAsync(mention.Id, ben.Id);
        Assert.Equal(1, await _store.UnreadNotificationCountAsync(ben.Id));
        Assert.True((await _store.ListNotificationsAsync(ben.Id)).Single(n => n.Id == mention.Id).Read);
        await _store.MarkAllNotificationsReadAsync(ben.Id);
        Assert.Equal(0, await _store.UnreadNotificationCountAsync(ben.Id));
        Assert.Equal(1, await _store.UnreadNotificationCountAsync(ann.Id));

        await _store.DeleteProjectAsync(pid);   // cascades to comments, replies, mentions and notifications
        Assert.Empty(await _store.ListNotificationsAsync(ben.Id));
        Assert.Empty(await _store.ListMentionsAsync("comment", root));
    }

    [Fact]
    public void Review_State_Follows_The_Votes()
    {
        UserAccount U(string n) => new(Guid.NewGuid(), n, n, "#fff");
        ReviewerInfo R(ReviewVote v) => new(U("x"), v, null);
        Assert.Equal(ReviewState.Open, ReviewRules.Compute(ReviewState.Open, []));
        Assert.Equal(ReviewState.Open, ReviewRules.Compute(ReviewState.Approved, [R(ReviewVote.Approve), R(ReviewVote.Pending)]));
        Assert.Equal(ReviewState.Approved, ReviewRules.Compute(ReviewState.Open, [R(ReviewVote.Approve), R(ReviewVote.Approve)]));
        Assert.Equal(ReviewState.ChangesRequested, ReviewRules.Compute(ReviewState.Open, [R(ReviewVote.Approve), R(ReviewVote.RequestChanges)]));
        Assert.Equal(ReviewState.Closed, ReviewRules.Compute(ReviewState.Closed, [R(ReviewVote.Approve)]));
    }

    [Fact]
    public void Mentions_Match_Members_And_Ignore_Punctuation_And_Emails()
    {
        var ann = new UserAccount(Guid.NewGuid(), "ann", "Ann Lee", "#fff");
        var jo = new UserAccount(Guid.NewGuid(), "jo.smith", "Jo Smith", "#fff");
        var people = new[] { ann, jo };
        var parts = Mentions.Split("Hi @ann, ask @jo.smith. Mail ann@example.com or @nobody.", people);
        Assert.Equal(["Hi ", "@ann", ", ask ", "@jo.smith", ". Mail ann@example.com or @nobody."], parts.Select(p => p.Text));
        Assert.Equal([ann.Id, jo.Id], parts.Where(p => p.User != null).Select(p => p.User!.Id));
        Assert.Equal([ann.Id], Mentions.Find("@ANN and @ann again.", people).Select(u => u.Id));
        Assert.Empty(Mentions.Find("no mentions here", people));
    }
}
