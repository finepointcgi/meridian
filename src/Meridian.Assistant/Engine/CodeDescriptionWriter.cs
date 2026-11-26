using System.Text;
using System.Text.Json;
using Meridian.Assistant.Providers;
using Meridian.Assistant.Tools;
using Meridian.Core.CodeSync;
using static Meridian.Assistant.Tools.ToolSchema;

namespace Meridian.Assistant.Engine;

/// <summary>
/// Code sync's optional AI pass (<see cref="ICodeDescriber"/>): the model names subsystems, packages, key blocks and
/// interfaces in plain words with a one-line purpose, and phrases behaviour decisions as questions and calls as steps,
/// answering through the strict <c>submit_descriptions</c> tool. Works with every provider through
/// <see cref="IChatProvider"/>. Items go in batches; a batch that fails ends the pass with its error (what earlier
/// batches returned is kept). Prompts are never logged.
/// </summary>
public sealed class CodeDescriptionWriter(IChatProvider provider) : ICodeDescriber
{
    public const string ToolName = "submit_descriptions";

    /// <summary>Items per model call.</summary>
    public const int BatchSize = 60;

    public string Name => provider.Info.AssistantName;

    internal static readonly ChatTool Tool = new(ToolName,
        "Submit a readable name (and for structure, a one-line purpose) for each item, by key.",
        Object(
            Req("items", Array(Object(
                Req("key", String("The item key, exactly as given.")),
                Req("name", String("The readable name: a few plain words for structure, a short yes/no question for a decision, a short imperative step for a step.")),
                Req("purpose", String("For subsystems, packages, blocks and interfaces: one sentence on what it does for the system. Empty for decisions and steps."))),
                "One entry per item."))));

    private const string System = """
        You help Meridian, an MBSE tool, explain a software system to systems engineers. Items come from code that was mapped to a SysML
        model. For each item write:
        - subsystem, package, block, interface: a short readable name in plain words and sentence case, the way an engineer would say
          it ("Order storage", "Code sync runner", "Git hosting"), not the code identifier; and a one-sentence purpose saying what it does
          for the system, based only on the code, names and documentation given.
        - decision: a short yes/no question about what the condition means ("Is the user signed in?", "Any changes to review?").
        - step: a short imperative step saying what the call achieves ("Load the project from the database").
        Keep names under 40 characters and purposes under 200. Do not invent behaviour that the code does not show.
        Call submit_descriptions exactly once with every item.
        """;

    public async Task<CodeDescribeResult> DescribeAsync(IReadOnlyList<CodeDescribeItem> items, CancellationToken ct = default)
    {
        var all = new List<CodeDescription>();
        for (var start = 0; start < items.Count; start += BatchSize)
        {
            var batch = items.Skip(start).Take(BatchSize).ToList();
            var (answers, error) = await BatchAsync(batch, ct);
            all.AddRange(answers);
            if (error != null) return new CodeDescribeResult(all, error);
        }
        return new CodeDescribeResult(all);
    }

    private async Task<(List<CodeDescription> Answers, string? Error)> BatchAsync(List<CodeDescribeItem> batch, CancellationToken ct)
    {
        var who = Name;
        JsonElement? submitted = null;
        ChatResponse response;
        try
        {
            var chat = new ChatRequest(System, [Tool], [new ChatMessage(ChatRole.User, [new TextPart(Prompt(batch))])], AssistantEffort.Medium)
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
            return ([], "Cancelled.");
        }
        catch (Exception e) when ((e is ChatProviderException ? e.Message : provider.DescribeError(e)) is { } text)
        {
            return ([], text);
        }
        if (response.Stop == ChatStopReason.Refusal) return ([], $"{who} declined to describe the code.");
        if (response.Stop == ChatStopReason.MaxTokens) return ([], "The answer hit the output limit and was not used.");
        if (submitted == null && response.ToolCalls.FirstOrDefault(c => c.Name == ToolName)?.Input is { ValueKind: JsonValueKind.Object } input)
        {
            if (Validate(Tool.Schema, input).Count > 0) return ([], $"{who}'s answer did not have the expected shape.");
            submitted = input;
        }
        if (submitted is not { } result) return ([], $"{who} did not answer.");
        var keys = batch.Select(b => b.Key).ToHashSet(StringComparer.Ordinal);
        var answers = result.Arr("items")
            .Select(x => new CodeDescription(x.Str("key") ?? "", x.Str("name") ?? "", x.Str("purpose") ?? ""))
            .Where(x => keys.Contains(x.Key) && x.Name.Trim().Length > 0)
            .GroupBy(x => x.Key).Select(g => g.First()).ToList();
        return (answers, null);
    }

    internal static string Prompt(IReadOnlyList<CodeDescribeItem> items)
    {
        var sb = new StringBuilder("Items (key | kind | code | current name, then context lines):\n");
        foreach (var i in items)
        {
            sb.AppendLine($"- {i.Key} | {i.Kind} | {Clip(i.Code, 200)} | {Clip(i.CurrentName, 80)}");
            foreach (var line in i.Context.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(4)) sb.AppendLine("    " + Clip(line, 300));
        }
        return sb.ToString();
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
