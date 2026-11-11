using Meridian.Data;
using Meridian.Data.History;
using Meridian.Serialization.History;

namespace Meridian.Tests.StoreContract;

/// <summary>Element history: the per-element change index (element_change_versions, element_changes).</summary>
public abstract partial class StoreContract
{
    private static ElementChange Change(string id, ElementChangeKind kind, params string[] fields) =>
        new(id, kind, fields, "Block", $"Label of {id}", "_owner", null, null);

    [SkippableFact]
    public async Task Element_Change_Index_Round_Trips_And_Is_Written_Once_Per_Version()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann", "password-1");
        var project = await s.CreateProjectAsync("P", Json, ann.Id);
        var other = await s.CreateProjectAsync("Other", Json, ann.Id);
        var v1 = await s.CommitBranchVersionAsync(project, null, "Start", Json, ann.Id);
        var branch = await s.CreateBranchAsync(project, "feature", Json, Json, v1.Id, ann.Id);
        var v2 = await s.CommitBranchVersionAsync(project, branch, "On the branch", Json, null);
        var v3 = await s.CommitBranchVersionAsync(project, null, "Merge it", Json, ann.Id);
        var foreign = await s.CommitBranchVersionAsync(other, null, "Elsewhere", Json, ann.Id);

        var versions = await s.ListHistoryVersionsAsync(project);
        Assert.Equal([1, 2, 3], versions.Select(v => v.Number));
        Assert.All(versions, v => Assert.False(v.Indexed));
        Assert.Equal(("Ann", (Guid?)ann.Id, "main", (Guid?)null), (versions[0].Author, versions[0].AuthorId, versions[0].BranchName, versions[0].BranchId));
        Assert.Equal(("unknown", (Guid?)null, "feature", (Guid?)branch), (versions[1].Author, versions[1].AuthorId, versions[1].BranchName, versions[1].BranchId));
        IsRecentUtc(versions[0].CreatedAt);

        // The kind recorded at commit time wins over what indexing guesses later.
        await s.SetVersionKindAsync(project, v3.Id, HistoryVersionKind.Merge, "feature");
        await s.SetVersionKindAsync(project, foreign.Id, HistoryVersionKind.Merge, "nope");   // another project's version: nothing happens
        await s.SetVersionKindAsync(project, v1.Id + 999_999, HistoryVersionKind.Merge, "x");   // no such version: nothing happens

        var label = new string('x', 1000) + " ünïcödé";
        Assert.True(await s.SaveElementChangesAsync(project, v2.Id, v1.Id, HistoryVersionKind.System, null,
        [
            Change("_a", ElementChangeKind.Changed, "name", "documentation", "tag.Mass (kg)") with { Label = label },
            new("_rel", ElementChangeKind.Added, [], "Satisfy", "«satisfy» A → R", "_pkg", "_a", "_r"),
            new("_self", ElementChangeKind.Added, [], "Dependency", "loop", "_pkg", "_a", "_a"),
            new("_a", ElementChangeKind.AddedToDiagram, [], "Block", "A", "_owner", null, null, "_diagram", "Overview"),
        ]));
        Assert.False(await s.SaveElementChangesAsync(project, v2.Id, v1.Id, HistoryVersionKind.System, null, [Change("_b", ElementChangeKind.Removed)]));   // already indexed
        Assert.False(await s.SaveElementChangesAsync(project, foreign.Id, null, HistoryVersionKind.Commit, null, [Change("_a", ElementChangeKind.Removed)]));   // another project's version
        Assert.True(await s.SaveElementChangesAsync(project, v3.Id, v1.Id, HistoryVersionKind.Commit, null, [Change("_a", ElementChangeKind.Moved, "owner")]));
        Assert.True(await s.SaveElementChangesAsync(project, v1.Id, null, HistoryVersionKind.Commit, null, []));

