using System.Net;
using System.Text.Json.Nodes;
using Meridian.Core.CodeSync;
using Meridian.Web.CodeSync.Hosts;

namespace Meridian.Web.Tests;

/// <summary>.gitmodules parsing and submodule URL resolution.</summary>
public class GitModulesTests
{
    [Fact]
    public void Parses_Sections_Quoting_Comments_And_Update_None()
    {
        var text = """
            # top comment
            [submodule "libs/core"]
            	path = libs/core
            	url = ../core.git   ; trailing comment
            	branch = main
            [submodule "vendor \"big\""]
            	path = "vendor/big lib"
            	url = "https://github.com/acme/big.git" # comment after quotes
            	update = none
            [core]
            	path = not/a/submodule
            [submodule.old-style]
            	PATH = old
            	URL = git@gitlab.com:team/old.git
            [submodule "no-url"]
            	path = ./tools/x/
            [submodule "dup"]
            	path = libs/core
            	url = ../other.git
            [submodule "broken"
            	path = ignored
            garbage line without equals!
            [submodule "cont"]
            	path = long
            	url = https://github.com/\
            acme/long.git
            """;
        var e = GitModules.Parse(text);

        Assert.Equal(["libs/core", "vendor/big lib", "old", "tools/x", "long"], e.Select(x => x.Path));
        Assert.Equal(("libs/core", "../core.git", "main", false), (e[0].Name, e[0].Url, e[0].Branch, e[0].UpdateNone));
        Assert.Equal(("vendor \"big\"", "https://github.com/acme/big.git", true), (e[1].Name, e[1].Url, e[1].UpdateNone));
        Assert.Equal(("old-style", "git@gitlab.com:team/old.git"), (e[2].Name, e[2].Url));
        Assert.Null(e[3].Url);
        Assert.Equal("https://github.com/acme/long.git", e[4].Url);
        Assert.Empty(GitModules.Parse(null));
        Assert.Empty(GitModules.Parse("[[[\n=\n"));
    }

    [Theory]
    [InlineData("https://github.com/acme/core.git", "github.com/acme/core")]
    [InlineData("https://user:s3cret@github.com/acme/core", "github.com/acme/core")]
    [InlineData("git@github.com:acme/core.git", "github.com/acme/core")]
    [InlineData("ssh://git@ssh.github.com:443/acme/core.git", "github.com/acme/core")]
    [InlineData("git@gitlab.com:group/sub/proj.git", "gitlab.com/group/sub/proj")]
    [InlineData("https://bitbucket.example.com/scm/PROJ/repo.git", "bitbucket.example.com/PROJ/repo")]
    [InlineData("ssh://git@bitbucket.example.com:7999/proj/repo.git", "bitbucket.example.com/proj/repo")]
    [InlineData("git@bitbucket.org:team/lib.git", "bitbucket.org/team/lib")]
    [InlineData("../core.git", "github.com/acme/core")]
    [InlineData("../../other/lib", "github.com/other/lib")]
    [InlineData("./nested", "github.com/acme/app/nested")]
    public void Resolves_Absolute_ScpLike_And_Relative_Urls(string url, string expected)
    {
        var parent = new GitRepoLocation("github.com", "acme/app");
        Assert.Equal(expected, GitRepoLocation.Resolve(url, parent)?.ToString());
    }

    [Theory]
    [InlineData("/srv/git/core.git")]
    [InlineData("file:///srv/git/core.git")]
    [InlineData("../../../too-far.git")]
    [InlineData("https://github.com/only-owner")]
    [InlineData("https://github.com/acme/..%2F..%2Fetc")]
    [InlineData("")]
    public void Rejects_Local_And_Malformed_Urls(string url) =>
        Assert.Null(GitRepoLocation.Resolve(url, new GitRepoLocation("github.com", "acme/app")));

