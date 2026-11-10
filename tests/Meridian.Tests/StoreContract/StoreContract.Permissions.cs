using System.Data.Common;
using Meridian.Core.Permissions;
using Meridian.Data;

namespace Meridian.Tests.StoreContract;

/// <summary>Per-package permissions (package_permissions): rules for users and teams, team membership lookups, cascades.</summary>
public abstract partial class StoreContract
{
    [SkippableFact]
    public async Task Package_Permissions_Round_Trip_Upsert_And_Remove()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var alice = await s.CreateUserAsync("alice", "Alice", "password-1");
        var bob = await s.CreateUserAsync("bob", "Bob", "password-2");
        var pid = await s.CreateProjectAsync("P", Json, alice.Id);
        var other = await s.CreateProjectAsync("Q", Json, alice.Id);
        var org = await s.CreateOrganizationAsync("Acme");
        var team = await s.CreateTeamAsync(org, "Avionics");
        Assert.Empty(await s.ListPackagePermissionsAsync(pid));

        await s.SetPackagePermissionAsync(pid, "_pkgA", PrincipalKind.User, bob.Id, PackageAccess.Read, alice.Id);
        await s.SetPackagePermissionAsync(pid, "_pkgB", PrincipalKind.Team, team, PackageAccess.None, alice.Id);
        await s.SetPackagePermissionAsync(other, "_pkgA", PrincipalKind.User, bob.Id, PackageAccess.Manage, null);

        var rules = await s.ListPackagePermissionsAsync(pid);
        Assert.Equal(2, rules.Count);
        var a = rules.Single(r => r.ElementId == "_pkgA");
        Assert.Equal((PrincipalKind.User, bob.Id, PackageAccess.Read, "Bob"), (a.PrincipalKind, a.PrincipalId, a.Level, a.PrincipalName));
        IsRecentUtc(a.UpdatedAt);
        var b = rules.Single(r => r.ElementId == "_pkgB");
        Assert.Equal((PrincipalKind.Team, team, PackageAccess.None, "Avionics"), (b.PrincipalKind, b.PrincipalId, b.Level, b.PrincipalName));
        Assert.Equal(new PermissionRule("_pkgB", PrincipalKind.Team, team, PackageAccess.None), b.Rule);

        // Setting again replaces the level (one rule per principal and package); another process sees it.
        await s.SetPackagePermissionAsync(pid, "_pkgA", PrincipalKind.User, bob.Id, PackageAccess.Edit, alice.Id);
        Assert.Equal(PackageAccess.Edit, (await h.OpenAnotherStore().ListPackagePermissionsAsync(pid)).Single(r => r.ElementId == "_pkgA").Level);

        await s.RemovePackagePermissionAsync(pid, "_pkgA", PrincipalKind.User, bob.Id);
        Assert.Equal(["_pkgB"], (await s.ListPackagePermissionsAsync(pid)).Select(r => r.ElementId));
        Assert.Single(await s.ListPackagePermissionsAsync(other));   // other projects untouched

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.SetPackagePermissionAsync(pid, " ", PrincipalKind.User, bob.Id, PackageAccess.Read, null));
        // The database itself refuses levels and principal kinds it doesn't know.
        await Assert.ThrowsAnyAsync<DbException>(() => h.ExecRawAsync(
            "insert into package_permissions(project_id, element_id, principal_kind, principal_id, level, updated_at) values ($1,'_x','user',$2,'owner',$3)",
            pid, bob.Id, DateTime.UtcNow));
    }

    [SkippableFact]
    public async Task Package_Permissions_Teams_Cascade_And_Survive_Remigration()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var alice = await s.CreateUserAsync("alice", "Alice", "password-1");
        var bob = await s.CreateUserAsync("bob", "Bob", "password-2");
        var pid = await s.CreateProjectAsync("P", Json, alice.Id);
        var org = await s.CreateOrganizationAsync("Acme");
        var t1 = await s.CreateTeamAsync(org, "Avionics");
        var t2 = await s.CreateTeamAsync(org, "Power");
        Assert.Empty(await s.UserTeamIdsAsync(bob.Id));
        await s.AddTeamMemberAsync(t1, bob.Id);
        await s.AddTeamMemberAsync(t2, bob.Id);
        Assert.Equal(new[] { t1, t2 }.Order(), (await s.UserTeamIdsAsync(bob.Id)).Order());

        await s.SetTeamGrantAsync(t1, pid, ProjectRole.Editor);
        var teams = await s.ProjectTeamsAsync(pid);
        Assert.Equal([(t1, "Avionics", ProjectRole.Editor)], teams.Select(t => (t.Id, t.Name, t.Role)));

        await s.SetPackagePermissionAsync(pid, "_pkg", PrincipalKind.Team, t1, PackageAccess.Read, alice.Id);
        await s.SetPackagePermissionAsync(pid, "_pkg", PrincipalKind.Team, t2, PackageAccess.Edit, alice.Id);
        await s.SetPackagePermissionAsync(pid, "_pkg", PrincipalKind.User, bob.Id, PackageAccess.None, alice.Id);
        await s.MigrateAsync();
        Assert.Equal(3, (await s.ListPackagePermissionsAsync(pid)).Count);

        await s.DeleteTeamAsync(t2);   // a deleted team's rules go with it
        Assert.DoesNotContain(await s.ListPackagePermissionsAsync(pid), r => r.PrincipalId == t2);
        Assert.Equal([t1], await s.UserTeamIdsAsync(bob.Id));

        await s.DeleteProjectAsync(pid);   // and a deleted project's rules
        Assert.Empty(await s.ListPackagePermissionsAsync(pid));
    }
}
