using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

/// <summary>Settings an analysis run takes from a verification run.</summary>
public sealed record AnalysisSettings
{
    public int Samples { get; init; } = 500;
    public int Seed { get; init; } = 12345;
    /// <summary>Timed simulation: time limit (s, default 3600). Co-simulation: stop time (s, default 10).</summary>
    public double? StopTime { get; init; }
    /// <summary>Co-simulation communication step (s, default 0.01).</summary>
    public double? Step { get; init; }
    /// <summary>Creates FMU units for co-simulation; null when no FMI runtime is available (algebraic parts still run).</summary>
    public Func<CoSimPart, IReadOnlyDictionary<string, double>, ICoSimUnit>? FmuFactory { get; init; }
}

/// <summary>The outputs of one analysis of one target, looked up by name. Records which elements it read.</summary>
public sealed class AnalysisOutput(string description, Func<string, (Measure? Measure, string? Problem)>? find = null) : IMeasureSource
{
    public string Description { get; } = description;
    public string? Error { get; init; }
    public List<string> Problems { get; } = new();

    /// <summary>Ids of the elements (with their contents) whose change can change these outputs.</summary>
    public HashSet<string> Dependencies { get; } = new(StringComparer.Ordinal);

    public Measure? Find(string name, out string? problem)
    {
        problem = null;
        if (find is null) { problem = Error ?? $"No value called '{name}'"; return null; }
        var (m, p) = find(name);
        problem = p;
        return m;
    }

    public static AnalysisOutput Failed(string description, string error) => new(description) { Error = error };
}

/// <summary>Runs an analysis on a target element. The model implementation is <see cref="ModelAnalyzer"/>; tests substitute fakes.</summary>
public interface IVerificationAnalyzer
{
    AnalysisOutput Analyze(ModelRepository model, AnalysisKind kind, Element target, AnalysisSettings settings, CancellationToken ct);
}

/// <summary>
/// Adapts Meridian's analyses to criteria: parametric solving (<see cref="Parametrics"/>), Monte Carlo statistics
/// (<see cref="MonteCarlo"/>), timed discrete-event simulation (<see cref="DiscreteEventSimulation"/>) and FMI
/// co-simulation (<see cref="CoSimulation"/>).
/// </summary>
public sealed class ModelAnalyzer : IVerificationAnalyzer
{
    public static readonly ModelAnalyzer Instance = new();

    public AnalysisOutput Analyze(ModelRepository m, AnalysisKind kind, Element target, AnalysisSettings s, CancellationToken ct) => kind switch
    {
        AnalysisKind.Parametric => Parametric(m, target),
        AnalysisKind.MonteCarlo => MonteCarloRun(m, target, s, ct),
        AnalysisKind.Timed => Timed(m, target, s, ct),
        _ => CoSim(m, target, s, ct),
    };

    private static bool IsBlock(Element? e) => e?.Kind is ElementKind.Block or ElementKind.Class;

    private static Element? BlockFor(Element target) => IsBlock(target) ? target
        : target.Kind == ElementKind.Property && IsBlock(target.Owner) ? target.Owner : null;

