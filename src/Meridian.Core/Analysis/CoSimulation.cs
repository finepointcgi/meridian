using System.Diagnostics;
using System.Globalization;
using System.Text;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

public enum CoSimSeverity { Info, Warning, Error }

public sealed record CoSimProblem(CoSimSeverity Severity, string Message, string? ElementId = null);

public enum CoSimPartKind
{
    /// <summary>Typed by an imported FMU block: stepped through the FMU's native code (out of process).</summary>
    Fmu,
    /// <summary>Typed by an ordinary block: outputs computed algebraically from inputs (port expressions or parametric constraints).</summary>
    Parametric,
}

public enum CoSimAlgorithm { GaussSeidel, Jacobi }

/// <summary>A port of a part as seen by the master: direction, unit and FMI type (for FMUs).</summary>
public sealed record CoSimPort(Element Port, string Name, bool IsInput, string? Unit, string Type);

/// <summary>A tunable value of a part: an FMU parameter, a value property of a parametric block, or an unconnected input.</summary>
public sealed record CoSimParameter(string Key, string Name, Element Element, double Default, string? Unit, bool IsInput);

public sealed class CoSimPart
{
    public required Element Part { get; init; }
    public required Element Type { get; init; }
    public required CoSimPartKind Kind { get; init; }
    public string Name => Part.Name;
    public List<CoSimPort> Inputs { get; } = new();
    public List<CoSimPort> Outputs { get; } = new();
    public List<CoSimParameter> Parameters { get; } = new();
    public override string ToString() => $"{Name} : {Type.Name}";
}

public sealed record CoSimConnection(CoSimPart FromPart, CoSimPort From, CoSimPart ToPart, CoSimPort To, Relationship Connector)
{
    public override string ToString() => $"{FromPart.Name}.{From.Name} → {ToPart.Name}.{To.Name}";
}

/// <summary>What will be simulated: the parts of a system block, their connections and anything wrong with them.</summary>
public sealed class CoSimPlan
{
    public required Element System { get; init; }
    public List<CoSimPart> Parts { get; } = new();
    public List<CoSimConnection> Connections { get; } = new();
    public List<CoSimProblem> Problems { get; } = new();
    public bool HasErrors => Problems.Any(p => p.Severity == CoSimSeverity.Error);
    public bool CanRun => !HasErrors && Parts.Count > 0;

    /// <summary>Recorded signals: every output of every part, keyed "part.port".</summary>
    public IEnumerable<string> OutputKeys => Parts.SelectMany(p => p.Outputs.Select(o => $"{p.Name}.{o.Name}"));

    public IEnumerable<CoSimParameter> AllParameters => Parts.SelectMany(p => p.Parameters);

    public CoSimConnection? Driver(CoSimPart part, CoSimPort input) =>
        Connections.FirstOrDefault(c => ReferenceEquals(c.ToPart, part) && ReferenceEquals(c.To, input));
}

public sealed class CoSimOptions
{
    public double Start { get; set; }
    public double Stop { get; set; } = 10;
    public double Step { get; set; } = 0.01;
    public CoSimAlgorithm Algorithm { get; set; } = CoSimAlgorithm.GaussSeidel;
    /// <summary>Parameter and unconnected-input values by key ("part.name"), replacing defaults.</summary>
    public Dictionary<string, double> Parameters { get; set; } = new(StringComparer.Ordinal);
    /// <summary>At most this many samples are kept per signal (the run is decimated evenly; the last step is always kept).</summary>
    public int MaxRecordedPoints { get; set; } = 5000;
    public const int MaxSteps = 10_000_000;
}

/// <summary>
/// One simulated component. Inputs and outputs are in the order of <see cref="CoSimPart.Inputs"/> / <see cref="CoSimPart.Outputs"/>.
/// </summary>
public interface ICoSimUnit : IDisposable
{
    /// <summary>False for algebraic units (outputs are a function of the inputs at the same instant).</summary>
    bool HasState { get; }
    /// <summary>Set by <see cref="Step"/> when the unit asks to end the simulation early.</summary>
    bool TerminateRequested { get; }
    /// <summary>Initializes with the inputs at start time; returns the initial outputs.</summary>
    double[] Initialize(double start, double stop, double[] inputs);
    /// <summary>Sets the inputs, advances from <paramref name="t"/> by <paramref name="h"/> and returns the outputs at t + h.</summary>
    double[] Step(double t, double h, double[] inputs);
    /// <summary>Algebraic units: outputs at time <paramref name="t"/> for these inputs.</summary>
    double[] Evaluate(double t, double[] inputs);
    /// <summary>Log lines produced since the last call.</summary>
    IReadOnlyList<string> DrainLog();
}

