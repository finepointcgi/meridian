using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Serialization;
using Meridian.Serialization.Spreadsheets;
using Microsoft.AspNetCore.Components.Forms;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>Requirements management commands: traceability table, suspect links, renumbering and the Excel round trip.</summary>
public partial class Workbench
{
    /// <summary>Bottom-dock index of the traceability table.</summary>
    private const int TraceabilityDock = 7;

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

    /// <summary>Requirements Excel / CSV re-import: the spreadsheet import dialog with the Requirements preset.</summary>
    public Task ImportRequirementsSheet()
    {
        var target = Ws.Selected?.AncestorsAndSelf().FirstOrDefault(a => a.Kind is ElementKind.Package or ElementKind.Model)
                     ?? Ws.Model.Root.OwnedElements.FirstOrDefault(p => p.Kind == ElementKind.Package && p.Name.Contains("Requirement", StringComparison.OrdinalIgnoreCase))
                     ?? Ws.Model.Root;
        return ImportSpreadsheet(SheetPresets.Requirements().Name, scope: target);
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
