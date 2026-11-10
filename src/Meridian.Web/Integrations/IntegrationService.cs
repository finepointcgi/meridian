using Meridian.Core.Model;
using Meridian.Data;
using Microsoft.AspNetCore.DataProtection;

namespace Meridian.Web.Integrations;

/// <summary>Result of a bulk operation over issue links.</summary>
public sealed record IssueBatchResult(int Done, int Skipped, IReadOnlyList<string> Errors)
{
    public string Summary(string verb) =>
        $"{verb} {Done}{(Skipped > 0 ? $", skipped {Skipped}" : "")}{(Errors.Count > 0 ? $", {Errors.Count} failed ({Errors[0]})" : "")}";
}

/// <summary>
/// Per-project integration settings (issue trackers and webhooks) and issue-link operations. Secrets are encrypted
/// with ASP.NET Data Protection before they reach the store, decrypted only to call the remote service, never logged
/// and never handed back to the UI.
/// </summary>
public sealed class IntegrationService(IMeridianStore db, IDataProtectionProvider protection, IHttpClientFactory httpFactory)
{
    public const string HttpClientName = "meridian-integrations";
    private readonly IDataProtector _protector = protection.CreateProtector("Meridian.Integrations.Secrets.v1");

    internal string Protect(string secret) => _protector.Protect(secret);

