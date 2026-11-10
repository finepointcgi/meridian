using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>Selecting several shapes and connectors on a diagram and editing or linking them together.</summary>
public sealed class MultiSelectTests(ServerFixture f) : E2ETest(f)
{
    private static ILocator Halos(IPage p) => p.Locator(".diagram-host svg.main polyline.edge-sel");
    private static ILocator Red(IPage p) => p.Locator(".diagram-host svg.main [stroke='#ff6b6b']");

    private static async Task ShiftClickAsync(IPage p, float x, float y)
    {
        await p.Keyboard.DownAsync("Shift");
        await p.Mouse.ClickAsync(x, y);
        await p.Keyboard.UpAsync("Shift");
    }

    [E2EFact]
    public async Task Shapes_and_connectors_are_multi_selected_recoloured_in_one_undo_and_bulk_linked_to_an_actor()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Multi " + Guid.NewGuid().ToString("N")[..6]);
        await p.Tree().GetByText("Driver Use Cases", new() { Exact = true }).First.DblClickAsync();
        await Expect(p.Node("Refuel")).ToHaveCountAsync(1);

        // Click one use case, Shift-click another and the «include» connector between "Drive Vehicle" and "Start Engine".
        var (dx, dy, _) = await p.Node("Drive Vehicle").CenterAsync();
        var (rx, ry, _) = await p.Node("Refuel").CenterAsync();
        var (sx, sy, _) = await p.Node("Start Engine").CenterAsync();
        var (ix, iy, _) = await p.Locator(".diagram-host svg.main text", new() { HasTextString = "include" }).First.CenterAsync();
        await p.Mouse.ClickAsync(dx, dy);
        await ShiftClickAsync(p, rx, ry);
        await ShiftClickAsync(p, ix, iy + 7);   // the line just under its «include» label
        await Expect(Halos(p)).ToHaveCountAsync(1);
        var inspector = p.Inspector();
        await Expect(inspector.Locator("[data-testid=bulk-count]")).ToHaveTextAsync("3 selected");
        await Expect(p.Locator("[data-testid=selection-count]")).ToHaveTextAsync("3 selected");

        // Line colour for all three in one go, then one undo puts every one back.
        await Expect(Red(p)).ToHaveCountAsync(0);
        await inspector.Locator("[data-testid=bulk-stroke] + .swatches .swatch[data-color='#ff6b6b']").ClickAsync();
        await Ui.EventuallyAsync(async () => await Red(p).CountAsync() >= 3, "both use cases and the connector should be drawn red");
        Assert.True(await p.Locator(".diagram-host svg.main polyline[stroke='#ff6b6b']").CountAsync() >= 1, "the connector (and its arrowhead) should be red");
        await p.Canvas().FocusAsync();
        await p.Keyboard.PressAsync("ControlOrMeta+z");
        await Expect(Red(p)).ToHaveCountAsync(0);

        // Esc clears; ⌘A selects every shape and connector.
        await p.Keyboard.PressAsync("Escape");
        await Expect(Halos(p)).ToHaveCountAsync(0);
        await p.Keyboard.PressAsync("ControlOrMeta+a");
        await Ui.EventuallyAsync(async () => await Halos(p).CountAsync() >= 3, "⌘A should select the connectors too");
        await p.Keyboard.PressAsync("Escape");

        // A new actor, then link all three use cases to it from the context menu: three associations, one undo step.
        await p.ShapeTileAsync("Actor");
        await p.Keyboard.TypeAsync("Mechanic", new() { Delay = 20 });
        await p.Keyboard.PressAsync("Enter");
        await p.NoInlineEditorAsync();
        await p.Keyboard.PressAsync("Escape");
        var before = await p.CountsAsync();

        (dx, dy, _) = await p.Node("Drive Vehicle").CenterAsync();
        (rx, ry, _) = await p.Node("Refuel").CenterAsync();
        (sx, sy, _) = await p.Node("Start Engine").CenterAsync();
        await p.Mouse.ClickAsync(dx, dy);
        await ShiftClickAsync(p, rx, ry);
        await ShiftClickAsync(p, sx, sy);
        await Expect(inspector.Locator("[data-testid=bulk-count]")).ToHaveTextAsync("3 selected");
        await p.Mouse.ClickAsync(sx, sy, new() { Button = MouseButton.Right });
        await p.Locator(".ctx-menu .ctx-item", new() { HasTextString = "Link to…" }).ClickAsync();

        var dlg = p.MudDialog();
        await dlg.Locator("[data-testid=bulk-link-search]").FillAsync("Mechanic");
        await dlg.Locator("[data-testid=bulk-link-target]", new() { HasTextString = "Mechanic" }).First.ClickAsync();
        await Expect(dlg.Locator("[data-testid=bulk-link-kinds] .chip.on")).ToContainTextAsync("Association");
        await dlg.Locator("[data-testid=bulk-link-ok]").ClickAsync();
        await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);

        await Ui.EventuallyAsync(async () => (await p.CountsAsync()).Relationships == before.Relationships + 3, "one association per selected use case");
        await Expect(p.StatusMessage()).ToContainTextAsync("Linked 3 elements");

        await p.Canvas().FocusAsync();
        await p.Keyboard.PressAsync("ControlOrMeta+z");
        await Ui.EventuallyAsync(async () => (await p.CountsAsync()).Relationships == before.Relationships, "one undo removes all three links");
    }
}
