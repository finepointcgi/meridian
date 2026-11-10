using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Meridian.Core.DataModeling;

namespace Meridian.Serialization.Database;

/// <summary>
/// SQL Server data-tier application packages (.dacpac): a zip whose model.xml lists schemas, tables, columns, keys,
/// indexes, views, sequences, routines, triggers and MS_Description comments. Read with <see cref="SafeZip"/> limits and a
/// depth-limited XML reader without DTDs.
/// </summary>
public static partial class DacpacReader
{
    public const long MaxModelBytes = 64L * 1024 * 1024;

    public static DbCatalog Read(Stream dacpac, string name = "database")
    {
        using var zip = new ZipArchive(dacpac, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("model.xml", StringComparison.OrdinalIgnoreCase))
                    ?? throw new FormatException("This is not a .dacpac: it has no model.xml.");
        using var s = SafeZip.Open(entry, MaxModelBytes);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
        using var reader = DepthLimitedXmlReader.Create(s, settings, 256);
        return FromModelXml(XDocument.Load(reader), name);
    }

    [GeneratedRegex(@"\[((?:[^\]]|\]\])*)\]")]
    private static partial Regex Part();

    /// <summary>"[dbo].[Orders].[Id]" → ["dbo", "Orders", "Id"].</summary>
    public static List<string> Parts(string? name) =>
        name == null ? [] : Part().Matches(name).Select(m => m.Groups[1].Value.Replace("]]", "]")).ToList();

