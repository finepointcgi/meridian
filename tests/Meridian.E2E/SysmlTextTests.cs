using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

public sealed class SysmlTextTests(ServerFixture f) : E2ETest(f)
{
    [E2EFact]
    public async Task Text_edits_apply_to_the_model_tree_and_undo_restores_it()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Blank project", "Text " + Guid.NewGuid().ToString("N")[..6]);
        var before = await p.CountsAsync();

        // Open the editor on the Structure package.
        await p.Tree().GetByText("Structure", new() { Exact = true }).First.ClickAsync();
        await p.RibbonAsync("Edit text");
        var panel = p.DockBody().Locator(".sx-panel");
        await Expect(panel.Locator(".sx-scope")).ToContainTextAsync("Structure");
        await Expect(panel.Locator(".sx-state")).ToContainTextAsync("In sync with the model");
        var ta = panel.Locator("textarea.sx-ta");
        var text = await ta.InputValueAsync();
        Assert.Contains("package Structure", text);

        // Add a part definition with an attribute, inside the package.
        var at = text.IndexOf('{', text.IndexOf("package Structure", StringComparison.Ordinal)) + 1;
        var edited = text[..at] + "\n    part def Turbocharger {\n        attribute boost : Real;\n    }\n" + text[at..];
        await ta.FillAsync(edited);
        await Expect(panel.Locator(".sx-state")).ToContainTextAsync("Modified — not applied");
        await Expect(panel.Locator(".sx-chip")).ToHaveCountAsync(0);
        await panel.Locator("button.sx-btn.primary").ClickAsync();
        await Expect(panel.Locator(".sx-state")).ToContainTextAsync("In sync with the model");

        // The tree shows the new block, and the counts went up.
        await Expect(p.Tree().GetByText("Turbocharger", new() { Exact = true })).ToBeVisibleAsync();
        var after = await p.CountsAsync();
        Assert.True(after.Elements >= before.Elements + 2, $"block + attribute expected: {before} -> {after}");

        // Errors are reported and nothing is applied.
        await ta.FillAsync(await ta.InputValueAsync() + "\npart def {");
        await Expect(panel.Locator(".sx-chip").First).ToHaveTextAsync(new Regex(@"\d+ errors?"));
        await Expect(panel.Locator(".sx-problem").First).ToBeVisibleAsync();
        await panel.Locator(".sx-bar button.sx-btn", new() { HasTextString = "Reload" }).ClickAsync();
        await Expect(panel.Locator(".sx-state")).ToContainTextAsync("In sync with the model");

        // Undo (title-bar button) removes the applied text edit as one step; the editor follows the model.
        await p.Locator(".tb-actions button").First.ClickAsync();
        await Expect(p.Tree().GetByText("Turbocharger", new() { Exact = true })).ToHaveCountAsync(0);
        Assert.Equal(before, await p.CountsAsync());
        await Expect(ta).Not.ToHaveValueAsync(new Regex("Turbocharger"));
    }
}
