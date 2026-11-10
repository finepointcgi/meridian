using Bunit;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

public class ReviewTests : TestContext
{
    public ReviewTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static ReviewInfo Review(ReviewScopeKind kind, params string[] ids) =>
        new(7, Guid.NewGuid(), "R", "", new UserAccount(Guid.NewGuid(), "a", "A", "#fff"), ReviewState.Approved, kind, ids, null, null, true,
            DateTime.UtcNow, DateTime.UtcNow, []);

    [Fact]
    public void Package_Scope_Covers_Owned_Requirements_And_Approval_Is_One_Undoable_Step()
    {
        var model = SampleModels.Vehicle();
        var reqs = model.OfKind(ElementKind.Requirement).ToList();
        var pkg = reqs[0].Owner!;
        var review = Review(ReviewScopeKind.Package, pkg.Id);

        var items = ReviewScope.Items(model, review, null);
        Assert.Same(pkg, items[0]);
        Assert.DoesNotContain(items, e => e is Relationship || e is Diagram);
        var inScope = ReviewScope.Requirements(model, review, null);
        Assert.Equal(pkg.Descendants().Count(e => e.Kind == ElementKind.Requirement), inScope.Count);
        Assert.NotEmpty(inScope);

        var changed = ReviewScope.ApplyApprovedStatus(model, review, null);
        Assert.Equal(inScope.Count, changed);
        Assert.All(inScope, r => Assert.Equal("approved", r.Get("status")));
        Assert.Equal(0, ReviewScope.ApplyApprovedStatus(model, review, null));   // idempotent
        model.History.Undo();
        Assert.All(inScope, r => Assert.Equal("", r.Get("status")));
    }

    [Fact]
    public void Element_Scope_Diff_Only_Shows_In_Scope_Changes()
    {
        var before = SampleModels.Vehicle();
        var after = ProjectFile.Deserialize(ProjectFile.Serialize(before));
        var leaves = after.OfKind(ElementKind.Requirement).Where(r => !r.OwnedElements.Any(o => o.Kind == ElementKind.Requirement)).ToList();
        var reqs = new[] { leaves[0], leaves.First(r => !r.IsAncestorOf(leaves[0]) && !leaves[0].IsAncestorOf(r) && r != leaves[0]) };
        after.Set(reqs[0], FieldKeys.Text, "Changed text");
        after.Set(reqs[1], FieldKeys.Text, "Out of scope change");
        var review = Review(ReviewScopeKind.Elements, reqs[0].Id);

        var diff = ReviewScope.Diff(before, after, review);
        Assert.Equal([reqs[0].Id], diff.Select(d => d.Id));
        Assert.Equal(2, ReviewScope.Diff(before, after, Review(ReviewScopeKind.Versions)).Count);
        Assert.Equal([reqs[0]], ReviewScope.Items(after, review, diff));
        Assert.Equal(2, ReviewScope.Items(after, Review(ReviewScopeKind.Versions), ReviewScope.Diff(before, after, Review(ReviewScopeKind.Versions))).Count);
    }

    [Fact]
    public void Inbox_Watchers_Are_Called_Until_Disposed()
    {
        var hub = new CollaborationHub(null!, NullLogger<CollaborationHub>.Instance);
        var user = Guid.NewGuid();
        var calls = 0;
        var watch = hub.WatchInbox(user, () => calls++);
        Assert.True(hub.IsOnline(user));
        hub.NotifyUser(user);
        hub.NotifyUser(Guid.NewGuid());
        Assert.Equal(1, calls);
        watch.Dispose();
        hub.NotifyUser(user);
        Assert.Equal(1, calls);
        Assert.False(hub.IsOnline(user));
    }

    [Fact]
    public void Mention_Input_Suggests_Members_And_Inserts_The_Pick()
    {
        var people = new[] { new UserAccount(Guid.NewGuid(), "ann", "Ann Lee", "#4f8cff"), new UserAccount(Guid.NewGuid(), "ben", "Ben Ito", "#2dd4bf") };
        string? value = null;
        var cut = RenderComponent<MentionInput>(p => p.Add(x => x.People, people).Add(x => x.ValueChanged, v => value = v));
        cut.Find("textarea").Input("Ping @an");
        cut.InvokeAsync(() => cut.Instance.MentionQuery("an", 5, 8));   // what the browser module reports for the caret
        var items = cut.FindAll(".mention-item");
        Assert.Single(items);
        Assert.Contains("Ann Lee", items[0].TextContent);
        items[0].MouseDown();
        Assert.Equal("Ping @ann ", value);
        Assert.Empty(cut.FindAll(".mention-pop"));
        Assert.Equal(["ben"], MentionInput.Match(people, "it").Select(u => u.Username));
    }

