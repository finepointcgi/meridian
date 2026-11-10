using System.Net;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Admin;
using Meridian.Web.Integrations;
using Meridian.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meridian.Web.Tests;

/// <summary>Regression tests for the security review (docs/SECURITY-REVIEW.md), against a real SQLite store.</summary>
public sealed class SecurityRegressionTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-sec-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;
    private CollaborationHub _hub = default!;
    private UserAccount _owner = default!, _viewer = default!;
    private Guid _project;

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        _hub = new CollaborationHub(_db, NullLogger<CollaborationHub>.Instance);
        _owner = await _db.CreateUserAsync("olga", "Olga", "correct horse");
        _viewer = await _db.CreateUserAsync("victor", "Victor", "battery staple");
        _project = await _db.CreateProjectAsync("Vehicle", ProjectFile.Serialize(SampleModels.Vehicle()), _owner.Id);
        await _db.SetMemberAsync(_project, _viewer.Id, ProjectRole.Viewer);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    private async Task<Workspace> Open(UserAccount user, ProjectRole role)
    {
        var ws = new Workspace { User = user };
        Assert.True(await ws.JoinAsync(_hub, _project, role));
        return ws;
    }

    // ------------------------------------------------------------------ SR-04: script console gate on team servers

    private static ScriptingSettings Server(bool allowOwners = false) => ScriptingSettings.From(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Meridian:Scripting:Enabled"] = "true",
            ["Meridian:Scripting:AllowProjectOwners"] = allowOwners ? "true" : null,
        }).Build(),
        new MeridianHostInfo(false, Path.GetTempPath(), null));

    [Fact]
    public async Task SR04_Server_Scripts_Require_A_Server_Administrator_Not_Just_A_Project_Owner()
    {
        var ws = await Open(_owner, ProjectRole.Owner);   // anyone can own a project they created
        Assert.Contains("server administrators", Server().DenialReason(ws));

        ws.IsServerAdmin = true;
        Assert.Null(Server().DenialReason(ws));

        ws.IsServerAdmin = false;
        Assert.Null(Server(allowOwners: true).DenialReason(ws));   // explicit opt-in restores the old rule
        var viewer = await Open(_viewer, ProjectRole.Viewer);
        Assert.NotNull(Server(allowOwners: true).DenialReason(viewer));

        // Local (desktop) mode is unchanged.
        var local = ScriptingSettings.From(new ConfigurationBuilder().Build(), new MeridianHostInfo(true, Path.GetTempPath(), null));
        Assert.Null(local.DenialReason(new Workspace { User = _owner }));
    }

    // ------------------------------------------------------------------ SR-06: viewers cannot change the model or commit

    [Fact]
    public async Task SR06_The_Session_Refuses_Operations_From_Viewers()
    {
        var owner = await Open(_owner, ProjectRole.Owner);
        var viewer = await Open(_viewer, ProjectRole.Viewer);
        var block = owner.Model.OfKind(ElementKind.Block).First();

        // A forged operation from the viewer's tab (bypassing its read-only replica).
        owner.Session!.Submit(viewer.ParticipantId, _viewer.Id, new SetFieldOp(block.Id, FieldKeys.Name, "Hacked"));
        Assert.DoesNotContain("Hacked", owner.Session.Snapshot());

        // Editors are unaffected.
        owner.Model.Set(block, FieldKeys.Name, "Renamed");
        Assert.Contains("Renamed", owner.Session.Snapshot());
        Assert.Contains("Renamed", viewer.Model.Find(block.Id)!.Name);
    }

    [Fact]
    public async Task SR06_Viewers_Cannot_Commit_Versions()
    {
        var viewer = await Open(_viewer, ProjectRole.Viewer);
        await Assert.ThrowsAsync<InvalidOperationException>(() => VersionControl.CommitAsync(_db, viewer.Session!, "sneaky", _viewer.Id, _viewer));
        Assert.Empty(await _db.ListVersionsAsync(_project));

        var owner = await Open(_owner, ProjectRole.Owner);
        await VersionControl.CommitAsync(_db, owner.Session!, "ok", _owner.Id, _owner);
        Assert.Single(await _db.ListVersionsAsync(_project));
    }

    // ------------------------------------------------------------------ SR-05: SSRF guard for webhooks and issue trackers

    [Theory]
    [InlineData("169.254.169.254", false, true)]
    [InlineData("169.254.169.254", true, true)]      // metadata stays blocked even when private networks are allowed
    [InlineData("::ffff:169.254.169.254", true, true)]
    [InlineData("fd00:ec2::254", true, true)]
    [InlineData("100.100.100.200", true, true)]
    [InlineData("0.0.0.0", true, true)]
    [InlineData("fe80::1", true, true)]
    [InlineData("127.0.0.1", false, true)]
    [InlineData("::1", false, true)]
    [InlineData("10.1.2.3", false, true)]
    [InlineData("172.20.0.5", false, true)]
    [InlineData("192.168.1.10", false, true)]
    [InlineData("fd12:3456::1", false, true)]
    [InlineData("10.1.2.3", true, false)]
    [InlineData("127.0.0.1", true, false)]
    [InlineData("8.8.8.8", false, false)]
    [InlineData("2606:4700:4700::1111", false, false)]
    public void SR05_Outbound_Guard_Blocks_Internal_Addresses(string ip, bool allowPrivate, bool blocked) =>
        Assert.Equal(blocked, OutboundGuard.IsBlocked(IPAddress.Parse(ip), allowPrivate));

    [Fact]
    public async Task SR05_Outbound_Guard_Refuses_To_Connect_To_Loopback()
    {
        using var http = new HttpClient(OutboundGuard.CreateHandler(allowPrivateNetworks: false));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://127.0.0.1:9/"));
        Assert.Contains("not allowed", ex.Message);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://localhost:9/"));
    }

    // ------------------------------------------------------------------ SR-08 / SR-10: headers and the launch redirect

    [Fact]
    public void SR08_Responses_Forbid_Framing_And_Sniffing_Except_Oslc_Dialogs()
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/";
        SecurityHeaders.Apply(http);
        Assert.Equal("SAMEORIGIN", http.Response.Headers.XFrameOptions.ToString());
        Assert.Contains("frame-ancestors 'self'", http.Response.Headers.ContentSecurityPolicy.ToString());
        Assert.Equal("nosniff", http.Response.Headers.XContentTypeOptions.ToString());

        var oslc = new DefaultHttpContext();
        oslc.Request.Path = "/oslc/projects/x/selector";
        SecurityHeaders.Apply(oslc);
        Assert.Empty(oslc.Response.Headers.XFrameOptions.ToString());

        var img = new DefaultHttpContext();
        img.Request.Path = "/_img/abc";
        img.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        SecurityHeaders.Apply(img);
        Assert.Equal("default-src 'none'; sandbox", img.Response.Headers.ContentSecurityPolicy.ToString());
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/login", "/")]
    [InlineData("/admin", "/admin")]
    [InlineData("//evil.example/x", "/")]
    [InlineData("/\\evil.example", "/")]
    public void SR10_Launch_Sign_In_Only_Redirects_Within_The_App(string path, string expected) =>
        Assert.Equal(expected, MeridianServer.LocalRedirectPath(new PathString(path)));
}
