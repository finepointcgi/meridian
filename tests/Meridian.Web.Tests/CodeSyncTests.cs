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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Sync_Collapses_The_Root_Namespace_Per_Connection_And_Draws_A_Package_Overview(bool collapse)
    {
        var conn = new IntegrationConnection(Guid.NewGuid(), _project, CodeSync.Hosts.CodeRepoKinds.CodeRepo, "acme/sat",
            new CodeSync.Hosts.CodeRepoConfig { Owner = "acme", Repo = "sat", CollapseRootNamespace = collapse }.ToJson(), null, DateTime.UtcNow);
        await _db.SaveIntegrationAsync(conn);
        var runner = new CodeSyncRunner(_db, _hub, [new Meridian.Serialization.CodeSync.CSharpCodeReader()], new CodeModelApplier(), _events, NullLogger<CodeSyncRunner>.Instance);
        var files = new[]
        {
            new CodeFile("src/Power/Battery.cs", "namespace Sat.Power;\npublic class Battery { public double Capacity { get; set; } }"),
            new CodeFile("src/Comms/Radio.cs", "namespace Sat.Comms;\npublic class Radio { public Sat.Power.Battery Supply { get; set; } }"),
        };
        var outcome = await runner.RunAsync(Request() with { ConnectionId = conn.Id, Files = files, IsFullSnapshot = true });
        Assert.Contains(" structure, 0 activity, 0 sequence diagram", outcome.Summary);

        var branch = Assert.Single(await _db.ListBranchesAsync(_project), b => b.Name == CodeSyncRunner.BranchNameFor("acme/sat"));
        var model = ProjectFile.Deserialize(await VersionControl.HeadJsonAsync(_db, _hub, _project, branch.Id));
        var root = Assert.Single(model.OfKind(ElementKind.Package), p => p.Name == "sat");
        var power = Assert.Single(model.AllElements, e => e.Get(CodeFieldKeys.Symbol) == "ns:Sat.Power");
        Assert.Equal(collapse ? "sat" : "Sat", power.Owner!.Name);
        var overview = Assert.Single(model.Diagrams, d => d.DiagramType == DiagramType.Package);
        Assert.Equal("sat – package overview", overview.Name);
        Assert.Same(root, overview.Owner);
        var dep = Assert.Single(overview.Edges).Relationship;
        Assert.Equal("pkgdep:Sat.Comms->Sat.Power", dep.Get(CodeFieldKeys.Symbol));   // the sub-namespaces are shown either way
        Assert.Equal(collapse ? 2 : 3, overview.Nodes.Count);
        Assert.Contains((await _db.ListCodeSyncRunsAsync(_project)).Single().Changes, c => c.ElementId == overview.Id && c.Kind == CodeSyncChangeKind.Added);
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
        Assert.Contains(" structure, 0 activity, 0 sequence diagram", outcome.Summary);
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

    // ================================================================ robustness

    [Fact]
    public async Task An_Edit_Made_On_The_Branch_During_A_Sync_Survives()
    {
        _applier.Script.Enqueue(AddBlocks("Battery"));
        await Runner().RunAsync(Request());
        var branchId = Assert.Single(await _db.ListBranchesAsync(_project)).Id;
        var ben = await Open(_ben, ProjectRole.Editor, branchId);   // Ben is working on the review branch

        _applier.Script.Enqueue(m =>
        {
            // While the sync maps the code onto its copy of the branch, Ben documents the engine in his tab.
            ben.Model.Set(Named(ben.Model, "Engine")!, FieldKeys.Documentation, "Checked by Ben");
            return AddBlocks("SolarArray")(m);
        });
        var outcome = await Runner().RunAsync(Request("Add solar array"));
        Assert.Equal(CodeSyncStatus.AwaitingReview, outcome.Status);

        Assert.Equal("Checked by Ben", Named(ben.Model, "Engine")!.Documentation);   // his edit is still there…
        Assert.NotNull(Named(ben.Model, "SolarArray"));                               // …next to the sync's
        var stored = ProjectFile.Deserialize((await _db.LoadBranchAsync(branchId))!.HeadJson);
        Assert.Equal("Checked by Ben", Named(stored, "Engine")!.Documentation);
        Assert.NotNull(Named(stored, "SolarArray"));
        var committed = (await _db.ListBranchVersionsAsync(_project, branchId)).Count;
        Assert.Equal(2, committed);
        ben.Dispose();
    }

    [Fact]
    public async Task An_Edit_Stored_During_A_Sync_With_No_One_On_The_Branch_Survives()
    {
        _applier.Script.Enqueue(AddBlocks("Battery"));
        await Runner().RunAsync(Request());
        var branchId = Assert.Single(await _db.ListBranchesAsync(_project)).Id;

        _applier.Script.Enqueue(m =>
        {
            // Someone's last edits reach the store (their tab closed) while the sync runs.
            var head = ProjectFile.Deserialize(_db.LoadBranchAsync(branchId).GetAwaiter().GetResult()!.HeadJson);
            head.Set(Named(head, "Engine")!, FieldKeys.Documentation, "Saved meanwhile");
            _db.SaveBranchAsync(branchId, ProjectFile.Serialize(head), _ben.Id).GetAwaiter().GetResult();
            return AddBlocks("SolarArray")(m);
        });
        await Runner().RunAsync(Request("Add solar array"));
        var stored = await BranchAsync(branchId);
        Assert.Equal("Saved meanwhile", Named(stored, "Engine")!.Documentation);
        Assert.NotNull(Named(stored, "SolarArray"));
    }

    [Fact]
    public async Task A_Failed_Sync_Deletes_The_Branch_It_Created_But_Not_An_Existing_One()
    {
        _applier.Script.Enqueue(m => { AddBlocks("Battery")(m); throw new InvalidOperationException("Mapper exploded"); });
        var failed = await Runner().RunAsync(Request());
        Assert.Equal(CodeSyncStatus.Failed, failed.Status);
        Assert.Empty(await _db.ListBranchesAsync(_project));
        Assert.Null(Assert.Single(await _db.ListCodeSyncRunsAsync(_project)).BranchId);

        _applier.Script.Enqueue(AddBlocks("Battery"));
        await Runner().RunAsync(Request());
        _applier.Script.Enqueue(_ => throw new InvalidOperationException("Mapper exploded again"));
        await Runner().RunAsync(Request());
        Assert.Single(await _db.ListBranchesAsync(_project));   // the review branch with Battery stays
    }

    [Fact]
    public void Auto_Diagrams_Follow_The_Connection_Unless_The_Model_Root_Says_Otherwise()
    {
        var m = SampleModels.Vehicle();
        var off = new CodeSync.Hosts.CodeRepoConfig { AutoDiagrams = false };
        Assert.True(CodeSyncRunner.AutoDiagrams(m.Root, null));
        Assert.False(CodeSyncRunner.AutoDiagrams(m.Root, off));
        m.Set(m.Root, CodeSyncRunner.AutoDiagramsField, "true");
        Assert.True(CodeSyncRunner.AutoDiagrams(m.Root, off));
        m.Set(m.Root, CodeSyncRunner.AutoDiagramsField, "false");
        Assert.False(CodeSyncRunner.AutoDiagrams(m.Root, new CodeSync.Hosts.CodeRepoConfig()));
    }

    [Fact]
    public async Task The_Connection_Turns_Diagrams_For_New_Packages_Off()
    {
        var conn = new IntegrationConnection(Guid.NewGuid(), _project, CodeSync.Hosts.CodeRepoKinds.CodeRepo, "acme/sat",
            new CodeSync.Hosts.CodeRepoConfig { Owner = "acme", Repo = "sat", AutoDiagrams = false }.ToJson(), null, DateTime.UtcNow);
        await _db.SaveIntegrationAsync(conn);
        _applier.Script.Enqueue(m =>
        {
            var pkg = m.Create(m.Root, ElementKind.Package, "Power");
            var a = m.Create(pkg, ElementKind.Block, "PowerA");
            return new CodeSyncChanges([pkg.Id, a.Id], [], [], []);
        });
        var outcome = await Runner().RunAsync(Request() with { ConnectionId = conn.Id });
        Assert.DoesNotContain("structure", outcome.Summary);
        Assert.Equal("sat", _applier.LastOptions!.RootPackage);   // the connection's model package (the repository name)
    }

    // ================================================================ pull request previews

    [Fact]
    public void Preview_Branch_Names_Carry_The_Pull_Request_Number()
    {
        Assert.Equal("code-sync/acme/sat/pr-7", CodeSyncRunner.PreviewBranchNameFor("acme/sat", 7));
        Assert.Equal(7, CodeSyncRunner.PreviewNumber("code-sync/acme/sat/pr-7"));
        Assert.Null(CodeSyncRunner.PreviewNumber("code-sync/acme/sat"));
        Assert.Null(CodeSyncRunner.PreviewNumber("feature/pr-7"));
        var longName = CodeSyncRunner.PreviewBranchNameFor(new string('a', 40) + "/" + new string('b', 40), 123456);
        Assert.True(longName.Length <= 60);
        Assert.Equal(123456, CodeSyncRunner.PreviewNumber(longName));
        Branching.NormalizeBranchName(longName);
        // A repository named like a preview does not become one.
        Assert.False(CodeSyncRunner.IsPreviewBranch(CodeSyncRunner.BranchNameFor("acme/pr-5")));
    }

    [Fact]
    public async Task A_Pull_Request_Preview_Is_Listed_But_Never_Accepted_And_Closing_Deletes_It()
    {
        _applier.Script.Enqueue(AddBlocks("Thruster"));
        var preview = await Runner().RunPreviewAsync(Request("Add thruster"), 7);
        Assert.Equal(CodeSyncStatus.AwaitingReview, preview.Status);
        var branch = Assert.Single(await _db.ListBranchesAsync(_project));
        Assert.Equal("code-sync/acme/sat/pr-7", branch.Name);
        Assert.Contains($"branch={branch.Id}", preview.ReviewUrl);

        _applier.Script.Enqueue(AddBlocks("Battery"));
        await Runner().RunAsync(Request());   // and a push to main, on the review branch
        var reviews = await _review.LoadAsync(_project);
        Assert.Equal(2, reviews.Count);
        var pr = Assert.Single(reviews, r => r.IsPreview);
        Assert.Equal(7, pr.PullRequest);
        Assert.Equal("Thruster", Assert.Single(pr.Changes).Name);
        var changes = await Runner().PreviewChangesAsync(_project, "acme/sat", 7);
        Assert.Equal((CodeSyncChangeKind.Added, "Thruster", "Block"), (Assert.Single(changes).Kind, changes[0].Name, changes[0].ElementKind));

        // Accepting everything takes the review branch, not the preview.
        var accepted = await _review.AcceptAsync(_project, null, null, _ann);
        Assert.Equal(1, accepted.Count);
        Assert.NotNull(Named(await MainAsync(), "Battery"));
        Assert.Null(Named(await MainAsync(), "Thruster"));
        Assert.Equal(0, (await _review.RejectAsync(_project, null, null, _ann)).Count);
        Assert.True(Assert.Single(await _review.LoadAsync(_project)).IsPreview);

        // Merged: the preview goes; the push to main brings the code to the review branch.
        var message = await Runner().ClosePreviewAsync(_project, "acme/sat", 7, merged: true);
        Assert.Contains("deleted", message);
        Assert.DoesNotContain(await _db.ListBranchesAsync(_project), b => b.Name == "code-sync/acme/sat/pr-7");
        Assert.Empty(await _review.LoadAsync(_project));
        Assert.DoesNotContain(await _db.ListCodeSyncRunsAsync(_project), r => r.IsOpen);
        Assert.Contains("No preview", await Runner().ClosePreviewAsync(_project, "acme/sat", 7, merged: false));
    }

    [Fact]
    public async Task Closing_A_Preview_Someone_Has_Open_Keeps_Its_Branch()
    {
        _applier.Script.Enqueue(AddBlocks("Thruster"));
        await Runner().RunPreviewAsync(Request(), 8);
        var branchId = Assert.Single(await _db.ListBranchesAsync(_project)).Id;
        var ws = await Open(_ben, ProjectRole.Editor, branchId);
        var message = await Runner().ClosePreviewAsync(_project, "acme/sat", 8, merged: false);
        Assert.Contains("kept", message);
        Assert.Equal(CodeSyncRunStatus.Rejected, Assert.Single(await _db.ListCodeSyncRunsAsync(_project)).Status);
        Assert.Empty(await _review.LoadAsync(_project));
        ws.Dispose();
    }

    [Fact]
    public async Task Badge_And_What_Changed_Show_A_Preview_As_PR_n_Without_Selection()
    {
        _applier.Script.Enqueue(AddBlocks("Thruster"));
        await Runner().RunPreviewAsync(Request("Add thruster"), 7);
        var (ws, state) = await UiAsync(_ann, ProjectRole.Owner, null);
        Assert.Equal(0, state.PendingCount);
        Assert.Single(state.Previews);
        Assert.Null(ws.CodeChanges);   // main is not highlighted for a preview

        RenderComponent<MudBlazor.MudPopoverProvider>();
        var badge = RenderComponent<CodeChangesBadge>();
        Assert.Contains("PR #7", badge.Find("[data-testid=code-changes-badge]").TextContent);
        Assert.DoesNotContain("Code changes (", badge.Markup);

        var cut = RenderComponent<CodeChangesTab>();
        Assert.Contains("PR #7", cut.Find("h3.chg-preview").TextContent);
        Assert.Contains("for looking only", cut.Markup);
        Assert.True(cut.Find(".chg-row input[type=checkbox]").HasAttribute("disabled"));
        cut.Find(".chg-select-all").Change(true);
        Assert.Contains("0 selected", cut.Markup);

        // A push to main shows next to it.
        _applier.Script.Enqueue(AddBlocks("Battery"));
        await Runner().RunAsync(Request());
        badge.WaitForAssertion(() => Assert.Contains("Code changes (1)", badge.Markup));
        Assert.Contains("PR #7", badge.Markup);
        ws.Dispose();
    }

    [Fact]
    public async Task Repository_Settings_Offer_Bitbucket_The_Model_Settings_And_List_Previews()
    {
        var ws = await Open(_ann, ProjectRole.Owner);
        var model = ws.Model;
        model.Set(model.Root, CodeSyncRunner.AutoDiagramsField, "false");   // the project's override
        var factory = new FakeHttpClientFactory(new FakeHandler((_, _) => FakeHandler.Json("{}")));
        var integrations = new Integrations.IntegrationService(_db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), factory);
        var hosts = new CodeSync.Hosts.CodeSyncHostService(_db, integrations, factory, new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new CodeSync.Hosts.CodeSyncQueue(), new CodeSync.Hosts.CodeSyncRateLimiter(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<CodeSync.Hosts.CodeSyncHostService>.Instance);
        var saved = await hosts.SaveAsync(_project, null, null, new CodeSync.Hosts.CodeRepoConfig { Host = CodeSync.Hosts.GitHostKind.Bitbucket, Owner = "acme", Repo = "sat" }, "token");
        // The server keeps pull request state; a save from the form never sets it, so put it there as a sync would.
        var row = (await _db.FindIntegrationAsync(saved.Connection.Id))!;
        var cfg = CodeSync.Hosts.CodeRepoConfig.Parse(row.ConfigJson);
        cfg.PullRequests = [new() { Number = 7, Title = "Add thruster", Status = "awaiting-review", Summary = "1 added", ReviewUrl = "/x" }];
        await _db.SaveIntegrationAsync(row with { ConfigJson = cfg.ToJson() });

        Services.AddSingleton(ws);
        Services.AddSingleton(hosts);
        Services.AddSingleton(new MeridianHostInfo(false, Path.GetTempPath(), null));
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = RenderComponent<CodeRepositoriesPanel>(p => p.Add(x => x.CanManage, true));
        cut.WaitForAssertion(() => Assert.Contains("Bitbucket Cloud", cut.Markup));
        Assert.Contains("PR #7", cut.Find(".code-repo-pr[data-pr='7']").TextContent);
        Assert.Contains("Add thruster", cut.Markup);

        // Webhook setup for Bitbucket Cloud: the URL carries the secret.
        cut.Find("button.code-repo-webhook").Click();
        cut.WaitForAssertion(() => Assert.Contains("Bitbucket Cloud", cut.Find(".code-repo-setup").TextContent));
        Assert.Contains("?token=", cut.Find(".code-repo-setup").TextContent);

        // The settings form: model package, diagrams switch (with the model's override) and the pull request switches.
        cut.Find("button.code-repo-edit").Click();
        cut.WaitForAssertion(() => Assert.Contains("Create diagrams for new packages and classes", cut.Markup));
        Assert.Contains("codeSync.autoDiagrams = false", cut.Markup);
        Assert.Contains("Workspace", cut.Markup);
        Assert.Contains("Repository slug", cut.Markup);
        Assert.Contains("Preview pull requests", cut.Markup);
        Assert.Contains("Comment the model changes on each pull request", cut.Markup);
        ws.Dispose();
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
        Assert.Equal("Code changes", ws.ActiveView!.Title);

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

    // ================================================================ Integrations dialog: tabs by key, review link, token label

    /// <summary>Everything the Integrations dialog and its panels need, and a dialog provider to open it in.</summary>
    private async Task<(Workspace Ws, IRenderedComponent<MudBlazor.MudDialogProvider> Dialogs, CodeSync.Hosts.CodeSyncHostService Hosts)> IntegrationsUiAsync()
    {
        var factory = new FakeHttpClientFactory(new FakeHandler((_, _) => FakeHandler.Json("{}")));
        var integrations = new Integrations.IntegrationService(_db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), factory);
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var hosts = new CodeSync.Hosts.CodeSyncHostService(_db, integrations, factory, new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new CodeSync.Hosts.CodeSyncQueue(), new CodeSync.Hosts.CodeSyncRateLimiter(), config, NullLogger<CodeSync.Hosts.CodeSyncHostService>.Instance);
        var hostInfo = new MeridianHostInfo(false, Path.GetTempPath(), null);
        var webhookOptions = new Integrations.WebhookOptions();
        Services.AddSingleton(integrations);
        Services.AddSingleton(hosts);
        Services.AddSingleton(hostInfo);
        Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(config);
        Services.AddSingleton(new Integrations.WebhookDispatcher(new Integrations.ProjectEvents(webhookOptions), _db, integrations, factory, webhookOptions, NullLogger<Integrations.WebhookDispatcher>.Instance));
        Services.AddSingleton(new Integrations.Requirements.RequirementsToolService(_db, integrations, factory, new ServiceCollection().BuildServiceProvider(), NullLogger<Integrations.Requirements.RequirementsToolService>.Instance));
        Services.AddSingleton(new Integrations.Databases.DatabaseConnectionService(integrations, _db, hostInfo, config));
        var (ws, _) = await UiAsync(_ann, ProjectRole.Owner, null);
        RenderComponent<MudBlazor.MudPopoverProvider>();
        return (ws, RenderComponent<MudBlazor.MudDialogProvider>(), hosts);
    }

    private static string ActiveTab(IRenderedComponent<MudBlazor.MudDialogProvider> dialogs) => dialogs.Find(".mud-tab.mud-tab-active").TextContent.Trim();

    public static TheoryData<string, string> IntegrationEntryPoints => new()
    {
        { "menu", "Issue trackers" },
        { "code-repositories", "Code repositories" },   // ⌘K "Connect a code repository…"
        { "databases", "Databases" },                   // "Connect to a database…" (menu and ⌘K)
        { "requirements-tools", "DOORS & Polarion" },   // ⌘K "Connect DOORS Next or Polarion…"
    };

    [Theory]
    [MemberData(nameof(IntegrationEntryPoints))]
    public async Task Each_Integrations_Entry_Point_Opens_Its_Own_Tab(string entry, string tab)
    {
        var (_, dialogs, _) = await IntegrationsUiAsync();
        var service = Services.GetRequiredService<MudBlazor.IDialogService>();
        await dialogs.InvokeAsync(() => entry switch
        {
            "code-repositories" => IntegrationsDialog.ShowCodeRepositoriesAsync(service),
            "databases" => IntegrationsDialog.ShowDatabasesAsync(service),
            "requirements-tools" => IntegrationsDialog.ShowRequirementsToolsAsync(service),
            _ => IntegrationsDialog.ShowAsync(service),
        });
        dialogs.WaitForAssertion(() => Assert.Equal(tab, ActiveTab(dialogs)));
        // The drawn order is the order the keys are listed in (so the first render already shows the right tab).
        Assert.Equal(["Issue trackers", "Webhooks", "DOORS & Polarion", "Code repositories", "Databases"],
            dialogs.FindAll(".mud-tab").Select(t => t.TextContent.Trim()));
        Assert.Equal(IntegrationsDialog.TabOrder.Length, dialogs.FindAll(".mud-tab").Count);
    }

    [Fact]
    public async Task The_Empty_What_Changed_Tab_Opens_Code_Repositories()
    {
        var (_, dialogs, _) = await IntegrationsUiAsync();
        var empty = RenderComponent<CodeChangesTab>();
        await empty.InvokeAsync(() => empty.Find("button.chg-connect").Click());
        dialogs.WaitForAssertion(() => Assert.Equal("Code repositories", ActiveTab(dialogs)));
        Assert.Contains("No code repository connected", dialogs.Markup);
    }

    [Fact]
    public async Task The_Review_Link_Of_A_Synced_Repository_Closes_Integrations_And_Opens_Code_Changes()
    {
        var (ws, dialogs, hosts) = await IntegrationsUiAsync();
        var saved = await hosts.SaveAsync(_project, null, null, new CodeSync.Hosts.CodeRepoConfig { Owner = "acme", Repo = "sat" }, "token");
        var row = (await _db.FindIntegrationAsync(saved.Connection.Id))!;
        var cfg = CodeSync.Hosts.CodeRepoConfig.Parse(row.ConfigJson);
        cfg.LastSync = new() { At = DateTime.UtcNow, Commit = "abcdef1234", Status = "awaiting-review", Summary = "1 added", ReviewUrl = "/?project=x&panel=code-changes" };
        await _db.SaveIntegrationAsync(row with { ConfigJson = cfg.ToJson() });

        await dialogs.InvokeAsync(() => IntegrationsDialog.ShowCodeRepositoriesAsync(Services.GetRequiredService<MudBlazor.IDialogService>()));
        dialogs.WaitForAssertion(() => Assert.Contains("Changes to review", dialogs.Markup));
        Assert.Null(ws.ActiveView);
        dialogs.Find("a.code-repo-review").Click();
        dialogs.WaitForAssertion(() => Assert.Empty(dialogs.FindAll(".mud-dialog")));
        Assert.Equal(ViewTabKind.CodeChanges, ws.ActiveView?.Kind);
    }

    [Theory]
    [InlineData(CodeSync.Hosts.GitHostKind.GitHub, null, true, true, true, "Access token (Contents: read, Commit statuses: write, Pull requests: write)")]
    [InlineData(CodeSync.Hosts.GitHostKind.GitHub, null, false, false, true, "Access token (Contents: read)")]
    [InlineData(CodeSync.Hosts.GitHostKind.GitHub, null, true, false, true, "Access token (Contents: read, Commit statuses: write)")]
    [InlineData(CodeSync.Hosts.GitHostKind.GitHub, null, false, true, false, "Access token (Contents: read, Pull requests: read)")]
    [InlineData(CodeSync.Hosts.GitHostKind.GitLab, null, false, false, true, "Access token (scope: read_api)")]
    [InlineData(CodeSync.Hosts.GitHostKind.GitLab, null, false, true, true, "Access token (scope: api)")]
    [InlineData(CodeSync.Hosts.GitHostKind.GitLab, null, true, false, false, "Access token (scope: api)")]
    [InlineData(CodeSync.Hosts.GitHostKind.Bitbucket, null, false, false, true, "Access token (Repositories: read) or username:app-password")]
    [InlineData(CodeSync.Hosts.GitHostKind.Bitbucket, null, true, true, true, "Access token (Repositories: write, Pull requests: write) or username:app-password")]
    [InlineData(CodeSync.Hosts.GitHostKind.Bitbucket, null, false, true, false, "Access token (Repositories: read, Pull requests: read) or username:app-password")]
    [InlineData(CodeSync.Hosts.GitHostKind.Bitbucket, "https://git.example.com", false, false, true, "HTTP access token (Repository: read)")]
    [InlineData(CodeSync.Hosts.GitHostKind.Bitbucket, "https://git.example.com", true, false, false, "HTTP access token (Repository: write)")]
    public void The_Token_Label_Lists_Only_What_The_Settings_Need(CodeSync.Hosts.GitHostKind host, string? baseUrl, bool status, bool previews, bool comments, string label)
    {
        var cfg = new CodeSync.Hosts.CodeRepoConfig
        {
            Host = host, BaseUrl = baseUrl ?? CodeSync.Hosts.CodeRepoConfig.DefaultBaseUrl(host),
            PostStatus = status, PreviewPullRequests = previews, CommentOnPullRequests = comments,
        };
        Assert.Equal(label, cfg.TokenLabel);
    }

    [Fact]
    public async Task The_Token_Field_Follows_The_Status_And_Pull_Request_Switches()
    {
        var (_, _, hosts) = await IntegrationsUiAsync();
        await hosts.SaveAsync(_project, null, null, new CodeSync.Hosts.CodeRepoConfig { Owner = "acme", Repo = "sat", PostStatus = false, PreviewPullRequests = false }, "token");
        var cut = RenderComponent<CodeRepositoriesPanel>(p => p.Add(x => x.CanManage, true));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.code-repo-edit")));
        cut.Find("button.code-repo-edit").Click();
        cut.WaitForAssertion(() => Assert.Contains("Access token (Contents: read)", cut.Markup));
        Assert.DoesNotContain("Commit statuses", cut.Markup);
        Assert.Contains("Collapse common root namespace", cut.Markup);

        cut.FindAll(".mud-switch").First(s => s.TextContent.Contains("Post a commit status")).QuerySelector("input")!.Change(true);
        cut.WaitForAssertion(() => Assert.Contains("Access token (Contents: read, Commit statuses: write)", cut.Markup));
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

    // ================================================================ submodules

    private const string CoreA = "1111111111111111111111111111111111111111";
    private const string CoreB = "2222222222222222222222222222222222222222";
    private const string CompareCore = "https://github.com/acme/core/compare/{old}...{new}";

    [Fact]
    public async Task A_Submodule_Bump_Shows_In_What_Changed_With_A_Compare_Link_And_Is_Accepted_With_Its_Changes()
    {
        var pinsKey = CodeSubmodulePins.FieldKey(_connection.ToString());
        _applier.Script.Enqueue(AddBlocks("Battery"));
        await Runner().RunAsync(Request() with { Submodules = [new("libs/core", CoreA, CompareCore)] });
        Assert.True((await _review.AcceptAsync(_project, null, null, _ann)).Done);
        Assert.Equal($"libs/core@{CoreA} <{CompareCore}>", (await MainAsync()).Root.Get(pinsKey));

        // The parent moves libs/core forward; the code in it changed Battery.
        _applier.Script.Enqueue(Document("Battery", "From core v2"));
        var outcome = await Runner().RunAsync(Request("Bump core") with { Submodules = [new("libs/core", CoreB, CompareCore)] });
        Assert.Equal(CodeSyncStatus.AwaitingReview, outcome.Status);
        Assert.Contains("2 changed", outcome.Summary);
        var preview = (await _review.LoadAsync(_project)).Single();
        Assert.Contains(preview.Changes, c => c.Fields.Any(f => f.Key == pinsKey));

        var branchId = Assert.Single(await _db.ListBranchesAsync(_project)).Id;
        var (ws, _) = await UiAsync(_ann, ProjectRole.Owner, branchId);
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = RenderComponent<CodeChangesTab>();
        var bump = cut.Find(".chg-submodule[data-submodule='libs/core']");
        Assert.Contains("1111111 → 2222222", bump.TextContent);
        Assert.Equal($"https://github.com/acme/core/compare/{CoreA}...{CoreB}", bump.QuerySelector("a.chg-compare")!.GetAttribute("href"));
        Assert.Contains("Battery", cut.Find(".chg-row[data-kind=changed]").TextContent);   // the element change it caused
        Assert.Single(cut.FindAll(".chg-row[data-kind=changed]"));                        // the pins are not listed as an element
        cut.Find(".chg-filter").Input("core");
        Assert.Single(cut.FindAll(".chg-submodule"));

        // Accepting everything moves main's pin forward too.
        cut.Find(".chg-filter").Input("");
        cut.Find(".chg-select-all").Change(true);
        Assert.Contains("2 selected", cut.Markup);
        cut.Find(".chg-accept").Click();
        cut.WaitForAssertion(() => Assert.Contains("No code changes to review", cut.Markup), TimeSpan.FromSeconds(5));
        Assert.Equal($"libs/core@{CoreB} <{CompareCore}>", (await MainAsync()).Root.Get(pinsKey));
        ws.Dispose();
    }

    [Fact]
    public async Task A_Bump_Alone_Is_A_Change_And_The_Same_Pins_Are_Not()
    {
        _applier.Script.Enqueue(_ => new CodeSyncChanges([], [], [], []));
        var first = await Runner().RunAsync(Request() with { Submodules = [new("libs/core", CoreA)] });
        Assert.Equal(CodeSyncStatus.AwaitingReview, first.Status);   // a new submodule is worth reviewing
        _applier.Script.Enqueue(_ => new CodeSyncChanges([], [], [], []));
        Assert.Equal(CodeSyncStatus.NoChanges, (await Runner().RunAsync(Request() with { Submodules = [new("libs/core", CoreA)] })).Status);

        // A partial push without submodule information leaves the pins alone.
        _applier.Script.Enqueue(_ => new CodeSyncChanges([], [], [], []));
        Assert.Equal(CodeSyncStatus.NoChanges, (await Runner().RunAsync(Request() with { IsFullSnapshot = false })).Status);

        // The pull request comment lists the bump by path (against main, which has no pins yet).
        _applier.Script.Enqueue(_ => new CodeSyncChanges([], [], [], []));
        await Runner().RunPreviewAsync(Request() with { Submodules = [new("libs/core", CoreB)] }, 4);
        var rows = await Runner().PreviewChangesAsync(_project, "acme/sat", 4);
        var row = Assert.Single(rows);
        Assert.Equal(("libs/core ∅ → 2222222", "Submodule", CodeSyncChangeKind.Added), (row.Name, row.ElementKind, row.Kind));
    }

    [Fact]
    public async Task Real_Mapper_Stamps_Elements_From_Submodule_Code()
    {
        var runner = new CodeSyncRunner(_db, _hub, [new Meridian.Serialization.CodeSync.CSharpCodeReader()], new CodeModelApplier(), _events, NullLogger<CodeSyncRunner>.Instance);
        var files = new[]
        {
            new CodeFile("src/Power/Eps.cs", "namespace Sat.Power;\npublic class Eps { public Battery Main { get; set; } = new(); }"),
            new CodeFile("libs/core/src/Battery.cs", "namespace Sat.Power;\npublic class Battery { public double Capacity { get; set; } }"),
        };
        await runner.RunAsync(Request() with { Files = files, Submodules = [new("libs/core", CoreA), new("libs/skipped", CoreB, Fetched: false)] });
        var branch = Assert.Single(await _db.ListBranchesAsync(_project));
        var model = await BranchAsync(branch.Id);
        var battery = Assert.Single(model.AllElements, e => e.Kind == ElementKind.Block && e.Name == "Battery");
        var eps = Assert.Single(model.AllElements, e => e.Kind == ElementKind.Block && e.Name == "Eps");
        Assert.Equal($"libs/core@{CoreA}", battery.Get(CodeFieldKeys.Submodule));
        Assert.Equal("", eps.Get(CodeFieldKeys.Submodule));
        Assert.Equal($"libs/core@{CoreA}\nlibs/skipped@{CoreB}", model.Root.Get(CodeSubmodulePins.FieldKey(_connection.ToString())));

        // A bump keeps the stamp current without reporting Battery as changed.
        var bump = Request() with { Files = files, Submodules = [new("libs/core", CoreB), new("libs/skipped", CoreB, Fetched: false)] };
        var again = await runner.RunAsync(bump);
        Assert.Equal(CodeSyncStatus.AwaitingReview, again.Status);
        var run = (await _db.ListCodeSyncRunsAsync(_project)).Single(r => r.Commit == bump.Commit);
        Assert.Equal([model.Root.Id], run.Changes.Select(c => c.ElementId));
        Assert.Equal($"libs/core@{CoreB}", (await BranchAsync(branch.Id)).Find(battery.Id)!.Get(CodeFieldKeys.Submodule));
    }

    [Fact]
    public async Task Repository_Settings_List_The_Last_Syncs_Submodules_And_Save_Submodule_Settings()
    {
        var ws = await Open(_ann, ProjectRole.Owner);
        var factory = new FakeHttpClientFactory(new FakeHandler((_, _) => FakeHandler.Json("{}")));
        var integrations = new Integrations.IntegrationService(_db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), factory);
        var hosts = new CodeSync.Hosts.CodeSyncHostService(_db, integrations, factory, new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new CodeSync.Hosts.CodeSyncQueue(), new CodeSync.Hosts.CodeSyncRateLimiter(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<CodeSync.Hosts.CodeSyncHostService>.Instance);
        var saved = await hosts.SaveAsync(_project, null, null, new CodeSync.Hosts.CodeRepoConfig { Owner = "acme", Repo = "app" }, "token");
        // What a sync records (server state, never taken from the form).
        var row = (await _db.FindIntegrationAsync(saved.Connection.Id))!;
        var cfg = CodeSync.Hosts.CodeRepoConfig.Parse(row.ConfigJson);
        cfg.Submodules =
        [
            new() { Path = "libs/core", Sha = CoreA, Status = CodeSync.Hosts.CodeRepoSubmodule.Fetched, Files = 12, Url = "../core.git" },
            new() { Path = "libs/core/deps/util", Sha = CoreB, Status = CodeSync.Hosts.CodeRepoSubmodule.Fetched, Files = 1, Depth = 2 },
            new() { Path = "closed", Sha = CoreB, Status = CodeSync.Hosts.CodeRepoSubmodule.NoAccess, Message = "Could not read github.com/acme/closed" },
            new() { Path = "stale", Status = CodeSync.Hosts.CodeRepoSubmodule.NotFound, Message = "No submodule at this path in the commit" },
            new() { Path = "vendor/big", Sha = CoreA, Status = CodeSync.Hosts.CodeRepoSubmodule.Skipped, Message = "update = none in .gitmodules." },
        ];
        await _db.SaveIntegrationAsync(row with { ConfigJson = cfg.ToJson() });

        Services.AddSingleton(ws);
        Services.AddSingleton(hosts);
        Services.AddSingleton(new MeridianHostInfo(false, Path.GetTempPath(), null));
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = RenderComponent<CodeRepositoriesPanel>(p => p.Add(x => x.CanManage, true));
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".code-repo-submodule").Count));
        var core = cut.Find(".code-repo-submodule[data-path='libs/core']");
        Assert.Contains("@1111111", core.TextContent);
        Assert.Contains("fetched (12 files)", core.TextContent);
        Assert.Contains("padding-left:14px", cut.Find(".code-repo-submodule[data-path='libs/core/deps/util']").GetAttribute("style"));
        Assert.Contains("no access", cut.Find(".code-repo-submodule[data-status='no-access']").TextContent);
        Assert.Contains("github.com/acme/closed", cut.Find(".code-repo-submodule[data-status='no-access']").TextContent);
        Assert.Contains("not found", cut.Find(".code-repo-submodule[data-status='not-found']").TextContent);
        Assert.Contains("skipped", cut.Find(".code-repo-submodule[data-path='vendor/big']").TextContent);

        // The form: the switch, and the paths to skip (shown only while submodules are included).
        cut.Find("button.code-repo-edit").Click();
        cut.WaitForAssertion(() => Assert.Contains("Include git submodules", cut.Markup));
        cut.Find(".code-repo-skip-submodules input").Change("./vendor/big/, libs/x");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save").Click();
        cut.WaitForAssertion(() => Assert.DoesNotContain("Include git submodules", cut.Markup));
        var stored = CodeSync.Hosts.CodeRepoConfig.Parse((await _db.FindIntegrationAsync(saved.Connection.Id))!.ConfigJson);
        Assert.Equal(["vendor/big", "libs/x"], stored.SkipSubmodules);
        Assert.True(stored.IncludeSubmodules);
        Assert.Equal(5, stored.Submodules.Count);   // kept: a save from the form never replaces sync state

        cut.Find("button.code-repo-edit").Click();
        cut.WaitForAssertion(() => Assert.Contains("vendor/big, libs/x", cut.Find(".code-repo-skip-submodules input").GetAttribute("value")));
        cut.Find(".code-repo-include-submodules input").Change(false);
        Assert.Empty(cut.FindAll(".code-repo-skip-submodules"));
        ws.Dispose();
    }

    // ================================================================ behaviour from code

    private CodeSync.Hosts.CodeSyncHostService Hosts()
    {
        var factory = new FakeHttpClientFactory(new FakeHandler((_, _) => FakeHandler.Json("{}")));
        var integrations = new Integrations.IntegrationService(_db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), factory);
        return new CodeSync.Hosts.CodeSyncHostService(_db, integrations, factory, new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new CodeSync.Hosts.CodeSyncQueue(), new CodeSync.Hosts.CodeSyncRateLimiter(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<CodeSync.Hosts.CodeSyncHostService>.Instance);
    }

    [Fact]
    public async Task Behaviour_Setting_Is_Entry_Points_For_New_Connections_And_On_Demand_For_Existing_Ones()
    {
        // A connection stored before the setting existed reads as "on demand".
        Assert.Equal(CodeBehaviourMode.OnDemand, CodeSync.Hosts.CodeRepoConfig.Parse("""{"owner":"acme","repo":"old"}""").EffectiveBehaviour);
        var hosts = Hosts();
        var saved = await hosts.SaveAsync(_project, null, null, new CodeSync.Hosts.CodeRepoConfig { Owner = "acme", Repo = "app" }, "token");
        Assert.Equal(CodeBehaviourMode.EntryPoints, CodeSync.Hosts.CodeRepoConfig.Parse((await _db.FindIntegrationAsync(saved.Connection.Id))!.ConfigJson).BehaviourDiagrams);
        var off = await hosts.SaveAsync(_project, null, "off", new CodeSync.Hosts.CodeRepoConfig { Owner = "acme", Repo = "b", BehaviourDiagrams = CodeBehaviourMode.Off }, "token");
        Assert.Equal(CodeBehaviourMode.Off, CodeSync.Hosts.CodeRepoConfig.Parse((await _db.FindIntegrationAsync(off.Connection.Id))!.ConfigJson).EffectiveBehaviour);

        // The settings form shows the setting.
        var ws = await Open(_ann, ProjectRole.Owner);
        Services.AddSingleton(ws);
        Services.AddSingleton(hosts);
        Services.AddSingleton(new MeridianHostInfo(false, Path.GetTempPath(), null));
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = RenderComponent<CodeRepositoriesPanel>(p => p.Add(x => x.CanManage, true));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("button.code-repo-edit").Count));
        cut.FindAll("button.code-repo-edit")[0].Click();
        cut.WaitForAssertion(() => Assert.Contains("Behaviour diagrams", cut.Markup));
        Assert.NotEmpty(cut.FindAll(".code-repo-behaviour"));
        Assert.Contains("Entry points", cut.Find(".code-repo-behaviour").ParentElement!.TextContent);
        ws.Dispose();
    }

    [Fact]
    public async Task Generate_Activity_And_Sequence_On_Demand_From_The_Stored_Code()
    {
        var hosts = Hosts();
        var saved = await hosts.SaveAsync(_project, null, null, new CodeSync.Hosts.CodeRepoConfig { Owner = "acme", Repo = "sat", BehaviourDiagrams = CodeBehaviourMode.OnDemand }, "token");
        var connection = saved.Connection.Id;
        var code = new Serialization.CodeSync.CSharpCodeReader().Read([new CodeFile("src/Fleet.cs", """
            namespace Sat;
            public class Battery { public bool IsLow() => true; public void Charge() { } }
            public class Controller
            {
                private Battery _b = new();
                public void Balance(int n) { if (_b.IsLow()) _b.Charge(); else { for (var i = 0; i < n; i++) _b.Charge(); } }
            }
            """)]);
        var ws = await Open(_ann, ProjectRole.Owner);
        CodeModelMapper.Apply(ws.Model, code, new CodeSyncOptions { RepoId = connection.ToString(), Commit = "abc" });
        var op = ws.Model.AllElements.Single(e => e.Get(CodeFieldKeys.Symbol) == "Sat.Controller.Balance(int)");

        // Menu entries only for operations from code (and not read-only).
        var entries = CodeBehaviourUi.MenuEntries(op, readOnly: false, (_, _) => Task.CompletedTask);
        Assert.Equal([CodeBehaviourUi.ActivityLabel, CodeBehaviourUi.SequenceLabel], entries.Skip(1).Select(x => x.Label));
        Assert.Empty(CodeBehaviourUi.MenuEntries(op, readOnly: true, (_, _) => Task.CompletedTask));
        Assert.Empty(CodeBehaviourUi.MenuEntries(op.Owner, readOnly: false, (_, _) => Task.CompletedTask));

        // Nothing synced yet for the connection: a clear message.
        var (none, why) = await CodeBehaviourUi.GenerateAsync(_db, _project, ws.Model, op, false);
        Assert.Null(none);
        Assert.Contains("sync the repository first", why);

        await new CodeSync.Conformance.ArchitectureStore(_db).UpdateAsync(_project, connection, r => { r.Commit = "abc"; r.PackedCode = Core.CodeSync.Conformance.CodeModelJson.Pack(code); });

        // The inspector section runs the generation through its callback.
        Services.AddSingleton(ws);
        (Element, bool)? asked = null;
        var section = RenderComponent<CodeBehaviourSection>(p => p.Add(x => x.E, op).Add(x => x.OnGenerate, x => { asked = x; }));
        section.Find(".code-behaviour-sequence button, button.code-behaviour-sequence").Click();
        Assert.Equal((op, true), asked);

        // On the renderer's dispatcher, as the workbench runs it: the workspace reacts to the new diagrams on the same thread.
        var (activity, _) = await section.InvokeAsync(() => CodeBehaviourUi.GenerateAsync(_db, _project, ws.Model, op, false));
        var (sequence, _) = await section.InvokeAsync(() => CodeBehaviourUi.GenerateAsync(_db, _project, ws.Model, op, true));
        Assert.Equal(DiagramType.Activity, activity!.DiagramType);
        Assert.Contains(activity.Nodes, n => n.Element.Kind == ElementKind.DecisionNode);
        Assert.Equal(DiagramType.Sequence, sequence!.DiagramType);
        Assert.Contains(sequence.Nodes, n => n.Element.Kind == ElementKind.CombinedFragment && n.Element.Get(FieldKeys.Operator) == "alt");
        section.Render();
        Assert.Contains("Update activity from code", section.Markup);

        // Off: refused with the reason.
        var cfg = CodeSync.Hosts.CodeRepoConfig.Parse((await _db.FindIntegrationAsync(connection))!.ConfigJson);
        cfg.BehaviourDiagrams = CodeBehaviourMode.Off;
        await hosts.SaveAsync(_project, connection, null, cfg, null);
        Assert.Contains("off", (await section.InvokeAsync(() => CodeBehaviourUi.GenerateAsync(_db, _project, ws.Model, op, false))).Message);
        ws.Dispose();
    }

    private const string PowerCode = """
        namespace Sat.Power
        {
            public class Battery { public double Level { get; set; } public bool IsLow() => Level < 0.2; public void Charge(double w) { Level += w; } public void Drain() { Level = 0; } }
            public class Panel { public double Watts { get; set; } }
            public class Eps { public Battery Main { get; set; } public Panel Wing { get; set; } }
        }
        namespace Sat.Control
        {
            using Sat.Power;
            public class PowerController
            {
                private readonly Battery _battery = new();
                public void Balance(int rounds) { for (var i = 0; i < rounds; i++) { if (_battery.IsLow()) _battery.Charge(5); else _battery.Drain(); } }
            }
        }
        """;

    private Func<ModelRepository, CodeSyncChanges> RealSync(string source, CodeBehaviourMode mode = CodeBehaviourMode.EntryPoints) => m =>
        CodeModelMapper.Apply(m, new Serialization.CodeSync.CSharpCodeReader().Read([new CodeFile("src/Power.cs", source)]),
            new CodeSyncOptions { RootPackage = "Code", RepoId = _connection.ToString(), Commit = "c0ffee", Behaviour = mode });

    [Fact]
    public async Task Run_Summary_Counts_Every_Diagram_Made_Structure_Activity_And_Sequence()
    {
        _applier.Script.Enqueue(RealSync(PowerCode));
        var outcome = await Runner().RunAsync(Request());
        var run = Assert.Single(await _db.ListCodeSyncRunsAsync(_project));
        var diagrams = run.Changes.Where(c => c.Kind == CodeSyncChangeKind.Added && c.ElementKind == nameof(ElementKind.Diagram)).ToList();
        var head = await BranchAsync(run.BranchId!.Value);
        var (structure, activity, sequence) = CodeSyncRunner.DiagramCounts(diagrams.Select(c => (Diagram)head.Find(c.ElementId)!));
        Assert.True(activity >= 1 && sequence >= 1 && structure >= 2, outcome.Summary);   // the overview and the Eps IBD at least
        Assert.Contains($"; {structure} structure, {activity} activity, {sequence} sequence diagrams", outcome.Summary);
        Assert.Contains(head.Diagrams, d => d.DiagramType == DiagramType.Sequence && d.Name == "PowerController.Balance sequence");

        Assert.Equal("3 structure, 217 activity, 83 sequence diagrams; 380 trivial diagrams removed", CodeSyncRunner.DiagramSummary(3, 217, 83, 380));
        Assert.Equal("1 structure, 0 activity, 0 sequence diagram", CodeSyncRunner.DiagramSummary(1, 0, 0, 0));
        Assert.Equal("", CodeSyncRunner.DiagramSummary(0, 0, 0, 0));
    }

    [Fact]
    public async Task A_Run_Without_Code_Changes_Still_Adds_The_Missing_Package_Overview_And_Reports_Trivial_Removals()
    {
        _applier.Script.Enqueue(RealSync(PowerCode, CodeBehaviourMode.OnDemand));
        await Runner().RunAsync(Request());
        var branchId = Assert.Single(await _db.ListBranchesAsync(_project)).Id;
        Assert.Single((await BranchAsync(branchId)).Diagrams, d => d.DiagramType == DiagramType.Package);

        // As a project synced before the overview existed: no overview and no marker. The same code again changes nothing,
        // yet the run adds the overview, and says it removed two trivial diagrams.
        _applier.Script.Enqueue(m =>
        {
            var root = CodeModelMapper.FindRootPackage(m, "Code")!;
            m.Delete(m.Diagrams.Single(d => d.DiagramType == DiagramType.Package));
            m.Set(root, CodeSyncDiagrams.OverviewMadeKey, null);
            return new CodeSyncChanges([], [], [], []) { TrivialDiagramsRemoved = 2 };
        });
        var outcome = await Runner().RunAsync(Request("Nothing new"));
        Assert.Equal(CodeSyncStatus.AwaitingReview, outcome.Status);
        Assert.Contains("1 structure, 0 activity, 0 sequence diagram", outcome.Summary);
        Assert.Contains("2 trivial diagrams removed", outcome.Summary);
        Assert.Single((await BranchAsync(branchId)).Diagrams, d => d.DiagramType == DiagramType.Package);
    }

    [Fact]
    public void Rejecting_A_Removed_Diagram_Brings_It_Back_With_Its_Shapes()
    {
        var reference = SampleModels.Vehicle();
        var block = reference.OfKind(ElementKind.Block).First();
        var d = reference.CreateDiagram(block.Owner!, DiagramType.BlockDefinition, "Kept");
        var node = reference.AddNode(d, block, new Bounds(10, 20, 160, 80));
        var target = ProjectFile.Deserialize(ProjectFile.Serialize(reference));
        target.Delete(target.Find(d.Id)!);
        Assert.Null(target.Find(d.Id));

        CodeSyncRevert.Revert(target, reference, [d.Id]);
        var back = Assert.IsType<Diagram>(target.Find(d.Id));
        Assert.Equal(("Kept", DiagramType.BlockDefinition), (back.Name, back.DiagramType));
        var shape = Assert.Single(back.Nodes);
        Assert.Equal((node.Id, block.Id, new Bounds(10, 20, 160, 80)), (shape.Id, shape.Element.Id, shape.Bounds));
    }

    [Fact]
    public async Task Runner_Passes_The_Behaviour_Setting_To_The_Mapper()
    {
        var hosts = Hosts();
        var saved = await hosts.SaveAsync(_project, null, null, new CodeSync.Hosts.CodeRepoConfig { Owner = "acme", Repo = "sat" }, "token");
        _applier.Script.Enqueue(AddBlocks("Battery"));
        await Runner().RunAsync(Request() with { ConnectionId = saved.Connection.Id });
        Assert.Equal(CodeBehaviourMode.EntryPoints, _applier.LastOptions!.Behaviour);
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
