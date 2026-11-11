using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>Small page helpers for the Meridian workbench, shared by the browser tests.</summary>
public static class Ui
{
    public static ILocator ProjectTitle(this IPage p) => p.Locator("header.titlebar .project span").First;
    public static ILocator StatusCounts(this IPage p) => p.Locator("footer.statusbar .item").Nth(1);
    public static ILocator StatusMessage(this IPage p) => p.Locator("footer.statusbar .msg");
    public static ILocator Canvas(this IPage p) => p.Locator(".diagram-host .canvas");
    public static ILocator Nodes(this IPage p) => p.Locator(".diagram-host svg.main g[data-node]");
    public static ILocator Node(this IPage p, string label) => p.Nodes().Filter(new() { Has = p.Locator("text", new() { HasTextString = label }) });
    public static ILocator Tree(this IPage p) => p.Locator("main.workbench > section.card").First.Locator(".card-body");
    public static ILocator DockBody(this IPage p) => p.Locator(".dock-body");
    /// <summary>The content of the active view tab (table, query, report template) in the centre.</summary>
    public static ILocator ViewTab(this IPage p) => p.Locator(".diagram-host .view-host");
    public static ILocator Tabs(this IPage p) => p.Locator(".tabstrip .tab");
    public static ILocator Inspector(this IPage p) => p.Locator("aside.stack > section.card").First;
    public static ILocator MudDialog(this IPage p) => p.Locator(".mud-dialog").Last;

    /// <summary>Counts from the status bar: "70 elements · 37 relationships · 8 diagrams" ("1 diagram" for one, see Plural).</summary>
    public static async Task<(int Elements, int Relationships, int Diagrams)> CountsAsync(this IPage p)
    {
        var m = Regex.Match(await p.StatusCounts().InnerTextAsync(), @"(\d+) elements? · (\d+) relationships? · (\d+) diagrams?\b");
        if (!m.Success) throw new InvalidOperationException("Unexpected status bar text");
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
    }

    /// <summary>The exports that sit in the project menu's Export ▸ submenu (since 0.7.1).</summary>
    private static readonly string[] ExportItems =
        ["Download .meridian file", "Export XMI", "Export SysML v2", "Export diagram as", "Copy diagram as"];

    /// <summary>Opens the project menu in the title bar and clicks an item (opening Export ▸ first for the exports).</summary>
    public static async Task ProjectMenuAsync(this IPage p, string item)
    {
        await p.Locator("header.titlebar .project").ClickAsync();
        if (ExportItems.Any(e => item.StartsWith(e, StringComparison.Ordinal)))
        {
            await p.Locator(".mud-popover-open .project-menu .mud-menu-sub-menu-activator").Filter(new() { HasTextString = "Export" }).ClickAsync();
            await p.Locator(".mud-popover-open .export-menu .mud-menu-item").Filter(new() { HasTextString = item }).First.ClickAsync();
            return;
        }
        await p.Locator(".mud-popover-open .mud-menu-item").Filter(new() { HasTextString = item }).First.ClickAsync();
    }

    /// <summary>Opens the Help menu (? in the title bar) and clicks an item (Tour…, Keyboard &amp; mouse, What's new…).</summary>
    public static async Task HelpMenuAsync(this IPage p, string item)
    {
        await p.Locator("header.titlebar [data-testid=help-menu]").ClickAsync();
        await p.Locator(".mud-popover-open .mud-menu-item").Filter(new() { HasTextString = item }).First.ClickAsync();
    }

    /// <summary>Clicks a ribbon button by its caption (e.g. "Trace table"), first switching to the ribbon tab that holds it.</summary>
    public static async Task RibbonAsync(this IPage p, string caption)
    {
        var button = p.Locator("nav.ribbon button.rbtn").Filter(new() { Has = p.Locator("span.t", new() { HasTextRegex = new Regex("^" + Regex.Escape(caption) + "$") }) }).First;
        await p.RibbonTabAsync(await button.EvaluateAsync<string>("b => b.closest('.rgroup')?.dataset.tab ?? ''"));
        await button.ClickAsync();
    }

