namespace Meridian.Core.Model;

/// <summary>
/// Field keys for variability (product lines), trade studies, timed simulation, Monte Carlo and FMI import.
/// Kept apart from <see cref="FieldKeys"/> so the feature stays self-contained.
/// </summary>
public static class ExtFieldKeys
{
    /// <summary>Any element or relationship: feature expression such as "GPS and not Basic". Empty = always present.</summary>
    public const string PresenceCondition = "presenceCondition";

    /// <summary>Features: mandatory / optional / alternative (xor group) / or (or group), relative to the parent feature.</summary>
    public const string FeatureKind = "featureKind";

    /// <summary>Configurations: comma separated ids of the selected features.</summary>
    public const string SelectedFeatures = "selectedFeatures";

    /// <summary>Trade studies: JSON definition (alternatives, criteria, normalization).</summary>
    public const string TradeStudyData = "tradeStudy";

    /// <summary>Actions: execution time, e.g. "5", "2.5s", "200ms", "1 min" or an expression over variables (seconds).</summary>
    public const string Duration = "duration";

    /// <summary>Actions: name of a signal broadcast when the action completes (send signal action).</summary>
    public const string SendSignal = "sendSignal";

    /// <summary>Actions: name of the signal the action waits for (accept event action).</summary>
    public const string AcceptEvent = "acceptEvent";

    /// <summary>Value properties: probability distribution for Monte Carlo, e.g. "normal(1500, 50)" or "uniform(90, 110)".</summary>
    public const string Distribution = "distribution";

    /// <summary>Value properties and ports: unit symbol (from FMI import or by hand).</summary>
    public const string Unit = "unit";

    public static readonly IReadOnlyList<string> FeatureKinds = ["mandatory", "optional", "alternative", "or"];

    public static readonly IReadOnlyList<string> All =
        [PresenceCondition, FeatureKind, SelectedFeatures, TradeStudyData, Duration, SendSignal, AcceptEvent, Distribution, Unit];

    public static string? Label(string key) => key switch
    {
        PresenceCondition => "Presence condition",
        FeatureKind => "Feature kind",
        SelectedFeatures => "Selected features",
        Duration => "Duration",
        SendSignal => "Send signal (on completion)",
        AcceptEvent => "Accept event (wait for signal)",
        Distribution => "Distribution (Monte Carlo)",
        Unit => "Unit",
        _ => null,
    };

    public static string? Help(string key) => key switch
    {
        PresenceCondition => "Feature expression, e.g. GPS and not Basic (empty = in every variant)",
        Duration => "Seconds or with unit: 5, 2.5s, 200ms, 1 min, or an expression",
        Distribution => "normal(mean, sd), uniform(min, max), triangular(min, mode, max), lognormal(mu, sigma)",
        SendSignal => "Signal name broadcast to accept events and transition triggers",
        AcceptEvent => "Signal name this action waits for",
        _ => null,
    };

    /// <summary>Extra inspector fields per kind (inserted before stereotypes / documentation).</summary>
    public static IEnumerable<string> EditableFields(ElementKind k)
    {
        if (k == ElementKind.Feature) yield return FeatureKind;
        if (k == ElementKind.Action) { yield return Duration; yield return SendSignal; yield return AcceptEvent; }
        if (k == ElementKind.Property) { yield return Distribution; yield return Unit; }
        if (k == ElementKind.Port) yield return Unit;
        if (k is not (ElementKind.Model or ElementKind.Diagram or ElementKind.Feature or ElementKind.Configuration or ElementKind.TradeStudy
            or ElementKind.FeatureDecomposition or ElementKind.Requires or ElementKind.Excludes))
            yield return PresenceCondition;
    }
}
