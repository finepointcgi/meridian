using Meridian.Core.Model;
using Meridian.Core.Validation;
using Meridian.Data;
using Meridian.Serialization;

namespace Meridian.Web.Services;

/// <summary>
/// Per-circuit (per browser tab) editing session: the signed-in user, the replica of the open project's model,
/// selection, open diagrams and log. It is also the collaboration client for the open project: local operations
/// are submitted to the shared <see cref="ProjectSession"/>, and everyone else's are applied here in order.
/// Components subscribe to <see cref="Changed"/> and re-render.
/// </summary>
public sealed class Workspace : IDisposable, ISessionClient
{
    private readonly List<DiagramController> _open = new();
    private readonly List<ModelOp> _buffered = new();
    private bool _joining;

    public Workspace()
    {
        Model = new ModelRepository("No project");
        Model.Changed += OnModelChanged;
        Model.History.Changed += OnHistoryChanged;
    }

    public ModelRepository Model { get; private set; }
    public Element? Selected { get; private set; }
    public IReadOnlyList<DiagramController> OpenDiagrams => _open;
    public DiagramController? Active { get; private set; }
    public List<string> Output { get; } = new();
    public string Status { get; private set; } = "Ready";
    public int Version { get; private set; }

    /// <summary>Elements highlighted by a running simulation (current tokens / active states).</summary>
    public HashSet<string> SimHighlight { get; } = new();

    /// <summary>Elements copied with ⌘C (pasted as duplicates with ⌘V).</summary>
    public List<Element> Clipboard { get; } = new();

    /// <summary>The element currently being dragged from the containment tree.</summary>
    public Element? Dragging { get; set; }

    public event Action? Changed;

    // ---------------------------------------------------------------- identity & collaboration

    public UserAccount? User { get; set; }
    public ProjectSession? Session { get; private set; }
    public ProjectRole Role { get; private set; } = ProjectRole.Owner;
    public bool ReadOnly => Role == ProjectRole.Viewer;
    public string ParticipantId { get; } = Guid.NewGuid().ToString("N");
    public int CommentsVersion { get; private set; }

    /// <summary>Marshals work onto this circuit's synchronization context (set by the root component).</summary>
    public Func<Action, Task> Dispatch { get; set; } = a => { a(); return Task.CompletedTask; };

    public IReadOnlyList<Presence> Others =>
        Session?.Participants.Where(p => p.ParticipantId != ParticipantId).ToList() ?? (IReadOnlyList<Presence>)[];

