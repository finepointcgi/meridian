using Meridian.Core.Model;

namespace Meridian.Core.Safety;

/// <summary>A minimal cut set: basic events that together cause the top event, with its probability (product).</summary>
public sealed record CutSet(IReadOnlyList<Element> Events, double Probability)
{
    public int Order => Events.Count;
}

/// <summary>Importance of one basic event for the top event.</summary>
/// <param name="FussellVesely">Share of the top-event probability (rare-event) carried by cut sets containing the event.</param>
/// <param name="Birnbaum">P(top | event occurs) − P(top | event does not occur); null when the exact evaluation was skipped.</param>
public sealed record EventImportance(Element Event, double Probability, double FussellVesely, double? Birnbaum);

/// <summary>Result of analysing one fault tree from its top event.</summary>
/// <param name="Probability">Exact top-event probability (independent basic events), or null when the tree is too large to evaluate exactly.</param>
/// <param name="RareEvent">Rare-event approximation: sum of the minimal cut set probabilities.</param>
/// <param name="MinCutUpperBound">1 − Π(1 − P(cut set)).</param>
/// <param name="Truncated">More minimal cut sets exist than were kept (see <see cref="FaultTree.MaxCutSets"/>).</param>
public sealed record FaultTreeResult(
    Element Top,
    IReadOnlyList<CutSet> CutSets,
    double? Probability,
    double RareEvent,
    double MinCutUpperBound,
    IReadOnlyList<Element> BasicEvents,
    IReadOnlyList<EventImportance> Importance,
    IReadOnlyList<string> Warnings,
    bool Truncated)
{
    /// <summary>The best available top-event probability: exact if computed, else the min-cut upper bound.</summary>
    public double BestProbability => Probability ?? MinCutUpperBound;
}

/// <summary>
/// Fault tree analysis. A tree is built from <see cref="ElementKind.FaultTreeEvent"/> and <see cref="ElementKind.FaultTreeGate"/>
/// elements joined by <see cref="ElementKind.FaultTreeLink"/> relationships pointing from each output (above) to its inputs (below).
/// An event's inputs are combined by the gate under it (or OR when it has several inputs and no gate); an event without inputs is a leaf.
/// Minimal cut sets come from top-down expansion with absorption (MOCUS); the top-event probability is evaluated exactly by
/// Shannon decomposition on events that appear more than once, assuming basic events are independent.
/// </summary>
public static class FaultTree
{
    /// <summary>Upper limit on kept minimal cut sets (larger trees are reported as truncated).</summary>
    public const int MaxCutSets = 20000;
    /// <summary>Exact probability is computed when at most this many basic events are repeated (shared) in the tree.</summary>
    public const int MaxRepeatedForExact = 18;

    // ------------------------------------------------------------------ structure

    private abstract record Node;
    private sealed record Leaf(int Index) : Node;
    private sealed record Constant(bool Value) : Node;
    private sealed record Gate(string Op, int K, IReadOnlyList<Node> Inputs) : Node;

    /// <summary>The inputs of an event or gate (elements below it), in left-to-right order on the first diagram showing them.</summary>
    public static IReadOnlyList<Element> Inputs(ModelRepository model, Element e) =>
        model.Outgoing(e, ElementKind.FaultTreeLink).Select(r => r.Target).ToList();

    /// <summary>The outputs of an event or gate (elements above it).</summary>
    public static IReadOnlyList<Element> Outputs(ModelRepository model, Element e) =>
        model.Incoming(e, ElementKind.FaultTreeLink).Select(r => r.Source).ToList();

    /// <summary>Candidate top events: events marked "top", else events with inputs and no outputs.</summary>
    public static IReadOnlyList<Element> TopEvents(ModelRepository model, Element? scope = null)
    {
        var events = model.OfKind(ElementKind.FaultTreeEvent).Where(e => scope == null || scope.IsAncestorOf(e) || ReferenceEquals(scope, e)).ToList();
        var marked = events.Where(e => SafetyFields.EventKind(e) == SafetyFields.Top).ToList();
        if (marked.Count > 0) return marked.OrderBy(e => e.Name).ToList();
        return events.Where(e => Inputs(model, e).Count > 0 && Outputs(model, e).Count == 0).OrderBy(e => e.Name).ToList();
    }

