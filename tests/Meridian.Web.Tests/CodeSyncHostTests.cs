using System.Formats.Tar;
using System.IO.Compression;
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

/// <summary>Builds .tar.gz archives shaped like GitHub / GitLab repository archives.</summary>
internal static class TestArchives
{
    public static byte[] TarGz(params (string Name, string? Content)[] files) =>
        TarGz(files.Select(f => (f.Name, f.Content, TarEntryType.RegularFile, (string?)null)).ToArray());

    public static byte[] TarGz((string Name, string? Content, TarEntryType Type, string? Link)[] entries, bool globalHeader = true)
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var w = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true))
        {
            if (globalHeader) w.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string> { ["comment"] = "abc123" }));
            foreach (var (name, content, type, link) in entries)
            {
                var e = new PaxTarEntry(type, name);
                if (link != null) e.LinkName = link;
                if (content != null) e.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                w.WriteEntry(e);
            }
        }
        return ms.ToArray();
    }

    public static Func<string, bool> Cs => p => p.EndsWith(".cs", StringComparison.Ordinal);
}

public class CodeSyncArchiveTests
{
    [Fact]
    public async Task Extracts_Watched_Source_Files_Strips_The_Root_Folder_And_Ignores_Links()
    {
        var tgz = TestArchives.TarGz(
        [
            ("acme-sat-abc123/", null, TarEntryType.Directory, null),
            ("acme-sat-abc123/src/Power/Battery.cs", "\uFEFFnamespace Sat.Power; public class Battery {}", TarEntryType.RegularFile, null),
            ("acme-sat-abc123/src/Power/README.md", "docs", TarEntryType.RegularFile, null),
            ("acme-sat-abc123/tests/BatteryTests.cs", "class T {}", TarEntryType.RegularFile, null),
            ("acme-sat-abc123/src/Link.cs", null, TarEntryType.SymbolicLink, "/etc/passwd"),
            ("acme-sat-abc123/src/Hard.cs", null, TarEntryType.HardLink, "acme-sat-abc123/src/Power/Battery.cs"),
            ("acme-sat-abc123/src/Bin.cs", "a\0b", TarEntryType.RegularFile, null),
        ]);

        var snap = await SnapshotArchive.ReadTarGzAsync(new MemoryStream(tgz), ["src"], TestArchives.Cs, SnapshotLimits.Default);

        var file = Assert.Single(snap);
        Assert.Equal("src/Power/Battery.cs", file.Path);
        Assert.Equal("namespace Sat.Power; public class Battery {}", file.Content);   // BOM removed
        Assert.Contains(snap.Skipped, s => s.StartsWith("src/Bin.cs"));

        var all = await SnapshotArchive.ReadTarGzAsync(new MemoryStream(tgz), [], TestArchives.Cs, SnapshotLimits.Default);
        Assert.Equal(["src/Power/Battery.cs", "tests/BatteryTests.cs"], all.Select(f => f.Path));
    }

    [Fact]
    public async Task Oversized_Archive_Is_Rejected()
    {
        var tgz = TestArchives.TarGz(("r/a.cs", new string('x', 50_000) + Guid.NewGuid()), ("r/b.cs", Convert.ToBase64String(Guid.NewGuid().ToByteArray())));
        var ex = await Assert.ThrowsAsync<GitHostException>(() =>
            SnapshotArchive.ReadTarGzAsync(new MemoryStream(tgz), [], TestArchives.Cs, new SnapshotLimits { MaxArchiveBytes = 64 }));
        Assert.Contains("archive is larger", ex.Message);
    }

    [Fact]
    public async Task Gzip_Bomb_Outside_The_Watched_Folders_Is_Still_Bounded()
    {
        var tgz = TestArchives.TarGz(("r/assets/zeros.bin", new string('\0', 2_000_000)), ("r/src/a.cs", "class A {}"));
        Assert.True(tgz.Length < 50_000);
        var ex = await Assert.ThrowsAsync<GitHostException>(() =>
            SnapshotArchive.ReadTarGzAsync(new MemoryStream(tgz), ["src"], TestArchives.Cs, new SnapshotLimits { MaxUncompressedBytes = 1_000_000 }));
        Assert.Contains("when unpacked", ex.Message);
    }

