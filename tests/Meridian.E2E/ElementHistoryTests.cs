using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>Element history: rename an element over two commits, read its history with diffs, restore the old name.</summary>
public sealed class ElementHistoryTests(ServerFixture f) : E2ETest(f)
{
    private static async Task RenameInTreeAsync(IPage p, string from, string to)
    {
        await p.Tree().GetByText(from, new() { Exact = true }).First.ClickAsync();
        var name = p.Inspector().GetByLabel("Name", new() { Exact = true });
        await Expect(name).ToHaveValueAsync(from);
        await name.FillAsync(to);
        await name.PressAsync("Tab");
        await Expect(p.Tree().GetByText(to, new() { Exact = true })).ToBeVisibleAsync();
    }

    private static async Task CommitAsync(IPage p, string message)
    {
        await p.Locator("header.titlebar .btn-primary").ClickAsync();
        var dlg = p.MudDialog();
        await dlg.Locator("input").FillAsync(message);
        await dlg.GetByRole(AriaRole.Button, new() { Name = "OK" }).ClickAsync();
        await p.SnackbarAsync("Committed version");
    }

    [E2EFact]
    public async Task Rename_commit_twice_read_the_history_and_restore_the_old_name()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "History " + Guid.NewGuid().ToString("N")[..6]);
        await CommitAsync(p, "Start");
        await RenameInTreeAsync(p, "Engine", "Engine Mk2");
        await CommitAsync(p, "First rename");
        await RenameInTreeAsync(p, "Engine Mk2", "Engine Mk3");
        await CommitAsync(p, "Second rename");

        // History… from the element's context menu.
        await p.Tree().GetByText("Engine Mk3", new() { Exact = true }).First.ClickAsync(new() { Button = MouseButton.Right });
        await p.Locator(".ctx-menu .ctx-item", new() { HasTextString = "History…" }).First.ClickAsync();
        var dlg = p.MudDialog();
        var entries = dlg.Locator("[data-testid=hist-entry]");
        await Expect(entries).ToHaveCountAsync(3);   // both renames, and "in the first version"
        await Expect(entries.Nth(0)).ToContainTextAsync("Second rename");
        await Expect(entries.Nth(0)).ToContainTextAsync("Name");
        await Expect(entries.Nth(1)).ToContainTextAsync("First rename");
        await Expect(entries.Nth(2)).ToContainTextAsync("First version");

        // Each change with its diff.
        await entries.Nth(0).ClickAsync();
        var name = dlg.Locator("[data-testid=hist-field][data-key=name]");
        await Expect(name.Locator(".w-removed")).ToContainTextAsync("Mk2");
        await Expect(name.Locator(".w-added")).ToContainTextAsync("Mk3");
        await entries.Nth(1).ClickAsync();
        await Expect(name.Locator(".w-removed")).ToHaveCountAsync(0);   // "Engine" → "Engine Mk2" only adds a word
        await Expect(name.Locator(".w-added")).ToContainTextAsync("Mk2");

        // Restore the name the element had in the first version: one undoable edit.
        await entries.Nth(2).ClickAsync();
        await name.Locator("[data-testid=hist-restore]").ClickAsync();
        await Expect(p.Tree().GetByText("Engine", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(dlg.Locator("[data-testid=hist-uncommitted]")).ToContainTextAsync("Uncommitted (current session)");
        await dlg.GetByRole(AriaRole.Button, new() { Name = "Close" }).Last.ClickAsync();
        await p.Keyboard.PressAsync("ControlOrMeta+z");
        await Expect(p.Tree().GetByText("Engine Mk3", new() { Exact = true })).ToBeVisibleAsync();
    }
}
