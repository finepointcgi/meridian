using System.Security.Cryptography;
using Meridian.Core.CodeSync;
using Meridian.Core.Model;
using Meridian.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>
/// Opt-in: writes the user guide's screenshots to <c>docs-site/images</c> in the dark and the light theme
/// (<c>MERIDIAN_DOCS_SCREENSHOTS=1 dotnet test tests/Meridian.E2E --filter DocsScreenshotTests</c>).
/// </summary>
public sealed class DocsScreenshotFactAttribute : FactAttribute
{
    public DocsScreenshotFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MERIDIAN_DOCS_SCREENSHOTS") is not ("1" or "true"))
            Skip = "Set MERIDIAN_DOCS_SCREENSHOTS=1 to regenerate the screenshots in docs-site/images.";
        else if (BrowserSupport.MissingReason is { } why) Skip = why;
    }
}

public sealed class DocsScreenshotTests(ServerFixture f) : E2ETest(f)
{
    private const string Flowchart = """
        ---
        title: Order flow
        ---
        flowchart TD
            start((Start)) --> receive[Receive order]
            receive --> check{In stock?}
            check -->|yes| ship[Ship parcel]
            check -->|no| wait[Back-order]
            ship --> done(((End)))
            wait --> done
        """;

    private static string ImagesDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Meridian.slnx"))) return Path.Combine(d.FullName, "docs-site", "images");
        throw new InvalidOperationException("Repository root (Meridian.slnx) not found");
    }

    private string _theme = "dark";
    private readonly string _dir = ImagesDir();

    private async Task ShotAsync(IPage p, string name)
    {
        await TidyAsync(p);
        await p.ScreenshotAsync(new() { Path = Path.Combine(_dir, $"{name}-{_theme}.png"), Animations = ScreenshotAnimations.Disabled });
    }

    /// <summary>No toasts or tooltips from earlier steps in the picture.</summary>
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

    private async Task ShotAsync(ILocator element, string name)
    {
        await TidyAsync(element.Page);
        await element.ScreenshotAsync(new() { Path = Path.Combine(_dir, $"{name}-{_theme}.png"), Animations = ScreenshotAnimations.Disabled });
    }

    private static ILocator MenuItem(IPage p, string text) => p.Locator(".ctx-menu .ctx-item", new() { HasTextString = text });

    [DocsScreenshotFact]
    public async Task Writes_the_user_guide_screenshots_in_both_themes()
    {
        Directory.CreateDirectory(_dir);
        // A fresh account, so the theme changes here never reach the other tests' users.
        var s = await OpenAsync(null, "/login?mode=register", waitForWorkbench: false);
        var p = s.Page;
        var user = "docs" + RandomNumberGenerator.GetHexString(4, lowercase: true);
        await p.GetByLabel("Username").FillAsync(user);
        await p.GetByLabel("Display name").FillAsync("Dana Docs");
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
            await CaptureAsync(s, user);
        }
    }

    private async Task CaptureAsync(BrowserSession s, string user)
    {
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", $"Vehicle ({_theme})");

        // The workbench with a block definition diagram (README).
        await p.Tree().GetByText("Vehicle Structure", new() { Exact = true }).First.DblClickAsync();
        await Expect(p.Locator(".diagram-host svg.main")).ToBeVisibleAsync();
        await p.Locator(".diagram-host .canvas").ClickAsync(new() { Position = new() { X = 5, Y = 5 } });
        await ShotAsync(p, "workbench");

        // Help ▸ Tour.
        await p.HelpMenuAsync("Tour…");
        await Expect(p.Locator(".tour-chapter")).ToHaveCountAsync(Meridian.Web.Components.Workbench.TourChapters.All.Length);
        await ShotAsync(p, "tour-chapters");
        await p.Locator(".tour-chapter[data-chapter=derive]").ClickAsync();
        await Expect(p.Locator(".tour-spot")).ToHaveCountAsync(1);
        await ShotAsync(p, "tour-step");
        await p.Keyboard.PressAsync("Escape");
        await Expect(p.Locator(".tour")).ToHaveCountAsync(0);

        // Create diagrams from diagrams: use case → activity.
        await p.Tree().GetByText("Driver Use Cases", new() { Exact = true }).First.DblClickAsync();
        await Expect(p.Node("Drive Vehicle")).ToHaveCountAsync(1);
        var (x, y, _) = await p.Node("Drive Vehicle").CenterAsync();
        await p.Mouse.ClickAsync(x, y, new() { Button = MouseButton.Right });
        await ShotAsync(p, "derive-menu");
        await MenuItem(p, "Create activity from use case").ClickAsync();
        var dlg = p.MudDialog();
        await Expect(dlg.Locator(".dv-row").First).ToBeVisibleAsync();
        await ShotAsync(dlg, "derive-dialog");
        await dlg.Locator("button.dv-create").ClickAsync();
        await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);
        await Expect(p.Locator(".crumbs .dtype")).ToHaveTextAsync("ACT");
        await p.Tree().GetByText("Drive Vehicle", new() { Exact = true }).First.ClickAsync();
        await ShotAsync(p, "derive-activity");

        // Multi-select and Link to….
        await p.Tree().GetByText("Driver Use Cases", new() { Exact = true }).First.DblClickAsync();
        await Expect(p.Node("Refuel")).ToHaveCountAsync(1);
        var (dx, dy, _) = await p.Node("Drive Vehicle").CenterAsync();
        var (rx, ry, _) = await p.Node("Refuel").CenterAsync();
        var (sx, sy, _) = await p.Node("Start Engine").CenterAsync();
        await p.Mouse.ClickAsync(dx, dy);
        foreach (var (px, py) in new[] { (rx, ry), (sx, sy) })
        {
            await p.Keyboard.DownAsync("Shift");
            await p.Mouse.ClickAsync(px, py);
            await p.Keyboard.UpAsync("Shift");
        }
        await Expect(p.Inspector().Locator("[data-testid=bulk-count]")).ToHaveTextAsync("3 selected");
        await ShotAsync(p, "multi-select");
        await p.Mouse.ClickAsync(sx, sy, new() { Button = MouseButton.Right });
        await MenuItem(p, "Link to…").ClickAsync();
        dlg = p.MudDialog();
        await dlg.Locator("[data-testid=bulk-link-search]").FillAsync("Driver");
        await dlg.Locator("[data-testid=bulk-link-target]", new() { HasTextString = "Driver" }).First.ClickAsync();
        await Expect(dlg.Locator("[data-testid=bulk-link-kinds] .chip.on")).ToBeVisibleAsync();
        await ShotAsync(dlg, "link-to");
        await dlg.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await p.Keyboard.PressAsync("Escape");

        // Mermaid.
        await p.ProjectMenuAsync("Paste Mermaid…");
        dlg = p.MudDialog();
        await dlg.Locator("textarea.mmd-input").FillAsync(Flowchart);
        await Expect(dlg.Locator(".mmd-row")).ToHaveCountAsync(1);
        await ShotAsync(dlg, "mermaid-dialog");
        await dlg.Locator("button.mmd-import").ClickAsync();
        await Expect(p.Locator(".tabstrip .tab.active")).ToContainTextAsync("Order flow");
        await Expect(p.Nodes()).ToHaveCountAsync(6);
        await ShotAsync(p, "mermaid-result");

        // Settings and the AI assistant.
        await p.Locator("header.titlebar .tb-settings").ClickAsync();
        var settings = p.Locator(".settings-dialog");
        await Expect(settings).ToBeVisibleAsync();
        await ShotAsync(settings, "settings-appearance");
        await settings.Locator(".settings-tab[data-section=Assistant]").ClickAsync();
        await Expect(settings.Locator(".settings-tab.on")).ToHaveTextAsync("AI assistant");
        await ShotAsync(settings, "settings-ai");
        await p.Keyboard.PressAsync("Escape");
        await Expect(settings).ToBeHiddenAsync();
        await p.DockTabAsync("Assistant");
        await Expect(p.DockBody().Locator(".ai-off")).ToBeVisibleAsync();
        await ShotAsync(p.Locator(".dock"), "assistant-off");
        await p.Locator(".dock-tabs > .mud-icon-button").Last.ClickAsync();   // fold the dock away again
        await Expect(p.Locator(".dock-body")).ToHaveCountAsync(0);

        // Code sync: a push lands on a review branch; What changed and the highlights.
        var db = F.App.Services.GetRequiredService<IMeridianStore>();
        var me = (await db.FindUserAsync(user))!;
        var project = (await db.ListProjectsAsync(me.Id)).Single(x => x.Name == $"Vehicle ({_theme})").Id;
        CodeSyncE2E.Script.Enqueue(m =>
        {
            var structure = m.OfKind(ElementKind.Package).First(x => x.Name == "Structure");
            var battery = m.Create(structure, ElementKind.Block, "Battery");
            var vehicle = m.OfKind(ElementKind.Block).First(x => x.Name == "Vehicle");
            m.Set(vehicle, FieldKeys.Documentation, "The whole car, as the code describes it.");
            return new CodeSyncChanges([battery.Id], [vehicle.Id], [], []);
        });
        var outcome = await F.App.Services.GetRequiredService<ICodeSyncRunner>().RunAsync(new CodeSyncRequest(
            project, await CodeSyncE2E.ReviewFirstAsync(F.App.Services, project, "acme/vehicle"), "acme/vehicle", "main", "0123456789abcdef0123456789abcdef01234567", "https://git.example/acme/vehicle/commit/0123456",
            "Dana Dev", "Add battery model", [new CodeFile("src/Battery.cs", "class Battery {}")], IsFullSnapshot: true));
        Assert.Equal(CodeSyncStatus.AwaitingReview, outcome.Status);
        var badge = p.Locator("[data-testid=code-changes-badge]");
        await Expect(badge).ToContainTextAsync("Code changes");
        await badge.ClickAsync();
        var review = p.ViewTab();
        await Expect(review.Locator(".chg-row[data-kind=added]")).ToContainTextAsync("Battery");
        await ShotAsync(p, "code-changes");
        await review.Locator(".chg-row[data-kind=added]").GetByRole(AriaRole.Button, new() { Name = "Show on diagram" }).ClickAsync();
        await Expect(p.Canvas().Locator("rect.chg-outline.chg-added")).ToHaveCountAsync(1);
        await ShotAsync(p, "code-highlights");

        // Auto-diagram.
        await p.NewProjectAsync("CubeSat mission", $"CubeSat ({_theme})");
        await p.RibbonAsync("Auto-diagram");
        dlg = p.MudDialog();
        await Expect(dlg.Locator(".ad-row").First).ToBeVisibleAsync();
        await ShotAsync(dlg, "auto-diagram-dialog");
        await dlg.Locator("button.ad-create").ClickAsync();
        await Expect(p.Locator(".tabstrip .tab.active")).ToContainTextAsync("Package overview");
        await ShotAsync(p, "auto-diagram-result");

        // A phone: the tour as a bottom sheet.
        await p.SetViewportSizeAsync(390, 844);
        await p.Locator(".m-topbar button[aria-label=Account]").ClickAsync();
        await p.Locator(".mud-popover-open .mud-menu-item", new() { HasTextString = "Tour…" }).ClickAsync();
        await p.Locator(".tour-chapter[data-chapter=generate]").ClickAsync();
        await Expect(p.Locator(".tour-card .tour-step")).ToContainTextAsync("Step 1 of 3");
        await ShotAsync(p, "tour-phone");
        await p.Keyboard.PressAsync("Escape");
        await p.SetViewportSizeAsync(1440, 900);
    }
}
