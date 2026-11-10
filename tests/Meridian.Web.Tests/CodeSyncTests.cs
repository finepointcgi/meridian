using System.Text;
using Bunit;
using Meridian.Core.CodeSync;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.CodeSync;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>
/// Code sync review branches: the runner (branch, run record, accumulation, no-change and failed runs, diagram refresh),
/// accepting all or some changes into main, rejecting, permissions, the "What changed" tab and diagram highlights.
/// Against a real SQLite store; the code reader and the code-to-model mapper are fakes.
/// </summary>
public sealed class CodeSyncTests : TestContext, IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-codesync-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;
    private CollaborationHub _hub = default!;
    private CodeSyncEvents _events = default!;
    private CodeSyncReviewService _review = default!;
    private readonly FakeApplier _applier = new();
    private UserAccount _ann = default!, _ben = default!, _vic = default!;
    private Guid _project;
    private readonly Guid _connection = Guid.NewGuid();

    public CodeSyncTests()
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
        _review = new CodeSyncReviewService(_db, _hub, _events, NullLogger<CodeSyncReviewService>.Instance);
        _ann = await _db.CreateUserAsync("ann", "Ann", "correct horse");
        _ben = await _db.CreateUserAsync("ben", "Ben", "battery staple");
        _vic = await _db.CreateUserAsync("vic", "Vic", "viewer only!");
        _project = await _db.CreateProjectAsync("Vehicle", ProjectFile.Serialize(SampleModels.Vehicle()), _ann.Id);
        await _db.SetMemberAsync(_project, _ben.Id, ProjectRole.Editor);
        await _db.SetMemberAsync(_project, _vic.Id, ProjectRole.Viewer);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    // ================================================================ fakes and helpers

    private sealed class FakeReader : ICodeReader
    {
        public string Language => "C#";
        public bool Handles(string path) => path.EndsWith(".cs", StringComparison.Ordinal);
        public CodeModel Read(IReadOnlyList<CodeFile> files, CancellationToken ct = default) => new("C#", [], []);
    }

    /// <summary>Applies the next scripted edit (as the mapper would) and reports its ids.</summary>
    private sealed class FakeApplier : ICodeModelApplier
    {
        public Queue<Func<ModelRepository, CodeSyncChanges>> Script { get; } = new();
        public CodeSyncOptions? LastOptions { get; private set; }

        public CodeSyncChanges Apply(ModelRepository model, CodeModel code, CodeSyncOptions options)
        {
            LastOptions = options;
            return Script.Dequeue()(model);
        }
    }

    private CodeSyncRunner Runner(ICodeModelApplier? applier = null) =>
        new(_db, _hub, [new FakeReader()], applier ?? _applier, _events, NullLogger<CodeSyncRunner>.Instance);

    private int _commits;

    private CodeSyncRequest Request(string message = "Add battery") => new(
        _project, _connection, "acme/sat", "main", $"{++_commits:D2}c0ffee{Guid.NewGuid():N}"[..40], "https://git.example/acme/sat/commit/abc",
        "Dana Dev", message + "\n\nLonger description", [new CodeFile("src/Battery.cs", "class Battery {}"), new CodeFile("README.md", "#")], IsFullSnapshot: true);

    private static Element Structure(ModelRepository m) => m.OfKind(ElementKind.Package).First(p => p.Name == "Structure");
    private static Element? Named(ModelRepository m, string name) => m.AllElements.FirstOrDefault(e => e.Name == name && e is not Diagram);

    /// <summary>A scripted sync step: new blocks in the Structure package.</summary>
    private static Func<ModelRepository, CodeSyncChanges> AddBlocks(params string[] names) => m =>
    {
        var ids = names.Select(n =>
        {
            var b = m.Create(Structure(m), ElementKind.Block, n);
            m.Set(b, CodeFieldKeys.Symbol, "Sat." + n);
            return b.Id;
        }).ToList();
        return new CodeSyncChanges(ids, [], [], []);
    };

    private static Func<ModelRepository, CodeSyncChanges> Document(string block, string doc) => m =>
    {
        var e = Named(m, block)!;
        m.Set(e, FieldKeys.Documentation, doc);
        return new CodeSyncChanges([], [e.Id], [], []);
    };

    private async Task<ModelRepository> MainAsync() => ProjectFile.Deserialize(await VersionControl.HeadJsonAsync(_db, _hub, _project, null));

    private async Task<ModelRepository> BranchAsync(Guid branchId) => ProjectFile.Deserialize(await VersionControl.HeadJsonAsync(_db, _hub, _project, branchId));

    private async Task<Workspace> Open(UserAccount user, ProjectRole role, Guid? branch = null)
    {
        var ws = new Workspace { User = user };
        Assert.True(await ws.JoinAsync(_hub, _project, role, branch));
        return ws;
    }

    // ================================================================ runner

    [Fact]
    public async Task Run_Creates_The_Review_Branch_Commits_And_Records_The_Run()
    {
        _applier.Script.Enqueue(AddBlocks("Battery"));
        var request = Request();
        var outcome = await Runner().RunAsync(request);

        Assert.Equal(CodeSyncStatus.AwaitingReview, outcome.Status);
        Assert.Equal("1 added", outcome.Summary);
        var branch = Assert.Single(await _db.ListBranchesAsync(_project));
        Assert.Equal("code-sync/acme/sat", branch.Name);
        Assert.Equal($"/?project={_project}&branch={branch.Id}&panel=code-changes", outcome.ReviewUrl);
        Assert.Equal((null, null), Integrations.DeepLink.ParseView("not a url"));
        Assert.Equal((branch.Id, "code-changes"), Integrations.DeepLink.ParseView("https://m.example" + outcome.ReviewUrl));

        // Only the branch has the new block; it was committed there with the commit's sha and first message line.
        Assert.NotNull(Named(await BranchAsync(branch.Id), "Battery"));
        Assert.Null(Named(await MainAsync(), "Battery"));
        var version = Assert.Single(await _db.ListBranchVersionsAsync(_project, branch.Id));
        Assert.Equal($"Code sync: {request.Commit[..7]} Add battery", version.Message);

        var run = Assert.Single(await _db.ListCodeSyncRunsAsync(_project));
        Assert.Equal(outcome.RunId, run.Id);
        Assert.Equal((CodeSyncRunStatus.AwaitingReview, branch.Id, _connection, request.Commit, request.CommitUrl, "Dana Dev"),
                     (run.Status, run.BranchId, run.ConnectionId, run.Commit, run.CommitUrl, run.Author));
        Assert.Equal((1, 0, 0), (run.Added, run.Changed, run.Removed));
        var change = Assert.Single(run.Changes);
        Assert.Equal((CodeSyncChangeKind.Added, "Battery", "Structure", "Block", CodeSyncChangeState.Pending),
                     (change.Kind, change.Name, change.Path, change.ElementKind, change.State));
        Assert.Equal(_connection.ToString(), _applier.LastOptions!.RepoId);
        Assert.True(_applier.LastOptions.RemoveDeleted);
    }

    [Fact]
    public async Task A_Second_Run_Accumulates_On_The_Same_Branch_And_Live_Users_See_It()
    {
        _applier.Script.Enqueue(AddBlocks("Battery"));
        await Runner().RunAsync(Request());
        var branchId = Assert.Single(await _db.ListBranchesAsync(_project)).Id;
        var live = await Open(_ben, ProjectRole.Editor, branchId);   // someone is looking at the review branch

        _applier.Script.Enqueue(Document("Engine", "Generated from Engine.cs"));
        var second = await Runner().RunAsync(Request("Document engine") with { IsFullSnapshot = false });
        Assert.Equal(CodeSyncStatus.AwaitingReview, second.Status);
        Assert.False(_applier.LastOptions!.RemoveDeleted);   // a partial push never deletes

        Assert.Single(await _db.ListBranchesAsync(_project));
        Assert.Equal("Generated from Engine.cs", Named(live.Model, "Engine")!.Documentation);   // pushed to the live session
        Assert.NotNull(Named(live.Model, "Battery"));
        Assert.Equal(2, (await _db.ListBranchVersionsAsync(_project, branchId)).Count);

        var review = Assert.Single(await _review.LoadAsync(_project));
        Assert.Equal(2, review.Runs.Count);
        Assert.Equal(["Battery", "Engine"], review.Changes.Select(c => c.Name).Order());
        var engine = review.Changes.Single(c => c.Name == "Engine");
        Assert.Equal(CodeSyncChangeKind.Changed, engine.Kind);
        Assert.Contains(engine.Fields, f => f.Key == FieldKeys.Documentation && f.After == "Generated from Engine.cs");
        live.Dispose();
    }

    [Fact]
    public async Task A_Run_Without_Changes_Leaves_No_Branch_And_Records_NoChanges()
    {
        _applier.Script.Enqueue(_ => new CodeSyncChanges([], [], [], []));
        var outcome = await Runner().RunAsync(Request());
        Assert.Equal(CodeSyncStatus.NoChanges, outcome.Status);
        Assert.Null(outcome.ReviewUrl);
        Assert.Empty(await _db.ListBranchesAsync(_project));
        Assert.Equal(CodeSyncRunStatus.NoChanges, Assert.Single(await _db.ListCodeSyncRunsAsync(_project)).Status);

        // A push with no source files the readers handle.
        var docs = await Runner().RunAsync(Request() with { Files = [new CodeFile("docs/x.md", "")] });
        Assert.Equal(CodeSyncStatus.NoChanges, docs.Status);
        Assert.Empty(await _review.LoadAsync(_project));
    }

    [Fact]
    public async Task Failures_Are_Recorded_As_Failed_Runs()
    {
        _applier.Script.Enqueue(_ => throw new InvalidOperationException("Mapper exploded"));
        var outcome = await Runner().RunAsync(Request());
        Assert.Equal(CodeSyncStatus.Failed, outcome.Status);
        Assert.Contains("Mapper exploded", outcome.Summary);
        var run = Assert.Single(await _db.ListCodeSyncRunsAsync(_project));
        Assert.Equal((CodeSyncRunStatus.Failed, outcome.RunId), (run.Status, (Guid?)run.Id));

        // Cancellation is honoured (and recorded).
        using var cts = new CancellationTokenSource();
        _applier.Script.Enqueue(_ => { cts.Cancel(); return new CodeSyncChanges([], [], [], []); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner().RunAsync(Request(), cts.Token));
        Assert.Equal(2, (await _db.ListCodeSyncRunsAsync(_project)).Count(r => r.Status == CodeSyncRunStatus.Failed));
    }

    [Fact]
    public async Task Real_Reader_And_Mapper_Put_Code_On_The_Review_Branch()
    {
        var runner = new CodeSyncRunner(_db, _hub, [new Meridian.Serialization.CodeSync.CSharpCodeReader()], new CodeModelApplier(), _events, NullLogger<CodeSyncRunner>.Instance);
        var files = new[]
        {
            new CodeFile("src/Power/Battery.cs", "namespace Sat.Power;\n/// <summary>Stores energy.</summary>\npublic class Battery { public double Capacity { get; set; } }"),
            new CodeFile("src/Power/Eps.cs", "namespace Sat.Power;\npublic class Eps { public Battery Main { get; set; } = new(); public void Charge() { } }"),
        };
        var outcome = await runner.RunAsync(Request() with { Files = files, IsFullSnapshot = true });
        Assert.Equal(CodeSyncStatus.AwaitingReview, outcome.Status);

        var branch = Assert.Single(await _db.ListBranchesAsync(_project), b => b.Name == CodeSyncRunner.BranchNameFor("acme/sat"));
        var model = ProjectFile.Deserialize(await VersionControl.HeadJsonAsync(_db, _hub, _project, branch.Id));
        var battery = Assert.Single(model.AllElements, e => e.Kind == ElementKind.Block && e.Name == "Battery");
        Assert.Equal("Sat.Power.Battery", battery.Get(CodeFieldKeys.Symbol));
        Assert.Contains(model.AllElements, e => e.Kind == ElementKind.Block && e.Name == "Eps");

        // Main is untouched until someone accepts the changes.
        var main = ProjectFile.Deserialize(await VersionControl.HeadJsonAsync(_db, _hub, _project, null));
        Assert.DoesNotContain(main.AllElements, e => e.Name == "Battery");

        // The same code again changes nothing.
        var again = await runner.RunAsync(Request() with { Files = files, IsFullSnapshot = true });
        Assert.Equal(CodeSyncStatus.NoChanges, again.Status);
    }

    [Fact]
    public async Task Runs_Of_One_Project_Are_Serialized()
    {
        var inside = 0;
        var overlapped = false;
        Func<ModelRepository, CodeSyncChanges> Slow(string name) => m =>
        {
            if (Interlocked.Increment(ref inside) > 1) overlapped = true;
            Thread.Sleep(50);
            var c = AddBlocks(name)(m);
            Interlocked.Decrement(ref inside);
            return c;
        };
        var applier = new FakeApplier();
        applier.Script.Enqueue(Slow("A"));
        applier.Script.Enqueue(Slow("B"));
        var runner = Runner(new LockedApplier(applier));
        await Task.WhenAll(Task.Run(() => runner.RunAsync(Request())), Task.Run(() => runner.RunAsync(Request())));
        Assert.False(overlapped);
        var head = await BranchAsync(Assert.Single(await _db.ListBranchesAsync(_project)).Id);
        Assert.NotNull(Named(head, "A"));
        Assert.NotNull(Named(head, "B"));
    }

    private sealed class LockedApplier(FakeApplier inner) : ICodeModelApplier
    {
        public CodeSyncChanges Apply(ModelRepository model, CodeModel code, CodeSyncOptions options)
        {
            lock (inner) return inner.Apply(model, code, options);
        }
    }

    [Fact]
    public async Task Diagram_Refresh_Adds_New_Children_Without_Moving_User_Shapes()
    {
        var before = await MainAsync();
        var bddBefore = before.Diagrams.Single(d => d.Name == "Vehicle Structure");
        var positions = bddBefore.Nodes.ToDictionary(n => n.Id, n => n.Bounds);

        _applier.Script.Enqueue(AddBlocks("Battery", "SolarArray"));
        await Runner().RunAsync(Request());
        var head = await BranchAsync(Assert.Single(await _db.ListBranchesAsync(_project)).Id);
        var bdd = head.Diagrams.Single(d => d.Name == "Vehicle Structure");

        foreach (var (id, bounds) in positions) Assert.Equal(bounds, bdd.FindNode(id)!.Bounds);   // nothing people placed moved
        var added = bdd.Nodes.Where(n => n.Element.Name is "Battery" or "SolarArray").ToList();
        Assert.Equal(2, added.Count);
        foreach (var n in added)
            Assert.DoesNotContain(bdd.Nodes.Where(o => !added.Contains(o) && o.ParentId == null), o => o.Bounds.Intersects(n.Bounds));
        Assert.False(added[0].Bounds.Intersects(added[1].Bounds));
        Assert.All(added, n => Assert.True(n.Bounds.Y > positions.Values.Max(b => b.Bottom)));   // placed below what was there

        // An unrelated diagram (requirements) got nothing.
        Assert.DoesNotContain(head.Diagrams.Single(d => d.Name == "Vehicle Requirements").Nodes, n => n.Element.Name == "Battery");
    }

    [Fact]
    public async Task New_Namespaces_Get_Diagrams_Unless_The_Project_Turns_It_Off()
    {
        Func<ModelRepository, CodeSyncChanges> NewNamespace(string name) => m =>
        {
            var pkg = m.Create(m.Root, ElementKind.Package, name);
            var a = m.Create(pkg, ElementKind.Block, name + "A");
            var b = m.Create(pkg, ElementKind.Block, name + "B");
            var g = m.Connect(ElementKind.Generalization, a, b, a);
            return new CodeSyncChanges([pkg.Id, a.Id, b.Id, g.Id], [], [], []);
        };
        _applier.Script.Enqueue(NewNamespace("Power"));
        var outcome = await Runner().RunAsync(Request());
        Assert.Contains("new diagram", outcome.Summary);
        var head = await BranchAsync(Assert.Single(await _db.ListBranchesAsync(_project)).Id);
        var created = head.Diagrams.Single(d => d.Owner?.Name == "Power");
        Assert.Equal(DiagramType.BlockDefinition, created.DiagramType);
        Assert.Single(created.Edges);
        Assert.Contains(Assert.Single(await _db.ListCodeSyncRunsAsync(_project)).Changes, c => c.ElementId == created.Id && c.Kind == CodeSyncChangeKind.Added);

        // Off for this project: the model root says so.
        var branchId = Assert.Single(await _db.ListBranchesAsync(_project)).Id;
        var withFlag = await BranchAsync(branchId);
        withFlag.Set(withFlag.Root, CodeSyncRunner.AutoDiagramsField, "false");
        await _db.SaveBranchAsync(branchId, ProjectFile.Serialize(withFlag), _ann.Id);
        _applier.Script.Enqueue(NewNamespace("Comms"));
        await Runner().RunAsync(Request());
        Assert.DoesNotContain((await BranchAsync(branchId)).Diagrams, d => d.Owner?.Name == "Comms");
    }

    // ================================================================ review: accept, partial accept, reject, permissions

    [Fact]
    public async Task Accept_All_Merges_Into_Main_Closes_The_Runs_And_Raises_Accepted()
    {
        _applier.Script.Enqueue(AddBlocks("Battery"));
        var request = Request();
        var outcome = await Runner().RunAsync(request);
        var raised = new List<(CodeSyncOutcome Outcome, Guid Connection, string Commit)>();
        _review.Accepted += (o, c, sha) => raised.Add((o, c, sha));
        var mainWs = await Open(_ann, ProjectRole.Owner);   // someone on main sees the accepted changes live

        var result = await _review.AcceptAsync(_project, null, null, _ben);
        Assert.True(result.Done);
        Assert.Equal(1, result.Count);
        Assert.NotNull(Named(await MainAsync(), "Battery"));
        Assert.NotNull(Named(mainWs.Model, "Battery"));
        Assert.Contains(await _db.ListBranchVersionsAsync(_project, null), v => v.Message.StartsWith("Accept 1 code change", StringComparison.Ordinal));

        var run = Assert.Single(await _db.ListCodeSyncRunsAsync(_project));
        Assert.Equal(CodeSyncRunStatus.Accepted, run.Status);
        Assert.All(run.Changes, c => Assert.Equal(CodeSyncChangeState.Accepted, c.State));
        var (o, connection, commit) = Assert.Single(raised);
        Assert.Equal((CodeSyncStatus.Accepted, outcome.RunId, _connection, request.Commit), (o.Status, o.RunId, connection, commit));
        Assert.Empty(await _review.LoadAsync(_project));

        // The branch is kept for the next push; the next run only shows what is new.
        _applier.Script.Enqueue(Document("Battery", "12 V"));
        await Runner().RunAsync(Request());
        var review = Assert.Single(await _review.LoadAsync(_project));
        Assert.Equal(CodeSyncChangeKind.Changed, Assert.Single(review.Changes).Kind);
        mainWs.Dispose();
    }

    [Fact]
    public async Task Partial_Accept_Merges_Only_The_Selection_And_Leaves_The_Rest_On_The_Branch()
    {
        _applier.Script.Enqueue(AddBlocks("Battery", "SolarArray"));
        await Runner().RunAsync(Request());
        var branch = await BranchAsync(Assert.Single(await _db.ListBranchesAsync(_project)).Id);
        var battery = Named(branch, "Battery")!.Id;

        var result = await _review.AcceptAsync(_project, null, [battery], _ann);
        Assert.True(result.Done);
        var main = await MainAsync();
        Assert.NotNull(main.Find(battery));
        Assert.Null(Named(main, "SolarArray"));
        // The BDD on main shows the accepted block but has no shape for the one left on the branch.
        var bdd = main.Diagrams.Single(d => d.Name == "Vehicle Structure");
        Assert.Contains(bdd.Nodes, n => n.Element.Id == battery);
        Assert.DoesNotContain(bdd.Nodes, n => n.Element.Name == "SolarArray");

        var review = Assert.Single(await _review.LoadAsync(_project));
        Assert.Equal("SolarArray", Assert.Single(review.Changes).Name);
        var run = Assert.Single(await _db.ListCodeSyncRunsAsync(_project));
        Assert.Equal(CodeSyncRunStatus.AwaitingReview, run.Status);
        Assert.Contains("1 accepted", run.Summary);

        // Later: the rest.
        await _review.AcceptAsync(_project, null, null, _ann);
        Assert.NotNull(Named(await MainAsync(), "SolarArray"));
        Assert.Equal(CodeSyncRunStatus.Accepted, Assert.Single(await _db.ListCodeSyncRunsAsync(_project)).Status);
    }

    [Fact]
    public async Task Reject_Puts_The_Branch_Back_To_Mains_Version()
    {
        _applier.Script.Enqueue(m =>
        {
            var a = AddBlocks("Battery")(m);
            var d = Document("Engine", "Electric")(m);
            return new CodeSyncChanges(a.Added, d.Changed, [], []);
        });
        await Runner().RunAsync(Request());
        var branchId = Assert.Single(await _db.ListBranchesAsync(_project)).Id;
        var engine = Named(await MainAsync(), "Engine")!.Id;
        var rejected = new List<Guid>();
        _review.Rejected += (_, c, _) => rejected.Add(c);

        await _review.RejectAsync(_project, null, [engine], _ben);
        Assert.Equal("", (await BranchAsync(branchId)).Find(engine)!.Documentation);
        Assert.Equal("Battery", Assert.Single(Assert.Single(await _review.LoadAsync(_project)).Changes).Name);

        await _review.RejectAsync(_project, null, null, _ben);
        Assert.Null(Named(await BranchAsync(branchId), "Battery"));
        Assert.Null(Named(await MainAsync(), "Battery"));
        var run = Assert.Single(await _db.ListCodeSyncRunsAsync(_project));
        Assert.Equal(CodeSyncRunStatus.Rejected, run.Status);
        Assert.All(run.Changes, c => Assert.Equal(CodeSyncChangeState.Rejected, c.State));
        Assert.Equal([_connection], rejected);
        Assert.Empty(await _review.LoadAsync(_project));
    }

    [Fact]
    public async Task Rejecting_A_Removal_Restores_The_Element_And_Its_Shapes()
    {
        _applier.Script.Enqueue(m =>
        {
            var tank = Named(m, "FuelTank")!;
            var ids = m.RelationshipsOf(tank).Select(r => r.Id).Prepend(tank.Id).ToList();
            m.Delete(tank);
            return new CodeSyncChanges([], [], ids, []);
        });
        await Runner().RunAsync(Request());
        var branchId = Assert.Single(await _db.ListBranchesAsync(_project)).Id;
        var main = await MainAsync();
        var tank = Named(main, "FuelTank")!;
        Assert.Null((await BranchAsync(branchId)).Find(tank.Id));
        Assert.Contains(Assert.Single(await _review.LoadAsync(_project)).Changes, c => c.ElementId == tank.Id && c.Kind == CodeSyncChangeKind.Removed);

        await _review.RejectAsync(_project, null, null, _ann);
        var head = await BranchAsync(branchId);
        Assert.NotNull(head.Find(tank.Id));
        var mainBdd = main.Diagrams.Single(d => d.Name == "Vehicle Structure");
        var headBdd = head.Diagrams.Single(d => d.Id == mainBdd.Id);
        Assert.Equal(mainBdd.NodeFor(tank)!.Bounds, headBdd.Nodes.Single(n => n.Element.Id == tank.Id).Bounds);
        Assert.Equal(mainBdd.Edges.Count, headBdd.Edges.Count);
    }

    [Fact]
    public async Task Viewers_Cannot_Accept_Or_Reject()
    {
        _applier.Script.Enqueue(AddBlocks("Battery"));
        await Runner().RunAsync(Request());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _review.AcceptAsync(_project, null, null, _vic));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _review.RejectAsync(_project, null, null, _vic));
        var stranger = await _db.CreateUserAsync("sam", "Sam", "not a member");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _review.AcceptAsync(_project, null, null, stranger));
        Assert.Null(Named(await MainAsync(), "Battery"));
        Assert.Equal(CodeSyncRunStatus.AwaitingReview, Assert.Single(await _db.ListCodeSyncRunsAsync(_project)).Status);
    }

    [Fact]
    public async Task Accepting_Over_A_Conflicting_Main_Edit_Asks_For_Decisions()
    {
        _applier.Script.Enqueue(Document("Engine", "From code"));
        await Runner().RunAsync(Request());
        var main = await MainAsync();
        main.Set(Named(main, "Engine")!, FieldKeys.Documentation, "From a modeller");
        await _db.SaveProjectAsync(_project, ProjectFile.Serialize(main), _ann.Id);

        var result = await _review.AcceptAsync(_project, null, null, _ann);
        Assert.False(result.Done);
        var plan = result.NeedsResolution!.Plan;
        var conflict = Assert.Single(plan.Result.Conflicts);
        Assert.Equal(("From a modeller", "From code"), (conflict.Ours, conflict.Theirs));
        Assert.Equal("From a modeller", Named(await MainAsync(), "Engine")!.Documentation);   // nothing written yet

        conflict.Choice = Meridian.Core.Analysis.MergeChoice.Theirs;
        var done = await _review.CompleteAcceptAsync(result.NeedsResolution, plan.Result.Resolve(), _ann);
        Assert.True(done.Done);
        Assert.Equal("From code", Named(await MainAsync(), "Engine")!.Documentation);
        Assert.Equal(CodeSyncRunStatus.Accepted, Assert.Single(await _db.ListCodeSyncRunsAsync(_project)).Status);
    }

    // ================================================================ UI

    private async Task<(Workspace Ws, CodeSyncState State)> UiAsync(UserAccount user, ProjectRole role, Guid? branch)
    {
        var ws = await Open(user, role, branch);
        Services.AddSingleton(ws);
        Services.AddSingleton<IMeridianStore>(_db);
        Services.AddSingleton(_hub);
        Services.AddSingleton(_events);
        Services.AddSingleton(_review);
        Services.AddSingleton<ImageCache>();
        Services.AddScoped<CodeSyncState>();
        var state = Services.GetRequiredService<CodeSyncState>();
        await state.ReloadAsync();
        return (ws, state);
    }

    [Fact]
    public async Task What_Changed_Tab_Lists_Changes_And_Accept_Selected_Works()
    {
        _applier.Script.Enqueue(AddBlocks("Battery", "SolarArray"));
        await Runner().RunAsync(Request());
        var branchId = Assert.Single(await _db.ListBranchesAsync(_project)).Id;
        var (ws, state) = await UiAsync(_ann, ProjectRole.Owner, branchId);
        Assert.Equal(2, state.PendingCount);

        RenderComponent<MudBlazor.MudPopoverProvider>();
        var badge = RenderComponent<CodeChangesBadge>();
        Assert.Contains("Code changes (2)", badge.Markup);
        badge.Find("[data-testid=code-changes-badge]").Click();
        Assert.Equal(ViewTabKind.CodeChanges, ws.ActiveView?.Kind);
        Assert.Equal("What changed", ws.ActiveView!.Title);

        var cut = RenderComponent<CodeChangesTab>();
        Assert.Contains("code-sync/acme/sat", cut.Markup);
        Assert.Contains("Add battery", cut.Markup);
        Assert.Contains("Dana Dev", cut.Markup);
        Assert.Equal(2, cut.FindAll(".chg-row[data-kind=added]").Count);
        Assert.True(cut.Find(".chg-accept").HasAttribute("disabled"));   // nothing selected yet

        // Filter, then select one change and accept it.
        cut.Find(".chg-filter").Input("solar");
        Assert.Single(cut.FindAll(".chg-row"));
        cut.Find(".chg-filter").Input("");
        var battery = Named(ws.Model, "Battery")!.Id;
        cut.Find($".chg-row[data-element='{battery}'] input[type=checkbox]").Change(true);
        Assert.Contains("1 selected", cut.Markup);
        cut.Find(".chg-accept").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".chg-row")), TimeSpan.FromSeconds(5));
        Assert.NotNull((await MainAsync()).Find(battery));
        Assert.Null(Named(await MainAsync(), "SolarArray"));
        badge.WaitForAssertion(() => Assert.Contains("Code changes (1)", badge.Markup));

        // Select all + reject the rest.
        cut.Find(".chg-select-all").Change(true);
        cut.Find(".chg-reject").Click();
        cut.WaitForAssertion(() => Assert.Contains("No code changes to review", cut.Markup), TimeSpan.FromSeconds(5));
        badge.WaitForAssertion(() => Assert.DoesNotContain("Code changes", badge.Markup));
        Assert.Null(Named(await BranchAsync(branchId), "SolarArray"));
        ws.Dispose();
    }

    [Fact]
    public async Task What_Changed_Tab_Shows_An_Empty_State_And_Is_Read_Only_For_Viewers()
    {
        var (_, _) = await UiAsync(_vic, ProjectRole.Viewer, null);
        var empty = RenderComponent<CodeChangesTab>();
        Assert.Contains("No code changes to review", empty.Markup);
        Assert.Empty(empty.FindAll(".chg-connect"));   // viewers can't connect repositories

        _applier.Script.Enqueue(AddBlocks("Battery"));
        await Runner().RunAsync(Request());
        empty.WaitForAssertion(() => Assert.Single(empty.FindAll(".chg-row")));   // a sync anywhere refreshes open tabs
        empty.Find(".chg-row input[type=checkbox]").Change(true);
        Assert.True(empty.Find(".chg-accept").HasAttribute("disabled"));
        Assert.Contains("View only", empty.Markup);
    }

    [Fact]
    public async Task What_Changed_Without_Changes_Offers_To_Connect_A_Repository()
    {
        await UiAsync(_ann, ProjectRole.Owner, null);
        var empty = RenderComponent<CodeChangesTab>();
        Assert.Contains("Connect a repository", empty.Find("button.chg-connect").TextContent);
    }

    [Fact]
    public async Task Show_On_Diagram_Opens_A_Diagram_And_Selects_The_Element()
    {
        _applier.Script.Enqueue(AddBlocks("Battery"));
        await Runner().RunAsync(Request());
        var branchId = Assert.Single(await _db.ListBranchesAsync(_project)).Id;
        var (ws, _) = await UiAsync(_ann, ProjectRole.Owner, branchId);
        ws.SetHighlightCodeChanges(false);
        var cut = RenderComponent<CodeChangesTab>();
        cut.Find(".chg-show").Click();
        Assert.Equal("Vehicle Structure", ws.Active!.Diagram.Name);
        Assert.Equal("Battery", ws.Selected?.Name);
        Assert.True(ws.HighlightCodeChanges);
        Assert.Null(ws.ActiveView);
    }

    [Fact]
    public async Task Diagrams_Highlight_Changes_With_Css_Classes_And_Ghosts_Of_Removed_Elements()
    {
        _applier.Script.Enqueue(m =>
        {
            var add = AddBlocks("Battery")(m);
            var doc = Document("Engine", "Electric")(m);
            var tank = Named(m, "FuelTank")!;
            var removed = m.RelationshipsOf(tank).Select(r => r.Id).Prepend(tank.Id).ToList();
            m.Delete(tank);
            return new CodeSyncChanges(add.Added, doc.Changed, removed, []);
        });
        await Runner().RunAsync(Request());
        var branchId = Assert.Single(await _db.ListBranchesAsync(_project)).Id;
        var (ws, state) = await UiAsync(_ann, ProjectRole.Owner, branchId);
        Assert.True(ws.HighlightCodeChanges);   // on by default on the review branch
        Assert.NotNull(ws.CodeChanges!.GhostSource);

        var bdd = ws.Model.Diagrams.Single(d => d.Name == "Vehicle Structure");
        var c = ws.Open(bdd);
        c.SetZoom(1);
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = RenderComponent<DiagramView>(p => p.Add(x => x.C, c));
        var battery = Named(ws.Model, "Battery")!.Id;
        var engine = Named(ws.Model, "Engine")!.Id;
        Assert.NotNull(cut.Find($"rect.chg-outline.chg-added[data-element='{battery}']"));
        Assert.NotNull(cut.Find($"rect.chg-outline.chg-changed[data-element='{engine}']"));
        var ghost = cut.Find("g.chg-ghost.chg-removed");
        Assert.Contains("FuelTank", ghost.TextContent);
        Assert.NotEmpty(cut.FindAll(".chg-legend .chg-sw.added"));

        // The legend's toggle hides them.
        cut.Find(".chg-legend .chg-toggle").Click();
        Assert.False(ws.HighlightCodeChanges);
        Assert.Empty(cut.FindAll(".chg-outline"));
        Assert.Contains("Highlight code changes", cut.Find(".chg-legend").TextContent);

        // On main (toggle on): changed/removed elements are outlined; nothing is a ghost there.
        var sb = new StringBuilder();
        var main = await MainAsync();
        var mainBdd = main.Diagrams.Single(d => d.Name == "Vehicle Structure");
        SvgRenderer.RenderChangeHighlights(sb, mainBdd, n => n.Bounds, null,
            state.Reviews[0].Kinds.ToDictionary(kv => kv.Key, kv => CodeSyncState.Highlight(kv.Value)));
        Assert.Contains("chg-outline chg-removed", sb.ToString());
        Assert.DoesNotContain("chg-ghost", sb.ToString());
        ws.Dispose();
    }

    [Fact]
    public void Branch_Names_Follow_The_Branch_Rules()
    {
        Assert.Equal("code-sync/acme/sat-fsw", CodeSyncRunner.BranchNameFor("acme/sat-fsw"));
        Assert.Equal("code-sync/group-sub/repo", CodeSyncRunner.BranchNameFor("group sub/repo"));
        Assert.Equal("code-sync/repository", CodeSyncRunner.BranchNameFor("  "));
        Assert.True(CodeSyncRunner.BranchNameFor(new string('x', 200)).Length <= 60);
        Assert.Equal(Branching.NormalizeBranchName(CodeSyncRunner.BranchNameFor("ö/ü:repo")), CodeSyncRunner.BranchNameFor("ö/ü:repo"));
        Assert.Equal("Code sync: abcdef1 Fix things", CodeSyncRunner.CommitMessage(new CodeSyncRequest(Guid.Empty, Guid.Empty, "r", "main", "abcdef1234", null, null, "Fix things\nmore", [], true)));
    }

    [Fact]
    public void Runner_Is_Registered_Over_The_Hosts_Fallback()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMeridianStore>(_ => throw new InvalidOperationException("not needed"));
        services.TryAddSingletonFallback();
        services.AddMeridianCodeSync();
        services.AddSingleton(sp => new CollaborationHub(sp.GetRequiredService<IMeridianStore>(), NullLogger<CollaborationHub>.Instance));
        var descriptors = services.Where(d => d.ServiceType == typeof(ICodeSyncRunner)).ToList();
        Assert.Equal(2, descriptors.Count);
        Assert.Null(descriptors[^1].ImplementationType);   // the last registration (ours) wins
        Assert.NotNull(descriptors[^1].ImplementationFactory);
    }
}

internal static class FallbackRegistration
{
    private sealed class NullRunner : ICodeSyncRunner
    {
        public Task<CodeSyncOutcome> RunAsync(CodeSyncRequest request, CancellationToken ct = default) =>
            Task.FromResult(new CodeSyncOutcome(CodeSyncStatus.Failed, "No runner", null, null));
    }

    /// <summary>What the git-host integration does: a fallback runner registered with TryAdd.</summary>
    public static void TryAddSingletonFallback(this IServiceCollection services) =>
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<ICodeSyncRunner, NullRunner>(services);
}
