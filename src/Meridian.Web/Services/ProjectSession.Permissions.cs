using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Data;
using Meridian.Serialization;

namespace Meridian.Web.Services;

/// <summary>
/// Per-package permissions in a live session (docs/PERMISSIONS.md). Every participant has an <see cref="AccessView"/>, resolved
/// when they join (project role, teams) and whenever the project's rules change.
/// <list type="bullet">
/// <item>Writes: <see cref="Submit"/> checks every operation with <see cref="PermissionCheck.Check"/> before applying it — the one
/// place all model edits pass (manual edits, scripts, the assistant, imports, SysML v2 text) — and rejects it with the reason.</item>
/// <item>Reads: a participant with hidden packages only ever receives a filtered model (<see cref="SnapshotFor"/>) and the
/// operations that touch what they can see; an operation that changes what they can see is replaced by a fresh filtered model.</item>
/// </list>
/// </summary>
public sealed partial class ProjectSession
{
    private sealed record ParticipantAccess(Guid UserId, ProjectRole Role, IReadOnlyList<Guid> Teams, AccessView View);

    private IReadOnlyList<PermissionRule> _rules = [];
    private readonly Dictionary<string, ParticipantAccess> _access = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stale = new(StringComparer.Ordinal);
    private Timer? _staleTimer;

    /// <summary>How long a filtered participant's refresh waits, so a burst of operations costs one filtered model.</summary>
    internal static TimeSpan ResyncDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>The project's explicit package permission rules.</summary>
    public IReadOnlyList<PermissionRule> Rules
    {
        get { lock (_gate) return _rules; }
    }

    /// <summary>Sets the rules before anyone joins (the hub loads them with the model).</summary>
    internal void InitRules(IReadOnlyList<PermissionRule> rules)
    {
        lock (_gate) _rules = rules;
    }

    /// <summary>What a participant may see and change (read-only, without rules, for someone who isn't connected).</summary>
    public AccessView AccessOf(string participantId)
    {
        lock (_gate) return _access.TryGetValue(participantId, out var a) ? a.View : AccessView.ReadAll;
    }

    /// <summary>Called under the session lock when a participant joins.</summary>
    private void RegisterAccess(string participantId, Guid userId, ProjectRole role, IReadOnlyList<Guid> teams) =>
        _access[participantId] = new ParticipantAccess(userId, role, teams, PackagePermissions.ViewFor(_rules, userId, teams, role));

    /// <summary>The model as one participant may see it (filtered when packages are hidden from them).</summary>
    public string SnapshotFor(string participantId)
    {
        lock (_gate) return SnapshotLocked(participantId);
    }

    /// <param name="fullJson">The authoritative model, already serialized (when the caller has it).</param>
    private string SnapshotLocked(string participantId, string? fullJson = null)
    {
        if (!_access.TryGetValue(participantId, out var a))
            throw new InvalidOperationException("Join the session before reading its model.");
        fullJson ??= SnapshotLocked();   // cached per model change (ProjectSession.Snapshot.cs)
        return a.View.HidesAnything ? PackagePermissions.RedactJson(fullJson, a.View) : fullJson;
    }

    /// <summary>True when an element of the authoritative model is hidden from <paramref name="view"/> (or doesn't exist).</summary>
    public bool IsHiddenFrom(AccessView view, string? elementId)
    {
        if (elementId is null) return false;
        lock (_gate) return _master.Find(elementId) is not { } e || view.IsHidden(e);
    }

    /// <summary>
    /// New rules for the project (someone changed them): every participant's view is resolved again, and participants whose view
    /// is or was filtered get a fresh model. Others re-render (lock badges, read-only diagrams).
    /// </summary>
    public void UpdateRules(IReadOnlyList<PermissionRule> rules)
    {
        lock (_gate)
        {
            _rules = rules;
            foreach (var (id, a) in _access.ToList())
            {
                var view = PackagePermissions.ViewFor(rules, a.UserId, a.Teams, a.Role);
                var refresh = a.View.HidesAnything || view.HidesAnything;
                _access[id] = a with { View = view };
                if (view.HidesAnything) LeaveTexts(id);   // shared SysML v2 texts are exported from the whole model
                if (refresh && _participants.TryGetValue(id, out var p))
                {
                    _stale.Remove(id);
                    _rejecting.Remove(id);
                    p.Client.Replaced(SnapshotLocked(id), "Package permissions changed — your view of the model was updated.");
                }
            }
            BroadcastPresence();
        }
    }

    /// <summary>The write check (under the session lock). Null when allowed, else the reason.</summary>
    private string? PermissionProblem(string participantId, ModelOp op) =>
        _access.TryGetValue(participantId, out var a) ? PermissionCheck.Check(_master, a.View, op) : "You are not connected to this project.";

    /// <summary>Before applying someone's operation: what filtered participants need to know about the model as it was.</summary>
    private Dictionary<string, (AccessView View, RelayProbe Probe)>? ProbeFiltered(string senderId, ModelOp op)
    {
        Dictionary<string, (AccessView, RelayProbe)>? probes = null;
        foreach (var (id, a) in _access)
        {
            if (id == senderId || !a.View.HidesAnything || _stale.Contains(id)) continue;
            (probes ??= new(StringComparer.Ordinal))[id] = (a.View, PermissionCheck.Probe(_master, a.View, op));
        }
        return probes;
    }

    /// <summary>Sends an applied operation to one participant: as is, not at all, or as a refresh of their filtered model.</summary>
    private void Relay(string id, ISessionClient client, ModelOp op, Dictionary<string, (AccessView View, RelayProbe Probe)>? probes)
    {
        if (_stale.Contains(id)) return;   // a fresh filtered model is on its way; it includes this operation
        if (probes is null || !probes.TryGetValue(id, out var f)) { client.RemoteOp(op); return; }
        switch (PermissionCheck.Decide(_master, f.View, op, f.Probe))
        {
            case RelayDecision.Relay: client.RemoteOp(op); break;
            case RelayDecision.Resync: MarkStale(id); break;
        }
    }

    private void MarkStale(string id)
    {
        _stale.Add(id);
        _staleTimer ??= new Timer(_ => FlushResyncs(), null, ResyncDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Sends pending filtered refreshes now (the timer does this shortly after the operations that need them).</summary>
    internal void FlushResyncs()
    {
        lock (_gate)
        {
            _staleTimer?.Dispose();
            _staleTimer = null;
            foreach (var id in _stale)
                if (_participants.TryGetValue(id, out var p))
                    p.Client.Replaced(SnapshotLocked(id), "The model changed where parts are hidden from you — your view was refreshed.");
            _stale.Clear();
        }
    }

    /// <summary>Under the lock: the view of a participant of <paramref name="userId"/>, or null when the user has no tab here.</summary>
    private AccessView? ViewOfUser(Guid userId) => _access.Values.FirstOrDefault(a => a.UserId == userId)?.View;

    private void ForgetAccess(string participantId)
    {
        _access.Remove(participantId);
        _stale.Remove(participantId);
    }
}
