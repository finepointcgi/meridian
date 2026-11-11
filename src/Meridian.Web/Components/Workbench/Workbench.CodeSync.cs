using Meridian.Data;
using Meridian.Web.Services;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>
/// Code sync review: the "Code changes (N)" badge, the "What changed" view tab, the diagram highlight toggle (ribbon,
/// canvas legend and ⌘K) and deep links that open a review branch with the changes panel (docs/CODE-SYNC.md).
/// </summary>
public partial class Workbench
{
    /// <summary>Opens (or shows) the "What changed" tab.</summary>
    public void OpenCodeChanges() => Ws.OpenView(ViewTabKind.CodeChanges);

    public void ToggleCodeHighlights()
    {
        Ws.SetHighlightCodeChanges(!Ws.HighlightCodeChanges);
        Ws.SetStatus(Ws.HighlightCodeChanges ? "Highlighting code changes on diagrams" : "Code change highlights hidden");
    }

    private IEnumerable<CommandPalette.Command> CodeSyncPaletteCommands() =>
    [
        new("Code changes (pushes from code to review)", "Command", Icons.Material.Outlined.Difference, () => { OpenCodeChanges(); return Task.CompletedTask; }),
        new("Highlight code changes", "Command", Icons.Material.Outlined.Highlight, () => { ToggleCodeHighlights(); return Task.CompletedTask; }),
    ];

    /// <summary>
    /// <c>?project=…&amp;branch=…&amp;panel=code-changes</c> (the review link a commit status points at): switch to the branch
    /// and open the panel.
    /// </summary>
    private async Task OpenDeepLinkView()
    {
        if (Ws.Session is not { } s) return;
        var (branch, panel) = Integrations.DeepLink.ParseView(Nav.Uri);
        if (branch is { } b && b != Ws.BranchId && await Db.GetBranchAsync(b) is { } info && info.ProjectId == s.ProjectId)
            await SwitchBranch(new BranchRef(info.Id, info.Name));
        if (panel == Integrations.DeepLink.CodeChangesPanel) OpenCodeChanges();
        else if (panel == Meridian.Web.CodeSync.Conformance.ArchitectureCheckService.Panel) OpenArchitecture();
    }
}
