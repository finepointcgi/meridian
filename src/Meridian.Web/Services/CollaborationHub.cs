using System.Collections.Concurrent;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;

namespace Meridian.Web.Services;

/// <summary>A tab in a session. <paramref name="CanEdit"/> is false for viewers: their operations are refused by the session, not only by their own replica.</summary>
public sealed record Presence(string ParticipantId, UserAccount User, string? SelectedId, string? DiagramId, bool CanEdit = true);

/// <summary>
/// Live collaboration. One <see cref="ProjectSession"/> per open project holds the authoritative model; every
/// browser tab keeps a replica, submits its operations here, and receives everyone else's in a single global order.
/// The authoritative model is persisted to PostgreSQL shortly after changes.
/// </summary>
public sealed partial class CollaborationHub(IMeridianStore db, ILogger<CollaborationHub> log, Integrations.ProjectEvents? events = null)
{
    // Sessions are keyed per project + branch (null branch = main).
    private readonly ConcurrentDictionary<(Guid Project, Guid? Branch), ProjectSession> _sessions = new();
    private readonly SemaphoreSlim _openGate = new(1, 1);

    public async Task<ProjectSession?> JoinAsync(Guid projectId, Presence me, ISessionClient client, Guid? branchId = null)
    {
        await _openGate.WaitAsync();
        try
        {
            var key = (projectId, branchId);
            if (!_sessions.TryGetValue(key, out var session))
            {
                var row = await db.LoadProjectAsync(projectId);
                if (row is null) return null;
                var modelJson = row.Value.ModelJson;
                string? branchName = null;
                if (branchId is { } bid)
                {
                    var branch = await db.LoadBranchAsync(bid);
                    if (branch is null || branch.ProjectId != projectId) return null;
                    (modelJson, branchName) = (branch.HeadJson, branch.Name);
                }
                session = new ProjectSession(projectId, row.Value.Name, ProjectFile.Deserialize(modelJson), db, log, () => _sessions.TryRemove(key, out _))
                {
                    BranchId = branchId,
                    BranchName = branchName ?? Branching.MainBranch,
                    Events = events,
                };
                session.InitRules(await LoadRulesAsync(projectId));
                _sessions[key] = session;
            }
            // Package permissions come from the store, not from the tab: the project role (a view-only tab stays view-only) and teams.
            var role = await db.RoleOfAsync(projectId, me.User.Id) ?? (me.CanEdit ? ProjectRole.Editor : ProjectRole.Viewer);
            if (!me.CanEdit) role = ProjectRole.Viewer;
            var teams = session.Rules.Any(r => r.PrincipalKind == Core.Permissions.PrincipalKind.Team) ? await db.UserTeamIdsAsync(me.User.Id) : [];
            session.Join(me, client, role, teams);
            return session;
        }
        finally { _openGate.Release(); }
    }

    private async Task<IReadOnlyList<Core.Permissions.PermissionRule>> LoadRulesAsync(Guid projectId) =>
        (await db.ListPackagePermissionsAsync(projectId)).Select(r => r.Rule).ToList();

    /// <summary>
    /// Re-reads a project's package permissions and applies them to every live session of it (main and branches), so they take
    /// effect at once for everyone connected.
    /// </summary>
    public async Task ReloadPermissionsAsync(Guid projectId)
    {
        var rules = await LoadRulesAsync(projectId);
        foreach (var s in SessionsOf(projectId).ToList()) s.UpdateRules(rules);
    }

    /// <summary>Flushes every session (used at shutdown).</summary>
    public async Task FlushAllAsync()
    {
        foreach (var s in _sessions.Values) await s.PersistAsync();
    }

    public bool IsOpen(Guid projectId) => _sessions.ContainsKey((projectId, null));

    /// <summary>The live session of a project's main line.</summary>
    public ProjectSession? Find(Guid projectId) => _sessions.GetValueOrDefault((projectId, null));

    /// <summary>The live session of one branch (null = main).</summary>
    public ProjectSession? Find(Guid projectId, Guid? branchId) => _sessions.GetValueOrDefault((projectId, branchId));

    /// <summary>Every live session of a project (main and branches).</summary>
    public IEnumerable<ProjectSession> SessionsOf(Guid projectId) => _sessions.Where(kv => kv.Key.Project == projectId).Select(kv => kv.Value);
}

/// <summary>What a session needs from a connected browser tab. Calls are made while the session lock is held, so
/// implementations must only queue work (e.g. Blazor <c>InvokeAsync</c>), never block.</summary>
public interface ISessionClient
{
    void RemoteOp(ModelOp op);
    void PresenceChanged();
    void Reset(string modelJson);
    void CommentsChanged();

