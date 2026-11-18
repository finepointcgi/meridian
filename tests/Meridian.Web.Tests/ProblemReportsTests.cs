using Meridian.Web.Hosting;
using Microsoft.Extensions.Logging;

namespace Meridian.Web.Tests;

/// <summary>Crash files and problem-report links (Hosting/ProblemReports.cs): what is redacted, and what the GitHub link holds.</summary>
public class ProblemReportsTests
{
    private const string MacHome = "/Users/jane.doe";
    private const string WinHome = @"C:\Users\Jane Doe";

    private static string R(string s, string home = MacHome) => CrashRedactor.Redact(s, home, user: "jane.doe", machine: "JANES-MBP");
    private static string M(string s, string home = MacHome) => CrashRedactor.RedactMessage(s, home, user: "jane.doe", machine: "JANES-MBP");

    [Fact]
    public void Paths_under_the_home_folder_keep_only_the_extension()
    {
        var s = R("Could not open /Users/jane.doe/Projects/Secret Program/brake model.mdzip now");
        Assert.DoesNotContain("Secret", s);
        Assert.DoesNotContain("jane", s);
        Assert.Contains("~/….mdzip", s);
    }

    [Fact]
    public void Paths_with_spaces_end_at_quotes_and_stack_line_numbers()
    {
        var s = R("   at Meridian.Web.Services.Workspace.Open() in /Users/jane.doe/src/My Meridian/Workspace.cs:line 42");
        Assert.Equal("   at Meridian.Web.Services.Workspace.Open() in ~/…/Workspace.cs:line 42", s);

        var q = R("File '/Users/jane.doe/Application Support/Meridian/meridian.db' is locked");
        Assert.Equal("File '~/….db' is locked", q);
    }

    [Fact]
    public void Windows_and_Linux_home_folders_of_other_users_are_redacted()
    {
        var s = R(@"at X in C:\Users\Jane Doe\models\plan.xmi and /home/bob/work/a b/c.sysml", WinHome);
        Assert.DoesNotContain("Jane", s);
        Assert.DoesNotContain("bob", s);
        Assert.DoesNotContain("plan", s);
        Assert.Contains("~/….xmi", s);
        Assert.Contains("~/….sysml", s);

        // Another account's folder on this machine, without knowing the home folder.
        Assert.Equal("see ~/….json", R("see /Users/someone/Library/Meridian/desktop.json", home: "/nonexistent"));
    }

    [Fact]
    public void User_and_machine_names_emails_and_tokens_are_removed()
    {
        var s = R("jane.doe@example.com on JANES-MBP as jane.doe: token=abc123 Bearer eyJhbGciOi ghp_" + new string('a', 36)
                  + " key " + new string('f', 48));
        Assert.DoesNotContain("example.com", s);
        Assert.DoesNotContain("JANES", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jane", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc123", s);
        Assert.DoesNotContain("eyJ", s);
        Assert.DoesNotContain("ghp_", s);
        Assert.DoesNotContain(new string('f', 48), s);
        Assert.Contains("<email>", s);
        Assert.Contains("<machine>", s);
    }

    [Fact]
    public void Urls_lose_credentials_and_query_strings_but_keep_host_and_path()
    {
        var s = R("GET https://bob:pw@meridian.example.com/project/42?launch=SECRET&x=1#frag failed");
        Assert.Equal("GET https://meridian.example.com/project/42 failed", s);
    }

    [Fact]
    public void Messages_lose_quoted_model_names_but_stack_traces_keep_their_text()
    {
        Assert.Equal("Element '…' was not found in “…”.", M("Element 'Brake Controller' was not found in “Vehicle”."));
        Assert.Equal("Key \"…\" missing", M("Key \"Fuel Pump\" missing"));
        Assert.Equal("at A.B()", R("at A.B()"));
    }

    [Fact]
    public void A_crash_report_holds_the_redacted_exception_chain_and_recent_log()
    {
        Exception ex;
        try { Throw(); } catch (Exception e) { ex = e; }
        var r = CrashReports.Create(ex, "AppDomain", "desktop", "1.2.3", ["[10:00:00] opened ~/….mdzip"], MacHome, "jane.doe", "JANES-MBP");
        Assert.Equal("System.InvalidOperationException", r.ExceptionType);
        Assert.Equal("Diagram '…' failed for <email>", r.Message);
        Assert.Contains("---> System.IO.IOException: Could not read ~/….meridian", r.Details);
        Assert.Contains(nameof(Throw), r.Details);
        Assert.DoesNotContain("Top Secret", r.Details);
        Assert.Equal("1.2.3", r.Version);
        Assert.Equal("desktop", r.Host);
        Assert.Single(r.RecentLog);
        Assert.StartsWith("System.InvalidOperationException: Diagram", r.Summary);

        static void Throw() => throw new InvalidOperationException("Diagram 'Top Secret' failed for jane.doe@corp.com",
            new IOException("Could not read /Users/jane.doe/Top Secret.meridian"));
    }

