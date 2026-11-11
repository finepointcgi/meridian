using System.Net;
using System.Text.Json.Nodes;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Integrations;
using Microsoft.AspNetCore.DataProtection;

namespace Meridian.Web.Tests;

/// <summary>GitLab Issues (REST v4) over a fake handler with realistic GitLab JSON: no network access.</summary>
public class GitLabTrackerTests
{
    private const string Token = "glpat-s3cr3tTokenValue123";

    private static IssueTrackerConfig Config(string baseUrl = "https://gitlab.com", string project = "acme/vehicles/car") =>
        new() { Type = TrackerTypes.GitLab, BaseUrl = baseUrl, Project = project };

    /// <summary>A trimmed issue as GET /projects/:id/issues/:iid returns it.</summary>
    internal static string IssueJson(int iid, string title, string state = "opened", string[]? assignees = null, string[]? labels = null, string project = "acme/vehicles/car", string host = "https://gitlab.com") =>
        new JsonObject
        {
            ["id"] = 90000 + iid, ["iid"] = iid, ["project_id"] = 4711, ["title"] = title, ["description"] = "Body", ["state"] = state,
            ["created_at"] = "2026-11-01T10:00:00.000Z", ["updated_at"] = "2026-11-09T08:30:12.345Z",
            ["closed_at"] = state == "closed" ? "2026-11-09T08:30:12.345Z" : null,
            ["labels"] = new JsonArray((labels ?? []).Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
            ["milestone"] = null,
            ["assignees"] = new JsonArray((assignees ?? []).Select((a, i) => (JsonNode)new JsonObject
            {
                ["id"] = 100 + i, ["username"] = a, ["name"] = a.ToUpperInvariant(), ["state"] = "active",
                ["avatar_url"] = "https://secure.gravatar.com/avatar/x", ["web_url"] = $"{host}/{a}",
            }).ToArray()),
            ["assignee"] = assignees is { Length: > 0 } ? new JsonObject { ["id"] = 100, ["username"] = assignees[0], ["name"] = assignees[0].ToUpperInvariant() } : null,
            ["author"] = new JsonObject { ["id"] = 1, ["username"] = "root", ["name"] = "Administrator" },
            ["type"] = "ISSUE", ["issue_type"] = "issue", ["confidential"] = false,
            ["web_url"] = $"{host}/{project}/-/issues/{iid}",
            ["references"] = new JsonObject { ["short"] = $"#{iid}", ["relative"] = $"#{iid}", ["full"] = $"{project}#{iid}" },
        }.ToJsonString();

    private static HttpResponseMessage Page(string json, string? next, int page, int totalPages = 3)
    {
        var r = FakeHandler.Json(json);
        r.Headers.TryAddWithoutValidation("x-page", page.ToString());
        r.Headers.TryAddWithoutValidation("x-per-page", "20");
        r.Headers.TryAddWithoutValidation("x-total-pages", totalPages.ToString());
        r.Headers.TryAddWithoutValidation("x-next-page", next ?? "");
        r.Headers.TryAddWithoutValidation("x-prev-page", page > 1 ? (page - 1).ToString() : "");
        return r;
    }

    [Fact]
    public async Task Creates_An_Issue_With_Private_Token_Labels_And_A_Back_Link()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(IssueJson(12, "REQ-002 Acceleration", labels: ["meridian", "requirement"]), HttpStatusCode.Created));
        var cfg = Config();
        cfg.Labels = " meridian , requirement,";
        cfg.IssueType = "Task";
        Assert.Null(cfg.Validate());
        var gl = IssueTrackerFactory.Create(cfg, Token, new HttpClient(handler));

        var issue = await gl.CreateAsync("REQ-002 Acceleration", "Zero to 100 in 8 s.", "https://meridian.test/?project=p&element=e");

        Assert.Equal(new IssueInfo("#12", "https://gitlab.com/acme/vehicles/car/-/issues/12", "REQ-002 Acceleration", "opened", null, false), issue with { Labels = null });
        Assert.Equal(["meridian", "requirement"], issue.Labels!);
        var (post, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal("https://gitlab.com/api/v4/projects/acme%2Fvehicles%2Fcar/issues", post.RequestUri!.AbsoluteUri);
        Assert.Equal(Token, Assert.Single(post.Headers.GetValues("PRIVATE-TOKEN")));
        Assert.Null(post.Headers.Authorization);
        var json = JsonNode.Parse(body!)!;
        Assert.Equal("meridian,requirement", (string)json["labels"]!);
        Assert.Equal("task", (string)json["issue_type"]!);
        Assert.EndsWith("[Open in Meridian](https://meridian.test/?project=p&element=e)", (string)json["description"]!);
    }

