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
public sealed partial class Workspace : IDisposable, ISessionClient
{
    private readonly List<DiagramController> _open = new();

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

    /// <summary>"Show variant" on diagrams (VariantView.cs) and verification results (VerificationView.cs).</summary>
    public VariantView VariantView => _variantView ??= new(this);
    public VerificationView Verification => _verification ??= new(this);
    private VariantView? _variantView;
    private VerificationView? _verification;

    /// <summary>Elements copied with ⌘C (pasted as duplicates with ⌘V).</summary>
    public List<Element> Clipboard { get; } = new();

    /// <summary>The element currently being dragged from the containment tree.</summary>
    public Element? Dragging { get; set; }

    public event Action? Changed;

    // ---------------------------------------------------------------- identity & collaboration

    public UserAccount? User { get; set; }
    public ProjectSession? Session { get; private set; }
    public ProjectRole Role { get; private set; } = ProjectRole.Owner;
    /// <summary>View-only role, or a historical view of a used project at its pinned version (<see cref="Pinned"/>).</summary>
    public bool ReadOnly => Role == ProjectRole.Viewer || Pinned != null;

    /// <summary>The signed-in user is a server administrator (team servers; set when the workbench starts, re-checked before scripts run).</summary>
    public bool IsServerAdmin { get; set; }
    public string ParticipantId { get; } = Guid.NewGuid().ToString("N");
    public int CommentsVersion { get; private set; }

    /// <summary>Marshals work onto this circuit's synchronization context (set by the root component).</summary>
    public Func<Action, Task> Dispatch { get; set; } = a => { a(); return Task.CompletedTask; };

    /// <summary>Imports or refreshes from a saved database connection (set by the workbench; used by Integrations ▸ Databases).</summary>
    public Func<Meridian.Data.IntegrationConnection, Task>? DatabaseImport { get; set; }

    public IReadOnlyList<Presence> Others =>
        Session?.Participants.Where(p => p.ParticipantId != ParticipantId).ToList() ?? (IReadOnlyList<Presence>)[];

    // ---------------------------------------------------------------- tabs across a rebuilt model

    /// <summary>The open tabs, the active one, the selection and the back/forward history (see <see cref="CaptureTabs"/>).</summary>
    private sealed record TabSnapshot(IReadOnlyList<DiagramController> Diagrams, string? ActiveDiagramId, IReadOnlyList<ViewTab> Views, ViewTab? ActiveView,
                                      string? SelectedId, IReadOnlyList<string> Back, IReadOnlyList<string> Forward);

    /// <summary>Remembers the tabs before another model is installed (a library update, a merge, a resync or a version restore).</summary>
    private TabSnapshot CaptureTabs() =>
        new(_open.ToList(), Active?.Diagram.Id, _views.ToList(), ActiveView, Selected?.Id, _back.Select(d => d.Id).ToList(), _forward.Select(d => d.Id).ToList());

    /// <summary>
    /// Re-opens the remembered tabs in the newly installed model: diagrams that still exist keep their tab, place, zoom, scroll
    /// and selection; the active tab stays active (or its neighbour when it is gone); view tabs of saved elements and the
    /// code-change and architecture reviews stay open. With no tab left, the default diagrams open.
    /// </summary>
    private void RestoreTabs(TabSnapshot tabs)
    {
        foreach (var old in tabs.Diagrams)
        {
            if (Model.Find(old.Diagram.Id) is not Diagram d || _open.Any(c => ReferenceEquals(c.Diagram, d))) continue;
            var c = new DiagramController(this, d);
            c.TakeViewFrom(old);
            _open.Add(c);
        }
        Active = _open.FirstOrDefault(c => c.Diagram.Id == tabs.ActiveDiagramId);
        if (Active == null && tabs.ActiveDiagramId != null && _open.Count > 0)
        {
            var at = tabs.Diagrams.ToList().FindIndex(c => c.Diagram.Id == tabs.ActiveDiagramId);
            Active = _open[Math.Clamp(at - 1, 0, _open.Count - 1)];
        }
        foreach (var v in tabs.Views)
        {
            if (v.Element is { } el)
            {
                if (Model.Find(el.Id) is not { } now) continue;
                var tab = new ViewTab(v.Kind) { Element = now, State = v.State };
                _views.Add(tab);
                if (ReferenceEquals(tabs.ActiveView, v)) ActiveView = tab;
            }
            else if (v.Kind is ViewTabKind.CodeChanges or ViewTabKind.Architecture)
            {
                _views.Add(v);   // reviews hold no model objects: the same tab carries on
                if (ReferenceEquals(tabs.ActiveView, v)) ActiveView = v;
            }
        }
        _back.AddRange(tabs.Back.Select(Model.Find).OfType<Diagram>());
        _forward.AddRange(tabs.Forward.Select(Model.Find).OfType<Diagram>());
        if (_open.Count == 0 && _views.Count == 0) OpenDefaultDiagrams();
        if (Model.Find(tabs.SelectedId) is { } sel) Select(sel, syncCanvas: false);
        SharePresence();
        Bump();
    }

