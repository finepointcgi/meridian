using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Serialization.Reports;
using Meridian.Web.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>
/// Opt-in, like <see cref="DocsScreenshotTests"/>: the user guide pages' remaining screenshots, each in the dark and the
/// light theme (<c>MERIDIAN_DOCS_SCREENSHOTS=1 dotnet test tests/Meridian.E2E --filter GuideScreenshotTests</c>). One test
/// per picture, each with a fresh account, so one flaky screen never blocks the others.
/// </summary>
public sealed class GuideScreenshotTests(ServerFixture f) : E2ETest(f)
{
    private static readonly string Dir = ImagesDir();

    private static string ImagesDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Meridian.slnx"))) return Path.Combine(d.FullName, "docs-site", "images");
        throw new InvalidOperationException("Repository root (Meridian.slnx) not found");
    }

    private string _theme = "dark";

    // ------------------------------------------------------------------ helpers

    /// <summary>A fresh account (theme changes never reach the shared test users), signed in on the workbench at 1440×900.</summary>
    private async Task<(BrowserSession S, string User)> FreshAsync()
    {
        Directory.CreateDirectory(Dir);
        var s = await OpenAsync(null, "/login?mode=register", waitForWorkbench: false);
        var p = s.Page;
        var user = "guide" + RandomNumberGenerator.GetHexString(6, lowercase: true);
        await p.GetByLabel("Username").FillAsync(user);
        await p.GetByLabel("Display name").FillAsync("Dana Docs");
        await p.GetByLabel("Password").FillAsync("Pw-" + RandomNumberGenerator.GetHexString(20, lowercase: true));
        await p.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();
        await s.WaitForWorkbenchAsync();
        await p.SetViewportSizeAsync(1440, 900);
        s.IgnoreErrors.Add(e => e.Contains("ResizeObserver", StringComparison.Ordinal));
        return (s, user);
    }

    /// <summary>Runs <paramref name="capture"/> once per theme, switching with the title bar's theme button.</summary>
    private async Task BothThemesAsync(IPage p, Func<Task> capture)
    {
        foreach (var theme in new[] { "dark", "light" })
        {
            _theme = theme;
            await SetThemeAsync(p, theme);
            await capture();
        }
    }

    private static async Task SetThemeAsync(IPage p, string theme)
    {
        if (await p.Locator("div.app").First.GetAttributeAsync("class") is { } cls && !cls.Split(' ').Contains(theme))
            await p.Locator("header.titlebar .tb-theme").ClickAsync();
        await Expect(p.Locator("div.app").First).ToHaveClassAsync(new Regex($@"\b{theme}\b"));
    }

    private static async Task TidyAsync(IPage p, bool moveMouse = true)
    {
        for (var i = 0; i < 5 && await p.Locator(".mud-snackbar button").CountAsync() > 0; i++)
        {
            try { await p.Locator(".mud-snackbar button").First.ClickAsync(new() { Timeout = 2000 }); } catch (PlaywrightException) { }
        }
        await Expect(p.Locator(".mud-snackbar")).ToHaveCountAsync(0);
        if (!moveMouse) return;
        // Somewhere with no tooltip: the tree's left edge, else an empty spot of the canvas or dock.
        foreach (var (x, y) in new[] { (2f, 300f), (1000f, 460f), (720f, 860f) })
        {
            await p.Mouse.MoveAsync(x, y);
            try { await Expect(p.Locator(".mud-tooltip.mud-popover-open:visible")).ToHaveCountAsync(0, new() { Timeout = 2000 }); return; }
            catch (PlaywrightException) { }
        }
        var open = p.Locator(".mud-tooltip.mud-popover-open:visible");
        if (await open.CountAsync() > 0) throw new InvalidOperationException("A tooltip stays open: " + string.Join(" | ", await open.AllInnerTextsAsync()));
    }

    private async Task ShotAsync(IPage p, string name, bool moveMouse = true)
    {
        await TidyAsync(p, moveMouse);
        await p.ScreenshotAsync(new() { Path = Path.Combine(Dir, $"{name}-{_theme}.png"), Animations = ScreenshotAnimations.Disabled });
    }

    private async Task ShotAsync(ILocator element, string name)
    {
        await TidyAsync(element.Page);
        await element.ScreenshotAsync(new() { Path = Path.Combine(Dir, $"{name}-{_theme}.png"), Animations = ScreenshotAnimations.Disabled });
    }

    private async Task<Guid> SeedProjectAsync(string user, string name, ModelRepository model)
    {
        var db = F.App.Services.GetRequiredService<IMeridianStore>();
        var me = (await db.FindUserAsync(user))!;
        return await db.CreateProjectAsync(name, ProjectFile.Serialize(model), me.Id);
    }

    private static async Task OpenProjectAsync(IPage p, Guid id, string name)
    {
        await p.GotoAsync($"/?project={id}");
        await Expect(p.ProjectTitle()).ToHaveTextAsync(name, new() { Timeout = 20_000 });
    }

    private static async Task CommitAsync(IPage p, string message)
    {
        await p.Locator("header.titlebar .btn-primary").ClickAsync();
        var dlg = p.MudDialog();
        await dlg.Locator("input").FillAsync(message);
        await dlg.GetByRole(AriaRole.Button, new() { Name = "OK" }).ClickAsync();
        await p.SnackbarAsync("Committed version");
        await TidyAsync(p);
    }

    /// <summary>The requirements table's row whose cells (text fields) hold <paramref name="value"/>.</summary>
    private static async Task<ILocator> RequirementRowAsync(IPage p, string value)
    {
        var rows = p.DockBody().Locator("tbody tr");
        var index = -1;
        await Ui.EventuallyAsync(async () =>
        {
            index = await rows.EvaluateAllAsync<int>("(rows, v) => rows.findIndex(r => [...r.querySelectorAll('input, textarea')].some(i => i.value === v))", value);
            return index >= 0;
        }, $"a requirements table row with {value}");
        return rows.Nth(index);
    }

    private static async Task RenameInInspectorAsync(IPage p, string from, string to)
    {
        await p.Tree().GetByText(from, new() { Exact = true }).First.ClickAsync();
        var name = p.Inspector().GetByLabel("Name", new() { Exact = true });
        await Expect(name).ToHaveValueAsync(from);
        await name.FillAsync(to);
        await name.PressAsync("Tab");
        await Expect(p.Tree().GetByText(to, new() { Exact = true }).First).ToBeVisibleAsync();
    }

    // ------------------------------------------------------------------ samples.md

    [DocsScreenshotFact]
    public async Task New_project_gallery()
    {
        var (s, _) = await FreshAsync();
        var p = s.Page;
        await BothThemesAsync(p, async () =>
        {
            await p.ProjectMenuAsync("New project…");
            var dlg = p.MudDialog();
            var cubesat = dlg.Locator(".tpl-card").Filter(new() { Has = p.Locator(".tpl-name", new() { HasTextString = "CubeSat mission" }) }).First;
            await cubesat.ClickAsync();
            foreach (var name in new[] { "Infusion pump", "Adaptive cruise control", "CubeSat mission" })
            {
                var card = dlg.Locator(".tpl-card").Filter(new() { Has = p.Locator(".tpl-name", new() { HasTextString = name }) }).First;
                await card.ScrollIntoViewIfNeededAsync();
                await Ui.EventuallyAsync(async () => await card.Locator("img").CountAsync() == 0
                    || await card.Locator("img").First.EvaluateAsync<bool>("i => i.complete && i.naturalWidth > 0"), $"{name} thumbnail");
            }
            await ShotAsync(dlg, "samples-gallery");
            await dlg.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
            await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);
        });
    }

    // ------------------------------------------------------------------ requirements.md

    [DocsScreenshotFact]
    public async Task Requirements_table_with_inspector()
    {
        var (s, _) = await FreshAsync();
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Vehicle");
        await p.RibbonAsync("Table");
        var row = await RequirementRowAsync(p, "REQ-002");
        await row.ClickAsync(new() { Position = new() { X = 3, Y = 3 } });
        await Expect(p.Inspector().GetByLabel("Id", new() { Exact = true })).ToHaveValueAsync("REQ-002");
        await BothThemesAsync(p, () => ShotAsync(p, "requirements-table"));
    }

    [DocsScreenshotFact]
    public async Task Requirements_reimport_preview()
    {
        var (s, _) = await FreshAsync();
        var p = s.Page;
        await p.NewProjectAsync("Blank project", "Brake requirements");

        async Task ImportAsync(string csv, bool apply)
        {
            await p.DockBody().GetByRole(AriaRole.Button, new() { Name = "Re-import" }).ClickAsync();
            var dlg = p.Locator(".ss-dialog");
            await dlg.Locator("input[type=file][data-testid=ss-file]").SetInputFilesAsync(new FilePayload { Name = "brake-requirements.csv", MimeType = "text/csv", Buffer = Encoding.UTF8.GetBytes(csv) });
            await Expect(dlg.Locator("[data-testid=ss-preview]")).ToBeVisibleAsync();
            await dlg.Locator("button.ss-next").ClickAsync();
            await Expect(dlg.Locator("[data-testid=ss-columns]")).ToBeVisibleAsync();
            await dlg.Locator("button.ss-check-btn").ClickAsync();
            await Expect(dlg.Locator("[data-testid=ss-summary]")).ToBeVisibleAsync();
            if (!apply) return;
            await dlg.Locator("button.ss-apply").ClickAsync();
            await p.SnackbarAsync("3 added");
            await Expect(p.Locator(".ss-dialog")).ToHaveCountAsync(0);
            await TidyAsync(p);
        }

        await p.RibbonAsync("Table");
        await ImportAsync("""
            Id,Name,Text
            REQ-1,Holding force,The brake shall hold the vehicle on a 30 % slope.
            REQ-2,Apply time,The brake shall apply within 600 ms.
            REQ-3,Release noise,Releasing the brake shall stay below 60 dB(A).
            """, apply: true);
        await RequirementRowAsync(p, "Apply time");
        await BothThemesAsync(p, async () =>
        {
            await ImportAsync("""
                Id,Name,Text
                REQ-1,Holding force,The brake shall hold the vehicle on a 30 % slope.
                REQ-2,Apply time,The brake shall apply within 450 ms.
                REQ-4,Auto release,The brake shall release when the driver pulls away.
                """, apply: false);
            var dlg = p.Locator(".ss-dialog");
            await Expect(dlg.Locator("tr.ss-change.added")).ToHaveCountAsync(1);
            await Expect(dlg.Locator("tr.ss-change.changed")).ToHaveCountAsync(1);
            await Expect(dlg.Locator("tr.ss-change.removed")).ToHaveCountAsync(1);
            await ShotAsync(dlg, "requirements-reimport");
            await dlg.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
            await Expect(p.Locator(".ss-dialog")).ToHaveCountAsync(0);
        });
    }

    // ------------------------------------------------------------------ administration.md

    [DocsScreenshotFact]
    public async Task Administration_users_tab()
    {
        var s = await OpenAsync(F.Admin);
        var p = s.Page;
        await p.SetViewportSizeAsync(1440, 900);
        var before = await p.Locator("div.app").First.GetAttributeAsync("class") ?? "";
        try
        {
            await BothThemesAsync(p, async () =>
            {
                await p.GotoAsync("/admin");
                await Expect(p.GetByText("Server administration").First).ToBeVisibleAsync();
                await Expect(p.Locator(".admin-table").First).ToContainTextAsync(F.Alice.Username);
                await ShotAsync(p, "admin-users");
                await p.GotoAsync("/");
                await s.WaitForWorkbenchAsync();
            });
        }
        finally
        {
            await SetThemeAsync(p, before.Split(' ').Contains("light") ? "light" : "dark");   // leave the shared admin as it was
        }
    }

    // ------------------------------------------------------------------ sysml-v2.md

    [DocsScreenshotFact]
    public async Task SysML_v2_text_next_to_a_diagram()
    {
        var (s, _) = await FreshAsync();
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Vehicle");
        await p.Tree().GetByText("Vehicle Structure", new() { Exact = true }).First.DblClickAsync();
        await Expect(p.Locator(".diagram-host svg.main")).ToBeVisibleAsync();
        await p.Tree().GetByText("Structure", new() { Exact = true }).First.ClickAsync();
        await p.RibbonAsync("Edit text");
        var panel = p.DockBody().Locator(".sx-panel");
        await Expect(panel.Locator(".sx-scope")).ToContainTextAsync("Structure");
        await Expect(panel.Locator(".sx-state")).ToContainTextAsync("In sync with the model");
        await BothThemesAsync(p, () => ShotAsync(p, "sysml-text"));
    }

    // ------------------------------------------------------------------ tables-queries.md

    [DocsScreenshotFact]
    public async Task Tables_hazard_analysis()
    {
        var (s, _) = await FreshAsync();
        var p = s.Page;
        await p.NewProjectAsync("Infusion pump", "Infusion pump");
        await p.Tree().GetByText("Hazard analysis", new() { Exact = true }).First.DblClickAsync();
        await Expect(p.Tabs().Filter(new() { HasTextString = "Hazard analysis" }).First).ToHaveClassAsync(new Regex(@"\bactive\b"));
        await Expect(p.ViewTab().Locator(".gt-status").First).ToContainTextAsync("row");
        await Expect(p.ViewTab().Locator("table.gt-table tbody tr").First).ToBeVisibleAsync();
        await BothThemesAsync(p, () => ShotAsync(p, "tables-hazard"));
    }

    // ------------------------------------------------------------------ drawing.md

    [DocsScreenshotFact]
    public async Task Drawing_with_hover_arrows_and_format_bar()
    {
        var (s, _) = await FreshAsync();
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Vehicle");
        await p.Tree().GetByText("Vehicle Structure", new() { Exact = true }).First.DblClickAsync();
        await Expect(p.Locator(".toolbox.open .shapes-body")).ToBeVisibleAsync();
        var engine = p.Nodes().Filter(new() { Has = p.Locator("text", new() { HasTextRegex = new Regex("^Engine$") }) }).First;
        await Expect(engine).ToBeVisibleAsync();
        await BothThemesAsync(p, async () =>
        {
            var box = (await engine.CenterAsync()).Box;
            await p.Mouse.ClickAsync(box.X + 20, box.Y + 12);
            await Expect(p.Locator(".selbar")).ToBeVisibleAsync();
            await TidyAsync(p);
            var (x, y, _) = await engine.CenterAsync();
            await p.Mouse.MoveAsync(x, y);
            await Expect(p.Locator(".diagram-host svg.main g").Filter(new() { Has = p.Locator("title", new() { HasTextString = "drag to connect" }) }).First).ToBeAttachedAsync();
            await ShotAsync(p, "drawing-bdd", moveMouse: false);
        });
    }

    // ------------------------------------------------------------------ version-control.md

    [DocsScreenshotFact]
    public async Task Version_history_compare()
    {
        var (s, _) = await FreshAsync();
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Vehicle");
        await CommitAsync(p, "Initial architecture");
        await RenameInInspectorAsync(p, "Engine", "Electric Motor");
        await RenameInInspectorAsync(p, "Driver", "Test Driver");
        await BothThemesAsync(p, async () =>
        {
            await p.ProjectMenuAsync("Version history…");
            var dlg = p.MudDialog();
            await dlg.Locator(".mud-list-item").Filter(new() { HasTextString = "Initial architecture" }).First.ClickAsync();
            await Expect(dlg.GetByText("Changes since v1").First).ToBeVisibleAsync();
            await Expect(dlg.GetByRole(AriaRole.Button, new() { Name = "Restore" }).First).ToBeVisibleAsync();
            await ShotAsync(dlg, "version-history");
            await p.Keyboard.PressAsync("Escape");
            await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);
        });
    }

    [DocsScreenshotFact]
    public async Task Merge_dialog_with_a_conflict()
    {
        var (s, _) = await FreshAsync();
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Vehicle");

        async Task BranchMenuAsync(string item)
        {
            await p.Locator(".vc-branch").ClickAsync();
            await p.Locator(".mud-popover-open .mud-menu-item").Filter(new() { HasTextString = item }).First.ClickAsync();
        }

        await BranchMenuAsync("New branch");
        var dlg = p.MudDialog();
        await dlg.Locator("input").First.FillAsync("lighter-engine");
        await dlg.GetByRole(AriaRole.Button, new() { Name = "Create" }).ClickAsync();
        await Expect(p.Locator(".vc-branch")).ToContainTextAsync("lighter-engine");
        await RenameInInspectorAsync(p, "Engine", "Engine Lite");
        await RenameInInspectorAsync(p, "Driver", "Test Driver");
        await BranchMenuAsync("main");
        await Expect(p.Locator(".vc-branch")).ToContainTextAsync("main");
        await RenameInInspectorAsync(p, "Engine", "Engine V8");
        await BranchMenuAsync("lighter-engine");
        await Expect(p.Locator(".vc-branch")).ToContainTextAsync("lighter-engine");
        await BothThemesAsync(p, async () =>
        {
            await BranchMenuAsync("Merge lighter-engine into main");
            var merge = p.MudDialog();
            await Expect(merge.Locator(".vc-stat.warn")).ToContainTextAsync("1 conflict");
            await Expect(merge.Locator(".vc-conflicts .vc-crow:not(.vc-head)")).ToHaveCountAsync(1);
            await merge.Locator(".vc-section").Filter(new() { HasTextString = "Result preview" }).First.ClickAsync();
            await Expect(merge.Locator(".vc-diff, .vc-group").First).ToBeVisibleAsync();
            await ShotAsync(merge, "merge-conflict");
            await merge.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
            await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);
        });
    }

    // ------------------------------------------------------------------ reviews.md

    [DocsScreenshotFact]
    public async Task Request_review_dialog()
    {
        var (s, _) = await FreshAsync();
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Vehicle");
        await p.Locator("header.titlebar button.rbtn").Filter(new() { HasTextString = "Share" }).ClickAsync();
        var share = p.MudDialog();
        foreach (var u in new[] { F.Alice, F.Bob })
        {
            await share.GetByPlaceholder("Username").FillAsync(u.Username);
            await share.GetByRole(AriaRole.Button, new() { Name = "Invite" }).ClickAsync();
            await Expect(share).ToContainTextAsync(u.DisplayName);
        }
        await share.GetByRole(AriaRole.Button, new() { Name = "Done" }).ClickAsync();
        await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);

        await BothThemesAsync(p, async () =>
        {
            await p.Tree().GetByText("Structure", new() { Exact = true }).First.ClickAsync();
            await p.RibbonAsync("Request review");
            var dlg = p.MudDialog();
            await dlg.GetByLabel("Title").FillAsync("Structure ready for PDR");
            foreach (var u in new[] { F.Alice, F.Bob })
                await dlg.Locator(".mud-checkbox", new() { HasTextString = u.DisplayName }).ClickAsync();
            await Expect(dlg).ToContainTextAsync("Structure");
            await ShotAsync(dlg, "request-review");
            await dlg.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
            await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);
        });
    }

    // ------------------------------------------------------------------ simulation.md

    [DocsScreenshotFact]
    public async Task Simulation_timeline()
    {
        var (s, _) = await FreshAsync();
        var p = s.Page;
        await p.NewProjectAsync("CubeSat mission", "CubeSat");
        await p.RibbonAsync("Simulate");
        var dock = p.DockBody();
        await dock.Locator(".mud-tab", new() { HasTextString = "Timed (discrete event)" }).ClickAsync();
        var behaviours = dock.Locator(".mud-select").Filter(new() { Has = p.Locator("label", new() { HasTextString = "Behaviors" }) }).First;
        await behaviours.ClickAsync();
        var items = p.Locator(".mud-popover-open .mud-list-item");
        await Expect(items.First).ToBeVisibleAsync();
        var states = await items.EvaluateAllAsync<string[][]>(
            "items => items.map(i => [i.textContent.trim(), String(!!i.querySelector('input[type=checkbox]:checked') || i.classList.contains('mud-selected-item'))])");
        foreach (var (label, want) in states.Select(x => (x[0], x[1] == "true")))
        {
            var wanted = label.StartsWith("Acquire Image", StringComparison.Ordinal) || label.StartsWith("Operating Modes", StringComparison.Ordinal);
            if (wanted != want) await items.Filter(new() { HasTextString = label }).First.ClickAsync();
        }
        if (await p.Locator(".mud-overlay").CountAsync() > 0) await p.Locator(".mud-overlay").Last.ClickAsync(new() { Force = true });
        else await behaviours.ClickAsync();
        await Expect(p.Locator(".mud-popover-open .mud-list-item")).ToHaveCountAsync(0);
        await dock.GetByRole(AriaRole.Button, new() { Name = "Start" }).ClickAsync();
        // A few events, not the whole mission: the image steps stay readable next to the mode they run in.
        var next = dock.GetByRole(AriaRole.Button, new() { Name = "Next event" });
        await Expect(next).ToBeEnabledAsync();
        for (var i = 0; i < 8 && await next.IsEnabledAsync(); i++)
        {
            await next.ClickAsync();
            await Expect(dock.Locator(".mud-chip").Filter(new() { HasTextString = $"· {i + 1} event" }).First).ToBeVisibleAsync();
        }
        await Expect(dock.Locator("svg.chart").First).ToBeVisibleAsync();
        await Expect(dock.Locator(".mud-chip").Filter(new() { HasTextRegex = new Regex(@"t = .* events") }).First).ToBeVisibleAsync();
        // The timeline sits under the controls: a taller window, and the simulation dock on its own.
        await p.SetViewportSizeAsync(1440, 1400);
        await dock.Locator("svg.chart").First.ScrollIntoViewIfNeededAsync();
        await BothThemesAsync(p, () => ShotAsync(p.Locator(".dock"), "simulation-timeline"));
    }

    // ------------------------------------------------------------------ variants-trade-studies.md

    [DocsScreenshotFact]
    public async Task Variants_touring_configuration()
    {
        var (s, _) = await FreshAsync();
        var p = s.Page;
        await p.NewProjectAsync("Blank project", "Product line");
        await p.RibbonAsync("Variants");
        var dock = p.DockBody();
        await dock.GetByRole(AriaRole.Button, new() { Name = "Add example product line" }).ClickAsync();
        await Expect(dock.Locator(".var-feature")).ToHaveCountAsync(12);
        await dock.Locator(".mud-select").Filter(new() { Has = p.Locator("label", new() { HasTextString = "Configuration" }) }).First.ClickAsync();
        await p.Locator(".mud-popover-open .mud-list-item").Filter(new() { HasTextString = "Touring" }).First.ClickAsync();
        await Expect(dock.Locator(".var-feature.excluded").First).ToBeVisibleAsync();
        await dock.Locator("[data-testid=variant-display]").SelectOptionAsync("Grey");
        await p.Locator("[data-testid=variant-chip] select").SelectOptionAsync(new SelectOptionValue { Label = "Touring" });
        await Expect(p.Node("Electric")).ToHaveCSSAsync("opacity", "0.22");
        await BothThemesAsync(p, () => ShotAsync(p, "variants-touring"));
    }

    // ------------------------------------------------------------------ document-generation.md

    [DocsScreenshotFact]
    public async Task Report_size_warning_and_background_job()
    {
        var (s, user) = await FreshAsync();
        var p = s.Page;
        var model = LargeModelGenerator.Generate(new LargeModelOptions { Scale = 0.4 });
        var t = ReportTemplates.Sdd();
        t.Name = "Huge matrix";
        t.Sections = [new DocSection { Title = "Allocation", Blocks = [new TraceMatrixBlock { RowQuery = "kind:Requirement", ColumnQuery = "kind:Block" }] }];
        ReportTemplates.Save(model, t);
        var id = await SeedProjectAsync(user, "Large system", model);
        await OpenProjectAsync(p, id, "Large system");
        // The ribbon button's tooltip stays up behind the modal dialog: keep tooltips out of these pictures.
        await p.AddStyleTagAsync(new() { Content = ".mud-tooltip { display: none !important; }" });

        async Task<ILocator> OpenReportAsync(string template)
        {
            await p.RibbonAsync("Report");
            var dlg = p.MudDialog();
            await dlg.Locator(".mud-select").Filter(new() { Has = p.Locator("label", new() { HasTextString = "Template" }) }).First.ClickAsync();
            await p.Locator(".mud-popover-open .mud-list-item").Filter(new() { HasTextString = template }).First.ClickAsync();
            await dlg.GetByRole(AriaRole.Button, new() { Name = "Generate", Exact = true }).ClickAsync();
            return dlg;
        }

        await BothThemesAsync(p, async () =>
        {
            var dlg = await OpenReportAsync("Huge matrix");
            await Expect(dlg.Locator(".report-size-warning")).ToContainTextAsync("pages", new() { Timeout = 60_000 });
            await ShotAsync(dlg, "report-size-warning");
            await dlg.GetByRole(AriaRole.Button, new() { Name = "Close" }).First.ClickAsync();
            await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);
        });

        // One background report, pictured in both themes (while it runs or once it is ready).
        var job = p.Locator("footer.statusbar .item.report-job").First;
        var background = await OpenReportAsync("Model Overview");
        await Expect(background.Locator(".report-background-note")).ToBeVisibleAsync(new() { Timeout = 60_000 });
        await background.GetByRole(AriaRole.Button, new() { Name = "Close" }).First.ClickAsync();
        await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);
        await Expect(job).ToBeVisibleAsync(new() { Timeout = 60_000 });
        await Expect(p.Locator("footer.statusbar .item.report-job")).ToHaveCountAsync(1);
        await BothThemesAsync(p, () => ShotAsync(p.Locator("footer.statusbar"), "report-background"));
    }

    // ------------------------------------------------------------------ integrations.md

    [DocsScreenshotFact]
    public async Task Linked_Jira_issue()
    {
        var (s, user) = await FreshAsync();
        var p = s.Page;
        var model = SampleModels.Vehicle();
        var connection = Guid.NewGuid();
        var req = model.OfKind(ElementKind.Requirement).First(e => e.Name == "Acceleration");
        IssueLinks.Add(model, req, new IssueLink
        {
            Connection = connection.ToString(), Key = "VEH-42", Url = "https://acme.atlassian.net/browse/VEH-42",
            Title = "Verify 0–100 km/h in 8 s on the test track", Status = "In Progress", Assignee = "Priya Patel", Synced = DateTime.UtcNow,
        });
        var id = await SeedProjectAsync(user, "Vehicle", model);
        var db = F.App.Services.GetRequiredService<IMeridianStore>();
        await db.SaveIntegrationAsync(new IntegrationConnection(connection, id, IntegrationKinds.IssueTracker, "Jira",
            new IssueTrackerConfig { Type = TrackerTypes.JiraCloud, BaseUrl = "https://acme.atlassian.net", Project = "VEH" }.ToJson(), null, DateTime.UtcNow));
        await p.GotoAsync($"/?project={id}&element={req.Id}");
        await Expect(p.ProjectTitle()).ToHaveTextAsync("Vehicle", new() { Timeout = 20_000 });
        await Expect(p.Inspector().GetByLabel("Name", new() { Exact = true })).ToHaveValueAsync("Acceleration");
        var key = p.Inspector().Locator(".issue-link .issue-key").First;
        await Expect(key).ToHaveTextAsync("VEH-42");
        await key.ScrollIntoViewIfNeededAsync();
        await BothThemesAsync(p, async () =>
        {
            await key.ScrollIntoViewIfNeededAsync();
            await ShotAsync(p.Inspector(), "linked-issues");
        });
    }

    // ------------------------------------------------------------------ getting-started.md

    [DocsScreenshotFact]
    public async Task Desktop_start_screen()
    {
        Directory.CreateDirectory(Dir);
        var folder = Directory.CreateTempSubdirectory("meridian-start-");
        try
        {
            var settings = new Meridian.Desktop.DesktopSettings();
            settings.Servers.Add("https://meridian.example.com");
            foreach (var name in new[] { "Infusion pump.meridian", "CubeSat mission.meridian", "Brake controller.qea" })
            {
                var path = Path.Combine(folder.FullName, name);
                await File.WriteAllTextAsync(path, "{}");
                settings.Remember(path, null, DateTime.UtcNow);
            }
            var html = Meridian.Desktop.StartPage.Html(settings).Replace(folder.FullName, "/Users/dana/Documents/Models", StringComparison.Ordinal);
            await using var context = await F.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1100, Height = 760 } });
            var page = await context.NewPageAsync();
            foreach (var theme in new[] { "dark", "light" })
            {
                _theme = theme;
                await page.EmulateMediaAsync(new() { ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light });
                await page.SetContentAsync(html);
                await Expect(page.Locator("#local")).ToBeVisibleAsync();
                await Expect(page.Locator(".recent button.link[data-file]")).ToHaveCountAsync(3);
                await page.ScreenshotAsync(new() { Path = Path.Combine(Dir, $"desktop-start-{_theme}.png"), Animations = ScreenshotAnimations.Disabled });
            }
        }
        finally { folder.Delete(true); }
    }
}
