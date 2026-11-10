using System.Text;
using System.Text.RegularExpressions;

namespace Meridian.Assistant.Markdown;

public abstract record MdInline;
public sealed record MdText(string Text) : MdInline;
public sealed record MdStrong(IReadOnlyList<MdInline> Children) : MdInline;
public sealed record MdEmphasis(IReadOnlyList<MdInline> Children) : MdInline;
public sealed record MdCode(string Text) : MdInline;
public sealed record MdLink(string Text, string Url) : MdInline;

/// <summary>A model element reference written as <c>[Name](el:ID)</c>; rendered as a clickable chip.</summary>
public sealed record MdMention(string Label, string ElementId) : MdInline;

public abstract record MdBlock;
public sealed record MdParagraph(IReadOnlyList<MdInline> Inlines) : MdBlock;
public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Inlines) : MdBlock;
public sealed record MdListItem(IReadOnlyList<MdInline> Inlines, bool? Checked, int Indent);
public sealed record MdList(bool Ordered, IReadOnlyList<MdListItem> Items) : MdBlock;
public sealed record MdCodeBlock(string Language, string Code) : MdBlock;
public sealed record MdQuote(IReadOnlyList<MdInline> Inlines) : MdBlock;
public sealed record MdTable(IReadOnlyList<IReadOnlyList<MdInline>> Header, IReadOnlyList<IReadOnlyList<IReadOnlyList<MdInline>>> Rows) : MdBlock;
public sealed record MdRule : MdBlock;

