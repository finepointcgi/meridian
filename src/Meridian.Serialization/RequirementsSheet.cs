using System.Text;
using Meridian.Core.Analysis;
using Meridian.Core.Model;

namespace Meridian.Serialization;

public enum SheetChangeKind { Added, Changed, Removed }

/// <summary>One field difference: model value → sheet value.</summary>
public sealed record SheetFieldChange(string Key, string Label, string Old, string New);

/// <summary>One row of a re-import preview. <see cref="Accept"/> is toggled by the user before applying.</summary>
public sealed class SheetRowChange
{
    public required SheetChangeKind Kind { get; init; }
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public Element? Existing { get; init; }
    public string ParentId { get; init; } = "";
    public IReadOnlyList<SheetFieldChange> Fields { get; init; } = [];
    /// <summary>Added and changed rows default to accepted; removals must be opted into.</summary>
    public bool Accept { get; set; }
}

public sealed record SheetDiff(IReadOnlyList<SheetRowChange> Rows, int Unchanged, IReadOnlyList<string> Warnings)
{
    public int Count(SheetChangeKind k) => Rows.Count(r => r.Kind == k);
}

/// <summary>
/// Requirements spreadsheet round trip (.xlsx and .csv share the same columns): export, then re-import matched by
/// requirement id with a reviewable diff, applied as one undoable transaction.
/// </summary>
public static class RequirementsSheet
{
    private const string AttrHeaderPrefix = "Attr: ";

    /// <summary>Editable columns (header, field key). "Parent Id", "Satisfied By" and "Verified By" are handled separately.</summary>
    public static readonly (string Header, string Key)[] FieldColumns =
    [
        ("Name", FieldKeys.Name),
        ("Text", FieldKeys.Text),
        ("Priority", ReqFields.Priority),
        ("Status", ReqFields.Status),
        ("Owner", ReqFields.Owner),
        ("Verification Method", ReqFields.VerificationMethod),
        ("Risk", ReqFields.Risk),
        ("Rationale", ReqFields.Rationale),
        ("Documentation", FieldKeys.Documentation),
    ];

    public static Xlsx.Sheet ToSheet(ModelRepository model)
    {
        var custom = ReqFields.CustomAttributeNames(model);
        var header = new List<string> { "Id" };
        header.AddRange(FieldColumns.Select(c => c.Header));
        header.Add("Parent Id");
        header.AddRange(custom.Select(c => AttrHeaderPrefix + c));
        header.Add("Satisfied By");
        header.Add("Verified By");
        var rows = new List<IReadOnlyList<string>>();
        foreach (var r in RequirementIds.InDocumentOrder(model))
        {
            var row = new List<string> { r.Get(FieldKeys.RequirementId) };
            row.AddRange(FieldColumns.Select(c => r.Get(c.Key)));
            row.Add(r.Owner?.Kind == ElementKind.Requirement ? r.Owner.Get(FieldKeys.RequirementId) : "");
            row.AddRange(custom.Select(c => r.Get(ReqFields.CustomPrefix + c)));
            row.Add(string.Join("; ", model.Incoming(r, ElementKind.Satisfy).Select(x => x.Source.Name)));
            row.Add(string.Join("; ", model.Incoming(r, ElementKind.Verify).Select(x => x.Source.Name)));
            rows.Add(row);
        }
        return new Xlsx.Sheet("Requirements", header, rows);
    }

    public static byte[] ExportXlsx(ModelRepository model) => Xlsx.Write(ToSheet(model));

    public static string ExportCsv(ModelRepository model) => ToCsv(ToSheet(model));

