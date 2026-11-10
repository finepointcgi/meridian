using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Helpers;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Meridian.Assistant.Providers;
using Meridian.Assistant.Tools;
using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization.Reports;
using static Meridian.Assistant.Tools.ToolSchema;

namespace Meridian.Assistant.Engine;

/// <summary>A drafted overview and rationale paragraph for one document section (editable before it is used).</summary>
public sealed class NarrativeSection(string title, string overview, string rationale)
{
    public string Title { get; } = title;
    public string Overview { get; set; } = overview;
    public string Rationale { get; set; } = rationale;
    public bool Include { get; set; } = true;
}

public sealed record NarrativeResult(bool Ok, string? Error, IReadOnlyList<NarrativeSection> Sections);

/// <summary>
/// Drafts the narrative of a generated document (the SDD's "AI narrative" option): Claude reads a compact digest of the model
/// and returns an overview and a rationale paragraph per section through the strict <c>submit_narrative</c> tool. The
/// result is only a draft: the report dialog shows it for review and editing, and it is never added to a document
/// silently. Same conventions as <see cref="AssistantEngine"/>: configured model, adaptive thinking, strict tool with
/// <c>tool_choice: auto</c>, refusal handling, optional server-side fallbacks. Prompts are never logged.
/// </summary>
public sealed class NarrativeWriter
{
    public const string ToolName = "submit_narrative";
    private readonly AssistantOptions _options;
    private readonly AnthropicClient? _client;
    private readonly IChatProvider? _provider;

    /// <summary>Claude through the Anthropic SDK (the original request, unchanged).</summary>
    public NarrativeWriter(AnthropicClient client, AssistantOptions? options = null)
    {
        _client = client;
        _options = options ?? new AssistantOptions();
    }

    /// <summary>Any provider: Claude is still called through the SDK request above; other providers through <see cref="IChatProvider"/>.</summary>
    public NarrativeWriter(IChatProvider provider, AssistantOptions? options = null)
    {
        _options = options ?? (provider as AnthropicChatProvider)?.Options ?? new AssistantOptions();
        if (provider is AnthropicChatProvider anthropic) _client = anthropic.Client;
        else _provider = provider;
    }

    private static readonly ChatTool NeutralTool = new(ToolName,
        "Submit the drafted narrative: one entry per document section, with the exact section title, an overview paragraph and a rationale paragraph.",
        Object(Req("sections", Array(Object(
            Req("title", String("The section title, exactly as given.")),
            Req("overview", String("Overview paragraph for the section.")),
            Req("rationale", String("Rationale paragraph for the section."))), "One entry per section."))));

    private const string System = """
        You write the narrative paragraphs of an engineering document (for example a System Design Description) that Meridian, an MBSE
        tool, generates from a SysML model. You receive a digest of the model and the document's section titles.
        For every section write:
        - overview: 2 to 4 sentences introducing what the section covers for THIS system, using the model's element names;
        - rationale: 1 to 3 sentences on why the design is organised that way, based only on what the model records (documentation,
          requirements satisfied, allocations). If the model records no rationale for a section, say briefly what the section's
          figures and tables show instead of inventing one.
        Rules: use only facts from the digest; never invent components, requirements, numbers or standards. Plain prose, no headings,
        no Markdown lists, no curly braces. Call submit_narrative exactly once with one entry per section title, in order.
        """;

