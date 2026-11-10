using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Meridian.Core.CodeSync;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.CodeSync.Hosts;
using Meridian.Web.Integrations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meridian.Web.Tests;

/// <summary>Bitbucket and pull request webhook payloads, signatures and the PR comment text (no network).</summary>
public class CodeSyncPullRequestParsingTests
{
    private const string Head = "c1b2c3d4e5f60718293a4b5c6d7e8f9012345678";

    public const string BitbucketCloudPush = """
        {"actor":{"display_name":"Cleo"},"repository":{"full_name":"acme/sat"},
         "push":{"changes":[
           {"new":{"type":"tag","name":"v1","target":{"hash":"0000000000000000000000000000000000000001"}},"old":null,"closed":false},
           {"new":{"type":"branch","name":"main","target":{"hash":"c1b2c3d4e5f60718293a4b5c6d7e8f9012345678","message":"Add Thruster\n","author":{"raw":"Cleo <cleo@acme.test>","user":{"display_name":"Cleo Ray"}},
                   "links":{"html":{"href":"https://bitbucket.org/acme/sat/commits/c1b2c3d"}}}},"old":{"type":"branch","name":"main"},"closed":false}]}}
        """;

    public const string BitbucketServerPush = """
        {"eventKey":"repo:refs_changed","actor":{"name":"dan","displayName":"Dan Hale"},
         "changes":[{"ref":{"id":"refs/heads/main","displayId":"main","type":"BRANCH"},"fromHash":"1111111111111111111111111111111111111111","toHash":"c1b2c3d4e5f60718293a4b5c6d7e8f9012345678","type":"UPDATE"}]}
        """;

    public static string GitHubPullRequest(string action, bool merged = false, string target = "main") => $$"""
        {"action":"{{action}}","number":7,
         "pull_request":{"number":7,"title":"Add thruster","html_url":"https://github.com/acme/sat/pull/7","merged":{{(merged ? "true" : "false")}},
           "user":{"login":"cleo"},"head":{"sha":"{{Head}}","ref":"feature/thruster"},"base":{"ref":"{{target}}"} } }
        """;

    public const string GitLabMergeRequest = """
        {"object_kind":"merge_request","user":{"name":"Ben Ode"},
         "object_attributes":{"iid":12,"action":"update","oldrev":"1111111111111111111111111111111111111111","title":"Rename panel","url":"https://gitlab.com/acme/sat/-/merge_requests/12",
           "source_branch":"rename","target_branch":"main","last_commit":{"id":"c1b2c3d4e5f60718293a4b5c6d7e8f9012345678","author":{"name":"Ben"}}}}
        """;

    public const string BitbucketCloudPullRequest = """
        {"pullrequest":{"id":3,"title":"Thrusters","links":{"html":{"href":"https://bitbucket.org/acme/sat/pull-requests/3"}},"author":{"display_name":"Cleo Ray"},
          "source":{"branch":{"name":"thrusters"},"commit":{"hash":"c1b2c3d4e5f6"}},"destination":{"branch":{"name":"main"}}}}
        """;

    public const string BitbucketServerPullRequest = """
        {"eventKey":"pr:from_ref_updated","pullRequest":{"id":4,"title":"Panels","author":{"user":{"displayName":"Dan Hale"}},
          "fromRef":{"id":"refs/heads/panels","displayId":"panels","latestCommit":"c1b2c3d4e5f60718293a4b5c6d7e8f9012345678"},
          "toRef":{"id":"refs/heads/main","displayId":"main"},"links":{"self":[{"href":"https://bb.acme.test/projects/SAT/repos/fsw/pull-requests/4"}]}}}
        """;

    [Fact]
    public void Parses_Bitbucket_Cloud_And_Data_Center_Pushes()
    {
        var cloud = CodeWebhooks.ParseBitbucketPush(BitbucketCloudPush);
        var main = Assert.Single(cloud);   // the tag is not a branch
        Assert.Equal(new PushEvent("refs/heads/main", "main", Head, false, "Add Thruster\n", "Cleo Ray", "https://bitbucket.org/acme/sat/commits/c1b2c3d"), main);

        var server = Assert.Single(CodeWebhooks.ParseBitbucketPush(BitbucketServerPush));
        Assert.Equal(("main", Head, false, "Dan Hale"), (server.Branch, server.After, server.Deleted, server.Author));
        Assert.True(Assert.Single(CodeWebhooks.ParseBitbucketPush(BitbucketServerPush.Replace("\"UPDATE\"", "\"DELETE\""))).Deleted);
        Assert.True(Assert.Single(CodeWebhooks.ParseBitbucketPush("""{"push":{"changes":[{"new":null,"old":{"type":"branch","name":"main"},"closed":true}]}}""")).Deleted);
        Assert.Empty(CodeWebhooks.ParseBitbucketPush("not json"));

        Assert.Equal(WebhookEventKind.Push, CodeWebhooks.BitbucketEvent("repo:push"));
        Assert.Equal(WebhookEventKind.Push, CodeWebhooks.BitbucketEvent("repo:refs_changed"));
        Assert.Equal(WebhookEventKind.Ping, CodeWebhooks.BitbucketEvent("diagnostics:ping"));
        Assert.Equal(WebhookEventKind.PullRequest, CodeWebhooks.BitbucketEvent("pr:merged"));
        Assert.Equal(WebhookEventKind.PullRequest, CodeWebhooks.BitbucketEvent("pullrequest:fulfilled"));
        Assert.Equal(WebhookEventKind.Other, CodeWebhooks.BitbucketEvent("repo:commit_comment_created"));
    }

