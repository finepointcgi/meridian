using Meridian.Core.Model;
using Meridian.Serialization;

namespace Meridian.Web.Services;

/// <summary>
/// Serialized snapshots of the authoritative model, shared while nothing changes, and replicas for joining tabs.
/// <list type="bullet">
/// <item>A 200,000-element model takes ~100 ms to serialize (~80 MB of JSON), all under the session lock: autosave, filtered
/// participants and resyncs after lock rejections would each pay that. The cache is keyed by <see cref="Sequence"/> (which every
/// model change bumps) and held weakly, so an idle session never pins a copy of the JSON in memory.</item>
/// <item>A tab that may see the whole model gets a replica copied from the authoritative model in memory
/// (<see cref="ModelRepository.CreateReplica"/>), sharing its strings and field dictionaries copy-on-write, instead of parsing a
/// snapshot: joining costs tens of milliseconds instead of half a second, and each tab holds a skeleton of the model rather than
/// a full copy. Participants with hidden packages or a different set of used projects still parse their filtered snapshot.</item>
/// </list>
/// </summary>
public sealed partial class ProjectSession
{
    private long _snapshotSequence = -1;
    private readonly WeakReference<string?> _snapshot = new(null);

    /// <summary>The current model as JSON (call under <see cref="_gate"/>).</summary>
    private string SnapshotLocked()
    {
        if (_snapshotSequence == Sequence && _snapshot.TryGetTarget(out var json) && json != null) return json;
        json = ProjectFile.Serialize(_master);
        _snapshot.SetTarget(json);
        _snapshotSequence = Sequence;
        return json;
    }

    /// <summary>
    /// A replica of the model as <paramref name="participantId"/> may see it, for their tab to edit. Operations relayed to the
    /// participant after this call apply on top of it.
    /// </summary>
    public ModelRepository ReplicaFor(string participantId)
    {
        string json;
        lock (_gate)
        {
            if (SeesWholeModelLocked(participantId)) return _master.CreateReplica();
            json = SnapshotLocked(participantId);
        }
        return ProjectFile.Deserialize(json);   // filtered (package permissions, used projects): parsed outside the lock
    }

    /// <summary>
    /// True when the participant's snapshot would be the authoritative model as it is: nothing hidden from them, and they read
    /// exactly the used projects mounted in it.
    /// </summary>
    private bool SeesWholeModelLocked(string participantId)
    {
        if (!_access.TryGetValue(participantId, out var a) || a.View.HidesAnything) return false;
        var mine = _libraryAccess.GetValueOrDefault(participantId) ?? [];
        var all = _libraries.Payloads;
        if (mine.Count == 0 && all.Count == 0) return !_master.HasLibraries;
        return mine.Count == all.Count && mine.SequenceEqual(all);
    }
}