    private static Element? FindBlock(ModelRepository m, string name) =>
        m.AllElements.FirstOrDefault(e => IsBlock(e) && e.Name == name)
        ?? m.AllElements.FirstOrDefault(e => IsBlock(e) && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    private static Element? Child(Element owner, string name) =>
        owner.OwnedElements.FirstOrDefault(c => c.Kind == ElementKind.Property && c.Name == name)
        ?? owner.OwnedElements.FirstOrDefault(c => c.Kind == ElementKind.Property && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Follows a dotted path ("engine.maxPower", "Vehicle.mass", "Engine.maxPower") from <paramref name="block"/> through
    /// part properties to the block that owns the last segment. Returns that block and the value name.
    /// </summary>
    internal static (Element? Block, string Name, string? Problem) Navigate(ModelRepository m, Element block, string path)
    {
        var segs = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segs.Length == 0) return (null, path, "Empty value name");
        var cur = block;
        var i = 0;
        if (segs.Length > 1)
        {
            if (string.Equals(segs[0], block.Name, StringComparison.OrdinalIgnoreCase)) i = 1;
            else if (Child(block, segs[0]) is null && FindBlock(m, segs[0]) is { } other) { cur = other; i = 1; }
        }
        for (; i < segs.Length - 1; i++)
        {
            var part = Child(cur, segs[i]);
            if (part is null || m.ResolveType(part) is not { } type || !IsBlock(type))
                return (null, segs[^1], $"{cur.Name} has no part '{segs[i]}'");
            cur = type;
        }
        return (cur, segs[^1], null);
    }

    // ------------------------------------------------------------------ parametric

    private static AnalysisOutput Parametric(ModelRepository m, Element target)
    {
        if (BlockFor(target) is not { } block)
            return AnalysisOutput.Failed("Parametric", $"Parametric analysis needs a block, not {Metamodel.DisplayName(target.Kind).ToLowerInvariant()} '{target.Name}'");
        var cache = new Dictionary<Element, ParametricResult>(ReferenceEqualityComparer.Instance);
        AnalysisOutput? output = null;
        ParametricResult Solve(Element b)
        {
            if (cache.TryGetValue(b, out var r)) return r;
            r = Parametrics.Solve(m, b);
            cache[b] = r;
            output!.Dependencies.Add(b.Id);
            foreach (var u in Parametrics.ConstraintUsages(m, b)) if (m.ResolveType(u) is { } cb) output.Dependencies.Add(cb.Id);
            foreach (var p in r.Problems) output.Problems.Add($"{b.Name}: {p}");
            return r;
        }
        output = new AnalysisOutput($"{VerificationCriterion.Label(AnalysisKind.Parametric)} ({block.Name})", name =>
        {
            var (owner, last, problem) = Navigate(m, block, name);
            if (owner is null) return (null, problem);
            var r = Solve(owner);
            var v = r.Values.FirstOrDefault(x => x.Property?.Name == last)
                    ?? r.Values.FirstOrDefault(x => string.Equals(x.Property?.Name, last, StringComparison.OrdinalIgnoreCase))
                    ?? r.Values.FirstOrDefault(x => string.Equals(x.Name, last, StringComparison.OrdinalIgnoreCase));
            if (v is null) return (null, $"{owner.Name} has no value property '{last}'");
            if (v.Value is not { } value)
            {
                var why = r.Problems.Count > 0 ? r.Problems[0] : r.Unsolved.Count > 0 ? $"unsolved: {string.Join(", ", r.Unsolved)}" : "no default value and no constraint computes it";
                return (null, $"{owner.Name}.{last} could not be computed ({why})");
            }
            return (new ScalarMeasure(value, v.Property is { } p ? CoSimulation.UnitOf(m, p) : null), null);
        });
        Solve(block);
        return output;
    }

    // ------------------------------------------------------------------ Monte Carlo

    private static AnalysisOutput MonteCarloRun(ModelRepository m, Element target, AnalysisSettings s, CancellationToken ct)
    {
        if (BlockFor(target) is not { } block)
            return AnalysisOutput.Failed("Monte Carlo", $"Monte Carlo needs a block, not '{target.Name}'");
        var run = MonteCarlo.Run(m, block, Math.Clamp(s.Samples, 1, 1_000_000), s.Seed, ct);
        var desc = $"{VerificationCriterion.Label(AnalysisKind.MonteCarlo)} ({block.Name}, {run.Requested - run.Failed} samples, seed {s.Seed})";
        var output = new AnalysisOutput(desc, name =>
        {
            var last = name.Contains('.') && name.Split('.')[0].Equals(block.Name, StringComparison.OrdinalIgnoreCase) ? name[(name.IndexOf('.') + 1)..] : name;
            var key = run.Samples.Keys.FirstOrDefault(k => k == last) ?? run.Samples.Keys.FirstOrDefault(k => string.Equals(k, last, StringComparison.OrdinalIgnoreCase));
            if (key is null) return (null, $"Monte Carlo of {block.Name} has no output '{name}'");
            return (new SampleMeasure(run.Samples[key], Child(block, key) is { } p ? CoSimulation.UnitOf(m, p) : null), null);
        });
        output.Dependencies.Add(block.Id);
        foreach (var u in Parametrics.ConstraintUsages(m, block)) if (m.ResolveType(u) is { } cb) output.Dependencies.Add(cb.Id);
        output.Problems.AddRange(run.Problems);
        if (run.Failed > 0) output.Problems.Add($"{run.Failed} of {run.Requested} samples could not be solved");
        return output;
    }

    // ------------------------------------------------------------------ timed simulation

    public const int MaxSnapshots = 20_000;

    private static AnalysisOutput Timed(ModelRepository m, Element target, AnalysisSettings s, CancellationToken ct)
    {
        var behaviors = target.Kind is ElementKind.Activity or ElementKind.StateMachine ? [target]
            : target.OwnedElements.Where(b => b.Kind is ElementKind.Activity or ElementKind.StateMachine).ToList();
        if (behaviors.Count == 0)
            return AnalysisOutput.Failed("Timed simulation", $"'{target.Name}' is not an activity or state machine and owns none to simulate");
        var sim = new DiscreteEventSimulation(m, behaviors);
        var stop = s.StopTime is > 0 ? s.StopTime.Value : 3600;
        var series = new Dictionary<string, (List<double> T, List<double> V)>(StringComparer.Ordinal);
        void Snapshot()
        {
            foreach (var (k, v) in sim.Variables)
            {
                if (!series.TryGetValue(k, out var sv)) series[k] = sv = (new List<double>(), new List<double>());
                if (sv.T.Count > 0 && sv.T[^1] == sim.Clock) { sv.V[^1] = v; continue; }
                if (sv.T.Count >= MaxSnapshots) continue;
                sv.T.Add(sim.Clock); sv.V.Add(v);
            }
        }
        sim.Start();
        Snapshot();
        while (!sim.Finished && !ct.IsCancellationRequested && sim.NextEventTime is { } t && t <= stop && sim.Step()) Snapshot();
        var context = behaviors.Select(b => b.Owner).FirstOrDefault(o => o != null && Metamodel.IsStructuralClassifier(o.Kind));
        var output = new AnalysisOutput($"{VerificationCriterion.Label(AnalysisKind.Timed)} ({string.Join(", ", behaviors.Select(b => b.Name))}, ended at {Units.Format(sim.Clock)} s)", name =>
        {
            var last = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
            switch (last.ToLowerInvariant())
            {
                case "time" or "endtime" or "clock" or "duration": return (new ScalarMeasure(sim.Clock, "s"), null);
                case "events": return (new ScalarMeasure(sim.EventsProcessed, null), null);
            }
            var key = series.Keys.FirstOrDefault(k => k == last) ?? series.Keys.FirstOrDefault(k => string.Equals(k, last, StringComparison.OrdinalIgnoreCase));
            if (key is null) return (null, $"The simulation has no variable '{name}'");
            var unit = context != null && Child(context, key) is { } p ? CoSimulation.UnitOf(m, p) : null;
            return (new SeriesMeasure(series[key].T, series[key].V, unit), null);
        });
        foreach (var b in behaviors) output.Dependencies.Add(b.Id);
        if (context != null) output.Dependencies.Add(context.Id);
        if (!sim.Finished) output.Problems.Add($"Stopped at the time limit {Units.Format(stop)} s");
        output.Problems.AddRange(sim.Trace.Where(e => e.Kind == TraceKind.Warning).Select(e => e.Text.Trim()).Distinct().Take(5));
        return output;
    }

    // ------------------------------------------------------------------ co-simulation

    private static AnalysisOutput CoSim(ModelRepository m, Element target, AnalysisSettings s, CancellationToken ct)
    {
        var plan = CoSimulation.Plan(m, target);
        if (plan.HasErrors)
            return AnalysisOutput.Failed("Co-simulation", string.Join("; ", plan.Problems.Where(p => p.Severity == CoSimSeverity.Error).Select(p => p.Message)));
        var options = new CoSimOptions { Start = 0, Stop = s.StopTime is > 0 ? s.StopTime.Value : 10, Step = s.Step is > 0 ? s.Step.Value : 0.01 };
        var factory = s.FmuFactory ?? ((part, _) => throw new CoSimException($"{part.Name}: no FMI runtime is available to run the FMU here"));
        var result = CoSimulation.Run(m, plan, options, factory, null, ct);
        if (result.Error != null) return AnalysisOutput.Failed("Co-simulation", result.Error.Split('\n')[0]);
        var output = new AnalysisOutput($"{VerificationCriterion.Label(AnalysisKind.CoSim)} ({plan.System.Name}, {Units.Format(options.Stop)} s)", name =>
        {
            var key = result.Series.Keys.FirstOrDefault(k => k == name)
                      ?? result.Series.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (key is null)
            {
                var tail = result.Series.Keys.Where(k => k.EndsWith("." + name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (tail.Count == 1) key = tail[0];
                else if (tail.Count > 1) return (null, $"'{name}' is ambiguous: {string.Join(", ", tail)}");
            }
            if (key is null) return (null, $"The co-simulation has no output '{name}' (outputs: {string.Join(", ", result.Series.Keys.Take(8))})");
            return (new SeriesMeasure(result.Time, result.Series[key], result.Units.GetValueOrDefault(key)), null);
        });
        output.Dependencies.Add(plan.System.Id);
        foreach (var p in plan.Parts) output.Dependencies.Add(p.Type.Id);
        output.Problems.AddRange(result.Problems.Where(p => p.Severity != CoSimSeverity.Info).Select(p => p.Message));
        return output;
    }
}
