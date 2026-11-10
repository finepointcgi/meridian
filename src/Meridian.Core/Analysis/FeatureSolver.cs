using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

/// <summary>What propagation concluded about one feature.</summary>
public enum FeatureState { Open, Selected, Implied, Deselected, Excluded }

/// <summary>One feature's state with the reason it was implied / excluded.</summary>
public sealed record FeatureVerdict(Element Feature, FeatureState State, string? Reason)
{
    public bool IsOn => State is FeatureState.Selected or FeatureState.Implied;
}

/// <summary>The result of propagating a (partial) selection through the feature model.</summary>
public sealed class FeatureAnalysis
{
    internal FeatureAnalysis(Dictionary<Element, FeatureVerdict> states, IReadOnlyList<string> conflicts)
    {
        States = states;
        Conflicts = conflicts;
    }

    public IReadOnlyDictionary<Element, FeatureVerdict> States { get; }

    /// <summary>Constraints that the decisions violate (empty when the selection can still be completed).</summary>
    public IReadOnlyList<string> Conflicts { get; }

    public bool Consistent => Conflicts.Count == 0;
    public FeatureVerdict this[Element f] => States[f];

    /// <summary>Features the decisions force on but which were not selected.</summary>
    public IEnumerable<Element> Implied => States.Values.Where(v => v.State == FeatureState.Implied).Select(v => v.Feature);

    /// <summary>Features that can no longer be selected.</summary>
    public IEnumerable<Element> Excluded => States.Values.Where(v => v.State == FeatureState.Excluded).Select(v => v.Feature);
}

/// <summary>
/// A small SAT engine over a feature model: the tree (root, parent, mandatory, alternative, or) and the cross-tree
/// Requires / Excludes constraints become clauses; decisions are propagated by unit propagation, and a one-step lookahead
/// (failed-literal probing) finds features that can no longer be selected or deselected. Also counts and enumerates
/// valid products with DPLL. Semantics match <see cref="Variability.Validate(ModelRepository, IReadOnlySet{Element}, Element?)"/>
/// with every root feature selected.
/// </summary>
public sealed class FeatureSolver
{
    private readonly List<Element> _features;
    private readonly Dictionary<Element, int> _index = new(ReferenceEqualityComparer.Instance);
    private readonly List<Clause> _clauses = new();

    /// <summary>Literals: +(i+1) = feature i selected, -(i+1) = not selected.</summary>
    private sealed record Clause(int[] Literals, string Description);

    public FeatureSolver(ModelRepository m)
    {
        _features = Variability.Tree(m).Select(t => t.Feature).ToList();
        for (var i = 0; i < _features.Count; i++) _index[_features[i]] = i;
        int L(Element f, bool on) => on ? _index[f] + 1 : -(_index[f] + 1);

        foreach (var f in _features)
        {
            var parent = Variability.Parent(m, f);
            if (parent is null || !_index.ContainsKey(parent))
            {
                _clauses.Add(new([L(f, true)], $"Root feature '{f.Name}' is always selected"));
                continue;
            }
            _clauses.Add(new([L(f, false), L(parent, true)], $"'{f.Name}' needs its parent '{parent.Name}'"));
            if (Variability.KindOf(f) == "mandatory")
                _clauses.Add(new([L(parent, false), L(f, true)], $"'{f.Name}' is mandatory in '{parent.Name}'"));
        }
        foreach (var p in _features)
        {
            var children = Variability.Children(m, p).Where(_index.ContainsKey).ToList();
            var alts = children.Where(c => Variability.KindOf(c) == "alternative").ToList();
            if (alts.Count > 0)
            {
                var names = string.Join(", ", alts.Select(a => a.Name));
                _clauses.Add(new([L(p, false), .. alts.Select(a => L(a, true))], $"'{p.Name}' needs one of {names}"));
                for (var i = 0; i < alts.Count; i++)
                    for (var j = i + 1; j < alts.Count; j++)
                        _clauses.Add(new([L(alts[i], false), L(alts[j], false)], $"'{alts[i].Name}' and '{alts[j].Name}' are alternatives"));
            }
            var ors = children.Where(c => Variability.KindOf(c) == "or").ToList();
            if (ors.Count > 0)
                _clauses.Add(new([L(p, false), .. ors.Select(o => L(o, true))], $"'{p.Name}' needs at least one of {string.Join(", ", ors.Select(o => o.Name))}"));
        }
        foreach (var r in m.Relationships.Where(r => r.Kind is ElementKind.Requires or ElementKind.Excludes))
        {
            if (!_index.ContainsKey(r.Source) || !_index.ContainsKey(r.Target)) continue;
            if (r.Kind == ElementKind.Requires) _clauses.Add(new([L(r.Source, false), L(r.Target, true)], $"'{r.Source.Name}' requires '{r.Target.Name}'"));
            else _clauses.Add(new([L(r.Source, false), L(r.Target, false)], $"'{r.Source.Name}' excludes '{r.Target.Name}'"));
        }
    }