    public static DbCatalog FromModelXml(XDocument doc, string name = "database")
    {
        var c = new DbCatalog { Name = name, Dialect = "sqlserver" };
        var elements = doc.Descendants().Where(e => e.Name.LocalName == "Element" && e.Parent?.Name.LocalName == "Model").ToList();
        string Type(XElement e) => (string?)e.Attribute("Type") ?? "";
        DbTableDef? TableOf(XElement e)
        {
            var p = Parts(Ref(e, "DefiningTable") ?? Ref(e, "IndexedObject") ?? Ref(e, "TriggerObject"));
            return p.Count >= 2 ? c.Schema(p[0]).FindTable(p[1]) : null;
        }

        foreach (var e in elements.Where(e => Type(e) == "SqlSchema"))
            if (Parts((string?)e.Attribute("Name")) is [var s]) c.Schema(s);

        foreach (var e in elements.Where(e => Type(e) is "SqlTable" or "SqlView"))
        {
            var p = Parts((string?)e.Attribute("Name"));
            if (p.Count < 2) continue;
            var t = c.Schema(p[0]).Table(p[1]);
            if (Type(e) == "SqlView") { t.Kind = DbFields.View; t.Definition = Script(e, "QueryScript"); }
            foreach (var col in Rel(e, "Columns").Select(x => x.Element(x.Name.Namespace + "Element") ?? x.Elements().FirstOrDefault(y => y.Name.LocalName == "Element")).OfType<XElement>())
            {
                var cp = Parts((string?)col.Attribute("Name"));
                if (cp.Count == 0) continue;
                var cd = new DbColumnDef { Name = cp[^1], Nullable = Prop(col, "IsNullable") != "False" };
                if (Type(col) == "SqlComputedColumn") { cd.Generated = Script(col, "ExpressionScript"); cd.Type = t.IsView ? "" : "computed"; }
                else cd.Type = TypeOf(col);
                if (Prop(col, "IsIdentity") == "True") { cd.Identity = "always"; cd.Nullable = false; }
                if (Ref(col, "Collation") is { } coll) cd.Collation = coll;
                else if (Prop(col, "Collation") is { } coll2) cd.Collation = coll2;
                t.Columns.Add(cd);
            }
        }

        foreach (var e in elements)
        {
            var type = Type(e);
            var p = Parts((string?)e.Attribute("Name"));
            switch (type)
            {
                case "SqlPrimaryKeyConstraint" when TableOf(e) is { } t:
                    t.PrimaryKey.Clear();
                    t.PrimaryKey.AddRange(ColumnSpecs(e));
                    if (p.Count >= 2) t.PrimaryKeyName = p[^1];
                    foreach (var k in t.PrimaryKey) if (t.FindColumn(k) is { } col) col.Nullable = false;
                    break;
                case "SqlUniqueConstraint" when TableOf(e) is { } t:
                {
                    var u = new DbConstraintDef { Name = p.Count > 0 ? p[^1] : $"uq_{t.Name}", Type = DbFields.UniqueConstraint };
                    u.Columns.AddRange(ColumnSpecs(e));
                    t.Constraints.Add(u);
                    break;
                }
                case "SqlCheckConstraint" when TableOf(e) is { } t:
                    t.Constraints.Add(new DbConstraintDef { Name = p.Count > 0 ? p[^1] : $"ck_{t.Name}", Type = DbFields.CheckConstraint, Expression = Unwrap(Script(e, "CheckExpressionScript")) });
                    break;
                case "SqlDefaultConstraint" when TableOf(e) is { } t:
                    if (Parts(Ref(e, "ForColumn")) is { Count: > 0 } fc && t.FindColumn(fc[^1]) is { } dcol) dcol.Default = Unwrap(Script(e, "DefaultExpressionScript"));
                    break;
                case "SqlForeignKeyConstraint" when TableOf(e) is { } t:
                {
                    var target = Parts(Ref(e, "ForeignTable"));
                    if (target.Count < 2) break;
                    var fk = new DbForeignKeyDef { Name = p.Count > 0 ? p[^1] : null, RefSchema = target[0], RefTable = target[1], OnDelete = Action(Prop(e, "DeleteAction")), OnUpdate = Action(Prop(e, "UpdateAction")) };
                    fk.Columns.AddRange(Refs(e, "Columns").Select(r => Parts(r)[^1]));
                    fk.RefColumns.AddRange(Refs(e, "ForeignColumns").Select(r => Parts(r)[^1]));
                    t.ForeignKeys.Add(fk);
                    break;
                }
                case "SqlIndex" when TableOf(e) is { } t:
                {
                    var ix = new DbIndexDef { Name = p.Count > 0 ? p[^1] : $"ix_{t.Name}", Unique = Prop(e, "IsUnique") == "True", Method = Prop(e, "IsClustered") == "True" ? "clustered" : null, Where = Unwrap(Script(e, "FilterPredicate")) };
                    ix.Columns.AddRange(ColumnSpecs(e));
                    t.Indexes.Add(ix);
                    break;
                }
                case "SqlSequence" when p.Count >= 2:
                    c.Schema(p[0]).Sequences.Add(new DbSequenceDef
                    {
                        Name = p[1], Start = Unwrap(Script(e, "StartValue") ?? Prop(e, "StartValue")), Increment = Unwrap(Script(e, "IncrementValue") ?? Prop(e, "IncrementValue")),
                        Cycle = Prop(e, "IsCycling") == "True", Type = Parts(RefDeep(e, "TypeSpecifier")).LastOrDefault(),
                    });
                    break;
                case "SqlProcedure" or "SqlScalarFunction" or "SqlTableValuedFunction" or "SqlInlineTableValuedFunction" or "SqlMultiStatementTableValuedFunction" when p.Count >= 2:
                {
                    var pars = Rel(e, "Parameters").Select(x => x.Elements().FirstOrDefault(y => y.Name.LocalName == "Element")).OfType<XElement>()
                        .Select(x => $"{Parts((string?)x.Attribute("Name")).LastOrDefault()} {TypeOf(x)}{(Prop(x, "IsOutput") == "True" ? " OUTPUT" : "")}");
                    c.Schema(p[0]).Routines.Add(new DbRoutineDef
                    {
                        Name = p[1], Kind = type == "SqlProcedure" ? DbFields.Procedure : DbFields.Function, Signature = "(" + string.Join(", ", pars) + ")",
                        Returns = type == "SqlScalarFunction" ? TypeOf(e) : type.Contains("TableValued") ? "TABLE" : null, Language = "tsql",
                    });
                    break;
                }
                case "SqlDmlTrigger" when TableOf(e) is { } t:
                {
                    var events = new List<string>();
                    if (Prop(e, "IsInsertTrigger") == "True") events.Add("INSERT");
                    if (Prop(e, "IsUpdateTrigger") == "True") events.Add("UPDATE");
                    if (Prop(e, "IsDeleteTrigger") == "True") events.Add("DELETE");
                    t.Triggers.Add(new DbTriggerDef { Name = p.Count > 0 ? p[^1] : "trigger", Timing = Prop(e, "SqlTriggerType") == "2" ? "INSTEAD OF" : "AFTER", Events = string.Join(", ", events), Level = "STATEMENT" });
                    break;
                }
            }
        }

        // MS_Description extended properties: [SqlColumn].[dbo].[T].[C].[MS_Description] / [SqlTableBase].[dbo].[T].[MS_Description]
        foreach (var e in elements.Where(e => Type(e) == "SqlExtendedProperty"))
        {
            var p = Parts((string?)e.Attribute("Name"));
            if (p.Count < 3 || !p[^1].Equals("MS_Description", StringComparison.OrdinalIgnoreCase)) continue;
            var value = Unwrap(Script(e, "Value") ?? Prop(e, "Value"))?.Trim('\'');
            if (value != null && value.StartsWith("N'", StringComparison.Ordinal)) value = value[2..];
            var host = p.Skip(1).Take(p.Count - 2).ToList();
            if (host.Count >= 2 && c.FindSchema(host[0])?.FindTable(host[1]) is { } t)
            {
                if (host.Count >= 3) { if (t.FindColumn(host[2]) is { } col) col.Comment = value; }
                else t.Comment = value;
            }
        }
        return c;
    }

