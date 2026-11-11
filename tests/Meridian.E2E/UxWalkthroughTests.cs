using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meridian.Core.CodeSync;
using Meridian.Core.Model;
using Meridian.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>
/// Opt-in: the ease-of-use walkthrough (<c>MERIDIAN_UX_WALKTHROUGH=1 dotnet test tests/Meridian.E2E --filter UxWalkthroughTests</c>).
/// </summary>
public sealed class UxWalkthroughFactAttribute : FactAttribute
{
    public UxWalkthroughFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MERIDIAN_UX_WALKTHROUGH") is not ("1" or "true"))
            Skip = "Set MERIDIAN_UX_WALKTHROUGH=1 to run the UX walkthrough (screenshots of every feature's entry point).";
        else if (BrowserSupport.MissingReason is { } why) Skip = why;
    }
}

/// <summary>
/// Visits every feature's entry point like a first-time user, at desktop, laptop, tablet and phone sizes in the dark and the
/// light theme. Screenshots, 2×2 contact sheets and <c>audit.json</c> (dialogs taller than the window, sideways scrolling,
/// icon buttons without a name) go to <c>$MERIDIAN_UX_DIR</c> or <c>$TMPDIR/meridian-ux</c>, outside the repository.
/// A step that fails is recorded in the audit and the walk goes on, so one broken entry point does not hide the rest.
/// </summary>
public sealed class UxWalkthroughTests(ServerFixture f) : E2ETest(f)
{
    private static readonly (string Name, int W, int H)[] Viewports =
        [("desktop", 1440, 900), ("laptop", 1280, 800), ("tablet", 1024, 768), ("phone", 390, 844)];

    private static string OutDir => Environment.GetEnvironmentVariable("MERIDIAN_UX_DIR") is { Length: > 0 } d ? d
        : Path.Combine(Path.GetTempPath(), "meridian-ux");

    private readonly List<object> _audit = [];
    private readonly List<string> _shots = [];
    private string _prefix = "";
    private int _n;

    private const string AuditJs = """
        () => {
          const vw = innerWidth, vh = innerHeight;
          const hidden = el => { for (let e = el; e; e = e.parentElement) { const s = getComputedStyle(e); if (s.display === 'none' || s.visibility === 'hidden') return true; } const b = el.getBoundingClientRect(); return b.width === 0 || b.height === 0; };
          const dialogs = [...document.querySelectorAll('.mud-dialog')].map(d => { const b = d.getBoundingClientRect();
            return { title: (d.querySelector('.mud-dialog-title')?.innerText || '').trim().slice(0, 60), top: Math.round(b.top), bottom: Math.round(b.bottom), height: Math.round(b.height), width: Math.round(b.width),
                     fits: b.top >= -1 && b.bottom <= vh + 1 && b.left >= -1 && b.right <= vw + 1,
                     focusInside: d.contains(document.activeElement), focused: document.activeElement?.tagName + '.' + (document.activeElement?.className || '').toString().slice(0, 40) }; });
          const noName = [], tipOnly = [];
          for (const b of document.querySelectorAll('button, [role=button], [role=tab]')) {
            if (hidden(b)) continue;
            if ((b.innerText || '').trim()) continue;
            const name = b.getAttribute('aria-label') || b.getAttribute('title') || b.getAttribute('aria-labelledby');
            if (name) continue;
            const html = b.outerHTML.replace(/<path[^>]*>/g, '').replace(/\s+/g, ' ').slice(0, 140);
            if (b.closest('.mud-tooltip-root')) tipOnly.push(html); else noName.push(html);
          }
          const sideways = document.documentElement.scrollWidth > vw + 1 || document.body.scrollWidth > vw + 1;
          const overflowing = [...document.querySelectorAll('body *')].filter(e => { if (hidden(e)) return false; const b = e.getBoundingClientRect(); return b.right > vw + 2 && b.left < vw && getComputedStyle(e).position !== 'fixed'; })
            .slice(0, 5).map(e => e.tagName + '.' + (e.className || '').toString().slice(0, 50));
          return { vw, vh, sideways, overflowing, dialogs, noName: [...new Set(noName)].slice(0, 30), tipOnly: [...new Set(tipOnly)].slice(0, 30) };
        }
        """;

