using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>The desktop app's local store: same behaviour as the PostgreSQL server store, in one file.</summary>
public sealed class SqliteStoreTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-test-{Guid.NewGuid():N}.db");
    private SqliteStore _store = default!;

    public async Task InitializeAsync()
    {
        _store = new SqliteStore(_path);
        await _store.MigrateAsync();
        await _store.MigrateAsync(); // idempotent
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Accounts_Projects_Versions_And_Comments_Work_End_To_End()
    {
        var ann = await _store.CreateUserAsync("Ann", "Ann Lee", "correct horse");
        var ben = await _store.CreateUserAsync("ben", "", "battery staple");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.CreateUserAsync("ann", "dup", "whatever123"));
        Assert.Equal(ann.Id, (await _store.VerifyUserAsync("ANN", "correct horse"))!.Id);
        Assert.Null(await _store.VerifyUserAsync("ann", "wrong password"));

        var json = ProjectFile.Serialize(SampleModels.Vehicle());
        var pid = await _store.CreateProjectAsync("Vehicle", json, ann.Id);
        Assert.Equal(ProjectRole.Owner, await _store.RoleOfAsync(pid, ann.Id));
        Assert.Null(await _store.RoleOfAsync(pid, ben.Id));
        await _store.SetMemberAsync(pid, ben.Id, ProjectRole.Editor);
        await _store.SetMemberAsync(pid, ben.Id, ProjectRole.Viewer);
        Assert.Equal(ProjectRole.Viewer, await _store.RoleOfAsync(pid, ben.Id));
        Assert.Equal(2, (await _store.MembersAsync(pid)).Count);

        Assert.Equal(1, await _store.SaveProjectAsync(pid, json, ben.Id));
        var loaded = await _store.LoadProjectAsync(pid);
        Assert.Equal(1, loaded!.Value.Revision);
        Assert.Equal("Ben", (await _store.ListProjectsAsync(ann.Id)).Single().UpdatedBy, ignoreCase: true);

        Assert.Equal(1, await _store.CommitVersionAsync(pid, "first", json, ann.Id));
        Assert.Equal(2, await _store.CommitVersionAsync(pid, "second", json, null));
        var versions = await _store.ListVersionsAsync(pid);
        Assert.Equal([2, 1], versions.Select(v => v.Number));
        Assert.Equal(json, await _store.LoadVersionAsync(pid, versions[1].Id));

        var cid = await _store.AddCommentAsync(pid, "el-1", ben.Id, "Looks off");
        await _store.SetCommentResolvedAsync(cid, true);
        var comment = (await _store.ListCommentsAsync(pid)).Single();
        Assert.True(comment.Resolved);
        Assert.Equal("ben", comment.Author.Username);

        await _store.RenameProjectAsync(pid, "  Car  ");
        Assert.Equal("Car", (await _store.LoadProjectAsync(pid))!.Value.Name);
        await _store.DeleteProjectAsync(pid);
        Assert.Null(await _store.LoadProjectAsync(pid));
        Assert.Empty(await _store.ListCommentsAsync(pid)); // cascades
    }
}
