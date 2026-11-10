using System.Text.RegularExpressions;

namespace Meridian.Core.Derivation;

public enum StepKind { Action, If, Alternate }

/// <summary>One step of a use case: an action, a conditional step ("If X, do Y") or an alternate flow ("Alternate: X - do Y").</summary>
/// <param name="Id">The step's number as written ("3", "2.1"), or "#n" for the n-th unnumbered step: its stable key for "update from source".</param>
/// <param name="Actor">The actor named at the start of the step, or null for the system.</param>
public sealed record UseCaseStep(string Id, StepKind Kind, string Text, string? Actor, string? Guard);

/// <summary>
/// Parses a use case's description into steps. Numbered or bulleted lines are steps; without a list, each sentence is one.
/// "If X, Y" becomes a decision with Y on the [X] branch; "Alternate: X - Y" (also "Alternative", "Exception", "Otherwise")
/// branches off the main flow. A step starting with an actor's name ("Driver presses start", "Driver: presses start") runs
/// in that actor's swimlane; everything else in the system's.
/// </summary>
public static partial class UseCaseSteps
{
    [GeneratedRegex(@"^\s*(?:(?<num>\d+(?:\.\d+)*)[.)]?|[-*•–]|[a-z][.)])\s+(?<t>.+)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^(?:alternate|alternative|alt|exception|otherwise|else)(?:\s+(?:flow|path|course))?\s*(?:\d+[a-z]?)?\s*[:\-–,]?\s*(?<t>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex AlternatePrefix();

    [GeneratedRegex(@"^(?:if|when|in case)\s+(?<t>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex IfPrefix();

    [GeneratedRegex(@"\s+(?:then|-|–)\s+|,\s*|:\s+")]
    private static partial Regex Split();

    public const int MaxSteps = 40;

    public static IReadOnlyList<UseCaseStep> Parse(string? text, IReadOnlyList<string> actors)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var items = new List<(string Text, string? Number)>();
        if (lines.Any(l => ListItem().IsMatch(l)))
        {
            foreach (var l in lines)
            {
                if (ListItem().Match(l) is { Success: true } m) items.Add((m.Groups["t"].Value, m.Groups["num"].Success ? m.Groups["num"].Value : null));
                else if (AlternatePrefix().IsMatch(l) && l.Contains(':') || IfPrefix().IsMatch(l)) items.Add((l, null));
                // other lines (headings such as "Main flow:" or prose) are not steps
            }
        }
        else items.AddRange(DeriveText.Sentences(string.Join(' ', lines)).Select(x => (x, (string?)null)));
        var numbers = items.Where(i => i.Number != null).Select(i => i.Number!).ToList();
        var unique = numbers.Distinct().Count() == numbers.Count;

        var steps = new List<UseCaseStep>();
        foreach (var (raw, number) in items.Take(MaxSteps))
        {
            var t = raw.Trim();
            if (t.EndsWith(':') && !t.Contains(' ')) continue;   // a heading
            var n = unique && number != null ? number : $"#{steps.Count + 1}";
            if (AlternatePrefix().Match(t) is { Success: true } am && LooksLikeAlternate(t))
            {
                var (guard, action) = Cut(am.Groups["t"].Value);
                var (actor, rest) = ActorOf(action ?? guard, actors);
                steps.Add(new UseCaseStep(n, StepKind.Alternate, DeriveText.Sentence(rest), actor, action == null ? "alternate" : guard.TrimEnd('.')));
            }
            else if (IfPrefix().Match(t) is { Success: true } im)
            {
                var (guard, action) = Cut(im.Groups["t"].Value);
                var (actor, rest) = ActorOf(action ?? guard, actors);
                steps.Add(new UseCaseStep(n, StepKind.If, DeriveText.Sentence(rest), actor, guard.TrimEnd('.')));
            }
            else
            {
                var (actor, rest) = ActorOf(t, actors);
                if (DeriveText.Sentence(rest) is { Length: > 0 } s) steps.Add(new UseCaseStep(n, StepKind.Action, s, actor, null));
            }
        }
        return steps;
    }

    private static bool LooksLikeAlternate(string t)
    {
        var first = t.Split([' ', ':', '-', ','], 2)[0].ToLowerInvariant();
        return first is "alternate" or "alternative" or "alt" or "exception" or "otherwise" or "else";
    }

    private static (string Guard, string? Action) Cut(string s)
    {
        var m = Split().Match(s);
        if (!m.Success || m.Index == 0) return (s.Trim(), null);
        var action = s[(m.Index + m.Length)..].Trim();
        return action.Length == 0 ? (s[..m.Index].Trim(), null) : (s[..m.Index].Trim(), action);
    }

    /// <summary>The actor a step starts with ("Driver presses…", "The driver presses…", "Driver: presses…") and the rest; "System…" is the system.</summary>
    public static (string? Actor, string Remainder) ActorOf(string text, IReadOnlyList<string> actors)
    {
        var t = text.Trim();
        foreach (var a in actors.Where(a => a.Length > 0).OrderByDescending(a => a.Length))
            foreach (var prefix in new[] { a, "the " + a })
                if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (t.Length == prefix.Length || t[prefix.Length] is ' ' or ':' or ','))
                    return (a, t[prefix.Length..].TrimStart(' ', ':', ','));
        foreach (var prefix in new[] { "the system", "system" })
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && t.Length > prefix.Length && t[prefix.Length] is ' ' or ':' or ',')
                return (null, t[prefix.Length..].TrimStart(' ', ':', ','));
        return (null, t);
    }

    /// <summary>Steps written back in the mini-language (what the AI fill-in returns and the use case description can hold).</summary>
    public static string Format(IEnumerable<UseCaseStep> steps) => string.Join("\n", steps.Select((s, i) => s.Kind switch
    {
        StepKind.If => $"{i + 1}. If {s.Guard}, {(s.Actor != null ? s.Actor + " " : "")}{Lower(s.Text)}",
        StepKind.Alternate => $"Alternate: {s.Guard} - {(s.Actor != null ? s.Actor + " " : "")}{Lower(s.Text)}",
        _ => $"{i + 1}. {(s.Actor != null ? s.Actor + " " : "")}{(s.Actor != null ? Lower(s.Text) : s.Text)}",
    }));

    private static string Lower(string s) => s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..] : s;
}
