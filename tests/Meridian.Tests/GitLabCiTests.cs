using System.Diagnostics;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Meridian.Tests;

/// <summary>
/// Structural checks of .gitlab-ci.yml (no GitLab lint is available offline): it parses, every job is well formed and
/// points at things that exist, and it builds and publishes the same files as .github/workflows/release.yml.
/// </summary>
public class GitLabCiTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Meridian.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static YamlMappingNode Load(string relative)
    {
        var stream = new YamlStream();
        using var reader = new StreamReader(Path.Combine(Root, relative));
        try { stream.Load(reader); }
        catch (YamlDotNet.Core.YamlException ex) { throw new InvalidOperationException($"{relative}:{ex.Start.Line}:{ex.Start.Column}: {ex.Message}", ex); }
        return Assert.IsType<YamlMappingNode>(Assert.Single(stream.Documents).RootNode);
    }

    private static readonly HashSet<string> Reserved = ["workflow", "stages", "variables", "default", "include", "image", "services", "cache", "before_script", "after_script"];

    private static YamlMappingNode Ci => Load(".gitlab-ci.yml");

    private static IEnumerable<(string Name, YamlMappingNode Job)> Jobs(YamlMappingNode ci) =>
        ci.Children.Select(kv => (((YamlScalarNode)kv.Key).Value!, kv.Value))
            .Where(x => !Reserved.Contains(x.Item1) && !x.Item1.StartsWith('.'))
            .Select(x => (x.Item1, Assert.IsType<YamlMappingNode>(x.Item2)));

    private static YamlNode? Get(YamlMappingNode m, string key) =>
        m.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;

    private static string? Str(YamlMappingNode m, string key) => (Get(m, key) as YamlScalarNode)?.Value;

    private static IEnumerable<string> Strings(YamlNode? n) => n switch
    {
        YamlScalarNode s => [s.Value!],
        YamlSequenceNode q => q.Children.Select(c => c is YamlScalarNode s ? s.Value! : c.ToString()),
        _ => [],
    };

    /// <summary>A job with its <c>extends</c> templates merged in (later keys win, like GitLab's shallow merge).</summary>
    private static YamlMappingNode Resolved(YamlMappingNode ci, YamlMappingNode job)
    {
        var merged = new YamlMappingNode();
        foreach (var parent in Strings(Get(job, "extends")))
        {
            var template = Assert.IsType<YamlMappingNode>(Get(ci, parent));
            foreach (var kv in Resolved(ci, template).Children) merged.Children[kv.Key] = kv.Value;
        }
        foreach (var kv in job.Children) if (((YamlScalarNode)kv.Key).Value != "extends") merged.Children[kv.Key] = kv.Value;
        return merged;
    }

    [Fact]
    public void Every_job_has_a_known_stage_a_script_and_valid_references()
    {
        var ci = Ci;
        var stages = Strings(Get(ci, "stages")).ToList();
        Assert.Equal(["test", "package", "release", "publish"], stages);
        var jobs = Jobs(ci).ToDictionary(j => j.Name, j => Resolved(ci, j.Job));
        Assert.True(jobs.Count >= 12, string.Join(", ", jobs.Keys));
        foreach (var (name, job) in jobs)
        {
            Assert.True(Str(job, "stage") is { } st && stages.Contains(st), $"{name}: unknown stage");
            Assert.True(Get(job, "script") is YamlSequenceNode { Children.Count: > 0 }, $"{name}: no script");
            Assert.True(Get(job, "rules") is YamlSequenceNode { Children.Count: > 0 }, $"{name}: no rules (it would run in every pipeline)");
            Assert.False(Get(job, "only") != null || Get(job, "except") != null, $"{name}: use rules, not only/except");
            if (Get(job, "needs") is YamlSequenceNode needs)
                foreach (var need in needs.Children)
                {
                    var target = need is YamlScalarNode s ? s.Value! : Str((YamlMappingNode)need, "job")!;
                    Assert.True(jobs.ContainsKey(target), $"{name}: needs unknown job {target}");
                    Assert.True(stages.IndexOf(Str(jobs[target], "stage")!) <= stages.IndexOf(Str(job, "stage")!), $"{name}: needs a later-stage job {target}");
                }
            foreach (var line in Strings(Get(job, "script")).Concat(Strings(Get(job, "before_script"))))
                foreach (Match m in Regex.Matches(line, @"(?<![\w/.-])((?:build|tests)/[\w./-]+\.(?:sh|ps1))"))
                    Assert.True(File.Exists(Path.Combine(Root, m.Groups[1].Value)), $"{name}: {m.Groups[1].Value} does not exist");
        }
        Assert.Contains("pages", jobs.Keys);   // GitLab Pages publishes the artifacts of the job called "pages"
        Assert.Equal(["public"], Strings(Get((YamlMappingNode)Get(jobs["pages"], "artifacts")!, "paths")));
    }

    [Fact]
    public void Workflow_runs_merge_requests_the_default_branch_v_tags_and_manual_pipelines()
    {
        var rules = ((YamlSequenceNode)Get((YamlMappingNode)Get(Ci, "workflow")!, "rules")!).Children
            .Select(r => Str((YamlMappingNode)r, "if")!).ToList();
        Assert.Contains(rules, r => r.Contains("merge_request_event"));
        Assert.Contains(rules, r => r.Contains("$CI_DEFAULT_BRANCH"));
        Assert.Contains(rules, r => r.Contains("$CI_COMMIT_TAG =~ /^v/"));
        Assert.Contains(rules, r => r.Contains("\"web\""));
    }

    private static Dictionary<string, YamlMappingNode> AllJobs() => Jobs(Ci).ToDictionary(j => j.Name, j => Resolved(Ci, j.Job));

    [Fact]
    public void Tests_mirror_ci_yml_with_postgres_and_playwright()
    {
        var jobs = AllJobs();
        Assert.Contains(Strings(Get(jobs["test"], "script")), s => s.Contains("dotnet test Meridian.slnx") && s.Contains("!~Meridian.E2E"));

        var e2e = jobs["e2e"];
        Assert.Equal("require", Str((YamlMappingNode)Get(e2e, "variables")!, "MERIDIAN_E2E"));
        Assert.Contains(Strings(Get(e2e, "script")), s => s.Contains("tests/Meridian.E2E/install-browsers.sh --with-deps"));

        var pg = jobs["postgres-store"];
        var service = (YamlMappingNode)((YamlSequenceNode)Get(pg, "services")!).Children[0];
        Assert.StartsWith("postgres:17", Str(service, "name"));
        Assert.Equal("postgres", Str(service, "alias"));
        var vars = (YamlMappingNode)Get(pg, "variables")!;
        Assert.Equal("1", Str(vars, "MERIDIAN_REQUIRE_POSTGRES"));
        Assert.Contains("Host=postgres", Str(vars, "MERIDIAN_POSTGRES_CONNECTION"));
        Assert.Contains($"Password={Str(vars, "POSTGRES_PASSWORD")}", Str(vars, "MERIDIAN_POSTGRES_CONNECTION"));
        Assert.Contains(Strings(Get(pg, "script")), s => s.Contains("Meridian.Tests.StoreContract"));
    }

    /// <summary>The files release.yml checks for, per platform, with $v as the version.</summary>
    private static Dictionary<string, string[]> ReleaseYmlFiles()
    {
        var text = File.ReadAllText(Path.Combine(Root, ".github/workflows/release.yml"));
        return Regex.Matches(text, @"^\s*(macos|windows|linux)\)\s+want=""([^""]+)""", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Packages_and_the_release_cover_the_same_files_as_release_yml()
    {
        var expected = ReleaseYmlFiles();
        Assert.Equal(["linux", "macos", "windows"], expected.Keys.Order());
        var jobs = AllJobs();
        foreach (var (platform, rids) in new[] { ("linux", "linux-x64"), ("windows", "win-x64"), ("macos", "osx-arm64 osx-x64") })
        {
            var job = jobs[$"package:{platform}"];
            var script = string.Join("\n", Strings(Get(job, "script")));
            Assert.Contains($"build/package.sh", script);
            Assert.Contains(rids, script);
            foreach (var file in expected[platform])
                Assert.Contains(file.Replace("$v", platform == "windows" ? "$v" : "$V"), script);   // the job checks every file release.yml checks
            Assert.Equal("1", Str((YamlMappingNode)Get(job, "variables")!, "NO_CHECKSUMS"));
        }
        Assert.Equal(["saas-windows-medium-amd64"], Strings(Get(jobs["package:windows"], "tags")));
        Assert.Contains(Strings(Get(jobs["package:windows"], "before_script")), s => s.Contains("choco install innosetup"));
        Assert.Equal(["saas-macos-medium-m1"], Strings(Get(jobs["package:macos"], "tags")));
        // macOS is optional: manual (allowed to fail) unless MACOS_RUNNER_ENABLED=true.
        var macRules = ((YamlSequenceNode)Get(jobs["package:macos"], "rules")!).Children.Cast<YamlMappingNode>().ToList();
        Assert.Contains(macRules, r => Str(r, "if")!.Contains("MACOS_RUNNER_ENABLED == \"true\"") && Str(r, "when") is null);
        Assert.Contains(macRules, r => Str(r, "when") == "manual" && Str(r, "allow_failure") == "true");
        Assert.Contains(Strings(Get(jobs["package:linux"], "before_script")), s => s.Contains("appimagetool"));

        // Windows + Linux files are linked by the release keyword; macOS files by release:macos; SHA256SUMS.txt by both.
        var release = (YamlMappingNode)Get(jobs["release:publish"], "release")!;
        Assert.Equal("$CI_COMMIT_TAG", Str(release, "tag_name"));
        var links = ((YamlSequenceNode)Get((YamlMappingNode)Get(release, "assets")!, "links")!).Children.Cast<YamlMappingNode>().ToList();
        var linked = links.Select(l => Str(l, "name")!).ToList();
        Assert.Equal(expected["windows"].Concat(expected["linux"]).Select(f => f.Replace("$v", "$RELEASE_VERSION")).Append("SHA256SUMS.txt").Order(), linked.Order());
        Assert.All(links, l => Assert.Equal($"$PACKAGE_REGISTRY_URL/$RELEASE_VERSION/{Str(l, "name")}", Str(l, "url")));
        Assert.Contains("packages/generic/", Str((YamlMappingNode)Get(Ci, "variables")!, "PACKAGE_REGISTRY_URL"));
        Assert.Contains(Strings(Get(jobs["release:upload"], "script")), s => s.Contains("sha256sum Meridian-* > SHA256SUMS.txt"));
        Assert.Contains(Strings(Get(jobs["release:macos"], "script")), s => s.Contains("sha256sum Meridian-*-macos-* >> SHA256SUMS.txt"));
        Assert.Contains(Strings(Get(jobs["release:macos"], "script")), s => s.Contains("/releases/$CI_COMMIT_TAG/assets/links"));
    }

    [Fact]
    public void The_container_goes_to_the_gitlab_registry_multi_arch_and_signed()
    {
        var job = AllJobs()["container"];
        var script = string.Join("\n", Strings(Get(job, "script")));
        Assert.Contains("$CI_REGISTRY_IMAGE", script);
        Assert.Contains("docker login \"$CI_REGISTRY\" -u \"$CI_REGISTRY_USER\" --password-stdin", script);
        Assert.Contains("--platform linux/amd64,linux/arm64", script);
        Assert.Contains("--provenance=mode=max", script);
        Assert.Contains("cosign sign", script);
        Assert.Equal("sigstore", Str((YamlMappingNode)Get((YamlMappingNode)Get(job, "id_tokens")!, "SIGSTORE_ID_TOKEN")!, "aud"));
        Assert.DoesNotContain("ghcr.io", File.ReadAllText(Path.Combine(Root, ".gitlab-ci.yml")));
    }

    [Fact]
    public void Github_workflows_still_parse() =>
        Assert.All(Directory.GetFiles(Path.Combine(Root, ".github/workflows"), "*.yml"), f => Assert.NotNull(Get(Load(Path.GetRelativePath(Root, f)), "jobs")));

    /// <summary>Every bash job's before_script + script is valid shell syntax (bash -n), as GitLab concatenates them.</summary>
    [SkippableFact]
    public void Shell_scripts_have_valid_syntax()
    {
        Skip.If(OperatingSystem.IsWindows(), "bash -n runs on macOS and Linux.");
        foreach (var (name, job) in AllJobs())
        {
            if (Strings(Get(job, "tags")).Any(t => t.Contains("windows"))) continue;   // PowerShell
            var lines = Strings(Get(job, "before_script")).Concat(Strings(Get(job, "script"))).Concat(Strings(Get(job, "after_script")));
            var psi = new ProcessStartInfo("bash", ["-n"]) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            p.StandardInput.Write(string.Join("\n", lines) + "\n");
            p.StandardInput.Close();
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"{name}: {err}");
        }
    }
}
