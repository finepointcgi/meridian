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
    private readonly ConcurrentDictionary<Guid, ProjectSession> _sessions = new();
    private readonly SemaphoreSlim _openGate = new(1, 1);

    public async Task<ProjectSession?> JoinAsync(Guid projectId, Presence me, ISessionClient client)
    {
        await _openGate.WaitAsync();
        try
        {
            if (!_sessions.TryGetValue(projectId, out var session))
            {
                var row = await db.LoadProjectAsync(projectId);
                if (row is null) return null;
                session = new ProjectSession(projectId, row.Value.Name, ProjectFile.Deserialize(row.Value.ModelJson), db, log, () => _sessions.TryRemove(projectId, out _)) { Events = events };
                _sessions[projectId] = session;
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

    public bool IsOpen(Guid projectId) => _sessions.ContainsKey(projectId);

    public ProjectSession? Find(Guid projectId) => _sessions.GetValueOrDefault(projectId);
}

/// <summary>What a session needs from a connected browser tab. Calls are made while the session lock is held, so
/// implementations must only queue work (e.g. Blazor <c>InvokeAsync</c>), never block.</summary>
public interface ISessionClient
{
    void RemoteOp(ModelOp op);
    void PresenceChanged();
    void Reset(string modelJson);
    void CommentsChanged();
}

public sealed class ProjectSession : IDisposable
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
            await _db.SaveProjectAsync(ProjectId, json, editor);
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
