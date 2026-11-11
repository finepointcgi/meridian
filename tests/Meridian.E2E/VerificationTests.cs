using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>Simulation-driven requirement verification and the variant view on diagrams.</summary>
public sealed class VerificationTests(ServerFixture f) : E2ETest(f)
{
    [E2EFact]
    public async Task Verify_the_vehicle_sample_and_see_pass_fail_on_the_table_and_the_diagram()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Verify " + Guid.NewGuid().ToString("N")[..6]);
        await p.Tree().GetByText("Vehicle Requirements", new() { Exact = true }).First.DblClickAsync();
        await Expect(p.Node("Acceleration")).ToHaveCountAsync(1);

        // Ribbon → Verify: the results open as a tab next to the diagrams (not in the bottom dock) with the pass / fail summary.
        await p.RibbonAsync("Verify");
        await Expect(p.Tabs().Filter(new() { HasTextString = "Verification" })).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"));
        var dock = p.ViewTab();
        await Expect(dock.Locator("[data-testid=verify-tab]")).ToBeVisibleAsync();
        var table = dock.Locator("[data-testid=verify-results]");
        await Expect(table.Locator("tr.row")).ToHaveCountAsync(3);
        await Expect(table.Locator("tr.row .verdict.pass")).ToHaveCountAsync(2, new() { Timeout = 20_000 });
        await Expect(table.Locator("tr.row .verdict.fail")).ToHaveCountAsync(1);
        await Expect(dock.Locator("[data-testid=verify-summary]")).ToContainTextAsync("2 pass");
        await Expect(p.StatusMessage()).ToContainTextAsync("Verified 3 requirements");

        // Drill down: the range requirement fails with its computed value and margin.
        var range = table.Locator("tr.row").Filter(new() { HasTextString = "REQ-003" });
        await Expect(range).ToContainTextAsync("550 km");
        await range.ClickAsync();
        await Expect(table.Locator("tr.detail")).ToContainTextAsync("Parametric (FuelTank)");

        // Requirements on the diagram are coloured by their result.
        var svg = p.Locator(".diagram-host svg.main");
        await Expect(svg.Locator("g.vfy-mark.pass")).ToHaveCountAsync(2);
        await Expect(svg.Locator("g.vfy-mark.fail")).ToHaveCountAsync(1);
        await Expect(p.Locator("[data-testid=verify-chip]")).ToContainTextAsync("✓ 2");

        // Editing the range criterion makes its result stale.
        var crit = range.Locator("input.vfy-crit");
        await crit.FillAsync("capacity * 12 km/L >= 600 km");
        await crit.PressAsync("Tab");
        await Expect(range.Locator(".verdict.stale")).ToHaveCountAsync(1);
        await Expect(svg.Locator("g.vfy-mark.stale")).ToHaveCountAsync(1);
    }

    /// <summary>
    /// The results are a centre tab (docs/UX-AUDIT-0.7.md finding 30): the dock's Verification entry opens it, it opens once,
    /// and at 1280×800 and 1440×900 the table fits without sideways scrolling.
    /// </summary>
    [E2EFact]
    public async Task Verification_results_open_as_a_centre_tab_that_fits_a_laptop_window()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Verify tab " + Guid.NewGuid().ToString("N")[..6]);
        await p.Tree().GetByText("Vehicle Requirements", new() { Exact = true }).First.DblClickAsync();

        // The bottom dock keeps a Verification shortcut: it opens the centre tab instead of a dock panel.
        await p.Locator("[data-testid=dock-verification]").ClickAsync();
        var tab = p.ViewTab().Locator("[data-testid=verify-tab]");
        await Expect(tab).ToBeVisibleAsync();
        await Expect(p.DockBody().Locator("[data-testid=verify-summary]")).ToHaveCountAsync(0);
        await tab.Locator("[data-testid=verify-run]").ClickAsync();
        await Expect(tab.Locator("[data-testid=verify-results] tr.row .verdict.pass")).ToHaveCountAsync(2, new() { Timeout = 20_000 });

        // Back to the diagram, then Verify again: the same tab comes forward (one Verification tab).
        await p.Tabs().Filter(new() { HasTextString = "Vehicle Requirements" }).First.ClickAsync();
        await Expect(tab).ToHaveCountAsync(0);
        await p.RibbonAsync("Verify");
        await Expect(tab).ToBeVisibleAsync();
        await Expect(p.Tabs().Filter(new() { HasTextString = "Verification" })).ToHaveCountAsync(1);

        foreach (var (w, h) in new[] { (1280, 800), (1440, 900) })
        {
            await p.SetViewportSizeAsync(w, h);
            await Expect(tab.Locator("[data-testid=verify-results]")).ToBeVisibleAsync();
            var fits = await p.EvaluateAsync<bool>(@"() => {
                const host = document.querySelector('.diagram-host .view-host');
                const table = host.querySelector('[data-testid=verify-results]');
                return host.scrollWidth <= host.clientWidth + 1 && table.getBoundingClientRect().right <= host.getBoundingClientRect().right + 1
                    && document.documentElement.scrollWidth <= window.innerWidth;
            }");
            Assert.True(fits, $"The verification results scroll sideways at {w}×{h}");
            // Requirement ids stay on one line (they wrapped in the 0.7.0 dock at these sizes).
            var idHeight = await p.EvaluateAsync<double>("() => document.querySelector('.view-host [data-testid=verify-results] tr.row td').getBoundingClientRect().height");
            var idLine = await p.EvaluateAsync<double>("() => parseFloat(getComputedStyle(document.querySelector('.view-host [data-testid=verify-results] tr.row td')).lineHeight) || 18");
            Assert.True(idHeight < idLine * 2 + 12, $"Requirement ids wrap at {w}×{h} ({idHeight}px rows)");
        }
    }

    [E2EFact]
    public async Task Switching_the_variant_greys_out_what_it_leaves_out()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Blank project", "Variants " + Guid.NewGuid().ToString("N")[..6]);
        await p.RibbonAsync("Variants");
        var dock = p.DockBody();
        await dock.GetByRole(AriaRole.Button, new() { Name = "Add example product line" }).ClickAsync();
        await Expect(p.Node("Combustion")).ToHaveCountAsync(1);
        await Expect(dock.Locator(".var-feature")).ToHaveCountAsync(12);

        // Show the "Eco" configuration on diagrams, greyed: the features it does not select fade out.
        await dock.Locator("[data-testid=variant-display]").SelectOptionAsync("Grey");
        await Expect(p.Node("Combustion")).ToHaveCSSAsync("opacity", "0.22");
        await Expect(p.Node("Electric")).ToHaveCSSAsync("opacity", "1");
        var chip = p.Locator("[data-testid=variant-chip]");
        await Expect(chip).ToContainTextAsync("Variant");

        // Switch to "Touring" from the canvas chip: now Electric is the one greyed out.
        await chip.Locator("select").SelectOptionAsync(new SelectOptionValue { Label = "Touring" });
        await Expect(p.Node("Electric")).ToHaveCSSAsync("opacity", "0.22");
        await Expect(p.Node("Combustion")).ToHaveCSSAsync("opacity", "1");

        // Hide instead of grey, then back to the whole model.
        await chip.GetByRole(AriaRole.Button, new() { Name = "Hide", Exact = true }).ClickAsync();
        await Expect(p.Node("Electric")).ToHaveCSSAsync("display", "none");
        await chip.GetByRole(AriaRole.Button, new() { Name = "Off", Exact = true }).ClickAsync();
        await Expect(p.Node("Electric")).ToBeVisibleAsync();
        await Expect(chip).ToHaveCountAsync(0);
    }
}
