using Meridian.Core.Model;
using Meridian.Core.Validation;

namespace Meridian.Core.Instances;

/// <summary>
/// Instance checks (rule ids INST-001 … INST-004):
/// <list type="bullet">
/// <item>INST-001 a slot whose feature no longer exists on the instance's classifiers (deleted, or the classifier changed): delete or remap it</item>
/// <item>INST-002 a slot whose value doesn't fit its feature (literal for a part, wrong enumeration, non-number for a value with a unit, instance of the wrong type …)</item>
/// <item>INST-003 a required feature (lower multiplicity ≥ 1) with no slot and no default value (values computed by parametrics are exempt)</item>
/// <item>INST-004 an instance with no classifier, or a classifier that no longer exists</item>
/// </list>
/// </summary>
public sealed class InstanceRules : IValidationRule
{
    public const string StaleSlot = "INST-001";
    public const string TypeMismatch = "INST-002";
    public const string MissingSlot = "INST-003";
    public const string MissingClassifier = "INST-004";

    public string Id => "INST";
    public string Description => "Instances: stale slots, slot type mismatches, missing required slots, classifiers";

    public IEnumerable<ValidationIssue> Check(ModelRepository m)
    {
        foreach (var inst in m.OfKind(ElementKind.InstanceSpecification))
        {
            var ids = InstanceModel.ClassifierIds(inst);
            if (ids.Count == 0) { yield return new(Severity.Warning, MissingClassifier, $"Instance '{inst.Name}' has no classifier", inst); continue; }
            foreach (var id in ids.Where(id => m.Find(id) is null))
                yield return new(Severity.Error, MissingClassifier, $"Instance '{inst.Name}' is classified by an element that no longer exists", inst);

            var features = InstanceModel.InstanceFeatures(m, inst);
            foreach (var slot in InstanceModel.Slots(inst))
            {
                var f = InstanceModel.FeatureOf(m, slot);
                if (f == null || !features.Contains(f))
                {
                    yield return new(Severity.Error, StaleSlot, f == null
                        ? $"Slot of '{inst.Name}' refers to a feature that no longer exists (delete it or remap it to another feature)"
                        : $"Slot '{f.Name}' of '{inst.Name}' is not a feature of {string.Join(", ", InstanceModel.Classifiers(m, inst).Select(c => c.Name))} (delete it or remap it)", slot);
                    continue;
                }
                if (Mismatch(m, slot, f) is { } why) yield return new(Severity.Error, TypeMismatch, $"{inst.Name}.{f.Name}: {why}", slot);
            }

            foreach (var f in features)
            {
                if (InstanceModel.SlotFor(inst, f) != null || f.Get(FieldKeys.DefaultValue).Length > 0) continue;
                if (InstanceModel.Bounds(f.Multiplicity).Lower < 1) continue;
                if (m.RelationshipsOf(f).Any(r => r.Kind == ElementKind.Binding)) continue;   // computed by parametrics
                yield return new(Severity.Warning, MissingSlot, $"Instance '{inst.Name}' has no value for required feature '{f.Name}'", inst);
            }
        }
    }

    /// <summary>Why a slot's value doesn't fit its feature, or null when it does.</summary>
    public static string? Mismatch(ModelRepository m, Element slot, Element feature)
    {
        var kind = InstanceModel.KindOf(slot);
        var value = slot.Get(InstanceFields.Value);
        if (kind == SlotValueKind.Expression) return value.Trim().Length == 0 ? "empty expression" : null;
        if (InstanceModel.IsInstanceValued(m, feature))
        {
            var type = m.ResolveType(feature)!;
            if (kind != SlotValueKind.Instance) return $"expects instances of {type.Name}, not '{InstanceModel.DisplayValue(m, slot)}'";
            var refs = InstanceModel.ValueIds(slot);
            if (refs.Any(id => m.Find(id) is not { Kind: ElementKind.InstanceSpecification })) return "refers to an instance that no longer exists";
            var wrong = refs.Select(m.Find).OfType<Element>().FirstOrDefault(i => !InstanceModel.Classifiers(m, i).Any(c => InstanceModel.Conforms(m, c, type)));
            if (wrong != null) return $"'{wrong.Name}' is not an instance of {type.Name}";
            var (_, upper) = InstanceModel.Bounds(feature.Multiplicity);
            if (upper is { } u && refs.Count > u) return $"{refs.Count} values for multiplicity [{feature.Multiplicity}]";
            return null;
        }
        if (InstanceModel.IsEnumValued(m, feature))
        {
            var en = m.ResolveType(feature)!;
            if (kind != SlotValueKind.Enum) return $"expects a literal of {en.Name}, not '{value}'";
            return InstanceModel.ValueIds(slot).Select(m.Find).Any(l => l is null || !ReferenceEquals(l.Owner, en)) ? $"is not a literal of {en.Name}" : null;
        }
        if (kind != SlotValueKind.Literal) return $"expects a value, not {(kind == SlotValueKind.Instance ? "an instance" : "an enumeration literal")}";
        try { InstanceModel.Parse(m, feature, value, slot.Owner); }
        catch (InvalidOperationException ex) { return ex.Message.StartsWith(feature.Name + ": ", StringComparison.Ordinal) ? ex.Message[(feature.Name.Length + 2)..] : ex.Message; }
        return null;
    }
}
