using Meridian.Core.DataModeling;
using Meridian.Serialization.Database;
using Oracle.ManagedDataAccess.Client;

namespace Meridian.Data.Databases;

/// <summary>
/// Oracle through the ALL_* data dictionary views: the connecting user's schema (or the schemas asked for) with tables,
/// views, materialized views, columns (types, identity, virtual columns, defaults), primary / unique / check / foreign keys,
/// indexes, triggers, sequences, procedures and functions (their source from ALL_SOURCE, read by the DDL parser), and
/// comments. Every query runs inside SET TRANSACTION READ ONLY. Packages and object types are listed as warnings.
/// </summary>
internal sealed class OracleSchema(OracleConnection c, int timeout)
{
    private OracleTransaction? _tx;
    private CancellationToken _ct;

    private async Task<List<object?[]>> Q(string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeout;
        cmd.Transaction = _tx;
        cmd.InitialLONGFetchSize = -1;   // view text, defaults and conditions are LONG columns: fetch them whole
        await using var reg = _ct.Register(() => { try { cmd.Cancel(); } catch (OracleException) { } catch (InvalidOperationException) { } });
        var rows = new List<object?[]>();
        await using var r = await cmd.ExecuteReaderAsync(_ct);
        while (await r.ReadAsync(_ct))
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public async Task<DbCatalog> ReadAsync(IReadOnlyCollection<string>? only, CancellationToken ct)
    {
        _ct = ct;
        await using var tx = c.BeginTransaction();
        _tx = tx;
        await using (var ro = new OracleCommand("SET TRANSACTION READ ONLY", c) { Transaction = tx, CommandTimeout = timeout }) await ro.ExecuteNonQueryAsync(ct);

        var who = (await Q("select user, sys_context('USERENV', 'DB_NAME') from dual")).Single();
        var user = S(who[0]) ?? "";
        var dbName = S(who[1]) ?? user;
        var catalog = new DbCatalog { Name = dbName, Dialect = "oracle" };
        var owners = only is { Count: > 0 } ? only.Select(o => o.Trim()).Where(o => o.Length > 0).ToList() : [user];
        // Unquoted names are stored in upper case; a name given in another case is matched without case.
        var existing = (await Q($"select username from all_users where upper(username) in ({List(owners.Select(o => o.ToUpperInvariant()))}) order by username")).Select(r => (string)r[0]!).ToList();
        if (existing.Count == 0) return catalog;
        foreach (var o in existing) catalog.Schema(o);
        var inOwners = List(existing);

        // Tables (not materialized-view containers, nested tables or the recycle bin), views and materialized views.
        foreach (var r in await Q($"select t.owner, t.table_name, cm.comments from all_tables t left join all_tab_comments cm on cm.owner = t.owner and cm.table_name = t.table_name " +
                                  $"where t.owner in ({inOwners}) and t.nested = 'NO' and t.secondary = 'N' and t.table_name not like 'BIN$%' and t.iot_type is null " +
                                  $"and not exists (select 1 from all_mviews mv where mv.owner = t.owner and mv.mview_name = t.table_name) order by t.owner, t.table_name"))
            catalog.Schema((string)r[0]!).Table((string)r[1]!).Comment = S(r[2]);
        foreach (var r in await Q($"select v.owner, v.view_name, v.text, cm.comments from all_views v left join all_tab_comments cm on cm.owner = v.owner and cm.table_name = v.view_name " +
                                  $"where v.owner in ({inOwners}) order by v.owner, v.view_name"))
        {
            var t = catalog.Schema((string)r[0]!).Table((string)r[1]!);
            t.Kind = DbFields.View;
            t.Definition = S(r[2])?.Trim().TrimEnd(';');
            t.Comment = S(r[3]);
        }
        foreach (var r in await Q($"select m.owner, m.mview_name, m.query, cm.comments from all_mviews m left join all_mview_comments cm on cm.owner = m.owner and cm.mview_name = m.mview_name " +
                                  $"where m.owner in ({inOwners}) order by m.owner, m.mview_name"))
        {
            var t = catalog.Schema((string)r[0]!).Table((string)r[1]!);
            t.Kind = DbFields.MaterializedView;
            t.Definition = S(r[2])?.Trim().TrimEnd(';');
            t.Comment = S(r[3]);
        }
        DbTableDef? Table(object? owner, object? name) => catalog.FindSchema((string)owner!)?.FindTable((string)name!);

        // Columns.
        var identity = (await Q($"select owner, table_name, column_name, generation_type from all_tab_identity_cols where owner in ({inOwners})"))
            .ToDictionary(r => ((string)r[0]!, (string)r[1]!, (string)r[2]!), r => S(r[3]));
        foreach (var r in await Q($"select c.owner, c.table_name, c.column_name, c.data_type, c.data_length, c.char_length, c.char_used, c.data_precision, c.data_scale, c.nullable, " +
                                  $"c.data_default, c.virtual_column, cm.comments from all_tab_cols c left join all_col_comments cm on cm.owner = c.owner and cm.table_name = c.table_name and cm.column_name = c.column_name " +
                                  $"where c.owner in ({inOwners}) and c.hidden_column = 'NO' order by c.owner, c.table_name, c.column_id"))
        {
            if (Table(r[0], r[1]) is not { } t) continue;
            var key = ((string)r[0]!, (string)r[1]!, (string)r[2]!);
            var def = S(r[10])?.Trim();
            var isIdentity = identity.TryGetValue(key, out var gen);
            var isVirtual = S(r[11]) == "YES";
            t.Columns.Add(new DbColumnDef
            {
                Name = (string)r[2]!, Type = TypeName((string)r[3]!, Int(r[4]), Int(r[5]), S(r[6]), r[7] == null ? null : Int(r[7]), r[8] == null ? null : Int(r[8])),
                Nullable = S(r[9]) != "N", Identity = isIdentity ? (gen == "ALWAYS" ? "always" : "byDefault") : null,
                Generated = isVirtual ? def : null, Default = isVirtual || isIdentity || def is null or "NULL" ? null : def, Comment = S(r[12]),
            });
        }

        // Keys and constraints (the NOT NULL checks Oracle names itself are the columns' NOT NULL).
        var consColumns = (await Q($"select owner, constraint_name, column_name from all_cons_columns where owner in ({inOwners}) order by owner, constraint_name, position"))
            .GroupBy(r => ((string)r[0]!, (string)r[1]!)).ToDictionary(g => g.Key, g => g.Select(r => (string)r[2]!).ToList());
        var cons = await Q($"select k.owner, k.table_name, k.constraint_name, k.constraint_type, k.search_condition, k.r_owner, k.r_constraint_name, k.delete_rule, k.generated " +
                           $"from all_constraints k where k.owner in ({inOwners}) and k.constraint_type in ('P', 'U', 'R', 'C') and k.table_name not like 'BIN$%' order by k.owner, k.table_name, k.constraint_name");
        var keyTables = cons.ToDictionary(r => ((string)r[0]!, (string)r[2]!), r => (string)r[1]!);
        var outside = cons.Where(r => (string)r[3]! == "R" && !keyTables.ContainsKey(((string)r[5]!, (string)r[6]!))).Select(r => ((string)r[5]!, (string)r[6]!)).Distinct().ToList();
        foreach (var (owner, name) in outside)
            foreach (var r in await Q($"select table_name from all_constraints where owner = {Lit(owner)} and constraint_name = {Lit(name)}"))
                keyTables[(owner, name)] = (string)r[0]!;
        foreach (var (owner, name) in outside)
            foreach (var r in await Q($"select column_name from all_cons_columns where owner = {Lit(owner)} and constraint_name = {Lit(name)} order by position"))
                (consColumns.TryGetValue((owner, name), out var l) ? l : consColumns[(owner, name)] = new()).Add((string)r[0]!);
        foreach (var r in cons)
        {
            if (Table(r[0], r[1]) is not { } t) continue;
            var key = ((string)r[0]!, (string)r[2]!);
            var cols = consColumns.GetValueOrDefault(key) ?? [];
            switch ((string)r[3]!)
            {
                case "P":
                    t.PrimaryKey.Clear();
                    t.PrimaryKey.AddRange(cols);
                    t.PrimaryKeyName = Generated(r[8]) ? null : (string)r[2]!;
                    break;
                case "U":
                {
                    var uq = new DbConstraintDef { Name = (string)r[2]!, Type = DbFields.UniqueConstraint };
                    uq.Columns.AddRange(cols);
                    t.Constraints.Add(uq);
                    break;
                }
                case "C":
                {
                    var cond = S(r[4])?.Trim();
                    if (cond == null || Generated(r[8]) && cols.Count == 1 && cond.Equals($"\"{cols[0]}\" IS NOT NULL", StringComparison.OrdinalIgnoreCase)) break;
                    t.Constraints.Add(new DbConstraintDef { Name = (string)r[2]!, Type = DbFields.CheckConstraint, Expression = cond });
                    break;
                }
                case "R":
                {
                    var refKey = ((string)r[5]!, (string)r[6]!);
                    var fk = new DbForeignKeyDef
                    {
                        Name = (string)r[2]!, RefSchema = refKey.Item1 == (string)r[0]! ? null : refKey.Item1, RefTable = keyTables.GetValueOrDefault(refKey) ?? "",
                        OnDelete = S(r[7]) is "CASCADE" or "SET NULL" ? S(r[7]) : null,
                    };
                    fk.Columns.AddRange(cols);
                    fk.RefColumns.AddRange(consColumns.GetValueOrDefault(refKey) ?? []);
                    t.ForeignKeys.Add(fk);
                    break;
                }
            }
        }

        // Indexes that do not back a constraint; function-based ones use their expressions.
        var backing = (await Q($"select owner, index_name from all_constraints where owner in ({inOwners}) and index_name is not null"))
            .Select(r => ((string)r[0]!, (string)r[1]!)).ToHashSet();
        var expressions = (await Q($"select index_owner, index_name, column_position, column_expression from all_ind_expressions where index_owner in ({inOwners})"))
            .ToDictionary(r => ((string)r[0]!, (string)r[1]!, Int(r[2])), r => S(r[3]));
        foreach (var g in (await Q($"select i.table_owner, i.table_name, i.index_name, i.uniqueness, i.index_type, c.column_name, c.column_position, i.owner " +
                                   $"from all_indexes i join all_ind_columns c on c.index_owner = i.owner and c.index_name = i.index_name " +
                                   $"where i.table_owner in ({inOwners}) and i.index_type not in ('LOB', 'IOT - TOP') and i.generated = 'N' order by i.owner, i.index_name, c.column_position"))
                     .GroupBy(r => ((string)r[7]!, (string)r[2]!)))
        {
            if (backing.Contains(g.Key)) continue;
            var first = g.First();
            if (Table(first[0], first[1]) is not { } t) continue;
            var ix = new DbIndexDef { Name = g.Key.Item2, Unique = S(first[3]) == "UNIQUE", Method = S(first[4])?.StartsWith("BITMAP", StringComparison.Ordinal) == true ? "bitmap" : null };
            ix.Columns.AddRange(g.Select(r => expressions.GetValueOrDefault((g.Key.Item1, g.Key.Item2, Int(r[6]))) is { } e ? e.Trim() : (string)r[5]!));
            t.Indexes.Add(ix);
        }

        // Sequences (not the ones behind identity columns).
        foreach (var r in await Q($"select sequence_owner, sequence_name, to_char(min_value), to_char(max_value), to_char(increment_by), cycle_flag from all_sequences " +
                                  $"where sequence_owner in ({inOwners}) and sequence_name not like 'ISEQ$$%' order by 1, 2"))
            catalog.Schema((string)r[0]!).Sequences.Add(new DbSequenceDef
            {
                Name = (string)r[1]!, Min = S(r[2]) == "1" ? null : S(r[2]), Max = S(r[3]) is { Length: >= 27 } ? null : S(r[3]), Increment = S(r[4]) == "1" ? null : S(r[4]), Cycle = S(r[5]) == "Y",
            });

        // Procedures, functions and triggers: their source, read by the DDL parser.
        var source = (await Q($"select owner, name, type, text from all_source where owner in ({inOwners}) and type in ('PROCEDURE', 'FUNCTION', 'TRIGGER') order by owner, type, name, line"))
            .GroupBy(r => ((string)r[0]!, (string)r[1]!, (string)r[2]!)).ToDictionary(g => g.Key, g => string.Concat(g.Select(r => (string?)r[3] ?? "")));
        foreach (var ((owner, name, type), text) in source.Where(s => s.Key.Item3 != "TRIGGER").OrderBy(s => s.Key.Item1).ThenBy(s => s.Key.Item2))
        {
            var rd = DdlParser.ParseModule("CREATE OR REPLACE " + text, "oracle").Routine ?? new DbRoutineDef();
            rd.Name = name;
            rd.Kind = type == "PROCEDURE" ? DbFields.Procedure : DbFields.Function;
            catalog.Schema(owner).Routines.Add(rd);
        }
        foreach (var r in await Q($"select owner, trigger_name, table_owner, table_name, trigger_type, triggering_event from all_triggers " +
                                  $"where owner in ({inOwners}) and base_object_type in ('TABLE', 'VIEW') order by owner, trigger_name"))
        {
            if (Table(r[2], r[3]) is not { } t) continue;
            var parsed = source.TryGetValue(((string)r[0]!, (string)r[1]!, "TRIGGER"), out var text) ? DdlParser.ParseModule("CREATE OR REPLACE " + text, "oracle").Trigger : null;
            var type = S(r[4]) ?? "";
            var tg = parsed ?? new DbTriggerDef();
            tg.Name = (string)r[1]!;
            tg.Timing = type.StartsWith("BEFORE", StringComparison.Ordinal) ? "BEFORE" : type.StartsWith("INSTEAD", StringComparison.Ordinal) ? "INSTEAD OF" : "AFTER";
            tg.Events = string.Join(", ", (S(r[5]) ?? "INSERT").Split(" OR ", StringSplitOptions.TrimEntries));
            tg.Level = type.Contains("EACH ROW", StringComparison.Ordinal) ? "ROW" : null;
            t.Triggers.Add(tg);
        }
        foreach (var r in await Q($"select owner, object_name, object_type from all_objects where owner in ({inOwners}) and object_type in ('PACKAGE', 'TYPE') order by 1, 3, 2"))
            catalog.Warnings.Add($"{((string)r[2]!).ToLowerInvariant()} {r[0]}.{r[1]}: not part of the data model; skipped");
        return catalog;
    }

    /// <summary>A column type as DDL writes it from the data dictionary's sizes.</summary>
    internal static string TypeName(string type, int dataLength, int charLength, string? charUsed, int? precision, int? scale)
    {
        var t = type.ToLowerInvariant();
        return t switch
        {
            "varchar2" or "nvarchar2" or "char" or "nchar" => $"{t}({(t.StartsWith('n') ? charLength : charLength > 0 ? charLength : dataLength)}{(charUsed == "C" && !t.StartsWith('n') ? " char" : "")})",
            "raw" => $"raw({dataLength})",
            "number" when precision == null && scale == 0 => "integer",
            "number" when precision == null => "number",
            "number" => scale is null or 0 ? $"number({precision})" : $"number({precision},{scale})",
            "float" => precision is null or 126 ? "float" : $"float({precision})",
            _ => t,
        };
    }

    private static bool Generated(object? g) => S(g) == "GENERATED NAME";
    private static int Int(object? o) => o == null ? 0 : Convert.ToInt32(o, System.Globalization.CultureInfo.InvariantCulture);
    private static string Lit(string s) => DdlWriter.Literal(s);
    private static string List(IEnumerable<string> items) => string.Join(", ", items.Select(Lit).DefaultIfEmpty("''"));
    private static string? S(object? o) => LiveSchemaReader.S(o);
}
