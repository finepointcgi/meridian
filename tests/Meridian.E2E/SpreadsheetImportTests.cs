using Meridian.Serialization;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>Spreadsheet import: a parts list (BOM) into the Vehicle sample, then a re-import of an edited sheet with the review.</summary>
public sealed class SpreadsheetImportTests(ServerFixture f) : E2ETest(f)
{
    private static readonly string[] Header = ["Level", "Part Number", "Name", "Description", "Qty", "Mass", "Material", "Supplier"];

    private static byte[] Parts(string wheelQty, string chassisMaterial) => Xlsx.Write(new Xlsx.Sheet("BOM", Header,
    [
        ["1", "V-100", "Vehicle", "", "1", "", "", ""],
        ["2", "C-200", "Chassis", "Ladder frame", "1", "320 kg", chassisMaterial, "Acme"],
        ["3", "W-300", "Wheel", "", wheelQty, "18 kg", "Aluminium", "Rimco"],
        ["2", "E-400", "Engine", "", "1", "", "", ""],
    ]));

    [E2EFact]
    public async Task Parts_list_import_then_reimport_shows_the_review()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "BOM " + Guid.NewGuid().ToString("N")[..6]);
        var before = await p.CountsAsync();

        // Import ▸ Spreadsheet (Excel / CSV)…, choose the file, the Parts list preset, check and apply.
        await p.ProjectMenuAsync("Spreadsheet (Excel / CSV)");
        var dlg = p.Locator(".ss-dialog");
        await Expect(dlg).ToBeVisibleAsync();
        await dlg.Locator("input[type=file][data-testid=ss-file]").SetInputFilesAsync(new FilePayload
        {
            Name = "parts.xlsx", MimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", Buffer = Parts("4", "Steel"),
        });
        await Expect(dlg.Locator("[data-testid=ss-preview]")).ToContainTextAsync("Chassis");   // preview of the first rows
        await dlg.Locator("button.ss-next").ClickAsync();
        await Expect(dlg.Locator("[data-testid=ss-columns]")).ToBeVisibleAsync();
        await dlg.Locator("select.ss-preset").SelectOptionAsync("builtin:Parts list (BOM)");
        await Expect(dlg.Locator("tr.ss-col[data-column='Qty'] select.ss-role")).ToHaveValueAsync("Multiplicity");
        await dlg.Locator("button.ss-check-btn").ClickAsync();
        await Expect(dlg.Locator("[data-testid=ss-summary]")).ToContainTextAsync("2 new");
        await Expect(dlg.Locator("tr.ss-change.added")).ToHaveCountAsync(2);
        await dlg.Locator("button.ss-apply").ClickAsync();
        await p.SnackbarAsync("2 added");
        await Expect(p.Tree().GetByText("Chassis", new() { Exact = true }).First).ToBeVisibleAsync();
        var after = await p.CountsAsync();
        Assert.True(after.Elements > before.Elements, $"{before} → {after}");

        // Re-import an edited sheet by dropping it on the model tree: five wheels, aluminium chassis.
        var edited = Parts("5", "Aluminium");
        var dataTransfer = await p.EvaluateHandleAsync(@"bytes => {
            const dt = new DataTransfer();
            dt.items.add(new File([new Uint8Array(bytes)], 'parts.xlsx', { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' }));
            return dt;
        }", edited.Select(b => (int)b).ToArray());
        var tree = p.Locator("[data-sheet-drop]");
        await tree.DispatchEventAsync("dragover", new Dictionary<string, object> { ["dataTransfer"] = dataTransfer });
        await tree.DispatchEventAsync("drop", new Dictionary<string, object> { ["dataTransfer"] = dataTransfer });
        await Expect(dlg).ToBeVisibleAsync();
        await dlg.Locator("select.ss-preset").SelectOptionAsync("builtin:Parts list (BOM)");
        await dlg.Locator("button.ss-check-btn").ClickAsync();

        var review = dlg.Locator("[data-testid=ss-review]");
        await Expect(dlg.Locator("[data-testid=ss-summary]")).ToContainTextAsync("0 new");
        await Expect(dlg.Locator("tr.ss-change.changed")).ToHaveCountAsync(2);
        var chassis = dlg.Locator("tr.ss-change.changed").Filter(new() { HasTextString = "C-200" });
        await Expect(chassis).ToContainTextAsync("Material");
        await Expect(chassis).ToContainTextAsync("Steel");
        await Expect(chassis).ToContainTextAsync("Aluminium");
        var wheel = dlg.Locator("tr.ss-change.changed").Filter(new() { HasTextString = "W-300" });
        await Expect(wheel).ToContainTextAsync("Part of");
        await Expect(wheel).ToContainTextAsync("Chassis [4]");   // before: four of them in the chassis
        await Expect(wheel).ToContainTextAsync("Chassis [5]");
        // Blocks the sheet does not list are offered for deletion, unticked.
        var removals = dlg.Locator("tr.ss-change.removed");
        if (await removals.CountAsync() > 0) await Expect(removals.First.Locator("input[type=checkbox]")).Not.ToBeCheckedAsync();
        await Expect(review).ToBeVisibleAsync();
        // Back to the mapping and forward again: the checked review is kept while nothing changed.
        await dlg.Locator("button.ss-step[data-step='1']").ClickAsync();
        await Expect(dlg.Locator("[data-testid=ss-columns]")).ToBeVisibleAsync();
        await dlg.Locator("button.ss-step[data-step='2']").ClickAsync();
        await Expect(review).ToBeVisibleAsync();

        await dlg.Locator("button.ss-apply").ClickAsync();
        await p.SnackbarAsync("2 changed");
        Assert.Equal(after.Elements, (await p.CountsAsync()).Elements);   // updated in place, nothing duplicated
    }
}
