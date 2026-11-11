using Meridian.Core.Model;

namespace Meridian.Core.Collaboration;

/// <summary>One local user action's replication operations, waiting for the server (see <see cref="ReplicaSync"/>).</summary>
public sealed class LocalBatch
{
    internal LocalBatch() { }

    /// <summary>The operations, in the order they were made (what is submitted to the server).</summary>
    public List<ModelOp> Ops { get; } = new();

    /// <summary>The server's sequence number of the batch's last operation once it was accepted; -1 before.</summary>
    public long Sequence { get; internal set; } = -1;

    internal RawJournal Journal { get; set; } = new();
}

/// <summary>A local edit that was dropped because someone else deleted what it changed (the server dropped it too).</summary>
public sealed record DroppedEdit(ModelOp Op, string TargetName);

/// <summary>
/// The client half of convergent collaboration: <b>server-authoritative sequencing with rebase</b> (docs/COLLABORATION.md).
/// <para>
/// The server applies every operation in one global order and relays it, with its sequence number, to everyone else; it tells
/// the author with an acknowledgement, sent through the same ordered channel. A replica applies its own edits at once (they
/// feel instant) and keeps them as <see cref="Pending"/> batches, each with a <see cref="RawJournal"/> of exact inverses, until
/// they are acknowledged. A remote operation that arrives while batches are pending was ordered by the server <i>before</i>
/// them (the channel is FIFO and their acknowledgement hasn't come yet), so the replica rewinds its pending batches, applies
/// the remote operation, and re-applies its batches the way the server did (<see cref="ModelRepository.TryApplyRemote"/>
/// semantics). Afterwards the replica holds exactly the server's model plus its own not-yet-confirmed batches, so once the
/// acknowledgements are in, every replica equals the server.
/// </para>
/// <para>
/// Consequences users see: the last write to a field in the server's order wins; an edit to something deleted meanwhile is
/// dropped (<see cref="DroppedEdit"/>); a batch the server refuses (permissions, locks, a move that would create a cycle) is
/// taken back with <see cref="Reject"/>. Re-applied operations reuse the objects the batch created the first time, so undo
/// history, selection and open diagrams keep pointing at live objects.
/// </para>
/// </summary>
public sealed class ReplicaSync : IDisposable
{
    private readonly List<LocalBatch> _pending = new();
    private LocalBatch _open = new();
    private ModelRepository _model;

    public ReplicaSync(ModelRepository model)
    {
        _model = model;
        Hook();
    }

    public ModelRepository Model => _model;

    /// <summary>Batches submitted to the server and not acknowledged yet, oldest first.</summary>
    public IReadOnlyList<LocalBatch> Pending => _pending;

    /// <summary>Operations of the action in progress (not closed into a batch yet).</summary>
    public IReadOnlyList<ModelOp> OpenOps => _open.Ops;

    /// <summary>How many times local edits were rewound and re-applied under someone else's operations.</summary>
    public int Rebases { get; private set; }

    private void Hook()
    {
        _model.OpEmitted += OnEmitted;
        _model.Journal = _open.Journal;
    }

    private void Unhook()
    {
        _model.OpEmitted -= OnEmitted;
        _model.Journal = null;
    }

    private void OnEmitted(ModelOp op) => _open.Ops.Add(op);

    /// <summary>
    /// Ends the current local action: its operations become a pending batch, to be submitted now. Null when it changed nothing.
    /// The batch is pending before it is submitted, so an acknowledgement delivered during the submit finds it.
    /// </summary>
    public LocalBatch? Close()
    {
        if (_open.Ops.Count == 0)
        {
            if (!_open.Journal.IsEmpty) _model.Journal = (_open = new LocalBatch()).Journal;
            return null;
        }
        var batch = _open;
        _pending.Add(batch);
        _open = new LocalBatch();
        _model.Journal = _open.Journal;
        return batch;
    }

    /// <summary>The server accepted <paramref name="batch"/>; <paramref name="sequence"/> is the number of its last operation.</summary>
    public static void Accepted(LocalBatch batch, long sequence) => batch.Sequence = sequence;

    /// <summary>The server confirmed the oldest pending batch (acknowledgements come in submission order).</summary>
    public void Acknowledge()
    {
        if (_pending.Count > 0) _pending.RemoveAt(0);
    }

    /// <summary>The server refused <paramref name="batch"/>: it is taken back exactly (anything applied after it is kept).</summary>
    public void Reject(LocalBatch batch)
    {
        var i = _pending.IndexOf(batch);
        if (i < 0) return;
        _model.BeginRebase();
        try
        {
            Rewind(i);
            _pending.RemoveAt(i);
            Replay(i, null, reuse: true);
        }
        finally { _model.EndRebase(); }
    }

