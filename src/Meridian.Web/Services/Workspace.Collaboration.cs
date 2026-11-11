using System.Collections.Concurrent;
using Meridian.Core.Collaboration;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;

namespace Meridian.Web.Services;

/// <summary>
/// The collaboration client of a tab (docs/COLLABORATION.md). Local edits apply at once and go to the session one user action
/// at a time (<see cref="ProjectSession.SubmitBatch"/>); until the session acknowledges them they stay pending in a
/// <see cref="ReplicaSync"/>, which rebases them under operations the session ordered earlier. Everything the session sends
/// (operations, acknowledgements, replacements) goes through one inbox, processed in arrival order on this tab's thread.
/// </summary>
public sealed partial class Workspace
{
    private abstract record Inbound;
    private sealed record InOp(ModelOp Op, long Sequence) : Inbound;
    private sealed record InAck(long Sequence) : Inbound;
    private sealed record InReplace(SessionModel Model, string? Message) : Inbound;

    private readonly ConcurrentQueue<Inbound> _inbox = new();
    private ReplicaSync? _sync;
    private bool _joining;
    private long _joinedAt = -1;
    private int _draining;
    private int _skippedSeen;
    private bool _cleanupDue;

    /// <summary>Raised when the session refused one of my actions (it has been taken back here): its operations and why.</summary>
    public event Action<IReadOnlyList<ModelOp>, string>? EditRefused;

    /// <summary>My actions the session has not acknowledged yet (diagnostics and tests).</summary>
    public int PendingBatches => _sync?.Pending.Count ?? 0;

    /// <summary>How often my unconfirmed edits were re-applied under someone else's earlier operations (diagnostics).</summary>
    public int Rebases => _sync?.Rebases ?? 0;

    public async Task<bool> JoinAsync(CollaborationHub hub, Guid projectId, ProjectRole role, Guid? branchId = null)
    {
        if (User is null) return false;
        LeaveSession();
        Pinned = null;
        _joining = true;
        _inbox.Clear();
        var session = await hub.JoinAsync(projectId, new Presence(ParticipantId, User, null, null, CanEdit: role != ProjectRole.Viewer), this, branchId);
        if (session is null) { _joining = false; return false; }
        var (replica, sequence) = session.ReplicaWithSequence(ParticipantId);   // shares the session's model; filtered when packages are hidden from me
        Session = session;
        Role = role;
        Install(replica);
        _joinedAt = sequence;   // operations relayed before the snapshot was taken are already in it
        _joining = false;
        DrainInbox();
        OpenDefaultDiagrams();
        Bump();
        return true;
    }

    /// <summary>Uses a model without a collaboration session (tests, offline previews).</summary>
    public void UseLocalModel(ModelRepository model)
    {
        LeaveSession();
        Install(model);
        Bump();
    }

    /// <summary>
    /// Makes <paramref name="replica"/> this tab's model. With <paramref name="keepPending"/> (a replacement sent by the session),
    /// my unacknowledged actions are re-applied on it: the session applied them after the snapshot it sent.
    /// </summary>
    private IReadOnlyList<DroppedEdit> Install(ModelRepository replica, bool keepPending = false)
    {
        Model.Changed -= OnModelChanged;
        Model.History.Changed -= OnHistoryChanged;
        Model.History.ActionCompleted -= FlushLocalOps;
        Model.OpEmitted -= OnLocalOp;
        IReadOnlyList<DroppedEdit> dropped = [];
        if (Session is null) { _sync?.Dispose(); _sync = null; }
        else if (keepPending && _sync != null) dropped = _sync.Rebind(replica);
        else { _sync?.Dispose(); _sync = new ReplicaSync(replica); }   // subscribes before OnLocalOp, so it has each op first
        Model = replica;
        Model.History.ReadOnly = ReadOnly;
        Model.Changed += OnModelChanged;
        Model.History.Changed += OnHistoryChanged;
        Model.OpEmitted += OnLocalOp;
        Model.History.ActionCompleted += FlushLocalOps;
        _skippedSeen = Model.SkippedEdits;
        _cleanupDue = false;
        ModelVersion++;
        TreeVersion++;
        _open.Clear();
        _views.Clear();
        ActiveView = null;
        _back.Clear();
        _forward.Clear();
        Active = null;
        Selected = null;
        return dropped;
    }

    /// <summary>An operation outside any action (rare: loaders) goes on its own; within an action they wait for its end.</summary>
    private void OnLocalOp(ModelOp op)
    {
        if (Session is null || Model.History.InAction) return;
        SubmitLocal(fromHistory: false);
    }

    /// <summary>
    /// The end of a user action: its operations go to the session together, so it accepts or refuses the action as a whole
    /// (package permissions, locks, move cycles) rather than part of it.
    /// </summary>
    private void FlushLocalOps() => SubmitLocal(fromHistory: true);