    public IReadOnlyList<Element> Features => _features;
    public int ClauseCount => _clauses.Count;

    // ------------------------------------------------------------------ propagation

    /// <summary>Assignment: 0 unknown, 1 true, -1 false; with the clause that forced each implied value.</summary>
    private (sbyte[] Values, string?[] Reasons, string? Conflict) Propagate(IEnumerable<(int Var, bool On)> decisions)
    {
        var vals = new sbyte[_features.Count];
        var reasons = new string?[_features.Count];
        foreach (var (v, on) in decisions)
        {
            var want = (sbyte)(on ? 1 : -1);
            if (vals[v] == -want) return (vals, reasons, $"'{_features[v].Name}' is both selected and deselected");
            vals[v] = want;
        }
        bool changed;
        do
        {
            changed = false;
            foreach (var c in _clauses)
            {
                var unassigned = 0;
                var last = 0;
                var satisfied = false;
                foreach (var lit in c.Literals)
                {
                    var v = vals[Math.Abs(lit) - 1];
                    if (v == 0) { unassigned++; last = lit; }
                    else if (v == 1 == lit > 0) { satisfied = true; break; }
                }
                if (satisfied) continue;
                if (unassigned == 0) return (vals, reasons, c.Description);
                if (unassigned == 1)
                {
                    var i = Math.Abs(last) - 1;
                    vals[i] = (sbyte)(last > 0 ? 1 : -1);
                    reasons[i] = c.Description;
                    changed = true;
                }
            }
        } while (changed);
        return (vals, reasons, null);
    }

    /// <summary>
    /// Propagates decisions: <paramref name="selected"/> are on, <paramref name="deselected"/> off, the rest open.
    /// Features forced on are <see cref="FeatureState.Implied"/>, those forced off (or whose selection would contradict the
    /// decisions) <see cref="FeatureState.Excluded"/>.
    /// </summary>
    public FeatureAnalysis Analyze(IEnumerable<Element> selected, IEnumerable<Element>? deselected = null, bool lookahead = true)
    {
        var decisions = selected.Where(_index.ContainsKey).Select(f => (_index[f], true))
            .Concat((deselected ?? []).Where(_index.ContainsKey).Select(f => (_index[f], false))).Distinct().ToList();
        var decided = decisions.ToDictionary(d => d.Item1, d => d.Item2);
        var (vals, reasons, conflict) = Propagate(decisions);
        var conflicts = new List<string>();
        if (conflict != null) conflicts.Add(conflict);

        var states = new Dictionary<Element, FeatureVerdict>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < _features.Count; i++)
        {
            var f = _features[i];
            FeatureVerdict v;
            if (decided.TryGetValue(i, out var on)) v = new(f, on ? FeatureState.Selected : FeatureState.Deselected, null);
            else if (conflict == null && vals[i] == 1) v = new(f, FeatureState.Implied, reasons[i]);
            else if (conflict == null && vals[i] == -1) v = new(f, FeatureState.Excluded, reasons[i]);
            else v = new(f, FeatureState.Open, null);
            states[f] = v;
        }
        // Lookahead: an open feature whose selection leads to a conflict cannot be selected.
        if (lookahead && conflict == null)
            for (var i = 0; i < _features.Count; i++)
            {
                if (vals[i] != 0) continue;
                var (_, _, c) = Propagate(decisions.Append((i, true)));
                if (c != null) states[_features[i]] = new(_features[i], FeatureState.Excluded, c);
                else
                {
                    var (_, _, c2) = Propagate(decisions.Append((i, false)));
                    if (c2 != null) states[_features[i]] = new(_features[i], FeatureState.Implied, c2);
                }
            }
        return new FeatureAnalysis(states, conflicts);
    }

    /// <summary>
    /// Completes a selection after the user turned <paramref name="turnedOn"/> on or <paramref name="turnedOff"/> off: that
    /// decision wins, previously selected features that contradict it are dropped (keeping as many as possible, in tree
    /// order), and everything propagation then forces on is added.
    /// </summary>
    public HashSet<Element> Complete(IEnumerable<Element> selection, Element? turnedOn = null, Element? turnedOff = null)
    {
        var on = new List<Element>();
        var off = new List<Element>();
        if (turnedOn != null && _index.ContainsKey(turnedOn)) on.Add(turnedOn);
        if (turnedOff != null && _index.ContainsKey(turnedOff)) off.Add(turnedOff);
        // A decision that no product allows (deselecting a root or mandatory feature, selecting a dead one) is ignored.
        if (!Analyze(on, off, lookahead: false).Consistent || !Satisfiable(on.Select(f => (_index[f], true)).Concat(off.Select(f => (_index[f], false))).ToList()))
            return new HashSet<Element>(selection.Where(_index.ContainsKey), ReferenceEqualityComparer.Instance);
        var kept = new List<Element>(on);
        foreach (var f in selection.Where(_index.ContainsKey).Distinct().OrderBy(f => _index[f]))
        {
            if (on.Contains(f) || off.Contains(f)) continue;
            if (Analyze(kept.Append(f), off, lookahead: false).Consistent) kept.Add(f);
        }
        var result = new HashSet<Element>(kept, ReferenceEqualityComparer.Instance);
        var a = Analyze(kept, off, lookahead: false);
        if (a.Consistent) foreach (var f in a.Implied) result.Add(f);
        return result;
    }

    // ------------------------------------------------------------------ counting / enumeration

    /// <summary>Number of valid products, up to <paramref name="limit"/> (DPLL with propagation).</summary>
    public long CountProducts(long limit = 1_000_000)
    {
        long count = 0;
        Search([], _ => { count++; return count < limit; });
        return count;
    }

    /// <summary>Valid products (feature sets), up to <paramref name="max"/>.</summary>
    public IReadOnlyList<HashSet<Element>> Products(int max = 1000)
    {
        var list = new List<HashSet<Element>>();
        Search([], vals =>
        {
            list.Add(new HashSet<Element>(Enumerable.Range(0, _features.Count).Where(i => vals[i] == 1).Select(i => _features[i]), ReferenceEqualityComparer.Instance));
            return list.Count < max;
        });
        return list;
    }

    /// <summary>Depth-first search; a fully assigned consistent model is a product (unassigned features are off-branches tried both ways).</summary>
    private bool Search(List<(int, bool)> decisions, Func<sbyte[], bool> onProduct)
    {
        var (vals, _, conflict) = Propagate(decisions);
        if (conflict != null) return true;
        var free = Array.IndexOf(vals, (sbyte)0);
        if (free < 0) return onProduct(vals);
        decisions.Add((free, true));
        var go = Search(decisions, onProduct);
        decisions.RemoveAt(decisions.Count - 1);
        if (!go) return false;
        decisions.Add((free, false));
        go = Search(decisions, onProduct);
        decisions.RemoveAt(decisions.Count - 1);
        return go;
    }

    /// <summary>Features in no valid product (dead) and in every valid product (core).</summary>
    public (IReadOnlyList<Element> Dead, IReadOnlyList<Element> Core) DeadAndCore()
    {
        var dead = new List<Element>();
        var core = new List<Element>();
        for (var i = 0; i < _features.Count; i++)
        {
            if (!Satisfiable([(i, true)])) dead.Add(_features[i]);
            else if (!Satisfiable([(i, false)])) core.Add(_features[i]);
        }
        return (dead, core);
    }

    private bool Satisfiable(List<(int, bool)> decisions)
    {
        var found = false;
        Search(decisions, _ => { found = true; return false; });
        return found;
    }
}

