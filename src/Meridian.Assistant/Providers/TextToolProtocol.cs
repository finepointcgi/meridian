using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meridian.Assistant.Tools;

namespace Meridian.Assistant.Providers;

/// <summary>
/// Tool calls for local models without native function calling: the model is asked to answer with a fenced JSON block
/// (<c>{"tool": "...", "arguments": {...}}</c>, or an array of them). Parsing is lenient (fences, <c>&lt;tool_call&gt;</c>
/// tags, bare JSON, trailing commas, comments, arguments sent as a string, a few common key spellings), but every call is
/// validated against its tool's schema before it is accepted.
/// </summary>
public static partial class TextToolProtocol
{
    /// <summary>Extra system-prompt text that describes the tools and the reply format.</summary>
    public static string Instructions(IReadOnlyList<ChatTool> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("<tool_protocol>");
        sb.AppendLine("You can call the tools below. This server has no native function calling, so call a tool by replying with a fenced JSON block, exactly like this:");
        sb.AppendLine("```json");
        sb.AppendLine("{\"tool\": \"search_elements\", \"arguments\": {\"query\": \"kind:Requirement\"}}");
        sb.AppendLine("```");
        sb.AppendLine("Rules:");
        sb.AppendLine("- \"tool\" is one of the tool names below; \"arguments\" is a JSON object that matches that tool's JSON schema exactly (required properties present, no other properties, enum values spelled exactly).");
        sb.AppendLine("- To call several tools at once, put a JSON array of such objects in one block. Put nothing after the block: wait for the results.");
        sb.AppendLine("- Tool results come back in a user message that starts with \"Tool results\". Then continue: call more tools or answer.");
        sb.AppendLine("- When you are done, answer in plain Markdown with no tool block.");
        sb.AppendLine("Tools:");
        foreach (var t in tools)
        {
            sb.Append("- ").Append(t.Name).Append(": ").AppendLine(t.Description);
            sb.Append("  schema: ").AppendLine(t.Schema.ToJsonString());
        }
        sb.Append("</tool_protocol>");
        return sb.ToString();
    }

    /// <summary>How a tool call is written back into the history for this protocol.</summary>
    public static string Render(IEnumerable<ToolCallPart> calls)
    {
        var list = calls.Select(c => $"{{\"tool\": {JsonSerializer.Serialize(c.Name)}, \"arguments\": {(c.Input is { ValueKind: JsonValueKind.Object } o ? o.GetRawText() : "{}")}}}").ToList();
        if (list.Count == 0) return "";
        return "```json\n" + (list.Count == 1 ? list[0] : "[" + string.Join(",\n", list) + "]") + "\n```";
    }

    /// <summary>The user message that carries tool results back to the model.</summary>
    public static string RenderResults(IEnumerable<ToolResultPart> results)
    {
        var sb = new StringBuilder("Tool results:");
        foreach (var r in results)
        {
            sb.Append("\n\n### ").Append(r.Name).Append(r.IsError ? " (error)" : "").Append('\n').Append(r.Content);
        }
        sb.Append("\n\nContinue: call another tool with a ```json block, or answer.");
        return sb.ToString();
    }