    /// <summary>Top events shown on a fault tree diagram.</summary>
    public static IReadOnlyList<Element> TopEvents(ModelRepository model, Diagram d)
    {
        var shown = d.Nodes.Select(n => n.Element).Where(e => e.Kind == ElementKind.FaultTreeEvent).Distinct().ToList();
        var marked = shown.Where(e => SafetyFields.EventKind(e) == SafetyFields.Top).ToList();
        return marked.Count > 0 ? marked : shown.Where(e => Inputs(model, e).Count > 0 && Outputs(model, e).Count == 0).ToList();
    }

    /// <summary>The probability of a basic event: its own (probability or λ·t), else that of the failure mode it represents.</summary>
    public static double? ProbabilityOf(ModelRepository model, Element basicEvent)
    {
        if (basicEvent.Kind == ElementKind.FaultTreeEvent && SafetyFields.EventKind(basicEvent) == SafetyFields.House)
            return basicEvent.Get(SafetyFields.HouseState) == "true" ? 1 : 0;
        if (SafetyFields.OwnProbability(basicEvent) is { } p) return p;
        if (model.Find(basicEvent.Get(SafetyFields.Ref)) is { Kind: ElementKind.FailureMode } fm && SafetyFields.OwnProbability(fm) is { } q) return q;
        return null;
    }

    private sealed class Builder(ModelRepository model)
    {
        public readonly List<Element> Leaves = [];
        public readonly List<string> Warnings = [];
        private readonly Dictionary<Element, int> _leafIndex = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Element> _path = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Element> _warned = new(ReferenceEqualityComparer.Instance);

        public Node Build(Element e)
        {
            if (!_path.Add(e))
            {
                Warn(e, $"'{e.DisplayName}' is part of a cycle; the loop is ignored");
                return new Constant(false);
            }
            try
            {
                var inputs = Inputs(model, e);
                if (e.Kind == ElementKind.FaultTreeGate)
                {
                    var op = SafetyFields.GateKind(e);
                    if (inputs.Count == 0)
                    {
                        Warn(e, $"Gate {GateName(e)} has no inputs");
                        return new Constant(false);
                    }
                    var kids = inputs.Select(Build).ToList();
                    var k = op == SafetyFields.Vote ? VoteK(e, kids.Count) : 0;
                    return new Gate(op, k, kids);
                }
                if (e.Kind != ElementKind.FaultTreeEvent && e.Kind != ElementKind.FailureMode)
                {
                    Warn(e, $"'{e.DisplayName}' is not a fault tree event or gate");
                    return new Constant(false);
                }
                if (SafetyFields.EventKind(e) == SafetyFields.House && e.Kind == ElementKind.FaultTreeEvent)
                    return new Constant(e.Get(SafetyFields.HouseState) == "true");
                if (inputs.Count == 0)
                {
                    var kind = SafetyFields.EventKind(e);
                    if (e.Kind == ElementKind.FaultTreeEvent && kind is SafetyFields.Top or SafetyFields.Intermediate)
                        Warn(e, $"'{e.DisplayName}' has no inputs; treated as an undeveloped event");
                    return Leaf(e);
                }
                if (inputs.Count == 1) return Build(inputs[0]);
                return new Gate(SafetyFields.Or, 0, inputs.Select(Build).ToList());
            }
            finally { _path.Remove(e); }
        }

        private Node Leaf(Element e)
        {
            if (!_leafIndex.TryGetValue(e, out var i))
            {
                i = Leaves.Count;
                Leaves.Add(e);
                _leafIndex[e] = i;
            }
            return new Leaf(i);
        }

