using Meridian.Web.Components.Workbench;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>Guards from the 0.7 ease-of-use pass (docs/UX-AUDIT-0.7.md) that need a real browser.</summary>
public sealed class UxTests(ServerFixture f) : E2ETest(f)
{
    /// <summary>
    /// Every tour step's spotlight target is on screen once its ribbon tab shows. Targets that only exist with a connected
    /// repository (the Code tab) are allowed to be missing: the tour then shows the card without a spotlight.
    /// </summary>
    [E2EFact]
    public async Task Every_tour_spotlight_target_is_on_screen()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.SetViewportSizeAsync(1440, 900);
        await p.NewProjectAsync("Vehicle sample", "Tour targets " + Guid.NewGuid().ToString("N")[..6]);
        await p.ProjectMenuAsync("Database schema");
        var dlg = p.MudDialog();
        await dlg.Locator("textarea").First.FillAsync("CREATE TABLE customer (id INT PRIMARY KEY);");
        await dlg.Locator("[data-testid=db-import-ok]").ClickAsync();
        await Expect(p.Locator("nav.ribbon .rtab[data-tab=database]")).ToBeVisibleAsync();

        var missing = new List<string>();
        foreach (var chapter in TourChapters.All)
            foreach (var step in chapter.Steps.Where(x => x.Selector.Length > 0))
            {
                if (Workbench.TourTargetTab(step.Selector) is { } tab)
                {
                    if (tab == "code") continue;
                    await p.RibbonTabAsync(tab);
                }
                var found = await p.EvaluateAsync<bool>("sel => meridian.rectOf(sel) !== null", step.Selector);
                if (!found) missing.Add($"{chapter.Id}: {step.Title} ({step.Selector})");
            }
        Assert.True(missing.Count == 0, "Tour targets not on screen:\n" + string.Join("\n", missing));
    }

    /// <summary>No dialog is taller or wider than a 1280×800 laptop window, and each takes the keyboard focus.</summary>
    [E2EFact]
    public async Task Dialogs_fit_a_1280_by_800_window()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Dialogs " + Guid.NewGuid().ToString("N")[..6]);
        await p.SetViewportSizeAsync(1280, 800);
        var openers = new (string Name, Func<Task> Open)[]
        {
            ("New project", () => p.ProjectMenuAsync("New project…")),
            ("Integrations", () => p.ProjectMenuAsync("Integrations")),
            ("Used projects", () => p.ProjectMenuAsync("Used projects")),
            ("Branches", () => p.ProjectMenuAsync("Branches")),
            ("Baselines", () => p.ProjectMenuAsync("Baselines")),
            ("Share", () => p.ProjectMenuAsync("Share")),
            ("Paste Mermaid", () => p.ProjectMenuAsync("Paste Mermaid")),
            ("Database schema", () => p.ProjectMenuAsync("Database schema")),
            ("Keyboard & mouse", () => p.HelpMenuAsync("Keyboard & mouse")),
            ("Auto-diagram", () => p.RibbonAsync("Auto-diagram")),
            ("Report", () => p.RibbonAsync("Report")),
        };
        var problems = new List<string>();
        foreach (var (name, open) in openers)
        {
            await open();
            var dialog = p.Locator(".mud-dialog").Last;
            await Expect(dialog).ToBeVisibleAsync();
            await Task.Delay(250);
            var box = (await dialog.BoundingBoxAsync())!;
            if (box.Y < 0 || box.Y + box.Height > 800 || box.X < 0 || box.X + box.Width > 1280)
                problems.Add($"{name}: {box.X:0},{box.Y:0} {box.Width:0}×{box.Height:0}");
            await p.Keyboard.PressAsync("Escape");
            await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);
        }
        Assert.True(problems.Count == 0, "Dialogs outside 1280×800:\n" + string.Join("\n", problems));
    }

    /// <summary>The tree's context menu nests New element ▸ / New diagram ▸, fits the window and closes with Escape.</summary>
    [E2EFact]
    public async Task Tree_context_menu_fits_the_window_and_closes_with_escape()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Menu " + Guid.NewGuid().ToString("N")[..6]);
        await p.SetViewportSizeAsync(1280, 800);
        await p.Tree().GetByText("Structure", new() { Exact = true }).First.ClickAsync(new() { Button = MouseButton.Right });
        var menu = p.Locator(".ctx-menu").First;
        await Expect(menu).ToBeVisibleAsync();
        var box = (await menu.BoundingBoxAsync())!;
        Assert.True(box.Y + box.Height <= 800, $"menu bottom {box.Y + box.Height}");
        await menu.Locator(".ctx-parent", new() { HasTextString = "New element" }).HoverAsync();
        var sub = p.Locator(".ctx-sub.open > .ctx-submenu");
        await Expect(sub).ToBeVisibleAsync();
        var subBox = (await sub.BoundingBoxAsync())!;
        Assert.True(subBox.Y + subBox.Height <= 800 && subBox.X + subBox.Width <= 1280, "the submenu fits the window");
        await Expect(menu.Locator(".ctx-item", new() { HasTextString = "Permissions…" })).ToBeVisibleAsync();
        await p.Keyboard.PressAsync("Escape");
        await p.Keyboard.PressAsync("Escape");
        await Expect(p.Locator(".ctx-menu")).ToHaveCountAsync(0);
    }
}