        versions = await s.ListHistoryVersionsAsync(project);
        Assert.All(versions, v => Assert.True(v.Indexed));
        Assert.Equal((v1.Id, HistoryVersionKind.System, false), (versions[1].ParentVersionId, versions[1].Kind, versions[1].KindRecorded));
        Assert.Equal((HistoryVersionKind.Merge, "feature", true), (versions[2].Kind, versions[2].MergeSource, versions[2].KindRecorded));
        Assert.Null(versions[0].ParentVersionId);

        var own = await s.ListElementChangesAsync(project, "_a", includeConnected: false);
        Assert.Equal([(v3.Id, ElementChangeKind.Moved), (v2.Id, ElementChangeKind.Changed), (v2.Id, ElementChangeKind.AddedToDiagram)], own.Select(r => (r.VersionId, r.Change)));
        var changed = own[1];
        Assert.Equal(["name", "documentation", "tag.Mass (kg)"], changed.Fields);
        Assert.Equal(("Block", "_owner", (string?)null, (string?)null), (changed.ElementKind, changed.OwnerId, changed.SourceId, changed.TargetId));
        Assert.True(changed.Label.Length <= 401);
        Assert.Equal(("_diagram", "Overview"), (own[2].DiagramId, own[2].DiagramName));
        Assert.Empty(own[0].Fields.Except(["owner"]));

        var connected = await s.ListElementChangesAsync(project, "_a", includeConnected: true);
        Assert.Equal(5, connected.Count);   // a relationship from the element to itself is listed once
        var rel = connected.Single(r => r.ElementId == "_rel");
        Assert.Equal(("_a", "_r", "_pkg", "Satisfy", "«satisfy» A → R"), (rel.SourceId, rel.TargetId, rel.OwnerId, rel.ElementKind, rel.Label));
        Assert.Single(await s.ListElementChangesAsync(project, "_r", includeConnected: true));
        Assert.Empty(await s.ListElementChangesAsync(other, "_a", includeConnected: true));

        // Reset forgets the rows (the recorded kind stays); deleting the project removes everything.
        await s.ResetElementChangesAsync(project);
        versions = await s.ListHistoryVersionsAsync(project);
        Assert.All(versions, v => Assert.False(v.Indexed));
        Assert.Equal((HistoryVersionKind.Merge, "feature"), (versions[2].Kind, versions[2].MergeSource));
        Assert.Empty(await s.ListElementChangesAsync(project, "_a", includeConnected: true));
        Assert.True(await s.SaveElementChangesAsync(project, v2.Id, v1.Id, HistoryVersionKind.Commit, null, [Change("_a", ElementChangeKind.Changed, "name")]));
        await s.DeleteProjectAsync(project);
        Assert.Equal(0L, Convert.ToInt64(await h.ScalarRawAsync("select count(*) from element_changes")));
        Assert.Equal(0L, Convert.ToInt64(await h.ScalarRawAsync("select count(*) from element_change_versions")));
    }

    [SkippableFact]
    public async Task Element_Changes_Of_A_Version_Are_Written_Once_By_Concurrent_Indexers()
    {
        await using var h = await OpenAsync();
        var ann = await h.Store.CreateUserAsync("ann", "Ann", "password-1");
        var project = await h.Store.CreateProjectAsync("P", Json, ann.Id);
        var v = await h.Store.CommitBranchVersionAsync(project, null, "Big", Json, ann.Id);
        var rows = Enumerable.Range(0, 5000).Select(i => Change($"_e{i}", ElementChangeKind.Changed, "name")).ToList();
        var stores = Enumerable.Range(0, 3).Select(_ => h.OpenAnotherStore()).ToList();   // several server processes
        var results = await Task.WhenAll(stores.Select(st => Task.Run(() => st.SaveElementChangesAsync(project, v.Id, null, HistoryVersionKind.Commit, null, rows))));
        Assert.Single(results, r => r);
        Assert.Equal(5000L, Convert.ToInt64(await h.ScalarRawAsync("select count(*) from element_changes")));
        Assert.Equal(["name"], (await h.Store.ListElementChangesAsync(project, "_e4999", false)).Single().Fields);
    }
}