/// <summary>Differences between two configurations of one product line.</summary>
public sealed record VariantComparison(
    Element A, Element B,
    IReadOnlyList<Element> FeaturesOnlyInA, IReadOnlyList<Element> FeaturesOnlyInB, IReadOnlyList<Element> CommonFeatures,
    IReadOnlyList<Element> ElementsOnlyInA, IReadOnlyList<Element> ElementsOnlyInB, int CommonElements)
{
    public bool Identical => FeaturesOnlyInA.Count == 0 && FeaturesOnlyInB.Count == 0 && ElementsOnlyInA.Count == 0 && ElementsOnlyInB.Count == 0;

    public static VariantComparison Compare(ModelRepository m, Element a, Element b)
    {
        var fa = Variability.Selected(m, a);
        var fb = Variability.Selected(m, b);
        var ra = Variability.Resolve(m, fa);
        var rb = Variability.Resolve(m, fb);
        var order = Variability.Tree(m).Select(t => t.Feature).ToList();
        static bool Shown(Element e) => e is not Diagram && e.Kind != ElementKind.Model;
        // Report the top-most differing elements (an excluded package stands for its contents).
        List<Element> Only(VariantResolution x, VariantResolution y) =>
            x.Included.Where(e => Shown(e) && y.Excluded.Contains(e) && (e.Owner is null || !(x.Included.Contains(e.Owner) && y.Excluded.Contains(e.Owner))))
             .OrderBy(e => e.QualifiedName, StringComparer.Ordinal).ToList();
        return new VariantComparison(a, b,
            order.Where(f => fa.Contains(f) && !fb.Contains(f)).ToList(),
            order.Where(f => fb.Contains(f) && !fa.Contains(f)).ToList(),
            order.Where(f => fa.Contains(f) && fb.Contains(f)).ToList(),
            Only(ra, rb), Only(rb, ra),
            ra.Included.Count(e => Shown(e) && rb.Included.Contains(e)));
    }
}
