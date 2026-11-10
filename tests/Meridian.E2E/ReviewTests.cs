using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

public sealed class ReviewTests(ServerFixture f) : E2ETest(f)
{
    private static ILocator Badge(IPage p) => p.Locator("header.titlebar .notif-badge");

    private static async Task<int> UnreadAsync(IPage p) =>
        await Badge(p).CountAsync() == 0 ? 0 : int.Parse(await Badge(p).InnerTextAsync());

    [E2EFact]
    public async Task Review_request_vote_and_notification_bell_across_two_users()
    {
        var alice = await OpenAsync(F.Alice);
        var a = alice.Page;
        var project = "Review " + Guid.NewGuid().ToString("N")[..6];
        await a.NewProjectAsync("Vehicle sample", project);

        // Share the project with Bob as an editor.
        await a.Locator("header.titlebar button.rbtn", new() { HasTextString = "Share" }).ClickAsync();
        var share = a.MudDialog();
        await share.GetByPlaceholder("Username").FillAsync(F.Bob.Username);
        await share.GetByRole(AriaRole.Button, new() { Name = "Invite" }).ClickAsync();
        await Expect(share.GetByText(F.Bob.DisplayName)).ToBeVisibleAsync();
        await share.GetByRole(AriaRole.Button, new() { Name = "Done" }).ClickAsync();

        // Bob is online in his own workspace; note his unread count.
        var bob = await OpenAsync(F.Bob);
        var b = bob.Page;
        var bobUnread = await UnreadAsync(b);
        var aliceUnread = await UnreadAsync(a);

        // Alice asks Bob to review Engine.
        await a.Tree().GetByText("Engine", new() { Exact = true }).First.ClickAsync();
        await a.RibbonAsync("Request");
        var dlg = a.MudDialog();
        await dlg.GetByLabel("Title").FillAsync("Check the engine");
        await Expect(dlg.Locator(".mud-chip", new() { HasTextString = "Engine" })).ToBeVisibleAsync();
        await dlg.Locator(".mud-checkbox", new() { HasTextString = F.Bob.DisplayName }).ClickAsync();
        await dlg.GetByRole(AriaRole.Button, new() { Name = "Request review" }).ClickAsync();
        await a.SnackbarAsync("Review requested");
        var panel = a.DockBody();
        await Expect(panel.Locator(".rv-h")).ToHaveTextAsync("Check the engine");

        // Bob's bell lights up live, and the notification takes him to the review.
        await Expect(Badge(b)).ToHaveTextAsync((bobUnread + 1).ToString(), new() { Timeout = 15_000 });
        await b.Locator("header.titlebar .notif-btn").ClickAsync();
        var item = b.Locator(".notif-item.unread").Filter(new() { HasTextString = "Check the engine" });
        await Expect(item).ToHaveCountAsync(1);
        await item.ClickAsync();
        await bob.WaitForWorkbenchAsync(project);
        await Expect(b.DockBody().Locator(".rv-h")).ToHaveTextAsync("Check the engine");
        await Expect(Badge(b)).ToHaveCountAsync(bobUnread == 0 ? 0 : 1);

        // Bob approves; Alice sees the vote in the panel and in her bell.
        await b.DockBody().Locator(".rv-actions button.rbtn", new() { HasTextString = "Approve" }).ClickAsync();
        await Expect(b.DockBody().Locator(".rv-actions button.rbtn", new() { HasTextString = "Approve" })).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bon\b"));
        await Expect(b.DockBody().Locator(".rv-head .rv-state")).ToHaveTextAsync("Approved");
        await Expect(Badge(a)).ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"^[1-9]"), new() { Timeout = 15_000 });
        Assert.True(await UnreadAsync(a) > aliceUnread);
        await Expect(panel.Locator(".rv-head .rv-state")).ToHaveTextAsync("Approved");
        await a.Locator("header.titlebar .notif-btn").ClickAsync();
        await Expect(a.Locator(".notif-item.unread").Filter(new() { HasTextString = F.Bob.DisplayName }).First).ToBeVisibleAsync();
    }
}
