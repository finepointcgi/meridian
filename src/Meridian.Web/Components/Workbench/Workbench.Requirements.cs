using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Serialization;
using Microsoft.AspNetCore.Components.Forms;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>Requirements management commands: traceability table, suspect links, renumbering and the Excel round trip.</summary>
public partial class Workbench
{
    /// <summary>Bottom-dock index of the traceability table.</summary>
    private const int TraceabilityDock = 7;

    private Element _sheetTarget = default!;

    private IEnumerable<CommandPalette.Command> RequirementPaletteCommands()
    {
        CommandPalette.Command C(string title, string icon, Action a) => new(title, "Command", icon, () => { a(); return Task.CompletedTask; });
        CommandPalette.Command CA(string title, string icon, Func<Task> a) => new(title, "Command", icon, a);
        return
        [
            C("Traceability table", Icons.Material.Outlined.FactCheck, () => OpenDock(TraceabilityDock)),
            C("Clear all suspect links", Icons.Material.Filled.DoneAll, ClearAllSuspectLinks),
            CA("Renumber requirements…", Icons.Material.Outlined.FormatListNumbered, RenumberRequirements),
            CA("Export requirements to Excel (.xlsx)", Icons.Material.Outlined.GridOn, ExportRequirementsXlsx),
            CA("Import requirements from Excel / CSV (with preview)…", Icons.Material.Outlined.UploadFile, ImportRequirementsSheet),
            CA("Export traceability table to Excel", Icons.Material.Outlined.GridOn,
               () => Download(BaseName + " traceability.xlsx", XlsxMime, TraceabilityExport.ToXlsx(Ws.Model))),
        ];
    }

    private const string XlsxMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public Task ExportRequirementsXlsx() => Download(BaseName + " requirements.xlsx", XlsxMime, RequirementsSheet.ExportXlsx(Ws.Model));

    public Task ImportRequirementsSheet()
    {
        _sheetTarget = Ws.Selected?.AncestorsAndSelf().FirstOrDefault(a => a.Kind is ElementKind.Package or ElementKind.Model)
                       ?? Ws.Model.Root.OwnedElements.FirstOrDefault(p => p.Kind == ElementKind.Package && p.Name.Contains("Requirement", StringComparison.OrdinalIgnoreCase))
                       ?? Ws.Model.Root;
        return PickFile("file-req-sheet");
    }

    private async Task OnRequirementsSheetFile(InputFileChangeEventArgs e)
    {
        var file = e.File;
        try
        {
            List<List<string>> rows;
            await using (var s = file.OpenReadStream(MaxUpload))
            {
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                ms.Position = 0;
                rows = RequirementsSheet.ReadRows(file.Name, ms);
            }
            var diff = RequirementsSheet.Diff(Ws.Model, rows);
            var target = _sheetTarget is { } t && Ws.Model.Contains(t) ? t : Ws.Model.Root;
            var parameters = new DialogParameters<SheetImportDialog>
            {
                { x => x.Diff, diff }, { x => x.FileName, file.Name }, { x => x.TargetName, target.QualifiedName },
            };
            var dialog = await Dialogs.ShowAsync<SheetImportDialog>("Import requirements", parameters,
                new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true, CloseButton = true });
            var result = await dialog.Result;
            if (result is null || result.Canceled) return;
            var (added, changed, removed) = RequirementsSheet.Apply(Ws.Model, target, diff.Rows);
            Ws.Log($"Requirements import from {file.Name}: {added} added, {changed} changed, {removed} deleted.");
            Snackbar.Add($"{added} added, {changed} changed, {removed} deleted — undo with ⌘Z", Severity.Success);
            OpenDock(1);
            Ws.Bump();
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException or ArgumentException
                                       or System.IO.InvalidDataException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException)
        {
            Snackbar.Add($"Import failed: {ex.Message}", Severity.Error);
        }
    }

    public async Task RenumberRequirements()
    {
        var prefix = await Prompt("Renumber requirements", "Id prefix (every requirement is renumbered in document order)", RequirementIds.Prefix(Ws.Model));
        if (prefix is null) return;
        try
        {
            int n;
            using (Ws.Model.History.Transaction("Renumber requirements"))
            {
                RequirementIds.SetPrefix(Ws.Model, prefix);
                n = RequirementIds.Renumber(Ws.Model);
            }
            Ws.SetStatus($"Renumbered {n} requirements");
        }
        catch (InvalidOperationException ex) { Snackbar.Add(ex.Message, Severity.Warning); }
    }

    private void ClearAllSuspectLinks()
    {
        try { Ws.SetStatus($"Cleared {SuspectLinks.ClearAll(Ws.Model)} suspect links"); }
        catch (InvalidOperationException ex) { Snackbar.Add(ex.Message, Severity.Warning); }
    }
}
