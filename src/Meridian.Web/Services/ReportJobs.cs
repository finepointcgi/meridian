using System.Collections.Concurrent;
using System.Security.Cryptography;
using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Serialization.Reports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace Meridian.Web.Services;

public enum ReportJobState { Queued, Running, Completed, Failed, Cancelled }

/// <summary>
/// A report being generated (or generated) on the server into a temporary file. The file is downloaded through
/// <c>GET /api/reports/{id}/file</c>, which streams it to the browser (or, in the desktop app, to the save-file bridge):
/// big documents never travel through a JavaScript interop call. Ids are random; only the owner can download.
/// </summary>
public sealed class ReportJob
{
    internal ReportJob(string title, string fileName, string mime, Guid? ownerId, Guid? projectId, bool background, long estimatedPages,
                       IReadOnlyList<string[]>? visiblePackages)
    {
        Title = title; FileName = fileName; Mime = mime; OwnerId = ownerId; ProjectId = projectId; Background = background;
        EstimatedPages = estimatedPages; VisiblePackages = visiblePackages;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; }
    public string FileName { get; }
    public string Mime { get; }
    public Guid? OwnerId { get; }
    public Guid? ProjectId { get; }
    /// <summary>Started as a background job (the status bar follows it and a notification says when it is ready).</summary>
    public bool Background { get; }
    public long EstimatedPages { get; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; internal set; }

    public ReportJobState State { get; internal set; } = ReportJobState.Queued;
    public double Fraction { get; internal set; }
    public string Phase { get; internal set; } = "Waiting to start…";
    public int Section { get; internal set; }
    public int Sections { get; internal set; }
    public long Elements { get; internal set; }
    public long Length { get; internal set; }
    public string? Error { get; internal set; }
    /// <summary>The user dismissed it from the status bar, or it was downloaded (the file stays available until it expires).</summary>
    public bool Dismissed { get; set; }
    /// <summary>The dialog that started it is open and will report the outcome itself (no notification needed).</summary>
    public bool Watched { get; set; }

    public bool IsDone => State is ReportJobState.Completed or ReportJobState.Failed or ReportJobState.Cancelled;

    /// <summary>"Section 3 of 7 · 12,345 elements" (empty before the first progress report).</summary>
    public string Detail => Sections > 0 ? $"Section {Math.Max(1, Section)} of {Sections} · {Elements:N0} elements" : Elements > 0 ? $"{Elements:N0} elements" : "";

    internal string? FilePath { get; set; }
    internal CancellationTokenSource Cts { get; } = new();
    internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Ancestor chains (the package's id first) of the packages whose contents the owner could see when the report
    /// was made; null when package permissions don't apply (no project, or nothing hidden by design).</summary>
    internal IReadOnlyList<string[]>? VisiblePackages { get; }
    internal DateTime LastNotified;
    internal int TrailingScheduled;
}

/// <summary>
/// Runs reports as jobs: a small number at a time on worker threads, each writing straight to a temporary file with
/// progress and cancellation. Small reports are awaited by the dialog and downloaded at once; big ones keep running
/// when the dialog closes, and the status bar shows their progress and a "Download" button when they are ready.
/// Finished jobs and their files are removed after <see cref="Retention"/>.
/// </summary>
public sealed class ReportJobService : IDisposable
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(2);
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(2);

    private static readonly Lazy<ReportJobService> _fallback = new(() => new ReportJobService());
    /// <summary>For hosts that don't register the service (component tests): one process-wide instance.</summary>
    public static ReportJobService Fallback => _fallback.Value;

    /// <summary>The registered service, else <see cref="Fallback"/>.</summary>
    public static ReportJobService From(IServiceProvider services) => services.GetService(typeof(ReportJobService)) as ReportJobService ?? Fallback;

    private readonly ConcurrentDictionary<Guid, ReportJob> _jobs = new();
    private readonly ConcurrentDictionary<string, (Guid JobId, DateTime Expires)> _tickets = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots;
    private readonly ILogger _log;
    private readonly Timer _sweep;
    public string Directory { get; }

    /// <summary>Raised (on a worker thread, at most a few times a second per job) when a job's progress or state changes.</summary>
    public event Action<ReportJob>? Changed;

    /// <summary>Every progress report, unthrottled, raised synchronously on the job's worker thread (after <see cref="Changed"/>
    /// handling): for logging and metrics; a handler that blocks holds the job.</summary>
    public event Action<ReportJob, ReportProgress>? Progress;

