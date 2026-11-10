using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Helpers;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Meridian.Assistant.Tools;
using Meridian.Core.Model;

namespace Meridian.Assistant.Engine;

public enum AssistantEffort { Medium, High }

/// <summary>Model and request settings. The defaults are what Meridian ships with; the model is configurable.</summary>
public sealed record AssistantOptions
{
    public const string DefaultModel = "claude-opus-5-5";

    public string Model { get; init; } = DefaultModel;
    public int MaxTokens { get; init; } = 16000;

    /// <summary>Upper bound on model ↔ tool round trips in one turn.</summary>
    public int MaxToolRounds { get; init; } = 24;

    /// <summary>Server-side refusal fallbacks (beta <c>server-side-fallback-2026-07-01</c>, <c>fallbacks: "default"</c>).</summary>
    public bool ServerSideFallbacks { get; init; } = true;
}

/// <summary>One user turn: what is sent, how it is shown, and how hard Claude should think.</summary>
public sealed record TurnRequest(string Prompt, string Context, AssistantEffort Effort = AssistantEffort.Medium, string? Label = null);

/// <summary>Creates SDK clients; tests substitute an <see cref="HttpClient"/> with a fake handler (no real API calls).</summary>
public interface IAssistantClientFactory
{
    AnthropicClient Create(string apiKey);
}

public sealed class AssistantClientFactory(HttpClient? http = null, int maxRetries = 2) : IAssistantClientFactory
{
    // One HttpClient for the process; the SDK applies its own per-request timeout, so this one must not cut streams short.
    private static readonly HttpClient Shared = new() { Timeout = Timeout.InfiniteTimeSpan };

    public AnthropicClient Create(string apiKey) => new()
    {
        ApiKey = apiKey,
        HttpClient = http ?? Shared,
        MaxRetries = maxRetries,
        Timeout = TimeSpan.FromMinutes(10),
        OpenTelemetry = false,   // never export prompt/response content as telemetry
    };
}

/// <summary>
/// The message loop: streams Claude's answer (text deltas go straight to the transcript), runs the client-side tools it
/// asks for and feeds the results back until the turn ends. Write tools only add proposals to the turn's change set,
/// which is attached to the transcript as a reviewable card when the turn completes. Prompts and keys are never logged.
/// </summary>
/// <remarks>Tools touch the model, so call <see cref="RunTurnAsync"/> on the thread that owns it (a Blazor circuit's
/// synchronisation context): awaits resume there, so tools run there too.</remarks>
public sealed class AssistantEngine(AnthropicClient client, AssistantOptions? options = null)
{
    private readonly AssistantOptions _options = options ?? new AssistantOptions();

    private static readonly IReadOnlyList<BetaToolUnion> ToolDefinitions = BuildTools();