    [Fact]
    public async Task Self_Managed_With_Bearer_And_Api_Base_Fetches_Status_Assignees_And_Labels()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(IssueJson(7, "Brake test", "closed", ["ann", "ben"], ["bug", "safety::high"], "eng/car", "https://gitlab.acme.test")));
        var cfg = Config("https://gitlab.acme.test/api/v4/", "eng/car");
        cfg.BearerAuth = true;
        var gl = IssueTrackerFactory.Create(cfg, Token, new HttpClient(handler));

        var issue = await gl.GetAsync("#7");

        Assert.Equal(("#7", "closed", "ann, ben", true), (issue.Key, issue.Status, issue.Assignee, issue.Closed));
        Assert.Equal(["bug", "safety::high"], issue.Labels!);
        Assert.Equal("https://gitlab.acme.test/eng/car/-/issues/7", issue.Url);
        var req = Assert.Single(handler.Requests).Request;
        Assert.Equal("https://gitlab.acme.test/api/v4/projects/eng%2Fcar/issues/7", req.RequestUri!.AbsoluteUri);
        Assert.Equal(("Bearer", Token), (req.Headers.Authorization!.Scheme, req.Headers.Authorization.Parameter));
        Assert.False(req.Headers.Contains("PRIVATE-TOKEN"));
    }

    [Fact]
    public async Task Search_Follows_X_Next_Page_Until_The_Last_Page()
    {
        var handler = new FakeHandler((req, _) =>
        {
            var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(req.RequestUri!.Query);
            var page = q.TryGetValue("page", out var p) ? int.Parse(p!) : 1;
            var items = Enumerable.Range(1, page == 2 ? 3 : 20).Select(i => JsonNode.Parse(IssueJson(page * 100 + i, $"Range {page}-{i}"))).ToArray();
            return Page(new JsonArray(items).ToJsonString(), page == 1 ? "2" : null, page, 2);
        });
        var gl = IssueTrackerFactory.Create(Config(), Token, new HttpClient(handler));

        var found = await gl.SearchAsync("range");

        Assert.Equal(23, found.Count);
        Assert.Equal(2, handler.Requests.Count);
        var first = handler.Requests[0].Request.RequestUri!;
        Assert.StartsWith("https://gitlab.com/api/v4/projects/acme%2Fvehicles%2Fcar/issues?", first.AbsoluteUri);
        var q1 = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(first.Query);
        Assert.Equal(("range", "title,description", "all", "20"), (q1["search"].ToString(), q1["in"].ToString(), q1["scope"].ToString(), q1["per_page"].ToString()));
        var q2 = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(handler.Requests[1].Request.RequestUri!.Query);
        Assert.Equal(("2", "range"), (q2["page"].ToString(), q2["search"].ToString()));
        Assert.All(handler.Requests, r => Assert.Equal(Token, r.Request.Headers.GetValues("PRIVATE-TOKEN").Single()));
    }

    [Fact]
    public async Task Search_Stops_At_The_Result_Cap()
    {
        var handler = new FakeHandler((req, _) =>
        {
            var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(req.RequestUri!.Query);
            var page = q.TryGetValue("page", out var p) ? int.Parse(p!) : 1;
            var items = Enumerable.Range(1, 20).Select(i => JsonNode.Parse(IssueJson(page * 100 + i, "x"))).ToArray();
            return Page(new JsonArray(items).ToJsonString(), (page + 1).ToString(), page, 99);
        });
        var gl = IssueTrackerFactory.Create(Config(), Token, new HttpClient(handler));
        Assert.Equal(GitLabTracker.MaxSearchResults, (await gl.SearchAsync("")).Count);
        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain("search=", handler.Requests[0].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task Link_Header_Paging_Is_Followed_Only_On_The_GitLab_Host()
    {
        var handler = new FakeHandler((req, _) =>
        {
            var url = req.RequestUri!;
            if (url.Host == "evil.example") return Page("[]", null, 9);
            var r = FakeHandler.Json(new JsonArray(JsonNode.Parse(IssueJson(url.Query.Contains("cursor=b") ? 2 : 1, "keyset"))).ToJsonString());
            if (!url.Query.Contains("cursor="))
                r.Headers.TryAddWithoutValidation("Link", "<https://gitlab.com/api/v4/projects/acme%2Fvehicles%2Fcar/issues?cursor=b&per_page=20>; rel=\"next\"");
            else if (url.Query.Contains("cursor=b"))
                r.Headers.TryAddWithoutValidation("Link", "<https://evil.example/steal?cursor=c>; rel=\"next\", <https://gitlab.com/x>; rel=\"first\"");
            return r;
        });
        var gl = IssueTrackerFactory.Create(Config(), Token, new HttpClient(handler));

        var found = await gl.SearchAsync("keyset");

        Assert.Equal(["#1", "#2"], found.Select(f => f.Key));
        Assert.DoesNotContain(handler.Requests, r => r.Request.RequestUri!.Host == "evil.example");
    }

    [Fact]
    public async Task Search_By_Key_Or_Url_Fetches_That_Issue_And_A_Missing_One_Is_Empty()
    {
        var handler = new FakeHandler((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/issues/404")
            ? FakeHandler.Json("""{"message":"404 Not found"}""", HttpStatusCode.NotFound)
            : FakeHandler.Json(IssueJson(9, "Range")));
        var gl = IssueTrackerFactory.Create(Config(), Token, new HttpClient(handler));

        Assert.Equal("#9", Assert.Single(await gl.SearchAsync("https://gitlab.com/acme/vehicles/car/-/issues/9")).Key);
        Assert.Empty(await gl.SearchAsync("#404"));
        Assert.Equal("https://gitlab.com/api/v4/projects/acme%2Fvehicles%2Fcar/issues/9", handler.Requests[0].Request.RequestUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("#9", "9")]
    [InlineData("9", "9")]
    [InlineData("https://gitlab.com/acme/vehicles/car/-/issues/9", "9")]
    [InlineData("https://gitlab.com/ACME/vehicles/car/-/issues/9#note_1", "9")]
    [InlineData("https://gitlab.com/acme/vehicles/car/-/work_items/14", "14")]
    [InlineData("https://gitlab.com/acme/vehicles/car/issues/3", "3")]
    [InlineData("https://gitlab.com/api/v4/projects/acme%2Fvehicles%2Fcar/issues/5", "5")]
    [InlineData("https://gitlab.com/acme/vehicles/other/-/issues/9", null)]
    [InlineData("https://gitlab.com/acme/vehicles/car/-/merge_requests/9", null)]
    [InlineData("https://gitlab.example.org/acme/vehicles/car/-/issues/9", null)]
    [InlineData("words", null)]
    public void Parses_Keys_And_Issue_Urls(string text, string? key) =>
        Assert.Equal(key, IssueTrackerFactory.Create(Config(), Token, new HttpClient(new FakeHandler((_, _) => FakeHandler.Json("{}")))).ParseKey(text));

    [Fact]
    public void A_Numeric_Project_Id_Accepts_Any_Issue_Url_On_The_Same_Host()
    {
        var gl = IssueTrackerFactory.Create(Config(project: "4711"), Token, new HttpClient(new FakeHandler((_, _) => FakeHandler.Json("{}"))));
        Assert.Equal("4", gl.ParseKey("https://gitlab.com/any/project/-/issues/4"));
        Assert.Equal("5", gl.ParseKey("https://gitlab.com/api/v4/projects/4711/issues/5"));
        Assert.Null(gl.ParseKey("https://gitlab.com/api/v4/projects/4712/issues/5"));
        Assert.Null(gl.ParseKey("https://elsewhere.test/any/project/-/issues/4"));
    }

    [Fact]
    public async Task Test_Connection_Reports_The_Project_And_Disabled_Issues()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""
            {"id":4711,"name":"car","path":"car","path_with_namespace":"acme/vehicles/car","default_branch":"main","visibility":"private",
             "web_url":"https://gitlab.com/acme/vehicles/car","issues_enabled":false,"issues_access_level":"disabled","open_issues_count":0}
            """));
        var gl = IssueTrackerFactory.Create(Config(project: "4711"), Token, new HttpClient(handler));
        Assert.Equal("Connected to GitLab project acme/vehicles/car (issues are disabled!)", await gl.TestAsync());
        Assert.Equal("https://gitlab.com/api/v4/projects/4711", handler.Requests[0].Request.RequestUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"message":"401 Unauthorized"}""", "401")]
    [InlineData(HttpStatusCode.Forbidden, """{"message":"403 Forbidden"}""", "permission")]
    [InlineData(HttpStatusCode.NotFound, """{"message":"404 Project Not Found"}""", "not found")]
    [InlineData(HttpStatusCode.BadRequest, """{"message":{"title":["is too long (maximum is 255 characters)"]}}""", "too long")]
    [InlineData(HttpStatusCode.TooManyRequests, "Retry later", "Retry later")]
    public async Task Errors_Explain_The_Failure_Without_Leaking_The_Token(HttpStatusCode status, string body, string expected)
    {
        var handler = new FakeHandler((req, _) => new HttpResponseMessage(status) { Content = new StringContent(body) });
        var gl = IssueTrackerFactory.Create(Config(), Token, new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<IssueTrackerException>(() => gl.CreateAsync("t", "d", "https://m.test/"));
        Assert.Equal(status, ex.Status);
        Assert.Contains(expected, ex.Message);
        Assert.DoesNotContain(Token, ex.Message);
    }

    [Fact]
    public async Task Html_Instead_Of_Json_Points_At_The_Base_Url()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>sign in</html>") });
        var gl = IssueTrackerFactory.Create(Config(), Token, new HttpClient(handler));
        Assert.Contains("base URL", (await Assert.ThrowsAsync<IssueTrackerException>(() => gl.SearchAsync("x"))).Message);
    }

    [Fact]
    public void Config_Validation_Checks_Project_Shape_And_Issue_Type()
    {
        Assert.Null(Config().Validate());
        Assert.Null(Config(project: "4711").Validate());
        Assert.Null(Config("https://gitlab.acme.test", "group/sub.group/my-project_1").Validate());
        Assert.NotNull(Config(project: "justone").Validate());
        Assert.NotNull(Config(project: "a//b c").Validate());
        Assert.Null(Config("http://gitlab.internal").Validate());
        Assert.NotNull(Config("ftp://gitlab.com").Validate());
        var bad = Config();
        bad.IssueType = "Epic";
        Assert.NotNull(bad.Validate());
        Assert.Contains(TrackerTypes.GitLab, TrackerTypes.All);
        Assert.Equal("GitLab Issues", TrackerTypes.Display(TrackerTypes.GitLab));
    }
}

/// <summary>GitLab through <see cref="IntegrationService"/>: encrypted token, bulk create from requirements and status sync.</summary>
public sealed class GitLabIntegrationServiceTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-web-gl-{Guid.NewGuid():N}.db");
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

    [Fact]
    public async Task Token_Is_Encrypted_And_Push_And_Sync_Work_Against_GitLab()
    {
        var created = 0;
        var handler = new FakeHandler((req, body) =>
        {
            if (req.Method == HttpMethod.Post)
            {
                var n = Interlocked.Increment(ref created);
                return FakeHandler.Json(GitLabTrackerTests.IssueJson(100 + n, (string)JsonNode.Parse(body!)!["title"]!, labels: ["meridian"]), HttpStatusCode.Created);
            }
            var iid = int.Parse(req.RequestUri!.Segments[^1]);
            return FakeHandler.Json(GitLabTrackerTests.IssueJson(iid, "Fresh title", "closed", ["ben"], ["meridian", "done"]));
        });
        var svc = new IntegrationService(_db, new EphemeralDataProtectionProvider(), new FakeHttpClientFactory(handler));
        var cfg = new IssueTrackerConfig { Type = TrackerTypes.GitLab, BaseUrl = "https://gitlab.com", Project = "acme/car", Labels = "meridian" };
        var conn = await svc.SaveAsync(_project, null, IntegrationKinds.IssueTracker, "GitLab", cfg.ToJson(), "glpat-plaintext-secret-123");
        var row = (await _db.ListIntegrationsAsync(_project)).Single();
        Assert.DoesNotContain("glpat-plaintext-secret-123", row.ProtectedSecret);
        Assert.DoesNotContain("glpat-plaintext-secret-123", row.ConfigJson);

        var m = SampleModels.Vehicle();
        var reqPkg = m.OfKind(ElementKind.Package).First(p => p.Name == "Requirements");
        var push = await svc.PushRequirementsAsync(m, reqPkg, conn, e => $"https://meridian.test/?element={e.Id}");
        Assert.Empty(push.Errors);
        Assert.True(push.Done > 0);
        Assert.All(handler.Requests, r => Assert.Equal("glpat-plaintext-secret-123", r.Request.Headers.GetValues("PRIVATE-TOKEN").Single()));
        Assert.All(handler.Requests, r => Assert.Equal("https://gitlab.com/api/v4/projects/acme%2Fcar/issues", r.Request.RequestUri!.AbsoluteUri));
        var anyReq = reqPkg.Descendants().First(e => e.Kind == ElementKind.Requirement);
        Assert.Equal(["meridian"], Assert.Single(IssueLinks.Read(anyReq)).Labels!);

        var sync = await svc.SyncAsync(m, _project);
        Assert.Equal(push.Done, sync.Done);
        var refreshed = Assert.Single(IssueLinks.Read(anyReq));
        Assert.Equal(("Fresh title", "closed", "ben", true), (refreshed.Title, refreshed.Status, refreshed.Assignee, refreshed.Closed));
        Assert.Equal(["meridian", "done"], refreshed.Labels!);
        // Labels survive the model file round trip.
        Assert.Equal(["meridian", "done"], Assert.Single(IssueLinks.Read(ProjectFile.Deserialize(ProjectFile.Serialize(m)).Find(anyReq.Id)!)).Labels!);
    }
}
