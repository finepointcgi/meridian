using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Meridian.Core.Model;
using Meridian.Data;

namespace Meridian.Web.Integrations;

public static class WebhookEvents
{
    public const string VersionCommitted = "version.committed";
    public const string ElementChanged = "element.changed";
    public const string CommentChanged = "comment.changed";
    public const string Ping = "ping";

    public static readonly string[] All = [VersionCommitted, ElementChanged, CommentChanged];

    public static string Display(string e) => e switch
    {
        VersionCommitted => "Version committed",
        ElementChanged => "Elements changed (batched)",
        CommentChanged => "Comments added / resolved / deleted",
        Ping => "Ping",
        _ => e,
    };
}

/// <summary>Non-secret webhook settings (the signing secret is stored encrypted, separately).</summary>
public sealed class WebhookConfig
{
    public string Url { get; set; } = "";
    /// <summary>Events to send; empty means all.</summary>
    public List<string> Events { get; set; } = [];
    public bool Enabled { get; set; } = true;

    public static WebhookConfig Parse(string json) => JsonSerializer.Deserialize<WebhookConfig>(json, IntegrationJson.Options) ?? new WebhookConfig();
    public string ToJson() => JsonSerializer.Serialize(this, IntegrationJson.Options);

    public bool Wants(string evt) => evt == WebhookEvents.Ping || Events.Count == 0 || Events.Contains(evt);

    public string? Validate() =>
        Uri.TryCreate(Url?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? null : "The webhook URL must be an absolute http(s):// URL.";
}

/// <summary>HMAC-SHA256 request signing: <c>X-Meridian-Signature-256: sha256=&lt;hex of HMAC(secret, raw body)&gt;</c>.</summary>
public static class WebhookSignature
{
    public const string Header = "X-Meridian-Signature-256";

    public static string Sign(string secret, string body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    public static bool Verify(string secret, string body, string? header) =>
        header != null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(secret, body)), Encoding.ASCII.GetBytes(header.Trim()));

    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}

/// <summary>A project event bound for webhooks.</summary>
public sealed record ProjectEvent(Guid ProjectId, string ProjectName, string Event, JsonObject Data, DateTime At);

