using System.IO.Compression;
using System.Text;
using Meridian.Core.Analysis;
using Meridian.Core.CodeGen;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Meridian.Serialization;
using Meridian.Serialization.Mermaid;
using Meridian.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>The application shell: owns layout, menus and every command; child panels call back into it.</summary>
public partial class Workbench
{
    private const long MaxUpload = 64 * 1024 * 1024;

    [Inject] private Workspace Ws { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IDialogService Dialogs { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IMeridianStore Db { get; set; } = default!;
    [Inject] private CollaborationHub Hub { get; set; } = default!;
    [Inject] private AuthenticationStateProvider Auth { get; set; } = default!;
    [Inject] private MeridianHostInfo Host { get; set; } = default!;
    [Inject] private Meridian.Web.Admin.IAuditLog Audit { get; set; } = default!;
    private bool _desktop;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IServiceProvider Services { get; set; } = default!;

    private ModelTree? _tree;
    private CommandPalette? _palette;
    private OnboardingTour? _tour;

    /// <summary>Everything the ⌘K palette can run: commands, open diagrams, go to elements, create diagrams.</summary>
    private IEnumerable<CommandPalette.Command> PaletteCommands()
    {
        CommandPalette.Command C(string title, string icon, Action a) => new(title, "Command", icon, () => { a(); return Task.CompletedTask; });
        CommandPalette.Command CA(string title, string icon, Func<Task> a) => new(title, "Command", icon, a);
        var list = new List<CommandPalette.Command>
        {
            CA("Open project…", Icons.Material.Outlined.FolderOpen, OpenProjects),
            CA("New project…", Icons.Material.Outlined.NoteAdd, NewModel),
            CA("Commit version…", Icons.Material.Outlined.Commit, Save),
            CA("Version history", Icons.Material.Outlined.History, ShowHistory),
            CA("Share & members", Icons.Material.Outlined.Group, ShareProject),
            C("Validate model", Icons.Material.Outlined.FactCheck, Validate),
            C("Requirement coverage", Icons.Material.Outlined.DonutLarge, Coverage),
            C("Traceability matrix", Icons.Material.Outlined.GridOn, () => OpenDock(2)),
            C("Requirements table", Icons.Material.Outlined.TableRows, () => OpenDock(1)),
            C("Simulate", Icons.Material.Outlined.PlayCircle, () => OpenDock(5)),
            C("Parametrics", Icons.Material.Outlined.Functions, () => OpenDock(6)),
            C("Variants, configurations & trade studies", Icons.Material.Outlined.Balance, () => OpenDock(11)),
            C("Impact analysis of selection", Icons.Material.Outlined.CallSplit, ImpactOfSelection),
            C("Toggle light / dark mode", Icons.Material.Outlined.DarkMode, ToggleTheme),
            C("Arrange active diagram", Icons.Material.Outlined.AutoAwesomeMosaic, () => Ws.Active?.Arrange()),
            C("Zoom to fit", Icons.Material.Outlined.FitScreen, () => Ws.Active?.ZoomToFit()),
            C("Fit diagram frames to contents", Icons.Material.Outlined.FitScreen, () => Ws.Active?.FitFrameToContents()),
            CA("Open HTML report", Icons.Material.Outlined.Article, OpenReport),
            CA("Generate document from template (PDF / Word / HTML)", Icons.Material.Outlined.Description, ExportDocx),
            CA("Export XMI", Icons.Material.Outlined.SwapHoriz, ExportXmi),
            CA("Export SysML v2 text", Icons.Material.Outlined.Code, ExportSysML),
            C("Edit as SysML v2 text", Icons.Material.Outlined.EditNote, EditAsSysml),
            CA("Export ReqIF", Icons.Material.Outlined.Assignment, ExportReqIf),
            CA("Export ArchiMate exchange file", Icons.Material.Outlined.Layers, ExportArchiMate),
            CA("Generate C# code", Icons.Material.Outlined.DataObject, GenerateCode),
            C("Add UAF profile", Icons.Material.Outlined.Badge, AddUafProfile),
            CA("New diagram from template…", Icons.Material.Outlined.AddChart, NewDiagramFromTemplate),
            CA("Export diagram as PNG…", Icons.Material.Outlined.Image, ExportImage),
            CA("Copy diagram as image", Icons.Material.Outlined.ContentCopy, CopyDiagramImage),
            CA("Take the guided tour", Icons.Material.Outlined.Tour, StartTour),
            CA("Import into this project (BPMN, ArchiMate, SQL, C#, Mermaid, MagicDraw re-import)…", Icons.Material.Outlined.Input, () => ImportInto("file-into")),
            CA("Import ReqIF…", Icons.Material.Outlined.UploadFile, ImportReqIfIntoSelection),
            CA("Request review of selection…", Icons.Material.Outlined.RateReview, () => RequestReview()),
            C("Reviews", Icons.Material.Outlined.Checklist, () => OpenDock(ReviewsDock)),
        };
        list.AddRange(RequirementPaletteCommands());
        list.AddRange(ScriptingPaletteCommands());
        list.AddRange(AssistantPaletteCommands());
        list.AddRange(SettingsPaletteCommands());
        list.AddRange(AutoDiagramPaletteCommands());
        list.AddRange(DerivationPaletteCommands());   // Workbench.Derivation.cs
        list.AddRange(CodeSyncPaletteCommands());
        list.AddRange(MermaidPaletteCommands());
        list.AddRange(OnboardingPaletteCommands());   // Workbench.Onboarding.cs
        foreach (var t in Enum.GetValues<DiagramType>())
        {
            var type = t;
            list.Add(new($"New {Metamodel.DisplayName(type)}", "Command", UiKinds.DiagramIcon(type), () => { NewDiagramSmart(type); return Task.CompletedTask; }));
        }
        list.AddRange(TablesPaletteCommands());
        foreach (var d in Ws.Model.Diagrams.OrderBy(d => d.Name))
        {
            var diagram = d;
            list.Add(new($"{diagram.Name}  ·  {Metamodel.DiagramAbbreviation(diagram.DiagramType)}", "Diagram", UiKinds.DiagramIcon(diagram.DiagramType), () => { Ws.Open(diagram); return Task.CompletedTask; }));
        }
        foreach (var e in Ws.Model.AllElements.Where(e => e is not Relationship && e is not Diagram && e.Kind != ElementKind.Model && !string.IsNullOrEmpty(e.Name)).OrderBy(e => e.Name))
        {
            var el = e;
            list.Add(new(el.Kind == ElementKind.Requirement ? $"{el.Get(FieldKeys.RequirementId)} {el.Name}" : el.Name, Metamodel.DisplayName(el.Kind), UiKinds.Icon(el.Kind), () => { Navigate(el); return Task.CompletedTask; }));
        }
        return list;
    }
    private ValidationPanel? _validation;
    private ElementReference _leftSplitter, _rightSplitter, _bottomSplitter;
    private DotNetObjectReference<Workbench>? _self;
    private IReadOnlyList<MenuEntry>? _menu;
    private double _menuX, _menuY;
    private int _dock;
    private bool _dockOpen;
    private Element _csvTarget = default!;

    protected override async Task OnInitializedAsync()
    {
        Ws.Dispatch = a => InvokeAsync(a);
        Ws.Confirm = ConfirmAsync;
        Ws.Changed += OnWorkspaceChanged;
        var user = (await Auth.GetAuthenticationStateAsync()).User;
        if (Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            Ws.User = new UserAccount(id, user.Identity?.Name ?? "", user.FindFirstValue("display_name") ?? user.Identity?.Name ?? "", user.FindFirstValue("color") ?? "#4f8cff");
        if (Services.GetService(typeof(Meridian.Web.Admin.AdminAccess)) is Meridian.Web.Admin.AdminAccess access)
            Ws.IsServerAdmin = await access.IsAdminAsync(user);
        _csvTarget = Ws.Model.Root;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        _self = DotNetObjectReference.Create(this);
        await JS.InvokeVoidAsync("meridian.installShortcuts", _self);
        await JS.InvokeVoidAsync("meridianSettings.installShortcut", _self);   // ⌘, / Ctrl+, opens Settings
        await JS.InvokeVoidAsync("meridian.splitter", _leftSplitter, "--left", "left", 180, 640);
        await JS.InvokeVoidAsync("meridian.splitter", _rightSplitter, "--right", "right", 240, 640);
        await JS.InvokeVoidAsync("meridian.splitter", _bottomSplitter, "--bottom", "bottom", 90, 700);
        if (await JS.InvokeAsync<bool>("meridian.isDesktop")) { _desktop = true; StateHasChanged(); }
        if (Ws.Session != null || Ws.User is null) return;

        // Reopen the project this browser last worked on, else the most recent one, else offer the project list.
        var projects = await Db.ListProjectsAsync(Ws.User.Id);
        var last = Theme.Prefs.LastProjectId?.ToString() ?? await JS.InvokeAsync<string?>("meridian.getLast");   // follows the user across devices
        var deep = Integrations.DeepLink.Parse(Nav.Uri);   // ?project=…&element=… links from issue trackers / OSLC
        var pick = projects.FirstOrDefault(p => p.Id == deep?.Project) ?? projects.FirstOrDefault(p => p.Id.ToString() == last) ?? projects.FirstOrDefault();
        if (pick != null) await OpenProject(pick.Id);
        else await OpenProjects();
        if (deep is not null && Ws.Session?.ProjectId == deep.Value.Project) await OpenDeepLinkView();   // &branch=…&panel=code-changes
        if (deep?.Element is { } deepElement && Ws.Model.Find(deepElement) is { } target) Navigate(target);
        StateHasChanged();
    }

    private void OnWorkspaceChanged() => _ = InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        Ws.Changed -= OnWorkspaceChanged;
        _self?.Dispose();
    }

    // ============================================================ projects (PostgreSQL) & collaboration

    public async Task OpenProject(Guid projectId)
    {
        if (Ws.User is null) return;
        var role = await Db.RoleOfAsync(projectId, Ws.User.Id);
        if (role is null) { Snackbar.Add("You don't have access to that project.", Severity.Warning); return; }
        if (!await Ws.JoinAsync(Hub, projectId, role.Value)) { Snackbar.Add("That project no longer exists.", Severity.Warning); return; }
        _csvTarget = Ws.Model.Root;
        await JS.InvokeVoidAsync("meridian.setLast", projectId.ToString());
        if (Theme.Prefs.LastProjectId != projectId) await Theme.UpdateAsync(p => p.LastProjectId = projectId);
        Ws.Log($"Opened “{Ws.Session!.Name}” as {role.Value.ToString().ToLowerInvariant()}");
        Ws.SetStatus(role == ProjectRole.Viewer ? "View-only: you can browse, comment and export, but not edit." : $"Opened {Ws.Session.Name}");
    }

    /// <summary>Creates and opens a new project from a model (used by panels, e.g. resolved variants).</summary>
    public Task<Guid?> CreateProjectFromModel(ModelRepository model, string name) => CreateProjectFrom(model, name);

    /// <summary>Offers a text file for download (used by panels' exports).</summary>
    public Task DownloadTextFile(string fileName, string mime, string text) => DownloadText(fileName, mime, text);

    private async Task<Guid?> CreateProjectFrom(ModelRepository model, string name)
    {
        if (Ws.User is null) return null;
        var id = await Db.CreateProjectAsync(name, ProjectFile.Serialize(model), Ws.User.Id);
        await Audit.RecordAsync(Meridian.Web.Admin.AuditActions.ProjectCreate, Ws.User, id, name, target: name);
        await OpenProject(id);
        return id;
    }

    private async Task OpenProjects()
    {
        var dialog = await Dialogs.ShowAsync<ProjectsDialog>("Projects", new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        var result = await dialog.Result;
        switch (result?.Data)
        {
            case Guid id: await OpenProject(id); break;
            case "__new__": await NewModel(); break;
            case "__sample__": await OpenSample(); break;
            case "__upload__": await PickFile("file-open"); break;
        }
    }

    private async Task ShareProject()
    {
        if (Ws.Session is null) return;
        await Dialogs.ShowAsync<MembersDialog>("Share", new DialogParameters<MembersDialog> { { x => x.ProjectId, Ws.Session.ProjectId } },
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
    }

    private async Task ShowHistory()
    {
        if (Ws.Session is null) return;
        var dialog = await Dialogs.ShowAsync<HistoryDialog>("Version history", new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        switch ((await dialog.Result)?.Data)
        {
            case HistoryDialog.BranchFrom b: await NewBranch(b.VersionId); break;
            case "__baselines__": await ShowBaselines(); break;
        }
    }

    private async Task DownloadProject()
    {
        var name = BaseName + ProjectFile.Extension;
        await DownloadText(name, "application/json", ProjectFile.Serialize(Ws.Model));
    }

    private void SignOut() => Nav.NavigateTo("/account/logout", forceLoad: true);

    /// <summary>Desktop app only: back to the start screen to pick local mode or another server.</summary>
    private async Task SwitchWorkspace()
    {
        await JS.InvokeVoidAsync("meridian.desktop", "switch");
    }

    [JSInvokable]
    public async Task OnShortcut(string name)
    {
        switch (name)
        {
            case "Save" or "SaveAs": await Save(); break;
            case "Open": await OpenProjects(); break;
            case "Find": await FocusSearch(); break;
            case "Palette": if (_palette != null) await _palette.Show(); break;
            case "Settings": await OpenSettings(); break;
            case "Undo": Undo(); break;
            case "Redo": Redo(); break;
            case "F7": Validate(); break;
        }
        StateHasChanged();
    }

    private string Counts()
    {
        var m = Ws.Model;
        var els = m.AllElements.Count(e => e is not Relationship && e is not Diagram) - 1;
        return $"{els} elements · {m.Relationships.Count()} relationships · {m.Diagrams.Count()} diagrams";
    }

    // ============================================================ navigation & shared commands (used by child panels)

    public void Navigate(Element e)
    {
        if (!Ws.Model.Contains(e)) return;
        Ws.Select(e);
    }

    /// <summary>Double-click in the tree: open diagrams, otherwise show the element's owned diagram if any.</summary>
    public void Activate(Element e)
    {
        if (e is Diagram d) { Ws.Open(d); return; }
        if (e.Kind is ElementKind.TableView or ElementKind.QueryView or ElementKind.ValidationRule) { OpenSavedView(e); return; }
        if (Meridian.Serialization.Reports.ReportTemplates.TryLoad(e, out var template)) { Ws.OpenView(ViewTabKind.ReportTemplate, e, template); return; }
        var owned = e.OwnedElements.OfType<Diagram>().FirstOrDefault();
        if (owned != null) Ws.Open(owned);
    }

    public void CreateElement(Element owner, ElementKind kind)
    {
        try
        {
            var el = Ws.Model.Create(owner, kind);
            if (kind == ElementKind.Comment) Ws.Model.Set(el, FieldKeys.Body, "Comment");
            Ws.Select(el);
        }
        catch (InvalidOperationException ex) { Ws.SetStatus(ex.Message); }
    }

    public void CreateDiagram(Element owner, DiagramType type)
    {
        try
        {
            Diagram d;
            using (Ws.Model.History.Transaction($"New {Metamodel.DisplayName(type)}"))
            {
                d = Ws.Model.CreateDiagram(owner, type);
                ApplyDiagramDefaults(d);   // Settings ▸ Diagrams
                var c = Ws.Open(d);
                // A new IBD shows the context block's parts and ports, as Cameo does.
                if (type == DiagramType.InternalBlock)
                    foreach (var p in owner.OwnedElements.Where(x => x.Kind is ElementKind.Property or ElementKind.Port)) c.Show(p);
            }
            Ws.Select(d, syncCanvas: false);
        }
        catch (InvalidOperationException ex) { Ws.SetStatus(ex.Message); }
    }

    public async Task Rename(Element e)
    {
        var key = e.Kind == ElementKind.Comment ? FieldKeys.Body : FieldKeys.Name;
        var name = await Prompt("Rename", $"New name for {UiKinds.KindName(e)}", e.Get(key), e.Kind == ElementKind.Comment);
        if (name != null && Ws.Model.Contains(e)) Ws.Model.Set(e, key, name.Trim());
    }

    public async Task Delete(Element e)
    {
        if (e.Owner == null) return;
        var owned = e.Descendants().Count();
        var rels = Ws.Model.RelationshipsOf(e).Count;
        if (owned > 0 || rels > 2 || Ws.Preferences.ConfirmDeleteFromModel)
        {
            var ok = await Dialogs.ShowMessageBox("Delete", $"Delete {e.DisplayName}? This also deletes {owned} owned elements, {rels} relationships and every diagram view of them. You can undo this.", yesText: "Delete", cancelText: "Cancel");
            if (ok != true) return;
        }
        Ws.Model.Delete(e);
        Ws.SetStatus($"Deleted {e.DisplayName}");
    }

    public void ImpactAnalysis(Element e)
    {
        var impact = Traceability.Impact(Ws.Model, e);
        Ws.Log($"Impact analysis for {e.QualifiedName}: {impact.Count} dependent elements");
        Ws.Select(e);
        OpenDock(4);
        Ws.Bump();
    }

    public void ShowOnActiveDiagram(Element e)
    {
        if (Ws.Active is not { } c) return;
        try
        {
            if (e is Relationship r) { if (Ws.Model.AddEdge(c.Diagram, r) == null) Ws.SetStatus("Show both ends of the relationship first"); }
            else c.Show(e);
        }
        catch (InvalidOperationException ex) { Ws.SetStatus(ex.Message); }
    }

    // ============================================================ context menus

    private void OpenMenu(IReadOnlyList<MenuEntry> entries, double x, double y)
    {
        _menu = entries;
        _menuX = x;
        _menuY = Math.Min(y, 2000);
        StateHasChanged();
    }

    public void ShowTreeMenu(Element e, double x, double y)
    {
        var m = new List<MenuEntry>();
        var groups = new (string, ElementKind[])[]
        {
            ("New structure", [ElementKind.Package, ElementKind.Block, ElementKind.InterfaceBlock, ElementKind.ValueType, ElementKind.ConstraintBlock, ElementKind.Class, ElementKind.Interface, ElementKind.DataType, ElementKind.Enumeration, ElementKind.Component]),
            ("New feature", [ElementKind.Property, ElementKind.Port, ElementKind.Operation, ElementKind.EnumerationLiteral]),
            ("New requirement", [ElementKind.Requirement, ElementKind.TestCase]),
            ("New profile", [ElementKind.Profile, ElementKind.Stereotype, ElementKind.TagDefinition]),
            ("New data model", [ElementKind.Table, ElementKind.Column]),
            ("New behavior", [ElementKind.Actor, ElementKind.UseCase, ElementKind.Activity, ElementKind.StateMachine, ElementKind.Interaction, ElementKind.Lifeline, ElementKind.Partition, ElementKind.Action, ElementKind.State, ElementKind.InitialNode, ElementKind.ActivityFinalNode, ElementKind.DecisionNode, ElementKind.InitialState, ElementKind.FinalState]),
        };
        foreach (var (title, kinds) in groups)
        {
            var allowed = kinds.Where(k => Metamodel.CanOwn(e.Kind, k)).ToList();
            if (allowed.Count == 0) continue;
            m.Add(MenuEntry.Title(title));
            foreach (var k in allowed) m.Add(MenuEntry.Do(Metamodel.DisplayName(k), () => CreateElement(e, k)));
        }
        var diagrams = Enum.GetValues<DiagramType>().Where(t => Metamodel.IsValidDiagramContext(t, e.Kind)).ToList();
        if (diagrams.Count > 0)
        {
            m.Add(MenuEntry.Title("New diagram"));
            foreach (var t in diagrams) m.Add(MenuEntry.Do(Metamodel.DisplayName(t), () => CreateDiagram(e, t)));
        }
        m.Add(MenuEntry.Sep());
        if (e is Diagram d) m.Add(MenuEntry.Do("Open diagram", () => Ws.Open(d)));
        if (Ws.Active is { } active && e is not Diagram && e.Kind != ElementKind.Model)
            m.Add(MenuEntry.Do($"Show on “{active.Diagram.Name}”", () => ShowOnActiveDiagram(e)));
        m.Add(MenuEntry.Async("Rename…", () => Rename(e), "F2"));
        if (e.Owner != null) m.Add(MenuEntry.Async("Delete", () => Delete(e), "⌦"));
        m.AddRange(LockEntries(e));   // element locking (Workbench.VersionControl.cs)
        m.Add(MenuEntry.Sep());
        m.AddRange(AutoDiagramTreeEntries(e));   // Workbench.AutoDiagram.cs
        m.AddRange(DerivationMenuEntries(e));    // Workbench.Derivation.cs
        m.Add(MenuEntry.Do("Impact analysis", () => ImpactAnalysis(e)));
        if (e.Owner != null) m.Add(MenuEntry.Async("Request review…", () => RequestReview([e])));
        if (e.Kind is ElementKind.Package or ElementKind.Model)
            m.Add(MenuEntry.Async("Import requirements CSV here…", () => { _csvTarget = e; return PickFile("file-csv"); }));
        if (e is not Diagram && e.Descendants().OfType<Diagram>().Any())
            m.Add(MenuEntry.Async("Export diagrams as images (zip)…", () => ExportImages(e)));
        OpenMenu(m, x, y);
    }

    private void ShowCanvasMenu((double X, double Y, DiagramNode? Node, DiagramEdge? Edge) r)
    {
        if (Ws.Active is not { } c) return;
        // Right-click on part of a multi-selection: actions on the whole selection.
        if (c.SelectionCount > 1 && (r.Node != null || r.Edge != null)) { OpenMenu(MultiSelectionMenu(c), r.X, r.Y); return; }
        var m = new List<MenuEntry>();
        if (r.Node is { } n)
        {
            var el = n.Element;
            m.Add(MenuEntry.Do("Rename", () => c.Editing = n, "F2"));
            m.Add(MenuEntry.Do("Select in containment tree", () => Navigate(el)));
            m.Add(MenuEntry.Do("Display related elements", () => c.DisplayRelated(n)));
            if (c.Diagram.DiagramType is DiagramType.InternalBlock or DiagramType.Parametric && el.Kind == ElementKind.Property)
            {
                m.Add(MenuEntry.Do("Show ports on border", () => c.ShowPortsOf(n)));
                m.Add(MenuEntry.Do("Show nested parts", () => c.ShowNestedParts(n)));
            }
            if (el.Kind == ElementKind.Action && !Ws.ReadOnly)
            {
                m.Add(MenuEntry.Do("Add input pin", () => AddPin(c, n, ElementKind.InputPin)));
                m.Add(MenuEntry.Do("Add output pin", () => AddPin(c, n, ElementKind.OutputPin)));
            }
            var features = new[] { ElementKind.Property, ElementKind.Port, ElementKind.Operation, ElementKind.EnumerationLiteral }.Where(k => Metamodel.CanOwn(el.Kind, k)).ToList();
            if (features.Count > 0)
            {
                m.Add(MenuEntry.Title("Add feature"));
                foreach (var k in features) m.Add(MenuEntry.Do(Metamodel.DisplayName(k), () => CreateElement(el, k)));
            }
            var owned = el.OwnedElements.OfType<Diagram>().ToList();
            var newTypes = Enum.GetValues<DiagramType>().Where(t => t is DiagramType.InternalBlock or DiagramType.Activity or DiagramType.StateMachine or DiagramType.Sequence && Metamodel.IsValidDiagramContext(t, el.Kind)).ToList();
            if (owned.Count > 0 || newTypes.Count > 0) m.Add(MenuEntry.Title("Diagrams"));
            foreach (var od in owned) m.Add(MenuEntry.Do($"Open “{od.Name}”", () => Ws.Open(od)));
            foreach (var t in newTypes) m.Add(MenuEntry.Do($"New {Metamodel.DisplayName(t)}", () => CreateDiagram(el, t)));
            m.AddRange(DerivationMenuEntries(el) is { Count: > 0 } derive ? derive : DerivationMenuEntries(c.Diagram));   // Workbench.Derivation.cs
            m.Add(MenuEntry.Sep());
        }
        if (r.Edge is { } edge)
        {
            m.Add(MenuEntry.Do("Select in containment tree", () => Navigate(edge.Relationship)));
            m.Add(MenuEntry.Do("Straighten path", c.StraightenSelectedEdge));
            m.Add(MenuEntry.Sep());
        }
        if (r.Node != null)
        {
            m.Add(MenuEntry.Do("Duplicate", c.DuplicateSelection, "⌘D"));
            m.Add(MenuEntry.Do("Copy", c.CopySelection, "⌘C"));
            if (!Ws.ReadOnly) m.Add(MenuEntry.Async(c.SelectionLink() == null ? "Add link…" : "Edit link…", () => LinkDialog.EditAsync(Dialogs, Ws, c)));
            if (c.SelectionLink() is { } lnk) m.Add(MenuEntry.Async("Follow link", () => DiagramLinks.FollowAsync(Ws, JS, lnk)));
        }
        if (r.Node != null || r.Edge != null)
        {
            m.Add(MenuEntry.Do("Remove from diagram", c.RemoveSelectionFromDiagram, "⌫"));
            m.Add(MenuEntry.Do("Delete from model", c.DeleteSelectionFromModel, "⌦"));
        }
        else
        {
            if (Ws.Clipboard.Any(Ws.Model.Contains)) m.Add(MenuEntry.Do($"Paste {Ws.Clipboard.Count} element{(Ws.Clipboard.Count == 1 ? "" : "s")}", c.Paste, "⌘V"));
            m.Add(MenuEntry.Do("Select all", c.SelectAll, "⌘A"));
            m.Add(MenuEntry.Do("Arrange", c.Arrange));
            if (c.Diagram.DiagramType == DiagramType.Activity) m.Add(MenuEntry.Do("Allocate actions to swimlanes", () => SyncAllocations(c)));
            m.AddRange(DerivationMenuEntries(c.Diagram));   // Workbench.Derivation.cs
            m.Add(MenuEntry.Do("Zoom to fit", c.ZoomToFit));
            m.Add(MenuEntry.Async("Export as SVG", ExportSvg));
            m.Add(MenuEntry.Async("Export as PNG…", ExportImage));
            m.Add(MenuEntry.Async("Copy diagram as image", CopyDiagramImage));
        }
        OpenMenu(m, r.X, r.Y);
    }

    private void AddPin(DiagramController c, DiagramNode action, ElementKind kind)
    {
        try
        {
            using (Ws.Model.History.Transaction($"Add {Metamodel.DisplayName(kind)}"))
            {
                var pin = Ws.Model.Create(action.Element, kind);
                var b = action.Bounds;
                var count = action.Element.OwnedElements.Count(x => x.Kind == kind);
                var x = kind == ElementKind.InputPin ? b.X : b.Right;
                var y = b.Y + Math.Min(b.Height - 8, 14 + (count - 1) * 20);
                Ws.Model.AddNode(c.Diagram, pin, Notation.SnapToBorder(new Bounds(x - 8, y - 8, 16, 16), b), parent: action);
                Ws.Select(pin);
            }
        }
        catch (InvalidOperationException ex) { Ws.SetStatus(ex.Message); }
    }

    private void SyncAllocations(DiagramController c)
    {
        try
        {
            var (created, removed) = Allocation.SyncFromSwimlanes(Ws.Model, c.Diagram);
            Ws.SetStatus(created + removed == 0 ? "Allocations already match the swimlanes (set each swimlane's Represents first)" : $"Allocations: {created} added, {removed} removed");
        }
        catch (InvalidOperationException ex) { Ws.SetStatus(ex.Message); }
    }

    // ============================================================ edit / model menu

    private void Undo() { Ws.Model.History.Undo(); Ws.Active?.ClearInvalidSelection(); }
    private void Redo() { Ws.Model.History.Redo(); Ws.Active?.ClearInvalidSelection(); }
    private Task RenameSelected() => Ws.Selected is { } e ? Rename(e) : Task.CompletedTask;
    private Task DeleteSelected() => Ws.Selected is { } e ? Delete(e) : Task.CompletedTask;
    private Task FocusSearch() => _tree?.FocusSearch() ?? Task.CompletedTask;
    private void ImpactOfSelection() { if (Ws.Selected is { } e) ImpactAnalysis(e); }

    private void Validate()
    {
        OpenDock(0);
        _validation?.Run();
        var h = Ws.Health;
        Ws.SetStatus(h.Errors == 0 ? $"Validation passed — {h.Warnings} warnings, {h.Infos} notes" : $"Validation found {h.Errors} errors");
    }

    private readonly SysmlTextPanel.EditorState _sysml = new();

    /// <summary>Opens the SysML v2 text editor on the selection's package (dock 12).</summary>
    private void EditAsSysml()
    {
        _sysml.RequestScope(Ws.Selected?.AncestorsAndSelf().FirstOrDefault(a => a.Kind is ElementKind.Package or ElementKind.Model));
        OpenDock(12);
    }

    private void OpenDock(int index)
    {
        _dock = index;
        _dockOpen = true;
    }

    private void ToggleDock(int index)
    {
        if (_dockOpen && _dock == index) _dockOpen = false;
        else OpenDock(index);
    }

    /// <summary>The title-bar sun/moon: a quick light/dark switch that updates the Appearance setting.</summary>
    private void ToggleTheme() => _ = Theme.ToggleDarkAsync();

    private string SavedText()
    {
        if (Ws.Session is null) return "No project open";
        if (Ws.Session.LastPersisted is not { } t) return "Synced";
        var ago = DateTime.Now - t;
        return ago.TotalMinutes < 1 ? "Saved just now" : ago.TotalHours < 1 ? $"Saved {(int)ago.TotalMinutes} min ago" : $"Saved {t:HH:mm}";
    }

    /// <summary>Breadcrumb click: open the element's own diagram if it has one, otherwise select it.</summary>
    private void Crumb(Element e)
    {
        var current = Ws.Active?.Diagram;
        var d = e.OwnedElements.OfType<Diagram>().FirstOrDefault(x => !ReferenceEquals(x, current));
        if (d != null) Ws.Open(d);
        else Ws.Select(e);
    }

    /// <summary>Toolbar "Create": new element in the selection's namespace, shown on the active diagram if its palette allows it.</summary>
    private void CreateFromRibbon(ElementKind kind)
    {
        var context = Ws.Selected ?? Ws.Active?.Diagram.Owner ?? Ws.Model.Root;
        if (kind == ElementKind.Requirement && context.Kind != ElementKind.Requirement)
            context = Ws.Model.Root.OwnedElements.FirstOrDefault(p => p.Kind == ElementKind.Package && p.Name.Contains("Requirement", StringComparison.OrdinalIgnoreCase)) ?? context;
        var owner = Ws.Model.DefaultOwnerFor(context, kind);
        try
        {
            Element el;
            using (Ws.Model.History.Transaction($"Create {Metamodel.DisplayName(kind)}"))
            {
                el = Ws.Model.Create(owner, kind);
                if (Ws.Active is { } c && Metamodel.PaletteNodes(c.Diagram.DiagramType).Contains(kind))
                {
                    c.Show(el);
                    c.Editing = c.Diagram.NodeFor(el);
                }
            }
            Ws.Select(el);
            Ws.SetStatus($"Created {Metamodel.DisplayName(kind)} in {owner.DisplayName}");
        }
        catch (InvalidOperationException ex) { Ws.SetStatus(ex.Message); }
    }

    /// <summary>
    /// Creates a diagram in the most sensible context for the selection: IBDs inside the selected block,
    /// activity / state machine diagrams inside a (new) behavior owned by the selected block, everything else in the
    /// selection's package.
    /// </summary>
    private void NewDiagramSmart(DiagramType type)
    {
        var context = Ws.Selected ?? Ws.Active?.Diagram.Owner ?? Ws.Model.Root;
        if (context is Diagram dg) context = dg.Owner ?? Ws.Model.Root;
        if (context is Relationship rel) context = rel.Owner ?? Ws.Model.Root;
        try
        {
            Element owner;
            switch (type)
            {
                case DiagramType.InternalBlock or DiagramType.Parametric:
                    owner = context.AncestorsAndSelf().FirstOrDefault(a => Metamodel.IsValidDiagramContext(type, a.Kind))
                            ?? throw new InvalidOperationException("Select a block first — an internal block diagram shows a block's parts and ports.");
                    break;
                case DiagramType.Activity or DiagramType.StateMachine or DiagramType.Sequence
                    or DiagramType.Timing or DiagramType.Communication or DiagramType.InteractionOverview:
                {
                    var behaviorKind = type switch { DiagramType.Activity or DiagramType.InteractionOverview => ElementKind.Activity, DiagramType.StateMachine => ElementKind.StateMachine, _ => ElementKind.Interaction };
                    var existing = context.AncestorsAndSelf().FirstOrDefault(a => a.Kind == behaviorKind);
                    if (existing != null) { owner = existing; break; }
                    var host = Ws.Model.DefaultOwnerFor(context, behaviorKind);
                    using (Ws.Model.History.Transaction($"New {Metamodel.DisplayName(type)}"))
                    {
                        owner = Ws.Model.Create(host, behaviorKind, host.Kind is ElementKind.Package or ElementKind.Model ? null : $"{host.Name} {type switch { DiagramType.Activity or DiagramType.InteractionOverview => "Behavior", DiagramType.StateMachine => "States", _ => "Interaction" }}");
                        CreateDiagram(owner, type);
                    }
                    return;
                }
                default:
                    owner = context.AncestorsAndSelf().FirstOrDefault(a => a.Kind is ElementKind.Package or ElementKind.Model) ?? Ws.Model.Root;
                    break;
            }
            CreateDiagram(owner, type);
        }
        catch (InvalidOperationException ex)
        {
            Ws.SetStatus(ex.Message);
            Snackbar.Add(ex.Message, Severity.Info);
        }
    }

    private void Coverage()
    {
        var c = Traceability.Coverage(Ws.Model);
        Ws.Log($"Requirement coverage: {c.Satisfied}/{c.Total} satisfied ({c.SatisfiedRatio:P0}), {c.Verified}/{c.Total} verified ({c.VerifiedRatio:P0})");
        foreach (var r in c.Unsatisfied) Ws.Log($"  not satisfied: {r.Get(FieldKeys.RequirementId)} {r.Name}");
        foreach (var r in c.Unverified) Ws.Log($"  not verified:  {r.Get(FieldKeys.RequirementId)} {r.Name}");
        OpenDock(3);
        Ws.Bump();
    }

    private Task ShowHelp() => Dialogs.ShowMessageBox("Keyboard & mouse", (MarkupString)(
        "<b>Create:</b> pick a palette tool, click the canvas. Relationship tools: drag from source shape to target shape. Hold Shift to keep the tool.<br/><br/>" +
        "<b>Tree:</b> drag items onto a diagram to show them, onto another tree item to move them. Right-click for New / Rename / Delete.<br/><br/>" +
        "<b>Canvas:</b> double-click a shape to rename · double-click a path to add a bend point · Delete removes from model · Backspace removes from diagram · arrows nudge · ⌘/Ctrl+wheel zoom · wheel or middle-drag pans.<br/><br/>" +
        "<b>Pictures, links, layers:</b> drop or paste images onto the canvas (Shift frees their aspect ratio when resizing) · Ctrl/⌘-click a linked shape or its link icon to follow it · the layers button hides, locks and reorders layers.<br/><br/>" +
        "<b>Global:</b> ⌘S save · ⌘O open · ⌘Z / ⇧⌘Z undo/redo · ⌘F find · F7 validate."));

    private Task ShowAbout() => Dialogs.ShowMessageBox("About Meridian",
        "Meridian 0.1 — a model-based systems engineering workbench: UML 2.5 / SysML 1.6 modeling, SysML v2 textual exchange, XMI 2.5.1 interchange with Cameo, Enterprise Architect and Papyrus, requirements traceability, validation, reports and code generation.");

    private async Task<string?> Prompt(string title, string label, string? value, bool multiline = false)
    {
        var parameters = new DialogParameters<PromptDialog>
        {
            { x => x.Title, title }, { x => x.Label, label }, { x => x.Value, value }, { x => x.Multiline, multiline },
        };
        var dialog = await Dialogs.ShowAsync<PromptDialog>(title, parameters);
        var result = await dialog.Result;
        return result is { Canceled: false, Data: string s } ? s : null;
    }

    // ============================================================ file menu

    /// <summary>New project: pick a starter template from the gallery (see <see cref="Templates"/>).</summary>
    private async Task NewModel()
    {
        if (await TemplateGallery.ShowAsync(Dialogs, forDiagram: false) is not { } choice) return;
        var name = string.IsNullOrWhiteSpace(choice.Name) ? "New Model" : choice.Name;
        await CreateProjectFrom(Templates.CreateProject(choice.TemplateId, name), name);
    }

    /// <summary>New diagram from a diagram template, in the selection's package.</summary>
    private async Task NewDiagramFromTemplate()
    {
        if (Ws.ReadOnly) return;
        if (await TemplateGallery.ShowAsync(Dialogs, forDiagram: true) is not { } choice) return;
        var context = Ws.Selected ?? Ws.Active?.Diagram.Owner ?? Ws.Model.Root;
        try
        {
            var d = Templates.CreateDiagram(Ws.Model, context, choice.TemplateId);
            Ws.Open(d);
            Ws.Select(d, syncCanvas: false);
        }
        catch (InvalidOperationException ex) { Ws.SetStatus(ex.Message); }
    }

    private async Task ExportImage()
    {
        if (Ws.Active is not { } c) { Ws.SetStatus("Open a diagram first"); return; }
        var parameters = new DialogParameters<ExportImageDialog> { { x => x.Diagram, c.Diagram } };
        await Dialogs.ShowAsync<ExportImageDialog>("Export image", parameters, new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, CloseOnEscapeKey = true });
    }

    private async Task ExportImages(Element container)
    {
        var parameters = new DialogParameters<ExportImageDialog> { { x => x.Container, container } };
        await Dialogs.ShowAsync<ExportImageDialog>("Export images", parameters, new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, CloseOnEscapeKey = true });
    }

    private async Task CopyDiagramImage()
    {
        if (Ws.Active is not { } c) { Ws.SetStatus("Open a diagram first"); return; }
        var problem = await DiagramImageExport.CopyAsync(JS, Ws.Model, c.Diagram, 2, Theme.ExportBackground);
        if (problem != null) Snackbar.Add(problem, Severity.Warning);
        else Ws.SetStatus($"Copied {c.Diagram.Name} to the clipboard as a picture");
    }

    private Task StartTour() => _tour?.StartAsync() ?? Task.CompletedTask;

    private async Task OpenSample() => await CreateProjectFrom(SampleModels.Vehicle(), "Vehicle System (sample)");

    private async Task PickFile(string inputId) => await JS.InvokeVoidAsync("meridian.click", inputId);

    private Task ImportCsvIntoSelection()
    {
        _csvTarget = Ws.Selected is { Kind: ElementKind.Package or ElementKind.Model } p ? p : Ws.Model.Root;
        return PickFile("file-csv");
    }

    private static async Task<string> ReadText(IBrowserFile file)
    {
        await using var s = file.OpenReadStream(MaxUpload);
        using var reader = new StreamReader(s, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private async Task OnOpenFile(InputFileChangeEventArgs e)
    {
        var file = e.File;
        try
        {
            var ext = Path.GetExtension(file.Name).ToLowerInvariant();
            ModelRepository model;
            ImportReport? report = null;
            if (ext is ".qea" or ".qeax" or ".eapx" or ".sqlite")
            {
                // EA repositories are SQLite databases: copy to a private temp file and read it there.
                var dir = Directory.CreateTempSubdirectory("meridian-ea-");
                var tmp = Path.Combine(dir.FullName, "model.qea");
                try
                {
                    await using (var src = file.OpenReadStream(MaxUpload))
                    await using (var dst = File.Create(tmp)) await src.CopyToAsync(dst);
                    (model, report) = EaImporter.Import(tmp);
                }
                finally
                {
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    try { dir.Delete(true); } catch (IOException) { }
                }
                await CreateProjectFrom(model, Path.GetFileNameWithoutExtension(file.Name));
                Ws.Log($"Imported EA repository {file.Name}: {report.Elements} elements, {report.Relationships} relationships, {report.Warnings.Count} warnings.");
                foreach (var w in report.Warnings.Take(200)) Ws.Log("  " + w);
                Snackbar.Add($"Imported {file.Name} as a new project", Severity.Success);
                return;
            }
            if (IsMagicDrawFile(file.Name)) { await OpenMagicDrawProject(file); return; }
            if (MermaidImporter.IsMermaidFile(file.Name)) { await OpenMermaidProject(file); return; }
            var text = await ReadText(file);
            if (ext is ".xmi" or ".xml" or ".uml")
                (model, report) = XmiExchange.Import(System.Xml.Linq.XDocument.Parse(text));
            else if (ext == ".sysml")
                (model, report) = SysMLv2Text.Import(text, Path.GetFileNameWithoutExtension(file.Name));
            else
                model = ProjectFile.Deserialize(text);

            var projectName = Path.GetFileNameWithoutExtension(file.Name);
            await CreateProjectFrom(model, projectName);
            if (report != null)
            {
                Ws.Log($"Imported {file.Name}: {report.Elements} elements, {report.Relationships} relationships, {report.Warnings.Count} warnings.");
                foreach (var w in report.Warnings) Ws.Log("  warning: " + w);
                if (report.Warnings.Count > 0) OpenDock(3);
            }
            Snackbar.Add($"Imported {file.Name} as a new project", Severity.Success);
        }
        catch (Exception ex) when (ex is IOException or FormatException or System.Xml.XmlException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException or InvalidDataException)
        {
            Snackbar.Add($"Could not open {file.Name}: {ex.Message}", Severity.Error);
        }
    }

    private Element _importTarget = default!;

    private Task ImportInto(string inputId)
    {
        _importTarget = Ws.Selected?.AncestorsAndSelf().FirstOrDefault(a => a.Kind is ElementKind.Package or ElementKind.Model) ?? Ws.Model.Root;
        return PickFile(inputId);
    }

    /// <summary>BPMN (.bpmn), ArchiMate exchange (.xml), SQL DDL (.sql/.ddl), Mermaid (.mmd/.mermaid/.md) and C# sources (.cs or a .zip of them) into the selected package.</summary>
    private async Task OnImportInto(InputFileChangeEventArgs e)
    {
        var target = _importTarget ?? Ws.Model.Root;
        var csharp = new List<(string, string)>();
        foreach (var file in e.GetMultipleFiles(500))
        {
            var ext = Path.GetExtension(file.Name).ToLowerInvariant();
            try
            {
                switch (ext)
                {
                    case ".bpmn":
                    {
                        var d = BpmnExchange.Import(Ws.Model, target, System.Xml.Linq.XDocument.Parse(await ReadText(file)), Path.GetFileNameWithoutExtension(file.Name));
                        Ws.Open(d);
                        Snackbar.Add($"Imported BPMN process '{d.Name}'", Severity.Success);
                        break;
                    }
                    case ".xml":
                    {
                        var (els, rels, views) = ArchiMateExchange.Import(Ws.Model, target, System.Xml.Linq.XDocument.Parse(await ReadText(file)));
                        Snackbar.Add($"ArchiMate: {els} elements, {rels} relationships, {views} views", Severity.Success);
                        break;
                    }
                    case ".sql" or ".ddl":
                    {
                        Diagram d;
                        using (Ws.Model.History.Transaction("Import DDL"))
                        {
                            d = Ws.Model.CreateDiagram(target, DiagramType.Database, Path.GetFileNameWithoutExtension(file.Name) + " schema");
                            var (tables, fks) = SqlDdl.Import(Ws.Model, target, await ReadText(file), d);
                            Snackbar.Add($"SQL: {tables} tables, {fks} foreign keys", Severity.Success);
                        }
                        Ws.Open(d);
                        break;
                    }
                    case ".mdzip" or ".mdxml":
                        await ReimportMagicDraw(file);   // updates elements by MagicDraw id
                        break;
                    case ".mmd" or ".mermaid" or ".md":
                        await PasteMermaid(await ReadMermaid(file), file.Name);   // preview and options first
                        break;
                    case ".cs":
                        csharp.Add((file.Name, await ReadText(file)));
                        break;
                    case ".zip":
                    {
                        await using var s = file.OpenReadStream(MaxUpload);
                        using var ms = new MemoryStream();
                        await s.CopyToAsync(ms);
                        ms.Position = 0;
                        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
                        foreach (var entry in zip.Entries.Where(x => x.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !x.FullName.Contains("/obj/") && !x.FullName.Contains("/bin/")))
                        {
                            csharp.Add((entry.FullName, SafeZip.ReadText(entry, 16 * 1024 * 1024)));
                            if (csharp.Sum(x => (long)x.Item2.Length) > MaxUpload) throw new InvalidDataException("The archive unpacks to more than 64 MB of source.");
                        }
                        break;
                    }
                    default:
                        Snackbar.Add($"Unsupported file type: {file.Name}", Severity.Warning);
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or FormatException or System.Xml.XmlException or InvalidOperationException or InvalidDataException)
            {
                Snackbar.Add($"Could not import {file.Name}: {ex.Message}", Severity.Error);
            }
        }
        if (csharp.Count > 0)
        {
            try
            {
                var result = CSharpReverse.Import(Ws.Model, target, csharp);
                if (result.Diagram != null) Ws.Open(result.Diagram);
                Snackbar.Add($"C#: {result.Types} types, {result.Relationships} relationships from {csharp.Count} files", Severity.Success);
            }
            catch (InvalidOperationException ex) { Snackbar.Add($"C# import failed: {ex.Message}", Severity.Error); }
        }
    }

    private void AddUafProfile()
    {
        try
        {
            var p = Profiles.AddUaf(Ws.Model, Ws.Model.Root);
            Ws.Select(p);
            Snackbar.Add("Added the UAF profile — apply its stereotypes from the Properties panel", Severity.Success);
        }
        catch (InvalidOperationException ex) { Ws.SetStatus(ex.Message); }
    }

    private Task ExportBpmn()
    {
        if (Ws.Active?.Diagram is not { DiagramType: DiagramType.Bpmn } d) return Task.CompletedTask;
        var x = BpmnExchange.Export(Ws.Model, d);
        return DownloadText(d.Name + ".bpmn", "application/xml", x.Declaration + "\n" + x.Root);
    }

    private Task ExportArchiMate()
    {
        var x = ArchiMateExchange.Export(Ws.Model);
        return DownloadText(BaseName + " archimate.xml", "application/xml", x.Declaration + "\n" + x.Root);
    }

    private Task ExportDdl(SqlDialect dialect) => DownloadText($"{BaseName}-{dialect.ToString().ToLowerInvariant()}.sql", "text/plain", SqlDdl.Generate(Ws.Model, dialect));

    private async Task ExportDocx() =>
        await Dialogs.ShowAsync<ReportDialog>("Generate document", new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, BackdropClick = false });

    private Task ImportReqIfIntoSelection()
    {
        _csvTarget = Ws.Selected is { Kind: ElementKind.Package or ElementKind.Model } p ? p : Ws.Model.Root;
        return PickFile("file-reqif");
    }

    private async Task OnReqIfFile(InputFileChangeEventArgs e)
    {
        try
        {
            System.Xml.Linq.XDocument doc;
            await using (var s = e.File.OpenReadStream(MaxUpload))
            {
                if (e.File.Name.EndsWith(".reqifz", StringComparison.OrdinalIgnoreCase))
                {
                    using var ms = new MemoryStream();
                    await s.CopyToAsync(ms);
                    ms.Position = 0;
                    using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
                    var entry = zip.Entries.FirstOrDefault(x => x.Name.EndsWith(".reqif", StringComparison.OrdinalIgnoreCase)) ?? throw new FormatException("No .reqif file inside the archive");
                    await using var es = SafeZip.Open(entry, MaxUpload);
                    doc = System.Xml.Linq.XDocument.Load(es);
                }
                else
                {
                    using var ms = new MemoryStream();
                    await s.CopyToAsync(ms);
                    ms.Position = 0;
                    doc = System.Xml.Linq.XDocument.Load(ms);
                }
            }
            var (created, updated, relations) = ReqIfExchange.Import(Ws.Model, _csvTarget, doc);
            Ws.Log($"ReqIF import into {_csvTarget.QualifiedName}: {created} created, {updated} updated, {relations} relations.");
            Snackbar.Add($"ReqIF: {created} created, {updated} updated, {relations} relations", Severity.Success);
            OpenDock(1);
        }
        catch (Exception ex) when (ex is IOException or FormatException or System.Xml.XmlException or InvalidOperationException or InvalidDataException)
        {
            Snackbar.Add($"ReqIF import failed: {ex.Message}", Severity.Error);
        }
    }

    private Task ExportReqIf()
    {
        var x = ReqIfExchange.Export(Ws.Model, Ws.Model.Root.Name);
        return DownloadText(BaseName + ".reqif", "application/xml", x.Declaration + "\n" + x.Root);
    }

    private async Task OnCsvFile(InputFileChangeEventArgs e)
    {
        try
        {
            var (created, updated) = RequirementsCsv.Import(Ws.Model, _csvTarget, await ReadText(e.File));
            Ws.Log($"Requirements import into {_csvTarget.QualifiedName}: {created} created, {updated} updated.");
            Snackbar.Add($"{created} requirements created, {updated} updated", Severity.Success);
            OpenDock(1);
            Ws.Bump();
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException)
        {
            Snackbar.Add($"Import failed: {ex.Message}", Severity.Error);
        }
    }

    private string BaseName => string.Join("_", Ws.Model.Root.Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim() is { Length: > 0 } n ? n : "model";

    private async Task Download(string fileName, string mime, byte[] bytes)
    {
        await JS.InvokeVoidAsync("meridian.download", fileName, mime, Convert.ToBase64String(bytes));
        Ws.Log($"Downloaded {fileName}");
        if (Ws.Session is { } s) await Audit.RecordAsync(Meridian.Web.Admin.AuditActions.ProjectExport, Ws.User, s.ProjectId, s.Name, target: fileName, details: new { mime, bytes = bytes.Length });
        Ws.SetStatus($"Downloaded {fileName}");
    }

    private Task DownloadText(string fileName, string mime, string text) => Download(fileName, mime, Encoding.UTF8.GetBytes(text));

    /// <summary>Edits are saved continuously; ⌘S records a named version in the project history.</summary>
    private async Task Save()
    {
        if (Ws.Session is null || Ws.User is null) return;
        // Reachable from ⌘S and the command palette as well as the (disabled) button, so check the role here.
        if (Ws.ReadOnly) { Snackbar.Add("This project is view-only for you.", Severity.Warning); return; }
        var message = await Prompt("Commit version", "Describe this version", $"Snapshot {DateTime.Now:MMM d HH:mm}");
        if (string.IsNullOrWhiteSpace(message)) return;
        var (_, n) = await VersionControl.CommitAsync(Db, Ws.Session, message, Ws.User.Id, Ws.User);   // on the open branch
        Snackbar.Add($"Committed version {n}{(Ws.BranchId is null ? "" : $" on {Ws.BranchName}")}", Severity.Success);
        Ws.Log($"Committed version {n}: {message}");
        await Audit.RecordAsync(Meridian.Web.Admin.AuditActions.VersionCommit, Ws.User, Ws.Session.ProjectId, Ws.Session.Name, target: $"v{n}", details: new { message = message.Trim() });
    }

    private Task ExportXmi()
    {
        var x = XmiExchange.ToXml(Ws.Model);
        return DownloadText(BaseName + ".xmi", "application/xml", x.Declaration + "\n" + x.Root);
    }

    private Task ExportSysML() => DownloadText(BaseName + ".sysml", "text/plain", SysMLv2Text.Export(Ws.Model));
    private Task ExportCsv() => DownloadText(BaseName + " requirements.csv", "text/csv", RequirementsCsv.Export(Ws.Model));
    private Task DownloadReport() => DownloadText(BaseName + " report.html", "text/html", HtmlReport.Generate(Ws.Model));
    private async Task OpenReport() => await JS.InvokeVoidAsync("meridian.openHtml", HtmlReport.Generate(Ws.Model));

    private Task ExportSvg()
    {
        if (Ws.Active is not { } c) { Ws.SetStatus("Open a diagram first"); return Task.CompletedTask; }
        return DownloadText(c.Diagram.Name + ".svg", "image/svg+xml", SvgRenderer.Render(Ws.Model, c.Diagram));
    }

    private async Task GenerateCode()
    {
        var files = CSharpGenerator.Generate(Ws.Model);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, code) in files)
            {
                var entry = zip.CreateEntry(path.Replace('\\', '/'));
                await using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
                await w.WriteAsync(code);
            }
        await Download(BaseName + "-csharp.zip", "application/zip", ms.ToArray());
        Ws.Log($"Generated {files.Count} C# files");
    }
}