    /// <summary>The result of parsing a reply.</summary>
    /// <param name="Text">The reply with the tool blocks removed (what the user should see).</param>
    /// <param name="Calls">Valid tool calls, in order.</param>
    /// <param name="Errors">Problems with blocks that looked like tool calls (empty = everything was usable).</param>
    public sealed record ParseResult(string Text, IReadOnlyList<ToolCallPart> Calls, IReadOnlyList<string> Errors);

    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 };

    [GeneratedRegex(@"```[ \t]*(?<lang>[A-Za-z0-9_-]*)[ \t]*\r?\n(?<body>.*?)```", RegexOptions.Singleline)]
    private static partial Regex Fence();

    [GeneratedRegex(@"<tool_call>(?<body>.*?)(</tool_call>|$)", RegexOptions.Singleline)]
    private static partial Regex ToolCallTag();

    /// <summary>Finds tool calls in <paramref name="reply"/> and validates each against <paramref name="tools"/>.</summary>
    public static ParseResult Parse(string reply, IReadOnlyList<ChatTool> tools, Func<string>? newId = null)
    {
        newId ??= NewId;
        var calls = new List<ToolCallPart>();
        var errors = new List<string>();
        var text = reply;

        var candidates = new List<(string Body, string Whole, bool Explicit)>();
        foreach (Match m in ToolCallTag().Matches(reply)) candidates.Add((m.Groups["body"].Value, m.Value, true));
        foreach (Match m in Fence().Matches(reply))
        {
            var lang = m.Groups["lang"].Value.ToLowerInvariant();
            if (lang is "" or "json" or "tool" or "tool_call" or "jsonc") candidates.Add((m.Groups["body"].Value, m.Value, lang is "tool" or "tool_call"));
        }
        if (candidates.Count == 0 && reply.TrimStart() is { Length: > 0 } t && (t[0] == '{' || t[0] == '['))
            candidates.Add((t, reply, false));   // the whole reply is JSON

        foreach (var (body, whole, isExplicit) in candidates)
        {
            var root = ParseJson(body);
            if (root == null)
            {
                // Only report blocks that were clearly meant as a tool call.
                if (isExplicit || LooksLikeCall(body)) { errors.Add("The tool block is not valid JSON."); text = text.Replace(whole, ""); }
                continue;
            }
            var items = root.Value.ValueKind == JsonValueKind.Array ? root.Value.EnumerateArray().ToList() : [root.Value];
            var any = false;
            var blockCalls = new List<ToolCallPart>();
            var blockErrors = new List<string>();
            foreach (var item in items)
            {
                if (!TryReadCall(item, out var name, out var args)) continue;
                any = true;
                var tool = tools.FirstOrDefault(x => x.Name == name);
                if (tool == null)
                {
                    blockErrors.Add($"Unknown tool '{name}'. Use one of: {string.Join(", ", tools.Select(x => x.Name))}.");
                    continue;
                }
                if (args is not { ValueKind: JsonValueKind.Object } a)
                {
                    blockErrors.Add($"{name}: \"arguments\" must be a JSON object.");
                    continue;
                }
                var problems = ToolSchema.Validate(tool.Schema, a);
                if (problems.Count > 0)
                {
                    blockErrors.Add($"{name}: " + string.Join("; ", problems.Take(8)));
                    continue;
                }
                blockCalls.Add(new ToolCallPart(newId(), name, a.Clone(), a.GetRawText()));
            }
            if (!any) continue;   // JSON, but not a tool call (an example or data in the answer)
            calls.AddRange(blockCalls);
            errors.AddRange(blockErrors);
            text = text.Replace(whole, "");
        }
        return new ParseResult(text.Trim(), calls, errors);
    }

    private static bool LooksLikeCall(string body) =>
        body.Contains("\"tool\"", StringComparison.Ordinal) || body.Contains("\"arguments\"", StringComparison.Ordinal) || body.Contains("\"name\"", StringComparison.Ordinal);

    private static bool TryReadCall(JsonElement item, out string name, out JsonElement? args)
    {
        name = "";
        args = null;
        if (item.ValueKind != JsonValueKind.Object) return false;
        var target = item;
        if (item.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object) target = fn;   // {"function": {"name", "arguments"}}
        string? n = null;
        foreach (var key in (string[])["tool", "name", "tool_name", "function"])
            if (target.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) { n = v.GetString(); break; }
        if (string.IsNullOrWhiteSpace(n)) return false;
        name = n.Trim();
        foreach (var key in (string[])["arguments", "parameters", "input", "args"])
        {
            if (!target.TryGetProperty(key, out var a)) continue;
            args = a.ValueKind == JsonValueKind.String ? ParseJson(a.GetString() ?? "") : a;   // arguments sent as a JSON string
            return true;
        }
        args = JsonDocument.Parse("{}").RootElement.Clone();   // a tool without arguments
        return true;
    }

    /// <summary>Parses JSON leniently: trailing commas, comments, typographic quotes and trailing prose after the value.</summary>
    internal static JsonElement? ParseJson(string s)
    {
        s = s.Trim().Replace('“', '"').Replace('”', '"');
        if (s.Length == 0) return null;
        if (TryParse(s) is { } direct) return direct;
        // Cut to the first complete JSON value (a model may add prose or a second fence after it).
        var start = s.IndexOfAny(['{', '[']);
        if (start < 0) return null;
        var end = MatchingEnd(s, start);
        return end > start ? TryParse(s[start..(end + 1)]) : null;
    }

    private static JsonElement? TryParse(string s)
    {
        try
        {
            using var doc = JsonDocument.Parse(s, Lenient);
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    private static int MatchingEnd(string s, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c is '{' or '[') depth++;
            else if (c is '}' or ']' && --depth == 0) return i;
        }
        return -1;
    }

    private static string NewId() => "call" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// How much of a partially streamed reply can be shown: everything before a tool block may start (a fence, a
    /// <c>&lt;tool_call&gt;</c> tag, or a reply that opens with JSON). The rest is shown when the reply is complete.
    /// </summary>
    public static int VisibleLength(string partial)
    {
        var trimmed = partial.TrimStart();
        if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[')) return 0;
        var cut = partial.Length;
        var fence = partial.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0) cut = fence;
        var tag = partial.IndexOf("<tool_call>", StringComparison.Ordinal);
        if (tag >= 0) cut = Math.Min(cut, tag);
        if (cut == partial.Length)
        {
            // Hold back a trailing partial "```" or "<tool_call" so it is never shown half-way.
            var hold = 0;
            for (var k = Math.Min(10, partial.Length); k > 0; k--)
            {
                var tail = partial[^k..];
                if ("```".StartsWith(tail, StringComparison.Ordinal) || "<tool_call>".StartsWith(tail, StringComparison.Ordinal)) { hold = k; break; }
            }
            cut -= hold;
        }
        return cut;
    }
}
