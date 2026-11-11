using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Meridian.Serialization.History;

namespace Meridian.Data.History;

/// <summary>How far a project's change index is: <paramref name="Indexed"/> of <paramref name="Total"/> versions.</summary>
public sealed record IndexProgress(int Indexed, int Total, bool Running, string? Error = null)
{
    public bool Complete => Indexed >= Total;
    public static readonly IndexProgress Empty = new(0, 0, false);
}

/// <summary>
/// Builds the element change index (<see cref="IMeridianStore.SaveElementChangesAsync"/>): for every committed version, the
/// per-element changes against its parent version. New commits are indexed in the background right after they are made
/// (<see cref="VersionCommitted"/>); versions committed before the index existed are indexed lazily, the first time someone opens an
/// element's history, oldest first, one transaction per version — so the work survives a restart and picks up where it stopped.
/// Several server processes may index the same project: each version is written once (the store refuses a second write).
/// <para>
/// The parent of a version is the previous version on its line. The first version of a branch is compared with the version the
/// branch was made from when that is recorded, else with the newest main version committed before the branch was created. A merge
/// commit is compared with the previous version of its target line, so what the merge brought in is attributed to it.
/// </para>
/// </summary>
public sealed class ElementHistoryIndexer
{
    private static readonly ConditionalWeakTable<IMeridianStore, ElementHistoryIndexer> PerStore = new();

    /// <summary>The indexer of a store (one per store instance, so one per server process).</summary>
    public static ElementHistoryIndexer For(IMeridianStore db) => PerStore.GetValue(db, d => new ElementHistoryIndexer(d));

    private readonly IMeridianStore _db;
    private readonly ConcurrentDictionary<Guid, ProjectState> _projects = new();

    private ElementHistoryIndexer(IMeridianStore db) => _db = db;

    /// <summary>Where failures of background indexing are reported (the web host logs them). Indexing is retried on the next request.</summary>
    public Action<Guid, Exception>? OnError { get; set; }

    /// <summary>Raised (on a worker thread) as background indexing makes progress.</summary>
    public event Action<Guid, IndexProgress>? ProgressChanged;

    /// <summary>The last measured cost of indexing one version, for diagnostics and benchmarks.</summary>
    public TimeSpan LastVersionCost { get; private set; }

    /// <summary>Where <see cref="LastVersionCost"/> went: reading stored versions, parsing them, comparing, writing the rows.</summary>
    public (TimeSpan Load, TimeSpan Parse, TimeSpan Compare, TimeSpan Write, int Rows) LastBreakdown { get; private set; }

    private TimeSpan _load, _parse;

    /// <summary>How long a parsed version stays cached for the next commit's comparison after the project was last indexed.</summary>
    public static TimeSpan CacheLifetime { get; set; } = TimeSpan.FromMinutes(5);