    [Fact]
    public void Mention_Text_Renders_Member_Mentions_As_Chips()
    {
        var people = new[] { new UserAccount(Guid.NewGuid(), "ann", "Ann Lee", "#4f8cff") };
        var cut = RenderComponent<MentionText>(p => p.Add(x => x.Text, "cc @ann and @ghost").Add(x => x.People, people));
        var chip = cut.Find(".mention-chip");
        Assert.Equal("@ann", chip.TextContent);
        Assert.Equal("Ann Lee", chip.GetAttribute("title"));
        Assert.Contains("@ghost", cut.Markup);
    }

    [Fact]
    public async Task Review_Service_Notifies_Reviewers_Mentions_Votes_And_Element_Watchers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meridian-rs-{Guid.NewGuid():N}.db");
        try
        {
            var db = new SqliteStore(path);
            await db.MigrateAsync();
            var ann = await db.CreateUserAsync("ann", "Ann Lee", "correct horse");
            var ben = await db.CreateUserAsync("ben", "Ben Ito", "battery staple");
            var pid = await db.CreateProjectAsync("Vehicle", ProjectFile.Serialize(SampleModels.Vehicle()), ann.Id);
            await db.SetMemberAsync(pid, ben.Id, ProjectRole.Editor);
            var hub = new CollaborationHub(db, NullLogger<CollaborationHub>.Instance);
            var pings = 0;
            using var watch = hub.WatchInbox(ben.Id, () => pings++);

            var ws = new Workspace { User = ann };
            Assert.True(await ws.JoinAsync(hub, pid, ProjectRole.Owner));
            var svc = new ReviewService(db, hub, ws);
            var req = ws.Model.OfKind(ElementKind.Requirement).First();

            var id = await svc.CreateAsync(new ReviewDraft("Check it", "", ReviewScopeKind.Elements, [req.Id], null, null, true, [ben.Id]));
            var review = (await db.GetReviewAsync(id))!;
            await svc.CommentOnReviewAsync(review, req.Id, null, "@ben please look");
            await svc.CommentOnElementAsync(req.Id, "First note");

            var inbox = await db.ListNotificationsAsync(ben.Id);
            Assert.Contains(inbox, n => n.Kind == NotificationKinds.ReviewRequested && n.ReviewId == id);
            Assert.Contains(inbox, n => n.Kind == NotificationKinds.Mention && n.ReviewId == id);
            Assert.DoesNotContain(inbox, n => n.Kind == NotificationKinds.Comment && n.ReviewId == id);   // the mention covers it
            Assert.Equal(2, pings);   // delivered live to Ben's open tabs
            Assert.Empty(await db.ListNotificationsAsync(ann.Id));                                        // never notified about your own actions
            Assert.Single(await db.ListMentionsAsync("review_comment", (await db.ListReviewCommentsAsync(id)).Single().Id));

            // Ben is the element owner, so he hears about comments on it; Ben voting moves the review and tells Ann.
            ws.Model.Set(req, ReviewService.OwnerKey, "ben");
            await svc.CommentOnElementAsync(req.Id, "Owner should see this");
            Assert.Contains(await db.ListNotificationsAsync(ben.Id), n => n.Kind == NotificationKinds.Comment && n.Text.Contains("Owner should see"));

            var benWs = new Workspace { User = ben };
            Assert.True(await benWs.JoinAsync(hub, pid, ProjectRole.Editor));
            var benSvc = new ReviewService(db, hub, benWs);
            var voted = await benSvc.VoteAsync(review, ReviewVote.Approve);
            Assert.Equal(ReviewState.Approved, voted!.State);
            Assert.Equal(ReviewState.Approved, (await db.GetReviewAsync(id))!.State);
            var annInbox = await db.ListNotificationsAsync(ann.Id);
            Assert.Contains(annInbox, n => n.Kind == NotificationKinds.ReviewVote);
            Assert.Contains(annInbox, n => n.Kind == NotificationKinds.ReviewState && n.Text.Contains("approved"));
            Assert.Equal(ReviewScope.Requirements(benWs.Model, voted, null).Count, await benSvc.ApplyApprovedStatusAsync(voted));
            Assert.Equal("approved", req.Get("status"));   // replicated to Ann's model through the session
            ws.LeaveSession();
            benWs.LeaveSession();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public async Task Panels_Render_Reviews_Threads_And_The_Inbox()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meridian-ui-{Guid.NewGuid():N}.db");
        try
        {
            var db = new SqliteStore(path);
            await db.MigrateAsync();
            var ann = await db.CreateUserAsync("ann", "Ann Lee", "correct horse");
            var ben = await db.CreateUserAsync("ben", "Ben Ito", "battery staple");
            var pid = await db.CreateProjectAsync("Vehicle", ProjectFile.Serialize(SampleModels.Vehicle()), ann.Id);
            await db.SetMemberAsync(pid, ben.Id, ProjectRole.Editor);
            var hub = new CollaborationHub(db, NullLogger<CollaborationHub>.Instance);
            Services.AddSingleton<IMeridianStore>(db);
            Services.AddSingleton(hub);
            Services.AddSingleton(new MeridianHostInfo(false, Path.GetTempPath(), null));
            Services.AddScoped<ReviewService>();
            var ws = Services.GetRequiredService<Workspace>();
            ws.User = ben;
            Assert.True(await ws.JoinAsync(hub, pid, ProjectRole.Editor));
            var req = ws.Model.OfKind(ElementKind.Requirement).First();

            var annWs = new Workspace { User = ann };
            await annWs.JoinAsync(hub, pid, ProjectRole.Owner);
            var annSvc = new ReviewService(db, hub, annWs);
            var id = await annSvc.CreateAsync(new ReviewDraft("Brakes", "Please check, @ben", ReviewScopeKind.Elements, [req.Id], null, null, true, [ben.Id]));
            var review = (await db.GetReviewAsync(id))!;
            var root = await annSvc.CommentOnReviewAsync(review, null, null, "Start here @ben");
            await annSvc.CommentOnReviewAsync(review, null, root, "And a reply");
            var c = await annSvc.CommentOnElementAsync(req.Id, "Element note");
            await annSvc.CommentOnElementAsync(req.Id, "Element reply", c);

            RenderComponent<MudBlazor.MudPopoverProvider>();
            var panel = RenderComponent<ReviewsPanel>(p => p.Add(x => x.Focus, (id, 1)));
            panel.WaitForAssertion(() => Assert.Contains("Brakes", panel.Find(".rv-detail .rv-h").TextContent));
            Assert.Contains("Waiting for me", panel.Find(".rv-filters").TextContent);
            Assert.Single(panel.FindAll(".rv-row"));
            panel.FindAll(".rv-tab")[2].Click();
            panel.WaitForAssertion(() => Assert.Equal(2, panel.FindAll(".rv-body .comment").Count));
            Assert.Single(panel.FindAll(".rv-body .comment.reply"));
            Assert.Contains("@ben", panel.Find(".rv-body .mention-chip.me").TextContent);
            panel.FindAll(".rv-actions .rbtn")[0].Click();     // Ben approves
            panel.WaitForAssertion(() => Assert.Equal(ReviewState.Approved, db.GetReviewAsync(id).Result!.State));
            panel.WaitForAssertion(() => Assert.Equal("approved", req.Get("status")));   // apply-status on approval

            var comments = RenderComponent<CommentsSection>(p => p.Add(x => x.ElementId, req.Id));
            comments.WaitForAssertion(() => Assert.Equal(2, comments.FindAll(".comment").Count));
            Assert.Single(comments.FindAll(".comment.reply"));
            Assert.NotNull(comments.Find(".owner-row select"));

            var bell = RenderComponent<NotificationBell>(p => p.Add(x => x.OnNavigate, (NotificationInfo _) => { }));
            bell.WaitForAssertion(() => Assert.NotEmpty(bell.FindAll(".notif-badge")));
            bell.Find(".notif-btn").Click();
            bell.WaitForAssertion(() => Assert.NotEmpty(bell.FindAll(".notif-item.unread")));
            bell.Find(".notif-link").Click();
            bell.WaitForAssertion(() => Assert.Empty(bell.FindAll(".notif-badge")));
            Assert.Equal(0, await db.UnreadNotificationCountAsync(ben.Id));
            ws.LeaveSession();
            annWs.LeaveSession();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }
}
