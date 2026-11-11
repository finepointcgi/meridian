using System.Text.RegularExpressions;
using Meridian.Core.DataModeling;

namespace Meridian.Serialization.Database;

/// <summary>
/// Tolerant reverse engineering of DDL scripts into a <see cref="DbCatalog"/>: CREATE SCHEMA / TABLE / VIEW / MATERIALIZED
/// VIEW / INDEX / SEQUENCE / TYPE / DOMAIN / FUNCTION / PROCEDURE / TRIGGER, ALTER TABLE (ADD / ALTER / DROP COLUMN, ADD /
/// DROP CONSTRAINT), COMMENT ON, SQL Server MS_Description extended properties and MySQL COMMENT clauses, for PostgreSQL,
/// SQL Server (GO batches, [brackets]), MySQL / MariaDB (`backticks`, DELIMITER), SQLite and Oracle ("/" blocks).
/// Statements it does not understand are skipped with a warning; data statements (INSERT, SET, GRANT …) are skipped silently.
/// </summary>
public sealed partial class DdlParser
{
    private readonly string _dialect;
    private readonly DbCatalog _catalog;
    private string _currentSchema;
    private List<SqlToken> _t = new();
    private string _src = "";
    /// <summary>Triggers whose table is not in the script (kept for <see cref="ParseModule"/>).</summary>
    private readonly List<(string? Schema, string Table, DbTriggerDef Trigger)> _looseTriggers = new();
    private int _p;
    private int _end;
    private readonly HashSet<string> _declared = new(StringComparer.OrdinalIgnoreCase);

    private DdlParser(string dialect, string catalogName)
    {
        _dialect = dialect;
        _catalog = new DbCatalog { Name = catalogName, Dialect = dialect };
        _currentSchema = DefaultSchema(dialect, catalogName);
        _catalog.Schema(_currentSchema);   // listed first; dropped again if nothing lands in it
    }

    /// <summary>The schema unqualified names belong to: public (PostgreSQL), dbo (SQL Server), main (SQLite), else the database name.</summary>
    public static string DefaultSchema(string dialect, string catalogName) => dialect switch
    {
        "postgresql" => "public",
        "sqlserver" => "dbo",
        "sqlite" => "main",
        _ => catalogName.Length > 0 ? catalogName : "default",
    };

    /// <summary>Parses a script. <paramref name="dialect"/> null or empty detects it from the text.</summary>
    public static DbCatalog Parse(string sql, string? dialect = null, string catalogName = "database")
    {
        dialect = string.IsNullOrEmpty(dialect) ? Detect(sql) : dialect;
        var p = new DdlParser(dialect, catalogName);
        var initial = p._currentSchema;
        p.Run(sql);
        p._catalog.Schemas.RemoveAll(s => s.Name == initial && s.Tables.Count + s.Sequences.Count + s.Types.Count + s.Routines.Count == 0 && !p._declared.Contains(s.Name));
        return p._catalog;
    }