    [Fact]
    public void Crash_files_are_pending_until_reported_or_dismissed_and_old_ones_are_pruned()
    {
        var dir = Directory.CreateTempSubdirectory("meridian-crashes-").FullName;
        try
        {
            Assert.Empty(CrashReports.Pending(dir));
            Assert.Empty(CrashReports.Pending(Path.Combine(dir, "missing")));
            var first = CrashReports.Write(Report("20260101-000000-aaaaaa"), dir);
            var second = CrashReports.Write(Report("20260102-000000-bbbbbb"), dir);
            var pending = CrashReports.Pending(dir);
            Assert.Equal([second, first], pending.Select(p => p.Path));
            Assert.Equal("Boom", pending[0].Report.Message);

            CrashReports.MarkHandled([second]);
            Assert.Equal([first], CrashReports.Pending(dir).Select(p => p.Path));

            for (var i = 0; i < 25; i++) CrashReports.Write(Report($"20270101-0000{i:00}-cccccc"), dir);
            Assert.Equal(20, Directory.GetFiles(dir, "crash-*.json").Length);
            Assert.False(File.Exists(first));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_crash_link_prefills_the_bug_report_form()
    {
        var url = IssueLinks.ForCrash(Report("20260101-000000-aaaaaa") with { RecentLog = ["[10:00:00] Warning Workspace: slow"] });
        Assert.StartsWith("https://github.com/finepointcgi/meridian/issues/new?template=bug_report.yml&", url);
        var q = Query(url);
        Assert.Equal("Crash: NullReferenceException — Boom", q["title"]);
        Assert.Equal("bug,crash", q["labels"]);
        Assert.Equal("0.7.0", q["version"]);
        Assert.Contains("macOS", q["os"]);
        Assert.Contains("Report id: 20260101-000000-aaaaaa", q["logs"]);
        Assert.Contains("at Meridian.A.B()", q["logs"]);
        Assert.Contains("Warning Workspace: slow", q["logs"]);
        Assert.Contains("What were you doing", q["what-happened"]);
    }

    [Fact]
    public void A_long_crash_is_trimmed_to_fit_the_address_limit()
    {
        var frames = string.Join("\n", Enumerable.Range(0, 2000).Select(i => $"   at Meridian.Some.Very.Long.Namespace.Type{i}.Method{i}(String argument) in ~/…/File{i}.cs:line {i}"));
        var report = Report("20260101-000000-aaaaaa") with { Details = "System.NullReferenceException: Boom\n" + frames, Message = new string('x', 500) };
        var url = IssueLinks.ForCrash(report);
        Assert.True(url.Length <= IssueLinks.MaxUrlLength, $"{url.Length}");
        var q = Query(url);
        Assert.True(q["title"].Length <= 120);
        Assert.Contains("Type0.Method0", q["logs"]);
        Assert.DoesNotContain("Type1999", q["logs"]);
        Assert.Contains("trimmed", q["logs"]);

        Assert.True(IssueLinks.ForCrash(report, maxLength: 1200).Length <= 1200);
    }

    [Fact]
    public void A_problem_link_without_a_crash_has_only_version_and_os()
    {
        var q = Query(IssueLinks.ForProblem("0.7.0", "Windows 11 (X64), desktop app"));
        Assert.Equal("0.7.0", q["version"]);
        Assert.Equal("Windows 11 (X64), desktop app", q["os"]);
        Assert.False(q.ContainsKey("logs"));
        Assert.False(q.ContainsKey("title"));
    }

    [Fact]
    public void Circuit_errors_are_captured_and_warnings_become_recent_log_lines()
    {
        var dir = Directory.CreateTempSubdirectory("meridian-crash-log-").FullName;
        var captured = new List<CrashReport>();
        var (folder, host) = (CrashReports.Folder, "server");
        try
        {
            CrashReports.Configure(dir, "desktop", captured.Add);
            using var provider = new CrashLogProvider();
            provider.CreateLogger("Meridian.Web.Services.Workspace").LogWarning("Opening {Path} was slow", "/Users/jane.doe/a b/model.mdzip");
            provider.CreateLogger("Microsoft.AspNetCore.Routing").LogDebug("not recorded");
            provider.CreateLogger(CrashLogProvider.CircuitCategory).LogError(new InvalidOperationException("Circuit broke"), "Unhandled exception in circuit");

            var report = Assert.Single(captured);
            Assert.Equal("Blazor circuit", report.Source);
            Assert.Contains(report.RecentLog, l => l.Contains("Warning Workspace: Opening") && !l.Contains("a b/model"));
            Assert.DoesNotContain(report.RecentLog, l => l.Contains("not recorded"));
            Assert.Single(CrashReports.Pending(dir));
        }
        finally
        {
            CrashReports.Configure(folder, host);
            Directory.Delete(dir, true);
        }
    }

    private static CrashReport Report(string id) => new()
    {
        Id = id, Time = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), Version = "0.7.0", Os = "macOS 15.2 (Arm64)", Runtime = ".NET 10.0.0",
        Host = "desktop", Source = "AppDomain", ExceptionType = "System.NullReferenceException", Message = "Boom",
        Details = "System.NullReferenceException: Boom\n   at Meridian.A.B()",
    };

    private static Dictionary<string, string> Query(string url) =>
        new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
}
