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
        yield return new($"What's new in {TourChapters.WhatsNewVersion}", "Command", Icons.Material.Outlined.NewReleases, ShowWhatsNew);
    }
}