    [UxWalkthroughFact]
    public async Task Walks_every_entry_point_at_four_sizes_in_both_themes()
    {
        Directory.CreateDirectory(OutDir);
        foreach (var old in Directory.GetFiles(OutDir, "*.png")) File.Delete(old);
        var s = await OpenAsync(null, "/login?mode=register", waitForWorkbench: false);
        var p = s.Page;
        s.IgnoreErrors.Add(_ => true);   // an audit, not a regression test: errors go to audit.json instead
        p.Console += (_, m) => { if (m.Type == "error") _audit.Add(new { step = _prefix + "#" + _n, consoleError = m.Text }); };
        var user = "ux" + RandomNumberGenerator.GetHexString(4, lowercase: true);
        await p.GetByLabel("Username").FillAsync(user);
        await p.GetByLabel("Display name").FillAsync("Una User");
        await p.GetByLabel("Password").FillAsync("Pw-" + RandomNumberGenerator.GetHexString(20, lowercase: true));
        await p.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();
        await s.WaitForWorkbenchAsync();

        // First run, exactly as a new user sees it: a context without the fixture's "tour seen" flags.
        _prefix = "first-run";
        if (Environment.GetEnvironmentVariable("MERIDIAN_UX_ONLY") is not { Length: > 0 })
        {
            await using var fresh = await F.Browser.NewContextAsync(new() { BaseURL = F.App.BaseUrl, ViewportSize = new() { Width = 1440, Height = 900 } });
            await fresh.AddCookiesAsync((await s.Context.CookiesAsync()).Select(c => new Cookie { Name = c.Name, Value = c.Value, Domain = c.Domain, Path = c.Path, Expires = c.Expires, HttpOnly = c.HttpOnly, Secure = c.Secure, SameSite = c.SameSite }));
            var first = await fresh.NewPageAsync();
            await first.GotoAsync("/");
            await Expect(first.ProjectTitle()).Not.ToHaveTextAsync("No project", new() { Timeout = 20_000 });
            await Task.Delay(2000);
            await ShotAsync(first, "first-run");
            await first.Keyboard.PressAsync("Escape");
            await Task.Delay(500);
            await ShotAsync(first, "first-run-after-escape");
        }
        await p.SetViewportSizeAsync(1440, 900);

        try { await WalkAsync(s, user); }
        finally
        {
            _audit.Add(new { step = "server", serverErrors = F.App.ServerErrors.Entries });
            await File.WriteAllTextAsync(Path.Combine(OutDir, "audit.json"), JsonSerializer.Serialize(_audit, new JsonSerializerOptions { WriteIndented = true }));
        }
        await ContactSheetsAsync(p);
    }

    private async Task WalkAsync(BrowserSession s, string user)
    {
        var p = s.Page;
        var only = Environment.GetEnvironmentVariable("MERIDIAN_UX_ONLY");   // e.g. "desktop-dark" for a quick re-run
        foreach (var theme in new[] { "dark", "light" })
        {
            await p.SetViewportSizeAsync(1440, 900);
            await SetThemeAsync(p, theme);
            foreach (var (vp, w, h) in Viewports)
            {
                _prefix = $"{vp}-{theme}";
                if (only is { Length: > 0 } && !only.Split(',').Contains(_prefix)) continue;
                _n = 0;
                await p.SetViewportSizeAsync(w, h);
                if (vp == "phone") await PhoneAsync(p);
                else await DesktopAsync(s, user, full: vp is "desktop" or "laptop", tablet: vp == "tablet");
                await p.SetViewportSizeAsync(1440, 900);
            }
        }
    }

    private static async Task SetThemeAsync(IPage p, string theme)
    {
        if (await p.Locator("div.app").First.GetAttributeAsync("class") is { } cls && !cls.Split(' ').Contains(theme))
            await p.Locator("header.titlebar .tb-theme").ClickAsync();
        await Expect(p.Locator("div.app").First).ToHaveClassAsync(new System.Text.RegularExpressions.Regex($@"\b{theme}\b"));
    }