    [Fact]
    public void Parses_Pull_And_Merge_Request_Events_Of_Every_Host()
    {
        var opened = CodeWebhooks.ParseGitHubPullRequest(GitHubPullRequest("opened"))!;
        Assert.Equal(new PullRequestEvent(7, PullRequestAction.Opened, Head, "feature/thruster", "main", "Add thruster", "https://github.com/acme/sat/pull/7", "cleo"), opened);
        Assert.Equal(PullRequestAction.Updated, CodeWebhooks.ParseGitHubPullRequest(GitHubPullRequest("synchronize"))!.Action);
        Assert.Equal(PullRequestAction.Merged, CodeWebhooks.ParseGitHubPullRequest(GitHubPullRequest("closed", merged: true))!.Action);
        Assert.Equal(PullRequestAction.Closed, CodeWebhooks.ParseGitHubPullRequest(GitHubPullRequest("closed"))!.Action);
        Assert.Equal(PullRequestAction.Other, CodeWebhooks.ParseGitHubPullRequest(GitHubPullRequest("labeled"))!.Action);
        Assert.Null(CodeWebhooks.ParseGitHubPullRequest(CodeWebhookParsingTests.GitHubPush));
        Assert.Equal(WebhookEventKind.PullRequest, CodeWebhooks.GitHubEvent("pull_request"));

        var mr = CodeWebhooks.ParseGitLabMergeRequest(GitLabMergeRequest)!;
        Assert.Equal((12, PullRequestAction.Updated, Head, "main", "Ben Ode"), (mr.Number, mr.Action, mr.HeadSha, mr.TargetBranch, mr.Author));
        Assert.Equal(PullRequestAction.Other, CodeWebhooks.ParseGitLabMergeRequest(GitLabMergeRequest.Replace("\"oldrev\"", "\"x\""))!.Action);   // a title edit
        Assert.Equal(PullRequestAction.Merged, CodeWebhooks.ParseGitLabMergeRequest(GitLabMergeRequest.Replace("\"update\"", "\"merge\""))!.Action);
        Assert.Equal(WebhookEventKind.PullRequest, CodeWebhooks.GitLabEvent("Merge Request Hook"));

        var cloud = CodeWebhooks.ParseBitbucketPullRequest(BitbucketCloudPullRequest, "pullrequest:created")!;
        Assert.Equal(new PullRequestEvent(3, PullRequestAction.Opened, "c1b2c3d4e5f6", "thrusters", "main", "Thrusters", "https://bitbucket.org/acme/sat/pull-requests/3", "Cleo Ray"), cloud);
        Assert.Equal(PullRequestAction.Closed, CodeWebhooks.ParseBitbucketPullRequest(BitbucketCloudPullRequest, "pullrequest:rejected")!.Action);
        var server = CodeWebhooks.ParseBitbucketPullRequest(BitbucketServerPullRequest, "pr:from_ref_updated")!;
        Assert.Equal(new PullRequestEvent(4, PullRequestAction.Updated, Head, "panels", "main", "Panels", "https://bb.acme.test/projects/SAT/repos/fsw/pull-requests/4", "Dan Hale"), server);
        Assert.Equal(PullRequestAction.Merged, CodeWebhooks.ParseBitbucketPullRequest(BitbucketServerPullRequest, "pr:merged")!.Action);
        Assert.Null(CodeWebhooks.ParseBitbucketPullRequest("{}", "pr:merged"));
    }

    [Fact]
    public void Bitbucket_Signature_And_Url_Token_Are_Checked_In_Constant_Time()
    {
        var body = Encoding.UTF8.GetBytes(BitbucketServerPush);
        var sig = CodeWebhooks.SignGitHub("s3cret", body);   // the same sha256=<hex HMAC> format
        Assert.True(CodeWebhooks.VerifyBitbucketSignature("s3cret", body, sig));
        Assert.False(CodeWebhooks.VerifyBitbucketSignature("s3cret", body, null));
        Assert.False(CodeWebhooks.VerifyBitbucketSignature("other", body, sig));
        Assert.True(CodeWebhooks.VerifyUrlToken("s3cret", "s3cret"));
        Assert.False(CodeWebhooks.VerifyUrlToken("s3cret", "s3cre"));
        Assert.False(CodeWebhooks.VerifyUrlToken("s3cret", null));
    }