    [Fact]
    public async Task Too_Many_Files_And_Too_Much_Content_Are_Rejected_And_Large_Files_Skipped()
    {
        var tgz = TestArchives.TarGz(("r/a.cs", "class A {}"), ("r/b.cs", "class B {}"), ("r/c.cs", "class C {}"));
        var many = await Assert.ThrowsAsync<GitHostException>(() =>
            SnapshotArchive.ReadTarGzAsync(new MemoryStream(tgz), [], TestArchives.Cs, new SnapshotLimits { MaxFiles = 2 }));
        Assert.Contains("More than 2 source files", many.Message);

        var total = await Assert.ThrowsAsync<GitHostException>(() =>
            SnapshotArchive.ReadTarGzAsync(new MemoryStream(tgz), [], TestArchives.Cs, new SnapshotLimits { MaxTotalBytes = 25 }));
        Assert.Contains("add up to more than", total.Message);

        var big = TestArchives.TarGz(("r/Generated.cs", new string('x', 5000)), ("r/Small.cs", "class S {}"));
        var snap = await SnapshotArchive.ReadTarGzAsync(new MemoryStream(big), [], TestArchives.Cs, new SnapshotLimits { MaxFileBytes = 1000 });
        Assert.Equal("Small.cs", Assert.Single(snap).Path);
        Assert.Contains(snap.Skipped, s => s.StartsWith("Generated.cs"));

        var entries = await Assert.ThrowsAsync<GitHostException>(() =>
            SnapshotArchive.ReadTarGzAsync(new MemoryStream(tgz), [], TestArchives.Cs, new SnapshotLimits { MaxEntries = 2 }));
        Assert.Contains("entries", entries.Message);
    }

    [Theory]
    [InlineData("r/../../etc/evil.cs")]
    [InlineData("/abs/evil.cs")]
    [InlineData("r/src/..\\..\\evil.cs")]
    [InlineData("C:/evil.cs")]
    public async Task Path_Traversal_Rejects_The_Archive(string name)
    {
        var tgz = TestArchives.TarGz(("r/ok.cs", "class Ok {}"), (name, "class Evil {}"));
        var ex = await Assert.ThrowsAsync<GitHostException>(() => SnapshotArchive.ReadTarGzAsync(new MemoryStream(tgz), [], TestArchives.Cs, SnapshotLimits.Default));
        Assert.Contains("unsafe path", ex.Message);
    }

    [Fact]
    public void Config_Normalises_And_Validates()
    {
        var c = new CodeRepoConfig { Owner = " acme ", Repo = "sat.git", Branch = "refs/heads/dev", BaseUrl = "https://api.github.com/", Paths = ["./src/", "", "src", "lib\\core"] };
        c.Normalize();
        Assert.Null(c.Validate());
        Assert.Equal(("acme", "sat", "dev", "https://api.github.com"), (c.Owner, c.Repo, c.Branch, c.BaseUrl));
        Assert.Equal(["src", "lib/core"], c.Paths);
        Assert.Equal("sat", c.EffectiveRootPackage);
        Assert.True(CodeRepoConfig.InPaths("src/a.cs", c.Paths));
        Assert.False(CodeRepoConfig.InPaths("srcx/a.cs", c.Paths));

        Assert.NotNull(new CodeRepoConfig { Owner = "a", Repo = "b", BaseUrl = "http://example.com" }.Validate());
        Assert.NotNull(new CodeRepoConfig { Owner = "a", Repo = "b", Paths = ["../x"] }.Validate());
        Assert.NotNull(new CodeRepoConfig { Owner = "a", Repo = "b", Branch = "a..b" }.Validate());
        Assert.NotNull(new CodeRepoConfig { Host = GitHostKind.GitLab, BaseUrl = "https://gitlab.com", Project = "g/../p" }.Validate());
        Assert.Null(new CodeRepoConfig { Host = GitHostKind.GitLab, BaseUrl = "https://gitlab.com", Project = "group/sub/proj" }.Validate());
        Assert.Equal("proj", new CodeRepoConfig { Host = GitHostKind.GitLab, Project = "group/sub/proj" }.EffectiveRootPackage);
        Assert.Contains("\"host\":\"GitLab\"", new CodeRepoConfig { Host = GitHostKind.GitLab }.ToJson());
    }
}

