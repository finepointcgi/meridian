using System.Text;
using Meridian.Core.Derivation;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization;
using Meridian.Web.Components.Workbench;

namespace Meridian.Web.Services;

/// <summary>
/// UI helpers for "create diagrams from diagrams": the menu entries a derivable element gets, and the "update from source"
/// preview — the plan applied to a copy of the model and drawn with the code-sync change highlights (green added, amber
/// changed, dashed ghosts for removed).
/// </summary>
public static class DerivationUi
{
    /// <summary>Header plus one entry per valid derivation of <paramref name="e"/> (empty when there is none or the project is read-only).</summary>
    public static IReadOnlyList<MenuEntry> MenuEntries(ModelRepository m, Element? e, bool readOnly, Func<DerivationOffer, Task> run)
    {
        if (readOnly || e == null) return [];
        var offers = DiagramDerivation.Offers(m, e);
        if (offers.Count == 0) return [];
        var list = new List<MenuEntry> { MenuEntry.Title("Create from…") };
        foreach (var o in offers)
        {
            var offer = o;
            list.Add(MenuEntry.Async(offer.Label + "…", () => run(offer)));
        }
        return list;
    }

    /// <summary>Change highlight per element id, from what applying the plan did.</summary>
    public static Dictionary<string, ChangeHighlight> Highlights(DerivationResult r)
    {
        var map = new Dictionary<string, ChangeHighlight>(StringComparer.Ordinal);
        foreach (var id in r.CreatedIds) map[id] = ChangeHighlight.Added;
        foreach (var id in r.ChangedIds) map[id] = ChangeHighlight.Changed;
        foreach (var id in r.RemovedIds) map[id] = ChangeHighlight.Removed;
        return map;
    }

    /// <summary>
    /// The derived diagram as it would look after the update, as SVG with change highlights; null when the plan has no earlier
    /// diagram to compare with (a first derivation) or the preview cannot be made.
    /// </summary>
    public static string? Preview(ModelRepository m, DerivationPlan plan, SvgTheme? theme = null)
    {
        if (!plan.IsUpdate || plan.Items.FirstOrDefault(i => i.IsDiagram && i.Existing is Diagram) is not { Existing: Diagram original }) return null;
        try
        {
            var copy = ProjectFile.Copy(m);
            if (copy.Find(plan.Source.Id) is not { } src) return null;
            var again = DiagramDerivation.Plan(copy, plan.Kind, src, plan.Target is { } t ? copy.Find(t.Id) : null, plan.Options);
            foreach (var i in again.Items)
                if (plan[i.Key] is { } mine) i.Selected = mine.Selected;
            var result = DiagramDerivation.Apply(copy, again);
            if (copy.Find(original.Id) is not Diagram d) return null;
            var svg = SvgRenderer.Render(copy, d, theme: theme);
            var sb = new StringBuilder();
            SvgRenderer.RenderChangeHighlights(sb, d, n => n.Bounds, x => x.Waypoints, Highlights(result), original);
            var end = svg.LastIndexOf("</svg>", StringComparison.Ordinal);
            return end < 0 ? svg : svg[..end] + sb + "</svg>";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>"new" / "change" / "remove" and the highlight class of a row.</summary>
    public static (string Text, string Css) Status(DerivedChange c) => c switch
    {
        DerivedChange.Add => ("new", SvgRenderer.ChangeClass(ChangeHighlight.Added)),
        DerivedChange.Change => ("change", SvgRenderer.ChangeClass(ChangeHighlight.Changed)),
        _ => ("remove", SvgRenderer.ChangeClass(ChangeHighlight.Removed)),
    };
}