    /// <summary>
    /// Applies operations the server sequenced (in order). With local batches pending they come first in the server's order,
    /// so the batches are rewound, the operations applied and the batches re-applied. Returns local edits that had to be
    /// dropped because their target was deleted by these operations.
    /// </summary>
    public IReadOnlyList<DroppedEdit> ApplyRemote(IReadOnlyList<ModelOp> ops)
    {
        if (_pending.Count == 0 && _open.Ops.Count == 0)
        {
            foreach (var op in ops) _model.TryApplyRemote(op);
            return [];
        }
        Rebases++;
        _model.BeginRebase();
        try
        {
            Rewind(0);
            var names = TargetNames();
            foreach (var op in ops) _model.TryApplyRemote(op);
            return Replay(0, names, reuse: true);
        }
        finally { _model.EndRebase(); }
    }

    /// <summary>
    /// The whole model was replaced by a snapshot the server took before any pending batch (a resync, merge or restore): the
    /// pending batches (and the action in progress) are re-applied on <paramref name="model"/>, as the server applied them.
    /// </summary>
    public IReadOnlyList<DroppedEdit> Rebind(ModelRepository model)
    {
        var names = TargetNames();
        Unhook();
        _model = model;
        Hook();
        if (_pending.Count == 0 && _open.Ops.Count == 0) return [];
        _model.BeginRebase();
        try { return Replay(0, names, reuse: false); }
        finally { _model.EndRebase(); }
    }

    public void Dispose() => Unhook();

    private void Rewind(int from)
    {
        _model.Revert(_open.Journal);
        for (var i = _pending.Count - 1; i >= from; i--) _model.Revert(_pending[i].Journal);
    }

    private List<DroppedEdit> Replay(int from, Dictionary<string, string>? names, bool reuse)
    {
        var dropped = new List<DroppedEdit>();
        void Run(LocalBatch b)
        {
            var old = b.Journal;
            var journal = new RawJournal();
            foreach (var op in b.Ops)
                if (!_model.Reapply(op, reuse ? old : null, journal) && MissingTarget(_model, op) is { } id)
                    dropped.Add(new DroppedEdit(op, names != null && names.TryGetValue(id, out var n) ? n : "an element"));
            b.Journal = journal;
        }
        for (var i = from; i < _pending.Count; i++) Run(_pending[i]);
        Run(_open);
        _model.Journal = _open.Journal;
        return dropped;
    }

    /// <summary>Display names of everything the local batches refer to (read before remote operations may remove them).</summary>
    private Dictionary<string, string> TargetNames()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var op in _pending.SelectMany(b => b.Ops).Concat(_open.Ops))
            foreach (var id in Referenced(op))
                if (!names.ContainsKey(id) && _model.Find(id) is { } e) names[id] = e.DisplayName;
        return names;
    }

    /// <summary>The first element <paramref name="op"/> refers to that is not in <paramref name="model"/> (null when all are).</summary>
    public static string? MissingTarget(ModelRepository model, ModelOp op)
    {
        if (op is DetachOp) return null;   // deleting what is already gone isn't a lost edit
        foreach (var id in Referenced(op))
            if (model.Find(id) is null) return id;
        return null;
    }

    private static IEnumerable<string> Referenced(ModelOp op)
    {
        switch (op)
        {
            case SetFieldOp x: yield return x.ElementId; break;
            case AttachOp x:
                yield return x.OwnerId;
                if (x.Snap.SourceId is { } source) yield return source;
                if (x.Snap.TargetId is { } target) yield return target;
                break;
            case DetachOp x: yield return x.ElementId; break;
            case MoveOp x: yield return x.ElementId; yield return x.NewOwnerId; break;
            case NodeInsertOp x: yield return x.DiagramId; yield return x.ElementId; break;
            case EdgeInsertOp x: yield return x.DiagramId; yield return x.RelationshipId; break;
            case NodeStyleOp x: yield return x.DiagramId; break;
            case NodeRemoveOp x: yield return x.DiagramId; break;
            case NodeBoundsOp x: yield return x.DiagramId; break;
            case EdgeStyleOp x: yield return x.DiagramId; break;
            case EdgeRemoveOp x: yield return x.DiagramId; break;
            case EdgeWaypointsOp x: yield return x.DiagramId; break;
        }
    }

    /// <summary>A short, friendly notice for dropped edits (null when there are none).</summary>
    public static string? Notice(IReadOnlyCollection<DroppedEdit> dropped)
    {
        if (dropped.Count == 0) return null;
        var names = dropped.Select(d => d.TargetName).Distinct().ToList();
        return names.Count == 1
            ? $"Someone else deleted “{names[0]}” — your change to it was dropped."
            : $"Someone else deleted what you were editing — {dropped.Count} of your changes were dropped.";
    }
}
