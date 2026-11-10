using Meridian.Data;

namespace Meridian.Tests.StoreContract;

/// <summary>Code-sync run records (one per push applied to a review branch).</summary>
public abstract partial class StoreContract
{
    private static CodeSyncRunRecord Run(Guid project, Guid? branch, string commit, CodeSyncRunStatus status, params CodeSyncRunChange[] changes) =>
        new(Guid.NewGuid(), project, Guid.NewGuid(), branch, "acme/sat", commit, $"https://git.example/c/{commit}", "Dana Dev", "Add battery\n\nbody",
            status, "1 added", changes.Count(c => c.Kind == CodeSyncChangeKind.Added), changes.Count(c => c.Kind == CodeSyncChangeKind.Changed),
            changes.Count(c => c.Kind == CodeSyncChangeKind.Removed), CodeSyncRuns.SerializeChanges(changes), DateTime.UtcNow, DateTime.UtcNow);

    [SkippableFact]
    public async Task Code_Sync_Runs_Round_Trip_List_Newest_First_And_Update()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann", "password-1");
        var pid = await s.CreateProjectAsync("P", Json, ann.Id);
        var other = await s.CreateProjectAsync("Q", Json, ann.Id);
        var branch = await s.CreateBranchAsync(pid, "code-sync/acme/sat", Json, Json, null, null);
        Assert.Empty(await s.ListCodeSyncRunsAsync(pid));

        var first = Run(pid, branch, "aaaaaaa1", CodeSyncRunStatus.AwaitingReview,
            new CodeSyncRunChange("_b1", CodeSyncChangeKind.Added, "Battery", "Structure", "Block"),
            new CodeSyncRunChange("_e1", CodeSyncChangeKind.Changed, "Engine", "Structure", "Block"));
        await s.AddCodeSyncRunAsync(first);
        var failed = Run(pid, null, "bbbbbbb2", CodeSyncRunStatus.Failed) with { CommitUrl = null, Author = null, Message = null, Summary = "boom" };
        await s.AddCodeSyncRunAsync(failed);
        await s.AddCodeSyncRunAsync(Run(other, null, "ccccccc3", CodeSyncRunStatus.NoChanges));

        var got = await s.GetCodeSyncRunAsync(first.Id);
        Assert.NotNull(got);
        Assert.Equal((first.ProjectId, first.ConnectionId, first.BranchId, first.Repo, first.Commit, first.CommitUrl, first.Author, first.Message),
                     (got!.ProjectId, got.ConnectionId, got.BranchId, got.Repo, got.Commit, got.CommitUrl, got.Author, got.Message));
        Assert.Equal((CodeSyncRunStatus.AwaitingReview, "1 added", 1, 1, 0), (got.Status, got.Summary, got.Added, got.Changed, got.Removed));
        Assert.Equal(first.Changes, got.Changes);
        IsRecentUtc(got.CreatedAt);
        IsRecentUtc(got.UpdatedAt);
        Assert.Null(await s.GetCodeSyncRunAsync(Guid.NewGuid()));

        var list = await s.ListCodeSyncRunsAsync(pid);
        Assert.Equal([failed.Id, first.Id], list.Select(r => r.Id));   // newest first, only this project
        Assert.Equal((null, null, null, null), (list[0].BranchId, list[0].CommitUrl, list[0].Author, list[0].Message));
        Assert.Single(await s.ListCodeSyncRunsAsync(pid, limit: 1));

        var decided = first.Changes.Select(c => c with { State = CodeSyncChangeState.Accepted }).ToList();
        await s.UpdateCodeSyncRunAsync(first.Id, CodeSyncRunStatus.Accepted, "1 added — 2 accepted", CodeSyncRuns.SerializeChanges(decided));
        var after = await h.OpenAnotherStore().GetCodeSyncRunAsync(first.Id);   // another server process sees it
        Assert.Equal((CodeSyncRunStatus.Accepted, "1 added — 2 accepted"), (after!.Status, after.Summary));
        Assert.Equal(decided, after.Changes);
        Assert.True(after.UpdatedAt >= got.UpdatedAt);

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.UpdateCodeSyncRunAsync(first.Id, CodeSyncRunStatus.Accepted, "x", "{\"not\":\"an array\"}"));
    }

    [SkippableFact]
    public async Task Code_Sync_Runs_Go_With_Their_Project_And_Survive_Remigration()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann", "password-1");
        var pid = await s.CreateProjectAsync("P", Json, ann.Id);
        var run = Run(pid, null, "aaaaaaa1", CodeSyncRunStatus.AwaitingReview, new CodeSyncRunChange("_x", CodeSyncChangeKind.Removed, "X", "", "Block"));
        await s.AddCodeSyncRunAsync(run);
        await s.MigrateAsync();
        Assert.Equal(run.Changes, (await s.GetCodeSyncRunAsync(run.Id))!.Changes);
        await s.DeleteProjectAsync(pid);
        Assert.Null(await s.GetCodeSyncRunAsync(run.Id));
        Assert.Empty(await s.ListCodeSyncRunsAsync(pid));
    }
}
