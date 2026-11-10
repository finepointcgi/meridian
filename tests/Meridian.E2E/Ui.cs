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
    public static ILocator Tree(this IPage p) => p.Locator("main.workbench > section.card").First;
    public static ILocator DockBody(this IPage p) => p.Locator(".dock-body");
    public static ILocator Inspector(this IPage p) => p.Locator("aside.stack > section.card").First;
    public static ILocator MudDialog(this IPage p) => p.Locator(".mud-dialog").Last;

    /// <summary>Counts from the status bar: "70 elements · 37 relationships · 8 diagrams".</summary>
    public static async Task<(int Elements, int Relationships, int Diagrams)> CountsAsync(this IPage p)
    {
        var m = Regex.Match(await p.StatusCounts().InnerTextAsync(), @"(\d+) elements · (\d+) relationships · (\d+) diagrams");
        if (!m.Success) throw new InvalidOperationException("Unexpected status bar text");
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
    }

    /// <summary>Opens the project menu in the title bar and clicks an item.</summary>
    public static async Task ProjectMenuAsync(this IPage p, string item)
    {
        await p.Locator("header.titlebar .project").ClickAsync();
        await p.Locator(".mud-popover-open .mud-menu-item").Filter(new() { HasTextString = item }).First.ClickAsync();
    }

    /// <summary>Clicks a ribbon button by its caption (e.g. "Trace table").</summary>
    public static Task RibbonAsync(this IPage p, string caption) =>
        p.Locator("nav.ribbon button.rbtn").Filter(new() { Has = p.Locator("span.t", new() { HasTextRegex = new Regex("^" + Regex.Escape(caption) + "$") }) }).First.ClickAsync();

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
        var b = await l.BoundingBoxAsync() ?? throw new InvalidOperationException("Element has no box");
        return (b.X + b.Width / 2, b.Y + b.Height / 2, b);
    }

    /// <summary>Waits until the inline label editor on the canvas is gone (edit committed or cancelled).</summary>
    public static Task NoInlineEditorAsync(this IPage p) => Expect(p.Locator(".inline-editor")).ToHaveCountAsync(0);

    public static Task SnackbarAsync(this IPage p, string text) =>
        Expect(p.Locator(".mud-snackbar").Filter(new() { HasTextString = text }).First).ToBeVisibleAsync();
}