        private int VoteK(Element gate, int n)
        {
            var k = SafetyFields.Number(gate, SafetyFields.K) is { } v ? (int)Math.Round(v) : 0;
            if (k < 1 || k > n)
            {
                Warn(gate, $"Vote gate {GateName(gate)} needs k between 1 and {n} (has '{gate.Get(SafetyFields.K)}'); using {Math.Clamp(k, 1, n)}");
                k = Math.Clamp(k, 1, n);
            }
            return k;
        }

        private void Warn(Element e, string message)
        {
            if (_warned.Add(e)) Warnings.Add(message);
        }
    }

    public static string GateName(Element gate) => string.IsNullOrEmpty(gate.Name) ? GateSymbol(gate) : $"'{gate.Name}'";

    /// <summary>"AND", "OR" or "k/n".</summary>
    public static string GateSymbol(Element gate, ModelRepository? model = null) => SafetyFields.GateKind(gate) switch
    {
        SafetyFields.And => "AND",
        SafetyFields.Vote => $"{(gate.Get(SafetyFields.K) is { Length: > 0 } k ? k : "k")}/{(model != null ? Inputs(model, gate).Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "n")}",
        _ => "OR",
    };

    // ------------------------------------------------------------------ analysis

    private sealed class ProbabilityCache
    {
        public readonly Dictionary<string, double?> Values = [];
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ModelRepository, ProbabilityCache> Caches = new();

    /// <summary>
    /// The probability of an event or gate's output, cached until the model changes (for diagrams). Leaves give their own
    /// probability; null when it cannot be computed exactly or cheaply.
    /// </summary>
    public static double? CachedProbability(ModelRepository model, Element e)
    {
        var cache = Caches.GetValue(model, m =>
        {
            var c = new ProbabilityCache();
            m.Changed += (_, _) => { lock (c.Values) c.Values.Clear(); };
            return c;
        });
        lock (cache.Values)
            if (cache.Values.TryGetValue(e.Id, out var hit)) return hit;
        double? value;
        try
        {
            var b = new Builder(model);
            var root = b.Build(e);
            var probs = b.Leaves.Select(l => ProbabilityOf(model, l) ?? 0).ToArray();
            var occurrences = new int[b.Leaves.Count];
            Count(root, occurrences);
            var repeated = Enumerable.Range(0, occurrences.Length).Where(i => occurrences[i] > 1).ToList();
            value = repeated.Count <= 12 ? Exact(root, probs, repeated, new Dictionary<int, bool>()) : null;
        }
        catch (InvalidOperationException) { value = null; }
        lock (cache.Values) cache.Values[e.Id] = value;
        return value;
    }

