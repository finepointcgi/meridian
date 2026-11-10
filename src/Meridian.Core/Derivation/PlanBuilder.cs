using System.Globalization;
using System.Text;
using Meridian.Core.Model;

namespace Meridian.Core.Derivation;

/// <summary>Builds a fresh plan (everything "add" or "reuse"); <see cref="DiagramDerivation"/> then diffs it against an earlier run.</summary>
internal sealed class PlanBuilder(DerivationPlan plan)
{
    public DerivationPlan Plan { get; } = plan;
    public ModelRepository Model { get; init; } = null!;

    public DerivedItem Element(string key, ElementKind kind, ItemRef owner, string? name, PlannedShape? shape = null, params (string Key, string Value)[] fields)
    {
        var i = new DerivedItem { Key = key, Kind = kind, Owner = owner, Shape = shape };
        if (name != null) i.Name = name;
        foreach (var (k, v) in fields) i.Fields[k] = v;
        Plan.Items.Add(i);
        return i;
    }

    public DerivedItem Rel(string key, ElementKind kind, ItemRef source, ItemRef target, string? name = null, params (string Key, string Value)[] fields)
    {
        var i = new DerivedItem { Key = key, Kind = kind, Source = source, Target = target };
        if (name != null) i.Name = name;
        foreach (var (k, v) in fields) i.Fields[k] = v;
        Plan.Items.Add(i);
        return i;
    }

    public DerivedItem Diagram(string key, DiagramType type, ItemRef owner, string name, bool autoLayout, string purpose)
    {
        var i = new DerivedItem { Key = key, Kind = ElementKind.Diagram, DiagramType = type, Owner = owner, AutoLayout = autoLayout };
        i.Name = name;
        i.Fields[FieldKeys.Documentation] = purpose;
        Plan.Items.Add(i);
        return i;
    }

    /// <summary>An existing element drawn on a derived diagram (never changed or removed).</summary>
    public void Show(Element e, PlannedShape shape)
    {
        var key = $"reuse:{e.Id}:{shape.DiagramKey}";
        if (Plan[key] != null) return;
        Plan.Items.Add(new DerivedItem { Key = key, Kind = e.Kind, Existing = e, Change = DerivedChange.Reuse, Shape = shape });
    }

    /// <summary>Sets <paramref name="field"/> to the id of <paramref name="target"/>: an existing element's id, or a reference to a planned item.</summary>
    public static void SetRef(DerivedItem item, string field, ItemRef target)
    {
        if (target.Element is { } e) item.Fields[field] = e.Id;
        else item.FieldRefs[field] = target.Key!;
    }

    public void Warn(string text)
    {
        if (!Plan.Warnings.Contains(text)) Plan.Warnings.Add(text);
    }
}

/// <summary>Text helpers shared by the rules: names from free text, identifiers, word matching.</summary>
internal static class DeriveText
{
    private static readonly TextInfo Ti = CultureInfo.InvariantCulture.TextInfo;

    /// <summary>"presses the start button." → "Presses the start button" (trimmed, first letter upper, at most <paramref name="max"/> characters).</summary>
    public static string Sentence(string s, int max = 60)
    {
        s = s.Trim().TrimEnd('.', ';', ':', ',', '!').Trim();
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (s.Length > max)
        {
            var cut = s.LastIndexOf(' ', max);
            s = (cut > max / 2 ? s[..cut] : s[..max]).TrimEnd(',', ';', ' ') + "…";
        }
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }

    /// <summary>"schedule imaging passes" → "Schedule Imaging Passes".</summary>
    public static string Title(string s) => Ti.ToTitleCase(s.Trim().ToLowerInvariant());

    /// <summary>"Check the brake pedal" → "checkTheBrakePedal" (a message / operation name).</summary>
    public static string Identifier(string s)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0) { words.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) words.Add(sb.ToString());
        if (words.Count == 0) return "message";
        static string Body(string w) => w.Length > 1 && w.All(c => !char.IsLower(c)) ? w.ToLowerInvariant() : w;
        var first = Body(words[0]);
        var id = new StringBuilder().Append(char.ToLowerInvariant(first[0])).Append(first[1..]);
        foreach (var w in words.Skip(1).Select(Body)) id.Append(char.ToUpperInvariant(w[0])).Append(w[1..]);
        var r = id.ToString();
        return char.IsDigit(r[0]) ? "m" + r : r;
    }

    /// <summary>"FuelTank" → "fuel tank"; "BrakeSystem" → "brake system".</summary>
    public static string Words(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c) && char.IsLower(name[i - 1])) sb.Append(' ');
            sb.Append(c is '_' or '-' ? ' ' : char.ToLowerInvariant(c));
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Does <paramref name="text"/> contain <paramref name="phrase"/> as whole words (case-insensitive)?</summary>
    public static bool Mentions(string text, string phrase)
    {
        if (phrase.Length < 3) return false;
        var t = " " + Normalize(text) + " ";
        var p = " " + Normalize(phrase) + " ";
        return t.Contains(p, StringComparison.Ordinal) || t.Contains(p.TrimEnd() + "s ", StringComparison.Ordinal);
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s) sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>"The system shall …" lines of free text (sentences), for steps without a list.</summary>
    public static IEnumerable<string> Sentences(string text)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            sb.Append(c);
            if (c is '.' or '!' or '?' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                if (sb.ToString().Trim() is { Length: > 1 } s) yield return s;
                sb.Clear();
            }
        }
        if (sb.ToString().Trim() is { Length: > 1 } rest) yield return rest;
    }
}
