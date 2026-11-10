using Meridian.Core.Model;

namespace Meridian.Core.Language;

/// <summary>Notation for feature models, configurations, trade studies and presence conditions.</summary>
public static class VariabilityNotation
{
    /// <summary>Feature tree edges: filled dot = mandatory, hollow dot = optional; alternative / or groups get an arc at the parent.</summary>
    public static EdgeStyle DecompositionStyle(Relationship r) =>
        r.Target.Get(ExtFieldKeys.FeatureKind) switch
        {
            "mandatory" => new(false, ArrowHead.Dot, ArrowHead.None, null),
            "alternative" or "or" => new(false, ArrowHead.None, ArrowHead.None, null),
            _ => new(false, ArrowHead.HollowDot, ArrowHead.None, null),
        };

    public static void Decorate(ModelRepository model, Element e, ref string? keyword, List<Compartment> comps)
    {
        switch (e.Kind)
        {
            case ElementKind.Feature:
                var kind = e.Get(ExtFieldKeys.FeatureKind);
                if (kind is "alternative" or "or" or "mandatory" or "optional") keyword = $"«{kind}»";
                break;
            case ElementKind.Configuration:
                var names = e.Get(ExtFieldKeys.SelectedFeatures).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(id => model.Find(id)?.Name ?? id).ToList();
                comps.Add(new Compartment("features", names.Count == 0 ? ["(none selected)"] : names));
                break;
            case ElementKind.TradeStudy:
                comps.Add(new Compartment("criteria", Analysis.TradeStudyDefinition.Parse(e).Criteria.Select(c => $"{c.Name} ×{c.Weight:G3} ({c.Direction})").DefaultIfEmpty("(none)").ToList()));
                break;
        }
        if (e.Get(ExtFieldKeys.PresenceCondition) is { Length: > 0 } pc)
            comps.Add(new Compartment(null, [$"«variant» {{{pc}}}"]));
        if (e.Kind == ElementKind.Action)
        {
            var extra = new List<string>();
            if (e.Get(ExtFieldKeys.Duration) is { Length: > 0 } d) extra.Add($"duration = {d}");
            if (e.Get(ExtFieldKeys.AcceptEvent) is { Length: > 0 } a) extra.Add($"accept {a}");
            if (e.Get(ExtFieldKeys.SendSignal) is { Length: > 0 } s) extra.Add($"send {s}");
            if (extra.Count > 0) comps.Add(new Compartment(null, extra));
        }
    }
}
