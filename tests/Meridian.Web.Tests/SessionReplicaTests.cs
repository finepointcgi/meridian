using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meridian.Web.Tests;

/// <summary>
/// Tabs get in-memory replicas of the session's model (ModelRepository.CreateReplica) when they join or must start over: each
/// replica equals the authoritative model, belongs to its tab alone, and keeps converging through relayed operations.
/// </summary>
public class SessionReplicaTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-sr-{Guid.NewGuid():N}.db");
    private SqliteStore _db = null!;
    private CollaborationHub _hub = null!;
    private Guid _project;
    private UserAccount _ann = null!, _ben = null!;

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        _ann = await _db.CreateUserAsync("ann", "Ann Lee", "correct horse");
        _ben = await _db.CreateUserAsync("ben", "Ben Ito", "battery staple");
        _project = await _db.CreateProjectAsync("Vehicle", ProjectFile.Serialize(SampleModels.Vehicle()), _ann.Id);
        await _db.SetMemberAsync(_project, _ben.Id, ProjectRole.Editor);
        _hub = new CollaborationHub(_db, NullLogger<CollaborationHub>.Instance);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    private async Task<(Workspace Ann, Workspace Ben)> TwoTabsAsync()
    {
        var ann = new Workspace { User = _ann };
        var ben = new Workspace { User = _ben };
        Assert.True(await ann.JoinAsync(_hub, _project, ProjectRole.Owner));
        Assert.True(await ben.JoinAsync(_hub, _project, ProjectRole.Editor));
        return (ann, ben);
    }

    private static void AssertSeparate(ModelRepository a, ModelRepository b)
    {
        Assert.NotSame(a, b);
        foreach (var e in a.AllElements) Assert.NotSame(e, b.Find(e.Id));
    }

    [Fact]
    public async Task Joining_Tabs_Get_Their_Own_Replica_Of_The_Session_Model()
    {
        var (ann, ben) = await TwoTabsAsync();
        var session = ann.Session!;
        Assert.Equal(session.Snapshot(), ProjectFile.Serialize(ann.Model));
        Assert.Equal(session.Snapshot(), ProjectFile.Serialize(ben.Model));
        AssertSeparate(ann.Model, ben.Model);

        // Each tab edits its own copy; the other sees the change through the session, nobody else's copy changes early.
        var engine = ann.Model.OfKind(ElementKind.Block).Single(b => b.Name == "Engine");
        var bensEngine = ben.Model.Find(engine.Id)!;
        ann.Model.Set(engine, FieldKeys.Documentation, "Electric");
        Assert.Equal("Electric", bensEngine.Documentation);
        ben.Model.Rename(bensEngine, "Motor");
        Assert.Equal("Motor", engine.Name);
        ann.Model.History.Undo();
        Assert.Equal("", bensEngine.Documentation);
        Assert.Equal(session.Snapshot(), ProjectFile.Serialize(ann.Model));
        Assert.Equal(session.Snapshot(), ProjectFile.Serialize(ben.Model));

        // A third tab joining the unchanged session gets the same model again.
        var third = new Workspace { User = _ann };
        Assert.True(await third.JoinAsync(_hub, _project, ProjectRole.Owner));
        Assert.Equal(session.Snapshot(), ProjectFile.Serialize(third.Model));
        AssertSeparate(third.Model, ann.Model);
        foreach (var ws in new[] { ann, ben, third }) ws.Dispose();
    }

    [Fact]
    public async Task A_Replaced_Model_Reaches_Every_Tab_As_Its_Own_Replica()
    {
        var (ann, ben) = await TwoTabsAsync();
        var session = ann.Session!;
        var merged = ProjectFile.Copy(SampleModels.Vehicle());
        merged.Create(merged.Root, ElementKind.Package, "Merged in");
        session.Replace(ProjectFile.Serialize(merged), _ann.Id, "Merged");

        Assert.Equal(session.Snapshot(), ProjectFile.Serialize(ann.Model));
        Assert.Equal(session.Snapshot(), ProjectFile.Serialize(ben.Model));
        Assert.Contains(ann.Model.AllElements, e => e.Name == "Merged in");
        AssertSeparate(ann.Model, ben.Model);

        // The new replicas keep converging, and a tab joining now (the frozen copy is current) gets the same model.
        var pkg = ben.Model.AllElements.Single(e => e.Name == "Merged in");
        ben.Model.Create(pkg, ElementKind.Block, "Added after the merge");
        Assert.Contains(ann.Model.AllElements, e => e.Name == "Added after the merge");
        var third = new Workspace { User = _ann };
        Assert.True(await third.JoinAsync(_hub, _project, ProjectRole.Owner));
        Assert.Equal(session.Snapshot(), ProjectFile.Serialize(third.Model));
        AssertSeparate(third.Model, ann.Model);
        foreach (var ws in new[] { ann, ben, third }) ws.Dispose();
    }

    [Fact]
    public async Task A_Join_While_A_Frozen_Copy_Is_Current_Copies_It()
    {
        var (ann, ben) = await TwoTabsAsync();
        var session = ann.Session!;
        session.Replace(session.Snapshot(), _ann.Id, "Reloaded");   // makes a frozen copy for both tabs
        var third = new Workspace { User = _ben };
        Assert.True(await third.JoinAsync(_hub, _project, ProjectRole.Editor));
        AssertSeparate(third.Model, ann.Model);
        AssertSeparate(third.Model, ben.Model);
        third.Model.Rename(third.Model.OfKind(ElementKind.Block).First(), "Renamed by the third tab");
        Assert.Equal(session.Snapshot(), ProjectFile.Serialize(ann.Model));
        Assert.Equal(session.Snapshot(), ProjectFile.Serialize(third.Model));
        foreach (var ws in new[] { ann, ben, third }) ws.Dispose();
    }
}
