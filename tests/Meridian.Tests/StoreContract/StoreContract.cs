using System.Data.Common;
using System.Text.Json.Nodes;
using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;

namespace Meridian.Tests.StoreContract;

/// <summary>
/// The behaviour every <see cref="IMeridianStore"/> must have. The same tests run against SQLite (desktop / local mode)
/// and PostgreSQL (team server); each test gets a fresh, empty database.
/// </summary>
public abstract partial class StoreContract
{
    protected static readonly string Json = ProjectFile.Serialize(SampleModels.Vehicle());

    /// <summary>An empty database (not migrated).</summary>
    protected abstract Task<IStoreHarness> NewDatabaseAsync();

    /// <summary>A migrated, empty database.</summary>
    protected async Task<IStoreHarness> OpenAsync()
    {
        var h = await NewDatabaseAsync();
        await h.Store.MigrateAsync();
        return h;
    }

    /// <summary>JSON documents are compared by value: PostgreSQL stores them as jsonb, which normalises whitespace and key order.</summary>
    protected static void SameJson(string expected, string? actual)
    {
        Assert.NotNull(actual);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual!)), $"JSON differs:\n{expected}\n---\n{actual}");
    }

    protected static void IsRecentUtc(DateTime t)
    {
        Assert.Equal(DateTimeKind.Utc, t.Kind);
        Assert.InRange(t, DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(2));
    }

    // ================================================================= migrations

    [SkippableFact]
    public async Task Migrate_Is_Idempotent()
    {
        await using var h = await OpenAsync();
        await h.Store.MigrateAsync();
        await h.Store.MigrateAsync();
        await h.OpenAnotherStore().MigrateAsync(); // a second server process starting against the same database

        var ann = await h.Store.CreateUserAsync("ann", "Ann", "password-1");
        var pid = await h.Store.CreateProjectAsync("P", Json, ann.Id);
        await h.Store.AppendAuditAsync(new AuditEntry(DateTime.UtcNow, ann.Id, "ann", "x"));
        await h.Store.MigrateAsync();   // with data present
        Assert.Equal(ProjectRole.Owner, await h.Store.RoleOfAsync(pid, ann.Id));
        await Assert.ThrowsAnyAsync<DbException>(() => h.ExecRawAsync("delete from audit_log")); // trigger survives re-migration
        Assert.Equal(1, (await h.Store.QueryAuditAsync(new AuditQuery())).Total);
    }

    [SkippableFact]
    public async Task Concurrent_Migrations_Of_A_Fresh_Database_Succeed()
    {
        await using var h = await NewDatabaseAsync();
        var stores = Enumerable.Range(0, 4).Select(_ => h.OpenAnotherStore()).ToList();   // several server processes starting at once
        await Task.WhenAll(stores.Select(s => Task.Run(s.MigrateAsync)));
        await Task.WhenAll(stores.Select(s => Task.Run(s.MigrateAsync)));
        var ann = await stores[0].CreateUserAsync("ann", "Ann", "password-1");
        Assert.Equal(ann, await stores[3].FindUserAsync("ann"));
    }

    [SkippableFact]
    public async Task Migrates_A_Database_Created_By_Version_0_1_0()
    {
        await using var h = await NewDatabaseAsync();
        await h.ExecRawAsync(h.V010Schema);

        // Data as 0.1.0 wrote it.
        var ann = Guid.NewGuid();
        var ben = Guid.NewGuid();
        var pid = Guid.NewGuid();
        var created = DateTime.UtcNow.AddDays(-30);
        await h.ExecRawAsync("insert into users(id, username, display_name, password_hash, color, created_at) values ($1,$2,$3,$4,$5,$6)",
            ann, "ann", "Ann", Accounts.HashPassword("legacy-password"), "#4f8cff", created);
        await h.ExecRawAsync("insert into users(id, username, display_name, password_hash, color, created_at) values ($1,$2,$3,$4,$5,$6)",
            ben, "ben", "Ben", Accounts.HashPassword("legacy-password2"), "#2dd4bf", created.AddMinutes(1));
        await h.ExecRawAsync($"insert into projects(id, name, model, revision, created_by, updated_by, created_at, updated_at) values ($1,$2,{h.JsonParam("$3")},$4,$5,$5,$6,$6)",
            pid, "Legacy", Json, 7L, ann, created);
        await h.ExecRawAsync("insert into project_members(project_id, user_id, role) values ($1,$2,$3)", pid, ann, (short)ProjectRole.Owner);
        await h.ExecRawAsync("insert into project_members(project_id, user_id, role) values ($1,$2,$3)", pid, ben, (short)ProjectRole.Viewer);
        await h.ExecRawAsync($"insert into project_versions(project_id, number, message, model, author, created_at) values ($1,1,'v1',{h.JsonParam("$2")},$3,$4)",
            pid, Json, ann, created);
        await h.ExecRawAsync("insert into comments(project_id, element_id, author, body, resolved, created_at) values ($1,'el',$2,'old comment',$3,$4)",
            pid, ben, false, created);

        await h.Store.MigrateAsync();
        await h.Store.MigrateAsync();

        // Old data is intact and readable through the new API.
        var s = h.Store;
        var loaded = (await s.LoadProjectAsync(pid))!.Value;
        Assert.Equal(("Legacy", 7L), (loaded.Name, loaded.Revision));
        SameJson(Json, loaded.ModelJson);
        Assert.Equal(ann, (await s.VerifyUserAsync("ann", "legacy-password"))!.Id);
        var admin = (await s.GetUserAsync(ann))!;
        Assert.False(admin.IsAdmin);
        Assert.False(admin.Disabled);
        Assert.True(admin.HasPassword);
        Assert.Equal(created, admin.CreatedAt, TimeSpan.FromSeconds(1));
        Assert.True(await s.EnsureAdminAsync());
        Assert.True((await s.GetUserAsync(ann))!.IsAdmin);   // the oldest account
        Assert.Equal(ProjectRole.Viewer, Assert.Single(await s.ListProjectsAsync(ben)).Role);
        var v = Assert.Single(await s.ListVersionsAsync(pid));
        Assert.Equal(1, v.Number);
        Assert.Equal(created, v.CreatedAt, TimeSpan.FromSeconds(1));
        Assert.Equal("old comment", Assert.Single(await s.ListCommentsAsync(pid)).Body);
        Assert.Equal([1], (await s.ListBranchVersionsAsync(pid, null)).Select(x => x.Number));

        // New features work on the upgraded schema.
        Assert.Equal(2, await s.CommitVersionAsync(pid, "v2", Json, ben));
        var bid = await s.CreateBranchAsync(pid, "feature", Json, Json, v.Id, ann);
        Assert.Equal(3, (await s.CommitBranchVersionAsync(pid, bid, "on branch", Json, ann)).Number);
        await s.CreateBaselineAsync(pid, v.Id, "PDR", "", ann);
        var review = await s.CreateReviewAsync(pid, new ReviewDraft("R", "", ReviewScopeKind.Elements, ["el"], v.Id, null, false, [ben]), ann);
        Assert.NotNull(await s.GetReviewAsync(review));
        var org = await s.CreateOrganizationAsync("Acme");
        var team = await s.CreateTeamAsync(org, "T");
        await s.AddTeamMemberAsync(team, ben);
        await s.SetTeamGrantAsync(team, pid, ProjectRole.Editor);
        Assert.Equal(ProjectRole.Editor, await s.RoleOfAsync(pid, ben));
        await s.AppendAuditAsync(new AuditEntry(DateTime.UtcNow, ann, "ann", "project.upgrade", pid, "Legacy"));
        Assert.Equal(1, (await s.QueryAuditAsync(new AuditQuery(ProjectId: pid))).Total);
        var token = await s.CreateApiTokenAsync(ann, "t", "hash-1", "mrd_1");
        Assert.Equal(ann, (await s.FindUserByApiTokenAsync("hash-1"))!.Id);
        Assert.Equal(token.Id, Assert.Single(await s.ListApiTokensAsync(ann)).Id);
    }

    // ================================================================= users

    [SkippableFact]
    public async Task Users_Are_Created_Normalised_Found_And_Verified()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("  Ann ", "  Ann Lee ", "correct horse");
        Assert.Equal(("ann", "Ann Lee"), (ann.Username, ann.DisplayName));
        Assert.Equal(Accounts.Palette[0], ann.Color);
        var ben = await s.CreateUserAsync("ben", "", "battery staple");
        Assert.Equal("ben", ben.DisplayName);
        Assert.Equal(Accounts.Palette[1], ben.Color);

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.CreateUserAsync("ANN", "dup", "whatever123"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.CreateUserAsync("x", "short", "whatever123"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.CreateUserAsync("carl", "Carl", "short"));

        Assert.Equal(ann, await s.FindUserAsync(" ANN "));
        Assert.Null(await s.FindUserAsync("nobody"));
        Assert.Equal(ann, await s.VerifyUserAsync("ANN", "correct horse"));
        Assert.Null(await s.VerifyUserAsync("ann", "wrong password"));
        Assert.Null(await s.VerifyUserAsync("nobody", "correct horse"));
    }

    // ================================================================= projects & roles

    [SkippableFact]
    public async Task Projects_Members_And_Roles()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann Lee", "correct horse");
        var ben = await s.CreateUserAsync("ben", "Ben", "battery staple");
        var cat = await s.CreateUserAsync("cat", "Cat", "battery staple");

        var pid = await s.CreateProjectAsync("  Vehicle ", Json, ann.Id);
        var (name, model, rev) = (await s.LoadProjectAsync(pid))!.Value;
        Assert.Equal(("Vehicle", 0L), (name, rev));
        SameJson(Json, model);
        Assert.NotNull(ProjectFile.Deserialize(model));
        Assert.Null(await s.LoadProjectAsync(Guid.NewGuid()));

        Assert.Equal(ProjectRole.Owner, await s.RoleOfAsync(pid, ann.Id));
        Assert.Null(await s.RoleOfAsync(pid, ben.Id));
        Assert.Null(await s.RoleOfAsync(Guid.NewGuid(), ann.Id));
        await s.SetMemberAsync(pid, ben.Id, ProjectRole.Editor);
        Assert.Equal(ProjectRole.Editor, await s.RoleOfAsync(pid, ben.Id));
        await s.SetMemberAsync(pid, ben.Id, ProjectRole.Viewer);   // upsert
        Assert.Equal(ProjectRole.Viewer, await s.RoleOfAsync(pid, ben.Id));
        await s.SetMemberAsync(pid, cat.Id, ProjectRole.Editor);

        var members = await s.MembersAsync(pid);
        Assert.Equal([(ann.Id, ProjectRole.Owner), (cat.Id, ProjectRole.Editor), (ben.Id, ProjectRole.Viewer)], members.Select(m => (m.User.Id, m.Role)));
        Assert.Equal(ann, members[0].User);
        await s.RemoveMemberAsync(pid, cat.Id);
        Assert.Equal(2, (await s.MembersAsync(pid)).Count);
        Assert.Empty(await s.ListProjectsAsync(cat.Id));

        Assert.Equal(1, await s.SaveProjectAsync(pid, Json, ben.Id));
        Assert.Equal(2, await s.SaveProjectAsync(pid, Json, null));
        Assert.Equal(3, await s.SaveProjectAsync(pid, Json, ben.Id));
        Assert.Equal(0, await s.SaveProjectAsync(Guid.NewGuid(), Json, ben.Id)); // unknown project
        Assert.Equal(3, (await s.LoadProjectAsync(pid))!.Value.Revision);

        var second = await s.CreateProjectAsync("Rover", Json, ben.Id);
        var listed = await s.ListProjectsAsync(ben.Id);
        Assert.Equal([second, pid], listed.Select(p => p.Id)); // most recently updated first
        var summary = listed[1];
        Assert.Equal(("Vehicle", ProjectRole.Viewer, 3L, "Ben"), (summary.Name, summary.Role, summary.Revision, summary.UpdatedBy));
        IsRecentUtc(summary.UpdatedAt);

        await s.SaveProjectAsync(pid, Json, null);
        Assert.Null((await s.ListProjectsAsync(ann.Id)).Single().UpdatedBy);

        await s.RenameProjectAsync(pid, "  Car  ");
        Assert.Equal("Car", (await s.LoadProjectAsync(pid))!.Value.Name);
        Assert.Equal(pid, (await s.ListProjectsAsync(ben.Id))[0].Id); // renaming touches updated_at

        await s.DeleteProjectAsync(pid);
        Assert.Null(await s.LoadProjectAsync(pid));
        Assert.Null(await s.RoleOfAsync(pid, ann.Id));
        Assert.Empty(await s.MembersAsync(pid));
        Assert.Empty(await s.ListProjectsAsync(ann.Id));
    }

    [SkippableFact]
    public async Task Effective_Role_Is_The_Max_Of_Direct_And_Team_Grants()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var owner = await s.CreateUserAsync("owner", "Owner", "password-1");
        var dev = await s.CreateUserAsync("dev", "Dev", "password-2");
        var pid = await s.CreateProjectAsync("Rover", Json, owner.Id);
        Assert.Null(await s.RoleOfAsync(pid, dev.Id));
        Assert.Empty(await s.ListProjectsAsync(dev.Id));

        var org = await s.CreateOrganizationAsync("Acme");
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.CreateOrganizationAsync("Acme"));
        var team = await s.CreateTeamAsync(org, "Avionics");
        var other = await s.CreateTeamAsync(org, "Ground");
        await s.AddTeamMemberAsync(team, dev.Id);
        await s.AddTeamMemberAsync(other, dev.Id);
        Assert.Contains(await s.OrganizationMembersAsync(org), u => u.Id == dev.Id); // joining a team joins its organization
        await s.SetTeamGrantAsync(team, pid, ProjectRole.Editor);
        await s.SetTeamGrantAsync(other, pid, ProjectRole.Viewer);

        Assert.Equal(ProjectRole.Editor, await s.RoleOfAsync(pid, dev.Id));
        var listed = Assert.Single(await s.ListProjectsAsync(dev.Id));   // two grants, one row
        Assert.Equal(ProjectRole.Editor, listed.Role);

        await s.SetMemberAsync(pid, dev.Id, ProjectRole.Viewer);   // lower direct role: team grant still wins
        Assert.Equal(ProjectRole.Editor, await s.RoleOfAsync(pid, dev.Id));
        Assert.Equal(ProjectRole.Editor, Assert.Single(await s.ListProjectsAsync(dev.Id)).Role);
        await s.SetMemberAsync(pid, dev.Id, ProjectRole.Owner);    // higher direct role wins
        Assert.Equal(ProjectRole.Owner, await s.RoleOfAsync(pid, dev.Id));
        Assert.Equal(ProjectRole.Owner, Assert.Single(await s.ListProjectsAsync(dev.Id)).Role);
        await s.RemoveMemberAsync(pid, dev.Id);
        Assert.Equal(ProjectRole.Editor, await s.RoleOfAsync(pid, dev.Id));

        await s.SetTeamGrantAsync(team, pid, ProjectRole.Owner); // upsert
        Assert.Equal(ProjectRole.Owner, await s.RoleOfAsync(pid, dev.Id));
        await s.RemoveTeamGrantAsync(team, pid);
        Assert.Equal(ProjectRole.Viewer, await s.RoleOfAsync(pid, dev.Id));
        await s.RemoveTeamMemberAsync(other, dev.Id);
        Assert.Null(await s.RoleOfAsync(pid, dev.Id));
        await s.SetTeamGrantAsync(team, pid, ProjectRole.Editor);

        Assert.Equal(["Avionics", "Ground"], (await s.ListTeamsAsync(org)).Select(t => t.Name));
        var info = (await s.ListTeamsAsync(org))[0];
        Assert.Equal((team, org, 1, 1), (info.Id, info.OrganizationId, info.MemberCount, info.ProjectCount));
        var grant = Assert.Single(await s.TeamGrantsAsync(team));
        Assert.Equal((pid, "Rover", ProjectRole.Editor), (grant.ProjectId, grant.ProjectName, grant.Role));
        Assert.Equal([dev.Id], (await s.TeamMembersAsync(team)).Select(u => u.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.CreateTeamAsync(org, "Avionics"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.CreateTeamAsync(org, "  "));

        await s.RemoveOrganizationMemberAsync(org, dev.Id); // leaving the organization leaves its teams
        Assert.Empty(await s.TeamMembersAsync(team));
        Assert.Empty(await s.OrganizationMembersAsync(org));
        Assert.Null(await s.RoleOfAsync(pid, dev.Id));

        await s.AddOrganizationMemberAsync(org, owner.Id);
        await s.AddOrganizationMemberAsync(org, owner.Id); // no duplicate
        await s.AddTeamMemberAsync(team, dev.Id);
        await s.AddTeamMemberAsync(team, dev.Id);
        var orgInfo = Assert.Single(await s.ListOrganizationsAsync());
        Assert.Equal((org, "Acme", 2, 2), (orgInfo.Id, orgInfo.Name, orgInfo.MemberCount, orgInfo.TeamCount));
        IsRecentUtc(orgInfo.CreatedAt);
        Assert.Equal(["Dev", "Owner"], (await s.OrganizationMembersAsync(org)).Select(u => u.DisplayName));

        await s.DeleteTeamAsync(other);
        Assert.Single(await s.ListTeamsAsync(org));
        await s.DeleteProjectAsync(pid);   // grants go with the project
        Assert.Empty(await s.TeamGrantsAsync(team));
        await s.DeleteOrganizationAsync(org);
        Assert.Empty(await s.ListOrganizationsAsync());
        Assert.Empty(await s.ListTeamsAsync(org));
        Assert.Empty(await s.TeamMembersAsync(team));
    }

    // ================================================================= versions, branches, baselines

    [SkippableFact]
    public async Task Versions_Are_Numbered_Per_Project_And_Round_Trip()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann", "correct horse");
        var p1 = await s.CreateProjectAsync("One", Json, ann.Id);
        var p2 = await s.CreateProjectAsync("Two", Json, ann.Id);
        var edited = ProjectFile.Deserialize(Json);
        edited.Rename(edited.OfKind(ElementKind.Block).First(), "Renamed");
        var editedJson = ProjectFile.Serialize(edited);

        Assert.Equal(1, await s.CommitVersionAsync(p1, "first", Json, ann.Id));
        Assert.Equal(2, await s.CommitVersionAsync(p1, "second", editedJson, null));
        Assert.Equal(1, await s.CommitVersionAsync(p2, "other project", Json, ann.Id));

        var versions = await s.ListVersionsAsync(p1);
        Assert.Equal([2, 1], versions.Select(v => v.Number));
        Assert.Equal(["second", "first"], versions.Select(v => v.Message));
        Assert.Equal(["unknown", "Ann"], versions.Select(v => v.Author));
        Assert.All(versions, v => IsRecentUtc(v.CreatedAt));
        SameJson(Json, await s.LoadVersionAsync(p1, versions[1].Id));
        var reloaded = ProjectFile.Deserialize((await s.LoadVersionAsync(p1, versions[0].Id))!);
        Assert.Contains(reloaded.OfKind(ElementKind.Block), b => b.Name == "Renamed");
        Assert.Null(await s.LoadVersionAsync(p2, versions[0].Id)); // version of another project
        Assert.Null(await s.LoadVersionAsync(p1, long.MaxValue));
    }

    [SkippableFact]
    public async Task Branches_Commits_Merge_Base_And_Baselines()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann", "correct horse");
        var pid = await s.CreateProjectAsync("Vehicle", Json, ann.Id);

        var v1 = await s.CommitBranchVersionAsync(pid, null, "Initial", Json, ann.Id);
        Assert.Equal(1, v1.Number);
        Assert.Equal(2, await s.CommitVersionAsync(pid, "Legacy commit", Json, ann.Id));

        var bid = await s.CreateBranchAsync(pid, " pdr-updates ", Json, Json, v1.Id, ann.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.CreateBranchAsync(pid, "pdr-updates", Json, Json, null, ann.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.CreateBranchAsync(pid, "Main", Json, Json, null, ann.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.CreateBranchAsync(pid, "bad;name", Json, Json, null, ann.Id));
        var unbased = await s.CreateBranchAsync(pid, "scratch", Json, Json, null, null);

        var branches = await s.ListBranchesAsync(pid);
        Assert.Equal(["pdr-updates", "scratch"], branches.Select(b => b.Name));
        var branch = branches[0];
        Assert.Equal((bid, pid, v1.Id, 1, "Ann", 0L), (branch.Id, branch.ProjectId, branch.BaseVersionId, branch.BaseVersionNumber, branch.CreatedBy, branch.Revision));
        IsRecentUtc(branch.CreatedAt);
        var scratch = (await s.GetBranchAsync(unbased))!;
        Assert.Equal((null, null, "unknown"), (scratch.BaseVersionId, scratch.BaseVersionNumber, scratch.CreatedBy));
        Assert.Null(await s.GetBranchAsync(Guid.NewGuid()));
        Assert.Null(await s.LoadBranchAsync(Guid.NewGuid()));

        var edited = ProjectFile.Deserialize(Json);
        edited.Rename(edited.OfKind(ElementKind.Block).First(), "Renamed on branch");
        var editedJson = ProjectFile.Serialize(edited);
        Assert.Equal(1, await s.SaveBranchAsync(bid, editedJson, ann.Id));
        Assert.Equal(2, await s.SaveBranchAsync(bid, editedJson, null));
        Assert.Equal(0, await s.SaveBranchAsync(Guid.NewGuid(), editedJson, null));
        var models = (await s.LoadBranchAsync(bid))!;
        Assert.Equal((pid, "pdr-updates", v1.Id, 2L), (models.ProjectId, models.Name, models.BaseVersionId, models.Revision));
        Assert.Contains("Renamed on branch", models.HeadJson);
        Assert.DoesNotContain("Renamed on branch", models.BaseJson);
        Assert.DoesNotContain("Renamed on branch", (await s.LoadProjectAsync(pid))!.Value.ModelJson);
        Assert.Equal(2, (await s.GetBranchAsync(bid))!.Revision);

        var v3 = await s.CommitBranchVersionAsync(pid, bid, "Branch work", editedJson, ann.Id);
        Assert.Equal(3, v3.Number);
        Assert.Equal([3], (await s.ListBranchVersionsAsync(pid, bid)).Select(v => v.Number));
        Assert.Equal([2, 1], (await s.ListBranchVersionsAsync(pid, null)).Select(v => v.Number));
        Assert.Empty(await s.ListBranchVersionsAsync(pid, unbased));
        Assert.Equal(3, (await s.ListVersionsAsync(pid)).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.CommitBranchVersionAsync(Guid.NewGuid(), bid, "wrong project", Json, ann.Id));

        // Merge base moves after a merge; can also be cleared.
        await s.SetBranchBaseAsync(bid, editedJson, v3.Id);
        var moved = (await s.LoadBranchAsync(bid))!;
        Assert.Equal(v3.Id, moved.BaseVersionId);
        Assert.Contains("Renamed on branch", moved.BaseJson);
        Assert.Equal(3, (await s.GetBranchAsync(bid))!.BaseVersionNumber);
        await s.SetBranchBaseAsync(unbased, Json, null);
        Assert.Null((await s.LoadBranchAsync(unbased))!.BaseVersionId);

        // Baselines.
        var b1 = await s.CreateBaselineAsync(pid, v1.Id, "  PDR 1.0 ", "  Preliminary design review ", ann.Id);
        await s.CreateBaselineAsync(pid, v3.Id, "CDR candidate", "", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.CreateBaselineAsync(pid, v3.Id, "PDR 1.0", "", ann.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.CreateBaselineAsync(Guid.NewGuid(), v3.Id, "Other", "", ann.Id));
        var baselines = await s.ListBaselinesAsync(pid);
        Assert.Equal(["CDR candidate", "PDR 1.0"], baselines.Select(b => b.Name));
        Assert.Equal(("pdr-updates", "unknown", 3, "Branch work"), (baselines[0].BranchName, baselines[0].CreatedBy, baselines[0].VersionNumber, baselines[0].VersionMessage));
        Assert.Equal(("main", "Ann", "Preliminary design review", b1), (baselines[1].BranchName, baselines[1].CreatedBy, baselines[1].Description, baselines[1].Id));
        IsRecentUtc(baselines[1].CreatedAt);

        var diff = ModelDiff.Compare(ProjectFile.Deserialize((await s.LoadVersionAsync(pid, baselines[1].VersionId))!),
                                     ProjectFile.Deserialize((await s.LoadVersionAsync(pid, baselines[0].VersionId))!));
        Assert.Contains(diff, d => d.Kind == DiffKind.Changed && d.Name == "Renamed on branch");

        await s.DeleteBranchAsync(bid);
        Assert.Equal(["scratch"], (await s.ListBranchesAsync(pid)).Select(b => b.Name));
        Assert.Equal(2, (await s.ListBaselinesAsync(pid)).Count);
        Assert.Equal(2, (await s.ListBranchVersionsAsync(pid, null)).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.CommitBranchVersionAsync(pid, bid, "gone", Json, ann.Id));

        await s.DeleteBaselineAsync(b1);
        Assert.Single(await s.ListBaselinesAsync(pid));

        await s.DeleteProjectAsync(pid);
        Assert.Empty(await s.ListBranchesAsync(pid));
        Assert.Empty(await s.ListBaselinesAsync(pid));
    }

    // ================================================================= comments, reviews, notifications

    [SkippableFact]
    public async Task Comments_Replies_And_Mentions()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann Lee", "correct horse");
        var ben = await s.CreateUserAsync("ben", "Ben Ito", "battery staple");
        var pid = await s.CreateProjectAsync("Vehicle", Json, ann.Id);
        var other = await s.CreateProjectAsync("Other", Json, ann.Id);

        var root = await s.AddCommentAsync(pid, "el", ann.Id, "Is this right, @ben?");
        var reply = await s.AddCommentAsync(pid, "el", ben.Id, "Yes");
        var reply2 = await s.AddCommentAsync(pid, "el2", ben.Id, "Also this");
        await s.AddCommentAsync(other, "el", ben.Id, "elsewhere");
        var comments = await s.ListCommentsAsync(pid);
        Assert.Equal([root, reply, reply2], comments.Select(c => c.Id));
        Assert.Equal(("el", "Is this right, @ben?", false), (comments[0].ElementId, comments[0].Body, comments[0].Resolved));
        Assert.Equal(ann, comments[0].Author);
        IsRecentUtc(comments[0].CreatedAt);

        // Only the author can change a comment's text (find and replace).
        Assert.False(await s.UpdateCommentBodyAsync(root, ben.Id, "hijacked"));
        Assert.True(await s.UpdateCommentBodyAsync(root, ann.Id, "Is this right now, @ben?"));
        Assert.Equal("Is this right now, @ben?", (await s.ListCommentsAsync(pid))[0].Body);
        Assert.True(await s.UpdateCommentBodyAsync(root, ann.Id, "Is this right, @ben?"));

        await s.SetCommentResolvedAsync(root, true);
        Assert.True((await s.ListCommentsAsync(pid))[0].Resolved);
        await s.SetCommentResolvedAsync(root, false);
        Assert.False((await s.ListCommentsAsync(pid))[0].Resolved);

        await s.SetCommentParentAsync(reply, root);
        await s.SetCommentParentAsync(reply2, reply);
        await s.SetCommentParentAsync(reply2, root);   // re-parenting replaces
        var parents = await s.ListCommentParentsAsync(pid);
        Assert.Equal(2, parents.Count);
        Assert.Equal((root, root), (parents[reply], parents[reply2]));
        Assert.Empty(await s.ListCommentParentsAsync(other));

        await s.AddMentionsAsync(pid, "comment", root, [ben.Id, ben.Id, ann.Id]);
        Assert.Equal(new[] { ann.Id, ben.Id }.Order(), (await s.ListMentionsAsync("comment", root)).Order());
        Assert.Empty(await s.ListMentionsAsync("review_comment", root));
        await s.AddMentionsAsync(pid, "comment", reply, []);
        Assert.Empty(await s.ListMentionsAsync("comment", reply));

        await s.DeleteCommentAsync(root);   // replies' parent links go with it
        Assert.Equal([reply, reply2], (await s.ListCommentsAsync(pid)).Select(c => c.Id));
        Assert.Empty(await s.ListCommentParentsAsync(pid));

        await s.DeleteProjectAsync(pid);
        Assert.Empty(await s.ListCommentsAsync(pid));
        Assert.Empty(await s.ListMentionsAsync("comment", root));
        Assert.Single(await s.ListCommentsAsync(other));
    }

    [SkippableFact]
    public async Task Reviews_Votes_Comments_And_Checks()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann Lee", "correct horse");
        var ben = await s.CreateUserAsync("ben", "Ben Ito", "battery staple");
        var cat = await s.CreateUserAsync("cat", "Cat Ruiz", "staple horse1");
        var pid = await s.CreateProjectAsync("Vehicle", Json, ann.Id);
        await s.CommitVersionAsync(pid, "baseline", Json, ann.Id);
        await s.CommitVersionAsync(pid, "head", Json, ann.Id);
        var vs = await s.ListVersionsAsync(pid);
        var (headId, baseId) = (vs[0].Id, vs[1].Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.CreateReviewAsync(pid, new ReviewDraft(" ", "", ReviewScopeKind.Elements, ["x"], null, null, false, []), ann.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.CreateReviewAsync(pid, new ReviewDraft("Empty", "", ReviewScopeKind.Elements, [], null, null, false, []), ann.Id));

        var id = await s.CreateReviewAsync(pid, new ReviewDraft(" Braking reqs ", " Check REQ-004 ", ReviewScopeKind.Elements, ["e1", "e2"], baseId, null, true, [ben.Id, cat.Id, ben.Id]), ann.Id);
        var id2 = await s.CreateReviewAsync(pid, new ReviewDraft("Diff", "", ReviewScopeKind.Versions, [], baseId, headId, false, []), ann.Id);
        var review = (await s.GetReviewAsync(id))!;
        Assert.Equal((id, pid, "Braking reqs", "Check REQ-004"), (review.Id, review.ProjectId, review.Title, review.Description));
        Assert.Equal((ReviewState.Open, ReviewScopeKind.Elements, baseId, (long?)null, true), (review.State, review.ScopeKind, review.BaseVersionId, review.HeadVersionId, review.ApplyStatus));
        Assert.Equal(["e1", "e2"], review.ElementIds);
        Assert.Equal(ann, review.Author);
        Assert.Equal(["Ben Ito", "Cat Ruiz"], review.Reviewers.Select(r => r.User.DisplayName));
        Assert.All(review.Reviewers, r => Assert.Equal((ReviewVote.Pending, (DateTime?)null), (r.Vote, r.VotedAt)));
        IsRecentUtc(review.CreatedAt);
        Assert.True(review.WaitingFor(ben.Id));
        var diffReview = (await s.GetReviewAsync(id2))!;
        Assert.Equal((ReviewScopeKind.Versions, headId, false), (diffReview.ScopeKind, diffReview.HeadVersionId, diffReview.ApplyStatus));
        Assert.Empty(diffReview.Reviewers);
        Assert.Equal(2, (await s.ListReviewsAsync(pid)).Count);
        Assert.Null(await s.GetReviewAsync(long.MaxValue));

        await s.SetReviewVoteAsync(id, ben.Id, ReviewVote.Approve);
        review = (await s.GetReviewAsync(id))!;
        Assert.Equal(ReviewVote.Approve, review.VoteOf(ben.Id));
        IsRecentUtc(review.Reviewers.Single(r => r.User.Id == ben.Id).VotedAt!.Value);
        Assert.Equal(id, (await s.ListReviewsAsync(pid))[0].Id); // voting touches updated_at
        await s.SetReviewVoteAsync(id, ben.Id, ReviewVote.Pending);   // withdrawn vote clears the time
        Assert.Null((await s.GetReviewAsync(id))!.Reviewers.Single(r => r.User.Id == ben.Id).VotedAt);
        await s.SetReviewVoteAsync(id, ben.Id, ReviewVote.Approve);
        await s.SetReviewVoteAsync(id, cat.Id, ReviewVote.RequestChanges);
        await s.SetReviewVoteAsync(id, ann.Id, ReviewVote.Approve);    // a non-reviewer voting joins the review
        review = (await s.GetReviewAsync(id))!;
        Assert.Equal(3, review.Reviewers.Count);
        Assert.Equal(ReviewState.ChangesRequested, ReviewRules.Compute(review.State, review.Reviewers));
        await s.SetReviewStateAsync(id, ReviewState.Closed);
        Assert.Equal(ReviewState.Closed, (await s.GetReviewAsync(id))!.State);

        var c1 = await s.AddReviewCommentAsync(id, null, null, ben.Id, "Looks good @ann");
        var c2 = await s.AddReviewCommentAsync(id, "e1", c1, ann.Id, "Thanks");
        var c3 = await s.AddReviewCommentAsync(id, "e2", null, cat.Id, "Separate");
        await s.AddReviewCommentAsync(id2, null, null, cat.Id, "Other review");
        var comments = await s.ListReviewCommentsAsync(id);
        Assert.Equal([c1, c2, c3], comments.Select(c => c.Id));
        Assert.Equal((id, (string?)null, (long?)null, "Looks good @ann"), (comments[0].ReviewId, comments[0].ElementId, comments[0].ParentId, comments[0].Body));
        Assert.Equal(("e1", (long?)c1), (comments[1].ElementId, comments[1].ParentId));
        Assert.Equal(ben, comments[0].Author);
        IsRecentUtc(comments[0].CreatedAt);
        await s.AddMentionsAsync(pid, "review_comment", c1, [ann.Id]);
        Assert.Equal([ann.Id], await s.ListMentionsAsync("review_comment", c1));
        await s.DeleteReviewCommentAsync(c1);   // replies go with their parent
        Assert.Equal([c3], (await s.ListReviewCommentsAsync(id)).Select(c => c.Id));

        await s.SetReviewCheckAsync(id, "e1", ben.Id, true);
        await s.SetReviewCheckAsync(id, "e1", ben.Id, true);
        await s.SetReviewCheckAsync(id, "e1", cat.Id, true);
        await s.SetReviewCheckAsync(id, "e2", ben.Id, true);
        await s.SetReviewCheckAsync(id, "e2", ben.Id, false);
        await s.SetReviewCheckAsync(id, "e9", ben.Id, false);   // unchecking nothing is fine
        var checks = await s.ListReviewChecksAsync(id);
        Assert.Equal([("e1", ben.Id), ("e1", cat.Id)], checks.Select(c => (c.ElementId, c.UserId)).OrderBy(c => c.UserId == ben.Id ? 0 : 1));
        Assert.Empty(await s.ListReviewChecksAsync(id2));

        await s.DeleteReviewAsync(id);
        Assert.Null(await s.GetReviewAsync(id));
        Assert.Empty(await s.ListReviewCommentsAsync(id));
        Assert.Empty(await s.ListReviewChecksAsync(id));
        Assert.Single(await s.ListReviewsAsync(pid));

        await s.DeleteProjectAsync(pid);
        Assert.Empty(await s.ListReviewsAsync(pid));
    }

    [SkippableFact]
    public async Task Notifications_Inbox_Unread_Counts_And_Limits()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann Lee", "correct horse");
        var ben = await s.CreateUserAsync("ben", "Ben Ito", "battery staple");
        var pid = await s.CreateProjectAsync("Vehicle", Json, ann.Id);
        var rid = await s.CreateReviewAsync(pid, new ReviewDraft("R", "", ReviewScopeKind.Elements, ["el"], null, null, false, [ben.Id]), ann.Id);

        var n1 = await s.AddNotificationAsync(new NotificationDraft(ben.Id, NotificationKinds.Mention, "Ann mentioned you", pid, "el", null, ann.Id));
        var n2 = await s.AddNotificationAsync(new NotificationDraft(ben.Id, NotificationKinds.ReviewRequested, "Review requested", pid, null, rid, ann.Id));
        var n3 = await s.AddNotificationAsync(new NotificationDraft(ben.Id, NotificationKinds.Comment, "System note"));
        await s.AddNotificationAsync(new NotificationDraft(ann.Id, NotificationKinds.Comment, "Ben replied", pid));
        Assert.True(n1 < n2 && n2 < n3);
        Assert.Equal(3, await s.UnreadNotificationCountAsync(ben.Id));
        Assert.Equal(0, await s.UnreadNotificationCountAsync(Guid.NewGuid()));

        var inbox = await s.ListNotificationsAsync(ben.Id);
        Assert.Equal([n3, n2, n1], inbox.Select(n => n.Id)); // newest first
        var mention = inbox[2];
        Assert.Equal((ben.Id, NotificationKinds.Mention, pid, "Vehicle", "el", (long?)null, "Ann mentioned you", false),
            (mention.UserId, mention.Kind, mention.ProjectId, mention.ProjectName, mention.ElementId, mention.ReviewId, mention.Text, mention.Read));
        Assert.Equal(ann, mention.Actor);
        IsRecentUtc(mention.CreatedAt);
        Assert.Equal(rid, inbox[1].ReviewId);
        Assert.Equal(((Guid?)null, (string?)null, (UserAccount?)null), (inbox[0].ProjectId, inbox[0].ProjectName, inbox[0].Actor));
        Assert.Equal([n3, n2], (await s.ListNotificationsAsync(ben.Id, 2)).Select(n => n.Id));

        await s.MarkNotificationReadAsync(n1, ann.Id);     // someone else's: no effect
        Assert.Equal(3, await s.UnreadNotificationCountAsync(ben.Id));
        await s.MarkNotificationReadAsync(n1, ben.Id);
        Assert.Equal(2, await s.UnreadNotificationCountAsync(ben.Id));
        Assert.True((await s.ListNotificationsAsync(ben.Id)).Single(n => n.Id == n1).Read);
        await s.MarkAllNotificationsReadAsync(ben.Id);
        Assert.Equal(0, await s.UnreadNotificationCountAsync(ben.Id));
        Assert.Equal(1, await s.UnreadNotificationCountAsync(ann.Id));

        await s.DeleteReviewAsync(rid);    // the notification stays, without its review
        Assert.Null((await s.ListNotificationsAsync(ben.Id)).Single(n => n.Id == n2).ReviewId);
        await s.DeleteProjectAsync(pid);   // project notifications go with the project
        Assert.Equal([n3], (await s.ListNotificationsAsync(ben.Id)).Select(n => n.Id));
    }

    // ================================================================= integrations & API tokens

    [SkippableFact]
    public async Task Integrations_Are_Saved_As_Given_Updated_Listed_And_Deleted()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann", "correct horse");
        var p1 = await s.CreateProjectAsync("One", Json, ann.Id);
        var p2 = await s.CreateProjectAsync("Two", Json, ann.Id);

        const string secret = "CfDJ8 encrypted/+== \"secret\" ünïcode";
        var jira = new IntegrationConnection(Guid.NewGuid(), p1, IntegrationKinds.IssueTracker, "Jira", """{"type":"jira-cloud","project":"MER"}""", secret, DateTime.UtcNow);
        await s.SaveIntegrationAsync(jira);
        var second = new IntegrationConnection(Guid.NewGuid(), p1, IntegrationKinds.Webhook, "hook", """{"url":"https://x"}""", null, DateTime.UtcNow);
        await s.SaveIntegrationAsync(second);
        await s.SaveIntegrationAsync(new IntegrationConnection(Guid.NewGuid(), p2, IntegrationKinds.Webhook, "hook", """{"url":"https://y"}""", null, DateTime.UtcNow));

        var list = await s.ListIntegrationsAsync(p1);
        Assert.Equal([jira.Id, second.Id], list.Select(i => i.Id));
        Assert.Equal((p1, IntegrationKinds.IssueTracker, "Jira", secret), (list[0].ProjectId, list[0].Kind, list[0].Name, list[0].ProtectedSecret));
        SameJson(jira.ConfigJson, list[0].ConfigJson);
        IsRecentUtc(list[0].CreatedAt);
        Assert.Null(list[1].ProtectedSecret);
        // The store never transforms the (already encrypted) secret.
        Assert.Equal(secret, await h.ScalarRawAsync("select secret from project_integrations where id = $1", jira.Id));

        await s.SaveIntegrationAsync(jira with { Name = "Jira Cloud", ConfigJson = """{"type":"jira-server"}""", ProtectedSecret = "other" });
        var found = (await s.FindIntegrationAsync(jira.Id))!;
        Assert.Equal(("Jira Cloud", "other"), (found.Name, found.ProtectedSecret));
        SameJson("""{"type":"jira-server"}""", found.ConfigJson);
        await s.SaveIntegrationAsync(found with { ProtectedSecret = null });
        Assert.Null((await s.FindIntegrationAsync(jira.Id))!.ProtectedSecret);
        Assert.Equal(2, (await s.ListIntegrationsAsync(p1)).Count);
        Assert.Null(await s.FindIntegrationAsync(Guid.NewGuid()));

        await s.DeleteIntegrationAsync(jira.Id);
        Assert.Equal([second.Id], (await s.ListIntegrationsAsync(p1)).Select(i => i.Id));
        await s.DeleteProjectAsync(p1);
        Assert.Null(await s.FindIntegrationAsync(second.Id));
        Assert.Single(await s.ListIntegrationsAsync(p2));
    }

    [SkippableFact]
    public async Task Webhook_Deliveries_Are_Logged_Newest_First()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann", "correct horse");
        var pid = await s.CreateProjectAsync("One", Json, ann.Id);
        var other = await s.CreateProjectAsync("Two", Json, ann.Id);
        var hook = Guid.NewGuid();
        await s.AddWebhookDeliveryAsync(new WebhookDelivery(0, pid, hook, "d1", "ping", 1, null, false, "Timed out", DateTime.UtcNow));
        await s.AddWebhookDeliveryAsync(new WebhookDelivery(0, pid, hook, "d1", "ping", 2, 200, true, null, DateTime.UtcNow));
        await s.AddWebhookDeliveryAsync(new WebhookDelivery(0, other, hook, "d2", "push", 1, 500, false, "boom", DateTime.UtcNow));
        var log = await s.ListWebhookDeliveriesAsync(pid);
        Assert.Equal(2, log.Count);
        Assert.Equal((pid, hook, "d1", "ping", 2, (int?)200, true, (string?)null), (log[0].ProjectId, log[0].HookId, log[0].DeliveryId, log[0].Event, log[0].Attempt, log[0].StatusCode, log[0].Success, log[0].Error));
        Assert.True(log[0].Id > log[1].Id);
        IsRecentUtc(log[0].CreatedAt);
        Assert.Null(log[1].StatusCode);
        Assert.Equal("Timed out", log[1].Error);
        Assert.Equal(2, Assert.Single(await s.ListWebhookDeliveriesAsync(pid, 1)).Attempt);
        await s.DeleteProjectAsync(pid);
        Assert.Empty(await s.ListWebhookDeliveriesAsync(pid));
        Assert.Single(await s.ListWebhookDeliveriesAsync(other));
    }

    [SkippableFact]
    public async Task Api_Tokens_Resolve_By_Hash_Record_Use_And_Can_Be_Revoked()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann", "correct horse");
        var ben = await s.CreateUserAsync("ben", "Ben", "battery staple");
        var token = ApiTokens.Generate();
        Assert.StartsWith("mrd_", token);

        var info = await s.CreateApiTokenAsync(ann.Id, "  CI  ", ApiTokens.Hash(token), ApiTokens.PrefixOf(token));
        Assert.Equal((ann.Id, "CI", token[..12], (DateTime?)null), (info.UserId, info.Name, info.Prefix, info.LastUsedAt));
        var unnamed = await s.CreateApiTokenAsync(ann.Id, " ", "other-hash", "mrd_other");
        Assert.Equal("API token", unnamed.Name);
        Assert.Equal(ApiTokens.Hash(token), await h.ScalarRawAsync("select token_hash from api_tokens where id = $1", info.Id));

        var listed = await s.ListApiTokensAsync(ann.Id);
        Assert.Equal(2, listed.Count);
        Assert.All(listed, t => Assert.Null(t.LastUsedAt));
        IsRecentUtc(listed[0].CreatedAt);

        Assert.Equal(ann, await s.FindUserByApiTokenAsync(ApiTokens.Hash(token)));
        Assert.Null(await s.FindUserByApiTokenAsync(ApiTokens.Hash(token + "x")));
        var used = (await s.ListApiTokensAsync(ann.Id)).Single(t => t.Id == info.Id);
        IsRecentUtc(used.LastUsedAt!.Value);
        Assert.Null((await s.ListApiTokensAsync(ann.Id)).Single(t => t.Id == unnamed.Id).LastUsedAt);
        Assert.Empty(await s.ListApiTokensAsync(ben.Id));

        await s.RevokeApiTokenAsync(ben.Id, info.Id);   // not ben's token: no effect
        Assert.NotNull(await s.FindUserByApiTokenAsync(ApiTokens.Hash(token)));
        await s.RevokeApiTokenAsync(ann.Id, info.Id);
        Assert.Null(await s.FindUserByApiTokenAsync(ApiTokens.Hash(token)));
        Assert.Equal([unnamed.Id], (await s.ListApiTokensAsync(ann.Id)).Select(t => t.Id));
        await Assert.ThrowsAnyAsync<Exception>(() => s.CreateApiTokenAsync(ben.Id, "dup", "other-hash", "x")); // hashes are unique
    }

    // ================================================================= administration

    [SkippableFact]
    public async Task First_Registered_User_Becomes_Admin_Once()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        Assert.False(await s.EnsureAdminAsync());   // nobody to promote
        await s.CreateUserAsync("local", "Local", "password-0"); // the desktop's built-in account never becomes admin
        var ann = await s.CreateUserAsync("ann", "Ann", "password-1");
        var ben = await s.CreateUserAsync("ben", "Ben", "password-2");
        Assert.True(await s.EnsureAdminAsync());
        Assert.False(await s.EnsureAdminAsync());
        Assert.True((await s.GetUserAsync(ann.Id))!.IsAdmin);
        Assert.False((await s.GetUserAsync(ben.Id))!.IsAdmin);
        Assert.Null(await s.GetUserAsync(Guid.NewGuid()));

        await s.SetUserAdminAsync(ben.Id, true);
        var users = await s.ListUsersAsync();
        Assert.Equal(["ann", "ben", "local"], users.Select(u => u.User.Username));
        Assert.Equal(2, users.Count(u => u.IsAdmin));
        Assert.All(users, u => Assert.True(u.HasPassword));
        Assert.All(users, u => IsRecentUtc(u.CreatedAt));
        Assert.All(users, u => Assert.Empty(u.SsoProviders));
        await s.SetUserAdminAsync(ann.Id, false);
        await s.SetUserAdminAsync(ben.Id, false);
        Assert.True(await s.EnsureAdminAsync());
        Assert.True((await s.GetUserAsync(ann.Id))!.IsAdmin);
    }

    [SkippableFact]
    public async Task Lockout_Counts_Failures_Unlocks_And_Refuses_Disabled_Accounts()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann", "correct horse");
        var lockout = TimeSpan.FromMinutes(5);
        for (var i = 1; i <= 2; i++)
        {
            var r = await s.AuthenticateAsync("ann", "nope", 3, lockout);
            Assert.Equal((LoginStatus.InvalidCredentials, (Guid?)ann.Id, (UserAccount?)null), (r.Status, r.UserId, r.User));
            Assert.Equal(i, (await s.GetUserAsync(ann.Id))!.FailedLogins);
        }
        var locked = await s.AuthenticateAsync("ann", "nope", 3, lockout);
        Assert.Equal(LoginStatus.LockedOut, locked.Status);
        Assert.InRange(locked.LockedUntil!.Value, DateTime.UtcNow.AddMinutes(4), DateTime.UtcNow.AddMinutes(6));
        var state = (await s.GetUserAsync(ann.Id))!;
        Assert.True(state.IsLockedOut(DateTime.UtcNow));
        Assert.Equal(locked.LockedUntil!.Value, state.LockedUntil!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(DateTimeKind.Utc, state.LockedUntil!.Value.Kind);
        Assert.Equal(LoginStatus.LockedOut, (await s.AuthenticateAsync("ann", "correct horse", 3, lockout)).Status);
        Assert.Null(await s.VerifyUserAsync("ann", "correct horse"));

        await s.UnlockUserAsync(ann.Id);
        state = (await s.GetUserAsync(ann.Id))!;
        Assert.Equal((0, (DateTime?)null), (state.FailedLogins, state.LockedUntil));
        Assert.Null(state.LastLoginAt);
        await s.AuthenticateAsync("ann", "nope", 3, lockout);
        var ok = await s.AuthenticateAsync("ANN", "correct horse", 3, lockout);
        Assert.Equal((LoginStatus.Success, ann), (ok.Status, ok.User));
        state = (await s.GetUserAsync(ann.Id))!;
        Assert.Equal(0, state.FailedLogins);   // success resets the count
        IsRecentUtc(state.LastLoginAt!.Value);

        // A lock that has expired lets the user in again.
        await s.AuthenticateAsync("ann", "nope", 1, TimeSpan.FromMilliseconds(1));
        await Task.Delay(20);
        Assert.Equal(LoginStatus.Success, (await s.AuthenticateAsync("ann", "correct horse", 1, lockout)).Status);
        // maxFailures = 0 disables lockout.
        for (var i = 0; i < 5; i++) Assert.Equal(LoginStatus.InvalidCredentials, (await s.AuthenticateAsync("ann", "nope", 0, lockout)).Status);

        await s.SetUserDisabledAsync(ann.Id, true);
        Assert.True((await s.GetUserAsync(ann.Id))!.Disabled);
        Assert.Equal(LoginStatus.Disabled, (await s.AuthenticateAsync("ann", "correct horse", 3, lockout)).Status);
        Assert.Null(await s.VerifyUserAsync("ann", "correct horse"));

        await s.SetUserDisabledAsync(ann.Id, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.SetPasswordAsync(ann.Id, "short"));
        await s.SetPasswordAsync(ann.Id, "a new password");
        Assert.Equal(0, (await s.GetUserAsync(ann.Id))!.FailedLogins);
        Assert.NotNull(await s.VerifyUserAsync("ann", "a new password"));
        Assert.Null(await s.VerifyUserAsync("ann", "correct horse"));
        var nobody = await s.AuthenticateAsync("nobody", "x", 3, lockout);
        Assert.Equal((LoginStatus.InvalidCredentials, (Guid?)null), (nobody.Status, nobody.UserId));
    }

    [SkippableFact]
    public async Task External_Logins_Provision_Unique_Usernames_And_Link()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var local = await s.CreateUserAsync("alice", "Local Alice", "password-1");
        var login = new ExternalLogin("entra", "https://login.example/tenant/v2.0", "sub-123", "alice@example.com");
        Assert.Null(await s.FindExternalLoginAsync(login.Issuer, login.Subject));

        var sso = await s.CreateExternalUserAsync(LoginPolicy.SuggestUsername("Alice@Example.com"), "Alice Smith", login);
        Assert.Equal(("alice2", "Alice Smith"), (sso.Username, sso.DisplayName));   // "alice" is a different, local account: never merged
        Assert.Equal(sso, await s.FindExternalLoginAsync(login.Issuer, login.Subject));
        Assert.Null(await s.FindExternalLoginAsync("https://other-issuer", "sub-123"));
        var third = await s.CreateExternalUserAsync("alice", "", login with { Subject = "sub-456", Email = null });
        Assert.Equal(("alice3", "alice3"), (third.Username, third.DisplayName));

        var state = (await s.GetUserAsync(sso.Id))!;
        Assert.False(state.HasPassword);
        Assert.Equal(["entra"], state.SsoProviders);
        Assert.Null(await s.VerifyUserAsync("alice2", LoginPolicy.NoPassword));

        // Linking more identities to an existing account; re-linking the same identity is a no-op.
        await s.LinkExternalLoginAsync(local.Id, new ExternalLogin("okta", "https://okta.example", "abc", null));
        await s.LinkExternalLoginAsync(local.Id, new ExternalLogin("google", "https://accounts.example", "abc", "a@example.com"));
        await s.LinkExternalLoginAsync(sso.Id, new ExternalLogin("okta", "https://okta.example", "abc", null));
        Assert.Equal(local, await s.FindExternalLoginAsync("https://okta.example", "abc"));
        Assert.Equal(["google", "okta"], (await s.GetUserAsync(local.Id))!.SsoProviders.Order());
        Assert.True((await s.GetUserAsync(local.Id))!.HasPassword);
    }

    [SkippableFact]
    public async Task Concurrent_First_Sign_Ins_Of_One_Identity_Create_One_Linked_Account()
    {
        await using var h = await OpenAsync();
        var other = h.OpenAnotherStore();
        var login = new ExternalLogin("entra", "https://login.example/t", "sub-1", "dana@example.com");
        var users = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => (i % 2 == 0 ? h.Store : other).CreateExternalUserAsync("dana", "Dana", login))));
        Assert.Single(users.Select(u => u.Id).Distinct());
        Assert.Equal(users[0], await h.Store.FindExternalLoginAsync(login.Issuer, login.Subject));
        Assert.Equal(["dana"], (await h.Store.ListUsersAsync()).Select(u => u.User.Username));
        // Provisioning an identity that is already linked returns its account.
        Assert.Equal(users[0], await h.Store.CreateExternalUserAsync("someone-else", "X", login));
    }

    [SkippableFact]
    public async Task Admin_Project_List_Transfer_Restore_And_Snapshots()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann", "password-1");
        var ben = await s.CreateUserAsync("ben", "Ben", "password-2");
        var pid = await s.CreateProjectAsync("rover", Json, ann.Id);
        await s.CreateProjectAsync("Apollo", Json, ben.Id);
        await s.CommitVersionAsync(pid, "first", Json, ann.Id);
        await s.CommitVersionAsync(pid, "second", Json, ben.Id);
        await s.SetMemberAsync(pid, ben.Id, ProjectRole.Viewer);
        await s.SaveProjectAsync(pid, Json, ann.Id);

        var all = await s.ListAllProjectsAsync();
        Assert.Equal(["Apollo", "rover"], all.Select(p => p.Name)); // case-insensitive order
        var p = all[1];
        Assert.Equal((pid, ann.Id, 2, 2, 1L), (p.Id, p.Owner!.Id, p.VersionCount, p.MemberCount, p.Revision));
        // Storage used by the model and its versions. SQLite counts the JSON text; PostgreSQL reports the stored (compressed jsonb) size.
        Assert.True(p.SizeBytes > Json.Length / 10, $"size {p.SizeBytes}");
        IsRecentUtc(p.CreatedAt);
        IsRecentUtc(p.UpdatedAt);

        await s.CommitVersionAsync(pid, "third", Json, ann.Id);
        Assert.True((await s.ListAllProjectsAsync()).Single(x => x.Id == pid).SizeBytes > p.SizeBytes);   // grows with history

        await s.TransferProjectAsync(pid, ben.Id);
        Assert.Equal(ProjectRole.Owner, await s.RoleOfAsync(pid, ben.Id));
        Assert.Equal(ProjectRole.Editor, await s.RoleOfAsync(pid, ann.Id));
        var carl = await s.CreateUserAsync("carl", "Carl", "password-3");
        await s.TransferProjectAsync(pid, carl.Id);   // not yet a member
        Assert.Equal([(carl.Id, ProjectRole.Owner), (ann.Id, ProjectRole.Editor), (ben.Id, ProjectRole.Editor)],
            (await s.MembersAsync(pid)).Select(m => (m.User.Id, m.Role)));

        var snapshots = await s.ListVersionSnapshotsAsync(pid);
        Assert.Equal([1, 2, 3], snapshots.Select(v => v.Number));
        Assert.Equal(["first", "second", "third"], snapshots.Select(v => v.Message));
        Assert.Equal([ann.Id, ben.Id, ann.Id], snapshots.Select(v => v.AuthorId!.Value));
        SameJson(Json, snapshots[0].ModelJson);
        Assert.Equal(DateTimeKind.Utc, snapshots[0].CreatedAt.Kind);

        // Same id still exists → restored as a new project; unknown members and authors are dropped.
        var oldTime = new DateTime(2020, 5, 17, 8, 30, 15, 123, DateTimeKind.Utc);
        var restoredVersions = snapshots.Take(2).Append(new VersionSnapshot(3, "ghost", Json, Guid.NewGuid(), oldTime)).ToList();
        var copy = await s.RestoreProjectAsync(new RestoredProject(pid, "  Rover (restored) ", Json, restoredVersions,
            [new RestoredMember(ann.Id, ProjectRole.Owner), new RestoredMember(Guid.NewGuid(), ProjectRole.Editor), new RestoredMember(ann.Id, ProjectRole.Viewer)]));
        Assert.NotEqual(pid, copy);
        Assert.Equal("Rover (restored)", (await s.LoadProjectAsync(copy))!.Value.Name);
        Assert.Equal(ProjectRole.Owner, await s.RoleOfAsync(copy, ann.Id));
        Assert.Single(await s.MembersAsync(copy));
        var versions = await s.ListVersionsAsync(copy);
        Assert.Equal(["ghost", "second", "first"], versions.Select(v => v.Message));
        Assert.Equal(("unknown", "Ben"), (versions[0].Author, versions[1].Author));
        Assert.Equal(oldTime, versions[0].CreatedAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal(snapshots[0].CreatedAt, versions[2].CreatedAt, TimeSpan.FromMilliseconds(1));
        Assert.Null((await s.ListVersionSnapshotsAsync(copy))[2].AuthorId);
        Assert.Equal(4, await s.CommitVersionAsync(copy, "after restore", Json, ann.Id));

        // Free id → kept.
        var fresh = Guid.NewGuid();
        Assert.Equal(fresh, await s.RestoreProjectAsync(new RestoredProject(fresh, "Other", Json, [], [new RestoredMember(ben.Id, ProjectRole.Owner)])));
        Assert.Equal(ProjectRole.Owner, await s.RoleOfAsync(fresh, ben.Id));
        Assert.Equal(4, (await s.ListAllProjectsAsync()).Count);
        Assert.Equal(ann.Id, (await s.ListAllProjectsAsync()).Single(x => x.Id == copy).Owner!.Id);
    }

    [SkippableFact]
    public async Task Restore_Brings_Back_Comments_Reviews_And_Baselines()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann", "password-1");
        var ben = await s.CreateUserAsync("ben", "Ben", "password-2");
        var t0 = new DateTime(2024, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        var ghost = Guid.NewGuid();   // an author with no account here
        var pid = Guid.NewGuid();
        var restored = await s.RestoreProjectAsync(new RestoredProject(pid, "Rover", Json,
            [new VersionSnapshot(1, "first", Json, ann.Id, t0), new VersionSnapshot(2, "second", Json, ben.Id, t0.AddDays(1))],
            [new RestoredMember(ann.Id, ProjectRole.Owner), new RestoredMember(ben.Id, ProjectRole.Editor)],
            Comments:
            [
                new RestoredComment(10, "el-1", ann.Id, "Is this right?", true, t0.AddHours(1)),
                new RestoredComment(11, "el-1", ghost, "Yes.", false, t0.AddHours(2), ParentKey: 10),
            ],
            Reviews:
            [
                new RestoredReview("Mass budget", "Check it", ann.Id, ReviewState.Approved, ReviewScopeKind.Versions, [], 1, 2, true,
                    t0.AddDays(2), t0.AddDays(3), [new RestoredReviewer(ben.Id, ReviewVote.Approve, t0.AddDays(3)), new RestoredReviewer(ghost, ReviewVote.Pending, null)],
                    [new RestoredReviewComment(5, "el-1", null, ben.Id, "Looks good", t0.AddDays(2)), new RestoredReviewComment(6, null, 5, ann.Id, "Thanks", t0.AddDays(2).AddHours(1))],
                    [new RestoredReviewCheck("el-1", ben.Id), new RestoredReviewCheck("el-2", ghost)]),
            ],
            Baselines: [new RestoredBaseline(1, "PDR", "Preliminary design", t0.AddDays(4)), new RestoredBaseline(9, "Lost", "", t0)]));
        Assert.Equal(pid, restored);

        var comments = (await s.ListCommentsAsync(pid)).OrderBy(c => c.Id).ToList();
        Assert.Equal(["Is this right?", "Yes."], comments.Select(c => c.Body));
        Assert.Equal((true, false), (comments[0].Resolved, comments[1].Resolved));
        Assert.Equal(ann.Id, comments[0].Author.Id);
        Assert.Equal(t0.AddHours(1), comments[0].CreatedAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal(comments[0].Id, (await s.ListCommentParentsAsync(pid))[comments[1].Id]);

        var versions = await s.ListVersionsAsync(pid);
        var review = Assert.Single(await s.ListReviewsAsync(pid));
        Assert.Equal(("Mass budget", ReviewState.Approved, ReviewScopeKind.Versions, true), (review.Title, review.State, review.ScopeKind, review.ApplyStatus));
        Assert.Equal((versions.Single(v => v.Number == 1).Id, versions.Single(v => v.Number == 2).Id), (review.BaseVersionId!.Value, review.HeadVersionId!.Value));
        Assert.Equal(ann.Id, review.Author.Id);
        Assert.Equal(t0.AddDays(2), review.CreatedAt, TimeSpan.FromMilliseconds(1));
        var reviewer = Assert.Single(review.Reviewers);   // the unknown reviewer is dropped
        Assert.Equal((ben.Id, ReviewVote.Approve), (reviewer.User.Id, reviewer.Vote));
        var rc = (await s.ListReviewCommentsAsync(review.Id)).OrderBy(c => c.Id).ToList();
        Assert.Equal(["Looks good", "Thanks"], rc.Select(c => c.Body));
        Assert.Equal(rc[0].Id, rc[1].ParentId);
        Assert.Equal("el-1", rc[0].ElementId);
        Assert.Equal([("el-1", ben.Id)], (await s.ListReviewChecksAsync(review.Id)).Select(c => (c.ElementId, c.UserId)));

        var baseline = Assert.Single(await s.ListBaselinesAsync(pid));   // the one on a missing version is skipped
        Assert.Equal(("PDR", "Preliminary design", 1), (baseline.Name, baseline.Description, baseline.VersionNumber));
    }

    [SkippableFact]
    public async Task Audit_Log_Is_Filterable_Paged_And_Append_Only()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann", "password-1");
        var pid = Guid.NewGuid();
        var t0 = DateTime.UtcNow.AddMinutes(-10);
        t0 = new DateTime(t0.Ticks - t0.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        for (var i = 0; i < 30; i++)
            await s.AppendAuditAsync(new AuditEntry(t0.AddSeconds(i), ann.Id, "ann", i % 3 == 0 ? "login.success" : "project.export",
                i % 2 == 0 ? pid : null, i % 2 == 0 ? "Rover" : null, $"file-{i}.xmi", i == 7 ? """{"reason":"Quarterly AUDIT"}""" : """{"bytes":42}""", "127.0.0.1"));
        await s.AppendAuditAsync(new AuditEntry(DateTime.UtcNow, null, "system", "backup.scheduled", Target: "nightly.zip"));

        var all = await s.QueryAuditAsync(new AuditQuery(Take: 10));
        Assert.Equal(31, all.Total);
        Assert.Equal(10, all.Items.Count);
        var newest = all.Items[0];
        Assert.Equal(("backup.scheduled", (Guid?)null, "system", (Guid?)null, (string?)null, "nightly.zip", (string?)null, (string?)null),
            (newest.Action, newest.ActorId, newest.Actor, newest.ProjectId, newest.ProjectName, newest.Target, newest.DetailsJson, newest.Ip));
        Assert.True(all.Items.Zip(all.Items.Skip(1)).All(x => x.First.Id > x.Second.Id)); // newest first
        var e29 = all.Items[1];
        Assert.Equal((ann.Id, "ann", "project.export", (Guid?)null, (string?)null, "file-29.xmi", "127.0.0.1"),
            (e29.ActorId!.Value, e29.Actor, e29.Action, e29.ProjectId, e29.ProjectName, e29.Target, e29.Ip));
        Assert.Equal((pid, "Rover"), (all.Items[2].ProjectId!.Value, all.Items[2].ProjectName));
        Assert.Equal(t0.AddSeconds(29), e29.At);
        Assert.Equal(DateTimeKind.Utc, e29.At.Kind);

        Assert.Equal(10, (await s.QueryAuditAsync(new AuditQuery(Action: "login."))).Total);
        Assert.Equal(15, (await s.QueryAuditAsync(new AuditQuery(ProjectId: pid))).Total);
        Assert.Equal(1, (await s.QueryAuditAsync(new AuditQuery(Text: "FILE-29"))).Total);
        Assert.Equal(1, (await s.QueryAuditAsync(new AuditQuery(Text: "quarterly audit"))).Total);   // searches the details too
        Assert.Equal(15, (await s.QueryAuditAsync(new AuditQuery(Text: "rover"))).Total);
        Assert.Equal(1, (await s.QueryAuditAsync(new AuditQuery(Actor: "SYS"))).Total);
        Assert.Equal(30, (await s.QueryAuditAsync(new AuditQuery(Actor: "ann"))).Total);
        Assert.Equal(5, (await s.QueryAuditAsync(new AuditQuery(From: t0.AddSeconds(25), To: t0.AddSeconds(30)))).Total);
        Assert.Equal(0, (await s.QueryAuditAsync(new AuditQuery(Action: "nothing"))).Total);
        var combined = await s.QueryAuditAsync(new AuditQuery(Action: "login.", ProjectId: pid, From: t0, To: t0.AddSeconds(13), Take: 1));
        Assert.Equal(3, combined.Total);   // 0, 6, 12
        Assert.Equal("file-12.xmi", Assert.Single(combined.Items).Target);
        var page2 = await s.QueryAuditAsync(new AuditQuery(Skip: 28, Take: 10));
        Assert.Equal(3, page2.Items.Count);
        Assert.Equal(31, page2.Total);
        SameJson("""{"bytes":42}""", page2.Items[0].DetailsJson);
        Assert.Equal(["file-2.xmi", "file-1.xmi", "file-0.xmi"], page2.Items.Select(e => e.Target));
        Assert.Empty((await s.QueryAuditAsync(new AuditQuery(Skip: 100))).Items);
        Assert.Single((await s.QueryAuditAsync(new AuditQuery(Take: 0))).Items);   // clamped to at least one

        // Append-only: the database itself refuses changes.
        await Assert.ThrowsAnyAsync<DbException>(() => h.ExecRawAsync("update audit_log set actor_name = 'mallory'"));
        await Assert.ThrowsAnyAsync<DbException>(() => h.ExecRawAsync("delete from audit_log"));
        await Assert.ThrowsAnyAsync<DbException>(() => h.ExecRawAsync("delete from audit_log where id = $1", newest.Id));
        Assert.Equal(31, (await s.QueryAuditAsync(new AuditQuery())).Total);
        Assert.Equal(0, (await s.QueryAuditAsync(new AuditQuery(Actor: "mallory"))).Total);
    }

    // ================================================================= concurrency

    [SkippableFact]
    public async Task Concurrent_Saves_Get_Distinct_Revisions()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann", "password-1");
        var pid = await s.CreateProjectAsync("P", Json, ann.Id);
        var other = h.OpenAnotherStore();   // a second server process

        var two = await Task.WhenAll(s.SaveProjectAsync(pid, Json, ann.Id), other.SaveProjectAsync(pid, Json, ann.Id));
        Assert.Equal([1L, 2L], two.Order());

        var many = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => (i % 2 == 0 ? s : other).SaveProjectAsync(pid, Json, ann.Id)));
        Assert.Equal(Enumerable.Range(3, 20).Select(i => (long)i), many.Order());
        Assert.Equal(22, (await s.LoadProjectAsync(pid))!.Value.Revision);
    }

    [SkippableFact]
    public async Task Concurrent_Commits_Get_Distinct_Version_Numbers()
    {
        await using var h = await OpenAsync();
        var s = h.Store;
        var ann = await s.CreateUserAsync("ann", "Ann", "password-1");
        var pid = await s.CreateProjectAsync("P", Json, ann.Id);
        var bid = await s.CreateBranchAsync(pid, "b", Json, Json, null, ann.Id);
        var other = h.OpenAnotherStore();

        var plain = Enumerable.Range(0, 10).Select(i => (i % 2 == 0 ? s : other).CommitVersionAsync(pid, $"main {i}", Json, ann.Id));
        var onBranch = Enumerable.Range(0, 10).Select(async i => (await (i % 2 == 0 ? other : s).CommitBranchVersionAsync(pid, i % 3 == 0 ? null : bid, $"branch {i}", Json, ann.Id)).Number);
        var numbers = await Task.WhenAll(plain.Concat(onBranch));
        Assert.Equal(Enumerable.Range(1, 20), numbers.Order());
        Assert.Equal(20, (await s.ListVersionsAsync(pid)).Select(v => v.Number).Distinct().Count());
    }
}