    private void SubmitLocal(bool fromHistory)
    {
        if (Session is null || _sync is null) return;
        var skipped = Model.SkippedEdits - _skippedSeen;
        _skippedSeen = Model.SkippedEdits;
        if (_sync.Close() is { } batch)
        {
            var result = Session.SubmitBatch(ParticipantId, User?.Id ?? Guid.Empty, batch.Ops);
            if (!result.Accepted)
            {
                // Refused (permissions, a lock, a move into something moved inside it meanwhile): take it back here too.
                _sync.Reject(batch);
                if (fromHistory) Model.History.DiscardLastAction();
                CleanUpAfterRebase();
                _skippedSeen = Model.SkippedEdits;
                var why = result.Refused!;
                EditRefused?.Invoke(batch.Ops, why);
                Log(why);
                SetStatus(why);
                return;
            }
            ReplicaSync.Accepted(batch, result.Sequence);
        }
        if (skipped > 0)
        {
            const string notice = "Someone else deleted part of what this changed — that part was skipped.";
            Log(notice);
            SetStatus(notice);
        }
    }

    public void LeaveSession()
    {
        Session?.Leave(ParticipantId);
        Session = null;
        _sync?.Dispose();
        _sync = null;
        _inbox.Clear();
        _joinedAt = -1;
    }

    void ISessionClient.RemoteOp(ModelOp op) => Enqueue(new InOp(op, -1));
    void ISessionClient.RemoteOp(ModelOp op, long sequence) => Enqueue(new InOp(op, sequence));
    void ISessionClient.Acked(long sequence) => Enqueue(new InAck(sequence));
    void ISessionClient.Reset(SessionModel model) => Enqueue(new InReplace(model, null));
    void ISessionClient.Replaced(SessionModel model, string message) => Enqueue(new InReplace(model, message));
    void ISessionClient.PresenceChanged() => _ = Dispatch(Bump);
    void ISessionClient.CommentsChanged() => _ = Dispatch(() => { CommentsVersion++; Bump(); });

    /// <summary>Called by the session under its lock: only queue (the inbox keeps the session's order), then drain on our thread.</summary>
    private void Enqueue(Inbound message)
    {
        _inbox.Enqueue(message);
        _ = Dispatch(DrainInbox);
    }

    /// <summary>
    /// Processes everything the session has sent, in order. Consecutive operations are applied together, so a burst costs one
    /// rebase of my pending actions rather than one per operation.
    /// </summary>
    private void DrainInbox()
    {
        if (Pinned != null) { _inbox.Clear(); return; }   // a historical view (Workspace.Libraries.cs) doesn't follow the live head
        if (_joining || Interlocked.Exchange(ref _draining, 1) == 1) return;
        var dropped = new List<DroppedEdit>();
        var changed = false;
        try
        {
            var ops = new List<ModelOp>();
            while (_inbox.TryDequeue(out var message))
            {
                changed = true;
                switch (message)
                {
                    case InOp o:
                        if (o.Sequence >= 0 && o.Sequence <= _joinedAt) break;   // already in the snapshot I joined with
                        ops.Add(o.Op);
                        if (_inbox.TryPeek(out var next) && next is InOp) break;
                        ApplyRemote(ops, dropped);
                        ops.Clear();
                        break;
                    case InAck a:
                        _sync?.Acknowledge(a.Sequence);
                        break;
                    case InReplace r:
                        dropped.AddRange(ApplyReplacement(r.Model, r.Message));
                        break;
                }
            }
            if (ops.Count > 0) ApplyRemote(ops, dropped);
        }
        finally { Volatile.Write(ref _draining, 0); }
        if (!changed) return;
        CleanUpAfterRebase();
        if (ReplicaSync.Notice(dropped) is { } notice)
        {
            Log(notice);
            SetStatus(notice);
        }
        // Something may have arrived after the last dequeue but before the flag was cleared (its own dispatch saw us draining).
        if (!_inbox.IsEmpty) _ = Dispatch(DrainInbox);
    }

    private void ApplyRemote(List<ModelOp> ops, List<DroppedEdit> dropped)
    {
        if (_sync != null) dropped.AddRange(_sync.ApplyRemote(ops));
        else foreach (var op in ops) Model.ApplyRemote(op);
    }

    /// <summary>The session replaced the whole model: a version restore (no message), a merge, a permission change or a resync.</summary>
    private IReadOnlyList<DroppedEdit> ApplyReplacement(SessionModel model, string? message)
    {
        var tabs = CaptureTabs();
        var dropped = Install(model.CreateReplica(), keepPending: true);
        if (message != null) Session?.Resynced(ParticipantId);
        RestoreTabs(tabs);   // the code-change review stays open across a merge (accepting code changes replaces main's model)
        message ??= "The model was restored to an earlier version";
        Log(message);
        SetStatus(message);
        return dropped;
    }

    /// <summary>After a rebase or a rejected action: what referred to elements that are now gone (tabs, selection).</summary>
    private void CleanUpAfterRebase()
    {
        if (!_cleanupDue) return;
        _cleanupDue = false;
        CleanUpRemoved();
        foreach (var c in _open.ToArray()) c.ClearInvalidSelection();
        Bump();
    }

    private void CleanUpRemoved()
    {
        if (Selected != null && !Model.Contains(Selected)) Selected = null;
        foreach (var c in _open.Where(c => !Model.Contains(c.Diagram)).ToList()) Close(c);
        foreach (var v in _views.Where(v => v.Element != null && !Model.Contains(v.Element)).ToList()) CloseView(v);
        foreach (var c in _open.ToArray()) c.ClearInvalidSelection();
    }
}