    private static IEnumerable<XElement> Rel(XElement e, string name) =>
        e.Elements().Where(x => x.Name.LocalName == "Relationship" && (string?)x.Attribute("Name") == name).SelectMany(x => x.Elements().Where(y => y.Name.LocalName == "Entry"));

    private static IEnumerable<string> Refs(XElement e, string name) =>
        Rel(e, name).SelectMany(x => x.Elements().Where(y => y.Name.LocalName == "References")).Select(x => (string?)x.Attribute("Name")).OfType<string>();

    private static string? Ref(XElement e, string name) => Refs(e, name).FirstOrDefault();

    /// <summary>A reference one element deeper (TypeSpecifier → Type).</summary>
    private static string? RefDeep(XElement e, string name) =>
        Rel(e, name).SelectMany(x => x.Elements().Where(y => y.Name.LocalName == "Element")).Select(x => Ref(x, "Type")).FirstOrDefault(x => x != null);

    private static string? Prop(XElement e, string name) =>
        e.Elements().FirstOrDefault(x => x.Name.LocalName == "Property" && (string?)x.Attribute("Name") == name) is { } p ? (string?)p.Attribute("Value") ?? p.Value : null;

    private static string? Script(XElement e, string name) =>
        e.Elements().FirstOrDefault(x => x.Name.LocalName == "Property" && (string?)x.Attribute("Name") == name) is { } p
            ? (string?)p.Attribute("Value") ?? p.Elements().FirstOrDefault(x => x.Name.LocalName == "Value")?.Value ?? p.Value
            : null;

    /// <summary>The column names of an index / key (ColumnSpecifications → Column).</summary>
    private static IEnumerable<string> ColumnSpecs(XElement e) =>
        Rel(e, "ColumnSpecifications").SelectMany(x => x.Elements().Where(y => y.Name.LocalName == "Element")).Select(x => Ref(x, "Column")).OfType<string>().Select(r => Parts(r)[^1]);

    private static string TypeOf(XElement column)
    {
        var spec = Rel(column, "TypeSpecifier").SelectMany(x => x.Elements().Where(y => y.Name.LocalName == "Element")).FirstOrDefault();
        if (spec == null) return "";
        var baseType = Parts(Ref(spec, "Type")).LastOrDefault() ?? "";
        if (Prop(spec, "IsMax") == "True") return $"{baseType}(max)";
        if (Prop(spec, "Length") is { } len) return $"{baseType}({len})";
        if (Prop(spec, "Precision") is { } prec) return Prop(spec, "Scale") is { } scale ? $"{baseType}({prec},{scale})" : $"{baseType}({prec})";
        return baseType;
    }

    private static string? Unwrap(string? script)
    {
        if (string.IsNullOrWhiteSpace(script)) return null;
        var s = script.Trim();
        while (s.Length > 1 && s[0] == '(' && s[^1] == ')') s = s[1..^1].Trim();
        return s;
    }

    private static string? Action(string? code) => code switch { "1" => "CASCADE", "2" => "SET NULL", "3" => "SET DEFAULT", _ => null };
}
