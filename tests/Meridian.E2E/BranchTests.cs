using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

public sealed class BranchTests(ServerFixture f) : E2ETest(f)
{
    private static ILocator BranchName(IPage p) => p.Locator(".vc-branch .name");

    private static async Task BranchMenuAsync(IPage p, string item)
    {
        await p.Locator(".vc-branch").ClickAsync();
        await p.Locator(".mud-popover-open .mud-menu-item").Filter(new() { HasTextString = item }).First.ClickAsync();
    }

    private static async Task RenameInTreeAsync(IPage p, string from, string to)
    {
        await p.Tree().GetByText(from, new() { Exact = true }).First.ClickAsync();
        var name = p.Inspector().GetByLabel("Name", new() { Exact = true });
        await Expect(name).ToHaveValueAsync(from);
        await name.FillAsync(to);
        await name.PressAsync("Tab");
        await Expect(p.Tree().GetByText(to, new() { Exact = true })).ToBeVisibleAsync();
    }

    [E2EFact]
    public async Task Branch_edit_switch_and_merge_with_a_resolved_conflict()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Branching " + Guid.NewGuid().ToString("N")[..6]);
        await Expect(BranchName(p)).ToHaveTextAsync("main");

        // New branch: the workbench switches to it.
        await BranchMenuAsync(p, "New branch from main");
        var dlg = p.MudDialog();
        await dlg.GetByLabel("Branch name").FillAsync("lighter-engine");
        await dlg.GetByRole(AriaRole.Button, new() { Name = "Create branch" }).ClickAsync();
        await Expect(BranchName(p)).ToHaveTextAsync("lighter-engine");

        // Edit on the branch: one conflicting rename and one clean change.
        await RenameInTreeAsync(p, "Engine", "Engine Lite");
        await RenameInTreeAsync(p, "Driver", "Test Driver");

        // Back on main the branch's edits are not there; make a conflicting rename.
        await BranchMenuAsync(p, "main");
        await Expect(BranchName(p)).ToHaveTextAsync("main");
        await Expect(p.Tree().GetByText("Engine Lite", new() { Exact = true })).ToHaveCountAsync(0);
        await Expect(p.Tree().GetByText("Driver", new() { Exact = true })).ToBeVisibleAsync();
        await RenameInTreeAsync(p, "Engine", "Engine V8");

        // Merge the branch into main: one conflict on Engine's name; take the branch's value.
        await BranchMenuAsync(p, "lighter-engine");
        await Expect(BranchName(p)).ToHaveTextAsync("lighter-engine");
        await Expect(p.Tree().GetByText("Engine Lite", new() { Exact = true })).ToBeVisibleAsync();
        await BranchMenuAsync(p, "Merge lighter-engine into main");
        dlg = p.MudDialog();
        await Expect(dlg.Locator(".vc-stat.warn")).ToContainTextAsync("1 conflict");
        var conflict = dlg.Locator(".vc-conflicts .vc-crow:not(.vc-head)");
        await Expect(conflict).ToHaveCountAsync(1);
        await Expect(conflict.Locator(".vc-val.base")).ToContainTextAsync("Engine");
        await Expect(conflict.Locator(".vc-val.pick").Nth(0)).ToContainTextAsync("Engine V8");
        await Expect(conflict.Locator(".vc-val.pick").Nth(1)).ToContainTextAsync("Engine Lite");
        await conflict.Locator(".vc-val.pick").Nth(1).ClickAsync();
        await Expect(conflict.Locator(".vc-val.pick").Nth(1)).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bon\b"));
        await Expect(dlg.Locator(".vc-stat.warn")).ToContainTextAsync("1 taken from lighter-engine");
        await dlg.GetByRole(AriaRole.Button, new() { Name = "Merge into main" }).ClickAsync();
        await p.SnackbarAsync("Merged “lighter-engine” into “main”");

        // Verify main: the resolved name and the branch's clean change both arrived.
        await BranchMenuAsync(p, "main");
        await Expect(BranchName(p)).ToHaveTextAsync("main");
        await Expect(p.Tree().GetByText("Engine Lite", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(p.Tree().GetByText("Engine V8", new() { Exact = true })).ToHaveCountAsync(0);
        await Expect(p.Tree().GetByText("Test Driver", new() { Exact = true })).ToBeVisibleAsync();
    }
}
