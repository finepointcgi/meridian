using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

public sealed class ReportTemplateTabTests(ServerFixture f) : E2ETest(f)
{
    [E2EFact]
    public async Task Editing_a_report_template_opens_it_as_a_tab_and_saving_keeps_it()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Report tab " + Guid.NewGuid().ToString("N")[..6]);

        // Generate document → ⋮ → Edit a copy…: the dialog closes and the editor opens as the active centre tab.
        await p.RibbonAsync("Report");
        var dlg = p.MudDialog();
        await Expect(dlg.GetByText("Generate document")).ToBeVisibleAsync();
        await dlg.Locator("button.mud-icon-button").Last.ClickAsync();
        await p.Locator(".mud-popover-open .mud-menu-item", new() { HasTextString = "Edit a copy" }).ClickAsync();
        await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);
        var tab = p.Tabs().Filter(new() { HasTextString = "(copy)" });
        await Expect(tab).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"));
        var editor = p.ViewTab().Locator(".rte-page");
        await Expect(editor.Locator(".rte-rows .rte-row").First).ToBeVisibleAsync();

        // Save to model: the template becomes a model element; the tab stays and is titled after it.
        await editor.GetByRole(AriaRole.Button, new() { Name = "Save to model" }).ClickAsync();
        await Expect(editor.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true })).ToBeVisibleAsync();
        await Expect(p.Locator(".mud-snackbar", new() { HasTextString = "Saved template" })).ToBeVisibleAsync();
        await Expect(tab).ToHaveCountAsync(1);

        // A diagram tab still works, and closing the template tab returns to it.
        await p.Tabs().Filter(new() { HasTextString = "Vehicle Structure" }).First.ClickAsync();
        await Expect(p.Nodes().First).ToBeVisibleAsync();
        await tab.Locator(".x").ClickAsync();
        await Expect(tab).ToHaveCountAsync(0);
    }
}
