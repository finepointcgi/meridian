using System.IO.Compression;
using Bunit;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Admin;
using Meridian.Web.Components.Admin;
using Meridian.Web.Components.Pages;
using Meridian.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>Enterprise administration: SSO sign-in page, settings binding, audit CSV, backups and the admin console.</summary>
public sealed class AdminTests : TestContext, IAsyncLifetime
{
    private readonly List<string> _files = [];
    private static readonly string Json = ProjectFile.Serialize(SampleModels.Vehicle());

    public AdminTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in _files.SelectMany(f => new[] { f, f + "-wal", f + "-shm" })) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    private async Task<SqliteStore> NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meridian-admin-web-{Guid.NewGuid():N}.db");
        _files.Add(path);
        var store = new SqliteStore(path);
        await store.MigrateAsync();
        return store;
    }

    private static AdminSettings Settings(params (string Key, string Value)[] values) =>
        AdminSettings.From(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build());

    [Fact]
    public void Settings_Bind_Providers_Admins_And_Drop_Incomplete_Providers()
    {
        var s = Settings(
            ("Meridian:Sso:Providers:0:Name", "entra"), ("Meridian:Sso:Providers:0:DisplayName", "Microsoft"),
            ("Meridian:Sso:Providers:0:Authority", "https://login.microsoftonline.com/t/v2.0"), ("Meridian:Sso:Providers:0:ClientId", "abc"),
            ("Meridian:Sso:Providers:0:ClaimMappings:Username", "upn"),
            ("Meridian:Sso:Providers:1:Name", "broken"),
            ("Meridian:Sso:DisableLocalPasswords", "true"),
            ("Meridian:Admins", "Alice, bob"), ("Meridian:Security:MaxFailedLogins", "3"),
            ("Meridian:Backup:Folder", "/tmp/x"), ("Meridian:Backup:Retain", "4"));
        var p = Assert.Single(s.Sso.Providers);
        Assert.Equal(("oidc-entra", "Microsoft", "upn"), (p.Scheme, p.Label, p.ClaimMappings.Username));
        Assert.True(s.Sso.DisableLocalPasswords);
        Assert.Equal(["alice", "bob"], s.Security.Admins);
        Assert.Equal(3, s.Security.MaxFailedLogins);
        Assert.True(s.Backup.Enabled);
        Assert.Equal(4, s.Backup.Retain);
    }

    [Fact]
    public void Login_Page_Shows_Sso_Buttons_And_Can_Hide_Passwords()
    {
        var provider = new SsoProviderOptions { Name = "okta", DisplayName = "Okta", Authority = "https://x.okta.com", ClientId = "c" };
        Services.AddSingleton(new AdminSettings(new SsoOptions { Providers = [provider], DisableLocalPasswords = true }, new SecurityOptions(), new BackupOptions()));
        var cut = RenderComponent<Login>();
        var button = cut.Find("a.auth-sso-btn");
        Assert.Equal("/account/sso/okta", button.GetAttribute("href"));
        Assert.Contains("Sign in with Okta", button.TextContent);
        Assert.Empty(cut.FindAll("form"));
        Assert.Empty(cut.FindAll(".auth-switch a[href*=register]"));
    }

    [Fact]
    public void Audit_Csv_Quotes_And_Neutralises_Formulas()
    {
        var csv = AuditLog.ToCsv([new AuditEntry(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), null, "=cmd", "project.export", null, "A, \"B\"", "x.xmi", """{"a":1}""", "::1", 7)]);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("id,time_utc,actor", lines[0]);
        Assert.Equal("7,2026-01-02T03:04:05Z,'=cmd,,project.export,\"A, \"\"B\"\"\",,x.xmi,\"{\"\"a\"\":1}\",::1", lines[1]);
    }

    [Fact]
    public async Task Backup_Round_Trips_Projects_History_And_Members_Into_Another_Server()
    {
        var source = await NewStore();
        var ann = await source.CreateUserAsync("ann", "Ann", "password-1");
        var ben = await source.CreateUserAsync("ben", "Ben", "password-2");
        var pid = await source.CreateProjectAsync("Rover", Json, ann.Id);
        await source.SetMemberAsync(pid, ben.Id, ProjectRole.Editor);
        await source.CommitVersionAsync(pid, "baseline", Json, ben.Id);

        var settings = new AdminSettings(new(), new(), new());
        BackupService Service(IMeridianStore db) => new(db, new CollaborationHub(db, NullLogger<CollaborationHub>.Instance),
            new AuditLog(db, NullLogger<AuditLog>.Instance), settings, NullLogger<BackupService>.Instance);

        using var ms = new MemoryStream();
        var manifest = await Service(source).WriteAsync(ms, includeUsers: true, "admin");
        Assert.Equal(pid, Assert.Single(manifest.Projects).Id);
        ms.Position = 0;
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.NotNull(zip.GetEntry($"projects/{pid:D}/versions/0001.meridian"));
            var users = new StreamReader(zip.GetEntry("users.json")!.Open()).ReadToEnd();
            Assert.Contains("\"ann\"", users);
            Assert.DoesNotContain("pbkdf2", users);
        }

        // Restore into another server: "ann" exists there (matched by username), "ben" is recreated from users.json.
        var target = await NewStore();
        var admin = await target.CreateUserAsync("root", "Root", "password-3");
        var annThere = await target.CreateUserAsync("ann", "Ann", "password-4");
        ms.Position = 0;
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true))
        {
            var result = Assert.Single(await Service(target).RestoreAsync(zip, null, recreateMissingUsers: true, admin));
            Assert.Null(result.Error);
            Assert.Equal(pid, result.NewId);
        }
        Assert.Equal("Rover", (await target.LoadProjectAsync(pid))!.Value.Name);
        Assert.Equal(ProjectRole.Owner, await target.RoleOfAsync(pid, annThere.Id));
        var benThere = await target.FindUserAsync("ben");
        Assert.NotNull(benThere);
        Assert.Equal(ProjectRole.Editor, await target.RoleOfAsync(pid, benThere.Id));
        var v = Assert.Single(await target.ListVersionsAsync(pid));
        Assert.Equal(("baseline", "Ben"), (v.Message, v.Author));
        Assert.Contains((await target.QueryAuditAsync(new AuditQuery(Action: "backup.restore"))).Items, e => e.ProjectId == pid);

        // Restoring again makes a copy rather than overwriting.
        ms.Position = 0;
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true))
            Assert.NotEqual(pid, Assert.Single(await Service(target).RestoreAsync(zip, null, false, admin)).NewId);
        Assert.Equal(2, (await target.ListAllProjectsAsync()).Count);
    }

    [Fact]
    public async Task Project_With_History_Moves_From_A_Local_Workspace_To_A_Team_Server()
    {
        var settings = new AdminSettings(new(), new(), new());
        BackupService Service(IMeridianStore db) => new(db, new CollaborationHub(db, NullLogger<CollaborationHub>.Instance),
            new AuditLog(db, NullLogger<AuditLog>.Instance), settings, NullLogger<BackupService>.Instance);

        // The desktop's local workspace: one account, a project with versions, a comment thread, a review and a baseline.
        var local = await NewStore();
        var me = await local.CreateUserAsync("local", "Me", "password-1");
        var pid = await local.CreateProjectAsync("Rover", Json, me.Id);
        var other = await local.CreateProjectAsync("Not this one", Json, me.Id);
        await local.CommitVersionAsync(pid, "first", Json, me.Id);
        await local.CommitVersionAsync(pid, "second", Json, me.Id);
        var v1 = (await local.ListVersionsAsync(pid)).Single(v => v.Number == 1);
        var question = await local.AddCommentAsync(pid, "el-1", me.Id, "Is the mass right?");
        var answer = await local.AddCommentAsync(pid, "el-1", me.Id, "Checked: yes.");
        await local.SetCommentParentAsync(answer, question);
        await local.SetCommentResolvedAsync(question, true);
        var review = await local.CreateReviewAsync(pid, new ReviewDraft("Design review", "", ReviewScopeKind.Versions, [], v1.Id, null, false, [me.Id]), me.Id);
        await local.AddReviewCommentAsync(review, "el-1", null, me.Id, "Fine by me");
        await local.SetReviewVoteAsync(review, me.Id, ReviewVote.Approve);
        await local.CreateBaselineAsync(pid, v1.Id, "PDR", "Preliminary design", me.Id);

        using var ms = new MemoryStream();
        var manifest = await Service(local).WriteProjectAsync(ms, pid, me.Username);
        Assert.Equal((BackupService.CurrentFormatVersion, false), (manifest.FormatVersion, manifest.IncludesUsers));
        Assert.Equal(pid, Assert.Single(manifest.Projects).Id);   // only this project
        Assert.Equal("Rover (with history).zip", BackupService.ProjectFileName("Rover"));
        ms.Position = 0;
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.Null(zip.GetEntry("users.json"));
            Assert.Null(zip.GetEntry($"projects/{other:D}/project.json"));
            Assert.NotNull(zip.GetEntry($"projects/{pid:D}/comments.json"));
        }

        // A team server where someone else happens to be called "local": the import must not hand them anything.
        var server = await NewStore();
        var dana = await server.CreateUserAsync("dana", "Dana", "password-2");
        var namesake = await server.CreateUserAsync("local", "Someone else", "password-3");
        ms.Position = 0;
        BackupService.RestoreResult result;
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true))
            result = Assert.Single(await Service(server).ImportProjectsAsync(zip, dana));
        Assert.Null(result.Error);
        var id = result.NewId!.Value;
        Assert.Equal(("Rover", 2), ((await server.LoadProjectAsync(id))!.Value.Name, result.Versions));
        Assert.Equal([(dana.Id, ProjectRole.Owner)], (await server.MembersAsync(id)).Select(m => (m.User.Id, m.Role)));
        Assert.Null(await server.RoleOfAsync(id, namesake.Id));

        var versions = await server.ListVersionsAsync(id);
        Assert.Equal(["second", "first"], versions.Select(v => v.Message));
        Assert.All(versions, v => Assert.Equal("Dana", v.Author));
        var comments = (await server.ListCommentsAsync(id)).OrderBy(c => c.Id).ToList();
        Assert.Equal(["Is the mass right?", "Checked: yes."], comments.Select(c => c.Body));
        Assert.All(comments, c => Assert.Equal(dana.Id, c.Author.Id));
        Assert.True(comments[0].Resolved);
        Assert.Equal(comments[0].Id, (await server.ListCommentParentsAsync(id))[comments[1].Id]);
        var r = Assert.Single(await server.ListReviewsAsync(id));
        Assert.Equal(("Design review", dana.Id), (r.Title, r.Author.Id));
        Assert.Equal(versions.Single(v => v.Number == 1).Id, r.BaseVersionId);
        Assert.Equal([(dana.Id, ReviewVote.Approve)], r.Reviewers.Select(x => (x.User.Id, x.Vote)));
        Assert.Equal(["Fine by me"], (await server.ListReviewCommentsAsync(r.Id)).Select(c => c.Body));
        var baseline = Assert.Single(await server.ListBaselinesAsync(id));
        Assert.Equal(("PDR", 1), (baseline.Name, baseline.VersionNumber));
        Assert.Contains((await server.QueryAuditAsync(new AuditQuery(Action: AuditActions.ProjectImportHistory))).Items, e => e.ProjectId == id);

        // Opening it a second time makes a copy; the first stays as it was.
        ms.Position = 0;
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true))
            Assert.NotEqual(id, Assert.Single(await Service(server).ImportProjectsAsync(zip, dana)).NewId);
    }

    [Fact]
    public async Task Format_1_Backups_Without_Comments_Still_Restore()
    {
        var store = await NewStore();
        var admin = await store.CreateUserAsync("root", "Root", "password-1");
        var pid = Guid.NewGuid();
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Write(string path, string text) { using var w = new StreamWriter(zip.CreateEntry(path).Open()); w.Write(text); }
            Write("manifest.json", $$"""{"format":"meridian-backup","formatVersion":1,"createdAt":"2025-01-01T00:00:00Z","createdBy":"x","includesUsers":false,"projects":[{"id":"{{pid}}","name":"Old","revision":1,"updatedAt":"2025-01-01T00:00:00Z","versions":0}]}""");
            Write($"projects/{pid:D}/project.json", $$"""{"id":"{{pid}}","name":"Old","revision":1,"createdAt":"2025-01-01T00:00:00Z","updatedAt":"2025-01-01T00:00:00Z","members":[]}""");
            Write($"projects/{pid:D}/model.meridian", Json);
            Write($"projects/{pid:D}/versions.json", "[]");
        }
        ms.Position = 0;
        using var read = new ZipArchive(ms, ZipArchiveMode.Read);
        var settings = new AdminSettings(new(), new(), new());
        var service = new BackupService(store, new CollaborationHub(store, NullLogger<CollaborationHub>.Instance), new AuditLog(store, NullLogger<AuditLog>.Instance), settings, NullLogger<BackupService>.Instance);
        var result = Assert.Single(await service.RestoreAsync(read, null, false, admin));
        Assert.Null(result.Error);
        Assert.Empty(await store.ListCommentsAsync(pid));
        Assert.Equal(ProjectRole.Owner, await store.RoleOfAsync(pid, admin.Id));
    }

    [Fact]
    public async Task Admin_Users_Tab_Lists_Accounts_And_Admin_Checks_Are_Live()
    {
        var db = await NewStore();
        var ann = await db.CreateUserAsync("ann", "Ann Lee", "password-1");
        var ben = await db.CreateUserAsync("ben", "Ben Ode", "password-2");
        await db.EnsureAdminAsync();
        Services.AddSingleton<IMeridianStore>(db);
        Services.AddSingleton<IAuditLog>(new AuditLog(db, NullLogger<AuditLog>.Instance));
        Services.AddSingleton(new MeridianHostInfo(false, Path.GetTempPath(), null));
        Services.AddSingleton<AdminAccess>();
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var me = (await db.GetUserAsync(ann.Id))!;
        var cut = RenderComponent<AdminUsers>(p => p.Add(x => x.Me, me));
        cut.WaitForState(() => cut.FindAll(".admin-table tbody tr").Count == 2);
        Assert.Contains("Ann Lee (you)", cut.Markup);
        Assert.Single(cut.FindAll(".pill.admin"));

        await db.SetUserAdminAsync(ben.Id, true);
        Assert.True(await new AdminAccess(db, new MeridianHostInfo(false, "", null)).IsAdminAsync(MeridianAuth.Principal(ben)));
        Assert.False(await new AdminAccess(db, new MeridianHostInfo(true, "", null)).IsAdminAsync(MeridianAuth.Principal(ben)));
    }
}
