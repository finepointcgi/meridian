using System.Globalization;

namespace Meridian.Core.Analysis;

/// <summary>
/// Small expression language shared by guards, action bodies, effects and constraint equations.
/// Numbers are doubles; booleans are 1/0. Supports + - * / % ^, comparisons, &amp;&amp; || !, functions
/// (sqrt abs sin cos tan asin acos atan exp ln log log10 min max pow floor ceil round sign), constants pi e true false,
/// dotted identifiers (engine.maxPower) and statements "x = expr; y += expr".
/// </summary>
public static class Expressions
{
    public sealed class ExpressionException(string message) : Exception(message);

    public static double Evaluate(string expression, IReadOnlyDictionary<string, double> vars) =>
        new Parser(Tokenize(expression), vars).ParseAll();

    public static bool IsTrue(string guard, IReadOnlyDictionary<string, double> vars)
    {
        var g = guard.Trim();
        if (g.Length == 0) return true;
        if (g.Equals("else", StringComparison.OrdinalIgnoreCase)) return false;
        return Math.Abs(Evaluate(g, vars)) > 1e-12;
    }

    /// <summary>Executes "a = 1; b += a * 2" style statements, updating <paramref name="vars"/>. Returns the assigned names.</summary>
    public static IReadOnlyList<string> Execute(string statements, IDictionary<string, double> vars)
    {
        var assigned = new List<string>();
        foreach (var raw in statements.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var op = FindAssignment(raw, out var name, out var rhs);
            if (op == null) { Evaluate(raw, new Dictionary<string, double>(vars)); continue; }   // bare expression: just validate
            var value = Evaluate(rhs, new Dictionary<string, double>(vars));
            vars.TryGetValue(name, out var old);
            vars[name] = op switch { "+=" => old + value, "-=" => old - value, "*=" => old * value, "/=" => old / value, _ => value };
            assigned.Add(name);
        }
        return assigned;
    }

    /// <summary>Names assigned by a statement list (left-hand sides).</summary>
    public static IReadOnlyList<string> AssignedNames(string statements) =>
        statements.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(st => FindAssignment(st, out var n, out _) != null ? n : null).OfType<string>().Distinct().ToList();

