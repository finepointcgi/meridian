using Meridian.Core.Model;

namespace Meridian.Core.DataModeling;

/// <summary>Options for reading a catalog out of the model.</summary>
public sealed class DbExtractOptions
{
    /// <summary>Use the names the source database had (from <see cref="DbFields.QualifiedName"/>) rather than the model's: for refresh comparisons.</summary>
    public bool SourceNames { get; init; }

    /// <summary>Include sequences, enum / domain types and routines of the schemas in scope.</summary>
    public bool IncludeOtherObjects { get; init; } = true;
}

/// <summary>
/// Reads the model's data model into a <see cref="DbCatalog"/> (for DDL, migration scripts and refresh comparisons).
/// <see cref="Index"/> maps each object's path (see <see cref="DbPaths"/>) back to the element it came from.
/// </summary>
public sealed class DbExtract
{
    public DbCatalog Catalog { get; } = new();
    public Dictionary<string, Element> Index { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The data model under <paramref name="scope"/>: a database, a schema, a subject area (its tables), a table, any package
    /// or the model root. <paramref name="tables"/> narrows it to a selection.
    /// </summary>
    public static DbExtract FromModel(ModelRepository m, Element scope, IEnumerable<Element>? tables = null, DbExtractOptions? options = null)
    {
        options ??= new DbExtractOptions();
        var x = new DbExtract();
        var list = (tables ?? DbModel.TablesIn(m, scope)).Where(t => t.Kind == ElementKind.Table).Distinct().ToList();
        var db = DbModel.DatabaseOf(scope) ?? list.Select(DbModel.DatabaseOf).FirstOrDefault(d => d != null) ?? (DbModel.IsGroup(scope) ? null : scope.Descendants().FirstOrDefault(DbModel.IsDatabase));
        x.Catalog.Name = db?.Name ?? scope.Name;
        x.Catalog.Dialect = db?.Get(DbFields.Dialect) ?? "";
        string? CommentOf(Element e) => options.SourceNames ? Null(e.Get(DbFields.Comment)) : Null(e.Documentation);
        string NameOf(Element e) => options.SourceNames && e.Get(DbFields.QualifiedName) is { Length: > 0 } q ? LastSegment(q) : e.Name;
        string SchemaName(Element e) => DbModel.SchemaOf(e) is { } s ? NameOf(s) : "";

        // Schemas in scope appear even when empty (a new schema is a migration step too).
        if (tables == null) foreach (var s in SchemasIn(scope)) Register(x, x.Catalog.Schema(NameOf(s)), s, DbPaths.Schema(NameOf(s)));

        foreach (var t in list)
        {
            var schemaName = SchemaName(t);
            var schema = x.Catalog.Schema(schemaName);
            if (DbModel.SchemaOf(t) is { } sp) Register(x, schema, sp, DbPaths.Schema(schemaName));
            var def = new DbTableDef { Name = NameOf(t), Kind = t.Get(FieldKeys.Subtype), Comment = CommentOf(t), Definition = Null(t.Get(DbFields.Definition)), PrimaryKeyName = Null(t.Get(DbFields.PkName)) };
            schema.Tables.Add(def);
            var tp = DbPaths.Table(schemaName, def.Name);
            x.Index[tp] = t;
            foreach (var c in DbModel.Columns(t))
            {
                var cd = new DbColumnDef
                {
                    Name = NameOf(c), Type = c.Get(FieldKeys.SqlType),
                    Nullable = DbModel.IsNullable(c), Default = Null(c.Get(FieldKeys.DefaultValue)), Identity = Null(c.Get(DbFields.Identity)),
                    Generated = Null(c.Get(DbFields.Generated)), Collation = Null(c.Get(DbFields.Collation)), Comment = CommentOf(c),
                    Unique = DbModel.IsUnique(c) && !DbModel.IsPk(c),
                };
                def.Columns.Add(cd);
                if (DbModel.IsPk(c)) def.PrimaryKey.Add(cd.Name);
                x.Index[DbPaths.Column(tp, cd.Name)] = c;
            }
            foreach (var fk in m.Outgoing(t, ElementKind.ForeignKey).Cast<Relationship>())
            {
                var fd = new DbForeignKeyDef
                {
                    Name = Null(fk.Name), RefTable = NameOf(fk.Target), RefSchema = DbModel.SchemaOf(fk.Target) is { } rs ? NameOf(rs) : null,
                    OnDelete = Null(fk.Get(DbFields.OnDelete)), OnUpdate = Null(fk.Get(DbFields.OnUpdate)),
                };
                fd.Columns.AddRange(DbModel.SplitList(fk.Get(FieldKeys.FkColumns)).Select(n => ColumnName(t, n, NameOf)));
                fd.RefColumns.AddRange(DbModel.RefColumns(fk).Select(NameOf));
                if (fd.Columns.Count == 0) fd.Columns.Add($"{DbModel.ToSnake(fk.Target.Name)}_id");
                def.ForeignKeys.Add(fd);
                x.Index[DbPaths.ForeignKey(tp, fd)] = fk;
            }
            foreach (var ix in DbModel.Indexes(t))
            {
                var id = new DbIndexDef { Name = NameOf(ix), Unique = ix.Get(FieldKeys.Unique) == "true", Method = Null(ix.Get(DbFields.Method)), Where = Null(ix.Get(DbFields.Expression)) };
                id.Columns.AddRange(DbModel.SplitList(ix.Get(DbFields.Columns)).Select(n => ColumnName(t, n, NameOf)));
                def.Indexes.Add(id);
                x.Index[DbPaths.Index(tp, id.Name)] = ix;
            }
            foreach (var ck in DbModel.Constraints(t))
            {
                var cd = new DbConstraintDef { Name = NameOf(ck), Type = ck.Get(FieldKeys.Subtype) == DbFields.CheckConstraint ? DbFields.CheckConstraint : DbFields.UniqueConstraint, Expression = Null(ck.Get(DbFields.Expression)) };
                cd.Columns.AddRange(DbModel.SplitList(ck.Get(DbFields.Columns)).Select(n => ColumnName(t, n, NameOf)));
                def.Constraints.Add(cd);
                x.Index[DbPaths.Constraint(tp, cd.Name)] = ck;
            }
            foreach (var tg in DbModel.Triggers(t))
            {
                var td = new DbTriggerDef { Name = NameOf(tg), Timing = Or(tg.Get(DbFields.Timing), "AFTER"), Events = Or(tg.Get(DbFields.Events), "INSERT"), Level = Null(tg.Get(DbFields.Level)), Action = Null(tg.Get(DbFields.Definition)) };
                def.Triggers.Add(td);
                x.Index[DbPaths.Trigger(tp, td.Name)] = tg;
            }
        }

        if (options.IncludeOtherObjects && tables == null && !DbModel.IsGroup(scope))
        {
            foreach (var s in SchemasIn(scope).DefaultIfEmpty(scope.Kind is ElementKind.Package or ElementKind.Model && !DbModel.IsDatabase(scope) ? scope : null).OfType<Element>())
            {
                var schemaName = DbModel.IsSchema(s) ? NameOf(s) : "";
                var schema = x.Catalog.Schema(schemaName);
                var owned = DbModel.IsSchema(s) ? s.OwnedElements : s.OwnedElements.Where(e => e.Kind is ElementKind.Sequence or ElementKind.Routine || e.Kind is ElementKind.Enumeration or ElementKind.DataType && e.Get(DbFields.QualifiedName).Length > 0);
                foreach (var e in owned)
                {
                    switch (e.Kind)
                    {
                        case ElementKind.Sequence:
                        {
                            var sd = new DbSequenceDef { Name = NameOf(e), Type = Null(e.Get(FieldKeys.SqlType)), Start = Null(e.Get(DbFields.Start)), Increment = Null(e.Get(DbFields.Increment)), Min = Null(e.Get(DbFields.MinValue)), Max = Null(e.Get(DbFields.MaxValue)), Cycle = e.Get(DbFields.Cycle) == "true" };
                            schema.Sequences.Add(sd);
                            x.Index[DbPaths.Sequence(schemaName, sd.Name)] = e;
                            break;
                        }
                        case ElementKind.Enumeration:
                        {
                            var td = new DbTypeDef { Name = NameOf(e), Kind = "enum", Comment = CommentOf(e) };
                            td.Values.AddRange(e.OwnedElements.Where(l => l.Kind == ElementKind.EnumerationLiteral).Select(l => l.Name));
                            schema.Types.Add(td);
                            x.Index[DbPaths.Type(schemaName, td.Name)] = e;
                            break;
                        }
                        case ElementKind.DataType when e.Get(DbFields.BaseType).Length > 0:
                        {
                            var td = new DbTypeDef { Name = NameOf(e), Kind = "domain", BaseType = e.Get(DbFields.BaseType), Check = Null(e.Get(DbFields.Expression)), NotNull = e.Get(FieldKeys.Nullable) == "false", Comment = CommentOf(e) };
                            schema.Types.Add(td);
                            x.Index[DbPaths.Type(schemaName, td.Name)] = e;
                            break;
                        }
                        case ElementKind.Routine:
                        {
                            var rd = new DbRoutineDef { Name = NameOf(e), Kind = e.Get(FieldKeys.Subtype) == DbFields.Procedure ? DbFields.Procedure : DbFields.Function, Signature = Or(e.Get(FieldKeys.Signature), "()"), Returns = Null(e.Get(DbFields.Returns)), Language = Null(e.Get(DbFields.Language)), Comment = CommentOf(e) };
                            schema.Routines.Add(rd);
                            x.Index[DbPaths.Routine(schemaName, rd.Name)] = e;
                            break;
                        }
                    }
                }
            }
        }
        // Drop the empty default schema a plain package produces when it has no tables.
        x.Catalog.Schemas.RemoveAll(s => s.Name.Length == 0 && s.Tables.Count == 0 && s.Sequences.Count == 0 && s.Types.Count == 0 && s.Routines.Count == 0);
        return x;
    }

    private static void Register(DbExtract x, DbSchemaDef s, Element e, string path)
    {
        x.Index.TryAdd(path, e);
        if (s.Comment == null && e.Documentation.Length > 0) s.Comment = e.Documentation;
    }

    private static IEnumerable<Element> SchemasIn(Element scope) =>
        DbModel.IsSchema(scope) ? [scope] : DbModel.IsGroup(scope) || scope.Kind == ElementKind.Table ? [] : scope.Descendants().Where(DbModel.IsSchema);

    private static string ColumnName(Element table, string name, Func<Element, string> nameOf) => DbModel.Column(table, name) is { } c ? nameOf(c) : name;

    public static string LastSegment(string qualified)
    {
        var i = qualified.LastIndexOf('.');
        return i >= 0 ? qualified[(i + 1)..] : qualified;
    }

    private static string? Null(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static string Or(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;
}

/// <summary>Paths that identify catalog objects across two catalogs (case-insensitive): "schema/table", "schema/table/column" …</summary>
public static class DbPaths
{
    public static string Schema(string schema) => $"schema:{schema}";
    public static string Table(string schema, string table) => $"{schema}/{table}";
    public static string Column(string tablePath, string column) => $"{tablePath}/{column}";
    public static string PrimaryKey(string tablePath) => $"{tablePath}#pk";
    public static string ForeignKey(string tablePath, DbForeignKeyDef fk) => $"{tablePath}#fk:{(string.IsNullOrEmpty(fk.Name) ? fk.Signature : fk.Name)}";
    public static string Index(string tablePath, string name) => $"{tablePath}#ix:{name}";
    public static string Constraint(string tablePath, string name) => $"{tablePath}#ck:{name}";
    public static string Trigger(string tablePath, string name) => $"{tablePath}#tg:{name}";
    public static string Sequence(string schema, string name) => $"{schema}#seq:{name}";
    public static string Type(string schema, string name) => $"{schema}#type:{name}";
    public static string Routine(string schema, string name) => $"{schema}#fn:{name}";
}
