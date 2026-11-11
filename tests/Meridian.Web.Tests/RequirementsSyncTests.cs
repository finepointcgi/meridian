using System.Net;
using System.Text;
using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Integrations;
using Meridian.Web.Integrations.Requirements;
using Meridian.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using static Meridian.Web.Tests.Fixtures;

namespace Meridian.Web.Tests;

/// <summary>Import mapping, re-sync, suspect marking, conflicts and pending links (no I/O).</summary>
public class RequirementsSyncTests
{
    private static readonly Guid Conn = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly RequirementsToolConfig Cfg = new() { Type = RequirementsToolKinds.DoorsNext };

    private static RemoteRequirement R(string n, string title, string text, string rev) => new($"https://dn/r/{n}", $"https://dn/r/{n}", n, title, text, rev);

    private static (ModelRepository Model, Element Block) NewModel()
    {
        var m = new ModelRepository("Car");
        var block = m.Create(m.Root, ElementKind.Block, "LaneKeepEcu");
        return (m, block);
    }

    private static Element Req(ModelRepository m, string id) => m.OfKind(ElementKind.Requirement).Single(e => e.Get(FieldKeys.RequirementId) == id);

    [Fact]
    public void Import_Creates_Requirements_With_External_Id_Url_And_Revision()
    {
        var (m, _) = NewModel();
        var result = RequirementsSync.Apply(m, Conn, "DOORS", Cfg, [R("1201", "Lane keeping", "Keep the lane.", "rev1"), R("1202", "Braking", "", "rev1")]);

        Assert.Equal((2, 0, 0), (result.Created, result.Updated, result.Unchanged));
        var e = Req(m, "1201");
        Assert.Equal(("Lane keeping", "Keep the lane.", "Imported from DOORS"), (e.Name, e.Get(FieldKeys.Text), e.Owner!.Name));
        Assert.Equal(("1201", "https://dn/r/1201", "rev1"), (e.Get(ExternalRequirements.IdAttribute), e.Get(ExternalRequirements.UrlAttribute), e.Get(ExternalRequirements.RevisionAttribute)));
        var sync = ExternalRequirements.Read(e)!;
        Assert.Equal((Conn.ToString(), "https://dn/r/1201", "rev1", "Lane keeping"), (sync.Connection, sync.Id, sync.Revision, sync.Title));
        Assert.Equal("Sync requirements from DOORS", m.History.UndoDescription);
        m.History.Undo();
        Assert.Empty(m.OfKind(ElementKind.Requirement));   // one undo step

        // A configured target package is used instead.
        var pkg = m.Create(m.Root, ElementKind.Package, "Stakeholder");
        RequirementsSync.Apply(m, Conn, "DOORS", new RequirementsToolConfig { TargetPackage = pkg.Id }, [R("1201", "Lane keeping", "", "rev1")]);
        Assert.Same(pkg, Req(m, "1201").Owner);
    }

    [Fact]
    public void Re_Sync_Applies_Remote_Changes_And_Marks_Trace_Links_Suspect()
    {
        var (m, block) = NewModel();
        RequirementsSync.Apply(m, Conn, "DOORS", Cfg, [R("1201", "Lane keeping", "Keep the lane.", "rev1"), R("1202", "Braking", "Stop.", "rev1")]);
        var sat = m.Connect(ElementKind.Satisfy, block, Req(m, "1201"));
        Assert.False(SuspectLinks.IsSuspect(sat));

        var result = RequirementsSync.Apply(m, Conn, "DOORS", Cfg, [R("1201", "Lane keeping", "Keep the lane above 60 km/h.", "rev2"), R("1202", "Braking", "Stop.", "rev1")]);

        Assert.Equal((0, 1, 1, 1), (result.Created, result.Updated, result.Unchanged, result.NewSuspectLinks));
        Assert.Equal("Keep the lane above 60 km/h.", Req(m, "1201").Get(FieldKeys.Text));
        Assert.True(SuspectLinks.IsSuspect(sat));
        Assert.Contains("changed since this «satisfy» link was reviewed", SuspectLinks.Describe(sat));
        Assert.Equal("rev2", Req(m, "1201").Get(ExternalRequirements.RevisionAttribute));
        Assert.Contains("1 link now suspect", result.Summary);

        // Undo restores the text and the link is no longer suspect (the suspect baseline is part of the same step).
        m.History.Undo();
        Assert.False(SuspectLinks.IsSuspect(sat));
    }

