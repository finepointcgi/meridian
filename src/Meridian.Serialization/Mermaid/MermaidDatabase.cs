using System.Text.RegularExpressions;
using Meridian.Core.DataModeling;

namespace Meridian.Serialization.Mermaid;

/// <summary>
/// Mermaid erDiagram as a database: entities become tables, attributes ("type name PK, FK "comment"") columns, and
/// relationships foreign keys from the "many" side to the "one" side (the referencing columns are the side's FK attributes,
/// or a new "&lt;table&gt;_&lt;key&gt;" column). The regular Mermaid import maps erDiagram to blocks instead.
/// </summary>
public static partial class MermaidDatabase
{
    [GeneratedRegex(@"^(?<type>[\w\-\(\),\[\]]+)\s+(?<name>[\w\-]+)(?<keys>(?:\s+(?:PK|FK|UK)(?:\s*,\s*(?:PK|FK|UK))*)?)\s*(?:""(?<comment>[^""]*)"")?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Attribute();

    /// <summary>True when the text holds at least one erDiagram.</summary>
    public static bool HasErDiagram(string text) => text.Contains("erDiagram", StringComparison.Ordinal);

    public static DbCatalog ToCatalog(string text, string name = "database", bool markdown = false)
    {
        var warnings = new List<string>();
        var catalog = new DbCatalog { Name = name, Dialect = "" };
        var schema = catalog.Schema("public");
        foreach (var block in MermaidSource.Split(text, markdown, warnings))
        {
            if (MermaidParser.KindOf(block.Keyword) != MermaidDiagramKind.EntityRelationship) continue;
            var g = MermaidParser.Parse(block);
            catalog.Warnings.AddRange(g.Warnings);
            foreach (var n in g.Nodes)
            {
                var t = schema.Table(MermaidSource.Name(n.Label.Length > 0 ? n.Label : n.Id).Replace(' ', '_'));
                foreach (var member in n.Members)
                {
                    var m = Attribute().Match(member.Trim());
                    if (!m.Success) { catalog.Warnings.Add($"{t.Name}: could not read attribute “{member}”"); continue; }
                    var keys = m.Groups["keys"].Value.ToUpperInvariant();
                    var col = new DbColumnDef { Name = m.Groups["name"].Value, Type = m.Groups["type"].Value, Comment = m.Groups["comment"].Success ? m.Groups["comment"].Value : null };
                    col.Unique = keys.Contains("UK");
                    if (keys.Contains("PK")) { t.PrimaryKey.Add(col.Name); col.Nullable = false; }
                    t.Columns.Add(col);
                }
            }
            foreach (var e in g.Edges)
            {
                var a = schema.FindTable(MermaidSource.Name(g.ById.TryGetValue(e.From, out var na) ? na.Label : e.From).Replace(' ', '_'));
                var b = schema.FindTable(MermaidSource.Name(g.ById.TryGetValue(e.To, out var nb) ? nb.Label : e.To).Replace(' ', '_'));
                if (a == null || b == null) continue;
                // The child is the "many" side; with 1:1 the right-hand entity references the left-hand one.
                var (parent, child, parentCard) = Many(e.FromCard) && !Many(e.ToCard) ? (b, a, e.ToCard) : (a, b, e.FromCard);
                var pk = parent.PrimaryKey.Count > 0 ? parent.PrimaryKey : parent.Columns.Take(1).Select(c => c.Name).ToList();
                if (pk.Count == 0) { parent.Columns.Insert(0, new DbColumnDef { Name = "id", Type = "int", Nullable = false }); parent.PrimaryKey.Add("id"); pk = ["id"]; }
                var fk = new DbForeignKeyDef { Name = $"fk_{child.Name}_{parent.Name}".ToLowerInvariant(), RefTable = parent.Name };
                foreach (var key in pk)
                {
                    var refType = parent.FindColumn(key)?.Type ?? "int";
                    var stem = DbModel.ToSnake(parent.Name);
                    var col = child.FindColumn($"{stem}_{key}") ?? child.FindColumn(key.Equals("id", StringComparison.OrdinalIgnoreCase) ? $"{stem}_id" : key)
                              ?? child.FindColumn($"{parent.Name}Id") ?? child.FindColumn($"{parent.Name}_id");
                    if (col == null || ReferenceEquals(child, parent) && child.IsPrimaryKey(col.Name))
                    {
                        col = new DbColumnDef { Name = key.Equals("id", StringComparison.OrdinalIgnoreCase) ? $"{stem}_id" : $"{stem}_{key}", Type = refType };
                        child.Columns.Add(col);
                    }
                    col.Nullable = parentCard is "0..1" or "0..*";
                    fk.Columns.Add(col.Name);
                    fk.RefColumns.Add(key);
                }
                if (!child.ForeignKeys.Any(x => x.Signature == fk.Signature)) child.ForeignKeys.Add(fk);
            }
        }
        catalog.Warnings.AddRange(warnings);
        return catalog;
    }

    private static bool Many(string card) => card is "0..*" or "1..*";
}
