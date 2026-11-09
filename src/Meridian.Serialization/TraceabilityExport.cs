using Meridian.Core.Analysis;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>Exports the traceability table (and its multi-level chain view) to .xlsx and .csv.</summary>
public static class TraceabilityExport
{
    public static Xlsx.Sheet TableSheet(IEnumerable<TraceRow> rows)
    {
        var header = new List<string> { "Id", "Name", "Text" };
        header.AddRange(TraceabilityTable.Columns.Select(TraceabilityTable.Title));
        header.AddRange(["Status", "Coverage", "Gaps", "Suspect links"]);
        var data = rows.Select(r =>
        {
            var row = new List<string> { r.Id, r.Name, r.Text };
            row.AddRange(TraceabilityTable.Columns.Select(c => string.Join("; ", r[c].Select(l => LinkText(c, l)))));
            row.Add(r.Status);
            row.Add(r.Coverage.ToString());
            row.Add(string.Join(", ", new[] { r.SatisfyGap ? "not satisfied" : null, r.VerifyGap ? "not verified" : null }.OfType<string>()));
            row.Add(r.Links.Values.Sum(l => l.Count(x => x.Suspect)) is var n and > 0 ? n.ToString() : "");
            return (IReadOnlyList<string>)row;
        }).ToList();
        return new Xlsx.Sheet("Traceability", header, data);
    }

    private static string LinkText(TraceColumn c, TraceLink l)
    {
        var s = TraceabilityTable.Label(l.Other);
        if (c == TraceColumn.VerifiedBy && TraceabilityTable.Verdict(l.Other) is { Length: > 0 } v) s += $" [{v}]";
        return l.Suspect ? s + " (suspect)" : s;
    }

    public static Xlsx.Sheet ChainSheet(IEnumerable<TraceChainRow> rows) => new("Trace chain",
        ["Need", "Requirement", "Derived", "Level", "Design", "Test", "Verdict", "Gap", "Suspect"],
        rows.Select(r => (IReadOnlyList<string>)
        [
            r.Need?.DisplayName ?? "", TraceabilityTable.Label(r.Requirement), r.Derived is { } d ? TraceabilityTable.Label(d) : "", r.Depth.ToString(),
            r.Design?.DisplayName ?? "", r.Test?.DisplayName ?? "", r.Verdict, r.Gap ? "yes" : "", r.Suspect ? "yes" : "",
        ]).ToList());

    public static Xlsx.Sheet OrphanSheet(IEnumerable<Element> orphans) => new("Orphan design",
        ["Element", "Kind", "Qualified name"],
        orphans.Select(o => (IReadOnlyList<string>)[o.DisplayName, o.Kind.ToString(), o.QualifiedName]).ToList());

    public static byte[] ToXlsx(ModelRepository model, IEnumerable<TraceRow>? rows = null) =>
        Xlsx.Write(TableSheet(rows ?? TraceabilityTable.Build(model)), ChainSheet(TraceabilityTable.Chains(model)),
                   OrphanSheet(TraceabilityTable.OrphanDesignElements(model)));

    public static string ToCsv(IEnumerable<TraceRow> rows) => RequirementsSheet.ToCsv(TableSheet(rows));
}
