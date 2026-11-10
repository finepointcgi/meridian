using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

/// <summary>A measure of effectiveness: what to read from each alternative, how much it counts and which way is better.</summary>
public sealed class TradeCriterion
{
    public string Name { get; set; } = "";
    /// <summary>Name of the value property / parametric value / tagged value read from each alternative (defaults to <see cref="Name"/>).</summary>
    public string Value { get; set; } = "";
    public double Weight { get; set; } = 1;
    /// <summary>maximize or minimize.</summary>
    public string Direction { get; set; } = "maximize";
    /// <summary>property (value property default, else tagged value), parametric (solved value of the block) or tag.</summary>
    public string Source { get; set; } = "property";
    /// <summary>minmax or utility.</summary>
    public string Normalization { get; set; } = "minmax";
    /// <summary>Utility curve points "x:u, x:u, …" (u in 0..1), piecewise linear, clamped at the ends.</summary>
    public string Utility { get; set; } = "";

    [JsonIgnore] public string ValueName => string.IsNullOrWhiteSpace(Value) ? Name : Value;
    [JsonIgnore] public bool Minimize => Direction.StartsWith("min", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Persisted in the trade study element's <see cref="ExtFieldKeys.TradeStudyData"/> field as JSON.</summary>
public sealed class TradeStudyDefinition
{
    public List<string> Alternatives { get; set; } = new();
    public List<TradeCriterion> Criteria { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    public static TradeStudyDefinition Parse(Element study)
    {
        var text = study.Get(ExtFieldKeys.TradeStudyData);
        if (string.IsNullOrWhiteSpace(text)) return new TradeStudyDefinition();
        try { return JsonSerializer.Deserialize<TradeStudyDefinition>(text, Json) ?? new TradeStudyDefinition(); }
        catch (JsonException) { return new TradeStudyDefinition(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public void Save(ModelRepository m, Element study) => m.Set(study, ExtFieldKeys.TradeStudyData, ToJson());

    public TradeStudyDefinition Clone() => JsonSerializer.Deserialize<TradeStudyDefinition>(ToJson(), Json)!;
}

public sealed record TradeRow(Element Alternative, double?[] Raw, double[] Normalized, double[] Weighted, double Score, int Rank);

public sealed record SensitivityCase(string Criterion, double Factor, double Weight, string Winner, bool WinnerChanged, int RankChanges, IReadOnlyList<string> Order);

/// <summary>For one criterion: the weight (as a share of the total) at which the leading alternative changes, if any.</summary>
public sealed record WeightThreshold(string Criterion, double CurrentShare, double? SwitchShare, string? NewWinner);

public sealed record TradeResult(
    IReadOnlyList<TradeCriterion> Criteria, IReadOnlyList<double> Weights, IReadOnlyList<TradeRow> Rows,
    IReadOnlyList<string> Problems)
{
    public IEnumerable<TradeRow> Ranked => Rows.OrderBy(r => r.Rank);
    public TradeRow? Winner => Rows.OrderBy(r => r.Rank).FirstOrDefault();
}

/// <summary>Weighted-sum trade study evaluation with min-max or utility-curve normalization and weight sensitivity.</summary>
public static class TradeStudies
{
    public static IEnumerable<Element> CandidateAlternatives(ModelRepository m) =>
        m.AllElements.Where(e => e.Kind is ElementKind.Block or ElementKind.Class or ElementKind.Component or ElementKind.Configuration)
            .OrderBy(e => e.Kind).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Numeric value names offered by an alternative (value properties, parametric values, tagged values).</summary>
    public static IEnumerable<string> ValueNames(ModelRepository m, Element alt)
    {
        var names = new List<string>();
        names.AddRange(alt.OwnedElements.Where(p => p.Kind == ElementKind.Property && m.ResolveType(p)?.Kind is not (ElementKind.Block or ElementKind.Class or ElementKind.ConstraintBlock)).Select(p => p.Name));
        names.AddRange(alt.TaggedValues.Where(t => double.TryParse(t.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)).Select(t => t.Key));
        if (alt.Kind == ElementKind.Configuration)
            foreach (var f in Variability.Selected(m, alt))
                names.AddRange(f.TaggedValues.Select(t => t.Key));
        return names.Where(n => n.Length > 0).Distinct();
    }

    private static double? Num(string s, IReadOnlyDictionary<string, double>? vars = null)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        try { return Expressions.Evaluate(s, vars ?? new Dictionary<string, double>()); }
        catch (Expressions.ExpressionException) { return null; }
    }

    /// <summary>Reads the criterion's value from an alternative. Null when it has none.</summary>
    public static double? ValueOf(ModelRepository m, Element alt, TradeCriterion c, Dictionary<Element, ParametricResult>? cache = null)
    {
        var name = c.ValueName;
        if (c.Source == "parametric" && alt.Kind != ElementKind.Configuration)
        {
            ParametricResult r;
            if (cache == null || !cache.TryGetValue(alt, out r!)) { r = Parametrics.Solve(m, alt); if (cache != null) cache[alt] = r; }
            return r.Values.FirstOrDefault(v => v.Name == name || v.Property?.Name == name)?.Value;
        }
        if (c.Source != "tag")
        {
            var p = alt.OwnedElements.FirstOrDefault(p => p.Kind == ElementKind.Property && p.Name == name);
            if (p != null && Num(p.Get(FieldKeys.DefaultValue)) is { } dv) return dv;
            if (p != null && Num(p.Get(FieldKeys.TagPrefix + "computed")) is { } cv) return cv;
        }
        if (Num(alt.Get(FieldKeys.TagPrefix + name)) is { } tv) return tv;
        if (alt.Kind == ElementKind.Configuration)
        {
            // A configuration's value is the sum of its selected features' tagged values (e.g. cost, mass).
            double sum = 0; var any = false;
            foreach (var f in Variability.Selected(m, alt))
                if (Num(f.Get(FieldKeys.TagPrefix + name)) is { } fv) { sum += fv; any = true; }
            return any ? sum : null;
        }
        return null;
    }

    /// <summary>Piecewise-linear utility from "x:u, x:u" points; null when the curve is malformed.</summary>
    public static Func<double, double>? UtilityCurve(string spec)
    {
        var pts = new List<(double X, double U)>();
        foreach (var part in spec.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var xy = part.Split(':', StringSplitOptions.TrimEntries);
            if (xy.Length != 2 || !double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var u)) return null;
            pts.Add((x, u));
        }
        if (pts.Count < 2) return null;
        pts.Sort((a, b) => a.X.CompareTo(b.X));
        return x =>
        {
            if (x <= pts[0].X) return pts[0].U;
            if (x >= pts[^1].X) return pts[^1].U;
            for (var i = 1; i < pts.Count; i++)
                if (x <= pts[i].X)
                {
                    var (x0, u0) = pts[i - 1]; var (x1, u1) = pts[i];
                    return x1 == x0 ? u1 : u0 + (u1 - u0) * (x - x0) / (x1 - x0);
                }
            return pts[^1].U;
        };
    }

    public static TradeResult Evaluate(ModelRepository m, TradeStudyDefinition def, IReadOnlyList<double>? weightOverride = null)
    {
        var problems = new List<string>();
        var alts = def.Alternatives.Select(m.Find).OfType<Element>().ToList();
        if (alts.Count < def.Alternatives.Count) problems.Add("Some alternatives no longer exist in the model");
        var crit = def.Criteria;
        var cache = new Dictionary<Element, ParametricResult>(ReferenceEqualityComparer.Instance);
        var raw = alts.Select(a => crit.Select(c => ValueOf(m, a, c, cache)).ToArray()).ToList();

        var weights = (weightOverride ?? crit.Select(c => Math.Max(0, c.Weight)).ToList()).ToList();
        var total = weights.Sum();
        var w = weights.Select(x => total > 0 ? x / total : crit.Count > 0 ? 1.0 / crit.Count : 0).ToList();

        var norm = alts.Select(_ => new double[crit.Count]).ToList();
        for (var j = 0; j < crit.Count; j++)
        {
            var c = crit[j];
            var col = raw.Select(r => r[j]).ToList();
            var missing = alts.Where((a, i) => col[i] is null).Select(a => a.DisplayName).ToList();
            if (missing.Count > 0) problems.Add($"{c.Name}: no value '{c.ValueName}' on {string.Join(", ", missing)} (scored 0)");
            if (c.Normalization == "utility")
            {
                var f = UtilityCurve(c.Utility);
                if (f == null) problems.Add($"{c.Name}: utility curve '{c.Utility}' needs at least two x:u points; using min-max");
                if (f != null)
                {
                    for (var i = 0; i < alts.Count; i++) norm[i][j] = col[i] is { } v ? Math.Clamp(f(v), 0, 1) : 0;
                    continue;
                }
            }
            var present = col.OfType<double>().ToList();
            if (present.Count == 0) continue;
            double lo = present.Min(), hi = present.Max();
            for (var i = 0; i < alts.Count; i++)
            {
                if (col[i] is not { } v) { norm[i][j] = 0; continue; }
                if (hi - lo < 1e-12) { norm[i][j] = 1; continue; }   // every alternative equal: all best
                var n = (v - lo) / (hi - lo);
                norm[i][j] = c.Minimize ? 1 - n : n;
            }
        }

        var scores = norm.Select(n => n.Select((x, j) => x * w[j]).Sum()).ToList();
        var order = Enumerable.Range(0, alts.Count).OrderByDescending(i => scores[i]).ThenBy(i => alts[i].Name, StringComparer.OrdinalIgnoreCase).ToList();
        var rows = alts.Select((a, i) => new TradeRow(a, raw[i], norm[i], norm[i].Select((x, j) => x * w[j]).ToArray(), scores[i], order.IndexOf(i) + 1)).ToList();
        if (alts.Count == 0) problems.Add("Add at least one alternative");
        if (crit.Count == 0) problems.Add("Add at least one criterion");
        return new TradeResult(crit, w, rows, problems);
    }

    /// <summary>
    /// One-at-a-time weight sensitivity: each criterion's weight is multiplied by each factor (others unchanged, then all
    /// renormalised) and the ranking compared with the baseline.
    /// </summary>
    public static IReadOnlyList<SensitivityCase> Sensitivity(ModelRepository m, TradeStudyDefinition def, IReadOnlyList<double>? factors = null)
    {
        factors ??= [0.5, 0.75, 1.25, 1.5];
        var baseResult = Evaluate(m, def);
        var baseOrder = baseResult.Ranked.Select(r => r.Alternative.DisplayName).ToList();
        var list = new List<SensitivityCase>();
        var weights = def.Criteria.Select(c => Math.Max(0, c.Weight)).ToList();
        for (var j = 0; j < def.Criteria.Count; j++)
            foreach (var f in factors)
            {
                var ws = weights.ToList();
                ws[j] = weights[j] * f;
                var r = Evaluate(m, def, ws);
                var order = r.Ranked.Select(x => x.Alternative.DisplayName).ToList();
                var changes = order.Where((name, i) => i >= baseOrder.Count || baseOrder[i] != name).Count();
                list.Add(new SensitivityCase(def.Criteria[j].Name, f, ws[j], order.FirstOrDefault() ?? "", order.FirstOrDefault() != baseOrder.FirstOrDefault(), changes, order));
            }
        return list;
    }

    /// <summary>Sweeps each criterion's share of the total weight from 0 to 1 (others keep their proportions) to find where the winner changes.</summary>
    public static IReadOnlyList<WeightThreshold> Thresholds(ModelRepository m, TradeStudyDefinition def, int steps = 100)
    {
        var res = new List<WeightThreshold>();
        var baseResult = Evaluate(m, def);
        var winner = baseResult.Winner?.Alternative;
        if (winner == null) return res;
        var weights = def.Criteria.Select(c => Math.Max(0, c.Weight)).ToList();
        var total = weights.Sum();
        for (var j = 0; j < def.Criteria.Count; j++)
        {
            var share = total > 0 ? weights[j] / total : 0;
            var others = total - weights[j];
            double? best = null; string? who = null;
            for (var k = 0; k <= steps; k++)
            {
                var s = (double)k / steps;
                var ws = weights.Select((x, i) => i == j ? s : others > 0 ? x / others * (1 - s) : (1 - s) / Math.Max(1, weights.Count - 1)).ToList();
                var r = Evaluate(m, def, ws);
                if (r.Winner is { } w && !ReferenceEquals(w.Alternative, winner) && Math.Abs(w.Score - r.Rows.First(x => ReferenceEquals(x.Alternative, winner)).Score) > 1e-9)
                {
                    if (best == null || Math.Abs(s - share) < Math.Abs(best.Value - share)) { best = s; who = w.Alternative.DisplayName; }
                }
            }
            res.Add(new WeightThreshold(def.Criteria[j].Name, share, best, who));
        }
        return res;
    }

    public static string ToCsv(TradeResult r)
    {
        static string Q(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        static string F(double? v) => v?.ToString("G6", CultureInfo.InvariantCulture) ?? "";
        var sb = new StringBuilder();
        sb.Append("Rank,Alternative,Score");
        foreach (var c in r.Criteria) sb.Append(',').Append(Q($"{c.Name} (raw)")).Append(',').Append(Q($"{c.Name} (normalized)")).Append(',').Append(Q($"{c.Name} (weighted)"));
        sb.Append('\n');
        sb.Append(",Weight,");
        foreach (var w in r.Weights) sb.Append(",,").Append(F(w)).Append(',');
        sb.Append('\n');
        foreach (var row in r.Ranked)
        {
            sb.Append(row.Rank).Append(',').Append(Q(row.Alternative.DisplayName)).Append(',').Append(F(row.Score));
            for (var j = 0; j < r.Criteria.Count; j++)
                sb.Append(',').Append(F(row.Raw[j])).Append(',').Append(F(row.Normalized[j])).Append(',').Append(F(row.Weighted[j]));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Creates a trade study element with the given alternatives and criteria. One undo step.</summary>
    public static Element Create(ModelRepository m, Element owner, string name, TradeStudyDefinition def)
    {
        using (m.History.Transaction($"New trade study {name}"))
        {
            var target = Metamodel.CanOwn(owner.Kind, ElementKind.TradeStudy) ? owner : m.DefaultOwnerFor(owner, ElementKind.TradeStudy);
            var e = m.Create(target, ElementKind.TradeStudy, name);
            def.Save(m, e);
            return e;
        }
    }
}
