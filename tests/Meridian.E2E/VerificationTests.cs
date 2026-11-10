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

        // Ribbon → Verify: the results tab opens with the pass / fail summary.
        await p.RibbonAsync("Verify");
        var dock = p.DockBody();
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

        // Changing the fuel tank makes the range result stale.
        await p.Tree().GetByText("capacity", new() { Exact = true }).First.ClickAsync();
        var value = p.Inspector().GetByLabel("Default value", new() { Exact = true });
        await value.FillAsync("70");
        await value.PressAsync("Tab");
        await Expect(range.Locator(".verdict.stale")).ToHaveCountAsync(1);
        await Expect(svg.Locator("g.vfy-mark.stale")).ToHaveCountAsync(1);
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
        await Expect(p.Node("Electric")).ToBeHiddenAsync();
        await chip.GetByRole(AriaRole.Button, new() { Name = "Off", Exact = true }).ClickAsync();
        await Expect(p.Node("Electric")).ToBeVisibleAsync();
        await Expect(chip).ToHaveCountAsync(0);
    }
}
