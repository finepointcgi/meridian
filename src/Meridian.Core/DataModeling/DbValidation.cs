using System.Text.RegularExpressions;
using Meridian.Core.Model;
using Meridian.Core.Validation;

namespace Meridian.Core.DataModeling;

/// <summary>
/// Data-model checks (rule ids DB-001 … DB-008): missing primary keys, foreign key type mismatches and dangling columns,
/// duplicate names, reserved words of the database's dialect, the naming convention, and tables in no subject area.
/// </summary>
public sealed partial class DbValidationRules : IValidationRule
{
    public string Id => "DB";
    public string Description => "Data model: keys, names, reserved words, naming convention, subject areas";

    public IEnumerable<ValidationIssue> Check(ModelRepository m)
    {
        var tables = m.OfKind(ElementKind.Table).ToList();
        if (tables.Count == 0) yield break;
        var anyGroups = DbModel.Groups(m).Count > 0;
        foreach (var t in tables)
        {
            var view = DbModel.IsView(t);
            var cols = DbModel.Columns(t).ToList();
            if (!view && cols.Count > 0 && !cols.Any(DbModel.IsPk))
                yield return new(Severity.Warning, "DB-001", $"Table '{t.Name}' has no primary key", t);
            foreach (var g in cols.Where(c => c.Name.Length > 0).GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                yield return new(Severity.Error, "DB-003", $"Column name '{g.Key}' is used {g.Count()} times in '{t.Name}'", g.Skip(1).First());
            var dialect = DbModel.DialectOf(t);
            if (DbReservedWords.IsReserved(t.Name, dialect))
                yield return new(Severity.Warning, "DB-004", $"Table name '{t.Name}' is a reserved word in {DbReservedWords.DialectName(dialect)} (it must be quoted)", t);
            foreach (var c in cols.Where(c => DbReservedWords.IsReserved(c.Name, dialect)))
                yield return new(Severity.Warning, "DB-004", $"Column name '{t.Name}.{c.Name}' is a reserved word in {DbReservedWords.DialectName(dialect)} (it must be quoted)", c);
            var naming = NamingOf(m, t);
            if (naming.Length > 0)
            {
                if (!Follows(t.Name, naming)) yield return new(Severity.Info, "DB-005", $"Table name '{t.Name}' does not follow the {naming} convention", t);
                foreach (var c in cols.Where(c => !Follows(c.Name, naming)))
                    yield return new(Severity.Info, "DB-005", $"Column name '{t.Name}.{c.Name}' does not follow the {naming} convention", c);
            }
            if (anyGroups && !view && DbModel.GroupIds(t).Count == 0)
                yield return new(Severity.Info, "DB-006", $"Table '{t.Name}' is not in any subject area", t);
            foreach (var fk in m.Outgoing(t, ElementKind.ForeignKey).Cast<Relationship>())
            {
                var names = DbModel.SplitList(fk.Get(FieldKeys.FkColumns));
                var src = names.Select(n => DbModel.Column(t, n)).ToList();
                var missing = names.Where((n, i) => src[i] == null).ToList();
                if (missing.Count > 0) yield return new(Severity.Error, "DB-007", $"Foreign key {Label(fk)} uses column{(missing.Count == 1 ? "" : "s")} {string.Join(", ", missing)} that '{t.Name}' does not have", fk);
                var refNames = DbModel.SplitList(fk.Get(FieldKeys.RefColumns));
                var refs = refNames.Count > 0 ? refNames.Select(n => DbModel.Column(fk.Target, n)).ToList() : DbModel.PrimaryKey(fk.Target).Select(c => (Element?)c).ToList();
                var refMissing = refNames.Where(n => DbModel.Column(fk.Target, n) == null).ToList();
                if (refMissing.Count > 0) yield return new(Severity.Error, "DB-007", $"Foreign key {Label(fk)} references column{(refMissing.Count == 1 ? "" : "s")} {string.Join(", ", refMissing)} that '{fk.Target.Name}' does not have", fk);
                if (src.Count > 0 && refs.Count > 0 && src.Count != refs.Count)
                    yield return new(Severity.Error, "DB-002", $"Foreign key {Label(fk)} has {src.Count} column{(src.Count == 1 ? "" : "s")} but references {refs.Count}", fk);
                for (var i = 0; i < Math.Min(src.Count, refs.Count); i++)
                    if (src[i] is { } a && refs[i] is { } b && !DbTypes.Compatible(a.Get(FieldKeys.SqlType), b.Get(FieldKeys.SqlType)))
                        yield return new(Severity.Warning, "DB-002", $"Foreign key {Label(fk)}: {t.Name}.{a.Name} is {Show(a)} but {fk.Target.Name}.{b.Name} is {Show(b)}", fk);
            }
        }
        // Table names unique per schema regardless of case (MER-002 catches exact duplicates).
        foreach (var g in tables.Where(t => t.Owner != null && t.Name.Length > 0).GroupBy(t => (t.Owner!.Id, t.Name.ToLowerInvariant())).Where(g => g.Count() > 1 && g.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() > 1))
            yield return new(Severity.Error, "DB-003", $"Table names '{string.Join("', '", g.Select(t => t.Name))}' differ only in case", g.Skip(1).First());
        foreach (var t in tables)
            foreach (var g in DbModel.Indexes(t).Concat(DbModel.Constraints(t)).Where(x => x.Name.Length > 0).GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                yield return new(Severity.Error, "DB-003", $"Index / constraint name '{g.Key}' is used {g.Count()} times on '{t.Name}'", g.Skip(1).First());
        foreach (var t in tables)
            foreach (var ix in DbModel.Indexes(t).Concat(DbModel.Constraints(t).Where(c => c.Get(FieldKeys.Subtype) != DbFields.CheckConstraint)))
                foreach (var n in DbModel.SplitList(ix.Get(DbFields.Columns)).Where(n => IsPlainName(n) && DbModel.Column(t, n) == null))
                    yield return new(Severity.Error, "DB-008", $"{(ix.Kind == ElementKind.DbIndex ? "Index" : "Constraint")} '{ix.Name}' uses column '{n}' that '{t.Name}' does not have", ix);
    }

    private static string Label(Relationship fk) => fk.Name.Length > 0 ? $"'{fk.Name}'" : $"{fk.Source.Name} → {fk.Target.Name}";
    private static string Show(Element c) => c.Get(FieldKeys.SqlType) is { Length: > 0 } t ? t : "untyped";

    /// <summary>The naming convention for a table: its database's, else the model root's.</summary>
    public static string NamingOf(ModelRepository m, Element e) =>
        DbModel.DatabaseOf(e)?.Get(DbFields.Naming) is { Length: > 0 } n ? n : m.Root.Get(DbFields.Naming);

    [GeneratedRegex(@"^[a-z][a-z0-9]*(_[a-z0-9]+)*$")] private static partial Regex Snake();
    [GeneratedRegex(@"^[A-Z][a-zA-Z0-9]*$")] private static partial Regex Pascal();
    [GeneratedRegex(@"^[a-z][a-zA-Z0-9]*$")] private static partial Regex Camel();
    [GeneratedRegex(@"^[A-Za-z_][\w$]*$")] private static partial Regex PlainName();

    private static bool IsPlainName(string s) => PlainName().IsMatch(s);

    public static bool Follows(string name, string convention) => convention switch
    {
        "snake_case" => Snake().IsMatch(name),
        "PascalCase" => Pascal().IsMatch(name),
        "camelCase" => Camel().IsMatch(name),
        _ => true,
    };
}

/// <summary>Reserved words per dialect (the common core of each list; enough to catch names that must be quoted).</summary>
public static class DbReservedWords
{
    private static readonly string[] Core =
    [
        "all", "alter", "and", "any", "as", "asc", "between", "by", "case", "check", "column", "constraint", "create", "cross", "current_date",
        "current_time", "current_timestamp", "current_user", "default", "delete", "desc", "distinct", "drop", "else", "end", "exists", "foreign",
        "from", "full", "grant", "group", "having", "in", "inner", "insert", "intersect", "into", "is", "join", "left", "like", "not", "null", "on",
        "or", "order", "outer", "primary", "references", "right", "select", "set", "table", "then", "to", "union", "unique", "update", "user",
        "using", "values", "when", "where", "with",
    ];

    private static readonly Dictionary<string, HashSet<string>> ByDialect = new()
    {
        ["postgresql"] = Set(Core, "analyse", "analyze", "array", "asymmetric", "both", "cast", "collate", "current_role", "deferrable", "do", "except",
            "false", "fetch", "for", "initially", "lateral", "leading", "limit", "localtime", "localtimestamp", "offset", "only", "placing",
            "returning", "session_user", "some", "symmetric", "trailing", "true", "variadic", "window"),
        ["sqlserver"] = Set(Core, "backup", "begin", "break", "browse", "bulk", "cascade", "checkpoint", "close", "clustered", "commit", "compute",
            "contains", "continue", "convert", "cursor", "database", "dbcc", "deallocate", "declare", "deny", "disk", "distributed", "double",
            "dump", "errlvl", "escape", "except", "exec", "execute", "exit", "external", "fetch", "file", "fillfactor", "for", "freetext", "function",
            "goto", "holdlock", "identity", "if", "index", "key", "kill", "load", "merge", "national", "nocheck", "nonclustered", "of", "off",
            "offsets", "open", "option", "over", "percent", "pivot", "plan", "print", "proc", "procedure", "public", "raiserror", "read",
            "reconfigure", "replication", "restore", "restrict", "return", "revert", "revoke", "rollback", "rowcount", "rule", "save", "schema",
            "session_user", "shutdown", "some", "statistics", "system_user", "top", "tran", "transaction", "trigger", "truncate", "tsequal",
            "unpivot", "updatetext", "use", "view", "waitfor", "while", "writetext"),
        ["mysql"] = Set(Core, "accessible", "add", "before", "both", "call", "cascade", "change", "condition", "continue", "convert", "cursor",
            "database", "databases", "declare", "delayed", "describe", "div", "double", "each", "enclosed", "escaped", "exit", "explain", "false",
            "fetch", "float", "for", "force", "fulltext", "generated", "if", "ignore", "index", "infile", "int", "integer", "interval", "key",
            "keys", "kill", "leading", "leave", "limit", "lines", "load", "lock", "long", "loop", "match", "mod", "natural", "option", "out",
            "partition", "precision", "procedure", "range", "read", "real", "regexp", "release", "rename", "repeat", "replace", "require",
            "restrict", "return", "revoke", "rlike", "schema", "show", "signal", "spatial", "sql", "starting", "terminated", "trailing",
            "trigger", "true", "undo", "unlock", "unsigned", "usage", "use", "varchar", "while", "write", "xor", "year_month", "zerofill", "rank", "row", "rows"),
        ["sqlite"] = Set(Core, "abort", "action", "add", "after", "analyze", "attach", "autoincrement", "before", "begin", "cascade", "collate",
            "commit", "conflict", "database", "deferrable", "deferred", "detach", "each", "escape", "except", "exclusive", "explain", "fail",
            "for", "glob", "if", "ignore", "immediate", "index", "indexed", "initially", "instead", "isnull", "key", "limit", "match", "natural",
            "no", "notnull", "of", "offset", "plan", "pragma", "query", "raise", "recursive", "regexp", "reindex", "release", "rename", "replace",
            "restrict", "rollback", "row", "savepoint", "temp", "temporary", "transaction", "trigger", "vacuum", "view", "virtual"),
        ["oracle"] = Set(Core, "access", "add", "audit", "char", "cluster", "comment", "compress", "connect", "date", "decimal", "exclusive", "file",
            "float", "identified", "immediate", "increment", "index", "initial", "integer", "level", "lock", "long", "maxextents", "minus",
            "mlslabel", "mode", "modify", "noaudit", "nocompress", "nowait", "number", "of", "offline", "online", "option", "pctfree", "prior",
            "privileges", "public", "raw", "rename", "resource", "revoke", "row", "rowid", "rownum", "rows", "session", "share", "size",
            "smallint", "start", "successful", "synonym", "sysdate", "trigger", "uid", "validate", "varchar", "varchar2", "view", "whenever"),
    };

    private static HashSet<string> Set(string[] core, params string[] extra) => new(core.Concat(extra), StringComparer.OrdinalIgnoreCase);

    public static bool IsReserved(string name, string dialect) =>
        name.Length > 0 && (ByDialect.TryGetValue(dialect, out var set) ? set : ByDialect["postgresql"]).Contains(name);

    public static string DialectName(string dialect) => dialect switch
    {
        "sqlserver" => "SQL Server",
        "mysql" => "MySQL",
        "sqlite" => "SQLite",
        "oracle" => "Oracle",
        _ => "PostgreSQL",
    };
}

/// <summary>Keeps foreign keys, indexes and constraints pointing at a column when it is renamed (same undo step as the rename).</summary>
public static class DbRename
{
    public static bool Watches(Element e, string key, string? oldValue, string? newValue) =>
        key == FieldKeys.Name && e.Kind == ElementKind.Column && e.Owner?.Kind == ElementKind.Table
        && !string.IsNullOrEmpty(oldValue) && !string.IsNullOrEmpty(newValue) && !string.Equals(oldValue, newValue, StringComparison.Ordinal);

    public static void Propagate(ModelRepository m, Element column, string oldName, string newName)
    {
        var table = column.Owner!;
        string Replace(string list) => DbModel.JoinList(DbModel.SplitList(list).Select(n => string.Equals(n, oldName, StringComparison.OrdinalIgnoreCase) ? newName : n));
        void Update(Element e, string key)
        {
            var v = e.Get(key);
            if (v.Length == 0) return;
            var nv = Replace(v);
            if (!string.Equals(DbModel.JoinList(DbModel.SplitList(v)), nv, StringComparison.Ordinal)) m.Set(e, key, nv);
        }
        foreach (var fk in m.Outgoing(table, ElementKind.ForeignKey)) Update(fk, FieldKeys.FkColumns);
        foreach (var fk in m.Incoming(table, ElementKind.ForeignKey)) Update(fk, FieldKeys.RefColumns);
        foreach (var x in DbModel.Indexes(table).Concat(DbModel.Constraints(table))) Update(x, DbFields.Columns);
    }
}
