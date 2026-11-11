using System.Globalization;
using System.Text;
using Meridian.Core.Analysis;
using Meridian.Core.Model;

namespace Meridian.Core.Safety;

/// <summary>AIAG-VDA action priority: how urgently a failure mode needs action.</summary>
public enum ActionPriority { Low, Medium, High }

/// <summary>One FMEA worksheet row, computed from a <see cref="ElementKind.FailureMode"/> and its links.</summary>
public sealed record FmeaRow(
    Element FailureMode,
    Element? Item,
    int? Severity, int? Occurrence, int? Detection,
    IReadOnlyList<Element> Mitigations,
    IReadOnlyList<Element> VerifiedBy)
{
    public int? Rpn => Fmea.Rpn(Severity, Occurrence, Detection);
    public ActionPriority? Priority => Fmea.Priority(Severity, Occurrence, Detection);
    public string Function => FailureMode.Get(SafetyFields.Function);
    public string Effect => FailureMode.Get(SafetyFields.Effect);
    public string Cause => FailureMode.Get(SafetyFields.Cause);
    public string Controls => FailureMode.Get(SafetyFields.Controls);
    public string Actions => FailureMode.Get(SafetyFields.Actions);
    public IEnumerable<Element> MitigatingRequirements => Mitigations.Where(m => m.Kind == ElementKind.Requirement);
}

/// <summary>
/// Failure mode and effects analysis: risk priority numbers, AIAG-VDA action priority, the worksheet rows and CSV export.
/// Failure modes are owned by a block, part or function, or owned by a package and allocated to one.
/// </summary>
public static class Fmea
{
    /// <summary>Risk priority number S × O × D (1 … 1000), or null until all three are rated 1 … 10.</summary>
    public static int? Rpn(int? s, int? o, int? d) => s is { } S && o is { } O && d is { } D ? S * O * D : null;

    public static int? Rpn(Element failureMode) =>
        Rpn(SafetyFields.Rating(failureMode, SafetyFields.Severity), SafetyFields.Rating(failureMode, SafetyFields.Occurrence), SafetyFields.Rating(failureMode, SafetyFields.Detection));

    public static ActionPriority? Priority(Element failureMode) =>
        Priority(SafetyFields.Rating(failureMode, SafetyFields.Severity), SafetyFields.Rating(failureMode, SafetyFields.Occurrence), SafetyFields.Rating(failureMode, SafetyFields.Detection));

    /// <summary>
    /// Action priority per the AIAG-VDA FMEA Handbook (2019) design FMEA table: severity first, then occurrence,
    /// then detection. Null until all three ratings are set.
    /// </summary>
    public static ActionPriority? Priority(int? s, int? o, int? d)
    {
        if (s is not { } S || o is not { } O || d is not { } D) return null;
        const ActionPriority H = ActionPriority.High, M = ActionPriority.Medium, L = ActionPriority.Low;
        // Detection bands: 7-10, 5-6, 2-4, 1.
        var db = D >= 7 ? 0 : D >= 5 ? 1 : D >= 2 ? 2 : 3;
        ActionPriority[] Row(ActionPriority a, ActionPriority b, ActionPriority c, ActionPriority e) => [a, b, c, e];
        ActionPriority[] row;
        if (S >= 9)
            row = O >= 6 ? Row(H, H, H, H) : O >= 4 ? Row(H, H, H, M) : O >= 2 ? Row(H, M, L, L) : Row(L, L, L, L);
        else if (S >= 7)
            row = O >= 8 ? Row(H, H, H, H) : O >= 6 ? Row(H, H, H, M) : O >= 4 ? Row(H, M, M, M) : O >= 2 ? Row(M, M, L, L) : Row(L, L, L, L);
        else if (S >= 4)
            row = O >= 8 ? Row(H, H, M, M) : O >= 6 ? Row(M, M, M, L) : O >= 4 ? Row(M, L, L, L) : Row(L, L, L, L);
        else if (S >= 2)
            row = O >= 8 ? Row(M, M, L, L) : Row(L, L, L, L);
        else
            row = Row(L, L, L, L);
        return row[db];
    }

    public static string Label(ActionPriority? p) => p switch { ActionPriority.High => "H", ActionPriority.Medium => "M", ActionPriority.Low => "L", _ => "" };

    /// <summary>The model's RPN threshold for "needs a mitigating requirement" (root field, default 100).</summary>
    public static int RpnThreshold(ModelRepository model) =>
        int.TryParse(model.Root.Get(SafetyFields.RpnThreshold), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) && t is >= 1 and <= 1000
            ? t : SafetyFields.DefaultRpnThreshold;