    [Fact]
    public void A_New_Revision_Without_Content_Changes_Only_Moves_The_Revision()
    {
        var (m, block) = NewModel();
        RequirementsSync.Apply(m, Conn, "DOORS", Cfg, [R("1201", "Lane keeping", "Keep the lane.", "rev1")]);
        var sat = m.Connect(ElementKind.Satisfy, block, Req(m, "1201"));
        var result = RequirementsSync.Apply(m, Conn, "DOORS", Cfg, [R("1201", "Lane keeping", "Keep the lane.\r\n", "rev9")]);
        Assert.Equal((0, 1, 0), (result.Updated, result.Unchanged, result.NewSuspectLinks));
        Assert.Equal("rev9", ExternalRequirements.Read(Req(m, "1201"))!.Revision);
        Assert.False(SuspectLinks.IsSuspect(sat));
    }

    [Fact]
    public void Local_Edits_Alone_Are_Kept_And_Reported()
    {
        var (m, _) = NewModel();
        RequirementsSync.Apply(m, Conn, "DOORS", Cfg, [R("1201", "Lane keeping", "Keep the lane.", "rev1")]);
        m.Set(Req(m, "1201"), FieldKeys.Text, "Keep the lane, Meridian edit.");
        var result = RequirementsSync.Apply(m, Conn, "DOORS", Cfg, [R("1201", "Lane keeping", "Keep the lane.", "rev1")]);
        Assert.Equal((1, 0), (result.LocalOnly, result.Conflicts.Count));
        Assert.Equal("Keep the lane, Meridian edit.", Req(m, "1201").Get(FieldKeys.Text));
    }

    [Fact]
    public void Changes_On_Both_Sides_Are_Conflicts_Until_Resolved()
    {
        var (m, _) = NewModel();
        RequirementsSync.Apply(m, Conn, "DOORS", Cfg, [R("1201", "Lane keeping", "Keep the lane.", "rev1"), R("1202", "Braking", "Stop.", "rev1")]);
        var e1 = Req(m, "1201");
        var e2 = Req(m, "1202");
        m.Set(e1, FieldKeys.Text, "Meridian text");
        m.Set(e2, FieldKeys.Text, "Meridian braking");
        IReadOnlyList<RemoteRequirement> remote = [R("1201", "Lane keeping", "DOORS text", "rev2"), R("1202", "Braking", "DOORS braking", "rev2")];

        var first = RequirementsSync.Apply(m, Conn, "DOORS", Cfg, remote);
        Assert.Equal(2, first.Conflicts.Count);
        var c = first.Conflicts.Single(x => x.ElementId == e1.Id);
        Assert.Equal(("Keep the lane.", "Meridian text", "DOORS text", "rev2"), (c.BaseText, c.LocalText, c.RemoteText, c.RemoteRevision));
        Assert.Equal("Meridian text", e1.Get(FieldKeys.Text));   // nothing applied

        var resolved = RequirementsSync.Apply(m, Conn, "DOORS", Cfg, remote,
            new Dictionary<string, ConflictChoice> { [e1.Id] = ConflictChoice.TakeRemote, [e2.Id] = ConflictChoice.KeepMeridian });
        Assert.Empty(resolved.Conflicts);
        Assert.Equal(("DOORS text", "Meridian braking"), (e1.Get(FieldKeys.Text), e2.Get(FieldKeys.Text)));

        // Afterwards Meridian's kept version counts as a local change, not a conflict.
        var again = RequirementsSync.Apply(m, Conn, "DOORS", Cfg, remote);
        Assert.Equal((0, 1), (again.Conflicts.Count, again.LocalOnly));
    }

