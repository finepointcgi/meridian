using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Integrations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meridian.Web.Tests;

/// <summary>Records requests and answers them from a script — no real network access.</summary>
public sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        lock (Requests) Requests.Add((request, body));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

public class IssueTrackerTests
{
    private const string Token = "s3cr3t-token-value";

    [Fact]
    public async Task Jira_Cloud_Creates_An_Issue_With_Basic_Auth_And_A_Back_Link()
    {
        var handler = new FakeHandler((req, _) => req.Method == HttpMethod.Post
            ? FakeHandler.Json("""{"id":"10001","key":"MER-7","self":"x"}""", HttpStatusCode.Created)
            : FakeHandler.Json("""{"key":"MER-7","fields":{"summary":"REQ-002 Acceleration","status":{"name":"To Do","statusCategory":{"key":"new"}},"assignee":{"displayName":"Ann Lee"}}}"""));
        var cfg = new IssueTrackerConfig { Type = TrackerTypes.JiraCloud, BaseUrl = "https://acme.atlassian.net/", Project = "MER", User = "ann@acme.test" };
        Assert.Null(cfg.Validate());
        var jira = IssueTrackerFactory.Create(cfg, Token, new HttpClient(handler));

        var issue = await jira.CreateAsync("REQ-002 Acceleration", "Zero to 100 in 8 s.", "https://meridian.test/?project=p&element=e");

        Assert.Equal(new IssueInfo("MER-7", "https://acme.atlassian.net/browse/MER-7", "REQ-002 Acceleration", "To Do", "Ann Lee", false), issue);
        var (post, body) = handler.Requests[0];
        Assert.Equal("https://acme.atlassian.net/rest/api/3/issue", post.RequestUri!.ToString());
        Assert.Equal("Basic", post.Headers.Authorization!.Scheme);
        Assert.Equal("ann@acme.test:" + Token, Encoding.UTF8.GetString(Convert.FromBase64String(post.Headers.Authorization.Parameter!)));
        var fields = JsonNode.Parse(body!)!["fields"]!;
        Assert.Equal("MER", (string)fields["project"]!["key"]!);
        Assert.Equal("Task", (string)fields["issuetype"]!["name"]!);
        Assert.Equal("doc", (string)fields["description"]!["type"]!);   // Atlassian Document Format
        var lastParagraph = fields["description"]!["content"]!.AsArray()[^1]!;
        Assert.Equal("https://meridian.test/?project=p&element=e", (string)lastParagraph["content"]![0]!["marks"]![0]!["attrs"]!["href"]!);
        Assert.Contains("/rest/api/3/issue/MER-7", handler.Requests[1].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Jira_Server_Uses_Bearer_Pat_And_Api_V2_Search()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""{"issues":[{"key":"OPS-1","fields":{"summary":"Brake test","status":{"name":"Done","statusCategory":{"key":"done"}}}}]}"""));
        var jira = IssueTrackerFactory.Create(new IssueTrackerConfig { Type = TrackerTypes.JiraServer, BaseUrl = "https://jira.acme.test", Project = "OPS" }, Token, new HttpClient(handler));

        var found = await jira.SearchAsync("brake");

        var r = Assert.Single(found);
        Assert.True(r.Closed);
        Assert.Null(r.Assignee);
        var req = handler.Requests[0].Request;
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal(Token, req.Headers.Authorization.Parameter);
        Assert.StartsWith("https://jira.acme.test/rest/api/2/search?jql=", req.RequestUri!.ToString());
        Assert.Contains("text ~ \"brake\"", Uri.UnescapeDataString(req.RequestUri.Query));
        Assert.Equal("OPS-12", jira.ParseKey("https://jira.acme.test/browse/OPS-12"));
        Assert.Equal("OPS-3", jira.ParseKey("ops-3"));
        Assert.Null(jira.ParseKey("just words"));
    }

    [Fact]
    public async Task Azure_DevOps_Creates_Work_Items_With_Json_Patch_And_Pat()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""{"id":42,"fields":{"System.Title":"Braking","System.State":"New","System.AssignedTo":{"displayName":"Ben"}}}"""));
        var ado = IssueTrackerFactory.Create(new IssueTrackerConfig { Type = TrackerTypes.AzureDevOps, BaseUrl = "https://dev.azure.com/acme", Project = "Car Program", IssueType = "User Story" }, Token, new HttpClient(handler));

        var issue = await ado.CreateAsync("Braking", "Stop within 40 m.", "https://meridian.test/x");