    private async Task ShotAsync(IPage p, string name)
    {
        _n++;
        var file = Path.Combine(OutDir, $"{_prefix}-{_n:00}-{name}.png");
        await p.Mouse.MoveAsync(1, 1);
        await Task.Delay(250);
        try { await p.ScreenshotAsync(new() { Path = file, Animations = ScreenshotAnimations.Disabled, Timeout = 10_000 }); _shots.Add(file); }
        catch (PlaywrightException ex) { _audit.Add(new { step = $"{_prefix}-{_n:00}-{name}", error = "screenshot: " + ex.Message.Split('\n')[0] }); }
        try { _audit.Add(new { step = $"{_prefix}-{_n:00}-{name}", audit = await p.EvaluateAsync<JsonElement>(AuditJs) }); }
        catch (PlaywrightException ex) { _audit.Add(new { step = $"{_prefix}-{_n:00}-{name}", error = "audit: " + ex.Message.Split('\n')[0] }); }
    }

    /// <summary>Runs one step; a failure is recorded with a screenshot of where it got stuck, and the walk goes on.</summary>
    private async Task StepAsync(IPage p, string name, Func<Task> step)
    {
        try { await step(); }
        catch (Exception ex)
        {
            _audit.Add(new { step = $"{_prefix} {name}", error = ex.Message.Split('\n')[0] });
            await ShotAsync(p, name + "-FAILED");
        }
        await CloseAllAsync(p);
    }

    private static async Task CloseAllAsync(IPage p)
    {
        for (var i = 0; i < 4; i++)
        {
            var open = await p.Locator(".mud-dialog, .mud-popover-open .mud-menu-item, .cmdp, .tour, .ctx-menu, .settings-dialog").CountAsync();
            if (open == 0) break;
            await p.Keyboard.PressAsync("Escape");
            await Task.Delay(200);
        }
        if (await p.Locator(".ctx-backdrop").CountAsync() > 0)
        {
            try { await p.Locator(".ctx-backdrop").ClickAsync(new() { Timeout = 1000, Position = new() { X = 2, Y = 2 } }); } catch (PlaywrightException) { }
        }
        if (await p.Locator(".mud-overlay").CountAsync() > 0)
        {
            try { await p.Locator(".mud-overlay").Last.ClickAsync(new() { Timeout = 1000, Position = new() { X = 2, Y = 2 } }); } catch (PlaywrightException) { }
        }
    }

    private async Task DialogShotAsync(IPage p, string name)
    {
        await Expect(p.Locator(".mud-dialog, .settings-dialog").Last).ToBeVisibleAsync();
        await Task.Delay(300);
        await ShotAsync(p, name);
    }

    private async Task MenuShotAsync(IPage p, string name, string item)
    {
        await p.ProjectMenuAsync(item);
        await DialogShotAsync(p, name);
    }

