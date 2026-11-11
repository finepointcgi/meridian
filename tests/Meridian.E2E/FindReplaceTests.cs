using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

public sealed class FindReplaceTests(ServerFixture f) : E2ETest(f)
{
    [E2EFact]
    public async Task Replace_Engine_With_Motor_In_Names_And_Documentation_Then_Undo()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Find " + Guid.NewGuid().ToString("N")[..6]);
        await Expect(p.Tree().GetByText("Engine", new() { Exact = true }).First).ToBeVisibleAsync();

        // ⌘⇧F / Ctrl+Shift+F opens the panel with the cursor in the find box.
        await p.Locator(".diagram-host .canvas").First.ClickAsync(new() { Position = new() { X = 5, Y = 5 } });
        await p.Keyboard.PressAsync("ControlOrMeta+Shift+KeyF");
        var panel = p.DockBody().Locator("[data-testid=find-replace]");
        await Expect(panel).ToBeVisibleAsync();
        await Expect(panel.Locator("[data-testid=fr-find]")).ToBeFocusedAsync();

        // Names and documentation only, matching case.
        foreach (var off in new[] { "Requirements", "Values", "TaggedValues", "DiagramText" })
            await panel.Locator($"[data-testid=fr-in-{off}]").UncheckAsync();
        await panel.Locator("[data-testid=fr-case]").ClickAsync();
        await panel.Locator("[data-testid=fr-replace]").FillAsync("Motor");
        await panel.Locator("[data-testid=fr-find]").FillAsync("Engine");
        var status = panel.Locator("[data-testid=fr-status]");
        await Expect(status).ToContainTextAsync(new Regex(@"\d+ match"));
        await Expect(status).Not.ToContainTextAsync("searching");
        await Expect(panel.Locator("[data-testid=fr-group]").Filter(new() { Has = p.Locator(".fr-name", new() { HasTextString = "Start Engine" }) }).First).ToBeVisibleAsync();
        await Expect(panel.Locator(".fr-row mark").First).ToHaveTextAsync("Engine");

        // A result selects its element.
        await panel.Locator("[data-testid=fr-group]").Filter(new() { Has = p.Locator(".fr-name", new() { HasTextRegex = new Regex("^Engine$") }) }).First.Locator(".fr-row").First.ClickAsync();
        await Expect(p.Inspector().GetByLabel("Name", new() { Exact = true })).ToHaveValueAsync("Engine");

        // Preview: a before/after diff; nothing changes yet.
        await panel.Locator("[data-testid=fr-replace-all]").ClickAsync();
        var preview = panel.Locator("[data-testid=fr-preview]");
        await Expect(preview).ToBeVisibleAsync();
        await Expect(preview.Locator("td.before del").First).ToHaveTextAsync("Engine");
        await Expect(preview.Locator("td.after ins").First).ToHaveTextAsync("Motor");
        await Expect(p.Tree().GetByText("Engine", new() { Exact = true }).First).ToBeVisibleAsync();

        // Apply: one undo step.
        await preview.Locator("[data-testid=fr-apply]").ClickAsync();
        await Expect(panel.Locator("[data-testid=fr-message]")).ToContainTextAsync("Replaced in");
        await Expect(p.Tree().GetByText("Motor", new() { Exact = true }).First).ToBeVisibleAsync();
        await Expect(p.Tree().GetByText("Engine", new() { Exact = true })).ToHaveCountAsync(0);
        await Expect(status).ToContainTextAsync("0 matches");

        // ⌘Z takes the whole replace back.
        await p.Keyboard.PressAsync("ControlOrMeta+z");
        await Expect(p.Tree().GetByText("Engine", new() { Exact = true }).First).ToBeVisibleAsync();
        await Expect(p.Tree().GetByText("Motor", new() { Exact = true })).ToHaveCountAsync(0);
        await Expect(status).ToContainTextAsync(new Regex(@"[1-9]\d* match"));
    }
}
