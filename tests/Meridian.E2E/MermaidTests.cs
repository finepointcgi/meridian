using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

public sealed class MermaidTests(ServerFixture f) : E2ETest(f)
{
    private const string Flowchart = """
        ---
        title: Pasted order flow
        ---
        flowchart TD
            start((Start)) --> receive[Receive order]
            receive --> check{In stock?}
            check -->|yes| ship[Ship parcel]
            check -->|no| wait[Back-order]
            ship --> done(((End)))
            wait --> done
        """;

    [E2EFact]
    public async Task Pasting_a_flowchart_creates_an_activity_diagram_tab_with_its_nodes()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Mermaid " + Guid.NewGuid().ToString("N")[..6]);
        var before = await p.CountsAsync();

        // ⌘K ▸ Paste Mermaid…
        await p.Locator(".cmdk").ClickAsync();
        await p.Locator(".cmdp-input input").FillAsync("Paste Mermaid");
        await p.Keyboard.PressAsync("Enter");

        var dlg = p.MudDialog();
        var input = dlg.Locator("textarea.mmd-input");
        await Expect(input).ToBeVisibleAsync();
        await input.FillAsync(Flowchart);
        await Expect(dlg.Locator(".mmd-row")).ToHaveCountAsync(1);
        await Expect(dlg.Locator(".mmd-row .mmd-counts")).ToHaveTextAsync("6 nodes · 6 flows");
        await dlg.Locator("button.mmd-import").ClickAsync();

        await Expect(p.Locator(".tabstrip .tab.active")).ToContainTextAsync("Pasted order flow");
        await Expect(p.Locator(".diagram-host svg.main")).ToBeVisibleAsync();
        await Expect(p.Nodes()).ToHaveCountAsync(6);
        await Expect(p.Node("Receive order")).ToBeVisibleAsync();
        await Expect(p.StatusMessage()).ToContainTextAsync("Mermaid");
        var after = await p.CountsAsync();
        Assert.Equal(before.Diagrams + 1, after.Diagrams);
        Assert.Equal(before.Relationships + 6, after.Relationships);

        // The whole import is one undo step.
        await p.Canvas().FocusAsync();
        await p.Keyboard.PressAsync("ControlOrMeta+z");
        await Ui.EventuallyAsync(async () => (await p.CountsAsync()).Diagrams == before.Diagrams, "undo removes the imported diagram");
        s.AssertNoErrors();
    }
}