    [Fact]
    public void The_Same_Change_On_Both_Sides_Is_Not_A_Conflict()
    {
        var (m, _) = NewModel();
        RequirementsSync.Apply(m, Conn, "DOORS", Cfg, [R("1201", "Lane keeping", "Keep the lane.", "rev1")]);
        m.Rename(Req(m, "1201"), "Lane keeping (LKA)");
        var result = RequirementsSync.Apply(m, Conn, "DOORS", Cfg, [R("1201", "Lane keeping (LKA)", "Keep the lane.", "rev2")]);
        Assert.Equal((0, 1, 0), (result.Conflicts.Count, result.Unchanged, result.LocalOnly));
        Assert.Equal("Lane keeping (LKA)", ExternalRequirements.Read(Req(m, "1201"))!.Title);
    }

    [Fact]
    public void Existing_Requirements_With_The_Same_Id_Are_Adopted_And_Missing_Ones_Reported()
    {
        var m = SampleModels.Vehicle();
        var before = m.OfKind(ElementKind.Requirement).Count();
        var accel = Req(m, "REQ-002");
        var satisfy = m.Incoming(accel, ElementKind.Satisfy).First();
        var result = RequirementsSync.Apply(m, Conn, "DOORS", Cfg, [new RemoteRequirement("https://dn/r/9", "https://dn/r/9", "REQ-002", "Acceleration", "0 to 100 km/h in 7 s.", "r1")]);
        Assert.Equal((0, 1), (result.Created, result.Updated));
        Assert.Equal(before, m.OfKind(ElementKind.Requirement).Count());
        Assert.Equal("https://dn/r/9", ExternalRequirements.Read(accel)!.Id);
        Assert.True(SuspectLinks.IsSuspect(satisfy));

        Assert.Equal(1, RequirementsSync.Apply(m, Conn, "DOORS", Cfg, []).Missing);
    }

    [Fact]
    public void Pending_Links_Cover_Satisfy_Verify_And_Trace_Once()
    {
        var (m, block) = NewModel();
        RequirementsSync.Apply(m, Conn, "Polarion", Cfg, [
            new RemoteRequirement("dp/DP-1", "https://pol/DP-1", "DP-1", "A", "", "1"),
            new RemoteRequirement("dp/DP-2", "https://pol/DP-2", "DP-2", "B", "", "1")]);
        var (a, b) = (Req(m, "DP-1"), Req(m, "DP-2"));
        var test = m.Create(m.Root, ElementKind.TestCase, "Brake test");
        m.Connect(ElementKind.Satisfy, block, a);
        m.Connect(ElementKind.Verify, test, a);
        m.Connect(ElementKind.Trace, a, b);
        m.Connect(ElementKind.DeriveReqt, b, a);   // not published

        var pending = RequirementsSync.PendingLinks(m, Conn, e => $"https://meridian.test/oslc/{e.Name}");
        Assert.Equal(3, pending.Count);
        Assert.Contains(pending, p => p.Link.Kind == "satisfy" && p.Link.TargetUri == "https://meridian.test/oslc/LaneKeepEcu" && p.Link.TargetExternalId is null && p.Sync.Id == "dp/DP-1");
        Assert.Contains(pending, p => p.Link.Kind == "verify" && p.Link.Label == "Brake test");
        var trace = pending.Single(p => p.Link.Kind == "trace");
        Assert.Equal(("dp/DP-1", "dp/DP-2", "https://pol/DP-2", "DP-2 B"), (trace.Sync.Id, trace.Link.TargetExternalId, trace.Link.TargetUri, trace.Link.Label));

        RequirementsSync.MarkPublished(m, pending.Take(2).Select(p => (p.Requirement, p.Key)));
        Assert.Single(RequirementsSync.PendingLinks(m, Conn, e => $"https://meridian.test/oslc/{e.Name}"));
        Assert.Equal("Record published requirement links", m.History.UndoDescription);
    }