    private sealed class ProjectState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public Task? Background;
        public volatile IndexProgress Progress = IndexProgress.Empty;
        // Parsed versions kept for the next comparison (the newest of each line); at most a few, dropped after CacheLifetime.
        public readonly Dictionary<long, FlatModel> Parsed = new();
        public readonly ConcurrentDictionary<long, string> Seeded = new();   // JSON handed over by a commit (not loaded again)
        public DateTime LastUsed = DateTime.UtcNow;
        public Timer? Expiry;
    }

    private ProjectState State(Guid projectId) => _projects.GetOrAdd(projectId, _ => new ProjectState());

    public IndexProgress Progress(Guid projectId) => _projects.TryGetValue(projectId, out var s) ? s.Progress : IndexProgress.Empty;

    /// <summary>
    /// A version was just committed: indexes it (and anything else pending) in the background. <paramref name="modelJson"/> is the
    /// committed document, if the caller has it, so it needn't be read back.
    /// </summary>
    public void VersionCommitted(Guid projectId, long versionId, string? modelJson = null)
    {
        var s = State(projectId);
        if (modelJson != null) s.Seeded[versionId] = modelJson;
        StartBackground(projectId, s);
    }

    /// <summary>
    /// Makes sure the project's index is complete before history is read: at most <paramref name="inlineLimit"/> missing versions are
    /// indexed now (a few recent commits), more are indexed in the background (the returned progress says how far it is).
    /// </summary>
    public async Task<IndexProgress> EnsureIndexedAsync(Guid projectId, int inlineLimit = 3, CancellationToken ct = default)
    {
        var s = State(projectId);
        if (s.Background is { IsCompleted: false } running)
        {
            // Background indexing is under way (e.g. right after a commit): wait briefly for it when little is left.
            var p = s.Progress;
            if (p.Total - p.Indexed <= inlineLimit) await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(10), ct));
            return s.Progress;
        }
        var versions = await _db.ListHistoryVersionsAsync(projectId);
        var pending = versions.Count(v => !v.Indexed);
        if (pending == 0)
        {
            s.Progress = new IndexProgress(versions.Count, versions.Count, false);
            return s.Progress;
        }
        if (pending <= inlineLimit)
        {
            await IndexPendingAsync(projectId, s, ct);
            return s.Progress;
        }
        s.Progress = new IndexProgress(versions.Count - pending, versions.Count, true);
        StartBackground(projectId, s);
        return s.Progress;
    }

    /// <summary>Indexes every pending version now (tests, benchmarks, an administrator's rebuild).</summary>
    public async Task<IndexProgress> IndexAllAsync(Guid projectId, CancellationToken ct = default)
    {
        var s = State(projectId);
        if (s.Background is { } running) await running.ContinueWith(_ => { }, TaskScheduler.Default);
        await IndexPendingAsync(projectId, s, ct);
        return s.Progress;
    }

    /// <summary>Waits for background indexing of a project to finish (tests).</summary>
    public Task WhenIdleAsync(Guid projectId) =>
        _projects.TryGetValue(projectId, out var s) && s.Background is { } t ? t.ContinueWith(_ => { }, TaskScheduler.Default) : Task.CompletedTask;

    private void StartBackground(Guid projectId, ProjectState s)
    {
        lock (s)
        {
            if (s.Background is { IsCompleted: false }) { s.Background = s.Background.ContinueWith(_ => Run(), TaskScheduler.Default).Unwrap(); return; }
            s.Background = Task.Run(Run);
        }

        async Task Run()
        {
            try { await IndexPendingAsync(projectId, s, CancellationToken.None); }
            catch (Exception ex)
            {
                s.Progress = s.Progress with { Running = false, Error = ex.Message };
                OnError?.Invoke(projectId, ex);
            }
        }
    }

    private async Task IndexPendingAsync(Guid projectId, ProjectState s, CancellationToken ct)
    {
        await s.Gate.WaitAsync(ct);
        try
        {
            var versions = await _db.ListHistoryVersionsAsync(projectId);
            var pending = versions.Where(v => !v.Indexed).OrderBy(v => v.Number).ToList();
            var done = versions.Count - pending.Count;
            s.Progress = new IndexProgress(done, versions.Count, pending.Count > 0);
            if (pending.Count == 0) return;
            ProgressChanged?.Invoke(projectId, s.Progress);
            IReadOnlyList<BranchInfo>? branches = null;
            var parents = versions.Where(v => v.Indexed).ToDictionary(v => v.Id, v => v.ParentVersionId);
            foreach (var v in pending)
            {
                ct.ThrowIfCancellationRequested();
                var t = Stopwatch.StartNew();
                (_load, _parse) = (TimeSpan.Zero, TimeSpan.Zero);
                if (v.BranchId != null && !versions.Any(x => x.BranchId == v.BranchId && x.Number < v.Number))
                    branches ??= await _db.ListBranchesAsync(projectId);
                var parent = ParentOf(v, versions, branches);
                var child = await ParsedAsync(projectId, s, v.Id);
                if (child is null) { done++; continue; }   // deleted meanwhile
                var before = parent is { } pid ? await ParsedAsync(projectId, s, pid) : null;
                var compare = Stopwatch.StartNew();
                var changes = ElementChangeComputer.Compute(before, child);
                var compared = compare.Elapsed;
                var (kind, source) = v.KindRecorded ? (v.Kind, v.MergeSource) : Classify(v);
                var write = Stopwatch.StartNew();
                await _db.SaveElementChangesAsync(projectId, v.Id, parent, kind, source, changes);
                LastBreakdown = (_load, _parse, compared, write.Elapsed, changes.Count);
                parents[v.Id] = parent;
                Keep(s, v, versions, child);
                LastVersionCost = t.Elapsed;
                s.Progress = new IndexProgress(++done, versions.Count, true);
                ProgressChanged?.Invoke(projectId, s.Progress);
            }
            s.Progress = new IndexProgress(done, versions.Count, false);
            ProgressChanged?.Invoke(projectId, s.Progress);
        }
        finally
        {
            s.LastUsed = DateTime.UtcNow;
            s.Gate.Release();
            ScheduleExpiry(s);
        }
    }

    /// <summary>The version <paramref name="v"/> is compared with (null: the first version of the project, which has no rows).</summary>
    public static long? ParentOf(HistoryVersion v, IReadOnlyList<HistoryVersion> versions, IReadOnlyList<BranchInfo>? branches)
    {
        if (v.Indexed) return v.ParentVersionId;
        var previous = versions.Where(x => x.BranchId == v.BranchId && x.Number < v.Number).MaxBy(x => x.Number);
        if (previous != null) return previous.Id;
        if (v.BranchId is not { } b) return null;
        var branch = branches?.FirstOrDefault(x => x.Id == b);
        if (branch?.BaseVersionId is { } baseId && versions.FirstOrDefault(x => x.Id == baseId) is { } based && based.Number < v.Number && based.BranchId != b)
            return baseId;
        var cutoff = branch?.CreatedAt ?? v.CreatedAt;
        return versions.Where(x => x.BranchId is null && x.Number < v.Number && x.CreatedAt <= cutoff).MaxBy(x => x.Number)?.Id
               ?? versions.Where(x => x.BranchId is null && x.Number < v.Number).MaxBy(x => x.Number)?.Id;
    }

    /// <summary>What kind of commit a version is when nobody recorded it: merges and code-sync commits by their messages, system commits by having no author.</summary>
    public static (HistoryVersionKind Kind, string? MergeSource) Classify(HistoryVersion v)
    {
        var m = v.Message;
        if (m.StartsWith("Code sync:", StringComparison.Ordinal)) return (HistoryVersionKind.CodeSync, null);
        if (m.StartsWith("Merge “", StringComparison.Ordinal) && m.IndexOf('”', 7) is var end and > 7 && m.AsSpan(end).StartsWith("” into “"))
            return (HistoryVersionKind.Merge, m[7..end]);
        if (v.AuthorId is null) return (HistoryVersionKind.System, null);
        return (HistoryVersionKind.Commit, null);
    }

    private async Task<FlatModel?> ParsedAsync(Guid projectId, ProjectState s, long versionId)
    {
        lock (s.Parsed) if (s.Parsed.TryGetValue(versionId, out var cached)) return cached;
        var t = Stopwatch.StartNew();
        var json = s.Seeded.TryRemove(versionId, out var seeded) ? seeded : await _db.LoadVersionAsync(projectId, versionId);
        _load += t.Elapsed;
        if (json is null) return null;
        t.Restart();
        var flat = FlatModel.Parse(json);
        _parse += t.Elapsed;
        lock (s.Parsed) s.Parsed[versionId] = flat;
        return flat;
    }

    /// <summary>Keeps the newest parsed version of the two most recently committed lines (the next commit on a line compares with it) and forgets the rest.</summary>
    private static void Keep(ProjectState s, HistoryVersion v, IReadOnlyList<HistoryVersion> versions, FlatModel flat)
    {
        lock (s.Parsed)
        {
            s.Parsed[v.Id] = flat;
            var byId = versions.ToDictionary(x => x.Id);
            var keep = s.Parsed.Keys.Where(byId.ContainsKey).GroupBy(id => byId[id].BranchId)
                .Select(g => g.MaxBy(id => byId[id].Number)).OrderByDescending(id => byId[id].Number).Take(2).ToHashSet();
            foreach (var id in s.Parsed.Keys.Where(id => !keep.Contains(id)).ToList())
                s.Parsed.Remove(id);
        }
    }

    private static void ScheduleExpiry(ProjectState s)
    {
        s.Expiry ??= new Timer(_ =>
        {
            if (DateTime.UtcNow - s.LastUsed < CacheLifetime || !s.Gate.Wait(0)) return;
            try
            {
                lock (s.Parsed) s.Parsed.Clear();
                s.Seeded.Clear();
            }
            finally { s.Gate.Release(); }
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }
}
