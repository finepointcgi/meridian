using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meridian.Assistant.Providers;

/// <summary>Whether the local model gets native function calling or the JSON-in-text protocol.</summary>
public enum ToolCallingMode { Auto, Native, Text }

/// <summary>Settings for an OpenAI-compatible server (Ollama, LM Studio, llama.cpp server, vLLM, LocalAI…).</summary>
public sealed record LocalModelOptions
{
    public const string OllamaUrl = "http://localhost:11434/v1";
    public const string LmStudioUrl = "http://localhost:1234/v1";

    /// <summary>The OpenAI-compatible base URL, ending in <c>/v1</c> (see <see cref="NormalizeBaseUrl"/>).</summary>
    public required Uri BaseUrl { get; init; }
    public required string Model { get; init; }
    public string? ApiKey { get; init; }

    /// <summary>The model's context window in tokens; older tool results are shortened to fit.</summary>
    public int ContextWindow { get; init; } = 32768;
    public int MaxTokens { get; init; } = 4096;
    public double? Temperature { get; init; } = 0.2;
    public ToolCallingMode ToolCalling { get; init; } = ToolCallingMode.Auto;

    /// <summary>How long to wait for the first token (a cold model may need to load first).</summary>
    public TimeSpan FirstTokenTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long the stream may stay silent once it has started.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Include the server address in error messages (desktop; servers keep it to administrators).</summary>
    public bool RevealEndpoint { get; init; }

    /// <summary>
    /// Parses a user- or admin-supplied base URL: http(s) only, no credentials, query or fragment; a bare host gets
    /// <c>/v1</c>. Returns null when it is not usable.
    /// </summary>
    public static Uri? NormalizeBaseUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.Host.Length == 0) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) path = path[..^"/chat/completions".Length];
        if (path.Length == 0) path = "/v1";
        return new Uri($"{uri.Scheme}://{uri.Authority}{path}");
    }

    /// <summary>Ollama is assumed on its default port (and confirmed by <c>/api/tags</c> during discovery).</summary>
    public bool LooksLikeOllama => BaseUrl.Port == 11434;
}

/// <summary>A model reported by the server.</summary>
public sealed record LocalModelInfo(string Id, long? SizeBytes = null);

/// <summary>Model discovery: the models found, whether the server is Ollama, and an error (null = reachable).</summary>
public sealed record LocalModelList(IReadOnlyList<LocalModelInfo> Models, bool IsOllama, string? Error);

/// <summary>The outcome of "Test connection".</summary>
public sealed record LocalConnectionTest(bool Ok, string Message, IReadOnlyList<LocalModelInfo> Models, bool? SupportsTools);

/// <summary>Remembers models that turned out not to support native tools (shared by all providers of the process).</summary>
public sealed class LocalToolSupportCache
{
    public static readonly LocalToolSupportCache Shared = new();
    private readonly ConcurrentDictionary<string, bool> _map = new(StringComparer.OrdinalIgnoreCase);
    private static string Key(Uri url, string model) => url.AbsoluteUri.TrimEnd('/') + "|" + model;
    public bool? Get(Uri url, string model) => _map.TryGetValue(Key(url, model), out var v) ? v : null;
    public void Set(Uri url, string model, bool supportsTools) => _map[Key(url, model)] = supportsTools;
}

/// <summary>
/// A local or self-hosted model behind an OpenAI-compatible <c>/chat/completions</c> endpoint, over raw HTTP with
/// server-sent events. Tools are offered as OpenAI <c>tools</c> (results go back as <c>role: tool</c> messages); a model
/// without function calling gets the JSON-in-text protocol of <see cref="TextToolProtocol"/> instead, with each call
/// validated against its schema and one corrective retry. The system prompt is Claude's, plus brief format guidance.
/// </summary>
public sealed class OpenAiCompatibleProvider(HttpClient http, LocalModelOptions options, LocalToolSupportCache? cache = null) : IChatProvider
{
    private readonly LocalToolSupportCache _cache = cache ?? LocalToolSupportCache.Shared;
    private int _ids;