    [Fact]
    public void Word_Diff_Marks_Removed_And_Added_Words()
    {
        var parts = TextDiff.Words("Stop within 40 m on dry asphalt.", "Stop within 38 m on asphalt.");
        Assert.Equal("Stop within 40 m on dry asphalt.", string.Concat(parts.Where(p => p.Op != TextDiff.Op.Added).Select(p => p.Text)));
        Assert.Equal("Stop within 38 m on asphalt.", string.Concat(parts.Where(p => p.Op != TextDiff.Op.Removed).Select(p => p.Text)));
        Assert.Contains(parts, p => p.Op == TextDiff.Op.Removed && p.Text.Contains("40"));
        Assert.Contains(parts, p => p.Op == TextDiff.Op.Added && p.Text.Contains("38"));
        Assert.Contains(parts, p => p.Op == TextDiff.Op.Removed && p.Text.Contains("dry"));
    }

    [Fact]
    public void Config_Validation_And_Schedule()
    {
        var c = new RequirementsToolConfig { Type = RequirementsToolKinds.DoorsNext, BaseUrl = "http://jazz.example.test/rm", User = "ann", Project = "p" };
        Assert.Null(c.Validate());   // plain http is allowed (internal servers)
        c.BaseUrl = "ftp://jazz.example.test/rm";
        Assert.Contains("http", c.Validate());
        c.BaseUrl = "https://jazz.example.test/rm";
        Assert.Null(c.Validate());
        c.User = null;
        Assert.Contains("username", c.Validate());
        var p = new RequirementsToolConfig { Type = RequirementsToolKinds.Polarion, BaseUrl = "https://polarion.example.test", Project = "" };
        Assert.NotNull(p.Validate());
        Assert.Null(p.Validate(needProject: false));
        p.ScheduleMinutes = 7;
        Assert.Contains("schedule", p.Validate(needProject: false));

        var due = new RequirementsToolConfig { ScheduleMinutes = 60, LastSync = new RequirementsLastSync { At = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc) } };
        Assert.False(RequirementsToolService.IsDue(due, new DateTime(2026, 1, 1, 10, 30, 0, DateTimeKind.Utc)));
        Assert.True(RequirementsToolService.IsDue(due, new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc)));
        Assert.False(RequirementsToolService.IsDue(new RequirementsToolConfig(), DateTime.UtcNow));
        Assert.True(RequirementsToolService.IsDue(new RequirementsToolConfig { ScheduleMinutes = 15 }, DateTime.UtcNow));
    }
}

/// <summary>All requirements-tool HTTP goes through the SSRF-guarded client.</summary>
public class RequirementsToolGuardTests
{
    private static ServiceProvider Services(bool localMode = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(new MeridianHostInfo(localMode, Path.GetTempPath(), null));
        RequirementsToolsSetup.AddMeridianRequirementsTools(services);
        return services.BuildServiceProvider();
    }

    private static SocketsHttpHandler Primary(HttpMessageHandler h)
    {
        while (h is DelegatingHandler d) h = d.InnerHandler!;
        return Assert.IsType<SocketsHttpHandler>(h);
    }

    [Fact]
    public void The_Named_Client_Uses_The_Outbound_Guard_Without_Cookies_Or_Automatic_Redirects()
    {
        using var sp = Services();
        var handler = Primary(sp.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(RequirementsToolService.HttpClientName));
        Assert.NotNull(handler.ConnectCallback);
        Assert.False(handler.UseCookies);
        Assert.False(handler.AllowAutoRedirect);
        // The shared integrations handler no longer keeps cookies across connections either.
        Assert.False(OutboundGuard.CreateHandler(false).UseCookies);
    }

