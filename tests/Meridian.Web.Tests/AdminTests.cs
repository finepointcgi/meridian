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
