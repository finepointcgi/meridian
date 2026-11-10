using System.Globalization;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>Combined fragment operands on the Vehicle sample's sequence diagram: make the fragment an alt, add an operand, guard it, drag its separator.</summary>
public sealed class OperandTests(ServerFixture f) : E2ETest(f)
{
    private static ILocator MenuItem(IPage p, string text) => p.Locator(".ctx-menu .ctx-item", new() { HasTextString = text });

    private static async Task<float> SeparatorYAsync(IPage p) =>
        float.Parse((await p.Locator(".diagram-host svg.main line.operand-sep").First.GetAttributeAsync("y1"))!, CultureInfo.InvariantCulture);

    [E2EFact]
    public async Task An_operand_is_added_to_an_alt_fragment_guarded_and_its_separator_dragged()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Operands " + Guid.NewGuid().ToString("N")[..6]);
        await p.Tree().GetByText("Start Engine Sequence", new() { Exact = true }).First.DblClickAsync();
        await Expect(p.Locator(".crumbs .dtype")).ToHaveTextAsync("SD");

        // Select the fragment by its operator tag and make it an alt.
        var fragment = p.Node("opt");
        await Expect(fragment).ToHaveCountAsync(1);
        var box = (await fragment.BoundingBoxAsync())!;
        await p.Mouse.ClickAsync(box.X + 12, box.Y + 9);
        await p.Inspector().Locator(".mud-select").Filter(new() { Has = p.Locator("label", new() { HasTextString = "Operator" }) }).First.ClickAsync();
        await p.Locator(".mud-popover-open .mud-list-item").Filter(new() { HasTextString = "alt" }).First.ClickAsync();
        await Expect(p.Node("alt")).ToHaveCountAsync(1);
        await Expect(p.Locator(".diagram-host svg.main line.operand-sep")).ToHaveCountAsync(0);

        // Right-click the fragment → Add operand: a dashed separator splits it, the new operand is guarded "else".
        box = (await p.Node("alt").BoundingBoxAsync())!;
        await p.Mouse.ClickAsync(box.X + 12, box.Y + 9, new() { Button = MouseButton.Right });
        await MenuItem(p, "Add operand").ClickAsync();
        await Expect(p.Locator(".diagram-host svg.main line.operand-sep")).ToHaveCountAsync(1);
        await Expect(p.StatusMessage()).ToContainTextAsync("Added an operand");
        var guards = p.Inspector().Locator("[data-testid=operands] .op-guard input");
        await Expect(guards).ToHaveCountAsync(2);
        await Expect(guards.Nth(0)).ToHaveValueAsync("engine cold");
        await Expect(guards.Nth(1)).ToHaveValueAsync("else");

        // Guard the new operand.
        await guards.Nth(1).FillAsync("engine warm");
        await guards.Nth(1).PressAsync("Tab");
        await Expect(p.Locator(".diagram-host svg.main text", new() { HasTextString = "[engine warm]" })).ToHaveCountAsync(1);

        // Drag the separator down: it follows, and one undo puts it back.
        var before = await SeparatorYAsync(p);
        var grip = p.Locator("line.operand-grip").First;
        await Expect(grip).ToHaveCountAsync(1);
        var (gx, gy, _) = await grip.CenterAsync();
        await p.Mouse.MoveAsync(gx, gy);
        await p.Mouse.DownAsync();
        await p.Mouse.MoveAsync(gx, gy + 10, new() { Steps = 3 });
        await p.Mouse.MoveAsync(gx, gy + 20, new() { Steps = 3 });
        await p.Mouse.UpAsync();
        await Ui.EventuallyAsync(async () => await SeparatorYAsync(p) > before + 5, "the separator should follow the drag");
        await Expect(p.Node("alt")).ToHaveCountAsync(1);

        await p.Canvas().FocusAsync();
        await p.Keyboard.PressAsync("ControlOrMeta+z");
        await Ui.EventuallyAsync(async () => Math.Abs(await SeparatorYAsync(p) - before) < 0.5, "undo should move the separator back");
        s.AssertNoErrors();
    }
}
