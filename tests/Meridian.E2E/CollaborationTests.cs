using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

public sealed class CollaborationTests(ServerFixture f) : E2ETest(f)
{
    [E2EFact]
    public async Task Two_browsers_on_one_project_see_each_others_edits_live()
    {
        // Alice creates a project and shares it with Bob.
        var alice = await OpenAsync(F.Alice);
        var a = alice.Page;
        var project = "Collab " + Guid.NewGuid().ToString("N")[..6];
        await a.NewProjectAsync("Blank project", project);
        await a.Locator("header.titlebar button.rbtn", new() { HasTextString = "Share" }).ClickAsync();
        var share = a.MudDialog();
        await share.GetByPlaceholder("Username").FillAsync(F.Bob.Username);
        await share.GetByRole(AriaRole.Button, new() { Name = "Invite" }).ClickAsync();
        await Expect(share.GetByText(F.Bob.DisplayName)).ToBeVisibleAsync();
        await share.GetByRole(AriaRole.Button, new() { Name = "Done" }).ClickAsync();

        // Bob opens the same project from the Projects dialog.
        var bob = await OpenAsync(F.Bob);
        var b = bob.Page;
        await b.ProjectMenuAsync("Projects…");
        await b.MudDialog().GetByText(project).First.ClickAsync();
        await bob.WaitForWorkbenchAsync(project);

        // Each sees the other's presence avatar.
        await Expect(a.Locator("header.titlebar .presence .avatar")).ToHaveCountAsync(1, new() { Timeout = 15_000 });
        await Expect(b.Locator("header.titlebar .presence .avatar")).ToHaveCountAsync(1);

        // Alice draws a block: it appears on Bob's canvas and tree.
        await a.ShapeTileAsync("Block");
        await a.Keyboard.TypeAsync("Shared Pump", new() { Delay = 20 });
        await a.Keyboard.PressAsync("Enter");
        await Expect(a.Node("Shared Pump")).ToHaveCountAsync(1);
        await Expect(b.Tree().GetByText("Shared Pump", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(b.Node("Shared Pump")).ToHaveCountAsync(1);

        // Bob renames it; Alice sees the new name.
        await b.Tree().GetByText("Shared Pump", new() { Exact = true }).ClickAsync();
        var name = b.Inspector().GetByLabel("Name", new() { Exact = true });
        await Expect(name).ToHaveValueAsync("Shared Pump");
        await name.FillAsync("Shared Pump v2");
        await name.PressAsync("Tab");
        await Expect(a.Node("Shared Pump v2")).ToHaveCountAsync(1);
        await Expect(a.Tree().GetByText("Shared Pump v2", new() { Exact = true })).ToBeVisibleAsync();

        // Alice deletes it; it disappears for Bob too.
        var (x, y, _) = await a.Node("Shared Pump v2").CenterAsync();   // a real click: selection adorners sit on top of the shape
        await a.Mouse.ClickAsync(x, y);
        await a.Keyboard.PressAsync("Delete");
        await Expect(b.Tree().GetByText("Shared Pump v2", new() { Exact = true })).ToHaveCountAsync(0);
        await Expect(b.Nodes()).ToHaveCountAsync(0);
        await Ui.EventuallyAsync(async () => await a.CountsAsync() == await b.CountsAsync(), "both browsers should show the same model counts");
    }
}
