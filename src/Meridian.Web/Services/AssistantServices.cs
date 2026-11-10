using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Meridian.Assistant.Changes;
using Meridian.Assistant.Engine;
using Meridian.Core.Model;
using Meridian.Data;
using Microsoft.AspNetCore.DataProtection;

namespace Meridian.Web.Services;

/// <summary>
/// AI assistant configuration (docs/AI.md).
/// <list type="bullet">
/// <item>Server mode: the API key comes from <c>Meridian:AI:ApiKey</c> or the <c>ANTHROPIC_API_KEY</c> environment variable.</item>
/// <item>Local/desktop mode: the key is entered in the assistant's settings and stored encrypted with ASP.NET Data Protection
/// in <c>{DataPath}/assistant.json</c> (config / environment keys are used when none is saved).</item>
/// <item><c>Meridian:AI:Enabled=false</c> turns the assistant off for everyone; <c>Meridian:AI:Model</c> picks the model.</item>
/// </list>
/// The key never leaves the server process: it is not rendered, logged or sent to the browser.
/// </summary>
public sealed class AssistantSettings
{
    private readonly IConfiguration _config;
    private readonly MeridianHostInfo _host;
    private readonly IDataProtector _protector;
    private readonly string _path;
    private readonly object _gate = new();
    private Stored _stored;

    private sealed class Stored
    {
        public string? ProtectedKey { get; set; }
        public List<Guid> Acknowledged { get; set; } = new();
    }

    public AssistantSettings(IConfiguration config, MeridianHostInfo host, IDataProtectionProvider protection)
    {
        _config = config;
        _host = host;
        _protector = protection.CreateProtector("Meridian.Assistant.ApiKey.v1");
        _path = Path.Combine(host.DataPath, "assistant.json");
        _stored = Load();
    }

    public bool LocalMode => _host.LocalMode;

    /// <summary>Environment lookup (replaceable in tests so a developer's own ANTHROPIC_API_KEY never leaks in).</summary>
    public Func<string, string?> ReadEnvironment { get; set; } = Environment.GetEnvironmentVariable;

    /// <summary>Turned off by the server administrator (<c>Meridian:AI:Enabled=false</c>).</summary>
    public bool DisabledByAdmin => string.Equals(_config["Meridian:AI:Enabled"], "false", StringComparison.OrdinalIgnoreCase);

    public AssistantOptions Options => new()
    {
        Model = _config["Meridian:AI:Model"] is { Length: > 0 } m ? m : AssistantOptions.DefaultModel,
        MaxTokens = int.TryParse(_config["Meridian:AI:MaxTokens"], out var t) && t >= 1024 ? t : 16000,
        ServerSideFallbacks = !string.Equals(_config["Meridian:AI:ServerSideFallbacks"], "false", StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>The key to use, or null. Server side only.</summary>
    public string? ApiKey()
    {
        if (_host.LocalMode && SavedKey() is { } saved) return saved;
        if (_config["Meridian:AI:ApiKey"] is { Length: > 0 } configured) return configured;
        return ReadEnvironment("ANTHROPIC_API_KEY") is { Length: > 0 } env ? env : null;
    }

    public bool HasKey => ApiKey() != null;

    /// <summary>True when a key was saved in the settings dialog (local mode).</summary>
    public bool HasSavedKey => _host.LocalMode && SavedKey() != null;

    private string? SavedKey()
    {
        lock (_gate)
        {
            if (_stored.ProtectedKey is not { Length: > 0 } p) return null;
            try { return _protector.Unprotect(p); }
            catch (CryptographicException) { return null; }   // keys rotated or file copied from another machine
        }
    }

    /// <summary>Saves the API key (local/desktop mode only), encrypted with Data Protection.</summary>
    public void SaveKey(string key)
    {
        if (!_host.LocalMode) throw new InvalidOperationException("On a server the API key is configured by the administrator (Meridian:AI:ApiKey).");
        key = key.Trim();
        if (key.Length < 20 || key.Any(char.IsWhiteSpace)) throw new ArgumentException("That does not look like an Anthropic API key.");
        lock (_gate)
        {
            _stored.ProtectedKey = _protector.Protect(key);
            Save();
        }
    }

    public void RemoveKey()
    {
        if (!_host.LocalMode) throw new InvalidOperationException("On a server the API key is configured by the administrator (Meridian:AI:ApiKey).");
        lock (_gate)
        {
            _stored.ProtectedKey = null;
            Save();
        }
    }

    /// <summary>Whether this user has seen the notice that model content is sent to Anthropic.</summary>
    public bool HasAcknowledged(Guid? user)
    {
        lock (_gate) return _stored.Acknowledged.Contains(user ?? Guid.Empty);
    }

    public void Acknowledge(Guid? user)
    {
        lock (_gate)
        {
            if (_stored.Acknowledged.Contains(user ?? Guid.Empty)) return;
            _stored.Acknowledged.Add(user ?? Guid.Empty);
            Save();
        }
    }

    /// <summary>Why the assistant can't be used, with how to enable it; null when it can.</summary>
    public string? UnavailableReason =>
        DisabledByAdmin ? "The AI assistant has been turned off by the administrator (Meridian:AI:Enabled=false)."
        : HasKey ? null
        : _host.LocalMode ? "Add your Anthropic API key in the assistant settings to turn the assistant on. Get a key at console.anthropic.com."
        : "The AI assistant is off: no Anthropic API key is configured. An administrator can enable it by setting Meridian:AI:ApiKey (or the ANTHROPIC_API_KEY environment variable) on the server.";

    private Stored Load()
    {
        try
        {
            if (File.Exists(_path)) return JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path)) ?? new Stored();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new Stored();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_stored));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Conversations, in memory, per user per project (and branch). They are lost when the server restarts.</summary>
public sealed class AssistantConversations
{
    private const int Max = 1000;
    private readonly ConcurrentDictionary<string, (Conversation Conversation, DateTime Used)> _map = new();

