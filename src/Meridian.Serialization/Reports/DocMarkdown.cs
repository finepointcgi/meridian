using System.Text;

namespace Meridian.Serialization.Reports;

/// <summary>
/// The small markdown subset used by report text: paragraphs, <c># headings</c> (rendered as run-in sub-headings),
/// <c>- bullets</c>, <c>1. numbered</c> items, <c>&gt; quotes</c>, fenced code, <c>**bold**</c>, <c>*italic*</c>,
/// <c>`code`</c>. Cross-references arrive as <see cref="RefOpen"/>part:name<see cref="RefClose"/> tokens.
/// </summary>
public static class DocMarkdown
{
    public const char RefOpen = '\u0001';
    public const char RefClose = '\u0002';

    public static List<DocNode> Parse(string? text)
    {
        var nodes = new List<DocNode>();
        if (string.IsNullOrWhiteSpace(text)) return nodes;
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var para = new StringBuilder();
        var number = 0;

        void Flush()
        {
            if (para.Length > 0) nodes.Add(new ParagraphNode(Inlines(para.ToString())));
            para.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                Flush();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++) code.Add(lines[i]);
                foreach (var c in code) nodes.Add(new ParagraphNode([new TextRun(c, Code: true)], ParaKind.Code));
                continue;
            }
            if (line.Length == 0) { Flush(); number = 0; continue; }
            var indent = (raw.Length - raw.TrimStart().Length) / 2;
            if (line.StartsWith('#'))
            {
                Flush();
                nodes.Add(new ParagraphNode(Inlines(line.TrimStart('#').Trim()), ParaKind.Subheading));
                continue;
            }
            if (line.Length > 1 && line[0] is '-' or '*' or '+' && line[1] == ' ')
            {
                Flush();
                nodes.Add(new ParagraphNode(Inlines(line[2..].Trim()), ParaKind.Bullet, Indent: Math.Min(indent, 3)));
                continue;
            }
            var dot = line.IndexOf(". ", StringComparison.Ordinal);
            if (dot is > 0 and < 4 && line[..dot].All(char.IsDigit))
            {
                Flush();
                number = int.TryParse(line[..dot], out var n) && number == 0 ? n : number + 1;
                nodes.Add(new ParagraphNode(Inlines(line[(dot + 2)..].Trim()), ParaKind.Numbered, number, Math.Min(indent, 3)));
                continue;
            }
            if (line.StartsWith('>'))
            {
                Flush();
                nodes.Add(new ParagraphNode(Inlines(line.TrimStart('>').Trim()), ParaKind.Quote));
                continue;
            }
            if (para.Length > 0) para.Append(' ');
            para.Append(line);
        }
        Flush();
        return nodes;
    }

    /// <summary>Inline emphasis, code and cross-reference tokens.</summary>
    public static List<Inline> Inlines(string s)
    {
        var runs = new List<Inline>();
        var sb = new StringBuilder();
        bool bold = false, italic = false;

        void Emit()
        {
            if (sb.Length > 0) runs.Add(new TextRun(sb.ToString(), bold, italic));
            sb.Clear();
        }

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length && "*`_\\{}".Contains(s[i + 1])) { sb.Append(s[++i]); continue; }
            if (c == RefOpen)
            {
                var end = s.IndexOf(RefClose, i + 1);
                if (end > i)
                {
                    Emit();
                    var token = s[(i + 1)..end];
                    var colon = token.IndexOf(':');
                    var part = token[..colon] switch { "page" => XRefPart.Page, "title" => XRefPart.Title, _ => XRefPart.Label };
                    runs.Add(new XRefRun(token[(colon + 1)..], part));
                    i = end;
                    continue;
                }
            }
            if (c == '`')
            {
                var end = s.IndexOf('`', i + 1);
                if (end > i)
                {
                    Emit();
                    runs.Add(new TextRun(s[(i + 1)..end], bold, italic, Code: true));
                    i = end;
                    continue;
                }
            }
            if (c == '*' && i + 1 < s.Length && s[i + 1] == '*')
            {
                if (bold || s.IndexOf("**", i + 2, StringComparison.Ordinal) > 0) { Emit(); bold = !bold; i++; continue; }
            }
            else if (c == '*' && (italic || (i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1]) && s.IndexOf('*', i + 1) > 0)))
            {
                Emit(); italic = !italic; continue;
            }
            else if (c == '_' && Underscore(s, i, italic))
            {
                Emit(); italic = !italic; continue;
            }
            sb.Append(c);
        }
        Emit();
        return runs;
    }

    /// <summary><c>_x_</c> emphasis only at word boundaries, so snake_case names stay intact.</summary>
    private static bool Underscore(string s, int i, bool open)
    {
        bool Word(int k) => k >= 0 && k < s.Length && char.IsLetterOrDigit(s[k]);
        if (open) return !Word(i + 1);
        if (Word(i - 1) || i + 1 >= s.Length || char.IsWhiteSpace(s[i + 1])) return false;
        for (var k = i + 2; k < s.Length; k++)
            if (s[k] == '_' && !Word(k + 1) && !char.IsWhiteSpace(s[k - 1])) return true;
        return false;
    }

    /// <summary>Plain text of inline runs, with cross-references rendered by <paramref name="xref"/>.</summary>
    public static string PlainText(IEnumerable<Inline> inlines, Func<XRefRun, string> xref) =>
        string.Concat(inlines.Select(i => i switch { TextRun t => t.Text, XRefRun x => xref(x), _ => "" }));
}
