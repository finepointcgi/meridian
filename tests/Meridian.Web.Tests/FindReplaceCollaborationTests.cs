using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meridian.Web.Tests;

/// <summary>
/// Find and replace through the real collaboration session (docs/COLLABORATION.md): the replace is one batch, a field someone
/// else changed after the preview is skipped, read-only packages are never part of the batch (so the server never refuses
/// it), and every tab converges.
/// </summary>
public sealed class FindReplaceCollaborationTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-findreplace-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;
    private CollaborationHub _hub = default!;
    private UserAccount _alice = default!, _bob = default!;
    private readonly List<Tab> _tabs = new();

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        _hub = new CollaborationHub(_db, NullLogger<CollaborationHub>.Instance);
        _alice = await _db.CreateUserAsync("alice", "Alice", "correct horse");
        _bob = await _db.CreateUserAsync("bob", "Bob", "battery staple");
    }

    public async Task DisposeAsync()
    {
        foreach (var t in _tabs) t.Ws.Dispose();
        await _hub.FlushAllAsync();
        await Task.Delay(50);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            try { File.Delete(f); } catch (IOException) { }
    }

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
        public void Deliver() { while (_inbox.TryDequeue(out var a)) a(); }
        public Element E(string name) => M.AllElements.First(e => e.Name == name);
    }

    private async Task<Guid> Project()
    {
        var id = await _db.CreateProjectAsync("Find " + Guid.NewGuid().ToString("N")[..6], ProjectFile.Serialize(SampleModels.Vehicle()), _alice.Id);
        await _db.SetMemberAsync(id, _bob.Id, ProjectRole.Editor);
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

    private void AssertConverged(Guid project)
    {
        var session = _hub.Find(project)!;
        session.FlushResyncs();
        DeliverAll();
        foreach (var t in _tabs)
        {
            Assert.Equal(0, t.Ws.PendingBatches);
            Assert.True(session.SnapshotFor(t.Ws.ParticipantId) == ProjectFile.Serialize(t.M), $"{t.Ws.User!.DisplayName}'s tab diverged");
        }
    }

    private static ReplacePlan PlanAll(Tab t, string find, string replace, SearchIn where = SearchIn.Names | SearchIn.Documentation)
    {
        var result = FindReplace.Run(t.M, new FindQuery(find) { Replace = replace, MatchCase = true, In = where, Access = t.Ws.Access });
        return FindReplace.Plan(t.M, result.Hits.SelectMany(h => h.Fields.Select(f => (h, f, (IReadOnlyCollection<TextMatch>?)null))));
    }

    [Fact]
    public async Task A_Field_Changed_By_Someone_Else_After_The_Preview_Is_Skipped_And_The_Rest_Converges()
    {
        var p = await Project();
        var (a, b) = (await Open(p, _alice), await Open(p, _bob));
        var plan = PlanAll(a, "Engine", "Motor");
        var engineId = a.E("Engine").Id;

        b.M.Rename(b.E("Engine"), "Engine Mk2");   // Bob's rename reaches Alice between her preview and her apply
        DeliverAll();
        Assert.Equal("Engine Mk2", a.M.Find(engineId)!.Name);

        var batches = 0;
        a.M.History.ActionCompleted += () => batches++;
        var outcome = FindReplace.ApplyPlan(a.M, plan);
        Assert.Equal(1, batches);
        Assert.Contains(outcome.Skipped, s => s.Element?.Id == engineId && s.SkipReason!.Contains("since the preview"));
        Assert.True(outcome.Applied > 2);
        AssertConverged(p);
        Assert.Equal("Engine Mk2", b.M.Find(engineId)!.Name);
        Assert.Contains(b.M.OfKind(ElementKind.UseCase), u => u.Name == "Start Motor");

        // Alice's undo takes the whole replace back for everyone.
        a.M.History.Undo();
        AssertConverged(p);
        Assert.Contains(b.M.OfKind(ElementKind.UseCase), u => u.Name == "Start Engine");
        Assert.Equal("Engine Mk2", b.M.Find(engineId)!.Name);
    }

    [Fact]
    public async Task Read_Only_Packages_Stay_Out_Of_The_Batch_So_The_Server_Accepts_It()
    {
        var p = await Project();
        var (a, b) = (await Open(p, _alice), await Open(p, _bob));
        var structure = a.M.AllElements.First(e => e.Kind == ElementKind.Package && e.Name == "Structure").Id;
        await _db.SetPackagePermissionAsync(p, structure, PrincipalKind.User, _bob.Id, PackageAccess.Read, _alice.Id);
        await _hub.ReloadPermissionsAsync(p);
        DeliverAll();

        var plan = PlanAll(b, "Engine", "Motor");
        Assert.Contains(plan.Changes, c => c.Skipped && c.Element!.Name == "Engine" && c.SkipReason!.Contains("Structure"));
        var outcome = FindReplace.ApplyPlan(b.M, plan);
        Assert.True(outcome.Applied > 0);
        Assert.DoesNotContain("read-only", b.Ws.Status);   // nothing was refused
        AssertConverged(p);
        Assert.Equal("Engine", a.E("Engine").Name);
        Assert.Contains(a.M.OfKind(ElementKind.UseCase), u => u.Name == "Start Motor");
    }
}
