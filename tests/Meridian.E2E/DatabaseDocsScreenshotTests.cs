using System.Security.Cryptography;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>
/// Opt-in: the screenshots of the "Database modelling" guide page, in both themes
/// (<c>MERIDIAN_DOCS_SCREENSHOTS=1 dotnet test tests/Meridian.E2E --filter DatabaseDocsScreenshotTests</c>).
/// </summary>
public sealed class DatabaseDocsScreenshotTests(ServerFixture f) : E2ETest(f)
{
    private string _theme = "dark";
    private readonly string _dir = ImagesDir();

    private static string ImagesDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Meridian.slnx"))) return Path.Combine(d.FullName, "docs-site", "images");
        throw new InvalidOperationException("Repository root (Meridian.slnx) not found");
    }

    private static async Task TidyAsync(IPage p)
    {
        for (var i = 0; i < 5 && await p.Locator(".mud-snackbar button").CountAsync() > 0; i++)
        {
            try { await p.Locator(".mud-snackbar button").First.ClickAsync(new() { Timeout = 2000 }); } catch (PlaywrightException) { }
        }
        await Expect(p.Locator(".mud-snackbar")).ToHaveCountAsync(0);
        await p.Mouse.MoveAsync(2, 300);
        await Expect(p.Locator(".mud-tooltip.mud-popover-open")).ToHaveCountAsync(0);
    }

    private async Task ShotAsync(IPage p, string name)
    {
        await TidyAsync(p);
        await p.ScreenshotAsync(new() { Path = Path.Combine(_dir, $"{name}-{_theme}.png"), Animations = ScreenshotAnimations.Disabled });
    }

    private async Task ShotAsync(ILocator element, string name)
    {
        await TidyAsync(element.Page);
        await element.ScreenshotAsync(new() { Path = Path.Combine(_dir, $"{name}-{_theme}.png"), Animations = ScreenshotAnimations.Disabled });
    }

    [DocsScreenshotFact]
    public async Task Writes_the_database_modelling_screenshots_in_both_themes()
    {
        Directory.CreateDirectory(_dir);
        var s = await OpenAsync(null, "/login?mode=register", waitForWorkbench: false);
        var p = s.Page;
        var user = "dbdocs" + RandomNumberGenerator.GetHexString(4, lowercase: true);
        await p.GetByLabel("Username").FillAsync(user);
        await p.GetByLabel("Display name").FillAsync("Dana Data");
        await p.GetByLabel("Password").FillAsync("Pw-" + RandomNumberGenerator.GetHexString(20, lowercase: true));
        await p.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();
        await s.WaitForWorkbenchAsync();
        await p.SetViewportSizeAsync(1440, 900);
        s.IgnoreErrors.Add(e => e.Contains("ResizeObserver", StringComparison.Ordinal));

        foreach (var theme in new[] { "dark", "light" })
        {
            _theme = theme;
            if (await p.Locator("div.app").First.GetAttributeAsync("class") is { } cls && !cls.Split(' ').Contains(theme))
                await p.Locator("header.titlebar .tb-theme").ClickAsync();
            await Expect(p.Locator("div.app").First).ToHaveClassAsync(new System.Text.RegularExpressions.Regex($@"\b{theme}\b"));
            await CaptureAsync(p);
        }
    }

    private async Task CaptureAsync(IPage p)
    {
        await p.NewProjectAsync("Blank project", $"Shop data ({_theme})");

        // Import: the dialog with its preview.
        await p.ProjectMenuAsync("Database schema (DDL / .dacpac)…");
        var dlg = p.MudDialog();
        await dlg.Locator("[data-testid=db-import-text]").FillAsync(DatabaseTests.Script);
        await Expect(dlg.Locator("[data-testid=db-import-preview]")).ToContainTextAsync("4 tables");
        await dlg.Locator("[data-testid=db-import-name] input, input[data-testid=db-import-name]").First.FillAsync("shop");
        await ShotAsync(dlg, "database-import");
        await dlg.Locator("[data-testid=db-import-ok]").ClickAsync();
        await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);
        await Expect(DatabaseTests.Table(p, "customer")).ToBeVisibleAsync();

        // A subject area across both schemas.
        var (cx, cy, _) = await DatabaseTests.Table(p, "customer").CenterAsync();
        await p.Mouse.ClickAsync(cx, cy);
        await p.Locator("[data-testid=ribbon-add-to-group]").ClickAsync();
        dlg = p.MudDialog();
        await dlg.Locator("[data-testid=db-new-group-name] input, input[data-testid=db-new-group-name]").First.FillAsync("Customers");
        await dlg.Locator("[data-testid=db-new-group-add]").ClickAsync();
        await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);
        await p.Tabs().Filter(new() { HasTextString = "sales – tables" }).ClickAsync();
        await Expect(DatabaseTests.Table(p, "orders")).ToBeVisibleAsync();
        var (ox, oy, _) = await DatabaseTests.Table(p, "orders").CenterAsync();
        var (lx, ly, _) = await DatabaseTests.Table(p, "order_line").CenterAsync();
        await p.Mouse.ClickAsync(ox, oy);
        await p.Keyboard.DownAsync("Shift");
        await p.Mouse.ClickAsync(lx, ly);
        await p.Keyboard.UpAsync("Shift");
        await p.Locator("[data-testid=ribbon-add-to-group]").ClickAsync();
        dlg = p.MudDialog();
        await dlg.Locator("[data-testid=db-new-group-name] input, input[data-testid=db-new-group-name]").First.FillAsync("Ordering");
        await dlg.Locator("[data-testid=db-new-group-add]").ClickAsync();
        await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);
        await p.Canvas().ClickAsync(new() { Position = new() { X = 5, Y = 5 } });
        await p.Mouse.ClickAsync(ox, oy);
        await Expect(p.Inspector().Locator("[data-testid=db-columns]")).ToBeVisibleAsync();
        await ShotAsync(p, "database-diagram");

        // Subject areas.
        await p.Locator("[data-testid=ribbon-groups]").ClickAsync();
        await p.Locator(".mud-popover-open .mud-menu-item").Filter(new() { HasTextString = "Subject areas…" }).ClickAsync();
        dlg = p.MudDialog();
        await dlg.Locator(".db-group-row[data-group=Ordering]").ClickAsync();
        await Expect(dlg.Locator("[data-testid=db-group-members]")).ToContainTextAsync("orders");
        await ShotAsync(dlg, "database-groups");
        await dlg.GetByRole(AriaRole.Button, new() { Name = "Done" }).ClickAsync();
        await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);

        // DDL.
        await p.Locator("[data-testid=ribbon-generate-ddl]").ClickAsync();
        dlg = p.MudDialog();
        await Expect(dlg.Locator("[data-testid=ddl-preview]")).ToContainTextAsync("CREATE TABLE");
        await ShotAsync(dlg, "database-ddl");
        await dlg.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
        await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);

        // Refresh from a changed script: the review, then the highlights.
        await p.Locator("[data-testid=ribbon-db-refresh]").ClickAsync();
        dlg = p.MudDialog();
        var changed = DatabaseTests.Script.Replace("total numeric(12,2)", "total numeric(14,2),\n    currency char(3) NOT NULL DEFAULT 'EUR'")
            .Replace("CREATE INDEX orders_customer_idx ON sales.orders (customer_id);", "CREATE INDEX orders_placed_idx ON sales.orders (placed_at);");
        await dlg.Locator("[data-testid=db-import-text]").FillAsync(changed);
        await Expect(dlg.Locator("[data-testid=db-import-ok]")).ToContainTextAsync("Review changes");
        await dlg.Locator("[data-testid=db-import-ok]").ClickAsync();
        dlg = p.MudDialog();
        await Expect(dlg.Locator(".db-review-row")).ToHaveCountAsync(4);
        await ShotAsync(dlg, "database-review");
        await dlg.Locator("[data-testid=db-review-apply]").ClickAsync();
        await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);
        await Expect(p.Locator(".diagram-host .chg-outline")).Not.ToHaveCountAsync(0);
        await ShotAsync(p, "database-refreshed");
        await p.Locator(".chg-legend .chg-toggle").ClickAsync();   // hide the highlights again
    }
}
