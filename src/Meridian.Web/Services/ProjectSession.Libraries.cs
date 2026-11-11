using Meridian.Core.Libraries;
using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Serialization;

namespace Meridian.Web.Services;

/// <summary>
/// Used projects (shared model libraries, docs/LIBRARIES.md) in a live session.
/// <list type="bullet">
/// <item>The authoritative model has every library mounted (read-only, <see cref="LibraryGuard"/> refuses edits to them in
/// <see cref="PermissionCheck.Check"/>). What is persisted never contains them: <see cref="ProjectFile.Serialize(ModelRepository)"/>
/// skips library roots.</item>
/// <item>Each participant receives the libraries they may read (<see cref="Libraries.LibraryLoader.ForUserAsync"/>: a role on the
/// source project, its hidden packages left out) in the <c>"libraries"</c> section of their snapshot, and none whose mount point
/// is hidden from them in this project.</item>
/// <item>Changing a usage or a pin rebuilds the model with the new libraries for everyone (<see cref="ReloadLibraries{T}"/>).</item>
/// </list>
/// </summary>
public sealed partial class ProjectSession
{
    private LibraryResolution _libraries = LibraryResolution.Empty;
    private readonly Dictionary<string, IReadOnlyList<LibraryPayload>> _libraryAccess = new(StringComparer.Ordinal);

    /// <summary>Every library loaded into the authoritative model (all participants' union) and what failed to load.</summary>
    public LibraryResolution Libraries
    {
        get { lock (_gate) return _libraries; }
    }

    /// <summary>Called by the hub before anyone joins: the libraries the model was loaded with.</summary>
    internal void InitLibraries(LibraryResolution libraries)
    {
        lock (_gate) _libraries = libraries;
    }

    /// <summary>Under the lock: what a joining participant may read.</summary>
    private void RegisterLibraries(string participantId, IReadOnlyList<LibraryPayload>? libraries)
    {
        if (libraries is null || libraries.Count == 0) _libraryAccess.Remove(participantId);
        else _libraryAccess[participantId] = libraries;
    }

    private void ForgetLibraries(string participantId) => _libraryAccess.Remove(participantId);

    /// <summary>Adds a participant's libraries to their (possibly filtered) project document.</summary>
    private string WithLibrariesFor(string participantId, AccessView view, string json)
    {
        if (!_libraryAccess.TryGetValue(participantId, out var mine) || mine.Count == 0) return json;
        var visible = mine.Where(p => MountVisible(view, p.Info)).ToList();
        return ProjectFile.WithLibraries(json, visible);
    }

    private bool MountVisible(AccessView view, LibraryInfo info)
    {
        if (!view.HidesAnything) return true;
        var mount = _master.Find(info.MountId);
        if (mount is null || _master.IsLibraryElement(mount)) return true;   // the model root, or inside another library
        return !view.IsHidden(mount) && view.CanSeeContents(mount);
    }

    /// <summary>The authoritative model rebuilt from a project document with the session's libraries mounted.</summary>
    private ModelRepository Materialize(string modelJson) =>
        _libraries.Payloads.Count == 0 ? ProjectFile.Deserialize(modelJson) : ProjectFile.Deserialize(modelJson, _libraries.Payloads);

    /// <summary>
    /// Replaces the libraries (a usage was added, removed or re-pinned): the model is rebuilt with them, <paramref name="afterMount"/>
    /// may change it (suspect marks after a library update), and everyone gets the new model — except <paramref name="initiator"/>,
    /// whose document is returned so that tab can install it itself (and keep an undo step).
    /// </summary>
    internal (string? InitiatorJson, T Result) ReloadLibraries<T>(LibraryResolution libraries, IReadOnlyDictionary<string, IReadOnlyList<LibraryPayload>> perParticipant,
                                                                 string message, string? initiator, Func<ModelRepository, T> afterMount)
    {
        lock (_gate)
        {
            var projectJson = SnapshotLocked();
            _libraries = libraries;
            _master = Materialize(projectJson);
            var result = afterMount(_master);
            InvalidateSnapshots();
            ScheduleTextRefresh();
            Sequence++;
            MarkDirty();
            _stale.Clear();
            foreach (var id in _participants.Keys)
                RegisterLibraries(id, perParticipant.TryGetValue(id, out var p) ? p : _libraryAccess.GetValueOrDefault(id));
            string? mine = null;
            foreach (var (id, p) in _participants)
            {
                _rejecting.Remove(id);
                if (id == initiator) mine = SnapshotLocked(id);
                else p.Client.Replaced(ModelForLocked(id), message);
            }
            return (mine, result);
        }
    }

    /// <summary>The users connected (to work out what libraries each may read).</summary>
    internal IReadOnlyList<(string ParticipantId, Guid UserId)> ParticipantUsers()
    {
        lock (_gate) return _participants.Select(kv => (kv.Key, kv.Value.Presence.User.Id)).ToList();
    }

    /// <summary>Runs <paramref name="read"/> on the authoritative model under the session lock (it must not keep references).</summary>
    internal T ReadMaster<T>(Func<ModelRepository, T> read)
    {
        lock (_gate) return read(_master);
    }
}
