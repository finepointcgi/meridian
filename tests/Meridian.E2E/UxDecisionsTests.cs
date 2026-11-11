using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>
/// The six "decisions needed" of docs/UX-AUDIT-0.7.md, as built in 0.7.1: Review / Extend merged into Traceability /
/// Generate, Export ▸ submenu, verification as a centre tab (VerificationTests), read-only phone lists, the bundled user
/// guide at /guide, and singular counts.
/// </summary>
public sealed class UxDecisionsTests(ServerFixture f) : E2ETest(f)
{
    [E2EFact]
    public async Task Ribbon_has_no_Review_or_Extend_tab_and_their_buttons_are_on_Traceability_and_Generate()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.SetViewportSizeAsync(1280, 800);
        await p.NewProjectAsync("Vehicle sample", "Ribbon " + Guid.NewGuid().ToString("N")[..6]);

        var tabs = await p.Locator("nav.ribbon .rtab").EvaluateAllAsync<string[]>("ts => ts.map(t => t.dataset.tab)");
        Assert.DoesNotContain("review", tabs);
        Assert.DoesNotContain("extend", tabs);
        Assert.Equal(["create", "diagram", "requirements", "trace", "views", "simulate", "safety", "generate", "ai"], tabs);

        foreach (var (tab, captions) in new[] { ("trace", new[] { "Matrix", "Validate", "Request review", "Reviews" }), ("generate", new[] { "Report", "Formats", "Script", "Plugins" }) })
        {
            await p.RibbonTabAsync(tab);
            foreach (var caption in captions)
                await Expect(p.Locator($"nav.ribbon .rgroup[data-tab={tab}] .rbtn").Filter(new() { HasTextString = caption }).First).ToBeVisibleAsync();
            // Everything fits a laptop window: no button is pushed past the right edge.
            var overflow = await p.Locator($"nav.ribbon .rgroup[data-tab={tab}] .rbtn").EvaluateAllAsync<int>("bs => bs.filter(b => b.getBoundingClientRect().right > innerWidth).length");
            Assert.Equal(0, overflow);
        }

        // The buttons still work from their new tabs.
        await p.RibbonAsync("Reviews");
        await Expect(p.DockBody().Locator(".rv")).ToBeVisibleAsync();
        await p.RibbonAsync("Script");
        await Expect(p.Locator(".dock-tabs .dock-tab.on")).ToContainTextAsync("Script");

