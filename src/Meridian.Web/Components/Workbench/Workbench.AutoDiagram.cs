using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization.Reports;
using Meridian.Web.Services;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>
/// "Diagram it all out" (ribbon Diagram ▸ Auto-diagram, ⌘K, the tree's context menu) and one-click SDD
/// (Generate ▸ Formats ▸ Generate SDD…). The plan comes from <see cref="AutoDiagrammer"/>; no AI involved.
/// </summary>
public partial class Workbench
{
    /// <summary>Most auto-generated diagrams opened as tabs at once (the overview is always one of them).</summary>
    public const int AutoDiagramOpenLimit = 8;

    /// <summary>Shows the plan for <paramref name="scope"/> (null = whole model) and creates the chosen diagrams as one undo step.</summary>
    public async Task AutoDiagram(Element? scope = null)
    {
        if (Ws.ReadOnly) { Ws.SetStatus("View-only: you can't add diagrams to this project"); return; }
        var answer = await AskAutoDiagram(scope, forSdd: false);
        if (answer?.Plan is { } plan) ApplyAutoDiagram(plan, openTabs: true);
    }

    private async Task<AutoDiagramDialog.Answer?> AskAutoDiagram(Element? scope, bool forSdd)
    {
        var parameters = new DialogParameters<AutoDiagramDialog> { { x => x.Scope, scope }, { x => x.ForSdd, forSdd } };
        var dialog = await Dialogs.ShowAsync<AutoDiagramDialog>(forSdd ? "Add missing views" : "Auto-diagram", parameters,
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true, CloseOnEscapeKey = true });
        var result = await dialog.Result;
        return result is { Canceled: false, Data: AutoDiagramDialog.Answer a } ? a : null;
    }

    /// <summary>Applies a plan (one undo step), opens the overview as the active tab plus the rest up to a limit, reports in the status bar.</summary>
    private AutoDiagramResult? ApplyAutoDiagram(AutoDiagramPlan plan, bool openTabs)
    {
        var locked = plan.Diagrams.Where(p => p.Selected && Ws.IsLockedForMe(p.Owner)).ToList();
        foreach (var p in locked) p.Selected = false;
        AutoDiagramResult result;
        try { result = AutoDiagrammer.Apply(Ws.Model, plan); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            var why = $"Auto-diagram could not finish: {FriendlyError.Describe(ex)}. Nothing was changed; try one package at a time.";
            Ws.SetStatus(why);
            Snackbar.Add(why, Severity.Error);
            return null;
        }
        if (openTabs && result.Count > 0)
        {
            var overview = result.Overview;
            var rest = result.All.Where(d => !ReferenceEquals(d, overview)).Take(AutoDiagramOpenLimit - (overview == null ? 0 : 1)).ToList();
            foreach (var d in rest) Ws.Open(d, recordHistory: false);
            Ws.Open(overview ?? rest[0]);
        }
        var summary = result.Summary + (locked.Count > 0 ? $"; skipped {locked.Count} locked by collaborators" : "");
        Ws.Log(summary + (result.Count > 0 ? ": " + string.Join(", ", result.All.Select(d => d.Name)) : ""));
        Ws.SetStatus(summary);
        return result;
    }

    /// <summary>
    /// One-click SDD: offers to add the views the SDD needs (listing them first), then opens the document dialog with the
    /// System Design Description template selected.
    /// </summary>
    private async Task GenerateSdd()
    {
        if (!Ws.ReadOnly && !AutoDiagrammer.Plan(Ws.Model).IsEmpty)
        {
            var answer = await AskAutoDiagram(null, forSdd: true);
            if (answer == null) return;   // cancelled
            if (answer.Plan is { } plan && ApplyAutoDiagram(plan, openTabs: false) == null) return;
        }
        await Dialogs.ShowAsync<ReportDialog>("Generate document",
            new DialogParameters<ReportDialog> { { x => x.Preselect, ReportTemplates.Sdd().Name } },
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, BackdropClick = false });
    }

    private IEnumerable<CommandPalette.Command> AutoDiagramPaletteCommands()
    {
        yield return new("Diagram the whole model", "Command", Icons.Material.Outlined.AutoAwesomeMosaic, () => AutoDiagram());
        if (Ws.Selected is { Kind: ElementKind.Package } p)
            yield return new($"Auto-diagram package {p.DisplayName}", "Command", Icons.Material.Outlined.AutoAwesomeMosaic, () => AutoDiagram(p));
        yield return new("Generate SDD (System Design Description)…", "Command", Icons.Material.Outlined.Description, GenerateSdd);
    }

    private IEnumerable<MenuEntry> AutoDiagramTreeEntries(Element e)
    {
        if (e.Kind is ElementKind.Package or ElementKind.Model && !Ws.ReadOnly)
            yield return MenuEntry.Async(e.Kind == ElementKind.Model ? "Auto-diagram the whole model…" : "Auto-diagram this package…", () => AutoDiagram(e));
    }
}
