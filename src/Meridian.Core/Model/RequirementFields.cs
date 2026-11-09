namespace Meridian.Core.Model;

/// <summary>
/// Field keys for requirement-management attributes. They live in the ordinary element field bag, so they
/// replicate, undo, save and diff like every other field.
/// </summary>
public static class ReqFields
{
    public const string Priority = "priority";
    public const string Status = "status";
    public const string Owner = "owner";
    public const string Rationale = "rationale";
    public const string VerificationMethod = "verifyMethod";
    public const string Risk = "risk";
    /// <summary>User-defined requirement attributes: "attr:Safety level" = "ASIL B".</summary>
    public const string CustomPrefix = "attr:";
    /// <summary>Test cases: last verdict (pass / fail / inconclusive / error).</summary>
    public const string Verdict = "verdict";
    /// <summary>Model root: prefix for auto-numbered requirement ids (default "REQ-").</summary>
    public const string IdPrefix = "reqIdPrefix";
    /// <summary>Trace relationships: fingerprint of the requirement at the source / target end when last reviewed.</summary>
    public const string SourceFingerprint = "suspectSrcFp";
    public const string TargetFingerprint = "suspectTgtFp";

    public const string DefaultIdPrefix = "REQ-";

    public static readonly string[] Priorities = ["low", "medium", "high", "critical"];
    public static readonly string[] Statuses = ["draft", "proposed", "approved", "implemented", "verified", "rejected"];
    public static readonly string[] VerificationMethods = ["test", "analysis", "inspection", "demonstration"];
    public static readonly string[] Risks = ["low", "medium", "high"];
    public static readonly string[] Verdicts = ["pass", "fail", "inconclusive", "error"];

    /// <summary>The standard attributes, in display order, with their labels and (where closed) allowed values.</summary>
    public static readonly (string Key, string Label, string[]? Values)[] Standard =
    [
        (Priority, "Priority", Priorities),
        (Status, "Status", Statuses),
        (Owner, "Owner", null),
        (VerificationMethod, "Verification method", VerificationMethods),
        (Risk, "Risk", Risks),
        (Rationale, "Rationale", null),
    ];

    public static IEnumerable<KeyValuePair<string, string>> CustomAttributes(Element e) =>
        e.Fields.Where(f => f.Key.StartsWith(CustomPrefix, StringComparison.Ordinal))
                .Select(f => new KeyValuePair<string, string>(f.Key[CustomPrefix.Length..], f.Value))
                .OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Names of every custom attribute used by any requirement of the model.</summary>
    public static IReadOnlyList<string> CustomAttributeNames(ModelRepository model) =>
        model.OfKind(ElementKind.Requirement).SelectMany(CustomAttributes).Select(a => a.Key)
             .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
}
