using System.Globalization;
using System.Text.RegularExpressions;

namespace Meridian.Core.Analysis;

/// <summary>Which analysis computes the values a criterion talks about.</summary>
public enum AnalysisKind { Parametric, MonteCarlo, Timed, CoSim }

/// <summary>A value an analysis produced, by name: one number, a set of Monte Carlo samples or a time series.</summary>
public abstract record Measure(string? Unit);
public sealed record ScalarMeasure(double Value, string? Unit) : Measure(Unit);
public sealed record SampleMeasure(IReadOnlyList<double> Values, string? Unit) : Measure(Unit);
public sealed record SeriesMeasure(IReadOnlyList<double> Time, IReadOnlyList<double> Values, string? Unit) : Measure(Unit);

/// <summary>Looks up analysis outputs by name for criterion evaluation.</summary>
public interface IMeasureSource
{
    /// <summary>The measure called <paramref name="name"/>, or null (with <paramref name="problem"/> saying why).</summary>
    Measure? Find(string name, out string? problem);
}

public enum Verdict { Pass, Fail, Inconclusive }

/// <summary>The outcome of evaluating one criterion.</summary>
public sealed record CriterionResult(
    Verdict Verdict, string Value, string Bound, string Margin, double? RelativeMargin, string Unit, string Message, IReadOnlyList<string> Notes)
{
    public static CriterionResult Inconclusive(string message, IReadOnlyList<string>? notes = null) => new(Verdict.Inconclusive, "", "", "", null, "", message, notes ?? []);
}

/// <summary>
/// A machine-checkable requirement criterion:
/// <code>
///   [analysis [(target)] :] expression comparator expression
/// </code>
/// <list type="bullet">
/// <item>analysis: <c>parametric</c> (default; also <c>param</c>, <c>constraint</c>), <c>montecarlo</c> (<c>mc</c>),
/// <c>timed</c> (<c>sim</c>), <c>cosim</c> (<c>fmi</c>); target: the block / behavior / system to analyse, by name.</item>
/// <item>expressions: numbers with units (<c>1500 kg</c>, <c>100 km/h</c>, <c>9.81 [m/s^2]</c>), value names (<c>mass</c>,
/// <c>engine.maxPower</c>, <c>'top speed'</c>), + - * / ^, parentheses, <c>abs sqrt min max</c>.</item>
/// <item>statistics over Monte Carlo samples or simulation time series: <c>mean sd min max median final initial p95 p5 pNN</c>
/// (<c>p95(accelTime)</c>), <c>prob(x &lt; 8 s)</c> (fraction of samples / time steps meeting a bound) and <c>at(x, 5 s)</c>.</item>
/// <item>comparators: <c>&lt; &lt;= &gt; &gt;= == != ≤ ≥ ≠</c> (a single <c>=</c> means equality).</item>
/// </list>
/// Values convert between compatible units (both sides go to SI); mismatched dimensions are inconclusive. A value without
/// a unit is read in the unit of the other side of the comparison.
/// </summary>
public sealed partial class VerificationCriterion
{
    public sealed class CriterionException(string message) : Exception(message);

    public string Text { get; }
    public AnalysisKind? Analysis { get; }
    public string? Target { get; }
    public string Comparator { get; }
    internal Node Left { get; }
    internal Node Right { get; }

    private VerificationCriterion(string text, AnalysisKind? analysis, string? target, Node left, string op, Node right)
    {
        Text = text; Analysis = analysis; Target = target; Left = left; Comparator = op; Right = right;
    }

    /// <summary>Value names the criterion refers to (for dependency tracking and hints).</summary>
    public IReadOnlyList<string> Names => Refs(Left).Concat(Refs(Right)).Distinct(StringComparer.Ordinal).ToList();

