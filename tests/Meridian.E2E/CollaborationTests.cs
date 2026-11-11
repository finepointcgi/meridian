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

    /// <summary>
    /// Both browsers rename the same element at the same moment, without a lock: the session orders the two renames, each tab
    /// rebases its own under the one ordered first, and both end with the same name (docs/COLLABORATION.md).
    /// </summary>
    [E2EFact]
    public async Task Two_browsers_rename_the_same_element_at_the_same_time_and_end_with_the_same_name()
    {
        var alice = await OpenAsync(F.Alice);
        var a = alice.Page;
        var project = "Rename race " + Guid.NewGuid().ToString("N")[..6];
        await a.NewProjectAsync("Blank project", project);
        await a.Locator("header.titlebar button.rbtn", new() { HasTextString = "Share" }).ClickAsync();
        var share = a.MudDialog();
        await share.GetByPlaceholder("Username").FillAsync(F.Bob.Username);
        await share.GetByRole(AriaRole.Button, new() { Name = "Invite" }).ClickAsync();
        await Expect(share.GetByText(F.Bob.DisplayName)).ToBeVisibleAsync();
        await share.GetByRole(AriaRole.Button, new() { Name = "Done" }).ClickAsync();

        var bob = await OpenAsync(F.Bob);
        var b = bob.Page;
        await b.ProjectMenuAsync("Projects…");
        await b.MudDialog().GetByText(project).First.ClickAsync();
        await bob.WaitForWorkbenchAsync(project);

        await a.ShapeTileAsync("Block");
        await a.Keyboard.TypeAsync("Contested", new() { Delay = 20 });
        await a.Keyboard.PressAsync("Enter");
        await Expect(b.Tree().GetByText("Contested", new() { Exact = true })).ToBeVisibleAsync();

        // Both select it and type a new name; both commit at the same moment.
        async Task<ILocator> NameField(IPage p)
        {
            await p.Tree().GetByText("Contested", new() { Exact = true }).ClickAsync();
            var name = p.Inspector().GetByLabel("Name", new() { Exact = true });
            await Expect(name).ToHaveValueAsync("Contested");
            return name;
        }
        var na = await NameField(a);
        var nb = await NameField(b);
        await na.FillAsync("Named by Alice");
        await nb.FillAsync("Named by Bob");
        await Task.WhenAll(na.PressAsync("Tab"), nb.PressAsync("Tab"));

        // One of the two names wins, the same one in both browsers; the other is gone from both.
        async Task<string?> Shown(IPage p)
        {
            var alice = await p.Tree().GetByText("Named by Alice", new() { Exact = true }).CountAsync();
            var bob = await p.Tree().GetByText("Named by Bob", new() { Exact = true }).CountAsync();
            return (alice, bob) switch { (1, 0) => "Named by Alice", (0, 1) => "Named by Bob", _ => null };
        }
        await Ui.EventuallyAsync(async () => await Shown(a) is { } x && x == await Shown(b), "both browsers should show the same, single name");
        var final = await Shown(a);
        foreach (var p in new[] { a, b })
        {
            await p.Tree().GetByText(final!, new() { Exact = true }).ClickAsync();
            await Expect(p.Inspector().GetByLabel("Name", new() { Exact = true })).ToHaveValueAsync(final!);
        }
        await Ui.EventuallyAsync(async () => await a.CountsAsync() == await b.CountsAsync(), "both browsers should show the same model counts");
    }

    [E2EFact]
    public async Task Two_browsers_edit_the_same_SysML_text_and_one_apply_updates_both()
    {
        var alice = await OpenAsync(F.Alice);
        var a = alice.Page;
        var project = "Text collab " + Guid.NewGuid().ToString("N")[..6];
        await a.NewProjectAsync("Blank project", project);
        await a.Locator("header.titlebar button.rbtn", new() { HasTextString = "Share" }).ClickAsync();
        var share = a.MudDialog();
        await share.GetByPlaceholder("Username").FillAsync(F.Bob.Username);
        await share.GetByRole(AriaRole.Button, new() { Name = "Invite" }).ClickAsync();
        await Expect(share.GetByText(F.Bob.DisplayName)).ToBeVisibleAsync();
        await share.GetByRole(AriaRole.Button, new() { Name = "Done" }).ClickAsync();

        var bob = await OpenAsync(F.Bob);
        var b = bob.Page;
        await b.ProjectMenuAsync("Projects…");
        await b.MudDialog().GetByText(project).First.ClickAsync();
        await bob.WaitForWorkbenchAsync(project);

        // Both open the shared text of the Structure package.
        async Task<ILocator> OpenText(IPage p)
        {
            await p.Tree().GetByText("Structure", new() { Exact = true }).First.ClickAsync();
            await p.RibbonAsync("Edit text");
            var panel = p.DockBody().Locator(".sx-panel");
            await Expect(panel.Locator(".sx-scope")).ToContainTextAsync("Structure");
            return panel.Locator("textarea.sx-ta");
        }
        var ta = await OpenText(a);
        var tb = await OpenText(b);

        static string InsertInPackage(string text, string part)
        {
            var at = text.IndexOf('{', text.IndexOf("package Structure", StringComparison.Ordinal)) + 1;
            return text[..at] + $"\n    part def {part} {{\n    }}\n" + text[at..];
        }

        // Alice types: Bob's editor shows it. Bob types too: Alice's editor shows both.
        await ta.FillAsync(InsertInPackage(await ta.InputValueAsync(), "AliceUnit"));
        await Expect(tb).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("AliceUnit"));
        await tb.FillAsync(InsertInPackage(await tb.InputValueAsync(), "BobUnit"));
        await Expect(ta).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("BobUnit"));
        await Expect(ta).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("AliceUnit"));
        Assert.Equal(await ta.InputValueAsync(), await tb.InputValueAsync());

        // Alice applies once: both trees show both blocks and both editors are in sync.
        await a.DockBody().Locator(".sx-panel button.sx-btn.primary").ClickAsync();
        foreach (var p in new[] { a, b })
        {
            await Expect(p.Tree().GetByText("AliceUnit", new() { Exact = true })).ToBeVisibleAsync();
            await Expect(p.Tree().GetByText("BobUnit", new() { Exact = true })).ToBeVisibleAsync();
            await Expect(p.DockBody().Locator(".sx-panel .sx-state")).ToContainTextAsync("In sync with the model");
        }
        await Ui.EventuallyAsync(async () => await ta.InputValueAsync() == await tb.InputValueAsync(), "both editors should show the same text");
    }
}
