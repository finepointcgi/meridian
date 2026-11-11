using Meridian.Core.Model;
using Meridian.Web.Services;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>Printing (PrintDialog.razor, Services/DiagramPrinting.cs): Print / PDF… for the active diagram (⌘P, its tab's
/// Export ▸ menu, the Export menu and ⌘K) and Print diagrams… for a package.</summary>
public partial class Workbench
{
    private IEnumerable<CommandPalette.Command> PrintPaletteCommands() =>
    [
        new("Print / PDF… (active diagram: page setup and preview)", "Command", Icons.Material.Outlined.Print, PrintDiagram),
        new("Print diagrams… (several diagrams of the selected package into one PDF)", "Command", Icons.Material.Outlined.PictureAsPdf,
            () => PrintDiagrams(Ws.Selected is { } e && e is not Diagram ? e : Ws.Active?.Diagram.Owner ?? Ws.Model.Root)),
    ];

    /// <summary>Page setup and preview for the active diagram, with its current view and selection.</summary>
    private async Task PrintDiagram()
    {
        if (Ws.Active is not { } c) { Ws.SetStatus("Open a diagram first"); return; }
        var parameters = new DialogParameters<PrintDialog>
        {
            { x => x.Diagram, c.Diagram },
            { x => x.View, c.Viewport },
            { x => x.Selection, c.SelectedNodes.Select(n => n.Id).ToList() },
        };
        await Dialogs.ShowAsync<PrintDialog>("Print", parameters, PrintDialogOptions);
    }

    private async Task PrintDiagrams(Element container)
    {
        if (!container.Descendants().OfType<Diagram>().Any()) { Ws.SetStatus($"“{container.DisplayName}” has no diagrams to print"); return; }
        var parameters = new DialogParameters<PrintDialog> { { x => x.Container, container } };
        await Dialogs.ShowAsync<PrintDialog>("Print diagrams", parameters, PrintDialogOptions);
    }

    private static readonly DialogOptions PrintDialogOptions = new() { MaxWidth = MaxWidth.Large, FullWidth = true, CloseOnEscapeKey = true };
}