public class CodeWebhookParsingTests
{
    private const string Secret = "webhook-secret-123";

    [Fact]
    public void GitHub_Signature_Valid_Invalid_And_Missing()
    {
        var body = Encoding.UTF8.GetBytes("""{"zen":"Keep it simple."}""");
        var sig = CodeWebhooks.SignGitHub(Secret, body);
        Assert.True(CodeWebhooks.VerifyGitHub(Secret, body, sig));
        Assert.True(CodeWebhooks.VerifyGitHub(Secret, body, sig.ToUpperInvariant().Replace("SHA256=", "sha256=")));
        Assert.False(CodeWebhooks.VerifyGitHub(Secret, body, null));
        Assert.False(CodeWebhooks.VerifyGitHub(Secret, body, ""));
        Assert.False(CodeWebhooks.VerifyGitHub(Secret, body, "sha256=zz"));
        Assert.False(CodeWebhooks.VerifyGitHub(Secret, body, sig[7..]));                          // no prefix
        Assert.False(CodeWebhooks.VerifyGitHub("other-secret", body, sig));
        Assert.False(CodeWebhooks.VerifyGitHub(Secret, Encoding.UTF8.GetBytes("""{"zen":"x"}"""), sig));
        Assert.False(CodeWebhooks.VerifyGitHub("", body, CodeWebhooks.SignGitHub("", body)));      // never accept an empty secret
    }

    [Fact]
    public void GitLab_Token_Valid_Invalid_And_Missing()
    {
        Assert.True(CodeWebhooks.VerifyGitLab(Secret, Secret));
        Assert.False(CodeWebhooks.VerifyGitLab(Secret, Secret + "x"));
        Assert.False(CodeWebhooks.VerifyGitLab(Secret, null));
        Assert.False(CodeWebhooks.VerifyGitLab(Secret, ""));
        Assert.False(CodeWebhooks.VerifyGitLab("", ""));
    }

    public const string GitHubPush = """
        {"ref":"refs/heads/main","before":"1111111111111111111111111111111111111111","after":"a1b2c3d4e5f60718293a4b5c6d7e8f9012345678",
         "deleted":false,"compare":"https://github.com/acme/sat/compare/1...a",
         "repository":{"full_name":"acme/sat"},"pusher":{"name":"ann"},
         "head_commit":{"id":"a1b2c3d4e5f60718293a4b5c6d7e8f9012345678","message":"Add Battery.Capacity","url":"https://github.com/acme/sat/commit/a1b2c3d","author":{"name":"Ann Lee","email":"ann@acme.test"}}}
        """;

    public const string GitLabPush = """
        {"object_kind":"push","ref":"refs/heads/main","before":"1111111111111111111111111111111111111111","after":"b1b2c3d4e5f60718293a4b5c6d7e8f9012345678",
         "checkout_sha":"b1b2c3d4e5f60718293a4b5c6d7e8f9012345678","user_name":"Ben",
         "project":{"path_with_namespace":"acme/sat"},
         "commits":[{"id":"0000aaaa","message":"older","url":"u0","author":{"name":"X"}},
                    {"id":"b1b2c3d4e5f60718293a4b5c6d7e8f9012345678","message":"Rename Panel","url":"https://gitlab.com/acme/sat/-/commit/b1b2c3d","author":{"name":"Ben Ode"}}]}
        """;

