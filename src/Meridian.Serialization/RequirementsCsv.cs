using System.Text;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>Round-trips requirements with spreadsheets (the most common way requirements enter these tools).</summary>
public static class RequirementsCsv
{
    public static readonly string[] Header = ["Id", "Name", "Text", "Parent Id", "Satisfied By", "Verified By", "Documentation"];

    public static string Export(ModelRepository model)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Header));
        foreach (var r in model.OfKind(ElementKind.Requirement).OrderBy(r => r.Get(FieldKeys.RequirementId), StringComparer.OrdinalIgnoreCase))
        {
            var parent = r.Owner?.Kind == ElementKind.Requirement ? r.Owner.Get(FieldKeys.RequirementId) : "";
            sb.AppendLine(string.Join(",", new[]
            {
                r.Get(FieldKeys.RequirementId), r.Name, r.Get(FieldKeys.Text), parent,
                string.Join("; ", model.Incoming(r, ElementKind.Satisfy).Select(x => x.Source.Name)),
                string.Join("; ", model.Incoming(r, ElementKind.Verify).Select(x => x.Source.Name)),
                r.Documentation,
            }.Select(Quote)));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Imports or updates requirements (matched by Id) into <paramref name="target"/>. Runs as one undoable transaction.
    /// Columns are matched by header name; "Id", "Name" and "Text" are recognised, "Parent Id" nests requirements.
    /// </summary>
    public static (int Created, int Updated) Import(ModelRepository model, Element target, string csv)
    {
        var rows = Parse(csv).ToList();
        if (rows.Count == 0) return (0, 0);
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Contains(h));
        var cId = Col("id", "req id", "requirement id");
        var cName = Col("name", "title");
        var cText = Col("text", "description", "requirement text");
        var cParent = Col("parent id", "parent");
        var cDoc = Col("documentation", "notes", "rationale");
        if (cId < 0 && cName < 0) throw new FormatException("CSV needs at least an 'Id' or 'Name' column");

        var byId = model.OfKind(ElementKind.Requirement).Where(r => r.Get(FieldKeys.RequirementId).Length > 0)
            .GroupBy(r => r.Get(FieldKeys.RequirementId)).ToDictionary(g => g.Key, g => g.First());
        int created = 0, updated = 0;
        var parents = new List<(Element req, string parentId)>();
        using (model.History.Transaction("Import requirements"))
        {
            foreach (var row in rows.Skip(1))
            {
                string Cell(int i) => i >= 0 && i < row.Count ? row[i].Trim() : "";
                var id = Cell(cId);
                var name = Cell(cName);
                if (id.Length == 0 && name.Length == 0) continue;
                if (id.Length > 0 && byId.TryGetValue(id, out var existing))
                {
                    if (cName >= 0) model.Set(existing, FieldKeys.Name, name);
                    if (cText >= 0) model.Set(existing, FieldKeys.Text, Cell(cText));
                    if (cDoc >= 0) model.Set(existing, FieldKeys.Documentation, Cell(cDoc));
                    updated++;
                    if (Cell(cParent).Length > 0) parents.Add((existing, Cell(cParent)));
                    continue;
                }
                var req = model.Create(target, ElementKind.Requirement, name.Length > 0 ? name : id);
                if (id.Length > 0) model.Set(req, FieldKeys.RequirementId, id);
                model.Set(req, FieldKeys.Text, Cell(cText));
                model.Set(req, FieldKeys.Documentation, Cell(cDoc));
                byId[req.Get(FieldKeys.RequirementId)] = req;
                created++;
                if (Cell(cParent).Length > 0) parents.Add((req, Cell(cParent)));
            }
            foreach (var (req, pid) in parents)
                if (byId.TryGetValue(pid, out var parent) && !ReferenceEquals(parent, req) && !req.IsAncestorOf(parent) && !ReferenceEquals(req.Owner, parent))
                    model.Move(req, parent);
        }
        return (created, updated);
    }

    private static string Quote(string s)
    {
        s = Meridian.Core.Analysis.CsvSafety.Neutralize(s);
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>RFC 4180 parser (quoted fields, embedded newlines and quotes).</summary>
    public static IEnumerable<List<string>> Parse(string csv)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(ReadCell(field)); field.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(ReadCell(field)); field.Clear();
                    yield return row;
                    row = new List<string>();
                    break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(ReadCell(field)); yield return row; }
    }

    // Cells Meridian exported with a formula guard (see CsvSafety) read back as the original text.
    private static string ReadCell(StringBuilder field) => Meridian.Core.Analysis.CsvSafety.Restore(field.ToString());
}
