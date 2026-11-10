using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
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

/// <summary>DOORS Next connector against a fake Jazz server serving recorded-style RDF.</summary>
public class DoorsNextSourceTests
{
    private const string Password = "pa55-w0rd!";

    private static RequirementsToolConfig Config(string auth = JazzAuth.Basic, string? scope = null) => new()
    {
        Type = RequirementsToolKinds.DoorsNext, BaseUrl = Rm, Auth = auth, User = "ann", Project = Provider1, Scope = scope,
        AuthUrl = auth == JazzAuth.Form ? Jazz + "/jts" : null,
    };

    /// <summary>A Jazz server; <paramref name="authorized"/> decides whether a request is signed in.</summary>
    internal static FakeServer Server(Func<HttpRequestMessage, bool>? authorized = null, Func<string>? r1 = null)
    {
        var s = new FakeServer();
        var next = QueryBase + "&oslc.paging=true&oslc.pageSize=100&page=2";
        bool Ok(HttpRequestMessage r) => authorized?.Invoke(r) ?? true;
        HttpResponseMessage Guarded(HttpRequestMessage r, Func<HttpResponseMessage> ok) => Ok(r) ? ok() : FakeServer.Redirect(Jazz + "/jts/authenticated/identity?redirectPath=%2Frm");
        s.On(HttpMethod.Get, u => u.AbsoluteUri.StartsWith(Jazz + "/jts/authenticated/identity"), (r, _) =>
        {
            var page = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><body>Log in</body></html>", Encoding.UTF8, "text/html") };
            if (!Ok(r)) page.Headers.TryAddWithoutValidation(DoorsNextSource.AuthMsgHeader, "authrequired");
            return page;
        });
        s.Get(Rm + "/rootservices", () => FakeServer.Rdf(RootServices));   // public, as on Jazz
        s.On(HttpMethod.Get, u => u.AbsoluteUri == Catalog, (r, _) => Guarded(r, () => FakeServer.Rdf(CatalogRdf)));
        s.On(HttpMethod.Get, u => u.AbsoluteUri == Provider1, (r, _) => Guarded(r, () => FakeServer.Rdf(ServicesRdf)));
        s.On(HttpMethod.Get, u => u.AbsoluteUri.StartsWith(QueryBase) && !u.Query.Contains("page=2"), (r, _) => Guarded(r, () =>
            FakeServer.Rdf(QueryPage(r.RequestUri!.AbsoluteUri, next, 3, (R1, r1?.Invoke() ?? R1Body()), (R2, R2Body())))));
        s.On(HttpMethod.Get, u => u.AbsoluteUri == next, (r, _) => Guarded(r, () => FakeServer.Rdf(QueryPage(next, null, 3, (R3, R3Body())))));
        s.On(HttpMethod.Get, u => u.AbsoluteUri.StartsWith(QueryBase) && Uri.UnescapeDataString(u.Query).Contains("rdf:type=<http://jazz.net/ns/rm#Module>"),
            (r, _) => Guarded(r, () => FakeServer.Rdf(ModuleList)));
        s.On(HttpMethod.Get, u => u.AbsoluteUri == Module, (r, _) => Guarded(r, () => FakeServer.Rdf(ModuleRdf)));
        s.On(HttpMethod.Get, u => u.AbsoluteUri == R1, (r, _) => Guarded(r, () => FakeServer.Rdf(Document(r1?.Invoke() ?? R1Body()), "\"r1-v7\"")));
        s.On(HttpMethod.Get, u => u.AbsoluteUri == R3, (r, _) => Guarded(r, () => FakeServer.Rdf(Document(R3Body()), "\"r3-v2\"")));
        return s;
    }

