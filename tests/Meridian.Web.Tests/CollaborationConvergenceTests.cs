using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Meridian.Web.Tests;

/// <summary>
/// Convergence without locks through the real session and real tabs (docs/COLLABORATION.md): each tab's inbox is a queue the
/// test drains, so the session's order and each tab's view of it can be interleaved at will. Covers the conflict rules (last
/// write per field, delete beats edit, refused move cycles), how refusals roll back (locks, package permissions — no resync, the
/// history kept), the filtered relay, code-sync operations, replacements (merges) with pending edits, late joiners and a
/// randomized property test over seeded interleavings of three to five tabs.
/// </summary>
public sealed class CollaborationConvergenceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-converge-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;
    private CollaborationHub _hub = default!;
    private UserAccount _alice = default!, _bob = default!, _carol = default!;
    private readonly List<Tab> _tabs = new();

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        _hub = new CollaborationHub(_db, NullLogger<CollaborationHub>.Instance);
        _alice = await _db.CreateUserAsync("alice", "Alice", "correct horse");
        _bob = await _db.CreateUserAsync("bob", "Bob", "battery staple");
        _carol = await _db.CreateUserAsync("carol", "Carol", "staple horse");
    }

    public async Task DisposeAsync()
    {
        foreach (var t in _tabs) t.Ws.Dispose();
        await _hub.FlushAllAsync();
        await Task.Delay(50);   // sessions left empty persist once more in the background
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            try { File.Delete(f); } catch (IOException) { }
    }

    /// <summary>A browser tab whose inbox the test drains (as a Blazor circuit would, but when the test says so).</summary>
    private sealed class Tab
    {
        private readonly Queue<Action> _inbox = new();

        public Tab(UserAccount user)
        {
            Ws = new Workspace { User = user };
            Ws.Dispatch = a => { _inbox.Enqueue(a); return Task.CompletedTask; };
        }

        public Workspace Ws { get; }
        public ModelRepository M => Ws.Model;
        public bool HasMail => _inbox.Count > 0;

        public void Deliver()
        {
            while (_inbox.TryDequeue(out var a)) a();
        }

        public Element E(string name) => M.AllElements.First(e => e.Name == name);
    }

    private async Task<Guid> Project(ModelRepository? model = null)
    {
        var id = await _db.CreateProjectAsync("Converge " + Guid.NewGuid().ToString("N")[..6], ProjectFile.Serialize(model ?? SampleModels.Vehicle()), _alice.Id);
        await _db.SetMemberAsync(id, _bob.Id, ProjectRole.Editor);
        await _db.SetMemberAsync(id, _carol.Id, ProjectRole.Editor);
        return id;
    }

    private async Task<Tab> Open(Guid project, UserAccount user)
    {
        var t = new Tab(user);
        Assert.True(await t.Ws.JoinAsync(_hub, project, ProjectRole.Editor));
        t.Deliver();
        _tabs.Add(t);
        return t;
    }

    private void DeliverAll()
    {
        for (var round = 0; round < 100 && _tabs.Any(t => t.HasMail); round++)
            foreach (var t in _tabs) t.Deliver();
    }

    /// <summary>Delivers everything; every tab of the project then equals the session's model (filtered tabs: their view of it).</summary>
    private void AssertConverged(Guid project)
    {
        var session = _hub.Find(project)!;
        session.FlushResyncs();
        DeliverAll();
        foreach (var t in _tabs.Where(t => t.Ws.Session == session))
        {
            Assert.Equal(0, t.Ws.PendingBatches);
            Assert.True(session.SnapshotFor(t.Ws.ParticipantId) == ProjectFile.Serialize(t.M), $"{t.Ws.User!.DisplayName}'s tab diverged from the session");
        }
    }

    // ------------------------------------------------------------------ conflicts

    [Fact]
    public async Task Two_Tabs_Rename_The_Same_Element_At_Once_And_Both_End_With_The_Same_Name()
    {
        var p = await Project();
        var (a, b) = (await Open(p, _alice), await Open(p, _bob));
        var id = a.E("Engine").Id;
        a.M.Rename(a.E("Engine"), "Engine (Alice)");   // ordered first
        b.M.Rename(b.E("Engine"), "Engine (Bob)");     // ordered second; Bob applied his before Alice's arrived
        Assert.Equal("Engine (Alice)", a.M.Find(id)!.Name);
        Assert.Equal("Engine (Bob)", b.M.Find(id)!.Name);
        AssertConverged(p);
        Assert.Equal("Engine (Bob)", a.M.Find(id)!.Name);
        Assert.Equal("Engine (Bob)", b.M.Find(id)!.Name);
        Assert.True(b.Ws.Rebases > 0);
    }

    [Fact]
    public async Task Delete_Beats_A_Concurrent_Edit_And_The_Editor_Is_Told_Gently()
    {
        var p = await Project();
        var (a, b) = (await Open(p, _alice), await Open(p, _bob));
        var id = a.E("Engine").Id;
        a.M.Delete(a.E("Engine"));
        b.M.Set(b.E("Engine"), FieldKeys.Documentation, "Bob's notes");
        AssertConverged(p);
        Assert.Null(b.M.Find(id));
        Assert.Equal("Someone else deleted “Engine” — your change to it was dropped.", b.Ws.Status);
    }

    [Fact]
    public async Task Moving_Two_Packages_Into_Each_Other_Refuses_The_Later_Move_And_Rolls_It_Back()
    {
        var m = SampleModels.Vehicle();
        m.Create(m.Root, ElementKind.Package, "Left");
        m.Create(m.Root, ElementKind.Package, "Right");
        var p = await Project(m);
        var (a, b) = (await Open(p, _alice), await Open(p, _bob));
        b.M.Rename(b.E("Right"), "Right side");   // an earlier edit of Bob's stays undoable
        DeliverAll();
        var model = b.M;
        a.M.Move(a.E("Left"), a.E("Right side"));
        b.M.Move(b.E("Right side"), b.E("Left"));
        Assert.Contains("can't be moved into", b.Ws.Status);
        Assert.Same(model, b.M);                               // rolled back in place, not resynchronised
        Assert.Same(b.M.Root, b.E("Right side").Owner);
        Assert.Equal($"Set {FieldKeys.Name}", b.M.History.UndoDescription);
        AssertConverged(p);
        Assert.Equal("Right side", a.E("Left").Owner!.Name);
    }

    [Fact]
    public async Task Same_Shape_Moved_By_Three_Tabs_Converges_On_The_Last_Move()
    {
        var p = await Project();
        var (a, b, c) = (await Open(p, _alice), await Open(p, _bob), await Open(p, _carol));
        foreach (var (t, x) in new[] { (a, 100.0), (b, 200.0), (c, 300.0) })
        {
            var d = t.M.Diagrams.First(d => d.NodeFor(t.E("Engine")) != null);
            t.M.SetBounds(d, new Dictionary<DiagramNode, Bounds> { [d.NodeFor(t.E("Engine"))!] = new(x, x, 120, 60) });
        }
        AssertConverged(p);
        var dd = a.M.Diagrams.First(d => d.NodeFor(a.E("Engine")) != null);
        Assert.Equal(300, dd.NodeFor(a.E("Engine"))!.Bounds.X);
    }

    [Fact]
    public async Task Undo_After_A_Rebase_Undoes_Only_My_Own_Edit_And_Skips_What_Others_Deleted()
    {
        var p = await Project();
        var (a, b) = (await Open(p, _alice), await Open(p, _bob));
        b.M.Set(b.E("Vehicle"), FieldKeys.Documentation, "Bob's");
        a.M.Set(a.E("Vehicle"), FieldKeys.Text, "Alice's");    // rebased under Bob's
        var made = a.M.Create(a.E("Vehicle").Owner!, ElementKind.Block, "Alice's block");
        AssertConverged(p);
        Assert.Same(made, a.M.Find(made.Id));                   // the rebase kept the object the undo history refers to
        a.M.History.Undo();
        a.M.History.Undo();
        AssertConverged(p);
        Assert.Null(b.M.Find(made.Id));
        Assert.Equal("Bob's", b.E("Vehicle").Documentation);
        Assert.Equal("", b.E("Vehicle").Get(FieldKeys.Text));

        a.M.Rename(a.E("Engine"), "Engine A");
        AssertConverged(p);
        b.M.Delete(b.E("Engine A"));
        AssertConverged(p);
        a.M.History.Undo();
        Assert.Contains("Someone else deleted part of what this changed", a.Ws.Status);
        AssertConverged(p);
    }

    // ------------------------------------------------------------------ existing features

    [Fact]
    public async Task A_Lock_Refusal_Rolls_Back_In_Place_With_The_Existing_Message()
    {
        var p = await Project();
        var (a, b) = (await Open(p, _alice), await Open(p, _bob));
        var session = a.Ws.Session!;
        b.M.Rename(b.E("FuelTank"), "FuelTank v2");
        DeliverAll();
        Assert.Null(session.TryLock(a.Ws.ParticipantId, a.E("Engine").Id, subtree: false));
        var model = b.M;
        b.M.Set(b.E("Engine"), FieldKeys.Documentation, "Bob was here");
        Assert.Equal("Engine is locked by Alice — your change was not applied.", b.Ws.Status);
        Assert.Equal("", b.E("Engine").Documentation);
        Assert.Same(model, b.M);
        Assert.Equal($"Set {FieldKeys.Name}", b.M.History.UndoDescription);   // the refused edit left no undo step
        a.M.Set(a.E("Engine"), FieldKeys.Documentation, "Alice's check-out");
        AssertConverged(p);
        Assert.Equal("Alice's check-out", b.E("Engine").Documentation);
    }

    [Fact]
    public async Task A_Package_Permission_Refusal_Rolls_Back_While_Earlier_Edits_Are_Pending()
    {
        var p = await Project();
        var (a, b) = (await Open(p, _alice), await Open(p, _bob));
        var structure = a.M.AllElements.First(e => e.Kind == ElementKind.Package && e.Name == "Structure").Id;
        await _db.SetPackagePermissionAsync(p, structure, PrincipalKind.User, _bob.Id, PackageAccess.Read, _alice.Id);
        await _hub.ReloadPermissionsAsync(p);
        DeliverAll();
        a.M.Rename(a.E("Vehicle"), "Car");                                       // pending for Bob's tab
        var req = b.M.OfKind(ElementKind.Requirement).First();
        b.M.Set(req, FieldKeys.Documentation, "allowed");                         // accepted, not yet acknowledged
        b.M.Rename(b.E("Engine"), "Hacked");                                       // refused
        Assert.Contains("read-only for you", b.Ws.Status);
        Assert.Equal("allowed", req.Documentation);
        Assert.Equal("Engine", b.M.Find(a.E("Engine").Id)!.Name);
        AssertConverged(p);
        Assert.Equal("Car", b.M.Find(a.E("Car").Id)!.Name);
    }

    [Fact]
    public async Task A_Tab_With_Hidden_Packages_Converges_On_Its_View()
    {
        var m = SampleModels.Vehicle();
        var secret = m.Create(m.Root, ElementKind.Package, "Secret");
        m.Create(secret, ElementKind.Block, "Hidden block");
        var p = await Project(m);
        await _db.SetPackagePermissionAsync(p, secret.Id, PrincipalKind.User, _bob.Id, PackageAccess.None, _alice.Id);
        var (a, b) = (await Open(p, _alice), await Open(p, _bob));
        Assert.True(b.Ws.HasHiddenPackages);
        a.M.Rename(a.E("Engine"), "Engine A");
        b.M.Rename(b.E("Engine"), "Engine B");
        a.M.Set(a.E("Hidden block"), FieldKeys.Documentation, "secret");
        b.M.Create(b.E("Structure"), ElementKind.Block, "Bob's");
        a.M.Move(a.E("Hidden block"), a.E("Structure"));   // becomes visible to Bob: his view is refreshed, pending kept
        b.M.Set(b.E("Vehicle"), FieldKeys.Documentation, "Bob after");
        AssertConverged(p);
        Assert.Contains(b.M.AllElements, e => e.Name == "Hidden block");
        Assert.Contains(a.M.AllElements, e => e.Name == "Engine B");   // Bob's rename came second
    }

    [Fact]
    public async Task Code_Sync_Operations_Interleave_With_Pending_Edits()
    {
        var p = await Project();
        var (a, b) = (await Open(p, _alice), await Open(p, _bob));
        var session = a.Ws.Session!;
        a.M.Rename(a.E("Engine"), "Engine (Alice)");
        // A code sync computed on a copy of the model: renames the same element and adds one.
        var copy = ProjectFile.Deserialize(session.Snapshot());
        var ops = new List<ModelOp>();
        copy.OpEmitted += ops.Add;
        copy.Rename(copy.AllElements.First(e => e.Id == a.E("Engine (Alice)").Id), "EngineService");
        copy.Create(copy.AllElements.First(e => e.Name == "Structure"), ElementKind.Block, "FromCode");
        session.ApplySystemOps(ops, null);
        b.M.Rename(b.E("Engine"), "Engine (Bob)");   // Bob hasn't seen Alice's rename or the sync yet
        AssertConverged(p);
        Assert.Equal("Engine (Bob)", a.M.Find(a.E("Engine (Bob)").Id)!.Name);
        Assert.Contains(a.M.AllElements, e => e.Name == "FromCode");
    }

    [Fact]
    public async Task A_Merge_Replacement_Keeps_Edits_Made_After_It_Was_Sent()
    {
        var p = await Project();
        var (a, b) = (await Open(p, _alice), await Open(p, _bob));
        var session = a.Ws.Session!;
        var merged = ProjectFile.Deserialize(session.Snapshot());
        merged.Rename(merged.AllElements.First(e => e.Name == "FuelTank"), "FuelTank (merged)");
        session.Replace(ProjectFile.Serialize(merged), _carol.Id, "Carol merged a branch");
        a.M.Rename(a.E("Engine"), "Engine after merge");   // Alice's tab hasn't installed the merge yet
        b.M.Set(b.E("Vehicle"), FieldKeys.Documentation, "Bob after merge");
        AssertConverged(p);
        Assert.Contains(a.M.AllElements, e => e.Name == "FuelTank (merged)");
        Assert.Contains(a.M.AllElements, e => e.Name == "Engine after merge");
        Assert.Equal("Bob after merge", a.E("Vehicle").Documentation);
    }

    [Fact]
    public async Task A_Late_Joiner_Converges_While_Others_Have_Pending_Edits()
    {
        var p = await Project();
        var (a, b) = (await Open(p, _alice), await Open(p, _bob));
        a.M.Rename(a.E("Engine"), "E1");
        b.M.Rename(b.E("Engine"), "E2");
        a.M.Move(a.E("FuelTank"), a.M.Root);
        var c = await Open(p, _carol);   // joins while Alice's and Bob's inboxes still hold each other's edits
        c.M.Move(c.E("FuelTank"), c.E("Structure"), 0);
        b.M.Delete(b.E("E2"));
        AssertConverged(p);
        Assert.DoesNotContain(c.M.AllElements, e => e.Name is "E1" or "E2");
    }

    // ------------------------------------------------------------------ the property test

    /// <summary>
    /// Seeded random interleavings of three to five real tabs on one session: field edits, creates at positions, deletes,
    /// moves (cycles are refused), shapes, relationships, undo / redo, element locks and partial inbox delivery. Every tab must
    /// end equal to the session's model. Seeds: MERIDIAN_CONVERGENCE_WEB_SEEDS (default 500).
    /// </summary>
    [Fact]
    public async Task Random_Interleavings_Of_Real_Tabs_Converge()
    {
        var seeds = int.TryParse(Environment.GetEnvironmentVariable("MERIDIAN_CONVERGENCE_WEB_SEEDS"), out var n) && n > 0 ? n : 500;
        var users = new[] { _alice, _bob, _carol, await _db.CreateUserAsync("dave", "Dave", "x horse battery"), await _db.CreateUserAsync("erin", "Erin", "y horse battery") };
        int actions = 0, rebases = 0, refused = 0;
        for (var seed = 0; seed < seeds; seed++)
        {
            var rng = new Random(seed);
            var m = SampleModels.Vehicle();
            foreach (var k in Enumerable.Range(0, 3)) m.Create(m.Root, ElementKind.Package, $"Shared {k}");
            var p = await _db.CreateProjectAsync($"Seed {seed}", ProjectFile.Serialize(m), _alice.Id);
            foreach (var u in users.Skip(1)) await _db.SetMemberAsync(p, u.Id, ProjectRole.Editor);
            foreach (var t in _tabs) t.Ws.Dispose();
            _tabs.Clear();
            var count = rng.Next(3, 6);
            for (var i = 0; i < count; i++) await Open(p, users[i]);
            var session = _tabs[0].Ws.Session!;
            for (var step = rng.Next(15, 40); step > 0; step--)
            {
                var t = _tabs[rng.Next(_tabs.Count)];
                if (rng.Next(3) == 0) { _tabs[rng.Next(_tabs.Count)].Deliver(); continue; }
                actions++;
                try { RandomEdit(rng, t, session); }
                catch (InvalidOperationException) { }
            }
            try { AssertConverged(p); }
            catch (Exception ex) { throw new Xunit.Sdk.XunitException($"seed {seed}: {ex.Message}"); }
            rebases += _tabs.Sum(t => t.Ws.Rebases);
            refused += _tabs.Count(t => t.Ws.Status.Contains("not applied") || t.Ws.Status.Contains("can't be moved"));
            foreach (var t in _tabs) t.Ws.Dispose();
            _tabs.Clear();
            await session.PersistAsync();
        }
        output.WriteLine($"{seeds} seeds: {actions} actions, {rebases} rebases, {refused} tabs ended on a refusal");
    }

    private static void RandomEdit(Random rng, Tab t, ProjectSession session)
    {
        var m = t.M;
        var all = m.AllElements.Where(e => e.Owner != null).ToList();
        var packages = m.OfKind(ElementKind.Package).ToList();
        var blocks = m.OfKind(ElementKind.Block).ToList();
        var diagram = m.Diagrams.FirstOrDefault(d => d.DiagramType == DiagramType.BlockDefinition);
        T Pick<T>(IReadOnlyList<T> xs) => xs[rng.Next(xs.Count)];
        switch (rng.Next(12))
        {
            case < 3 when all.Count > 0:
                m.Set(Pick(all), rng.Next(2) == 0 ? FieldKeys.Name : FieldKeys.Documentation, $"{t.Ws.User!.DisplayName} {rng.Next(1000)}");
                break;
            case 3 when packages.Count > 0:
            {
                var owner = Pick(packages);
                m.Create(owner, rng.Next(3) == 0 ? ElementKind.Package : ElementKind.Block, $"new {rng.Next(1000)}", rng.Next(-1, owner.OwnedElements.Count + 1));
                break;
            }
            case 4 when blocks.Count > 0:
                m.Delete(Pick(blocks));
                break;
            case 5 when blocks.Count > 0 && packages.Count > 0:
            {
                var e = Pick(rng.Next(2) == 0 ? packages : blocks);
                var to = Pick(packages);
                if (Metamodel.CanOwn(to.Kind, e.Kind)) m.Move(e, to, rng.Next(-1, to.OwnedElements.Count + 1));
                break;
            }
            case 6 when diagram is { Nodes.Count: > 0 }:
            {
                var node = Pick(diagram.Nodes);
                m.SetBounds(diagram, new Dictionary<DiagramNode, Bounds> { [node] = node.Bounds.Offset(rng.Next(-40, 41), rng.Next(-40, 41)) });
                break;
            }
            case 7 when diagram != null && blocks.Count > 0:
                m.AddNode(diagram, Pick(blocks), new Bounds(rng.Next(800), rng.Next(600), 120, 60));
                break;
            case 8 when blocks.Count > 1:
                m.Connect(ElementKind.Dependency, Pick(blocks), Pick(blocks));
                break;
            case 9 when m.History.CanUndo:
                m.History.Undo();
                break;
            case 10 when m.History.CanRedo:
                m.History.Redo();
                break;
            case 11 when all.Count > 0:
            {
                var target = Pick(all);
                if (session.TryLock(t.Ws.ParticipantId, target.Id, subtree: rng.Next(2) == 0) != null)
                    foreach (var l in session.Locks.Where(l => l.ParticipantId == t.Ws.ParticipantId)) session.Unlock(l.ElementId, t.Ws.User!.Id);
                break;
            }
        }
    }
}
