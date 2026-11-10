using Meridian.Web.Services;

namespace Meridian.Web.Components.Workbench;

/// <summary>The canvas context menu for a multi-selection (several shapes and/or connectors).</summary>
public partial class Workbench
{
    private List<MenuEntry> MultiSelectionMenu(DiagramController c)
    {
        var m = new List<MenuEntry> { MenuEntry.Title($"{c.SelectionCount} selected") };
        if (!Ws.ReadOnly && c.LinkableSelection.Count > 0)
        {
            m.Add(MenuEntry.Async("Link to…", () => BulkLinkDialog.ShowAsync(Dialogs, Ws, c)));
            m.Add(MenuEntry.Async("Trace to requirement…", () => BulkLinkDialog.ShowAsync(Dialogs, Ws, c, requirementsOnly: true)));
            m.Add(MenuEntry.Sep());
        }
        if (!Ws.ReadOnly && c.SelectedNodes.Count >= 2)
        {
            m.Add(MenuEntry.Title("Align"));
            m.Add(MenuEntry.Do("Align left", () => c.Align("left")));
            m.Add(MenuEntry.Do("Align centers", () => c.Align("center")));
            m.Add(MenuEntry.Do("Align right", () => c.Align("right")));
            m.Add(MenuEntry.Do("Align top", () => c.Align("top")));
            m.Add(MenuEntry.Do("Align middles", () => c.Align("middle")));
            m.Add(MenuEntry.Do("Align bottom", () => c.Align("bottom")));
            if (c.SelectedNodes.Count >= 3)
            {
                m.Add(MenuEntry.Do("Distribute horizontally", () => c.Align("hdist")));
                m.Add(MenuEntry.Do("Distribute vertically", () => c.Align("vdist")));
            }
            m.Add(MenuEntry.Sep());
        }
        if (c.SelectedNodes.Count > 0)
        {
            m.Add(MenuEntry.Do("Copy", c.CopySelection, "⌘C"));
            if (!Ws.ReadOnly) m.Add(MenuEntry.Do("Duplicate", c.DuplicateSelection, "⌘D"));
        }
        if (!Ws.ReadOnly && Ws.Clipboard.Any(Ws.Model.Contains))
            m.Add(MenuEntry.Do($"Paste {Ws.Clipboard.Count} element{(Ws.Clipboard.Count == 1 ? "" : "s")}", c.Paste, "⌘V"));
        if (!Ws.ReadOnly)
        {
            m.Add(MenuEntry.Sep());
            m.Add(MenuEntry.Do("Reset look", c.ClearFormat));
            m.Add(MenuEntry.Do("Delete from diagram", c.RemoveSelectionFromDiagram, "⌫"));
            m.Add(MenuEntry.Do("Delete from model", c.DeleteSelectionFromModel, "⌦"));
        }
        return m;
    }
}
