using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Meridian.Serialization.Mermaid;

/// <summary>One source line of a Mermaid diagram, with its 1-based line number in the input.</summary>
internal readonly record struct MLine(int No, string Text);

/// <summary>One Mermaid diagram found in the input: its header keyword, the rest of the header line and its body.</summary>
internal sealed class MermaidBlock
{
    public required string Keyword { get; init; }
    public required string HeaderRest { get; init; }
    public required int Line { get; init; }
    public string? Title { get; set; }
    public List<MLine> Body { get; } = new();
}

/// <summary>Size and nesting limits that keep a hostile or broken input from exhausting the server.</summary>
public static class MermaidLimits
{
    /// <summary>Largest input read, in characters (longer input is cut off with a warning).</summary>
    public const int MaxInputChars = 4 * 1024 * 1024;

    /// <summary>Most diagrams imported from one input.</summary>
    public const int MaxDiagrams = 100;

    /// <summary>Most statements read per diagram.</summary>
    public const int MaxStatements = 20_000;

    /// <summary>Most nodes (shapes) per diagram.</summary>
    public const int MaxNodes = 2_000;

    /// <summary>Most edges (relationships) per diagram.</summary>
    public const int MaxEdges = 5_000;

    /// <summary>Deepest nesting of subgraphs, composite states, namespaces and fragments.</summary>
    public const int MaxDepth = 32;

    /// <summary>Longest line kept (longer lines are cut off).</summary>
    public const int MaxLineLength = 16_384;
}

/// <summary>
/// Finds the Mermaid diagrams in a text: a plain .mmd file (one or several diagrams, one after the other) or a
/// Markdown file with ```mermaid fences. Front matter (<c>---</c> … <c>---</c>), <c>%%{init}%%</c> directives and
/// <c>%%</c> comments are removed here; the front matter's <c>title:</c> and <c>accTitle:</c> name the diagram.
/// </summary>
internal static class MermaidSource
{
    private static readonly string[] Keywords =
    [
        "flowchart-elk", "flowchart", "graph", "classDiagram-v2", "classDiagram", "stateDiagram-v2", "stateDiagram", "sequenceDiagram",
        "requirementDiagram", "erDiagram", "C4Context", "C4Container", "C4Component", "C4Dynamic", "C4Deployment",
        "gantt", "journey", "gitGraph", "quadrantChart", "sankey-beta", "xychart-beta", "block-beta", "packet-beta", "architecture-beta",
        "zenuml", "radar-beta", "treemap-beta", "mindmap", "timeline", "pie", "kanban", "info", "sankey", "xychart", "block", "packet",
        "architecture",
    ];

    /// <summary>Header keywords that are ordinary words: recognised only when they stand alone (with their usual arguments).</summary>
    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
        { "mindmap", "timeline", "pie", "kanban", "info", "sankey", "xychart", "block", "packet", "architecture", "gantt", "journey" };

    private static readonly Regex FlowHeader = new(@"^(flowchart-elk|flowchart|graph)(?:\s+(TB|TD|BT|RL|LR)\b)?\s*(?:;(.*))?$", RegexOptions.CultureInvariant);

    public static List<MermaidBlock> Split(string? input, bool markdown, List<string> warnings)
    {
        var blocks = new List<MermaidBlock>();
        if (string.IsNullOrEmpty(input)) { warnings.Add("The input is empty."); return blocks; }
        var text = input;
        if (text.Length > MermaidLimits.MaxInputChars)
        {
            warnings.Add($"The input is larger than {MermaidLimits.MaxInputChars / (1024 * 1024)} MB characters; the rest was ignored.");
            text = text[..MermaidLimits.MaxInputChars];
        }
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('﻿');
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Length > MermaidLimits.MaxLineLength)
            {
                warnings.Add($"Line {i + 1} is longer than {MermaidLimits.MaxLineLength} characters and was cut off.");
                lines[i] = lines[i][..MermaidLimits.MaxLineLength];
            }

        var fenced = Fences(lines);
        if (fenced.Count > 0 || markdown)
        {
            if (fenced.Count == 0) warnings.Add("No ```mermaid blocks were found in the Markdown file.");
            foreach (var f in fenced) SplitSource(f, blocks, warnings);
        }
        else SplitSource(lines.Select((l, i) => new MLine(i + 1, l)).ToList(), blocks, warnings);

