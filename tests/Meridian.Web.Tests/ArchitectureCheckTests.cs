using System.Net;
using System.Text.Json.Nodes;
using Bunit;
using Meridian.Core.CodeSync;
using Meridian.Core.CodeSync.Conformance;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.CodeSync;
using Meridian.Web.CodeSync.Conformance;
using Meridian.Web.CodeSync.Hosts;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>
/// Architecture checks in the web host: the check after each code-sync run, the separate "meridian/architecture" commit
/// status (fake hosts, no network), the per-repository "fail pull requests" setting, the Architecture tab and the red
/// dashed edges on diagrams. Against a real SQLite store.
/// </summary>
public sealed class ArchitectureCheckTests : TestContext, IAsyncLifetime
{
    private const string Sha = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-arch-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;
    private CollaborationHub _hub = default!;
    private CodeSyncEvents _events = default!;
    private ArchitectureCheckService _checks = default!;
    private readonly FakeHost _host = new();
    private UserAccount _ann = default!;
    private Guid _project;
    private Guid _connection;
    private string _thermalId = "", _busId = "";

    public ArchitectureCheckTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        _hub = new CollaborationHub(_db, NullLogger<CollaborationHub>.Instance);
        _events = new CodeSyncEvents();
        _ann = await _db.CreateUserAsync("ann", "Ann", "correct horse");
        _project = await _db.CreateProjectAsync("Sat", ProjectFile.Serialize(ThermalModel()), _ann.Id);
        _connection = await Connect(new CodeRepoConfig { Host = GitHostKind.GitHub, Owner = "acme", Repo = "sat", Branch = "main" });
        _checks = Service();
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    // ================================================================ fakes and helpers

    /// <summary>The thermal example with a BDD showing both blocks.</summary>
    private ModelRepository ThermalModel()
    {
        var m = new ModelRepository("Sat");
        var arch = m.Create(m.Root, ElementKind.Package, "Architecture");
        var iface = m.Create(arch, ElementKind.InterfaceBlock, "IPowerBus");
        m.Create(iface, ElementKind.Operation, "Draw");
        var thermal = m.Create(arch, ElementKind.Block, "ThermalControl");
        var bus = m.Create(arch, ElementKind.Block, "PowerBus");
        var need = m.Create(thermal, ElementKind.Port, "power");
        m.Set(need, FieldKeys.Type, iface.Id);
        var supply = m.Create(bus, ElementKind.Port, "supply");
        m.Set(supply, FieldKeys.Type, iface.Id);
        m.Connect(ElementKind.Realization, bus, iface);
        var d = m.CreateDiagram(arch, DiagramType.BlockDefinition, "Power architecture");
        m.AddNode(d, thermal, new Bounds(40, 40, 160, 80));
        m.AddNode(d, bus, new Bounds(340, 40, 160, 80));
        m.AddNode(d, iface, new Bounds(190, 220, 160, 80));
        _thermalId = thermal.Id;
        _busId = bus.Id;
        return m;
    }

    private static readonly CodeModel DirectCall = new("csharp",
    [
        new CodeType("Sat.Thermal.ThermalControl", "ThermalControl", "Sat.Thermal", CodeTypeKind.Class, "src/Thermal/ThermalControl.cs", 12, null, [], [], [], ["Sat.Power.PowerBus"]),
        new CodeType("Sat.Power.PowerBus", "PowerBus", "Sat.Power", CodeTypeKind.Class, "src/Power/PowerBus.cs", 5, null, [], ["Sat.Power.IPowerBus"],
            [new CodeMember("Draw", CodeMemberKind.Method, "void", null, false, "public")], []),
        new CodeType("Sat.Power.IPowerBus", "IPowerBus", "Sat.Power", CodeTypeKind.Interface, "src/Power/IPowerBus.cs", 3, null, [], [],
            [new CodeMember("Draw", CodeMemberKind.Method, "void", null, false, "public")], []),
    ], []);

    private sealed class Reader(CodeModel code) : ICodeReader
    {
        public int Reads { get; private set; }
        public string Language => "csharp";
        public bool Handles(string path) => path.EndsWith(".cs", StringComparison.Ordinal);
        public CodeModel Read(IReadOnlyList<CodeFile> files, CancellationToken ct = default)
        {
            Reads++;
            return code;
        }
    }

