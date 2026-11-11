using Meridian.Core.Model;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>Model-wide find and replace (FindReplacePanel): the bottom-dock panel, ⌘⇧F, the ribbon button and the ⌘K command.</summary>
public partial class Workbench
{
    /// <summary>Bottom-dock index of the find and replace panel.</summary>
    public const int FindDock = 23;

    /// <summary>The panel's query, options and results survive switching to another dock tab.</summary>
    private readonly FindReplacePanel.PanelState _find = new();

    /// <summary>Opens the find and replace panel and puts the cursor in the find box (⌘⇧F).</summary>
    public void OpenFindReplace()
    {
        _find.RequestFocus();
        OpenDock(FindDock);
        StateHasChanged();
    }

    /// <summary>Shows the SysML v2 text editor (a find result in the text).</summary>
    public void ShowSysmlText(Element? scope)
    {
        _sysml.RequestScope(scope);
        OpenDock(12);
        StateHasChanged();
    }

    private IEnumerable<CommandPalette.Command> FindReplacePaletteCommands()
    {
        yield return new("Find and replace in the model…", "Command", Icons.Material.Outlined.FindReplace, () => { OpenFindReplace(); return Task.CompletedTask; });
    }
}
