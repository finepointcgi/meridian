using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

public sealed class DrawingTests(ServerFixture f) : E2ETest(f)
{
    private static string Unique(string name) => $"{name} {Guid.NewGuid().ToString("N")[..6]}";

    /// <summary>The quick-connect arrows drawn around a hovered shape.</summary>
    private static ILocator QuickArrows(IPage p) => p.Locator(".diagram-host svg.main g").Filter(new() { Has = p.Locator("title", new() { HasTextString = "drag to connect" }) });

    private static async Task RenameSelectedByTypingAsync(IPage p, string name)
    {
        await p.Keyboard.TypeAsync(name, new() { Delay = 20 });
        await p.Keyboard.PressAsync("Enter");
        await p.NoInlineEditorAsync();
    }

    [E2EFact]
    public async Task Shapes_can_be_added_connected_renamed_moved_restyled_undone_and_deleted()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Blank project", Unique("Drawing"));
        await Expect(p.Locator(".tabstrip .tab.active")).ToBeVisibleAsync();
        var before = await p.CountsAsync();
        var nodesBefore = await p.Nodes().CountAsync();

        // Add two blocks from the shapes panel.
        await p.ShapeTileAsync("Block");
        await Expect(p.Nodes()).ToHaveCountAsync(nodesBefore + 1);
        await RenameSelectedByTypingAsync(p, "Engine");
        await Expect(p.Node("Engine")).ToHaveCountAsync(1);

        await p.ShapeTileAsync("Block");
        await Expect(p.Nodes()).ToHaveCountAsync(nodesBefore + 2);
        await RenameSelectedByTypingAsync(p, "Gearbox");
        await Expect(p.Node("Gearbox")).ToHaveCountAsync(1);
        Assert.Equal(before.Elements + 2, (await p.CountsAsync()).Elements);
        await Expect(p.Tree().GetByText("Engine", new() { Exact = true })).ToBeVisibleAsync();

        // Move Gearbox away from Engine so the two do not overlap, then connect them with a quick-connect arrow drag.
        var engine = p.Node("Engine");
        var gearbox = p.Node("Gearbox");
        var (ex, ey, eb) = await engine.CenterAsync();
        var (gx0, gy0, _) = await gearbox.CenterAsync();
        await p.Mouse.MoveAsync(gx0, gy0);
        await p.Mouse.DownAsync();
        await p.Mouse.MoveAsync(gx0 + 60, gy0 + 40, new() { Steps = 4 });
        await p.Mouse.MoveAsync(ex - 300, ey + 220, new() { Steps = 8 });
        await p.Mouse.UpAsync();
        var (gx, gy, _) = await gearbox.CenterAsync();
        Assert.True(Math.Abs(gx - (ex - 300)) < 30, $"Gearbox should follow the drag: {gx} vs {ex - 300}");

        // Undo puts it back where it was; redo moves it again.
        await p.Canvas().FocusAsync();
        await p.Keyboard.PressAsync("ControlOrMeta+z");
        await Ui.EventuallyAsync(async () => Math.Abs((await gearbox.CenterAsync()).X - gx0) < 3, "undo should move Gearbox back");
        await p.Keyboard.PressAsync("ControlOrMeta+Shift+z");
        await Ui.EventuallyAsync(async () => Math.Abs((await gearbox.CenterAsync()).X - gx) < 3, "redo should move Gearbox again");

        // Hover Engine, then drag its right-hand arrow onto Gearbox.
        await p.Mouse.MoveAsync(ex, ey);
        await Expect(QuickArrows(p).First).ToBeAttachedAsync();
        var arrows = new List<(float X, float Y)>();
        foreach (var a in await QuickArrows(p).AllAsync()) { var (ax, ay, _) = await a.CenterAsync(); arrows.Add((ax, ay)); }
        var right = arrows.OrderByDescending(a => a.X).First();
        await p.Mouse.MoveAsync(right.X, right.Y, new() { Steps = 3 });
        await p.Mouse.DownAsync();
        await p.Mouse.MoveAsync(right.X + 40, right.Y, new() { Steps = 4 });
        await p.Mouse.MoveAsync(gx, gy, new() { Steps = 10 });
        await p.Mouse.UpAsync();
        await Ui.EventuallyAsync(async () => (await p.CountsAsync()).Relationships > before.Relationships, "dragging an arrow onto Gearbox should connect it");
        Assert.Equal(nodesBefore + 2, await p.Nodes().CountAsync());   // a connection, not a new shape

