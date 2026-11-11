using Meridian.Core.DataModeling;
using Meridian.Serialization.Database;
using Microsoft.Data.SqlClient;

namespace Meridian.Data.Databases;

/// <summary>
/// SQL Server and Azure SQL through the sys.* catalog views of the connection's database: schemas, tables, views, columns
/// (types, identity, computed, defaults with their constraint names, collations), primary / unique / check / foreign keys,
/// indexes, triggers, sequences, alias types, procedures and functions, and MS_Description comments. Module text (views,
/// routines, triggers) comes from sys.sql_modules and is read by the DDL parser. SQL Server has no read-only transaction:
/// only SELECTs run, inside a transaction that is rolled back, with a lock timeout so nothing waits on other sessions.
/// </summary>
internal sealed class SqlServerSchema(SqlConnection c, int timeout)
{
    private static readonly string[] SystemSchemas = ["sys", "INFORMATION_SCHEMA", "guest"];

    public async Task<DbCatalog> ReadAsync(IReadOnlyCollection<string>? only, CancellationToken ct)
    {
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        Task<List<object?[]>> Q(string sql) => LiveSchemaReader.Rows(c, tx, sql, timeout, ct);
        await using (var lockTimeout = new SqlCommand($"SET LOCK_TIMEOUT {Math.Min(timeout, 600) * 1000}", c, tx)) await lockTimeout.ExecuteNonQueryAsync(ct);

        var dbRow = (await Q("select db_name(), cast(databasepropertyex(db_name(), 'Collation') as nvarchar(128))")).Single();
        var catalog = new DbCatalog { Name = S(dbRow[0]) ?? "database", Dialect = "sqlserver" };
        var dbCollation = S(dbRow[1]);

        // Schemas: user schemas (not the fixed database-role schemas), the ones asked for or all.
        var schemaRows = await Q("select s.name, cast(ep.value as nvarchar(max)) from sys.schemas s " +
                                 "left join sys.extended_properties ep on ep.class = 3 and ep.major_id = s.schema_id and ep.minor_id = 0 and ep.name = N'MS_Description' " +
                                 "where s.schema_id < 16384 order by s.name");
        var schemas = schemaRows.Select(r => (Name: (string)r[0]!, Comment: S(r[1])))
            .Where(s => !SystemSchemas.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
            .Where(s => only is not { Count: > 0 } || only.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (schemas.Count == 0) return catalog;
        foreach (var s in schemas) catalog.Schema(s.Name).Comment = s.Comment;
        var inSchemas = $"s.name in ({string.Join(", ", schemas.Select(s => "N" + DdlWriter.Literal(s.Name)))})";
        const string Desc = "cast(ep.value as nvarchar(max))";
        string Ep(string major, string minor = "0") => $"left join sys.extended_properties ep on ep.class = 1 and ep.major_id = {major} and ep.minor_id = {minor} and ep.name = N'MS_Description'";

        // Tables and views.
        var tables = new Dictionary<int, (string Schema, DbTableDef Table)>();
        foreach (var r in await Q($"select o.object_id, s.name, o.name, o.type, {Desc}, m.definition from sys.objects o join sys.schemas s on s.schema_id = o.schema_id " +
                                  $"{Ep("o.object_id")} left join sys.sql_modules m on m.object_id = o.object_id " +
                                  $"where o.type in ('U', 'V') and o.is_ms_shipped = 0 and {inSchemas} order by s.name, o.name"))
        {
            var t = catalog.Schema((string)r[1]!).Table((string)r[2]!);
            var view = ((string)r[3]!).Trim() == "V";
            t.Kind = view ? DbFields.View : "";
            t.Comment = S(r[4]);
            if (view && S(r[5]) is { } def) t.Definition = ViewQuery(def);
            tables[Convert.ToInt32(r[0])] = ((string)r[1]!, t);
        }
        var inTables = $"o.type in ('U', 'V') and o.is_ms_shipped = 0 and {inSchemas}";
        var objects = $"sys.objects o join sys.schemas s on s.schema_id = o.schema_id";

        // Columns.
        var columns = new Dictionary<(int, int), string>();
        foreach (var r in await Q($"select c.object_id, c.column_id, c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, cc.definition, " +
                                  $"dc.definition, dc.name, c.collation_name, {Desc}, t.is_user_defined, ts.name " +
                                  $"from sys.columns c join {objects} on o.object_id = c.object_id join sys.types t on t.user_type_id = c.user_type_id join sys.schemas ts on ts.schema_id = t.schema_id " +
                                  $"left join sys.computed_columns cc on cc.object_id = c.object_id and cc.column_id = c.column_id " +
                                  $"left join sys.default_constraints dc on dc.parent_object_id = c.object_id and dc.parent_column_id = c.column_id " +
                                  $"{Ep("c.object_id", "c.column_id")} where {inTables} order by c.object_id, c.column_id"))
        {
            if (!tables.TryGetValue(Convert.ToInt32(r[0]), out var owner)) continue;
            var computed = S(r[9]);
            var userType = r[14] is true;
            var type = computed != null ? "computed" : userType ? (S(r[15]) is { } ts && ts != "dbo" ? $"{ts}.{r[3]}" : (string)r[3]!) : TypeName((string)r[3]!, Convert.ToInt32(r[4]), Convert.ToInt32(r[5]), Convert.ToInt32(r[6]));
            var collation = S(r[12]);
            owner.Table.Columns.Add(new DbColumnDef
            {
                Name = (string)r[2]!, Type = type, Nullable = r[7] is true, Identity = r[8] is true ? "always" : null, Generated = computed,
                Default = S(r[10]), DefaultName = S(r[10]) != null ? S(r[11]) : null,
                Collation = collation != null && !string.Equals(collation, dbCollation, StringComparison.OrdinalIgnoreCase) ? collation : null,
                Comment = S(r[13]),
            });
            columns[(Convert.ToInt32(r[0]), Convert.ToInt32(r[1]))] = (string)r[2]!;
        }

        // Primary keys and unique constraints (with their index columns).
        foreach (var g in (await Q($"select k.parent_object_id, k.name, k.type, ic.column_id from sys.key_constraints k join {objects} on o.object_id = k.parent_object_id " +
                                   $"join sys.index_columns ic on ic.object_id = k.parent_object_id and ic.index_id = k.unique_index_id " +
                                   $"where {inTables} and ic.is_included_column = 0 order by k.parent_object_id, k.name, ic.key_ordinal"))
                     .GroupBy(r => (Table: Convert.ToInt32(r[0]), Name: (string)r[1]!, Type: ((string)r[2]!).Trim())))
        {
            if (!tables.TryGetValue(g.Key.Table, out var owner)) continue;
            var cols = g.Select(r => columns.GetValueOrDefault((g.Key.Table, Convert.ToInt32(r[3]))) ?? "?").ToList();
            if (g.Key.Type == "PK")
            {
                owner.Table.PrimaryKey.Clear();
                owner.Table.PrimaryKey.AddRange(cols);
                owner.Table.PrimaryKeyName = g.Key.Name;
            }
            else
            {
                var uq = new DbConstraintDef { Name = g.Key.Name, Type = DbFields.UniqueConstraint };
                uq.Columns.AddRange(cols);
                owner.Table.Constraints.Add(uq);
            }
        }

        // Check constraints.
        foreach (var r in await Q($"select k.parent_object_id, k.name, k.definition from sys.check_constraints k join {objects} on o.object_id = k.parent_object_id where {inTables} order by k.name"))
            if (tables.TryGetValue(Convert.ToInt32(r[0]), out var owner))
                owner.Table.Constraints.Add(new DbConstraintDef { Name = (string)r[1]!, Type = DbFields.CheckConstraint, Expression = Unwrap(S(r[2])) });

        // Foreign keys (the referenced table may sit in a schema that is not imported).
        foreach (var g in (await Q($"select f.parent_object_id, f.name, rs.name, ro.name, f.delete_referential_action_desc, f.update_referential_action_desc, " +
                                   $"pc.name, rc.name from sys.foreign_keys f join {objects} on o.object_id = f.parent_object_id " +
                                   $"join sys.objects ro on ro.object_id = f.referenced_object_id join sys.schemas rs on rs.schema_id = ro.schema_id " +
                                   $"join sys.foreign_key_columns k on k.constraint_object_id = f.object_id " +
                                   $"join sys.columns pc on pc.object_id = k.parent_object_id and pc.column_id = k.parent_column_id " +
                                   $"join sys.columns rc on rc.object_id = k.referenced_object_id and rc.column_id = k.referenced_column_id " +
                                   $"where {inTables} order by f.parent_object_id, f.name, k.constraint_column_id"))
                     .GroupBy(r => (Table: Convert.ToInt32(r[0]), Name: (string)r[1]!)))
        {
            if (!tables.TryGetValue(g.Key.Table, out var owner)) continue;
            var first = g.First();
            var fk = new DbForeignKeyDef
            {
                Name = g.Key.Name, RefSchema = S(first[2]) is { } rs && !DbCatalog.Eq(rs, owner.Schema) ? rs : null, RefTable = S(first[3]) ?? "",
                OnDelete = Action(S(first[4])), OnUpdate = Action(S(first[5])),
            };
            fk.Columns.AddRange(g.Select(r => (string)r[6]!));
            fk.RefColumns.AddRange(g.Select(r => (string)r[7]!));
            owner.Table.ForeignKeys.Add(fk);
        }

        // Indexes that do not back a key constraint.
        foreach (var g in (await Q($"select i.object_id, i.name, i.is_unique, i.type_desc, i.filter_definition, c.name from sys.indexes i join {objects} on o.object_id = i.object_id " +
                                   $"join sys.index_columns ic on ic.object_id = i.object_id and ic.index_id = i.index_id join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id " +
                                   $"where {inTables} and i.is_primary_key = 0 and i.is_unique_constraint = 0 and i.is_hypothetical = 0 and i.type > 0 and ic.is_included_column = 0 " +
                                   $"order by i.object_id, i.name, ic.key_ordinal"))
                     .GroupBy(r => (Table: Convert.ToInt32(r[0]), Name: (string)r[1]!)))
        {
            if (!tables.TryGetValue(g.Key.Table, out var owner)) continue;
            var first = g.First();
            var method = S(first[3])?.ToLowerInvariant() switch { "clustered" => "clustered", "clustered columnstore" or "nonclustered columnstore" => "columnstore", "xml" => "xml", "spatial" => "spatial", _ => null };
            var ix = new DbIndexDef { Name = g.Key.Name, Unique = first[2] is true, Method = method, Where = Unwrap(S(first[4])) };
            ix.Columns.AddRange(g.Select(r => (string)r[5]!));
            owner.Table.Indexes.Add(ix);
        }

        // Triggers: the definition read by the DDL parser.
        foreach (var r in await Q($"select tr.parent_id, tr.name, m.definition from sys.triggers tr join {objects} on o.object_id = tr.parent_id " +
                                  $"left join sys.sql_modules m on m.object_id = tr.object_id where tr.parent_class = 1 and {inTables} order by tr.name"))
        {
            if (!tables.TryGetValue(Convert.ToInt32(r[0]), out var owner)) continue;
            var tg = S(r[2]) is { } def ? DdlParser.ParseModule(def, "sqlserver").Trigger : null;
            tg ??= new DbTriggerDef { Action = null };
            tg.Name = (string)r[1]!;
            owner.Table.Triggers.Add(tg);
        }

        // Sequences.
        foreach (var r in await Q($"select s.name, q.name, t.name, cast(q.start_value as nvarchar(40)), cast(q.increment as nvarchar(40)), cast(q.minimum_value as nvarchar(40)), " +
                                  $"cast(q.maximum_value as nvarchar(40)), q.is_cycling, q.precision from sys.sequences q join sys.schemas s on s.schema_id = q.schema_id " +
                                  $"join sys.types t on t.user_type_id = q.user_type_id where {inSchemas} order by s.name, q.name"))
        {
            var type = (string)r[2]!;
            catalog.Schema((string)r[0]!).Sequences.Add(new DbSequenceDef
            {
                Name = (string)r[1]!, Type = type == "bigint" ? null : type, Start = S(r[3]) == "1" ? null : S(r[3]), Increment = S(r[4]) == "1" ? null : S(r[4]),
                Min = DefaultBound(type, S(r[5]), min: true) ? null : S(r[5]), Max = DefaultBound(type, S(r[6]), min: false) ? null : S(r[6]), Cycle = r[7] is true,
            });
        }

        // Alias types (CREATE TYPE x FROM base): domains.
        foreach (var r in await Q($"select s.name, t.name, bt.name, t.max_length, t.precision, t.scale, t.is_nullable from sys.types t join sys.schemas s on s.schema_id = t.schema_id " +
                                  $"join sys.types bt on bt.user_type_id = t.system_type_id where t.is_user_defined = 1 and t.is_table_type = 0 and {inSchemas} order by s.name, t.name"))
            catalog.Schema((string)r[0]!).Types.Add(new DbTypeDef
            {
                Name = (string)r[1]!, Kind = "domain", BaseType = TypeName((string)r[2]!, Convert.ToInt32(r[3]), Convert.ToInt32(r[4]), Convert.ToInt32(r[5])), NotNull = r[6] is false,
            });

        // Procedures and functions: the module text read by the DDL parser; parameters from sys.parameters when it can't be read.
        var parameters = (await Q($"select p.object_id, p.name, t.name, p.max_length, p.precision, p.scale, p.is_output from sys.parameters p join {objects} on o.object_id = p.object_id " +
                                  $"join sys.types t on t.user_type_id = p.user_type_id where o.type in ('P', 'FN', 'IF', 'TF') and {inSchemas} and p.parameter_id > 0 order by p.object_id, p.parameter_id"))
            .GroupBy(r => Convert.ToInt32(r[0])).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(r => $"{r[1]} {TypeName((string)r[2]!, Convert.ToInt32(r[3]), Convert.ToInt32(r[4]), Convert.ToInt32(r[5]))}{(r[6] is true ? " OUTPUT" : "")}")));
        foreach (var r in await Q($"select o.object_id, s.name, o.name, o.type, m.definition, {Desc} from {objects} left join sys.sql_modules m on m.object_id = o.object_id " +
                                  $"{Ep("o.object_id")} where o.type in ('P', 'FN', 'IF', 'TF') and o.is_ms_shipped = 0 and {inSchemas} order by s.name, o.name"))
        {
            var kind = ((string)r[3]!).Trim() == "P" ? DbFields.Procedure : DbFields.Function;
            var parsed = S(r[4]) is { } def ? DdlParser.ParseModule(def, "sqlserver").Routine : null;
            var rd = parsed ?? new DbRoutineDef { Signature = $"({parameters.GetValueOrDefault(Convert.ToInt32(r[0])) ?? ""})" };
            rd.Name = (string)r[2]!;
            rd.Kind = kind;
            rd.Comment = S(r[5]);
            catalog.Schema((string)r[1]!).Routines.Add(rd);
        }
        if (catalog.Schemas.Any(s => s.Routines.Any(r => r.Body == null)))
            catalog.Warnings.Add("Some routine definitions could not be read (encrypted, or VIEW DEFINITION is not granted): their signatures were imported without bodies.");
        return catalog;
    }