    [Fact]
    public void Pull_Request_Comment_Lists_Counts_The_Top_Ten_And_Escapes_Names()
    {
        var changes = Enumerable.Range(1, 12).Select(i => new PreviewChange(i <= 9 ? CodeSyncChangeKind.Added : CodeSyncChangeKind.Removed, $"Block{i}", "Code::Sat", "Block"))
            .Append(new PreviewChange(CodeSyncChangeKind.Changed, "Evil|<img src=x>[link](http://x) @team", "Code", "Block")).ToList();
        var body = CodeSyncHostService.PreviewComment(7, Head, new CodeSyncOutcome(CodeSyncStatus.AwaitingReview, "x", "/r", Guid.NewGuid()), changes, "https://meridian.test/r");
        Assert.Contains("PR #7 at `c1b2c3d` changes the model: **9 added, 1 changed, 3 removed**", body);
        Assert.Equal(10, body.Split('\n').Count(l => l.StartsWith("| Added") || l.StartsWith("| Changed") || l.StartsWith("| Removed")));
        Assert.Contains("…and 3 more.", body);
        Assert.Contains("[Open the preview in Meridian](https://meridian.test/r)", body);
        Assert.Contains(@"Evil\|\<img src=x\>\[link\](http://x) \@team", body);

        Assert.Contains("does not change the model", CodeSyncHostService.PreviewComment(7, Head, new CodeSyncOutcome(CodeSyncStatus.NoChanges, "No model changes", null, null), [], null));
        Assert.Contains("failed: boom", CodeSyncHostService.PreviewComment(7, Head, new CodeSyncOutcome(CodeSyncStatus.Failed, "boom", null, null), [], null));
    }

    [Fact]
    public void Queue_Keeps_A_Pull_Request_Apart_From_Pushes_And_A_Close_Replaces_A_Waiting_Preview()
    {
        var q = new CodeSyncQueue();
        Guid p = Guid.NewGuid(), a = Guid.NewGuid();
        Assert.True(q.TryEnqueue(new CodeSyncJob(p, a, "main", "aaaaaaa1", null, null, null, "webhook")));
        Assert.True(q.TryEnqueue(new CodeSyncJob(p, a, "feature", "bbbbbbb1", null, null, null, "webhook", PullRequest: 7)));
        Assert.True(q.TryEnqueue(new CodeSyncJob(p, a, "feature", "bbbbbbb2", null, null, null, "webhook", PullRequest: 7, ClosePreview: true, Merged: true)));
        Assert.Equal(2, q.Count);
        Assert.True(q.TryDequeue(out var push));
        Assert.Null(push!.PullRequest);
        Assert.True(q.TryDequeue(out var close));
        Assert.Equal((7, true, true), (close!.PullRequest, close.ClosePreview, close.Merged));
    }

    [Fact]
    public void Config_Knows_Bitbucket_Cloud_From_Data_Center()
    {
        var cloud = new CodeRepoConfig { Host = GitHostKind.Bitbucket, BaseUrl = "https://bitbucket.org/", Owner = "acme", Repo = "sat" };
        cloud.Normalize();
        Assert.Null(cloud.Validate());
        Assert.Equal((CodeRepoConfig.BitbucketCloudApi, true, "Bitbucket Cloud", "acme/sat", "sat"), (cloud.BaseUrl, cloud.IsBitbucketCloud, cloud.HostLabel, cloud.Display, cloud.EffectiveRootPackage));
        var dc = new CodeRepoConfig { Host = GitHostKind.Bitbucket, BaseUrl = "https://bb.acme.test/rest/api/1.0", Owner = "SAT", Repo = "fsw" };
        dc.Normalize();
        Assert.Null(dc.Validate());
        Assert.Equal((false, "Bitbucket Data Center"), (dc.IsBitbucketCloud, dc.HostLabel));
        Assert.Equal("https://bb.acme.test", BitbucketHost.ServerRoot(dc.BaseUrl));
        Assert.Contains("project key", new CodeRepoConfig { Host = GitHostKind.Bitbucket, BaseUrl = "https://bb.acme.test", Repo = "fsw" }.Validate());
        Assert.Equal(CodeRepoConfig.BitbucketCloudApi, CodeRepoConfig.DefaultBaseUrl(GitHostKind.Bitbucket));
        Assert.Contains("\"host\":\"Bitbucket\"", cloud.ToJson());
        Assert.True(CodeRepoConfig.Parse("{}").CommentOnPullRequests && CodeRepoConfig.Parse("{}").PreviewPullRequests && CodeRepoConfig.Parse("{}").AutoDiagrams);
    }
}

