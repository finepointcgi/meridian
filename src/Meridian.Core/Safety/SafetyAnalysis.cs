using Meridian.Core.Model;
using Meridian.Core.Validation;

namespace Meridian.Core.Safety;

/// <summary>
/// Safety &amp; reliability validation: FMEA rules (SAF-*) and fault tree structure rules (FTA-*).
/// </summary>
public sealed class SafetyValidationRules : IValidationRule
{
    public string Id => "SAF";
    public string Description => "Safety & reliability: FMEA ratings, high-risk failure modes without mitigation, fault tree structure";

    public IEnumerable<ValidationIssue> Check(ModelRepository m)
    {
        var modes = m.OfKind(ElementKind.FailureMode).ToList();
        if (modes.Count > 0)
        {
            var threshold = Fmea.RpnThreshold(m);
            foreach (var fm in modes)
            {
                foreach (var (key, label) in new[] { (SafetyFields.Severity, "severity"), (SafetyFields.Occurrence, "occurrence"), (SafetyFields.Detection, "detection") })
                    if (SafetyFields.InvalidRating(fm, key))
                        yield return new(Severity.Error, "SAF-001", $"Failure mode '{fm.DisplayName}' has an invalid {label} '{fm.Get(key)}' (expected 1 … 10)", fm);

                var row = Fmea.Row(m, fm);
                if (row.Rpn is null && !new[] { SafetyFields.Severity, SafetyFields.Occurrence, SafetyFields.Detection }.Any(k => SafetyFields.InvalidRating(fm, k)))
                    yield return new(Severity.Info, "SAF-002", $"Failure mode '{fm.DisplayName}' is not fully rated (severity, occurrence and detection)", fm);

                var high = row.Rpn is { } rpn && rpn >= threshold || row.Priority == ActionPriority.High;
                if (high && !row.MitigatingRequirements.Any())
                    yield return new(Severity.Warning, "SAF-003",
                        $"High-risk failure mode '{fm.DisplayName}' (RPN {row.Rpn}{(row.Priority is { } ap ? $", AP {Fmea.Label(ap)}" : "")}) has no mitigating requirement", fm);
                else if (high && row.VerifiedBy.Count == 0)
                    yield return new(Severity.Info, "SAF-004", $"Mitigation of high-risk failure mode '{fm.DisplayName}' is not verified by any test case", fm);

                if (row.Item == null)
                    yield return new(Severity.Warning, "SAF-005", $"Failure mode '{fm.DisplayName}' is not owned by or allocated to a block, part or function", fm);

                if (row.Severity is >= 9 && string.IsNullOrWhiteSpace(row.Actions) && row.Priority != ActionPriority.Low)
                    yield return new(Severity.Info, "SAF-006", $"Failure mode '{fm.DisplayName}' has severity {row.Severity} but no recommended action", fm);

                if (fm.Get(SafetyFields.Probability).Length > 0 && SafetyFields.OwnProbability(fm) == null)
                    yield return new(Severity.Warning, "SAF-007", $"Failure mode '{fm.DisplayName}' has an invalid probability '{fm.Get(SafetyFields.Probability)}' (expected 0 … 1)", fm);
            }
        }
        foreach (var (e, id, severity, message) in FaultTree.Problems(m)) yield return new(severity, id, message, e);
    }
}

/// <summary>Traceability of safety elements: what a change to an element puts at risk.</summary>
public static class SafetyTrace
{
    /// <summary>
    /// Safety elements impacted when <paramref name="changed"/> changes: failure modes it owns, fault tree events that represent it
    /// and the gates / events above an impacted event. (Relationship-based links — allocations, mitigations, fault tree links —
    /// are followed by <see cref="Analysis.Traceability.Impact"/> itself.)
    /// </summary>
    public static IEnumerable<Element> ImpactedBy(ModelRepository model, Element changed)
    {
        if (changed.Kind != ElementKind.FailureMode && changed.Kind is not (ElementKind.Package or ElementKind.Model))
            foreach (var fm in changed.OwnedElements.Where(c => c.Kind == ElementKind.FailureMode)) yield return fm;
        foreach (var ev in model.OfKind(ElementKind.FaultTreeEvent))
            if (ev.Get(SafetyFields.Ref) == changed.Id) yield return ev;
    }

    /// <summary>Fault tree events that represent the element (failure mode, block, part or function).</summary>
    public static IReadOnlyList<Element> EventsFor(ModelRepository model, Element e) =>
        model.OfKind(ElementKind.FaultTreeEvent).Where(ev => ev.Get(SafetyFields.Ref) == e.Id).ToList();
}
