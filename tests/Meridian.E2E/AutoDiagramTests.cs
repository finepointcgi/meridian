using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

public sealed class AutoDiagramTests(ServerFixture f) : E2ETest(f)
{
    [E2EFact]
    public async Task Auto_diagram_creates_the_planned_views_and_opens_them_as_tabs()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("CubeSat mission", "Auto " + Guid.NewGuid().ToString("N")[..6]);
        var before = await p.CountsAsync();

        await p.RibbonAsync("Auto-diagram");
        var dlg = p.MudDialog();
        await Expect(dlg.Locator(".ad-row").First).ToBeVisibleAsync();
        await Expect(dlg.Locator(".ad-row").First).ToContainTextAsync("Package overview");
        var planned = await dlg.Locator(".ad-row").CountAsync();
        Assert.True(planned >= 2, $"planned {planned}");
        await dlg.Locator("button.ad-create").ClickAsync();

        // The overview is the active tab, the other new diagrams are open too, and the status bar sums it up.
        await Expect(p.Locator(".tabstrip .tab.active")).ToContainTextAsync("Package overview");
        await Expect(p.Tabs().Filter(new() { HasTextString = "EPS – internal structure" })).ToHaveCountAsync(1);
        await Expect(p.StatusMessage()).ToContainTextAsync("Auto-diagram");
        await Expect(p.Locator(".diagram-host svg.main")).ToBeVisibleAsync();
        await Expect(p.Node("Architecture")).ToBeVisibleAsync();
        var after = await p.CountsAsync();
        Assert.Equal(before.Diagrams + planned, after.Diagrams);

        // One undo removes them all.
        await p.Canvas().FocusAsync();
        await p.Keyboard.PressAsync("ControlOrMeta+z");
        await Ui.EventuallyAsync(async () => (await p.CountsAsync()).Diagrams == before.Diagrams, "undo removes every auto diagram");
        s.AssertNoErrors();
    }
}