    private async Task DesktopAsync(BrowserSession s, string user, bool full, bool tablet)
    {
        var p = s.Page;
        var tag = _prefix;

        // A blank project: the empty states.
        await StepAsync(p, "blank-project", async () =>
        {
            await p.NewProjectAsync("Blank project", $"Blank {tag}");
            await ShotAsync(p, "blank-project");
            foreach (var d in new[] { "Requirements", "Matrix", "Trace table", "Verification", "Reviews", "Variants", "Simulation", "Assistant" })
            {
                if (!full && d is not ("Requirements" or "Verification")) continue;
                if (tablet) break;
                await p.DockTabAsync(d);
                await Expect(p.DockBody()).ToBeVisibleAsync();
                await ShotAsync(p, "empty-dock-" + d.Replace(' ', '-').ToLowerInvariant());
            }
        });

        await StepAsync(p, "vehicle", async () =>
        {
            await p.NewProjectAsync("Vehicle sample", $"Vehicle {tag}");
            await p.Tree().GetByText("Vehicle Structure", new() { Exact = true }).First.DblClickAsync();
            await Expect(p.Locator(".diagram-host svg.main")).ToBeVisibleAsync();
            await ShotAsync(p, "vehicle-bdd");
        });

        if (tablet)
        {
            await StepAsync(p, "tablet-drawers", async () =>
            {
                await p.Locator(".t-drawer-btn").First.ClickAsync();
                await ShotAsync(p, "tablet-left-drawer");
                await p.Locator(".t-drawer-btn").First.ClickAsync();
                await p.Locator(".t-drawer-btn").Last.ClickAsync();
                await ShotAsync(p, "tablet-right-drawer");
                await p.Locator(".t-drawer-btn").Last.ClickAsync();
            });
        }

        // Every ribbon tab.
        var tabs = await p.Locator("nav.ribbon .rtab").EvaluateAllAsync<string[]>("ts => ts.map(t => t.dataset.tab)");
        _audit.Add(new { step = _prefix + " ribbon", tabs });
        foreach (var t in tabs)
        {
            if (!full && t is not ("create" or "diagram" or "simulate")) continue;
            await StepAsync(p, "ribbon-" + t, async () =>
            {
                await p.RibbonTabAsync(t);
                var buttons = await p.Locator($"nav.ribbon .rgroup[data-tab={t}] .rbtn").EvaluateAllAsync<string[]>("bs => bs.map(b => (b.innerText || b.title).trim())");
                _audit.Add(new { step = _prefix + " ribbon-" + t, buttons });
                await ShotAsync(p, "ribbon-" + t);
            });
        }

        await StepAsync(p, "project-menu", async () =>
        {
            await p.Locator("header.titlebar .project").ClickAsync();
            await Expect(p.Locator(".mud-popover-open .mud-menu-item").First).ToBeVisibleAsync();
            var items = await p.Locator(".mud-popover-open .mud-menu-item").EvaluateAllAsync<string[]>("xs => xs.map(x => x.innerText.trim())");
            _audit.Add(new { step = _prefix + " project-menu", items });
            await ShotAsync(p, "project-menu");
        });

        await StepAsync(p, "palette", async () =>
        {
            await p.Locator(".cmdk").ClickAsync();
            await p.Locator(".cmdp-input input").FillAsync("library");
            await ShotAsync(p, "palette-library");
        });

        // Dialogs from the project menu.
        await StepAsync(p, "settings", async () =>
        {
            await p.Locator("header.titlebar .tb-settings").ClickAsync();
            await Expect(p.Locator(".settings-dialog")).ToBeVisibleAsync();
            await ShotAsync(p, "settings");
            if (full)
            {
                await p.Locator(".settings-dialog .settings-tab[data-section=Assistant]").ClickAsync();
                await ShotAsync(p, "settings-ai");
            }
        });
        await StepAsync(p, "integrations", async () =>
        {
            await MenuShotAsync(p, "integrations", "Integrations");
            if (!full) return;
            foreach (var tab in new[] { "Webhooks", "DOORS", "Code repositories", "Databases" })
            {
                await p.MudDialog().Locator(".mud-tab", new() { HasTextString = tab }).First.ClickAsync();
                await ShotAsync(p, "integrations-" + tab.Split(' ')[0].ToLowerInvariant());
            }
        });
        await StepAsync(p, "used-projects", () => MenuShotAsync(p, "used-projects", "Used projects"));
        await StepAsync(p, "help-menu", async () => { await p.Locator("header.titlebar [data-testid=help-menu]").ClickAsync(); await ShotAsync(p, "help-menu"); });
        await StepAsync(p, "keyboard", async () => { await p.HelpMenuAsync("Keyboard & mouse"); await DialogShotAsync(p, "keyboard-help"); });
        await StepAsync(p, "tour", async () => { await p.HelpMenuAsync("Tour…"); await Expect(p.Locator(".tour-chapter").First).ToBeVisibleAsync(); await ShotAsync(p, "tour-chapters"); });
        await StepAsync(p, "whats-new", async () => { await p.HelpMenuAsync("What's new"); await Task.Delay(500); await ShotAsync(p, "whats-new"); });
        if (full)
        {
            await StepAsync(p, "new-project", async () =>
            {
                await p.ProjectMenuAsync("New project…");
                await DialogShotAsync(p, "new-project");
            });
            await StepAsync(p, "projects", () => MenuShotAsync(p, "projects", "Projects…"));
            await StepAsync(p, "branches", () => MenuShotAsync(p, "branches", "Branches"));
            await StepAsync(p, "history", () => MenuShotAsync(p, "history", "Version history"));
            await StepAsync(p, "baselines", () => MenuShotAsync(p, "baselines", "Baselines"));
            await StepAsync(p, "share", () => MenuShotAsync(p, "share", "Share"));
            await StepAsync(p, "commit", async () => { await p.Locator("header.titlebar .btn-primary").ClickAsync(); await DialogShotAsync(p, "commit"); });
            await StepAsync(p, "mermaid", () => MenuShotAsync(p, "mermaid", "Paste Mermaid"));
            await StepAsync(p, "db-import", () => MenuShotAsync(p, "db-import", "Database schema"));
            await StepAsync(p, "db-connect", () => MenuShotAsync(p, "db-connect", "Connect to a database"));
            await StepAsync(p, "about", async () => { await p.HelpMenuAsync("About"); await DialogShotAsync(p, "about"); });
            await StepAsync(p, "auto-diagram", async () => { await p.RibbonAsync("Auto-diagram"); await DialogShotAsync(p, "auto-diagram"); });
            await StepAsync(p, "report", async () => { await p.RibbonAsync("Report"); await DialogShotAsync(p, "report"); });
            await StepAsync(p, "permissions", async () =>
            {
                await p.Tree().GetByText("Structure", new() { Exact = true }).First.ClickAsync(new() { Button = MouseButton.Right });
                await p.Locator(".ctx-menu").First.WaitForAsync();
                await ShotAsync(p, "tree-context-menu");
                await p.Locator(".ctx-menu .ctx-item", new() { HasTextString = "Permissions…" }).ClickAsync();
                await DialogShotAsync(p, "permissions");
            });
            await StepAsync(p, "derive", async () =>
            {
                await p.Tree().GetByText("Driver Use Cases", new() { Exact = true }).First.DblClickAsync();
                await Expect(p.Node("Drive Vehicle")).ToHaveCountAsync(1);
                var (x, y, _) = await p.Node("Drive Vehicle").CenterAsync();
                await p.Mouse.ClickAsync(x, y, new() { Button = MouseButton.Right });
                await ShotAsync(p, "canvas-context-menu");
                await p.Locator(".ctx-menu .ctx-item", new() { HasTextString = "Create activity from use case" }).ClickAsync();
                await DialogShotAsync(p, "derive");
            });
            await StepAsync(p, "verify", async () =>
            {
                await p.RibbonAsync("Verify");
                await Expect(p.DockBody()).ToBeVisibleAsync();
                await Task.Delay(1500);
                await ShotAsync(p, "verify");
            });
            await StepAsync(p, "custom-table", async () => { await p.RibbonAsync("Generic table"); await Task.Delay(500); await ShotAsync(p, "custom-table"); });
            await StepAsync(p, "query", async () => { await p.RibbonAsync("Query"); await Task.Delay(500); await ShotAsync(p, "query"); });
            await StepAsync(p, "variants", async () => { await p.RibbonAsync("Variants"); await Task.Delay(300); await ShotAsync(p, "variants"); });
            await StepAsync(p, "script", async () => { await p.RibbonAsync("Script"); await Task.Delay(300); await ShotAsync(p, "script"); });
            await StepAsync(p, "assistant", async () => { await p.RibbonAsync("Assistant"); await Task.Delay(300); await ShotAsync(p, "assistant"); });

            // Database tools: import a small schema so the Database tab appears.
            await StepAsync(p, "database", async () =>
            {
                await p.ProjectMenuAsync("Database schema");
                var dlg = p.MudDialog();
                await dlg.Locator("textarea").First.FillAsync("CREATE TABLE customer (id INT PRIMARY KEY, name VARCHAR(80));\nCREATE TABLE orders (id INT PRIMARY KEY, customer_id INT REFERENCES customer(id));");
                await ShotAsync(p, "db-import-filled");
                await dlg.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^(Import|Preview|Next)") }).First.ClickAsync();
                await Task.Delay(800);
                await ShotAsync(p, "db-import-next");
                if (await p.Locator(".mud-dialog").CountAsync() > 0)
                {
                    await p.MudDialog().GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^(Import|Apply|Accept)") }).Last.ClickAsync();
                    await Task.Delay(800);
                }
                await p.RibbonTabAsync("database");
                await ShotAsync(p, "ribbon-database");
                await p.RibbonAsync("DDL…");
                await DialogShotAsync(p, "generate-ddl");
            });

            // Code sync: a push waiting for review shows the Code tab and What changed.
            await StepAsync(p, "code", async () =>
            {
                var db = F.App.Services.GetRequiredService<IMeridianStore>();
                var me = (await db.FindUserAsync(user))!;
                var project = (await db.ListProjectsAsync(me.Id)).Single(x => x.Name == $"Vehicle {tag}").Id;
                CodeSyncE2E.Script.Enqueue(m =>
                {
                    var structure = m.OfKind(ElementKind.Package).First(x => x.Name == "Structure");
                    var battery = m.Create(structure, ElementKind.Block, "Battery");
                    return new CodeSyncChanges([battery.Id], [], [], []);
                });
                await F.App.Services.GetRequiredService<ICodeSyncRunner>().RunAsync(new CodeSyncRequest(
                    project, Guid.NewGuid(), "acme/vehicle", "main", "0123456789abcdef0123456789abcdef01234567", "https://git.example/acme/vehicle/commit/0123456",
                    "Dana Dev", "Add battery model", [new CodeFile("src/Battery.cs", "class Battery {}")], IsFullSnapshot: true));
                await Expect(p.Locator("[data-testid=code-changes-badge]")).ToBeVisibleAsync();
                await ShotAsync(p, "code-badge");
                await p.RibbonTabAsync("code");
                await ShotAsync(p, "ribbon-code");
                await p.Locator("[data-testid=code-changes-badge]").ClickAsync();
                await Task.Delay(500);
                await ShotAsync(p, "code-changes");
            });
        }
    }