    // ---------------------------------------------------------------- branches & element locks

    /// <summary>The branch being edited (null = main).</summary>
    public Guid? BranchId => Session?.BranchId;
    public string BranchName => Session?.BranchName ?? Meridian.Data.Branching.MainBranch;

    // ---------------------------------------------------------------- package permissions (docs/PERMISSIONS.md)

    /// <summary>My package permissions in the open project (everything without a session: local previews, tests).</summary>
    public Meridian.Core.Permissions.AccessView Access => Session?.AccessOf(ParticipantId) ?? Meridian.Core.Permissions.AccessView.Full;

    /// <summary>True when I may change <paramref name="e"/> (role and package permissions; the session enforces the same rules).</summary>
    public bool CanEdit(Element e) => !ReadOnly && Access.CanEdit(e) && !Model.IsLibraryElement(e);   // used projects are read-only (Workspace.Libraries.cs)

    /// <summary>True when the diagram is read-only for me (view-only project, or a diagram owned by a package I can't edit).</summary>
    public bool IsReadOnly(Diagram d) => ReadOnly || !Access.CanEdit(d) || Model.IsLibraryElement(d);

    /// <summary>Some packages' contents are hidden from me: my model is a filtered copy.</summary>
    public bool HasHiddenPackages => Access.HidesAnything;

    /// <summary>
    /// The lock badge of a package in the tree: hidden or read-only for me, or (for those who manage it) protected by rules.
    /// Null when the package has no rule that matters here.
    /// </summary>
    public (string Css, string Tip)? PermissionBadge(Element e)
    {
        if (e.Kind is not (ElementKind.Package or ElementKind.Model) || Session is not { } s) return null;
        var mine = Access.ExplicitAt(e.Id);
        if (mine == Meridian.Core.Permissions.PackageAccess.None) return ("none", "Hidden from you: you can see this package, not its contents");
        if (mine == Meridian.Core.Permissions.PackageAccess.Read) return ("read", "Read-only for you (package permissions)");
        var rules = s.Rules;
        if (rules.Count == 0) return null;
        var count = rules.Count(r => r.ElementId == e.Id);
        if (count == 0) return null;
        if (Access.CanManage(e)) return ("managed", $"Protected: {count} permission rule{(count == 1 ? "" : "s")} (right-click ▸ Permissions…)");
        return Access.CanEdit(e) ? null : ("read", "Read-only for you (package permissions)");
    }