        // Format bar: give Engine a fill colour.
        await p.Keyboard.PressAsync("Escape");
        await engine.ClickAsync(new() { Position = new() { X = 20, Y = 12 } });
        var fill = p.Locator(".selbar .fmt-wrap").Filter(new() { Has = p.Locator("button.color-btn") }).First;
        await fill.Locator("button.color-btn").ClickAsync();
        var swatch = p.Locator(".selbar .fmt-pop .swatch-grid .sw").Nth(3);
        var color = await swatch.GetAttributeAsync("title") ?? throw new InvalidOperationException("swatch without colour");
        await swatch.ClickAsync();
        await Expect(fill.Locator(".cbar")).ToHaveAttributeAsync("style", new System.Text.RegularExpressions.Regex("background:" + color, System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        await Expect(engine.Locator($"[fill='{color}' i]").First).ToBeAttachedAsync();

        // Delete Gearbox from the model: the shape, its element and the connector all go.
        await p.Keyboard.PressAsync("Escape");
        await gearbox.ClickAsync(new() { Position = new() { X = 20, Y = 12 } });
        await p.Keyboard.PressAsync("Delete");
        await Expect(p.Node("Gearbox")).ToHaveCountAsync(0);
        await Expect(p.Tree().GetByText("Gearbox", new() { Exact = true })).ToHaveCountAsync(0);
        var after = await p.CountsAsync();
        Assert.Equal(before.Elements + 1, after.Elements);
        Assert.Equal(before.Relationships, after.Relationships);

        // Undo brings it back.
        await p.Keyboard.PressAsync("ControlOrMeta+z");
        await Expect(p.Node("Gearbox")).ToHaveCountAsync(1);
        Assert.Equal(before.Elements + 2, (await p.CountsAsync()).Elements);
    }

    [E2EFact]
    public async Task Whiteboard_diagram_takes_shapes_and_lines()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Blank project", Unique("Board"));
        var before = await p.CountsAsync();

        await p.RibbonAsync("BOARD");
        await Expect(p.Locator(".crumbs .dtype")).ToHaveTextAsync("BOARD");
        Assert.Equal(before.Diagrams + 1, (await p.CountsAsync()).Diagrams);
        await Expect(p.Nodes()).ToHaveCountAsync(0);

        await p.ShapeTileAsync("Rectangle");
        await Expect(p.Nodes()).ToHaveCountAsync(1);
        await RenameSelectedByTypingAsync(p, "Idea");
        await p.ShapeTileAsync("Ellipse");
        await Expect(p.Nodes()).ToHaveCountAsync(2);
        await RenameSelectedByTypingAsync(p, "Plan");
        await Expect(p.Node("Idea")).ToHaveCountAsync(1);
        await Expect(p.Node("Plan")).ToHaveCountAsync(1);

        // Pick a line tool from the Connectors section and draw from one shape to the other.
        var idea = p.Node("Idea");
        var plan = p.Node("Plan");
        var (ix, iy, _) = await idea.CenterAsync();
        var (px, py, _) = await plan.CenterAsync();
        if (Math.Abs(ix - px) < 120 && Math.Abs(iy - py) < 80)
        {
            await p.Mouse.MoveAsync(px, py);
            await p.Mouse.DownAsync();
            await p.Mouse.MoveAsync(px + 250, py + 150, new() { Steps = 10 });
            await p.Mouse.UpAsync();
            (px, py, _) = await plan.CenterAsync();
        }
        var rels = (await p.CountsAsync()).Relationships;
        await p.Locator(".toolbox .shapes-sec", new() { HasTextString = "Connectors" }).ScrollIntoViewIfNeededAsync();
        var lineTile = p.Locator(".toolbox .shapes-body .shapes-grid").Last.Locator(".tile").First;
        await lineTile.ClickAsync();
        await p.Mouse.MoveAsync(ix, iy);
        await p.Mouse.DownAsync();
        await p.Mouse.MoveAsync(px, py, new() { Steps = 10 });
        await p.Mouse.UpAsync();
        await Ui.EventuallyAsync(async () => (await p.CountsAsync()).Relationships == rels + 1, "the line tool should connect the two shapes");
        Assert.Equal(2, await p.Nodes().CountAsync());
    }

    /// <summary>Regression: keys typed before the inline editor had focus used to be dropped ("Gearbox" became "Geax").</summary>
    [E2EFact]
    public async Task Typing_a_label_quickly_on_a_new_shape_keeps_every_key()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Blank project", Unique("Typing"));
        var names = new[] { "Gearbox", "Shared Pump", "Alternator", "Radiator", "Crankshaft", "Fuel Injector" };
        foreach (var name in names)
        {
            await p.ShapeTileAsync("Block");
            await p.Keyboard.TypeAsync(name);   // no delay: faster than the server round trip
            await p.Keyboard.PressAsync("Enter");
            await Expect(p.Tree().GetByText(name, new() { Exact = true })).ToHaveCountAsync(1);
        }
        await Expect(p.Nodes()).ToHaveCountAsync(names.Length);
    }
}
