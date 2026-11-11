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

/// <summary>One element a pull request preview changes, for the pull request comment.</summary>
public sealed record PreviewChange(CodeSyncChangeKind Kind, string Name, string Path, string ElementKind)
{
    /// <summary>For a diagram: "structure", "activity" or "sequence"; null for other elements.</summary>
    public string? Diagram { get; init; }
}

/// <summary>Pull request preview branches (implemented by the review-branch runner, <see cref="CodeSyncRunner"/>).</summary>
public interface IPullRequestPreviews
{
    /// <summary>Syncs a pull request's head into its preview branch <c>code-sync/&lt;repo&gt;/pr-&lt;n&gt;</c>.</summary>
    Task<CodeSyncOutcome> RunPreviewAsync(CodeSyncRequest request, int pullRequest, CancellationToken ct = default);
    /// <summary>Closes a preview (the pull request was merged or closed): deletes its branch. Returns a short message.</summary>
    Task<string> ClosePreviewAsync(Guid projectId, string repoDisplay, int pullRequest, bool merged, CancellationToken ct = default);
    /// <summary>The net model changes a preview branch shows (empty when there is none).</summary>
    Task<IReadOnlyList<PreviewChange>> PreviewChangesAsync(Guid projectId, string repoDisplay, int pullRequest);
}