    /// <summary>Records statuses, and the status context in effect when each was posted.</summary>
    private sealed class FakeHost : IGitHost
    {
        public List<(string Commit, CommitState State, string Description, string? Url, string Context)> Statuses { get; } = new();
        public string RepoDisplay => "acme/sat";
        public Task<string> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult("ok");
        public Task<GitCommitInfo> ResolveBranchHeadAsync(string branch, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CodeSnapshot> FetchSnapshotAsync(string commit, IReadOnlyList<string> paths, Func<string, bool> include, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PostCommitStatusAsync(string commit, CommitState state, string description, string? targetUrl, CancellationToken ct = default)
        {
            lock (Statuses) Statuses.Add((commit, state, description, targetUrl, GitHostFactory.StatusContext));
            return Task.CompletedTask;
        }
    }

    private async Task<Guid> Connect(CodeRepoConfig config)
    {
        var id = Guid.NewGuid();
        await _db.SaveIntegrationAsync(new IntegrationConnection(id, _project, CodeRepoKinds.CodeRepo, config.Display, config.ToJson(), null, DateTime.UtcNow));
        return id;
    }

    private ArchitectureCheckService Service(ICodeReader? reader = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Meridian:PublicUrl"] = "https://meridian.test/" }).Build();
        return new ArchitectureCheckService(_db, _hub, [reader ?? new Reader(DirectCall)], new ArchitectureStore(_db), new ServiceCollection().BuildServiceProvider(),
            config, NullLogger<ArchitectureCheckService>.Instance) { HostFactory = _ => _host };
    }

    private CodeSyncRequest Request(bool full = true) => new(_project, _connection, "acme/sat", "main", Sha, $"https://github.com/acme/sat/commit/{Sha}",
        "Dana Dev", "Call the bus", [new CodeFile("src/Thermal/ThermalControl.cs", "class ThermalControl {}")], full);

    private static readonly CodeSyncOutcome NoChanges = new(CodeSyncStatus.NoChanges, "No model changes", null, Guid.NewGuid());

    // ================================================================ checks and statuses

    [Fact]
    public async Task A_completed_run_posts_a_separate_architecture_status_that_passes_by_default()
    {
        var report = await _checks.CheckRunAsync(Request(), NoChanges);

        Assert.Equal(1, report!.Result.Errors);
        var status = Assert.Single(_host.Statuses);
        Assert.Equal((Sha, CommitState.Success, "1 violation (not enforced)", "meridian/architecture"), (status.Commit, status.State, status.Description, status.Context));
        Assert.Equal($"https://meridian.test/?project={_project}&panel=architecture", status.Url);
        Assert.Equal("meridian/model", GitHostFactory.StatusContext);   // only inside the architecture post

        var rec = (await _checks.Store.FindAsync(_project, _connection))!;
        Assert.Equal((Sha, "1 violation", 1, false), (rec.Commit, rec.Summary, rec.Errors, rec.FailPullRequests));
        Assert.Equal(3, rec.Code()!.Types.Count);   // kept for "Check architecture"
        // Stored as its own integration kind; the connection's settings are untouched.
        Assert.Single(await _db.ListIntegrationsAsync(_project), c => c.Kind == ArchitectureStore.Kind);
        Assert.DoesNotContain("PackedCode", (await _db.FindIntegrationAsync(_connection))!.ConfigJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task With_fail_pull_requests_on_violations_fail_the_status()
    {
        await _checks.SetFailPullRequestsAsync(_project, _connection, true);
        await _checks.CheckRunAsync(Request(), NoChanges);
        var status = Assert.Single(_host.Statuses);
        Assert.Equal((CommitState.Failure, "1 violation"), (status.State, status.Description));

        // An «allow» in main's model, then the on-demand re-check: green again.
        var main = ProjectFile.Deserialize(await VersionControl.HeadJsonAsync(_db, _hub, _project, null));
        var allow = main.Connect(ElementKind.Dependency, main.Find(_thermalId)!, main.Find(_busId)!);
        main.Set(allow, FieldKeys.Stereotypes, "allow");
        var again = await _checks.RecheckAsync(_project, _connection, main);
        Assert.Equal(CommitState.Success, again!.State);
        Assert.Equal((CommitState.Success, "No architecture violations", "meridian/architecture"), (_host.Statuses[^1].State, _host.Statuses[^1].Description, _host.Statuses[^1].Context));
        Assert.Single(again.Result.Suppressed);
    }

    [Fact]
    public async Task The_runner_raises_run_completed_and_the_check_follows()
    {
        var reader = new Reader(DirectCall);
        var checks = Service(reader);
        checks.Attach(_events);
        var applier = new NoChangeApplier();
        var runner = new CodeSyncRunner(_db, _hub, [reader], applier, _events, NullLogger<CodeSyncRunner>.Instance);

        var outcome = await runner.RunAsync(Request());
        Assert.Equal(CodeSyncStatus.NoChanges, outcome.Status);
        await checks.Idle;
        Assert.Equal(2, reader.Reads);   // the run's read, and the check's own
        Assert.Equal("meridian/architecture", Assert.Single(_host.Statuses).Context);

        checks.Detach(_events);
        await runner.RunAsync(Request());
        await checks.Idle;
        Assert.Single(_host.Statuses);
    }

    private sealed class NoChangeApplier : ICodeModelApplier
    {
        public CodeSyncChanges Apply(ModelRepository model, CodeModel code, CodeSyncOptions options) => new([], [], [], []);
    }

    [Fact]
    public async Task Failed_runs_and_connections_without_statuses_post_nothing()
    {
        Assert.Null(await _checks.CheckRunAsync(Request(), new CodeSyncOutcome(CodeSyncStatus.Failed, "boom", null, null)));
        Assert.Empty(_host.Statuses);

        var quiet = await Connect(new CodeRepoConfig { Host = GitHostKind.GitHub, Owner = "acme", Repo = "quiet", PostStatus = false });
        var report = await _checks.CheckRunAsync(Request() with { ConnectionId = quiet }, NoChanges);
        Assert.Equal(1, report!.Result.Errors);   // still checked and stored
        Assert.Empty(_host.Statuses);
        Assert.NotNull(await _checks.Store.FindAsync(_project, quiet));

        // A partial push does not report «implemented» blocks as missing.
        var main = ProjectFile.Deserialize(await VersionControl.HeadJsonAsync(_db, _hub, _project, null));
        main.Set(main.Find(_busId)!, FieldKeys.Stereotypes, "implemented");
        Assert.DoesNotContain(ArchitectureCheckService.Check(main, new CodeModel("csharp", [], []), _connection, complete: false).Violations, v => v.Rule == ConformanceRule.MissingInCode);
        Assert.Contains(ArchitectureCheckService.Check(main, new CodeModel("csharp", [], []), _connection, complete: true).Violations, v => v.Rule == ConformanceRule.MissingInCode);
    }

    [Fact]
    public async Task The_github_host_posts_the_architecture_context_in_the_status_body()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("{}", HttpStatusCode.Created));
        var host = GitHostFactory.Create(new CodeRepoConfig { Host = GitHostKind.GitHub, Owner = "acme", Repo = "sat" }, "token", new HttpClient(handler));
        using (GitHostFactory.UseStatusContext(ArchitectureCheckService.StatusContext))
            await host.PostCommitStatusAsync(Sha, CommitState.Failure, "2 violations", "https://meridian.test/x");
        await host.PostCommitStatusAsync(Sha, CommitState.Success, "1 added", null);
        var bodies = handler.Requests.Select(r => JsonNode.Parse(r.Body!)!).ToList();
        Assert.Equal(("meridian/architecture", "failure", "2 violations"), ((string?)bodies[0]["context"], (string?)bodies[0]["state"], (string?)bodies[0]["description"]));
        Assert.Equal("meridian/model", (string?)bodies[1]["context"]);

        var lab = new FakeHandler((_, _) => FakeHandler.Json("{}", HttpStatusCode.Created));
        var gitlab = GitHostFactory.Create(new CodeRepoConfig { Host = GitHostKind.GitLab, BaseUrl = "https://gitlab.example.com", Project = "acme/sat" }, "token", new HttpClient(lab));
        using (GitHostFactory.UseStatusContext(ArchitectureCheckService.StatusContext))
            await gitlab.PostCommitStatusAsync(Sha, CommitState.Failure, "2 violations", null);
        Assert.Equal("meridian/architecture", (string?)JsonNode.Parse(lab.Requests[0].Body!)!["name"]);
    }

    [Fact]
    public void Status_text_and_file_links()
    {
        var one = new ArchitectureViolation(ConformanceRule.Dependency, ViolationSeverity.Error, "m", "h", "a.cs", 1, null, null, null, null, null, null);
        var warn = one with { Rule = ConformanceRule.MissingInModel, Severity = ViolationSeverity.Warning };
        Assert.Equal((CommitState.Success, "No architecture violations"), ArchitectureCheckService.StatusFor(ConformanceResult.Empty, true));
        Assert.Equal((CommitState.Success, "No architecture violations (1 warning)"), ArchitectureCheckService.StatusFor(new([warn], [], 1, 1), true));
        Assert.Equal((CommitState.Failure, "2 violations (1 warning)"), ArchitectureCheckService.StatusFor(new([one, one, warn], [], 1, 1), true));
        Assert.Equal((CommitState.Success, "2 violations, 1 warning (not enforced)"), ArchitectureCheckService.StatusFor(new([one, one, warn], [], 1, 1), false));

        Assert.Equal($"https://github.com/acme/sat/blob/{Sha}/src/My%20Dir/A.cs#L12", ArchitectureCheckService.FileUrl($"https://github.com/acme/sat/commit/{Sha}", Sha, "src/My Dir/A.cs", 12));
        Assert.Equal($"https://gitlab.com/g/p/-/blob/{Sha}/src/A.cs#L3", ArchitectureCheckService.FileUrl($"https://gitlab.com/g/p/-/commit/{Sha}", Sha, "src/A.cs", 3));
        Assert.Equal($"https://bitbucket.org/w/r/src/{Sha}/src/A.cs#lines-3", ArchitectureCheckService.FileUrl($"https://bitbucket.org/w/r/commits/{Sha}", Sha, "src/A.cs", 3));
        Assert.Equal("https://git.example/x", ArchitectureCheckService.FileUrl("https://git.example/x", Sha, "src/A.cs", 3));
        Assert.Null(ArchitectureCheckService.FileUrl("javascript:alert(1)", Sha, "a.cs", 1));
        Assert.Null(ArchitectureCheckService.FileUrl(null, Sha, "a.cs", 1));
    }

    // ================================================================ UI

    private async Task<Workspace> UiAsync(ProjectRole role = ProjectRole.Owner)
    {
        var ws = new Workspace { User = _ann };
        Assert.True(await ws.JoinAsync(_hub, _project, role, null));
        Services.AddSingleton(ws);
        Services.AddSingleton<IMeridianStore>(_db);
        Services.AddSingleton(_hub);
        Services.AddSingleton(_checks);
        Services.AddSingleton<ImageCache>();
        Services.AddScoped<ArchitectureState>();
        return ws;
    }

    [Fact]
    public async Task Architecture_tab_lists_violations_by_rule_with_links_and_shows_them_on_diagrams()
    {
        await _checks.CheckRunAsync(Request(), NoChanges);
        var ws = await UiAsync();
        ws.OpenView(ViewTabKind.Architecture);
        Assert.Equal("Architecture", ws.ActiveView!.Title);

        RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = RenderComponent<ArchitectureTab>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".arch-row")), TimeSpan.FromSeconds(5));
        Assert.Contains("acme/sat", cut.Markup);
        Assert.Equal("dependency", cut.Find("h3.arch-rule").GetAttribute("data-rule"));
        Assert.Contains("Dependency not allowed", cut.Find("h3.arch-rule").TextContent);
        var row = cut.Find(".arch-row.error");
        Assert.Contains("ThermalControl uses PowerBus", row.TextContent);
        Assert.Contains("Depend on IPowerBus instead", row.TextContent);
        Assert.Equal($"https://github.com/acme/sat/blob/{Sha}/src/Thermal/ThermalControl.cs#L12", row.QuerySelector("a.loc")!.GetAttribute("href"));
        Assert.Contains("src/Thermal/ThermalControl.cs:12", row.TextContent);
        Assert.Contains("main: 1 violation", cut.Find(".arch-status").TextContent);