    /// <summary>Minimal cut sets, top-event probability and importance measures for the tree under <paramref name="top"/>.</summary>
    public static FaultTreeResult Analyze(ModelRepository model, Element top)
    {
        var b = new Builder(model);
        var root = b.Build(top);
        var leaves = b.Leaves;
        var probs = new double[leaves.Count];
        for (var i = 0; i < leaves.Count; i++)
        {
            if (ProbabilityOf(model, leaves[i]) is { } p) probs[i] = p;
            else b.Warnings.Add($"Basic event '{leaves[i].DisplayName}' has no probability; 0 assumed");
        }

        var truncated = false;
        var sets = CutSets(root, ref truncated);
        var cutSets = sets.Select(s => new CutSet(s.Select(i => leaves[i]).ToList(), s.Aggregate(1.0, (acc, i) => acc * probs[i])))
            .OrderBy(c => c.Order).ThenByDescending(c => c.Probability).ToList();
        if (truncated) b.Warnings.Add($"More than {MaxCutSets} minimal cut sets: the list is truncated");

        var rare = Math.Min(1, cutSets.Sum(c => c.Probability));
        var upper = 1 - cutSets.Aggregate(1.0, (acc, c) => acc * (1 - c.Probability));

        // Exact evaluation by Shannon decomposition over leaves that occur more than once in the expanded tree.
        var occurrences = new int[leaves.Count];
        Count(root, occurrences);
        var repeated = Enumerable.Range(0, leaves.Count).Where(i => occurrences[i] > 1).ToList();
        double? exact = null;
        var canExact = repeated.Count <= MaxRepeatedForExact;
        if (canExact) exact = Exact(root, probs, repeated, new Dictionary<int, bool>());
        else b.Warnings.Add($"{repeated.Count} repeated basic events: exact probability skipped, the min-cut upper bound is shown instead");

        var importance = new List<EventImportance>();
        var cheapEnough = canExact && (1L << repeated.Count) * Math.Max(1, leaves.Count) <= 4_000_000;
        for (var i = 0; i < leaves.Count; i++)
        {
            if (leaves[i].Kind == ElementKind.FaultTreeEvent && SafetyFields.EventKind(leaves[i]) == SafetyFields.House) continue;
            var fv = rare > 0 ? cutSets.Where(c => c.Events.Contains(leaves[i])).Sum(c => c.Probability) / cutSets.Sum(c => c.Probability) : 0;
            double? birnbaum = null;
            if (cheapEnough)
            {
                var others = repeated.Where(r => r != i).ToList();
                birnbaum = Exact(root, probs, others, new Dictionary<int, bool> { [i] = true }) - Exact(root, probs, others, new Dictionary<int, bool> { [i] = false });
            }
            importance.Add(new EventImportance(leaves[i], probs[i], fv, birnbaum));
        }

        return new FaultTreeResult(top, cutSets, exact, rare, upper, leaves.ToList(),
            importance.OrderByDescending(x => x.FussellVesely).ThenByDescending(x => x.Birnbaum ?? 0).ToList(), b.Warnings, truncated);
    }

    private static void Count(Node n, int[] occurrences)
    {
        switch (n)
        {
            case Leaf l: occurrences[l.Index]++; break;
            case Gate g: foreach (var c in g.Inputs) Count(c, occurrences); break;
        }
    }

    /// <summary>P(node) with the given leaves fixed, conditioning on <paramref name="pending"/> repeated leaves first.</summary>
    private static double Exact(Node root, double[] probs, IReadOnlyList<int> pending, Dictionary<int, bool> fixedLeaves)
    {
        if (pending.Count == 0) return Eval(root, probs, fixedLeaves);
        var x = pending[0];
        var rest = pending.Skip(1).ToList();
        if (fixedLeaves.ContainsKey(x)) return Exact(root, probs, rest, fixedLeaves);
        var px = probs[x];
        fixedLeaves[x] = true;
        var on = px > 0 ? Exact(root, probs, rest, fixedLeaves) : 0;
        fixedLeaves[x] = false;
        var off = px < 1 ? Exact(root, probs, rest, fixedLeaves) : 0;
        fixedLeaves.Remove(x);
        return px * on + (1 - px) * off;
    }

    /// <summary>Bottom-up evaluation assuming the (unfixed) inputs of each gate are independent.</summary>
    private static double Eval(Node n, double[] probs, Dictionary<int, bool> fixedLeaves) => n switch
    {
        Leaf l => fixedLeaves.TryGetValue(l.Index, out var v) ? (v ? 1 : 0) : probs[l.Index],
        Constant c => c.Value ? 1 : 0,
        Gate { Op: SafetyFields.And } g => g.Inputs.Aggregate(1.0, (acc, c) => acc * Eval(c, probs, fixedLeaves)),
        Gate { Op: SafetyFields.Vote } g => AtLeast(g.K, g.Inputs.Select(c => Eval(c, probs, fixedLeaves)).ToList()),
        Gate g => 1 - g.Inputs.Aggregate(1.0, (acc, c) => acc * (1 - Eval(c, probs, fixedLeaves))),
        _ => 0,
    };