/// <summary>
/// Code repository connections: settings (token and webhook secret encrypted like other integrations), connection
/// tests, incoming webhooks, "Sync now", and the sync pipeline the background worker runs —
/// pending status → archive snapshot → <see cref="ICodeSyncRunner"/> → final status (plus, for a pull request preview,
/// one comment on the pull request that is updated in place).
/// </summary>
public sealed class CodeSyncHostService(
    IMeridianStore db, IntegrationService integrations, IHttpClientFactory httpFactory, IServiceScopeFactory scopes,
    CodeSyncQueue queue, CodeSyncRateLimiter limiter, IConfiguration configuration, ILogger<CodeSyncHostService> log)
{
    public const string HttpClientName = "meridian-code-sync";
    /// <summary>Largest webhook body accepted (GitHub caps payloads at 25 MB; pushes are far smaller).</summary>
    public const int MaxWebhookBytes = 10 * 1024 * 1024;
    /// <summary>Changes listed by name in a pull request comment (the rest are counted).</summary>
    public const int CommentTopItems = 10;

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
        // State the server keeps, not settings: never taken from the form.
        config.ProtectedWebhookSecret = stored?.ProtectedWebhookSecret;
        config.LastSync = stored?.LastSync;
        config.PullRequests = stored?.PullRequests ?? [];
        config.Submodules = stored?.Submodules ?? [];
        // New connections draw behaviour diagrams for entry points unless the form chose otherwise.
        if (existing is null) config.BehaviourDiagrams ??= Meridian.Core.CodeSync.CodeBehaviourMode.EntryPoints;
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

    /// <summary>The webhook URL for Bitbucket Cloud, which carries the secret (Cloud does not sign deliveries by default).</summary>
    public static string WebhookUrlWithToken(string baseUrl, Guid connectionId, string secret) =>
        $"{WebhookUrl(baseUrl, connectionId)}?{CodeWebhooks.UrlTokenParameter}={Uri.EscapeDataString(secret)}";

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

    /// <summary>
    /// Handles a webhook request (the endpoint passes the raw body so signatures can be checked, and the <c>token</c>
    /// query parameter for Bitbucket Cloud, whose deliveries are not signed by default).
    /// </summary>
    public async Task<WebhookResult> HandleWebhookAsync(Guid connectionId, IHeaderDictionary headers, byte[] body, string? urlToken = null, CancellationToken ct = default)
    {
        var c = await db.FindIntegrationAsync(connectionId);
        if (c is null || c.Kind != CodeRepoKinds.CodeRepo) return new(404, "Unknown repository connection.");
        var config = CodeRepoConfig.Parse(c.ConfigJson);
        var secret = integrations.Unprotect(config.ProtectedWebhookSecret);
        if (string.IsNullOrEmpty(secret)) return new(401, "This connection has no webhook secret; generate one in Meridian.");

        WebhookEventKind kind;
        string? eventKey = null;
        switch (config.Host)
        {
            case GitHostKind.GitHub:
                if (!CodeWebhooks.VerifyGitHub(secret, body, Header(headers, CodeWebhooks.GitHubSignatureHeader))) return new(401, "Missing or invalid signature.");
                kind = CodeWebhooks.GitHubEvent(Header(headers, CodeWebhooks.GitHubEventHeader));
                break;
            case GitHostKind.Bitbucket:
                var signature = Header(headers, CodeWebhooks.BitbucketSignatureHeader);
                var verified = config.IsBitbucketCloud
                    // Cloud: the secret in the URL, or a signature when a secret was also set in Bitbucket.
                    ? CodeWebhooks.VerifyUrlToken(secret, urlToken) || (signature is not null && CodeWebhooks.VerifyBitbucketSignature(secret, body, signature))
                    : CodeWebhooks.VerifyBitbucketSignature(secret, body, signature);
                if (!verified) return new(401, config.IsBitbucketCloud ? "Missing or invalid token." : "Missing or invalid signature.");
                eventKey = Header(headers, CodeWebhooks.BitbucketEventHeader);
                kind = CodeWebhooks.BitbucketEvent(eventKey);
                break;
            default:
                if (!CodeWebhooks.VerifyGitLab(secret, Header(headers, CodeWebhooks.GitLabTokenHeader))) return new(401, "Missing or invalid token.");
                kind = CodeWebhooks.GitLabEvent(Header(headers, CodeWebhooks.GitLabEventHeader));
                break;
        }
        if (!limiter.TryAcquire(connectionId)) return new(429, "Too many requests for this connection.");
        if (kind == WebhookEventKind.Ping) return new(200, "pong");
        var json = Encoding.UTF8.GetString(body);
        if (kind == WebhookEventKind.PullRequest) return await HandlePullRequestAsync(c, config, json, eventKey);
        if (kind != WebhookEventKind.Push) return new(202, "Ignored: not a push or pull request event.");

        var push = config.Host switch
        {
            GitHostKind.GitHub => CodeWebhooks.ParseGitHubPush(json),
            GitHostKind.Bitbucket => CodeWebhooks.ParseBitbucketPush(json) is { Count: > 0 } pushes
                ? pushes.LastOrDefault(p => p.Branch == config.Branch) ?? pushes[0] : null,
            _ => CodeWebhooks.ParseGitLabPush(json),
        };
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

    /// <summary>A pull / merge request into the followed branch: opened or updated → queue a preview sync; merged or
    /// closed → queue closing its preview (after any sync of it that is still waiting).</summary>
    private async Task<WebhookResult> HandlePullRequestAsync(IntegrationConnection c, CodeRepoConfig config, string json, string? eventKey)
    {
        var pr = config.Host switch
        {
            GitHostKind.GitHub => CodeWebhooks.ParseGitHubPullRequest(json),
            GitHostKind.Bitbucket => CodeWebhooks.ParseBitbucketPullRequest(json, eventKey),
            _ => CodeWebhooks.ParseGitLabMergeRequest(json),
        };
        if (pr is null) return new(400, "Not a pull request payload.");
        if (pr.TargetBranch != config.Branch) return new(202, $"Ignored: PR #{pr.Number} targets {Clip(pr.TargetBranch ?? "another branch")}; this connection follows {config.Branch}.");
        switch (pr.Action)
        {
            case PullRequestAction.Opened or PullRequestAction.Updated:
                if (!config.PreviewPullRequests) return new(202, "Ignored: pull request previews are off for this connection.");
                if (!GitHostBase.IsSha(pr.HeadSha)) return new(400, "The pull request has no head commit id.");
                var job = new CodeSyncJob(c.ProjectId, c.Id, pr.SourceBranch ?? config.Branch, pr.HeadSha!, pr.Url, pr.Author, pr.Title, "webhook",
                    PullRequest: pr.Number, PullRequestTitle: CodeSyncRuns.Clip(pr.Title, 300), PullRequestUrl: CodeSyncRuns.Clip(pr.Url, 500));
                if (!queue.TryEnqueue(job)) return new(503, "The sync queue is full; try again later.");
                await NotePullRequestAsync(c.Id, job, "queued", "Queued");
                return new(202, $"Queued a preview of PR #{pr.Number} at {Short(pr.HeadSha!)}.");
            case PullRequestAction.Merged or PullRequestAction.Closed:
                var close = new CodeSyncJob(c.ProjectId, c.Id, pr.SourceBranch ?? config.Branch, pr.HeadSha ?? "", pr.Url, pr.Author, pr.Title, "webhook",
                    PullRequest: pr.Number, ClosePreview: true, Merged: pr.Action == PullRequestAction.Merged);
                if (!queue.TryEnqueue(close)) return new(503, "The sync queue is full; try again later.");
                return new(202, $"Closing the preview of PR #{pr.Number}.");
            default:
                return new(202, $"Ignored: PR #{pr.Number} changed without new commits.");
        }
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
        if (job.PullRequest is { } number) return await ProcessPreviewAsync(job, c, config, number, ct);

        IGitHost host;
        try { host = Host(c); }
        catch (GitHostException ex)
        {
            await SetLastSyncAsync(c.Id, job.Commit, "failed", ex.Message);
            return new CodeSyncOutcome(CodeSyncStatus.Failed, ex.Message, null, null);
        }

        await SetLastSyncAsync(c.Id, job.Commit, "running", "Reading the code…");
        if (config.PostStatus) await TryPostStatusAsync(host, c, job.Commit, CommitState.Pending, "Meridian is updating the model…", null, ct);

        var outcome = await SyncAsync(job, c, config, host, (sp, request) => ResolveRunner(sp).RunAsync(request, ct), ct);

        var reviewUrl = AbsoluteUrl(outcome.ReviewUrl);
        if (config.PostStatus)
            await TryPostStatusAsync(host, c, job.Commit, StateOf(outcome.Status), outcome.Summary, reviewUrl, ct);
        await SetLastSyncAsync(c.Id, job.Commit, StatusText(outcome.Status), outcome.Summary, reviewUrl ?? outcome.ReviewUrl);
        return outcome;
    }

    /// <summary>Snapshot → runner, with every failure turned into a failed outcome.</summary>
    private async Task<CodeSyncOutcome> SyncAsync(CodeSyncJob job, IntegrationConnection c, CodeRepoConfig config, IGitHost host,
                                                   Func<IServiceProvider, CodeSyncRequest, Task<CodeSyncOutcome>> run, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var reader = sp.GetServices<ICodeReader>().LastOrDefault(r => r.Language == config.Language);
            Func<string, bool> include = reader is null ? DefaultInclude(config.Language) : reader.Handles;
            // The archive, then its git submodules (archives leave their code out), all within one set of limits.
            var fetched = await new SubmoduleFetcher(Host, log).FetchAsync(host, config, integrations.Unprotect(c.ProtectedSecret) ?? "", job.Commit, include,
                await OtherCredentialsAsync(c), new SnapshotBudget(Limits), ct);
            var snapshot = fetched.Snapshot;
            if (job.PullRequest is null && (fetched.Submodules.Count > 0 || config.Submodules.Count > 0)) await UpdateConfigAsync(c.Id, cfg => cfg.Submodules = [.. fetched.Submodules]);
            if (snapshot.Skipped.Count > 0)
                log.LogInformation("Code sync {Connection} at {Commit}: skipped {Count} files ({First})", c.Id, Short(job.Commit), snapshot.Skipped.Count, snapshot.Skipped[0]);
            var request = new CodeSyncRequest(c.ProjectId, c.Id, host.RepoDisplay, job.Branch, job.Commit, job.CommitUrl, job.Author, job.Message, snapshot, IsFullSnapshot: true)
            {
                Submodules = fetched.Pins,
            };
            var outcome = await run(sp, request);
            if (snapshot.Skipped.Count > 0 && outcome.Status != CodeSyncStatus.Failed)
                outcome = outcome with { Summary = $"{outcome.Summary} ({snapshot.Skipped.Count} file{(snapshot.Skipped.Count == 1 ? "" : "s")} skipped)" };
            if (fetched.Missing > 0 && outcome.Status != CodeSyncStatus.Failed)
                outcome = outcome with { Summary = $"{outcome.Summary} ({fetched.Missing} submodule{(fetched.Missing == 1 ? "" : "s")} not read)" };
            return outcome;
        }
        catch (GitHostException ex)
        {
            log.LogWarning("Code sync {Connection} at {Commit} failed: {Error}", c.Id, Short(job.Commit), ex.Message);
            return new CodeSyncOutcome(CodeSyncStatus.Failed, ex.Message, null, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogError(ex, "Code sync {Connection} at {Commit} failed", c.Id, Short(job.Commit));
            return new CodeSyncOutcome(CodeSyncStatus.Failed, "Sync failed with an internal error (see the server log).", null, null);
        }
    }

    /// <summary>The project's other code-repo connections whose token can be read: a submodule on another host or
    /// account uses the one for exactly its repository (<see cref="SubmoduleFetcher"/>).</summary>
    private async Task<IReadOnlyList<SubmoduleCredential>> OtherCredentialsAsync(IntegrationConnection c)
    {
        var list = new List<SubmoduleCredential>();
        foreach (var other in await integrations.ListAsync(c.ProjectId, CodeRepoKinds.CodeRepo))
        {
            if (other.Id == c.Id || other.ProjectId != c.ProjectId) continue;
            if (integrations.Unprotect(other.ProtectedSecret) is not { Length: > 0 } token) continue;
            var cfg = CodeRepoConfig.Parse(other.ConfigJson);
            cfg.Normalize();
            if (cfg.Validate() is null) list.Add(new SubmoduleCredential(other.Name, cfg, token.Trim()));
        }
        return list;
    }

    /// <summary>
    /// A pull request job: syncs its head into the preview branch (commit status on the head, and the one Meridian
    /// comment on the pull request, updated in place), or closes the preview once it was merged or closed.
    /// </summary>
    private async Task<CodeSyncOutcome> ProcessPreviewAsync(CodeSyncJob job, IntegrationConnection c, CodeRepoConfig config, int number, CancellationToken ct)
    {
        IPullRequestPreviews? previews;
        await using (var scope = scopes.CreateAsyncScope())
            previews = ResolvePreviews(scope.ServiceProvider);
        if (previews is null) return new CodeSyncOutcome(CodeSyncStatus.Failed, "Pull request previews are not available.", null, null);

        if (job.ClosePreview)
        {
            // Merged: deleting the preview is enough — the merge is a push to the followed branch, whose full-snapshot
            // sync brings the same changes (against the current main) to the review branch. No call to the host needed.
            var message = await previews.ClosePreviewAsync(c.ProjectId, config.Display, number, job.Merged, ct);
            await ForgetPullRequestAsync(c.Id, number);
            return new CodeSyncOutcome(CodeSyncStatus.NoChanges, message, null, null);
        }

        IGitHost host;
        try { host = Host(c); }
        catch (GitHostException ex)
        {
            await NotePullRequestAsync(c.Id, job, "failed", ex.Message);
            return new CodeSyncOutcome(CodeSyncStatus.Failed, ex.Message, null, null);
        }

        await NotePullRequestAsync(c.Id, job, "running", "Reading the code…");
        if (config.PostStatus) await TryPostStatusAsync(host, c, job.Commit, CommitState.Pending, "Meridian is previewing the model changes…", null, ct);

        var outcome = await SyncAsync(job, c, config, host, (_, request) => previews.RunPreviewAsync(request, number, ct), ct);
        var reviewUrl = AbsoluteUrl(outcome.ReviewUrl);
        if (config.PostStatus)
        {
            // A preview is never accepted, so it must not leave a pending status on the pull request.
            var state = outcome.Status == CodeSyncStatus.Failed ? CommitState.Failure : CommitState.Success;
            await TryPostStatusAsync(host, c, job.Commit, state, $"Model preview: {outcome.Summary}", reviewUrl, ct);
        }
        string? commentId = null;
        if (config.CommentOnPullRequests)
        {
            IReadOnlyList<PreviewChange> changes = outcome.Status == CodeSyncStatus.Failed ? [] : await previews.PreviewChangesAsync(c.ProjectId, host.RepoDisplay, number);
            var known = (await ConfigAsync(c.Id))?.PullRequests.FirstOrDefault(p => p.Number == number)?.CommentId;
            try { commentId = await host.UpsertPullRequestCommentAsync(number, known, PreviewComment(number, job.Commit, outcome, changes, reviewUrl), ct); }
            catch (GitHostException ex) { log.LogWarning("Commenting on PR #{Number} of {Connection} failed: {Error}", number, c.Id, ex.Message); }
        }
        await NotePullRequestAsync(c.Id, job, StatusText(outcome.Status), outcome.Summary, reviewUrl ?? outcome.ReviewUrl, commentId);
        return outcome;
    }

    /// <summary>
    /// The pull request comment: the counts, the first <see cref="CommentTopItems"/> changes, and the link to the
    /// preview in Meridian. Markdown that GitHub, GitLab and Bitbucket all render.
    /// </summary>
    public static string PreviewComment(int number, string commit, CodeSyncOutcome outcome, IReadOnlyList<PreviewChange> changes, string? reviewUrl)
    {
        var sb = new StringBuilder();
        sb.Append("### Meridian model preview\n\n");
        if (outcome.Status == CodeSyncStatus.Failed)
        {
            sb.Append($"The preview of PR #{number} at `{Short(commit)}` failed: {Escape(outcome.Summary)}\n");
            return sb.ToString();
        }
        int added = changes.Count(x => x.Kind == CodeSyncChangeKind.Added), changed = changes.Count(x => x.Kind == CodeSyncChangeKind.Changed),
            removed = changes.Count(x => x.Kind == CodeSyncChangeKind.Removed);
        if (changes.Count == 0)
        {
            sb.Append($"PR #{number} at `{Short(commit)}` does not change the model.\n");
            return sb.ToString();
        }
        sb.Append($"PR #{number} at `{Short(commit)}` changes the model: **{added} added, {changed} changed, {removed} removed**.\n\n");
        var newDiagrams = changes.Where(x => x.Kind == CodeSyncChangeKind.Added && x.Diagram != null).ToList();
        var removedDiagrams = changes.Count(x => x.Kind == CodeSyncChangeKind.Removed && x.Diagram != null);
        var diagrams = CodeSyncRunner.DiagramSummary(newDiagrams.Count(x => x.Diagram == "structure"), newDiagrams.Count(x => x.Diagram == "activity"),
            newDiagrams.Count(x => x.Diagram == "sequence"), 0);
        if (removedDiagrams > 0) diagrams += (diagrams.Length > 0 ? "; " : "") + $"{removedDiagrams} diagram{(removedDiagrams == 1 ? "" : "s")} removed";
        if (diagrams.Length > 0) sb.Append($"Diagrams: {diagrams}.\n\n");
        sb.Append("| Change | Element | Kind | Owner |\n|---|---|---|---|\n");
        foreach (var x in changes.OrderBy(x => x.Kind).Take(CommentTopItems))
            sb.Append($"| {x.Kind} | {Escape(x.Name)} | {Escape(x.ElementKind)} | {Escape(x.Path)} |\n");
        if (changes.Count > CommentTopItems) sb.Append($"\n…and {changes.Count - CommentTopItems} more.\n");
        if (reviewUrl is { Length: > 0 }) sb.Append($"\n[Open the preview in Meridian]({reviewUrl})\n");
        sb.Append("\n<sub>Meridian updates this comment on every push to the pull request. Accept the changes after the merge, from the sync of the target branch.</sub>\n");
        return sb.ToString();
    }

    /// <summary>Keeps element names from breaking the table or turning into links, mentions or HTML.</summary>
    private static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Length > 200 ? s[..200] + "…" : s)
        {
            if (ch is '\r' or '\n') { sb.Append(' '); continue; }
            if (ch is '|' or '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '#' or '@' or '!' or '~') sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
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

    /// <summary>The pull request preview runner: a registered <see cref="IPullRequestPreviews"/>, else the runner if it is one.</summary>
    public static IPullRequestPreviews? ResolvePreviews(IServiceProvider sp) =>
        sp.GetServices<IPullRequestPreviews>().LastOrDefault() ?? ResolveRunner(sp) as IPullRequestPreviews;

    public static Func<string, bool> DefaultInclude(string language) => Meridian.Serialization.CodeSync.CodeReaders.Include(language);

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

    private Task SetLastSyncAsync(Guid connectionId, string commit, string status, string summary, string? reviewUrl = null) =>
        UpdateConfigAsync(connectionId, config =>
            config.LastSync = new CodeRepoLastSync { At = DateTime.UtcNow, Commit = commit, Status = status, Summary = summary, ReviewUrl = reviewUrl });

    /// <summary>Records a pull request's preview state (head, status, review link and comment id) on the connection.</summary>
    private Task NotePullRequestAsync(Guid connectionId, CodeSyncJob job, string status, string summary, string? reviewUrl = null, string? commentId = null) =>
        UpdateConfigAsync(connectionId, config =>
        {
            var pr = config.PullRequests.FirstOrDefault(p => p.Number == job.PullRequest);
            if (pr is null)
            {
                pr = new CodeRepoPullRequest { Number = job.PullRequest!.Value };
                config.PullRequests.Add(pr);
            }
            pr.Title = job.PullRequestTitle ?? pr.Title;
            pr.Url = job.PullRequestUrl ?? pr.Url;
            pr.Head = job.Commit;
            pr.Status = status;
            pr.Summary = summary;
            if (reviewUrl is not null) pr.ReviewUrl = reviewUrl;
            if (commentId is not null) pr.CommentId = commentId;
            pr.UpdatedAt = DateTime.UtcNow;
            if (config.PullRequests.Count > CodeRepoConfig.MaxPullRequests)
                config.PullRequests = config.PullRequests.OrderByDescending(p => p.UpdatedAt).Take(CodeRepoConfig.MaxPullRequests).ToList();
        });

    private Task ForgetPullRequestAsync(Guid connectionId, int number) =>
        UpdateConfigAsync(connectionId, config => config.PullRequests.RemoveAll(p => p.Number == number));

    /// <summary>Changes the stored state of a connection (one writer at a time) and tells the UI.</summary>
    private async Task UpdateConfigAsync(Guid connectionId, Action<CodeRepoConfig> change)
    {
        await _statusGate.WaitAsync();
        try
        {
            if (await db.FindIntegrationAsync(connectionId) is not { Kind: CodeRepoKinds.CodeRepo } c) return;
            var config = CodeRepoConfig.Parse(c.ConfigJson);
            config.PullRequests ??= [];
            change(config);
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
