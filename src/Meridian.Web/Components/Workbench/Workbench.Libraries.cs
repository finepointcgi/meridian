using Meridian.Core.Libraries;
using Meridian.Core.Model;
using Meridian.Serialization;
using Meridian.Web.Libraries;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>Used projects / shared model libraries (docs/LIBRARIES.md): the dialog, update checks and reviews, "Open in source project".</summary>
public partial class Workbench
{
    [Inject] private LibraryState LibState { get; set; } = default!;
    [Inject] private LibraryService LibSvc { get; set; } = default!;

    /// <summary>A Cameo used project that already exists in Meridian and will be used instead of copied: the project, its packages and pin.</summary>
    private sealed record CameoUsage(Guid ProjectId, string Name, IReadOnlyList<string> Packages, LibraryPinKind Kind, string Value);

    /// <summary>
    /// Cameo import (docs/LIBRARIES.md): for each used project of the .mdzip that already exists in Meridian (same Cameo project id, or
    /// the same name), offers to use it instead of copying its content. Copying stays the default. Adjusts <paramref name="model"/>
    /// for the accepted ones (<see cref="MagicDrawImporter.UseLibraries"/>); the usages are created once the project exists.
    /// </summary>
    private async Task<IReadOnlyList<CameoUsage>> OfferCameoUsagesAsync(ModelRepository model, MagicDrawImportReport report)
    {
        if (report.UsedProjects.Count == 0 || Ws.User is not { } me) return [];
        var projects = await Db.ListProjectsAsync(me.Id);
        var chosen = new List<CameoUsage>();
        foreach (var used in report.UsedProjects)
        {
            var match = projects.FirstOrDefault(p => MagicDrawImporter.MatchKey(p.Name) == MagicDrawImporter.MatchKey(used.Name));
            if (match is null && used.ProjectId is { } cid)
                foreach (var p in projects.Take(100))
                    if (await Db.LoadProjectAsync(p.Id) is { } row && row.ModelJson.Contains($"\"{MagicDrawImporter.ProjectIdField}\"", StringComparison.Ordinal)
                        && row.ModelJson.Contains($"\"{cid}\"", StringComparison.Ordinal)) { match = p; break; }
            if (match is null) continue;
            var use = await Dialogs.ShowMessageBox("Used project found",
                $"This Cameo project uses “{used.Name}”, which already exists in Meridian as “{match.Name}”. Use that project (read-only, pinned) " +
                "instead of copying its content into the new project?", yesText: "Use existing project", cancelText: "Copy content");
            if (use != true) continue;
            var existing = ProjectFile.Deserialize(await Meridian.Web.Services.VersionControl.HeadJsonAsync(Db, Hub, match.Id, null));
            var packages = MagicDrawImporter.SuggestPackages(existing, used);
            if (packages.Count == 0) continue;
            var (removed, retyped, linked) = MagicDrawImporter.UseLibraries(model, report, existing, packages);
            Ws.Log($"“{used.Name}” will be used from “{match.Name}”: {removed} copied elements left out, {retyped} types and {linked} relationships linked to it.");
            var latest = (await Db.ListBranchVersionsAsync(match.Id, null)).Select(v => v.Number).DefaultIfEmpty(0).Max();
            chosen.Add(new CameoUsage(match.Id, match.Name, packages, latest > 0 ? LibraryPinKind.Version : LibraryPinKind.Branch,
                latest > 0 ? latest.ToString() : Meridian.Data.Branching.MainBranch));
        }
        return chosen;
    }

    private async Task CreateCameoUsagesAsync(Guid projectId, IReadOnlyList<CameoUsage> usages)
    {
        if (Ws.User is not { } me) return;
        foreach (var u in usages)
            foreach (var package in u.Packages)
            {
                try { await LibSvc.AddAsync(projectId, u.ProjectId, package, u.Kind, u.Value, null, me.Id); }
                catch (InvalidOperationException ex) { Ws.Log($"Could not use “{u.Name}”: {ex.Message}"); }
            }
        _ = LibState.CheckAsync();
    }