        // The red dashed edge between the two blocks.
        var edge = Assert.Single(ws.ArchitectureViolations!);
        Assert.Equal((_thermalId, _busId), (edge.SourceId, edge.TargetId));

        // "Show on diagram" opens the BDD with both blocks and selects the source.
        cut.Find(".arch-show").Click();
        Assert.Equal("Power architecture", ws.Active!.Diagram.Name);
        Assert.Equal("ThermalControl", ws.Selected?.Name);
        var view = RenderComponent<DiagramView>(p => p.Add(x => x.C, ws.Active!));
        var drawn = view.Find($"g.arch-violation[data-source='{_thermalId}'][data-target='{_busId}']");
        Assert.NotNull(drawn.QuerySelector("line"));

        // Hiding them clears the overlay.
        cut.Find(".arch-toggle").Click();
        Assert.Null(ws.ArchitectureViolations);
        view.Render();
        Assert.Empty(view.FindAll("g.arch-violation"));
    }

    [Fact]
    public async Task The_fail_pull_requests_toggle_is_saved_per_repository_and_check_reposts_on_main()
    {
        await _checks.CheckRunAsync(Request(), NoChanges);
        var ws = await UiAsync();
        RenderComponent<MudBlazor.MudPopoverProvider>();
        RenderComponent<MudBlazor.MudSnackbarProvider>();
        var cut = RenderComponent<ArchitectureTab>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".arch-row")), TimeSpan.FromSeconds(5));

        cut.Find($".arch-repo[data-connection='{_connection}'] input.arch-fail-prs").Change(true);
        cut.WaitForAssertion(() => Assert.True(_checks.Store.FindAsync(_project, _connection).Result!.FailPullRequests), TimeSpan.FromSeconds(5));

        // "Check architecture" on main re-posts the status, now failing.
        cut.Find(".arch-check").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, _host.Statuses.Count), TimeSpan.FromSeconds(5));
        Assert.Equal((CommitState.Failure, "1 violation", "meridian/architecture"), (_host.Statuses[1].State, _host.Statuses[1].Description, _host.Statuses[1].Context));

        // Fixing the model (an «allow») and checking again clears the tab.
        var allow = ws.Model.Connect(ElementKind.Dependency, ws.Model.Find(_thermalId)!, ws.Model.Find(_busId)!);
        ws.Model.Set(allow, FieldKeys.Stereotypes, "allow");
        cut.Find(".arch-check").Click();
        cut.WaitForAssertion(() => Assert.Contains("conforms to the architecture (1 suppressed)", cut.Markup), TimeSpan.FromSeconds(5));
        Assert.Equal(CommitState.Success, _host.Statuses[^1].State);
    }

    [Fact]
    public async Task Viewers_see_the_result_but_cannot_change_settings_or_post()
    {
        await _checks.CheckRunAsync(Request(), NoChanges);
        await UiAsync(ProjectRole.Viewer);
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = RenderComponent<ArchitectureTab>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".arch-row")), TimeSpan.FromSeconds(5));
        Assert.True(cut.Find("input.arch-fail-prs").HasAttribute("disabled"));
        cut.Find(".arch-check").Click();
        cut.WaitForAssertion(() => Assert.False(cut.Find(".arch-check").HasAttribute("disabled")), TimeSpan.FromSeconds(5));
        Assert.Single(_host.Statuses);   // only the run's
    }

    [Fact]
    public async Task Without_repositories_the_tab_explains_how_to_connect_one()
    {
        await _db.DeleteIntegrationAsync(_connection);
        await UiAsync();
        var cut = RenderComponent<ArchitectureTab>();
        cut.WaitForAssertion(() => Assert.Contains("No connected repositories", cut.Markup), TimeSpan.FromSeconds(5));
    }
}