    /// <summary>
    /// Parses one stored CREATE FUNCTION / PROCEDURE / TRIGGER statement as a database catalog keeps it (pg_get_functiondef,
    /// sys.sql_modules, ALL_SOURCE with "CREATE OR REPLACE " in front). The trigger's table need not be defined.
    /// </summary>
    public static (DbRoutineDef? Routine, DbTriggerDef? Trigger, string? TriggerTable) ParseModule(string sql, string dialect)
    {
        var p = new DdlParser(dialect, "database");
        // One statement: a batch end after it keeps T-SQL bodies without BEGIN … END (and Oracle blocks) in one piece.
        p.Run(sql.TrimEnd() + (dialect switch { "sqlserver" => "\nGO\n", "oracle" => "\n/\n", _ => "" }));
        var routine = p._catalog.Schemas.SelectMany(s => s.Routines).FirstOrDefault();
        var tableTrigger = p._catalog.AllTables.SelectMany(t => t.Triggers.Select(tg => (t.Name, tg))).FirstOrDefault();
        if (tableTrigger.tg != null) return (routine, tableTrigger.tg, tableTrigger.Name);
        var loose = p._looseTriggers.FirstOrDefault();
        return (routine, loose.Trigger, loose.Table);
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)] private static partial Regex GoPattern();
    [GeneratedRegex(@"\bAUTO_INCREMENT\b|\bENGINE\s*=|`|\bUNSIGNED\b", RegexOptions.IgnoreCase)] private static partial Regex MySqlPattern();
    [GeneratedRegex(@"\bAUTOINCREMENT\b|\bWITHOUT\s+ROWID\b|\bPRAGMA\b", RegexOptions.IgnoreCase)] private static partial Regex SqlitePattern();
    [GeneratedRegex(@"\bVARCHAR2\b|\bNUMBER\s*\(|\bNVARCHAR2\b|\bCLOB\b|\bSYSDATE\b|^\s*/\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)] private static partial Regex OraclePattern();
    [GeneratedRegex(@"\[\w+\]|\bIDENTITY\s*\(|\bNVARCHAR\b|\bsp_addextendedproperty\b|\bdbo\.", RegexOptions.IgnoreCase)] private static partial Regex SqlServerPattern();

    /// <summary>Guesses the dialect of a script from telltale syntax (PostgreSQL when nothing stands out).</summary>
    public static string Detect(string sql)
    {
        if (GoPattern().IsMatch(sql) || SqlServerPattern().IsMatch(sql)) return "sqlserver";
        if (MySqlPattern().IsMatch(sql)) return "mysql";
        if (SqlitePattern().IsMatch(sql)) return "sqlite";
        if (OraclePattern().IsMatch(sql)) return "oracle";
        return "postgresql";
    }

    // ================================================================== statements

    private void Run(string sql)
    {
        _t = SqlTokenizer.Tokenize(sql, _dialect == "mysql", out _src);
        var hasBatches = _t.Any(x => x.Kind == SqlTokenKind.BatchEnd);
        var i = 0;
        while (i < _t.Count)
        {
            if (_t[i].Kind == SqlTokenKind.BatchEnd || _t[i].IsSymbol(";")) { i++; continue; }
            var end = StatementEnd(i, hasBatches);
            _p = i;
            _end = end;
            try { Statement(); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
            {
                Warn(i, $"could not read the statement ({ex.Message}); skipped");
            }
            i = end + 1;
        }
    }

    /// <summary>Index of the token that ends the statement starting at <paramref name="i"/> (its ';' or batch end, or the end).</summary>
    private int StatementEnd(int i, bool hasBatches)
    {
        var routine = IsRoutineStart(i);
        // T-SQL bodies need no semicolons and Oracle declarations end in them: a routine runs to its GO / "/". MySQL's
        // DELIMITER blocks also end in a batch mark, but a routine after DELIMITER ; is closed by its own END and semicolon.
        if (routine && hasBatches && _dialect != "mysql")
        {
            for (var k = i; k < _t.Count; k++) if (_t[k].Kind == SqlTokenKind.BatchEnd) return k;
            return _t.Count;
        }
        var depth = 0;
        var parens = 0;
        for (var k = i; k < _t.Count; k++)
        {
            var t = _t[k];
            if (t.Kind == SqlTokenKind.BatchEnd) return k;
            if (t.IsSymbol("(")) parens++;
            else if (t.IsSymbol(")")) parens = Math.Max(0, parens - 1);
            if (routine && t.Kind == SqlTokenKind.Word)
            {
                if (t.Is("BEGIN") || t.Is("CASE") && depth > 0) depth++;
                else if (t.Is("END") && depth > 0)
                {
                    depth--;
                    // END IF / END LOOP / END WHILE close their own blocks, not BEGIN; END CASE closes the CASE counted above.
                    if (k + 1 < _t.Count && (_t[k + 1].Is("IF") || _t[k + 1].Is("LOOP") || _t[k + 1].Is("WHILE") || _t[k + 1].Is("REPEAT"))) depth++;
                    else if (k + 1 < _t.Count && _t[k + 1].Is("CASE")) k++;
                }
            }
            if (t.IsSymbol(";") && depth == 0 && parens == 0) return k;
            // T-SQL needs no semicolons: a new statement keyword at the top level ends the previous statement.
            if (!routine && k > i && parens == 0 && StartsStatement(k)) return k - 1;
        }
        return _t.Count;
    }

    private bool StartsStatement(int k)
    {
        var t = _t[k];
        if (t.Kind != SqlTokenKind.Word) return false;
        var next = k + 1 < _t.Count ? _t[k + 1] : default;
        if (t.Is("CREATE") || t.Is("GRANT") || t.Is("USE") || t.Is("PRINT")) return true;
        if (t.Is("EXEC") || t.Is("EXECUTE")) return !_t[k - 1].Is("FOR") && !_t[k - 1].Is("TO") && !_t[k - 1].Is("GRANT") && !(next.Is("FUNCTION") || next.Is("PROCEDURE"));
        if (t.Is("COMMENT")) return next.Is("ON");
        if (t.Is("ALTER") || t.Is("DROP"))
            return (next.Is("TABLE") || next.Is("VIEW") || next.Is("INDEX") || next.Is("SEQUENCE") || next.Is("SCHEMA") || next.Is("PROCEDURE") || next.Is("FUNCTION") || next.Is("TRIGGER") || next.Is("TYPE"))
                   && !_t[k - 1].IsSymbol(",") && !_t[k - 1].Is("ON");
        return false;
    }

    private bool IsRoutineStart(int i)
    {
        if (!_t[i].Is("CREATE") && !_t[i].Is("ALTER")) return false;
        for (var k = i + 1; k < Math.Min(_t.Count, i + 8); k++)
        {
            var t = _t[k];
            if (t.Is("FUNCTION") || t.Is("PROCEDURE") || t.Is("PROC") || t.Is("TRIGGER") || t.Is("PACKAGE")) return true;
            if (t.Is("TABLE") || t.Is("VIEW") || t.Is("INDEX") || t.Is("SCHEMA") || t.Is("SEQUENCE") || t.Is("TYPE")) return false;
        }
        return false;
    }

    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "SET", "INSERT", "UPDATE", "DELETE", "SELECT", "GRANT", "REVOKE", "BEGIN", "COMMIT", "ROLLBACK", "START", "END", "DROP", "PRAGMA",
        "LOCK", "UNLOCK", "ANALYZE", "VACUUM", "TRUNCATE", "PRINT", "DECLARE", "IF", "WITH", "EXEC", "EXECUTE", "CALL", "DO", "SAVEPOINT",
        "RELEASE", "REINDEX", "CLUSTER", "COPY", "\\connect", "OWNER", "REFRESH", "MERGE", "RAISERROR", "SELECT", "VALUES",
    };

    private void Statement()
    {
        var first = Peek();
        if (first.Is("CREATE")) { _p++; Create(); return; }
        if (first.Is("ALTER")) { _p++; Alter(); return; }
        if (first.Is("COMMENT")) { _p++; CommentOn(); return; }
        if (first.Is("USE")) { _p++; Use(); return; }
        if ((first.Is("EXEC") || first.Is("EXECUTE")) && _t.Skip(_p).Take(_end - _p).Any(x => x.Text.Contains("sp_addextendedproperty", StringComparison.OrdinalIgnoreCase)))
        {
            ExtendedProperty();
            return;
        }
        if (first.Kind == SqlTokenKind.Word && Ignored.Contains(first.Text)) return;
        Warn(_p, $"skipped: {Snippet(_p)}");
    }

    private void Create()
    {
        Accept("OR"); Accept("REPLACE"); Accept("ALTER");
        // Modifiers before the object kind.
        while (Peek().Is("TEMP") || Peek().Is("TEMPORARY") || Peek().Is("GLOBAL") || Peek().Is("LOCAL") || Peek().Is("UNLOGGED") || Peek().Is("EDITIONABLE")
               || Peek().Is("NONEDITIONABLE") || Peek().Is("FORCE") || Peek().Is("NOFORCE") || Peek().Is("SECURE") || Peek().Is("RECURSIVE") || Peek().Is("CONSTRAINT")
               || Peek().Is("DEFINER") || Peek().Is("SQL") || Peek().Is("ALGORITHM") || Peek().Is("VIRTUAL"))
        {
            if (Peek().Is("DEFINER"))
            {
                _p++;
                while (_p < _end && !(Peek().Is("PROCEDURE") || Peek().Is("FUNCTION") || Peek().Is("TRIGGER") || Peek().Is("VIEW") || Peek().Is("SQL") || Peek().Is("EVENT"))) _p++;
                continue;
            }
            if (Peek().Is("ALGORITHM")) { _p++; Accept("="); _p++; continue; }
            if (Peek().Is("SQL")) { _p++; Accept("SECURITY"); _p++; continue; }
            _p++;
        }
        var kind = Next();
        if (kind.Is("TABLE")) { Table(); return; }
        if (kind.Is("VIEW")) { View(""); return; }
        if (kind.Is("MATERIALIZED") && Accept("VIEW")) { View(DbFields.MaterializedView); return; }
        if (kind.Is("UNIQUE") || kind.Is("INDEX") || kind.Is("CLUSTERED") || kind.Is("NONCLUSTERED") || kind.Is("FULLTEXT") || kind.Is("SPATIAL") || kind.Is("BITMAP"))
        {
            var unique = kind.Is("UNIQUE");
            string? method = kind.Is("FULLTEXT") || kind.Is("SPATIAL") || kind.Is("BITMAP") ? kind.Text.ToLowerInvariant() : kind.Is("CLUSTERED") ? "clustered" : null;
            while (!kind.Is("INDEX") && !Peek().Is("INDEX") && _p < _end)
            {
                if (Peek().Is("CLUSTERED")) method = "clustered";
                else if (Peek().Is("FULLTEXT") || Peek().Is("SPATIAL")) method = Peek().Text.ToLowerInvariant();
                _p++;
            }
            if (!kind.Is("INDEX")) Accept("INDEX");
            Index(unique, method);
            return;
        }
        if (kind.Is("SCHEMA") || kind.Is("DATABASE"))
        {
            Accept("IF"); Accept("NOT"); Accept("EXISTS");
            if (!Peek().IsName) return;
            var name = Next().Text;
            if (Peek().Is("AUTHORIZATION") && name.Equals("AUTHORIZATION", StringComparison.OrdinalIgnoreCase)) name = _t[_p + 1].Text;
            if (kind.Is("DATABASE"))
            {
                if (_dialect == "mysql") { _catalog.Schema(name); _currentSchema = name; }
                else if (_catalog.Name is "" or "database") _catalog.Name = name;
                return;
            }
            _catalog.Schema(name);
            _declared.Add(name);
            return;
        }
        if (kind.Is("SEQUENCE")) { Sequence(); return; }
        if (kind.Is("TYPE")) { TypeDef(); return; }
        if (kind.Is("DOMAIN")) { Domain(); return; }
        if (kind.Is("FUNCTION") || kind.Is("PROCEDURE") || kind.Is("PROC")) { Routine(kind.Is("FUNCTION") ? DbFields.Function : DbFields.Procedure); return; }
        if (kind.Is("TRIGGER")) { Trigger(); return; }
        if (kind.Is("EXTENSION") || kind.Is("ROLE") || kind.Is("USER") || kind.Is("LOGIN") || kind.Is("SYNONYM") || kind.Is("TABLESPACE") || kind.Is("PACKAGE") || kind.Is("POLICY") || kind.Is("PUBLICATION") || kind.Is("RULE"))
        {
            Warn(_p - 1, $"skipped: CREATE {kind.Text.ToUpperInvariant()} {(_p < _end ? Peek().Text : "")} (not part of the data model)");
            return;
        }
        Warn(_p - 1, $"skipped: CREATE {kind.Text} … (not understood)");
    }

    private void Use()
    {
        if (!Peek().IsName) return;
        var name = Next().Text;
        if (_dialect == "mysql") { _currentSchema = name; _catalog.Schema(name); }
        else if (_catalog.Name is "" or "database") _catalog.Name = name;
    }

    // ================================================================== tables

    private void Table()
    {
        Accept("IF"); Accept("NOT"); Accept("EXISTS");
        var (schema, name) = QualifiedName();
        var table = _catalog.Schema(schema).Table(name);
        if (Peek().Is("AS") || Peek().Is("LIKE") || Peek().Is("OF") || Peek().Is("PARTITION"))
        {
            Warn(_p, $"{schema}.{name}: CREATE TABLE … {Peek().Text.ToUpperInvariant()} — the columns can't be read from the script");
            return;
        }
        if (!Peek().IsSymbol("(")) { Warn(_p, $"{schema}.{name}: no column list"); return; }
        var close = Close(_p);
        _p++;
        foreach (var (from, to) in Elements(_p))
        {
            _p = from;
            TableElement(table, schema, to);
        }
        _p = close + 1;
        // Table options after the column list.
        while (_p < _end)
        {
            if (Peek().Is("COMMENT")) { _p++; Accept("="); if (Peek().Kind == SqlTokenKind.String) table.Comment = Next().Text; continue; }
            if (Peek().Is("WITHOUT") || Peek().Is("ENGINE") || Peek().Is("TABLESPACE") || Peek().Is("ON") || Peek().Is("WITH") || Peek().Is("INHERITS")) { _p++; continue; }
            _p++;
        }
    }

    /// <summary>Splits the parenthesised list that starts at <paramref name="from"/> (just after "(") into its top-level items.</summary>
    private List<(int From, int To)> Elements(int from)
    {
        var items = new List<(int, int)>();
        var depth = 0;
        var start = from;
        for (var k = from; k < _end; k++)
        {
            var t = _t[k];
            if (t.IsSymbol("(")) depth++;
            else if (t.IsSymbol(")"))
            {
                if (depth == 0) { if (k > start) items.Add((start, k)); return items; }
                depth--;
            }
            else if (t.IsSymbol(",") && depth == 0) { if (k > start) items.Add((start, k)); start = k + 1; }
        }
        if (_end > start) items.Add((start, _end));
        return items;
    }

    /// <summary>Index of the ")" closing the "(" at <paramref name="open"/> (or the statement end).</summary>
    private int Close(int open)
    {
        var depth = 0;
        for (var k = open; k < _end; k++)
        {
            if (_t[k].IsSymbol("(")) depth++;
            else if (_t[k].IsSymbol(")") && --depth == 0) return k;
        }
        return _end - 1;
    }

    private void TableElement(DbTableDef table, string schema, int to)
    {
        var save = _end;
        _end = to;
        try
        {
            string? constraintName = null;
            if (Peek().Is("CONSTRAINT")) { _p++; constraintName = Next().Text; }
            var t = Peek();
            if (t.Is("PRIMARY") && Ahead(1).Is("KEY"))
            {
                _p += 2;
                SkipWords("CLUSTERED", "NONCLUSTERED", "USING");
                table.PrimaryKey.Clear();
                table.PrimaryKey.AddRange(ColumnList());
                table.PrimaryKeyName = constraintName ?? table.PrimaryKeyName;
                return;
            }
            if (t.Is("FOREIGN") && Ahead(1).Is("KEY"))
            {
                _p += 2;
                if (Peek().IsName) _p++;   // MySQL: FOREIGN KEY name (cols)
                var fk = new DbForeignKeyDef { Name = constraintName };
                fk.Columns.AddRange(ColumnList());
                References(fk, schema);
                table.ForeignKeys.Add(fk);
                return;
            }
            if (t.Is("UNIQUE"))
            {
                _p++;
                var isIndex = Accept("KEY") || Accept("INDEX");
                SkipWords("CLUSTERED", "NONCLUSTERED", "NULLS", "NOT", "DISTINCT");
                var indexName = Peek().IsName ? Next().Text : null;
                var cols = ColumnList();
                var name = constraintName ?? indexName ?? $"uq_{table.Name}_{string.Join("_", cols)}";
                if (isIndex && _dialect == "mysql")
                {
                    var ix = new DbIndexDef { Name = name, Unique = true };
                    ix.Columns.AddRange(cols);
                    table.Indexes.Add(ix);
                }
                else if (cols.Count == 1 && constraintName == null && indexName == null && table.FindColumn(cols[0]) is { } col) col.Unique = true;
                else
                {
                    var c = new DbConstraintDef { Name = name, Type = DbFields.UniqueConstraint };
                    c.Columns.AddRange(cols);
                    table.Constraints.Add(c);
                }
                return;
            }
            if (t.Is("CHECK"))
            {
                _p++;
                var expr = Parenthesised();
                table.Constraints.Add(new DbConstraintDef { Name = constraintName ?? $"ck_{table.Name}_{table.Constraints.Count(c => c.Type == DbFields.CheckConstraint) + 1}", Type = DbFields.CheckConstraint, Expression = expr });
                return;
            }
            if ((t.Is("KEY") || t.Is("INDEX") || t.Is("FULLTEXT") || t.Is("SPATIAL")) && constraintName == null && (_dialect is "mysql" || Ahead(1).IsName && Ahead(2).IsSymbol("(")))
            {
                _p++;
                var method = t.Is("FULLTEXT") || t.Is("SPATIAL") ? t.Text.ToLowerInvariant() : null;
                Accept("KEY"); Accept("INDEX");
                var name = Peek().IsName ? Next().Text : $"ix_{table.Name}_{table.Indexes.Count + 1}";
                var ix = new DbIndexDef { Name = name, Method = method };
                ix.Columns.AddRange(ColumnList());
                table.Indexes.Add(ix);
                return;
            }
            if (t.Is("EXCLUDE") || t.Is("LIKE") || t.Is("PERIOD") || t.Is("INDEX") || t.Is("CONSTRAINT"))
            {
                Warn(_p, $"{table.Name}: skipped table element {t.Text.ToUpperInvariant()} …");
                return;
            }
            Column(table, schema);
        }
        finally { _end = save; }
    }

    private void Column(DbTableDef table, string schema)
    {
        if (!Peek().IsName) { Warn(_p, $"{table.Name}: unreadable column definition"); return; }
        var name = Next().Text;
        var col = table.FindColumn(name) ?? new DbColumnDef { Name = name };
        if (!table.Columns.Contains(col)) table.Columns.Add(col);
        // T-SQL computed column without a type: name AS (expr)
        if (Peek().Is("AS"))
        {
            _p++;
            col.Generated = Peek().IsSymbol("(") ? Parenthesised() : Expression();
            col.Type = col.Type.Length > 0 ? col.Type : "computed";
            SkipWords("PERSISTED", "STORED", "VIRTUAL");
        }
        else col.Type = DataType();
        ColumnConstraints(table, schema, col);
    }

    private static readonly HashSet<string> ConstraintWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "NOT", "NULL", "PRIMARY", "UNIQUE", "DEFAULT", "REFERENCES", "CHECK", "CONSTRAINT", "GENERATED", "AUTO_INCREMENT", "AUTOINCREMENT",
        "IDENTITY", "COLLATE", "COMMENT", "ON", "AS", "CHARACTER", "CHARSET", "ENCODE", "SPARSE", "ROWGUIDCOL", "FILESTREAM", "MASKED",
        "ENCRYPT", "VISIBLE", "INVISIBLE", "STORAGE", "COMPRESSION", "WITH", "ENABLE", "DISABLE",
    };

    /// <summary>A data type: words (multi-word types such as "double precision", "timestamp with time zone"), size arguments, array suffixes.</summary>
    private string DataType()
    {
        var parts = new List<string>();
        while (_p < _end)
        {
            var t = Peek();
            if (t.IsSymbol("(") && parts.Count > 0)
            {
                var close = Close(_p);
                parts[^1] += SqlTokenizer.Text(_t, _p, close + 1).Replace(", ", ",");
                _p = close + 1;
                continue;
            }
            if (t.IsSymbol("[]") || t.IsSymbol("[") ) { parts[^1] += "[]"; _p++; if (Peek().IsSymbol("]")) _p++; continue; }
            if (t.IsSymbol(".") && parts.Count > 0 && Ahead(1).IsName) { parts[^1] += "." + Ahead(1).Text; _p += 2; continue; }
            if (t.Kind is not (SqlTokenKind.Word or SqlTokenKind.Quoted)) break;
            if (parts.Count == 0 && t.Kind == SqlTokenKind.Word && t.Text.ToUpperInvariant() is "PRIMARY" or "NOT" or "NULL" or "UNIQUE" or "DEFAULT" or "REFERENCES" or "CHECK"
                    or "CONSTRAINT" or "GENERATED" or "AUTO_INCREMENT" or "AUTOINCREMENT" or "COLLATE") break;   // SQLite: a column without a type
            if (parts.Count > 0 && t.Kind == SqlTokenKind.Word && ConstraintWords.Contains(t.Text) && !(t.Is("WITH") && TypeContinuation(parts, t.Text))) break;
            if (parts.Count > 0 && t.Kind == SqlTokenKind.Word && !TypeContinuation(parts, t.Text)) break;
            parts.Add(t.Kind == SqlTokenKind.Quoted ? t.Text : t.Text);
            _p++;
        }
        return string.Join(" ", parts);
    }

    /// <summary>Words that continue a multi-word type ("double precision", "character varying", "with time zone", "int unsigned").</summary>
    private static bool TypeContinuation(List<string> parts, string word) =>
        word.ToLowerInvariant() is "precision" or "varying" or "without" or "time" or "zone" or "unsigned" or "signed" or "zerofill" or "array"
            or "large" or "object" or "local" or "byte" or "char" or "to" or "second" or "minute" or "hour" or "day" or "month" or "year" or "varchar"
        || parts[^1].Equals("with", StringComparison.OrdinalIgnoreCase) || parts[^1].Equals("without", StringComparison.OrdinalIgnoreCase)
        || word.Equals("with", StringComparison.OrdinalIgnoreCase) && parts[^1].StartsWith("timestamp", StringComparison.OrdinalIgnoreCase)
        || word.Equals("with", StringComparison.OrdinalIgnoreCase) && parts[^1].StartsWith("time", StringComparison.OrdinalIgnoreCase)
        || word.Equals("varying", StringComparison.OrdinalIgnoreCase);

    private void ColumnConstraints(DbTableDef table, string schema, DbColumnDef col)
    {
        string? constraintName = null;
        while (_p < _end)
        {
            var t = Peek();
            if (t.Is("CONSTRAINT")) { _p++; constraintName = Next().Text; continue; }
            if (t.Is("NOT") && Ahead(1).Is("NULL")) { _p += 2; col.Nullable = false; continue; }
            if (t.Is("NULL")) { _p++; col.Nullable = true; continue; }
            if (t.Is("PRIMARY") && Ahead(1).Is("KEY"))
            {
                _p += 2;
                SkipWords("ASC", "DESC", "CLUSTERED", "NONCLUSTERED");
                if (Peek().Is("ON") && Ahead(1).Is("CONFLICT")) _p += 3;
                if (!table.IsPrimaryKey(col.Name)) table.PrimaryKey.Add(col.Name);
                if (constraintName != null) table.PrimaryKeyName = constraintName;
                col.Nullable = false;
                if (Accept("AUTOINCREMENT")) col.Identity = "autoIncrement";
                constraintName = null;
                continue;
            }
            if (t.Is("UNIQUE"))
            {
                _p++;
                Accept("KEY"); SkipWords("CLUSTERED", "NONCLUSTERED");
                if (constraintName != null)
                {
                    var c = new DbConstraintDef { Name = constraintName, Type = DbFields.UniqueConstraint };
                    c.Columns.Add(col.Name);
                    table.Constraints.Add(c);
                }
                else col.Unique = true;
                constraintName = null;
                continue;
            }
            if (t.Is("DEFAULT"))
            {
                _p++;
                col.Default = Expression();
                col.DefaultName = constraintName;
                constraintName = null;
                continue;
            }
            if (t.Is("REFERENCES"))
            {
                var fk = new DbForeignKeyDef { Name = constraintName };
                fk.Columns.Add(col.Name);
                References(fk, schema);
                table.ForeignKeys.Add(fk);
                constraintName = null;
                continue;
            }
            if (t.Is("CHECK"))
            {
                _p++;
                var expr = Parenthesised();
                table.Constraints.Add(new DbConstraintDef { Name = constraintName ?? $"ck_{table.Name}_{col.Name}", Type = DbFields.CheckConstraint, Expression = expr });
                constraintName = null;
                continue;
            }
            if (t.Is("GENERATED"))
            {
                _p++;
                if (Accept("ALWAYS")) { }
                else if (Accept("BY")) { Accept("DEFAULT"); Accept("ON"); Accept("NULL"); col.Identity = "byDefault"; }
                Accept("AS");
                if (Accept("IDENTITY"))
                {
                    col.Identity ??= "always";
                    if (Peek().IsSymbol("(")) _p = Close(_p) + 1;
                    col.Nullable = false;
                }
                else if (Peek().IsSymbol("("))
                {
                    col.Generated = Parenthesised();
                    col.Identity = null;
                    SkipWords("STORED", "VIRTUAL", "PERSISTED");
                }
                continue;
            }
            if (t.Is("AS") && Ahead(1).IsSymbol("("))
            {
                _p++;
                col.Generated = Parenthesised();
                SkipWords("STORED", "VIRTUAL", "PERSISTED");
                continue;
            }
            if (t.Is("IDENTITY"))
            {
                _p++;
                if (Peek().IsSymbol("(")) _p = Close(_p) + 1;
                col.Identity = "always";
                col.Nullable = false;
                continue;
            }
            if (t.Is("AUTO_INCREMENT") || t.Is("AUTOINCREMENT")) { _p++; col.Identity = "autoIncrement"; continue; }
            if (t.Is("COLLATE")) { _p++; col.Collation = Next().Text; continue; }
            if (t.Is("COMMENT")) { _p++; if (Peek().Kind == SqlTokenKind.String) col.Comment = Next().Text; continue; }
            if (t.Is("ON") && Ahead(1).Is("UPDATE")) { _p += 2; Expression(); continue; }
            if (t.Is("CHARACTER") && Ahead(1).Is("SET") || t.Is("CHARSET")) { _p += t.Is("CHARSET") ? 1 : 2; Accept("="); _p++; continue; }
            _p++;   // anything else (SPARSE, ENCODE …) is skipped
        }
    }

    /// <summary>An expression up to the next column constraint keyword or the end of the element.</summary>
    private string Expression()
    {
        var from = _p;
        var depth = 0;
        while (_p < _end)
        {
            var t = Peek();
            if (t.IsSymbol("(")) depth++;
            else if (t.IsSymbol(")")) { if (depth == 0) break; depth--; }
            else if (depth == 0 && _p > from && t.Kind == SqlTokenKind.Word && ExpressionStop(t)) break;
            else if (depth == 0 && t.IsSymbol(",")) break;
            _p++;
        }
        return SqlTokenizer.Text(_t, from, _p);
    }

    private bool ExpressionStop(SqlToken t) =>
        t.Is("NOT") && Ahead(1).Is("NULL") || t.Is("NULL") && !Ahead(-1).Is("IS") && !Ahead(-1).Is("NOT") || t.Is("PRIMARY") || t.Is("UNIQUE") || t.Is("REFERENCES")
        || t.Is("CHECK") || t.Is("CONSTRAINT") || t.Is("GENERATED") || t.Is("COLLATE") || t.Is("COMMENT") || t.Is("AUTO_INCREMENT")
        || t.Is("ON") && Ahead(1).Is("UPDATE") || t.Is("FOR") || t.Is("WITH") || t.Is("IDENTITY");

    /// <summary>The text inside the parentheses at the current position (the position moves past them).</summary>
    private string Parenthesised()
    {
        if (!Peek().IsSymbol("(")) return Expression();
        var close = Close(_p);
        var text = SqlTokenizer.Text(_t, _p + 1, close);
        _p = close + 1;
        return text;
    }

    private void References(DbForeignKeyDef fk, string schema)
    {
        Expect("REFERENCES");
        var (rs, rt) = QualifiedName();
        fk.RefSchema = rs;
        fk.RefTable = rt;
        if (Peek().IsSymbol("(")) fk.RefColumns.AddRange(ColumnList());
        while (_p < _end)
        {
            if (Peek().Is("ON") && (Ahead(1).Is("DELETE") || Ahead(1).Is("UPDATE")))
            {
                var delete = Ahead(1).Is("DELETE");
                _p += 2;
                var action = Next().Text.ToUpperInvariant();
                if (action is "SET" or "NO") action += " " + Next().Text.ToUpperInvariant();
                if (delete) fk.OnDelete = action; else fk.OnUpdate = action;
                continue;
            }
            if (Peek().Is("MATCH")) { _p += 2; continue; }
            if (Peek().Is("DEFERRABLE") || Peek().Is("INITIALLY") || Peek().Is("DEFERRED") || Peek().Is("IMMEDIATE") || Peek().Is("NOT") && Ahead(1).Is("DEFERRABLE")
                || Peek().Is("NOT") && Ahead(1).Is("FOR") || Peek().Is("FOR") && Ahead(1).Is("REPLICATION") || Peek().Is("REPLICATION") || Peek().Is("ENABLE") || Peek().Is("NOVALIDATE"))
            {
                _p++;
                continue;
            }
            break;
        }
        if (fk.RefSchema == _currentSchema && schema == _currentSchema) fk.RefSchema = null;
    }

    /// <summary>A parenthesised column list (sort order, lengths and operator classes dropped; expressions kept as text).</summary>
    private List<string> ColumnList()
    {
        var list = new List<string>();
        if (!Peek().IsSymbol("(")) return list;
        var close = Close(_p);
        var save = _end;
        _end = close;
        _p++;
        foreach (var (from, to) in Elements(_p))
        {
            var t = _t[from];
            if (to - from == 1 || t.IsName && (_t[from + 1].Is("ASC") || _t[from + 1].Is("DESC") || _t[from + 1].Is("NULLS") || _t[from + 1].Is("COLLATE") || _t[from + 1].IsSymbol("(") && _dialect == "mysql" || _t[from + 1].IsName && !_t[from + 1].IsSymbol("(")))
                list.Add(t.Text);
            else list.Add(SqlTokenizer.Text(_t, from, to));
        }
        _end = save;
        _p = close + 1;
        return list;
    }

    // ================================================================== other objects

    private void View(string kind)
    {
        Accept("IF"); Accept("NOT"); Accept("EXISTS");
        var (schema, name) = QualifiedName();
        var view = _catalog.Schema(schema).Table(name);
        view.Kind = kind == DbFields.MaterializedView ? DbFields.MaterializedView : DbFields.View;
        var named = Peek().IsSymbol("(") ? ColumnList() : [];
        while (_p < _end && !Peek().Is("AS")) _p++;
        if (!Accept("AS")) { Warn(_p, $"view {name}: no AS SELECT"); return; }
        var from = _p;
        var to = _end;
        for (var k = from; k < _end; k++) if (_t[k].Is("WITH") && k + 1 < _end && (_t[k + 1].Is("DATA") || _t[k + 1].Is("NO") || _t[k + 1].Is("CHECK") || _t[k + 1].Is("READ"))) { to = k; break; }
        view.Definition = SqlTokenizer.Text(_t, from, to);
        var columns = named.Count > 0 ? named : SelectColumns(from, to);
        foreach (var c in columns.Where(c => c.Length > 0 && view.FindColumn(c) == null)) view.Columns.Add(new DbColumnDef { Name = c, Type = "", Nullable = true });
    }

    /// <summary>Output column names of a simple SELECT (aliases, else the last identifier of each item; "*" gives none).</summary>
    private List<string> SelectColumns(int from, int to)
    {
        var k = from;
        while (k < to && !_t[k].Is("SELECT")) k++;
        if (k >= to) return [];
        k++;
        if (k < to && (_t[k].Is("DISTINCT") || _t[k].Is("ALL"))) k++;
        if (k < to && _t[k].Is("TOP")) k += 2;
        var names = new List<string>();
        var depth = 0;
        var start = k;
        for (; k <= to; k++)
        {
            var end = k == to || depth == 0 && _t[k].Is("FROM");
            if (!end)
            {
                if (_t[k].IsSymbol("(")) depth++;
                else if (_t[k].IsSymbol(")")) depth--;
                else if (_t[k].IsSymbol(",") && depth == 0) { names.Add(ItemName(start, k)); start = k + 1; }
                continue;
            }
            if (k > start) names.Add(ItemName(start, k));
            break;
        }
        return names.Where(n => n != "*").ToList();
    }

    private string ItemName(int from, int to)
    {
        if (to - from >= 2 && _t[to - 2].Is("AS") && _t[to - 1].IsName) return _t[to - 1].Text;
        if (to - from >= 2 && _t[to - 1].IsName && (_t[to - 2].IsName || _t[to - 2].IsSymbol(")")) && !_t[to - 2].IsSymbol(".")) return _t[to - 1].Text;
        if (_t[to - 1].IsSymbol("*")) return "*";
        return _t[to - 1].IsName ? _t[to - 1].Text : "";
    }

    private void Index(bool unique, string? method)
    {
        Accept("CONCURRENTLY"); Accept("IF"); Accept("NOT"); Accept("EXISTS");
        var name = Peek().IsName && !Peek().Is("ON") ? QualifiedName().Name : "";
        Expect("ON");
        Accept("ONLY");
        var (schema, tableName) = QualifiedName();
        if (Accept("USING")) method = Next().Text.ToLowerInvariant();
        var ix = new DbIndexDef { Name = name.Length > 0 ? name : $"ix_{tableName}", Unique = unique, Method = method };
        ix.Columns.AddRange(ColumnList());
        while (_p < _end)
        {
            if (Peek().Is("WHERE")) { _p++; ix.Where = SqlTokenizer.Text(_t, _p, _end); break; }
            if (Peek().Is("USING") && _dialect == "mysql") { _p++; ix.Method = Next().Text.ToLowerInvariant(); continue; }
            _p++;
        }
        var table = _catalog.FindSchema(schema)?.FindTable(tableName) ?? _catalog.FindTable(null, tableName);
        if (table == null) { Warn(_p, $"index {ix.Name}: table {tableName} is not defined in the script"); table = _catalog.Schema(schema).Table(tableName); }
        table.Indexes.RemoveAll(x => DbCatalog.Eq(x.Name, ix.Name));
        table.Indexes.Add(ix);
    }

    private void Sequence()
    {
        Accept("IF"); Accept("NOT"); Accept("EXISTS");
        var (schema, name) = QualifiedName();
        var seq = new DbSequenceDef { Name = name };
        while (_p < _end)
        {
            var t = Next();
            if (t.Is("AS")) seq.Type = DataType();
            else if (t.Is("START")) { Accept("WITH"); seq.Start = SignedNumber(); }
            else if (t.Is("INCREMENT")) { Accept("BY"); seq.Increment = SignedNumber(); }
            else if (t.Is("MINVALUE")) seq.Min = SignedNumber();
            else if (t.Is("MAXVALUE")) seq.Max = SignedNumber();
            else if (t.Is("CYCLE")) seq.Cycle = true;
            else if (t.Is("NO") || t.Is("NOCYCLE")) { if (!t.Is("NOCYCLE")) _p++; }
        }
        var s = _catalog.Schema(schema);
        s.Sequences.RemoveAll(x => DbCatalog.Eq(x.Name, name));
        s.Sequences.Add(seq);
    }

    private string SignedNumber()
    {
        var sign = Accept("-") ? "-" : "";
        return sign + (_p < _end ? Next().Text : "");
    }

    private void TypeDef()
    {
        var (schema, name) = QualifiedName();
        if (Accept("AS"))
        {
            if (Accept("ENUM"))
            {
                var td = new DbTypeDef { Name = name, Kind = "enum" };
                if (Accept("("))
                    while (_p < _end && !Peek().IsSymbol(")"))
                    {
                        var t = Next();
                        if (t.Kind == SqlTokenKind.String) td.Values.Add(t.Text);
                    }
                Replace(_catalog.Schema(schema).Types, td);
                return;
            }
            Warn(_p, $"type {name}: composite and range types are not modelled; skipped");
            return;
        }
        if (Accept("FROM"))   // SQL Server alias type: CREATE TYPE x FROM varchar(20) NOT NULL
        {
            var td = new DbTypeDef { Name = name, Kind = "domain", BaseType = DataType() };
            if (Peek().Is("NOT") && Ahead(1).Is("NULL")) td.NotNull = true;
            Replace(_catalog.Schema(schema).Types, td);
            return;
        }
        Warn(_p, $"type {name}: not understood; skipped");
    }

    private void Domain()
    {
        var (schema, name) = QualifiedName();
        Accept("AS");
        var td = new DbTypeDef { Name = name, Kind = "domain", BaseType = DataType() };
        while (_p < _end)
        {
            if (Peek().Is("CHECK")) { _p++; td.Check = Parenthesised(); continue; }
            if (Peek().Is("NOT") && Ahead(1).Is("NULL")) { td.NotNull = true; _p += 2; continue; }
            _p++;
        }
        Replace(_catalog.Schema(schema).Types, td);
    }

    private static void Replace(List<DbTypeDef> list, DbTypeDef td)
    {
        list.RemoveAll(x => DbCatalog.Eq(x.Name, td.Name));
        list.Add(td);
    }

    /// <summary>MySQL routine characteristics (between the parameters / RETURNS and the body).</summary>
    private static readonly HashSet<string> MySqlCharacteristics = new(StringComparer.OrdinalIgnoreCase)
    {
        "COMMENT", "LANGUAGE", "NOT", "DETERMINISTIC", "CONTAINS", "NO", "READS", "MODIFIES", "SQL", "SECURITY", "DEFINER", "INVOKER", "DATA",
    };

    /// <summary>
    /// CREATE FUNCTION / PROCEDURE: the signature, return type and language, the characteristics written around the body
    /// (<see cref="DbRoutineDef.Options"/>) and the body itself, verbatim (<see cref="DbRoutineDef.Body"/>): PostgreSQL's
    /// dollar-quoted (or quoted) text or SQL-standard BEGIN ATOMIC / RETURN body, everything after AS in T-SQL, after IS / AS in
    /// Oracle, and from the first statement (usually BEGIN) in MySQL.
    /// </summary>
    private void Routine(string kind)
    {
        Accept("IF"); Accept("NOT"); Accept("EXISTS");
        var (schema, name) = QualifiedName();
        var r = new DbRoutineDef { Name = name, Kind = kind };
        if (Peek().IsSymbol("("))
        {
            var close = Close(_p);
            r.Signature = "(" + OneLine(Verbatim(_p + 1, close)) + ")";
            _p = close + 1;
        }
        else
        {
            // T-SQL: parameters without parentheses, up to AS / WITH.
            var from = _p;
            while (_p < _end && !Peek().Is("AS") && !Peek().Is("WITH") && !Peek().Is("BEGIN") && !Peek().Is("RETURNS") && !Peek().Is("FOR")
                   && !(_dialect == "oracle" && (Peek().Is("IS") || Peek().Is("RETURN") || Peek().Is("AUTHID")))) _p++;
            r.Signature = "(" + OneLine(Verbatim(from, _p)) + ")";
        }
        var options = new List<string>();
        while (_p < _end)
        {
            var t = Peek();
            if (t.Is("RETURNS") && !(r.Returns != null && Ahead(1).Is("NULL")) || t.Is("RETURN") && kind == DbFields.Function && r.Returns == null && _dialect != "postgresql")
            {
                _p++;
                if (Peek().Is("TABLE") && Ahead(1).IsSymbol("(")) { _p++; var close = Close(_p); r.Returns = "TABLE(" + SqlTokenizer.Text(_t, _p + 1, close) + ")"; _p = close + 1; }
                else if (Peek().Kind == SqlTokenKind.Word && Peek().Text.StartsWith('@') && Ahead(1).Is("TABLE"))
                {
                    // T-SQL multi-statement table function: RETURNS @t TABLE (…)
                    var from = _p;
                    _p += 2;
                    if (Peek().IsSymbol("(")) _p = Close(_p) + 1;
                    r.Returns = SqlTokenizer.Text(_t, from, _p);
                }
                else r.Returns = DataType();
                continue;
            }
            if (t.Is("LANGUAGE") && Ahead(1).IsName)
            {
                _p++;
                r.Language = Next().Text.ToLowerInvariant();
                continue;
            }
            if (_dialect == "postgresql" && t.Is("AS") && Ahead(1).Kind == SqlTokenKind.String)
            {
                _p++;
                r.Body = Next().Text;
                if (Accept(",") && Peek().Kind == SqlTokenKind.String) _p++;   // AS 'obj_file', 'link_symbol'
                continue;
            }
            if (_dialect == "mysql" && (t.Is("CHARSET") || t.Is("COLLATE") || t.Is("CHARACTER") && Ahead(1).Is("SET")))
            {
                var n = t.Is("CHARACTER") ? 3 : 2;   // RETURNS varchar(20) CHARSET utf8mb4
                options.Add(SqlTokenizer.Text(_t, _p, _p + n));
                _p += n;
                continue;
            }
            if (_dialect == "sqlserver" && t.Is("EXECUTE") && Ahead(1).Is("AS"))
            {
                // WITH EXECUTE AS OWNER | CALLER | SELF | 'user'
                options.Add(SqlTokenizer.Text(_t, _p, _p + 3));
                _p += 3;
                continue;
            }
            if (t.Is("AS") || t.Is("IS"))
            {
                r.Body = Verbatim(_p + 1, _end);
                break;
            }
            var bodyStarts = t.Is("BEGIN") || _dialect == "postgresql" && t.Is("RETURN") && r.Returns != null
                             || _dialect == "mysql" && t.Kind == SqlTokenKind.Word && !MySqlCharacteristics.Contains(t.Text) && !t.Is("RETURNS");
            if (bodyStarts)
            {
                r.Body = Verbatim(_p, _end);
                break;
            }
            // A characteristic / attribute: keep it as written.
            var start = _p;
            _p++;
            if (Peek().IsSymbol("(")) _p = Close(_p) + 1;
            else if (_dialect == "mysql" && t.Is("COMMENT") && Peek().Kind == SqlTokenKind.String) _p++;
            options.Add(SqlTokenizer.Text(_t, start, _p));
        }
        // Option words separated by commas (T-SQL WITH a, b) read better without the leading WITH's own comma noise.
        var opts = string.Join(" ", options).Replace(" ,", ",").Trim();
        r.Options = opts.Length == 0 ? null : opts;
        if (_dialect == "mysql" && r.Options is { } mo && Regex.Match(mo, @"\bCOMMENT\s+'((?:[^']|'')*)'", RegexOptions.IgnoreCase) is { Success: true } cm)
            r.Comment = cm.Groups[1].Value.Replace("''", "'");
        var s = _catalog.Schema(schema);
        s.Routines.RemoveAll(x => DbCatalog.Eq(x.Name, name));
        s.Routines.Add(r);
    }

    /// <summary>
    /// CREATE TRIGGER for every dialect: timing, events, table and level from the header, and the action verbatim
    /// (<see cref="DbTriggerDef.Action"/>): from WHEN / EXECUTE in PostgreSQL, after AS in T-SQL, the statement after FOR EACH
    /// ROW in MySQL, from WHEN / DECLARE / BEGIN in Oracle and SQLite.
    /// </summary>
    private void Trigger()
    {
        Accept("IF"); Accept("NOT"); Accept("EXISTS");
        var (_, name) = QualifiedName();
        var tg = new DbTriggerDef { Name = name };
        string? tableSchema = null, tableName = null;
        var events = new List<string>();
        while (_p < _end)
        {
            var t = Peek();
            // MySQL: after FOR EACH ROW [FOLLOWS | PRECEDES other] comes the body, whatever statement it starts with.
            if (_dialect == "mysql" && tg.Level != null && tableName != null && !t.Is("FOLLOWS") && !t.Is("PRECEDES")) { tg.Action = Verbatim(_p, _end); break; }
            if (t.Is("BEFORE") || t.Is("AFTER")) { _p++; tg.Timing = t.Text.ToUpperInvariant(); }
            else if (t.Is("INSTEAD") && Ahead(1).Is("OF")) { _p += 2; tg.Timing = "INSTEAD OF"; }
            else if (t.Is("FOR") && Ahead(1).Is("EACH")) { _p += 2; tg.Level = Next().Text.ToUpperInvariant(); }
            else if (t.Is("FOR") && (Ahead(1).Is("ROW") || Ahead(1).Is("STATEMENT"))) { _p++; tg.Level = Next().Text.ToUpperInvariant(); }
            else if (t.Is("FOR") && _dialect == "sqlserver") { _p++; tg.Timing = "AFTER"; }   // T-SQL FOR = AFTER
            else if (t.Is("INSERT") || t.Is("UPDATE") || t.Is("DELETE") || t.Is("TRUNCATE"))
            {
                _p++;
                events.Add(t.Text.ToUpperInvariant());
                if (Peek().Is("OF")) { _p++; while (Peek().IsName && !Peek().Is("ON") && !Peek().Is("OR")) { _p++; Accept(","); } }
            }
            else if (t.Is("OR") || t.IsSymbol(",")) _p++;
            else if (t.Is("ON") && tableName == null) { _p++; (tableSchema, tableName) = QualifiedName(); }
            else if (t.Is("REFERENCING"))
            {
                Warn(_p, $"trigger {name}: the REFERENCING clause is not kept");
                _p++;
                while (_p < _end && !Peek().Is("FOR") && !Peek().Is("WHEN") && !Peek().Is("BEGIN") && !Peek().Is("DECLARE") && !Peek().Is("EXECUTE")) _p++;
            }
            else if (t.Is("FOLLOWS") || t.Is("PRECEDES")) { _p++; NameParts(); }
            else if (t.Is("ENABLE") || t.Is("DISABLE") || t.Is("DEFERRABLE") || t.Is("ROW") && tg.Level == null) _p++;
            else if (t.Is("NOT") && (Ahead(1).Is("DEFERRABLE") || Ahead(1).Is("FOR"))) _p += Ahead(1).Is("FOR") ? 3 : 2;   // NOT DEFERRABLE, NOT FOR REPLICATION
            else if (t.Is("INITIALLY") || t.Is("FROM")) { _p++; NameParts(); }
            else if (t.Is("WITH") && _dialect == "sqlserver")
            {
                // WITH ENCRYPTION | EXECUTE AS … | APPEND, up to the timing or AS
                _p++;
                while (_p < _end && !Peek().Is("FOR") && !Peek().Is("AFTER") && !Peek().Is("INSTEAD") && !(Peek().Is("AS") && !_t[_p - 1].Is("EXECUTE"))) _p++;
            }
            else if (t.Is("AS") && _dialect != "postgresql") { tg.Action = Verbatim(_p + 1, _end); break; }
            else if (t.Is("WHEN") || t.Is("EXECUTE") || t.Is("BEGIN") || t.Is("DECLARE") || t.Is("CALL") || t.Is("COMPOUND"))
            {
                tg.Action = Verbatim(_p, _end);
                break;
            }
            else _p++;
        }
        if (events.Count > 0) tg.Events = string.Join(", ", events);
        if (tableName == null) { Warn(_p, $"trigger {name}: no table; skipped"); return; }
        var table = _catalog.FindSchema(tableSchema ?? _currentSchema)?.FindTable(tableName) ?? _catalog.FindTable(null, tableName);
        if (table == null)
        {
            _looseTriggers.Add((tableSchema, tableName, tg));
            Warn(_p, $"trigger {name}: table {tableName} is not defined in the script; skipped");
            return;
        }
        table.Triggers.RemoveAll(x => DbCatalog.Eq(x.Name, name));
        table.Triggers.Add(tg);
    }

    // ================================================================== ALTER, COMMENT

    private void Alter()
    {
        if (!Accept("TABLE"))
        {
            if (Peek().Is("SESSION") && _dialect == "oracle")
            {
                for (var k = _p; k + 2 < _end; k++) if (_t[k].Is("CURRENT_SCHEMA")) { _currentSchema = _t[k + 2].Text; _catalog.Schema(_currentSchema); }
                return;
            }
            if (Peek().Is("SEQUENCE") || Peek().Is("INDEX") || Peek().Is("FUNCTION") || Peek().Is("VIEW") || Peek().Is("SCHEMA") || Peek().Is("TYPE") || Peek().Is("DATABASE") || Peek().Is("ROLE") || Peek().Is("USER") || Peek().Is("PROCEDURE"))
                return;   // ownership, options: nothing to model
            Warn(_p - 1, $"skipped: ALTER {Peek().Text} …");
            return;
        }
        Accept("ONLY"); Accept("IF"); Accept("EXISTS"); Accept("ONLY");
        var (schema, name) = QualifiedName();
        var table = _catalog.FindSchema(schema)?.FindTable(name) ?? _catalog.FindTable(null, name);
        if (table == null) { Warn(_p, $"ALTER TABLE {name}: the table is not defined in the script; skipped"); return; }
        var tableSchema = _catalog.Schemas.First(s => s.Tables.Contains(table)).Name;
        // Several actions separated by commas.
        var actions = new List<(int, int)>();
        var depth = 0;
        var start = _p;
        for (var k = _p; k < _end; k++)
        {
            if (_t[k].IsSymbol("(")) depth++;
            else if (_t[k].IsSymbol(")")) depth--;
            else if (_t[k].IsSymbol(",") && depth == 0 && k + 1 < _end && (_t[k + 1].Is("ADD") || _t[k + 1].Is("DROP") || _t[k + 1].Is("ALTER") || _t[k + 1].Is("MODIFY") || _t[k + 1].Is("RENAME")))
            { actions.Add((start, k)); start = k + 1; }
        }
        actions.Add((start, _end));
        var save = _end;
        foreach (var (from, to) in actions)
        {
            _p = from;
            _end = to;
            AlterAction(table, tableSchema);
            _end = save;
        }
    }

    private void AlterAction(DbTableDef table, string schema)
    {
        if ((Peek().Is("CHECK") || Peek().Is("NOCHECK")) && Ahead(1).Is("CONSTRAINT")) return;   // T-SQL: (re)enable a constraint
        SkipWords("WITH", "NOCHECK", "CHECK");
        if (Accept("ADD"))
        {
            if (Peek().IsSymbol("("))   // Oracle: ADD (col type, …)
            {
                _p++;
                foreach (var (from, to) in Elements(_p)) { _p = from; TableElement(table, schema, to); }
                return;
            }
            Accept("COLUMN");
            Accept("IF"); Accept("NOT"); Accept("EXISTS");
            // T-SQL default constraint: ADD CONSTRAINT DF_x DEFAULT (0) FOR col
            if (Peek().Is("CONSTRAINT") && Ahead(2).Is("DEFAULT"))
            {
                var dfName = Ahead(1).Text;
                _p += 3;
                var expr = Expression();
                if (Accept("FOR") && table.FindColumn(Next().Text) is { } col) { col.Default = expr.Trim(); col.DefaultName = dfName; }
                return;
            }
            TableElement(table, schema, _end);
            return;
        }
        if (Accept("ALTER") || Accept("MODIFY"))
        {
            Accept("COLUMN");
            if (Peek().IsSymbol("(")) { _p++; }
            var col = table.FindColumn(Next().Text);
            if (col == null) return;
            if (Accept("SET"))
            {
                if (Peek().Is("NOT") && Ahead(1).Is("NULL")) col.Nullable = false;
                else if (Accept("DEFAULT")) col.Default = Expression();
                else if (Accept("DATA") && Accept("TYPE")) col.Type = DataType();
                return;
            }
            if (Accept("DROP"))
            {
                if (Peek().Is("NOT")) col.Nullable = true;
                else if (Peek().Is("DEFAULT")) col.Default = null;
                return;
            }
            Accept("TYPE");
            if (Peek().IsName && !Peek().Is("NOT") && !Peek().Is("NULL")) col.Type = DataType();
            if (Peek().Is("NOT") && Ahead(1).Is("NULL")) col.Nullable = false;
            else if (Peek().Is("NULL")) col.Nullable = true;
            return;
        }
        if (Accept("DROP"))
        {
            if (Accept("CONSTRAINT") || Accept("FOREIGN") && Accept("KEY") || Accept("INDEX") || Accept("KEY"))
            {
                Accept("IF"); Accept("EXISTS");
                var n = Next().Text;
                table.ForeignKeys.RemoveAll(f => DbCatalog.Eq(f.Name, n));
                table.Constraints.RemoveAll(c => DbCatalog.Eq(c.Name, n));
                table.Indexes.RemoveAll(c => DbCatalog.Eq(c.Name, n));
                if (DbCatalog.Eq(table.PrimaryKeyName, n)) { table.PrimaryKey.Clear(); table.PrimaryKeyName = null; }
                return;
            }
            if (Peek().Is("PRIMARY")) { table.PrimaryKey.Clear(); table.PrimaryKeyName = null; return; }
            Accept("COLUMN"); Accept("IF"); Accept("EXISTS");
            var c = Next().Text;
            table.Columns.RemoveAll(x => DbCatalog.Eq(x.Name, c));
            table.PrimaryKey.RemoveAll(x => DbCatalog.Eq(x, c));
            return;
        }
        if (Accept("RENAME"))
        {
            if (Accept("COLUMN") || Peek().IsName && Ahead(1).Is("TO"))
            {
                var from = Next().Text;
                Accept("TO");
                var to = Next().Text;
                if (table.FindColumn(from) is { } col) col.Name = to;
                for (var i = 0; i < table.PrimaryKey.Count; i++) if (DbCatalog.Eq(table.PrimaryKey[i], from)) table.PrimaryKey[i] = to;
            }
            return;
        }
        if (Peek().Is("OWNER") || Peek().Is("ENABLE") || Peek().Is("DISABLE") || Peek().Is("SET") || Peek().Is("CHECK") || Peek().Is("NOCHECK") || Peek().Is("CLUSTER") || Peek().Is("REPLICA") || Peek().Is("FORCE") || Peek().Is("INHERIT") || Peek().Is("ATTACH")) return;
        Warn(_p, $"ALTER TABLE {table.Name}: skipped {Snippet(_p)}");
    }

    private void CommentOn()
    {
        Expect("ON");
        var kind = Next();
        if (kind.Is("MATERIALIZED")) Accept("VIEW");
        if (kind.Is("COLUMN"))
        {
            var parts = NameParts();
            if (parts.Count < 2) return;
            var colName = parts[^1];
            var tableName = parts[^2];
            var schema = parts.Count >= 3 ? parts[^3] : _currentSchema;
            var text = CommentText();
            if ((_catalog.FindSchema(schema)?.FindTable(tableName) ?? _catalog.FindTable(null, tableName))?.FindColumn(colName) is { } col) col.Comment = text;
            else Warn(_p, $"COMMENT ON COLUMN {tableName}.{colName}: unknown column");
            return;
        }
        if (kind.Is("TABLE") || kind.Is("VIEW") || kind.Is("MATERIALIZED"))
        {
            var (schema, name) = QualifiedName();
            var text = CommentText();
            if ((_catalog.FindSchema(schema)?.FindTable(name) ?? _catalog.FindTable(null, name)) is { } t) t.Comment = text;
            return;
        }
        if (kind.Is("SCHEMA"))
        {
            var n = Next().Text;
            _catalog.Schema(n).Comment = CommentText();
            return;
        }
        if (kind.Is("TYPE") || kind.Is("DOMAIN"))
        {
            var (schema, name) = QualifiedName();
            var text = CommentText();
            if (_catalog.FindSchema(schema)?.Types.FirstOrDefault(x => DbCatalog.Eq(x.Name, name)) is { } td) td.Comment = text;
            return;
        }
        if (kind.Is("FUNCTION") || kind.Is("PROCEDURE"))
        {
            var (schema, name) = QualifiedName();
            if (Peek().IsSymbol("(")) _p = Close(_p) + 1;
            var text = CommentText();
            if (_catalog.FindSchema(schema)?.Routines.FirstOrDefault(x => DbCatalog.Eq(x.Name, name)) is { } r) r.Comment = text;
        }
        // Comments on indexes, constraints, sequences … are not kept.
    }

    private string? CommentText()
    {
        while (_p < _end && !Peek().Is("IS")) _p++;
        Accept("IS");
        return Peek().Kind == SqlTokenKind.String ? Next().Text : null;
    }

    /// <summary>EXEC sp_addextendedproperty 'MS_Description', 'text', 'SCHEMA', 'dbo', 'TABLE', 't'[, 'COLUMN', 'c'] (positional or @name = value).</summary>
    private void ExtendedProperty()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<string>();
        string[] names = ["@name", "@value", "@level0type", "@level0name", "@level1type", "@level1name", "@level2type", "@level2name"];
        for (var k = _p; k < _end; k++)
        {
            var t = _t[k];
            if (t.Kind == SqlTokenKind.Word && t.Text.StartsWith('@') && k + 2 < _end && _t[k + 1].IsSymbol("=")) { values[t.Text] = _t[k + 2].Text; k += 2; continue; }
            if (t.Kind is SqlTokenKind.String) positional.Add(t.Text);
        }
        for (var i = 0; i < positional.Count && i < names.Length; i++) values.TryAdd(names[i], positional[i]);
        if (!values.TryGetValue("@name", out var prop) || !prop.Equals("MS_Description", StringComparison.OrdinalIgnoreCase)) return;
        var text = values.GetValueOrDefault("@value");
        var schema = values.GetValueOrDefault("@level0name") ?? _currentSchema;
        var tableName = values.GetValueOrDefault("@level1name");
        if (tableName == null) { if (_catalog.FindSchema(schema) is { } s) s.Comment = text; return; }
        var table = _catalog.FindSchema(schema)?.FindTable(tableName) ?? _catalog.FindTable(null, tableName);
        if (table == null) return;
        if (values.GetValueOrDefault("@level2type")?.Equals("COLUMN", StringComparison.OrdinalIgnoreCase) == true && values.GetValueOrDefault("@level2name") is { } colName)
        {
            if (table.FindColumn(colName) is { } col) col.Comment = text;
        }
        else table.Comment = text;
    }

    // ================================================================== helpers

    private (string Schema, string Name) QualifiedName()
    {
        var parts = NameParts();
        if (parts.Count == 0) throw new FormatException($"a name was expected at line {(_p < _t.Count ? _t[_p].Line : 0)}");
        var name = parts[^1];
        var schema = parts.Count >= 2 ? parts[^2] : _currentSchema;
        if (parts.Count >= 2) _catalog.Schema(schema);
        return (schema, name);
    }

    private List<string> NameParts()
    {
        var parts = new List<string>();
        if (_p >= _end || !Peek().IsName) return parts;
        parts.Add(Next().Text);
        while (_p + 1 < _end && Peek().IsSymbol(".") && Ahead(1).IsName) { _p++; parts.Add(Next().Text); }
        // T-SQL "db..table"
        while (_p + 2 < _end && Peek().IsSymbol(".") && Ahead(1).IsSymbol(".") && Ahead(2).IsName) { _p += 2; parts.Add(Next().Text); }
        return parts;
    }

    private SqlToken Peek() => _p < _end && _p < _t.Count ? _t[_p] : new SqlToken(SqlTokenKind.Symbol, ";", ";", 0);
    private SqlToken Ahead(int k) => _p + k >= 0 && _p + k < _end && _p + k < _t.Count ? _t[_p + k] : new SqlToken(SqlTokenKind.Symbol, ";", ";", 0);
    private SqlToken Next() { var t = Peek(); _p++; return t; }

    private bool Accept(string wordOrSymbol)
    {
        var t = Peek();
        if (t.Is(wordOrSymbol) || t.IsSymbol(wordOrSymbol)) { _p++; return true; }
        return false;
    }

    private void Expect(string word)
    {
        if (!Accept(word)) throw new FormatException($"{word} expected near “{Peek().Raw}”");
    }

    private void SkipWords(params string[] words)
    {
        while (words.Any(w => Peek().Is(w))) _p++;
    }

    /// <summary>Text with its whitespace (line breaks too) collapsed to single spaces.</summary>
    private static string OneLine(string? s) => s == null ? "" : Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>The script text of tokens [from, to) as written (comments and layout inside kept), or null when empty.</summary>
    private string? Verbatim(int from, int to)
    {
        to = Math.Min(to, _t.Count);
        // A trailing ";" ends the statement, except in Oracle where it belongs to the PL/SQL block ("END;").
        while (to > from && (_t[to - 1].Kind == SqlTokenKind.BatchEnd || _t[to - 1].Offset < 0 || _dialect != "oracle" && _t[to - 1].IsSymbol(";"))) to--;
        if (from >= to || _t[from].Offset < 0) return null;
        var text = _src[_t[from].Offset.._t[to - 1].End].Trim();
        return text.Length == 0 ? null : text;
    }

    private string Snippet(int from)
    {
        var text = SqlTokenizer.Text(_t, from, Math.Min(_end, from + 8));
        return text.Length > 60 ? text[..60] + "…" : text + (from + 8 < _end ? " …" : "");
    }

    private void Warn(int at, string message)
    {
        var line = at >= 0 && at < _t.Count ? _t[at].Line : 0;
        _catalog.Warnings.Add(line > 0 ? $"line {line}: {message}" : message);
    }
}
