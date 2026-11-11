using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>Deployment diagram: a node and an artifact from the shapes panel, a line between them becomes «deploy», and the SDD has a Deployment view.</summary>
public sealed class DeploymentTests(ServerFixture f) : E2ETest(f)
{
    private static ILocator QuickArrows(IPage p) => p.Locator(".diagram-host svg.main g").Filter(new() { Has = p.Locator("title", new() { HasTextString = "drag to connect" }) });

    private static async Task RenameSelectedByTypingAsync(IPage p, string name)
    {
        await p.Keyboard.TypeAsync(name, new() { Delay = 20 });
        await p.Keyboard.PressAsync("Enter");
        await p.NoInlineEditorAsync();
    }

    [E2EFact]
    public async Task A_line_from_an_artifact_to_a_node_becomes_deploy_and_the_SDD_shows_the_deployment_view()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Blank project", "Deployment " + Guid.NewGuid().ToString("N")[..6]);

        // ⌘K ▸ New Deployment Diagram
        await p.Locator(".cmdk").ClickAsync();
        await p.Locator(".cmdp-input input").FillAsync("New Deployment Diagram");
        await p.Keyboard.PressAsync("Enter");
        await Expect(p.Locator(".crumbs .dtype")).ToHaveTextAsync("DEPLOY");
        await Expect(p.Nodes()).ToHaveCountAsync(0);
        var before = await p.CountsAsync();

        // A node (drawn as a 3D box) and an artifact from the shapes panel.
        await p.ShapeTileAsync("Node");
        await Expect(p.Nodes()).ToHaveCountAsync(1);
        await RenameSelectedByTypingAsync(p, "AppServer");
        await Expect(p.Node("AppServer").Locator("path.node-3d")).ToHaveCountAsync(1);
        await p.ShapeTileAsync("Artifact");
        await Expect(p.Nodes()).ToHaveCountAsync(2);
        await RenameSelectedByTypingAsync(p, "orders.jar");
        await Expect(p.Node("orders.jar").Locator("path.artifact-icon")).ToHaveCountAsync(1);
        await p.Keyboard.PressAsync("Escape");

        // Drag the artifact's right-hand quick-connect arrow onto the node: the line is a «deploy» on its own.
        var (ax, ay, artifactBox) = await p.Node("orders.jar").CenterAsync();
        var (nx, ny, _) = await p.Node("AppServer").CenterAsync();
        await p.Mouse.MoveAsync(ax, ay);
        await Expect(QuickArrows(p).First).ToBeAttachedAsync();
        var arrows = new List<(float X, float Y)>();
        foreach (var a in await QuickArrows(p).AllAsync()) { var (x, y, _) = await a.CenterAsync(); arrows.Add((x, y)); }
        // The artifact's own arrows (not those of a selected node), and of them the one facing the node.
        var start = arrows.Where(a => Math.Abs(a.X - ax) < artifactBox.Width / 2 + 40 && Math.Abs(a.Y - ay) < artifactBox.Height / 2 + 40)
                          .OrderBy(a => Math.Abs(a.X - nx) + Math.Abs(a.Y - ny)).First();
        await p.Mouse.MoveAsync(start.X, start.Y, new() { Steps = 3 });
        await p.Mouse.DownAsync();
        await p.Mouse.MoveAsync((start.X + nx) / 2, (start.Y + ny) / 2, new() { Steps = 4 });
        await p.Mouse.MoveAsync(nx, ny, new() { Steps = 10 });
        await p.Mouse.UpAsync();
        await Ui.EventuallyAsync(async () => (await p.CountsAsync()).Relationships == before.Relationships + 1, "the line should create one relationship");
        await Expect(p.Locator("[data-testid=connector-chip]")).ToContainTextAsync("«deploy»");
        await Expect(p.Locator(".diagram-host svg.main text", new() { HasTextString = "«deploy»" }).First).ToBeAttachedAsync();

        // Generate the System Design Description as HTML: it has a Deployment view listing the artifact on its node.
        await p.Keyboard.PressAsync("Escape");
        await p.RibbonAsync("Report");
        var dlg = p.MudDialog();
        await Expect(dlg.GetByText("Generate document")).ToBeVisibleAsync();
        await dlg.Locator(".mud-select").First.ClickAsync();
        await p.Locator(".mud-popover-open .mud-list-item", new() { HasTextString = "System Design Description" }).First.ClickAsync();
        await dlg.GetByText("HTML (single file)").ClickAsync();
        var html = await p.RunAndWaitForDownloadAsync(() => dlg.GetByRole(AriaRole.Button, new() { Name = "Generate", Exact = true }).ClickAsync());
        var text = await File.ReadAllTextAsync(await html.PathAsync());
        Assert.Contains("System Design Description", text);
        Assert.Contains("Deployment view", text);
        Assert.Contains("orders.jar", text);
        Assert.Contains("AppServer", text);
        s.AssertNoErrors();
    }
}