        if (blocks.Count > MermaidLimits.MaxDiagrams)
        {
            warnings.Add($"Only the first {MermaidLimits.MaxDiagrams} of {blocks.Count} diagrams were read.");
            blocks.RemoveRange(MermaidLimits.MaxDiagrams, blocks.Count - MermaidLimits.MaxDiagrams);
        }
        if (blocks.Count == 0 && fenced.Count > 0) warnings.Add("The ```mermaid blocks contain no diagram.");
        else if (blocks.Count == 0 && !markdown) warnings.Add("No Mermaid diagram was found (expected a header such as flowchart TD, classDiagram or sequenceDiagram).");
        return blocks;
    }

    /// <summary>The bodies of ```mermaid (or ~~~mermaid, :::mermaid) fences of a Markdown text.</summary>
    private static List<List<MLine>> Fences(string[] lines)
    {
        var result = new List<List<MLine>>();
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimStart();
            string? close = null;
            if (t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal))
            {
                var fenceChar = t[0];
                var n = t.TakeWhile(c => c == fenceChar).Count();
                var info = t[n..].Trim().Trim('{', '}').Trim();
                if (info.StartsWith("mermaid", StringComparison.OrdinalIgnoreCase)) close = new string(fenceChar, n);
                else
                {
                    // Skip a non-mermaid fenced block entirely, so a ```mermaid inside it is not read.
                    for (i++; i < lines.Length && !ClosesFence(lines[i], fenceChar, n); i++) { }
                    continue;
                }
            }
            else if (t.StartsWith(":::mermaid", StringComparison.OrdinalIgnoreCase)) close = ":::";
            if (close == null) continue;
            var body = new List<MLine>();
            for (i++; i < lines.Length && !ClosesFence(lines[i], close[0], close.Length); i++) body.Add(new MLine(i + 1, lines[i]));
            result.Add(body);
        }
        return result;
    }

    /// <summary>A closing fence: only fence characters (at least as many as the opening one).</summary>
    private static bool ClosesFence(string line, char c, int n)
    {
        var t = line.Trim();
        return t.Length >= n && t.All(x => x == c);
    }

    private static void SplitSource(List<MLine> lines, List<MermaidBlock> blocks, List<string> warnings)
    {
        MermaidBlock? current = null;
        string? pendingTitle = null;
        var ignoredBefore = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i].Text;
            var t = raw.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("%%{", StringComparison.Ordinal) && !t.Contains("}%%", StringComparison.Ordinal))
            {
                // A directive spread over several lines.
                for (i++; i < lines.Count && !lines[i].Text.Contains("}%%", StringComparison.Ordinal); i++) { }
                continue;
            }
            if (t.StartsWith("%%", StringComparison.Ordinal)) continue;   // comments and %%{init}%% directives

            // Front matter (config and title), before a diagram header, up to the closing ---.
            if (t == "---")
            {
                var (title, end) = FrontMatter(lines, i);
                if (end > i && (current == null || IsFrontMatterAhead(lines, i)))
                {
                    current = null;
                    pendingTitle = title;
                    i = end;
                    continue;
                }
            }

            if (Header(t) is { } h)
            {
                current = new MermaidBlock { Keyword = h.Keyword, HeaderRest = h.Rest, Line = lines[i].No, Title = pendingTitle };
                pendingTitle = null;
                blocks.Add(current);
                if (h.Inline is { Length: > 0 } inline) current.Body.Add(new MLine(lines[i].No, inline));
                continue;
            }
            if (current == null) { ignoredBefore++; continue; }

            var body = StripTrailingComment(raw);
            if (body.Trim().Length == 0) continue;
            var bt = body.Trim();
            if (bt.StartsWith("accTitle", StringComparison.Ordinal) && bt.IndexOf(':') is var c && c > 0 && bt[..c].Trim() == "accTitle")
            {
                current.Title ??= Clean(bt[(c + 1)..]);
                continue;
            }
            if (bt.StartsWith("accDescr", StringComparison.Ordinal))
            {
                if (bt.Contains('{') && !bt.Contains('}'))
                    for (i++; i < lines.Count && !lines[i].Text.Contains('}'); i++) { }
                continue;
            }
            current.Body.Add(new MLine(lines[i].No, body));
        }
        if (ignoredBefore > 0) warnings.Add($"{ignoredBefore} line(s) before the first diagram header were ignored.");
    }

    /// <summary>True when the --- at <paramref name="i"/> opens a front matter block followed by a diagram header.</summary>
    private static bool IsFrontMatterAhead(List<MLine> lines, int i)
    {
        var (_, end) = FrontMatter(lines, i);
        if (end <= i) return false;
        for (var j = end + 1; j < lines.Count; j++)
        {
            var t = lines[j].Text.Trim();
            if (t.Length == 0 || t.StartsWith("%%", StringComparison.Ordinal)) continue;
            return Header(t) != null;
        }
        return false;
    }

    /// <summary>Reads front matter starting at a --- line: its title and the index of the closing ---, or (null, start) when unclosed.</summary>
    private static (string? Title, int End) FrontMatter(List<MLine> lines, int start)
    {
        string? title = null;
        for (var j = start + 1; j < lines.Count && j < start + 500; j++)
        {
            var t = lines[j].Text;
            if (t.Trim() == "---") return (title, j);
            if (t.StartsWith("title:", StringComparison.Ordinal)) title = Clean(Unquote(t["title:".Length..].Trim()));
        }
        return (null, start);
    }

    public sealed record HeaderMatch(string Keyword, string Rest, string? Inline);

    /// <summary>Recognises a diagram header line.</summary>
    public static HeaderMatch? Header(string line)
    {
        var t = line.Trim();
        var f = FlowHeader.Match(t);
        if (f.Success) return new HeaderMatch(f.Groups[1].Value == "flowchart-elk" ? "flowchart" : f.Groups[1].Value, f.Groups[2].Value, f.Groups[3].Success ? f.Groups[3].Value : null);
        foreach (var k in Keywords)
        {
            if (k is "flowchart" or "flowchart-elk" or "graph") continue;
            if (!t.StartsWith(k, StringComparison.Ordinal)) continue;
            var rest = t[k.Length..];
            if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]) && rest[0] != ':' && rest[0] != ';') continue;
            rest = rest.Trim();
            // Bare words could be flowchart node ids: accept them only alone (or a pie chart with its usual arguments).
            if (GenericWords.Contains(k) && rest.Length > 0
                && !(k == "pie" && (rest.StartsWith("showData", StringComparison.Ordinal) || rest.StartsWith("title", StringComparison.Ordinal)))) continue;
            if (k is "classDiagram" or "classDiagram-v2" or "stateDiagram" or "stateDiagram-v2" or "sequenceDiagram" or "requirementDiagram" or "erDiagram"
                && rest.Length > 0 && !rest.StartsWith(';')) rest = rest.TrimStart(':').Trim();
            return new HeaderMatch(k, rest, rest.StartsWith(';') ? rest[1..] : null);
        }
        return null;
    }

    /// <summary>Removes a trailing %% comment that is not inside quotes.</summary>
    private static string StripTrailingComment(string s)
    {
        var q = false;
        for (var i = 0; i + 1 < s.Length; i++)
        {
            if (s[i] == '"') q = !q;
            else if (!q && s[i] == '%' && s[i + 1] == '%' && (i == 0 || char.IsWhiteSpace(s[i - 1]))) return s[..i];
        }
        return s;
    }

    // ============================================================ text helpers

    public static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '\'' && s[^1] == '\'')) s = s[1..^1];
        if (s.Length >= 2 && s[0] == '`' && s[^1] == '`') s = s[1..^1];   // markdown strings
        return s;
    }

    private static readonly Regex Br = new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Tag = new(@"</?(?:b|i|u|s|em|strong|span|div|p|small|big|sub|sup|code|font|a|strike|mark|del|ins|tt|center|h[1-6])(?:\s[^<>]*)?/?>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Entity = new(@"#([a-zA-Z]+|\d+);", RegexOptions.CultureInvariant);

    /// <summary>Label text: quotes removed, <c>&lt;br&gt;</c> as a line break, other HTML tags and markdown emphasis dropped, #entities; decoded.</summary>
    public static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Unquote(s);
        s = Br.Replace(s, "\n");
        s = Tag.Replace(s, "");
        s = Entity.Replace(s, m =>
        {
            var v = m.Groups[1].Value;
            if (char.IsDigit(v[0]))
                return int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var cp) && cp is > 0 and <= 0x10FFFF && (cp < 0xD800 || cp > 0xDFFF)
                    ? char.ConvertFromUtf32(cp) : m.Value;
            return v switch
            {
                "quot" => "\"", "amp" => "&", "lt" => "<", "gt" => ">", "nbsp" => " ", "apos" => "'", "colon" => ":", "semi" => ";",
                "hash" or "num" => "#", "lpar" => "(", "rpar" => ")", "lsqb" => "[", "rsqb" => "]", "lcub" => "{", "rcub" => "}",
                _ => m.Value,
            };
        });
        if (s.StartsWith("**", StringComparison.Ordinal) && s.EndsWith("**", StringComparison.Ordinal) && s.Length > 4) s = s[2..^2];
        return string.Join("\n", s.Split('\n').Select(l => l.Trim())).Trim();
    }

    /// <summary>A label as an element name: one line.</summary>
    public static string Name(string? s) => Regex.Replace(Clean(s), @"\s*\n\s*", " ").Trim();

    /// <summary>Splits on <paramref name="sep"/> outside quotes and brackets.</summary>
    public static List<string> SplitTop(string s, char sep)
    {
        var parts = new List<string>();
        var depth = 0;
        var q = false;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"') q = !q;
            else if (q) continue;
            else if (c is '[' or '(' or '{') depth++;
            else if (c is ']' or ')' or '}') depth = Math.Max(0, depth - 1);
            else if (c == sep && depth == 0) { parts.Add(s[start..i]); start = i + 1; }
        }
        parts.Add(s[start..]);
        return parts;
    }

    /// <summary>A Mermaid identifier made from a name (for export).</summary>
    public static string IdFrom(string name, ISet<string> used, string fallback = "n")
    {
        var sb = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(c) && c < 128 || c == '_') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var id = sb.ToString().Trim('_');
        if (id.Length == 0 || char.IsDigit(id[0])) id = fallback + id;
        if (id.Length > 40) id = id[..40];
        if (Reserved.Contains(id)) id += "_";
        var unique = id;
        for (var i = 2; !used.Add(unique); i++) unique = id + i;
        return unique;
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { "end", "graph", "flowchart", "subgraph", "class", "classDef", "style", "linkStyle", "click", "state", "note", "direction", "default", "call", "href", "namespace", "loop", "alt", "else", "opt", "par", "and", "rect", "critical", "break", "option", "participant", "actor", "activate", "deactivate", "autonumber", "title", "box", "requirement", "element", "o", "x" };
}
