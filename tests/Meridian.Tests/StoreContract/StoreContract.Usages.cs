using System.Data.Common;
using Meridian.Core.Libraries;
using Meridian.Data;

namespace Meridian.Tests.StoreContract;

/// <summary>Used projects / shared model libraries (project_usages).</summary>
public abstract partial class StoreContract
{
    [SkippableFact]
    public async Task Project_Usages_Round_Trip_Update_And_Remove()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann", "password-1");
        var vehicle = await s.CreateProjectAsync("Vehicle", Json, ann.Id);
        var parts = await s.CreateProjectAsync("Parts Library", Json, ann.Id);
        var other = await s.CreateProjectAsync("Other", Json, ann.Id);
        Assert.Empty(await s.ListProjectUsagesAsync(vehicle));

        var u1 = await s.AddProjectUsageAsync(vehicle, parts, "_std", LibraryPinKind.Version, "3", null, ann.Id);
        await Task.Delay(5);
        var u2 = await s.AddProjectUsageAsync(vehicle, other, "_misc", LibraryPinKind.Branch, "main", "_structure", null);
        await s.AddProjectUsageAsync(other, parts, "_std", LibraryPinKind.Baseline, "7", null, ann.Id);

        var list = await s.ListProjectUsagesAsync(vehicle);
        Assert.Equal([u1, u2], list.Select(u => u.Id));   // oldest first (the load order)
        var a = list[0];
        Assert.Equal((vehicle, parts, "Parts Library", "_std", LibraryPinKind.Version, "3", (string?)null, (Guid?)ann.Id, "Ann"),
                     (a.ProjectId, a.UsedProjectId, a.UsedProjectName, a.RootPackageId, a.PinKind, a.PinValue, a.MountPointId, a.AddedBy, a.AddedByName));
        IsRecentUtc(a.AddedAt);
        Assert.Equal((LibraryPinKind.Branch, "main", "_structure", (Guid?)null), (list[1].PinKind, list[1].PinValue, list[1].MountPointId, list[1].AddedBy));
        Assert.Equal(u1.ToString("N"), a.Request.UsageId);
        Assert.Equal(2, (await s.ListProjectUsersAsync(parts)).Count);   // who uses the parts library

        // One usage per package; a project can't use itself; pins must be given.
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AddProjectUsageAsync(vehicle, parts, "_std", LibraryPinKind.Version, "4", null, ann.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AddProjectUsageAsync(vehicle, vehicle, "_x", LibraryPinKind.Version, "1", null, ann.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AddProjectUsageAsync(vehicle, parts, "_y", LibraryPinKind.Version, " ", null, ann.Id));
        await Assert.ThrowsAnyAsync<DbException>(() => h.ExecRawAsync(
            "insert into project_usages(id, project_id, used_project_id, root_package_id, pin_kind, pin_value, added_at) values ($1,$2,$3,'_z','tag','1',$4)",
            Guid.NewGuid(), vehicle, parts, DateTime.UtcNow));

        // Moving the pin (and the mount point); another server process sees it.
        await s.UpdateProjectUsageAsync(u1, LibraryPinKind.Branch, "release", "_structure");
        var moved = await h.OpenAnotherStore().GetProjectUsageAsync(u1);
        Assert.Equal((LibraryPinKind.Branch, "release", "_structure"), (moved!.PinKind, moved.PinValue, moved.MountPointId));
        await s.UpdateProjectUsageAsync(u1, LibraryPinKind.Version, "5", null);
        Assert.Null((await s.GetProjectUsageAsync(u1))!.MountPointId);

        await s.RemoveProjectUsageAsync(u2);
        Assert.Equal([u1], (await s.ListProjectUsagesAsync(vehicle)).Select(u => u.Id));
        Assert.Null(await s.GetProjectUsageAsync(u2));
    }

    [SkippableFact]
    public async Task Project_Usages_Survive_Remigration_And_Outlive_The_Used_Project()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann", "password-1");
        var vehicle = await s.CreateProjectAsync("Vehicle", Json, ann.Id);
        var parts = await s.CreateProjectAsync("Parts Library", Json, ann.Id);
        var u = await s.AddProjectUsageAsync(vehicle, parts, "_std", LibraryPinKind.Version, "1", null, ann.Id);
        await s.MigrateAsync();
        await h.OpenAnotherStore().MigrateAsync();
        Assert.Single(await s.ListProjectUsagesAsync(vehicle));

        // Deleting the used project keeps the usage (reported as unavailable, with an empty name) …
        await s.DeleteProjectAsync(parts);
        var kept = Assert.Single(await s.ListProjectUsagesAsync(vehicle));
        Assert.Equal((u, ""), (kept.Id, kept.UsedProjectName));
        // … deleting the using project removes it.
        await s.DeleteProjectAsync(vehicle);
        Assert.Null(await s.GetProjectUsageAsync(u));
    }
}