/// <summary>Bitbucket hosts and pull request previews through <see cref="CodeSyncHostService"/>, over SQLite and a scripted HTTP handler.</summary>
public sealed class CodeSyncPullRequestServiceTests : IAsyncLifetime
{
    private const string Sha = "c1b2c3d4e5f60718293a4b5c6d7e8f9012345678";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-web-pr-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;
    private Guid _project;

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        var user = await _db.CreateUserAsync("ann", "Ann", "correct horse");
        _project = await _db.CreateProjectAsync("Sat", ProjectFile.Serialize(SampleModels.Vehicle()), user.Id);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    /// <summary>A runner that also does previews, recording what it was asked.</summary>
    private sealed class FakePreviews : ICodeSyncRunner, IPullRequestPreviews
    {
        public List<CodeSyncRequest> Requests { get; } = new();
        public List<(int Number, CodeSyncRequest Request)> Previews { get; } = new();
        public List<(int Number, bool Merged)> Closed { get; } = new();
        public IReadOnlyList<PreviewChange> Changes { get; set; } = [new(CodeSyncChangeKind.Added, "Thruster", "Code::Sat", "Block")];

        public Task<CodeSyncOutcome> RunAsync(CodeSyncRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new CodeSyncOutcome(CodeSyncStatus.AwaitingReview, "1 added", "/review/main", Guid.NewGuid()));
        }

        public Task<CodeSyncOutcome> RunPreviewAsync(CodeSyncRequest request, int pullRequest, CancellationToken ct = default)
        {
            Previews.Add((pullRequest, request));
            return Task.FromResult(new CodeSyncOutcome(CodeSyncStatus.AwaitingReview, "1 added", $"/review/pr-{pullRequest}", Guid.NewGuid()));
        }

        public Task<string> ClosePreviewAsync(Guid projectId, string repoDisplay, int pullRequest, bool merged, CancellationToken ct = default)
        {
            Closed.Add((pullRequest, merged));
            return Task.FromResult($"closed {repoDisplay} #{pullRequest}");
        }