    [Theory]
    [InlineData("https://169.254.169.254/rm")]
    [InlineData("https://127.0.0.1/rm")]
    [InlineData("https://10.1.2.3/rm")]
    public async Task Metadata_Loopback_And_Private_Addresses_Are_Refused(string baseUrl)
    {
        using var sp = Services();
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(RequirementsToolService.HttpClientName);
        var dn = new DoorsNextSource(new RequirementsToolConfig { Type = RequirementsToolKinds.DoorsNext, BaseUrl = baseUrl, Auth = JazzAuth.Basic, User = "ann", Project = "p" }, "pw", client);
        var ex = await Assert.ThrowsAsync<RequirementsToolException>(() => dn.ProjectsAsync());
        Assert.Contains("not allowed", ex.Message);
        var pol = new PolarionSource(new RequirementsToolConfig { Type = RequirementsToolKinds.Polarion, BaseUrl = baseUrl, Project = "p" }, "tok", client);
        Assert.Contains("not allowed", (await Assert.ThrowsAsync<RequirementsToolException>(() => pol.ProjectsAsync())).Message);
    }

    /// <summary>The fake answers for *.example.test; everything else goes to the real guarded handler.</summary>
    private sealed class SplitHandler(HttpMessageHandler fake, HttpMessageHandler real) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _fake = new(fake), _real = new(real);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            request.RequestUri!.Host.EndsWith(".example.test", StringComparison.Ordinal) ? _fake.SendAsync(request, ct) : _real.SendAsync(request, ct);
    }

    [Fact]
    public async Task Redirects_Are_Checked_Too_And_Never_Carry_Credentials_To_Another_Host()
    {
        var fake = new FakeServer()
            .Get(Rm + "/rootservices", () => FakeServer.Redirect("https://mirror.example.test/rm/rootservices"))
            .Get("https://mirror.example.test/rm/rootservices", () => FakeServer.Redirect("https://169.254.169.254/latest/meta-data/"));
        using var real = OutboundGuard.CreateHandler(allowPrivateNetworks: true, followRedirects: false);
        var dn = new DoorsNextSource(new RequirementsToolConfig { Type = RequirementsToolKinds.DoorsNext, BaseUrl = Rm, Auth = JazzAuth.Basic, User = "ann", Project = "p" }, "pw",
            new HttpClient(new SplitHandler(fake, real)));

        var ex = await Assert.ThrowsAsync<RequirementsToolException>(() => dn.ProjectsAsync());

        Assert.Contains("169.254.169.254", ex.Message);
        Assert.Contains("not allowed", ex.Message);   // link-local is refused even with private networks allowed
        Assert.NotNull(fake.Requests[0].Request.Headers.Authorization);
        Assert.Null(fake.Requests[1].Request.Headers.Authorization);   // other origin: no Basic credentials
    }
}