        // A tab remembered by 0.7.0 ("review") opens the tab its buttons moved to.
        await p.EvaluateAsync("() => meridian.setFlag('meridian.ribbonTab', 'review')");
        await p.ReloadAsync();
        await s.WaitForWorkbenchAsync();
        await Expect(p.Locator("nav.ribbon .rtab[data-tab=trace]")).ToHaveAttributeAsync("aria-selected", "true");
        await p.EvaluateAsync("() => meridian.setFlag('meridian.ribbonTab', 'extend')");
        await p.ReloadAsync();
        await s.WaitForWorkbenchAsync();
        await Expect(p.Locator("nav.ribbon .rtab[data-tab=generate]")).ToHaveAttributeAsync("aria-selected", "true");
    }

    [E2EFact]
    public async Task Project_menu_exports_are_an_Export_submenu_with_keyboard_and_Escape()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.SetViewportSizeAsync(1280, 800);
        await p.NewProjectAsync("Vehicle sample", "Export menu " + Guid.NewGuid().ToString("N")[..6]);

        await p.Locator("header.titlebar .project").ClickAsync();
        var root = p.Locator(".mud-popover-open .project-menu");
        await Expect(root).ToBeVisibleAsync();
        await Expect(root.Locator(":scope > .mud-menu-item", new() { HasTextString = "Download .meridian file" })).ToHaveCountAsync(0);
        var export = root.Locator(".mud-menu-sub-menu-activator", new() { HasTextString = "Export" });
        await Expect(export).ToBeVisibleAsync();

        // Mouse: the submenu lists the project and diagram exports, inside the window.
        await export.ClickAsync();
        var sub = p.Locator(".mud-popover-open .export-menu");
        await Expect(sub.Locator(".mud-menu-item", new() { HasTextString = "Download .meridian file" })).ToBeVisibleAsync();
        foreach (var item in new[] { "Export XMI 2.5.1", "Export SysML v2 text", "Export diagram as SVG", "Export diagram as PNG…", "Copy diagram as Mermaid" })
            await Expect(sub.Locator(".mud-menu-item", new() { HasTextString = item })).ToBeVisibleAsync();
        var box = (await sub.BoundingBoxAsync())!;
        Assert.True(box.X >= 0 && box.Y >= 0 && box.X + box.Width <= 1280 && box.Y + box.Height <= 800, $"the Export submenu leaves the window ({box.X}, {box.Y}, {box.Width}×{box.Height})");

        // Escape closes the submenu first, then the menu.
        await p.Keyboard.PressAsync("Escape");
        await Expect(sub).ToHaveCountAsync(0);
        await Expect(root).ToBeVisibleAsync();
        await p.Keyboard.PressAsync("Escape");
        await Expect(root).ToHaveCountAsync(0);

        // Keyboard: ↑ to the last item (Export), → opens the submenu at its first item, ↓ moves, ← goes back.
        await p.Locator("header.titlebar .project").ClickAsync();
        await Expect(root).ToBeVisibleAsync();
        await p.Keyboard.PressAsync("ArrowUp");
        await Expect(export).ToBeFocusedAsync();
        await p.Keyboard.PressAsync("ArrowRight");
        await Expect(sub.Locator(".mud-menu-item").First).ToBeFocusedAsync();
        await p.Keyboard.PressAsync("ArrowDown");
        await Expect(sub.Locator(".mud-menu-item", new() { HasTextString = "Export XMI 2.5.1" })).ToBeFocusedAsync();
        await p.Keyboard.PressAsync("ArrowLeft");
        await Expect(sub).ToHaveCountAsync(0);
        await Expect(export).ToBeFocusedAsync();
        await p.Keyboard.PressAsync("Escape");
        await Expect(root).ToHaveCountAsync(0);

        // The diagram tab's context menu has the same diagram exports in an Export ▸ submenu.
        await p.Tabs().First.ClickAsync(new() { Button = MouseButton.Right });
        var ctx = p.Locator(".ctx-menu").First;
        await ctx.Locator(".ctx-parent", new() { HasTextString = "Export" }).HoverAsync();
        await Expect(ctx.Locator(".ctx-submenu .ctx-item", new() { HasTextString = "Export as SVG" })).ToBeVisibleAsync();
        await p.Keyboard.PressAsync("Escape");
        await p.Keyboard.PressAsync("Escape");
        await Expect(p.Locator(".ctx-menu")).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task Help_opens_the_bundled_user_guide_and_the_sign_in_page_links_to_it()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        var guide = await p.Context.RunAndWaitForPageAsync(() => p.HelpMenuAsync("User guide"));
        await guide.WaitForLoadStateAsync();
        Assert.Equal("/guide/", new Uri(guide.Url).AbsolutePath);
        await Expect(guide).ToHaveTitleAsync("Meridian test guide");
        await guide.CloseAsync();

        // "Learn more" in an empty state opens the matching page.
        await p.NewProjectAsync("Blank project", "Guide " + Guid.NewGuid().ToString("N")[..6]);
        if (await p.Locator("[data-testid=empty-project]").CountAsync() > 0)
        {
            var page = await p.Context.RunAndWaitForPageAsync(() => p.Locator("[data-testid=empty-project] a[data-guide]", new() { HasTextString = "Coming from Cameo or EA" }).ClickAsync());
            await page.WaitForLoadStateAsync();
            Assert.Equal("/guide/docs/migration.html", new Uri(page.Url).AbsolutePath);
            await page.CloseAsync();
        }

        // Signed out: the sign-in page links to the guide, and the guide opens without signing in.
        var anon = await OpenAsync(null, "/login", waitForWorkbench: false);
        var link = anon.Page.Locator("[data-testid=login-guide]");
        await Expect(link).ToHaveAttributeAsync("href", "/guide/");
        var res = await anon.Page.GotoAsync("/guide/");
        Assert.Equal(200, res!.Status);
        await Expect(anon.Page).ToHaveTitleAsync("Meridian test guide");
    }

    [E2EFact]
    public async Task Phone_shows_read_only_verification_results_and_code_changes_from_the_account_menu()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Phone lists " + Guid.NewGuid().ToString("N")[..6]);
        await p.RibbonAsync("Verify");
        await Expect(p.ViewTab().Locator("[data-testid=verify-results] tr.row .verdict.pass")).ToHaveCountAsync(2, new() { Timeout = 20_000 });

        await p.SetViewportSizeAsync(390, 844);
        await Expect(p.Locator(".m-topbar")).ToBeVisibleAsync();
        await p.Locator(".m-topbar button[aria-label=Account]").ClickAsync();
        await p.Locator(".mud-popover-open [data-testid=m-menu-verification]").ClickAsync();
        var list = p.Locator("[data-testid=m-verification]");
        await Expect(list.Locator(".m-vfy")).ToHaveCountAsync(3);
        var range = list.Locator(".m-vfy[data-status=fail]");
        await Expect(range).ToContainTextAsync("REQ-003");
        await Expect(range).ToContainTextAsync("550 km vs 600 km");
        await Expect(range).ToContainTextAsync("margin");
        await Expect(list).ToContainTextAsync("read-only");
        Assert.Empty(await list.Locator("button, input").Filter(new() { HasTextString = "Verify" }).AllAsync());   // nothing to run here
        await list.Locator(".m-chip", new() { HasTextString = "Fail" }).ClickAsync();
        await Expect(list.Locator(".m-vfy")).ToHaveCountAsync(1);
        await NoSidewaysScrollAsync(p);

        await p.Locator(".m-topbar button[aria-label=Account]").ClickAsync();
        await p.Locator(".mud-popover-open [data-testid=m-menu-code-changes]").ClickAsync();
        var changes = p.Locator("[data-testid=m-code-changes]");
        await Expect(changes).ToContainTextAsync("larger screen");
        await Expect(changes).ToContainTextAsync("No code changes to review");
        await NoSidewaysScrollAsync(p);
    }

    [E2EFact]
    public async Task Status_bar_counts_use_the_singular_for_one()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Blank project", "Plural " + Guid.NewGuid().ToString("N")[..6]);
        var before = await p.CountsAsync();
        await p.RibbonAsync("Block");
        await Ui.EventuallyAsync(async () => (await p.CountsAsync()).Elements == before.Elements + 1, "a new block is counted");
        var (elements, relationships, diagrams) = await p.CountsAsync();
        var text = await p.StatusCounts().InnerTextAsync();
        Assert.Contains(elements == 1 ? "1 element ·" : $"{elements} elements ·", text);
        Assert.Contains(relationships == 1 ? "1 relationship ·" : $"{relationships} relationships ·", text);
        Assert.EndsWith(diagrams == 1 ? "1 diagram" : $"{diagrams} diagrams", text.Trim());
        Assert.DoesNotMatch(new Regex(@"\b1 (elements|relationships|diagrams)\b"), text);
    }

    private static async Task NoSidewaysScrollAsync(IPage p)
    {
        var wide = await p.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth || [...document.querySelectorAll('.m-view')].some(v => v.scrollWidth > v.clientWidth + 1)");
        Assert.False(wide, "the phone view scrolls sideways");
    }
}
