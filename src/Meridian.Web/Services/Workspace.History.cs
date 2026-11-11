using Meridian.Core.Libraries;
using Meridian.Data;
using Meridian.Serialization;

namespace Meridian.Web.Services;

/// <summary>Element history in this tab: the read-only view of the project at a committed version ("Show model at this version").</summary>
public sealed partial class Workspace
{
    private PinnedView? _versionPin;
    private BranchRef? _versionLine;

    /// <summary>The line a "model at version" view was opened from (Go to latest returns to it), or null.</summary>
    public BranchRef? HistoricalLine => Pinned != null && ReferenceEquals(Pinned, _versionPin) ? _versionLine : null;

    /// <summary>
    /// Opens the current project at a committed version, read-only (the same historical view as a used project's pinned version):
    /// the stored version as this user may see it, with the line's live session joined as a viewer. Returns null when it worked.
    /// </summary>
    public async Task<string?> OpenVersionAsync(IMeridianStore db, CollaborationHub hub, long versionId, int number)
    {
        if (User is null || Session is not { } s) return "Open a project first.";
        var (projectId, name, line) = (s.ProjectId, s.Name, new BranchRef(s.BranchId, s.BranchName));
        var json = await db.LoadVersionAsync(projectId, versionId);
        if (json is null) return $"Version {number} no longer exists.";
        await s.PersistAsync();
        if (!await JoinAsync(hub, projectId, ProjectRole.Viewer, line.BranchId)) return $"“{name}” no longer exists.";   // as a viewer: no edits
        // Pinned first: from here on the tab ignores the live session's operations (Workspace.Collaboration.cs).
        _versionPin = Pinned = new PinnedView(projectId, name, LibraryPinKind.Version, number.ToString(System.Globalization.CultureInfo.InvariantCulture), $"Version {number}");
        _versionLine = line;
        var mine = PackagePermissions.RedactJson(json, Access);   // as I may see it
        var libraries = Session?.LibrariesFor(ParticipantId) ?? [];
        Install(libraries.Count == 0 ? ProjectFile.Deserialize(mine) : ProjectFile.Deserialize(mine, libraries));
        OpenDefaultDiagrams();
        SetStatus($"Version {number} of “{name}” — read-only. Go to latest to edit.");
        Bump();
        return null;
    }
}
