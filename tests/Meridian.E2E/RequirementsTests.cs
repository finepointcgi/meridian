using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

public sealed class RequirementsTests(ServerFixture f) : E2ETest(f)
{
    /// <summary>Types into an inspector text field and commits it (MudTextField commits on blur).</summary>
    private static async Task SetFieldAsync(IPage p, string label, string value)
    {
        var field = p.Inspector().GetByLabel(label, new() { Exact = true });
        await field.FillAsync(value);
        await field.PressAsync("Tab");
    }

    private static async Task PickSelectAsync(IPage p, string label, string option)
    {
        await p.Inspector().Locator(".mud-select").Filter(new() { Has = p.Locator("label", new() { HasTextString = label }) }).First.ClickAsync();
        await p.Locator(".mud-popover-open .mud-list-item").Filter(new() { HasTextString = option }).First.ClickAsync();
    }

    [E2EFact]
    public async Task Requirement_attributes_satisfy_link_coverage_and_suspect_flag()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Blank project", "Reqs " + Guid.NewGuid().ToString("N")[..6]);

        // A design element to satisfy the requirement with.
        await p.RibbonAsync("Block");
        await Expect(p.Inspector()).ToContainTextAsync("Block · in");
        await SetFieldAsync(p, "Name", "Pump");
        await Expect(p.Tree().GetByText("Pump", new() { Exact = true })).ToBeVisibleAsync();

        // New requirement; edit its attributes in the Inspector.
        await p.RibbonAsync("Requirement");
        await Expect(p.Inspector()).ToContainTextAsync("Requirement · in");
        await SetFieldAsync(p, "Name", "Flow rate");
        await SetFieldAsync(p, "Requirement text", "The pump shall deliver 10 l/min.");
        await PickSelectAsync(p, "Priority", "High");
        await SetFieldAsync(p, "Owner", "Hydraulics team");
        await Expect(p.Tree().GetByText("Flow rate").First).ToBeVisibleAsync();
        await Expect(p.Inspector().Locator(".mud-select").Filter(new() { Has = p.Locator("label", new() { HasTextString = "Priority" }) }).First)
            .ToContainTextAsync(new System.Text.RegularExpressions.Regex("high", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        var reqId = await p.Inspector().GetByLabel("Id", new() { Exact = true }).InputValueAsync();
        Assert.False(string.IsNullOrWhiteSpace(reqId));

        // Traceability table: the requirement is not satisfied yet.
        await p.RibbonAsync("Trace table");
        var dock = p.DockBody();
        var row = dock.Locator("table.tt tbody tr").Filter(new() { HasTextString = "Flow rate" });
        await Expect(row).ToHaveCountAsync(1);
        await Expect(row.Locator(".gap-label").First).ToHaveTextAsync("not satisfied");
        await Expect(dock.Locator(".tt-coverage .cov-num").First).ToContainTextAsync("0/1 sat");

        // Add a satisfy link from the table.
        var headers = await dock.Locator("table.tt thead th").AllInnerTextsAsync();
        var satisfiedCol = headers.ToList().FindIndex(h => h.Trim().StartsWith("Satisfied", StringComparison.OrdinalIgnoreCase));
        Assert.True(satisfiedCol > 1, "Satisfied-by column: " + string.Join(" | ", headers));
        var satisfiedBy = row.Locator("td").Nth(satisfiedCol);
        await satisfiedBy.Locator(".tt-add").ClickAsync();
        await satisfiedBy.Locator(".tt-picker input").FillAsync("Pump");
        await p.Locator(".mud-popover-open .mud-list-item").Filter(new() { HasTextString = "Pump" }).First.ClickAsync();
        await Expect(row.Locator(".tt-pill").Filter(new() { HasTextString = "Pump" })).ToHaveCountAsync(1);
        await Expect(row.Locator(".gap-label").Filter(new() { HasTextString = "not satisfied" })).ToHaveCountAsync(0);
        await Expect(dock.Locator(".tt-coverage .cov-num").First).ToContainTextAsync("1/1 sat");
        Assert.Equal(1, (await p.CountsAsync()).Relationships);

        // Changing the requirement text makes the satisfy link suspect.
        await row.ClickAsync();
        await SetFieldAsync(p, "Requirement text", "The pump shall deliver 12 l/min.");
        var pill = row.Locator(".tt-pill").Filter(new() { HasTextString = "Pump" });
        await Expect(pill).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bsuspect\b"));
        await Expect(p.Inspector().Locator(".suspect-row")).ToHaveCountAsync(1);
        await Expect(dock.GetByRole(AriaRole.Button, new() { Name = "Clear 1 suspect" })).ToBeVisibleAsync();

        // Clear it from the table.
        await pill.Locator(".sus").ClickAsync();
        await Expect(pill).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bsuspect\b"));
        await Expect(p.Inspector().Locator(".suspect-row")).ToHaveCountAsync(0);
        await Expect(dock.Locator(".tt-coverage .cov-num").First).ToContainTextAsync("1/1 sat");
    }
}
