using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

public sealed class TablesTests(ServerFixture f) : E2ETest(f)
{
    [E2EFact]
    public async Task Query_results_open_as_an_editable_table_that_can_be_saved()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Tables " + Guid.NewGuid().ToString("N")[..6]);
        var dock = p.ViewTab();

        // Run a text query for blocks.
        await p.RibbonAsync("Query");
        // The query opens as a tab in the centre, next to the diagram tabs.
        await Expect(p.Tabs().Filter(new() { HasTextString = "New query" })).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"));
        await dock.Locator(".gt-seg.small button", new() { HasTextString = "Text" }).ClickAsync();
        await dock.Locator("textarea.gt-qtext").FillAsync("kind:Block");
        var status = dock.Locator(".gt-status").First;
        await Expect(status).ToContainTextAsync(new System.Text.RegularExpressions.Regex(@"^\s*\d+ match"));
        var matches = int.Parse(System.Text.RegularExpressions.Regex.Match(await status.InnerTextAsync(), @"\d+").Value);
        Assert.True(matches >= 4, $"expected the sample's blocks, got {matches}");
        await Expect(dock.Locator("table.gt-table tbody tr").Filter(new() { HasTextString = "Engine" }).First).ToBeVisibleAsync();
        await Expect(dock.Locator("table.gt-table tbody tr").Filter(new() { HasTextString = "REQ-" })).ToHaveCountAsync(0);

        // Open the results as a table: same rows, now editable.
        await dock.GetByRole(AriaRole.Button, new() { Name = "Open as table" }).ClickAsync();
        await Expect(dock.Locator(".gt-status").First).ToContainTextAsync($"{matches} row");
        ILocator? nameCell = null;
        foreach (var cell in await dock.Locator("input.gt-cell.strong").AllAsync())
            if (await cell.InputValueAsync() == "Engine") nameCell = cell;
        Assert.NotNull(nameCell);
        await nameCell.FillAsync("Engine Mk2");
        await nameCell.PressAsync("Tab");
        await Expect(p.Tree().GetByText("Engine Mk2", new() { Exact = true })).ToBeVisibleAsync();

        // Save the view: it becomes a model element and appears under Saved views.
        await dock.GetByRole(AriaRole.Button, new() { Name = "Save view" }).ClickAsync();
        var dlg = p.MudDialog();
        await dlg.GetByLabel("Name").FillAsync("Engine blocks");
        await dlg.GetByRole(AriaRole.Button, new() { Name = "OK" }).ClickAsync();
        await Expect(dock.Locator(".gt-saved")).ToContainTextAsync("Engine blocks");
        await Expect(p.StatusMessage()).ToContainTextAsync("Engine blocks");
        await dock.Locator("button.gt-views").ClickAsync();
        await Expect(p.Locator(".mud-popover-open .mud-menu-item", new() { HasTextString = "Engine blocks" })).ToBeVisibleAsync();
        await p.Mouse.ClickAsync(5, (p.ViewportSize?.Height ?? 800) - 5);   // click away (bottom-left corner): the menu closes
        await Expect(p.Locator(".mud-popover-open .mud-menu-item")).ToHaveCountAsync(0);
        await Expect(p.Tree().GetByText("Engine blocks").First).ToBeAttachedAsync();
        // The tab is now titled after the saved view.
        await Expect(p.Tabs().Filter(new() { HasTextString = "Engine blocks" })).ToHaveCountAsync(1);

        // Back on a diagram tab, the renamed block shows on the canvas; the table tab stays open.
        await p.Tabs().Filter(new() { HasTextString = "Vehicle Structure" }).First.ClickAsync();
        await Expect(p.Node("Engine Mk2").First).ToBeVisibleAsync();
        await Expect(p.Tabs().Filter(new() { HasTextString = "Engine blocks" })).ToHaveCountAsync(1);

        // Closing the tab leaves the saved view in the model; double-clicking it in the tree reopens it as a tab.
        await p.Tabs().Filter(new() { HasTextString = "Engine blocks" }).Locator(".x").ClickAsync();
        await Expect(p.Tabs().Filter(new() { HasTextString = "Engine blocks" })).ToHaveCountAsync(0);
        await p.Tree().GetByText("Engine blocks").First.DblClickAsync();
        await Expect(p.Tabs().Filter(new() { HasTextString = "Engine blocks" })).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"));
    }
}
