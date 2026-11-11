using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

/// <summary>One column of a generic table: a <see cref="ModelValues"/> key, an optional header and a saved filter.</summary>
public sealed class TableViewColumn
{
    public TableViewColumn() { }
    public TableViewColumn(string key, string? label = null) { Key = key; Label = label; }

    [JsonPropertyName("key")] public string Key { get; set; } = "name";
    [JsonPropertyName("label")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Label { get; set; }
    [JsonPropertyName("filter")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Filter { get; set; }
    [JsonPropertyName("width")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Width { get; set; }

    [JsonIgnore] public string Header => string.IsNullOrWhiteSpace(Label) ? ModelValues.Label(Key) : Label!;
}

/// <summary>
/// A generic table (Cameo "generic table" / EA "model view"): rows are the elements matched by a query, columns are value keys.
/// Persisted on a <see cref="ElementKind.TableView"/> element: the row query in <see cref="ViewFields.Query"/>, the columns and
/// sort order as JSON in <see cref="ViewFields.Columns"/>.
/// </summary>
public sealed class TableDefinition
{
    public string Query { get; set; } = "kind:Block";
    public List<TableViewColumn> Columns { get; set; } = new();
    public string? SortKey { get; set; }
    public bool SortDescending { get; set; }

    private sealed class Dto
    {
        [JsonPropertyName("columns")] public List<TableViewColumn> Columns { get; set; } = new();
        [JsonPropertyName("sort")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Sort { get; set; }
        [JsonPropertyName("desc")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Desc { get; set; }
    }

    public string ColumnsJson() => JsonSerializer.Serialize(new Dto { Columns = Columns, Sort = SortKey, Desc = SortDescending });

    public static TableDefinition FromElement(Element e)
    {
        var def = new TableDefinition { Query = e.Get(ViewFields.Query) };
        var json = e.Get(ViewFields.Columns);
        if (json.Length > 0)
        {
            try
            {
                var dto = JsonSerializer.Deserialize<Dto>(json);
                if (dto != null) { def.Columns = dto.Columns.Where(c => !string.IsNullOrWhiteSpace(c.Key)).ToList(); def.SortKey = dto.Sort; def.SortDescending = dto.Desc; }
            }
            catch (JsonException) { /* fall back to default columns */ }
        }
        if (def.Columns.Count == 0) def.Columns = DefaultColumns(ModelQuery.TryParse(def.Query, out var q, out _) && q.Kinds.Count == 1 ? q.Kinds[0] : null);
        return def;
    }

    public TableDefinition Clone() => new()
    {
        Query = Query, SortKey = SortKey, SortDescending = SortDescending,
        Columns = Columns.Select(c => new TableViewColumn(c.Key, c.Label) { Filter = c.Filter, Width = c.Width }).ToList(),
    };

    /// <summary>Sensible starting columns for a row kind.</summary>
    public static List<TableViewColumn> DefaultColumns(ElementKind? kind) => kind switch
    {
        ElementKind.Requirement => [new("reqId"), new("name"), new("text"), new("satisfiedBy"), new("verifiedBy")],
        ElementKind.Block or ElementKind.Class or ElementKind.Component or ElementKind.InterfaceBlock => [new("name"), new("owner"), new("stereotype"), new("satisfies"), new("allocatedFrom")],
        ElementKind.Property or ElementKind.Port => [new("name"), new("owner"), new("type"), new("field:multiplicity"), new("field:defaultValue")],
        ElementKind.Action or ElementKind.Activity => [new("name"), new("owner"), new("allocatedTo")],
        ElementKind.TestCase => [new("name"), new("owner"), new("verifies")],
        ElementKind.Artifact or ElementKind.DeploymentSpecification => [.. Deployment.DeploymentModel.DeploymentTable().Columns],
        _ => [new("name"), new("kind"), new("owner"), new("documentation")],
    };
}

public sealed record ModelTableRow(Element Element, IReadOnlyList<string> Cells);

/// <summary>Evaluates table definitions: rows, filters, sorting and CSV export.</summary>
public static class ModelTables
{
    /// <summary>The rows of a table after the query, the per-column filters and the sort order are applied.</summary>
    public static IReadOnlyList<ModelTableRow> Build(ModelRepository m, TableDefinition def)
    {
        var q = ModelQuery.Parse(def.Query);
        var rows = q.Run(m).Select(e => new ModelTableRow(e, def.Columns.Select(c => ModelValues.Get(m, e, c.Key)).ToList())).ToList();
        for (var i = 0; i < def.Columns.Count; i++)
        {
            var f = def.Columns[i].Filter;
            if (string.IsNullOrWhiteSpace(f)) continue;
            var col = i;
            rows = rows.Where(r => CellMatches(r.Cells[col], f)).ToList();
        }
        var sortIdx = def.SortKey == null ? -1 : def.Columns.FindIndex(c => c.Key == def.SortKey);
        if (sortIdx >= 0)
        {
            // Stable sort; empty cells stay last in both directions.
            var keyed = rows.Select((r, i) => (r, i)).ToList();
            keyed.Sort((x, y) =>
            {
                string a = x.r.Cells[sortIdx], b = y.r.Cells[sortIdx];
                bool ea = string.IsNullOrWhiteSpace(a), eb = string.IsNullOrWhiteSpace(b);
                var c = ea || eb ? (ea == eb ? 0 : ea ? 1 : -1) : (def.SortDescending ? -1 : 1) * CompareCells(a, b);
                return c != 0 ? c : x.i.CompareTo(y.i);
            });
            rows = keyed.Select(k => k.r).ToList();
        }
        return rows;
    }

    /// <summary>Per-column filter: plain text is a case-insensitive substring; <c>&gt;10</c>, <c>&lt;=5</c>, <c>=x</c>, <c>!=x</c> compare; <c>!x</c> excludes.</summary>
    public static bool CellMatches(string cell, string filter)
    {
        filter = filter.Trim();
        if (filter.Length == 0) return true;
        foreach (var (prefix, test) in new (string, Func<int, bool>)[] { (">=", c => c >= 0), ("<=", c => c <= 0), ("!=", c => c != 0), (">", c => c > 0), ("<", c => c < 0), ("=", c => c == 0) })
        {
            if (!filter.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var v = filter[prefix.Length..].Trim();
            if (prefix is "=" or "!=") return test(CompareExpr.Compare(cell, v));
            return cell.Trim().Length > 0 && test(CompareExpr.Compare(cell, v));
        }
        if (filter.StartsWith('!')) return !cell.Contains(filter[1..].Trim(), StringComparison.OrdinalIgnoreCase);
        return cell.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Numbers sort numerically, empty cells last, everything else case-insensitively.</summary>
    public static int CompareCells(string a, string b)
    {
        var ea = string.IsNullOrWhiteSpace(a);
        var eb = string.IsNullOrWhiteSpace(b);
        if (ea || eb) return ea == eb ? 0 : ea ? 1 : -1;
        return CompareExpr.Compare(a, b);
    }

    public static string ToCsv(TableDefinition def, IEnumerable<ModelTableRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", def.Columns.Select(c => Csv(c.Header))));
        foreach (var r in rows) sb.AppendLine(string.Join(",", r.Cells.Select(Csv)));
        return sb.ToString();
    }

    private static string Csv(string s) => Quote(CsvSafety.Neutralize(s));

    private static string Quote(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>Column keys worth offering for rows of the given elements: fields, tagged values, stereotype properties and value properties.</summary>
    public static IReadOnlyList<(string Group, string Key)> SuggestColumns(ModelRepository m, IEnumerable<Element> rows, IEnumerable<ElementKind> kinds)
    {
        var list = new List<(string, string)>();
        foreach (var k in new[] { "name", "kind", "owner", "qualifiedName", "stereotype", "type", "documentation" }) list.Add(("General", k));
        var sample = rows.Take(500).ToList();
        var fields = kinds.SelectMany(Metamodel.EditableFields)
            .Concat(sample.SelectMany(e => e.Fields.Keys))
            .Where(f => !f.StartsWith(FieldKeys.TagPrefix, StringComparison.Ordinal) && f is not (FieldKeys.Name or FieldKeys.Documentation or FieldKeys.Stereotypes or FieldKeys.Type))
            .Distinct().OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var f in fields) list.Add(("Fields", "field:" + f));
        var tags = sample.SelectMany(e => e.TaggedValues.Select(t => t.Key))
            .Concat(sample.SelectMany(e => e.Stereotypes).Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(s => m.OfKind(ElementKind.Stereotype).Where(x => string.Equals(x.Name, s, StringComparison.OrdinalIgnoreCase)))
                .SelectMany(s => s.OwnedElements.Where(t => t.Kind == ElementKind.TagDefinition).Select(t => t.Name)))
            .Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase);
        foreach (var t in tags) list.Add(("Tagged values", FieldKeys.TagPrefix + t));
        var props = sample.SelectMany(e => e.OwnedElements.Where(c => c.Kind == ElementKind.Property && c.Get(FieldKeys.DefaultValue).Length > 0).Select(c => c.Name))
            .Where(n => n.Length > 0).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        foreach (var p in props) list.Add(("Value properties", "prop:" + p));
        foreach (var r in ModelValues.RelationshipColumns) list.Add(("Relationships", r));
        return list;
    }
}

/// <summary>Creates and updates saved views and rules as model elements (undoable, replicated, saved in .meridian files).</summary>
public static class SavedViews
{
    public const string ViewsPackageName = "Views";

    public static IEnumerable<Element> All(ModelRepository m) =>
        m.AllElements.Where(e => e.Kind is ElementKind.TableView or ElementKind.QueryView or ElementKind.ValidationRule)
            .OrderBy(e => e.Kind).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The default home for new views: a top-level "Views" package (created on demand inside the caller's transaction).</summary>
    public static Element DefaultOwner(ModelRepository m) =>
        m.Root.OwnedElements.FirstOrDefault(e => e.Kind == ElementKind.Package && e.Name == ViewsPackageName)
        ?? m.Create(m.Root, ElementKind.Package, ViewsPackageName);

    public static Element SaveTable(ModelRepository m, TableDefinition def, string name, Element? existing = null, Element? owner = null)
    {
        ModelQuery.Parse(def.Query);   // refuse to save an invalid query
        using (m.History.Transaction($"Save table view {name}"))
        {
            var e = Upsert(m, ElementKind.TableView, name, existing, owner);
            SetIfChanged(m, e, ViewFields.Query, def.Query);
            SetIfChanged(m, e, ViewFields.Columns, def.ColumnsJson());
            return e;
        }
    }

    public static Element SaveQuery(ModelRepository m, string query, string name, Element? existing = null, Element? owner = null)
    {
        ModelQuery.Parse(query);
        using (m.History.Transaction($"Save query {name}"))
        {
            var e = Upsert(m, ElementKind.QueryView, name, existing, owner);
            SetIfChanged(m, e, ViewFields.Query, query);
            return e;
        }
    }

    public static Element SaveRule(ModelRepository m, string query, string name, string severity, string message, Element? existing = null, Element? owner = null)
    {
        ModelQuery.Parse(query);
        using (m.History.Transaction($"Save validation rule {name}"))
        {
            var e = Upsert(m, ElementKind.ValidationRule, name, existing, owner);
            SetIfChanged(m, e, ViewFields.Query, query);
            SetIfChanged(m, e, ViewFields.Severity, severity.ToLowerInvariant());
            SetIfChanged(m, e, ViewFields.Message, message);
            return e;
        }
    }

    private static Element Upsert(ModelRepository m, ElementKind kind, string name, Element? existing, Element? owner)
    {
        name = string.IsNullOrWhiteSpace(name) ? Metamodel.DisplayName(kind) : name.Trim();
        if (existing != null && m.Contains(existing) && existing.Kind == kind)
        {
            SetIfChanged(m, existing, FieldKeys.Name, name);
            return existing;
        }
        var home = owner != null && m.Contains(owner) && Metamodel.CanOwn(owner.Kind, kind) ? owner : DefaultOwner(m);
        return m.Create(home, kind, name);
    }

    private static void SetIfChanged(ModelRepository m, Element e, string key, string value)
    {
        if (e.Get(key) != value) m.Set(e, key, string.IsNullOrEmpty(value) ? null : value);
    }
}