    /// <summary>Probability that at least <paramref name="k"/> of independent events with probabilities <paramref name="p"/> occur.</summary>
    public static double AtLeast(int k, IReadOnlyList<double> p)
    {
        // dist[j] = P(exactly j occurred so far)
        var dist = new double[p.Count + 1];
        dist[0] = 1;
        for (var i = 0; i < p.Count; i++)
            for (var j = i + 1; j >= 0; j--)
                dist[j] = dist[j] * (1 - p[i]) + (j > 0 ? dist[j - 1] * p[i] : 0);
        return dist.Skip(k).Sum();
    }

    // ------------------------------------------------------------------ minimal cut sets

    private static List<int[]> CutSets(Node n, ref bool truncated)
    {
        switch (n)
        {
            case Leaf l: return [[l.Index]];
            case Constant c: return c.Value ? [[]] : [];
            case Gate { Op: SafetyFields.And } g:
            {
                List<int[]> acc = [[]];
                foreach (var input in g.Inputs)
                {
                    var sets = CutSets(input, ref truncated);
                    acc = Product(acc, sets, ref truncated);
                    if (acc.Count == 0) break;
                }
                return acc;
            }
            case Gate { Op: SafetyFields.Vote } g:
            {
                var inputs = g.Inputs.Select(i => { var t = false; var s = CutSets(i, ref t); return (s, t); }).ToList();
                if (inputs.Any(x => x.t)) truncated = true;
                var all = new List<int[]>();
                foreach (var combo in Combinations(inputs.Count, g.K))
                {
                    List<int[]> acc = [[]];
                    foreach (var idx in combo) { acc = Product(acc, inputs[idx].s, ref truncated); if (acc.Count == 0) break; }
                    all.AddRange(acc);
                    if (all.Count > MaxCutSets * 4) { all = Minimize(all, ref truncated); }
                }
                return Minimize(all, ref truncated);
            }
            case Gate g:
            {
                var all = new List<int[]>();
                foreach (var input in g.Inputs) all.AddRange(CutSets(input, ref truncated));
                return Minimize(all, ref truncated);
            }
            default: return [];
        }
    }

    private static List<int[]> Product(List<int[]> a, List<int[]> b, ref bool truncated)
    {
        var result = new List<int[]>(Math.Min(a.Count * b.Count, MaxCutSets * 4));
        foreach (var x in a)
            foreach (var y in b)
            {
                result.Add(Union(x, y));
                if (result.Count > MaxCutSets * 4) result = Minimize(result, ref truncated);
            }
        return Minimize(result, ref truncated);
    }

    private static int[] Union(int[] x, int[] y)
    {
        var r = new int[x.Length + y.Length];
        int i = 0, j = 0, k = 0;
        while (i < x.Length && j < y.Length)
        {
            if (x[i] < y[j]) r[k++] = x[i++];
            else if (x[i] > y[j]) r[k++] = y[j++];
            else { r[k++] = x[i++]; j++; }
        }
        while (i < x.Length) r[k++] = x[i++];
        while (j < y.Length) r[k++] = y[j++];
        return k == r.Length ? r : r[..k];
    }

    /// <summary>Removes duplicates and supersets (absorption), keeping at most <see cref="MaxCutSets"/> (smallest first).</summary>
    private static List<int[]> Minimize(List<int[]> sets, ref bool truncated)
    {
        var kept = new List<int[]>();
        var keys = new HashSet<string>();
        foreach (var s in sets.OrderBy(s => s.Length))
        {
            if (!keys.Add(string.Join(",", s))) continue;
            if (kept.Any(k => k.Length < s.Length && IsSubset(k, s))) continue;
            kept.Add(s);
            if (kept.Count >= MaxCutSets) { truncated = true; break; }
        }
        return kept;
    }

    private static bool IsSubset(int[] small, int[] big)
    {
        int i = 0, j = 0;
        while (i < small.Length && j < big.Length)
        {
            if (small[i] == big[j]) { i++; j++; }
            else if (small[i] > big[j]) j++;
            else return false;
        }
        return i == small.Length;
    }