    private static readonly BetaToolUnion Tool = new BetaTool
    {
        Name = ToolName,
        Description = "Submit the drafted narrative: one entry per document section, with the exact section title, an overview paragraph and a rationale paragraph.",
        InputSchema = InputSchema.FromRawUnchecked(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Object(
            Req("sections", Array(Object(
                Req("title", String("The section title, exactly as given.")),
                Req("overview", String("Overview paragraph for the section.")),
                Req("rationale", String("Rationale paragraph for the section."))), "One entry per section."))).ToJsonString())!),
        Strict = true,
    };

    /// <summary>Asks Claude for the narrative. Never throws for API failures: they come back as an error result.</summary>
    public async Task<NarrativeResult> DraftAsync(ModelRepository m, string documentTitle, IReadOnlyList<string> sectionTitles, CancellationToken ct = default)
    {
        if (sectionTitles.Count == 0) return new NarrativeResult(false, "The template has no sections.", []);
        var prompt = $"""
            Document: {documentTitle}
            Sections, in order:
            {string.Join("\n", sectionTitles.Select((t, i) => $"{i + 1}. {t}"))}

            <model>
            {Digest(m)}
            </model>
            """;
        if (_client is not { } client) return await DraftWithProviderAsync(_provider!, prompt, sectionTitles, ct);
        var p = new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = _options.MaxTokens,
            System = new List<BetaTextBlockParam> { new() { Text = System } },
            Tools = [Tool],
            ToolChoice = new BetaToolChoiceAuto(),
            OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
            Messages = [new BetaUserMessageParam { Content = new List<BetaContentBlockParam> { new BetaTextBlockParam { Text = prompt } } }],
        };
        if (_options.ServerSideFallbacks)
            p = p with { Betas = [AnthropicBeta.ServerSideFallback2026_07_01], Fallbacks = new BetaFallbacksParam(new Default()) };

        BetaMessage message;
        try
        {
            var events = new List<BetaRawMessageStreamEvent>();
            await foreach (var ev in client.Beta.Messages.CreateStreaming(p, ct).WithCancellation(ct)) events.Add(ev);
            message = await Replay(events).Aggregate();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new NarrativeResult(false, "Cancelled.", []);
        }
        catch (Exception e) when (AssistantEngine.Describe(e) is { } text)
        {
            return new NarrativeResult(false, text, []);
        }

        var stop = message.StopReason?.Raw();
        if (stop == "refusal")
            return new NarrativeResult(false, "Claude declined to draft this narrative." + (message.StopDetails?.Explanation is { Length: > 0 } why ? " " + why : ""), []);
        if (stop == "max_tokens") return new NarrativeResult(false, "The narrative hit the output limit and was not used. Try a template with fewer sections.", []);
        var use = message.Content.Select(b => b.TryPickToolUse(out var tu) ? tu : null).FirstOrDefault(t => t?.Name == ToolName);
        if (use == null) return new NarrativeResult(false, "Claude did not return a narrative. Try again.", []);

        JsonElement input;
        try { input = JsonSerializer.SerializeToElement(use.Input); }
        catch (Exception e) when (e is JsonException or NotSupportedException) { return new NarrativeResult(false, "The narrative was not valid JSON.", []); }
        return Match(input, sectionTitles, "Claude");
    }

    /// <summary>A local model or Claude Code: the same prompt and tool through the provider-neutral interface.</summary>
    private static async Task<NarrativeResult> DraftWithProviderAsync(IChatProvider provider, string prompt, IReadOnlyList<string> sectionTitles, CancellationToken ct)
    {
        var who = provider.Info.AssistantName;
        JsonElement? submitted = null;
        ChatResponse response;
        try
        {
            var request = new ChatRequest(System, [NeutralTool], [new ChatMessage(ChatRole.User, [new TextPart(prompt)])], AssistantEffort.Medium)
            {
                ExecuteTool = provider.RunsToolLoop ? call =>
                {
                    if (call.Name != ToolName || call.Input is not { ValueKind: JsonValueKind.Object } i) return ToolResult.Error($"Call {ToolName}.");
                    var errors = ToolSchema.Validate(NeutralTool.Schema, i);
                    if (errors.Count > 0) return ToolResult.Error("Invalid input: " + string.Join("; ", errors.Take(8)));
                    submitted = i;
                    return ToolResult.Ok(new { received = true, note = "Done. Do not call any more tools." });
                } : null,
            };
            response = await provider.CompleteAsync(request, NullChatStream.Instance, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new NarrativeResult(false, "Cancelled.", []);
        }
        catch (Exception e) when ((e is ChatProviderException ? e.Message : provider.DescribeError(e)) is { } text)
        {
            return new NarrativeResult(false, text, []);
        }
        if (response.Stop == ChatStopReason.Refusal) return new NarrativeResult(false, $"{who} declined to draft this narrative.", []);
        if (response.Stop == ChatStopReason.MaxTokens) return new NarrativeResult(false, "The narrative hit the output limit and was not used. Try a template with fewer sections.", []);
        if (submitted == null && response.ToolCalls.FirstOrDefault(c => c.Name == ToolName)?.Input is { ValueKind: JsonValueKind.Object } input)
        {
            if (ToolSchema.Validate(NeutralTool.Schema, input).Count > 0) return new NarrativeResult(false, $"{who}'s narrative did not have the expected shape. Try again.", []);
            submitted = input;
        }
        if (submitted is not { } result) return new NarrativeResult(false, $"{who} did not return a narrative. Try again.", []);
        return Match(result, sectionTitles, who);
    }

    private static NarrativeResult Match(JsonElement input, IReadOnlyList<string> sectionTitles, string who)
    {
        var drafted = input.Arr("sections").Select(s => (Title: s.Str("title") ?? "", Overview: Clean(s.Str("overview")), Rationale: Clean(s.Str("rationale")))).ToList();
        var sections = new List<NarrativeSection>();
        foreach (var title in sectionTitles)
        {
            var hit = drafted.FirstOrDefault(d => string.Equals(d.Title.Trim(), title, StringComparison.OrdinalIgnoreCase));
            if (hit.Title is { Length: > 0 } && (hit.Overview.Length > 0 || hit.Rationale.Length > 0))
                sections.Add(new NarrativeSection(title, hit.Overview, hit.Rationale));
        }
        return sections.Count == 0
            ? new NarrativeResult(false, $"{who}'s narrative did not match the document's sections. Try again.", [])
            : new NarrativeResult(true, null, sections);
    }

    private static string Clean(string? s) => (s ?? "").Replace('{', '(').Replace('}', ')').Trim();

    private static async IAsyncEnumerable<BetaRawMessageStreamEvent> Replay(List<BetaRawMessageStreamEvent> events)
    {
        foreach (var e in events) yield return e;
        await Task.CompletedTask;
    }

    /// <summary>
    /// Adds the reviewed narrative to a template: the overview (and rationale) open the matching top-level sections.
    /// Only sections the user kept (<see cref="NarrativeSection.Include"/>) with text are used.
    /// </summary>
    public static DocTemplate ApplyTo(DocTemplate template, IEnumerable<NarrativeSection> narrative)
    {
        foreach (var n in narrative.Where(n => n.Include))
        {
            var section = template.Sections.FirstOrDefault(s => string.Equals(s.Title, n.Title, StringComparison.OrdinalIgnoreCase));
            if (section == null) continue;
            var blocks = new List<DocBlock>();
            if (Clean(n.Overview) is { Length: > 0 } o) blocks.Add(new TextBlock { Markdown = o });
            if (Clean(n.Rationale) is { Length: > 0 } r) blocks.Add(new TextBlock { Markdown = "*Rationale.* " + r });
            section.Blocks.InsertRange(0, blocks);
        }
        return template;
    }

    /// <summary>A compact, bounded summary of the model for the prompt (names, documentation, structure, requirements, diagrams).</summary>
    public static string Digest(ModelRepository m, int maxChars = 40_000)
    {
        var sb = new StringBuilder();
        string T(string s, int max) => s.Length <= max ? s : s[..max] + "…";
        void Line(string s) { if (sb.Length < maxChars) sb.AppendLine(s); }
        Line($"Model: {m.Root.Name}" + (m.Root.Documentation.Length > 0 ? $" — {T(m.Root.Documentation, 600)}" : ""));
        Line("Packages:");
        foreach (var p in m.OfKind(ElementKind.Package).OrderBy(p => p.QualifiedName, StringComparer.OrdinalIgnoreCase).Take(80))
            Line($"- {p.QualifiedName}" + (p.Documentation.Length > 0 ? $": {T(p.Documentation, 200)}" : ""));
        Line("Blocks:");
        foreach (var b in m.OfKind(ElementKind.Block).Concat(m.OfKind(ElementKind.InterfaceBlock)).OrderBy(b => b.QualifiedName, StringComparer.OrdinalIgnoreCase).Take(80))
        {
            var parts = b.OwnedElements.Where(x => x.Kind == ElementKind.Property).Take(12).Select(x => $"{x.Name} : {m.TypeDisplayName(x)}");
            var ports = b.OwnedElements.Where(x => x.Kind == ElementKind.Port).Take(10).Select(x => x.Name);
            var satisfies = m.Outgoing(b, ElementKind.Satisfy).Take(8).Select(r => r.Target.Get(FieldKeys.RequirementId) is { Length: > 0 } id ? id : r.Target.Name);
            Line($"- {b.QualifiedName} [{b.Kind}]" + (b.Documentation.Length > 0 ? $": {T(b.Documentation, 240)}" : "") +
                 (parts.Any() ? $" | features: {string.Join(", ", parts)}" : "") + (ports.Any() ? $" | ports: {string.Join(", ", ports)}" : "") +
                 (satisfies.Any() ? $" | satisfies: {string.Join(", ", satisfies)}" : ""));
        }
        Line("Behaviors:");
        foreach (var b in m.AllElements.Where(e => Metamodel.IsBehavior(e.Kind)).OrderBy(b => b.QualifiedName, StringComparer.OrdinalIgnoreCase).Take(40))
            Line($"- {b.QualifiedName} [{b.Kind}]" + (b.Documentation.Length > 0 ? $": {T(b.Documentation, 200)}" : ""));
        var coverage = Traceability.Coverage(m);
        Line($"Requirements: {coverage.Total} ({coverage.Satisfied} satisfied, {coverage.Verified} verified).");
        foreach (var r in m.OfKind(ElementKind.Requirement).OrderBy(r => r.Get(FieldKeys.RequirementId), StringComparer.OrdinalIgnoreCase).Take(60))
            Line($"- {r.Get(FieldKeys.RequirementId)} {r.Name}: {T(r.Get(FieldKeys.Text), 200)}" + (r.Get(ReqFields.Rationale) is { Length: > 0 } why ? $" (rationale: {T(why, 160)})" : ""));
        Line("Diagrams:");
        foreach (var d in m.Diagrams.OrderBy(d => d.QualifiedName, StringComparer.OrdinalIgnoreCase).Take(80))
            Line($"- {d.Name} [{Metamodel.DisplayName(d.DiagramType)}] in {d.Owner?.Name}" + (d.Documentation.Length > 0 ? $": {T(d.Documentation, 160)}" : ""));
        return sb.Length > maxChars ? sb.ToString(0, maxChars) : sb.ToString();
    }
}