/// <summary>
/// A small, safe Markdown parser for assistant answers: paragraphs, headings, lists (with checkboxes), code, quotes,
/// pipe tables, emphasis, inline code, http(s) links and element mentions. It produces a tree that the panel renders
/// with Blazor (so nothing is ever injected as raw HTML), and it tolerates half-written input while a reply streams.
/// </summary>
public static partial class MarkdownLite
{
    public static IReadOnlyList<MdBlock> Parse(string? text)
    {
        var blocks = new List<MdBlock>();
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        var para = new List<string>();
        void FlushPara()
        {
            if (para.Count > 0) blocks.Add(new MdParagraph(Inlines(string.Join("\n", para))));
            para.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0) { FlushPara(); continue; }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushPara();
                var lang = trimmed[3..].Trim();
                var code = new StringBuilder();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal); i++)
                    code.Append(code.Length > 0 ? "\n" : "").Append(lines[i]);
                blocks.Add(new MdCodeBlock(lang, code.ToString()));
                continue;
            }

            if (HeadingRegex().Match(trimmed) is { Success: true } h)
            {
                FlushPara();
                blocks.Add(new MdHeading(h.Groups[1].Length, Inlines(h.Groups[2].Value.Trim())));
                continue;
            }

            if (RuleRegex().IsMatch(trimmed)) { FlushPara(); blocks.Add(new MdRule()); continue; }

            if (trimmed.StartsWith('>'))
            {
                FlushPara();
                var quote = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('>'); i++) quote.Add(lines[i].TrimStart()[1..].Trim());
                i--;
                blocks.Add(new MdQuote(Inlines(string.Join("\n", quote))));
                continue;
            }

            if (trimmed.StartsWith('|') && i + 1 < lines.Length && TableSeparatorRegex().IsMatch(lines[i + 1].Trim()))
            {
                FlushPara();
                var header = Cells(trimmed);
                var rows = new List<IReadOnlyList<IReadOnlyList<MdInline>>>();
                for (i += 2; i < lines.Length && lines[i].Trim().StartsWith('|'); i++)
                    rows.Add(Cells(lines[i].Trim()).Select(c => c).ToList());
                i--;
                blocks.Add(new MdTable(header, rows));
                continue;
            }

            if (ListRegex().Match(line) is { Success: true } li)
            {
                FlushPara();
                var ordered = char.IsDigit(li.Groups[2].Value[0]);
                var items = new List<MdListItem>();
                for (; i < lines.Length; i++)
                {
                    var m = ListRegex().Match(lines[i]);
                    if (m.Success && char.IsDigit(m.Groups[2].Value[0]) == ordered || m.Success && m.Groups[1].Length > 0)
                    {
                        var body = m.Groups[3].Value;
                        bool? check = null;
                        if (CheckRegex().Match(body) is { Success: true } c)
                        {
                            check = c.Groups[1].Value is "x" or "X";
                            body = body[c.Length..];
                        }
                        items.Add(new MdListItem(Inlines(body.Trim()), check, Math.Min(m.Groups[1].Value.Replace("\t", "  ").Length / 2, 4)));
                    }
                    else if (lines[i].Trim().Length > 0 && lines[i].StartsWith("  ", StringComparison.Ordinal) && items.Count > 0)
                    {
                        var last = items[^1];
                        items[^1] = last with { Inlines = [.. last.Inlines, new MdText(" "), .. Inlines(lines[i].Trim())] };
                    }
                    else break;
                }
                i--;
                blocks.Add(new MdList(ordered, items));
                continue;
            }

            para.Add(trimmed);
        }
        FlushPara();
        return blocks;
    }

    private static List<IReadOnlyList<MdInline>> Cells(string row)
    {
        var inner = row.Trim().Trim('|');
        return inner.Split('|').Select(c => Inlines(c.Trim())).ToList();
    }

    /// <summary>Parses inline markup. Unclosed markers are kept as literal text.</summary>
    public static IReadOnlyList<MdInline> Inlines(string s)
    {
        var result = new List<MdInline>();
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length > 0) result.Add(new MdText(text.ToString()));
            text.Clear();
        }

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length && "\\`*_[]()#|>-".Contains(s[i + 1])) { text.Append(s[++i]); continue; }
            if (c == '`')
            {
                var end = s.IndexOf('`', i + 1);
                if (end > i)
                {
                    Flush();
                    result.Add(new MdCode(s[(i + 1)..end]));
                    i = end;
                    continue;
                }
            }
            if ((c == '*' || c == '_') && i + 1 < s.Length && s[i + 1] == c)
            {
                var marker = new string(c, 2);
                var end = s.IndexOf(marker, i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    result.Add(new MdStrong(Inlines(s[(i + 2)..end])));
                    i = end + 1;
                    continue;
                }
            }
            if ((c == '*' || c == '_') && i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1]) && (c == '*' || i == 0 || !char.IsLetterOrDigit(s[i - 1])))
            {
                var end = s.IndexOf(c, i + 1);
                if (end > i + 1 && !char.IsWhiteSpace(s[end - 1]) && (c == '*' || end + 1 >= s.Length || !char.IsLetterOrDigit(s[end + 1])))
                {
                    Flush();
                    result.Add(new MdEmphasis(Inlines(s[(i + 1)..end])));
                    i = end;
                    continue;
                }
            }
            if (c == '[' && LinkRegex().Match(s, i) is { Success: true } link && link.Index == i)
            {
                var label = link.Groups[1].Value;
                var url = link.Groups[2].Value.Trim();
                if (url.StartsWith("el:", StringComparison.OrdinalIgnoreCase))
                {
                    Flush();
                    result.Add(new MdMention(label, url[3..].Trim()));
                    i += link.Length - 1;
                    continue;
                }
                if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    Flush();
                    result.Add(new MdLink(label, url));
                    i += link.Length - 1;
                    continue;
                }
                // Any other scheme (javascript:, data:, relative paths…) stays plain text.
                Flush();
                result.Add(new MdText(label));
                i += link.Length - 1;
                continue;
            }
            text.Append(c);
        }
        Flush();
        return result;
    }

    /// <summary>Plain text of inlines (for tests and accessibility labels).</summary>
    public static string PlainText(IEnumerable<MdInline> inlines) => string.Concat(inlines.Select(i => i switch
    {
        MdText t => t.Text,
        MdStrong s => PlainText(s.Children),
        MdEmphasis e => PlainText(e.Children),
        MdCode c => c.Text,
        MdLink l => l.Text,
        MdMention m => m.Label,
        _ => "",
    }));

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")] private static partial Regex HeadingRegex();
    [GeneratedRegex(@"^(-{3,}|\*{3,}|_{3,})$")] private static partial Regex RuleRegex();
    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")] private static partial Regex TableSeparatorRegex();
    [GeneratedRegex(@"^(\s*)([-*+]|\d{1,3}[.)])\s+(.*)$")] private static partial Regex ListRegex();
    [GeneratedRegex(@"^\[([ xX])\]\s+")] private static partial Regex CheckRegex();
    [GeneratedRegex(@"\[([^\[\]\n]+)\]\(([^()\s]+)\)")] private static partial Regex LinkRegex();
}
