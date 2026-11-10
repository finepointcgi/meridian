using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Helpers;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Meridian.Assistant.Engine;

namespace Meridian.Assistant.Providers;

/// <summary>
/// Claude through the official Anthropic C# SDK (Messages API, beta surface, streaming). This is the original assistant
/// request path, unchanged: strict tools with one cache breakpoint after the last tool, a cached system prompt, automatic
/// caching of the conversation, adaptive thinking with the turn's effort, and optional server-side refusal fallbacks.
/// </summary>
public sealed class AnthropicChatProvider(AnthropicClient client, AssistantOptions? options = null) : IChatProvider
{
    private readonly AssistantOptions _options = options ?? new AssistantOptions();
    private readonly ConditionalWeakTable<IReadOnlyList<ChatTool>, List<BetaToolUnion>> _toolCache = new();

    public AnthropicClient Client => client;
    public AssistantOptions Options => _options;
    public ChatProviderInfo Info => new(ChatProviderKind.Anthropic, _options.Model);

    private List<BetaToolUnion> ToolsFor(IReadOnlyList<ChatTool> tools) => _toolCache.GetValue(tools, BuildTools);

    internal static List<BetaToolUnion> BuildTools(IReadOnlyList<ChatTool> tools)
    {
        var list = new List<BetaToolUnion>();
        for (var i = 0; i < tools.Count; i++)
        {
            var t = tools[i];
            var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(t.Schema.ToJsonString())!;
            list.Add(new BetaTool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = InputSchema.FromRawUnchecked(schema),
                Strict = true,                // schema-valid arguments (forced tool_choice is not available on this model)
                EagerInputStreaming = true,   // streamed requests; inputs are re-validated before any tool runs
                // The tool list is identical in every request: one breakpoint after the last tool caches system + tools.
                CacheControl = i == tools.Count - 1 ? new BetaCacheControlEphemeral() : null,
            });
        }
        return list;
    }

    internal MessageCreateParams BuildParams(ChatRequest request)
    {
        var p = new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = _options.MaxTokens,
            System = new List<BetaTextBlockParam>
            {
                new() { Text = request.System, CacheControl = new BetaCacheControlEphemeral() },
            },
            Tools = ToolsFor(request.Tools).ToList(),
            ToolChoice = new BetaToolChoiceAuto(),
            OutputConfig = new BetaOutputConfig { Effort = request.Effort == AssistantEffort.High ? Effort.High : Effort.Medium },
            Messages = request.Messages.Select(ToNative).ToList(),
            // Automatic caching of the growing conversation, so each tool round re-reads the previous rounds from cache.
            CacheControl = new BetaCacheControlEphemeral(),
        };
        if (_options.ServerSideFallbacks)
            p = p with
            {
                Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
                Fallbacks = new BetaFallbacksParam(new Default()),
            };
        return p;
    }

    /// <summary>The SDK message for a history entry: replayed as-is when Claude produced it, otherwise rebuilt from its parts.</summary>
    internal static BetaMessageParam ToNative(ChatMessage m)
    {
        if (m.Native is BetaMessageParam native) return native;
        var blocks = new List<BetaContentBlockParam>();
        foreach (var part in m.Parts)
        {
            switch (part)
            {
                case TextPart t when t.Text.Length > 0:
                    blocks.Add(new BetaTextBlockParam { Text = t.Text });
                    break;
                case ToolCallPart c:
                    var input = c.Input is { ValueKind: JsonValueKind.Object } o
                        ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(o.GetRawText())!
                        : new Dictionary<string, JsonElement>();
                    blocks.Add(new BetaToolUseBlockParam { ID = SafeId(c.Id), Name = c.Name, Input = input });
                    break;
                case ToolResultPart r:
                    blocks.Add(new BetaToolResultBlockParam { ToolUseID = SafeId(r.ToolCallId), Content = r.Content, IsError = r.IsError });
                    break;
            }
        }
        return m.Role == ChatRole.User ? new BetaUserMessageParam { Content = blocks } : new BetaAssistantMessageParam { Content = blocks };
    }

    /// <summary>Tool ids from another provider may contain characters Claude does not accept.</summary>
    private static string SafeId(string id)
    {
        var clean = new string(id.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').ToArray());
        return clean.Length > 0 ? clean : "call";
    }

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, IChatStream stream, CancellationToken ct)
    {
        // Read the stream here (on the caller's context, so the transcript is only touched there), then let the SDK's
        // aggregator build the final message from the recorded events.
        var events = new List<BetaRawMessageStreamEvent>();
        var inText = false;
        await foreach (var ev in client.Beta.Messages.CreateStreaming(BuildParams(request), ct).WithCancellation(ct))
        {
            events.Add(ev);
            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
            {
                inText = true;
                stream.Text(text.Text);
            }
            else if (ev.TryPickContentBlockStop(out _))
            {
                if (inText) stream.EndText();
                inText = false;
            }
        }
        var message = await Replay(events).Aggregate();

        var (blocks, uses) = ToParams(message);
        var parts = new List<ChatPart>();
        foreach (var b in message.Content)
        {
            if (b.TryPickText(out var t)) { if (t.Text.Length > 0) parts.Add(new TextPart(t.Text)); }
            else if (b.TryPickToolUse(out var tu)) parts.Add(ToPart(tu));
        }
        return new ChatResponse
        {
            Stop = message.StopReason?.Raw() switch
            {
                "end_turn" or "stop_sequence" => ChatStopReason.EndTurn,
                "tool_use" => ChatStopReason.ToolUse,
                "max_tokens" => ChatStopReason.MaxTokens,
                "refusal" => ChatStopReason.Refusal,
                "pause_turn" => ChatStopReason.PauseTurn,
                _ => ChatStopReason.Other,
            },
            Message = blocks.Count > 0 ? new ChatMessage(ChatRole.Assistant, parts, new BetaAssistantMessageParam { Content = blocks }) : null,
            Usage = new ChatUsage(message.Usage.InputTokens, message.Usage.OutputTokens, message.Usage.CacheReadInputTokens ?? 0),
            RefusalExplanation = message.StopDetails?.Explanation,
        };
    }

    private static ToolCallPart ToPart(BetaToolUseBlock use)
    {
        JsonElement? input;
        try { input = JsonSerializer.SerializeToElement(use.Input); }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException) { input = null; }
        return new ToolCallPart(use.ID, use.Name, input, input?.GetRawText() ?? "");
    }

    private static async IAsyncEnumerable<BetaRawMessageStreamEvent> Replay(List<BetaRawMessageStreamEvent> events)
    {
        foreach (var e in events) yield return e;
        await Task.CompletedTask;
    }

    /// <summary>Converts a response into the assistant message to append (there is no ToParam helper) and its tool calls.</summary>
    internal static (List<BetaContentBlockParam> Blocks, List<BetaToolUseBlock> ToolUses) ToParams(BetaMessage message)
    {
        var blocks = new List<BetaContentBlockParam>();
        var uses = new List<BetaToolUseBlock>();
        foreach (var b in message.Content)
        {
            if (b.TryPickText(out var t)) { if (t.Text.Length > 0) blocks.Add(new BetaTextBlockParam { Text = t.Text }); }
            else if (b.TryPickThinking(out var th)) blocks.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
            else if (b.TryPickRedactedThinking(out var rt)) blocks.Add(new BetaRedactedThinkingBlockParam { Data = rt.Data });
            else if (b.TryPickToolUse(out var tu))
            {
                blocks.Add(new BetaToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                uses.Add(tu);
            }
        }
        return (blocks, uses);
    }

    public string? DescribeError(Exception e) => Describe(e);

    /// <summary>User-facing text for API failures. Never includes the key or the prompt.</summary>
    internal static string? Describe(Exception e) => e switch
    {
        AnthropicUnauthorizedException => "The Anthropic API rejected the API key (401). Check the key in the assistant settings.",
        AnthropicForbiddenException => "This API key is not allowed to use the model (403).",
        AnthropicNotFoundException => "The configured model was not found or is not available to this API key (404).",
        AnthropicRateLimitException => "Rate limited by the Anthropic API (429). Wait a moment and try again.",
        AnthropicBadRequestException => "The Anthropic API rejected the request (400): " + Short(e.Message),
        Anthropic5xxException => "The Anthropic API is temporarily unavailable or overloaded. Try again shortly.",
        AnthropicIOException => "Could not reach the Anthropic API. Check the network connection.",
        AnthropicApiException => "The Anthropic API returned an error: " + Short(e.Message),
        AnthropicException => "The assistant failed: " + Short(e.Message),
        HttpRequestException => "Could not reach the Anthropic API. Check the network connection.",
        TimeoutException or TaskCanceledException => "The request to the Anthropic API timed out.",
        _ => null,
    };

    private static string Short(string s) => s.Length <= 400 ? s : s[..400] + "…";
}
