using Meridian.Core.Deployment;
using Meridian.Core.Model;
using Meridian.Web.Services;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>Deployment entry points: "Deploy to…" on artifacts, databases and multi-selections, and the deployment table.</summary>
public partial class Workbench
{
    /// <summary>Containment tree: "Deploy to…" on an artifact, deployment specification, database or schema.</summary>
    private IEnumerable<MenuEntry> DeploymentTreeEntries(Element e)
    {
        if (Ws.ReadOnly || !DeploymentModel.CanDeploy(e)) yield break;
        yield return MenuEntry.Async("Deploy to…", () => DeployDialog.ShowAsync(Dialogs, Ws, [e], Ws.Active));
    }

    /// <summary>Canvas: "Deploy to…" on one artifact shape.</summary>
    private IEnumerable<MenuEntry> DeploymentCanvasEntries(DiagramController c, DiagramNode n)
    {
        if (c.ReadOnly || !DeploymentModel.CanDeploy(n.Element)) yield break;
        yield return MenuEntry.Async("Deploy to…", () => DeployDialog.ShowAsync(Dialogs, Ws, [n.Element], c));
    }

    /// <summary>Multi-selection: "Deploy to…" for every selected artifact (the rest of the selection is left alone).</summary>
    private IEnumerable<MenuEntry> DeploymentMultiEntries(DiagramController c)
    {
        if (c.ReadOnly) yield break;
        var deployable = c.LinkableSelection.Where(DeploymentModel.CanDeploy).ToList();
        if (deployable.Count == 0) yield break;
        yield return MenuEntry.Async($"Deploy {deployable.Count} to…", () => DeployDialog.ShowAsync(Dialogs, Ws, deployable, c));
    }

    /// <summary>Opens the deployment table: every artifact with its node and execution environment (generic table preset).</summary>
    public void OpenDeploymentTable()
    {
        Ws.OpenView(ViewTabKind.Table, null, new TablesPanel.Request(TablesPanel.Mode.Table, null, DeploymentModel.DeploymentTable()));
        StateHasChanged();
    }

    private IEnumerable<CommandPalette.Command> DeploymentPaletteCommands()
    {
        yield return new("Deployment table (artifact, node, environment)", "Command", Icons.Material.Outlined.Dns, () => { OpenDeploymentTable(); return Task.CompletedTask; });
        if (Ws.ReadOnly || Ws.Active is not { } c) yield break;
        var deployable = c.LinkableSelection.Where(DeploymentModel.CanDeploy).ToList();
        if (deployable.Count > 0)
            yield return new("Deploy selection to…", "Command", Icons.Material.Outlined.Publish, () => DeployDialog.ShowAsync(Dialogs, Ws, deployable, c));
    }
}