    public async Task ShowUsedProjects()
    {
        if (Ws.Session is null) { Ws.SetStatus("Open a project first"); return; }
        await Dialogs.ShowAsync<UsedProjectsDialog>("Used projects", new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true, CloseOnEscapeKey = true });
    }

    /// <summary>⌘K / ribbon: checks the usages for newer versions, branch heads and baselines.</summary>
    public async Task CheckLibraryUpdates()
    {
        if (Ws.Session is null) return;
        var found = await LibState.CheckAsync();
        if (Ws.Model.Libraries.Count == 0 && found.Count == 0) Ws.SetStatus("This project doesn't use other projects (Project ▸ Used projects…)");
        else Ws.SetStatus(found.Count == 0 ? "Used projects are up to date" : $"{found.Count} library update{(found.Count == 1 ? "" : "s")} available");
        StateHasChanged();
    }

    /// <summary>"Update used project…": the review of the one available update, or the list when there are several.</summary>
    public async Task UpdateUsedProject()
    {
        if (LibState.Updates.Count == 0) await LibState.CheckAsync();
        if (LibState.Updates.Count == 1) await LibraryUpdateDialog.ShowAsync(Dialogs, LibState.Updates[0], OnPhone);
        else await ShowUsedProjects();
    }

    /// <summary>The phone layout is showing (dialogs open as full-screen sheets).</summary>
    private bool OnPhone => Services.GetService(typeof(Meridian.Web.Services.MobileUiState)) is Meridian.Web.Services.MobileUiState { IsPhone: true };

    /// <summary>
    /// Follows a "library changed upstream" notification: opens the using project and the review of that usage's update (or the
    /// Used projects dialog when there is nothing newer any more).
    /// </summary>
    private async Task ReviewLibraryUpdateAsync(Guid projectId, string? usageId)
    {
        if (Ws.Session?.ProjectId != projectId || Ws.Pinned != null) await OpenProject(projectId);
        if (Ws.Session?.ProjectId != projectId) return;
        var offers = await LibState.CheckAsync();
        if (offers.FirstOrDefault(o => o.UsageId.ToString("N") == usageId) is { } offer) await LibraryUpdateDialog.ShowAsync(Dialogs, offer, OnPhone);
        else
        {
            Ws.SetStatus("That library update was already applied (or the usage changed).");
            await ShowUsedProjects();
        }
    }

    /// <summary>
    /// Opens the project a library element comes from at the library's pin, with the element selected: the pinned branch, or a
    /// read-only historical view of the pinned version or baseline ("Viewing version 3 (pinned)", with Go to latest).
    /// </summary>
    public async Task OpenInSourceProject(Element e)
    {
        if (Ws.LibraryOf(e) is not { } lib) return;
        var id = e.Id;
        if (Ws.Session is { } s) await s.PersistAsync();
        if (await Ws.OpenAtPinAsync(Db, Hub, lib) is { } why) { Snackbar.Add(why, Severity.Warning); return; }
        _csvTarget = Ws.Model.Root;
        if (Ws.Model.Find(id) is { } there) Navigate(there);
        if (Ws.Pinned is null) Ws.SetStatus($"{LibraryPins.ViewLabel(lib.PinKind, lib.PinLabel, null)} of “{lib.SourceProjectName}” — the branch the library is pinned to, live.");
        Ws.Log($"Opened “{lib.SourceProjectName}” at {lib.PinLabel} (the pin of the used project)");
    }

    /// <summary>Leaves a historical (pinned) view: opens the source project's latest main line.</summary>
    public async Task GoToLatest()
    {
        if (Ws.Pinned is not { } pin) return;
        await OpenProject(pin.ProjectId);
    }

    private IEnumerable<CommandPalette.Command> LibraryPaletteCommands()
    {
        yield return new("Used projects…", "Command", Icons.Material.Outlined.LibraryBooks, ShowUsedProjects);
        yield return new("Check for library updates", "Command", Icons.Material.Outlined.Sync, CheckLibraryUpdates);
        yield return new("Update used project…", "Command", Icons.Material.Outlined.Difference, UpdateUsedProject);
        if (Ws.Selected is { } sel && Ws.LibraryOf(sel) is not null)
            yield return new("Open in source project", "Command", Icons.Material.Outlined.OpenInNew, () => OpenInSourceProject(sel));
    }

    /// <summary>The tree menu of a library element: it can be shown and followed, not changed.</summary>
    private List<MenuEntry> LibraryTreeMenu(Element e, LibraryInfo lib)
    {
        var m = new List<MenuEntry> { MenuEntry.Title($"From {lib.SourceProjectName} · {lib.PinLabel} (read-only)") };
        if (e is Diagram d) m.Add(MenuEntry.Do("Open diagram", () => Ws.Open(d)));
        if (Ws.Active is { } active && e is not Diagram && !Ws.IsReadOnly(active.Diagram))
            m.Add(MenuEntry.Do($"Show on “{active.Diagram.Name}”", () => ShowOnActiveDiagram(e)));
        m.Add(MenuEntry.Async("Open in source project", () => OpenInSourceProject(e)));
        m.Add(MenuEntry.Async("Used projects…", ShowUsedProjects));
        return m;
    }

    /// <summary>
    /// The model an export should use: when the project uses other projects, asks whether to inline their content or only
    /// reference it (null when cancelled).
    /// </summary>
    private async Task<ModelRepository?> ModelForExport(string what)
    {
        if (!Ws.Model.HasLibraries) return Ws.Model;
        var choice = await Dialogs.ShowMessageBox($"Export {what}",
            (MarkupString)$"This project uses {Ws.Model.Libraries.Count} other project{(Ws.Model.Libraries.Count == 1 ? "" : "s")}. " +
            "Include their packages in the export, or only reference them (by id and qualified name)?",
            yesText: "Include library content", noText: "References only", cancelText: "Cancel");
        return choice switch
        {
            true => LibraryExport.Prepare(Ws.Model, LibraryExportMode.Inline),
            false => LibraryExport.Prepare(Ws.Model, LibraryExportMode.Reference),
            _ => null,
        };
    }
}
