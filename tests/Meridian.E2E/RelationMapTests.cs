using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>Relation map (Cameo's Relation Map): explore a requirement's connections and turn a selection into a diagram.</summary>
public sealed class RelationMapTests(ServerFixture f) : E2ETest(f)
{
    private static string DocsImage(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Meridian.slnx"))) return Path.Combine(d.FullName, "docs-site", "images", name);
        throw new InvalidOperationException("Repository root (Meridian.slnx) not found");
    }

    [E2EFact]
    public async Task Relation_map_of_a_requirement_deepens_filters_and_becomes_a_diagram()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Relation map " + Guid.NewGuid().ToString("N")[..6]);
        await p.Tree().GetByText("Vehicle Requirements", new() { Exact = true }).First.DblClickAsync();
        await Expect(p.Node("Acceleration")).ToHaveCountAsync(1);

        // ⌘K "Relation map of REQ-002 Acceleration": a centre tab with the requirement's traceability.
        await p.Locator(".cmdk").ClickAsync();
        await p.Locator(".cmdp-input input").FillAsync("Relation map of REQ-002");
        await p.Keyboard.PressAsync("Enter");
        await Expect(p.Tabs().Filter(new() { HasTextString = "Relation map: Acceleration" })).ToHaveClassAsync(new Regex(@"\bactive\b"));
        var tab = p.ViewTab().Locator("[data-testid=relation-map]");
        await Expect(tab).ToBeVisibleAsync();
        await Expect(tab.Locator("[data-testid=rm-root]")).ToContainTextAsync("REQ-002 Acceleration");
        await Expect(tab.Locator("[data-testid=rm-rel-filter]")).ToContainTextAsync("Traceability");
        ILocator MapNode(string name) => tab.Locator("svg.rm-svg g[data-node]").Filter(new() { Has = p.Locator("text.rm-name", new() { HasTextRegex = new Regex("^" + Regex.Escape(name) + "$") }) });
        await Expect(MapNode("Vehicle")).ToHaveCountAsync(1);
        await Expect(MapNode("Acceleration Test")).ToHaveCountAsync(1);
        await Expect(tab.Locator("svg.rm-svg text", new() { HasTextString = "«verify»" })).ToHaveCountAsync(1);

        // Depth 1 → 3: Engine (behind Engine Power) only shows from level 2.
        await tab.Locator("[data-testid=rm-depth-dec]").ClickAsync();
        await Expect(tab.Locator("[data-testid=rm-depth]")).ToHaveTextAsync("1");
        await Expect(MapNode("Engine")).ToHaveCountAsync(0);
        await tab.Locator("[data-testid=rm-depth-inc]").ClickAsync();
        await tab.Locator("[data-testid=rm-depth-inc]").ClickAsync();
        await Expect(tab.Locator("[data-testid=rm-depth]")).ToHaveTextAsync("3");
        await Expect(MapNode("Engine")).ToHaveCountAsync(1);

        // Every relationship kind brings in the structure; the Traceability preset takes it out again.
        await tab.Locator("[data-testid=rm-rel-filter]").ClickAsync();
        await tab.Locator("[data-testid=rm-preset-All]").ClickAsync();
        await Expect(MapNode("FuelTank")).ToHaveCountAsync(1);
        await tab.Locator("[data-testid=rm-preset-Traceability]").ClickAsync();
        await Expect(MapNode("FuelTank")).ToHaveCountAsync(0);
        await Expect(tab.Locator("[data-testid=rm-rel-filter]")).ToContainTextAsync("Traceability");
        await tab.Locator("[data-testid=rm-rel-filter]").ClickAsync();   // close the panel

        // Fit, then select three nodes (click, ⇧-click) and make a diagram of them.
        await tab.Locator("[data-testid=rm-fit]").ClickAsync();
        if (Environment.GetEnvironmentVariable("MERIDIAN_DOCS_SCREENSHOTS") is "1" or "true")   // the user guide's picture (docs-site/docs/relation-map.md)
            await tab.ScreenshotAsync(new() { Path = DocsImage("relation-map-dark.png"), Animations = ScreenshotAnimations.Disabled });
        await MapNode("REQ-002 Acceleration").Locator("text.rm-name").ClickAsync();
        await Expect(tab.Locator("svg.rm-svg g.rm-node.sel")).ToHaveCountAsync(1);
        await MapNode("Vehicle").Locator("text.rm-name").ClickAsync(new() { Modifiers = [KeyboardModifier.Shift] });
        await MapNode("Acceleration Test").Locator("text.rm-name").ClickAsync(new() { Modifiers = [KeyboardModifier.Shift] });
        await Expect(tab.Locator("svg.rm-svg g.rm-node.sel")).ToHaveCountAsync(3);
        await Expect(tab.Locator("[data-testid=rm-create-diagram]")).ToContainTextAsync("(3)");
        await tab.Locator("[data-testid=rm-create-diagram]").ClickAsync();

        await Expect(p.Tabs().Filter(new() { HasTextString = "Acceleration relations" })).ToHaveClassAsync(new Regex(@"\bactive\b"));
        await Expect(p.Nodes()).ToHaveCountAsync(3);
        await Expect(p.StatusMessage()).ToContainTextAsync("Created");
        // The map stays open as its own tab.
        await Expect(p.Tabs().Filter(new() { HasTextString = "Relation map: Acceleration" })).ToHaveCountAsync(1);
    }
}