    [Fact]
    public async Task Discovers_Projects_From_Root_Services_And_The_Catalog_With_Basic_Auth()
    {
        var server = Server();
        var dn = new DoorsNextSource(Config(), Password, new HttpClient(server));

        var projects = await dn.ProjectsAsync();

        Assert.Equal([new RemoteContainer(Provider2, "Braking Systems"), new RemoteContainer(Provider1, "Drive Assist (Requirements)")], projects);
        Assert.Equal(Rm + "/rootservices", server.Requests[0].Request.RequestUri!.AbsoluteUri);
        Assert.All(server.Requests, r =>
        {
            Assert.Equal("Basic", r.Request.Headers.Authorization!.Scheme);
            Assert.Equal("ann:" + Password, Encoding.UTF8.GetString(Convert.FromBase64String(r.Request.Headers.Authorization.Parameter!)));
            Assert.Equal("2.0", r.Request.Headers.GetValues("OSLC-Core-Version").Single());
            Assert.Contains("application/rdf+xml", r.Request.Headers.Accept.ToString());
        });
        Assert.Equal("Connected to DOORS Next — project “Drive Assist (Requirements)”", await dn.TestAsync());
    }

    [Fact]
    public async Task App_Password_Is_Sent_As_Basic_And_Configuration_Context_Is_Forwarded()
    {
        var server = Server();
        var cfg = Config(JazzAuth.AppPassword);
        cfg.ConfigurationContext = Rm + "/cm/stream/_stream1";
        await new DoorsNextSource(cfg, "app-pw-123", new HttpClient(server)).ProjectsAsync();
        var req = server.Requests.Last().Request;
        Assert.Equal("ann:app-pw-123", Encoding.UTF8.GetString(Convert.FromBase64String(req.Headers.Authorization!.Parameter!)));
        Assert.Equal(Rm + "/cm/stream/_stream1", req.Headers.GetValues("Configuration-Context").Single());
    }

