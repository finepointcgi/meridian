using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

public sealed record ConfigurationProblem(Element? Feature, string Message);

/// <summary>Which elements belong to a variant (configuration) of a 150% model.</summary>
public sealed record VariantResolution(
    IReadOnlySet<Element> SelectedFeatures,
    IReadOnlySet<Element> Included,
    IReadOnlySet<Element> Excluded,
    IReadOnlyList<ConfigurationProblem> Problems)
{
    public bool IsIncluded(Element e) => !Excluded.Contains(e);

    /// <summary>Excluded elements whose owner is not excluded (deleting these removes the whole excluded set).</summary>
    public IEnumerable<Element> ExcludedRoots => Excluded.Where(e => e.Owner is null || !Excluded.Contains(e.Owner));
}

/// <summary>
/// Feature models (product lines), configurations and variability binding.
/// <para>
/// The feature tree is given by containment (a Feature owning Features) and/or by Feature Decomposition relationships
/// (parent → child), so it can be built in the containment tree or drawn on a Feature Model diagram. A child's
/// <see cref="ExtFieldKeys.FeatureKind"/> says how it relates to its parent: mandatory, optional, alternative (exactly one of
/// the alternative siblings) or or (at least one of the or siblings). Requires / Excludes relationships are cross-tree
/// constraints. A Configuration lists the selected feature ids. Any element may carry a presence condition, a
/// <see cref="FeatureExpression"/> over feature names; a variant contains exactly the elements whose condition holds
/// (and whose owner, and for relationships both ends, are in the variant).
/// </para>
/// </summary>
public static class Variability
{
    public static IEnumerable<Element> Features(ModelRepository m) => m.OfKind(ElementKind.Feature);

    public static Element? Parent(ModelRepository m, Element feature) =>
        feature.Owner is { Kind: ElementKind.Feature } o ? o
        : m.Incoming(feature, ElementKind.FeatureDecomposition).Select(r => r.Source).FirstOrDefault(s => s.Kind == ElementKind.Feature);

    public static IReadOnlyList<Element> Children(ModelRepository m, Element feature) =>
        feature.OwnedElements.Where(c => c.Kind == ElementKind.Feature)
            .Concat(m.Outgoing(feature, ElementKind.FeatureDecomposition).Select(r => r.Target).Where(t => t.Kind == ElementKind.Feature))
            .Distinct().ToList();

    public static IReadOnlyList<Element> Roots(ModelRepository m) =>
        Features(m).Where(f => Parent(m, f) is null).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>mandatory / optional / alternative / or (default optional; a root is always mandatory).</summary>
    public static string KindOf(Element feature) =>
        feature.Get(ExtFieldKeys.FeatureKind) is { Length: > 0 } k && ExtFieldKeys.FeatureKinds.Contains(k) ? k : "optional";

    /// <summary>Features in tree order (depth first) with their depth.</summary>
    public static IEnumerable<(Element Feature, int Depth)> Tree(ModelRepository m)
    {
        var seen = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        IEnumerable<(Element, int)> Walk(Element f, int depth)
        {
            if (!seen.Add(f)) yield break;   // guards against decomposition cycles
            yield return (f, depth);
            foreach (var c in Children(m, f))
                foreach (var x in Walk(c, depth + 1)) yield return x;
        }
        foreach (var r in Roots(m))
            foreach (var x in Walk(r, 0)) yield return x;
        // Features only reachable through a cycle.
        foreach (var f in Features(m).Where(f => !seen.Contains(f)).ToList())
            foreach (var x in Walk(f, 0)) yield return x;
    }