    /// <summary>High risk: RPN at or above the model's threshold, or action priority High.</summary>
    public static bool IsHighRisk(ModelRepository model, Element failureMode) =>
        Rpn(failureMode) is { } rpn && rpn >= RpnThreshold(model) || Priority(failureMode) == ActionPriority.High;

    /// <summary>
    /// The block, part or function a failure mode belongs to: its owner unless that is a package, else the first
    /// element it is allocated to (or allocated from).
    /// </summary>
    public static Element? ItemOf(ModelRepository model, Element failureMode)
    {
        if (failureMode.Owner is { } o && o.Kind is not (ElementKind.Package or ElementKind.Model)) return o;
        return model.Outgoing(failureMode, ElementKind.Allocate).Select(r => r.Target).FirstOrDefault()
            ?? model.Incoming(failureMode, ElementKind.Allocate).Select(r => r.Source).FirstOrDefault();
    }

    /// <summary>Everything that mitigates the failure mode (requirements first).</summary>
    public static IReadOnlyList<Element> Mitigations(ModelRepository model, Element failureMode) =>
        model.Incoming(failureMode, ElementKind.Mitigate).Select(r => r.Source)
            .OrderBy(e => e.Kind == ElementKind.Requirement ? 0 : 1).ThenBy(e => e.Get(FieldKeys.RequirementId)).ThenBy(e => e.Name).ToList();

    /// <summary>Test cases that verify the failure mode directly or verify one of its mitigating requirements.</summary>
    public static IReadOnlyList<Element> VerifiedBy(ModelRepository model, Element failureMode)
    {
        var seen = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        var list = new List<Element>();
        foreach (var t in model.Incoming(failureMode, ElementKind.Verify).Select(r => r.Source)) if (seen.Add(t)) list.Add(t);
        foreach (var m in Mitigations(model, failureMode).Where(m => m.Kind == ElementKind.Requirement))
            foreach (var t in model.Incoming(m, ElementKind.Verify).Select(r => r.Source)) if (seen.Add(t)) list.Add(t);
        return list;
    }

    public static FmeaRow Row(ModelRepository model, Element fm) => new(fm, ItemOf(model, fm),
        SafetyFields.Rating(fm, SafetyFields.Severity), SafetyFields.Rating(fm, SafetyFields.Occurrence), SafetyFields.Rating(fm, SafetyFields.Detection),
        Mitigations(model, fm), VerifiedBy(model, fm));

    /// <summary>Worksheet rows for every failure mode (optionally under <paramref name="scope"/>), highest RPN first.</summary>
    public static IReadOnlyList<FmeaRow> Rows(ModelRepository model, Element? scope = null) =>
        model.OfKind(ElementKind.FailureMode)
            .Where(fm => scope == null || ReferenceEquals(scope, fm) || scope.IsAncestorOf(fm))
            .Select(fm => Row(model, fm))
            .OrderByDescending(r => r.Rpn ?? -1).ThenByDescending(r => r.Severity ?? -1).ThenBy(r => r.Item?.Name).ThenBy(r => r.FailureMode.Name)
            .ToList();

    public static readonly IReadOnlyList<string> CsvHeaders =
        ["Item", "Function", "Failure mode", "Effect", "Cause", "S", "O", "D", "RPN", "AP", "Current controls", "Recommended actions",
         "Owner", "Status", "Mitigating requirements", "Verified by", "Id"];

    /// <summary>Cells of one row in <see cref="CsvHeaders"/> order (also used by reports).</summary>
    public static IReadOnlyList<string> Cells(FmeaRow r) =>
    [
        r.Item?.Name ?? "", r.Function, r.FailureMode.Name, r.Effect, r.Cause,
        Num(r.Severity), Num(r.Occurrence), Num(r.Detection), Num(r.Rpn), Label(r.Priority),
        r.Controls, r.Actions, r.FailureMode.Get(SafetyFields.ActionOwner), r.FailureMode.Get(SafetyFields.ActionStatus),
        string.Join("; ", r.Mitigations.Select(Describe)), string.Join("; ", r.VerifiedBy.Select(t => t.Name)), r.FailureMode.Id,
    ];

    /// <summary>The FMEA worksheet as CSV (RFC 4180, formula-injection safe), highest RPN first.</summary>
    public static string ToCsv(ModelRepository model, Element? scope = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", CsvHeaders.Select(Quote)));
        foreach (var r in Rows(model, scope)) sb.AppendLine(string.Join(",", Cells(r).Select(Quote)));
        return sb.ToString();
    }

    public static string Describe(Element e) =>
        e.Kind == ElementKind.Requirement ? $"{e.Get(FieldKeys.RequirementId)} {e.Name}".Trim() : e.Name;

    private static string Num(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Quote(string s)
    {
        s = CsvSafety.Neutralize(s);
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
