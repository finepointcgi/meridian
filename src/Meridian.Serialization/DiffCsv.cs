using System.Text;
using Meridian.Core.Analysis;
using Meridian.Core.Language;

namespace Meridian.Serialization;

/// <summary>Exports a model comparison (e.g. baseline vs baseline) as CSV: one row per changed field, one per added/removed/moved element.</summary>
public static class DiffCsv
{
    public static readonly string[] Header = ["Change", "Element Kind", "Element", "Path", "Element Id", "Field", "Before", "After"];

    public static string Export(IReadOnlyList<ElementDiff> diff, string beforeLabel, string afterLabel)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Quote($"Comparison: {beforeLabel} → {afterLabel}"));
        sb.AppendLine(string.Join(",", Header));
        foreach (var d in diff.OrderBy(d => d.Kind).ThenBy(d => d.Path, StringComparer.Ordinal).ThenBy(d => d.Name, StringComparer.Ordinal))
        {
            var lead = new[] { d.Kind.ToString(), Metamodel.DisplayName(d.ElementKind), d.Name, d.Path, d.Id };
            if (d.Fields.Count == 0)
                sb.AppendLine(string.Join(",", lead.Concat(["", "", ""]).Select(Quote)));
            else
                foreach (var f in d.Fields)
                    sb.AppendLine(string.Join(",", lead.Concat([f.Key, f.Before ?? "", f.After ?? ""]).Select(Quote)));
        }
        return sb.ToString();
    }

    private static string Quote(string s)
    {
        // Guard against spreadsheet formula injection, then CSV-quote.
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@') s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
