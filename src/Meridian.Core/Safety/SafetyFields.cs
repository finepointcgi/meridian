using System.Globalization;
using Meridian.Core.Model;

namespace Meridian.Core.Safety;

/// <summary>
/// Field keys, subtypes and labels for safety &amp; reliability analysis: FMEA failure modes (<c>fmea.*</c>) and
/// fault tree events and gates (<c>fta.*</c>). Kept apart from <see cref="FieldKeys"/> so the feature stays self-contained.
/// </summary>
public static class SafetyFields
{
    // ------------------------------------------------------------------ FMEA (failure modes)

    /// <summary>The function of the item that fails (FMEA "function / requirement" column).</summary>
    public const string Function = "fmea.function";
    /// <summary>Effect of the failure on the system, user or mission.</summary>
    public const string Effect = "fmea.effect";
    /// <summary>Cause / mechanism of the failure.</summary>
    public const string Cause = "fmea.cause";
    /// <summary>Severity of the effect, 1 (none) … 10 (hazardous without warning).</summary>
    public const string Severity = "fmea.severity";
    /// <summary>Occurrence (likelihood of the cause), 1 (remote) … 10 (very high).</summary>
    public const string Occurrence = "fmea.occurrence";
    /// <summary>Detection (ability of current controls to detect it), 1 (certain) … 10 (cannot detect).</summary>
    public const string Detection = "fmea.detection";
    /// <summary>Current prevention / detection controls.</summary>
    public const string Controls = "fmea.controls";
    /// <summary>Recommended actions to reduce the risk.</summary>
    public const string Actions = "fmea.actions";
    /// <summary>Who owns the recommended actions.</summary>
    public const string ActionOwner = "fmea.actionOwner";
    /// <summary>open / in progress / done.</summary>
    public const string ActionStatus = "fmea.actionStatus";

    /// <summary>Model root: RPN at or above which a failure mode needs a mitigating requirement (default <see cref="DefaultRpnThreshold"/>).</summary>
    public const string RpnThreshold = "fmea.rpnThreshold";
    public const int DefaultRpnThreshold = 100;

    // ------------------------------------------------------------------ fault trees

    /// <summary>Events: id of the failure mode, block, part or function the event stands for.</summary>
    public const string Ref = "fta.ref";
    /// <summary>Basic events (and failure modes): probability of occurrence over the mission, 0 … 1 (e.g. 1e-4).</summary>
    public const string Probability = "fta.probability";
    /// <summary>Basic events (and failure modes): constant failure rate λ per hour; with <see cref="ExposureTime"/> gives 1 − e^(−λt).</summary>
    public const string FailureRate = "fta.lambda";
    /// <summary>Basic events (and failure modes): exposure / mission time in hours.</summary>
    public const string ExposureTime = "fta.time";
    /// <summary>House events: "true" (always occurs) or "false" (never occurs).</summary>
    public const string HouseState = "fta.houseState";
    /// <summary>Vote (k-of-n) gates: how many inputs must occur.</summary>
    public const string K = "fta.k";

    public const string Top = "top", Intermediate = "intermediate", Basic = "basic", Undeveloped = "undeveloped", House = "house";
    public static readonly IReadOnlyList<string> EventKinds = [Top, Intermediate, Basic, Undeveloped, House];

    public const string And = "and", Or = "or", Vote = "vote";
    public static readonly IReadOnlyList<string> GateKinds = [And, Or, Vote];

    public static readonly IReadOnlyList<string> ActionStatuses = ["open", "in progress", "done"];

    /// <summary>Every safety field, for serializers that carry them as extension fields.</summary>
    public static readonly IReadOnlyList<string> All =
        [Function, Effect, Cause, Severity, Occurrence, Detection, Controls, Actions, ActionOwner, ActionStatus, RpnThreshold,
         Ref, Probability, FailureRate, ExposureTime, HouseState, K];

    public static bool IsSafetyField(string key) => key.StartsWith("fmea.", StringComparison.Ordinal) || key.StartsWith("fta.", StringComparison.Ordinal);

    /// <summary>The event kind of a fault tree event (basic when unset).</summary>
    public static string EventKind(Element e) => e.Get(Model.FieldKeys.Subtype) is { Length: > 0 } s ? s : Basic;

    /// <summary>The gate kind of a fault tree gate (OR when unset).</summary>
    public static string GateKind(Element e) => e.Get(Model.FieldKeys.Subtype) is { Length: > 0 } s ? s : Or;

    public static string? Label(string key) => key switch
    {
        Function => "Function", Effect => "Failure effect", Cause => "Failure cause", Severity => "Severity (S)",
        Occurrence => "Occurrence (O)", Detection => "Detection (D)", Controls => "Current controls", Actions => "Recommended actions",
        ActionOwner => "Action owner", ActionStatus => "Action status", Ref => "Represents", Probability => "Probability",
        FailureRate => "Failure rate λ (per hour)", ExposureTime => "Exposure time (hours)", HouseState => "House event state", K => "k (inputs that must occur)",
        _ => null,
    };

    public static string? Help(string key) => key switch
    {
        Severity => "1 = no effect … 10 = hazardous without warning",
        Occurrence => "1 = remote … 10 = very high",
        Detection => "1 = almost certain to detect … 10 = cannot detect",
        Probability => "0 … 1, e.g. 1e-4 (overrides λ and time)",
        FailureRate => "e.g. 2e-6",
        ExposureTime => "e.g. 10000",
        _ => null,
    };

    /// <summary>Inspector fields for the safety kinds (rendered by the safety inspector section, not the generic list).</summary>
    public static IEnumerable<string> EditableFields(ElementKind k)
    {
        switch (k)
        {
            case ElementKind.FailureMode:
                return [Function, Effect, Cause, Severity, Occurrence, Detection, Controls, Actions, ActionOwner, ActionStatus, Probability, FailureRate, ExposureTime];
            case ElementKind.FaultTreeEvent:
                return [Model.FieldKeys.Subtype, Ref, Probability, FailureRate, ExposureTime, HouseState];
            case ElementKind.FaultTreeGate:
                return [Model.FieldKeys.Subtype, K];
            default:
                return [];
        }
    }

    // ------------------------------------------------------------------ parsing

    /// <summary>A 1 … 10 rating, or null when missing or invalid.</summary>
    public static int? Rating(Element e, string key) =>
        int.TryParse(e.Get(key).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v is >= 1 and <= 10 ? v : null;

    /// <summary>True when the field holds something that is not a 1 … 10 rating.</summary>
    public static bool InvalidRating(Element e, string key) => e.Get(key).Trim().Length > 0 && Rating(e, key) == null;

    public static double? Number(Element e, string key) =>
        double.TryParse(e.Get(key).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    /// <summary>
    /// The probability an element carries directly: <see cref="Probability"/>, else 1 − e^(−λt) from
    /// <see cref="FailureRate"/> and <see cref="ExposureTime"/>. Null when neither is set (or the value is out of range).
    /// </summary>
    public static double? OwnProbability(Element e)
    {
        if (e.Get(Probability).Trim().Length > 0)
            return Number(e, Probability) is { } p && p is >= 0 and <= 1 ? p : null;
        if (Number(e, FailureRate) is { } lambda && Number(e, ExposureTime) is { } t && lambda >= 0 && t >= 0)
            return 1 - Math.Exp(-lambda * t);
        return null;
    }

    public static string Format(double p) =>
        p == 0 ? "0" : p >= 1e-3 ? p.ToString("0.####", CultureInfo.InvariantCulture) : p.ToString("0.00E+0", CultureInfo.InvariantCulture);
}