    [Fact]
    public void Locations_Of_Connections_And_Redaction()
    {
        Assert.Equal("github.com/acme/app", GitRepoLocation.Of(new CodeRepoConfig { Owner = "acme", Repo = "app" })!.ToString());
        Assert.Equal("ghe.example.com/acme/app", GitRepoLocation.Of(new CodeRepoConfig { BaseUrl = "https://ghe.example.com/api/v3", Owner = "acme", Repo = "app" })!.ToString());
        Assert.Equal("bitbucket.org/team/app", GitRepoLocation.Of(new CodeRepoConfig { Host = GitHostKind.Bitbucket, BaseUrl = CodeRepoConfig.BitbucketCloudApi, Owner = "team", Repo = "app" })!.ToString());
        Assert.Equal("gitlab.example.com/g/sub/app", GitRepoLocation.Of(new CodeRepoConfig { Host = GitHostKind.GitLab, BaseUrl = "https://gitlab.example.com", Project = "g/sub/app" })!.ToString());
        Assert.Null(GitRepoLocation.Of(new CodeRepoConfig { Host = GitHostKind.GitLab, BaseUrl = "https://gitlab.com", Project = "1234" }));

        Assert.DoesNotContain("s3cret", GitRepoLocation.Redact("https://ann:s3cret@github.com/acme/core.git"));
        Assert.Equal("git@github.com:acme/core.git", GitRepoLocation.Redact("git@github.com:acme/core.git"));
    }

    [Fact]
    public void Pins_Round_Trip_And_Bumps_Have_Compare_Links()
    {
        var a = "1111111111111111111111111111111111111111";
        var b = "2222222222222222222222222222222222222222";
        var before = CodeSubmodulePins.Format([new("libs/core", a, "https://github.com/acme/core/compare/{old}...{new}"), new("gone", a)]);
        var after = CodeSubmodulePins.Format([new("libs/core", b, "https://github.com/acme/core/compare/{old}...{new}"), new("added", b)]);
        Assert.Equal($"gone@{a}\nlibs/core@{a} <https://github.com/acme/core/compare/{{old}}...{{new}}>", before);
        Assert.Equal(2, CodeSubmodulePins.Parse(before).Count);

        var bumps = CodeSubmodulePins.Bumps(before, after);
        Assert.Equal(
        [
            new CodeSubmoduleBump("added", null, b, null),
            new CodeSubmoduleBump("gone", a, null, null),
            new CodeSubmoduleBump("libs/core", a, b, $"https://github.com/acme/core/compare/{a}...{b}"),
        ], bumps);
        Assert.Empty(CodeSubmodulePins.Bumps(after, after));
        Assert.Null(CodeSubmodulePins.Format([]));
        Assert.Equal("libs/core/x", CodeSubmodulePins.Containing([new("libs/core", a), new("libs/core/x", b)], "libs/core/x/A.cs")!.Path);
        Assert.Null(CodeSubmodulePins.Containing([new("libs/core", a)], "libs/corex/A.cs"));

        Assert.Equal("https://gitlab.com/g/p/-/compare/{old}...{new}", SubmoduleFetcher.CompareUrl(new("gitlab.com", "g/p"), GitHostKind.GitLab));
        Assert.Equal("https://bitbucket.org/t/r/branches/compare/{new}%0D{old}", SubmoduleFetcher.CompareUrl(new("bitbucket.org", "t/r"), GitHostKind.Bitbucket));
        Assert.Contains("/projects/P/repos/r/compare/commits", SubmoduleFetcher.CompareUrl(new("bb.example.com", "P/r"), GitHostKind.Bitbucket));
        Assert.Null(SubmoduleFetcher.CompareUrl(new("git.example.com", "a/b"), null));
    }

    [Fact]
    public void Folders_Inside_A_Submodule_Follow_The_Connections_Paths()
    {
        Assert.Empty(SubmoduleFetcher.PathsInside("libs/core", [])!);
        Assert.Empty(SubmoduleFetcher.PathsInside("libs/core", ["libs"])!);
        Assert.Equal(["src", "api"], SubmoduleFetcher.PathsInside("libs/core", ["src", "libs/core/src", "libs/core/api"]));
        Assert.Null(SubmoduleFetcher.PathsInside("libs/core", ["src", "libs/corex"]));
    }
}