    public Conversation Get(string key)
    {
        var entry = _map.AddOrUpdate(key, _ => (new Conversation(), DateTime.UtcNow), (_, e) => (e.Conversation, DateTime.UtcNow));
        if (_map.Count > Max)
            foreach (var old in _map.OrderBy(kv => kv.Value.Used).Take(_map.Count - Max).ToList()) _map.TryRemove(old.Key, out _);
        return entry.Conversation;
    }

    public Conversation Reset(string key)
    {
        var c = new Conversation();
        _map[key] = (c, DateTime.UtcNow);
        return c;
    }
}

/// <summary>Quick actions offered above the chat.</summary>
public enum QuickAction { GenerateRequirements, SuggestTraceLinks, ReviewModel, DiagramFromDescription, DiagramModel }

/// <summary>
/// The assistant for one circuit: builds the workbench context, runs turns through <see cref="AssistantEngine"/> and
/// applies reviewed change sets to the workspace model (one undoable, replicated step). Viewers can ask but not apply.
/// </summary>
public sealed class AssistantService(Workspace ws, AssistantSettings settings, AssistantConversations conversations, IAssistantClientFactory clients)
{
    private CancellationTokenSource? _cts;

    public AssistantSettings Settings => settings;
    public event Action? Changed;

    public string Key => $"{ws.User?.Id ?? Guid.Empty}:{ws.Session?.ProjectId.ToString() ?? "local"}:{ws.BranchId?.ToString() ?? "main"}";
    public Conversation Conversation => conversations.Get(Key);

    public string? UnavailableReason => settings.UnavailableReason;
    public bool Acknowledged => settings.HasAcknowledged(ws.User?.Id);
    public void Acknowledge() { settings.Acknowledge(ws.User?.Id); Changed?.Invoke(); }
    public bool CanApply => !ws.ReadOnly;
    public bool Busy => Conversation.Busy;

    public void NewChat()
    {
        if (Busy) return;
        conversations.Reset(Key);
        Changed?.Invoke();
    }

    public void Stop() => _cts?.Cancel();

    public string Context() => Prompts.Context(ws.Model, ws.Session?.Name ?? ws.Model.Root.Name, ws.Selected, ws.Active?.Diagram, ws.ReadOnly);

    public Task AskAsync(string question) => RunAsync(new TurnRequest(question, Context(), AssistantEffort.Medium));

