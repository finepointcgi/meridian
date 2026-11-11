using System.Globalization;
using Meridian.Core.Model;

namespace Meridian.Core.Instances;

/// <summary>
/// Instances as the subject of analyses: verification runs and parametric solves use an instance's slot values in place
/// of its classifiers' default values. Nested part instances give the values of the part blocks (vehicle1.engine's
/// maxPower overrides Engine::maxPower).
/// </summary>
public static class InstanceAnalysis
{
    /// <summary>
    /// Writes the slot values of <paramref name="instance"/> (and its nested and referenced part instances) as default values
    /// of their features on <paramref name="work"/>, a copy of the model. When two instances of the same block give a
    /// feature different values, the first one wins and a problem says so. Returns the block to analyse (the instance's
    /// main classifier in the copy), or null when it has none.
    /// </summary>
    public static Element? ApplyTo(ModelRepository work, Element instance, List<string> problems)
    {
        var applied = new Dictionary<string, (Element Instance, string Value)>(StringComparer.Ordinal);
        var visited = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        void Apply(Element inst)
        {
            if (!visited.Add(inst) || visited.Count > InstanceModel.MaxTreeInstances) return;
            foreach (var slot in InstanceModel.Slots(inst))
            {
                if (InstanceModel.FeatureOf(work, slot) is not { } f) continue;
                if (InstanceModel.KindOf(slot) == SlotValueKind.Instance)
                {
                    foreach (var part in InstanceModel.ReferencedInstances(work, slot)) Apply(part);
                    continue;
                }
                var value = InstanceModel.KindOf(slot) switch
                {
                    SlotValueKind.Enum => string.Join(",", InstanceModel.ValueIds(slot).Select(id => work.Find(id)?.Name ?? id)),
                    SlotValueKind.Expression => slot.Get(InstanceFields.Value),
                    _ => InstanceModel.NumericValue(work, slot) is { } n ? n.ToString("R", CultureInfo.InvariantCulture) : slot.Get(InstanceFields.Value),
                };
                if (applied.TryGetValue(f.Id, out var prior))
                {
                    if (prior.Value != value)
                        problems.Add($"{f.Owner?.Name}.{f.Name}: '{inst.Name}' and '{prior.Instance.Name}' give different values; {prior.Value} from '{prior.Instance.Name}' is used");
                    continue;
                }
                applied[f.Id] = (inst, value);
                work.Set(f, FieldKeys.DefaultValue, value);
            }
            foreach (var nested in InstanceModel.NestedInstances(inst)) Apply(nested);
        }
        Apply(instance);
        return InstanceModel.MainClassifier(work, instance);
    }

    /// <summary>
    /// Numeric slot values of an instance for the value properties of <paramref name="block"/> (by property id, in each
    /// property's unit): the overrides <see cref="Analysis.Parametrics.Solve"/> takes for a what-if solve of that instance.
    /// </summary>
    public static Dictionary<string, double> ValueOverrides(ModelRepository m, Element instance, Element block)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var slot in InstanceModel.Slots(instance))
        {
            if (InstanceModel.FeatureOf(m, slot) is not { } f || !ReferenceEquals(f.Owner, block) && !InstanceModel.Features(m, block).Contains(f)) continue;
            if (InstanceModel.NumericValue(m, slot) is { } v) map[f.Id] = v;
            else if (InstanceModel.KindOf(slot) == SlotValueKind.Expression)
            {
                try { map[f.Id] = Analysis.Expressions.Evaluate(slot.Get(InstanceFields.Value), new Dictionary<string, double>()); }
                catch (Analysis.Expressions.ExpressionException) { /* left to the solver's own default */ }
            }
        }
        return map;
    }

    /// <summary>Ids of the instance's subtree (instances and slots), for staleness fingerprints of results computed from it.</summary>
    public static IEnumerable<string> DependencyIds(Element instance) =>
        instance.Descendants().Prepend(instance).Select(e => e.Id);
}
