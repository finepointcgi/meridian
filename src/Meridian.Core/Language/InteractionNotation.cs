using System.Globalization;
using Meridian.Core.Model;

namespace Meridian.Core.Language;

/// <summary>
/// Data and notation for timing, interaction overview and communication diagrams. Everything is stored in plain
/// text fields so it round-trips through every format (project file, XMI extension, replication).
/// </summary>
public static class InteractionNotation
{
    /// <summary>Timing lifeline: its states, top to bottom, comma separated ("Idle, Busy, Off").</summary>
    public const string States = "states";

    /// <summary>Timing lifeline: state changes "time state" separated by ';' or new lines ("0 Idle; 10 Busy; 25 Idle").</summary>
    public const string Timeline = "timeline";

    /// <summary>Duration / time constraints: the constraint text ("{0..5 ms}", "{t = 12}").</summary>
    public const string Constraint = "constraint";

    /// <summary>Interaction use: id or name of the referenced interaction.</summary>
    public const string RefersTo = "refersTo";

    /// <summary>Inline interaction: one message per line, "A -> B : message".</summary>
    public const string Sequence = "sequence";

    /// <summary>Communication link: one numbered message per line ("1: request()"); a leading "&lt;" sends it backwards.</summary>
    public const string Messages = "messages";

    /// <summary>Timing diagram field: time unit shown on the axis ("ms", "s").</summary>
    public const string TimeUnit = "timeUnit";

    public static readonly IReadOnlyList<string> All = [States, Timeline, Constraint, RefersTo, Sequence, Messages, TimeUnit];

    public static IReadOnlyList<string> EditableFields(ElementKind k) => k switch
    {
        ElementKind.TimingLifeline => [States, Timeline],
        ElementKind.DurationConstraint or ElementKind.TimeConstraint => [Constraint],
        ElementKind.InteractionUse => [RefersTo],
        ElementKind.InlineInteraction => [Sequence],
        ElementKind.CommunicationLink => [Messages],
        _ => [],
    };

    public static string? Label(string key) => key switch
    {
        States => "States (top to bottom)",
        Timeline => "Timeline (time state; …)",
        Constraint => "Constraint",
        RefersTo => "Refers to interaction",
        Sequence => "Messages (A -> B : text)",
        Messages => "Messages (1: text, < for reverse)",
        TimeUnit => "Time unit",
        _ => null,
    };

    public static string? Help(string key) => key switch
    {
        States => "Comma separated, e.g. Idle, Busy, Off",
        Timeline => "e.g. 0 Idle; 10 Busy; 25 Idle",
        Constraint => "e.g. {0..5 ms} or {t = 12}",
        Sequence => "One per line, e.g. Client -> Server : request",
        Messages => "One per line, e.g. 1: request()  or  < 1.1: reply",
        _ => null,
    };

    // ================================================================ timing

    public static IReadOnlyList<string> StatesOf(Element e)
    {
        var declared = e.Get(States).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var (_, s) in TimelineOf(e))
            if (!declared.Contains(s)) declared.Add(s);
        return declared.Count > 0 ? declared : ["Idle", "Active"];
    }

    public static IReadOnlyList<(double Time, string State)> TimelineOf(Element e)
    {
        var list = new List<(double, string)>();
        foreach (var part in e.Get(Timeline).Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sp = part.IndexOfAny([' ', ':', '\t']);
            if (sp <= 0) continue;
            if (!double.TryParse(part[..sp], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) continue;
            var state = part[(sp + 1)..].Trim(' ', ':', '\t');
            if (state.Length > 0) list.Add((t, state));
        }
        return list.OrderBy(x => x.Item1).ToList();
    }

    /// <summary>Common time range of every timing lifeline on a diagram (the axis), with a little room after the last change.</summary>
    public static (double From, double To) TimeRange(Diagram d)
    {
        var times = d.Nodes.Where(n => n.Element.Kind == ElementKind.TimingLifeline).SelectMany(n => TimelineOf(n.Element)).Select(x => x.Time).ToList();
        if (times.Count == 0) return (0, 100);
        double lo = Math.Min(0, times.Min()), hi = times.Max();
        var span = Math.Max(1, hi - lo);
        return (lo, hi + span * 0.15);
    }

    /// <summary>Width of the name/state label column on the left of a timing lifeline.</summary>
    public const double TimingLabelWidth = 120;

    /// <summary>Nice tick step for an axis covering <paramref name="span"/> in about <paramref name="ticks"/> steps.</summary>
    public static double TickStep(double span, int ticks = 8)
    {
        var raw = Math.Max(1e-9, span / ticks);
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var n = raw / mag;
        return (n < 1.5 ? 1 : n < 3.5 ? 2 : n < 7.5 ? 5 : 10) * mag;
    }

    // ================================================================ interaction overview

    /// <summary>Messages of an inline interaction: (from, to, text) in order.</summary>
    public static IReadOnlyList<(string From, string To, string Text)> InlineMessages(Element e)
    {
        var list = new List<(string, string, string)>();
        foreach (var raw in e.Get(Sequence).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var arrow = raw.IndexOf("->", StringComparison.Ordinal);
            if (arrow <= 0) continue;
            var from = raw[..arrow].Trim();
            var rest = raw[(arrow + 2)..];
            var colon = rest.IndexOf(':');
            var to = (colon >= 0 ? rest[..colon] : rest).Trim();
            var text = colon >= 0 ? rest[(colon + 1)..].Trim() : "";
            if (from.Length > 0 && to.Length > 0) list.Add((from, to, text));
        }
        return list;
    }

    public static IReadOnlyList<string> InlineParticipants(Element e)
    {
        var names = new List<string>();
        foreach (var (f, t, _) in InlineMessages(e))
        {
            if (!names.Contains(f)) names.Add(f);
            if (!names.Contains(t)) names.Add(t);
        }
        return names;
    }

    /// <summary>The name shown in an interaction use frame: the referenced interaction (by id or name), else the use's own name.</summary>
    public static string RefName(ModelRepository m, Element e)
    {
        var r = e.Get(RefersTo);
        if (r.Length > 0) return m.Find(r) is { } target ? target.DisplayName : r;
        return e.DisplayName;
    }

    // ================================================================ communication

    /// <summary>Messages on a communication link: (text, forward?) in order.</summary>
    public static IReadOnlyList<(string Text, bool Forward)> LinkMessages(Element link)
    {
        var list = new List<(string, bool)>();
        foreach (var raw in link.Get(Messages).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var back = raw.StartsWith('<') || raw.StartsWith('←');
            var text = back ? raw[1..].Trim() : raw.TrimStart('>', '→').Trim();
            if (text.Length > 0) list.Add((text, !back));
        }
        return list;
    }
}
