namespace Meridian.Core.Analysis;

/// <summary>
/// Boolean feature expressions used as presence conditions: <c>GPS and not Basic</c>, <c>(A || B) &amp;&amp; !C</c>,
/// <c>Premium implies Sunroof</c>, <c>"Lane Keeping" xor Cruise</c>. Operators by increasing precedence:
/// implies (=&gt;, -&gt;), or (||, |), xor (^), and (&amp;&amp;, &amp;), not (!). Names may be quoted to include spaces.
/// </summary>
public static class FeatureExpression
{
    public sealed class FeatureExpressionException(string message) : Exception(message);

    /// <summary>Evaluates the expression; an empty expression is true.</summary>
    public static bool Evaluate(string? expression, Func<string, bool> isSelected)
    {
        if (string.IsNullOrWhiteSpace(expression)) return true;
        var p = new Parser(Tokenize(expression), isSelected);
        return p.ParseAll();
    }

    /// <summary>Feature names referenced by the expression.</summary>
    public static IReadOnlyList<string> Names(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return [];
        return Tokenize(expression).Where(t => t.Kind == 'i').Select(t => t.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Null when the expression is well formed, otherwise the syntax error.</summary>
    public static string? Check(string? expression)
    {
        try { Evaluate(expression, _ => true); return null; }
        catch (FeatureExpressionException ex) { return ex.Message; }
    }

    private readonly record struct Tok(char Kind, string Text);   // i identifier, o operator, ( )

    private static List<Tok> Tokenize(string s)
    {
        var list = new List<Tok>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c is '"' or '\'' or '`')
            {
                var end = s.IndexOf(c, i + 1);
                if (end < 0) throw new FeatureExpressionException($"Unterminated quoted name in '{s}'");
                list.Add(new Tok('i', s[(i + 1)..end].Trim()));
                i = end + 1;
                continue;
            }
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                var st = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '.' || s[i] == '-' && i + 1 < s.Length && s[i + 1] != '>')) i++;
                var word = s[st..i];
                switch (word.ToLowerInvariant())
                {
                    case "and": list.Add(new Tok('o', "&")); break;
                    case "or": list.Add(new Tok('o', "|")); break;
                    case "not": list.Add(new Tok('o', "!")); break;
                    case "xor": list.Add(new Tok('o', "^")); break;
                    case "implies": list.Add(new Tok('o', "=>")); break;
                    case "true": list.Add(new Tok('t', "1")); break;
                    case "false": list.Add(new Tok('t', "0")); break;
                    default: list.Add(new Tok('i', word)); break;
                }
                continue;
            }
            var two = i + 1 < s.Length ? s.Substring(i, 2) : "";
            if (two is "&&") { list.Add(new Tok('o', "&")); i += 2; continue; }
            if (two is "||") { list.Add(new Tok('o', "|")); i += 2; continue; }
            if (two is "=>" or "->") { list.Add(new Tok('o', "=>")); i += 2; continue; }
            if (c is '&' or '|' or '^' or '!' or '~') { list.Add(new Tok('o', c == '~' ? "!" : c.ToString())); i++; continue; }
            if (c is '(' or ')') { list.Add(new Tok(c, c.ToString())); i++; continue; }
            throw new FeatureExpressionException($"Unexpected character '{c}' in '{s}'");
        }
        return list;
    }

    private sealed class Parser(List<Tok> toks, Func<string, bool> isSelected)
    {
        private int _i;
        private Tok? Cur => _i < toks.Count ? toks[_i] : null;
        private bool IsOp(string op) => Cur is { Kind: 'o' } t && t.Text == op;

        public bool ParseAll()
        {
            if (toks.Count == 0) throw new FeatureExpressionException("Empty expression");
            var v = Implies();
            if (Cur != null) throw new FeatureExpressionException($"Unexpected '{Cur.Value.Text}'");
            return v;
        }

        private bool Implies()
        {
            var l = Or();
            while (IsOp("=>")) { _i++; var r = Or(); l = !l || r; }
            return l;
        }

        private bool Or()
        {
            var l = Xor();
            while (IsOp("|")) { _i++; var r = Xor(); l = l || r; }
            return l;
        }

        private bool Xor()
        {
            var l = And();
            while (IsOp("^")) { _i++; var r = And(); l ^= r; }
            return l;
        }

        private bool And()
        {
            var l = Not();
            while (IsOp("&")) { _i++; var r = Not(); l = l && r; }
            return l;
        }

        private bool Not()
        {
            if (IsOp("!")) { _i++; return !Not(); }
            return Primary();
        }

        private bool Primary()
        {
            var t = Cur ?? throw new FeatureExpressionException("Unexpected end of expression");
            _i++;
            switch (t.Kind)
            {
                case 'i': return isSelected(t.Text);
                case 't': return t.Text == "1";
                case '(':
                    var v = Implies();
                    if (Cur is not { Kind: ')' }) throw new FeatureExpressionException("Missing ')'");
                    _i++;
                    return v;
                default: throw new FeatureExpressionException($"Unexpected '{t.Text}'");
            }
        }
    }
}