    [Fact]
    public void Parses_GitHub_And_GitLab_Push_Payloads()
    {
        var gh = CodeWebhooks.ParseGitHubPush(GitHubPush)!;
        Assert.Equal(new PushEvent("refs/heads/main", "main", "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678", false, "Add Battery.Capacity", "Ann Lee", "https://github.com/acme/sat/commit/a1b2c3d"), gh);

        var gl = CodeWebhooks.ParseGitLabPush(GitLabPush)!;
        Assert.Equal(new PushEvent("refs/heads/main", "main", "b1b2c3d4e5f60718293a4b5c6d7e8f9012345678", false, "Rename Panel", "Ben Ode", "https://gitlab.com/acme/sat/-/commit/b1b2c3d"), gl);

        var tag = CodeWebhooks.ParseGitHubPush("""{"ref":"refs/tags/v1","after":"a1b2c3d4e5f60718293a4b5c6d7e8f9012345678"}""")!;
        Assert.Null(tag.Branch);
        Assert.True(CodeWebhooks.ParseGitHubPush("""{"ref":"refs/heads/main","after":"0000000000000000000000000000000000000000","deleted":true}""")!.Deleted);
        Assert.True(CodeWebhooks.ParseGitLabPush("""{"ref":"refs/heads/main","after":"0000000000000000000000000000000000000000","checkout_sha":null}""")!.Deleted);
        Assert.Null(CodeWebhooks.ParseGitHubPush("not json"));
        Assert.Null(CodeWebhooks.ParseGitHubPush("""{"zen":"hi"}"""));

        Assert.Equal(WebhookEventKind.Ping, CodeWebhooks.GitHubEvent("ping"));
        Assert.Equal(WebhookEventKind.Push, CodeWebhooks.GitHubEvent("push"));
        Assert.Equal(WebhookEventKind.Other, CodeWebhooks.GitHubEvent("issues"));
        Assert.Equal(WebhookEventKind.Push, CodeWebhooks.GitLabEvent("Push Hook"));
        Assert.Equal(WebhookEventKind.Other, CodeWebhooks.GitLabEvent("Tag Push Hook"));
    }

    [Fact]
    public void Queue_Coalesces_Pushes_Per_Connection_To_The_Latest_Commit()
    {
        var q = new CodeSyncQueue(capacity: 2);
        Guid p = Guid.NewGuid(), a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        CodeSyncJob Job(Guid conn, string sha) => new(p, conn, "main", sha, null, null, null, "webhook");

        Assert.True(q.TryEnqueue(Job(a, "aaaaaaa1")));
        Assert.True(q.TryEnqueue(Job(b, "bbbbbbb1")));
        Assert.True(q.TryEnqueue(Job(a, "aaaaaaa2")));
        Assert.True(q.TryEnqueue(Job(a, "aaaaaaa3")));
        Assert.False(q.TryEnqueue(Job(c, "ccccccc1")));   // bounded: two connections already waiting
        Assert.Equal(2, q.Count);

        Assert.True(q.TryDequeue(out var first));
        Assert.Equal((a, "aaaaaaa3"), (first!.ConnectionId, first.Commit));
        Assert.True(q.TryDequeue(out var second));
        Assert.Equal((b, "bbbbbbb1"), (second!.ConnectionId, second.Commit));
        Assert.False(q.TryDequeue(out _));

        Assert.True(q.TryEnqueue(Job(a, "aaaaaaa4")));      // a new push after the job was taken queues again
        Assert.True(q.TryDequeue(out var third));
        Assert.Equal("aaaaaaa4", third!.Commit);
    }

    [Fact]
    public void Rate_Limiter_Is_Per_Connection()
    {
        using var limiter = new CodeSyncRateLimiter(2, TimeSpan.FromMinutes(1));
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        Assert.True(limiter.TryAcquire(a));
        Assert.True(limiter.TryAcquire(a));
        Assert.False(limiter.TryAcquire(a));
        Assert.True(limiter.TryAcquire(b));
    }
}

