using System.Text;
using System.Text.RegularExpressions;

namespace Meridian.Serialization.Database;

public enum SqlTokenKind { Word, Quoted, String, Number, Symbol, BatchEnd }

/// <summary>
/// One lexical token. <see cref="Text"/> is the identifier / literal content (quotes removed); <see cref="Raw"/> the source
/// text; <see cref="Offset"/> where it starts in the (batch-marked) script, -1 when unknown.
/// </summary>
public readonly record struct SqlToken(SqlTokenKind Kind, string Text, string Raw, int Line, int Offset = -1)
{
    /// <summary>Offset just after the token in the script.</summary>
    public int End => Offset + Raw.Length;

    public bool Is(string word) => Kind == SqlTokenKind.Word && Text.Equals(word, StringComparison.OrdinalIgnoreCase);
    public bool IsSymbol(string s) => Kind == SqlTokenKind.Symbol && Text == s;
    public bool IsName => Kind is SqlTokenKind.Word or SqlTokenKind.Quoted;
    public override string ToString() => Raw;
}

/// <summary>
/// A forgiving SQL lexer for PostgreSQL, SQL Server (T-SQL), MySQL / MariaDB, SQLite and Oracle scripts: quoted identifiers
/// ("x", `x`, [x]), strings with doubled quotes and N'…' / E'…' prefixes, dollar-quoted bodies ($$…$$, $tag$…$tag$), line and
/// nested block comments. Batch separators (T-SQL GO, Oracle "/", MySQL DELIMITER) become <see cref="SqlTokenKind.BatchEnd"/>.
/// </summary>
public static partial class SqlTokenizer
{
    [GeneratedRegex(@"^[ \t]*GO(?:[ \t]+\d+)?[ \t]*;?[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex GoLine();

    [GeneratedRegex(@"^[ \t]*/[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex SlashLine();

    private const char BatchMark = '\u0001';

    /// <summary>Replaces batch separator lines with a marker the lexer turns into <see cref="SqlTokenKind.BatchEnd"/>.</summary>
    public static string MarkBatches(string sql)
    {
        sql = sql.Replace("\r\n", "\n").Replace('\r', '\n');
        sql = GoLine().Replace(sql, BatchMark.ToString());
        sql = SlashLine().Replace(sql, BatchMark.ToString());
        if (sql.Contains("DELIMITER", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new StringBuilder();
            var delimiter = ";";
            foreach (var line in sql.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("DELIMITER ", StringComparison.OrdinalIgnoreCase))
                {
                    delimiter = trimmed[10..].Trim();
                    if (delimiter.Length == 0) delimiter = ";";
                    continue;
                }
                if (delimiter != ";" && trimmed.EndsWith(delimiter, StringComparison.Ordinal))
                    sb.Append(line[..line.LastIndexOf(delimiter, StringComparison.Ordinal)]).Append('\n').Append(BatchMark).Append('\n');
                else sb.Append(line).Append('\n');
            }
            sql = sb.ToString();
        }
        return sql;
    }

    public static List<SqlToken> Tokenize(string sql, bool hashComments = false) => Tokenize(sql, hashComments, out _);

    /// <summary>Tokenizes; <paramref name="marked"/> is the script the token offsets refer to (batch separators replaced).</summary>
    public static List<SqlToken> Tokenize(string sql, bool hashComments, out string marked)
    {
        sql = MarkBatches(sql);
        marked = sql;
        var tokens = new List<SqlToken>();
        var i = 0;
        var line = 1;
        var n = sql.Length;
        while (i < n)
        {
            var c = sql[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == BatchMark) { tokens.Add(new SqlToken(SqlTokenKind.BatchEnd, "", "GO", line, i)); i++; continue; }
            if (c == '-' && i + 1 < n && sql[i + 1] == '-' || hashComments && c == '#')
            {
                while (i < n && sql[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                var depth = 0;
                while (i < n)
                {
                    if (sql[i] == '/' && i + 1 < n && sql[i + 1] == '*') { depth++; i += 2; continue; }
                    if (sql[i] == '*' && i + 1 < n && sql[i + 1] == '/') { depth--; i += 2; if (depth == 0) break; continue; }
                    if (sql[i] == '\n') line++;
                    i++;
                }
                continue;
            }
            var start = i;
            // N'…', E'…', B'…', X'…' prefixed strings
            if (c is 'N' or 'n' or 'E' or 'e' or 'B' or 'b' or 'X' or 'x' && i + 1 < n && sql[i + 1] == '\'' && (i == 0 || !IsIdentChar(sql[i - 1])))
            {
                i++;
                var (text, end) = ReadQuoted(sql, i, '\'', ref line, backslash: c is 'E' or 'e');
                tokens.Add(new SqlToken(SqlTokenKind.String, text, sql[start..end], line, start));
                i = end;
                continue;
            }
            if (c == '\'')
            {
                var (text, end) = ReadQuoted(sql, i, '\'', ref line, backslash: false);
                tokens.Add(new SqlToken(SqlTokenKind.String, text, sql[start..end], line, start));
                i = end;
                continue;
            }
            if (c is '"' or '`')
            {
                var (text, end) = ReadQuoted(sql, i, c, ref line, backslash: false);
                tokens.Add(new SqlToken(SqlTokenKind.Quoted, text, sql[start..end], line, start));
                i = end;
                continue;
            }
            if (c == '[')
            {
                var end = sql.IndexOf(']', i + 1);
                while (end > 0 && end + 1 < n && sql[end + 1] == ']') end = sql.IndexOf(']', end + 2);   // ]] escapes ]
                if (end < 0) end = n - 1;
                var text = sql[(i + 1)..end].Replace("]]", "]");
                // "[]" after a type (int[]) is an array suffix, not an identifier.
                if (text.Length == 0 || text.All(char.IsDigit)) tokens.Add(new SqlToken(SqlTokenKind.Symbol, "[]", sql[i..(end + 1)], line, i));
                else tokens.Add(new SqlToken(SqlTokenKind.Quoted, text, sql[i..(end + 1)], line, start));
                i = end + 1;
                continue;
            }
            if (c == '$' && i + 1 < n && (sql[i + 1] == '$' || char.IsLetter(sql[i + 1]) || sql[i + 1] == '_'))
            {
                // Dollar-quoted body: $$…$$ or $tag$…$tag$
                var close = sql.IndexOf('$', i + 1);
                var tag = close > 0 ? sql[i..(close + 1)] : "$$";
                if (close > 0 && tag.Skip(1).Take(tag.Length - 2).All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                {
                    var end = sql.IndexOf(tag, close + 1, StringComparison.Ordinal);
                    if (end < 0) end = n - tag.Length;
                    var body = sql[(close + 1)..Math.Max(close + 1, end)];
                    line += body.Count(ch => ch == '\n');
                    tokens.Add(new SqlToken(SqlTokenKind.String, body, sql[i..Math.Min(n, end + tag.Length)], line, start));
                    i = Math.Min(n, end + tag.Length);
                    continue;
                }
            }
            if (char.IsDigit(c) || c == '.' && i + 1 < n && char.IsDigit(sql[i + 1]))
            {
                while (i < n && (char.IsDigit(sql[i]) || sql[i] == '.' || (sql[i] is 'e' or 'E') && i + 1 < n && (char.IsDigit(sql[i + 1]) || sql[i + 1] is '+' or '-'))) i++;
                tokens.Add(new SqlToken(SqlTokenKind.Number, sql[start..i], sql[start..i], line, start));
                continue;
            }
            if (IsIdentStart(c))
            {
                while (i < n && IsIdentChar(sql[i])) i++;
                tokens.Add(new SqlToken(SqlTokenKind.Word, sql[start..i], sql[start..i], line, start));
                continue;
            }
            // Multi-character operators.
            var two = i + 1 < n ? sql.Substring(i, 2) : "";
            if (two is "::" or "<=" or ">=" or "<>" or "!=" or "||" or "=>" or ":=")
            {
                tokens.Add(new SqlToken(SqlTokenKind.Symbol, two, two, line, i));
                i += 2;
                continue;
            }
            tokens.Add(new SqlToken(SqlTokenKind.Symbol, c.ToString(), c.ToString(), line, i));
            i++;
        }
        return tokens;
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c is '_' or '@' or '#';
    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '@' or '#';

    private static (string Text, int End) ReadQuoted(string sql, int i, char q, ref int line, bool backslash)
    {
        var sb = new StringBuilder();
        i++;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (backslash && c == '\\' && i + 1 < sql.Length) { sb.Append(sql[i + 1]); i += 2; continue; }
            if (c == q)
            {
                if (i + 1 < sql.Length && sql[i + 1] == q) { sb.Append(q); i += 2; continue; }
                return (sb.ToString(), i + 1);
            }
            if (c == '\n') line++;
            sb.Append(c);
            i++;
        }
        return (sb.ToString(), sql.Length);
    }

    /// <summary>Source text of a token range, re-spaced (for definitions and expressions).</summary>
    public static string Text(IReadOnlyList<SqlToken> tokens, int from, int to)
    {
        var sb = new StringBuilder();
        for (var k = from; k < to && k < tokens.Count; k++)
        {
            var t = tokens[k];
            if (t.Kind == SqlTokenKind.BatchEnd) continue;
            if (sb.Length > 0 && NeedsSpace(tokens[k - 1], t)) sb.Append(' ');
            sb.Append(t.Raw);
        }
        return sb.ToString();
    }

    private static bool NeedsSpace(SqlToken prev, SqlToken cur)
    {
        if (cur.Kind == SqlTokenKind.Symbol && cur.Text is "," or ")" or "." or "::" or "[]" or ";") return false;
        if (prev.Kind == SqlTokenKind.Symbol && prev.Text is "(" or "." or "::") return false;
        if (cur.Kind == SqlTokenKind.Symbol && cur.Text == "(" && prev.IsName) return false;
        return true;
    }
}