/// <summary>The service end to end: encrypted settings, sync into a model, re-sync, publish-back, schedule.</summary>
public sealed class RequirementsToolServiceTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-rt-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;
    private CollaborationHub _hub = default!;
    private UserAccount _owner = default!;
    private Guid _project;
    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();
    private static readonly Oslc.Urls Urls = new("https://meridian.test");

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        _hub = new CollaborationHub(_db, NullLogger<CollaborationHub>.Instance);
        _owner = await _db.CreateUserAsync("olga", "Olga", "correct horse");
        _project = await _db.CreateProjectAsync("Vehicle", ProjectFile.Serialize(SampleModels.Vehicle()), _owner.Id);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    private (RequirementsToolService Tools, IntegrationService Integrations) Service(HttpMessageHandler handler, int polarionPageSize = 2)
    {
        var factory = new FakeHttpClientFactory(handler);
        var integrations = new IntegrationService(_db, Protection, factory);
        var services = new ServiceCollection().AddSingleton(_hub).BuildServiceProvider();
        var tools = new RequirementsToolService(_db, integrations, factory, services, NullLogger<RequirementsToolService>.Instance);
        tools.SourceFactory = (cfg, secret, http) => cfg.IsDoors ? new DoorsNextSource(cfg, secret, http) : new PolarionSource(cfg, secret, http) { PageSize = polarionPageSize };
        return (tools, integrations);
    }

    private static RequirementsToolConfig Doors(bool publish = false) => new()
    {
        Type = RequirementsToolKinds.DoorsNext, BaseUrl = Rm, Auth = JazzAuth.Basic, User = "ann", Project = Provider1, ProjectTitle = "Drive Assist", PublishLinks = publish,
    };

    [Fact]
    public async Task Doors_Next_Connection_Saves_Encrypted_Syncs_Re_Syncs_And_Publishes_Links()
    {
        var r1Text = "The system shall keep the vehicle centred in its lane at speeds above 60&#160;km/h.";
        var r1Modified = "2026-10-02T09:14:31.112Z";
        var server = DoorsNextSourceTests.Server(r1: () => R1Body(text: r1Text, modified: r1Modified));
        var puts = new List<string>();
        server.On(HttpMethod.Put, u => u.AbsoluteUri.StartsWith(Rm + "/resources/"), (r, body) => { puts.Add(body!); return new HttpResponseMessage(HttpStatusCode.OK); });
        var (tools, _) = Service(server);

        Assert.Equal("Connected to DOORS Next — project “Drive Assist (Requirements)”", await tools.TestAsync(_project, Doors(), "s3cret-pw", null));
        var conn = await tools.SaveAsync(_project, null, "DOORS", Doors(publish: true), "s3cret-pw", _owner.Id);
        var row = (await _db.ListIntegrationsAsync(_project)).Single();
        Assert.DoesNotContain("s3cret-pw", row.ProtectedSecret);
        Assert.DoesNotContain("s3cret-pw", row.ConfigJson);
        Assert.Equal(_owner.Id, RequirementsToolConfig.Parse(row.ConfigJson).RunAs);

        var m = SampleModels.Vehicle();
        var first = await tools.SyncAsync(m, conn, Urls);
        Assert.Equal(3, first.Created);
        var lane = m.OfKind(ElementKind.Requirement).Single(e => e.Get(FieldKeys.RequirementId) == "1201");
        Assert.Equal("Imported from DOORS", lane.Owner!.Name);
        var last = RequirementsToolConfig.Parse((await _db.FindIntegrationAsync(conn.Id))!.ConfigJson).LastSync!;
        Assert.Equal(("ok", 0), (last.Status, last.Conflicts));
        Assert.Contains("3 new", last.Summary);

        // Meridian links the requirement; the next sync publishes it back with If-Match, and only once.
        var vehicle = m.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle");
        m.Connect(ElementKind.Satisfy, vehicle, lane);
        var second = await tools.SyncAsync(m, conn, Urls);
        Assert.Equal(1, second.LinksPublished);
        Assert.Contains($"https://meridian.test/oslc/projects/{_project}/resources/{vehicle.Id}", Assert.Single(puts));
        await tools.SyncAsync(m, conn, Urls);
        Assert.Single(puts);

        // DOORS Next changes the text: re-sync updates it and the satisfy link becomes suspect.
        r1Text = "The system shall keep the vehicle centred in its lane above 50&#160;km/h.";
        r1Modified = "2026-11-01T08:00:00.000Z";
        var third = await tools.SyncAsync(m, conn, Urls);
        Assert.Equal((1, 1), (third.Updated, third.NewSuspectLinks));
        Assert.Contains("above 50 km/h", lane.Get(FieldKeys.Text));
        Assert.True(SuspectLinks.Of(m, lane).Any());

        // Both sides change: a conflict, recorded in the connection.
        m.Set(lane, FieldKeys.Text, "Meridian's own wording.");
        r1Text = "Remote wording.";
        r1Modified = "2026-11-02T08:00:00.000Z";
        var fourth = await tools.SyncAsync(m, conn, Urls);
        Assert.Single(fourth.Conflicts);
        Assert.Equal("conflicts", RequirementsToolConfig.Parse((await _db.FindIntegrationAsync(conn.Id))!.ConfigJson).LastSync!.Status);
        var fifth = await tools.SyncAsync(m, conn, Urls, new Dictionary<string, ConflictChoice> { [lane.Id] = ConflictChoice.TakeRemote });
        Assert.Empty(fifth.Conflicts);
        Assert.Equal("Remote wording.", lane.Get(FieldKeys.Text));
    }

    [Fact]
    public async Task Publishing_Is_Off_By_Default()
    {
        var server = DoorsNextSourceTests.Server();
        var (tools, _) = Service(server);
        var conn = await tools.SaveAsync(_project, null, "DOORS", Doors(), "pw", _owner.Id);
        var m = SampleModels.Vehicle();
        await tools.SyncAsync(m, conn, Urls);
        m.Connect(ElementKind.Satisfy, m.OfKind(ElementKind.Block).First(), m.OfKind(ElementKind.Requirement).Single(e => e.Get(FieldKeys.RequirementId) == "1201"));
        var result = await tools.SyncAsync(m, conn, Urls);
        Assert.Equal(0, result.LinksPublished);
        Assert.DoesNotContain(server.Requests, r => r.Request.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task Polarion_Connection_Publishes_Linked_Work_Items_And_Oslc_Links()
    {
        var server = PolarionSourceTests.Server();
        var posts = new List<(string Path, string Body)>();
        server.On(HttpMethod.Post, u => u.AbsolutePath.Contains("/workitems/"), (r, body) => { posts.Add((r.RequestUri!.AbsolutePath, body!)); return FakeServer.Json("{}", HttpStatusCode.Created); });
        var (tools, _) = Service(server);
        var cfg = new RequirementsToolConfig { Type = RequirementsToolKinds.Polarion, BaseUrl = Polarion, Project = "drivepilot", PublishLinks = true };
        var conn = await tools.SaveAsync(_project, null, "Polarion", cfg, "pat-123", _owner.Id);

        var m = SampleModels.Vehicle();
        Assert.Equal(3, (await tools.SyncAsync(m, conn, Urls)).Created);
        var (dp101, dp102) = (m.OfKind(ElementKind.Requirement).Single(e => e.Get(FieldKeys.RequirementId) == "DP-101"), m.OfKind(ElementKind.Requirement).Single(e => e.Get(FieldKeys.RequirementId) == "DP-102"));
        m.Connect(ElementKind.Trace, dp101, dp102);
        var brakes = m.OfKind(ElementKind.Block).Single(b => b.Name == "BrakeSystem");
        m.Connect(ElementKind.Satisfy, brakes, dp101);

        var result = await tools.SyncAsync(m, conn, Urls);

        Assert.Equal(2, result.LinksPublished);
        Assert.Contains(posts, p => p.Path.EndsWith("/workitems/DP-101/linkedworkitems") && p.Body.Contains("\"drivepilot/DP-102\"") && p.Body.Contains("relates_to"));
        Assert.Contains(posts, p => p.Path.EndsWith("/workitems/DP-101/linkedoslcresources") && p.Body.Contains($"/oslc/projects/{_project}/resources/{brakes.Id}"));
        Assert.All(server.Requests, r => Assert.Equal("Bearer", r.Request.Headers.Authorization!.Scheme));
    }

    [Fact]
    public async Task A_Failed_Sync_Is_Recorded_And_Reported()
    {
        var server = new FakeServer().On(HttpMethod.Get, _ => true, (_, _) => FakeServer.Json(PolarionUnauthorized, HttpStatusCode.Unauthorized));
        var (tools, _) = Service(server);
        var conn = await tools.SaveAsync(_project, null, "Polarion", new RequirementsToolConfig { Type = RequirementsToolKinds.Polarion, BaseUrl = Polarion, Project = "drivepilot" }, "pat-bad", _owner.Id);
        await Assert.ThrowsAsync<RequirementsToolException>(() => tools.SyncAsync(SampleModels.Vehicle(), conn, Urls));
        var last = RequirementsToolConfig.Parse((await _db.FindIntegrationAsync(conn.Id))!.ConfigJson).LastSync!;
        Assert.Equal("failed", last.Status);
        Assert.DoesNotContain("pat-bad", last.Summary);

        var readOnly = SampleModels.Vehicle();
        readOnly.History.ReadOnly = true;
        await Assert.ThrowsAsync<RequirementsToolException>(() => tools.SyncAsync(readOnly, conn, Urls));
    }

    [Fact]
    public async Task Scheduled_Sync_Edits_The_Live_Project_As_The_Saving_Owner()
    {
        var server = PolarionSourceTests.Server();
        var (tools, _) = Service(server);
        var cfg = new RequirementsToolConfig { Type = RequirementsToolKinds.Polarion, BaseUrl = Polarion, Project = "drivepilot", ScheduleMinutes = 15 };
        await tools.SaveAsync(_project, null, "Polarion", cfg, "pat-123", _owner.Id);
        var scheduler = new RequirementsSyncScheduler(_db, tools, new ConfigurationBuilder().Build(), NullLogger<RequirementsSyncScheduler>.Instance);

        Assert.Equal(1, await scheduler.RunDueAsync(DateTime.UtcNow));

        var saved = ProjectFile.Deserialize((await _db.LoadProjectAsync(_project))!.Value.ModelJson);
        Assert.Equal(["DP-101", "DP-102", "DP-205"], saved.OfKind(ElementKind.Requirement).Select(e => e.Get(FieldKeys.RequirementId)).Where(id => id.StartsWith("DP-")).OrderBy(x => x));
        var conn = (await _db.ListIntegrationsAsync(_project)).Single();
        Assert.Equal("ok", RequirementsToolConfig.Parse(conn.ConfigJson).LastSync!.Status);
        Assert.Equal(0, await scheduler.RunDueAsync(DateTime.UtcNow));   // not due again yet

        // An open tab sees the scheduled change live.
        var ws = new Workspace { User = _owner };
        Assert.True(await ws.JoinAsync(_hub, _project, ProjectRole.Owner));
        Assert.Contains(ws.Model.OfKind(ElementKind.Requirement), e => e.Get(FieldKeys.RequirementId) == "DP-205");
        ws.LeaveSession();
    }

    [Fact]
    public async Task Scheduled_Sync_Is_Skipped_When_The_Owner_Lost_Edit_Rights()
    {
        var server = PolarionSourceTests.Server();
        var (tools, _) = Service(server);
        var editor = await _db.CreateUserAsync("ed", "Ed", "pw pw pw pw");
        await _db.SetMemberAsync(_project, editor.Id, ProjectRole.Editor);
        var conn = await tools.SaveAsync(_project, null, "Polarion", new RequirementsToolConfig { Type = RequirementsToolKinds.Polarion, BaseUrl = Polarion, Project = "drivepilot", ScheduleMinutes = 15 }, "pat", editor.Id);
        await _db.SetMemberAsync(_project, editor.Id, ProjectRole.Viewer);

        Assert.Null(await tools.SyncScheduledAsync(conn, null));
        Assert.Empty(server.Requests);
        Assert.Contains("can no longer edit", RequirementsToolConfig.Parse((await _db.FindIntegrationAsync(conn.Id))!.ConfigJson).LastSync!.Summary);
    }
}