/// <summary>Raised by units for failures that should end the run with a readable message.</summary>
public sealed class CoSimException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record CoSimCheck(bool Pass, double Worst, double? FirstViolation, string Mode);

public sealed class CoSimResult
{
    public List<double> Time { get; } = new();
    public Dictionary<string, List<double>> Series { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string?> Units { get; } = new(StringComparer.Ordinal);
    public List<CoSimProblem> Problems { get; } = new();
    public List<string> Log { get; } = new();
    public bool Completed { get; set; }
    public bool Cancelled { get; set; }
    public string? Error { get; set; }
    public int Steps { get; set; }
    public TimeSpan Elapsed { get; set; }

    public string ToCsv()
    {
        var keys = Series.Keys.ToList();
        var sb = new StringBuilder("time");
        foreach (var k in keys) sb.Append(',').Append(Csv(Units.GetValueOrDefault(k) is { Length: > 0 } u ? $"{k} [{u}]" : k));
        sb.Append('\n');
        for (var i = 0; i < Time.Count; i++)
        {
            sb.Append(Time[i].ToString("R", CultureInfo.InvariantCulture));
            foreach (var k in keys) sb.Append(',').Append(i < Series[k].Count ? Series[k][i].ToString("R", CultureInfo.InvariantCulture) : "");
            sb.Append('\n');
        }
        return sb.ToString();

        static string Csv(string s) => s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>
    /// Checks a recorded signal against a threshold. Mode "always": every sample must meet it (worst sample reported);
    /// "final": only the last sample counts.
    /// </summary>
    public CoSimCheck? Check(string key, string op, double threshold, string mode = "always")
    {
        if (!Series.TryGetValue(key, out var xs) || xs.Count == 0) return null;
        if (mode == "final") return new CoSimCheck(MonteCarlo.Meets(xs[^1], op, threshold), xs[^1], MonteCarlo.Meets(xs[^1], op, threshold) ? null : Time[^1], mode);
        double? first = null;
        var worst = xs[0];
        var lower = op is "<" or "<=";
        for (var i = 0; i < xs.Count; i++)
        {
            if (lower ? xs[i] > worst : xs[i] < worst) worst = xs[i];
            if (first == null && !MonteCarlo.Meets(xs[i], op, threshold)) first = Time[i];
        }
        return new CoSimCheck(first == null, worst, first, mode);
    }
}

/// <summary>
/// Co-simulation master. A system block's parts (typed by imported FMU blocks or by ordinary blocks with in / out ports)
/// are connected through the internal block diagram's connectors between their ports; the master advances them together
/// with a fixed communication step, Gauss-Seidel (each FMU sees the outputs its predecessors just produced) or Jacobi
/// (all FMUs step from the same snapshot). Parts typed by ordinary blocks are algebraic: their out ports are computed
/// from the port's default-value expression over the block's inputs and value properties, or from the block's parametric
/// constraints (an out port takes the value property of the same name).
/// </summary>
public static class CoSimulation
{
    public const string FmuStereotype = "FMU";

    public static bool IsFmuBlock(Element? block) =>
        block != null && (block.Stereotypes.Contains(FmuStereotype) || block.Get(FieldKeys.TagPrefix + "fmi.modelName").Length > 0);

    /// <summary>Blocks that can be co-simulated: they own at least one part typed by a block with ports, and a connector between parts.</summary>
    public static IEnumerable<Element> Systems(ModelRepository m) =>
        m.AllElements.Where(e => e.Kind is ElementKind.Block or ElementKind.Class && Parts(m, e).Any(p => IsFmuBlock(m.ResolveType(p))))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<Element> Parts(ModelRepository m, Element block) =>
        block.OwnedElements.Where(p => p.Kind == ElementKind.Property && m.ResolveType(p)?.Kind is ElementKind.Block or ElementKind.Class);

    /// <summary>The block a diagram or element stands for: an IBD's owning block, a part's type, or the block itself.</summary>
    public static Element? SystemOf(ModelRepository m, Element? e) => e switch
    {
        null => null,
        Diagram d => d.Owner is { Kind: ElementKind.Block or ElementKind.Class } o ? o : null,
        { Kind: ElementKind.Block or ElementKind.Class } => e,
        { Kind: ElementKind.Property } when m.ResolveType(e) is { Kind: ElementKind.Block or ElementKind.Class } t => t,
        _ => e.Owner is { Kind: ElementKind.Block or ElementKind.Class } o2 ? o2 : null,
    };

    private static double? Num(string? s, IReadOnlyDictionary<string, double>? vars = null)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        if (t is "true") return 1;
        if (t is "false") return 0;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        try { return Expressions.Evaluate(t, vars ?? new Dictionary<string, double>()); }
        catch (Expressions.ExpressionException) { return null; }
    }

    public static string? UnitOf(ModelRepository m, Element e) =>
        e.Get(ExtFieldKeys.Unit) is { Length: > 0 } u ? u.Trim()
        : m.ResolveType(e)?.Get(ExtFieldKeys.Unit) is { Length: > 0 } tu ? tu.Trim() : null;

    public static CoSimPlan Plan(ModelRepository m, Element systemOrDiagram)
    {
        var system = SystemOf(m, systemOrDiagram) ?? throw new ArgumentException("Pick a block or an internal block diagram");
        var plan = new CoSimPlan { System = system };

        foreach (var p in Parts(m, system))
        {
            var type = m.ResolveType(p)!;
            var fmu = IsFmuBlock(type);
            var part = new CoSimPart { Part = p, Type = type, Kind = fmu ? CoSimPartKind.Fmu : CoSimPartKind.Parametric };
            foreach (var port in type.OwnedElements.Where(x => x.Kind == ElementKind.Port))
            {
                var causality = port.Get(FieldKeys.TagPrefix + "fmi.causality");
                var dir = fmu && causality is "input" or "output" ? (causality == "input" ? "in" : "out") : port.Get(FieldKeys.Direction);
                if (dir is not ("in" or "out"))
                {
                    plan.Problems.Add(new(CoSimSeverity.Warning, $"{p.Name}.{port.Name}: set the port's direction to in or out to use it in co-simulation", port.Id));
                    continue;
                }
                var cp = new CoSimPort(port, port.Name, dir == "in", UnitOf(m, port), port.TypeRef is { Length: > 0 } tr ? m.Find(tr)?.Name ?? tr : "Real");
                (cp.IsInput ? part.Inputs : part.Outputs).Add(cp);
            }
            if (part.Inputs.Count == 0 && part.Outputs.Count == 0)
            {
                if (fmu) plan.Problems.Add(new(CoSimSeverity.Warning, $"{p.Name}: the FMU has no inputs or outputs", p.Id));
                continue;
            }
            // Parameters: FMU parameters, or the value properties of an ordinary block that are not driven through ports.
            foreach (var vp in type.OwnedElements.Where(x => x.Kind == ElementKind.Property && m.ResolveType(x)?.Kind is not (ElementKind.Block or ElementKind.Class or ElementKind.ConstraintBlock)))
            {
                if (fmu ? vp.Get(FieldKeys.TagPrefix + "fmi.causality") != "parameter" || vp.Get(FieldKeys.TagPrefix + "fmi.variability") == "constant"
                        : part.Inputs.Any(i => i.Name == vp.Name) || part.Outputs.Any(o => o.Name == vp.Name)) continue;
                if (Num(vp.Get(FieldKeys.DefaultValue)) is not { } dv) continue;
                part.Parameters.Add(new CoSimParameter($"{p.Name}.{vp.Name}", vp.Name, vp, dv, UnitOf(m, vp), false));
            }
            if (!fmu && part.Outputs.Count > 0)
            {
                var usesConstraints = Parametrics.ConstraintUsages(m, type).Any();
                foreach (var o in part.Outputs.Where(o => o.Port.Get(FieldKeys.DefaultValue).Length == 0))
                    if (!usesConstraints || !type.OwnedElements.Any(x => x.Kind == ElementKind.Property && x.Name == o.Name))
                        plan.Problems.Add(new(CoSimSeverity.Error,
                            $"{p.Name}.{o.Name}: give the out port an expression (default value, e.g. k * u) or add a value property '{o.Name}' solved by the block's constraints", o.Port.Id));
            }
            plan.Parts.Add(part);
        }

        // Connectors between the parts' ports.
        var byPart = plan.Parts.ToDictionary(p => p.Part.Id);
        CoSimPart? PartFor(Element port, string partId)
        {
            if (partId.Length > 0) return byPart.GetValueOrDefault(partId);
            var candidates = plan.Parts.Where(p => ReferenceEquals(p.Type, port.Owner)).ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }
        foreach (var r in m.AllElements.OfType<Relationship>().Where(r => r.Kind == ElementKind.Connector && r.Source.Kind == ElementKind.Port && r.Target.Kind == ElementKind.Port))
        {
            var a = PartFor(r.Source, r.Get(FieldKeys.SourcePart));
            var b = PartFor(r.Target, r.Get(FieldKeys.TargetPart));
            if (a == null || b == null) continue;
            var pa = a.Inputs.Concat(a.Outputs).FirstOrDefault(x => ReferenceEquals(x.Port, r.Source));
            var pb = b.Inputs.Concat(b.Outputs).FirstOrDefault(x => ReferenceEquals(x.Port, r.Target));
            if (pa == null || pb == null) continue;
            var label = $"{a.Name}.{pa.Name} — {b.Name}.{pb.Name}";
            if (pa.IsInput == pb.IsInput)
            {
                plan.Problems.Add(new(CoSimSeverity.Error, $"{label}: connects two {(pa.IsInput ? "inputs" : "outputs")}", r.Id));
                continue;
            }
            var (fp, from, tp, to) = pa.IsInput ? (b, pb, a, pa) : (a, pa, b, pb);
            var conn = new CoSimConnection(fp, from, tp, to, r);
            if (from.Type == "String" || to.Type == "String")
                plan.Problems.Add(new(CoSimSeverity.Error, $"{conn}: string signals cannot be exchanged", r.Id));
            if (from.Unit != null && to.Unit != null && !SameUnit(from.Unit, to.Unit))
                plan.Problems.Add(new(CoSimSeverity.Error, $"{conn}: unit mismatch, {from.Unit} → {to.Unit}", r.Id));
            else if ((from.Unit == null) != (to.Unit == null))
                plan.Problems.Add(new(CoSimSeverity.Warning, $"{conn}: only one end has a unit ({from.Unit ?? to.Unit}); the value is passed unchanged", r.Id));
            if (plan.Connections.Any(c => ReferenceEquals(c.ToPart, tp) && ReferenceEquals(c.To, to)))
                plan.Problems.Add(new(CoSimSeverity.Error, $"{tp.Name}.{to.Name}: driven by more than one output", r.Id));
            plan.Connections.Add(conn);
        }

        // Unconnected inputs keep a constant value (FMU start value, port default value, else 0); they can be overridden like parameters.
        foreach (var part in plan.Parts)
            foreach (var i in part.Inputs.Where(i => plan.Driver(part, i) == null))
            {
                var start = Num(i.Port.Get(FieldKeys.TagPrefix + "fmi.start")) ?? Num(i.Port.Get(FieldKeys.DefaultValue)) ?? 0;
                part.Parameters.Add(new CoSimParameter($"{part.Name}.{i.Name}", i.Name, i.Port, start, i.Unit, true));
            }

        if (AlgebraicOrder(plan) == null)
            plan.Problems.Add(new(CoSimSeverity.Error, "Algebraic loop: parts typed by ordinary blocks feed each other without an FMU in between"));
        if (plan.Parts.Count == 0)
            plan.Problems.Add(new(CoSimSeverity.Error, $"{system.Name} has no parts with in / out ports to simulate"));
        else if (plan.Parts.All(p => p.Kind != CoSimPartKind.Fmu))
            plan.Problems.Add(new(CoSimSeverity.Info, "No FMU parts: only algebraic blocks are evaluated over time"));
        return plan;
    }

    /// <summary>Unit symbols compare as written, ignoring white space and a leading "1" for dimensionless quantities.</summary>
    public static bool SameUnit(string a, string b)
    {
        static string N(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c))).Replace("·", "*").Replace("**", "^");
        var (x, y) = (N(a), N(b));
        if (x is "1" or "-") x = "";
        if (y is "1" or "-") y = "";
        return string.Equals(x, y, StringComparison.Ordinal);
    }

    /// <summary>Parameter overrides read from a trade-study alternative or variant configuration (values named like the parameter).</summary>
    public static Dictionary<string, double> ParametersFrom(ModelRepository m, CoSimPlan plan, Element alternative)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var p in plan.AllParameters)
        {
            var v = TradeStudies.ValueOf(m, alternative, new TradeCriterion { Name = p.Key })
                    ?? TradeStudies.ValueOf(m, alternative, new TradeCriterion { Name = p.Name });
            if (v is { } d) result[p.Key] = d;
        }
        return result;
    }

    /// <summary>Topological order of the algebraic parts (null when they form a loop).</summary>
    private static List<CoSimPart>? AlgebraicOrder(CoSimPlan plan)
    {
        var alg = plan.Parts.Where(p => p.Kind == CoSimPartKind.Parametric).ToList();
        var order = new List<CoSimPart>();
        var done = new HashSet<CoSimPart>();
        while (order.Count < alg.Count)
        {
            var next = alg.FirstOrDefault(p => !done.Contains(p) && plan.Connections.Where(c => ReferenceEquals(c.ToPart, p) && c.FromPart.Kind == CoSimPartKind.Parametric)
                .All(c => done.Contains(c.FromPart)));
            if (next == null) return null;
            order.Add(next);
            done.Add(next);
        }
        return order;
    }

    /// <summary>Stepping order for Gauss-Seidel: parts whose inputs come from earlier parts first (cycles broken in model order).</summary>
    private static List<CoSimPart> SteppingOrder(CoSimPlan plan)
    {
        var stateful = plan.Parts.Where(p => p.Kind == CoSimPartKind.Fmu).ToList();
        var order = new List<CoSimPart>();
        while (order.Count < stateful.Count)
        {
            var rest = stateful.Where(p => !order.Contains(p)).ToList();
            var next = rest.FirstOrDefault(p => !rest.Any(q => !ReferenceEquals(q, p) && Feeds(plan, q, p, [])) ) ?? rest[0];
            order.Add(next);
        }
        return order;
    }

    /// <summary>Whether <paramref name="from"/> drives <paramref name="to"/>, directly or through algebraic parts.</summary>
    private static bool Feeds(CoSimPlan plan, CoSimPart from, CoSimPart to, HashSet<CoSimPart> seen)
    {
        foreach (var c in plan.Connections.Where(c => ReferenceEquals(c.FromPart, from)))
        {
            if (ReferenceEquals(c.ToPart, to)) return true;
            if (c.ToPart.Kind == CoSimPartKind.Parametric && seen.Add(c.ToPart) && Feeds(plan, c.ToPart, to, seen)) return true;
        }
        return false;
    }

    /// <summary>Parameters of one part (by name) after overrides.</summary>
    public static Dictionary<string, double> PartParameters(CoSimPart part, IReadOnlyDictionary<string, double> overrides) =>
        part.Parameters.Where(p => !p.IsInput).ToDictionary(p => p.Name, p => overrides.TryGetValue(p.Key, out var v) ? v : p.Default, StringComparer.Ordinal);

    /// <summary>
    /// Runs the co-simulation. <paramref name="fmuFactory"/> creates the unit for each FMU part (given its parameters by name);
    /// parts typed by ordinary blocks use <see cref="ParametricUnit"/>. Units are always disposed. Failures end the run with
    /// <see cref="CoSimResult.Error"/> set and the samples recorded so far kept.
    /// </summary>
    public static CoSimResult Run(ModelRepository m, CoSimPlan plan, CoSimOptions o, Func<CoSimPart, IReadOnlyDictionary<string, double>, ICoSimUnit> fmuFactory,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var result = new CoSimResult();
        result.Problems.AddRange(plan.Problems);
        var sw = Stopwatch.StartNew();
        if (plan.HasErrors) { result.Error = "The model has errors (see the list): fix them before running"; return result; }
        if (!(o.Step > 0) || !(o.Stop > o.Start) || !double.IsFinite(o.Stop) || !double.IsFinite(o.Start))
        {
            result.Error = "Step size must be positive and the end time after the start time";
            return result;
        }
        var steps = (long)Math.Ceiling((o.Stop - o.Start) / o.Step - 1e-9);
        if (steps > CoSimOptions.MaxSteps) { result.Error = $"{steps:N0} steps is too many (at most {CoSimOptions.MaxSteps:N0}); use a larger step size"; return result; }
        var every = Math.Max(1, (long)Math.Ceiling(steps / (double)Math.Max(2, o.MaxRecordedPoints - 1)));

        var parts = plan.Parts;
        var index = parts.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
        var units = new ICoSimUnit?[parts.Count];
        var outs = parts.Select(p => p.Outputs.Select(x => Num(x.Port.Get(FieldKeys.TagPrefix + "fmi.start")) ?? 0).ToArray()).ToArray();
        // Each input: (source part, source output) or a constant.
        var sources = parts.Select(p => p.Inputs.Select(i => plan.Driver(p, i) is { } c ? (index[c.FromPart], c.FromPart.Outputs.IndexOf(c.From)) : (-1, -1)).ToArray()).ToArray();
        var constants = parts.Select(p => p.Inputs.Select(i => p.Parameters.FirstOrDefault(x => x.IsInput && ReferenceEquals(x.Element, i.Port)) is { } par
            ? o.Parameters.TryGetValue(par.Key, out var v) ? v : par.Default : 0).ToArray()).ToArray();
        var algebraic = AlgebraicOrder(plan)!.Select(p => index[p]).ToList();
        var stepping = SteppingOrder(plan).Select(p => index[p]).ToList();

        foreach (var part in parts)
            foreach (var x in part.Outputs)
            {
                var key = $"{part.Name}.{x.Name}";
                result.Series[key] = new List<double>();
                result.Units[key] = x.Unit;
            }

        double[] Inputs(int p)
        {
            var src = sources[p];
            var v = new double[src.Length];
            for (var i = 0; i < src.Length; i++) v[i] = src[i].Item1 >= 0 ? outs[src[i].Item1][src[i].Item2] : constants[p][i];
            return v;
        }
        void Algebraic(double t) { foreach (var p in algebraic) outs[p] = Checked(p, units[p]!.Evaluate(t, Inputs(p))); }
        double[] Checked(int p, double[] v) =>
            v.Length == parts[p].Outputs.Count ? v : throw new CoSimException($"{parts[p].Name}: returned {v.Length} outputs, expected {parts[p].Outputs.Count}");
        void Record(double t)
        {
            result.Time.Add(t);
            for (var p = 0; p < parts.Count; p++)
                for (var i = 0; i < parts[p].Outputs.Count; i++)
                    result.Series[$"{parts[p].Name}.{parts[p].Outputs[i].Name}"].Add(outs[p][i]);
        }
        void DrainLogs()
        {
            for (var p = 0; p < parts.Count; p++)
                foreach (var l in units[p]?.DrainLog() ?? [])
                    if (result.Log.Count < 5000) result.Log.Add($"{parts[p].Name}: {l}");
        }

        var current = -1;
        try
        {
            for (var p = 0; p < parts.Count; p++)
            {
                current = p;
                ct.ThrowIfCancellationRequested();
                var pars = PartParameters(parts[p], o.Parameters);
                units[p] = parts[p].Kind == CoSimPartKind.Fmu ? fmuFactory(parts[p], pars) : new ParametricUnit(m, parts[p], pars);
            }
            current = -1;
            Algebraic(o.Start);
            foreach (var p in stepping) { current = p; outs[p] = Checked(p, units[p]!.Initialize(o.Start, o.Stop, Inputs(p))); Algebraic(o.Start); }
            current = -1;
            Record(o.Start);
            DrainLogs();

            var lastReport = 0.0;
            for (long k = 1; k <= steps; k++)
            {
                ct.ThrowIfCancellationRequested();
                var t = o.Start + (k - 1) * o.Step;
                var tn = k == steps ? o.Stop : Math.Min(o.Stop, o.Start + k * o.Step);
                var h = tn - t;
                if (o.Algorithm == CoSimAlgorithm.Jacobi)
                {
                    var snapshot = stepping.ToDictionary(p => p, Inputs);
                    foreach (var p in stepping) { current = p; outs[p] = Checked(p, units[p]!.Step(t, h, snapshot[p])); }
                    current = -1;
                    Algebraic(tn);
                }
                else
                {
                    foreach (var p in stepping) { current = p; outs[p] = Checked(p, units[p]!.Step(t, h, Inputs(p))); current = -1; Algebraic(tn); }
                    if (stepping.Count == 0) Algebraic(tn);
                }
                result.Steps = (int)Math.Min(int.MaxValue, k);
                var stop = stepping.FirstOrDefault(p => units[p]!.TerminateRequested, -1);
                if (k % every == 0 || k == steps || stop >= 0) Record(tn);
                if (k % 64 == 0) DrainLogs();
                if (stop >= 0)
                {
                    result.Problems.Add(new(CoSimSeverity.Info, $"{parts[stop].Name} asked to end the simulation at t = {tn.ToString("G6", CultureInfo.InvariantCulture)}"));
                    break;
                }
                var frac = (double)k / steps;
                if (frac - lastReport >= 0.01) { progress?.Report(frac); lastReport = frac; }
            }
            result.Completed = true;
            progress?.Report(1);
        }
        catch (OperationCanceledException) { result.Cancelled = true; }
        catch (Exception ex) when (ex is CoSimException or Expressions.ExpressionException or InvalidOperationException or IOException or TimeoutException)
        {
            result.Error = current >= 0 && !ex.Message.StartsWith(parts[current].Name + ":", StringComparison.Ordinal) ? $"{parts[current].Name}: {ex.Message}" : ex.Message;
        }
        finally
        {
            try { DrainLogs(); } catch (Exception ex) when (ex is CoSimException or InvalidOperationException or IOException) { }
            foreach (var u in units)
            {
                try { u?.Dispose(); }
                catch (Exception ex) when (ex is CoSimException or InvalidOperationException or IOException or TimeoutException) { result.Log.Add($"cleanup: {ex.Message}"); }
            }
            result.Elapsed = sw.Elapsed;
        }
        return result;
    }
}

