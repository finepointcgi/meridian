using System.Text.Json;
using System.Text.Json.Nodes;
using Meridian.Assistant.Engine;
using Meridian.Assistant.Tools;

namespace Meridian.Assistant.Providers;

/// <summary>Which kind of model service answers.</summary>
public enum ChatProviderKind { Anthropic, OpenAiCompatible, ClaudeCode }

public enum ChatRole { User, Assistant }

/// <summary>One piece of a provider-neutral message.</summary>
public abstract record ChatPart;

public sealed record TextPart(string Text) : ChatPart;

/// <summary>A tool call the model made. <see cref="Input"/> is null when the arguments were not a JSON object.</summary>
public sealed record ToolCallPart(string Id, string Name, JsonElement? Input, string RawArguments) : ChatPart;

public sealed record ToolResultPart(string ToolCallId, string Name, string Content, bool IsError) : ChatPart;

/// <summary>
/// A provider-neutral message of the conversation history. <see cref="Native"/> optionally carries the provider's own
/// representation (for Claude: the exact content blocks, thinking signatures included), which that provider replays
/// unchanged; any other provider rebuilds the message from <see cref="Parts"/>.
/// </summary>
public sealed class ChatMessage(ChatRole role, IReadOnlyList<ChatPart> parts, object? native = null)
{
    public ChatRole Role { get; } = role;
    public IReadOnlyList<ChatPart> Parts { get; } = parts;
    public object? Native { get; } = native;

    public IEnumerable<ToolCallPart> ToolCalls => Parts.OfType<ToolCallPart>();
    public IEnumerable<ToolResultPart> ToolResults => Parts.OfType<ToolResultPart>();
    public string Text => string.Concat(Parts.OfType<TextPart>().Select(p => p.Text));
}

/// <summary>A tool offered to the model: its name, description and JSON schema (the strict subset of <see cref="ToolSchema"/>).</summary>
public sealed record ChatTool(string Name, string Description, JsonObject Schema)
{
    public static ChatTool From(AssistantTool t) => new(t.Name, t.Description, t.Schema);

    /// <summary>The assistant's tools in their fixed order.</summary>
    public static IReadOnlyList<ChatTool> Toolbox { get; } = Tools.Toolbox.All.Select(From).ToList();
}

/// <summary>One model call: system prompt, tools, history so far and how hard to think.</summary>
public sealed record ChatRequest(string System, IReadOnlyList<ChatTool> Tools, IReadOnlyList<ChatMessage> Messages, AssistantEffort Effort)
{
    /// <summary>
    /// Runs a tool on the caller's context, for providers that drive the tool loop themselves (<see cref="IChatProvider.RunsToolLoop"/>):
    /// the input is validated against the tool's schema and write tools only add proposals to the turn's change set.
    /// </summary>
    public Func<ToolCallPart, ToolResult>? ExecuteTool { get; init; }
}

public enum ChatStopReason { EndTurn, ToolUse, MaxTokens, Refusal, PauseTurn, Other }

public sealed record ChatUsage(long InputTokens, long OutputTokens, long CacheReadTokens)
{
    public static readonly ChatUsage None = new(0, 0, 0);
}

/// <summary>The outcome of one model call.</summary>
public sealed class ChatResponse
{
    public required ChatStopReason Stop { get; init; }

    /// <summary>The assistant message to append to the history (null when the model returned no content).</summary>
    public ChatMessage? Message { get; init; }

    public ChatUsage Usage { get; init; } = ChatUsage.None;

    /// <summary>The provider's explanation of a refusal, if any.</summary>
    public string? RefusalExplanation { get; init; }

    /// <summary>Something the user should know about this call (for example a local model's tool call that stayed invalid).</summary>
    public string? Warning { get; init; }

    public IReadOnlyList<ToolCallPart> ToolCalls => Message?.ToolCalls.ToList() ?? [];
}

/// <summary>Where a provider streams what the user sees while a call runs.</summary>
public interface IChatStream
{
    /// <summary>More answer text (Markdown).</summary>
    void Text(string delta);

    /// <summary>The current text block ended (the next text starts a new transcript entry).</summary>
    void EndText();

    /// <summary>A short progress line ("Loading the model…"), or null to clear it.</summary>
    void Status(string? status);
}

/// <summary>Display facts about the active provider (the panel header, notices and the privacy text).</summary>
public sealed record ChatProviderInfo(ChatProviderKind Kind, string Model, string? Endpoint = null)
{
    /// <summary>How the model is called in notices: "Claude" or "The local model".</summary>
    public string AssistantName => Kind == ChatProviderKind.OpenAiCompatible ? "The local model" : "Claude";

    public string Label => Kind switch
    {
        ChatProviderKind.Anthropic => "Claude (Anthropic)",
        ChatProviderKind.ClaudeCode => "Claude Code (your login)",
        _ => "Local model",
    };
}

/// <summary>
/// A chat model service with streaming and tool calls. <see cref="AnthropicChatProvider"/> calls Claude through the
/// official SDK; <see cref="OpenAiCompatibleProvider"/> calls a local or self-hosted OpenAI-compatible server.
/// </summary>
public interface IChatProvider
{
    ChatProviderInfo Info { get; }

    /// <summary>
    /// True when one <see cref="CompleteAsync"/> runs the whole tool loop (Claude Code calls Meridian's tools over MCP,
    /// through <see cref="ChatRequest.ExecuteTool"/>) and returns only the final answer.
    /// </summary>
    bool RunsToolLoop => false;

    /// <summary>Runs one model call, streaming text into <paramref name="stream"/>. Throws on transport or API errors.</summary>
    Task<ChatResponse> CompleteAsync(ChatRequest request, IChatStream stream, CancellationToken ct);

    /// <summary>User-facing text for an error thrown by <see cref="CompleteAsync"/> (never includes keys or prompts); null if unknown.</summary>
    string? DescribeError(Exception e);
}

/// <summary>An error with a message that is safe and useful to show to the user.</summary>
public sealed class ChatProviderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A stream that discards everything (non-interactive calls such as the narrative writer).</summary>
public sealed class NullChatStream : IChatStream
{
    public static readonly NullChatStream Instance = new();
    public void Text(string delta) { }
    public void EndText() { }
    public void Status(string? status) { }
}
