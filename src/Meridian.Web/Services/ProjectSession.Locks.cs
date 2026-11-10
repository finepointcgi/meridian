using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;

namespace Meridian.Web.Services;

/// <summary>A pessimistic edit lock on one element, or on an element and everything it contains (a package subtree).</summary>
public sealed record ElementLock(string ElementId, bool Subtree, string ParticipantId, UserAccount User, DateTime Since);

/// <summary>
/// Optional pessimistic element locking (server mode). Locks live in memory with the session: they end on unlock or when
/// the holder's tab leaves. Operations from other users that touch a locked element are rejected and the sender is
/// resynchronised to the authoritative model.
/// </summary>
public sealed partial class ProjectSession
{
    private readonly Dictionary<string, ElementLock> _locks = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rejecting = new(StringComparer.Ordinal);

    public IReadOnlyList<ElementLock> Locks
    {
        get { lock (_gate) return _locks.Values.ToList(); }
    }

    /// <summary>Locks an element (and, with <paramref name="subtree"/>, everything it owns). Returns null on success, else why not.</summary>
    public string? TryLock(string participantId, string elementId, bool subtree)
    {
        lock (_gate)
        {
            if (!_participants.TryGetValue(participantId, out var me)) return "You are not connected to this project.";
            if (_master.Find(elementId) is not { } element) return "That element no longer exists.";
            var user = me.Presence.User;
            foreach (var l in _locks.Values.Where(l => l.User.Id != user.Id))
            {
                var other = _master.Find(l.ElementId);
                if (other is null) continue;
                if (l.ElementId == elementId) return $"{element.DisplayName} is already locked by {l.User.DisplayName}.";
                if (l.Subtree && other.IsAncestorOf(element)) return $"{other.DisplayName} (containing it) is locked by {l.User.DisplayName}.";
                if (subtree && element.IsAncestorOf(other)) return $"{other.DisplayName} inside it is locked by {l.User.DisplayName}.";
            }
            _locks[elementId] = new ElementLock(elementId, subtree, participantId, user, DateTime.UtcNow);
            BroadcastPresence();
            return null;
        }
    }

    /// <summary>Releases a lock held by <paramref name="userId"/> (or any lock, with <paramref name="force"/> for project owners).</summary>
    public bool Unlock(string elementId, Guid userId, bool force = false)
    {
        lock (_gate)
        {
            if (!_locks.TryGetValue(elementId, out var l) || (!force && l.User.Id != userId)) return false;
            _locks.Remove(elementId);
            BroadcastPresence();
            return true;
        }
    }

    /// <summary>The lock (held by someone other than <paramref name="userId"/>) that stops that user editing <paramref name="elementId"/>.</summary>
    public ElementLock? BlockingLock(Guid userId, string? elementId)
    {
        lock (_gate) return Blocking(userId, elementId);
    }

    private ElementLock? Blocking(Guid userId, string? elementId)
    {
        if (elementId is null || _locks.Count == 0) return null;
        var element = _master.Find(elementId);
        foreach (var l in _locks.Values)
        {
            if (l.User.Id == userId) continue;
            if (l.ElementId == elementId) return l;
            if (l.Subtree && element != null && _master.Find(l.ElementId) is { } owner && owner.IsAncestorOf(element)) return l;
        }
        return null;
    }

    /// <summary>A lock held by someone else on anything inside <paramref name="elementId"/> (deleting or moving it would take that along).</summary>
    private ElementLock? LockedInside(Guid userId, string elementId)
    {
        if (_master.Find(elementId) is not { } element) return null;
        return _locks.Values.FirstOrDefault(l => l.User.Id != userId && _master.Find(l.ElementId) is { } x && element.IsAncestorOf(x));
    }

    private ElementLock? Blocks(Guid userId, ModelOp op) => op switch
    {
        SetFieldOp sf => Blocking(userId, sf.ElementId),
        AttachOp a => Blocking(userId, a.OwnerId),
        DetachOp d => Blocking(userId, d.ElementId) ?? LockedInside(userId, d.ElementId),
        MoveOp m => Blocking(userId, m.ElementId) ?? Blocking(userId, m.NewOwnerId) ?? LockedInside(userId, m.ElementId),
        NodeInsertOp x => Blocking(userId, x.DiagramId),
        NodeStyleOp x => Blocking(userId, x.DiagramId),
        NodeRemoveOp x => Blocking(userId, x.DiagramId),
        NodeBoundsOp x => Blocking(userId, x.DiagramId),
        EdgeInsertOp x => Blocking(userId, x.DiagramId),
        EdgeStyleOp x => Blocking(userId, x.DiagramId),
        EdgeRemoveOp x => Blocking(userId, x.DiagramId),
        EdgeWaypointsOp x => Blocking(userId, x.DiagramId),
        _ => null,
    };

    /// <summary>
    /// Hook called (under the session lock) before an operation is applied. A rejected operation is dropped, as is every
    /// later one from the same tab until it has re-synchronised to the authoritative model.
    /// </summary>
    private bool AllowEdit(string participantId, Guid userId, ModelOp op)
    {
        if (_rejecting.Contains(participantId)) return false;
        // Only known participants with an editing role may change the model (viewers are read-only server-side too).
        if (!_participants.TryGetValue(participantId, out var me) || !me.Presence.CanEdit)
        {
            if (me.Client is { } c) { _rejecting.Add(participantId); c.Replaced(SnapshotLocked(), "This project is view-only for you — your change was not applied."); }
            return false;
        }
        if (_locks.Count == 0 || Blocks(userId, op) is not { } l) return true;
        _rejecting.Add(participantId);
        var what = _master.Find(l.ElementId)?.DisplayName ?? "That element";
        if (_participants.TryGetValue(participantId, out var p))
            p.Client.Replaced(SnapshotLocked(), $"{what} is locked by {l.User.DisplayName} — your change was not applied.");
        return false;
    }

    /// <summary>Called by a tab once it has installed the model it was sent after a rejection.</summary>
    public void Resynced(string participantId)
    {
        lock (_gate) _rejecting.Remove(participantId);
    }

    private void ReleaseLocksOf(string participantId)
    {
        foreach (var id in _locks.Where(kv => kv.Value.ParticipantId == participantId).Select(kv => kv.Key).ToList()) _locks.Remove(id);
        _rejecting.Remove(participantId);
    }

    /// <summary>Replaces the whole model for everyone (a merge result), telling them why. A null user is the system (code sync).</summary>
    public void Replace(string modelJson, Guid? userId, string message)
    {
        lock (_gate)
        {
            _master = ProjectFile.Deserialize(modelJson);
            Sequence++;
            MarkDirty();
            _lastEditor = userId;
            foreach (var (id, p) in _participants)
            {
                _rejecting.Remove(id);
                p.Client.Replaced(modelJson, message);
            }
        }
    }
}