    public LocalModelOptions Options => options;
    public ChatProviderInfo Info => new(ChatProviderKind.OpenAiCompatible, options.Model, options.BaseUrl.GetLeftPart(UriPartial.Authority));

    /// <summary>Brief extra guidance for local models (only on this path; Claude's prompt is unchanged).</summary>
    internal const string LocalGuidance = """

        <format_guidance>
        - Reply in GitHub-flavoured Markdown. Refer to elements as [Name](el:ID) using ids returned by tools; never invent ids.
        - Use the tools for facts about the model. Call a tool with arguments that match its JSON schema exactly.
        - Keep answers short. When you propose changes, say they are proposed and ready to review.
        </format_guidance>
        """;

    private Uri Endpoint(string path) => new(options.BaseUrl.AbsoluteUri.TrimEnd('/') + "/" + path);
    private Uri Root(string path) => new(options.BaseUrl.GetLeftPart(UriPartial.Authority) + "/" + path);
    private string Where => options.RevealEndpoint ? $" at {options.BaseUrl.GetLeftPart(UriPartial.Authority)}" : "";

    // ================================================================ chat

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, IChatStream stream, CancellationToken ct)
    {
        var mode = options.ToolCalling;
        if (mode == ToolCallingMode.Auto && _cache.Get(options.BaseUrl, options.Model) == false) mode = ToolCallingMode.Text;
        if (request.Tools.Count == 0) mode = ToolCallingMode.Native;   // nothing to call: plain chat

        if (mode != ToolCallingMode.Text)
        {
            try
            {
                var native = await CallAsync(BuildBody(request, native: true, extra: null), request.Tools, stream, ct);
                return ToResponse(native, request.Tools, textMode: false);
            }
            catch (ToolsUnsupportedException) when (options.ToolCalling == ToolCallingMode.Auto)
            {
                _cache.Set(options.BaseUrl, options.Model, false);
                stream.Status($"{options.Model} has no native tool calling; switching to text tool calls…");
            }
            catch (ToolsUnsupportedException)
            {
                throw new ChatProviderException($"Model '{options.Model}' does not support tool calling on this server. Set \"Model supports tool calling\" to Off (or Auto) in Settings ▸ AI assistant.");
            }
        }

        var first = await CallAsync(BuildBody(request, native: false, extra: null), request.Tools, stream, ct);
        var parsed = TextToolProtocol.Parse(first.Text, request.Tools, NextId);
        if (parsed.Errors.Count == 0 || first.Finish == "length") return ToResponse(first, request.Tools, textMode: true, parsed);

        // One corrective retry: show the model what was wrong and ask for the block again.
        stream.EndText();
        stream.Status("The tool call was not valid; asking the model to correct it…");
        var correction = new List<JsonObject>
        {
            new() { ["role"] = "assistant", ["content"] = first.Text },
            new()
            {
                ["role"] = "user",
                ["content"] = "Your tool call could not be used:\n- " + string.Join("\n- ", parsed.Errors) +
                              "\nReply again with ONLY a corrected ```json block``` ({\"tool\": ..., \"arguments\": {...}}) that matches the tool's schema exactly.",
            },
        };
        var second = await CallAsync(BuildBody(request, native: false, extra: correction), request.Tools, stream, ct);
        var reparsed = TextToolProtocol.Parse(second.Text, request.Tools, NextId);
        var response = ToResponse(second with { Usage = Add(first.Usage, second.Usage) }, request.Tools, textMode: true, reparsed);
        if (reparsed.Errors.Count == 0) return response;
        return new ChatResponse
        {
            Stop = reparsed.Calls.Count > 0 ? ChatStopReason.ToolUse : ChatStopReason.EndTurn,
            Message = response.Message,
            Usage = response.Usage,
            Warning = $"{options.Model} sent a tool call that was still invalid after one retry, so it was not run: " +
                      Short(string.Join("; ", reparsed.Errors)) + " A model with native tool calling usually does better.",
        };
    }

    private static ChatUsage Add(ChatUsage a, ChatUsage b) => new(a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens, 0);

    private string NextId() => "call" + Interlocked.Increment(ref _ids).ToString("D5", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record CallResult(string Text, List<ToolCallPart> NativeCalls, string? Finish, ChatUsage Usage);

    private ChatResponse ToResponse(CallResult r, IReadOnlyList<ChatTool> tools, bool textMode, TextToolProtocol.ParseResult? parsed = null)
    {
        var calls = r.NativeCalls;
        var text = r.Text;
        if (calls.Count == 0)
        {
            // Text mode, or a native-mode model that wrote its call as text (<tool_call>… or a fenced block).
            parsed ??= TextToolProtocol.Parse(r.Text, tools, NextId);
            if (parsed.Calls.Count > 0 || textMode)
            {
                calls = parsed.Calls.ToList();
                text = parsed.Text;
            }
        }
        var parts = new List<ChatPart>();
        if (text.Length > 0) parts.Add(new TextPart(text));
        parts.AddRange(calls);
        var stop = r.Finish switch
        {
            "length" => ChatStopReason.MaxTokens,
            "content_filter" => ChatStopReason.Refusal,
            _ when calls.Count > 0 => ChatStopReason.ToolUse,
            _ => ChatStopReason.EndTurn,
        };
        return new ChatResponse
        {
            Stop = stop,
            Message = parts.Count > 0 ? new ChatMessage(ChatRole.Assistant, parts) : null,
            Usage = r.Usage,
        };
    }

    // ---------------------------------------------------------------- request

    internal JsonObject BuildBody(ChatRequest request, bool native, List<JsonObject>? extra)
    {
        var system = request.System + LocalGuidance + (native ? "" : TextToolProtocol.Instructions(request.Tools));
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in Fit(request.Messages, system.Length + (native ? ToolsJson(request.Tools).ToJsonString().Length : 0)))
            foreach (var o in ToOpenAi(m, native)) messages.Add(o);
        if (extra != null) foreach (var o in extra) messages.Add(o.DeepClone());

        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["max_tokens"] = options.MaxTokens,
        };
        if (options.Temperature is { } t) body["temperature"] = t;
        if (native && request.Tools.Count > 0)
        {
            body["tools"] = ToolsJson(request.Tools);
            body["tool_choice"] = "auto";
        }
        return body;
    }

    internal static JsonArray ToolsJson(IReadOnlyList<ChatTool> tools) => new(tools.Select(t => (JsonNode)new JsonObject
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["parameters"] = t.Schema.DeepClone(),
        },
    }).ToArray());

    private static IEnumerable<JsonObject> ToOpenAi(ChatMessage m, bool native)
    {
        var text = string.Join("\n\n", m.Parts.OfType<TextPart>().Select(p => p.Text).Where(s => s.Length > 0));
        if (m.Role == ChatRole.Assistant)
        {
            var calls = m.ToolCalls.ToList();
            if (!native)
            {
                var rendered = TextToolProtocol.Render(calls);
                yield return new JsonObject { ["role"] = "assistant", ["content"] = text.Length > 0 && rendered.Length > 0 ? text + "\n\n" + rendered : text + rendered };
                yield break;
            }
            var o = new JsonObject { ["role"] = "assistant", ["content"] = text };
            if (calls.Count > 0)
                o["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = c.Name,
                        ["arguments"] = c.Input is { ValueKind: JsonValueKind.Object } i ? i.GetRawText() : (c.RawArguments.Length > 0 ? c.RawArguments : "{}"),
                    },
                }).ToArray());
            yield return o;
            yield break;
        }
        var results = m.ToolResults.ToList();
        if (results.Count > 0)
        {
            if (native)
                foreach (var r in results)
                    yield return new JsonObject { ["role"] = "tool", ["tool_call_id"] = r.ToolCallId, ["name"] = r.Name, ["content"] = r.IsError ? "ERROR: " + r.Content : r.Content };
            else
                yield return new JsonObject { ["role"] = "user", ["content"] = TextToolProtocol.RenderResults(results) };
        }
        if (text.Length > 0) yield return new JsonObject { ["role"] = "user", ["content"] = text };
    }

    /// <summary>
    /// Keeps the request inside the context window (about 3.5 characters per token): tool results of earlier rounds are
    /// shortened first; if it still does not fit, the conversation is too long for this model.
    /// </summary>
    private IReadOnlyList<ChatMessage> Fit(IReadOnlyList<ChatMessage> messages, int fixedChars)
    {
        var budget = (long)Math.Max(1024, options.ContextWindow - options.MaxTokens) * 7 / 2;
        long Size(IEnumerable<ChatMessage> ms) => fixedChars + ms.Sum(m => m.Parts.Sum(p => p switch
        {
            TextPart t => t.Text.Length,
            ToolResultPart r => r.Content.Length + 40,
            ToolCallPart c => c.RawArguments.Length + c.Name.Length + 40,
            _ => 0,
        }));
        if (Size(messages) <= budget) return messages;
        var list = messages.ToList();
        foreach (var limit in (int[])[4000, 1200, 300])
        {
            for (var i = 0; i < list.Count - 2; i++)
            {
                var m = list[i];
                if (!m.ToolResults.Any(r => r.Content.Length > limit)) continue;
                list[i] = new ChatMessage(m.Role, m.Parts.Select(p => p is ToolResultPart r && r.Content.Length > limit
                    ? r with { Content = r.Content[..limit] + "… [shortened to fit the local model's context window]" } : p).ToList());
            }
            if (Size(list) <= budget) return list;
        }
        throw new ChatProviderException(
            $"This conversation no longer fits {options.Model}'s context window (about {options.ContextWindow:N0} tokens). " +
            "Start a new chat, select a smaller package, or raise the context window in Settings ▸ AI assistant if the model supports more.");
    }

    // ---------------------------------------------------------------- streaming call

    private sealed class ToolsUnsupportedException() : Exception("tools unsupported");

    private sealed class PartialCall
    {
        public string? Id;
        public string Name = "";
        public readonly StringBuilder Args = new();
    }

    private async Task<CallResult> CallAsync(JsonObject body, IReadOnlyList<ChatTool> tools, IChatStream stream, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.FirstTokenTimeout);
        stream.Status($"Waiting for {options.Model}… (a local model may need a moment to load)");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint("chat/completions"))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (options.ApiKey is { Length: > 0 } key) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!res.IsSuccessStatusCode) throw await ErrorFor(res, body["tools"] != null, deadline.Token);

            var text = new StringBuilder();
            var shown = 0;
            var calls = new SortedDictionary<int, PartialCall>();
            string? finish = null;
            var usage = ChatUsage.None;

            void Show(bool final)
            {
                // Tool blocks of the text protocol are held back; everything else streams as it arrives.
                if (final)
                {
                    // The reply without its tool blocks; show whatever was held back.
                    var parsedText = TextToolProtocol.Parse(text.ToString(), tools, () => "").Text;
                    var already = text.ToString(0, shown).TrimStart();
                    if (parsedText.Length > already.Length && parsedText.StartsWith(already, StringComparison.Ordinal))
                        stream.Text(parsedText[already.Length..]);
                    return;
                }
                var visible = TextToolProtocol.VisibleLength(text.ToString());
                if (visible > shown)
                {
                    stream.Text(text.ToString(shown, visible - shown));
                    shown = visible;
                }
                else if (text.Length > shown) stream.Status("Preparing a tool call…");
            }

            if (res.Content.Headers.ContentType?.MediaType == "application/json")
            {
                // A server that ignored "stream": one complete JSON answer.
                var json = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(deadline.Token), default, deadline.Token);
                ReadChunk(json.RootElement, message: true);
            }
            else
            {
                using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(deadline.Token), Encoding.UTF8);
                while (true)
                {
                    var line = await reader.ReadLineAsync(deadline.Token);
                    if (line == null) break;
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;   // comments, event names, blank lines
                    var data = line[5..].Trim();
                    if (data == "[DONE]") break;
                    if (data.Length == 0) continue;
                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(data); }
                    catch (JsonException) { continue; }
                    using (doc)
                    {
                        if (doc.RootElement.TryGetProperty("error", out var err)) throw new ChatProviderException($"{options.Model} failed while answering: " + Short(ErrorText(err)));
                        ReadChunk(doc.RootElement, message: false);
                    }
                    deadline.CancelAfter(options.IdleTimeout);   // it has started: from now on only long silences time out
                }
            }
            Show(final: true);
            stream.Status(null);

            var parsedCalls = new List<ToolCallPart>();
            foreach (var (_, c) in calls)
            {
                if (c.Name.Length == 0) continue;
                var raw = c.Args.ToString();
                var input = string.IsNullOrWhiteSpace(raw) ? JsonDocument.Parse("{}").RootElement.Clone() : TextToolProtocol.ParseJson(raw);
                parsedCalls.Add(new ToolCallPart(c.Id is { Length: > 0 } id ? id : NextId(), c.Name, input is { ValueKind: JsonValueKind.Object } ? input : null, raw));
            }
            return new CallResult(text.ToString(), parsedCalls, finish, usage);

            void ReadChunk(JsonElement root, bool message)
            {
                if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                    usage = new ChatUsage(Long(u, "prompt_tokens"), Long(u, "completion_tokens"), 0);
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) return;
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String) finish = f.GetString();
                    if (!choice.TryGetProperty(message ? "message" : "delta", out var d) || d.ValueKind != JsonValueKind.Object) continue;
                    if (d.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } s)
                    {
                        text.Append(s);
                        Show(final: false);
                    }
                    else if ((d.TryGetProperty("reasoning_content", out var r) || d.TryGetProperty("reasoning", out r)) && r.ValueKind == JsonValueKind.String)
                        stream.Status("Thinking…");
                    if (d.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                    {
                        var n = 0;
                        foreach (var tc in tcs.EnumerateArray())
                        {
                            var index = tc.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var i) ? i : (message ? n : calls.Count == 0 ? 0 : calls.Keys.Max());
                            n++;
                            if (!calls.TryGetValue(index, out var pc)) calls[index] = pc = new PartialCall();
                            if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } idText) pc.Id = idText;
                            if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                            {
                                if (fn.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } nm) pc.Name = nm;
                                if (fn.TryGetProperty("arguments", out var a))
                                    pc.Args.Append(a.ValueKind == JsonValueKind.String ? a.GetString() : a.GetRawText());   // some servers send an object
                            }
                            stream.Status(pc.Name.Length > 0 ? $"Preparing {pc.Name}…" : "Preparing a tool call…");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ChatProviderException($"{options.Model} did not answer in time (no output for {(int)options.FirstTokenTimeout.TotalMinutes} min / " +
                                            $"{(int)options.IdleTimeout.TotalMinutes} min once started). The model may be too large for this computer, or the server is busy.");
        }
        catch (HttpRequestException e)
        {
            throw Unreachable(e);
        }
        catch (IOException e) when (!ct.IsCancellationRequested)
        {
            throw new ChatProviderException($"The connection to the local model server{Where} was interrupted.", e);
        }
    }

    private static long Long(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.TryGetInt64(out var l) ? l : 0;

    private ChatProviderException Unreachable(HttpRequestException e)
    {
        var refused = e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused } || e.HttpRequestError == HttpRequestError.ConnectionError;
        var hint = options.LooksLikeOllama ? " Start Ollama (open the app or run `ollama serve`)."
                 : options.BaseUrl.Port == 1234 ? " Start LM Studio's local server (Developer ▸ Start server)."
                 : " Check that the server is running and the base URL is right.";
        return new ChatProviderException((refused ? $"Could not connect to the local model server{Where}." : $"Could not reach the local model server{Where}.") + hint, e);
    }

    private async Task<Exception> ErrorFor(HttpResponseMessage res, bool withTools, CancellationToken ct)
    {
        var raw = "";
        try { raw = await res.Content.ReadAsStringAsync(ct); } catch (Exception e) when (e is IOException or HttpRequestException) { }
        var message = raw;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var err)) message = ErrorText(err);
            else if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString() ?? raw;
        }
        catch (JsonException) { }
        var lower = message.ToLowerInvariant();
        var status = (int)res.StatusCode;

        if (withTools && status is 400 or 422 or 500 && lower.Contains("tool", StringComparison.Ordinal) &&
            (lower.Contains("support", StringComparison.Ordinal) || lower.Contains("jinja", StringComparison.Ordinal) || lower.Contains("not allowed", StringComparison.Ordinal)))
            return new ToolsUnsupportedException();
        if (status == 404 && (lower.Contains("model", StringComparison.Ordinal) || lower.Contains("pull", StringComparison.Ordinal)))
            return new ChatProviderException(ModelMissing());
        if (status is 400 or 422 && lower.Contains("model", StringComparison.Ordinal) && (lower.Contains("not found", StringComparison.Ordinal) || lower.Contains("does not exist", StringComparison.Ordinal) || lower.Contains("no model", StringComparison.Ordinal)))
            return new ChatProviderException(ModelMissing());
        return status switch
        {
            401 or 403 => new ChatProviderException($"The local model server rejected the request ({status}). Check the API key in the assistant settings."),
            404 => new ChatProviderException($"The server{Where} has no /chat/completions endpoint (404). Check the base URL: it usually ends in /v1."),
            413 => new ChatProviderException("The request is too large for the local model server (413). Start a new chat or select a smaller package."),
            429 => new ChatProviderException("The local model server is busy (429). Wait a moment and try again."),
            >= 500 => new ChatProviderException($"The local model server failed ({status}): " + Short(message)),
            _ => new ChatProviderException($"The local model server rejected the request ({status}): " + Short(message)),
        };
    }

    private string ModelMissing() => options.LooksLikeOllama
        ? $"Model '{options.Model}' isn't installed — run `ollama pull {options.Model}`."
        : $"Model '{options.Model}' isn't available on the local model server. Load or download it there, or pick another model in Settings ▸ AI assistant.";

    private static string ErrorText(JsonElement err) => err.ValueKind switch
    {
        JsonValueKind.String => err.GetString() ?? "",
        JsonValueKind.Object when err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String => m.GetString() ?? "",
        _ => err.GetRawText(),
    };

    private static string Short(string s) => s.Length <= 300 ? s : s[..300] + "…";

    public string? DescribeError(Exception e) => e switch
    {
        ChatProviderException => e.Message,
        HttpRequestException h => Unreachable(h).Message,
        _ => null,
    };

    // ================================================================ discovery

    /// <summary>Lists the server's models: OpenAI <c>/v1/models</c>, plus Ollama's <c>/api/tags</c> (which also tells it is Ollama).</summary>
    public async Task<LocalModelList> ListModelsAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var models = new Dictionary<string, LocalModelInfo>(StringComparer.Ordinal);
        string? error = null;
        var isOllama = false;
        try
        {
            using (var res = await GetAsync(Endpoint("models"), timeout.Token))
            {
                if (res.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(timeout.Token));
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        foreach (var m in data.EnumerateArray())
                            if (m.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s) models[s] = new LocalModelInfo(s);
                }
                else error = res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? $"The server rejected the request ({(int)res.StatusCode}). Check the API key."
                    : $"The server answered {(int)res.StatusCode} for /models. Check the base URL (it usually ends in /v1).";
            }
            try
            {
                using var tags = await GetAsync(Root("api/tags"), timeout.Token);
                if (tags.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await tags.Content.ReadAsStringAsync(timeout.Token));
                    if (doc.RootElement.TryGetProperty("models", out var list) && list.ValueKind == JsonValueKind.Array)
                    {
                        isOllama = true;
                        error = null;
                        foreach (var m in list.EnumerateArray())
                        {
                            var name = m.TryGetProperty("name", out var n) ? n.GetString() : m.TryGetProperty("model", out var mm) ? mm.GetString() : null;
                            if (string.IsNullOrEmpty(name)) continue;
                            models[name] = new LocalModelInfo(name, m.TryGetProperty("size", out var size) && size.TryGetInt64(out var sz) ? sz : null);
                        }
                    }
                }
            }
            catch (JsonException) { }   // not Ollama
        }
        catch (HttpRequestException e) { error = Unreachable(e).Message; }
        catch (JsonException) { error ??= "The server's model list was not valid JSON. Is this an OpenAI-compatible server?"; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { error = $"The local model server{Where} did not answer within 15 seconds."; }
        catch (ChatProviderException e) { error = e.Message; }
        return new LocalModelList(models.Values.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList(), isOllama, error);
    }

    /// <summary>
    /// Whether the model supports native tool calling: Ollama reports it in <c>/api/show</c> capabilities; for other servers
    /// it is unknown (null) and Auto mode finds out on the first request.
    /// </summary>
    public async Task<bool?> DetectToolSupportAsync(CancellationToken ct = default)
    {
        if (_cache.Get(options.BaseUrl, options.Model) is { } known) return known;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Root("api/show"))
            {
                Content = new StringContent(new JsonObject { ["model"] = options.Model }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (options.ApiKey is { Length: > 0 } key) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var res = await http.SendAsync(req, timeout.Token);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(timeout.Token));
            if (!doc.RootElement.TryGetProperty("capabilities", out var caps) || caps.ValueKind != JsonValueKind.Array) return null;
            var tools = caps.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "tools");
            _cache.Set(options.BaseUrl, options.Model, tools);
            return tools;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException or ChatProviderException) { return null; }
    }

    /// <summary>"Test connection": reaches the server, lists its models, checks the configured model is there and probes tool support.</summary>
    public async Task<LocalConnectionTest> TestConnectionAsync(CancellationToken ct = default)
    {
        var list = await ListModelsAsync(ct);
        if (list.Error != null) return new LocalConnectionTest(false, list.Error, list.Models, null);
        var server = list.IsOllama ? "Ollama" : "the server";
        if (list.Models.Count == 0)
            return new LocalConnectionTest(false, list.IsOllama ? "Connected to Ollama, but no models are installed. Run `ollama pull qwen2.5:14b` (for example)." : "Connected, but the server lists no models. Load a model first.", list.Models, null);
        var present = list.Models.Any(m => string.Equals(m.Id, options.Model, StringComparison.OrdinalIgnoreCase) ||
                                           (list.IsOllama && !options.Model.Contains(':') && string.Equals(m.Id, options.Model + ":latest", StringComparison.OrdinalIgnoreCase)));
        if (string.IsNullOrWhiteSpace(options.Model))
            return new LocalConnectionTest(true, $"Connected to {server}: {list.Models.Count} model(s). Pick one.", list.Models, null);
        if (!present)
            return new LocalConnectionTest(false, list.IsOllama ? $"Connected, but model '{options.Model}' isn't installed — run `ollama pull {options.Model}`."
                                                                : $"Connected, but {server} does not list model '{options.Model}'.", list.Models, null);
        var tools = list.IsOllama ? await DetectToolSupportAsync(ct) : null;
        var toolText = tools switch { true => " It supports tool calling.", false => " It has no native tool calling; Meridian will use text tool calls.", _ => "" };
        return new LocalConnectionTest(true, $"Connected to {server}; model '{options.Model}' is available.{toolText}", list.Models, tools);
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        if (options.ApiKey is { Length: > 0 } key) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return await http.SendAsync(req, ct);
    }
}