    internal string? Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret)) return null;
        try { return _protector.Unprotect(protectedSecret); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }   // key ring lost: the secret must be re-entered
    }

    private HttpClient Http() => httpFactory.CreateClient(HttpClientName);
    private int _settingsVersion;

    /// <summary>Bumped whenever any connection is saved or deleted, so open panels reload their lists.</summary>
    public int SettingsVersion => Volatile.Read(ref _settingsVersion);

    // ---------------------------------------------------------------- settings

    public async Task<IReadOnlyList<IntegrationConnection>> ListAsync(Guid projectId, string kind) =>
        (await db.ListIntegrationsAsync(projectId)).Where(c => c.Kind == kind).ToList();

    public Task<IReadOnlyList<WebhookDelivery>> DeliveriesAsync(Guid projectId, int limit = 50) => db.ListWebhookDeliveriesAsync(projectId, limit);

    /// <summary>Creates or updates a connection. <paramref name="newSecret"/> null or empty keeps the stored secret.</summary>
    public async Task<IntegrationConnection> SaveAsync(Guid projectId, Guid? id, string kind, string name, string configJson, string? newSecret)
    {
        IntegrationConnection? existing = id is { } i ? await db.FindIntegrationAsync(i) : null;
        if (existing != null && (existing.ProjectId != projectId || existing.Kind != kind)) throw new InvalidOperationException("That integration belongs to another project.");
        var secret = !string.IsNullOrEmpty(newSecret) ? Protect(newSecret.Trim()) : existing?.ProtectedSecret;
        var c = new IntegrationConnection(existing?.Id ?? Guid.NewGuid(), projectId, kind, string.IsNullOrWhiteSpace(name) ? kind : name.Trim(), configJson, secret, existing?.CreatedAt ?? DateTime.UtcNow);
        await db.SaveIntegrationAsync(c);
        Interlocked.Increment(ref _settingsVersion);
        return c;
    }

    public async Task DeleteAsync(Guid projectId, Guid id)
    {
        if (await db.FindIntegrationAsync(id) is { } c && c.ProjectId == projectId) await db.DeleteIntegrationAsync(id);
        Interlocked.Increment(ref _settingsVersion);
    }

    // ---------------------------------------------------------------- issue trackers

    public IIssueTracker Tracker(IssueTrackerConfig config, string secret) => IssueTrackerFactory.Create(config, secret, Http());

    public IIssueTracker Tracker(IntegrationConnection c)
    {
        if (c.Kind != IntegrationKinds.IssueTracker) throw new IssueTrackerException("Not an issue tracker connection.");
        var secret = Unprotect(c.ProtectedSecret) ?? throw new IssueTrackerException($"The token for “{c.Name}” is missing or can no longer be decrypted — enter it again in Integrations.");
        return Tracker(IssueTrackerConfig.Parse(c.ConfigJson), secret);
    }

    /// <summary>Tests unsaved settings; an empty secret falls back to the stored one of <paramref name="existingId"/>.</summary>
    public async Task<string> TestAsync(Guid projectId, IssueTrackerConfig config, string? newSecret, Guid? existingId)
    {
        if (config.Validate() is { } err) throw new IssueTrackerException(err);
        var secret = newSecret;
        if (string.IsNullOrEmpty(secret) && existingId is { } id && await db.FindIntegrationAsync(id) is { } c && c.ProjectId == projectId)
            secret = Unprotect(c.ProtectedSecret);
        if (string.IsNullOrEmpty(secret)) throw new IssueTrackerException("Enter the API token.");
        return await Tracker(config, secret.Trim()).TestAsync();
    }

    /// <summary>The issue title and description Meridian uses for an element.</summary>
    public static (string Title, string Description) Describe(Element e)
    {
        var title = e.Kind == ElementKind.Requirement && e.Get(FieldKeys.RequirementId) is { Length: > 0 } rid ? $"{rid} {e.DisplayName}" : e.DisplayName;
        var parts = new List<string>();
        if (e.Get(FieldKeys.Text) is { Length: > 0 } text) parts.Add(text);
        if (e.Documentation is { Length: > 0 } doc) parts.Add(doc);
        parts.Add($"Meridian {Meridian.Core.Language.Metamodel.DisplayName(e.Kind)}: {e.QualifiedName}");
        return (title, string.Join("\n\n", parts));
    }

    /// <summary>Creates an issue for an element and links it.</summary>
    public async Task<IssueLink> CreateIssueAsync(ModelRepository model, Element e, IntegrationConnection c, string backLink)
    {
        var (title, description) = Describe(e);
        var issue = await Tracker(c).CreateAsync(title, description, backLink);
        var link = IssueLink.From(c.Id, issue);
        if (model.Contains(e)) IssueLinks.Add(model, e, link);
        return link;
    }

    /// <summary>Pushes every requirement under <paramref name="package"/> that has no issue in <paramref name="c"/> yet.</summary>
    public async Task<IssueBatchResult> PushRequirementsAsync(ModelRepository model, Element package, IntegrationConnection c, Func<Element, string> backLink)
    {
        var tracker = Tracker(c);
        var reqs = package.Descendants().Where(x => x.Kind == ElementKind.Requirement).ToList();
        int done = 0, skipped = 0;
        var errors = new List<string>();
        foreach (var r in reqs)
        {
            if (IssueLinks.Read(r).Any(l => l.Connection == c.Id.ToString())) { skipped++; continue; }
            try
            {
                var (title, description) = Describe(r);
                var issue = await tracker.CreateAsync(title, description, backLink(r));
                if (model.Contains(r)) IssueLinks.Add(model, r, IssueLink.From(c.Id, issue));
                done++;
            }
            catch (IssueTrackerException ex)
            {
                errors.Add($"{r.DisplayName}: {ex.Message}");
                if (ex.Status is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) break;
            }
        }
        return new IssueBatchResult(done, skipped, errors);
    }

    /// <summary>Fetches fresh status for links. Returns the refreshed links per element (unchanged links keep their cache).</summary>
    public async Task<(Dictionary<Element, List<IssueLink>> Links, IssueBatchResult Result)> FetchAsync(Guid projectId, IEnumerable<Element> elements)
    {
        var connections = (await ListAsync(projectId, IntegrationKinds.IssueTracker)).ToDictionary(c => c.Id.ToString());
        var trackers = new Dictionary<string, IIssueTracker?>();
        var errors = new List<string>();
        IIssueTracker? TrackerFor(string id)
        {
            if (trackers.TryGetValue(id, out var t)) return t;
            try { t = connections.TryGetValue(id, out var c) ? Tracker(c) : null; }
            catch (IssueTrackerException ex) { errors.Add(ex.Message); t = null; }
            return trackers[id] = t;
        }

        var work = elements.Select(e => (Element: e, Links: IssueLinks.Read(e))).Where(x => x.Links.Count > 0).ToList();
        using var gate = new SemaphoreSlim(4);
        int done = 0, skipped = 0;
        var result = new Dictionary<Element, List<IssueLink>>();
        foreach (var (e, links) in work)
        {
            var refreshed = await Task.WhenAll(links.Select(async l =>
            {
                if (TrackerFor(l.Connection) is not { } t) { Interlocked.Increment(ref skipped); return l; }
                await gate.WaitAsync();
                try
                {
                    var info = await t.GetAsync(l.Key.TrimStart('#'));
                    Interlocked.Increment(ref done);
                    return l.Refreshed(info);
                }
                catch (IssueTrackerException ex)
                {
                    lock (errors) errors.Add($"{l.Key}: {ex.Message}");
                    return l;
                }
                finally { gate.Release(); }
            }));
            result[e] = refreshed.ToList();
        }
        return (result, new IssueBatchResult(done, skipped, errors));
    }

    /// <summary>Refreshes every issue link in the model and writes the new values as one undoable step.</summary>
    public async Task<IssueBatchResult> SyncAsync(ModelRepository model, Guid projectId, IEnumerable<Element>? scope = null)
    {
        var (links, result) = await FetchAsync(projectId, (scope ?? IssueLinks.Linked(model)).ToList());
        if (model.History.ReadOnly) return result;
        using (model.History.Transaction("Sync issue status"))
            foreach (var (e, list) in links)
                if (model.Contains(e)) IssueLinks.Replace(model, e, list);
        return result;
    }
}