        Assert.Equal("42", issue.Key);
        Assert.Equal("https://dev.azure.com/acme/Car%20Program/_workitems/edit/42", issue.Url);
        Assert.Equal("Ben", issue.Assignee);
        var (req, body) = handler.Requests[0];
        Assert.Equal("https://dev.azure.com/acme/Car%20Program/_apis/wit/workitems/$User%20Story?api-version=7.1", req.RequestUri!.AbsoluteUri);
        Assert.Equal("application/json-patch+json", req.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(":" + Token, Encoding.UTF8.GetString(Convert.FromBase64String(req.Headers.Authorization!.Parameter!)));
        var ops = JsonNode.Parse(body!)!.AsArray();
        Assert.Contains(ops, o => (string)o!["path"]! == "/relations/-" && (string)o["value"]!["url"]! == "https://meridian.test/x");
        Assert.Equal("17", ado.ParseKey("https://dev.azure.com/acme/Car%20Program/_workitems/edit/17"));
    }

    [Fact]
    public async Task GitHub_Searches_The_Repository_And_Parses_Issue_Urls()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""{"items":[{"number":5,"title":"Range too short","state":"open","html_url":"https://github.com/acme/car/issues/5","assignee":{"login":"ann"}}]}"""));
        var gh = IssueTrackerFactory.Create(new IssueTrackerConfig { Type = TrackerTypes.GitHub, BaseUrl = "https://api.github.com", Project = "acme/car" }, Token, new HttpClient(handler));

        var found = await gh.SearchAsync("range");

        Assert.Equal(new IssueInfo("#5", "https://github.com/acme/car/issues/5", "Range too short", "open", "ann", false), Assert.Single(found));
        var req = handler.Requests[0].Request;
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Contains("repo:acme/car", Uri.UnescapeDataString(req.RequestUri!.Query));
        Assert.Equal("9", gh.ParseKey("https://github.com/acme/car/issues/9"));
        Assert.Equal("9", gh.ParseKey("#9"));
        Assert.Null(gh.ParseKey("https://github.com/other/repo/issues/9"));
    }

    [Fact]
    public async Task Errors_Explain_The_Failure_Without_Leaking_The_Token()
    {
        var handler = new FakeHandler((req, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("Bad credentials for " + req.Headers.Authorization) });
        var gh = IssueTrackerFactory.Create(new IssueTrackerConfig { Type = TrackerTypes.GitHub, BaseUrl = "https://api.github.com", Project = "acme/car" }, Token, new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<IssueTrackerException>(() => gh.TestAsync());
        Assert.Contains("401", ex.Message);
        Assert.DoesNotContain(Token, ex.Message);
    }

    [Fact]
    public void Config_Validation_Requires_Https_And_Repository_Shape()
    {
        Assert.NotNull(new IssueTrackerConfig { Type = TrackerTypes.GitHub, BaseUrl = "http://api.github.com", Project = "a/b" }.Validate());
        Assert.NotNull(new IssueTrackerConfig { Type = TrackerTypes.GitHub, BaseUrl = "https://api.github.com", Project = "nope" }.Validate());
        Assert.NotNull(new IssueTrackerConfig { Type = TrackerTypes.JiraCloud, BaseUrl = "https://x.atlassian.net", Project = "MER" }.Validate());   // needs email
        Assert.Null(new IssueTrackerConfig { Type = TrackerTypes.AzureDevOps, BaseUrl = "https://dev.azure.com/acme", Project = "Car" }.Validate());
    }

    [Fact]
    public void Issue_Links_Round_Trip_Through_The_Element_Field_And_Deep_Links_Parse()
    {
        var m = SampleModels.Vehicle();
        var req = m.OfKind(ElementKind.Requirement).First();
        var conn = Guid.NewGuid();
        IssueLinks.Add(m, req, IssueLink.From(conn, new IssueInfo("MER-1", "https://j/browse/MER-1", "T", "Open", null, false)));
        IssueLinks.Add(m, req, IssueLink.From(conn, new IssueInfo("MER-1", "https://j/browse/MER-1", "T2", "Done", "Ann", true)));   // replaces
        var reloaded = ProjectFile.Deserialize(ProjectFile.Serialize(m)).Find(req.Id)!;
        var link = Assert.Single(IssueLinks.Read(reloaded));
        Assert.Equal("Done", link.Status);
        m.History.Undo();
        Assert.Equal("Open", Assert.Single(IssueLinks.Read(req)).Status);

        var pid = Guid.NewGuid();
        var url = DeepLink.For("https://meridian.test/", pid, req.Id);
        Assert.Equal((pid, req.Id), DeepLink.Parse(url));
        Assert.Null(DeepLink.Parse("https://meridian.test/"));
    }
}

/// <summary>Settings encryption, sync and bulk push through <see cref="IntegrationService"/> over a temporary SQLite store.</summary>
public sealed class IntegrationServiceTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-web-ix-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;
    private Guid _project;

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        var user = await _db.CreateUserAsync("ann", "Ann", "correct horse");
        _project = await _db.CreateProjectAsync("Vehicle", ProjectFile.Serialize(SampleModels.Vehicle()), user.Id);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    [Fact]
    public async Task Secrets_Are_Encrypted_At_Rest_And_Kept_When_Not_Re_Entered()
    {
        var svc = new IntegrationService(_db, Protection, new FakeHttpClientFactory(new FakeHandler((_, _) => FakeHandler.Json("{}"))));
        var cfg = new IssueTrackerConfig { Type = TrackerTypes.GitHub, BaseUrl = "https://api.github.com", Project = "acme/car" };
        var saved = await svc.SaveAsync(_project, null, IntegrationKinds.IssueTracker, "GitHub", cfg.ToJson(), "ghp_plaintext_secret");

        var row = (await _db.ListIntegrationsAsync(_project)).Single();
        Assert.NotNull(row.ProtectedSecret);
        Assert.DoesNotContain("ghp_plaintext_secret", row.ProtectedSecret);
        Assert.DoesNotContain("ghp_plaintext_secret", row.ConfigJson);
        Assert.Equal("ghp_plaintext_secret", svc.Unprotect(row.ProtectedSecret));

        await svc.SaveAsync(_project, saved.Id, IntegrationKinds.IssueTracker, "GitHub (renamed)", cfg.ToJson(), "");
        var updated = (await _db.ListIntegrationsAsync(_project)).Single();
        Assert.Equal("GitHub (renamed)", updated.Name);
        Assert.Equal("ghp_plaintext_secret", svc.Unprotect(updated.ProtectedSecret));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SaveAsync(Guid.NewGuid(), saved.Id, IntegrationKinds.IssueTracker, "x", cfg.ToJson(), null));
    }

    [Fact]
    public async Task Sync_Refreshes_Every_Link_And_Push_Creates_Issues_For_Unlinked_Requirements()
    {
        var created = 0;
        var handler = new FakeHandler((req, body) =>
        {
            if (req.Method == HttpMethod.Post)
            {
                var n = Interlocked.Increment(ref created);
                var title = (string)JsonNode.Parse(body!)!["title"]!;
                return FakeHandler.Json($$"""{"number":{{100 + n}},"title":"{{title}}","state":"open","html_url":"https://github.com/acme/car/issues/{{100 + n}}"}""");
            }
            var number = req.RequestUri!.Segments[^1];
            return FakeHandler.Json(new JsonObject
            {
                ["number"] = int.Parse(number), ["title"] = "Fresh title", ["state"] = "closed",
                ["html_url"] = $"https://github.com/acme/car/issues/{number}", ["assignee"] = new JsonObject { ["login"] = "ben" },
            }.ToJsonString());
        });
        var svc = new IntegrationService(_db, Protection, new FakeHttpClientFactory(handler));
        var cfg = new IssueTrackerConfig { Type = TrackerTypes.GitHub, BaseUrl = "https://api.github.com", Project = "acme/car" };
        var conn = await svc.SaveAsync(_project, null, IntegrationKinds.IssueTracker, "GitHub", cfg.ToJson(), "tok");

        var m = SampleModels.Vehicle();
        var reqPkg = m.OfKind(ElementKind.Package).First(p => p.Name == "Requirements");
        var first = reqPkg.Descendants().First(e => e.Kind == ElementKind.Requirement);
        IssueLinks.Add(m, first, new IssueLink { Connection = conn.Id.ToString(), Key = "#7", Url = "u", Title = "Old", Status = "open" });

        var push = await svc.PushRequirementsAsync(m, reqPkg, conn, e => $"https://meridian.test/?element={e.Id}");
        Assert.Equal(4, push.Done);
        Assert.Equal(1, push.Skipped);
        Assert.Empty(push.Errors);
        Assert.Contains(handler.Requests, r => r.Body?.Contains("REQ-004 Braking") == true);
        Assert.All(reqPkg.Descendants().Where(e => e.Kind == ElementKind.Requirement), r => Assert.Single(IssueLinks.Read(r)));

        var sync = await svc.SyncAsync(m, _project);
        Assert.Equal(5, sync.Done);
        var refreshed = Assert.Single(IssueLinks.Read(first));
        Assert.Equal(("Fresh title", "closed", "ben", true), (refreshed.Title, refreshed.Status, refreshed.Assignee, refreshed.Closed));
        Assert.Equal("Sync issue status", m.History.UndoDescription);
    }
}

public class WebhookTests
{
    [Fact]
    public void Signature_Is_Hmac_Sha256_Of_The_Raw_Body()
    {
        // HMAC-SHA256("key", "The quick brown fox jumps over the lazy dog") — RFC 4231-style well-known vector.
        Assert.Equal("sha256=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8",
            WebhookSignature.Sign("key", "The quick brown fox jumps over the lazy dog"));
        Assert.True(WebhookSignature.Verify("key", "The quick brown fox jumps over the lazy dog", "sha256=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8"));
        Assert.False(WebhookSignature.Verify("key", "tampered", "sha256=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8"));
    }

    [Fact]
    public async Task Element_Changes_Are_Debounced_Into_One_Event()
    {
        var events = new ProjectEvents(new WebhookOptions { ElementQuietPeriod = TimeSpan.FromMilliseconds(150), ElementMaxDelay = TimeSpan.FromSeconds(30) });
        var m = SampleModels.Vehicle();
        var ops = new List<ModelOp>();
        m.OpEmitted += ops.Add;
        var block = m.OfKind(ElementKind.Block).First();
        m.Rename(block, "Car");
        m.Set(block, FieldKeys.Documentation, "Four wheels");
        var created = m.Create(block, ElementKind.Property, "speed");
        var pid = Guid.NewGuid();
        foreach (var op in ops) events.OperationApplied(pid, "Vehicle", Guid.NewGuid(), op, m);

        var e = await events.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebhookEvents.ElementChanged, e.Event);
        Assert.Equal(2, (int)e.Data["count"]!);
        var elements = e.Data["elements"]!.AsArray();
        Assert.Contains(elements, x => (string)x!["id"]! == block.Id && (string)x["change"]! == "updated" && (string)x["name"]! == "Car");
        Assert.Contains(elements, x => (string)x!["id"]! == created.Id && (string)x["change"]! == "added");
        Assert.False(events.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Dispatcher_Signs_Retries_Failures_Filters_Events_And_Logs_Every_Attempt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meridian-hooks-{Guid.NewGuid():N}.db");
        try
        {
            var db = new SqliteStore(path);
            await db.MigrateAsync();
            var user = await db.CreateUserAsync("ann", "Ann", "correct horse");
            var pid = await db.CreateProjectAsync("Vehicle", ProjectFile.Serialize(SampleModels.Vehicle()), user.Id);

            var calls = 0;
            var handler = new FakeHandler((_, _) => Interlocked.Increment(ref calls) == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.NoContent));
            var factory = new FakeHttpClientFactory(handler);
            var svc = new IntegrationService(db, new EphemeralDataProtectionProvider(), factory);
            var options = new WebhookOptions { RetryDelays = [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20)] };
            var events = new ProjectEvents(options);
            var dispatcher = new WebhookDispatcher(events, db, svc, factory, options, NullLogger<WebhookDispatcher>.Instance);

            var hook = await svc.SaveAsync(pid, null, IntegrationKinds.Webhook, "hooks.test",
                new WebhookConfig { Url = "https://hooks.test/meridian", Events = [WebhookEvents.VersionCommitted] }.ToJson(), "hook-secret");

            await dispatcher.DispatchAsync(new ProjectEvent(pid, "Vehicle", WebhookEvents.CommentChanged, new JsonObject(), DateTime.UtcNow));   // filtered out
            await dispatcher.DispatchAsync(new ProjectEvent(pid, "Vehicle", WebhookEvents.VersionCommitted, new JsonObject { ["number"] = 3 }, DateTime.UtcNow));
            await dispatcher.DrainAsync();

            Assert.Equal(2, handler.Requests.Count);
            var (req, body) = handler.Requests[1];
            Assert.Equal("https://hooks.test/meridian", req.RequestUri!.ToString());
            Assert.Equal("version.committed", req.Headers.GetValues("X-Meridian-Event").Single());
            Assert.True(WebhookSignature.Verify("hook-secret", body!, req.Headers.GetValues(WebhookSignature.Header).Single()));
            var payload = JsonNode.Parse(body!)!;
            Assert.Equal(3, (int)payload["data"]!["number"]!);
            Assert.Equal(pid.ToString(), (string)payload["project"]!["id"]!);
            Assert.Equal(handler.Requests[0].Body, body);   // retries resend the same delivery

            var log = await db.ListWebhookDeliveriesAsync(pid);
            Assert.Equal(2, log.Count);
            Assert.Equal((2, 204, true), (log[0].Attempt, log[0].StatusCode, log[0].Success));
            Assert.Equal((1, 503, false), (log[1].Attempt, log[1].StatusCode, log[1].Success));
            Assert.All(log, d => Assert.Equal(hook.Id, d.HookId));

            var (status, _) = await dispatcher.PingAsync(hook, "Vehicle");
            Assert.Equal(204, status);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }
}

public class OslcTests
{
    private static readonly Oslc.Urls Urls = new("https://meridian.test");
    private static readonly Guid Project = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Query_Returns_Requirements_As_Rdf_Xml_With_Trace_Links()
    {
        var m = SampleModels.Vehicle();
        var all = Oslc.Requirements(m, Urls, Project);
        Assert.Equal(m.OfKind(ElementKind.Requirement).Count(), all.Count);

        var doc = Oslc.QueryRdf(Urls.Query(Project), Urls.Query(Project), all, all.Count, null, Urls.Provider(Project));
        var reparsed = XDocument.Parse(doc.ToString());
        var reqs = reparsed.Root!.Elements(Oslc.Rm + "Requirement").ToList();
        Assert.Equal(all.Count, reqs.Count);
        var accel = reqs.Single(r => r.Element(Oslc.Dcterms + "identifier")!.Value == "REQ-002");
        Assert.Equal("Acceleration", accel.Element(Oslc.Dcterms + "title")!.Value);
        Assert.Contains("0 to 100", accel.Element(Oslc.Dcterms + "description")!.Value);
        Assert.Equal(2, accel.Elements(Oslc.Rm + "satisfiedBy").Count());
        Assert.Single(accel.Elements(Oslc.Rm + "validatedBy"));
        Assert.StartsWith("https://meridian.test/?project=", accel.Element(Oslc.Rm + "satisfiedBy")!.Attribute(Oslc.Rdf + "resource")!.Value);
        Assert.Equal(all.Count.ToString(), reparsed.Root.Element(Oslc.Core + "ResponseInfo")!.Element(Oslc.Core + "totalCount")!.Value);
    }

    [Fact]
    public void Query_Filters_And_Json_Ld()
    {
        var m = SampleModels.Vehicle();
        var all = Oslc.Requirements(m, Urls, Project);
        Assert.Equal("REQ-004", Assert.Single(Oslc.Filter(all, "dcterms:identifier=\"REQ-004\"", null)).Identifier);
        Assert.Equal(2, Oslc.Filter(all, "dcterms:identifier in [\"REQ-001\",\"REQ-005\"]", null).Count());
        Assert.Equal("REQ-003", Assert.Single(Oslc.Filter(all, null, "600 km")).Identifier);

        var json = Oslc.QueryJsonLd(Urls.Query(Project), Urls.Query(Project), all, all.Count, null, Urls.Provider(Project));
        Assert.Equal(Oslc.Rm.NamespaceName, (string)json["@context"]!["oslc_rm"]!);
        var member = json["rdfs:member"]!.AsArray().Single(x => (string)x!["dcterms:identifier"]! == "REQ-002")!;
        Assert.Equal("oslc_rm:Requirement", (string)member["@type"]!);
        Assert.Equal(2, member["oslc_rm:satisfiedBy"]!.AsArray().Count);
    }

    [Fact]
    public void Discovery_Documents_Point_At_Each_Other()
    {
        var root = Oslc.RootServices(Urls, "Meridian");
        Assert.Equal(Urls.Catalog, root.Root!.Element(Oslc.RmRoot + "rmServiceProviders")!.Attribute(Oslc.Rdf + "resource")!.Value);
        var catalog = Oslc.Catalog(Urls, "Meridian", [(Project, "Vehicle")]);
        Assert.Contains(Urls.Provider(Project), catalog.ToString());
        var provider = Oslc.Provider(Urls, Project, "Vehicle").ToString();
        Assert.Contains(Urls.Query(Project), provider);
        Assert.Contains(Urls.Selector(Project), provider);
        Assert.Contains("http://open-services.net/ns/rm#Requirement", provider);
    }

    [Fact]
    public void Selector_Page_Escapes_Model_Text()
    {
        var reqs = new List<Oslc.Req> { new("https://m/r/1", "REQ-1", "<script>alert(1)</script>", "", [], [], "https://m") };
        var html = IntegrationsSetup.SelectorPage("P & Q", "\"x", reqs);
        Assert.DoesNotContain("<script>alert(1)", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("P &amp; Q", html);
        Assert.Contains("oslc-response:", html);
    }
}
