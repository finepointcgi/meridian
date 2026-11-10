using System.Text.Json;
using Anthropic;
using Meridian.Assistant.Providers;
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

/// <summary>One user turn: what is sent, how it is shown, and how hard the model should think.</summary>
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
/// The message loop: streams the model's answer (text deltas go straight to the transcript), runs the client-side tools it
/// asks for and feeds the results back until the turn ends. Write tools only add proposals to the turn's change set,
/// which is attached to the transcript as a reviewable card when the turn completes. Prompts and keys are never logged.
/// The model is reached through an <see cref="IChatProvider"/>: Claude (<see cref="AnthropicChatProvider"/>) or a local
/// OpenAI-compatible server (<see cref="OpenAiCompatibleProvider"/>).
/// </summary>
/// <remarks>Tools touch the model, so call <see cref="RunTurnAsync"/> on the thread that owns it (a Blazor circuit's
/// synchronisation context): awaits resume there, so tools run there too.</remarks>
public sealed class AssistantEngine
{
    private readonly IChatProvider _provider;
    private readonly AssistantOptions _options;

    /// <summary>Claude through the Anthropic SDK.</summary>
    public AssistantEngine(AnthropicClient client, AssistantOptions? options = null)
        : this(new AnthropicChatProvider(client, options), options) { }

    public AssistantEngine(IChatProvider provider, AssistantOptions? options = null)
    {
        _provider = provider;
        _options = options ?? new AssistantOptions();
    }

    public IChatProvider Provider => _provider;

    /// <summary>Streams into the transcript: one <see cref="AssistantTextItem"/> per text block, progress in <see cref="Conversation.Status"/>.</summary>
    private sealed class TranscriptStream(Conversation conv, Action onUpdate) : IChatStream
    {
        private AssistantTextItem? _current;

        public void Text(string delta)
        {
            if (delta.Length == 0) return;
            if (_current == null)
            {
                _current = new AssistantTextItem();
                conv.Items.Add(_current);
            }
            conv.Status = null;
            _current.Append(delta);
            onUpdate();
        }

        public void EndText()
        {
            if (_current != null) _current.Streaming = false;
            _current = null;
        }

        public void Status(string? status)
        {
            conv.Status = status;
            onUpdate();
        }
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
        conv.History.Add(new ChatMessage(ChatRole.User, [new TextPart(context), new TextPart(request.Prompt)]));
        onUpdate();

        var completed = false;
        var drop = false;
        var stream = new TranscriptStream(conv, onUpdate);
        try
        {
            for (var round = 0; ; round++)
            {
                if (round >= _options.MaxToolRounds)
                {
                    conv.Items.Add(new NoticeItem(NoticeKind.Warning, $"Stopped after {_options.MaxToolRounds} tool rounds. Ask again to continue."));
                    break;
                }

                var modelCall = new ChatRequest(Prompts.System, ChatTool.Toolbox, conv.History, request.Effort)
                {
                    // Providers that run their own tool loop (Claude Code over MCP) call back here, so tools still run on
                    // this context and write tools still only add to this turn's change set.
                    ExecuteTool = _provider.RunsToolLoop ? use => { var r = RunTool(conv, ctx, use); onUpdate(); return r; } : null,
                };
                var response = await _provider.CompleteAsync(modelCall, stream, ct);
                stream.EndText();
                conv.Status = null;
                conv.InputTokens += response.Usage.InputTokens;
                conv.OutputTokens += response.Usage.OutputTokens;
                conv.CacheReadTokens += response.Usage.CacheReadTokens;
                if (response.Warning != null) conv.Items.Add(new NoticeItem(NoticeKind.Warning, response.Warning));

                if (response.Stop == ChatStopReason.Refusal)
                {
                    // A refusal can cut a tool call off mid-input: never run that turn's tools.
                    var why = response.RefusalExplanation;
                    conv.Items.Add(new NoticeItem(NoticeKind.Refusal,
                        $"{_provider.Info.AssistantName} declined to continue with this request." + (string.IsNullOrWhiteSpace(why) ? "" : " " + why)));
                    drop = true;   // drop anything proposed in this turn
                    ctx.Findings.Clear();
                    break;
                }

                var toolCalls = response.ToolCalls;
                if (response.Message != null) conv.History.Add(response.Message);

                if (response.Stop == ChatStopReason.MaxTokens)
                {
                    conv.Items.Add(new NoticeItem(NoticeKind.Warning, toolCalls.Count > 0
                        ? "The answer hit the output limit in the middle of a tool call, so it was not run. Try a narrower request."
                        : "The answer hit the output limit and was cut off."));
                    completed = toolCalls.Count == 0;
                    break;
                }

                if (response.Stop == ChatStopReason.PauseTurn) continue;   // resume where the server left off

                if (toolCalls.Count == 0 || response.Stop != ChatStopReason.ToolUse)
                {
                    completed = true;
                    break;
                }

                var results = new List<ChatPart>();
                foreach (var call in toolCalls)
                {
                    var result = RunTool(conv, ctx, call);
                    results.Add(new ToolResultPart(call.Id, call.Name, result.Content, result.IsError));
                    onUpdate();
                }
                conv.History.Add(new ChatMessage(ChatRole.User, results));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            conv.Items.Add(new NoticeItem(NoticeKind.Info, "Stopped."));
            drop = true;
            ctx.Findings.Clear();
        }
        catch (Exception e) when ((e is ChatProviderException ? e.Message : _provider.DescribeError(e)) is { } text)
        {
            conv.Items.Add(new NoticeItem(NoticeKind.Error, text));
        }
        finally
        {
            conv.Status = null;
            foreach (var t in conv.Items.OfType<AssistantTextItem>()) t.Streaming = false;
            foreach (var t in conv.Items.OfType<ToolActivityItem>()) t.Done = true;
            // Only complete turns stay in the history, so the next request is always well-formed.
            if (!completed && conv.History.Count > mark) conv.History.RemoveRange(mark, conv.History.Count - mark);
            if (ctx.Findings.Count > 0) conv.Items.Add(new FindingsItem(ctx.Findings.ToList()));
            if (!drop && !changes.IsEmpty) conv.Items.Add(new ChangeSetItem(changes));
            conv.Busy = false;
            onUpdate();
        }
    }

    private static ToolResult RunTool(Conversation conv, ToolContext ctx, ToolCallPart call)
    {
        var tool = Toolbox.Find(call.Name);
        var input = call.Input ?? default;
        var activity = new ToolActivityItem(call.Name, tool != null && input.ValueKind == JsonValueKind.Object ? SafeDescribe(tool, input) : call.Name);
        conv.Items.Add(activity);
        ToolResult result;
        if (tool == null) result = ToolResult.Error($"Unknown tool '{call.Name}'.");
        else if (input.ValueKind != JsonValueKind.Object)
            result = ToolResult.Error(JsonSerializer.Serialize(new Dictionary<string, string> { ["INVALID_JSON"] = "The tool input was not a JSON object." }));
        else result = tool.Run(ctx, input);   // validates the input against the tool's schema first
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

    /// <summary>User-facing text for Anthropic API failures. Never includes the key or the prompt.</summary>
    internal static string? Describe(Exception e) => AnthropicChatProvider.Describe(e);
}
