using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Meridian.Assistant.Changes;
using Meridian.Assistant.Engine;
using Meridian.Assistant.Providers;
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
/// <item><c>Meridian:AI:Provider</c> picks who answers: <c>Anthropic</c> (default), <c>Local</c> (an OpenAI-compatible server:
/// <c>Meridian:AI:Local:BaseUrl</c>, <c>:Model</c>, <c>:ApiKey</c>…) or, in local/desktop mode only, <c>ClaudeCode</c>. On a
/// server only the administrator sets these (configuration); users can't point the server at another address. In
/// local/desktop mode they are per-device settings stored next to the API key.</item>
/// </list>
/// Keys never leave the server process: they are not rendered, logged or sent to the browser.
/// </summary>
public sealed class AssistantSettings
{
    private readonly IConfiguration _config;
    private readonly MeridianHostInfo _host;
    private readonly Hosting.SecretProtector _protector;
    private readonly string _path;
    private readonly object _gate = new();
    private Stored _stored;

    private sealed class Stored
    {
        public string? ProtectedKey { get; set; }
        public List<Guid> Acknowledged { get; set; } = new();

        /// <summary>Who acknowledged the local-model notice (content stays on the configured server).</summary>
        public List<Guid> AcknowledgedLocal { get; set; } = new();
        public AssistantProvider? Provider { get; set; }
        public LocalStored? Local { get; set; }
        public ClaudeCodeStored? ClaudeCode { get; set; }
    }

    private sealed class LocalStored
    {
        public string? BaseUrl { get; set; }
        public string? Model { get; set; }
        public string? ProtectedApiKey { get; set; }
        public int? ContextWindow { get; set; }
        public int? MaxTokens { get; set; }
        public double? Temperature { get; set; }
        public ToolCallingMode? ToolCalling { get; set; }
    }

    private sealed class ClaudeCodeStored
    {
        public string? Path { get; set; }
        public string? Model { get; set; }
    }

    public AssistantSettings(IConfiguration config, MeridianHostInfo host, IDataProtectionProvider protection, Hosting.LegacySecretKeys? legacyKeys = null)
    {
        _config = config;
        _host = host;
        _protector = new Hosting.SecretProtector(protection, "Meridian.Assistant.ApiKey.v1", legacyKeys);
        _path = Path.Combine(host.DataPath, "assistant.json");
        _stored = Load();
        MigratedSecrets = MigrateLegacyKeys();
    }

    /// <summary>How many saved keys written by an older version were re-protected under the stable key name when loading.</summary>
    public int MigratedSecrets { get; }

