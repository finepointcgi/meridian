using System.Globalization;
using System.Text.RegularExpressions;
using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

/// <summary>A probability distribution written on a value property: normal(μ, σ), uniform(a, b), triangular(a, mode, b), lognormal(μ, σ).</summary>
public sealed record Distribution(string Kind, double[] Parameters)
{
    public static Distribution? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text.Trim(), @"^(\w+)\s*\(([^)]*)\)$");
        if (!m.Success) return null;
        var kind = m.Groups[1].Value.ToLowerInvariant() switch { "gauss" or "gaussian" => "normal", "uni" => "uniform", "tri" => "triangular", var k => k };
        var ps = m.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var vals = new double[ps.Length];
        for (var i = 0; i < ps.Length; i++)
            if (!double.TryParse(ps[i], NumberStyles.Float, CultureInfo.InvariantCulture, out vals[i])) return null;
        return (kind, vals.Length) switch
        {
            ("normal", 2) when vals[1] >= 0 => new Distribution(kind, vals),
            ("lognormal", 2) when vals[1] >= 0 => new Distribution(kind, vals),
            ("uniform", 2) when vals[1] >= vals[0] => new Distribution(kind, vals),
            ("triangular", 3) when vals[0] <= vals[1] && vals[1] <= vals[2] => new Distribution(kind, vals),
            _ => null,
        };
    }

    public double Sample(Random rng)
    {
        var p = Parameters;
        switch (Kind)
        {
            case "normal": return p[0] + p[1] * StandardNormal(rng);
            case "lognormal": return Math.Exp(p[0] + p[1] * StandardNormal(rng));
            case "uniform": return p[0] + (p[1] - p[0]) * rng.NextDouble();
            case "triangular":
            {
                double a = p[0], c = p[1], b = p[2], u = rng.NextDouble();
                if (b - a < 1e-15) return a;
                var fc = (c - a) / (b - a);
                return u < fc ? a + Math.Sqrt(u * (b - a) * (c - a)) : b - Math.Sqrt((1 - u) * (b - a) * (b - c));
            }
            default: return double.NaN;
        }
    }

    private static double StandardNormal(Random rng)
    {
        // Box–Muller.
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    public override string ToString() => $"{Kind}({string.Join(", ", Parameters.Select(x => x.ToString("G6", CultureInfo.InvariantCulture)))})";
}

public sealed record SampleStats(int Count, double Mean, double StdDev, double Min, double Max, double P5, double P50, double P95)
{
    public static SampleStats Of(IReadOnlyList<double> xs)
    {
        if (xs.Count == 0) return new SampleStats(0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        var sorted = xs.OrderBy(x => x).ToArray();
        var mean = sorted.Average();
        var sd = sorted.Length > 1 ? Math.Sqrt(sorted.Sum(x => (x - mean) * (x - mean)) / (sorted.Length - 1)) : 0;
        return new SampleStats(sorted.Length, mean, sd, sorted[0], sorted[^1], Percentile(sorted, 5), Percentile(sorted, 50), Percentile(sorted, 95));
    }

    /// <summary>Linear-interpolated percentile of an ascending array.</summary>
    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return double.NaN;
        var pos = Math.Clamp(p / 100.0, 0, 1) * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos); var hi = (int)Math.Ceiling(pos);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}