    public Task QuickAsync(QuickAction action, string? text = null, Element? target = null, string? diagramKind = null)
    {
        var scope = target ?? ws.Selected?.AncestorsAndSelf().FirstOrDefault(a => a.Kind is ElementKind.Package or ElementKind.Model);
        var package = scope ?? ws.Model.Root;
        var (prompt, label) = action switch
        {
            QuickAction.GenerateRequirements => (Prompts.GenerateRequirements(ws.Model, package, text ?? ""), $"Generate requirements in {package.DisplayName}"),
            QuickAction.SuggestTraceLinks => (Prompts.SuggestTraceLinks(scope), "Suggest trace links" + (scope is { Kind: ElementKind.Package } ? $" in {scope.DisplayName}" : "")),
            QuickAction.ReviewModel => (Prompts.ReviewModel(scope), "Review this model" + (scope is { Kind: ElementKind.Package } ? $" ({scope.DisplayName})" : "")),
            QuickAction.DiagramModel => (Prompts.DiagramModel(scope), "Diagram my model" + (scope is { Kind: ElementKind.Package } ? $" ({scope.DisplayName})" : "")),
            _ => (Prompts.DiagramFromDescription(package, diagramKind ?? Prompts.DiagramKinds[0], text ?? ""), $"{diagramKind ?? Prompts.DiagramKinds[0]} from description"),
        };
        return RunAsync(new TurnRequest(prompt, Context(), AssistantEffort.High, label));
    }

    /// <summary>Longest prompt one turn may send (pasted specifications included): requests are paid with the server's key.</summary>
    public const int MaxPromptChars = 200_000;

    /// <summary>Most API messages a conversation may hold; every turn resends the whole history.</summary>
    public const int MaxHistoryMessages = 400;

    private async Task RunAsync(TurnRequest request)
    {
        if (UnavailableReason != null || Busy || !Acknowledged) return;
        var key = settings.ApiKey();
        if (key == null) return;
        var conversation = Conversation;
        var tooLong = request.Prompt.Length > MaxPromptChars ? $"That request is too long ({request.Prompt.Length:N0} characters; at most {MaxPromptChars:N0}). Shorten it or split it up."
            : conversation.History.Count >= MaxHistoryMessages ? "This conversation is too long to continue. Start a new chat."
            : null;
        if (tooLong != null)
        {
            conversation.Items.Add(new NoticeItem(NoticeKind.Warning, tooLong));
            Changed?.Invoke();
            return;
        }
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var engine = new AssistantEngine(clients.Create(key), settings.Options);
        await engine.RunTurnAsync(conversation, ws.Model, request, () => Changed?.Invoke(), _cts.Token);
    }

    /// <summary>
    /// A narrative writer for generated documents (the report dialog's "AI narrative"), or null when the assistant is off or
    /// this user has not accepted the notice that model content is sent to Anthropic.
    /// </summary>
    public NarrativeWriter? NarrativeWriter() =>
        UnavailableReason == null && Acknowledged && settings.ApiKey() is { } key ? new NarrativeWriter(clients.Create(key), settings.Options) : null;

    public ApplyResult Apply(ChangeSet set)
    {
        if (ws.ReadOnly) return new ApplyResult(false, "This project is view-only for you: ask an editor to apply these changes.", set.CreatedIds);
        // Respect element locks held by collaborators, as manual edits do.
        var locked = set.Items.Where(set.IsEffective)
            .SelectMany(p => new[] { p.OwnerRef, p.SubjectRef, p.SourceRef, p.TargetRef })
            .Select(ws.Model.Find).OfType<Element>().FirstOrDefault(ws.IsLockedForMe);
        if (locked != null)
            return new ApplyResult(false, $"'{locked.DisplayName}' is locked by {ws.LockOf(locked)?.User.DisplayName ?? "someone else"}; nothing was applied.", set.CreatedIds);
        var result = set.Apply(ws.Model);
        Conversation.NoteOutcome(set);
        ws.Log($"Assistant: {set.Outcome}");
        ws.SetStatus(result.Ok ? set.Outcome ?? "Applied" : "The assistant's changes were not applied — see the assistant panel");
        Changed?.Invoke();
        return result;
    }

    public void Discard(ChangeSet set)
    {
        set.Discard();
        Conversation.NoteOutcome(set);
        Changed?.Invoke();
    }
}

public static class AssistantServiceExtensions
{
    public static IServiceCollection AddMeridianAssistant(this IServiceCollection services)
    {
        services.AddSingleton<AssistantSettings>();
        services.AddSingleton<AssistantConversations>();
        services.AddSingleton<IAssistantClientFactory>(new AssistantClientFactory());
        services.AddScoped<AssistantService>();
        return services;
    }
}