    private int MigrateLegacyKeys()
    {
        lock (_gate)
        {
            var n = 0;
            _protector.Unprotect(_stored.ProtectedKey, out var key);
            if (key is not null) { _stored.ProtectedKey = key; n++; }
            if (_stored.Local is { } local)
            {
                _protector.Unprotect(local.ProtectedApiKey, out var localKey);
                if (localKey is not null) { local.ProtectedApiKey = localKey; n++; }
            }
            if (n > 0) Save();
            return n;
        }
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
            return _protector.Unprotect(p);   // null: keys lost or file copied from another machine
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

    /// <summary>
    /// Whether this user has seen the data notice for the active provider: that content is sent to Anthropic (Claude, and
    /// Claude Code, which uses the user's own Anthropic login), or that it stays on the configured local model server.
    /// </summary>
    public bool HasAcknowledged(Guid? user)
    {
        lock (_gate) return AckList.Contains(user ?? Guid.Empty);
    }

    public void Acknowledge(Guid? user)
    {
        lock (_gate)
        {
            var list = AckList;
            if (list.Contains(user ?? Guid.Empty)) return;
            list.Add(user ?? Guid.Empty);
            Save();
        }
    }

    private List<Guid> AckList => Provider == AssistantProvider.Local ? _stored.AcknowledgedLocal : _stored.Acknowledged;

    /// <summary>Why the assistant can't be used, with how to enable it; null when it can.</summary>
    public string? UnavailableReason =>
        DisabledByAdmin ? "The AI assistant has been turned off by the administrator (Meridian:AI:Enabled=false)."
        : Provider switch
        {
            AssistantProvider.Local => LocalOptions() != null ? null
                : _host.LocalMode ? "Set the local model server's URL and pick a model in Settings ▸ AI assistant to turn the assistant on."
                : "The AI assistant is set to use a local model, but it is not fully configured. An administrator can set Meridian:AI:Local:BaseUrl and Meridian:AI:Local:Model on the server.",
            AssistantProvider.ClaudeCode => !_host.LocalMode ? "Claude Code can only be used by the desktop app or a local Meridian; a server uses an API key or a local model server."
                : ClaudeCodePath() == null ? ClaudeCodeProvider.NotInstalled : null,
            _ => HasKey ? null
                : _host.LocalMode ? "Add an Anthropic API key, or choose a local model or Claude Code, in Settings ▸ AI assistant to turn the assistant on."
                : "The AI assistant is off on this server. Ask your administrator to turn it on with an Anthropic API key or a local model server (the Administration page of the user guide explains the Meridian:AI:ApiKey and Meridian:AI:Local settings).",
        };

    // ================================================================ provider (Claude, local model, Claude Code)

    /// <summary>Who answers: per device in local/desktop mode, set by the administrator (configuration) on servers.</summary>
    public AssistantProvider Provider
    {
        get
        {
            if (_host.LocalMode) lock (_gate) if (_stored.Provider is { } p && Enum.IsDefined(p)) return p;
            return ParseProvider(_config["Meridian:AI:Provider"]);
        }
    }

    public static AssistantProvider ParseProvider(string? value) => value?.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "") switch
    {
        "local" or "openai" or "openaicompatible" or "ollama" or "lmstudio" => AssistantProvider.Local,
        "claudecode" => AssistantProvider.ClaudeCode,
        _ => AssistantProvider.Anthropic,
    };

    /// <summary>Whether this installation lets the user change the provider and its server (local/desktop mode only).</summary>
    public bool CanConfigureProvider => _host.LocalMode;

    private void RequireLocalMode()
    {
        if (!_host.LocalMode) throw new InvalidOperationException("On a server the assistant's provider is configured by the administrator (Meridian:AI:Provider and Meridian:AI:Local:*).");
    }

    public void SetProvider(AssistantProvider provider)
    {
        RequireLocalMode();
        lock (_gate)
        {
            _stored.Provider = provider;
            Save();
        }
    }

    /// <summary>The local model server's settings as entered (stored values over configuration), for the settings dialog.</summary>
    public sealed record LocalSettings(string BaseUrl, string Model, bool HasApiKey, int ContextWindow, int MaxTokens, double? Temperature, ToolCallingMode ToolCalling);

