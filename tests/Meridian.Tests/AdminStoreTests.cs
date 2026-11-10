using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Microsoft.Data.Sqlite;

namespace Meridian.Tests;

/// <summary>Enterprise administration in the store: server roles, lockout, SSO links, teams, transfer, restore and the audit log.</summary>
public sealed class AdminStoreTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-admin-{Guid.NewGuid():N}.db");
    private SqliteStore _store = default!;
    private static readonly string Json = ProjectFile.Serialize(SampleModels.Vehicle());

    public async Task InitializeAsync()
    {
        _store = new SqliteStore(_path);
        await _store.MigrateAsync();
        await _store.MigrateAsync(); // idempotent, including the added user columns
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task First_Registered_User_Becomes_Admin_Once()
    {
        var ann = await _store.CreateUserAsync("ann", "Ann", "password-1");
        var ben = await _store.CreateUserAsync("ben", "Ben", "password-2");
        Assert.True(await _store.EnsureAdminAsync());
        Assert.False(await _store.EnsureAdminAsync());
        Assert.True((await _store.GetUserAsync(ann.Id))!.IsAdmin);
        Assert.False((await _store.GetUserAsync(ben.Id))!.IsAdmin);

        await _store.SetUserAdminAsync(ben.Id, true);
        var users = await _store.ListUsersAsync();
        Assert.Equal(2, users.Count(u => u.IsAdmin));
        Assert.All(users, u => Assert.True(u.HasPassword));
    }

    [Fact]
    public async Task Repeated_Failures_Lock_The_Account_And_Disabled_Accounts_Are_Refused()
    {
        var ann = await _store.CreateUserAsync("ann", "Ann", "correct horse");
        for (var i = 0; i < 2; i++)
            Assert.Equal(LoginStatus.InvalidCredentials, (await _store.AuthenticateAsync("ann", "nope", 3, TimeSpan.FromMinutes(5))).Status);
        var locked = await _store.AuthenticateAsync("ann", "nope", 3, TimeSpan.FromMinutes(5));
        Assert.Equal(LoginStatus.LockedOut, locked.Status);
        Assert.True(locked.LockedUntil > DateTime.UtcNow);
        // Even the right password is refused while locked.
        Assert.Equal(LoginStatus.LockedOut, (await _store.AuthenticateAsync("ann", "correct horse", 3, TimeSpan.FromMinutes(5))).Status);
        Assert.True((await _store.GetUserAsync(ann.Id))!.IsLockedOut(DateTime.UtcNow));

        await _store.UnlockUserAsync(ann.Id);
        var ok = await _store.AuthenticateAsync("ANN", "correct horse", 3, TimeSpan.FromMinutes(5));
        Assert.Equal(LoginStatus.Success, ok.Status);
        Assert.Equal(ann.Id, ok.User!.Id);
        Assert.NotNull((await _store.GetUserAsync(ann.Id))!.LastLoginAt);

        await _store.SetUserDisabledAsync(ann.Id, true);
        Assert.Equal(LoginStatus.Disabled, (await _store.AuthenticateAsync("ann", "correct horse", 3, TimeSpan.FromMinutes(5))).Status);
        Assert.Null(await _store.VerifyUserAsync("ann", "correct horse"));

        await _store.SetUserDisabledAsync(ann.Id, false);
        await _store.SetPasswordAsync(ann.Id, "a new password");
        Assert.NotNull(await _store.VerifyUserAsync("ann", "a new password"));
        Assert.Equal(LoginStatus.InvalidCredentials, (await _store.AuthenticateAsync("nobody", "x", 3, TimeSpan.FromMinutes(5))).Status);
    }

    [Fact]
    public async Task External_Logins_Provision_Unique_Usernames_Without_Passwords()
    {
        await _store.CreateUserAsync("alice", "Local Alice", "password-1");
        var login = new ExternalLogin("entra", "https://login.example/tenant/v2.0", "sub-123", "alice@example.com");
        Assert.Null(await _store.FindExternalLoginAsync(login.Issuer, login.Subject));

        var sso = await _store.CreateExternalUserAsync(LoginPolicy.SuggestUsername("Alice@Example.com"), "Alice Smith", login);
        Assert.Equal("alice2", sso.Username); // "alice" is a different, local account: never merged
        Assert.Equal(sso.Id, (await _store.FindExternalLoginAsync(login.Issuer, login.Subject))!.Id);
        Assert.Null(await _store.FindExternalLoginAsync("https://other-issuer", "sub-123"));

        var state = (await _store.GetUserAsync(sso.Id))!;
        Assert.False(state.HasPassword);
        Assert.Equal(["entra"], state.SsoProviders);
        Assert.Null(await _store.VerifyUserAsync("alice2", LoginPolicy.NoPassword));

        Assert.Equal("j.doe-x", LoginPolicy.SuggestUsername("J.Doe X@corp"));
        Assert.Equal("user", LoginPolicy.SuggestUsername("@@"));
    }

    [Fact]
    public async Task Effective_Role_Is_The_Max_Of_Direct_And_Team_Grants()
    {
        var owner = await _store.CreateUserAsync("owner", "Owner", "password-1");
        var dev = await _store.CreateUserAsync("dev", "Dev", "password-2");
        var pid = await _store.CreateProjectAsync("Rover", Json, owner.Id);
        Assert.Null(await _store.RoleOfAsync(pid, dev.Id));
        Assert.Empty(await _store.ListProjectsAsync(dev.Id));

        var org = await _store.CreateOrganizationAsync("Acme");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.CreateOrganizationAsync("Acme"));
        var team = await _store.CreateTeamAsync(org, "Avionics");
        await _store.AddTeamMemberAsync(team, dev.Id);
        Assert.Contains(await _store.OrganizationMembersAsync(org), u => u.Id == dev.Id); // joining a team joins its organization
        await _store.SetTeamGrantAsync(team, pid, ProjectRole.Editor);

        Assert.Equal(ProjectRole.Editor, await _store.RoleOfAsync(pid, dev.Id));
        var listed = Assert.Single(await _store.ListProjectsAsync(dev.Id));
        Assert.Equal(ProjectRole.Editor, listed.Role);

        await _store.SetMemberAsync(pid, dev.Id, ProjectRole.Viewer);   // lower direct role: team grant still wins
        Assert.Equal(ProjectRole.Editor, await _store.RoleOfAsync(pid, dev.Id));
        await _store.SetMemberAsync(pid, dev.Id, ProjectRole.Owner);    // higher direct role wins
        Assert.Equal(ProjectRole.Owner, await _store.RoleOfAsync(pid, dev.Id));
        await _store.RemoveMemberAsync(pid, dev.Id);
        Assert.Equal(ProjectRole.Editor, await _store.RoleOfAsync(pid, dev.Id));

        var info = Assert.Single(await _store.ListTeamsAsync(org));
        Assert.Equal((1, 1), (info.MemberCount, info.ProjectCount));
        Assert.Equal(ProjectRole.Editor, Assert.Single(await _store.TeamGrantsAsync(team)).Role);

        await _store.RemoveOrganizationMemberAsync(org, dev.Id); // leaving the organization leaves its teams
        Assert.Empty(await _store.TeamMembersAsync(team));
        Assert.Null(await _store.RoleOfAsync(pid, dev.Id));

        await _store.AddTeamMemberAsync(team, dev.Id);
        await _store.DeleteOrganizationAsync(org);
        Assert.Null(await _store.RoleOfAsync(pid, dev.Id));
        Assert.Empty(await _store.ListOrganizationsAsync());
    }

    [Fact]
    public async Task Admin_Project_List_Transfer_And_Restore()
    {
        var ann = await _store.CreateUserAsync("ann", "Ann", "password-1");
        var ben = await _store.CreateUserAsync("ben", "Ben", "password-2");
        var pid = await _store.CreateProjectAsync("Rover", Json, ann.Id);
        await _store.CommitVersionAsync(pid, "first", Json, ann.Id);
        await _store.CommitVersionAsync(pid, "second", Json, ben.Id);

        var p = Assert.Single(await _store.ListAllProjectsAsync());
        Assert.Equal(("Rover", ann.Id, 2, 1), (p.Name, p.Owner!.Id, p.VersionCount, p.MemberCount));
        Assert.True(p.SizeBytes >= Json.Length * 3);

        await _store.TransferProjectAsync(pid, ben.Id);
        Assert.Equal(ProjectRole.Owner, await _store.RoleOfAsync(pid, ben.Id));
        Assert.Equal(ProjectRole.Editor, await _store.RoleOfAsync(pid, ann.Id));

        var snapshots = await _store.ListVersionSnapshotsAsync(pid);
        Assert.Equal([1, 2], snapshots.Select(v => v.Number));
        Assert.Equal(ben.Id, snapshots[1].AuthorId);

        // Same id still exists → restored as a new project; unknown members are skipped.
        var copy = await _store.RestoreProjectAsync(new RestoredProject(pid, "Rover (restored)", Json, snapshots,
            [new RestoredMember(ann.Id, ProjectRole.Owner), new RestoredMember(Guid.NewGuid(), ProjectRole.Editor)]));
        Assert.NotEqual(pid, copy);
        Assert.Equal(ProjectRole.Owner, await _store.RoleOfAsync(copy, ann.Id));
        var versions = await _store.ListVersionsAsync(copy);
        Assert.Equal(["second", "first"], versions.Select(v => v.Message));
        Assert.Equal(snapshots[0].CreatedAt, versions[1].CreatedAt, TimeSpan.FromSeconds(1));
        Assert.Single(await _store.MembersAsync(copy));

        // Free id → kept.
        var fresh = Guid.NewGuid();
        Assert.Equal(fresh, await _store.RestoreProjectAsync(new RestoredProject(fresh, "Other", Json, [], [new RestoredMember(ben.Id, ProjectRole.Owner)])));
    }

    [Fact]
    public async Task Audit_Log_Is_Filterable_Paged_And_Append_Only()
    {
        var ann = await _store.CreateUserAsync("ann", "Ann", "password-1");
        var pid = Guid.NewGuid();
        var t0 = DateTime.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 30; i++)
            await _store.AppendAuditAsync(new AuditEntry(t0.AddSeconds(i), ann.Id, "ann", i % 3 == 0 ? "login.success" : "project.export",
                i % 2 == 0 ? pid : null, i % 2 == 0 ? "Rover" : null, $"file-{i}.xmi", """{"bytes":42}""", "127.0.0.1"));
        await _store.AppendAuditAsync(new AuditEntry(DateTime.UtcNow, null, "system", "backup.scheduled", Target: "nightly.zip"));

        var all = await _store.QueryAuditAsync(new AuditQuery(Take: 10));
        Assert.Equal(31, all.Total);
        Assert.Equal(10, all.Items.Count);
        Assert.Equal("backup.scheduled", all.Items[0].Action); // newest first
        Assert.Null(all.Items[0].ActorId);

        Assert.Equal(10, (await _store.QueryAuditAsync(new AuditQuery(Action: "login."))).Total);
        Assert.Equal(15, (await _store.QueryAuditAsync(new AuditQuery(ProjectId: pid))).Total);
        Assert.Equal(1, (await _store.QueryAuditAsync(new AuditQuery(Text: "FILE-29"))).Total);
        Assert.Equal(1, (await _store.QueryAuditAsync(new AuditQuery(Actor: "SYS"))).Total);
        Assert.Equal(5, (await _store.QueryAuditAsync(new AuditQuery(From: t0.AddSeconds(25), To: t0.AddSeconds(30)))).Total);
        var page2 = await _store.QueryAuditAsync(new AuditQuery(Skip: 28, Take: 10));
        Assert.Equal(3, page2.Items.Count);
        Assert.Equal("""{"bytes":42}""", page2.Items[0].DetailsJson);

        await using var c = new SqliteConnection($"Data Source={_path}");
        await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = "update audit_log set actor_name = 'mallory'";
        await Assert.ThrowsAsync<SqliteException>(() => cmd.ExecuteNonQueryAsync());
        cmd.CommandText = "delete from audit_log";
        await Assert.ThrowsAsync<SqliteException>(() => cmd.ExecuteNonQueryAsync());
    }
}