    private async Task PhoneAsync(IPage p)
    {
        await StepAsync(p, "phone-model", async () =>
        {
            await Expect(p.Locator(".m-topbar")).ToBeVisibleAsync();
            await p.Locator(".m-bottomnav .m-tab[data-view=Model]").ClickAsync();
            await ShotAsync(p, "model");
        });
        foreach (var view in new[] { "Diagram", "Requirements", "Reviews", "Comments" })
            await StepAsync(p, "phone-" + view, async () => { await p.Locator($".m-bottomnav .m-tab[data-view={view}]").ClickAsync(); await Task.Delay(300); await ShotAsync(p, view.ToLowerInvariant()); });
        await StepAsync(p, "phone-account", async () =>
        {
            await p.Locator(".m-topbar button[aria-label=Account]").ClickAsync();
            await ShotAsync(p, "account-menu");
        });
        await StepAsync(p, "phone-settings", async () =>
        {
            await p.Locator(".m-topbar button[aria-label=Account]").ClickAsync();
            await p.Locator(".mud-popover-open .mud-menu-item", new() { HasTextString = "Settings" }).ClickAsync();
            await Expect(p.Locator(".settings-dialog")).ToBeVisibleAsync();
            await ShotAsync(p, "settings");
        });
        await StepAsync(p, "phone-tour", async () =>
        {
            await p.Locator(".m-topbar button[aria-label=Account]").ClickAsync();
            await p.Locator(".mud-popover-open .mud-menu-item", new() { HasTextString = "Tour…" }).ClickAsync();
            await ShotAsync(p, "tour");
        });
        await StepAsync(p, "phone-projects", async () =>
        {
            await p.Locator(".m-topbar .m-project").ClickAsync();
            await Task.Delay(400);
            await ShotAsync(p, "projects");
        });
        await StepAsync(p, "phone-search", async () =>
        {
            await p.Locator(".m-topbar button[aria-label=Search]").ClickAsync();
            await Task.Delay(300);
            await ShotAsync(p, "search");
        });
    }

