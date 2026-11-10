using System.Text;
using Meridian.Core.CodeSync;
using Meridian.Data;
using Meridian.Web.Integrations;
using Microsoft.Extensions.Primitives;

namespace Meridian.Web.CodeSync.Hosts;

/// <summary>Answer to an incoming webhook request.</summary>
public sealed record WebhookResult(int StatusCode, string Message);

/// <summary>A saved connection, plus its webhook secret when one was just generated (shown once).</summary>
public sealed record CodeRepoSaveResult(IntegrationConnection Connection, string? NewWebhookSecret);

/// <summary>Stand-in until the review-branch runner is registered: every sync fails with a clear message.</summary>
public sealed class NullCodeSyncRunner : ICodeSyncRunner
{
    public static readonly NullCodeSyncRunner Instance = new();

    public Task<CodeSyncOutcome> RunAsync(CodeSyncRequest request, CancellationToken ct = default) =>
        Task.FromResult(new CodeSyncOutcome(CodeSyncStatus.Failed, "Code sync runner not available", null, null));
}

/// <summary>
/// Code repository connections: settings (token and webhook secret encrypted like other integrations), connection
/// tests, incoming webhooks, "Sync now", and the sync pipeline the background worker runs —
/// pending status → archive snapshot → <see cref="ICodeSyncRunner"/> → final status.
/// </summary>
public sealed class CodeSyncHostService(
    IMeridianStore db, IntegrationService integrations, IHttpClientFactory httpFactory, IServiceScopeFactory scopes,
    CodeSyncQueue queue, CodeSyncRateLimiter limiter, IConfiguration configuration, ILogger<CodeSyncHostService> log)
{
    public const string HttpClientName = "meridian-code-sync";
    /// <summary>Largest webhook body accepted (GitHub caps payloads at 25 MB; pushes are far smaller).</summary>
    public const int MaxWebhookBytes = 10 * 1024 * 1024;

    private readonly SemaphoreSlim _statusGate = new(1, 1);
    private string? _seenBaseUrl;

    /// <summary>Archive limits (tests shrink them).</summary>
    public SnapshotLimits Limits { get; set; } = SnapshotLimits.Default;

    /// <summary>Raised with the connection id whenever a connection's last-sync status changes.</summary>
    public event Action<Guid>? StatusChanged;

    // ---------------------------------------------------------------- settings

    public Task<IReadOnlyList<IntegrationConnection>> ListAsync(Guid projectId) => integrations.ListAsync(projectId, CodeRepoKinds.CodeRepo);

    /// <summary>Settings of a connection (the review-branch runner reads <see cref="CodeRepoConfig.EffectiveRootPackage"/> here).</summary>
    public async Task<CodeRepoConfig?> ConfigAsync(Guid connectionId) =>
        await db.FindIntegrationAsync(connectionId) is { Kind: CodeRepoKinds.CodeRepo } c ? CodeRepoConfig.Parse(c.ConfigJson) : null;

    /// <summary>
    /// Creates or updates a connection. An empty <paramref name="newToken"/> keeps the stored token. A new connection
    /// gets a webhook secret, returned once in the result.
    /// </summary>
    public async Task<CodeRepoSaveResult> SaveAsync(Guid projectId, Guid? id, string? name, CodeRepoConfig config, string? newToken)
    {
        config = config.Clone();
        config.Normalize();
        if (config.Validate() is { } err) throw new GitHostException(err);
        IntegrationConnection? existing = id is { } i ? await db.FindIntegrationAsync(i) : null;
        if (existing != null && (existing.ProjectId != projectId || existing.Kind != CodeRepoKinds.CodeRepo)) throw new InvalidOperationException("That integration belongs to another project.");
        if (existing is null && string.IsNullOrWhiteSpace(newToken)) throw new GitHostException("Enter the access token.");

        string? newSecret = null;
        var stored = existing is null ? null : CodeRepoConfig.Parse(existing.ConfigJson);
        config.ProtectedWebhookSecret = stored?.ProtectedWebhookSecret;
        config.LastSync = stored?.LastSync;
        if (config.ProtectedWebhookSecret is null)
        {
            newSecret = CodeWebhooks.NewSecret();
            config.ProtectedWebhookSecret = integrations.Protect(newSecret);
        }
        var c = await integrations.SaveAsync(projectId, id, CodeRepoKinds.CodeRepo, string.IsNullOrWhiteSpace(name) ? config.Display : name, config.ToJson(), newToken);
        return new CodeRepoSaveResult(c, newSecret);
    }

    /// <summary>Replaces the webhook secret; returns the new one (shown once).</summary>
    public async Task<string> RegenerateWebhookSecretAsync(Guid projectId, Guid id)
    {
        var c = await Find(projectId, id);
        var config = CodeRepoConfig.Parse(c.ConfigJson);
        var secret = CodeWebhooks.NewSecret();
        config.ProtectedWebhookSecret = integrations.Protect(secret);
        await integrations.SaveAsync(projectId, id, CodeRepoKinds.CodeRepo, c.Name, config.ToJson(), null);
        return secret;
    }

    public Task DeleteAsync(Guid projectId, Guid id) => integrations.DeleteAsync(projectId, id);

    /// <summary>Tests unsaved settings; an empty token falls back to the stored one of <paramref name="existingId"/>.</summary>
    public async Task<string> TestAsync(Guid projectId, CodeRepoConfig config, string? newToken, Guid? existingId, CancellationToken ct = default)
    {
        config = config.Clone();
        config.Normalize();
        if (config.Validate() is { } err) throw new GitHostException(err);
        var token = newToken;
        if (string.IsNullOrWhiteSpace(token) && existingId is { } id && await db.FindIntegrationAsync(id) is { } c && c.ProjectId == projectId && c.Kind == CodeRepoKinds.CodeRepo)
            token = integrations.Unprotect(c.ProtectedSecret);
        if (string.IsNullOrWhiteSpace(token)) throw new GitHostException("Enter the access token.");
        return await Host(config, token.Trim()).TestConnectionAsync(ct);
    }

    private async Task<IntegrationConnection> Find(Guid projectId, Guid id) =>
        await db.FindIntegrationAsync(id) is { Kind: CodeRepoKinds.CodeRepo } c && c.ProjectId == projectId
            ? c : throw new GitHostException("That repository connection no longer exists.");

    public IGitHost Host(CodeRepoConfig config, string token) =>
        GitHostFactory.Create(config, token, httpFactory.CreateClient(HttpClientName), Limits);

    public IGitHost Host(IntegrationConnection c)
    {
        var token = integrations.Unprotect(c.ProtectedSecret)
            ?? throw new GitHostException($"The access token for “{c.Name}” is missing or can no longer be decrypted — enter it again in Integrations.");
        return Host(CodeRepoConfig.Parse(c.ConfigJson), token);
    }

    /// <summary>The URL to paste into the git host's webhook settings.</summary>
    public static string WebhookUrl(string baseUrl, Guid connectionId) => $"{baseUrl.TrimEnd('/')}/api/code-sync/{connectionId}/webhook";

    /// <summary>The public base URL: Meridian:PublicUrl, else the last one a request or the UI reported.</summary>
    public string? PublicBaseUrl => configuration["Meridian:PublicUrl"] is { Length: > 0 } p ? p.TrimEnd('/') : _seenBaseUrl;

    /// <summary>Remembers the base URL users reach Meridian at (for review links in commit statuses).</summary>
    public void NoteBaseUrl(string? baseUrl)
    {
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            _seenBaseUrl = u.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    // ---------------------------------------------------------------- triggers

    /// <summary>"Sync now": resolves the branch head and queues it. Returns a short message for the UI.</summary>
    public async Task<string> SyncNowAsync(Guid projectId, Guid connectionId, CancellationToken ct = default)
    {
        var c = await Find(projectId, connectionId);
        if (!limiter.TryAcquire(connectionId)) throw new GitHostException("Too many syncs for this repository; wait a minute.");
        var config = CodeRepoConfig.Parse(c.ConfigJson);
        var head = await Host(c).ResolveBranchHeadAsync(config.Branch, ct);
        var job = new CodeSyncJob(projectId, connectionId, config.Branch, head.Sha, head.Url, head.Author, head.Message, "manual");
        if (!queue.TryEnqueue(job)) throw new GitHostException("The sync queue is full; try again shortly.");
        await SetLastSyncAsync(connectionId, head.Sha, "queued", "Queued");
        return $"Sync of {config.Branch} at {Short(head.Sha)} queued.";
    }

    /// <summary>Handles a webhook request (the endpoint passes the raw body so signatures can be checked).</summary>
    public async Task<WebhookResult> HandleWebhookAsync(Guid connectionId, IHeaderDictionary headers, byte[] body, CancellationToken ct = default)
    {
        var c = await db.FindIntegrationAsync(connectionId);
        if (c is null || c.Kind != CodeRepoKinds.CodeRepo) return new(404, "Unknown repository connection.");
        var config = CodeRepoConfig.Parse(c.ConfigJson);
        var secret = integrations.Unprotect(config.ProtectedWebhookSecret);
        if (string.IsNullOrEmpty(secret)) return new(401, "This connection has no webhook secret; generate one in Meridian.");

        WebhookEventKind kind;
        if (config.Host == GitHostKind.GitHub)
        {
            if (!CodeWebhooks.VerifyGitHub(secret, body, Header(headers, CodeWebhooks.GitHubSignatureHeader))) return new(401, "Missing or invalid signature.");
            kind = CodeWebhooks.GitHubEvent(Header(headers, CodeWebhooks.GitHubEventHeader));
        }
        else
        {
            if (!CodeWebhooks.VerifyGitLab(secret, Header(headers, CodeWebhooks.GitLabTokenHeader))) return new(401, "Missing or invalid token.");
            kind = CodeWebhooks.GitLabEvent(Header(headers, CodeWebhooks.GitLabEventHeader));
        }
        if (!limiter.TryAcquire(connectionId)) return new(429, "Too many requests for this connection.");
        if (kind == WebhookEventKind.Ping) return new(200, "pong");
        if (kind != WebhookEventKind.Push) return new(202, "Ignored: not a push event.");

        var json = Encoding.UTF8.GetString(body);
        var push = config.Host == GitHostKind.GitHub ? CodeWebhooks.ParseGitHubPush(json) : CodeWebhooks.ParseGitLabPush(json);
        if (push is null) return new(400, "Not a push payload.");
        if (push.Branch != config.Branch) return new(202, $"Ignored: push to {Clip(push.Ref)}; this connection follows {config.Branch}.");
        if (push.Deleted) return new(202, "Ignored: the branch was deleted.");
        if (!GitHostBase.IsSha(push.After)) return new(400, "The push has no commit id.");
        if (!config.AutoSync) return new(202, "Ignored: automatic sync is off for this connection.");

        var job = new CodeSyncJob(c.ProjectId, c.Id, config.Branch, push.After, push.CommitUrl, push.Author, push.Message, "webhook");
        if (!queue.TryEnqueue(job)) return new(503, "The sync queue is full; try again later.");
        await SetLastSyncAsync(c.Id, push.After, "queued", "Queued");
        return new(202, $"Queued {Short(push.After)}.");
    }

    private static string? Header(IHeaderDictionary headers, string name) =>
        headers.TryGetValue(name, out StringValues v) && v.Count > 0 ? v[0] : null;

    private static string Clip(string s) => s.Length > 120 ? s[..120] : s;
    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    // ---------------------------------------------------------------- pipeline

    /// <summary>Runs one job: pending status, snapshot, runner, final status. Never throws for sync failures.</summary>
    public async Task<CodeSyncOutcome> ProcessAsync(CodeSyncJob job, CancellationToken ct = default)
    {
        var c = await db.FindIntegrationAsync(job.ConnectionId);
        if (c is null || c.Kind != CodeRepoKinds.CodeRepo || c.ProjectId != job.ProjectId)
            return new CodeSyncOutcome(CodeSyncStatus.Failed, "The repository connection was removed.", null, null);
        var config = CodeRepoConfig.Parse(c.ConfigJson);

        IGitHost host;
        try { host = Host(c); }
        catch (GitHostException ex)
        {
            await SetLastSyncAsync(c.Id, job.Commit, "failed", ex.Message);
            return new CodeSyncOutcome(CodeSyncStatus.Failed, ex.Message, null, null);
        }

        await SetLastSyncAsync(c.Id, job.Commit, "running", "Reading the code…");
        if (config.PostStatus) await TryPostStatusAsync(host, c, job.Commit, CommitState.Pending, "Meridian is updating the model…", null, ct);

        CodeSyncOutcome outcome;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var reader = sp.GetServices<ICodeReader>().LastOrDefault(r => r.Language == config.Language);
            Func<string, bool> include = reader is null ? DefaultInclude(config.Language) : reader.Handles;
            var snapshot = await host.FetchSnapshotAsync(job.Commit, config.Paths, include, ct);
            if (snapshot.Skipped.Count > 0)
                log.LogInformation("Code sync {Connection} at {Commit}: skipped {Count} files ({First})", c.Id, Short(job.Commit), snapshot.Skipped.Count, snapshot.Skipped[0]);
            var request = new CodeSyncRequest(c.ProjectId, c.Id, host.RepoDisplay, config.Branch, job.Commit, job.CommitUrl, job.Author, job.Message, snapshot, IsFullSnapshot: true);
            outcome = await ResolveRunner(sp).RunAsync(request, ct);
            if (snapshot.Skipped.Count > 0 && outcome.Status != CodeSyncStatus.Failed)
                outcome = outcome with { Summary = $"{outcome.Summary} ({snapshot.Skipped.Count} file{(snapshot.Skipped.Count == 1 ? "" : "s")} skipped)" };
        }
        catch (GitHostException ex)
        {
            log.LogWarning("Code sync {Connection} at {Commit} failed: {Error}", c.Id, Short(job.Commit), ex.Message);
            outcome = new CodeSyncOutcome(CodeSyncStatus.Failed, ex.Message, null, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogError(ex, "Code sync {Connection} at {Commit} failed", c.Id, Short(job.Commit));
            outcome = new CodeSyncOutcome(CodeSyncStatus.Failed, "Sync failed with an internal error (see the server log).", null, null);
        }

        var reviewUrl = AbsoluteUrl(outcome.ReviewUrl);
        if (config.PostStatus)
            await TryPostStatusAsync(host, c, job.Commit, StateOf(outcome.Status), outcome.Summary, reviewUrl, ct);
        await SetLastSyncAsync(c.Id, job.Commit, StatusText(outcome.Status), outcome.Summary, reviewUrl ?? outcome.ReviewUrl);
        return outcome;
    }

    /// <summary>Reports a reviewed sync back to the git host: the commit status turns green with the review summary.</summary>
    public async Task ReportReviewedAsync(Guid connectionId, string commit, CodeSyncOutcome outcome, CancellationToken ct = default)
    {
        if (await db.FindIntegrationAsync(connectionId) is not { Kind: CodeRepoKinds.CodeRepo } c) return;
        var config = CodeRepoConfig.Parse(c.ConfigJson);
        var reviewUrl = AbsoluteUrl(outcome.ReviewUrl);
        if (config.PostStatus)
        {
            IGitHost host;
            try { host = Host(c); }
            catch (GitHostException ex) { log.LogWarning("Posting the review status for {Connection} failed: {Error}", c.Id, ex.Message); return; }
            await TryPostStatusAsync(host, c, commit, CommitState.Success, outcome.Summary, reviewUrl, ct);
        }
        if (config.LastSync?.Commit == commit)
            await SetLastSyncAsync(c.Id, commit, StatusText(outcome.Status), outcome.Summary, reviewUrl ?? outcome.ReviewUrl);
    }

    /// <summary>The registered runner; the stand-in only when no real one is registered (whatever the registration order).</summary>
    public static ICodeSyncRunner ResolveRunner(IServiceProvider sp) =>
        sp.GetServices<ICodeSyncRunner>().LastOrDefault(r => r is not NullCodeSyncRunner) ?? NullCodeSyncRunner.Instance;

    public static Func<string, bool> DefaultInclude(string language) => language switch
    {
        "csharp" => p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase),
        _ => _ => false,
    };

    public static CommitState StateOf(CodeSyncStatus status) => status switch
    {
        CodeSyncStatus.Pending => CommitState.Pending,
        CodeSyncStatus.Failed => CommitState.Failure,
        CodeSyncStatus.AwaitingReview => CommitState.Pending,   // turns green when someone accepts or rejects it
        _ => CommitState.Success,   // no changes, or reviewed
    };

    private static string StatusText(CodeSyncStatus s) => s switch
    {
        CodeSyncStatus.Pending => "running",
        CodeSyncStatus.NoChanges => "no-changes",
        CodeSyncStatus.AwaitingReview => "awaiting-review",
        CodeSyncStatus.Accepted => "accepted",
        _ => "failed",
    };

    private string? AbsoluteUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs) && (abs.Scheme == Uri.UriSchemeHttps || abs.Scheme == Uri.UriSchemeHttp)) return abs.ToString();
        return PublicBaseUrl is { } b && url.StartsWith('/') ? b + url : null;
    }

    private async Task TryPostStatusAsync(IGitHost host, IntegrationConnection c, string commit, CommitState state, string description, string? targetUrl, CancellationToken ct)
    {
        try { await host.PostCommitStatusAsync(commit, state, description, targetUrl, ct); }
        catch (GitHostException ex) { log.LogWarning("Posting the {State} commit status for {Connection} failed: {Error}", state, c.Id, ex.Message); }
    }

    private async Task SetLastSyncAsync(Guid connectionId, string commit, string status, string summary, string? reviewUrl = null)
    {
        await _statusGate.WaitAsync();
        try
        {
            if (await db.FindIntegrationAsync(connectionId) is not { Kind: CodeRepoKinds.CodeRepo } c) return;
            var config = CodeRepoConfig.Parse(c.ConfigJson);
            config.LastSync = new CodeRepoLastSync { At = DateTime.UtcNow, Commit = commit, Status = status, Summary = summary, ReviewUrl = reviewUrl };
            await db.SaveIntegrationAsync(c with { ConfigJson = config.ToJson() });
        }
        catch (Exception ex) { log.LogWarning(ex, "Recording the sync status of {Connection} failed", connectionId); }
        finally { _statusGate.Release(); }
        StatusChanged?.Invoke(connectionId);
    }
}

/// <summary>Runs queued syncs one at a time (each holds a whole snapshot in memory).</summary>
public sealed class CodeSyncWorker(CodeSyncQueue queue, CodeSyncHostService service, ILogger<CodeSyncWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var job = await queue.DequeueAsync(stoppingToken);
                try { await service.ProcessAsync(job, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Code sync job for {Connection} failed", job.ConnectionId); }
            }
        }
        catch (OperationCanceledException) { }
    }
}
