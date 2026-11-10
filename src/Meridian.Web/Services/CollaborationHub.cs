using System.Collections.Concurrent;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;

namespace Meridian.Web.Services;

public sealed record Presence(string ParticipantId, UserAccount User, string? SelectedId, string? DiagramId);

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
                _sessions[key] = session;
            }
            session.Join(me, client);
            return session;
        }
        finally { _openGate.Release(); }
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
        _persistTimer = new Timer(_ => _ = PersistAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
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

    internal void Join(Presence me, ISessionClient client)
    {
        lock (_gate)
        {
            _participants[me.ParticipantId] = (me, client);
            BroadcastPresence();
        }
    }

    public void Leave(string participantId)
    {
        bool empty;
        lock (_gate)
        {
            _participants.Remove(participantId);
            ReleaseLocksOf(participantId);   // element locks end when their holder leaves
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
            if (!AllowEdit(participantId, userId, op)) return;   // element locks (ProjectSession.Locks.cs)
            _master.ApplyRemote(op);
            Events?.OperationApplied(ProjectId, Name, userId, op, _master);
            Sequence++;
            _dirty = true;
            _lastEditor = userId;
            foreach (var (id, p) in _participants)
                if (id != participantId) p.Client.RemoteOp(op);
        }
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

    /// <summary>Replaces the whole model (version restore) for everyone.</summary>
    public void Reset(string modelJson, Guid userId)
    {
        lock (_gate)
        {
            _master = ProjectFile.Deserialize(modelJson);
            Sequence++;
            _dirty = true;
            _lastEditor = userId;
            foreach (var p in _participants.Values) p.Client.Reset(modelJson);
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

    public async Task PersistAsync()
    {
        string json;
        Guid? editor;
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
        }
        catch (Exception ex)
        {
            lock (_gate) _dirty = true;   // retry on the next tick
            _log.LogError(ex, "Persisting project {Project} failed", ProjectId);
        }
    }

    public void Dispose() => _persistTimer.Dispose();
}
