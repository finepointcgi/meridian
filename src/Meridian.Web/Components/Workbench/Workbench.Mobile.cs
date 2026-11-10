using Meridian.Core.Model;

namespace Meridian.Web.Components.Workbench;

/// <summary>Entry points for the phone shell, the tablet drawers and the ribbon overflow menu (Components/Mobile).</summary>
public partial class Workbench
{
    /// <summary>Re-renders the shell (and so the canvas) without bumping the workspace version, e.g. after a touch pan or pinch.</summary>
    public void Repaint() => _ = InvokeAsync(StateHasChanged);

    /// <summary>Opens the ⌘K search / command palette.</summary>
    public void ShowPalette() => _palette?.Show();

    public Task ShowProjects() => OpenProjects();

    public void ToggleDarkMode() => ToggleTheme();

    public void SignOutOfMeridian() => SignOut();

    public bool IsLocalMode => Host.LocalMode;

    public string SavedStatus => SavedText();

    /// <summary>Opens a bottom-dock panel (validation, tables, simulation, …) from outside the ribbon.</summary>
    public void ShowDock(int index)
    {
        OpenDock(index);
        StateHasChanged();
    }

    public void ShowTables(TablesPanel.Mode mode)
    {
        ToggleTables(mode);
        StateHasChanged();
    }

    public void RunValidate() { Validate(); StateHasChanged(); }

    public void RunCoverage() => Coverage();

    public void RunImpactOfSelection() { ImpactOfSelection(); StateHasChanged(); }

    public Task RunOpenReport() => OpenReport();

    public Task RunExportSysML() => ExportSysML();

    public Task RunExportXmi() => ExportXmi();

    public Task RunGenerateCode() => GenerateCode();

    public Task RunExportDocx() => ExportDocx();

    public void RunEditAsSysml() { EditAsSysml(); StateHasChanged(); }

    /// <summary>Whether an element has a diagram to open: it is one, owns one, or calls a behavior that has one.</summary>
    public static Diagram? DiagramFor(Element e) =>
        e as Diagram ?? e.OwnedElements.OfType<Diagram>().FirstOrDefault();
}
