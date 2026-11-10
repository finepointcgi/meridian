using System.Text;

namespace Meridian.Serialization.CodeSync.Parsing;

internal enum TokKind { Ident, Number, String, Punct, Newline }

/// <summary>A token of a hand-parsed source file. <see cref="Doc"/> is the doc comment (<c>/** … */</c>) that directly
/// precedes it, already cleaned; <see cref="Indent"/> is the column of the first token on a Python logical line.</summary>
internal sealed record Tok(TokKind Kind, string Text, int Line, string? Doc = null, int Indent = -1)
{
    public bool Is(string text) => Kind is TokKind.Punct or TokKind.Ident && Text == text;
    public override string ToString() => $"{Kind} '{Text}' @{Line}";
}

/// <summary>
/// A tolerant tokenizer for the brace languages (TypeScript, Java) and for Python. It never throws: unterminated
/// strings and comments end at the end of the file (or line), unknown characters become punctuation. String contents
/// are kept (Python annotations may be strings); comments are dropped except doc comments, which attach to the next token.
/// </summary>
internal static partial class Lexer
{
    private static readonly string[] MultiPunct = ["...", "=>", "?.", "::", "->", "**"];

    /// <summary>Tokens of TypeScript / Java. <paramref name="typeScript"/> enables template literals, regex literals and
    /// <c>#private</c> names; Java gets text blocks.</summary>
    public static List<Tok> CLike(string src, bool typeScript)
    {
        var toks = new List<Tok>();
        var i = 0;
        var line = 1;
        string? doc = null;
        var n = src.Length;
        while (i < n)
        {
            var c = src[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < n && src[i + 1] == '/')
            {
                while (i < n && src[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < n && src[i + 1] == '*')
            {
                var end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? n : end + 2;
                var isDoc = i + 2 < n && src[i + 2] == '*' && !(i + 3 < n && src[i + 3] == '/');
                if (isDoc) doc = CleanDocComment(src[(i + 3)..Math.Max(i + 3, end < 0 ? n : end)]);
                line += Count(src, i, stop, '\n');
                i = stop;
                continue;
            }
            var startLine = line;
            if (c is '"' or '\'')
            {
                if (!typeScript && c == '"' && i + 2 < n && src[i + 1] == '"' && src[i + 2] == '"')
                {
                    var end = src.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                    var stop = end < 0 ? n : end + 3;
                    line += Count(src, i, stop, '\n');
                    toks.Add(new Tok(TokKind.String, src[(i + 3)..Math.Max(i + 3, end < 0 ? n : end)], startLine, Take(ref doc)));
                    i = stop;
                    continue;
                }
                var (text, next) = QuotedLine(src, i, c);
                toks.Add(new Tok(TokKind.String, text, startLine, Take(ref doc)));
                i = next;
                continue;
            }
            if (typeScript && c == '`')
            {
                var next = SkipTemplate(src, i + 1);
                line += Count(src, i, next, '\n');
                toks.Add(new Tok(TokKind.String, "", startLine, Take(ref doc)));
                i = next;
                continue;
            }
            if (typeScript && c == '/' && RegexAllowed(toks))
            {
                var next = SkipRegex(src, i);
                if (next > 0)
                {
                    toks.Add(new Tok(TokKind.String, "", startLine, Take(ref doc)));
                    i = next;
                    continue;
                }
            }
            if (IsIdentStart(c) || (typeScript && c == '#' && i + 1 < n && IsIdentStart(src[i + 1])))
            {
                var s = i;
                i++;
                while (i < n && IsIdentPart(src[i])) i++;
                toks.Add(new Tok(TokKind.Ident, src[s..i], startLine, Take(ref doc)));
                continue;
            }
            if (char.IsDigit(c))
            {
                var s = i;
                while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] is '_' or '.')) i++;
                toks.Add(new Tok(TokKind.Number, src[s..i], startLine, Take(ref doc)));
                continue;
            }
            var multi = MultiPunct.FirstOrDefault(p => string.CompareOrdinal(src, i, p, 0, p.Length) == 0);
            var punct = multi ?? c.ToString();
            toks.Add(new Tok(TokKind.Punct, punct, startLine, Take(ref doc)));
            i += punct.Length;
        }
        return toks;
    }

    /// <summary>
    /// Tokens of Python, with a <see cref="TokKind.Newline"/> token at the end of every logical line (not inside brackets
    /// or after a backslash). The first token of each logical line carries its indentation column.
    /// </summary>
    public static List<Tok> Python(string src)
    {
        var toks = new List<Tok>();
        var i = 0;
        var line = 1;
        var n = src.Length;
        var depth = 0;
        var atLineStart = true;
        var indent = 0;
        while (i < n)
        {
            var c = src[i];
            if (atLineStart)
            {
                // Measure indentation; blank and comment-only lines do not start a logical line.
                indent = 0;
                while (i < n && src[i] is ' ' or '\t' or '\f') { indent += src[i] == '\t' ? 8 - indent % 8 : 1; i++; }
                if (i >= n) break;
                if (src[i] is '\n' or '\r' or '#')
                {
                    while (i < n && src[i] != '\n') i++;
                    if (i < n) { line++; i++; }
                    continue;
                }
                atLineStart = false;
                c = src[i];
            }
            if (c == '\n')
            {
                line++;
                i++;
                // An unclosed bracket must not swallow the rest of the file: a following def / class / decorator line
                // ends the broken logical line.
                if (depth > 0 && StartsDeclaration(src, i)) depth = 0;
                if (depth == 0)
                {
                    if (toks.Count > 0 && toks[^1].Kind != TokKind.Newline) toks.Add(new Tok(TokKind.Newline, "\n", line - 1));
                    atLineStart = true;
                }
                continue;
            }
            if (c == '\\' && i + 1 < n && (src[i + 1] == '\n' || (src[i + 1] == '\r' && i + 2 < n && src[i + 2] == '\n')))
            {
                i += src[i + 1] == '\r' ? 3 : 2;
                line++;
                continue;
            }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '#') { while (i < n && src[i] != '\n') i++; continue; }
            var first = toks.Count == 0 || toks[^1].Kind == TokKind.Newline;
            var ind = first ? indent : -1;
            var startLine = line;

            // String prefixes (r, b, f, u and combinations) followed by a quote.
            var p = i;
            while (p < n && p - i < 3 && src[p] is 'r' or 'R' or 'b' or 'B' or 'f' or 'F' or 'u' or 'U') p++;
            if (p < n && src[p] is '"' or '\'')
            {
                var q = src[p];
                if (p + 2 < n && src[p + 1] == q && src[p + 2] == q)
                {
                    var delim = new string(q, 3);
                    var end = IndexOfUnescaped(src, delim, p + 3);
                    var stop = end < 0 ? n : end + 3;
                    line += Count(src, i, stop, '\n');
                    toks.Add(new Tok(TokKind.String, src[(p + 3)..Math.Max(p + 3, end < 0 ? n : end)], startLine, null, ind));
                    i = stop;
                    continue;
                }
                var (text, next) = QuotedLine(src, p, q);
                toks.Add(new Tok(TokKind.String, text, startLine, null, ind));
                i = next;
                continue;
            }
            if (IsIdentStart(c) && c != '$')
            {
                var s = i;
                i++;
                while (i < n && IsIdentPart(src[i]) && src[i] != '$') i++;
                toks.Add(new Tok(TokKind.Ident, src[s..i], startLine, null, ind));
                continue;
            }
            if (char.IsDigit(c))
            {
                var s = i;
                while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] is '_' or '.')) i++;
                toks.Add(new Tok(TokKind.Number, src[s..i], startLine, null, ind));
                continue;
            }
            var multi = MultiPunct.FirstOrDefault(m => string.CompareOrdinal(src, i, m, 0, m.Length) == 0);
            var punct = multi ?? c.ToString();
            if (punct is "(" or "[" or "{") depth++;
            else if (punct is ")" or "]" or "}") depth = Math.Max(0, depth - 1);
            toks.Add(new Tok(TokKind.Punct, punct, startLine, null, ind));
            i += punct.Length;
        }
        if (toks.Count > 0 && toks[^1].Kind != TokKind.Newline) toks.Add(new Tok(TokKind.Newline, "\n", line));
        return toks;
    }

    private static bool StartsDeclaration(string src, int i)
    {
        while (i < src.Length && src[i] is ' ' or '\t') i++;
        return string.CompareOrdinal(src, i, "def ", 0, 4) == 0 || string.CompareOrdinal(src, i, "class ", 0, 6) == 0
            || string.CompareOrdinal(src, i, "async def ", 0, 10) == 0 || (i < src.Length && src[i] == '@');
    }

    private static string? Take(ref string? doc)
    {
        var d = doc;
        doc = null;
        return d;
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c is '_' or '$';
    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    private static int Count(string s, int from, int to, char c)
    {
        var k = 0;
        for (var i = from; i < to && i < s.Length; i++) if (s[i] == c) k++;
        return k;
    }

    /// <summary>A single-line quoted string starting at <paramref name="i"/>; ends at the closing quote or the line end.</summary>
    private static (string Text, int Next) QuotedLine(string src, int i, char q)
    {
        var sb = new StringBuilder();
        var j = i + 1;
        while (j < src.Length && src[j] != q && src[j] != '\n')
        {
            if (src[j] == '\\' && j + 1 < src.Length && src[j + 1] != '\n') { sb.Append(src[j + 1]); j += 2; continue; }
            sb.Append(src[j]);
            j++;
        }
        return (sb.ToString(), j < src.Length && src[j] == q ? j + 1 : j);
    }

    private static int IndexOfUnescaped(string s, string delim, int from)
    {
        for (var j = from; j <= s.Length - delim.Length; j++)
        {
            if (s[j] == '\\') { j++; continue; }
            if (string.CompareOrdinal(s, j, delim, 0, delim.Length) == 0) return j;
        }
        return -1;
    }

    /// <summary>Skips a template literal body (after the opening backtick), including <c>${…}</c> substitutions.</summary>
    private static int SkipTemplate(string src, int i, int guard = 0)
    {
        while (i < src.Length)
        {
            var c = src[i];
            if (c == '\\') { i += 2; continue; }
            if (c == '`') return i + 1;
            if (c == '$' && i + 1 < src.Length && src[i + 1] == '{' && guard < 32)
            {
                i += 2;
                var depth = 1;
                while (i < src.Length && depth > 0)
                {
                    var d = src[i];
                    if (d == '{') depth++;
                    else if (d == '}') depth--;
                    else if (d == '`') { i = SkipTemplate(src, i + 1, guard + 1); continue; }
                    else if (d is '"' or '\'') { i = QuotedLine(src, i, d).Next; continue; }
                    i++;
                }
                continue;
            }
            i++;
        }
        return src.Length;
    }

    private static readonly HashSet<string> RegexKeywords = new(StringComparer.Ordinal) { "return", "typeof", "case", "in", "of", "new", "delete", "void", "throw", "instanceof", "yield", "await", "else", "do" };

    /// <summary>A '/' starts a regex literal where an expression is expected (after an operator, '(' or a keyword).</summary>
    private static bool RegexAllowed(List<Tok> toks)
    {
        if (toks.Count == 0) return true;
        var last = toks[^1];
        return last.Kind switch
        {
            TokKind.Ident => RegexKeywords.Contains(last.Text),
            TokKind.Number or TokKind.String => false,
            _ => last.Text is not (")" or "]" or "}"),
        };
    }

    /// <summary>The end of a regex literal starting at '/', or -1 when the line ends first (then it was a division).</summary>
    private static int SkipRegex(string src, int i)
    {
        var j = i + 1;
        var inClass = false;
        while (j < src.Length && src[j] != '\n')
        {
            var c = src[j];
            if (c == '\\') { j += 2; continue; }
            if (c == '[') inClass = true;
            else if (c == ']') inClass = false;
            else if (c == '/' && !inClass)
            {
                j++;
                while (j < src.Length && char.IsLetter(src[j])) j++;
                return j;
            }
            j++;
        }
        return -1;
    }

    /// <summary>JSDoc / Javadoc body: leading '*' removed, text up to the first block tag, inline tags reduced to their
    /// text, whitespace collapsed. Null when empty.</summary>
    public static string? CleanDocComment(string body)
    {
        var sb = new StringBuilder();
        foreach (var raw in body.Split('\n'))
        {
            var l = raw.Trim();
            while (l.StartsWith('*')) l = l[1..];
            l = l.Trim();
            if (l.StartsWith('@')) break;
            sb.Append(l).Append(' ');
        }
        var text = sb.ToString();
        // A block tag inside a one-line comment ("/** Text. @see X */") ends the description too.
        var depth = 0;
        for (var k = 0; k < text.Length; k++)
        {
            if (text[k] == '{') depth++;
            else if (text[k] == '}') depth = Math.Max(0, depth - 1);
            else if (depth == 0 && text[k] == '@' && (k == 0 || char.IsWhiteSpace(text[k - 1])) && k + 1 < text.Length && char.IsLetter(text[k + 1]))
            {
                text = text[..k];
                break;
            }
        }
        // {@link Foo#bar label} -> "Foo#bar label"; {@code x} -> "x".
        var outText = new StringBuilder();
        for (var k = 0; k < text.Length; k++)
        {
            if (text[k] == '{' && k + 1 < text.Length && text[k + 1] == '@')
            {
                var close = text.IndexOf('}', k);
                if (close > 0)
                {
                    var inner = text[(k + 2)..close];
                    var sp = inner.IndexOf(' ');
                    outText.Append(sp < 0 ? "" : inner[(sp + 1)..]);
                    k = close;
                    continue;
                }
            }
            outText.Append(text[k]);
        }
        var plain = HtmlTag().Replace(outText.ToString(), " ");
        var collapsed = string.Join(" ", plain.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length == 0 ? null : collapsed;
    }

    /// <summary>Javadoc's common HTML tags (not generics such as &lt;T&gt;).</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"</?(?:p|br|b|i|em|strong|code|pre|ul|ol|li|a|tt|h[1-6]|blockquote|table|tr|td|th|div|span|sup|sub)(?:\s[^<>]*)?/?>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex HtmlTag();

    /// <summary>Python docstring: the first paragraph, whitespace collapsed.</summary>
    public static string? CleanDocString(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        var sb = new StringBuilder();
        var started = false;
        foreach (var l in lines)
        {
            if (l.Trim().Length == 0)
            {
                if (started) break;
                continue;
            }
            started = true;
            sb.Append(l.Trim()).Append(' ');
        }
        var collapsed = string.Join(" ", sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length == 0 ? null : collapsed;
    }
}