/// <summary>Webhook handling and the sync pipeline over a temporary SQLite store and a scripted HTTP handler.</summary>
public sealed class CodeSyncHostServiceTests : IAsyncLifetime
{
    private const string Sha = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";
    private const string Token = "ghp_super_secret_token";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-web-cs-{Guid.NewGuid():N}.db");
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

    private sealed class FakeRunner(CodeSyncOutcome outcome) : ICodeSyncRunner
    {
        public List<CodeSyncRequest> Requests { get; } = new();

        public Task<CodeSyncOutcome> RunAsync(CodeSyncRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(outcome);
        }
    }

    private (CodeSyncHostService Service, CodeSyncQueue Queue) Create(FakeHandler handler, ICodeSyncRunner? runner = null, int permits = 30)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICodeSyncRunner, NullCodeSyncRunner>();   // the stand-in, registered first…
        if (runner != null) services.AddSingleton(runner);               // …loses to a real runner
        var sp = services.BuildServiceProvider();
        var factory = new FakeHttpClientFactory(handler);
        var integrations = new IntegrationService(_db, new EphemeralDataProtectionProvider(), factory);
        var queue = new CodeSyncQueue();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Meridian:PublicUrl"] = "https://meridian.test/" }).Build();
        var svc = new CodeSyncHostService(_db, integrations, factory, sp.GetRequiredService<IServiceScopeFactory>(), queue,
            new CodeSyncRateLimiter(permits), config, NullLogger<CodeSyncHostService>.Instance);
        return (svc, queue);
    }

    private static CodeRepoConfig GitHub(bool autoSync = true) => new() { Host = GitHostKind.GitHub, BaseUrl = "https://api.github.com", Owner = "acme", Repo = "sat", Branch = "main", Paths = ["src"], AutoSync = autoSync };
    private static CodeRepoConfig GitLab() => new() { Host = GitHostKind.GitLab, BaseUrl = "https://gitlab.example.com", Project = "acme/sat", Branch = "main" };

    private static HeaderDictionary Headers(params (string, string)[] h)
    {
        var d = new HeaderDictionary();
        foreach (var (k, v) in h) d[k] = v;
        return d;
    }

    [Fact]
    public async Task Saving_Generates_A_Webhook_Secret_Once_And_Encrypts_Both_Secrets()
    {
        var (svc, _) = Create(new FakeHandler((_, _) => FakeHandler.Json("{}")));
        var saved = await svc.SaveAsync(_project, null, null, GitHub(), Token);
        Assert.NotNull(saved.NewWebhookSecret);
        Assert.Equal("acme/sat", saved.Connection.Name);
        var row = (await _db.ListIntegrationsAsync(_project)).Single();
        Assert.Equal(CodeRepoKinds.CodeRepo, row.Kind);
        Assert.DoesNotContain(Token, row.ProtectedSecret);
        Assert.DoesNotContain(Token, row.ConfigJson);
        Assert.DoesNotContain(saved.NewWebhookSecret!, row.ConfigJson);

        var again = await svc.SaveAsync(_project, saved.Connection.Id, "Satellite", GitHub(), "");
        Assert.Null(again.NewWebhookSecret);   // kept, not shown again
        Assert.Equal(CodeRepoConfig.Parse(row.ConfigJson).ProtectedWebhookSecret, CodeRepoConfig.Parse(again.Connection.ConfigJson).ProtectedWebhookSecret);
        Assert.Equal(row.ProtectedSecret, again.Connection.ProtectedSecret);
        await Assert.ThrowsAsync<GitHostException>(() => svc.SaveAsync(_project, null, null, GitHub(), null));   // a new connection needs a token
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SaveAsync(Guid.NewGuid(), saved.Connection.Id, null, GitHub(), null));
    }

    [Fact]
    public async Task GitHub_Webhook_Checks_The_Signature_Event_And_Branch_Before_Queueing()
    {
        var (svc, queue) = Create(new FakeHandler((_, _) => FakeHandler.Json("{}")));
        var saved = await svc.SaveAsync(_project, null, null, GitHub(), Token);
        var secret = saved.NewWebhookSecret!;
        var id = saved.Connection.Id;
        var body = Encoding.UTF8.GetBytes(CodeWebhookParsingTests.GitHubPush);
        var sig = CodeWebhooks.SignGitHub(secret, body);

        Assert.Equal(404, (await svc.HandleWebhookAsync(Guid.NewGuid(), Headers(), body)).StatusCode);
        Assert.Equal(401, (await svc.HandleWebhookAsync(id, Headers(("X-GitHub-Event", "push")), body)).StatusCode);
        Assert.Equal(401, (await svc.HandleWebhookAsync(id, Headers(("X-GitHub-Event", "push"), ("X-Hub-Signature-256", CodeWebhooks.SignGitHub("wrong", body))), body)).StatusCode);
        Assert.Equal(0, queue.Count);

        var ping = Encoding.UTF8.GetBytes("""{"zen":"hi","hook_id":1}""");
        Assert.Equal(200, (await svc.HandleWebhookAsync(id, Headers(("X-GitHub-Event", "ping"), ("X-Hub-Signature-256", CodeWebhooks.SignGitHub(secret, ping))), ping)).StatusCode);
        var other = await svc.HandleWebhookAsync(id, Headers(("X-GitHub-Event", "issues"), ("X-Hub-Signature-256", CodeWebhooks.SignGitHub(secret, ping))), ping);
        Assert.Equal((202, true), (other.StatusCode, other.Message.StartsWith("Ignored")));

        var devPush = Encoding.UTF8.GetBytes(CodeWebhookParsingTests.GitHubPush.Replace("refs/heads/main", "refs/heads/dev"));
        var dev = await svc.HandleWebhookAsync(id, Headers(("X-GitHub-Event", "push"), ("X-Hub-Signature-256", CodeWebhooks.SignGitHub(secret, devPush))), devPush);
        Assert.Equal(202, dev.StatusCode);
        Assert.Contains("follows main", dev.Message);
        Assert.Equal(0, queue.Count);

        var ok = await svc.HandleWebhookAsync(id, Headers(("X-GitHub-Event", "push"), ("X-Hub-Signature-256", sig)), body);
        Assert.Equal(202, ok.StatusCode);
        Assert.StartsWith("Queued", ok.Message);
        Assert.True(queue.TryDequeue(out var job));
        Assert.Equal((id, Sha, "Add Battery.Capacity", "Ann Lee", "webhook"), (job!.ConnectionId, job.Commit, job.Message, job.Author, job.Trigger));
        Assert.Equal("queued", CodeRepoConfig.Parse((await _db.FindIntegrationAsync(id))!.ConfigJson).LastSync!.Status);
    }

    [Fact]
    public async Task GitLab_Webhook_Checks_The_Token_And_Respects_Auto_Sync_And_Rate_Limit()
    {
        var (svc, queue) = Create(new FakeHandler((_, _) => FakeHandler.Json("{}")), permits: 2);
        var saved = await svc.SaveAsync(_project, null, "GitLab", GitLab(), "glpat-xyz");
        var id = saved.Connection.Id;
        var body = Encoding.UTF8.GetBytes(CodeWebhookParsingTests.GitLabPush);

        Assert.Equal(401, (await svc.HandleWebhookAsync(id, Headers(("X-Gitlab-Event", "Push Hook")), body)).StatusCode);
        Assert.Equal(401, (await svc.HandleWebhookAsync(id, Headers(("X-Gitlab-Event", "Push Hook"), ("X-Gitlab-Token", "nope")), body)).StatusCode);
        var good = Headers(("X-Gitlab-Event", "Push Hook"), ("X-Gitlab-Token", saved.NewWebhookSecret!));
        Assert.Equal(202, (await svc.HandleWebhookAsync(id, good, body)).StatusCode);
        Assert.True(queue.TryDequeue(out var job));
        Assert.Equal(("b1b2c3d4e5f60718293a4b5c6d7e8f9012345678", "Rename Panel"), (job!.Commit, job.Message));

        var manual = GitLab();
        manual.AutoSync = false;
        await svc.SaveAsync(_project, id, "GitLab", manual, null);
        var off = await svc.HandleWebhookAsync(id, good, body);
        Assert.Equal(202, off.StatusCode);
        Assert.Contains("automatic sync is off", off.Message);
        Assert.Equal(0, queue.Count);

        Assert.Equal(429, (await svc.HandleWebhookAsync(id, good, body)).StatusCode);   // 2 permits used by verified requests
    }

    [Fact]
    public async Task Pipeline_Posts_Pending_Then_Success_Status_And_Hands_A_Full_Snapshot_To_The_Runner()
    {
        var tgz = TestArchives.TarGz(("acme-sat-a1b2c3d/src/Battery.cs", "namespace Sat; public class Battery {}"), ("acme-sat-a1b2c3d/docs/x.cs", "class X {}"), ("acme-sat-a1b2c3d/src/notes.txt", "n"));
        var handler = new FakeHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url == $"https://api.github.com/repos/acme/sat/tarball/{Sha}") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tgz) };
            if (url == "https://api.github.com/repos/acme/sat/commits/main") return FakeHandler.Json(new JsonObject
            {
                ["sha"] = Sha, ["html_url"] = "https://github.com/acme/sat/commit/a1b2",
                ["commit"] = new JsonObject { ["message"] = "Head", ["author"] = new JsonObject { ["name"] = "Ann" } },
            }.ToJsonString());
            if (url.StartsWith("https://api.github.com/repos/acme/sat/statuses/")) return FakeHandler.Json("{}", HttpStatusCode.Created);
            return FakeHandler.Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound);
        });
        var runner = new FakeRunner(new CodeSyncOutcome(CodeSyncStatus.AwaitingReview, "2 added, 1 changed — review the code-sync branch", "/review/run-1", Guid.NewGuid()));
        var (svc, queue) = Create(handler, runner);
        var saved = await svc.SaveAsync(_project, null, null, GitHub(), Token);

        var msg = await svc.SyncNowAsync(_project, saved.Connection.Id);
        Assert.Contains("a1b2c3d", msg);
        Assert.True(queue.TryDequeue(out var job));
        var outcome = await svc.ProcessAsync(job!);

        Assert.Equal(CodeSyncStatus.AwaitingReview, outcome.Status);
        var request = Assert.Single(runner.Requests);
        Assert.True(request.IsFullSnapshot);
        Assert.Equal((_project, saved.Connection.Id, "acme/sat", "main", Sha, "Head", "Ann"), (request.ProjectId, request.ConnectionId, request.RepoDisplay, request.Branch, request.Commit, request.Message, request.Author));
        Assert.Equal("src/Battery.cs", Assert.Single(request.Files).Path);

        var statuses = handler.Requests.Where(r => r.Request.Method == HttpMethod.Post).Select(r => JsonNode.Parse(r.Body!)!).ToList();
        Assert.Equal(2, statuses.Count);
        Assert.Equal(("pending", "meridian/model"), ((string)statuses[0]["state"]!, (string)statuses[0]["context"]!));
        Assert.Equal("success", (string)statuses[1]["state"]!);
        Assert.Equal("https://meridian.test/review/run-1", (string)statuses[1]["target_url"]!);
        Assert.StartsWith("2 added", (string)statuses[1]["description"]!);
        Assert.All(handler.Requests, r => Assert.Equal(("Bearer", Token), (r.Request.Headers.Authorization!.Scheme, r.Request.Headers.Authorization.Parameter)));

        var last = CodeRepoConfig.Parse((await _db.FindIntegrationAsync(saved.Connection.Id))!.ConfigJson).LastSync!;
        Assert.Equal(("awaiting-review", Sha), (last.Status, last.Commit));
    }

    [Fact]
    public async Task Pipeline_Reports_Failure_To_GitLab_Without_A_Runner_And_On_Fetch_Errors()
    {
        var tgz = TestArchives.TarGz(("sat-b1b2/Panel.cs", "class Panel {}"));
        var fail = false;
        var handler = new FakeHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/repository/archive.tar.gz"))
                return fail ? FakeHandler.Json("""{"message":"404 Commit Not Found"}""", HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tgz) };
            if (url.Contains("/statuses/")) return FakeHandler.Json("{}", HttpStatusCode.Created);
            return FakeHandler.Json("{}", HttpStatusCode.NotFound);
        });
        var (svc, _) = Create(handler);   // no real runner registered
        var saved = await svc.SaveAsync(_project, null, null, GitLab(), "glpat-xyz");
        var job = new CodeSyncJob(_project, saved.Connection.Id, "main", Sha, null, null, null, "webhook");

        var outcome = await svc.ProcessAsync(job);
        Assert.Equal((CodeSyncStatus.Failed, "Code sync runner not available"), (outcome.Status, outcome.Summary));
        var archive = handler.Requests.First(r => r.Request.RequestUri!.ToString().Contains("archive"));
        Assert.Equal($"https://gitlab.example.com/api/v4/projects/acme%2Fsat/repository/archive.tar.gz?sha={Sha}", archive.Request.RequestUri!.AbsoluteUri);
        Assert.Equal("glpat-xyz", archive.Request.Headers.GetValues("PRIVATE-TOKEN").Single());
        var posts = handler.Requests.Where(r => r.Request.Method == HttpMethod.Post).ToList();
        Assert.Equal($"https://gitlab.example.com/api/v4/projects/acme%2Fsat/statuses/{Sha}", posts[0].Request.RequestUri!.AbsoluteUri);
        Assert.Equal(["pending", "failed"], posts.Select(p => (string)JsonNode.Parse(p.Body!)!["state"]!));
        Assert.Equal("meridian/model", (string)JsonNode.Parse(posts[1].Body!)!["name"]!);

        fail = true;
        handler.Requests.Clear();
        var notFound = await svc.ProcessAsync(job);
        Assert.Equal(CodeSyncStatus.Failed, notFound.Status);
        Assert.Contains("not found", notFound.Summary);
        Assert.DoesNotContain("glpat", notFound.Summary);
        Assert.Equal("failed", (string)JsonNode.Parse(handler.Requests.Last(r => r.Request.Method == HttpMethod.Post).Body!)!["state"]!);
    }

    [Fact]
    public async Task Status_Posting_Is_Skipped_When_Turned_Off_And_Test_Connection_Reports_The_Head()
    {
        var handler = new FakeHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.EndsWith("/repos/acme/sat")) return FakeHandler.Json("""{"full_name":"acme/sat","permissions":{"push":false}}""");
            if (url.EndsWith("/commits/main")) return FakeHandler.Json($$"""{"sha":"{{Sha}}"}""");
            if (url.Contains("/tarball/")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestArchives.TarGz(("r/src/A.cs", "class A {}"))) };
            return FakeHandler.Json("{}", HttpStatusCode.Created);
        });
        var runner = new FakeRunner(new CodeSyncOutcome(CodeSyncStatus.NoChanges, "No model changes", null, null));
        var (svc, _) = Create(handler, runner);
        var test = await svc.TestAsync(_project, GitHub(), Token, null);
        Assert.Contains("Connected to acme/sat: main is at a1b2c3d.", test);
        Assert.Contains("cannot post commit statuses", test);
        await Assert.ThrowsAsync<GitHostException>(() => svc.TestAsync(_project, GitHub(), null, null));

        var cfg = GitHub();
        cfg.PostStatus = false;
        var saved = await svc.SaveAsync(_project, null, null, cfg, Token);
        handler.Requests.Clear();
        var outcome = await svc.ProcessAsync(new CodeSyncJob(_project, saved.Connection.Id, "main", Sha, null, null, null, "manual"));
        Assert.Equal(CodeSyncStatus.NoChanges, outcome.Status);
        Assert.DoesNotContain(handler.Requests, r => r.Request.Method == HttpMethod.Post);
    }
}