    /// <summary>2×2 contact sheets of the screenshots (sheet-*.png), so a reviewer can look at many at once.</summary>
    private async Task ContactSheetsAsync(IPage p)
    {
        var groups = _shots.GroupBy(f => string.Join('-', Path.GetFileName(f).Split('-').Take(2)));
        await p.SetViewportSizeAsync(1600, 1000);
        foreach (var g in groups)
        {
            var files = g.ToList();
            for (var i = 0; i < files.Count; i += 4)
            {
                var html = new StringBuilder("<html><body style='margin:0;background:#888;font:14px sans-serif'><div style='display:grid;grid-template-columns:1fr 1fr;gap:4px'>");
                foreach (var file in files.Skip(i).Take(4))
                    html.Append($"<figure style='margin:0;background:#fff'><figcaption style='padding:2px 6px'>{Path.GetFileNameWithoutExtension(file)}</figcaption>")
                        .Append($"<img style='width:100%;display:block' src='data:image/png;base64,{Convert.ToBase64String(await File.ReadAllBytesAsync(file))}'/></figure>");
                html.Append("</div></body></html>");
                await p.SetContentAsync(html.ToString());
                await p.ScreenshotAsync(new() { Path = Path.Combine(OutDir, $"sheet-{g.Key}-{i / 4 + 1:00}.png"), FullPage = true });
            }
        }
    }
}
