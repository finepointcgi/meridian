using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

public sealed class InstanceTests(ServerFixture f) : E2ETest(f)
{
    private static ILocator MenuItem(IPage p, string text) => p.Locator(".ctx-menu .ctx-item", new() { HasTextString = text });

    [E2EFact]
    public async Task Create_an_instance_of_Vehicle_edit_a_slot_in_the_instance_table_and_see_it_on_an_object_diagram()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Instances " + Guid.NewGuid().ToString("N")[..6]);

        // "Create instance of…" on the block in the containment tree builds the whole instance tree.
        await p.Tree().GetByText("Vehicle", new() { Exact = true }).First.ClickAsync(new() { Button = MouseButton.Right });
        await MenuItem(p, "Create instance of Vehicle").ClickAsync();
        await Expect(p.StatusMessage()).ToContainTextAsync("Created vehicle1 : Vehicle with 3 part instances");
        await Expect(p.Inspector().Locator(".insp-head .title")).ToHaveTextAsync("vehicle1");

        // The instance table: one row, a column per property, unit-aware cells.
        await p.Inspector().GetByRole(AriaRole.Button, new() { Name = "Instance table" }).ClickAsync();
        await Expect(p.Tabs().Filter(new() { HasTextString = "Vehicle instances" })).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"));
        var table = p.ViewTab().Locator("[data-testid='instance-table']");
        var row = table.Locator("tr[data-instance='vehicle1']");
        await Expect(row).ToBeVisibleAsync();
        var mass = row.Locator("input[data-feature='mass']");
        await Expect(mass).ToHaveValueAsync("1450 kg");
        await mass.FillAsync("1.6 t");
        await mass.PressAsync("Tab");
        await Expect(mass).ToHaveValueAsync("1600 kg");

        // A value that doesn't fit is refused with the reason.
        await mass.FillAsync("3 m");
        await mass.PressAsync("Tab");
        await Expect(table.Locator("[data-testid='instance-error']")).ToContainTextAsync("does not convert to kg");

        // On an object diagram: the instance box, underlined "name : Type", its slots, and its part instances with links.
        await row.Locator("td.gt-num").ClickAsync();
        await table.GetByRole(AriaRole.Button, new() { Name = "Object diagram" }).ClickAsync();
        await Expect(p.Tabs().Filter(new() { HasTextString = "vehicle1 objects" })).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"));
        var node = p.Node("vehicle1 : Vehicle").First;
        await Expect(node).ToBeVisibleAsync();
        await Expect(node.Locator("text", new() { HasTextString = "vehicle1 : Vehicle" })).ToHaveAttributeAsync("text-decoration", "underline");
        await Expect(node).ToContainTextAsync("mass = 1600 kg");
        await Expect(p.Node("engine : Engine").First).ToBeVisibleAsync();
        await Expect(p.Locator(".diagram-host svg.main g[data-edge]")).ToHaveCountAsync(3);
    }
}
