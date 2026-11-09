using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

/// <summary>Auto-numbered requirement ids ("REQ-001") with a per-model configurable prefix.</summary>
public static class RequirementIds
{
    public static string Prefix(ModelRepository model) =>
        model.Root.Get(ReqFields.IdPrefix) is { Length: > 0 } p ? p : ReqFields.DefaultIdPrefix;

    /// <summary>Changes the prefix used for new ids (undoable; stored on the model root so it replicates).</summary>
    public static void SetPrefix(ModelRepository model, string? prefix)
    {
        prefix = prefix?.Trim();
        model.Set(model.Root, ReqFields.IdPrefix, string.IsNullOrEmpty(prefix) || prefix == ReqFields.DefaultIdPrefix ? null : prefix);
    }

    public static string Format(string prefix, int n) => $"{prefix}{n:000}";

    /// <summary>Requirements in document order: containment depth first, siblings in model order.</summary>
    public static IEnumerable<Element> InDocumentOrder(ModelRepository model)
    {
        IEnumerable<Element> Walk(Element e)
        {
            if (e.Kind == ElementKind.Requirement) yield return e;
            foreach (var c in e.OwnedElements)
                if (c is not Relationship && c is not Diagram)
                    foreach (var x in Walk(c)) yield return x;
        }
        return Walk(model.Root);
    }

    /// <summary>
    /// Renumbers every requirement as prefix + 001, 002, … in document order. One undoable step.
    /// Returns how many ids changed.
    /// </summary>
    public static int Renumber(ModelRepository model, string? prefix = null)
    {
        prefix ??= Prefix(model);
        var reqs = InDocumentOrder(model).ToList();
        var changed = 0;
        using (model.History.Transaction("Renumber requirements"))
        {
            for (var i = 0; i < reqs.Count; i++)
            {
                var id = Format(prefix, i + 1);
                if (reqs[i].Get(FieldKeys.RequirementId) == id) continue;
                model.Set(reqs[i], FieldKeys.RequirementId, id);
                changed++;
            }
        }
        return changed;
    }
}
