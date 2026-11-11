using System.Collections.Concurrent;
using System.Net;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Web.Services;

namespace Meridian.Web.Integrations.Requirements;

/// <summary>
/// DOORS Next and Polarion connections of a project: settings (secrets encrypted by <see cref="IntegrationService"/>),
/// browsing, "Sync now" on a tab's model, scheduled syncs on the live model, and link publishing. All HTTP goes through
/// the guarded <see cref="HttpClientName"/> client (<see cref="OutboundGuard"/>, no redirects or cookies in the shared handler).
/// </summary>
public sealed class RequirementsToolService(IMeridianStore db, IntegrationService integrations, IHttpClientFactory httpFactory, IServiceProvider services, ILogger<RequirementsToolService> log)
{
    public const string HttpClientName = "meridian-requirements-tools";

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _running = new();

    /// <summary>Builds the client for a config (tests replace it).</summary>
    public Func<RequirementsToolConfig, string, HttpClient, IRequirementsSource> SourceFactory { get; set; } = Create;

    public static IRequirementsSource Create(RequirementsToolConfig config, string secret, HttpClient http) => config.Type switch
    {
        RequirementsToolKinds.DoorsNext => new DoorsNextSource(config, secret, http),
        RequirementsToolKinds.Polarion => new PolarionSource(config, secret, http),
        _ => throw new RequirementsToolException($"Unknown requirements tool '{config.Type}'."),
    };

    public IRequirementsSource Source(RequirementsToolConfig config, string secret) => SourceFactory(config, secret, httpFactory.CreateClient(HttpClientName));

    public IRequirementsSource Source(IntegrationConnection c)
    {
        if (c.Kind != RequirementsToolKinds.RequirementsTool) throw new RequirementsToolException("Not a requirements tool connection.");
        var secret = integrations.Unprotect(c.ProtectedSecret, c.Id)
                     ?? throw new RequirementsToolException($"The password or token for “{c.Name}” is missing or can no longer be decrypted — enter it again in Integrations.");
        return Source(RequirementsToolConfig.Parse(c.ConfigJson), secret);
    }

    public Task<IReadOnlyList<IntegrationConnection>> ListAsync(Guid projectId) => integrations.ListAsync(projectId, RequirementsToolKinds.RequirementsTool);

    public Task DeleteAsync(Guid projectId, Guid id) => integrations.DeleteAsync(projectId, id);

    /// <summary>An unsaved form's secret, or the stored one of <paramref name="existingId"/> when the field was left empty.</summary>
    private async Task<string> SecretAsync(Guid projectId, string? newSecret, Guid? existingId)
    {
        var secret = newSecret;
        if (string.IsNullOrEmpty(secret) && existingId is { } id && await db.FindIntegrationAsync(id) is { } c && c.ProjectId == projectId && c.Kind == RequirementsToolKinds.RequirementsTool)
            secret = integrations.Unprotect(c.ProtectedSecret, c.Id);
        if (string.IsNullOrEmpty(secret)) throw new RequirementsToolException("Enter the password or token.");
        return secret.Trim();
    }

    public async Task<string> TestAsync(Guid projectId, RequirementsToolConfig config, string? newSecret, Guid? existingId, CancellationToken ct = default)
    {
        if (config.Validate(needProject: false) is { } err) throw new RequirementsToolException(err);
        return await Source(config, await SecretAsync(projectId, newSecret, existingId)).TestAsync(ct);
    }

    public async Task<IReadOnlyList<RemoteContainer>> ProjectsAsync(Guid projectId, RequirementsToolConfig config, string? newSecret, Guid? existingId, CancellationToken ct = default)
    {
        if (config.Validate(needProject: false) is { } err) throw new RequirementsToolException(err);
        return await Source(config, await SecretAsync(projectId, newSecret, existingId)).ProjectsAsync(ct);
    }

    public async Task<IReadOnlyList<RemoteContainer>> ScopesAsync(Guid projectId, RequirementsToolConfig config, string? newSecret, Guid? existingId, CancellationToken ct = default)
    {
        if (config.Validate() is { } err) throw new RequirementsToolException(err);
        return await Source(config, await SecretAsync(projectId, newSecret, existingId)).ScopesAsync(config.Project.Trim(), ct);
    }

    /// <summary>Saves a connection; <paramref name="savedBy"/> becomes the account scheduled syncs edit as.</summary>
    public async Task<IntegrationConnection> SaveAsync(Guid projectId, Guid? id, string name, RequirementsToolConfig config, string? newSecret, Guid savedBy)
    {
        if (config.Validate() is { } err) throw new RequirementsToolException(err);
        if (id is { } existingId && await db.FindIntegrationAsync(existingId) is { } existing && existing.Kind == RequirementsToolKinds.RequirementsTool)
            config.LastSync ??= RequirementsToolConfig.Parse(existing.ConfigJson).LastSync;
        config.RunAs = savedBy;
        if (string.IsNullOrEmpty(newSecret) && id is null) throw new RequirementsToolException(config.IsDoors ? "Enter the password." : "Enter the personal access token.");
        return await integrations.SaveAsync(projectId, id, RequirementsToolKinds.RequirementsTool, string.IsNullOrWhiteSpace(name) ? RequirementsToolKinds.Display(config.Type) : name, config.ToJson(), newSecret);
    }

