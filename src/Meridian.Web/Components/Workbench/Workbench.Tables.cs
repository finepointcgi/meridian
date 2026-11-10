using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>Shell wiring for the generic tables / model query dock panel (dock tab 8).</summary>
public partial class Workbench
{
    private const int TablesDock = 8;
    private TablesPanel.Request _tablesRequest = new(TablesPanel.Mode.Table);

    /// <summary>Opens the Tables dock in the given mode, optionally loading a saved table, query or rule.</summary>
    public void OpenTables(TablesPanel.Mode mode = TablesPanel.Mode.Table, Element? view = null)
    {
        _tablesRequest = new TablesPanel.Request(mode, view);
        OpenDock(TablesDock);
        StateHasChanged();
    }

    /// <summary>Opens a saved view element (TableView, QueryView or ValidationRule) in the Tables dock.</summary>
    public void OpenSavedView(Element view) =>
        OpenTables(view.Kind == ElementKind.TableView ? TablesPanel.Mode.Table : TablesPanel.Mode.Query, view);

    private void ToggleTables(TablesPanel.Mode mode)
    {
        if (_dockOpen && _dock == TablesDock && _tablesRequest.Mode == mode && _tablesRequest.View == null) _dockOpen = false;
        else OpenTables(mode);
    }

    private IEnumerable<CommandPalette.Command> TablesPaletteCommands()
    {
        yield return new("Generic table", "Command", Icons.Material.Outlined.TableChart, () => { OpenTables(TablesPanel.Mode.Table); return Task.CompletedTask; });
        yield return new("Model query / search builder", "Command", Icons.Material.Outlined.ManageSearch, () => { OpenTables(TablesPanel.Mode.Query); return Task.CompletedTask; });
        foreach (var v in SavedViews.All(Ws.Model).ToList())
        {
            var view = v;
            yield return new($"Open {Metamodel.DisplayName(view.Kind).ToLowerInvariant()}: {view.DisplayName}", "View", UiKinds.Icon(view.Kind),
                () => { OpenSavedView(view); return Task.CompletedTask; });
        }
    }
}
