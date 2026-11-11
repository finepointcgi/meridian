using System.Globalization;

namespace Meridian.Web.Services;

/// <summary>
/// Counts with the right noun form for text shown to people: "1 diagram", "2 diagrams", "0 relationships",
/// "1 push", "3 pushes". English only, like the rest of the UI.
/// </summary>
public static class Plural
{
    /// <summary>The noun for a count: <paramref name="singular"/> for exactly one, else <paramref name="plural"/> (default: singular + "s").</summary>
    public static string Noun(long count, string singular, string? plural = null) => count == 1 ? singular : plural ?? singular + "s";

    /// <summary>"<paramref name="count"/> noun", e.g. <c>Of(1, "diagram")</c> → "1 diagram", <c>Of(2, "push", "pushes")</c> → "2 pushes".</summary>
    public static string Of(long count, string singular, string? plural = null) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + Noun(count, singular, plural);
}