    // ---------------------------------------------------------------- sync

    /// <summary>
    /// Fetches the connection's requirements and applies them to <paramref name="model"/> (one undo step). With
    /// <c>PublishLinks</c> on and <paramref name="urls"/> given, publishes Meridian links afterwards. Records the outcome
    /// in the connection's config. Conflicts are returned unresolved unless <paramref name="choices"/> resolves them.
    /// </summary>
    public async Task<RequirementsSyncResult> SyncAsync(ModelRepository model, IntegrationConnection c, Oslc.Urls? urls,
        IReadOnlyDictionary<string, ConflictChoice>? choices = null, CancellationToken ct = default)
    {
        if (model.History.ReadOnly) throw new RequirementsToolException("This project is view-only for you.");
        var gate = _running.GetOrAdd(c.Id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) throw new RequirementsToolException($"“{c.Name}” is already syncing.");
        try
        {
            return await RunAsync(c, urls, ct, apply: (remote, cfg) => Task.FromResult(RequirementsSync.Apply(model, c.Id, c.Name, cfg, remote, choices)),
                publish: src => PublishAsync(model, c, src, urls, ct));
        }
        finally { gate.Release(); }
    }

    private async Task<RequirementsSyncResult> RunAsync(IntegrationConnection c, Oslc.Urls? urls, CancellationToken ct,
        Func<IReadOnlyList<RemoteRequirement>, RequirementsToolConfig, Task<RequirementsSyncResult>> apply,
        Func<IRequirementsSource, Task<(int Published, List<string> Errors)>> publish)
    {
        var config = RequirementsToolConfig.Parse(c.ConfigJson);
        RequirementsSyncResult result;
        try
        {
            var source = Source(c);
            var remote = await source.RequirementsAsync(ct);
            result = await apply(remote, config);
            if (config.PublishLinks && urls != null)
            {
                var (published, errors) = await publish(source);
                result = result with { LinksPublished = published, Errors = errors };
            }
        }
        catch (RequirementsToolException ex)
        {
            await RecordAsync(c.Id, new RequirementsLastSync { At = DateTime.UtcNow, Status = "failed", Summary = ex.Message });
            throw;
        }
        await RecordAsync(c.Id, new RequirementsLastSync
        {
            At = DateTime.UtcNow, Status = result.Conflicts.Count > 0 ? "conflicts" : "ok", Summary = result.Summary, Conflicts = result.Conflicts.Count,
        });
        return result;
    }

    private static async Task<(int Published, List<string> Errors)> PublishAsync(ModelRepository model, IntegrationConnection c, IRequirementsSource source, Oslc.Urls? urls, CancellationToken ct)
    {
        var errors = new List<string>();
        if (urls is null) return (0, errors);
        var pending = RequirementsSync.PendingLinks(model, c.Id, e => urls.Resource(c.ProjectId, e));
        var done = new List<PendingLink>();
        foreach (var p in pending)
        {
            try
            {
                await source.PublishLinkAsync(p.Sync.Id, p.Link, ct);
                done.Add(p);
            }
            catch (RequirementsToolException ex)
            {
                errors.Add($"{p.Sync.Identifier} → {p.Link.Label}: {ex.Message}");
                if (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) break;
            }
        }
        if (!model.History.ReadOnly) RequirementsSync.MarkPublished(model, done.Select(p => (p.Requirement, p.Key)));
        return (done.Count, errors);
    }

    /// <summary>Stores the outcome of a sync in the connection's config (re-read, so concurrent edits of other settings survive).</summary>
    private async Task RecordAsync(Guid connectionId, RequirementsLastSync last)
    {
        if (await db.FindIntegrationAsync(connectionId) is not { } current) return;
        var cfg = RequirementsToolConfig.Parse(current.ConfigJson);
        cfg.LastSync = last;
        await integrations.SaveAsync(current.ProjectId, current.Id, current.Kind, current.Name, cfg.ToJson(), null);
    }

