using Meridian.Core.Commands;
using Meridian.Core.Libraries;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;

namespace Meridian.Web.Services;

/// <summary>Used projects (docs/LIBRARIES.md) in this tab: what a library element is, and installing a model rebuilt with new libraries.</summary>
public sealed partial class Workspace
{
    /// <summary>The used project an element comes from (read-only here), or null for the project's own elements.</summary>
    public LibraryInfo? LibraryOf(Element? e) => Model.LibraryOf(e)?.Info;

    /// <summary>
    /// Installs a model the session rebuilt for this tab (a usage was added, re-pinned or removed by me), keeping every diagram tab
    /// whose diagram still exists (with its zoom, scroll and selection), the active tab and the selected element;
    /// <paramref name="undo"/> becomes the one undo step for it.
    /// </summary>
    public void InstallLibraryChange(string modelJson, string message, IModelCommand? undo = null)
    {
        var tabs = CaptureTabs();
        Install(ProjectFile.Deserialize(modelJson));
        RestoreTabs(tabs);
        if (undo != null && !Model.History.ReadOnly) Model.History.Execute(undo);
        Log(message);
        SetStatus(message);
    }

    // ---------------------------------------------------------------- "Open in source project" at the pin

    /// <summary>
    /// Set while this tab shows a used project's source at a pinned version or baseline: a read-only historical view of a stored
    /// version (it ignores the live session's operations). Null on the live head of a line.
    /// </summary>
    public PinnedView? Pinned { get; private set; }

    /// <summary>
    /// "Open in source project" respecting the pin: a branch pin opens that branch of the source project (live, editable for its
    /// editors); a version or baseline pin opens the stored version read-only (<see cref="Pinned"/>), as this user may see it
    /// (packages hidden from them in the source project stay hidden). Returns null when it worked, else why not.
    /// </summary>
    public async Task<string?> OpenAtPinAsync(IMeridianStore db, CollaborationHub hub, LibraryInfo lib)
    {
        if (User is null) return "Sign in first.";
        var role = await db.RoleOfAsync(lib.SourceProjectId, User.Id);
        if (role is null) return $"You don't have access to “{lib.SourceProjectName}”.";
        if (lib.PinKind == LibraryPinKind.Branch)
        {
            Guid? branchId = null;
            if (!string.Equals(lib.PinValue, Branching.MainBranch, StringComparison.OrdinalIgnoreCase))
            {
                if ((await db.ListBranchesAsync(lib.SourceProjectId)).FirstOrDefault(b => b.Name == lib.PinValue) is not { } branch)
                    return $"Branch “{lib.PinValue}” of “{lib.SourceProjectName}” no longer exists.";
                branchId = branch.Id;
            }
            return await JoinAsync(hub, lib.SourceProjectId, role.Value, branchId) ? null : $"“{lib.SourceProjectName}” no longer exists.";
        }
        if (await Libraries.LibraryLoader.LoadAtPinAsync(db, hub, lib.SourceProjectId, lib.PinKind, lib.PinValue) is not { } pinned)
            return $"{lib.PinLabel} of “{lib.SourceProjectName}” no longer exists.";
        if (!await JoinAsync(hub, lib.SourceProjectId, ProjectRole.Viewer)) return $"“{lib.SourceProjectName}” no longer exists.";   // as a viewer: the session refuses edits too
        int? number = int.TryParse(pinned.Resolved.TrimStart('v'), out var n) ? n : null;
        Pinned = new PinnedView(lib.SourceProjectId, lib.SourceProjectName, lib.PinKind, lib.PinValue, LibraryPins.ViewLabel(lib.PinKind, pinned.Label, number));
        var mine = PackagePermissions.RedactJson(pinned.ModelJson, Access);   // as I may see it in the source project
        var libraries = Session?.LibrariesFor(ParticipantId) ?? [];
        Install(libraries.Count == 0 ? ProjectFile.Deserialize(mine) : ProjectFile.Deserialize(mine, libraries));
        OpenDefaultDiagrams();
        SetStatus($"{Pinned.Label} of “{lib.SourceProjectName}” — read-only. Go to latest to edit.");
        return null;
    }
}

/// <summary>A historical, read-only view of a source project at a used project's pin ("Viewing version 3 (pinned)").</summary>
public sealed record PinnedView(Guid ProjectId, string ProjectName, LibraryPinKind Kind, string Value, string Label);

/// <summary>
/// An undo step whose effect lives outside the model (a library pin): the first <see cref="Do"/> is the change already made;
/// undo (and redo) run <paramref name="undo"/> / <paramref name="redo"/>, which rebuild the model.
/// </summary>
public sealed class ExternalCommand(string description, Action undo, Action? redo = null) : IModelCommand
{
    private bool _done;
    public string Description { get; } = description;

    public void Do()
    {
        if (_done) redo?.Invoke();
        _done = true;
    }

    public void Undo() => undo();
}
