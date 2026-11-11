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
/// <item>Tabs that must start over (merge, version restore, a rejected edit) receive a <see cref="SessionModel"/>: one frozen
/// replica per model state, made under the lock and copied by each tab on its own thread, instead of a document each tab parses.
/// A tab joining while that frozen replica is current copies it outside the lock too.</item>
/// </list>
/// </summary>
public sealed partial class ProjectSession
{
    private long _snapshotSequence = -1;
    private readonly WeakReference<string?> _snapshot = new(null);
    private long _frozenSequence = -1;
    private readonly WeakReference<ModelRepository?> _frozen = new(null);

    /// <summary>The model changed or was replaced: cached snapshots no longer apply (call under <see cref="_gate"/>).</summary>
    private void InvalidateSnapshots()
    {
        _snapshotSequence = -1;
        _frozenSequence = -1;
    }

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
    public ModelRepository ReplicaFor(string participantId) => ReplicaWithSequence(participantId).Replica;

    /// <summary>
    /// <see cref="ReplicaFor"/> with the sequence number it reflects: a joining tab skips relayed operations up to it (they
    /// were queued for it before the replica was taken, and are already in it).
    /// </summary>
    public (ModelRepository Replica, long Sequence) ReplicaWithSequence(string participantId)
    {
        SessionModel model;
        long sequence;
        lock (_gate)
        {
            sequence = Sequence;
            if (SeesWholeModelLocked(participantId))
            {
                // A frozen replica of this very state (made for a resync) is copied outside the lock; otherwise copy the model.
                if (!TryFrozenLocked(out var frozen)) return (_master.CreateReplica(), sequence);
                model = new SessionModel(frozen);
            }
            else model = new SessionModel(SnapshotLocked(participantId));
        }
        return (model.CreateReplica(), sequence);   // filtered (package permissions, used projects): parsed outside the lock
    }

    /// <summary>
    /// What a tab that must start over receives (call under <see cref="_gate"/>): a frozen replica shared by every recipient of
    /// this model state, copied by each tab on its own thread, or its filtered snapshot.
    /// </summary>
    /// <param name="fullJson">The authoritative model, already serialized (when the caller has it; used for filtered views).</param>
    private SessionModel ModelForLocked(string participantId, string? fullJson = null)
    {
        if (!SeesWholeModelLocked(participantId)) return new SessionModel(SnapshotLocked(participantId, fullJson));
        if (!TryFrozenLocked(out var frozen))
        {
            frozen = _master.CreateReplica();
            _frozen.SetTarget(frozen);
            _frozenSequence = Sequence;
        }
        return new SessionModel(frozen);
    }

    private bool TryFrozenLocked(out ModelRepository frozen)
    {
        frozen = null!;
        return _frozenSequence == Sequence && _frozen.TryGetTarget(out frozen!) && frozen != null;
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
