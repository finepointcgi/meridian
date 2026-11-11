using Meridian.Core.Instances;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>
/// Enterprise Architect objects (t_object.Object_Type = 'Object') as instance specifications: the classifier from
/// t_object.Classifier, slot values from t_object.RunState
/// (<c>@VAR;Variable=mass;Value=1500;Op==;@ENDVAR;@VAR;…</c>), and object-to-object associations as links.
/// </summary>
internal static class EaInstances
{
    /// <summary>The (variable, value) pairs of an EA run state.</summary>
    public static IReadOnlyList<(string Variable, string Value)> ParseRunState(string runState)
    {
        var list = new List<(string, string)>();
        foreach (var chunk in (runState ?? "").Split("@ENDVAR;", StringSplitOptions.RemoveEmptyEntries))
        {
            var start = chunk.IndexOf("@VAR;", StringComparison.Ordinal);
            if (start < 0) continue;
            string? variable = null, value = null;
            foreach (var part in chunk[(start + 5)..].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var k = part[..eq].Trim();
                var v = part[(eq + 1)..];
                if (k.Equals("Variable", StringComparison.OrdinalIgnoreCase)) variable = v.Trim();
                else if (k.Equals("Value", StringComparison.OrdinalIgnoreCase)) value = v.Trim();
            }
            if (!string.IsNullOrEmpty(variable) && value != null) list.Add((variable, value));
        }
        return list;
    }

    public static void Resolve(ModelRepository model, IReadOnlyList<(Element e, int classifier, string runState)> instances, IReadOnlyDictionary<int, Element> objById, List<string> warnings)
    {
        foreach (var (inst, cid, runState) in instances)
        {
            var cls = cid > 0 ? objById.GetValueOrDefault(cid) : null;
            if (cls != null && InstanceModel.CanClassify(cls.Kind)) ModelRepository.SetFieldForLoad(inst, InstanceFields.Classifier, cls.Id);
            var values = ParseRunState(runState);
            if (values.Count == 0) continue;
            if (cls == null) { warnings.Add($"Object '{inst.Name}': run state values skipped (it has no classifier)"); continue; }
            var features = InstanceModel.Features(model, cls);
            foreach (var (variable, value) in values)
            {
                var f = features.FirstOrDefault(x => x.Name == variable) ?? features.FirstOrDefault(x => string.Equals(x.Name, variable, StringComparison.OrdinalIgnoreCase));
                if (f == null) { warnings.Add($"Object '{inst.Name}': run state variable '{variable}' is not an attribute of {cls.Name}"); continue; }
                var kind = SlotValueKind.Literal;
                var text = value;
                if (InstanceModel.IsEnumValued(model, f) && model.ResolveType(f)!.OwnedElements.FirstOrDefault(l => l.Kind == ElementKind.EnumerationLiteral && l.Name == value) is { } lit)
                    (kind, text) = (SlotValueKind.Enum, lit.Id);
                var slot = new Element(ElementKind.Slot);
                ModelRepository.SetFieldForLoad(slot, InstanceFields.Feature, f.Id);
                ModelRepository.SetFieldForLoad(slot, InstanceFields.ValueKind, InstanceModel.KindCode(kind));
                ModelRepository.SetFieldForLoad(slot, InstanceFields.Value, text);
                model.AttachForLoad(inst, slot);
            }
        }
    }

    /// <summary>Types each imported link by the one association between its instances' classifiers, when there is exactly one.</summary>
    public static void TypeLinks(ModelRepository model)
    {
        foreach (var link in model.Relationships.Where(r => r.Kind == ElementKind.Link).ToList())
            if (InstanceModel.MatchingAssociations(model, link.Source, link.Target) is [var only])
                ModelRepository.SetFieldForLoad(link, InstanceFields.Association, only.Id);
    }
}
