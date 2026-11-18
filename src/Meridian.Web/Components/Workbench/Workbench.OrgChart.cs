using Meridian.Core.Model;
using Meridian.Web.Services;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>
/// Import ▸ "Org chart from spreadsheet…" and ⌘K: people from an Excel / CSV sheet drawn as a whiteboard org chart
/// (Core/Model/OrgChart.cs). Importing the same file again updates that chart.
/// </summary>
public partial class Workbench
{
    private IEnumerable<CommandPalette.Command> OrgChartPaletteCommands() =>
    [
        new("Org chart from spreadsheet (Excel / CSV)…", "Command", Icons.Material.Outlined.AccountTree, () => ImportOrgChart()),
    ];

    /// <summary>Asks for a sheet of people, previews it and draws (or updates) the org chart as one undo step.</summary>
    public async Task ImportOrgChart(string? fileName = null, byte[]? bytes = null)
    {
        if (Ws.ReadOnly) { Snackbar.Add("This project is view-only.", Severity.Warning); return; }
        var target = MermaidImport.TargetFor(Ws);
        var choice = await OrgChartDialog.ShowAsync(Dialogs, target, fileName, bytes);
        if (choice == null) return;
        if (!Ws.CanEdit(target))
        {
            Snackbar.Add(Ws.Access.ContentsDeniedMessage(target).Replace("your change was", "nothing was"), Severity.Warning);
            return;
        }
        try
        {
            var r = OrgChart.Build(Ws.Model, target, choice.Sheet.Tree, choice.Name, choice.Source);
            Ws.Log($"Org chart {(r.Replaced ? "updated" : "imported")}{(choice.Source is { } s ? " from " + s : "")}: {r.Added} added, {r.Updated} changed, {r.Removed} removed.");
            foreach (var w in choice.Sheet.Warnings.Take(200)) Ws.Log("  warning: " + w);
            if (choice.Sheet.Warnings.Count > 0) OpenDock(3);
            Ws.Open(r.Diagram);
            Ws.Active?.ZoomToFit();
            Snackbar.Add(r.Replaced
                ? $"Org chart updated: {r.Added} added, {r.Updated} changed, {r.Removed} removed — undo with ⌘Z"
                : $"Org chart of {choice.Sheet.Tree.People.Count} people created — undo with ⌘Z", Severity.Success);
            Ws.Bump();
        }
        catch (InvalidOperationException ex)
        {
            ErrorWithGuide($"Could not draw the org chart: {FriendlyError.Describe(ex)}. Nothing was changed.", Meridian.Web.Hosting.UserGuide.Pages.SpreadsheetImport);
        }
    }
}
