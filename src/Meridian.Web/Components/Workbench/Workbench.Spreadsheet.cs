using Meridian.Core.Model;
using Meridian.Serialization.Spreadsheets;
using Microsoft.AspNetCore.Components.Forms;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>
/// Spreadsheet (Excel / CSV) import and round-trip sync for any element kind: Import ▸ "Spreadsheet (Excel / CSV)…", the
/// Requirements tab (with the Requirements preset), ⌘K, and files dropped on the model tree (wwwroot/js/sheet-drop.js).
/// </summary>
public partial class Workbench
{
    private IEnumerable<CommandPalette.Command> SpreadsheetPaletteCommands()
    {
        CommandPalette.Command CA(string title, string icon, Func<Task> a) => new(title, "Command", icon, a);
        return
        [
            CA("Import spreadsheet (Excel / CSV)…", Icons.Material.Outlined.GridOn, () => ImportSpreadsheet()),
            CA("Export to spreadsheet with a mapping…", Icons.Material.Outlined.GridOn, () => ImportSpreadsheet()),
            CA("Import parts list (BOM) from Excel / CSV…", Icons.Material.Outlined.AccountTree, () => ImportSpreadsheet(SheetPresets.PartsList().Name)),
            // Cameo / EA names for the same thing.
            CA("Excel/CSV import, CSV import/export specification → Spreadsheet import", Icons.Material.Outlined.GridOn, () => ImportSpreadsheet()),
        ];
    }

    /// <summary>Opens the spreadsheet import dialog, optionally with a preset and an already chosen file.</summary>
    public async Task ImportSpreadsheet(string? preset = null, string? fileName = null, byte[]? bytes = null, Element? scope = null)
    {
        scope ??= Ws.Selected?.AncestorsAndSelf().FirstOrDefault(a => a.Kind is ElementKind.Package or ElementKind.Model) ?? Ws.Model.Root;
        var selection = Ws.Active?.SelectedElements is { Count: > 0 } sel ? sel : Ws.Selected is { } one ? [one] : (IReadOnlyList<Element>)[];
        var parameters = new DialogParameters<SpreadsheetImportDialog>
        {
            { x => x.Preset, preset }, { x => x.FileName, fileName ?? "" }, { x => x.Bytes, bytes }, { x => x.Scope, scope },
            { x => x.Selection, selection },
            { x => x.Download, (Func<string, string, byte[], Task>)((name, mime, data) => Download(name, mime, data)) },
        };
        var dialog = await Dialogs.ShowAsync<SpreadsheetImportDialog>("Spreadsheet import", parameters,
            new DialogOptions { MaxWidth = MaxWidth.ExtraLarge, FullWidth = true, CloseButton = true });
        var result = await dialog.Result;
        if (result is null || result.Canceled || result.Data is not SheetImportPlan plan) return;
        if (Ws.ReadOnly) { Snackbar.Add("This project is view-only.", Severity.Warning); return; }
        try
        {
            var r = SheetImport.Apply(Ws.Model, plan);
            var types = r.TypesCreated > 0 ? $", {r.TypesCreated} types created" : "";
            Ws.Log($"Spreadsheet import{(fileName is { Length: > 0 } ? " from " + fileName : "")}: {r.Added} added, {r.Changed} changed, {r.Removed} deleted{types}, {plan.Errors.Count} rows skipped.");
            foreach (var w in r.Warnings.Take(20)) Ws.Log("  " + w);
            Snackbar.Add($"{r.Added} added, {r.Changed} changed, {r.Removed} deleted{types} — undo with ⌘Z", Severity.Success);
            if (plan.Mapping.Kind == "Requirement") OpenDock(1);
            Ws.Bump();
        }
        catch (InvalidOperationException ex)
        {
            ErrorWithGuide($"Could not apply the import: {Meridian.Web.Services.FriendlyError.Describe(ex)}. Nothing was changed.", Meridian.Web.Hosting.UserGuide.Pages.SpreadsheetImport);
        }
    }

    /// <summary>A spreadsheet dropped on the model tree (the drop handler routes it to the hidden file input).</summary>
    private async Task OnSpreadsheetFile(InputFileChangeEventArgs e)
    {
        if (Ws.ReadOnly) { Snackbar.Add("This project is view-only.", Severity.Warning); return; }
        byte[] bytes;
        try
        {
            await using var s = e.File.OpenReadStream(SheetLimits.MaxFileBytes);
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            bytes = ms.ToArray();
        }
        catch (IOException ex)
        {
            Snackbar.Add($"Could not read {e.File.Name}: {Meridian.Web.Services.FriendlyError.Describe(ex)}", Severity.Warning);
            return;
        }
        await ImportSpreadsheet(null, e.File.Name, bytes);
    }
}