    [GeneratedRegex(@"^\s*(?<a>parametrics?|param|constraints?|montecarlo|monte[- ]carlo|mc|timed|sim|simulation|cosim|co-sim|fmi)\s*(\(\s*(?<t>[^)]*?)\s*\))?\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex Prefix();

    public static AnalysisKind? ParseAnalysis(string? s) => (s ?? "").Trim().ToLowerInvariant().Replace(" ", "-") switch
    {
        "parametric" or "parametrics" or "param" or "constraint" or "constraints" => AnalysisKind.Parametric,
        "montecarlo" or "monte-carlo" or "mc" => AnalysisKind.MonteCarlo,
        "timed" or "sim" or "simulation" => AnalysisKind.Timed,
        "cosim" or "co-sim" or "fmi" => AnalysisKind.CoSim,
        _ => null,
    };

    public static string Label(AnalysisKind k) => k switch
    {
        AnalysisKind.Parametric => "Parametric",
        AnalysisKind.MonteCarlo => "Monte Carlo",
        AnalysisKind.Timed => "Timed simulation",
        _ => "Co-simulation (FMI)",
    };

    public static VerificationCriterion Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new CriterionException("The criterion is empty");
        var body = text;
        AnalysisKind? kind = null;
        string? target = null;
        if (Prefix().Match(text) is { Success: true } m)
        {
            kind = ParseAnalysis(m.Groups["a"].Value);
            target = m.Groups["t"].Success && m.Groups["t"].Value.Length > 0 ? m.Groups["t"].Value.Trim('\'', '"', ' ') : null;
            body = text[m.Length..];
        }
        var p = new Parser(Tokenize(body));
        var (l, op, r) = p.Comparison();
        return new VerificationCriterion(text.Trim(), kind, target, l, op, r);
    }

    public static bool TryParse(string? text, out VerificationCriterion? criterion, out string? error)
    {
        criterion = null; error = null;
        try { criterion = Parse(text ?? ""); return true; }
        catch (CriterionException ex) { error = ex.Message; return false; }
    }

    /// <summary>Null when the text is a well-formed criterion, otherwise the error.</summary>
    public static string? Check(string? text) => TryParse(text, out _, out var e) ? null : e;

    // ================================================================== evaluation

    /// <summary>Evaluates the criterion against an analysis' outputs.</summary>
    public CriterionResult Evaluate(IMeasureSource source)
    {
        var notes = new List<string>();
        try
        {
            var ev = new Evaluator(source, notes);
            var l = ev.Eval(Left);
            var r = ev.Eval(Right);
            // Display unit: the bound's written unit, else the value's, else SI.
            var display = r.Unit ?? l.Unit;
            (l, r) = Align(l, r, notes);
            if (l.Dim != r.Dim)
                return CriterionResult.Inconclusive($"Units do not match: {DimLabel(l)} vs {DimLabel(r)}", notes);
            var unit = display is { } du && Units.Parse(du) is { } u && u.Dimension == l.Dim ? u : new Unit(l.Dim.IsNone ? "" : l.Dim.ToString(), 1, l.Dim);
            if (!double.IsFinite(l.Si) || !double.IsFinite(r.Si))
                return CriterionResult.Inconclusive($"The computed value is not a finite number ({Units.Format(unit.FromSi(l.Si))})", notes);

            var tol = 1e-9 * Math.Max(1, Math.Max(Math.Abs(l.Si), Math.Abs(r.Si)));
            var pass = Comparator switch
            {
                "<" => l.Si < r.Si, "<=" => l.Si <= r.Si + tol, ">" => l.Si > r.Si, ">=" => l.Si >= r.Si - tol,
                "==" => Math.Abs(l.Si - r.Si) <= tol, _ => Math.Abs(l.Si - r.Si) > tol,
            };
            var marginSi = Comparator switch
            {
                "<" or "<=" => r.Si - l.Si,
                ">" or ">=" => l.Si - r.Si,
                "==" => -Math.Abs(l.Si - r.Si),
                _ => Math.Abs(l.Si - r.Si),
            };
            var rel = Math.Abs(r.Si - (unit.Offset)) > 1e-300 ? marginSi / Math.Abs(r.Si - unit.Offset) : (double?)null;
            var sym = unit.Symbol.Length == 0 || unit.Symbol == "1" ? "" : " " + unit.Symbol;
            string Q(double si) => Units.Format(unit.FromSi(si)) + sym;
            var margin = Units.Format(unit.DeltaFromSi(marginSi)) + sym + (rel is { } rr && double.IsFinite(rr) ? $" ({(rr * 100).ToString("0.#", CultureInfo.InvariantCulture)} %)" : "");
            return new CriterionResult(pass ? Verdict.Pass : Verdict.Fail, Q(l.Si), Q(r.Si), margin, rel, unit.Symbol, $"{Q(l.Si)} {Comparator} {Q(r.Si)}", notes);
        }
        catch (CriterionException ex) { return CriterionResult.Inconclusive(ex.Message, notes); }
    }

    private static string DimLabel(Q q) => q.Unit ?? (q.Dim.IsNone ? "a plain number" : q.Dim.ToString());

    /// <summary>A side without a declared unit takes the other side's unit.</summary>
    private static (Q, Q) Align(Q l, Q r, List<string> notes)
    {
        if (l.Dim == r.Dim) return (l, r);
        if (l.Assumed && l.Dim.IsNone && r.Unit is { } ru && Units.Parse(ru) is { } u1)
        {
            notes.Add($"{l.Origin ?? "The value"} has no unit; read as {ru}");
            return (l with { Si = u1.ToSi(l.Si), Dim = u1.Dimension, Unit = ru }, r);
        }
        if (r.Assumed && r.Dim.IsNone && l.Unit is { } lu && Units.Parse(lu) is { } u2)
        {
            notes.Add($"{r.Origin ?? "The bound"} has no unit; read as {lu}");
            return (l, r with { Si = u2.ToSi(r.Si), Dim = u2.Dimension, Unit = lu });
        }
        return (l, r);
    }

    // ================================================================== syntax tree

    internal abstract record Node;
    internal sealed record Num(double Value, string? Unit) : Node;
    internal sealed record Ref(string Name) : Node;
    internal sealed record Call(string Fn, IReadOnlyList<Node> Args) : Node;
    internal sealed record Prob(Ref Of, string Op, Node Bound) : Node;
    internal sealed record Bin(char Op, Node L, Node R) : Node;
    internal sealed record Neg(Node X) : Node;
    internal sealed record Power(Node X, double Exp) : Node;

    private static IEnumerable<string> Refs(Node n) => n switch
    {
        Ref r => [r.Name],
        Call c => c.Args.SelectMany(Refs),
        Prob p => Refs(p.Of).Concat(Refs(p.Bound)),
        Bin b => Refs(b.L).Concat(Refs(b.R)),
        Neg x => Refs(x.X),
        Power p => Refs(p.X),
        _ => [],
    };

    private static readonly HashSet<string> Stats = new(StringComparer.OrdinalIgnoreCase)
        { "mean", "avg", "average", "sd", "std", "stddev", "min", "max", "median", "final", "last", "end", "initial", "first", "start", "count" };

    [GeneratedRegex(@"^p(\d{1,2}(?:\.\d+)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex Percentile();

    internal static bool IsStat(string fn) => Stats.Contains(fn) || Percentile().IsMatch(fn);

    // ================================================================== tokens

    private readonly record struct Tok(char Kind, string Text, string? Unit = null);   // n number, i identifier, o operator, c comparator, ( ) ,

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
                if (i < s.Length && s[i] is 'e' or 'E' && i + 1 < s.Length && (char.IsDigit(s[i + 1]) || s[i + 1] is '-' or '+' && i + 2 < s.Length && char.IsDigit(s[i + 2])))
                {
                    i += 2;
                    while (i < s.Length && char.IsDigit(s[i])) i++;
                }
                var num = s[st..i];
                list.Add(new Tok('n', num, ReadUnit(s, ref i)));
                continue;
            }
            if (c is '\'' or '"' or '`')
            {
                var end = s.IndexOf(c, i + 1);
                if (end < 0) throw new CriterionException("Unterminated quoted name");
                list.Add(new Tok('i', s[(i + 1)..end].Trim()));
                i = end + 1;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var st = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '.')) i++;
                list.Add(new Tok('i', s[st..i].TrimEnd('.')));
                continue;
            }
            if (c == '[')
            {
                // A bracketed unit after a parenthesis: (a + b) [m/s]
                var end = s.IndexOf(']', i);
                if (end < 0) throw new CriterionException("Missing ']' after unit");
                list.Add(new Tok('u', s[(i + 1)..end].Trim()));
                i = end + 1;
                continue;
            }
            var two = i + 1 < s.Length ? s.Substring(i, 2) : "";
            if (two is "<=" or ">=" or "==" or "!=" or "=<" or "=>")
            {
                list.Add(new Tok('c', two switch { "=<" => "<=", "=>" => ">=", _ => two }));
                i += 2; continue;
            }
            switch (c)
            {
                case '<' or '>': list.Add(new Tok('c', c.ToString())); i++; continue;
                case '=': list.Add(new Tok('c', "==")); i++; continue;
                case '≤': list.Add(new Tok('c', "<=")); i++; continue;
                case '≥': list.Add(new Tok('c', ">=")); i++; continue;
                case '≠': list.Add(new Tok('c', "!=")); i++; continue;
                case '+' or '-' or '*' or '/' or '^' or '·' or '×':
                    list.Add(new Tok('o', c is '·' or '×' ? "*" : c.ToString())); i++; continue;
                case '(' or ')' or ',': list.Add(new Tok(c, c.ToString())); i++; continue;
            }
            throw new CriterionException($"Unexpected character '{c}'");
        }
        return list;
    }

    /// <summary>A unit written right after a number: "1500 kg", "100km/h", "9.81 [m/s^2]", "95 %".</summary>
    private static string? ReadUnit(string s, ref int i)
    {
        var j = i;
        while (j < s.Length && s[j] == ' ') j++;
        if (j >= s.Length) return null;
        if (s[j] == '[')
        {
            var end = s.IndexOf(']', j);
            if (end < 0) throw new CriterionException("Missing ']' after unit");
            var u = s[(j + 1)..end].Trim();
            if (!Units.TryParse(u, out _, out var err)) throw new CriterionException(err ?? $"Unknown unit '{u}'");
            i = end + 1;
            return u;
        }
        if (!(char.IsLetter(s[j]) || s[j] is '°' or '%' or 'µ' or 'μ' or 'Ω' or '℃')) return null;
        var st = j;
        while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] is '°' or '%' or 'µ' or 'μ' or 'Ω' or '℃' or '/' or '*' or '·' or '^' or '²' or '³'
                                || s[j] == '-' && j > st && s[j - 1] == '^'))
            j++;
        // Longest prefix that is a unit (so "8 s)" or "5 km/h*2" still read their unit).
        for (var end = j; end > st; end--)
        {
            var cand = s[st..end].TrimEnd('/', '*', '·', '^');
            if (cand.Length == 0) continue;
            if (!Units.TryParse(cand, out _, out _)) continue;
            i = st + cand.Length;
            return cand;
        }
        return null;
    }

    // ================================================================== parser

    private sealed class Parser(List<Tok> toks)
    {
        private const int MaxDepth = 100;
        private int _i, _depth;
        private Tok? Cur => _i < toks.Count ? toks[_i] : null;
        private bool IsOp(string op) => Cur is { Kind: 'o' } t && t.Text == op;

        public (Node, string, Node) Comparison()
        {
            if (toks.Count == 0) throw new CriterionException("The criterion is empty");
            var l = Add();
            if (Cur is not { Kind: 'c' } c) throw new CriterionException(Cur is null ? "Missing comparison (<, <=, >, >=, ==, !=)" : $"Unexpected '{Cur.Value.Text}'");
            _i++;
            var r = Add();
            if (Cur is { } extra) throw new CriterionException($"Unexpected '{extra.Text}'");
            return (l, c.Text, r);
        }

        private Node Add()
        {
            var l = Mul();
            while (IsOp("+") || IsOp("-")) { var op = toks[_i++].Text[0]; l = new Bin(op, l, Mul()); }
            return l;
        }

        private Node Mul()
        {
            var l = Unary();
            while (IsOp("*") || IsOp("/")) { var op = toks[_i++].Text[0]; l = new Bin(op, l, Unary()); }
            return l;
        }

        private Node Unary()
        {
            if (++_depth > MaxDepth) throw new CriterionException("The criterion is nested too deeply");
            try
            {
                if (IsOp("-")) { _i++; return new Neg(Unary()); }
                if (IsOp("+")) { _i++; return Unary(); }
                var b = Primary();
                if (IsOp("^"))
                {
                    _i++;
                    var neg = false;
                    if (IsOp("-")) { _i++; neg = true; }
                    if (Cur is not { Kind: 'n' } e || e.Unit != null) throw new CriterionException("An exponent must be a plain number");
                    _i++;
                    var x = double.Parse(e.Text, CultureInfo.InvariantCulture);
                    b = new Power(b, neg ? -x : x);
                }
                return b;
            }
            finally { _depth--; }
        }

        private Node Primary()
        {
            var t = Cur ?? throw new CriterionException("Unexpected end of criterion");
            _i++;
            switch (t.Kind)
            {
                case 'n':
                    if (!double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) throw new CriterionException($"Bad number '{t.Text}'");
                    return new Num(v, t.Unit);
                case '(':
                {
                    var inner = Add();
                    if (Cur?.Kind != ')') throw new CriterionException("Missing ')'");
                    _i++;
                    if (Cur is { Kind: 'u' } u) { _i++; return new Bin('*', inner, new Num(1, u.Text)); }
                    return inner;
                }
                case 'i':
                    if (Cur?.Kind == '(')
                    {
                        _i++;
                        var fn = t.Text.ToLowerInvariant();
                        if (fn == "prob")
                        {
                            if (Cur is not { Kind: 'i' } name) throw new CriterionException("prob(...) needs a value name, e.g. prob(accelTime < 8 s)");
                            _i++;
                            if (Cur is not { Kind: 'c' } op) throw new CriterionException("prob(...) needs a comparison, e.g. prob(accelTime < 8 s)");
                            _i++;
                            var bound = Add();
                            Close(fn);
                            return new Prob(new Ref(name.Text), op.Text, bound);
                        }
                        var args = new List<Node>();
                        if (Cur?.Kind != ')')
                        {
                            args.Add(Add());
                            while (Cur?.Kind == ',') { _i++; args.Add(Add()); }
                        }
                        Close(fn);
                        return new Call(fn, args);
                    }
                    return new Ref(t.Text);
                default:
                    throw new CriterionException($"Unexpected '{t.Text}'");
            }
        }

        private void Close(string fn)
        {
            if (Cur?.Kind != ')') throw new CriterionException($"Missing ')' after {fn}(");
            _i++;
        }
    }

    // ================================================================== evaluator

    /// <summary>A quantity in SI. <see cref="Assumed"/>: no declared unit (a bare model value).</summary>
    private readonly record struct Q(double Si, Dimension Dim, string? Unit, bool Assumed, string? Origin = null);

    private sealed class Evaluator(IMeasureSource source, List<string> notes)
    {
        public Q Eval(Node n) => n switch
        {
            Num x => Literal(x),
            Ref r => FromMeasure(r.Name, Lookup(r.Name), null),
            Call c => CallFn(c),
            Prob p => Probability(p),
            Neg x => Negate(Eval(x.X)),
            Power p => Pow(Eval(p.X), p.Exp),
            Bin b => Binary(b.Op, Eval(b.L), Eval(b.R)),
            _ => throw new CriterionException("Unsupported expression"),
        };

        private static Q Negate(Q q) => q with { Si = -q.Si };

        private static Q Literal(Num x)
        {
            if (x.Unit is null) return new Q(x.Value, Dimension.None, null, true);
            if (!Units.TryParse(x.Unit, out var u, out var err)) throw new CriterionException(err ?? $"Unknown unit '{x.Unit}'");
            return new Q(u.ToSi(x.Value), u.Dimension, x.Unit, false);
        }

        private Measure Lookup(string name) =>
            source.Find(name, out var problem) ?? throw new CriterionException(problem ?? $"No value called '{name}'");

        private Q FromMeasure(string name, Measure m, string? stat)
        {
            double raw;
            switch (m)
            {
                case ScalarMeasure s:
                    raw = s.Value;
                    break;
                case SampleMeasure sm:
                    if (sm.Values.Count == 0) throw new CriterionException($"'{name}' has no samples");
                    raw = Statistic(stat ?? "mean", sm.Values, name);
                    if (stat is null) notes.Add($"{name}: mean of {sm.Values.Count} samples (use p95(), max() … for another statistic)");
                    break;
                case SeriesMeasure ts:
                    if (ts.Values.Count == 0) throw new CriterionException($"'{name}' has no recorded values");
                    raw = Statistic(stat ?? "final", ts.Values, name);
                    if (stat is null) notes.Add($"{name}: final value at t = {Units.Format(ts.Time.Count > 0 ? ts.Time[^1] : 0)} s");
                    break;
                default: throw new CriterionException($"'{name}' has no value");
            }
            return WithUnit(raw, m.Unit, name);
        }

        private Q WithUnit(double raw, string? unit, string origin)
        {
            if (string.IsNullOrWhiteSpace(unit)) return new Q(raw, Dimension.None, null, true, origin);
            if (!Units.TryParse(unit, out var u, out _))
            {
                notes.Add($"{origin}: unknown unit '{unit}' ignored");
                return new Q(raw, Dimension.None, null, true, origin);
            }
            return new Q(u.ToSi(raw), u.Dimension, unit, false, origin);
        }

        private static double Statistic(string stat, IReadOnlyList<double> xs, string name)
        {
            var s = stat.ToLowerInvariant();
            if (Percentile().Match(s) is { Success: true } pm)
            {
                var p = double.Parse(pm.Groups[1].Value, CultureInfo.InvariantCulture);
                var sorted = xs.OrderBy(x => x).ToArray();
                return SampleStats.Percentile(sorted, p);
            }
            return s switch
            {
                "mean" or "avg" or "average" => xs.Average(),
                "sd" or "std" or "stddev" => StdDev(xs),
                "min" => xs.Min(),
                "max" => xs.Max(),
                "median" => SampleStats.Percentile(xs.OrderBy(x => x).ToArray(), 50),
                "final" or "last" or "end" => xs[^1],
                "initial" or "first" or "start" => xs[0],
                "count" => xs.Count,
                _ => throw new CriterionException($"Unknown statistic '{stat}' for {name}"),
            };
        }

        private static double StdDev(IReadOnlyList<double> xs)
        {
            if (xs.Count < 2) return 0;
            var mean = xs.Average();
            return Math.Sqrt(xs.Sum(x => (x - mean) * (x - mean)) / (xs.Count - 1));
        }

        private Q CallFn(Call c)
        {
            if (IsStat(c.Fn) && c.Args is [Ref r])
            {
                var q = FromMeasure(r.Name, Lookup(r.Name), c.Fn);
                return c.Fn.Equals("count", StringComparison.OrdinalIgnoreCase) ? new Q(q.Si, Dimension.None, null, false) : q;
            }
            if (c.Fn == "at")
            {
                if (c.Args is not [Ref name, var timeNode]) throw new CriterionException("at() takes a value name and a time: at(speed, 5 s)");
                var m = Lookup(name.Name);
                var t = Eval(timeNode);
                if (t.Dim != new Dimension(0, 0, 1, 0, 0, 0, 0) && !(t.Assumed && t.Dim.IsNone)) throw new CriterionException("at(): the time must be in seconds (or another time unit)");
                if (m is not SeriesMeasure ts) return FromMeasure(name.Name, m, null);
                var idx = 0;
                for (var i = 0; i < ts.Time.Count; i++) if (ts.Time[i] <= t.Si + 1e-12) idx = i;
                return WithUnit(ts.Values[idx], ts.Unit, name.Name);
            }
            var args = c.Args.Select(Eval).ToList();
            switch (c.Fn)
            {
                case "abs" when args.Count == 1: return args[0] with { Si = Math.Abs(args[0].Si) };
                case "sqrt" when args.Count == 1:
                    var d = args[0].Dim.Sqrt() ?? throw new CriterionException("sqrt() of a quantity whose unit has odd powers");
                    return new Q(Math.Sqrt(args[0].Si), d, null, args[0].Assumed);
                case "min" or "max" when args.Count >= 2:
                    var acc = args[0];
                    foreach (var a in args.Skip(1))
                    {
                        var (x, y) = Same(acc, a, c.Fn);
                        acc = c.Fn == "min" ? (x.Si <= y.Si ? x : y) : (x.Si >= y.Si ? x : y);
                    }
                    return acc;
            }
            throw new CriterionException(IsStat(c.Fn) ? $"{c.Fn}() takes one value name, e.g. {c.Fn}(mass)" : $"Unknown function '{c.Fn}'");
        }

        private Q Probability(Prob p)
        {
            var m = Lookup(p.Of.Name);
            var bound = Eval(p.Bound);
            IReadOnlyList<double> xs = m switch
            {
                SampleMeasure s => s.Values,
                SeriesMeasure t => t.Values,
                ScalarMeasure sc => [sc.Value],
                _ => [],
            };
            if (xs.Count == 0) throw new CriterionException($"'{p.Of.Name}' has no values");
            var unit = string.IsNullOrWhiteSpace(m.Unit) ? null : Units.Parse(m.Unit);
            double ToSi(double raw) => unit?.ToSi(raw) ?? raw;
            var dim = unit?.Dimension ?? Dimension.None;
            var b = bound;
            if (b.Dim != dim)
            {
                if (unit is null && b.Unit != null) { notes.Add($"{p.Of.Name} has no unit; read as {b.Unit}"); var bu = Units.Parse(b.Unit)!; unit = bu; dim = bu.Dimension; }
                else if (b.Assumed && b.Dim.IsNone && unit != null) b = b with { Si = unit.ToSi(b.Si), Dim = dim };
                else throw new CriterionException($"prob(): units do not match for {p.Of.Name}");
            }
            var ok = xs.Count(x => MonteCarlo.Meets(ToSi(x), p.Op, b.Si));
            return new Q((double)ok / xs.Count, Dimension.None, null, false);
        }

        private static Q Pow(Q q, double exp)
        {
            if (!q.Dim.IsNone && Math.Abs(exp - Math.Round(exp)) > 1e-12) throw new CriterionException("Only whole-number powers of quantities with units are supported");
            return new Q(Math.Pow(q.Si, exp), q.Dim.IsNone ? q.Dim : q.Dim.Pow((int)Math.Round(exp)), null, q.Assumed);
        }

        private (Q, Q) Same(Q a, Q b, string what)
        {
            if (a.Dim == b.Dim) return (a, b);
            var (x, y) = Align(a, b, notes);
            if (x.Dim != y.Dim) throw new CriterionException($"{what}: units do not match ({DimLabel(a)} vs {DimLabel(b)})");
            return (x, y);
        }

        private Q Binary(char op, Q a, Q b)
        {
            switch (op)
            {
                case '+' or '-':
                {
                    var (x, y) = Same(a, b, op == '+' ? "Addition" : "Subtraction");
                    return new Q(op == '+' ? x.Si + y.Si : x.Si - y.Si, x.Dim, x.Unit ?? y.Unit, x.Assumed && y.Assumed);
                }
                case '*':
                    return new Q(a.Si * b.Si, a.Dim * b.Dim, a.Assumed ? b.Unit : b.Assumed ? a.Unit : null, a.Assumed && b.Assumed);
                default:
                    return new Q(a.Si / b.Si, a.Dim / b.Dim, b.Assumed ? a.Unit : null, a.Assumed && b.Assumed);
            }
        }
    }
}