    /// <summary>The whole model was replaced (merge, or an edit rejected by an element lock); <paramref name="message"/> explains why.</summary>
    void Replaced(string modelJson, string message) => Reset(modelJson);
}

public sealed partial class ProjectSession : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (Presence Presence, ISessionClient Client)> _participants = new();
    private readonly IMeridianStore _db;
    private readonly ILogger _log;
    private readonly Action _onEmpty;
    private readonly Timer _persistTimer;
    private ModelRepository _master;
    private bool _dirty;
    private Guid? _lastEditor;

    internal ProjectSession(Guid id, string name, ModelRepository master, IMeridianStore db, ILogger log, Action onEmpty)
    {
        ProjectId = id;
        Name = name;
        _master = master;
        _db = db;
        _log = log;
        _onEmpty = onEmpty;
        _persistTimer = new Timer(_ => _ = PersistIfDueAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public Guid ProjectId { get; }
    /// <summary>Webhook event sink (integrations); null when not configured.</summary>
    internal Integrations.ProjectEvents? Events { get; init; }
    public string Name { get; set; }
    /// <summary>The branch this session edits; null for the project's main line.</summary>
    public Guid? BranchId { get; init; }
    public string BranchName { get; init; } = Branching.MainBranch;
    public long Sequence { get; private set; }
    public DateTime? LastPersisted { get; private set; }

    public IReadOnlyList<Presence> Participants
    {
        get { lock (_gate) return _participants.Values.Select(p => p.Presence).ToList(); }
    }

    public string Snapshot()
    {
        lock (_gate) return ProjectFile.Serialize(_master);
    }

    internal void Join(Presence me, ISessionClient client, ProjectRole role = ProjectRole.Editor, IReadOnlyList<Guid>? teams = null)
    {
        lock (_gate)
        {
            _participants[me.ParticipantId] = (me, client);
            RegisterAccess(me.ParticipantId, me.User.Id, me.CanEdit ? role : ProjectRole.Viewer, teams ?? []);   // ProjectSession.Permissions.cs
            BroadcastPresence();
        }
    }

    public void Leave(string participantId)
    {
        bool empty;
        lock (_gate)
        {
            _participants.Remove(participantId);
            ForgetAccess(participantId);     // package permissions (ProjectSession.Permissions.cs)
            ReleaseLocksOf(participantId);   // element locks end when their holder leaves
            LeaveTexts(participantId);       // and so does their place in shared SysML v2 texts
            BroadcastPresence();
            empty = _participants.Count == 0;
        }
        if (empty)
        {
            _ = PersistAsync().ContinueWith(_ =>
            {
                lock (_gate) if (_participants.Count > 0) return;
                _onEmpty();
                Dispose();
            });
        }
    }

    /// <summary>Applies a participant's operation to the authoritative model and relays it to everyone else, in order.</summary>
    public void Submit(string participantId, Guid userId, ModelOp op)
    {
        lock (_gate)
        {
            if (!AllowEdit(participantId, userId, op)) return;   // role, package permissions and element locks (ProjectSession.Locks.cs)
            ApplyLocked(participantId, userId, op);
        }
    }

    /// <summary>
    /// Applies the operations of one user action (e.g. a delete with its cascade) all or nothing: each is checked first, and if
    /// any is refused none is applied and the sender is resynchronised (with the reason).
    /// </summary>
    public void SubmitAll(string participantId, Guid userId, IReadOnlyList<ModelOp> ops)
    {
        lock (_gate)
        {
            foreach (var op in ops)
                if (!AllowEdit(participantId, userId, op)) return;
            foreach (var op in ops) ApplyLocked(participantId, userId, op);
        }
    }

    /// <summary>Applies an allowed operation and relays it (under the session lock).</summary>
    private void ApplyLocked(string participantId, Guid userId, ModelOp op, bool events = true)
    {
        var probes = ProbeFiltered(participantId, op);       // what participants with hidden packages may receive (ProjectSession.Permissions.cs)
        _master.ApplyRemote(op);
        ScheduleTextRefresh();   // shared SysML v2 texts follow the model (ProjectSession.SharedText.cs)
        if (events) Events?.OperationApplied(ProjectId, Name, userId, op, _master);
        Sequence++;
        MarkDirty();
        _lastEditor = userId;
        foreach (var (id, p) in _participants)
            if (id != participantId) Relay(id, p.Client, op, probes);
    }

    public void UpdatePresence(string participantId, string? selectedId, string? diagramId)
    {
        lock (_gate)
        {
            if (!_participants.TryGetValue(participantId, out var p)) return;
            if (p.Presence.SelectedId == selectedId && p.Presence.DiagramId == diagramId) return;
            _participants[participantId] = (p.Presence with { SelectedId = selectedId, DiagramId = diagramId }, p.Client);
            BroadcastPresence();
        }
    }

    /// <summary>
    /// Replaces the whole model (version restore) for everyone. The user must be connected to this session and may only restore
    /// changes to packages they can edit (<see cref="PermissionDeniedException"/> otherwise).
    /// </summary>
    public void Reset(string modelJson, Guid userId)
    {
        var restored = ProjectFile.Deserialize(modelJson);
        lock (_gate)
        {
            if (_rules.Count > 0)
            {
                var view = ViewOfUser(userId) ?? throw new PermissionDeniedException("Open the project before restoring a version.");
                if (Core.Permissions.PermissionCheck.CheckReplace(_master, restored, view) is { } why) throw new PermissionDeniedException(why);
            }
            _master = restored;
            ScheduleTextRefresh();
            Sequence++;
            MarkDirty();
            _lastEditor = userId;
            _stale.Clear();
            foreach (var (id, p) in _participants) p.Client.Reset(SnapshotLocked(id, modelJson));   // filtered per participant
        }
    }

    /// <summary>Tells integrations (webhooks) that a version was committed.</summary>
    public void NotifyVersionCommitted(int number, string message, UserAccount? author) => Events?.VersionCommitted(ProjectId, Name, number, message, author);

    public void NotifyComments()
    {
        Events?.CommentsChanged(ProjectId, Name);
        lock (_gate)
            foreach (var p in _participants.Values) p.Client.CommentsChanged();
    }

    private void BroadcastPresence()
    {
        foreach (var p in _participants.Values) p.Client.PresenceChanged();
    }

    // ---------------------------------------------------------------- persistence (batched)
    //
    // Operations are never serialized one by one: they mark the session dirty, and the whole model is written at most once per
    // burst of edits — after editing pauses for QuietPeriod, or after MaxStaleness of continuous editing — and never more often
    // than ~10× the time the last save took (so a huge model doesn't spend its time serializing). Explicit flushes (leave,
    // shutdown, version commits) still persist immediately.

    internal static TimeSpan QuietPeriod = TimeSpan.FromSeconds(2);
    internal static TimeSpan MaxStaleness = TimeSpan.FromSeconds(10);

    private DateTime _firstDirtyAt, _lastOpAt;
    private TimeSpan _lastSaveCost;
    private DateTime _lastSaveAt;
    private int _persisting;

    private void MarkDirty()
    {
        var now = DateTime.UtcNow;
        if (!_dirty) _firstDirtyAt = now;
        _lastOpAt = now;
        _dirty = true;
    }

    /// <summary>The timer's check: persist when the edit burst has settled (or has gone on too long).</summary>
    internal Task PersistIfDueAsync()
    {
        lock (_gate)
        {
            if (!_dirty) return Task.CompletedTask;
            var now = DateTime.UtcNow;
            var settled = now - _lastOpAt >= QuietPeriod || now - _firstDirtyAt >= MaxStaleness;
            var throttled = now - _lastSaveAt < _lastSaveCost * 10;
            if (!settled || throttled) return Task.CompletedTask;
        }
        return PersistAsync();
    }

    public async Task PersistAsync()
    {
        // One save at a time; a tick that overlaps a running save simply waits for the next one.
        if (Interlocked.Exchange(ref _persisting, 1) == 1)
        {
            while (Volatile.Read(ref _persisting) == 1) await Task.Delay(20);
            if (Interlocked.Exchange(ref _persisting, 1) == 1) return;
        }
        try
        {
            string json;
            Guid? editor;
            var started = DateTime.UtcNow;
            lock (_gate)
            {
                if (!_dirty) return;
                json = ProjectFile.Serialize(_master);
                editor = _lastEditor;
                _dirty = false;
            }
            try
            {
                if (BranchId is { } branch) await _db.SaveBranchAsync(branch, json, editor);
                else await _db.SaveProjectAsync(ProjectId, json, editor);
                LastPersisted = DateTime.Now;
                _lastSaveAt = DateTime.UtcNow;
                _lastSaveCost = _lastSaveAt - started;
            }
            catch (Exception ex)
            {
                lock (_gate) if (!_dirty) { _dirty = true; _firstDirtyAt = DateTime.MinValue; }   // retry on the next tick
                _log.LogError(ex, "Persisting project {Project} failed", ProjectId);
            }
        }
        finally { Volatile.Write(ref _persisting, 0); }
    }

    public void Dispose()
    {
        _persistTimer.Dispose();
        _staleTimer?.Dispose();
        _textRefresh?.Dispose();
    }
}
