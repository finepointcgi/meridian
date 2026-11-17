using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Meridian.Web.Hosting;

/// <summary>
/// Removes personal details from crash text before it is written or offered for reporting: paths under a home folder,
/// user and machine names, e-mail addresses, URL query strings and credentials, access tokens and (in messages) quoted text,
/// which is where element, diagram and project names show up.
/// </summary>
public static partial class CrashRedactor
{
    /// <summary>Source files keep their name (they come from the build, and say where the fault is); every other path is hidden.</summary>
    private static readonly string[] SourceExtensions = [".cs", ".razor", ".js"];

    // A path that starts at a home folder runs until a file extension followed by a space, a stack frame's ":line N", a quote,
    // a bracket or the end of the line (home paths often contain spaces: "Application Support", "My Models").
    private const string PathTail = @"(?:[\\/][^\r\n'""<>|]*?)?(?=:line \d|['""<>|\r\n]|$|(?<=\.[A-Za-z0-9]{1,8})[\s,;)])";

    [GeneratedRegex(@"(?:[A-Za-z]:\\(?:Users|Documents and Settings)\\[^\\/\r\n'""<>|]+|/(?:Users|home)/[^/\r\n'""<>|\s]+)" + PathTail, RegexOptions.Multiline)]
    private static partial Regex AnyHomePath();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(?<scheme>https?|wss?)://(?:[^\s/@'""<>]+@)?(?<host>[^\s/?#'""<>]+)(?<path>/[^\s?#'""<>]*)?(?:[?#][^\s'""<>]*)?")]
    private static partial Regex Url();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_-]{16,}|sk-ant-[A-Za-z0-9_-]{16,}|xox[abpr]-[A-Za-z0-9-]{10,})")]
    private static partial Regex KnownToken();

    [GeneratedRegex(@"(?i)\b(?:(bearer|basic)(\s+)|(token|password|pwd|secret|apikey|api_key|access_token)(\s*[:=]\s*))[^\s,;'""]+")]
    private static partial Regex Credential();

    [GeneratedRegex(@"\b[0-9A-Fa-f]{32,}\b")]
    private static partial Regex LongHex();

    [GeneratedRegex(@"'[^'\r\n]{1,400}'|""[^""\r\n]{1,400}""|‘[^’\r\n]{1,400}’|“[^”\r\n]{1,400}”")]
    private static partial Regex Quoted();

    /// <summary>Redacts a stack trace or log line. <paramref name="home"/>, <paramref name="user"/> and <paramref name="machine"/> default to this computer's.</summary>
    public static string Redact(string? text, string? home = null, string? user = null, string? machine = null)
    {
        if (string.IsNullOrEmpty(text)) return "";
        home ??= SafeHome();
        user ??= SafeUser();
        machine ??= SafeMachine();

        var s = text;
        // URLs first: their paths and queries are not file paths, and query strings carry tokens (?launch=…).
        s = Url().Replace(s, m => $"{m.Groups["scheme"].Value}://{m.Groups["host"].Value}{m.Groups["path"].Value}");
        s = KnownToken().Replace(s, "<token>");
        if (home is { Length: > 2 })
        {
            var known = new Regex(Regex.Escape(home.TrimEnd('/', '\\')) + PathTail, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            s = known.Replace(s, m => HidePath(m.Value, home.TrimEnd('/', '\\').Length));
        }
        s = AnyHomePath().Replace(s, m => HidePath(m.Value, m.Value.IndexOfAny(['/', '\\'], m.Value.IndexOfAny(['/', '\\'], 3) + 1) is > 0 and var i ? i : m.Value.Length));
        s = Credential().Replace(s, m => m.Groups[1].Success ? $"{m.Groups[1].Value}{m.Groups[2].Value}<redacted>" : $"{m.Groups[3].Value}{m.Groups[4].Value}<redacted>");
        s = Email().Replace(s, "<email>");
        s = LongHex().Replace(s, "<hex>");
        if (user is { Length: >= 3 }) s = Regex.Replace(s, $@"(?<![A-Za-z0-9]){Regex.Escape(user)}(?![A-Za-z0-9])", "<user>", RegexOptions.IgnoreCase);
        if (machine is { Length: >= 3 }) s = Regex.Replace(s, $@"(?<![A-Za-z0-9]){Regex.Escape(machine)}(?![A-Za-z0-9])", "<machine>", RegexOptions.IgnoreCase);
        return s;
    }

    /// <summary>Redacts an exception message or log message: as <see cref="Redact"/>, and quoted text (usually model content) is replaced too.</summary>
    public static string RedactMessage(string? text, string? home = null, string? user = null, string? machine = null)
    {
        var s = Redact(text, home, user, machine);
        return Quoted().Replace(s, m => m.Value[0] + "…" + m.Value[^1]);
    }

    /// <summary>"~/…" with the source file name kept for .cs/.razor/.js (stack frames), otherwise only the extension ("~/….mdzip").</summary>
    private static string HidePath(string path, int homeLength)
    {
        var name = path[Math.Min(homeLength, path.Length)..].Split('/', '\\').LastOrDefault(p => p.Length > 0) ?? "";
        var ext = Path.GetExtension(name);
        if (SourceExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return "~/…/" + name;
        return ext.Length is > 1 and <= 9 && !ext.Contains(' ') ? "~/…" + ext : "~/…";
    }

    private static string? SafeHome() { try { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch { return null; } }
    private static string? SafeUser() { try { return Environment.UserName; } catch { return null; } }
    private static string? SafeMachine() { try { return Environment.MachineName; } catch { return null; } }
}

/// <summary>What a crash file holds: no model content, no user names, home paths redacted (see <see cref="CrashRedactor"/>).</summary>
public sealed record CrashReport
{
    public string Id { get; init; } = "";
    public DateTimeOffset Time { get; init; }
    public string Version { get; init; } = "";
    public string Os { get; init; } = "";
    public string Runtime { get; init; } = "";
    /// <summary>"desktop" or "server".</summary>
    public string Host { get; init; } = "";
    /// <summary>Where it was caught: "AppDomain", "TaskScheduler" or "Blazor circuit".</summary>
    public string Source { get; init; } = "";
    public string ExceptionType { get; init; } = "";
    public string Message { get; init; } = "";
    /// <summary>The exception chain (inner exceptions included), each with its redacted message and stack.</summary>
    public string Details { get; init; } = "";
    /// <summary>The last application log lines before the crash, redacted.</summary>
    public List<string> RecentLog { get; init; } = new();

    /// <summary>One line for notices: "NullReferenceException: Object reference not set…".</summary>
    public string Summary => ExceptionType + (Message.Length > 0 ? ": " + Message : "");
}

/// <summary>
/// Local crash capture: unhandled exceptions (AppDomain, unobserved tasks, Blazor circuits) become JSON files in the data
/// folder's <c>crashes</c> folder. Nothing is ever sent anywhere: the desktop app offers to open a prefilled GitHub issue
/// (<see cref="IssueLinks"/>) that the user reviews and submits themselves.
/// </summary>
public static class CrashReports
{
    public const int LogLines = 60;
    private const int KeepFiles = 20;
    private static readonly ConcurrentQueue<string> Recent = new();
    private static string? _folder;
    private static string _host = "server";
    private static Action<CrashReport>? _onCapture;
    private static int _installed;

    /// <summary>The crash folder (null: crashes are only logged, as on a team server).</summary>
    public static string? Folder => _folder;

    /// <summary>Sets where crash files go and what to call this host. A server passes no folder and logs instead (<paramref name="onCapture"/>).</summary>
    public static void Configure(string? folder, string host, Action<CrashReport>? onCapture = null)
    {
        _folder = folder;
        _host = host;
        _onCapture = onCapture;
    }

    /// <summary>Hooks AppDomain and TaskScheduler exceptions once per process.</summary>
    public static void InstallProcessHandlers()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Capture(ex, "AppDomain"); };
        TaskScheduler.UnobservedTaskException += (_, e) => { Capture(e.Exception, "TaskScheduler"); e.SetObserved(); };
    }

    /// <summary>Keeps a log line (redacted) for the next crash file.</summary>
    public static void Record(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        Recent.Enqueue($"[{DateTimeOffset.Now:HH:mm:ss}] {CrashRedactor.RedactMessage(line.Length > 600 ? line[..600] + "…" : line)}");
        while (Recent.Count > LogLines && Recent.TryDequeue(out _)) { }
    }

    /// <summary>Writes a crash file (when a folder is configured) and calls the capture callback; never throws.</summary>
    public static string? Capture(Exception ex, string source)
    {
        try
        {
            var report = Create(ex, source, _host, Version(), Recent.ToList());
            try { _onCapture?.Invoke(report); } catch { /* a logger failing must not hide the crash file */ }
            return _folder is { } folder ? Write(report, folder) : null;
        }
        catch { return null; }
    }

    /// <summary>Builds a redacted report for <paramref name="ex"/>.</summary>
    public static CrashReport Create(Exception ex, string source, string host, string version, IEnumerable<string>? recentLog = null,
        string? home = null, string? user = null, string? machine = null)
    {
        var root = ex is AggregateException { InnerExceptions.Count: 1 } agg ? agg.InnerException! : ex;
        var details = new StringBuilder();
        var depth = 0;
        for (var e = root; e != null && depth < 8; e = e.InnerException, depth++)
        {
            if (depth > 0) details.Append("---> ");
            details.Append(e.GetType().FullName).Append(": ").AppendLine(CrashRedactor.RedactMessage(e.Message, home, user, machine));
            if (e.StackTrace is { } st) details.AppendLine(CrashRedactor.Redact(st, home, user, machine));
        }
        var now = DateTimeOffset.UtcNow;
        return new CrashReport
        {
            Id = $"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            Time = now,
            Version = version,
            Os = $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture})",
            Runtime = RuntimeInformation.FrameworkDescription,
            Host = host,
            Source = source,
            ExceptionType = root.GetType().FullName ?? root.GetType().Name,
            Message = CrashRedactor.RedactMessage(root.Message, home, user, machine),
            Details = details.ToString().TrimEnd(),
            RecentLog = (recentLog ?? []).TakeLast(LogLines).ToList(),
        };
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Saves <paramref name="report"/> as <c>crash-&lt;id&gt;.json</c> and prunes old files.</summary>
    public static string Write(CrashReport report, string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"crash-{report.Id}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, Json));
        foreach (var old in Directory.GetFiles(folder, "crash-*.json").OrderByDescending(f => f, StringComparer.Ordinal).Skip(KeepFiles))
            try { File.Delete(old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return path;
    }

    /// <summary>Crash files not yet reported or dismissed, newest first.</summary>
    public static List<(string Path, CrashReport Report)> Pending(string? folder)
    {
        var list = new List<(string, CrashReport)>();
        if (folder is null || !Directory.Exists(folder)) return list;
        foreach (var f in Directory.GetFiles(folder, "crash-*.json").OrderByDescending(f => f, StringComparer.Ordinal))
        {
            if (File.Exists(f + ".handled")) continue;
            try { if (JsonSerializer.Deserialize<CrashReport>(File.ReadAllText(f), Json) is { } r) list.Add((f, r)); }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return list;
    }

    /// <summary>Marks crash files as dealt with (reported or dismissed) so they are not offered again; the files stay for reference.</summary>
    public static void MarkHandled(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            try { File.WriteAllText(p + ".handled", DateTimeOffset.Now.ToString("O")); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The running app's version (entry assembly's informational version without a +commit suffix).</summary>
    public static string Version()
    {
        var asm = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(CrashReports).Assembly;
        var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        return (info ?? asm.GetName().Version?.ToString(3) ?? "0.0.0").Split('+')[0];
    }

    /// <summary>This computer's operating system, for a problem report without a crash.</summary>
    public static string ThisOs() => $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture})";
}

/// <summary>
/// Prefilled GitHub new-issue links. They open in the user's browser, where the issue form shows everything before it is
/// submitted; nothing is sent by Meridian itself. Field ids match <c>.github/ISSUE_TEMPLATE/bug_report.yml</c>.
/// </summary>
public static class IssueLinks
{
    public const string Repository = "finepointcgi/meridian";
    public const string BugTemplate = "bug_report.yml";

    /// <summary>Browsers and GitHub accept much longer addresses, but some proxies and servers stop near 8 KB.</summary>
    public const int MaxUrlLength = 8000;

    private const int MaxTitle = 120;

    /// <summary>A bug report for a captured crash: title, version, OS and the redacted exception, trimmed to fit <paramref name="maxLength"/>.</summary>
    public static string ForCrash(CrashReport r, int maxLength = MaxUrlLength, string repository = Repository)
    {
        var title = Clip($"Crash: {Short(r.ExceptionType)}{(r.Message.Length > 0 ? " — " + r.Message : "")}", MaxTitle);
        var what = $"Meridian ran into an unexpected error ({r.Source}, {r.Host}) on {r.Time:yyyy-MM-dd HH:mm} UTC.\n\n<!-- What were you doing just before? -->\n";
        var head = $"Report id: {r.Id}\n{r.Summary}\n\n";
        var log = r.RecentLog.Count > 0 ? "\n\nRecent log:\n" + string.Join("\n", r.RecentLog.TakeLast(15)) : "";
        var fields = new List<(string, string)>
        {
            ("title", title), ("labels", "bug,crash"), ("version", r.Version), ("os", $"{r.Os}, {r.Runtime}"), ("what-happened", what),
        };
        // Shrink the stack and log until the address fits: keep the head, drop lines from the end.
        var lines = (r.Details + log).Split('\n').ToList();
        var full = lines.Count;
        while (true)
        {
            var body = head + string.Join("\n", lines) + (lines.Count < full ? "\n… (trimmed: the full report is in the crash file)" : "");
            var url = Build(repository, fields.Append(("logs", body)));
            if (url.Length <= maxLength) return url;
            if (lines.Count == 0) return Build(repository, fields.Append(("logs", Clip(head, 200))));
            lines.RemoveAt(lines.Count - 1);
        }
    }

    /// <summary>A blank bug report with the version and OS filled in (Help ▸ Report a problem).</summary>
    public static string ForProblem(string version, string os, string repository = Repository) =>
        Build(repository, [("labels", "bug"), ("version", version), ("os", os)]);

    private static string Build(string repository, IEnumerable<(string Key, string Value)> fields)
    {
        var sb = new StringBuilder($"https://github.com/{repository}/issues/new?template={BugTemplate}");
        foreach (var (k, v) in fields) if (!string.IsNullOrEmpty(v)) sb.Append('&').Append(k).Append('=').Append(Uri.EscapeDataString(v));
        return sb.ToString();
    }

    private static string Short(string type) => type.LastIndexOf('.') is >= 0 and var i ? type[(i + 1)..] : type;

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>
/// Feeds <see cref="CrashReports"/>: warnings and errors (and Meridian's own information lines) become the "recent log" of the
/// next crash file, and an unhandled exception in a Blazor circuit (the error ASP.NET logs before it ends the circuit) is captured.
/// </summary>
public sealed class CrashLogProvider : ILoggerProvider
{
    /// <summary>The category ASP.NET Core logs unhandled circuit exceptions under.</summary>
    public const string CircuitCategory = "Microsoft.AspNetCore.Components.Server.Circuits.CircuitHost";

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName);
    public void Dispose() { }

    private sealed class Logger(string category) : ILogger
    {
        private readonly bool _ours = category.StartsWith("Meridian", StringComparison.Ordinal);
        private readonly bool _circuit = category.StartsWith(CircuitCategory, StringComparison.Ordinal);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning || (_ours && logLevel >= LogLevel.Information);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var shortCategory = category.LastIndexOf('.') is >= 0 and var i ? category[(i + 1)..] : category;
            if (_circuit && logLevel >= LogLevel.Error && exception != null)
            {
                CrashReports.Capture(exception, "Blazor circuit");
                return;   // the crash file holds the exception; keep it out of the next one's log
            }
            CrashReports.Record($"{logLevel} {shortCategory}: {formatter(state, exception)}{(exception != null ? $" ({exception.GetType().Name})" : "")}");
        }
    }
}
