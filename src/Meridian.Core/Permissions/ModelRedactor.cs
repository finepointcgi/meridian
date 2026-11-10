using Meridian.Core.Model;

namespace Meridian.Core.Permissions;

/// <summary>
/// Builds a user's filtered view of a model: everything the user may not see (<see cref="AccessView.IsHidden"/>) is removed,
/// and shapes on visible diagrams that showed a hidden element become nameless placeholders (<see cref="IsPlaceholder"/>).
/// Connectors to placeholders and of hidden relationships are removed. The placeholder elements are not part of the model
/// (no owner, not indexed): they exist only to be drawn, carry no data from the hidden element, and their ids are derived
/// from the shape's id, so nothing of the hidden element — not even its id — is in the result.
/// </summary>
public static class ModelRedactor
{
    /// <summary>Id prefix of placeholder elements. The project-file reader recreates placeholders for shapes that reference such ids.</summary>
    public const string PlaceholderPrefix = "_redacted_";

    /// <summary>Field set on placeholder elements.</summary>
    public const string PlaceholderField = "redacted";

    public static bool IsPlaceholder(Element e) => e.Owner == null && e.Kind == ElementKind.Shape && e.Id.StartsWith(PlaceholderPrefix, StringComparison.Ordinal);

    /// <summary>A fresh placeholder element (for a shape whose element is hidden).</summary>
    public static Element Placeholder(string id)
    {
        var e = new Element(ElementKind.Shape, id.StartsWith(PlaceholderPrefix, StringComparison.Ordinal) ? id : PlaceholderPrefix + id);
        e.SetRaw(FieldKeys.Subtype, "rectangle");
        e.SetRaw(PlaceholderField, "true");
        return e;
    }

    /// <summary>
    /// Redacts <paramref name="model"/> in place for <paramref name="view"/> and returns how many elements were removed.
    /// Pass a private copy (e.g. freshly deserialized): the model is changed without undo history or change events.
    /// </summary>
    public static int Redact(ModelRepository model, AccessView view)
    {
        if (!view.HidesAnything) return 0;
        var hidden = new HashSet<Element>(model.AllElements.Where(view.IsHidden), ReferenceEqualityComparer.Instance);
        if (hidden.Count == 0) return 0;

        foreach (var d in model.Diagrams.Where(d => !hidden.Contains(d)).ToList())
        {
            var replaced = new HashSet<DiagramNode>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < d.NodeList.Count; i++)
            {
                var n = d.NodeList[i];
                if (!hidden.Contains(n.Element)) continue;
                replaced.Add(n);
                // Same shape id and place (layout edits keep replicating); no style (it may carry text or links).
                d.NodeList[i] = new DiagramNode(Placeholder(n.Id), n.Bounds, n.Id) { ParentId = n.ParentId };
            }
            d.EdgeList.RemoveAll(x => hidden.Contains(x.Relationship) || replaced.Contains(x.Source) || replaced.Contains(x.Target));
        }

        // References from visible elements (a part typed by a hidden block, a call of a hidden behavior…) are cleared, so the
        // hidden elements' ids don't remain either.
        var hiddenIds = hidden.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var e in model.AllElements.Where(e => !hidden.Contains(e)).ToList())
            foreach (var (key, value) in e.Fields.ToList())
            {
                if (hiddenIds.Contains(value)) e.SetRaw(key, null);
                else if (value.Contains(',') && value.Split(',').Any(p => hiddenIds.Contains(p.Trim())))
                    e.SetRaw(key, string.Join(",", value.Split(',').Where(p => !hiddenIds.Contains(p.Trim()))));
            }

        var removed = 0;
        foreach (var e in hidden.Where(e => e.Owner is { } o && !hidden.Contains(o)).ToList())
        {
            removed += 1 + e.Descendants().Count();
            model.DetachForRedaction(e);
        }
        return removed;
    }
}