    private static List<BetaToolUnion> BuildTools()
    {
        var list = new List<BetaToolUnion>();
        for (var i = 0; i < Toolbox.All.Count; i++)
        {
            var t = Toolbox.All[i];
            var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(t.Schema.ToJsonString())!;
            list.Add(new BetaTool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = InputSchema.FromRawUnchecked(schema),
                Strict = true,                // schema-valid arguments (forced tool_choice is not available on this model)
                EagerInputStreaming = true,   // streamed requests; inputs are re-validated before any tool runs
                // The tool list is identical in every request: one breakpoint after the last tool caches system + tools.
                CacheControl = i == Toolbox.All.Count - 1 ? new BetaCacheControlEphemeral() : null,
            });
        }
        return list;
    }

    internal MessageCreateParams BuildParams(IReadOnlyList<BetaMessageParam> messages, AssistantEffort effort)
    {
        var p = new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = _options.MaxTokens,
            System = new List<BetaTextBlockParam>
            {
                new() { Text = Prompts.System, CacheControl = new BetaCacheControlEphemeral() },
            },
            Tools = ToolDefinitions.ToList(),
            ToolChoice = new BetaToolChoiceAuto(),
            OutputConfig = new BetaOutputConfig { Effort = effort == AssistantEffort.High ? Effort.High : Effort.Medium },
            Messages = messages.ToList(),
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

    /// <summary>Runs one turn. Never throws for API or tool failures: they become notices in the transcript.</summary>
    public async Task RunTurnAsync(Conversation conv, ModelRepository model, TurnRequest request, Action onUpdate, CancellationToken ct = default)
    {
        if (conv.Busy) throw new InvalidOperationException("The assistant is already answering.");
        conv.Busy = true;
        var mark = conv.History.Count;
        var changes = conv.NewChangeSet();
        var ctx = new ToolContext(model, changes, conv.NextRef);
        conv.Items.Add(new UserItem(request.Prompt, request.Label));

        var context = request.Context;
        if (conv.PendingNotes.Count > 0)
        {
            context += "\n<notes>\n" + string.Join("\n", conv.PendingNotes) + "\n</notes>";
            conv.PendingNotes.Clear();
        }
        conv.History.Add(new BetaUserMessageParam { Content = new List<BetaContentBlockParam>
            {
                new BetaTextBlockParam { Text = context },
                new BetaTextBlockParam { Text = request.Prompt },
            },
        });
        onUpdate();

        var completed = false;
        var drop = false;
        try
        {
            for (var round = 0; ; round++)
            {
                if (round >= _options.MaxToolRounds)
                {
                    conv.Items.Add(new NoticeItem(NoticeKind.Warning, $"Stopped after {_options.MaxToolRounds} tool rounds. Ask again to continue."));
                    break;
                }

                var message = await StreamOnce(conv, request.Effort, onUpdate, ct);
                conv.InputTokens += message.Usage.InputTokens;
                conv.OutputTokens += message.Usage.OutputTokens;
                conv.CacheReadTokens += message.Usage.CacheReadInputTokens ?? 0;

                var stop = StopReasonOf(message);
                if (stop == "refusal")
                {
                    // A refusal can cut a tool call off mid-input: never run that turn's tools.
                    var why = message.StopDetails?.Explanation;
                    conv.Items.Add(new NoticeItem(NoticeKind.Refusal,
                        "Claude declined to continue with this request." + (string.IsNullOrWhiteSpace(why) ? "" : " " + why)));
                    drop = true;   // drop anything proposed in this turn
                    ctx.Findings.Clear();
                    break;
                }

                var (assistantBlocks, toolUses) = ToParams(message);
                if (assistantBlocks.Count > 0) conv.History.Add(new BetaAssistantMessageParam { Content = assistantBlocks });

                if (stop == "max_tokens")
                {
                    conv.Items.Add(new NoticeItem(NoticeKind.Warning, toolUses.Count > 0
                        ? "The answer hit the output limit in the middle of a tool call, so it was not run. Try a narrower request."
                        : "The answer hit the output limit and was cut off."));
                    completed = toolUses.Count == 0;
                    break;
                }

                if (stop == "pause_turn") continue;   // resume where the server left off

                if (toolUses.Count == 0 || stop != "tool_use")
                {
                    completed = true;
                    break;
                }

                var results = new List<BetaContentBlockParam>();
                foreach (var use in toolUses)
                {
                    var result = RunTool(conv, ctx, use);
                    results.Add(new BetaToolResultBlockParam { ToolUseID = use.ID, Content = result.Content, IsError = result.IsError });
                    onUpdate();
                }
                conv.History.Add(new BetaUserMessageParam { Content = results });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            conv.Items.Add(new NoticeItem(NoticeKind.Info, "Stopped."));
            drop = true;
            ctx.Findings.Clear();
        }
        catch (Exception e) when (Describe(e) is { } text)
        {
            conv.Items.Add(new NoticeItem(NoticeKind.Error, text));
        }
        finally
        {
            foreach (var t in conv.Items.OfType<AssistantTextItem>()) t.Streaming = false;
            foreach (var t in conv.Items.OfType<ToolActivityItem>()) t.Done = true;
            // Only complete turns stay in the API history, so the next request is always well-formed.
            if (!completed && conv.History.Count > mark) conv.History.RemoveRange(mark, conv.History.Count - mark);
            if (ctx.Findings.Count > 0) conv.Items.Add(new FindingsItem(ctx.Findings.ToList()));
            if (!drop && !changes.IsEmpty) conv.Items.Add(new ChangeSetItem(changes));
            conv.Busy = false;
            onUpdate();
        }
    }

    private async Task<BetaMessage> StreamOnce(Conversation conv, AssistantEffort effort, Action onUpdate, CancellationToken ct)
    {
        // Read the stream here (on the caller's context, so the transcript is only touched there), then let the SDK's
        // aggregator build the final message from the recorded events.
        var events = new List<BetaRawMessageStreamEvent>();
        AssistantTextItem? current = null;
        await foreach (var ev in client.Beta.Messages.CreateStreaming(BuildParams(conv.History, effort), ct).WithCancellation(ct))
        {
            events.Add(ev);
            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
            {
                if (current == null)
                {
                    current = new AssistantTextItem();
                    conv.Items.Add(current);
                }
                current.Append(text.Text);
                onUpdate();
            }
            else if (ev.TryPickContentBlockStop(out _))
            {
                if (current != null) current.Streaming = false;
                current = null;
            }
        }
        return await Replay(events).Aggregate();
    }

    private static async IAsyncEnumerable<BetaRawMessageStreamEvent> Replay(List<BetaRawMessageStreamEvent> events)
    {
        foreach (var e in events) yield return e;
        await Task.CompletedTask;
    }

    private static string? StopReasonOf(BetaMessage m) => m.StopReason?.Raw();

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

    private static ToolResult RunTool(Conversation conv, ToolContext ctx, BetaToolUseBlock use)
    {
        var tool = Toolbox.Find(use.Name);
        JsonElement input;
        try { input = JsonSerializer.SerializeToElement(use.Input); }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            input = default;
        }
        var activity = new ToolActivityItem(use.Name, tool != null && input.ValueKind == JsonValueKind.Object ? SafeDescribe(tool, input) : use.Name);
        conv.Items.Add(activity);
        ToolResult result;
        if (tool == null) result = ToolResult.Error($"Unknown tool '{use.Name}'.");
        else if (input.ValueKind != JsonValueKind.Object)
            result = ToolResult.Error(JsonSerializer.Serialize(new Dictionary<string, string> { ["INVALID_JSON"] = "The tool input was not a JSON object." }));
        else result = tool.Run(ctx, input);
        activity.IsError = result.IsError;
        activity.Error = result.IsError ? ToolContext.Truncate(result.Content, 300) : null;
        activity.Done = true;
        return result;
    }

    private static string SafeDescribe(AssistantTool tool, JsonElement input)
    {
        try { return tool.Describe(input); }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException) { return tool.Name; }
    }

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
