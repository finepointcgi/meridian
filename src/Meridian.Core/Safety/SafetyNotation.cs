using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Safety;

/// <summary>What safety elements show on diagrams: failure mode compartments and the failure modes of a block.</summary>
public static class SafetyNotation
{
    public static IEnumerable<Compartment> FailureModeCompartments(ModelRepository model, Element fm)
    {
        var lines = new List<string>();
        if (fm.Get(SafetyFields.Effect) is { Length: > 0 } effect) lines.Add($"effect: {effect}");
        if (fm.Get(SafetyFields.Cause) is { Length: > 0 } cause) lines.Add($"cause: {cause}");
        var row = Fmea.Row(model, fm);
        if (row.Severity != null || row.Occurrence != null || row.Detection != null)
            lines.Add($"S {row.Severity?.ToString() ?? "–"} · O {row.Occurrence?.ToString() ?? "–"} · D {row.Detection?.ToString() ?? "–"}"
                      + (row.Rpn is { } rpn ? $" · RPN {rpn}" : "") + (row.Priority is { } ap ? $" · AP {Fmea.Label(ap)}" : ""));
        if (lines.Count > 0) yield return new Compartment(null, lines);
    }

    /// <summary>
    /// Fault tree links without manual bend points run tree-style: down from the bottom centre of the output, across at
    /// a short drop, and down into the top centre of the input. Null when the input is not below the output.
    /// </summary>
    public static IReadOnlyList<Pt>? TreeRoute(DiagramEdge e, Func<DiagramNode, Bounds> boundsOf, IReadOnlyList<Pt>? waypoints)
    {
        if (e.Relationship.Kind != ElementKind.FaultTreeLink || (waypoints ?? e.Waypoints).Count > 0) return null;
        var s = boundsOf(e.Source);
        var t = boundsOf(e.Target);
        if (t.Y < s.Bottom + 8) return null;
        var from = new Pt(s.Center.X, s.Bottom);
        var to = new Pt(t.Center.X, t.Y);
        if (Math.Abs(from.X - to.X) < 0.5) return [from, to];
        var mid = Math.Round(s.Bottom + Math.Min(24, (t.Y - s.Bottom) / 2));   // siblings share one horizontal bus
        return [from, new Pt(from.X, mid), new Pt(to.X, mid), to];
    }

    /// <summary>A "failure modes" compartment for a block that owns failure modes (null when it owns none).</summary>
    public static Compartment? FailureModesCompartment(Element owner)
    {
        var lines = owner.OwnedElements.Where(c => c.Kind == ElementKind.FailureMode)
            .Select(fm => Fmea.Rpn(fm) is { } rpn ? $"{fm.Name} (RPN {rpn})" : fm.Name).ToList();
        return lines.Count == 0 ? null : new Compartment("failure modes", lines);
    }
}