/// <summary>Tuning for the event stream and delivery queue (tests shorten the delays).</summary>
public sealed class WebhookOptions
{
    /// <summary>Element changes are sent once edits pause for this long…</summary>
    public TimeSpan ElementQuietPeriod { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>…or at the latest this long after the first change of a batch.</summary>
    public TimeSpan ElementMaxDelay { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Delay before each retry; the number of entries + 1 is the number of attempts.</summary>
    public TimeSpan[] RetryDelays { get; set; } = [TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Collects project events (from the collaboration hub and the UI) and queues them for webhook delivery. Element
/// changes are debounced per project into one <c>element.changed</c> event listing every touched element.
/// Calls from the hub are made under its lock, so everything here only records and returns.
/// </summary>
public sealed class ProjectEvents(WebhookOptions options)
{
    private readonly Channel<ProjectEvent> _queue = Channel.CreateUnbounded<ProjectEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, ChangeBatch> _batches = new();

    public ChannelReader<ProjectEvent> Reader => _queue.Reader;

    public void Publish(Guid projectId, string projectName, string evt, JsonObject data) =>
        _queue.Writer.TryWrite(new ProjectEvent(projectId, projectName, evt, data, DateTime.UtcNow));

    public void VersionCommitted(Guid projectId, string projectName, int number, string message, UserAccount? author) =>
        Publish(projectId, projectName, WebhookEvents.VersionCommitted, new JsonObject
        {
            ["number"] = number, ["message"] = message, ["author"] = Actor(author),
        });

    public void CommentsChanged(Guid projectId, string projectName) =>
        Publish(projectId, projectName, WebhookEvents.CommentChanged, new JsonObject());

    private static JsonObject? Actor(UserAccount? u) => u is null ? null : new JsonObject { ["id"] = u.Id.ToString(), ["username"] = u.Username, ["displayName"] = u.DisplayName };

    /// <summary>Records an applied model operation (called by the collaboration hub with the authoritative model).</summary>
    public void OperationApplied(Guid projectId, string projectName, Guid userId, ModelOp op, ModelRepository model)
    {
        string change;
        string? id;
        switch (op)
        {
            case AttachOp a: change = "added"; id = a.Snap.Id; break;
            case DetachOp d: change = "removed"; id = d.ElementId; break;
            case SetFieldOp { Key: IssueLinks.FieldKey }: return;   // issue status sync is not a model change
            case SetFieldOp s: change = "updated"; id = s.ElementId; break;
            case MoveOp m: change = "moved"; id = m.ElementId; break;
            case NodeInsertOp n: change = "diagram"; id = n.DiagramId; break;
            case NodeStyleOp n: change = "diagram"; id = n.DiagramId; break;
            case NodeRemoveOp n: change = "diagram"; id = n.DiagramId; break;
            case NodeBoundsOp n: change = "diagram"; id = n.DiagramId; break;
            case EdgeInsertOp x: change = "diagram"; id = x.DiagramId; break;
            case EdgeStyleOp x: change = "diagram"; id = x.DiagramId; break;
            case EdgeRemoveOp x: change = "diagram"; id = x.DiagramId; break;
            case EdgeWaypointsOp x: change = "diagram"; id = x.DiagramId; break;
            default: return;
        }
        var e = model.Find(id);
        var batch = _batches.GetOrAdd(projectId, p => new ChangeBatch(this, p, options));
        batch.Add(projectName, userId, id, change, e?.Name, e is null ? null : e.Kind.ToString());
    }

    /// <summary>Sends pending element batches now (shutdown and tests).</summary>
    public void FlushAll()
    {
        foreach (var b in _batches.Values) b.Flush();
    }

    private sealed class ChangeBatch
    {
        private readonly object _gate = new();
        private readonly ProjectEvents _owner;
        private readonly Guid _projectId;
        private readonly WebhookOptions _options;
        private readonly Timer _timer;
        private readonly Dictionary<string, (string Change, string? Name, string? Kind)> _elements = new();
        private readonly HashSet<Guid> _users = new();
        private string _projectName = "";
        private DateTime? _first;

        public ChangeBatch(ProjectEvents owner, Guid projectId, WebhookOptions options)
        {
            _owner = owner;
            _projectId = projectId;
            _options = options;
            _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Add(string projectName, Guid userId, string id, string change, string? name, string? kind)
        {
            bool flushNow;
            lock (_gate)
            {
                _projectName = projectName;
                _first ??= DateTime.UtcNow;
                if (userId != Guid.Empty) _users.Add(userId);
                // "added" or "removed" wins over later updates within a batch.
                if (_elements.TryGetValue(id, out var prev))
                    _elements[id] = (change == "removed" ? "removed" : prev.Change == "added" ? "added" : change, name ?? prev.Name, kind ?? prev.Kind);
                else
                    _elements[id] = (change, name, kind);
                flushNow = DateTime.UtcNow - _first.Value >= _options.ElementMaxDelay || _elements.Count >= 1000;
                if (!flushNow) _timer.Change(_options.ElementQuietPeriod, Timeout.InfiniteTimeSpan);
            }
            if (flushNow) Flush();
        }

        public void Flush()
        {
            JsonObject data;
            string name;
            lock (_gate)
            {
                if (_elements.Count == 0) return;
                data = new JsonObject
                {
                    ["count"] = _elements.Count,
                    ["elements"] = new JsonArray(_elements.Select(kv => (JsonNode)new JsonObject
                    {
                        ["id"] = kv.Key, ["change"] = kv.Value.Change, ["name"] = kv.Value.Name, ["kind"] = kv.Value.Kind,
                    }).ToArray()),
                    ["userIds"] = new JsonArray(_users.Select(u => (JsonNode)JsonValue.Create(u.ToString())!).ToArray()),
                    ["since"] = _first?.ToString("O"),
                };
                name = _projectName;
                _elements.Clear();
                _users.Clear();
                _first = null;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            _owner.Publish(_projectId, name, WebhookEvents.ElementChanged, data);
        }
    }
}

/// <summary>
/// Background delivery of project events to webhooks: JSON POST with an HMAC-SHA256 signature header, retried with
/// back-off; every attempt is written to the delivery log shown in the Integrations dialog.
/// </summary>
public sealed class WebhookDispatcher(ProjectEvents events, IMeridianStore db, IntegrationService integrations, IHttpClientFactory httpFactory,
    WebhookOptions options, ILogger<WebhookDispatcher> log) : BackgroundService
{
    private readonly SemaphoreSlim _concurrency = new(8);
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var e in events.Reader.ReadAllAsync(stoppingToken))
            {
                try { await DispatchAsync(e, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Dispatching {Event} for project {Project} failed", e.Event, e.ProjectId); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Queues delivery of one event to every enabled hook of its project that wants it.</summary>
    public async Task DispatchAsync(ProjectEvent e, CancellationToken ct = default)
    {
        foreach (var hook in await integrations.ListAsync(e.ProjectId, IntegrationKinds.Webhook))
        {
            var config = WebhookConfig.Parse(hook.ConfigJson);
            if (!config.Enabled || !config.Wants(e.Event)) continue;
            var task = DeliverWithRetriesAsync(hook, config, e, ct);
            _inFlight[task] = 0;
            _ = task.ContinueWith(t => _inFlight.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    /// <summary>Waits for queued deliveries (tests).</summary>
    public Task DrainAsync() => Task.WhenAll(_inFlight.Keys.ToArray());

    public static string Payload(ProjectEvent e, string deliveryId) => new JsonObject
    {
        ["id"] = deliveryId,
        ["event"] = e.Event,
        ["timestamp"] = e.At.ToString("O"),
        ["project"] = new JsonObject { ["id"] = e.ProjectId.ToString(), ["name"] = e.ProjectName },
        ["data"] = e.Data.DeepClone(),
    }.ToJsonString();

    private async Task DeliverWithRetriesAsync(IntegrationConnection hook, WebhookConfig config, ProjectEvent e, CancellationToken ct)
    {
        var deliveryId = Guid.NewGuid().ToString("N");
        var body = Payload(e, deliveryId);
        for (var attempt = 1; attempt <= options.RetryDelays.Length + 1; attempt++)
        {
            if (attempt > 1)
            {
                try { await Task.Delay(options.RetryDelays[attempt - 2], ct); }
                catch (OperationCanceledException) { return; }
            }
            var (status, error) = await SendAsync(hook, config, e.Event, deliveryId, body, ct);
            var ok = status is >= 200 and < 300;
            try { await db.AddWebhookDeliveryAsync(new WebhookDelivery(0, hook.ProjectId, hook.Id, deliveryId, e.Event, attempt, status, ok, error, DateTime.UtcNow)); }
            catch (Exception ex) { log.LogWarning(ex, "Recording webhook delivery failed"); }
            if (ok) return;
            // Client errors other than throttling/timeouts will not succeed on retry.
            if (status is >= 400 and < 500 and not 408 and not 429) return;
        }
    }

    /// <summary>One signed POST. Returns the HTTP status (null if no response) and an error summary.</summary>
    public async Task<(int? Status, string? Error)> SendAsync(IntegrationConnection hook, WebhookConfig config, string evt, string deliveryId, string body, CancellationToken ct = default)
    {
        await _concurrency.WaitAsync(ct);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, config.Url.Trim());
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Meridian-Webhooks", "1.0"));
            request.Headers.TryAddWithoutValidation("X-Meridian-Event", evt);
            request.Headers.TryAddWithoutValidation("X-Meridian-Delivery", deliveryId);
            if (integrations.Unprotect(hook.ProtectedSecret, hook.Id) is { Length: > 0 } secret)
                request.Headers.TryAddWithoutValidation(WebhookSignature.Header, WebhookSignature.Sign(secret, body));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Timeout);
            using var response = await httpFactory.CreateClient(IntegrationService.HttpClientName).SendAsync(request, timeout.Token);
            return ((int)response.StatusCode, response.IsSuccessStatusCode ? null : response.ReasonPhrase);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, "Timed out"); }
        catch (HttpRequestException ex) { return (null, ex.Message); }
        catch (InvalidOperationException ex) { return (null, ex.Message); }
        finally { _concurrency.Release(); }
    }

    /// <summary>Sends a single "ping" now (the Test button) and records it.</summary>
    public async Task<(int? Status, string? Error)> PingAsync(IntegrationConnection hook, string projectName)
    {
        var e = new ProjectEvent(hook.ProjectId, projectName, WebhookEvents.Ping, new JsonObject { ["message"] = "Hello from Meridian" }, DateTime.UtcNow);
        var id = Guid.NewGuid().ToString("N");
        var result = await SendAsync(hook, WebhookConfig.Parse(hook.ConfigJson), e.Event, id, Payload(e, id));
        await db.AddWebhookDeliveryAsync(new WebhookDelivery(0, hook.ProjectId, hook.Id, id, e.Event, 1, result.Status, result.Status is >= 200 and < 300, result.Error, DateTime.UtcNow));
        return result;
    }
}