/// <summary>Gitlink (pinned commit) resolution through each host's API, over a scripted handler.</summary>
public class SubmoduleGitlinkTests
{
    private const string Parent = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";
    private const string Sub1 = "1111111111111111111111111111111111111111";
    private const string Sub2 = "2222222222222222222222222222222222222222";

    private static IGitHost Host(CodeRepoConfig c, FakeHandler h, string token = "tok") => GitHostFactory.Create(c, token, new HttpClient(h));

    [Fact]
    public async Task GitHub_Reads_The_Contents_Api()
    {
        var h = new FakeHandler((req, _) => req.RequestUri!.AbsoluteUri switch
        {
            $"https://api.github.com/repos/acme/app/contents/libs/core?ref={Parent}" => FakeHandler.Json($$"""{"type":"submodule","sha":"{{Sub1}}","path":"libs/core"}"""),
            $"https://api.github.com/repos/acme/app/contents/libs/big%20lib?ref={Parent}" => FakeHandler.Json($$"""{"type":"submodule","sha":"{{Sub2}}"}"""),
            $"https://api.github.com/repos/acme/app/contents/docs?ref={Parent}" => FakeHandler.Json("""[{"type":"file"}]"""),
            _ => FakeHandler.Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound),
        });
        var shas = await Host(new CodeRepoConfig { Owner = "acme", Repo = "app" }, h).ResolveSubmoduleCommitsAsync(Parent, ["libs/core", "libs/big lib", "docs", "missing"]);
        Assert.Equal(new Dictionary<string, string> { ["libs/core"] = Sub1, ["libs/big lib"] = Sub2 }, shas);
        Assert.All(h.Requests, r => Assert.Equal("tok", r.Request.Headers.Authorization!.Parameter));
    }

    [Fact]
    public async Task GitLab_Reads_The_Repository_Tree_Of_Each_Folder()
    {
        var h = new FakeHandler((req, _) => req.RequestUri!.AbsoluteUri switch
        {
            $"https://gitlab.example.com/api/v4/projects/g%2Fapp/repository/tree?ref={Parent}&per_page=100&page=1&path=libs" =>
                FakeHandler.Json($$"""[{"id":"{{Sub1}}","name":"core","type":"commit","path":"libs/core"},{"id":"x","name":"README","type":"blob","path":"libs/README"}]"""),
            $"https://gitlab.example.com/api/v4/projects/g%2Fapp/repository/tree?ref={Parent}&per_page=100&page=1" =>
                FakeHandler.Json($$"""[{"id":"{{Sub2}}","name":"tools","type":"commit","path":"tools"},{"id":"{{Sub1}}","name":"src","type":"tree","path":"src"}]"""),
            _ => FakeHandler.Json("{}", HttpStatusCode.NotFound),
        });
        var host = Host(new CodeRepoConfig { Host = GitHostKind.GitLab, BaseUrl = "https://gitlab.example.com", Project = "g/app" }, h);
        var shas = await host.ResolveSubmoduleCommitsAsync(Parent, ["libs/core", "tools", "src"]);
        Assert.Equal(new Dictionary<string, string> { ["libs/core"] = Sub1, ["tools"] = Sub2 }, shas);
        Assert.All(h.Requests, r => Assert.Equal("tok", r.Request.Headers.GetValues("PRIVATE-TOKEN").Single()));
    }

    [Fact]
    public async Task Bitbucket_Cloud_Reads_The_Source_Listing_And_Follows_Same_Host_Pages_Only()
    {
        var h = new FakeHandler((req, _) => req.RequestUri!.AbsoluteUri switch
        {
            $"https://api.bitbucket.org/2.0/repositories/team/app/src/{Parent}/libs/?pagelen=100" => FakeHandler.Json($$$"""
                {"values":[{"type":"commit_directory","path":"libs/plain","commit":{"hash":"{{{Parent}}}"}},
                           {"type":"commit_file","path":"libs/a.cs","commit":{"hash":"{{{Sub2}}}"}}],
                 "next":"https://api.bitbucket.org/2.0/repositories/team/app/src/{{{Parent}}}/libs/?pagelen=100&page=2"}
                """),
            $"https://api.bitbucket.org/2.0/repositories/team/app/src/{Parent}/libs/?pagelen=100&page=2" => FakeHandler.Json($$$"""
                {"values":[{"type":"commit_directory","path":"libs/core","commit":{"hash":"{{{Sub1}}}"}}],"next":"https://evil.example/steal"}
                """),
            $"https://api.bitbucket.org/2.0/repositories/team/app/src/{Parent}/?pagelen=100" => FakeHandler.Json($$$"""
                {"values":[{"type":"submodule","path":"tools","commit":{"hash":"{{{Sub2}}}"}}]}
                """),
            _ => FakeHandler.Json("{}", HttpStatusCode.NotFound),
        });
        var host = Host(new CodeRepoConfig { Host = GitHostKind.Bitbucket, BaseUrl = CodeRepoConfig.BitbucketCloudApi, Owner = "team", Repo = "app" }, h);
        var shas = await host.ResolveSubmoduleCommitsAsync(Parent, ["libs/core", "libs/plain", "libs/x", "tools"]);
        Assert.Equal(new Dictionary<string, string> { ["libs/core"] = Sub1, ["tools"] = Sub2 }, shas);
        Assert.DoesNotContain(h.Requests, r => r.Request.RequestUri!.Host == "evil.example");
    }

    [Fact]
    public async Task Bitbucket_Data_Center_Reads_The_Browse_Api()
    {
        var h = new FakeHandler((req, _) => req.RequestUri!.AbsoluteUri switch
        {
            $"https://bb.example.com/rest/api/1.0/projects/PROJ/repos/app/browse/libs?at={Parent}&limit=500&start=0" => FakeHandler.Json($$$"""
                {"children":{"values":[{"path":{"toString":"core","name":"core"},"type":"SUBMODULE","contentId":"{{{Sub1}}}"},
                                       {"path":{"toString":"other","name":"other"},"type":"DIRECTORY"}],"isLastPage":true}}
                """),
            _ => FakeHandler.Json("{}", HttpStatusCode.NotFound),
        });
        var host = Host(new CodeRepoConfig { Host = GitHostKind.Bitbucket, BaseUrl = "https://bb.example.com", Owner = "PROJ", Repo = "app" }, h);
        Assert.Equal(new Dictionary<string, string> { ["libs/core"] = Sub1 }, await host.ResolveSubmoduleCommitsAsync(Parent, ["libs/core", "libs/other"]));
        Assert.All(h.Requests, r => Assert.Equal(("Bearer", "tok"), (r.Request.Headers.Authorization!.Scheme, r.Request.Headers.Authorization.Parameter)));
    }
}