    public static Element? FindFeature(ModelRepository m, string name)
    {
        var n = name.Trim();
        if (m.Find(n) is { Kind: ElementKind.Feature } byId) return byId;
        var all = Features(m).ToList();
        return all.FirstOrDefault(f => f.Name == n)
               ?? all.FirstOrDefault(f => string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(f => string.Equals(f.Name.Replace(" ", ""), n.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ configurations

    public static IReadOnlyList<string> SelectedIds(Element configuration) =>
        configuration.Get(ExtFieldKeys.SelectedFeatures).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The features a configuration selects (entries may be ids or names).</summary>
    public static HashSet<Element> Selected(ModelRepository m, Element configuration)
    {
        var set = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        foreach (var id in SelectedIds(configuration))
            if ((m.Find(id) is { Kind: ElementKind.Feature } f ? f : FindFeature(m, id)) is { } feature) set.Add(feature);
        return set;
    }

    public static string Serialize(IEnumerable<Element> features) => string.Join(",", features.Select(f => f.Id).Distinct());

    /// <summary>Checks a selection against the feature tree and the cross-tree constraints.</summary>
    public static IReadOnlyList<ConfigurationProblem> Validate(ModelRepository m, IReadOnlySet<Element> selected, Element? configuration = null)
    {
        var problems = new List<ConfigurationProblem>();
        if (configuration != null)
            foreach (var id in SelectedIds(configuration))
                if (m.Find(id) is not { Kind: ElementKind.Feature } && FindFeature(m, id) is null)
                    problems.Add(new(null, $"Unknown feature '{id}'"));
        if (selected.Count == 0) { problems.Add(new(null, "No features are selected")); return problems; }

        foreach (var root in Roots(m))
        {
            var inTree = Tree(m).Where(t => ReferenceEquals(Root(m, t.Feature), root)).Select(t => t.Feature).ToList();
            if (inTree.Any(selected.Contains) && !selected.Contains(root))
                problems.Add(new(root, $"Root feature '{root.Name}' must be selected"));
        }

        foreach (var f in Features(m))
        {
            var parent = Parent(m, f);
            if (selected.Contains(f) && parent != null && !selected.Contains(parent))
                problems.Add(new(f, $"'{f.Name}' is selected but its parent '{parent.Name}' is not"));
            if (!selected.Contains(f)) continue;
            var children = Children(m, f);
            foreach (var c in children.Where(c => KindOf(c) == "mandatory" && !selected.Contains(c)))
                problems.Add(new(c, $"Mandatory feature '{c.Name}' of '{f.Name}' is not selected"));
            var alternatives = children.Where(c => KindOf(c) == "alternative").ToList();
            if (alternatives.Count > 0)
            {
                var n = alternatives.Count(selected.Contains);
                if (n != 1)
                    problems.Add(new(f, $"Exactly one of the alternatives of '{f.Name}' ({string.Join(", ", alternatives.Select(a => a.Name))}) must be selected, {n} are"));
            }
            var ors = children.Where(c => KindOf(c) == "or").ToList();
            if (ors.Count > 0 && !ors.Any(selected.Contains))
                problems.Add(new(f, $"At least one of '{f.Name}''s or-group ({string.Join(", ", ors.Select(a => a.Name))}) must be selected"));
        }

        foreach (var r in m.Relationships.Where(r => r.Kind == ElementKind.Requires))
            if (selected.Contains(r.Source) && !selected.Contains(r.Target))
                problems.Add(new(r.Source, $"'{r.Source.Name}' requires '{r.Target.Name}'"));
        foreach (var r in m.Relationships.Where(r => r.Kind == ElementKind.Excludes))
            if (selected.Contains(r.Source) && selected.Contains(r.Target))
                problems.Add(new(r.Source, $"'{r.Source.Name}' excludes '{r.Target.Name}'"));
        return problems;
    }

    public static IReadOnlyList<ConfigurationProblem> Validate(ModelRepository m, Element configuration) =>
        Validate(m, Selected(m, configuration), configuration);

    public static Element Root(ModelRepository m, Element feature)
    {
        var f = feature;
        var guard = 0;
        while (Parent(m, f) is { } p && guard++ < 1000) f = p;
        return f;
    }

    /// <summary>
    /// Propagates a selection: ancestors of selected features, mandatory children of selected features and targets of
    /// requires constraints are added (repeated to a fixpoint). Alternatives and or-groups are left to the user.
    /// </summary>
    public static HashSet<Element> Propagate(ModelRepository m, IEnumerable<Element> selection)
    {
        var set = new HashSet<Element>(selection, ReferenceEqualityComparer.Instance);
        bool changed;
        var guard = 0;
        do
        {
            changed = false;
            foreach (var f in set.ToList())
            {
                if (Parent(m, f) is { } p && set.Add(p)) changed = true;
                foreach (var c in Children(m, f).Where(c => KindOf(c) == "mandatory"))
                    if (set.Add(c)) changed = true;
                foreach (var r in m.Outgoing(f, ElementKind.Requires))
                    if (set.Add(r.Target)) changed = true;
            }
        } while (changed && guard++ < 1000);
        return set;
    }

    /// <summary>Every valid selection of the feature model, up to <paramref name="max"/> (see <see cref="FeatureSolver"/>).</summary>
    public static IReadOnlyList<HashSet<Element>> EnumerateProducts(ModelRepository m, int max = 1000) =>
        Features(m).Any() ? new FeatureSolver(m).Products(max) : [];

    // ------------------------------------------------------------------ presence conditions / resolution

    public static string PresenceCondition(Element e) => e.Get(ExtFieldKeys.PresenceCondition);

    /// <summary>Evaluates one element's own presence condition (not its owners'). Unknown names count as unselected.</summary>
    public static bool Holds(ModelRepository m, Element e, IReadOnlySet<Element> selected, List<ConfigurationProblem>? problems = null)
    {
        var cond = PresenceCondition(e);
        if (string.IsNullOrWhiteSpace(cond)) return true;
        try
        {
            return FeatureExpression.Evaluate(cond, name =>
            {
                var f = FindFeature(m, name);
                if (f is null) problems?.Add(new(null, $"{e.DisplayName}: unknown feature '{name}' in presence condition"));
                return f != null && selected.Contains(f);
            });
        }
        catch (FeatureExpression.FeatureExpressionException ex)
        {
            problems?.Add(new(null, $"{e.DisplayName}: {ex.Message} (element kept)"));
            return true;
        }
    }

    /// <summary>Works out which elements belong to the variant described by <paramref name="selected"/>.</summary>
    public static VariantResolution Resolve(ModelRepository m, IReadOnlySet<Element> selected)
    {
        var problems = new List<ConfigurationProblem>();
        var excluded = new HashSet<Element>(ReferenceEqualityComparer.Instance);

        void Walk(Element e, bool ownerExcluded)
        {
            var out_ = ownerExcluded || !Holds(m, e, selected, problems);
            if (out_) excluded.Add(e);
            foreach (var c in e.OwnedElements) Walk(c, out_);
        }
        Walk(m.Root, false);

        // Relationships whose ends are gone go too (and whatever they own).
        bool changed;
        do
        {
            changed = false;
            foreach (var r in m.Relationships)
                if (!excluded.Contains(r) && (excluded.Contains(r.Source) || excluded.Contains(r.Target)))
                {
                    excluded.Add(r);
                    foreach (var d in r.Descendants()) excluded.Add(d);
                    changed = true;
                }
        } while (changed);

        var included = new HashSet<Element>(m.AllElements.Where(e => !excluded.Contains(e)), ReferenceEqualityComparer.Instance);
        return new VariantResolution(selected, included, excluded, problems.DistinctBy(p => p.Message).ToList());
    }

    public static VariantResolution Resolve(ModelRepository m, Element configuration) => Resolve(m, Selected(m, configuration));

    /// <summary>
    /// Turns <paramref name="target"/> (a copy of the model the resolution was computed on, with the same ids) into the
    /// variant: excluded elements are deleted (with their diagram views and relationships) and presence conditions are
    /// cleared, since everything left is present. One undo step. Returns the number of elements removed.
    /// </summary>
    public static int ApplyTo(ModelRepository target, VariantResolution resolution, bool clearConditions = true)
    {
        var removed = 0;
        using (target.History.Transaction("Resolve variant"))
        {
            foreach (var e in resolution.ExcludedRoots.ToList())
                if (target.Find(e.Id) is { Owner: not null } copy && target.Contains(copy))
                {
                    removed += 1 + copy.Descendants().Count();
                    target.Delete(copy);
                }
            if (clearConditions)
                foreach (var e in target.AllElements.Where(e => PresenceCondition(e).Length > 0).ToList())
                    target.Set(e, ExtFieldKeys.PresenceCondition, null);
        }
        return removed;
    }

    /// <summary>
    /// Copies <paramref name="package"/> (with its contents, not diagrams) next to itself, removing what the variant
    /// excludes. Returns the copy. One undo step.
    /// </summary>
    public static Element CopyPackageAsVariant(ModelRepository m, Element package, VariantResolution resolution, string variantName)
    {
        using (m.History.Transaction($"Variant copy of {package.DisplayName}"))
        {
            var before = new HashSet<Relationship>(m.Relationships, ReferenceEqualityComparer.Instance);
            var map = m.Duplicate([package], package.Owner ?? m.Root, $" [{variantName}]");
            var copy = map[package];
            m.Rename(copy, $"{package.Name} [{variantName}]");
            var back = new Dictionary<Element, Element>(ReferenceEqualityComparer.Instance);
            foreach (var (o, d) in map) back[d] = o;

            // Relationships re-created between copies: find each one's original to know whether it is excluded.
            foreach (var r in m.Relationships.Where(r => !before.Contains(r) && back.ContainsKey(r.Source) && back.ContainsKey(r.Target)).ToList())
            {
                var orig = m.Relationships.FirstOrDefault(o => o.Kind == r.Kind && ReferenceEquals(o.Source, back[r.Source]) && ReferenceEquals(o.Target, back[r.Target]));
                if (orig != null && resolution.Excluded.Contains(orig) && m.Contains(r)) m.Delete(r);
                else if (m.Contains(r) && PresenceCondition(r).Length > 0) m.Set(r, ExtFieldKeys.PresenceCondition, null);
            }
            foreach (var (orig, dup) in map)
            {
                if (!m.Contains(dup)) continue;
                if (resolution.Excluded.Contains(orig) && dup.Owner != null) m.Delete(dup);
                else if (PresenceCondition(dup).Length > 0) m.Set(dup, ExtFieldKeys.PresenceCondition, null);
            }
            return copy;
        }
    }

    /// <summary>CSV listing each element with its presence condition and whether the variant includes it.</summary>
    public static string InclusionCsv(ModelRepository m, VariantResolution res)
    {
        static string Q(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new System.Text.StringBuilder("Element,Kind,Presence condition,Included\n");
        foreach (var e in m.AllElements.Where(e => e is not Diagram && e.Kind != ElementKind.Model).OrderBy(e => e.QualifiedName, StringComparer.Ordinal))
            sb.Append(Q(e is Relationship r ? $"{r.Source.QualifiedName} -> {r.Target.QualifiedName}" : e.QualifiedName)).Append(',')
              .Append(Metamodel.DisplayName(e.Kind)).Append(',').Append(Q(PresenceCondition(e))).Append(',')
              .Append(res.IsIncluded(e) ? "yes" : "no").Append('\n');
        return sb.ToString();
    }
}