    /// <summary>
    /// A scheduled sync: edits the live project as the account that saved the connection (if it can still edit),
    /// applies everything except conflicts (they wait for "Sync now"), and publishes links when enabled and
    /// <c>Meridian:PublicUrl</c> is set.
    /// </summary>
    public async Task<RequirementsSyncResult?> SyncScheduledAsync(IntegrationConnection c, string? publicUrl, CancellationToken ct = default)
    {
        var config = RequirementsToolConfig.Parse(c.ConfigJson);
        if (config.RunAs is not { } runAs || await db.RoleOfAsync(c.ProjectId, runAs) is not { } role || role < ProjectRole.Editor
            || await db.GetUserAsync(runAs) is not { Disabled: false } account)
        {
            await RecordAsync(c.Id, new RequirementsLastSync { At = DateTime.UtcNow, Status = "failed", Summary = "Scheduled sync skipped: the owner who saved this connection can no longer edit the project — save it again." });
            return null;
        }
        var gate = _running.GetOrAdd(c.Id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return null;
        try
        {
            if (services.GetService<CollaborationHub>() is not { } hub) return null;
            await using var edit = await ServerModelEdit.OpenAsync(hub, c.ProjectId, account.User);
            if (edit is null) return null;
            var urls = publicUrl is { Length: > 0 } ? new Oslc.Urls(publicUrl.TrimEnd('/')) : null;
            return await RunAsync(c, urls, ct,
                apply: (remote, cfg) => edit.EditAsync(model => RequirementsSync.Apply(model, c.Id, c.Name, cfg, remote)),
                publish: async src =>
                {
                    // Publish against a snapshot, then record what went out in a second edit.
                    var pending = await edit.EditAsync(model => RequirementsSync.PendingLinks(model, c.Id, e => urls!.Resource(c.ProjectId, e))
                        .Select(p => (p.Requirement.Id, p.Sync.Id, p.Link, p.Key)).ToList());
                    var errors = new List<string>();
                    var done = new List<(string ElementId, string Key)>();
                    foreach (var (elementId, externalId, link, key) in pending)
                    {
                        try { await src.PublishLinkAsync(externalId, link, ct); done.Add((elementId, key)); }
                        catch (RequirementsToolException ex)
                        {
                            errors.Add($"{link.Label}: {ex.Message}");
                            if (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) break;
                        }
                    }
                    await edit.EditAsync(model =>
                    {
                        RequirementsSync.MarkPublished(model, done.Select(d => (model.Find(d.ElementId), d.Key)).Where(x => x.Item1 != null).Select(x => (x.Item1!, x.Key)));
                        return 0;
                    });
                    return (done.Count, errors);
                });
        }
        catch (RequirementsToolException ex)
        {
            log.LogWarning("Scheduled requirements sync of {Connection} failed: {Message}", c.Id, ex.Message);
            return null;
        }
        finally { gate.Release(); }
    }

    /// <summary>Connections whose schedule is due at <paramref name="now"/>.</summary>
    public static bool IsDue(RequirementsToolConfig config, DateTime now) =>
        config.ScheduleMinutes > 0 && (config.LastSync is null || now - config.LastSync.At >= TimeSpan.FromMinutes(config.ScheduleMinutes));
}

/// <summary>Runs scheduled requirements syncs (checks once a minute).</summary>
public sealed class RequirementsSyncScheduler(IMeridianStore db, RequirementsToolService tools, IConfiguration configuration, ILogger<RequirementsSyncScheduler> log) : BackgroundService
{
    public static TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await RunDueAsync(DateTime.UtcNow, stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Scheduled requirements sync pass failed"); }
        }
    }

    /// <summary>Syncs every due connection once. Returns how many ran.</summary>
    public async Task<int> RunDueAsync(DateTime now, CancellationToken ct = default)
    {
        var ran = 0;
        foreach (var project in await db.ListAllProjectsAsync())
            foreach (var c in await db.ListIntegrationsAsync(project.Id))
            {
                if (c.Kind != RequirementsToolKinds.RequirementsTool || !RequirementsToolService.IsDue(RequirementsToolConfig.Parse(c.ConfigJson), now)) continue;
                ct.ThrowIfCancellationRequested();
                await tools.SyncScheduledAsync(c, configuration["Meridian:PublicUrl"], ct);
                ran++;
            }
        return ran;
    }
}

/// <summary>Registers the DOORS Next / Polarion connectors.</summary>
public static class RequirementsToolsSetup
{
    public static IServiceCollection AddMeridianRequirementsTools(this IServiceCollection services)
    {
        // Same SSRF guard as the other integrations; redirects are followed (and checked) by ToolHttp itself so it can
        // keep each connection's Jazz session cookies in a private jar.
        services.AddHttpClient(RequirementsToolService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(60))
            .RedactLoggedHeaders(_ => true)
            .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<MeridianHostInfo>().LocalMode
                ? new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }
                : OutboundGuard.CreateHandler(string.Equals(sp.GetRequiredService<IConfiguration>()[OutboundGuard.AllowPrivateNetworksKey], "true", StringComparison.OrdinalIgnoreCase), followRedirects: false));
        services.AddSingleton<RequirementsToolService>();
        services.AddSingleton<RequirementsSyncScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<RequirementsSyncScheduler>());
        return services;
    }
}