/// <summary>
/// The merged snapshot: discovery, credentials (and where tokens go), nesting, cycles, limits over the whole snapshot
/// and warnings that let the sync continue. GitHub, GitLab and Bitbucket over one scripted handler; no network.
/// </summary>
public class SubmoduleFetcherTests
{
    public const string Parent = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";
    public const string CoreSha = "1111111111111111111111111111111111111111";
    public const string UtilSha = "2222222222222222222222222222222222222222";
    public const string LibSha = "3333333333333333333333333333333333333333";
    public const string PubSha = "4444444444444444444444444444444444444444";
    private const string Token = "ghp_parent_token";

    /// <summary>A fake set of hosts: archives and gitlinks by URL.</summary>
    public sealed class FakeHosts
    {
        public readonly Dictionary<string, byte[]> Archives = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Json = new(StringComparer.Ordinal);
        /// <summary>URL → required credential (Authorization parameter or PRIVATE-TOKEN); others get 404.</summary>
        public readonly Dictionary<string, string?> Requires = new(StringComparer.Ordinal);
        public readonly FakeHandler Handler;

        public FakeHosts() => Handler = new FakeHandler((req, _) => Respond(req));

        public HttpResponseMessage Respond(HttpRequestMessage req)
        {
            var url = req.RequestUri!.AbsoluteUri;
            var cred = req.Headers.Authorization?.Parameter ?? (req.Headers.TryGetValues("PRIVATE-TOKEN", out var v) ? v.Single() : null);
            if (Requires.TryGetValue(url, out var need) && need != cred) return FakeHandler.Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound);
            if (Archives.TryGetValue(url, out var tgz)) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tgz) };
            if (Json.TryGetValue(url, out var json)) return FakeHandler.Json(json);
            return FakeHandler.Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound);
        }

        public void GitHubRepo(string repo, string sha, params (string Name, string? Content)[] files) =>
            Archives[$"https://api.github.com/repos/{repo}/tarball/{sha}"] = TestArchives.TarGz(files.Select(f => ($"{repo.Replace('/', '-')}-{sha[..7]}/{f.Name}", f.Content)).ToArray());

        public void GitHubLink(string repo, string sha, string path, string sub) =>
            Json[$"https://api.github.com/repos/{repo}/contents/{path}?ref={sha}"] = $$"""{"type":"submodule","sha":"{{sub}}"}""";

        public SubmoduleFetcher Fetcher() => new((c, t) => GitHostFactory.Create(c, t, new HttpClient(Handler), Limits));
        public SnapshotLimits Limits { get; set; } = SnapshotLimits.Default;
    }

    public static CodeRepoConfig App(params string[] paths) => new() { Owner = "acme", Repo = "app", Branch = "main", Paths = [.. paths] };

    private static Task<SubmoduleSnapshot> Fetch(FakeHosts h, CodeRepoConfig config, params SubmoduleCredential[] others) =>
        h.Fetcher().FetchAsync(GitHostFactory.Create(config, Token, new HttpClient(h.Handler), h.Limits), config, Token, Parent, TestArchives.Cs, others, new SnapshotBudget(h.Limits));

    [Fact]
    public async Task Nested_Submodules_Are_Merged_Under_Their_Paths_With_Cycles_Cut()
    {
        var h = new FakeHosts();
        h.GitHubRepo("acme/app", Parent, ("src/App.cs", "class App {}"), (".gitmodules", """
            [submodule "core"]
            	path = libs/core
            	url = ../core.git
            [submodule "skipped"]
            	path = vendor/skip
            	url = https://github.com/acme/skip.git
            	update = none
            """));
        h.GitHubLink("acme/app", Parent, "libs/core", CoreSha);
        h.GitHubLink("acme/app", Parent, "vendor/skip", PubSha);
        h.GitHubRepo("acme/core", CoreSha, ("Core.cs", "class Core {}"), ("README.md", "x"), (".gitmodules", """
            [submodule "util"]
            	path = deps/util
            	url = git@github.com:acme/util.git
            """));
        h.GitHubLink("acme/core", CoreSha, "deps/util", UtilSha);
        h.GitHubRepo("acme/util", UtilSha, ("Util.cs", "class Util {}"), (".gitmodules", """
            [submodule "back"]
            	path = app
            	url = https://github.com/acme/app
            """));
        h.GitHubLink("acme/util", UtilSha, "app", Parent);

        var result = await Fetch(h, App());

        Assert.Equal(["libs/core/Core.cs", "libs/core/deps/util/Util.cs", "src/App.cs"], result.Snapshot.Select(f => f.Path));
        var subs = result.Submodules.ToDictionary(s => s.Path);
        Assert.Equal((CodeRepoSubmodule.Fetched, CoreSha, 1, 1), (subs["libs/core"].Status, subs["libs/core"].Sha, subs["libs/core"].Depth, subs["libs/core"].Files));
        Assert.Equal((CodeRepoSubmodule.Fetched, 2), (subs["libs/core/deps/util"].Status, subs["libs/core/deps/util"].Depth));
        Assert.Equal(CodeRepoSubmodule.Skipped, subs["vendor/skip"].Status);
        Assert.Contains("update = none", subs["vendor/skip"].Message);
        Assert.Equal(CodeRepoSubmodule.Skipped, subs["libs/core/deps/util/app"].Status);
        Assert.Contains("Cycle", subs["libs/core/deps/util/app"].Message);
        Assert.DoesNotContain(h.Handler.Requests, r => r.Request.RequestUri!.AbsoluteUri.Contains("acme/skip"));

        // Every pin is recorded (skipped ones too, so their bumps show), with GitHub compare links.
        Assert.Equal(["libs/core", "libs/core/deps/util", "libs/core/deps/util/app", "vendor/skip"], result.Pins.Select(p => p.Path).Order(StringComparer.Ordinal));
        var core = result.Pins.Single(p => p.Path == "libs/core");
        Assert.Equal(("https://github.com/acme/core/compare/{old}...{new}", true), (core.CompareUrl, core.Fetched));
        Assert.False(result.Pins.Single(p => p.Path == "vendor/skip").Fetched);
        Assert.All(h.Handler.Requests, r => Assert.Equal(Token, r.Request.Headers.Authorization!.Parameter));
    }

    [Fact]
    public async Task Nesting_Stops_At_Depth_Three()
    {
        var h = new FakeHosts();
        string[] repos = ["acme/app", "acme/l1", "acme/l2", "acme/l3", "acme/l4"];
        string[] shas = [Parent, CoreSha, UtilSha, LibSha, PubSha];
        for (var i = 0; i < repos.Length; i++)
        {
            var files = new List<(string, string?)> { ($"L{i}.cs", $"class L{i} {{}}") };
            if (i + 1 < repos.Length)
            {
                files.Add((".gitmodules", $"[submodule \"next\"]\n\tpath = n\n\turl = ../{repos[i + 1].Split('/')[1]}.git\n"));
                h.GitHubLink(repos[i], shas[i], "n", shas[i + 1]);
            }
            h.GitHubRepo(repos[i], shas[i], [.. files]);
        }
        var result = await Fetch(h, App());
        Assert.Equal(["L0.cs", "n/L1.cs", "n/n/L2.cs", "n/n/n/L3.cs"], result.Snapshot.Select(f => f.Path));
        var deepest = result.Submodules.Single(s => s.Path == "n/n/n/n");
        Assert.Equal((CodeRepoSubmodule.Skipped, 4), (deepest.Status, deepest.Depth));
        Assert.Contains("deeper than 3", deepest.Message);
    }

    [Fact]
    public async Task Limits_Count_Across_The_Whole_Snapshot()
    {
        // The parent keeps three files (.gitmodules included), the submodule two: each fits four, together they do not.
        const string gitmodules = "[submodule \"c\"]\npath = core\nurl = ../core.git";
        var h = new FakeHosts { Limits = new SnapshotLimits { MaxFiles = 4 } };
        h.GitHubRepo("acme/app", Parent, ("A.cs", "class A {}"), ("B.cs", "class B {}"), (".gitmodules", gitmodules));
        h.GitHubLink("acme/app", Parent, "core", CoreSha);
        h.GitHubRepo("acme/core", CoreSha, ("C.cs", "class C {}"), ("D.cs", "class D {}"));
        var many = await Assert.ThrowsAsync<SnapshotLimitException>(() => Fetch(h, App()));
        Assert.Contains("More than 4 source files", many.Message);

        // Each archive alone fits the byte limit; together they do not.
        var parentBytes = h.Archives[$"https://api.github.com/repos/acme/app/tarball/{Parent}"].Length;
        var coreBytes = h.Archives[$"https://api.github.com/repos/acme/core/tarball/{CoreSha}"].Length;
        h.Limits = new SnapshotLimits { MaxArchiveBytes = Math.Max(parentBytes, coreBytes) + 10 };
        Assert.True(parentBytes + coreBytes > h.Limits.MaxArchiveBytes);
        var bytes = await Assert.ThrowsAsync<SnapshotLimitException>(() => Fetch(h, App()));
        Assert.Contains("archive is larger", bytes.Message);

        // Kept bytes: the parent's 20 + .gitmodules, then C (10) fits and D (10) tips it.
        h.Limits = new SnapshotLimits { MaxTotalBytes = 20 + gitmodules.Length + 15 };
        var total = await Assert.ThrowsAsync<SnapshotLimitException>(() => Fetch(h, App()));
        Assert.Contains("add up to more than", total.Message);
        h.Limits = new SnapshotLimits { MaxTotalBytes = 20 + gitmodules.Length + 20 };
        Assert.Equal(4, (await Fetch(h, App())).Snapshot.Count);
    }

    [Fact]
    public async Task Tokens_Only_Go_To_Their_Own_Host_And_Unknown_Hosts_Are_Never_Contacted()
    {
        var h = new FakeHosts();
        h.GitHubRepo("acme/app", Parent, ("A.cs", "class A {}"), (".gitmodules", """
            [submodule "pub"]
            	path = pub
            	url = https://gitlab.com/oss/pub.git
            [submodule "evil"]
            	path = evil
            	url = https://169.254.169.254/latest/meta-data.git
            [submodule "private-host"]
            	path = corp
            	url = git@git.corp.example:team/lib.git
            """));
        h.GitHubLink("acme/app", Parent, "pub", PubSha);
        h.GitHubLink("acme/app", Parent, "evil", LibSha);
        h.GitHubLink("acme/app", Parent, "corp", LibSha);
        h.Archives[$"https://gitlab.com/api/v4/projects/oss%2Fpub/repository/archive.tar.gz?sha={PubSha}"] = TestArchives.TarGz(("pub-x/Pub.cs", "class Pub {}"));

        var result = await Fetch(h, App());

        Assert.Equal(["A.cs", "pub/Pub.cs"], result.Snapshot.Select(f => f.Path));
        var subs = result.Submodules.ToDictionary(s => s.Path);
        Assert.Equal(CodeRepoSubmodule.Fetched, subs["pub"].Status);
        Assert.Contains("anonymously", subs["pub"].Message);
        Assert.Equal(CodeRepoSubmodule.NoAccess, subs["corp"].Status);
        Assert.Contains("no connection of the project", subs["corp"].Message);
        Assert.Equal(CodeRepoSubmodule.NoAccess, subs["evil"].Status);   // no connection for that address, and not a public host

        Assert.All(h.Handler.Requests, r => Assert.True(r.Request.RequestUri!.Host is "api.github.com" or "gitlab.com", r.Request.RequestUri.ToString()));
        foreach (var (req, _) in h.Handler.Requests)
        {
            var sent = req.Headers.Authorization?.Parameter ?? (req.Headers.TryGetValues("PRIVATE-TOKEN", out var v) ? v.Single() : null);
            Assert.Equal(req.RequestUri!.Host == "api.github.com" ? Token : null, sent);
        }
    }

    [Fact]
    public async Task A_Matching_Connection_Lends_Its_Credentials()
    {
        var h = new FakeHosts();
        h.GitHubRepo("acme/app", Parent, ("A.cs", "class A {}"), (".gitmodules", "[submodule \"lib\"]\n\tpath = lib\n\turl = https://gitlab.example.com/team/lib.git\n"));
        h.GitHubLink("acme/app", Parent, "lib", LibSha);
        var archive = $"https://gitlab.example.com/api/v4/projects/team%2Flib/repository/archive.tar.gz?sha={LibSha}";
        h.Archives[archive] = TestArchives.TarGz(("lib-x/Lib.cs", "class Lib {}"));
        h.Requires[archive] = "glpat-team";
        var other = new CodeRepoConfig { Host = GitHostKind.GitLab, BaseUrl = "https://gitlab.example.com", Project = "team/lib" };
        var unrelated = new CodeRepoConfig { Host = GitHostKind.GitLab, BaseUrl = "https://gitlab.example.com", Project = "team/other" };

        var result = await Fetch(h, App(), new SubmoduleCredential("Other", unrelated, "glpat-wrong"), new SubmoduleCredential("Team lib", other, "glpat-team"));

        Assert.Contains("lib/Lib.cs", result.Snapshot.Select(f => f.Path));
        var lib = Assert.Single(result.Submodules);
        Assert.Equal(CodeRepoSubmodule.Fetched, lib.Status);
        Assert.Contains("Team lib", lib.Message);
        var sent = h.Handler.Requests.Single(r => r.Request.RequestUri!.AbsoluteUri == archive).Request;
        Assert.Equal("glpat-team", sent.Headers.GetValues("PRIVATE-TOKEN").Single());
        Assert.Null(sent.Headers.Authorization);
        Assert.Equal("https://gitlab.example.com/team/lib/-/compare/{old}...{new}", result.Pins.Single().CompareUrl);
    }

    [Fact]
    public async Task Same_Host_Token_First_Then_Anonymous_And_No_Access_Lets_The_Sync_Continue()
    {
        var h = new FakeHosts();
        h.GitHubRepo("acme/app", Parent, ("src/A.cs", "class A {}"), (".gitmodules", """
            [submodule "pub"]
            	path = src/pub
            	url = https://github.com/someone/public-lib
            [submodule "closed"]
            	path = src/closed
            	url = ../closed.git
            [submodule "outside"]
            	path = docs/theme
            	url = ../theme.git
            [submodule "listed-skip"]
            	path = src/skip-me
            	url = ../skip.git
            [submodule "stale"]
            	path = src/stale
            	url = ../stale.git
            """));
        h.GitHubLink("acme/app", Parent, "src/pub", PubSha);
        h.GitHubLink("acme/app", Parent, "src/closed", LibSha);
        h.GitHubLink("acme/app", Parent, "docs/theme", UtilSha);
        h.GitHubLink("acme/app", Parent, "src/skip-me", UtilSha);
        // The public repository refuses the parent's token (another account) but serves anyone.
        var pub = $"https://api.github.com/repos/someone/public-lib/tarball/{PubSha}";
        h.GitHubRepo("someone/public-lib", PubSha, ("Pub.cs", "class Pub {}"));
        h.Requires[pub] = null;
        var config = App("src");
        config.SkipSubmodules = ["src/skip-me"];

        var result = await Fetch(h, config);

        Assert.Equal(["src/A.cs", "src/pub/Pub.cs"], result.Snapshot.Select(f => f.Path));
        var subs = result.Submodules.ToDictionary(s => s.Path);
        Assert.Equal(CodeRepoSubmodule.Fetched, subs["src/pub"].Status);
        Assert.Contains("anonymously", subs["src/pub"].Message);
        Assert.Equal(CodeRepoSubmodule.NoAccess, subs["src/closed"].Status);
        Assert.Contains("github.com/acme/closed", subs["src/closed"].Message);
        Assert.Equal((CodeRepoSubmodule.Skipped, "Outside the folders to include."), (subs["docs/theme"].Status, subs["docs/theme"].Message));
        Assert.Equal(CodeRepoSubmodule.Skipped, subs["src/skip-me"].Status);
        Assert.Equal(CodeRepoSubmodule.NotFound, subs["src/stale"].Status);   // in .gitmodules, but no gitlink at the commit
        Assert.Equal(2, result.Missing);
        var tries = h.Handler.Requests.Where(r => r.Request.RequestUri!.AbsoluteUri == pub).Select(r => r.Request.Headers.Authorization?.Parameter).ToList();
        Assert.Equal([Token, null], tries);
    }

    [Fact]
    public async Task Turned_Off_Lists_Submodules_Without_Calling_The_Host()
    {
        var h = new FakeHosts();
        h.GitHubRepo("acme/app", Parent, ("A.cs", "class A {}"), (".gitmodules", "[submodule \"c\"]\npath = core\nurl = ../core.git"));
        var config = App();
        config.IncludeSubmodules = false;
        var result = await Fetch(h, config);
        Assert.Equal(["A.cs"], result.Snapshot.Select(f => f.Path));
        Assert.Equal(CodeRepoSubmodule.Skipped, Assert.Single(result.Submodules).Status);
        Assert.Empty(result.Pins);
        Assert.Single(h.Handler.Requests);   // just the archive
    }
}