    private static string? FindAssignment(string s, out string name, out string rhs)
    {
        name = rhs = "";
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '=') continue;
            var prev = i > 0 ? s[i - 1] : '\0';
            var next = i + 1 < s.Length ? s[i + 1] : '\0';
            if (next == '=' || prev is '=' or '!' or '<' or '>') { if (next == '=') i++; continue; }
            var op = prev is '+' or '-' or '*' or '/' ? prev + "=" : "=";
            var lhs = s[..(op.Length == 2 ? i - 1 : i)].Trim();
            if (lhs.Length == 0 || !lhs.All(c => char.IsLetterOrDigit(c) || c is '_' or '.')) return null;
            name = lhs;
            rhs = s[(i + 1)..].Trim();
            return op;
        }
        return null;
    }

    /// <summary>Identifiers referenced by an expression (excluding function names and constants).</summary>
    public static IReadOnlySet<string> Variables(string expression)
    {
        var toks = Tokenize(expression);
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < toks.Count; i++)
            if (toks[i].Kind == 'i' && !(i + 1 < toks.Count && toks[i + 1].Text == "(") && !Constants.ContainsKey(toks[i].Text.ToLowerInvariant()))
                set.Add(toks[i].Text);
        return set;
    }

    private static readonly Dictionary<string, double> Constants = new()
    {
        ["pi"] = Math.PI, ["e"] = Math.E, ["true"] = 1, ["false"] = 0,
    };

    private readonly record struct Tok(char Kind, string Text); // n number, i identifier, o operator, ( ) ,

    private static List<Tok> Tokenize(string s)
    {
        var list = new List<Tok>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (char.IsDigit(c) || c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1]))
            {
                var st = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                if (i < s.Length && s[i] is 'e' or 'E' && i + 1 < s.Length && (char.IsDigit(s[i + 1]) || s[i + 1] is '-' or '+'))
                {
                    i += 2;
                    while (i < s.Length && char.IsDigit(s[i])) i++;
                }
                list.Add(new Tok('n', s[st..i]));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var st = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '.')) i++;
                list.Add(new Tok('i', s[st..i]));
                continue;
            }
            var two = i + 1 < s.Length ? s.Substring(i, 2) : "";
            if (two is "<=" or ">=" or "==" or "!=" or "&&" or "||") { list.Add(new Tok('o', two)); i += 2; continue; }
            if ("+-*/%^<>!".Contains(c)) { list.Add(new Tok('o', c.ToString())); i++; continue; }
            if (c is '(' or ')' or ',') { list.Add(new Tok(c, c.ToString())); i++; continue; }
            if (c == '=') { list.Add(new Tok('o', "==")); i++; continue; }   // lone '=' in a guard reads as equality
            throw new ExpressionException($"Unexpected character '{c}' in '{s}'");
        }
        return list;
    }

    private sealed class Parser(List<Tok> toks, IReadOnlyDictionary<string, double> vars)
    {
        private int _i;
        private Tok? Cur => _i < toks.Count ? toks[_i] : null;
        private bool IsOp(string op) => Cur is { Kind: 'o' } t && t.Text == op;

        public double ParseAll()
        {
            if (toks.Count == 0) throw new ExpressionException("Empty expression");
            var v = Or();
            if (_i < toks.Count) throw new ExpressionException($"Unexpected '{toks[_i].Text}'");
            return v;
        }

        private double Or() { var v = And(); while (IsOp("||")) { _i++; var r = And(); v = (v != 0 || r != 0) ? 1 : 0; } return v; }
        private double And() { var v = Cmp(); while (IsOp("&&")) { _i++; var r = Cmp(); v = (v != 0 && r != 0) ? 1 : 0; } return v; }

        private double Cmp()
        {
            var v = Add();
            while (Cur is { Kind: 'o' } t && t.Text is "<" or ">" or "<=" or ">=" or "==" or "!=")
            {
                _i++;
                var r = Add();
                v = t.Text switch
                {
                    "<" => v < r, ">" => v > r, "<=" => v <= r, ">=" => v >= r, "==" => Math.Abs(v - r) < 1e-9, _ => Math.Abs(v - r) >= 1e-9,
                } ? 1 : 0;
            }
            return v;
        }

        private double Add()
        {
            var v = Mul();
            while (IsOp("+") || IsOp("-")) { var op = toks[_i++].Text; var r = Mul(); v = op == "+" ? v + r : v - r; }
            return v;
        }

        private double Mul()
        {
            var v = Unary();
            while (IsOp("*") || IsOp("/") || IsOp("%"))
            {
                var op = toks[_i++].Text;
                var r = Unary();
                v = op switch { "*" => v * r, "/" => v / r, _ => v % r };
            }
            return v;
        }

        private double Unary()
        {
            if (IsOp("-")) { _i++; return -Unary(); }
            if (IsOp("+")) { _i++; return Unary(); }
            if (IsOp("!")) { _i++; return Unary() == 0 ? 1 : 0; }
            return Pow();
        }

        private double Pow()
        {
            var b = Primary();
            if (IsOp("^")) { _i++; return Math.Pow(b, Unary()); }
            return b;
        }

        private double Primary()
        {
            var t = Cur ?? throw new ExpressionException("Unexpected end of expression");
            _i++;
            switch (t.Kind)
            {
                case 'n': return double.Parse(t.Text, CultureInfo.InvariantCulture);
                case '(':
                {
                    var v = Or();
                    if (Cur?.Kind != ')') throw new ExpressionException("Missing ')'");
                    _i++;
                    return v;
                }
                case 'i':
                {
                    if (Cur?.Kind == '(')
                    {
                        _i++;
                        var args = new List<double>();
                        if (Cur?.Kind != ')')
                        {
                            args.Add(Or());
                            while (Cur?.Kind == ',') { _i++; args.Add(Or()); }
                        }
                        if (Cur?.Kind != ')') throw new ExpressionException($"Missing ')' after {t.Text}(");
                        _i++;
                        return Call(t.Text.ToLowerInvariant(), args);
                    }
                    if (vars.TryGetValue(t.Text, out var v)) return v;
                    if (Constants.TryGetValue(t.Text.ToLowerInvariant(), out var k)) return k;
                    // Last segment of a dotted name ("engine.maxPower" → "maxPower") as a convenience.
                    var dot = t.Text.LastIndexOf('.');
                    if (dot > 0 && vars.TryGetValue(t.Text[(dot + 1)..], out var tail)) return tail;
                    throw new ExpressionException($"Unknown variable '{t.Text}'");
                }
                default: throw new ExpressionException($"Unexpected '{t.Text}'");
            }
        }

        private static double Call(string f, List<double> a)
        {
            double A(int i) => i < a.Count ? a[i] : throw new ExpressionException($"{f} needs {i + 1} argument(s)");
            return f switch
            {
                "sqrt" => Math.Sqrt(A(0)), "abs" => Math.Abs(A(0)), "sin" => Math.Sin(A(0)), "cos" => Math.Cos(A(0)), "tan" => Math.Tan(A(0)),
                "asin" => Math.Asin(A(0)), "acos" => Math.Acos(A(0)), "atan" => Math.Atan(A(0)), "exp" => Math.Exp(A(0)),
                "ln" or "log" => Math.Log(A(0)), "log10" => Math.Log10(A(0)), "pow" => Math.Pow(A(0), A(1)),
                "min" => a.Count == 0 ? throw new ExpressionException("min needs arguments") : a.Min(),
                "max" => a.Count == 0 ? throw new ExpressionException("max needs arguments") : a.Max(),
                "floor" => Math.Floor(A(0)), "ceil" => Math.Ceiling(A(0)), "round" => Math.Round(A(0)), "sign" => Math.Sign(A(0)),
                _ => throw new ExpressionException($"Unknown function '{f}'"),
            };
        }
    }
}