public sealed record MonteCarloRun(
    Element Block, int Requested, IReadOnlyList<(Element Property, Distribution Distribution)> Inputs,
    IReadOnlyDictionary<string, List<double>> Samples, int Failed, IReadOnlyList<string> Problems)
{
    public IEnumerable<string> Outputs => Samples.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Monte Carlo over parametrics: sample every value property that has a distribution and solve the block N times.</summary>
public static class MonteCarlo
{
    public static IReadOnlyList<(Element Property, Distribution Distribution)> Inputs(Element block) =>
        block.OwnedElements.Where(p => p.Kind == ElementKind.Property)
            .Select(p => (p, Distribution.Parse(p.Get(ExtFieldKeys.Distribution))))
            .Where(t => t.Item2 != null).Select(t => (t.p, t.Item2!)).ToList();

    public static MonteCarloRun Run(ModelRepository m, Element block, int samples, int seed = 12345, CancellationToken ct = default)
    {
        var rng = new Random(seed);
        var inputs = Inputs(block);
        var problems = new List<string>();
        foreach (var p in block.OwnedElements.Where(p => p.Kind == ElementKind.Property && p.Get(ExtFieldKeys.Distribution).Length > 0 && Distribution.Parse(p.Get(ExtFieldKeys.Distribution)) is null))
            problems.Add($"{p.Name}: cannot read distribution '{p.Get(ExtFieldKeys.Distribution)}'");
        if (inputs.Count == 0) problems.Add("No value property of this block has a distribution (e.g. normal(1500, 50)); every sample is identical");
        var data = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var failed = 0;
        for (var i = 0; i < samples && !ct.IsCancellationRequested; i++)
        {
            var overrides = inputs.ToDictionary(t => t.Property.Id, t => t.Distribution.Sample(rng));
            var r = Parametrics.Solve(m, block, overrides);
            if (r.Problems.Count > 0 && r.Values.All(v => v.Value is null)) { failed++; continue; }
            foreach (var v in r.Values)
                if (v.Value is { } x && double.IsFinite(x))
                {
                    if (!data.TryGetValue(v.Name, out var list)) data[v.Name] = list = new List<double>(samples);
                    list.Add(x);
                }
        }
        return new MonteCarloRun(block, samples, inputs, data, failed, problems);
    }

    /// <summary>Equal-width histogram: bin lower edges and counts.</summary>
    public static (double[] Edges, int[] Counts) Histogram(IReadOnlyList<double> xs, int bins = 20)
    {
        if (xs.Count == 0) return ([], []);
        double lo = xs.Min(), hi = xs.Max();
        if (hi - lo < 1e-12) { lo -= 0.5; hi += 0.5; }
        var width = (hi - lo) / bins;
        var counts = new int[bins];
        foreach (var x in xs) counts[Math.Clamp((int)((x - lo) / width), 0, bins - 1)]++;
        return (Enumerable.Range(0, bins + 1).Select(i => lo + i * width).ToArray(), counts);
    }

    public static bool Meets(double x, string op, double threshold) => op switch
    {
        "<" => x < threshold, "<=" => x <= threshold, ">" => x > threshold, ">=" => x >= threshold,
        "==" => Math.Abs(x - threshold) < 1e-9 * Math.Max(1, Math.Abs(threshold)),
        _ => false,
    };

    public static double Probability(IReadOnlyList<double> xs, string op, double threshold) =>
        xs.Count == 0 ? double.NaN : (double)xs.Count(x => Meets(x, op, threshold)) / xs.Count;

    /// <summary>
    /// Reads a comparison and a number from requirement text ("shall be less than 8 s", "≥ 500 km", "at most 1500 kg").
    /// Null when the text has no recognisable bound.
    /// </summary>
    public static (string Op, double Value)? ParseThreshold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.ToLowerInvariant().Replace("≤", "<=").Replace("≥", ">=");
        var patterns = new (string Re, string Op)[]
        {
            (@"(<=|no more than|not more than|not exceed|not to exceed|at most|maximum of|max(?:imum)?|up to|within|less than or equal to)", "<="),
            (@"(>=|no less than|not less than|at least|minimum of|min(?:imum)?|greater than or equal to)", ">="),
            (@"(<|less than|under|below|fewer than|shorter than|lower than|faster than)", "<"),
            (@"(>|more than|greater than|over|above|exceed|higher than|longer than)", ">"),
        };
        foreach (var (re, op) in patterns)
        {
            var m = Regex.Match(t, @"(?<![a-z])" + re + @"\s*(?:a |an |of |than )?\s*(-?\d+(?:[.,]\d+)?(?:e-?\d+)?)");
            if (m.Success && double.TryParse(m.Groups[2].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return (op, v);
        }
        return null;
    }
}
