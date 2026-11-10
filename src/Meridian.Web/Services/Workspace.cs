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

    /// <summary>The signed-in user is a server administrator (team servers; set when the workbench starts, re-checked before scripts run).</summary>
    public bool IsServerAdmin { get; set; }
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
        var session = await hub.JoinAsync(projectId, new Presence(ParticipantId, User, null, null, CanEdit: role != ProjectRole.Viewer), this, branchId);
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
        ModelVersion++;
        TreeVersion++;
        _open.Clear();
        _views.Clear();
        ActiveView = null;
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
    private ModelRepository? _healthModel;
    private (int Errors, int Warnings, int Infos) _health;
    private DateTime _healthAt;

    /// <summary>
    /// Issue counts for the status bar. Cached against <see cref="ModelVersion"/> (selection and other UI-only changes never
    /// re-validate); while the model is being edited it refreshes at most every 1.5 s. Only counts are needed, so the rules
    /// run without the sorting <see cref="ModelValidator.Validate"/> does for the issue list.
    /// </summary>
    public (int Errors, int Warnings, int Infos) Health
    {
        get
        {
            var sameModel = ReferenceEquals(_healthModel, Model);
            if (sameModel && _healthVersion == ModelVersion) return _health;
            // Validation is O(model); while editing, refresh it at most every 1.5 s.
            if (sameModel && _healthVersion >= 0 && (DateTime.UtcNow - _healthAt).TotalSeconds < 1.5) return _health;
            _healthAt = DateTime.UtcNow;
            int errors = 0, warnings = 0, infos = 0;
            foreach (var rule in new ModelValidator().Rules)
            foreach (var issue in rule.Check(Model))
                switch (issue.Severity)
                {
                    case Severity.Error: errors++; break;
                    case Severity.Warning: warnings++; break;
                    default: infos++; break;
                }
            _health = (errors, warnings, infos);
            _healthVersion = ModelVersion;
            _healthModel = Model;
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
        ActiveView = null;
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
        ActiveView = null;
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

    /// <summary>
    /// Bumped by every change to model content (elements, fields, ownership; not diagram layout) and when another model is
    /// installed. Panels cache derived data (traceability rows, table rows, tree rows) against it, so selection changes and
    /// other UI-only bumps of <see cref="Version"/> don't recompute them.
    /// </summary>
    public int ModelVersion { get; private set; }

    /// <summary>
    /// Bumped when the containment tree's shape or sort keys change: elements added, removed or moved, a name or requirement
    /// id edited, or another model installed. The model tree rebuilds its rows only when this moves.
    /// </summary>
    public int TreeVersion { get; private set; }

    private void OnModelChanged(object? sender, ModelChange change)
    {
        if (change.Kind != ChangeKind.DiagramChanged) ModelVersion++;
        if (change.Kind is ChangeKind.Added or ChangeKind.Removed or ChangeKind.Moved or ChangeKind.Reset
            || change is { Kind: ChangeKind.Updated, Field: FieldKeys.Name or FieldKeys.RequirementId })
            TreeVersion++;
        if (change.Kind == ChangeKind.Removed)
        {
            if (Selected != null && !Model.Contains(Selected)) Selected = null;
            foreach (var c in _open.Where(c => !Model.Contains(c.Diagram)).ToList()) Close(c);
            foreach (var v in _views.Where(v => v.Element != null && !Model.Contains(v.Element)).ToList()) CloseView(v);
            foreach (var c in _open) c.ClearInvalidSelection();
        }
        if (change.Kind == ChangeKind.DiagramChanged) foreach (var c in _open) c.ClearInvalidSelection();
        if (change is { Kind: ChangeKind.Added, Element: Diagram created } && !Model.IsApplyingRemote) QueueCreatedDiagram(created);
        Bump();
    }

    // ---------------------------------------------------------------- view tabs (tables, queries, report templates)

    private readonly List<ViewTab> _views = new();

    /// <summary>Non-diagram tabs in the centre tab strip, after the diagrams.</summary>
    public IReadOnlyList<ViewTab> OpenViews => _views;

    /// <summary>The view tab shown in the centre, or null when the active diagram is shown.</summary>
    public ViewTab? ActiveView { get; private set; }

    /// <summary>
    /// Opens (or brings forward) a view tab. A saved element opens once; an unsaved view of a kind reuses the open unsaved
    /// tab of that kind unless <paramref name="forceNew"/> is set.
    /// </summary>
    public ViewTab OpenView(ViewTabKind kind, Element? element = null, object? state = null, bool forceNew = false)
    {
        var tab = element != null
            ? _views.FirstOrDefault(v => ReferenceEquals(v.Element, element))
            : forceNew || state != null ? null : _views.FirstOrDefault(v => v.Kind == kind && v.Element == null);
        if (tab == null)
        {
            tab = new ViewTab(kind) { Element = element, State = state };
            _views.Add(tab);
        }
        ActiveView = tab;
        Bump();
        return tab;
    }

    public void ActivateView(ViewTab tab)
    {
        if (!_views.Contains(tab)) return;
        ActiveView = tab;
        Bump();
    }

    public void CloseView(ViewTab tab)
    {
        var i = _views.IndexOf(tab);
        if (i < 0) return;
        _views.RemoveAt(i);
        if (ReferenceEquals(ActiveView, tab)) ActiveView = _views.Count == 0 ? null : _views[Math.Clamp(i - 1, 0, _views.Count - 1)];
        Bump();
    }

    // ---------------------------------------------------------------- new diagrams open as tabs

    /// <summary>Most diagrams one action may create and still all open as tabs; beyond this (imports), only the first opens.</summary>
    public const int AutoOpenLimit = 3;

    private readonly List<Diagram> _created = new();

    /// <summary>
    /// A diagram made here (any command, template, import, script, the assistant, SysML v2 text): open it as a tab once the
    /// current action has finished, so its content is in place and callers that already opened it aren't disturbed.
    /// Diagrams made by collaborators don't open on this screen.
    /// </summary>
    private void QueueCreatedDiagram(Diagram d)
    {
        _created.Add(d);
        if (_created.Count > 1) return;   // a flush is already scheduled for this action
        if (SynchronizationContext.Current == null) { FlushCreatedDiagrams(); return; }   // tests / no UI thread: now
        FlushLater();
    }

    private async void FlushLater()
    {
        await Task.Yield();   // after the current synchronous action, on the circuit's thread
        FlushCreatedDiagrams();
    }

    private void FlushCreatedDiagrams()
    {
        var created = _created.Where(Model.Contains).ToList();
        _created.Clear();
        if (created.Count == 0) return;
        var toOpen = created.Count <= AutoOpenLimit ? created : created.Take(1).ToList();
        var already = Active;
        foreach (var d in toOpen) if (!_open.Any(c => ReferenceEquals(c.Diagram, d))) Open(d);
        // Keep a tab the action chose to show; otherwise show the newest diagram.
        if (already != null && toOpen.Any(d => ReferenceEquals(d, already.Diagram))) Activate(already);
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

/// <summary>What a view tab shows.</summary>
public enum ViewTabKind { Table, Query, ReportTemplate }

/// <summary>A non-diagram tab: a generic table, a model query, or a report template being edited.</summary>
public sealed class ViewTab(ViewTabKind kind)
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public ViewTabKind Kind { get; } = kind;
    /// <summary>The saved model element (TableView, QueryView, ValidationRule, report template), or null while unsaved.</summary>
    public Element? Element { get; set; }
    /// <summary>Per-tab state owned by the view (e.g. the template being edited).</summary>
    public object? State { get; set; }

    public string Title => Element is { } e ? e.DisplayName : Kind switch
    {
        ViewTabKind.Table => "New table",
        ViewTabKind.Query => "New query",
        _ => State is Meridian.Serialization.Reports.DocTemplate t && t.Name.Length > 0 ? t.Name : "Report template",
    };
}