    public LocalSettings Local
    {
        get
        {
            LocalStored s;
            lock (_gate) s = _host.LocalMode ? _stored.Local ?? new LocalStored() : new LocalStored();
            var c = _config.GetSection("Meridian:AI:Local");
            var d = new LocalModelOptions { BaseUrl = new Uri(LocalModelOptions.OllamaUrl), Model = "" };
            return new LocalSettings(
                s.BaseUrl ?? c["BaseUrl"] ?? "",
                s.Model ?? c["Model"] ?? "",
                LocalApiKey() != null,
                s.ContextWindow ?? (int.TryParse(c["ContextWindow"], out var cw) && cw >= 2048 ? cw : d.ContextWindow),
                s.MaxTokens ?? (int.TryParse(c["MaxTokens"], out var mt) && mt >= 256 ? mt : d.MaxTokens),
                s.Temperature ?? (double.TryParse(c["Temperature"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : d.Temperature),
                s.ToolCalling ?? (Enum.TryParse<ToolCallingMode>(c["ToolCalling"], true, out var tc) && Enum.IsDefined(tc) ? tc : ToolCallingMode.Auto));
        }
    }

    private string? LocalApiKey()
    {
        if (_host.LocalMode)
            lock (_gate)
                if (_stored.Local?.ProtectedApiKey is { Length: > 0 } p)
                    if (_protector.Unprotect(p) is { } key) return key;
        return _config["Meridian:AI:Local:ApiKey"] is { Length: > 0 } k ? k : null;
    }

    /// <summary>
    /// Options for <see cref="OpenAiCompatibleProvider"/>, or null when the base URL or model is missing or invalid.
    /// The URL is only ever the configured one (administrator on servers, the user on their own computer).
    /// </summary>
    public LocalModelOptions? LocalOptions()
    {
        var s = Local;
        if (LocalModelOptions.NormalizeBaseUrl(s.BaseUrl) is not { } url || string.IsNullOrWhiteSpace(s.Model)) return null;
        var firstToken = int.TryParse(_config["Meridian:AI:Local:TimeoutSeconds"], out var secs) && secs >= 10 ? TimeSpan.FromSeconds(secs) : TimeSpan.FromMinutes(5);
        return new LocalModelOptions
        {
            BaseUrl = url,
            Model = s.Model.Trim(),
            ApiKey = LocalApiKey(),
            ContextWindow = s.ContextWindow,
            MaxTokens = s.MaxTokens,
            Temperature = s.Temperature,
            ToolCalling = s.ToolCalling,
            FirstTokenTimeout = firstToken,
            RevealEndpoint = _host.LocalMode,
        };
    }

    /// <summary>Options to probe a server before saving it (desktop only: "Test connection" on an unsaved URL).</summary>
    public LocalModelOptions? DraftLocalOptions(string baseUrl, string model)
    {
        RequireLocalMode();
        return LocalModelOptions.NormalizeBaseUrl(baseUrl) is { } url
            ? (LocalOptions() ?? new LocalModelOptions { BaseUrl = url, Model = "" }) with { BaseUrl = url, Model = model.Trim(), RevealEndpoint = true, ApiKey = LocalApiKey() }
            : null;
    }

    /// <summary>Saves the local model settings (local/desktop mode only). Throws <see cref="ArgumentException"/> for an unusable URL.</summary>
    public void SaveLocal(string baseUrl, string model, int contextWindow, int maxTokens, double? temperature, ToolCallingMode toolCalling)
    {
        RequireLocalMode();
        var url = LocalModelOptions.NormalizeBaseUrl(baseUrl) ?? throw new ArgumentException("Enter an http:// or https:// address, such as http://localhost:11434/v1.");
        if (model.Length > 200 || model.Any(char.IsControl)) throw new ArgumentException("That model name is not valid.");
        lock (_gate)
        {
            var s = _stored.Local ??= new LocalStored();
            s.BaseUrl = url.AbsoluteUri.TrimEnd('/');
            s.Model = model.Trim();
            s.ContextWindow = Math.Clamp(contextWindow, 2048, 2_000_000);
            s.MaxTokens = Math.Clamp(maxTokens, 256, 200_000);
            s.Temperature = temperature is { } t ? Math.Clamp(t, 0, 2) : null;
            s.ToolCalling = Enum.IsDefined(toolCalling) ? toolCalling : ToolCallingMode.Auto;
            Save();
        }
    }

    public void SaveLocalApiKey(string? key)
    {
        RequireLocalMode();
        key = key?.Trim();
        if (key is { Length: > 0 } && (key.Length > 4096 || key.Any(char.IsWhiteSpace))) throw new ArgumentException("That does not look like an API key.");
        lock (_gate)
        {
            var s = _stored.Local ??= new LocalStored();
            s.ProtectedApiKey = key is { Length: > 0 } ? _protector.Protect(key) : null;
            Save();
        }
    }

    /// <summary>Claude Code: the path override and model (local/desktop mode).</summary>
    public (string? Path, string? Model) ClaudeCode
    {
        get
        {
            ClaudeCodeStored? s;
            lock (_gate) s = _stored.ClaudeCode;
            return (s?.Path ?? (_config["Meridian:AI:ClaudeCode:Path"] is { Length: > 0 } p ? p : null),
                    s?.Model ?? (_config["Meridian:AI:ClaudeCode:Model"] is { Length: > 0 } m ? m : null));
        }
    }

    public void SaveClaudeCode(string? path, string? model)
    {
        RequireLocalMode();
        path = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        if (path != null && !File.Exists(path)) throw new ArgumentException("No file at that path.");
        if (model is { Length: > 100 } || (model?.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ?? false)) throw new ArgumentException("That model name is not valid.");
        lock (_gate)
        {
            _stored.ClaudeCode = new ClaudeCodeStored { Path = path, Model = model };
            Save();
        }
    }

    /// <summary>The Claude Code executable found (override, PATH or the usual install folders), without running it.</summary>
    public string? ClaudeCodePath() => _host.LocalMode ? ClaudeCodeLocator.Candidates(ClaudeCode.Path).FirstOrDefault(File.Exists) : null;

    private ClaudeCodeInstall? _detected;
    private DateTime _detectedAt;

    /// <summary>Runs <c>claude --version</c> (cached for a minute); null when Claude Code isn't installed or this is a server.</summary>
    public async Task<ClaudeCodeInstall?> DetectClaudeCodeAsync(bool refresh = false)
    {
        if (!_host.LocalMode) return null;
        if (!refresh && DateTime.UtcNow - _detectedAt < TimeSpan.FromMinutes(1)) return _detected;
        var found = await ClaudeCodeLocator.DetectAsync(ClaudeCode.Path);
        _detected = found;
        _detectedAt = DateTime.UtcNow;
        return found;
    }

    /// <summary>The last detection result (no process is started).</summary>
    public ClaudeCodeInstall? DetectedClaudeCode => _detected;

    public ClaudeCodeOptions? ClaudeCodeOptions() =>
        ClaudeCodePath() is { } path ? new ClaudeCodeOptions { ExecutablePath = path, Model = ClaudeCode.Model, MaxTurns = 24 } : null;

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

/// <summary>Who answers the assistant's questions.</summary>
public enum AssistantProvider { Anthropic, Local, ClaudeCode }

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
public sealed class AssistantService(Workspace ws, AssistantSettings settings, AssistantConversations conversations, IAssistantClientFactory clients,
                                     ILocalModelHttpFactory? localHttp = null)
{
    private readonly ILocalModelHttpFactory _localHttp = localHttp ?? new LocalModelHttpFactory();

    private CancellationTokenSource? _cts;

    public AssistantSettings Settings => settings;
    public event Action? Changed;

    /// <summary>Conversations are per user, project and branch — and start over when packages become hidden from the user, so an
    /// earlier conversation (which saw the whole model) isn't continued with a filtered one.</summary>
    public string Key => $"{ws.User?.Id ?? Guid.Empty}:{ws.Session?.ProjectId.ToString() ?? "local"}:{ws.BranchId?.ToString() ?? "main"}{(ws.HasHiddenPackages ? ":filtered" : "")}";
    public Conversation Conversation => conversations.Get(Key);

    public string? UnavailableReason => !ws.Preferences.AssistantEnabled ? "You turned the AI assistant off in Settings ▸ AI assistant." : settings.UnavailableReason;

    /// <summary>The server's options with the model chosen in Settings ▸ AI assistant (the server's model when the user kept the default).</summary>
    public AssistantOptions Options => ws.Preferences.AssistantModel is { } m && m != Meridian.Web.Settings.AssistantModels.Default ? settings.Options with { Model = m } : settings.Options;
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

    /// <summary>The configured provider, or null when it is not usable. Claude keeps the exact SDK path; nothing here takes a URL from the browser.</summary>
    public IChatProvider? CreateProvider() => settings.Provider switch
    {
        AssistantProvider.Local => settings.LocalOptions() is { } o ? new OpenAiCompatibleProvider(_localHttp.Create(o.BaseUrl), o) : null,
        AssistantProvider.ClaudeCode => settings.ClaudeCodeOptions() is { } c ? new ClaudeCodeProvider(c) : null,
        _ => settings.ApiKey() is { } key ? new AnthropicChatProvider(clients.Create(key), Options) : null,
    };

    /// <summary>What the panel header shows: provider and model.</summary>
    public ChatProviderInfo ProviderInfo => settings.Provider switch
    {
        AssistantProvider.Local => new ChatProviderInfo(ChatProviderKind.OpenAiCompatible, settings.Local.Model, settings.LocalOptions()?.BaseUrl.GetLeftPart(UriPartial.Authority)),
        AssistantProvider.ClaudeCode => new ChatProviderInfo(ChatProviderKind.ClaudeCode, settings.ClaudeCode.Model ?? "Claude Code default"),
        _ => new ChatProviderInfo(ChatProviderKind.Anthropic, Options.Model),
    };

    /// <summary>A provider for the settings dialog's discovery and "Test connection" (the configured server only).</summary>
    public OpenAiCompatibleProvider? LocalProvider(LocalModelOptions? draft = null) =>
        (draft ?? settings.LocalOptions()) is { } o ? new OpenAiCompatibleProvider(_localHttp.Create(o.BaseUrl), o) : null;

    private async Task RunAsync(TurnRequest request)
    {
        if (UnavailableReason != null || Busy || !Acknowledged) return;
        var provider = CreateProvider();
        if (provider == null) return;
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
        var engine = new AssistantEngine(provider, Options);
        await engine.RunTurnAsync(conversation, ws.Model, request, () => Changed?.Invoke(), _cts.Token);
    }

    /// <summary>
    /// A narrative writer for generated documents (the report dialog's "AI narrative"), or null when the assistant is off or
    /// this user has not accepted the data notice of the active provider.
    /// </summary>
    public NarrativeWriter? NarrativeWriter()
    {
        if (UnavailableReason != null || !Acknowledged) return null;
        if (settings.Provider == AssistantProvider.Anthropic)
            return settings.ApiKey() is { } key ? new NarrativeWriter(clients.Create(key), settings.Options) : null;
        return CreateProvider() is { } provider ? new NarrativeWriter(provider, settings.Options) : null;
    }

    /// <summary>The derivations' optional AI fill-in ("Fill in with AI"), or null when the assistant is off or its data notice is not accepted.</summary>
    public Meridian.Core.Derivation.IDerivationAssistant? DerivationAssistant() =>
        UnavailableReason != null || !Acknowledged ? null : CreateProvider() is { } provider ? new DerivationWriter(provider) : null;

    public ApplyResult Apply(ChangeSet set)
    {
        if (ws.ReadOnly) return new ApplyResult(false, "This project is view-only for you: ask an editor to apply these changes.", set.CreatedIds);
        // Respect element locks held by collaborators, as manual edits do.
        var locked = set.Items.Where(set.IsEffective)
            .SelectMany(p => new[] { p.OwnerRef, p.SubjectRef, p.SourceRef, p.TargetRef })
            .Select(ws.Model.Find).OfType<Element>().FirstOrDefault(ws.IsLockedForMe);
        if (locked != null)
            return new ApplyResult(false, $"'{locked.DisplayName}' is locked by {ws.LockOf(locked)?.User.DisplayName ?? "someone else"}; nothing was applied.", set.CreatedIds);
        // Package permissions: the session refuses edits to read-only packages; say so before anything is applied.
        var protectedElement = set.Items.Where(set.IsEffective)
            .SelectMany(p => new[] { p.OwnerRef, p.SubjectRef, p.SourceRef, p.TargetRef })
            .Select(ws.Model.Find).OfType<Element>().FirstOrDefault(e => !ws.CanEdit(e));
        if (protectedElement != null)
            return new ApplyResult(false, ws.Access.DeniedMessage(protectedElement).Replace("your change was", "nothing was"), set.CreatedIds);
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
        services.AddSingleton<ILocalModelHttpFactory>(new LocalModelHttpFactory());   // only ever the configured base URL's host
        services.AddScoped<AssistantService>();
        return services;
    }
}
