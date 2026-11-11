using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>
/// Onboarding: the guided tour's chapters (Help ▸ Tour, ⌘K, the phone account menu), the What's new card and the
/// one-click actions the empty states offer (the empty project).
/// </summary>
public partial class Workbench
{
    /// <summary>Help ▸ Tour: the list of tour chapters.</summary>
    public Task ShowTour() => _tour?.ShowChaptersAsync() ?? Task.CompletedTask;

    /// <summary>Opens one tour chapter (<see cref="TourChapters"/>).</summary>
    public Task StartTourChapter(string id) => _tour?.StartChapterAsync(id) ?? Task.CompletedTask;

    public Task ShowWhatsNew() => _tour?.ShowWhatsNewAsync() ?? Task.CompletedTask;

    private Task EmptyProjectAction(string action) => action switch
    {
        EmptyProjectHint.AutoDiagram => AutoDiagram(),
        EmptyProjectHint.Mermaid => PasteMermaid(),
        EmptyProjectHint.OpenFile => PickFile("file-open"),
        EmptyProjectHint.Sample => OpenSample(),
        EmptyProjectHint.OpenCodeBranch => OpenCodeBranch(),
        EmptyProjectHint.AcceptCode => AcceptCodeIntoMain(),
        _ => Task.CompletedTask,
    };

    private IEnumerable<CommandPalette.Command> OnboardingPaletteCommands()
    {
        yield return new("Tour: pick a chapter…", "Command", Icons.Material.Outlined.Tour, ShowTour);
        foreach (var c in TourChapters.All)
        {
            var id = c.Id;
            yield return new($"Tour: {c.Title}", "Command", c.Icon, () => StartTourChapter(id));
        }
        if (Ws.Session != null && !Ws.ReadOnly)
            yield return new("Connect a code repository…", "Command", Icons.Material.Outlined.Extension, () => IntegrationsDialog.ShowCodeRepositoriesAsync(Dialogs));
        if (Ws.Session != null && !Ws.ReadOnly)
            yield return new("Connect DOORS Next or Polarion…", "Command", Icons.Material.Outlined.SyncAlt, () => IntegrationsDialog.ShowRequirementsToolsAsync(Dialogs));
        yield return new($"What's new in {TourChapters.WhatsNewVersion}", "Command", Icons.Material.Outlined.NewReleases, ShowWhatsNew);
        yield return new("Keyboard & mouse shortcuts", "Command", Icons.Material.Outlined.Keyboard, ShowHelp);
        yield return new("User guide", "Command", Icons.Material.Outlined.MenuBook, () => OpenGuide());

        // Cameo / MagicDraw and Enterprise Architect names, so ⌘K finds the Meridian equivalent by the name people know.
        foreach (var (alias, icon, run) in CameoAliases())
            yield return new(alias, "Cameo / EA", icon, run);
    }

    private IEnumerable<(string Alias, string Icon, Func<Task> Run)> CameoAliases()
    {
        Func<Task> Dock(int index) => () => { OpenDock(index); StateHasChanged(); return Task.CompletedTask; };
        yield return ("Dependency matrix → Traceability matrix", Icons.Material.Outlined.GridOn, Dock(2));
        yield return ("Relationship matrix → Traceability matrix", Icons.Material.Outlined.GridOn, Dock(2));
        yield return ("Requirement table → Requirements table", Icons.Material.Outlined.TableRows, Dock(1));
        yield return ("Report wizard → Generate a document", Icons.Material.Outlined.Article, ExportDocx);
        yield return ("Use project / project usages → Used projects", Icons.Material.Outlined.LibraryBooks, ShowUsedProjects);
        yield return ("Quick diagram layout → Arrange", Icons.Material.Outlined.AutoAwesomeMosaic, () => { Ws.Active?.Arrange(); return Task.CompletedTask; });
        yield return ("Containment tree / Project browser → Find in model explorer", Icons.Material.Outlined.AccountTree, FocusSearch);
        yield return ("Model search → Query", Icons.Material.Outlined.ManageSearch, () => { OpenTables(TablesPanel.Mode.Query); return Task.CompletedTask; });
        yield return ("Macros / scripts → Script console", Icons.Material.Outlined.DataObject, Dock(10));
        yield return ("Teamwork Cloud commit → Commit version", Icons.Material.Outlined.Commit, Save);
    }
}