    /// <summary>The SELECT of a CREATE VIEW statement.</summary>
    private static string? ViewQuery(string definition)
    {
        var parsed = DdlParser.Parse(definition.TrimEnd() + "\nGO\n", "sqlserver");
        return parsed.AllTables.FirstOrDefault(t => t.IsView)?.Definition?.Trim().TrimEnd(';');
    }

    /// <summary>A type as written in DDL from sys.types / sys.columns sizes (max_length is in bytes; -1 is max).</summary>
    internal static string TypeName(string type, int maxLength, int precision, int scale)
    {
        var t = type.ToLowerInvariant();
        return t switch
        {
            "varchar" or "char" or "varbinary" or "binary" => $"{t}({(maxLength < 0 ? "max" : maxLength.ToString())})",
            "nvarchar" or "nchar" => $"{t}({(maxLength < 0 ? "max" : (maxLength / 2).ToString())})",
            "decimal" or "numeric" => $"{t}({precision},{scale})",
            "datetime2" or "time" or "datetimeoffset" => scale == 7 ? t : $"{t}({scale})",
            "float" => precision == 53 ? t : $"{t}({precision})",
            _ => t,
        };
    }

    /// <summary>Check and filter definitions come wrapped in parentheses: "([amount]>(0))" → "[amount]>(0)".</summary>
    private static string? Unwrap(string? s)
    {
        if (s == null) return null;
        s = s.Trim();
        while (s.Length > 1 && s[0] == '(' && s[^1] == ')' && Balanced(s[1..^1])) s = s[1..^1].Trim();
        return s;
    }

    private static bool Balanced(string s)
    {
        var d = 0;
        foreach (var ch in s) { if (ch == '(') d++; else if (ch == ')' && --d < 0) return false; }
        return d == 0;
    }

    private static bool DefaultBound(string type, string? value, bool min) => (type.ToLowerInvariant(), min) switch
    {
        ("bigint", true) => value == long.MinValue.ToString(),
        ("bigint", false) => value == long.MaxValue.ToString(),
        ("int", true) => value == int.MinValue.ToString(),
        ("int", false) => value == int.MaxValue.ToString(),
        ("smallint", true) => value == short.MinValue.ToString(),
        ("smallint", false) => value == short.MaxValue.ToString(),
        ("tinyint", true) => value == "0",
        ("tinyint", false) => value == "255",
        _ => false,
    };

    private static string? Action(string? desc) => desc switch { "CASCADE" => "CASCADE", "SET_NULL" => "SET NULL", "SET_DEFAULT" => "SET DEFAULT", _ => null };

    private static string? S(object? o) => LiveSchemaReader.S(o);
}