    /// <summary>
    /// Shows a ribbon tab by its id (create, diagram, requirements, trace, views, simulate, generate, ai, database, code). The
    /// Review and Extend tabs of 0.7.0 were merged into trace and generate; their old ids still work here.
    /// </summary>
    public static async Task RibbonTabAsync(this IPage p, string tab)
    {
        if (tab.Length == 0) return;
        tab = Meridian.Web.Components.Workbench.Workbench.CurrentRibbonTabId(tab);
        var t = p.Locator($"nav.ribbon .rtab[data-tab={tab}]");
        if (await t.GetAttributeAsync("aria-selected") != "true") await t.ClickAsync();
        await Expect(p.Locator($"nav.ribbon .rgroup[data-tab={tab}]")).ToBeVisibleAsync();
    }

    /// <summary>Clicks a dock tab by its label.</summary>
    public static Task DockTabAsync(this IPage p, string label) =>
        p.Locator(".dock-tabs .dock-tab").Filter(new() { HasTextString = label }).First.ClickAsync();

    /// <summary>New project from the template gallery; waits until it is open.</summary>
    public static async Task NewProjectAsync(this IPage p, string templateName, string projectName)
    {
        await p.ProjectMenuAsync("New project…");
        var dlg = p.MudDialog();
        await dlg.Locator(".tpl-card").Filter(new() { Has = p.Locator(".tpl-name", new() { HasTextString = templateName }) }).First.ClickAsync();
        var name = dlg.GetByLabel("Project name");
        await name.FillAsync(projectName);
        await dlg.GetByRole(AriaRole.Button, new() { Name = "Create project" }).ClickAsync();
        await Expect(p.ProjectTitle()).ToHaveTextAsync(projectName, new() { Timeout = 20_000 });
    }

    /// <summary>Clicks a tile in the diagram's shapes panel.</summary>
    public static Task ShapeTileAsync(this IPage p, string title) =>
        p.Locator(".toolbox .shapes-body .tile").Filter(new() { Has = p.Locator("span", new() { HasTextRegex = new Regex("^" + Regex.Escape(title)) }) }).First.ClickAsync();

    /// <summary>Center of a locator's box in page coordinates.</summary>
    public static async Task<(float X, float Y, LocatorBoundingBoxResult Box)> CenterAsync(this ILocator l)
    {
        // The canvas SVG is re-rendered as markup, so a shape can be swapped for an identical copy mid-measure: retry briefly.
        for (var attempt = 0; ; attempt++)
        {
            await l.WaitForAsync(new() { State = WaitForSelectorState.Attached });
            if (await l.BoundingBoxAsync() is { } b) return (b.X + b.Width / 2, b.Y + b.Height / 2, b);
            if (attempt >= 20) throw new InvalidOperationException("Element has no box");
            await Task.Delay(50);
        }
    }

    /// <summary>Waits until the inline label editor on the canvas is gone (edit committed or cancelled).</summary>
    public static Task NoInlineEditorAsync(this IPage p) => Expect(p.Locator(".inline-editor")).ToHaveCountAsync(0);

    public static Task SnackbarAsync(this IPage p, string text) =>
        Expect(p.Locator(".mud-snackbar").Filter(new() { HasTextString = text }).First).ToBeVisibleAsync();

    /// <summary>Polls a condition (like Expect.Poll) until it holds or the timeout passes.</summary>
    public static async Task EventuallyAsync(Func<Task<bool>> condition, string because, int timeoutMs = 10_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            if (await condition()) return;
            if (DateTime.UtcNow > until) throw new Xunit.Sdk.XunitException("Timed out: " + because);
            await Task.Delay(100);
        }
    }
}
