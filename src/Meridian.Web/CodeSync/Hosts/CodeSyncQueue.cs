using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Threading.RateLimiting;

namespace Meridian.Web.CodeSync.Hosts;

/// <summary>One sync to run: a commit of a connection, from a webhook or the "Sync now" button.</summary>
public sealed record CodeSyncJob(Guid ProjectId, Guid ConnectionId, string Branch, string Commit, string? CommitUrl, string? Author, string? Message, string Trigger);

/// <summary>
/// Pending syncs. At most one job waits per connection: a newer push replaces the waiting one (only the latest commit
/// matters, since every sync reads a full snapshot). The channel carries connection ids and is bounded, so a flood of
/// connections cannot grow memory without limit.
/// </summary>
public sealed class CodeSyncQueue(int capacity = 256)
{
    private readonly Channel<Guid> _ids = Channel.CreateBounded<Guid>(new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<Guid, CodeSyncJob> _pending = new();
    private readonly object _gate = new();

    /// <summary>Queues a job. Returns false only when the queue is full (the caller answers 503).</summary>
    public bool TryEnqueue(CodeSyncJob job)
    {
        lock (_gate)
        {
            if (_pending.ContainsKey(job.ConnectionId)) { _pending[job.ConnectionId] = job; return true; }   // coalesce
            if (!_ids.Writer.TryWrite(job.ConnectionId)) return false;
            _pending[job.ConnectionId] = job;
            return true;
        }
    }

    public bool IsPending(Guid connectionId) => _pending.ContainsKey(connectionId);

    public int Count => _pending.Count;

    /// <summary>Waits for the next job (the latest one queued for its connection).</summary>
    public async ValueTask<CodeSyncJob> DequeueAsync(CancellationToken ct)
    {
        while (true)
        {
            var id = await _ids.Reader.ReadAsync(ct);
            lock (_gate)
                if (_pending.TryRemove(id, out var job)) return job;
        }
    }

    /// <summary>Takes the next job if one is waiting (tests).</summary>
    public bool TryDequeue(out CodeSyncJob? job)
    {
        job = null;
        while (_ids.Reader.TryRead(out var id))
            lock (_gate)
                if (_pending.TryRemove(id, out job)) return true;
        return false;
    }
}

/// <summary>Per-connection request budget for webhooks and "Sync now".</summary>
public sealed class CodeSyncRateLimiter(int permitsPerWindow = 30, TimeSpan? window = null) : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> _limiter = PartitionedRateLimiter.Create<Guid, Guid>(id =>
        RateLimitPartition.GetFixedWindowLimiter(id, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitsPerWindow, Window = window ?? TimeSpan.FromMinutes(1), QueueLimit = 0,
        }));

    public bool TryAcquire(Guid connectionId)
    {
        using var lease = _limiter.AttemptAcquire(connectionId);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