    public async Task<bool> JoinAsync(CollaborationHub hub, Guid projectId, ProjectRole role, Guid? branchId = null)
    {
        if (User is null) return false;
        LeaveSession();
        _joining = true;
        _buffered.Clear();
        var session = await hub.JoinAsync(projectId, new Presence(ParticipantId, User, null, null), this, branchId);
        if (session is null) { _joining = false; return false; }
        var replica = ProjectFile.Deserialize(session.Snapshot());
        Session = session;
        Role = role;
        Install(replica);
        // Operations that arrived between registration and snapshot install, in order.
        foreach (var op in _buffered) Model.ApplyRemote(op);
        _buffered.Clear();
        _joining = false;
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

    private void Install(ModelRepository replica)
    {
        Model.Changed -= OnModelChanged;
        Model.History.Changed -= OnHistoryChanged;
        Model.OpEmitted -= OnLocalOp;
        Model = replica;
        Model.History.ReadOnly = ReadOnly;
        Model.Changed += OnModelChanged;
        Model.History.Changed += OnHistoryChanged;
        Model.OpEmitted += OnLocalOp;
        _open.Clear();
        _back.Clear();
        _forward.Clear();
        Active = null;
        Selected = null;
    }

    private void OnLocalOp(ModelOp op) => Session?.Submit(ParticipantId, User?.Id ?? Guid.Empty, op);

    public void LeaveSession()
    {
        Session?.Leave(ParticipantId);
        Session = null;
    }

    void ISessionClient.RemoteOp(ModelOp op) => _ = Dispatch(() =>
    {
        if (_joining) { _buffered.Add(op); return; }
        Model.ApplyRemote(op);
    });

    void ISessionClient.PresenceChanged() => _ = Dispatch(Bump);

    void ISessionClient.Reset(string modelJson) => _ = Dispatch(() =>
    {
        var activeId = Active?.Diagram.Id;
        Install(ProjectFile.Deserialize(modelJson));
        if (activeId != null && Model.Find(activeId) is Diagram d) Open(d, recordHistory: false);
        else OpenDefaultDiagrams();
        SetStatus("The model was restored to an earlier version");
    });

    void ISessionClient.CommentsChanged() => _ = Dispatch(() => { CommentsVersion++; Bump(); });

    void ISessionClient.Replaced(string modelJson, string message) => _ = Dispatch(() =>
    {
        var activeId = Active?.Diagram.Id;
        var selectedId = Selected?.Id;
        Install(ProjectFile.Deserialize(modelJson));
        Session?.Resynced(ParticipantId);
        if (activeId != null && Model.Find(activeId) is Diagram d) Open(d, recordHistory: false);
        else OpenDefaultDiagrams();
        if (Model.Find(selectedId) is { } sel) Select(sel);
        Log(message);
        SetStatus(message);
    });

    // ---------------------------------------------------------------- branches & element locks

    /// <summary>The branch being edited (null = main).</summary>
    public Guid? BranchId => Session?.BranchId;
    public string BranchName => Session?.BranchName ?? Meridian.Data.Branching.MainBranch;

    /// <summary>The lock covering an element (held by anyone, including me), if any.</summary>
    public ElementLock? LockOf(Element e)
    {
        if (Session is null) return null;
        var locks = Session.Locks;
        if (locks.Count == 0) return null;
        foreach (var a in e.AncestorsAndSelf())
            if (locks.FirstOrDefault(l => l.ElementId == a.Id && (l.Subtree || ReferenceEquals(a, e))) is { } l) return l;
        return null;
    }

    /// <summary>True when someone else's lock stops me editing <paramref name="e"/>.</summary>
    public bool IsLockedForMe(Element e) => User != null && LockOf(e) is { } l && l.User.Id != User.Id;

    private void SharePresence() => Session?.UpdatePresence(ParticipantId, Selected?.Id, Active?.Diagram.Id);

    /// <summary>Opens the requirement diagram and the BDD (active), and selects the top-level block.</summary>
    public void OpenDefaultDiagrams()
    {
        if (Model.Diagrams.FirstOrDefault(d => d.DiagramType == DiagramType.Requirement) is { } req) Open(req, recordHistory: false);
        var bdd = Model.Diagrams.FirstOrDefault(d => d.DiagramType == DiagramType.BlockDefinition) ?? Model.Diagrams.FirstOrDefault();
        if (bdd != null) Open(bdd, recordHistory: false);
        var top = Model.OfKind(ElementKind.Block).FirstOrDefault(b => b.Name == "Vehicle") ?? bdd?.Nodes.FirstOrDefault()?.Element;
        if (top != null) Select(top);
    }

    // ---------------------------------------------------------------- UI state

    public bool DarkMode { get; set; } = true;
    public HashSet<string> Favorites { get; } = new();
    private readonly List<Diagram> _back = new();
    private readonly List<Diagram> _forward = new();
    public bool CanGoBack => _back.Any(Model.Contains);
    public bool CanGoForward => _forward.Any(Model.Contains);

    public void ToggleFavorite(Element e)
    {
        if (!Favorites.Remove(e.Id)) Favorites.Add(e.Id);
        Bump();
    }

    public IEnumerable<Element> FavoriteElements => Favorites.Select(Model.Find).OfType<Element>();

    public void GoBack()
    {
        while (_back.Count > 0)
        {
            var d = _back[^1];
            _back.RemoveAt(_back.Count - 1);
            if (!Model.Contains(d)) continue;
            if (Active != null) _forward.Add(Active.Diagram);
            Open(d, recordHistory: false);
            return;
        }
    }

    public void GoForward()
    {
        while (_forward.Count > 0)
        {
            var d = _forward[^1];
            _forward.RemoveAt(_forward.Count - 1);
            if (!Model.Contains(d)) continue;
            if (Active != null) _back.Add(Active.Diagram);
            Open(d, recordHistory: false);
            return;
        }
    }

    /// <summary>Drill down into the sub-diagram owned by an element (Cameo-style). Returns false if it owns none.</summary>
    public bool DrillDown(Element e)
    {
        var d = e as Diagram ?? e.OwnedElements.OfType<Diagram>().FirstOrDefault()
                ?? (e.Kind == ElementKind.Action ? Model.Find(e.Get(FieldKeys.Behavior))?.OwnedElements.OfType<Diagram>().FirstOrDefault() : null);
        if (d == null) return false;
        Open(d);
        return true;
    }

    // ---------------------------------------------------------------- model health (cached per version)

    private int _healthVersion = -1;
    private (int Errors, int Warnings, int Infos) _health;
    private DateTime _healthAt;

    public (int Errors, int Warnings, int Infos) Health
    {
        get
        {
            if (_healthVersion == Version) return _health;
            // Validation is O(model); while editing, refresh it at most every 1.5 s.
            if (_healthVersion >= 0 && (DateTime.UtcNow - _healthAt).TotalSeconds < 1.5) return _health;
            _healthAt = DateTime.UtcNow;
            var issues = new ModelValidator().Validate(Model);
            _health = (issues.Count(i => i.Severity == Severity.Error), issues.Count(i => i.Severity == Severity.Warning), issues.Count(i => i.Severity == Severity.Info));
            _healthVersion = Version;
            return _health;
        }
    }

    public void Select(Element? e, bool syncCanvas = true)
    {
        Selected = e != null && Model.Contains(e) ? e : null;
        if (syncCanvas) Active?.Select(Selected);
        SharePresence();
        Bump();
    }

    public DiagramController Open(Diagram d, bool recordHistory = true)
    {
        if (recordHistory && Active != null && !ReferenceEquals(Active.Diagram, d))
        {
            _back.Add(Active.Diagram);
            _forward.Clear();
        }
        var c = _open.FirstOrDefault(x => ReferenceEquals(x.Diagram, d));
        if (c == null)
        {
            c = new DiagramController(this, d);
            _open.Add(c);
        }
        Active = c;
        SharePresence();
        Bump();
        return c;
    }

    public void Close(DiagramController c)
    {
        var i = _open.IndexOf(c);
        _open.Remove(c);
        if (ReferenceEquals(Active, c)) Active = _open.Count == 0 ? null : _open[Math.Clamp(i - 1, 0, _open.Count - 1)];
        SharePresence();
        Bump();
    }

    public void Activate(DiagramController c)
    {
        Active = c;
        SharePresence();
        Bump();
    }

    public void SetStatus(string text)
    {
        Status = text;
        Bump();
    }

    public void Log(string text)
    {
        Output.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
        if (Output.Count > 2000) Output.RemoveRange(0, 500);
    }

    public void Bump()
    {
        Version++;
        Changed?.Invoke();
    }

    private void OnModelChanged(object? sender, ModelChange change)
    {
        if (change.Kind == ChangeKind.Removed)
        {
            if (Selected != null && !Model.Contains(Selected)) Selected = null;
            foreach (var c in _open.Where(c => !Model.Contains(c.Diagram)).ToList()) Close(c);
            foreach (var c in _open) c.ClearInvalidSelection();
        }
        if (change.Kind == ChangeKind.DiagramChanged) foreach (var c in _open) c.ClearInvalidSelection();
        Bump();
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Bump();

    public void Dispose()
    {
        LeaveSession();
        Model.Changed -= OnModelChanged;
        Model.History.Changed -= OnHistoryChanged;
        Model.OpEmitted -= OnLocalOp;
    }
}
