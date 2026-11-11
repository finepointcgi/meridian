using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Web.Services;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>
/// Relation maps (Cameo's Relation Map): a view tab exploring everything connected to an element. Opened from an element's
/// context menu, the inspector, ⌘K "Relation map of …", the Traceability ribbon tab, and saved relation map elements.
/// </summary>
public partial class Workbench
{
    /// <summary>Opens a relation map of <paramref name="root"/> (or brings its open map forward).</summary>
    public void OpenRelationMap(Element root)
    {
        if (root is Relationship || root is Diagram || root.Kind == ElementKind.Model || !Ws.Model.Contains(root)) { Ws.SetStatus("Pick an element (not a relationship or diagram) for a relation map"); return; }
        if (root.Kind == ElementKind.RelationMap) { OpenSavedRelationMap(root); return; }
        var open = Ws.OpenViews.FirstOrDefault(v => v.Kind == ViewTabKind.RelationMap && v.Element == null && v.State is RelationMapSettings s && s.RootId == root.Id);
        if (open != null) { Ws.ActivateView(open); return; }
        Ws.OpenView(ViewTabKind.RelationMap, null, RelationMapSettings.For(root), forceNew: true);
    }

    /// <summary>Opens a saved relation map element in its own tab.</summary>
    public void OpenSavedRelationMap(Element saved) =>
        Ws.OpenView(ViewTabKind.RelationMap, saved, RelationMaps.Load(saved));

    private void RelationMapOfSelection()
    {
        if (Ws.Selected is { } e) OpenRelationMap(e);
        else Ws.SetStatus("Select an element first, then open its relation map");
    }

    private bool RelationMapTabActive => Ws.ActiveView?.Kind == ViewTabKind.RelationMap;

    /// <summary>⌘K: "Relation map of …" for the selection, the requirements and the blocks (bounded for very large models), and saved maps.</summary>
    private IEnumerable<CommandPalette.Command> RelationMapPaletteCommands()
    {
        var icon = Icons.Material.Outlined.Hub;
        if (Ws.Selected is { } sel && sel is not Relationship && sel is not Diagram && sel.Kind != ElementKind.Model)
            yield return new($"Relation map of selection: {sel.DisplayName}", "Command", icon, () => { OpenRelationMap(sel); return Task.CompletedTask; });
        foreach (var saved in RelationMaps.Saved(Ws.Model).ToList())
        {
            var s = saved;
            yield return new($"Open relation map: {s.DisplayName}", "View", icon, () => { OpenSavedRelationMap(s); return Task.CompletedTask; });
        }
        var count = 0;
        foreach (var kind in new[] { ElementKind.Requirement, ElementKind.Block, ElementKind.TestCase, ElementKind.UseCase, ElementKind.Activity })
            foreach (var e in Ws.Model.OfKind(kind))
            {
                if (string.IsNullOrEmpty(e.Name) || ++count > 20_000) continue;
                var el = e;
                var label = el.Kind == ElementKind.Requirement && el.Get(FieldKeys.RequirementId) is { Length: > 0 } id ? $"{id} {el.Name}" : el.Name;
                yield return new($"Relation map of {label}", "Relation map", icon, () => { OpenRelationMap(el); return Task.CompletedTask; });
            }
    }
}
