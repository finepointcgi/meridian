using Meridian.Serialization;

namespace Meridian.Web.Services;

/// <summary>
/// Serialized snapshots of the authoritative model, shared while nothing changes. A 200,000-element model takes ~100 ms to
/// serialize (~80 MB of JSON), all under the session lock: tabs joining together, autosave, and resyncs after lock rejections
/// would each pay that. The cache is keyed by <see cref="Sequence"/> (which every model change bumps) and held weakly, so an
/// idle session never pins a copy of the JSON in memory.
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
}
