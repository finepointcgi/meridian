using Meridian.Core.Libraries;
using Meridian.Core.Model;

namespace Meridian.Serialization.History;

public enum WordOp { Same, Removed, Added }

/// <summary>A run of words in an inline word diff.</summary>
public sealed record WordSegment(WordOp Op, string Text);

/// <summary>
/// One field of an element compared between two versions: raw values (what "Restore" writes), how they are shown (references
/// by name, suspect marks in words) and, for text, an inline word diff.
/// </summary>
public sealed record FieldDiffRow(string Key, string Label, string? Before, string? After, string BeforeText, string AfterText,
                                  IReadOnlyList<WordSegment>? Words, bool Restorable);

/// <summary>Field-by-field comparison of one element in two versions (or a version and the current model).</summary>
public static class FieldDiff
{
    /// <summary>Fields that only the system writes (suspect marks): shown, never restored.</summary>
    private static readonly HashSet<string> SystemKeys = new(StringComparer.Ordinal) { ReqFields.LibraryChange };

    /// <summary>
    /// Every field that differs between <paramref name="before"/> (in <paramref name="beforeModel"/>) and <paramref name="after"/>
    /// (in <paramref name="afterModel"/>); either may be null (added / removed). Values must have been kept for both elements.
    /// </summary>
    public static IReadOnlyList<FieldDiffRow> Compare(FlatElement? before, FlatModel? beforeModel, FlatElement? after, FlatModel? afterModel)
    {
        var rows = new List<FieldDiffRow>();
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        if (before != null) keys.UnionWith(before.Keys);
        if (after != null) keys.UnionWith(after.Keys);
        // The name first, then the other fields alphabetically.
        foreach (var key in keys.OrderBy(k => k == FieldKeys.Name ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal))
        {
            var a = before?.Get(key);
            var b = after?.Get(key);
            if (a == b) continue;
            rows.Add(Row(key, a, b, beforeModel, afterModel));
        }
        if (before?.SourceId != after?.SourceId && (before?.IsRelationship == true || after?.IsRelationship == true))
            rows.Add(Reference(HistoryKeys.Source, "Source", before?.SourceId, after?.SourceId, beforeModel, afterModel));
        if (before?.TargetId != after?.TargetId && (before?.IsRelationship == true || after?.IsRelationship == true))
            rows.Add(Reference(HistoryKeys.Target, "Target", before?.TargetId, after?.TargetId, beforeModel, afterModel));
        if (before != null && after != null && before.OwnerId != after.OwnerId)
            rows.Add(Reference(HistoryKeys.Owner, "Owner", before.OwnerId, after.OwnerId, beforeModel, afterModel));
        if ((before?.IsDiagram == true || after?.IsDiagram == true) && before?.LayoutHash != after?.LayoutHash)
            rows.Add(new FieldDiffRow(HistoryKeys.Layout, "Layout", null, null, Shapes(before), Shapes(after), null, false));
        return rows;
    }

    private static string Shapes(FlatElement? d) => d is null ? "∅" : $"{d.NodeCount} shapes, {d.EdgeCount} paths";

    private static FieldDiffRow Reference(string key, string label, string? a, string? b, FlatModel? am, FlatModel? bm) =>
        new(key, label, a, b, Name(a, am), Name(b, bm), null, false);

    private static string Name(string? id, FlatModel? m) => id is null ? "∅" : m?.NameOf(id) ?? "(not in this version)";

    private static FieldDiffRow Row(string key, string? a, string? b, FlatModel? am, FlatModel? bm)
    {
        if (SystemKeys.Contains(key))
            return new FieldDiffRow(key, LabelOf(key), a, b, Suspect(a), Suspect(b), null, false);
        var aText = Display(a, am);
        var bText = Display(b, bm);
        var isReference = !ReferenceEquals(aText, a) || !ReferenceEquals(bText, b);
        var words = !isReference && (a ?? b) is { } some && !LooksLikeId(some) ? WordDiff.Compare(a ?? "", b ?? "") : null;
        return new FieldDiffRow(key, LabelOf(key), a, b, aText ?? "∅", bText ?? "∅", words, true);
    }

    /// <summary>Text shown for a value: element ids (one, or comma separated) by name; anything else as is.</summary>
    public static string? Display(string? value, FlatModel? model)
    {
        if (value is null || model is null || !value.StartsWith('_')) return value;
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (!parts.Any(model.Contains)) return value;
        return string.Join(", ", parts.Select(p => model.NameOf(p) ?? p));
    }

    /// <summary>An element id that isn't in the version (e.g. a library element): shown as is, never word-diffed.</summary>
    private static bool LooksLikeId(string v) => v.Length == 33 && v[0] == '_' && v.Skip(1).All(char.IsAsciiHexDigit);

