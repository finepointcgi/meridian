using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>Help ▸ Tour: picking a chapter on the desktop layout and on a phone.</summary>
public sealed class TourTests(ServerFixture f) : E2ETest(f)
{
    private static ILocator Card(IPage p) => p.Locator(".tour .tour-card");

    [E2EFact]
    public async Task Help_tour_lists_the_chapters_and_runs_the_one_picked()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Tour " + Guid.NewGuid().ToString("N")[..6]);

        await p.HelpMenuAsync("Tour…");
        await Expect(p.Locator(".tour-chapter")).ToHaveCountAsync(Meridian.Web.Components.Workbench.TourChapters.All.Length);
        await p.Locator(".tour-chapter[data-chapter=derive]").ClickAsync();

        // The first step spotlights the ribbon's Create from… button and puts the card beside it.
        await Expect(Card(p).Locator(".tour-kicker")).ToHaveTextAsync("Create diagrams from diagrams");
        await Expect(Card(p).Locator(".tour-count")).ToHaveTextAsync("Step 1 of 3");
        await Expect(Card(p).Locator("h3")).ToContainTextAsync("Create from…");
        await Expect(p.Locator(".tour-spot")).ToHaveCountAsync(1);
        var spot = (await p.Locator(".tour-spot").BoundingBoxAsync())!;
        var target = (await p.Locator("[data-tour=create-from]").BoundingBoxAsync())!;
        Assert.InRange(spot.X, target.X - 10, target.X);
        Assert.InRange(spot.Y, target.Y - 10, target.Y);

        await Card(p).GetByRole(AriaRole.Button, new() { Name = "Next" }).ClickAsync();
        await Expect(Card(p).Locator(".tour-step")).ToContainTextAsync("Step 2 of 3");
        await p.Keyboard.PressAsync("ArrowRight");
        await Expect(Card(p).Locator(".tour-step")).ToContainTextAsync("Step 3 of 3");
        await Card(p).GetByRole(AriaRole.Button, new() { Name = "Done" }).ClickAsync();
        await Expect(p.Locator(".tour")).ToHaveCountAsync(0);

        // ⌘K opens a chapter directly.
        await p.Locator(".cmdk").ClickAsync();
        await p.Locator(".cmdp-input input").FillAsync("Tour: Settings");
        await p.Keyboard.PressAsync("Enter");
        await Expect(Card(p).Locator(".tour-kicker")).ToHaveTextAsync("Settings & themes");
        await p.Keyboard.PressAsync("Escape");
        await Expect(p.Locator(".tour")).ToHaveCountAsync(0);
        s.AssertNoErrors();
    }

    [E2EFact]
    public async Task On_a_phone_a_chapter_is_a_bottom_sheet_from_the_account_menu()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Tour phone " + Guid.NewGuid().ToString("N")[..6]);
        await p.SetViewportSizeAsync(390, 844);
        await Expect(p.Locator(".m-topbar")).ToBeVisibleAsync();

        await p.Locator(".m-topbar button[aria-label=Account]").ClickAsync();
        await p.Locator(".mud-popover-open .mud-menu-item", new() { HasTextString = "Tour…" }).ClickAsync();
        await p.Locator(".tour-chapter[data-chapter=multi]").ClickAsync();
        await Expect(Card(p).Locator(".tour-kicker")).ToHaveTextAsync("Select many & link");
        await Expect(Card(p).Locator(".tour-count")).ToHaveTextAsync("Step 1 of 3");
        await Expect(p.Locator(".tour-spot")).ToHaveCountAsync(0);

        // The card spans the screen above the bottom navigation and fits on it.
        var card = (await Card(p).BoundingBoxAsync())!;
        Assert.InRange(card.X, 0, 16);
        Assert.InRange(card.Width, 390 - 32, 390);
        Assert.True(card.Y > 844 / 3.0, $"the card should sit low on the screen (y = {card.Y})");
        Assert.True(card.Y + card.Height <= 844, "the card fits on the screen");
        var nav = await p.Locator(".m-bottomnav, nav.m-nav").First.BoundingBoxAsync();
        if (nav != null) Assert.True(card.Y + card.Height <= nav.Y + 1, "the card stays above the bottom navigation");

        await Card(p).GetByRole(AriaRole.Button, new() { Name = "Next" }).ClickAsync();
        await Card(p).GetByRole(AriaRole.Button, new() { Name = "Next" }).ClickAsync();
        await Card(p).GetByRole(AriaRole.Button, new() { Name = "Done" }).ClickAsync();
        await Expect(p.Locator(".tour")).ToHaveCountAsync(0);
        s.AssertNoErrors();
    }
}