/// <summary>
/// An algebraic part: each out port is computed from its default-value expression over the part's inputs (by port name),
/// its parameters (value properties) and <c>time</c>; or, without an expression, read from the value property of the
/// same name after solving the block's parametric constraints with the inputs bound to same-named value properties.
/// </summary>
public sealed class ParametricUnit : ICoSimUnit
{
    private readonly ModelRepository _m;
    private readonly CoSimPart _part;
    private readonly Dictionary<string, double> _parameters;
    private readonly string?[] _expressions;
    private readonly Dictionary<string, Element> _properties;

    public ParametricUnit(ModelRepository m, CoSimPart part, IReadOnlyDictionary<string, double> parameters)
    {
        _m = m;
        _part = part;
        _parameters = new Dictionary<string, double>(parameters, StringComparer.Ordinal);
        _expressions = part.Outputs.Select(o => o.Port.Get(FieldKeys.DefaultValue) is { Length: > 0 } e ? e : null).ToArray();
        _properties = part.Type.OwnedElements.Where(p => p.Kind == ElementKind.Property).GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public bool HasState => false;
    public bool TerminateRequested => false;
    public IReadOnlyList<string> DrainLog() => [];
    public double[] Initialize(double start, double stop, double[] inputs) => Evaluate(start, inputs);
    public double[] Step(double t, double h, double[] inputs) => Evaluate(t + h, inputs);

    public double[] Evaluate(double t, double[] inputs)
    {
        var vars = new Dictionary<string, double>(_parameters, StringComparer.Ordinal) { ["time"] = t };
        for (var i = 0; i < inputs.Length; i++) vars[_part.Inputs[i].Name] = inputs[i];
        var result = new double[_part.Outputs.Count];
        ParametricResult? solved = null;
        for (var i = 0; i < result.Length; i++)
        {
            if (_expressions[i] is { } expr) { result[i] = Expressions.Evaluate(expr, vars); continue; }
            if (solved == null)
            {
                var overrides = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var (name, v) in vars)
                    if (_properties.TryGetValue(name, out var p)) overrides[p.Id] = v;
                solved = Parametrics.Solve(_m, _part.Type, overrides);
            }
            var name2 = _part.Outputs[i].Name;
            result[i] = solved.Values.FirstOrDefault(v => v.Property?.Name == name2)?.Value
                        ?? throw new CoSimException($"{_part.Name}.{name2}: the block's constraints did not solve '{name2}'"
                                                    + (solved.Problems.Count > 0 ? $" ({solved.Problems[0]})" : ""));
        }
        return result;
    }

    public void Dispose() { }
}