    private static string Suspect(string? value) => value switch
    {
        null or "" => "∅",
        _ when LibraryChangeMark.Parse(value) is { } m => m.Removed ? "Suspect: a used-project element was removed" : "Suspect: a used-project element changed",
        _ => "Suspect (library update)",   // the earlier format named library elements: never shown from history
    };

    /// <summary>A readable label for a field key ("documentation" → "Documentation", "tag.Mass" → "Mass (tag)").</summary>
    public static string LabelOf(string key)
    {
        if (key.StartsWith(FieldKeys.TagPrefix, StringComparison.Ordinal)) return $"{key[FieldKeys.TagPrefix.Length..]} (tag)";
        if (key == ReqFields.LibraryChange) return "Library suspect mark";
        if (key == FieldKeys.RequirementId) return "Requirement id";
        var words = new System.Text.StringBuilder();
        foreach (var c in key)
        {
            if (char.IsUpper(c) && words.Length > 0) words.Append(' ');
            words.Append(words.Length == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
        }
        return words.ToString();
    }

    /// <summary>
    /// "Restore this field to this version": sets <paramref name="key"/> of <paramref name="e"/> to <paramref name="value"/> as one
    /// undoable change of <paramref name="model"/> (in a workspace this goes to the session like any edit). Refuses pseudo-fields,
    /// system marks and values that refer to elements the current model doesn't have.
    /// </summary>
    public static void Restore(ModelRepository model, Element e, string key, string? value, FlatModel? version = null)
    {
        if (HistoryKeys.IsPseudo(key) || SystemKeys.Contains(key)) throw new InvalidOperationException($"“{LabelOf(key)}” can't be restored from history.");
        if (!model.Contains(e)) throw new InvalidOperationException("That element is no longer in the model.");
        if (value is { Length: > 0 } && value.StartsWith('_') && version != null)
            foreach (var id in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (version.Contains(id) && model.Find(id) is null)
                    throw new InvalidOperationException($"“{version.NameOf(id)}” no longer exists, so “{LabelOf(key)}” can't be restored to this version.");
        if (e.Get(key) == (value ?? "")) return;
        model.Set(e, key, string.IsNullOrEmpty(value) ? null : value);
    }
}

/// <summary>Inline word diff: the longest common subsequence of words and the whitespace between them.</summary>
public static class WordDiff
{
    /// <summary>Above this many word pairs the diff shows the whole old and new text (keeps big documents cheap).</summary>
    public const int MaxCells = 400_000;

    public static IReadOnlyList<WordSegment> Compare(string before, string after)
    {
        var a = Tokens(before);
        var b = Tokens(after);
        int prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix]) prefix++;
        int suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
        var segs = new List<WordSegment>();
        if (prefix > 0) Add(segs, WordOp.Same, string.Concat(a.Take(prefix)));
        var am = a.Skip(prefix).Take(a.Count - prefix - suffix).ToList();
        var bm = b.Skip(prefix).Take(b.Count - prefix - suffix).ToList();
        if ((long)am.Count * bm.Count > MaxCells)
        {
            Add(segs, WordOp.Removed, string.Concat(am));
            Add(segs, WordOp.Added, string.Concat(bm));
        }
        else Middle(segs, am, bm);
        if (suffix > 0) Add(segs, WordOp.Same, string.Concat(a.Skip(a.Count - suffix)));
        return segs;
    }

    private static void Middle(List<WordSegment> segs, List<string> a, List<string> b)
    {
        var l = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
            for (var j = b.Count - 1; j >= 0; j--)
                l[i, j] = a[i] == b[j] ? l[i + 1, j + 1] + 1 : Math.Max(l[i + 1, j], l[i, j + 1]);
        int x = 0, y = 0;
        while (x < a.Count || y < b.Count)
        {
            if (x < a.Count && y < b.Count && a[x] == b[y]) { Add(segs, WordOp.Same, a[x]); x++; y++; }
            else if (x < a.Count && (y >= b.Count || l[x + 1, y] >= l[x, y + 1])) Add(segs, WordOp.Removed, a[x++]);   // removed words first
            else Add(segs, WordOp.Added, b[y++]);
        }
    }

    private static void Add(List<WordSegment> segs, WordOp op, string text)
    {
        if (text.Length == 0) return;
        if (segs.Count > 0 && segs[^1].Op == op) segs[^1] = segs[^1] with { Text = segs[^1].Text + text };
        else segs.Add(new WordSegment(op, text));
    }

    /// <summary>Words and the whitespace between them, as separate tokens.</summary>
    internal static List<string> Tokens(string s)
    {
        var list = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            var start = i;
            var space = char.IsWhiteSpace(s[i]);
            while (i < s.Length && char.IsWhiteSpace(s[i]) == space) i++;
            list.Add(s[start..i]);
        }
        return list;
    }
}