        public Task<IReadOnlyList<PreviewChange>> PreviewChangesAsync(Guid projectId, string repoDisplay, int pullRequest) => Task.FromResult(Changes);
    }

    private (CodeSyncHostService Service, CodeSyncQueue Queue) Create(FakeHandler handler, ICodeSyncRunner runner)
    {
        var services = new ServiceCollection();
        services.AddSingleton(runner);
        var sp = services.BuildServiceProvider();
        var factory = new FakeHttpClientFactory(handler);
        var integrations = new IntegrationService(_db, new EphemeralDataProtectionProvider(), factory);
        var queue = new CodeSyncQueue();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Meridian:PublicUrl"] = "https://meridian.test/" }).Build();
        return (new CodeSyncHostService(_db, integrations, factory, sp.GetRequiredService<IServiceScopeFactory>(), queue,
            new CodeSyncRateLimiter(), config, NullLogger<CodeSyncHostService>.Instance), queue);
    }

    private static HeaderDictionary Headers(params (string, string)[] h)
    {
        var d = new HeaderDictionary();
        foreach (var (k, v) in h) d[k] = v;
        return d;
    }

    private static HttpResponseMessage Archive(string root) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(TestArchives.TarGz(($"{root}/src/Thruster.cs", "class Thruster {}"), ($"{root}/README.md", "#"))) };

    private static JsonNode Body(FakeHandler h, Func<string, bool> url, HttpMethod? method = null) =>
        JsonNode.Parse(h.Requests.Last(r => url(r.Request.RequestUri!.AbsoluteUri) && (method is null || r.Request.Method == method)).Body!)!;

    [Fact]
    public async Task Bitbucket_Cloud_Tests_Fetches_The_Archive_And_Posts_Build_Statuses()
    {
        var handler = new FakeHandler((req, _) =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url == "https://api.bitbucket.org/2.0/repositories/acme/sat") return FakeHandler.Json("""{"full_name":"acme/sat"}""");
            if (url == "https://api.bitbucket.org/2.0/repositories/acme/sat/refs/branches/main")
                return FakeHandler.Json($$"""{"name":"main","target":{"hash":"{{Sha}}","message":"Head","author":{"raw":"Cleo <c@x>"},"links":{"html":{"href":"https://bitbucket.org/acme/sat/commits/{{Sha}}"} } } }""");
            if (url == $"https://bitbucket.org/acme/sat/get/{Sha}.tar.gz") return Archive("acme-sat-c1b2c3d4e5f6");
            if (url.EndsWith("/statuses/build")) return FakeHandler.Json("{}", HttpStatusCode.Created);
            return FakeHandler.Json("""{"type":"error","error":{"message":"Repository not found"}}""", HttpStatusCode.NotFound);
        });
        var runner = new FakePreviews();
        var (svc, queue) = Create(handler, runner);
        var config = new CodeRepoConfig { Host = GitHostKind.Bitbucket, BaseUrl = "https://bitbucket.org", Owner = "acme", Repo = "sat", Paths = ["src"] };

        Assert.Equal("Connected to acme/sat on Bitbucket: main is at c1b2c3d.", await svc.TestAsync(_project, config, "bb-token", null));
        Assert.Equal(("Bearer", "bb-token"), (handler.Requests[0].Request.Headers.Authorization!.Scheme, handler.Requests[0].Request.Headers.Authorization!.Parameter));
        var missing = await Assert.ThrowsAsync<GitHostException>(() => svc.TestAsync(_project, new CodeRepoConfig { Host = GitHostKind.Bitbucket, Owner = "acme", Repo = "nope" }, "bb-token", null));
        Assert.Contains("Repository not found", missing.Message);

        var saved = await svc.SaveAsync(_project, null, null, config, "bb-token");
        await svc.SyncNowAsync(_project, saved.Connection.Id);
        Assert.True(queue.TryDequeue(out var job));
        var outcome = await svc.ProcessAsync(job!);
        Assert.Equal(CodeSyncStatus.AwaitingReview, outcome.Status);
        Assert.Equal("src/Thruster.cs", Assert.Single(Assert.Single(runner.Requests).Files).Path);

        var statuses = handler.Requests.Where(r => r.Request.RequestUri!.AbsoluteUri == $"https://api.bitbucket.org/2.0/repositories/acme/sat/commit/{Sha}/statuses/build").Select(r => JsonNode.Parse(r.Body!)!).ToList();
        Assert.Equal(["INPROGRESS", "INPROGRESS"], statuses.Select(s => (string)s["state"]!));   // waiting for review
        Assert.Equal((BitbucketHost.StatusKey, "meridian/model"), ((string)statuses[0]["key"]!, (string)statuses[0]["name"]!));
        Assert.Equal($"https://bitbucket.org/acme/sat/commits/{Sha}", (string)statuses[0]["url"]!);   // Bitbucket needs a link
        Assert.Equal("https://meridian.test/review/main", (string)statuses[1]["url"]!);
    }

    [Fact]
    public async Task Bitbucket_Data_Center_Uses_Rest_1_0_Basic_Auth_And_The_Build_Status_Api()
    {
        var handler = new FakeHandler((req, _) =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url == "https://bb.acme.test/rest/api/1.0/projects/SAT/repos/fsw") return FakeHandler.Json("""{"slug":"fsw","project":{"key":"SAT"}}""");
            if (url == "https://bb.acme.test/rest/api/1.0/projects/SAT/repos/fsw/commits?until=refs%2Fheads%2Fmain&limit=1")
                return FakeHandler.Json($$"""{"values":[{"id":"{{Sha}}","message":"Head","author":{"name":"dan","displayName":"Dan Hale"} } ] }""");
            if (url == $"https://bb.acme.test/rest/api/1.0/projects/SAT/repos/fsw/archive?at={Sha}&format=tar.gz&prefix=fsw%2F") return Archive("fsw");
            if (url == $"https://bb.acme.test/rest/build-status/1.0/commits/{Sha}") return new HttpResponseMessage(HttpStatusCode.NoContent);
            return FakeHandler.Json("""{"errors":[{"message":"Project SAT does not exist."}]}""", HttpStatusCode.NotFound);
        });
        var runner = new FakePreviews();
        var (svc, queue) = Create(handler, runner);
        var config = new CodeRepoConfig { Host = GitHostKind.Bitbucket, BaseUrl = "https://bb.acme.test", Owner = "SAT", Repo = "fsw" };

        Assert.Equal("Connected to SAT/fsw on Bitbucket Data Center: main is at c1b2c3d.", await svc.TestAsync(_project, config, "dan:app-password", null));
        var auth = handler.Requests[0].Request.Headers.Authorization!;
        Assert.Equal(("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("dan:app-password"))), (auth.Scheme, auth.Parameter));

        var saved = await svc.SaveAsync(_project, null, null, config, "dan:app-password");
        await svc.SyncNowAsync(_project, saved.Connection.Id);
        Assert.True(queue.TryDequeue(out var job));
        Assert.Equal(CodeSyncStatus.AwaitingReview, (await svc.ProcessAsync(job!)).Status);
        Assert.Equal("src/Thruster.cs", Assert.Single(Assert.Single(runner.Requests).Files).Path);
        Assert.Equal("Dan Hale", runner.Requests[0].Author);
        var status = Body(handler, u => u.Contains("/rest/build-status/1.0/commits/"));
        Assert.Equal(("INPROGRESS", "https://meridian.test/review/main"), ((string)status["state"]!, (string)status["url"]!));

        var wrong = await Assert.ThrowsAsync<GitHostException>(() => svc.TestAsync(_project, new CodeRepoConfig { Host = GitHostKind.Bitbucket, BaseUrl = "https://bb.acme.test", Owner = "NOPE", Repo = "fsw" }, "x", null));
        Assert.Contains("Project SAT does not exist", wrong.Message);
    }

    [Fact]
    public async Task Bitbucket_Cloud_Webhook_Needs_The_Url_Token_And_Data_Center_The_Signature()
    {
        var (svc, queue) = Create(new FakeHandler((_, _) => FakeHandler.Json("{}")), new FakePreviews());
        var cloud = await svc.SaveAsync(_project, null, null, new CodeRepoConfig { Host = GitHostKind.Bitbucket, Owner = "acme", Repo = "sat" }, "t");
        var body = Encoding.UTF8.GetBytes(CodeSyncPullRequestParsingTests.BitbucketCloudPush);
        var push = Headers(("X-Event-Key", "repo:push"));
        Assert.Equal(401, (await svc.HandleWebhookAsync(cloud.Connection.Id, push, body)).StatusCode);
        Assert.Equal(401, (await svc.HandleWebhookAsync(cloud.Connection.Id, push, body, "wrong")).StatusCode);
        Assert.Equal(0, queue.Count);
        var ok = await svc.HandleWebhookAsync(cloud.Connection.Id, push, body, cloud.NewWebhookSecret);
        Assert.Equal(202, ok.StatusCode);
        Assert.True(queue.TryDequeue(out var job));
        Assert.Equal((Sha, "Cleo Ray", "Add Thruster\n"), (job!.Commit, job.Author, job.Message));
        // A Cloud webhook that also has the secret set is signed instead.
        var signed = Headers(("X-Event-Key", "repo:push"), ("X-Hub-Signature", CodeWebhooks.SignGitHub(cloud.NewWebhookSecret!, body)));
        Assert.Equal(202, (await svc.HandleWebhookAsync(cloud.Connection.Id, signed, body)).StatusCode);
        Assert.True(queue.TryDequeue(out _));
        Assert.Contains("?token=", CodeSyncHostService.WebhookUrlWithToken("https://m.test", cloud.Connection.Id, "abc"));

        var dc = await svc.SaveAsync(_project, null, null, new CodeRepoConfig { Host = GitHostKind.Bitbucket, BaseUrl = "https://bb.acme.test", Owner = "SAT", Repo = "fsw" }, "t");
        var dcBody = Encoding.UTF8.GetBytes(CodeSyncPullRequestParsingTests.BitbucketServerPush);
        Assert.Equal(401, (await svc.HandleWebhookAsync(dc.Connection.Id, Headers(("X-Event-Key", "repo:refs_changed")), dcBody, dc.NewWebhookSecret)).StatusCode);   // no URL token on Data Center
        var ping = Encoding.UTF8.GetBytes("{}");
        Assert.Equal(200, (await svc.HandleWebhookAsync(dc.Connection.Id, Headers(("X-Event-Key", "diagnostics:ping"), ("X-Hub-Signature", CodeWebhooks.SignGitHub(dc.NewWebhookSecret!, ping))), ping)).StatusCode);
        var dcOk = await svc.HandleWebhookAsync(dc.Connection.Id, Headers(("X-Event-Key", "repo:refs_changed"), ("X-Hub-Signature", CodeWebhooks.SignGitHub(dc.NewWebhookSecret!, dcBody))), dcBody);
        Assert.Equal(202, dcOk.StatusCode);
        Assert.True(queue.TryDequeue(out var dcJob));
        Assert.Equal((dc.Connection.Id, Sha), (dcJob!.ConnectionId, dcJob.Commit));
    }

    [Fact]
    public async Task GitHub_Pull_Request_Is_Previewed_Commented_Once_Updated_In_Place_And_Closed_On_Merge()
    {
        var nextComment = 900;
        var handler = new FakeHandler((req, _) =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("/tarball/")) return Archive("acme-sat-c1b2c3d");
            if (url.Contains("/statuses/")) return FakeHandler.Json("{}", HttpStatusCode.Created);
            if (req.Method == HttpMethod.Post && url == "https://api.github.com/repos/acme/sat/issues/7/comments") return FakeHandler.Json($$"""{"id":{{++nextComment}}}""", HttpStatusCode.Created);
            if (req.Method == HttpMethod.Patch && url == "https://api.github.com/repos/acme/sat/issues/comments/901") return FakeHandler.Json("""{"id":901}""");
            return FakeHandler.Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound);
        });
        var runner = new FakePreviews();
        var (svc, queue) = Create(handler, runner);
        var saved = await svc.SaveAsync(_project, null, null, new CodeRepoConfig { Owner = "acme", Repo = "sat" }, "ghp_x");
        var id = saved.Connection.Id;
        async Task<WebhookResult> Deliver(string json)
        {
            var b = Encoding.UTF8.GetBytes(json);
            return await svc.HandleWebhookAsync(id, Headers(("X-GitHub-Event", "pull_request"), ("X-Hub-Signature-256", CodeWebhooks.SignGitHub(saved.NewWebhookSecret!, b))), b);
        }

        var other = await Deliver(CodeSyncPullRequestParsingTests.GitHubPullRequest("opened", target: "release"));
        Assert.Contains("targets release", other.Message);
        Assert.Equal(202, (await Deliver(CodeSyncPullRequestParsingTests.GitHubPullRequest("labeled"))).StatusCode);
        Assert.Equal(0, queue.Count);

        // Opened: a preview sync of the PR head, a green status, one comment.
        Assert.StartsWith("Queued a preview of PR #7", (await Deliver(CodeSyncPullRequestParsingTests.GitHubPullRequest("opened"))).Message);
        Assert.True(queue.TryDequeue(out var job));
        Assert.Equal((7, Sha, "feature/thruster"), (job!.PullRequest, job.Commit, job.Branch));
        var outcome = await svc.ProcessAsync(job);
        Assert.Equal(CodeSyncStatus.AwaitingReview, outcome.Status);
        Assert.Empty(runner.Requests);   // not the main review branch
        var (number, request) = Assert.Single(runner.Previews);
        Assert.Equal((7, "src/Thruster.cs"), (number, Assert.Single(request.Files).Path));
        var statuses = handler.Requests.Where(r => r.Request.RequestUri!.AbsoluteUri.Contains("/statuses/")).Select(r => JsonNode.Parse(r.Body!)!).ToList();
        Assert.Equal(["pending", "success"], statuses.Select(s => (string)s["state"]!));
        Assert.Equal(("Model preview: 1 added", "https://meridian.test/review/pr-7"), ((string)statuses[1]["description"]!, (string)statuses[1]["target_url"]!));
        var comment = (string)Body(handler, u => u.EndsWith("/issues/7/comments"), HttpMethod.Post)["body"]!;
        Assert.Contains("Thruster", comment);
        Assert.Contains("https://meridian.test/review/pr-7", comment);
        var pr = Assert.Single(CodeRepoConfig.Parse((await _db.FindIntegrationAsync(id))!.ConfigJson).PullRequests);
        Assert.Equal((7, "901", "awaiting-review", "Add thruster"), (pr.Number, pr.CommentId, pr.Status, pr.Title));

        // Updated: the same comment is edited, no second one.
        await Deliver(CodeSyncPullRequestParsingTests.GitHubPullRequest("synchronize"));
        Assert.True(queue.TryDequeue(out var again));
        runner.Changes = [new(CodeSyncChangeKind.Added, "Thruster", "Code::Sat", "Block"), new(CodeSyncChangeKind.Changed, "Engine", "Code::Sat", "Block")];
        await svc.ProcessAsync(again!);
        Assert.Single(handler.Requests, r => r.Request.Method == HttpMethod.Post && r.Request.RequestUri!.AbsoluteUri.EndsWith("/comments"));
        Assert.Contains("1 added, 1 changed, 0 removed", (string)Body(handler, u => u.EndsWith("/issues/comments/901"), HttpMethod.Patch)["body"]!);

        // A comment someone deleted is posted again.
        handler.Requests.Clear();
        await Deliver(CodeSyncPullRequestParsingTests.GitHubPullRequest("synchronize"));
        Assert.True(queue.TryDequeue(out var third));
        var stored = CodeRepoConfig.Parse((await _db.FindIntegrationAsync(id))!.ConfigJson);
        stored.PullRequests[0].CommentId = "555";
        await _db.SaveIntegrationAsync((await _db.FindIntegrationAsync(id))! with { ConfigJson = stored.ToJson() });
        await svc.ProcessAsync(third!);
        Assert.Contains(handler.Requests, r => r.Request.Method == HttpMethod.Patch && r.Request.RequestUri!.AbsoluteUri.EndsWith("/issues/comments/555"));
        Assert.Equal("902", Assert.Single(CodeRepoConfig.Parse((await _db.FindIntegrationAsync(id))!.ConfigJson).PullRequests).CommentId);

        // Merged: the preview is closed and forgotten; the push to main does the rest.
        Assert.Equal(202, (await Deliver(CodeSyncPullRequestParsingTests.GitHubPullRequest("closed", merged: true))).StatusCode);
        Assert.True(queue.TryDequeue(out var close));
        Assert.True(close!.ClosePreview);
        Assert.Equal(CodeSyncStatus.NoChanges, (await svc.ProcessAsync(close)).Status);
        Assert.Equal((7, true), Assert.Single(runner.Closed));
        Assert.Empty(CodeRepoConfig.Parse((await _db.FindIntegrationAsync(id))!.ConfigJson).PullRequests);
    }

    [Fact]
    public async Task Previews_And_Comments_Follow_The_Connection_Settings_And_Saving_Keeps_Pull_Request_State()
    {
        var handler = new FakeHandler((req, _) =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("/repository/archive.tar.gz")) return Archive("sat-c1b2");
            if (url.EndsWith("/merge_requests/12/notes")) return FakeHandler.Json("""{"id":77}""", HttpStatusCode.Created);
            return FakeHandler.Json("{}", HttpStatusCode.Created);
        });
        var runner = new FakePreviews();
        var (svc, queue) = Create(handler, runner);
        var config = new CodeRepoConfig { Host = GitHostKind.GitLab, BaseUrl = "https://gitlab.example.com", Project = "acme/sat", CommentOnPullRequests = false };
        var saved = await svc.SaveAsync(_project, null, null, config, "glpat");
        var id = saved.Connection.Id;
        var body = Encoding.UTF8.GetBytes(CodeSyncPullRequestParsingTests.GitLabMergeRequest);
        var headers = Headers(("X-Gitlab-Event", "Merge Request Hook"), ("X-Gitlab-Token", saved.NewWebhookSecret!));

        Assert.Equal(202, (await svc.HandleWebhookAsync(id, headers, body)).StatusCode);
        Assert.True(queue.TryDequeue(out var job));
        await svc.ProcessAsync(job!);
        Assert.Single(runner.Previews);
        Assert.DoesNotContain(handler.Requests, r => r.Request.RequestUri!.AbsoluteUri.Contains("/notes"));   // comments off

        // Saving the settings again keeps the pull request state the server holds.
        config.CommentOnPullRequests = true;
        await svc.SaveAsync(_project, id, null, config, null);
        Assert.Single(CodeRepoConfig.Parse((await _db.FindIntegrationAsync(id))!.ConfigJson).PullRequests);
        await svc.HandleWebhookAsync(id, headers, body);
        Assert.True(queue.TryDequeue(out var next));
        await svc.ProcessAsync(next!);
        Assert.Contains("Thruster", (string)Body(handler, u => u.EndsWith("/merge_requests/12/notes"))["body"]!);
        Assert.Equal("77", Assert.Single(CodeRepoConfig.Parse((await _db.FindIntegrationAsync(id))!.ConfigJson).PullRequests).CommentId);

        config.PreviewPullRequests = false;
        await svc.SaveAsync(_project, id, null, config, null);
        Assert.Contains("previews are off", (await svc.HandleWebhookAsync(id, headers, body)).Message);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Bitbucket_Comments_Are_Created_Then_Updated_With_Their_Version()
    {
        var handler = new FakeHandler((req, _) =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (req.Method == HttpMethod.Post && url.EndsWith("/pull-requests/4/comments")) return FakeHandler.Json("""{"id":31,"version":0}""", HttpStatusCode.Created);
            if (req.Method == HttpMethod.Get && url.EndsWith("/pull-requests/4/comments/31")) return FakeHandler.Json("""{"id":31,"version":3}""");
            if (req.Method == HttpMethod.Put && url.EndsWith("/pull-requests/4/comments/31")) return FakeHandler.Json("""{"id":31,"version":4}""");
            if (req.Method == HttpMethod.Post && url.EndsWith("/pullrequests/3/comments")) return FakeHandler.Json("""{"id":41}""", HttpStatusCode.Created);
            if (req.Method == HttpMethod.Put && url.EndsWith("/pullrequests/3/comments/41")) return FakeHandler.Json("""{"id":41}""");
            return FakeHandler.Json("{}", HttpStatusCode.NotFound);
        });
        var http = new HttpClient(handler);
        var dc = GitHostFactory.Create(new CodeRepoConfig { Host = GitHostKind.Bitbucket, BaseUrl = "https://bb.acme.test", Owner = "SAT", Repo = "fsw" }, "t", http);
        Assert.Equal("31", await dc.UpsertPullRequestCommentAsync(4, null, "first"));
        Assert.Equal("first", (string)Body(handler, u => u.EndsWith("/comments"), HttpMethod.Post)["text"]!);
        Assert.Equal("31", await dc.UpsertPullRequestCommentAsync(4, "31", "second"));
        var put = Body(handler, u => u.EndsWith("/comments/31"), HttpMethod.Put);
        Assert.Equal(("second", 3), ((string)put["text"]!, (int)put["version"]!));

        var cloud = GitHostFactory.Create(new CodeRepoConfig { Host = GitHostKind.Bitbucket, BaseUrl = CodeRepoConfig.BitbucketCloudApi, Owner = "acme", Repo = "sat" }, "t", http);
        Assert.Equal("41", await cloud.UpsertPullRequestCommentAsync(3, null, "hello"));
        Assert.Equal("41", await cloud.UpsertPullRequestCommentAsync(3, "41", "again"));
        Assert.Equal("again", (string)Body(handler, u => u.EndsWith("/pullrequests/3/comments/41"), HttpMethod.Put)["content"]!["raw"]!);
        await Assert.ThrowsAsync<GitHostException>(() => cloud.UpsertPullRequestCommentAsync(0, null, "x"));
    }
}
