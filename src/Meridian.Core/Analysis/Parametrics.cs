using System.Globalization;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

public sealed record ParametricValue(Element? Property, string Name, double? Value, bool Computed, string? Source);

public sealed record ParametricResult(IReadOnlyList<ParametricValue> Values, IReadOnlyList<string> Unsolved, IReadOnlyList<string> Problems)
{
    public bool Complete => Unsolved.Count == 0 && Problems.Count == 0;
}

/// <summary>
/// SysML parametric evaluation. A block's constraint properties (properties typed by constraint blocks) carry
/// equations over their parameters; binding connectors tie parameters to the block's value properties.
/// Value properties with a default value are inputs, the rest are solved for, one equation at a time.
/// </summary>
public static class Parametrics
{
    public static IEnumerable<Element> ConstraintUsages(ModelRepository m, Element block) =>
        block.OwnedElements.Where(p => p.Kind == ElementKind.Property && m.ResolveType(p)?.Kind == ElementKind.ConstraintBlock);

    public static IEnumerable<Element> Parameters(Element constraintBlock) =>
        constraintBlock.OwnedElements.Where(p => p.Kind == ElementKind.Property);

    public static IEnumerable<string> Equations(Element constraintBlock) =>
        constraintBlock.Get(FieldKeys.Body).Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.Trim('{', '}', ' ')).Where(e => e.Contains('='));

    /// <summary>Binding connectors of a block: (value property, constraint usage, parameter).</summary>
    public static IEnumerable<(Element Value, Element Usage, Element Parameter)> Bindings(ModelRepository m, Element block)
    {
        var usages = ConstraintUsages(m, block).ToList();
        foreach (var r in m.AllElements.OfType<Relationship>().Where(r => r.Kind == ElementKind.Binding))
        {
            var (param, value, partId) = r.Target.Owner?.Kind == ElementKind.ConstraintBlock
                ? (r.Target, r.Source, r.Get(FieldKeys.TargetPart))
                : (r.Source, r.Target, r.Get(FieldKeys.SourcePart));
            if (param.Owner?.Kind != ElementKind.ConstraintBlock || !ReferenceEquals(value.Owner, block)) continue;
            var usage = usages.FirstOrDefault(u => u.Id == partId) ?? usages.FirstOrDefault(u => ReferenceEquals(m.ResolveType(u), param.Owner));
            if (usage != null) yield return (value, usage, param);
        }
    }

    public static ParametricResult Solve(ModelRepository m, Element block)
    {
        var problems = new List<string>();
        var known = new Dictionary<string, double>(StringComparer.Ordinal);   // variable key -> value
        var names = new Dictionary<string, (Element? Prop, string Label)>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var bindings = Bindings(m, block).ToList();

        string Key(Element usage, Element param) =>
            bindings.FirstOrDefault(b => ReferenceEquals(b.Usage, usage) && ReferenceEquals(b.Parameter, param)) is { Value: { } v } ? v.Id : $"{usage.Id}.{param.Name}";

        // Inputs: value properties with a numeric default.
        foreach (var p in block.OwnedElements.Where(p => p.Kind == ElementKind.Property && m.ResolveType(p)?.Kind != ElementKind.ConstraintBlock && m.ResolveType(p)?.Kind is not (ElementKind.Block or ElementKind.Class)))
        {
            names[p.Id] = (p, p.Name);
            if (p.Get(FieldKeys.DefaultValue) is { Length: > 0 } dv)
            {
                if (double.TryParse(dv, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) known[p.Id] = v;
                else
                {
                    try { known[p.Id] = Expressions.Evaluate(dv, known); }
                    catch (Expressions.ExpressionException) { problems.Add($"{p.Name}: default value '{dv}' is not a number"); }
                }
            }
        }

        // Equations rewritten over variable keys.
        var equations = new List<(string Usage, string Eq, Dictionary<string, string> Map)>();
        foreach (var usage in ConstraintUsages(m, block))
        {
            var cb = m.ResolveType(usage)!;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var param in Parameters(cb))
            {
                var key = Key(usage, param);
                map[param.Name] = key;
                if (!names.ContainsKey(key)) names[key] = (null, $"{usage.Name}.{param.Name}");
            }
            foreach (var eq in Equations(cb))
            {
                foreach (var v in Expressions.Variables(eq.Replace("=", "-")).Where(v => !map.ContainsKey(v)))
                {
                    map[v] = $"{usage.Id}.{v}";
                    names.TryAdd(map[v], (null, $"{usage.Name}.{v}"));
                }
                equations.Add((usage.Name, eq, map));
            }
        }

        // Repeatedly solve any equation with exactly one unknown.
        var pending = equations.ToList();
        bool progress;
        do
        {
            progress = false;
            foreach (var (usage, eq, map) in pending.ToList())
            {
                var parts = eq.Split('=', 2);
                var lhs = parts[0]; var rhs = parts[1];
                var vars = Expressions.Variables(lhs + "-(" + rhs + ")").Select(v => map[v]).Distinct().ToList();
                var unknown = vars.Where(v => !known.ContainsKey(v)).ToList();
                if (unknown.Count == 0)
                {
                    pending.Remove((usage, eq, map));
                    var residual = Residual(lhs, rhs, map, known, null, 0);
                    if (Math.Abs(residual) > 1e-6 * Math.Max(1, Math.Abs(Residual(lhs, "0", map, known, null, 0))))
                        problems.Add($"{usage}: '{eq.Trim()}' does not hold for the given inputs (residual {residual:G4})");
                    continue;
                }
                if (unknown.Count > 1) continue;
                var x = unknown[0];
                var root = FindRoot(v => Residual(lhs, rhs, map, known, x, v));
                if (root is null) { problems.Add($"{usage}: could not solve '{eq.Trim()}' for {names[x].Label}"); pending.Remove((usage, eq, map)); continue; }
                known[x] = root.Value;
                sources[x] = $"{usage}: {eq.Trim()}";
                pending.Remove((usage, eq, map));
                progress = true;
            }
        } while (progress && pending.Count > 0);

        var values = names.Select(kv => new ParametricValue(kv.Value.Prop, kv.Value.Label, known.TryGetValue(kv.Key, out var v) ? v : null, sources.ContainsKey(kv.Key), sources.GetValueOrDefault(kv.Key)))
            .OrderBy(v => v.Property == null).ThenBy(v => v.Name).ToList();
        var unsolved = names.Where(kv => !known.ContainsKey(kv.Key) && equations.Any(e => e.Map.ContainsValue(kv.Key))).Select(kv => kv.Value.Label).ToList();
        return new ParametricResult(values, unsolved, problems);
    }

    private static double Residual(string lhs, string rhs, Dictionary<string, string> map, Dictionary<string, double> known, string? unknown, double guess)
    {
        var vars = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (param, key) in map)
        {
            if (key == unknown) vars[param] = guess;
            else if (known.TryGetValue(key, out var v)) vars[param] = v;
        }
        return Expressions.Evaluate(lhs, vars) - Expressions.Evaluate(rhs, vars);
    }

    /// <summary>Newton's method from a few starting points, then a bracketing scan with bisection.</summary>
    public static double? FindRoot(Func<double, double> f)
    {
        double Safe(double x) { try { return f(x); } catch (Expressions.ExpressionException) { return double.NaN; } }
        foreach (var start in new[] { 1.0, 0.0, 10, -1, 100, 0.1, 1000 })
        {
            var x = start;
            for (var i = 0; i < 60; i++)
            {
                var y = Safe(x);
                if (double.IsNaN(y) || double.IsInfinity(y)) break;
                if (Math.Abs(y) < 1e-10) return Clean(x);
                var h = Math.Max(1e-7, Math.Abs(x) * 1e-7);
                var d = (Safe(x + h) - y) / h;
                if (d == 0 || double.IsNaN(d)) break;
                var nx = x - y / d;
                if (Math.Abs(nx - x) < 1e-12 * Math.Max(1, Math.Abs(x))) { if (Math.Abs(Safe(nx)) < 1e-6) return Clean(nx); break; }
                x = nx;
            }
        }
        // Bracketing scan over a logarithmic grid in both signs.
        var grid = new List<double> { 0 };
        for (var e = -6; e <= 9; e++) { grid.Add(Math.Pow(10, e)); grid.Add(-Math.Pow(10, e)); }
        grid.Sort();
        for (var i = 1; i < grid.Count; i++)
        {
            double a = grid[i - 1], b = grid[i], fa = Safe(a), fb = Safe(b);
            if (double.IsNaN(fa) || double.IsNaN(fb) || Math.Sign(fa) == Math.Sign(fb)) continue;
            for (var k = 0; k < 200; k++)
            {
                var mid = (a + b) / 2; var fm = Safe(mid);
                if (Math.Sign(fm) == Math.Sign(fa)) { a = mid; fa = fm; } else { b = mid; }
            }
            return Clean((a + b) / 2);
        }
        return null;
    }

    private static double Clean(double x) => Math.Abs(x - Math.Round(x)) < 1e-9 ? Math.Round(x) : x;
}
