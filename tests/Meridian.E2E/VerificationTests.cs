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

        // Editing the range criterion makes its result stale.
        var crit = range.Locator("input.vfy-crit");
        await crit.FillAsync("capacity * 12 km/L >= 600 km");
        await crit.PressAsync("Tab");
        await Expect(range.Locator(".verdict.stale")).ToHaveCountAsync(1);
        await Expect(svg.Locator("g.vfy-mark.stale")).ToHaveCountAsync(1);
    }

    [E2EFact]
    public async Task Adding_a_second_criterion_gives_each_its_own_result_and_the_requirement_the_aggregate()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Criteria " + Guid.NewGuid().ToString("N")[..6]);
        await p.Tree().GetByText("Vehicle Requirements", new() { Exact = true }).First.DblClickAsync();
        await Expect(p.Node("Acceleration")).ToHaveCountAsync(1);

        await p.RibbonAsync("Verify");
        var dock = p.DockBody();
        var table = dock.Locator("[data-testid=verify-results]");
        var accel = table.Locator("tr.row").Filter(new() { HasTextString = "REQ-002" });
        await Expect(accel.Locator(".verdict.pass")).ToHaveCountAsync(1, new() { Timeout = 20_000 });

        // A second criterion for the acceleration requirement: the vehicle's 1450 kg breaks it.
        await accel.Locator("[data-testid=criterion-add]").ClickAsync();
        var add = table.Locator("input.vfy-crit-new");
        await add.FillAsync("mass <= 1400 kg");
        await add.PressAsync("Tab");
        await Expect(accel.Locator("[data-testid=criteria-toggle]")).ToContainTextAsync("2 criteria");
        var crits = table.Locator("tr.crit-row:not(.new)");
        await Expect(crits).ToHaveCountAsync(2);
        await Expect(crits.Nth(1).Locator(".verdict.none")).ToHaveCountAsync(1);          // not run yet
        await Expect(accel.Locator(".verdict.stale")).ToHaveCountAsync(1);

        // Verify again: each criterion has its own result, the requirement fails because one does.
        await dock.Locator("[data-testid=verify-run]").ClickAsync();
        await Expect(crits.Nth(1).Locator(".verdict.fail")).ToHaveCountAsync(1, new() { Timeout = 20_000 });
        await Expect(crits.Nth(0).Locator(".verdict.pass")).ToHaveCountAsync(1);
        await Expect(crits.Nth(1)).ToContainTextAsync("1450 kg");
        await Expect(accel.Locator(".verdict.fail")).ToHaveCountAsync(1);
        await Expect(accel.Locator(".verdict.stale")).ToHaveCountAsync(0);
        await Expect(dock.Locator("[data-testid=verify-summary]")).ToContainTextAsync("2 fail");

        // The diagram colours it by the aggregate.
        var svg = p.Locator(".diagram-host svg.main");
        await Expect(svg.Locator("g.vfy-mark.fail")).ToHaveCountAsync(2);
        await Expect(svg.Locator("g.vfy-mark.pass")).ToHaveCountAsync(1);
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