    /// <summary>A model loaded from history (versions, baselines, reviews) as I may see it.</summary>
    public ModelRepository RedactForMe(ModelRepository model) => PackagePermissions.Redact(model, Access);

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
        IEnumerable<Diagram> own = Model.HasLibraries ? Model.Diagrams.Where(d => !Model.IsLibraryElement(d)).ToList() : Model.Diagrams;   // not a used project's
        if (own.FirstOrDefault(d => d.DiagramType == DiagramType.Requirement) is { } req) Open(req, recordHistory: false);
        var bdd = own.FirstOrDefault(d => d.DiagramType == DiagramType.BlockDefinition) ?? own.FirstOrDefault();
        if (bdd != null) Open(bdd, recordHistory: false);
        var top = Model.OfKind(ElementKind.Block).FirstOrDefault(b => b.Name == "Vehicle") ?? bdd?.Nodes.FirstOrDefault()?.Element;
        if (top != null) Select(top, syncCanvas: false);   // shown in the inspector, not highlighted on the canvas
    }

    // ---------------------------------------------------------------- UI state

    /// <summary>Mirrors <see cref="Settings.ThemeService.IsDark"/> (set by it); read the ThemeService in new code.</summary>
    public bool DarkMode { get; set; } = true;
    /// <summary>The user's preferences (set by <see cref="Settings.ThemeService"/>): diagram defaults, snapping, tabs.</summary>
    public Settings.UserPreferences Preferences { get; set; } = new();
    /// <summary>Asks the user to confirm (title, message); set by the workbench. Null: no confirmation possible.</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }
    /// <summary>Raised when the favourites change (they are saved with the user's preferences).</summary>
    public event Action? FavoritesChanged;
    public HashSet<string> Favorites { get; } = new();
    private readonly List<Diagram> _back = new();
    private readonly List<Diagram> _forward = new();
    public bool CanGoBack => _back.Any(Model.Contains);
    public bool CanGoForward => _forward.Any(Model.Contains);

    public void ToggleFavorite(Element e)
    {
        if (!Favorites.Remove(e.Id)) Favorites.Add(e.Id);
        FavoritesChanged?.Invoke();
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
            // "Open new diagrams as tabs" off: the new diagram takes the active tab's place.
            if (!Preferences.OpenDiagramsAsTabs && Active != null && _open.IndexOf(Active) is var at and >= 0) _open[at] = c;
            else _open.Add(c);
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
        // While my unconfirmed edits are rewound and re-applied (a rebase), removals are transient: tabs and selection are
        // checked once it is over (Workspace.Collaboration.cs).
        if (Model.IsRebasing)
        {
            _cleanupDue = true;
            return;
        }
        if (change.Kind == ChangeKind.Removed) CleanUpRemoved();
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

    // ---------------------------------------------------------------- code-sync review highlights

    /// <summary>Code changes waiting for review (set by <see cref="Meridian.Web.CodeSync.CodeSyncState"/>); null when there are none.</summary>
    public CodeChangeOverlay? CodeChanges { get; private set; }

    /// <summary>Draw the code-change highlights on diagrams (on by default on a code-sync review branch).</summary>
    public bool HighlightCodeChanges { get; private set; }

    public void SetCodeChanges(CodeChangeOverlay? overlay)
    {
        CodeChanges = overlay;
        Bump();
    }

    public void SetHighlightCodeChanges(bool on)
    {
        if (HighlightCodeChanges == on) return;
        HighlightCodeChanges = on;
        Bump();
    }

    /// <summary>Architecture violations drawn on diagrams as red dashed edges (set by
    /// <see cref="Meridian.Web.CodeSync.Conformance.ArchitectureState"/>); null when hidden or there are none.</summary>
    public IReadOnlyList<Meridian.Serialization.ViolationEdge>? ArchitectureViolations { get; private set; }

    public void SetArchitectureViolations(IReadOnlyList<Meridian.Serialization.ViolationEdge>? edges)
    {
        ArchitectureViolations = edges;
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
        Model.History.ActionCompleted -= FlushLocalOps;
        Model.OpEmitted -= OnLocalOp;
    }
}

/// <summary>What a view tab shows.</summary>
public enum ViewTabKind { Table, Query, ReportTemplate, CodeChanges, Architecture, Verification, Instances }

/// <summary>A non-diagram tab: a generic table, a model query, or a report template being edited.</summary>
public sealed class ViewTab(ViewTabKind kind)
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public ViewTabKind Kind { get; } = kind;
    /// <summary>The saved model element (TableView, QueryView, ValidationRule, report template), or null while unsaved.</summary>
    public Element? Element { get; set; }
    /// <summary>Per-tab state owned by the view (e.g. the template being edited).</summary>
    public object? State { get; set; }

    public string Title => Kind == ViewTabKind.Instances && Element is { } c ? $"{c.DisplayName} instances" : Element is { } e ? e.DisplayName : Kind switch
    {
        ViewTabKind.Table => "New generic table",
        ViewTabKind.Query => "New query",
        ViewTabKind.CodeChanges => "Code changes",
        ViewTabKind.Architecture => "Architecture",
        ViewTabKind.Verification => "Verification",
        _ => State is Meridian.Serialization.Reports.DocTemplate t && t.Name.Length > 0 ? t.Name : "Report template",
    };
}

/// <summary>
/// What the diagrams highlight during a code-sync review: element id → how it changed, and (on the review branch) main's
/// model, whose diagrams give the last place of removed elements (drawn as ghosts).
/// </summary>
public sealed record CodeChangeOverlay(IReadOnlyDictionary<string, Meridian.Serialization.ChangeHighlight> Kinds, ModelRepository? GhostSource, string Label = "code changes")
{
    public int Count(Meridian.Serialization.ChangeHighlight k) => Kinds.Values.Count(v => v == k);
}
