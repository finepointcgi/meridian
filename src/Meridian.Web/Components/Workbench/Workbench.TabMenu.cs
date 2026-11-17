using Meridian.Serialization.Mermaid;
using Meridian.Web.Services;
using Microsoft.AspNetCore.Components.Web;

namespace Meridian.Web.Components.Workbench;

/// <summary>Right-click on a diagram tab: Export ▸ for that diagram, select it in the tree, close it or the others.</summary>
public partial class Workbench
{
    private void ShowDiagramTabMenu(DiagramController tab, MouseEventArgs e) => OpenMenu(DiagramTabMenu(tab), e.ClientX, e.ClientY);

    /// <summary>The entries of a diagram tab's context menu; the diagram's exports sit in an Export ▸ submenu.</summary>
    internal IReadOnlyList<MenuEntry> DiagramTabMenu(DiagramController tab)
    {
        // The exports act on the active diagram: show the right-clicked tab first (when an entry runs, so opening the menu
        // does not move the keyboard focus to the canvas and Escape still closes it).
        Func<Task> On(Func<Task> export) => () =>
        {
            if (!ReferenceEquals(Ws.Active, tab) || Ws.ActiveView != null) Ws.Activate(tab);
            return export();
        };
        var export = new List<MenuEntry>
        {
            MenuEntry.Async("Export as SVG", On(ExportSvg)),
            MenuEntry.Async("Export as PNG…", On(ExportImage)),
            MenuEntry.Async("Copy as image", On(CopyDiagramImage)),
            MenuEntry.Async("Print / PDF…", On(PrintDiagram)),
            MenuEntry.Async("Export as draw.io", On(ExportDrawIo)),
        };
        if (MermaidExporter.CanExport(tab.Diagram))
        {
            export.Add(MenuEntry.Async("Export as Mermaid (.mmd)", On(ExportMermaid)));
            export.Add(MenuEntry.Async("Copy as Mermaid", On(CopyMermaid)));
        }
        var m = new List<MenuEntry>
        {
            MenuEntry.Sub("Export", export),
            MenuEntry.Sep(),
            MenuEntry.Do("Select in containment tree", () => Navigate(tab.Diagram)),
            MenuEntry.Do("Close", () => Ws.Close(tab)),
        };
        if (Ws.OpenDiagrams.Count > 1)
            m.Add(MenuEntry.Do("Close others", () => { foreach (var other in Ws.OpenDiagrams.Where(o => !ReferenceEquals(o, tab)).ToList()) Ws.Close(other); }));
        return m;
    }
}