    public static string ToCsv(Xlsx.Sheet sheet)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", sheet.Header.Select(Quote)));
        foreach (var row in sheet.Rows) sb.AppendLine(string.Join(",", row.Select(Quote)));
        return sb.ToString();
    }

    private static string Quote(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>Reads .xlsx or .csv content into rows.</summary>
    public static List<List<string>> ReadRows(string fileName, Stream content)
    {
        if (fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(content, Encoding.UTF8);
            return RequirementsCsv.Parse(reader.ReadToEnd()).ToList();
        }
        using var ms = new MemoryStream();
        content.CopyTo(ms);
        ms.Position = 0;
        return Xlsx.Read(ms);
    }

    /// <summary>Compares sheet rows with the model's requirements, matching by id.</summary>
    public static SheetDiff Diff(ModelRepository model, IReadOnlyList<List<string>> rows)
    {
        var warnings = new List<string>();
        if (rows.Count == 0) return new SheetDiff([], 0, ["The sheet is empty"]);
        var header = rows[0].Select(h => h.Trim()).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Any(n => string.Equals(h, n, StringComparison.OrdinalIgnoreCase)));
        var cId = Col("Id", "Req Id", "Requirement Id");
        var cParent = Col("Parent Id", "Parent");
        var fieldCols = FieldColumns.Select(c => (c.Header, c.Key, Index: c.Key == FieldKeys.Text ? Col(c.Header, "Description", "Requirement Text") : c.Key == FieldKeys.Name ? Col(c.Header, "Title") : Col(c.Header)))
                                    .Where(c => c.Index >= 0).ToList();
        var attrCols = header.Select((h, i) => (h, i)).Where(t => t.h.StartsWith(AttrHeaderPrefix, StringComparison.OrdinalIgnoreCase) && t.h.Length > AttrHeaderPrefix.Length)
                             .Select(t => (Header: t.h, Key: ReqFields.CustomPrefix + t.h[AttrHeaderPrefix.Length..].Trim(), Index: t.i)).ToList();
        if (cId < 0) throw new FormatException("The sheet needs an 'Id' column to match requirements");

        var byId = new Dictionary<string, Element>(StringComparer.Ordinal);
        foreach (var r in model.OfKind(ElementKind.Requirement))
        {
            var id = r.Get(FieldKeys.RequirementId);
            if (id.Length > 0 && !byId.TryAdd(id, r)) warnings.Add($"Id {id} is used by several requirements in the model; the first one is matched");
        }

        var changes = new List<SheetRowChange>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unchanged = 0;
        foreach (var (row, line) in rows.Skip(1).Select((r, i) => (r, i + 2)))
        {
            string Cell(int i) => i >= 0 && i < row.Count ? row[i].Trim() : "";
            var id = Cell(cId);
            var values = fieldCols.Select(c => (c.Header, c.Key, Value: Cell(c.Index))).Concat(attrCols.Select(c => (c.Header, c.Key, Value: Cell(c.Index)))).ToList();
            if (id.Length == 0 && values.All(v => v.Value.Length == 0)) continue;
            if (id.Length > 0 && !seen.Add(id)) { warnings.Add($"Row {line}: id {id} appears more than once; only the first row is used"); continue; }
            var parent = Cell(cParent);
            if (id.Length > 0 && byId.TryGetValue(id, out var existing))
            {
                var diffs = values.Where(v => existing.Get(v.Key) != v.Value)
                                  .Select(v => new SheetFieldChange(v.Key, v.Header.StartsWith(AttrHeaderPrefix) ? v.Header[AttrHeaderPrefix.Length..] : v.Header, existing.Get(v.Key), v.Value)).ToList();
                var currentParent = existing.Owner?.Kind == ElementKind.Requirement ? existing.Owner.Get(FieldKeys.RequirementId) : "";
                if (cParent >= 0 && parent != currentParent && (parent.Length == 0 || byId.ContainsKey(parent)))
                    diffs.Add(new SheetFieldChange("parent", "Parent Id", currentParent, parent));
                if (diffs.Count == 0) { unchanged++; continue; }
                changes.Add(new SheetRowChange { Kind = SheetChangeKind.Changed, Id = id, Name = existing.Name, Existing = existing, ParentId = parent, Fields = diffs, Accept = true });
            }
            else
            {
                var fields = values.Where(v => v.Value.Length > 0)
                                   .Select(v => new SheetFieldChange(v.Key, v.Header.StartsWith(AttrHeaderPrefix) ? v.Header[AttrHeaderPrefix.Length..] : v.Header, "", v.Value)).ToList();
                var name = values.FirstOrDefault(v => v.Key == FieldKeys.Name).Value ?? "";
                changes.Add(new SheetRowChange { Kind = SheetChangeKind.Added, Id = id, Name = name.Length > 0 ? name : id, ParentId = parent, Fields = fields, Accept = true });
            }
        }
        foreach (var (id, r) in byId.Where(kv => !seen.Contains(kv.Key)).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            changes.Add(new SheetRowChange { Kind = SheetChangeKind.Removed, Id = id, Name = r.Name, Existing = r, Accept = false });
        return new SheetDiff(changes, unchanged, warnings);
    }

    /// <summary>Applies the accepted rows as one undoable transaction. New requirements go into <paramref name="target"/>.</summary>
    public static (int Added, int Changed, int Removed) Apply(ModelRepository model, Element target, IEnumerable<SheetRowChange> rows)
    {
        var accepted = rows.Where(r => r.Accept).ToList();
        int added = 0, changed = 0, removed = 0;
        if (accepted.Count == 0) return (0, 0, 0);
        var byId = model.OfKind(ElementKind.Requirement).Where(r => r.Get(FieldKeys.RequirementId).Length > 0)
                        .GroupBy(r => r.Get(FieldKeys.RequirementId)).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var parents = new List<(Element Req, string ParentId)>();
        using (model.History.Transaction($"Import requirements ({accepted.Count} rows)"))
        {
            foreach (var row in accepted)
            {
                switch (row.Kind)
                {
                    case SheetChangeKind.Added:
                    {
                        var req = model.Create(target, ElementKind.Requirement, row.Name.Length > 0 ? row.Name : null);
                        if (row.Id.Length > 0) model.Set(req, FieldKeys.RequirementId, row.Id);
                        foreach (var f in row.Fields.Where(f => f.Key != FieldKeys.Name)) model.Set(req, f.Key, f.New);
                        byId[req.Get(FieldKeys.RequirementId)] = req;
                        if (row.ParentId.Length > 0) parents.Add((req, row.ParentId));
                        added++;
                        break;
                    }
                    case SheetChangeKind.Changed when row.Existing is { } e && model.Contains(e):
                        foreach (var f in row.Fields)
                        {
                            if (f.Key == "parent") parents.Add((e, f.New));
                            else model.Set(e, f.Key, f.Key == FieldKeys.Name ? f.New.Trim() : f.New);
                        }
                        changed++;
                        break;
                    case SheetChangeKind.Removed when row.Existing is { } e && model.Contains(e):
                        model.Delete(e);
                        removed++;
                        break;
                }
            }
            foreach (var (req, pid) in parents)
            {
                if (!model.Contains(req)) continue;
                if (pid.Length == 0)
                {
                    if (req.Owner?.Kind == ElementKind.Requirement) model.Move(req, TraceabilityTable.PackageOf(req) ?? target);
                }
                else if (byId.TryGetValue(pid, out var parent) && model.Contains(parent) && !ReferenceEquals(parent, req) && !req.IsAncestorOf(parent) && !ReferenceEquals(req.Owner, parent))
                    model.Move(req, parent);
            }
        }
        return (added, changed, removed);
    }
}