    [Fact]
    public async Task Form_Login_Posts_Credentials_Once_And_Keeps_The_Session_Cookie()
    {
        var server = Server(authorized: r => r.Headers.TryGetValues("Cookie", out var c) && c.Any(x => x.Contains("LtpaToken2=tok123")));
        server.On(HttpMethod.Post, u => u.AbsoluteUri == Jazz + "/jts/j_security_check", (_, body) =>
            body == "j_username=ann&j_password=" + Uri.EscapeDataString(Password)
                ? FakeServer.Redirect(Jazz + "/jts/authenticated/identity", "JSESSIONID=js1; Path=/jts; Secure; HttpOnly", "LtpaToken2=tok123; Path=/; Secure; HttpOnly")
                : FakeServer.Redirect(Jazz + "/jts/authenticated/identity?authfailed"));
        var dn = new DoorsNextSource(Config(JazzAuth.Form), Password, new HttpClient(server));

        var projects = await dn.ProjectsAsync();

        Assert.Equal(2, projects.Count);
        var posts = server.Requests.Where(r => r.Request.Method == HttpMethod.Post).ToList();
        Assert.Single(posts);
        Assert.All(server.Requests, r => Assert.Null(r.Request.Headers.Authorization));
        Assert.All(server.Requests.Where(r => r.Request.Method != HttpMethod.Post), r => Assert.DoesNotContain(Password, r.Request.RequestUri!.ToString()));
        var catalogCalls = server.Requests.Where(r => r.Request.RequestUri!.AbsoluteUri == Catalog).ToList();
        Assert.Equal(2, catalogCalls.Count);   // once redirected to the login page, then again with the cookie
        Assert.Contains("LtpaToken2=tok123", catalogCalls[1].Request.Headers.GetValues("Cookie").Single());
        Assert.DoesNotContain("JSESSIONID", catalogCalls[1].Request.Headers.GetValues("Cookie").Single());   // path-scoped to /jts

        // Still signed in: no second login.
        await dn.ProjectsAsync();
        Assert.Single(server.Requests, r => r.Request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Form_Login_Failure_Says_The_Credentials_Were_Rejected()
    {
        var server = Server(authorized: _ => false);
        server.On(HttpMethod.Post, u => u.AbsoluteUri == Jazz + "/jts/j_security_check", (_, _) => FakeServer.Redirect(Jazz + "/jts/authfailed"));
        server.On(HttpMethod.Get, u => u.AbsoluteUri == Jazz + "/jts/authfailed", (_, _) =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>failed</html>") };
            r.Headers.TryAddWithoutValidation(DoorsNextSource.AuthMsgHeader, "authfailed");
            return r;
        });
        var ex = await Assert.ThrowsAsync<RequirementsToolException>(() => new DoorsNextSource(Config(JazzAuth.Form), Password, new HttpClient(server)).ProjectsAsync());
        Assert.Contains("rejected", ex.Message);
        Assert.DoesNotContain(Password, ex.Message);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
    }

    [Fact]
    public async Task Each_Connection_Keeps_Its_Own_Cookie_Jar()
    {
        var server = Server(authorized: r => r.Headers.TryGetValues("Cookie", out var c) && c.Any(x => x.Contains("LtpaToken2=")));
        var logins = 0;
        server.On(HttpMethod.Post, u => u.AbsoluteUri == Jazz + "/jts/j_security_check", (_, _) =>
            FakeServer.Redirect(Jazz + "/jts/authenticated/identity", $"LtpaToken2=user{Interlocked.Increment(ref logins)}; Path=/; Secure"));
        var shared = new HttpClient(server);   // one pooled handler, as in production
        var a = new DoorsNextSource(Config(JazzAuth.Form), Password, shared);
        var b = new DoorsNextSource(Config(JazzAuth.Form), "other", shared);
        await a.ProjectsAsync();
        var before = server.Requests.Count;
        await b.ProjectsAsync();
        Assert.Equal(2, logins);   // b had to sign in itself
        Assert.DoesNotContain(server.Requests.Skip(before), r => r.Request.Headers.TryGetValues("Cookie", out var c) && c.Any(x => x.Contains("user1")));
    }

    [Fact]
    public async Task Queries_Requirements_Across_Pages_And_Maps_Rich_Text()
    {
        var server = Server();
        var reqs = await new DoorsNextSource(Config(), Password, new HttpClient(server)).RequirementsAsync();

        Assert.Equal(["1201", "1202", "1203"], reqs.Select(r => r.Identifier));
        var r1 = reqs[0];
        Assert.Equal((R1, R1, "Lane keeping above 60 km/h"), (r1.ExternalId, r1.Url, r1.Title));
        Assert.Equal("The system shall keep the vehicle centred in its lane at speeds above 60 km/h.", r1.Text);   // XHTML + nbsp → plain text
        Assert.Equal("2026-10-02T09:14:31.112Z", r1.Revision);
        Assert.Equal("From 100 km/h the vehicle shall stop within 40 m on dry asphalt.", reqs[1].Text);
        Assert.Equal("The driver shall be alerted <1 s after a lane departure.", reqs[2].Text);
        var first = server.Requests.First(r => r.Request.RequestUri!.AbsoluteUri.StartsWith(QueryBase)).Request.RequestUri!;
        var query = Uri.UnescapeDataString(first.Query);
        Assert.Contains("oslc.paging=true", query);
        Assert.Contains("oslc.select=dcterms:identifier,dcterms:title", query);
        Assert.Contains("jazz_rm=<http://jazz.net/ns/rm#>", query);
        Assert.Contains(server.Requests, r => r.Request.RequestUri!.Query.Contains("page=2"));
    }

    [Fact]
    public async Task Lists_Modules_And_Reads_A_Module_Through_Its_Uses()
    {
        var server = Server();
        var dn = new DoorsNextSource(Config(scope: Module), Password, new HttpClient(server));
        Assert.Equal([new RemoteContainer(Module, "System Requirements"), new RemoteContainer(Rm + "/resources/_MOD2stakeholder", "Stakeholder Needs")],
            (await dn.ScopesAsync(Provider1)).OrderByDescending(m => m.Title == "System Requirements"));
        var reqs = await dn.RequirementsAsync();
        Assert.Equal(["1201", "1203"], reqs.Select(r => r.Identifier));
        Assert.DoesNotContain(server.Requests, r => r.Request.RequestUri!.Query.Contains("page=2"));
    }

    [Fact]
    public async Task Publishing_A_Link_Puts_The_Requirement_Back_With_If_Match()
    {
        var server = Server();
        var target = "https://meridian.test/oslc/projects/p/resources/_blk";
        string? lastPut = null;
        server.On(HttpMethod.Put, u => u.AbsoluteUri == R1, (r, body) =>
        {
            lastPut = body;
            Assert.Equal("\"r1-v7\"", r.Headers.IfMatch.Single().ToString());
            Assert.Equal("application/rdf+xml", r.Content!.Headers.ContentType!.MediaType);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var dn = new DoorsNextSource(Config(), Password, new HttpClient(server));

        Assert.Equal(PublishOutcome.Created, await dn.PublishLinkAsync(R1, new OutboundLink("satisfy", target, "Lane keeping ECU", null)));

        var g = RdfXmlParser.Parse(lastPut!);
        Assert.Contains(target, g.Iris(new RdfIri(R1), "http://open-services.net/ns/rm#satisfiedBy"));
        Assert.Equal("1201", g.Value(new RdfIri(R1), "http://purl.org/dc/terms/identifier"));   // the rest of the resource is kept

        // Already there: nothing is written.
        var withLink = new DoorsNextSource(Config(), Password, new HttpClient(Server(r1: () => R1Body().Replace("</oslc_rm:Requirement>", $"<oslc_rm:validatedBy rdf:resource=\"{target}\"/></oslc_rm:Requirement>"))));
        Assert.Equal(PublishOutcome.AlreadyPresent, await withLink.PublishLinkAsync(R1, new OutboundLink("verify", target, "Test", null)));
    }

    [Fact]
    public async Task Publishing_Retries_Once_When_The_Requirement_Changed_Meanwhile()
    {
        var server = Server();
        var puts = 0;
        server.On(HttpMethod.Put, u => u.AbsoluteUri == R1, (_, _) => new HttpResponseMessage(Interlocked.Increment(ref puts) == 1 ? HttpStatusCode.PreconditionFailed : HttpStatusCode.OK));
        await new DoorsNextSource(Config(), Password, new HttpClient(server)).PublishLinkAsync(R1, new OutboundLink("trace", "https://m/x", "x", null));
        Assert.Equal(2, puts);
        Assert.Equal(2, server.Requests.Count(r => r.Request.Method == HttpMethod.Get && r.Request.RequestUri!.AbsoluteUri == R1));
    }

    [Fact]
    public async Task Errors_Are_Explained_Without_Secrets()
    {
        var forbidden = Server();
        forbidden.On(HttpMethod.Get, u => u.AbsoluteUri == Catalog, (_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<html>CRJAZ1234E</html>") });
        var ex = await Assert.ThrowsAsync<RequirementsToolException>(() => new DoorsNextSource(Config(), Password, new HttpClient(forbidden)).ProjectsAsync());
        Assert.Contains("403", ex.Message);
        Assert.Contains("lacks permission", ex.Message);
        Assert.DoesNotContain(Password, ex.Message);

        var html = new FakeServer().Get(Rm + "/rootservices", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><body>Welcome") });
        Assert.Contains("isn't RDF/XML", (await Assert.ThrowsAsync<RequirementsToolException>(() => new DoorsNextSource(Config(), Password, new HttpClient(html)).ProjectsAsync())).Message);

        var wrongApp = new FakeServer().Get(Rm + "/rootservices", () => FakeServer.Rdf("<rdf:Description xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" rdf:about=\"x\"/>"));
        Assert.Contains("rmServiceProviders", (await Assert.ThrowsAsync<RequirementsToolException>(() => new DoorsNextSource(Config(), Password, new HttpClient(wrongApp)).ProjectsAsync())).Message);
    }

    [Fact]
    public async Task A_Server_That_Repeats_Its_Next_Page_Does_Not_Loop()
    {
        var server = Server();
        server.On(HttpMethod.Get, u => u.AbsoluteUri.StartsWith(QueryBase), (r, _) => FakeServer.Rdf(QueryPage(r.RequestUri!.AbsoluteUri, r.RequestUri.AbsoluteUri, 1, (R1, R1Body()))));
        var reqs = await new DoorsNextSource(Config(), Password, new HttpClient(server)).RequirementsAsync();
        Assert.Single(reqs);
    }
}

/// <summary>Polarion connector against a fake REST API serving recorded-style JSON:API documents.</summary>
public class PolarionSourceTests
{
    private const string Token = "pat-eyJhbGciOi-secret";

    private static RequirementsToolConfig Config(string? scope = null, string? query = null) => new()
    {
        Type = RequirementsToolKinds.Polarion, BaseUrl = Polarion + "/polarion", Project = "drivepilot", Scope = scope, Query = query,
    };

    internal static FakeServer Server(Func<string>? dp101Title = null, string updated101 = "2026-10-05T08:00:00.000Z")
    {
        var s = new FakeServer();
        s.On(HttpMethod.Get, u => u.AbsolutePath == "/polarion/rest/v1/projects", (_, _) => FakeServer.Json(PolarionProjects));
        s.On(HttpMethod.Get, u => u.AbsolutePath == "/polarion/rest/v1/projects/drivepilot", (_, _) => FakeServer.Json("""{"data":{"type":"projects","id":"drivepilot","attributes":{"name":"DrivePilot"}}}"""));
        s.On(HttpMethod.Get, u => u.AbsolutePath == "/polarion/rest/v1/projects/drivepilot/workitems" && u.Query.Contains("page%5Bnumber%5D=1"), (_, _) => FakeServer.Json(WorkItemPage(1, true, 3,
            WorkItem("DP-101", dp101Title?.Invoke() ?? "Emergency braking", "<p>The vehicle shall brake automatically when a collision is <b>imminent</b>.</p>", updated101),
            WorkItem("DP-102", "Brake light", "<p>Brake lights shall turn on within 100&nbsp;ms.</p>", "2026-10-05T09:30:00.000Z"))));
        s.On(HttpMethod.Get, u => u.AbsolutePath == "/polarion/rest/v1/projects/drivepilot/workitems" && u.Query.Contains("page%5Bnumber%5D=2"), (_, _) => FakeServer.Json(WorkItemPage(2, false, 3,
            WorkItem("DP-205", "Safe state", "<p>Enter a safe state on sensor loss.</p>", "2026-10-06T10:00:00.000Z", "drivepilot/Specification/Safety Goals"))));
        return s;
    }

    private static PolarionSource Source(FakeServer s, RequirementsToolConfig? cfg = null) => new(cfg ?? Config(), Token, new HttpClient(s)) { PageSize = 2 };

    [Fact]
    public async Task Lists_Projects_With_A_Bearer_Token()
    {
        var server = Server();
        var projects = await Source(server).ProjectsAsync();
        Assert.Equal([new RemoteContainer("chassis", "Chassis Platform"), new RemoteContainer("drivepilot", "DrivePilot")], projects);
        var req = server.Requests.Single().Request;
        Assert.Equal(new AuthenticationHeaderValue("Bearer", Token), req.Headers.Authorization);
        Assert.Equal("https://polarion.example.test/polarion/rest/v1/projects?fields%5Bprojects%5D=name&page%5Bsize%5D=2&page%5Bnumber%5D=1", req.RequestUri!.AbsoluteUri);
        Assert.Equal("Connected to Polarion project “DrivePilot”", await Source(server).TestAsync());
    }

    [Fact]
    public async Task Pages_Through_Requirement_Work_Items_And_Maps_Them()
    {
        var server = Server();
        var reqs = await Source(server, Config(query: "status:approved")).RequirementsAsync();

        Assert.Equal(["DP-101", "DP-102", "DP-205"], reqs.Select(r => r.Identifier));
        var r = reqs[0];
        Assert.Equal("drivepilot/DP-101", r.ExternalId);
        Assert.Equal("https://polarion.example.test/polarion/#/project/drivepilot/workitem?id=DP-101", r.Url);
        Assert.Equal("The vehicle shall brake automatically when a collision is imminent.", r.Text);
        Assert.Equal("2026-10-05T08:00:00.000Z", r.Revision);
        Assert.Equal("Brake lights shall turn on within 100 ms.", reqs[1].Text);
        var first = server.Requests[0].Request.RequestUri!;
        Assert.Contains("query=type%3Arequirement%20AND%20%28status%3Aapproved%29", first.AbsoluteUri);
        Assert.Contains("fields%5Bworkitems%5D=title,description,type,status,updated,module", first.AbsoluteUri);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Document_Scope_Filters_By_The_Module_Relationship_And_Documents_Come_From_Included()
    {
        var server = Server();
        var scoped = await Source(server, Config(scope: "drivepilot/Specification/Safety Goals")).RequirementsAsync();
        Assert.Equal(["DP-205"], scoped.Select(r => r.Identifier));
        var docs = await Source(server).ScopesAsync("drivepilot");
        Assert.Equal([new RemoteContainer("drivepilot/Specification/Safety Goals", "Safety Goals (Specification)"), new RemoteContainer("drivepilot/Specification/System Requirements", "System Requirements (Specification)")], docs);
        Assert.Contains(server.Requests, r => r.Request.RequestUri!.AbsoluteUri.Contains("include=module"));
    }

    [Fact]
    public async Task Publishes_Linked_Work_Items_And_Linked_Oslc_Resources()
    {
        var server = Server();
        var bodies = new List<(string Path, JsonNode Body)>();
        server.On(HttpMethod.Post, u => u.AbsolutePath.StartsWith("/polarion/rest/v1/projects/drivepilot/workitems/DP-101/"), (r, body) =>
        {
            bodies.Add((r.RequestUri!.AbsolutePath, JsonNode.Parse(body!)!));
            return bodies.Count == 3 ? FakeServer.Json("""{"errors":[{"status":"409","detail":"Link exists"}]}""", HttpStatusCode.Conflict) : FakeServer.Json("""{"data":[{"type":"x","id":"y"}]}""", HttpStatusCode.Created);
        });
        var p = Source(server);

        Assert.Equal(PublishOutcome.Created, await p.PublishLinkAsync("drivepilot/DP-101", new OutboundLink("trace", "https://polarion.example.test/polarion/#/project/drivepilot/workitem?id=DP-102", "DP-102 Brake light", "drivepilot/DP-102")));
        Assert.Equal(PublishOutcome.Created, await p.PublishLinkAsync("drivepilot/DP-101", new OutboundLink("satisfy", "https://meridian.test/oslc/projects/p/resources/_abs", "ABS controller", null)));
        Assert.Equal(PublishOutcome.AlreadyPresent, await p.PublishLinkAsync("drivepilot/DP-101", new OutboundLink("verify", "https://meridian.test/oslc/projects/p/resources/_tc", "Brake test", null)));

        Assert.EndsWith("/linkedworkitems", bodies[0].Path);
        var wi = bodies[0].Body["data"]![0]!;
        Assert.Equal(("linkedworkitems", "relates_to", "drivepilot/DP-102"), ((string)wi["type"]!, (string)wi["attributes"]!["role"]!, (string)wi["relationships"]!["workItem"]!["data"]!["id"]!));
        Assert.EndsWith("/linkedoslcresources", bodies[1].Path);
        var oslc = bodies[1].Body["data"]![0]!["attributes"]!;
        Assert.Equal(("http://open-services.net/ns/rm#satisfiedBy", "https://meridian.test/oslc/projects/p/resources/_abs", "ABS controller"),
            ((string)oslc["role"]!, (string)oslc["uri"]!, (string)oslc["label"]!));
        Assert.All(server.Requests.Where(r => r.Request.Method == HttpMethod.Post), r => Assert.Equal("application/json", r.Request.Content!.Headers.ContentType!.MediaType));
    }

    [Fact]
    public async Task Errors_Are_Explained_Without_The_Token()
    {
        var unauthorized = new FakeServer().On(HttpMethod.Get, _ => true, (_, _) => FakeServer.Json(PolarionUnauthorized, HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<RequirementsToolException>(() => Source(unauthorized).ProjectsAsync());
        Assert.Contains("credentials were rejected", ex.Message);
        Assert.DoesNotContain(Token, ex.Message);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);

        var login = new FakeServer().On(HttpMethod.Get, _ => true, (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Polarion login</html>") });
        Assert.Contains("isn't JSON", (await Assert.ThrowsAsync<RequirementsToolException>(() => Source(login).ProjectsAsync())).Message);

        Assert.Contains("not a Polarion work item id", (await Assert.ThrowsAsync<RequirementsToolException>(() => Source(Server()).PublishLinkAsync("nope", new OutboundLink("trace", "x", "x", null)))).Message);
    }

    [Fact]
    public void Base_Url_Forms_Are_Normalized()
    {
        foreach (var b in new[] { Polarion, Polarion + "/", Polarion + "/polarion", Polarion + "/polarion/rest/v1" })
            Assert.Equal(PolarionApi, new PolarionSource(new RequirementsToolConfig { Type = RequirementsToolKinds.Polarion, BaseUrl = b, Project = "x" }, Token, new HttpClient()).Api);
    }
}
