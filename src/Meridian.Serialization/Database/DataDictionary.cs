using System.Text;
using Meridian.Core.Analysis;
using Meridian.Core.DataModeling;
using Meridian.Core.Model;

namespace Meridian.Serialization.Database;

/// <summary>The data dictionary: one row per column (schema, table, column, type, keys, comments, subject areas) as CSV or .xlsx.</summary>
public static class DataDictionary
{
    public static readonly string[] Header =
        ["Database", "Schema", "Table", "Table kind", "Table comment", "Subject areas", "#", "Column", "Data type", "Nullable", "Default", "Keys", "References", "Identity / generated", "Column comment"];

    public static List<IReadOnlyList<string>> Rows(ModelRepository m, Element scope, IEnumerable<Element>? tables = null)
    {
        var rows = new List<IReadOnlyList<string>>();
        var list = (tables ?? DbModel.TablesIn(m, scope)).Where(t => t.Kind == ElementKind.Table).Distinct()
            .OrderBy(t => DbModel.SchemaOf(t)?.Name ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var t in list)
        {
            var fkCols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fk in m.Outgoing(t, ElementKind.ForeignKey).Cast<Relationship>())
            {
                var refs = DbModel.RefColumns(fk);
                var cols = DbModel.SplitList(fk.Get(FieldKeys.FkColumns));
                for (var i = 0; i < cols.Count; i++) fkCols[cols[i]] = $"{fk.Target.Name}.{(i < refs.Count ? refs[i].Name : "?")}";
            }
            var groups = string.Join(", ", DbModel.GroupsOf(m, t).Select(g => g.Name));
            var kind = DbModel.KindName(t) ?? "Table";
            var n = 0;
            var columns = DbModel.Columns(t).ToList();
            if (columns.Count == 0)
                rows.Add([DbModel.DatabaseOf(t)?.Name ?? "", DbModel.SchemaOf(t)?.Name ?? "", t.Name, kind, t.Documentation, groups, "", "", "", "", "", "", "", "", ""]);
            foreach (var c in columns)
            {
                n++;
                var keys = new List<string>();
                if (DbModel.IsPk(c)) keys.Add("PK");
                if (fkCols.ContainsKey(c.Name)) keys.Add("FK");
                if (DbModel.IsUnique(c) && !DbModel.IsPk(c)) keys.Add("UQ");
                var gen = c.Get(DbFields.Generated) is { Length: > 0 } g ? $"generated: {g}" : c.Get(DbFields.Identity) is { Length: > 0 } id ? $"identity ({id})" : "";
                rows.Add([
                    DbModel.DatabaseOf(t)?.Name ?? "", DbModel.SchemaOf(t)?.Name ?? "", t.Name, kind, n == 1 ? t.Documentation : "", n == 1 ? groups : "",
                    n.ToString(), c.Name, c.Get(FieldKeys.SqlType), DbModel.IsNullable(c) ? "yes" : "no", c.Get(FieldKeys.DefaultValue),
                    string.Join(", ", keys), fkCols.GetValueOrDefault(c.Name) ?? "", gen, c.Documentation,
                ]);
            }
        }
        return rows;
    }

    public static string Csv(ModelRepository m, Element scope, IEnumerable<Element>? tables = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Header.Select(Cell)));
        foreach (var r in Rows(m, scope, tables)) sb.AppendLine(string.Join(",", r.Select(Cell)));
        return sb.ToString();
    }

    public static byte[] Xlsx(ModelRepository m, Element scope, IEnumerable<Element>? tables = null)
    {
        var rows = Rows(m, scope, tables);
        var tableRows = rows.GroupBy(r => (r[1], r[2])).Select(g => (IReadOnlyList<string>)new[] { g.First()[0], g.Key.Item1, g.Key.Item2, g.First()[3], g.Count(x => x[7].Length > 0).ToString(), g.First()[5], g.First()[4] }).ToList();
        return Serialization.Xlsx.Write(
            new Serialization.Xlsx.Sheet("Columns", Header, rows),
            new Serialization.Xlsx.Sheet("Tables", ["Database", "Schema", "Table", "Kind", "Columns", "Subject areas", "Comment"], tableRows));
    }

    private static string Cell(string s)
    {
        s = CsvSafety.Neutralize(s);
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

/// <summary>Reads a database schema from an uploaded file: DDL (.sql, .ddl, .txt), SQL Server .dacpac, or Mermaid erDiagram (.mmd, .mermaid, .md).</summary>
public static class DatabaseFiles
{
    public static readonly string[] Extensions = [".sql", ".ddl", ".txt", ".dacpac", ".mmd", ".mermaid", ".md"];

    public static bool IsDatabaseFile(string fileName) => Extensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>The catalog in <paramref name="content"/>; <paramref name="dialect"/> null detects it for DDL.</summary>
    public static DbCatalog Read(string fileName, Stream content, string? dialect = null)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (ext == ".dacpac") return DacpacReader.Read(content, name);
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        if (ext is ".mmd" or ".mermaid" or ".md" || ext == ".txt" && Mermaid.MermaidDatabase.HasErDiagram(text) && !text.Contains("CREATE", StringComparison.OrdinalIgnoreCase))
        {
            var c = Mermaid.MermaidDatabase.ToCatalog(text, name, markdown: ext == ".md");
            if (!string.IsNullOrEmpty(dialect)) c.Dialect = dialect;
            return c;
        }
        return DdlParser.Parse(text, dialect, name);
    }

    public static string DialectOf(SqlDialect d) => d switch
    {
        SqlDialect.MySql => "mysql",
        SqlDialect.SqlServer => "sqlserver",
        SqlDialect.Sqlite => "sqlite",
        SqlDialect.Oracle => "oracle",
        _ => "postgresql",
    };

    public static SqlDialect FromDialect(string? d) => d switch
    {
        "mysql" => SqlDialect.MySql,
        "sqlserver" => SqlDialect.SqlServer,
        "sqlite" => SqlDialect.Sqlite,
        "oracle" => SqlDialect.Oracle,
        _ => SqlDialect.PostgreSql,
    };
}