    public ReportJobService(ILogger<ReportJobService>? log = null, int concurrency = 2, string? directory = null)
    {
        _log = log ?? (ILogger)NullLogger.Instance;
        _slots = new SemaphoreSlim(Math.Max(1, concurrency));
        Directory = directory ?? Path.Combine(Path.GetTempPath(), "meridian-reports", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        _sweep = new Timer(_ => Sweep(DateTime.UtcNow), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Starts a job that writes its output with <paramref name="write"/> (on a worker thread; report progress, honour the token).
    /// <paramref name="access"/> and <paramref name="model"/> record which packages the owner could see, so a download after
    /// the owner lost access to one of them is refused.
    /// </summary>
    public ReportJob Start(string title, string fileName, string mime, Action<Stream, IProgress<ReportProgress>, CancellationToken> write,
                           Guid? ownerId, Guid? projectId, bool background = false, long estimatedPages = 0,
                           ModelRepository? model = null, AccessView? access = null)
    {
        var job = new ReportJob(title, fileName, mime, ownerId, projectId, background, estimatedPages,
                                projectId != null && model != null ? VisiblePackages(model, access ?? AccessView.Full) : null);
        _jobs[job.Id] = job;
        Raise(job, force: true);
        _ = Task.Run(() => RunAsync(job, write));
        return job;
    }

    private async Task RunAsync(ReportJob job, Action<Stream, IProgress<ReportProgress>, CancellationToken> write)
    {
        var ct = job.Cts.Token;
        var path = Path.Combine(Directory, job.Id.ToString("N"));
        try
        {
            await _slots.WaitAsync(ct);
            try
            {
                job.State = ReportJobState.Running;
                job.Phase = "Starting…";
                Raise(job, force: true);
                job.FilePath = path;
                await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan))
                    write(file, new JobProgress(this, job), ct);
                ct.ThrowIfCancellationRequested();
                job.Length = new FileInfo(path).Length;
                job.Fraction = 1;
                job.Phase = "Ready";
                job.State = ReportJobState.Completed;
            }
            finally { _slots.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.State = ReportJobState.Cancelled;
            job.Phase = "Cancelled";
            DeleteFile(job);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Report {Title} failed", job.Title);
            job.State = ReportJobState.Failed;
            job.Error = FriendlyError.Describe(ex);
            job.Phase = "Failed";
            DeleteFile(job);
        }
        job.FinishedAt = DateTime.UtcNow;
        Raise(job, force: true);
        job.Done.TrySetResult();
    }

    public ReportJob? Find(Guid id) => _jobs.TryGetValue(id, out var j) ? j : null;

    /// <summary>The user's jobs, newest first.</summary>
    public IReadOnlyList<ReportJob> JobsFor(Guid? ownerId) =>
        _jobs.Values.Where(j => j.OwnerId == ownerId).OrderByDescending(j => j.CreatedAt).ToList();

    /// <summary>Completes when the job has finished (whatever the outcome).</summary>
    public Task WhenDone(ReportJob job) => job.Done.Task;

    public void Cancel(ReportJob job)
    {
        if (job.IsDone) return;
        job.Phase = "Cancelling…";
        Raise(job, force: true);
        job.Cts.Cancel();
    }

    /// <summary>Forgets a job and deletes its file (cancelling it first if it is still running).</summary>
    public void Remove(ReportJob job)
    {
        Cancel(job);
        if (_jobs.TryRemove(job.Id, out _) && job.IsDone) DeleteFile(job);
    }

    /// <summary>The finished file, opened for reading (the download endpoint streams it).</summary>
    public string? FileOf(ReportJob job) => job.State == ReportJobState.Completed && job.FilePath is { } p && File.Exists(p) ? p : null;

    // ------------------------------------------------------------ download tickets (desktop app)

    /// <summary>
    /// A single-use ticket that lets the desktop app's host process (which has no browser cookie) download this job's file
    /// once, within two minutes. The page asks for one and hands the URL to the host's save-file bridge.
    /// </summary>
    public string IssueTicket(ReportJob job)
    {
        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _tickets[ticket] = (job.Id, DateTime.UtcNow + TicketLifetime);
        return ticket;
    }

    /// <summary>Redeems a ticket for <paramref name="job"/> (it can't be used again).</summary>
    public bool RedeemTicket(ReportJob job, string? ticket)
    {
        if (string.IsNullOrEmpty(ticket) || !_tickets.TryRemove(ticket, out var t)) return false;
        return t.JobId == job.Id && t.Expires > DateTime.UtcNow;
    }

    // ------------------------------------------------------------ package permissions

    /// <summary>Ancestor chains of the packages whose contents <paramref name="access"/> shows.</summary>
    public static IReadOnlyList<string[]> VisiblePackages(ModelRepository model, AccessView access)
    {
        var list = new List<string[]>();
        foreach (var p in model.OfKind(ElementKind.Package))
        {
            var chain = p.AncestorsAndSelf().Select(a => a.Id).ToArray();
            if (!chain.Any(id => access.ExplicitAt(id) == PackageAccess.None)) list.Add(chain);
        }
        return list;
    }

    /// <summary>True when <paramref name="now"/> hides the contents of a package the report was made with: the file may
    /// hold content its owner may no longer see, so it isn't handed out.</summary>
    public static bool PermissionsNarrowed(ReportJob job, AccessView now) =>
        job.VisiblePackages is { } visible && now.HidesAnything && visible.Any(chain => chain.Any(id => now.ExplicitAt(id) == PackageAccess.None));

    // ------------------------------------------------------------ housekeeping

    /// <summary>Removes finished jobs (and their files) older than <see cref="Retention"/>, and expired tickets. Runs every five minutes.</summary>
    public void Sweep(DateTime now)
    {
        foreach (var j in _jobs.Values)
            if (j.IsDone && j.FinishedAt is { } f && now - f > Retention && _jobs.TryRemove(j.Id, out _)) DeleteFile(j);
        foreach (var (k, t) in _tickets)
            if (t.Expires <= now) _tickets.TryRemove(k, out _);
    }

    private void DeleteFile(ReportJob job)
    {
        if (job.FilePath is not { } p) return;
        try { File.Delete(p); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogDebug(ex, "Could not delete {Path}", p); }
    }

    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(250);

    private void Raise(ReportJob job, bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && now - job.LastNotified < Throttle)
        {
            // Too soon: make sure the latest state still goes out shortly (a job may then sit quietly for a long time).
            if (Interlocked.Exchange(ref job.TrailingScheduled, 1) == 0)
                _ = Task.Delay(Throttle).ContinueWith(_ =>
                {
                    Volatile.Write(ref job.TrailingScheduled, 0);
                    Raise(job, force: true);
                }, TaskScheduler.Default);
            return;
        }
        job.LastNotified = now;
        try { Changed?.Invoke(job); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { _log.LogDebug(ex, "Report job listener failed"); }
    }

    public void Dispose()
    {
        _sweep.Dispose();
        foreach (var j in _jobs.Values) j.Cts.Cancel();
        try { System.IO.Directory.Delete(Directory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogDebug(ex, "Could not delete {Dir}", Directory); }
    }

    private sealed class JobProgress(ReportJobService service, ReportJob job) : IProgress<ReportProgress>
    {
        public void Report(ReportProgress p)
        {
            if (job.Cts.IsCancellationRequested) return;
            if (!double.IsNaN(p.Fraction)) job.Fraction = Math.Max(job.Fraction, Math.Clamp(p.Fraction, 0, 1));
            job.Phase = p.Phase;
            if (p.Sections > 0) { job.Section = p.Section; job.Sections = p.Sections; }
            if (p.Elements > 0) job.Elements = p.Elements;
            service.Raise(job);
            service.Progress?.Invoke(job, p);
        }
    }
}

/// <summary>Hands a finished report to the browser (a streamed download) or to the desktop app's save-file bridge.</summary>
public static class ReportDownloads
{
    public static string Url(ReportJob job) => $"api/reports/{job.Id}/file";

    public static async Task DownloadAsync(Microsoft.JSInterop.IJSRuntime js, ReportJobService jobs, ReportJob job)
    {
        var url = Url(job);
        // The desktop host fetches the file itself (it has no browser cookie): give it a single-use ticket.
        if (await IsDesktopAsync(js)) url += "?ticket=" + Uri.EscapeDataString(jobs.IssueTicket(job));
        await js.InvokeVoidAsync("meridian.downloadUrl", job.FileName, url);
    }

    /// <summary>Opens an HTML report in a new browser tab (the desktop app saves it instead).</summary>
    public static async Task OpenAsync(Microsoft.JSInterop.IJSRuntime js, ReportJobService jobs, ReportJob job)
    {
        if (await IsDesktopAsync(js)) { await DownloadAsync(js, jobs, job); return; }
        await js.InvokeVoidAsync("meridian.openUrl", Url(job) + "?inline=true");
    }

    private static async Task<bool> IsDesktopAsync(Microsoft.JSInterop.IJSRuntime js)
    {
        try { return await js.InvokeAsync<bool>("meridian.isDesktop"); }
        catch (Microsoft.JSInterop.JSException) { return false; }
    }
}
