using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Web.Services;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>Shell wiring for the generic tables / model query dock panel (dock tab 8).</summary>
public partial class Workbench
{
    /// <summary>Opens a generic table or model query as a tab in the centre, optionally loading a saved table, query or rule.</summary>
    public void OpenTables(TablesPanel.Mode mode = TablesPanel.Mode.Table, Element? view = null)
    {
        var kind = mode == TablesPanel.Mode.Table ? ViewTabKind.Table : ViewTabKind.Query;
        var tab = Ws.OpenView(kind, view);
        tab.State ??= new TablesPanel.Request(mode, view);
        StateHasChanged();
    }

    /// <summary>Opens a saved view element (TableView, QueryView or ValidationRule) in its own tab.</summary>
    public void OpenSavedView(Element view) =>
        OpenTables(view.Kind == ElementKind.TableView ? TablesPanel.Mode.Table : TablesPanel.Mode.Query, view);

    private bool TablesTabActive(TablesPanel.Mode mode) =>
        Ws.ActiveView is { } v && v.Kind == (mode == TablesPanel.Mode.Table ? ViewTabKind.Table : ViewTabKind.Query);

    private void ToggleTables(TablesPanel.Mode mode) => OpenTables(mode);

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