    private static IEnumerable<int[]> Combinations(int n, int k)
    {
        var idx = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return (int[])idx.Clone();
            var i = k - 1;
            while (i >= 0 && idx[i] == n - k + i) i--;
            if (i < 0) yield break;
            idx[i]++;
            for (var j = i + 1; j < k; j++) idx[j] = idx[j - 1] + 1;
        }
    }

    // ------------------------------------------------------------------ checks

    /// <summary>Structural problems of all fault trees in the model (gates without inputs, bad k, cycles, missing probabilities…).</summary>
    public static IEnumerable<(Element Element, string RuleId, Validation.Severity Severity, string Message)> Problems(ModelRepository model)
    {
        foreach (var g in model.OfKind(ElementKind.FaultTreeGate))
        {
            var n = Inputs(model, g).Count;
            if (n == 0) yield return (g, "FTA-001", Validation.Severity.Error, $"Fault tree gate {GateName(g)} has no inputs");
            else if (SafetyFields.GateKind(g) == SafetyFields.Vote)
            {
                var k = SafetyFields.Number(g, SafetyFields.K);
                if (k is not { } kv || kv != Math.Round(kv) || kv < 1 || kv > n)
                    yield return (g, "FTA-002", Validation.Severity.Error, $"Vote gate {GateName(g)} needs k between 1 and {n} (has '{g.Get(SafetyFields.K)}')");
            }
            if (Outputs(model, g).Count == 0) yield return (g, "FTA-003", Validation.Severity.Warning, $"Fault tree gate {GateName(g)} has no output event");
        }
        foreach (var e in model.OfKind(ElementKind.FaultTreeEvent))
        {
            var kind = SafetyFields.EventKind(e);
            var inputs = Inputs(model, e).Count;
            if (kind is SafetyFields.Basic or SafetyFields.Undeveloped)
            {
                if (inputs > 0) yield return (e, "FTA-004", Validation.Severity.Warning, $"{(kind == SafetyFields.Basic ? "Basic" : "Undeveloped")} event '{e.DisplayName}' has inputs; make it an intermediate event");
                if (ProbabilityOf(model, e) == null)
                    yield return (e, "FTA-005", Validation.Severity.Warning, e.Get(SafetyFields.Probability).Length > 0
                        ? $"Event '{e.DisplayName}' has an invalid probability '{e.Get(SafetyFields.Probability)}' (expected 0 … 1)"
                        : $"Basic event '{e.DisplayName}' has no probability (set one, a failure rate and time, or link a failure mode that has one)");
            }
            if (kind is SafetyFields.Top or SafetyFields.Intermediate && inputs == 0)
                yield return (e, "FTA-006", Validation.Severity.Warning, $"{(kind == SafetyFields.Top ? "Top" : "Intermediate")} event '{e.DisplayName}' has no inputs");
            if (e.Get(SafetyFields.Ref) is { Length: > 0 } rid && model.Find(rid) == null)
                yield return (e, "FTA-007", Validation.Severity.Warning, $"Event '{e.DisplayName}' represents an element that no longer exists");
        }
        foreach (var start in model.OfKind(ElementKind.FaultTreeEvent).Concat(model.OfKind(ElementKind.FaultTreeGate)))
            if (OnCycle(model, start)) yield return (start, "FTA-008", Validation.Severity.Error, $"'{(start.Kind == ElementKind.FaultTreeGate ? GateName(start) : start.DisplayName)}' is part of a fault tree cycle");
    }

    private static bool OnCycle(ModelRepository model, Element start)
    {
        var seen = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<Element>(Inputs(model, start));
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            if (ReferenceEquals(x, start)) return true;
            if (seen.Add(x)) foreach (var y in Inputs(model, x)) stack.Push(y);
        }
        return false;
    }
}
