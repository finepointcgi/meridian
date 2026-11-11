using Meridian.Core.CodeSync;
using Meridian.Core.CodeSync.Conformance;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.CodeSync.Hosts;
using Meridian.Web.Services;

namespace Meridian.Web.CodeSync.Conformance;

/// <summary>A finished check: the connection and commit it covered and what it found.</summary>
public sealed record ArchitectureReport(Guid ProjectId, Guid ConnectionId, string Commit, ConformanceResult Result, CommitState State, string Description);

/// <summary>
/// Model-to-code architecture checks (docs/ARCHITECTURE-CHECKS.md). After every code-sync run (the runner's
/// <see cref="CodeSyncEvents.RunCompleted"/>), reads the pushed code again, keeps it for later checks, checks it against
/// main's model and posts a separate <c>meridian/architecture</c> commit status. "Check architecture" in the workbench
/// runs the same check on demand against the last synced code.
/// </summary>
public sealed class ArchitectureCheckService(
    IMeridianStore db, CollaborationHub hub, IEnumerable<ICodeReader> readers, ArchitectureStore store,
    IServiceProvider services, IConfiguration config, ILogger<ArchitectureCheckService> log)
{
    public const string StatusContext = "meridian/architecture";
    /// <summary>Deep-link panel that opens the Architecture tab (<c>&amp;panel=architecture</c>).</summary>
    public const string Panel = "architecture";

    private readonly object _chain = new();
    private Task _last = Task.CompletedTask;

    /// <summary>Builds the git host of a connection (tests replace it; default: the code-sync host service).</summary>
    public Func<IntegrationConnection, IGitHost>? HostFactory { get; set; }

    public ArchitectureStore Store => store;

    /// <summary>Raised with the project id after a check stored a new result (open Architecture tabs reload).</summary>
    public event Action<Guid>? Checked;

    /// <summary>The checks queued so far have finished (tests).</summary>
    public Task Idle { get { lock (_chain) return _last; } }

    /// <summary>Runs checks after code-sync runs: subscribes to <see cref="CodeSyncEvents.RunCompleted"/>.</summary>
    public void Attach(CodeSyncEvents events) => events.RunCompleted += Queue;

    public void Detach(CodeSyncEvents events) => events.RunCompleted -= Queue;

    /// <summary>Queues a check (one at a time, in order); the sync that raised it does not wait.</summary>
    public void Queue(CodeSyncRequest request, CodeSyncOutcome outcome)
    {
        lock (_chain)
            _last = _last.ContinueWith(async _ =>
            {
                try { await CheckRunAsync(request, outcome); }
                catch (Exception ex) { log.LogWarning(ex, "Architecture check of {Repo}@{Commit} failed", request.RepoDisplay, request.Commit); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Checks a sync's code against main's model, stores the code and the result, and posts the status.</summary>
    public async Task<ArchitectureReport?> CheckRunAsync(CodeSyncRequest request, CodeSyncOutcome outcome, CancellationToken ct = default)
    {
        if (outcome.Status == CodeSyncStatus.Failed) return null;
        var reader = readers.FirstOrDefault(r => request.Files.Any(f => r.Handles(f.Path)));
        if (reader is null) return null;
        var code = reader.Read(request.Files.Where(f => reader.Handles(f.Path)).ToList(), ct);
        var model = ProjectFile.Deserialize(await VersionControl.HeadJsonAsync(db, hub, request.ProjectId, null));
        var result = Check(model, code, request.ConnectionId, request.IsFullSnapshot);
        var packed = CodeModelJson.Pack(code);
        var rec = await store.UpdateAsync(request.ProjectId, request.ConnectionId, r =>
        {
            r.RepoDisplay = request.RepoDisplay;
            r.Commit = request.Commit;
            r.CommitUrl = request.CommitUrl;
            r.IsFullSnapshot = request.IsFullSnapshot;
            r.PackedCode = packed;
            Note(r, result);
        });
        var report = await PostAsync(request.ProjectId, request.ConnectionId, rec, result, ct);
        Raise(request.ProjectId);
        return report;
    }

    /// <summary>Checks the stored code of a connection against <paramref name="model"/>; null when no code was synced yet.</summary>
    public static ConformanceResult? Check(ModelRepository model, ArchitectureRecord rec) =>
        rec.Code() is { } code ? Check(model, code, rec.ConnectionId, rec.IsFullSnapshot) : null;

    public static ConformanceResult Check(ModelRepository model, CodeModel code, Guid connectionId, bool complete) =>
        ArchitectureConformance.Check(model, code, ConformanceOptions.FromModel(model, connectionId.ToString()) with { CodeIsComplete = complete });

    /// <summary>On demand against main's model: stores the new result and posts the status again for the last synced commit.</summary>
    public async Task<ArchitectureReport?> RecheckAsync(Guid projectId, Guid connectionId, ModelRepository mainModel, CancellationToken ct = default)
    {
        if (await store.FindAsync(projectId, connectionId) is not { } rec || Check(mainModel, rec) is not { } result) return null;
        rec = await store.UpdateAsync(projectId, connectionId, r => Note(r, result));
        var report = await PostAsync(projectId, connectionId, rec, result, ct);
        Raise(projectId);
        return report;
    }

    /// <summary>Changes "Fail pull requests on architecture violations" for one repository.</summary>
    public async Task SetFailPullRequestsAsync(Guid projectId, Guid connectionId, bool on)
    {
        await store.UpdateAsync(projectId, connectionId, r => r.FailPullRequests = on);
        Raise(projectId);
    }

    private static void Note(ArchitectureRecord r, ConformanceResult result)
    {
        r.CheckedAt = DateTime.UtcNow;
        r.Summary = result.Summary;
        r.Errors = result.Errors;
        r.Warnings = result.Warnings;
    }

    private void Raise(Guid projectId)
    {
        try { Checked?.Invoke(projectId); }
        catch (Exception) { /* a closed circuit must not fail a check */ }
    }

    /// <summary>Longest status description the hosts keep (GitHub and GitLab: 140 characters).</summary>
    public const int MaxDescription = 140;

    /// <summary>The status to post: failure with "N violations" only when the repository fails pull requests on them;
    /// otherwise success with a summary (warnings never fail). With violations it cites the first line that breaks the
    /// architecture ("…, first at src/Thermal/ThermalControl.cs:42"), shortening the path to fit the hosts' limit.</summary>
    public static (CommitState State, string Description) StatusFor(ConformanceResult result, bool failPullRequests)
    {
        if (result.Errors == 0) return (CommitState.Success, result.Warnings == 0 ? "No architecture violations" : $"No architecture violations ({result.Summary})");
        var n = $"{result.Errors} violation{(result.Errors == 1 ? "" : "s")}";
        var (state, text) = failPullRequests
            ? (CommitState.Failure, n + (result.Warnings > 0 ? $" ({result.Warnings} warning{(result.Warnings == 1 ? "" : "s")})" : ""))
            : (CommitState.Success, $"{result.Summary} (not enforced)");
        var first = result.Violations.FirstOrDefault(v => v.Severity == ViolationSeverity.Error && v.FirstLocation.Length > 0);
        return (state, first is null ? text : Cite(text, first.FirstLocation));
    }

    /// <summary>"text, first at path:line"; a long path keeps its last folders ("…/Thermal/ThermalControl.cs:42").</summary>
    private static string Cite(string text, string location)
    {
        const string sep = ", first at ";
        var room = MaxDescription - text.Length - sep.Length;
        if (room <= 0) return text;
        if (location.Length > room)
        {
            var parts = location.Split('/');
            var tail = parts[^1];
            for (var i = parts.Length - 2; i >= 0 && ("…/" + parts[i] + "/" + tail).Length <= room; i--) tail = parts[i] + "/" + tail;
            location = "…/" + tail;
            if (location.Length > room) return text;
        }
        return text + sep + location;
    }

    private async Task<ArchitectureReport> PostAsync(Guid projectId, Guid connectionId, ArchitectureRecord rec, ConformanceResult result, CancellationToken ct)
    {
        var (state, description) = StatusFor(result, rec.FailPullRequests);
        var report = new ArchitectureReport(projectId, connectionId, rec.Commit, result, state, description);
        if (!GitHostBase.IsSha(rec.Commit)) return report;
        if (await db.FindIntegrationAsync(connectionId) is not { Kind: CodeRepoKinds.CodeRepo } c || c.ProjectId != projectId) return report;
        if (!CodeRepoConfig.Parse(c.ConfigJson).PostStatus) return report;
        try
        {
            var host = HostFactory?.Invoke(c) ?? services.GetRequiredService<CodeSyncHostService>().Host(c);
            using (GitHostFactory.UseStatusContext(StatusContext))
                await host.PostCommitStatusAsync(rec.Commit, state, description, TargetUrl(projectId), ct);
        }
        catch (GitHostException ex) { log.LogWarning("Posting the architecture status for {Connection} failed: {Error}", connectionId, ex.Message); }
        return report;
    }

    /// <summary>The workbench with the Architecture tab open; absolute when the public URL is known.</summary>
    public string? TargetUrl(Guid projectId)
    {
        var baseUrl = config["Meridian:PublicUrl"] is { Length: > 0 } p ? p : services.GetService<CodeSyncHostService>()?.PublicBaseUrl;
        return baseUrl is { Length: > 0 } b ? $"{b.TrimEnd('/')}/?project={projectId}&panel={Panel}" : null;
    }

    /// <summary>A link to a file and line at the synced commit, from the commit URL:
    /// GitHub <c>/commit/sha</c> → <c>/blob/sha/path#L12</c>; GitLab <c>/-/commit/sha</c> → <c>/-/blob/sha/path#L12</c>;
    /// Bitbucket Cloud <c>/commits/sha</c> → <c>/src/sha/path#lines-12</c>; Bitbucket Data Center
    /// <c>/projects/K/repos/r/commits/sha</c> → <c>/projects/K/repos/r/browse/path?at=sha#12</c>. The commit URL itself when
    /// it has another shape; null when it is not an http(s) URL.</summary>
    public static string? FileUrl(string? commitUrl, string commit, string? path, int line)
    {
        if (!Uri.TryCreate(commitUrl, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return null;
        if (string.IsNullOrEmpty(path)) return u.ToString();
        var url = u.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var encoded = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        var dc = url.LastIndexOf("/commits/", StringComparison.Ordinal);
        if (dc > 0 && url.IndexOf("/projects/", StringComparison.Ordinal) is var pi and >= 0 && pi < dc && url.IndexOf("/repos/", pi, StringComparison.Ordinal) is var ri and > 0 && ri < dc)
        {
            var sha = url[(dc + "/commits/".Length)..];
            if (sha.Length == 0) sha = commit;
            return $"{url[..dc]}/browse/{encoded}?at={Uri.EscapeDataString(sha)}{(line > 0 ? $"#{line}" : "")}";
        }
        foreach (var (marker, replacement, anchor) in new[] { ("/-/commit/", "/-/blob/", "#L"), ("/commit/", "/blob/", "#L"), ("/commits/", "/src/", "#lines-") })
        {
            var i = url.LastIndexOf(marker, StringComparison.Ordinal);
            if (i < 0) continue;
            var sha = url[(i + marker.Length)..];
            if (sha.Length == 0) sha = commit;
            return $"{url[..i]}{replacement}{sha}/{encoded}{(line > 0 ? $"{anchor}{line}" : "")}";
        }
        return u.ToString();
    }
}

/// <summary>Hooks the architecture check onto code-sync runs for the app's lifetime.</summary>
public sealed class ArchitectureCheckBridge(ArchitectureCheckService checks, CodeSyncEvents events) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        checks.Attach(events);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        checks.Detach(events);
        return Task.CompletedTask;
    }
}

/// <summary>Registers the architecture check, its storage, the run hook and the per-tab state.</summary>
public static class ArchitectureSetup
{
    public static IServiceCollection AddMeridianArchitectureChecks(this IServiceCollection services)
    {
        services.AddSingleton<ArchitectureStore>();
        services.AddSingleton<ArchitectureCheckService>();
        services.AddHostedService<ArchitectureCheckBridge>();
        services.AddScoped<ArchitectureState>();
        return services;
    }
}
