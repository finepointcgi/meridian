using System.Text;
using System.Text.Json;
using Meridian.Assistant.Providers;
using Meridian.Assistant.Tools;
using Meridian.Core.Derivation;
using static Meridian.Assistant.Tools.ToolSchema;

namespace Meridian.Assistant.Engine;

/// <summary>
/// The derivations' optional AI fill-in (<see cref="IDerivationAssistant"/>): the model writes what the rules can't — a use
/// case's steps and alternate flows from free text, or names for the messages of a derived sequence — and returns them
/// through the strict <c>submit_derivation</c> tool. Works with every provider (Claude, local models, Claude Code) through
/// <see cref="IChatProvider"/>. The answer is validated by <see cref="DerivationAi.Validate"/> and only ever feeds a plan
/// the user reviews; nothing is applied from here. Prompts are never logged.
/// </summary>
public sealed class DerivationWriter(IChatProvider provider) : IDerivationAssistant
{
    public const string ToolName = "submit_derivation";

    public string Name => provider.Info.AssistantName;

    internal static readonly ChatTool Tool = new(ToolName,
        "Submit the derivation fill-in: the use case steps (empty when not asked) and the message names (empty when not asked).",
        Object(
            Req("steps", Array(String("One step, e.g. \"Driver presses start\", \"If the brake is off, System shows a warning\" or \"Alternate: engine fails - System reports a fault\"."),
                "The use case's steps in order (empty when not asked).")),
            Req("messages", Array(Object(
                Req("key", String("The message key, exactly as given.")),
                Req("name", String("The message name in lowerCamelCase, e.g. requestStart."))), "One entry per message (empty when not asked)."))));

    private const string System = """
        You help Meridian, an MBSE tool, derive one SysML view from another. You fill in only what rules cannot infer.
        - For a use case: write its main success scenario as short steps, one action each, in order (at most 15). Start a step with
          an actor's name when that actor does it, otherwise the system does it. Write a conditional step as "If <condition>, <action>"
          and an alternate or exception flow as "Alternate: <condition> - <action>". Use only facts from the description and names given.
        - For messages of a sequence diagram: give each message a short lowerCamelCase operation name (verb first) that says what the
          receiver is asked to do, based on the action it hands over to.
        Never invent components that are not named. Call submit_derivation exactly once.
        """;

    public async Task<DerivationAiResult> FillInAsync(DerivationAiRequest request, CancellationToken ct = default)
    {
        var who = Name;
        var prompt = Prompt(request);
        JsonElement? submitted = null;
        ChatResponse response;
        try
        {
            var chat = new ChatRequest(System, [Tool], [new ChatMessage(ChatRole.User, [new TextPart(prompt)])], AssistantEffort.Medium)
            {
                ExecuteTool = provider.RunsToolLoop ? call =>
                {
                    if (call.Name != ToolName || call.Input is not { ValueKind: JsonValueKind.Object } i) return ToolResult.Error($"Call {ToolName}.");
                    var errors = Validate(Tool.Schema, i);
                    if (errors.Count > 0) return ToolResult.Error("Invalid input: " + string.Join("; ", errors.Take(8)));
                    submitted = i;
                    return ToolResult.Ok(new { received = true, note = "Done. Do not call any more tools." });
                } : null,
            };
            response = await provider.CompleteAsync(chat, NullChatStream.Instance, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return DerivationAiResult.Fail("Cancelled.");
        }
        catch (Exception e) when ((e is ChatProviderException ? e.Message : provider.DescribeError(e)) is { } text)
        {
            return DerivationAiResult.Fail(text);
        }
        if (response.Stop == ChatStopReason.Refusal) return DerivationAiResult.Fail($"{who} declined to fill this in.");
        if (response.Stop == ChatStopReason.MaxTokens) return DerivationAiResult.Fail("The answer hit the output limit and was not used.");
        if (submitted == null && response.ToolCalls.FirstOrDefault(c => c.Name == ToolName)?.Input is { ValueKind: JsonValueKind.Object } input)
        {
            if (Validate(Tool.Schema, input).Count > 0) return DerivationAiResult.Fail($"{who}'s answer did not have the expected shape. Try again.");
            submitted = input;
        }
        if (submitted is not { } result) return DerivationAiResult.Fail($"{who} did not answer. Try again.");
        var steps = result.Arr("steps").Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString() ?? "").ToList();
        var names = result.Arr("messages").Select(x => (Key: x.Str("key") ?? "", Name: x.Str("name") ?? ""))
                          .Where(x => x.Key.Length > 0 && x.Name.Length > 0).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Name);
        return new DerivationAiResult(true, null, steps, names);
    }

    internal static string Prompt(DerivationAiRequest r)
    {
        var sb = new StringBuilder();
        if (r.Kind == DerivationKind.ActivityFromUseCase)
        {
            sb.AppendLine($"Use case: {r.SourceName}");
            sb.AppendLine($"Actors: {(r.Actors.Count == 0 ? "(none named)" : string.Join(", ", r.Actors))}");
            sb.AppendLine("Description:");
            sb.AppendLine("<description>");
            sb.AppendLine(r.Description.Length > 4000 ? r.Description[..4000] : r.Description);
            sb.AppendLine("</description>");
            sb.AppendLine("Write the steps; leave messages empty.");
        }
        else
        {
            sb.AppendLine($"Activity: {r.SourceName}");
            sb.AppendLine("Messages (key | from → to | action it hands over to):");
            foreach (var (key, from, to, action) in r.Messages.Take(80)) sb.AppendLine($"- {key} | {from} → {to} | {action}");
            sb.AppendLine("Name every message; leave steps empty.");
        }
        return sb.ToString();
    }
}
