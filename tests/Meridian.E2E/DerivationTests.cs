using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>"Create diagrams from diagrams": use case → activity → sequence from the context menus, each opening as a tab.</summary>
public sealed class DerivationTests(ServerFixture f) : E2ETest(f)
{
    private static ILocator MenuItem(IPage p, string text) => p.Locator(".ctx-menu .ctx-item", new() { HasTextString = text });

    [E2EFact]
    public async Task Use_case_to_activity_to_sequence_from_the_context_menus()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Derive " + Guid.NewGuid().ToString("N")[..6]);
        var before = await p.CountsAsync();

        // Right-click the "Drive Vehicle" use case on its diagram → Create activity from use case.
        await p.Tree().GetByText("Driver Use Cases", new() { Exact = true }).First.DblClickAsync();
        await Expect(p.Node("Drive Vehicle")).ToHaveCountAsync(1);
        var (x, y, _) = await p.Node("Drive Vehicle").CenterAsync();
        await p.Mouse.ClickAsync(x, y, new() { Button = MouseButton.Right });
        await MenuItem(p, "Create activity from use case").ClickAsync();

        var dlg = p.MudDialog();
        await Expect(dlg.Locator(".dv-title")).ToContainTextAsync("Create activity from use case");
        await Expect(dlg.Locator(".dv-row").First).ToBeVisibleAsync();
        await Expect(dlg.Locator(".dv-warn")).ToContainTextAsync("skeleton");
        await dlg.Locator("button.dv-create").ClickAsync();
        await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);

        // The activity opens as the active tab, with swimlanes and the action.
        await Expect(p.Locator(".tabstrip .tab.active")).ToContainTextAsync("Drive Vehicle");
        await Expect(p.Locator(".crumbs .dtype")).ToHaveTextAsync("ACT");
        await Expect(p.Node("Drive Vehicle")).ToHaveCountAsync(1);
        await Expect(p.Node("Driver")).ToHaveCountAsync(1);
        await Expect(p.Node("System")).ToHaveCountAsync(1);
        await Expect(p.StatusMessage()).ToContainTextAsync("⌘Z to undo");
        var afterActivity = await p.CountsAsync();
        Assert.Equal(before.Diagrams + 1, afterActivity.Diagrams);

        // Right-click the empty canvas of the new activity diagram → Create sequence from activity.
        var box = (await p.Canvas().BoundingBoxAsync())!;
        await p.Mouse.ClickAsync(box.X + box.Width - 250, box.Y + 150, new() { Button = MouseButton.Right });
        await MenuItem(p, "Create sequence from activity").ClickAsync();
        dlg = p.MudDialog();
        await Expect(dlg.Locator(".dv-row", new() { HasTextString = "Lifeline" }).First).ToBeVisibleAsync();
        await dlg.Locator("button.dv-create").ClickAsync();
        await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);

        // The sequence opens with a lifeline per swimlane and the actor's message to the system.
        await Expect(p.Locator(".tabstrip .tab.active")).ToContainTextAsync("Drive Vehicle sequence");
        await Expect(p.Locator(".crumbs .dtype")).ToHaveTextAsync("SD");
        await Expect(p.Node("driver")).ToHaveCountAsync(1);
        await Expect(p.Node("system")).ToHaveCountAsync(1);
        await Expect(p.Locator(".diagram-host svg.main text", new() { HasTextString = "driveVehicle" })).ToHaveCountAsync(1);

        // The inspector shows the chain for the selected interaction's lifeline's diagram owner.
        await p.Tree().GetByText("Drive Vehicle sequence", new() { Exact = true }).First.ClickAsync();
        await Expect(p.Inspector().Locator("[data-testid=derive-chain]")).ToContainTextAsync("Activity");

        // One undo removes the sequence again.
        await p.Canvas().FocusAsync();
        await p.Keyboard.PressAsync("ControlOrMeta+z");
        await Ui.EventuallyAsync(async () => (await p.CountsAsync()).Diagrams == afterActivity.Diagrams, "undo removes the derived sequence");
        s.AssertNoErrors();
    }
}
