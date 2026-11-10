using System.Text.RegularExpressions;

namespace Meridian.Web.Integrations.Requirements;

/// <summary>Word-level diff (longest common subsequence) for the requirement conflict dialog.</summary>
public static partial class TextDiff
{
    public enum Op { Same, Removed, Added }

    public sealed record Part(Op Op, string Text);

    /// <summary>Above this many words per side the diff falls back to "all removed, all added".</summary>
    public const int MaxWords = 2000;

    public static IReadOnlyList<Part> Words(string before, string after)
    {
        var a = Tokens().Matches(before ?? "").Select(m => m.Value).ToArray();
        var b = Tokens().Matches(after ?? "").Select(m => m.Value).ToArray();
        if (a.Length > MaxWords || b.Length > MaxWords)
            return Merge([new Part(Op.Removed, before ?? ""), new Part(Op.Added, after ?? "")]);
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var parts = new List<Part>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { parts.Add(new Part(Op.Same, a[x])); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) parts.Add(new Part(Op.Removed, a[x++]));
            else parts.Add(new Part(Op.Added, b[y++]));
        }
        while (x < a.Length) parts.Add(new Part(Op.Removed, a[x++]));
        while (y < b.Length) parts.Add(new Part(Op.Added, b[y++]));
        return Merge(parts);
    }

    private static List<Part> Merge(IEnumerable<Part> parts)
    {
        var merged = new List<Part>();
        foreach (var p in parts.Where(p => p.Text.Length > 0))
        {
            if (merged.Count > 0 && merged[^1].Op == p.Op) merged[^1] = p with { Text = merged[^1].Text + p.Text };
            else merged.Add(p);
        }
        return merged;
    }

    // Words with their trailing whitespace, so joining the parts gives back the text.
    [GeneratedRegex(@"\S+\s*|\s+")]
    private static partial Regex Tokens();
}
