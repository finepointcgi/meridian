using System.Text.RegularExpressions;
using Meridian.Core.Analysis;

namespace Meridian.Core.DataModeling;

public enum DbChangeKind { Added, Changed, Removed, Renamed }

/// <summary>
/// A rename to apply when comparing two catalogs: <paramref name="OldName"/> in the before catalog is
/// <paramref name="NewName"/> in the after catalog. <paramref name="Table"/> (columns only) is the table's name in the after
/// catalog. Model versions find renames by element id; for live databases and scripts <see cref="DbDiff.SuggestRenames"/>
/// proposes them and the user confirms. <paramref name="Score"/> is the suggestion's confidence (0–1).
/// </summary>
public sealed record DbRenameHint(DbObjectType Type, string Schema, string? Table, string OldName, string NewName, double Score = 1)
{
    public string Key => $"{Type}:{Schema}/{Table}/{OldName}->{NewName}".ToLowerInvariant();

    public string Describe() => Type == DbObjectType.Column
        ? $"column {Table}.{OldName} → {NewName}"
        : $"{(Type == DbObjectType.View ? "view" : "table")} {(Schema.Length > 0 ? Schema + "." : "")}{OldName} → {NewName}";
}

public enum DbObjectType { Schema, Table, View, Column, PrimaryKey, ForeignKey, Index, Constraint, Trigger, Sequence, Type, Routine }

/// <summary>
/// One difference between two catalogs (before → after): a refresh review item, or a step of a migration script.
/// <see cref="Before"/> and <see cref="After"/> hold the definitions (null when added / removed).
/// </summary>
public sealed record DbChange(DbChangeKind Kind, DbObjectType Type, string Schema, string? Table, string Name, string Path,
    IReadOnlyList<FieldChange> Fields, object? Before, object? After)
{
    /// <summary>Stable id for selections in the review.</summary>
    public string Id => $"{Kind}:{Path}";

    public string Label => Type switch
    {
        DbObjectType.Schema => $"schema {Name}",
        DbObjectType.Table or DbObjectType.View or DbObjectType.Sequence or DbObjectType.Type or DbObjectType.Routine => $"{TypeName.ToLowerInvariant()} {Qualified(Schema, Name)}",
        DbObjectType.PrimaryKey => $"primary key of {Qualified(Schema, Table!)}",
        _ => $"{TypeName.ToLowerInvariant()} {Qualified(Schema, Table!)}.{Name}",
    };

    public string TypeName => Type switch
    {
        DbObjectType.PrimaryKey => "Primary key",
        DbObjectType.ForeignKey => "Foreign key",
        _ => Type.ToString(),
    };

    /// <summary>A change that can lose data when run against a database (drops, type changes, NOT NULL on existing rows).</summary>
    public bool Destructive => Kind == DbChangeKind.Removed && Type is DbObjectType.Table or DbObjectType.Column or DbObjectType.Schema or DbObjectType.Sequence or DbObjectType.Type
        || Kind == DbChangeKind.Changed && Type == DbObjectType.Column && Fields.Any(f => f.Key == "type" || f.Key == "nullable" && f.After == "false");

    public string Describe() => Kind switch
    {
        DbChangeKind.Added => $"Added {Label}",
        DbChangeKind.Removed => $"Removed {Label}",
        DbChangeKind.Renamed => $"Renamed {Label} (was {Fields.FirstOrDefault(f => f.Key == "name")?.Before})",
        _ => $"Changed {Label}: " + string.Join("; ", Fields.Select(f => $"{f.Key} {Show(f.Before)} → {Show(f.After)}")),
    };

    private static string Show(string? s) => string.IsNullOrEmpty(s) ? "(none)" : s.Length > 60 ? s[..57] + "…" : s;
    private static string Qualified(string schema, string name) => schema.Length > 0 ? $"{schema}.{name}" : name;
}

/// <summary>Compares two catalogs object by object (names compared without case; types and defaults compared in a canonical form).</summary>
public static partial class DbDiff
{
    /// <summary>
    /// Differences from <paramref name="before"/> to <paramref name="after"/>. Tables and columns with the same element id
    /// (<see cref="DbTableDef.Id"/>, <see cref="DbColumnDef.Id"/>) and another name are renames; <paramref name="renames"/>
    /// adds confirmed renames for catalogs without ids (live databases, scripts). Keys, indexes and constraints that only
    /// follow a renamed column or table are not reported as changed (the database renames them along).
    /// </summary>
    public static IReadOnlyList<DbChange> Compare(DbCatalog before, DbCatalog after, IEnumerable<DbRenameHint>? renames = null)
    {
        var hints = (renames ?? []).ToList();
        var list = new List<DbChange>();
        var schemas = before.Schemas.Select(s => s.Name).Concat(after.Schemas.Select(s => s.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var map = RenameMap.Build(before, after, hints);
        foreach (var sn in schemas)
        {
            var a = before.FindSchema(sn);
            var b = after.FindSchema(sn);
            if (a == null && b != null && sn.Length > 0) list.Add(new(DbChangeKind.Added, DbObjectType.Schema, sn, null, sn, DbPaths.Schema(sn), [], null, b));
            if (b == null && a != null && sn.Length > 0 && a.Tables.Count + a.Sequences.Count + a.Types.Count + a.Routines.Count > 0)
                list.Add(new(DbChangeKind.Removed, DbObjectType.Schema, sn, null, sn, DbPaths.Schema(sn), [], a, null));
            a ??= new DbSchemaDef { Name = sn };
            b ??= new DbSchemaDef { Name = sn };
            Pair(list, a.Types, b.Types, t => t.Name, (x, y) => TypeFields(x, y), DbObjectType.Type, sn, null, t => DbPaths.Type(sn, t.Name));
            Pair(list, a.Sequences, b.Sequences, t => t.Name, (x, y) => SequenceFields(x, y), DbObjectType.Sequence, sn, null, t => DbPaths.Sequence(sn, t.Name));
            foreach (var name in a.Tables.Select(t => t.Name).Concat(b.Tables.Select(t => t.Name)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var tb = b.FindTable(name);
                if (tb == null && map.IsRenamedTable(sn, name)) continue;   // compared under its new name
                var ta = a.FindTable(map.OldTable(sn, name) ?? name);
                var path = DbPaths.Table(sn, name);
                var type = (tb ?? ta)!.IsView ? DbObjectType.View : DbObjectType.Table;
                if (ta == null) { list.Add(new(DbChangeKind.Added, type, sn, null, tb!.Name, path, [], null, tb)); continue; }
                if (tb == null) { list.Add(new(DbChangeKind.Removed, type, sn, null, ta.Name, path, [], ta, null)); continue; }
                if (!DbCatalog.Eq(ta.Name, tb.Name))
                    list.Add(new(DbChangeKind.Renamed, type, sn, null, tb.Name, path, [new FieldChange("name", ta.Name, tb.Name)], ta, tb));
                CompareTable(list, sn, path, ta, tb, type, map);
            }
            Pair(list, a.Routines, b.Routines, t => t.Name, (x, y) => RoutineFields(x, y), DbObjectType.Routine, sn, null, t => DbPaths.Routine(sn, t.Name));
        }
        return list;
    }

    private static void CompareTable(List<DbChange> list, string sn, string path, DbTableDef a, DbTableDef b, DbObjectType type, RenameMap map)
    {
        var f = new List<FieldChange>();
        Field(f, "kind", Kind(a.Kind), Kind(b.Kind));
        Field(f, "comment", a.Comment, b.Comment);
        Field(f, "definition", Sql(a.Definition), Sql(b.Definition), a.Definition, b.Definition);
        if (f.Count > 0) list.Add(new(DbChangeKind.Changed, type, sn, null, b.Name, path, f, a, b));

        // Before-side column names as they are called after the renames.
        string Col(string c) => map.NewColumn(sn, b.Name, c) ?? c;
        foreach (var cn in a.Columns.Select(c => Col(c.Name)).Concat(b.Columns.Select(c => c.Name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var cb = b.FindColumn(cn);
            var oldName = map.OldColumn(sn, b.Name, cn) ?? cn;
            var ca = a.FindColumn(oldName);
            if (ca != null && !DbCatalog.Eq(Col(ca.Name), cn)) ca = null;   // that name now belongs to a renamed column
            var cp = DbPaths.Column(path, cn);
            // Changes inside the table are scripted after the renames, so they name the table and columns as they are now.
            if (ca == null) list.Add(new(DbChangeKind.Added, DbObjectType.Column, sn, b.Name, cb!.Name, cp, [], null, cb));
            else if (cb == null) list.Add(new(DbChangeKind.Removed, DbObjectType.Column, sn, b.Name, ca.Name, cp, [], ca, null));
            else
            {
                if (!DbCatalog.Eq(ca.Name, cb.Name))
                    list.Add(new(DbChangeKind.Renamed, DbObjectType.Column, sn, b.Name, cb.Name, cp, [new FieldChange("name", ca.Name, cb.Name)], ca, cb));
                var cf = new List<FieldChange>();
                if (DbTypes.Normalize(ca.Type) != DbTypes.Normalize(cb.Type)) cf.Add(new("type", ca.Type, cb.Type));
                var pkA = a.IsPrimaryKey(ca.Name); var pkB = b.IsPrimaryKey(cb.Name);
                if ((ca.Nullable && !pkA) != (cb.Nullable && !pkB)) cf.Add(new("nullable", (ca.Nullable && !pkA) ? "true" : "false", (cb.Nullable && !pkB) ? "true" : "false"));
                Field(cf, "default", Default(ca.Default), Default(cb.Default), ca.Default, cb.Default);
                Field(cf, "identity", ca.Identity, cb.Identity);
                Field(cf, "generated", Sql(ca.Generated), Sql(cb.Generated), ca.Generated, cb.Generated);
                Field(cf, "collation", ca.Collation, cb.Collation);
                Field(cf, "comment", ca.Comment, cb.Comment);
                if (ca.Unique != cb.Unique) cf.Add(new("unique", ca.Unique ? "true" : "false", cb.Unique ? "true" : "false"));
                if (cf.Count > 0) list.Add(new(DbChangeKind.Changed, DbObjectType.Column, sn, b.Name, cb.Name, cp, cf, ca, cb));
            }
        }

        var pka = string.Join(", ", a.PrimaryKey.Select(Col)); var pkb = string.Join(", ", b.PrimaryKey);
        if (!string.Equals(pka, pkb, StringComparison.OrdinalIgnoreCase))
        {
            var kind = pka.Length == 0 ? DbChangeKind.Added : pkb.Length == 0 ? DbChangeKind.Removed : DbChangeKind.Changed;
            list.Add(new(kind, DbObjectType.PrimaryKey, sn, b.Name, "primary key", DbPaths.PrimaryKey(path),
                kind == DbChangeKind.Changed ? [new FieldChange("columns", pka, pkb)] : [], a, b));
        }

        // Foreign keys: by name when both sides have one, else by columns and referenced table (both as renamed).
        string RefTable(DbForeignKeyDef fk) => map.NewTable(fk.RefSchema ?? sn, fk.RefTable) ?? fk.RefTable;
        string RefCols(DbForeignKeyDef fk) => List(fk.RefColumns.Select(c => map.NewColumn(fk.RefSchema ?? sn, RefTable(fk), c) ?? c));
        string Signature(DbForeignKeyDef fk) => $"{List(fk.Columns.Select(Col))}->{RefTable(fk).ToLowerInvariant()}";
        var fkA = a.ForeignKeys.ToList(); var fkB = b.ForeignKeys.ToList();
        foreach (var fb in fkB)
        {
            var fa = fkA.FirstOrDefault(x => !string.IsNullOrEmpty(x.Name) && !string.IsNullOrEmpty(fb.Name) && DbCatalog.Eq(x.Name, fb.Name))
                     ?? fkA.FirstOrDefault(x => Signature(x) == fb.Signature);
            var fp = DbPaths.ForeignKey(path, fb);
            if (fa == null) { list.Add(new(DbChangeKind.Added, DbObjectType.ForeignKey, sn, b.Name, FkName(fb), fp, [], null, fb)); continue; }
            fkA.Remove(fa);
            var ff = new List<FieldChange>();
            Field(ff, "columns", List(fa.Columns.Select(Col)), List(fb.Columns), List(fa.Columns), List(fb.Columns));
            Field(ff, "references", RefTable(fa).ToLowerInvariant(), fb.RefTable.ToLowerInvariant(), fa.RefTable, fb.RefTable);
            Field(ff, "refColumns", RefCols(fa), List(fb.RefColumns), List(fa.RefColumns), List(fb.RefColumns));
            Field(ff, "onDelete", Action(fa.OnDelete), Action(fb.OnDelete));
            Field(ff, "onUpdate", Action(fa.OnUpdate), Action(fb.OnUpdate));
            if (ff.Count > 0) list.Add(new(DbChangeKind.Changed, DbObjectType.ForeignKey, sn, b.Name, FkName(fb), DbPaths.ForeignKey(path, fa), ff, fa, fb));
        }
        foreach (var fa in fkA) list.Add(new(DbChangeKind.Removed, DbObjectType.ForeignKey, sn, b.Name, FkName(fa), DbPaths.ForeignKey(path, fa), [], fa, null));

        Pair(list, a.Indexes, b.Indexes, x => x.Name, (x, y) =>
        {
            var l = new List<FieldChange>();
            Field(l, "columns", List(x.Columns.Select(Col)), List(y.Columns), List(x.Columns), List(y.Columns));
            if (x.Unique != y.Unique) l.Add(new("unique", x.Unique ? "true" : "false", y.Unique ? "true" : "false"));
            Field(l, "method", Method(x.Method), Method(y.Method));
            Field(l, "where", Sql(x.Where), Sql(y.Where), x.Where, y.Where);
            return l;
        }, DbObjectType.Index, sn, b.Name, x => DbPaths.Index(path, x.Name));
        Pair(list, a.Constraints, b.Constraints, x => x.Name, (x, y) =>
        {
            var l = new List<FieldChange>();
            Field(l, "type", x.Type, y.Type);
            Field(l, "columns", List(x.Columns.Select(Col)), List(y.Columns), List(x.Columns), List(y.Columns));
            Field(l, "expression", Sql(x.Expression), Sql(y.Expression), x.Expression, y.Expression);
            return l;
        }, DbObjectType.Constraint, sn, b.Name, x => DbPaths.Constraint(path, x.Name));
        Pair(list, a.Triggers, b.Triggers, x => x.Name, (x, y) =>
        {
            var l = new List<FieldChange>();
            Field(l, "timing", x.Timing.ToUpperInvariant(), y.Timing.ToUpperInvariant());
            Field(l, "events", Events(x.Events), Events(y.Events), x.Events, y.Events);
            Field(l, "level", Level(x.Level), Level(y.Level), x.Level, y.Level);
            Field(l, "action", Sql(x.Action), Sql(y.Action), x.Action, y.Action);
            return l;
        }, DbObjectType.Trigger, sn, b.Name, x => DbPaths.Trigger(path, x.Name));
    }

    /// <summary>The renames between two catalogs: by element id, plus the confirmed hints.</summary>
    private sealed class RenameMap
    {
        private readonly Dictionary<(string Schema, string Old), string> _tables = new(KeyComparer.Instance);
        private readonly Dictionary<(string Schema, string New), string> _tablesBack = new(KeyComparer.Instance);
        private readonly Dictionary<(string Schema, string Table, string Old), string> _columns = new(TripleComparer.Instance);
        private readonly Dictionary<(string Schema, string Table, string New), string> _columnsBack = new(TripleComparer.Instance);

        public static RenameMap Build(DbCatalog before, DbCatalog after, List<DbRenameHint> hints)
        {
            var m = new RenameMap();
            foreach (var b in after.Schemas)
            {
                if (before.FindSchema(b.Name) is not { } a) continue;
                foreach (var tb in b.Tables)
                {
                    var ta = tb.Id is { } id ? a.Tables.FirstOrDefault(t => t.Id == id) : null;
                    var hint = hints.FirstOrDefault(h => h.Type is DbObjectType.Table or DbObjectType.View && DbCatalog.Eq(h.Schema, b.Name) && DbCatalog.Eq(h.NewName, tb.Name));
                    if (ta == null && hint != null) ta = a.FindTable(hint.OldName);
                    // Only a real rename: the old name is gone from the after side and the new one was free before.
                    if (ta != null && !DbCatalog.Eq(ta.Name, tb.Name) && b.FindTable(ta.Name) == null && a.FindTable(tb.Name) == null && !m._tables.ContainsKey((b.Name, ta.Name)))
                    {
                        m._tables[(b.Name, ta.Name)] = tb.Name;
                        m._tablesBack[(b.Name, tb.Name)] = ta.Name;
                    }
                    ta ??= a.FindTable(tb.Name);
                    if (ta == null || ta.IsView != tb.IsView && ta.Id == null) continue;
                    foreach (var cb in tb.Columns)
                    {
                        var ca = cb.Id is { } cid ? ta.Columns.FirstOrDefault(c => c.Id == cid) : null;
                        var ch = hints.FirstOrDefault(h => h.Type == DbObjectType.Column && DbCatalog.Eq(h.Schema, b.Name) && DbCatalog.Eq(h.Table, tb.Name) && DbCatalog.Eq(h.NewName, cb.Name));
                        if (ca == null && ch != null) ca = ta.FindColumn(ch.OldName);
                        if (ca != null && !DbCatalog.Eq(ca.Name, cb.Name) && tb.FindColumn(ca.Name) == null && ta.FindColumn(cb.Name) == null && !m._columns.ContainsKey((b.Name, tb.Name, ca.Name)))
                        {
                            m._columns[(b.Name, tb.Name, ca.Name)] = cb.Name;
                            m._columnsBack[(b.Name, tb.Name, cb.Name)] = ca.Name;
                        }
                    }
                }
            }
            return m;
        }

        public bool IsRenamedTable(string schema, string oldName) => _tables.ContainsKey((schema, oldName));
        public string? NewTable(string schema, string oldName) => _tables.GetValueOrDefault((schema, oldName));
        public string? OldTable(string schema, string newName) => _tablesBack.GetValueOrDefault((schema, newName));
        /// <summary><paramref name="table"/> is the table's new name.</summary>
        public string? NewColumn(string schema, string table, string oldName) => _columns.GetValueOrDefault((schema, table, oldName));
        public string? OldColumn(string schema, string table, string newName) => _columnsBack.GetValueOrDefault((schema, table, newName));

        private sealed class KeyComparer : IEqualityComparer<(string, string)>
        {
            public static readonly KeyComparer Instance = new();
            public bool Equals((string, string) x, (string, string) y) => DbCatalog.Eq(x.Item1, y.Item1) && DbCatalog.Eq(x.Item2, y.Item2);
            public int GetHashCode((string, string) k) => HashCode.Combine(k.Item1.ToLowerInvariant(), k.Item2.ToLowerInvariant());
        }

        private sealed class TripleComparer : IEqualityComparer<(string, string, string)>
        {
            public static readonly TripleComparer Instance = new();
            public bool Equals((string, string, string) x, (string, string, string) y) => DbCatalog.Eq(x.Item1, y.Item1) && DbCatalog.Eq(x.Item2, y.Item2) && DbCatalog.Eq(x.Item3, y.Item3);
            public int GetHashCode((string, string, string) k) => HashCode.Combine(k.Item1.ToLowerInvariant(), k.Item2.ToLowerInvariant(), k.Item3.ToLowerInvariant());
        }
    }

    /// <summary>
    /// Likely renames between two catalogs whose objects carry no ids (a live database or a script against the model): a
    /// table that disappeared and one that appeared in the same schema with mostly the same columns, and within a table a
    /// column that disappeared and one of the same type that appeared, scored by name similarity, position and nullability.
    /// Pairs where both sides have element ids are left out (the ids already tell). Best matches first, each name used once.
    /// </summary>
    public static IReadOnlyList<DbRenameHint> SuggestRenames(DbCatalog before, DbCatalog after, double threshold = 0.6)
    {
        var result = new List<DbRenameHint>();
        foreach (var b in after.Schemas)
        {
            if (before.FindSchema(b.Name) is not { } a) continue;
            var removed = a.Tables.Where(t => b.FindTable(t.Name) == null).ToList();
            var added = b.Tables.Where(t => a.FindTable(t.Name) == null).ToList();
            var tablePairs = Greedy(
                from r in removed from n in added
                where r.IsView == n.IsView && !(r.Id != null && n.Id != null)
                select (Old: r, New: n, Score: TableScore(r, n)), threshold);
            foreach (var (r, n, s) in tablePairs)
                result.Add(new DbRenameHint(n.IsView ? DbObjectType.View : DbObjectType.Table, b.Name, null, r.Name, n.Name, Math.Round(s, 2)));

            var pairs = b.Tables.Select(tb => (Old: a.FindTable(tb.Name) ?? tablePairs.FirstOrDefault(p => p.New == tb).Old, New: tb)).Where(p => p.Old != null && !p.New.IsView);
            foreach (var (ta, tb) in pairs)
            {
                var gone = ta!.Columns.Where(c => tb.FindColumn(c.Name) == null).ToList();
                var come = tb.Columns.Where(c => ta.FindColumn(c.Name) == null).ToList();
                if (gone.Count == 0 || come.Count == 0) continue;
                var cols = Greedy(
                    from g in gone from c in come
                    where !(g.Id != null && c.Id != null) && DbTypes.Normalize(g.Type) == DbTypes.Normalize(c.Type)
                    select (Old: g, New: c, Score: ColumnScore(ta, g, tb, c)), threshold);
                foreach (var (g, c, s) in cols)
                    result.Add(new DbRenameHint(DbObjectType.Column, b.Name, tb.Name, g.Name, c.Name, Math.Round(s, 2)));
            }
        }
        return result;
    }

    private static List<(T Old, T New, double Score)> Greedy<T>(IEnumerable<(T Old, T New, double Score)> candidates, double threshold) where T : class
    {
        var chosen = new List<(T, T, double)>();
        var usedOld = new HashSet<T>(ReferenceEqualityComparer.Instance);
        var usedNew = new HashSet<T>(ReferenceEqualityComparer.Instance);
        foreach (var c in candidates.Where(c => c.Score >= threshold).OrderByDescending(c => c.Score))
        {
            if (usedOld.Contains(c.Old) || usedNew.Contains(c.New)) continue;
            usedOld.Add(c.Old); usedNew.Add(c.New);
            chosen.Add(c);
        }
        return chosen;
    }

    /// <summary>Mostly the same columns (name and type), plus a little for a similar name.</summary>
    private static double TableScore(DbTableDef a, DbTableDef b)
    {
        static string Key(DbColumnDef c) => $"{c.Name.ToLowerInvariant()}:{DbTypes.Normalize(c.Type)}";
        var x = a.Columns.Select(Key).ToHashSet();
        var y = b.Columns.Select(Key).ToHashSet();
        var union = x.Union(y).Count();
        var columns = union == 0 ? (a.IsView ? 0.5 : 0) : (double)x.Intersect(y).Count() / union;
        return 0.75 * columns + 0.25 * NameSimilarity(a.Name, b.Name);
    }

    /// <summary>Same type (required by the caller), name similarity, position in the table and nullability.</summary>
    private static double ColumnScore(DbTableDef ta, DbColumnDef a, DbTableDef tb, DbColumnDef b)
    {
        var pa = ta.Columns.IndexOf(a); var pb = tb.Columns.IndexOf(b);
        var position = 1.0 - Math.Min(1.0, Math.Abs(pa - pb) / 3.0);
        return 0.4 + 0.35 * NameSimilarity(a.Name, b.Name) + 0.15 * position + (a.Nullable == b.Nullable ? 0.1 : 0);
    }

    /// <summary>1 for equal names, 0.8 when one contains the other ("amount" / "total_amount"), else 1 − edit distance / length.</summary>
    public static double NameSimilarity(string a, string b)
    {
        static string N(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var x = N(a); var y = N(b);
        if (x.Length == 0 || y.Length == 0) return 0;
        if (x == y) return 1;
        var edit = 1.0 - (double)Levenshtein(x, y) / Math.Max(x.Length, y.Length);
        return x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal) ? Math.Max(0.8, edit) : edit;
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private static void Pair<T>(List<DbChange> list, List<T> a, List<T> b, Func<T, string> name, Func<T, T, List<FieldChange>> fields,
        DbObjectType type, string schema, string? table, Func<T, string> path) where T : class
    {
        foreach (var n in a.Select(name).Concat(b.Select(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var x = a.FirstOrDefault(i => DbCatalog.Eq(name(i), n));
            var y = b.FirstOrDefault(i => DbCatalog.Eq(name(i), n));
            if (x == null) list.Add(new(DbChangeKind.Added, type, schema, table, name(y!), path(y!), [], null, y));
            else if (y == null) list.Add(new(DbChangeKind.Removed, type, schema, table, name(x), path(x), [], x, null));
            else if (fields(x, y) is { Count: > 0 } f) list.Add(new(DbChangeKind.Changed, type, schema, table, name(y), path(y), f, x, y));
        }
    }

    private static List<FieldChange> TypeFields(DbTypeDef a, DbTypeDef b)
    {
        var f = new List<FieldChange>();
        Field(f, "kind", a.Kind, b.Kind);
        if (!a.Values.SequenceEqual(b.Values, StringComparer.Ordinal)) f.Add(new("values", string.Join(", ", a.Values), string.Join(", ", b.Values)));
        Field(f, "baseType", Norm(a.BaseType), Norm(b.BaseType), a.BaseType, b.BaseType);
        Field(f, "check", Sql(a.Check), Sql(b.Check), a.Check, b.Check);
        return f;
    }

    private static List<FieldChange> SequenceFields(DbSequenceDef a, DbSequenceDef b)
    {
        var f = new List<FieldChange>();
        Field(f, "start", a.Start ?? "1", b.Start ?? "1");
        Field(f, "increment", a.Increment ?? "1", b.Increment ?? "1");
        return f;
    }

    private static List<FieldChange> RoutineFields(DbRoutineDef a, DbRoutineDef b)
    {
        var f = new List<FieldChange>();
        Field(f, "kind", a.Kind, b.Kind);
        Field(f, "signature", Sql(a.Signature), Sql(b.Signature), a.Signature, b.Signature);
        Field(f, "returns", Norm(a.Returns), Norm(b.Returns), a.Returns, b.Returns);
        // A side that does not know the language, options or body (a signature-only source) does not count as a change.
        if (a.Language != null && b.Language != null) Field(f, "language", Lower(a.Language), Lower(b.Language), a.Language, b.Language);
        if (a.Body != null && b.Body != null)
        {
            Field(f, "options", Sql(a.Options), Sql(b.Options), a.Options, b.Options);
            Field(f, "body", Sql(a.Body), Sql(b.Body), a.Body, b.Body);
        }
        return f;
    }

    private static string FkName(DbForeignKeyDef fk) => string.IsNullOrEmpty(fk.Name) ? $"({string.Join(", ", fk.Columns)}) → {fk.RefTable}" : fk.Name!;

    /// <summary>Adds a field change when the compared forms differ; shows the raw values.</summary>
    private static void Field(List<FieldChange> list, string key, string? compareA, string? compareB, string? showA = null, string? showB = null)
    {
        if (string.Equals(compareA ?? "", compareB ?? "", StringComparison.Ordinal)) return;
        list.Add(new FieldChange(key, showA ?? compareA, showB ?? compareB));
    }

    private static string Kind(string k) => k is DbFields.View or DbFields.MaterializedView ? k : "table";
    /// <summary>The default access methods (btree, nonclustered) compare equal to none.</summary>
    private static string? Method(string? m) => Lower(m) is "btree" or "nonclustered" ? null : Lower(m);
    private static string? Lower(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToLowerInvariant();
    /// <summary>Trigger events in a comparable form: "INSERT OR UPDATE" = "update, insert".</summary>
    private static string? Events(string? s) => string.IsNullOrWhiteSpace(s) ? null
        : string.Join(",", Regex.Split(s.Trim().ToLowerInvariant(), @"\s*,\s*|\s+or\s+").Select(e => e.Split(' ')[0]).Where(e => e.Length > 0).Order(StringComparer.Ordinal));
    /// <summary>FOR EACH STATEMENT is the default.</summary>
    private static string? Level(string? s) => Lower(s) is "statement" or null ? null : Lower(s);
    private static string? Norm(string? t) => string.IsNullOrWhiteSpace(t) ? null : DbTypes.Normalize(t);
    private static string List(IEnumerable<string> items) => string.Join(",", items.Select(i => i.Trim().ToLowerInvariant()));
    private static string? Action(string? a) => string.IsNullOrWhiteSpace(a) || a.Trim().Equals("NO ACTION", StringComparison.OrdinalIgnoreCase) ? null : a.Trim().ToUpperInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    [GeneratedRegex(@"::[\w .]+(\(\d+(,\d+)?\))?(\[\])?")]
    private static partial Regex Cast();

    /// <summary>SQL text in a comparable form: whitespace collapsed, lower case, trailing semicolon and quotes removed.</summary>
    public static string? Sql(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = Space().Replace(s.Trim().TrimEnd(';'), " ").ToLowerInvariant();
        t = t.Replace("\"", "").Replace("`", "").Replace("[", "").Replace("]", "").Replace("( ", "(").Replace(" )", ")");
        return t;
    }

    /// <summary>A default in a comparable form: casts ("'new'::character varying") and wrapping parentheses ("((0))") removed.</summary>
    public static string? Default(string? s)
    {
        var t = Sql(s);
        if (t == null) return null;
        t = Cast().Replace(t, "");
        while (t.Length > 1 && t[0] == '(' && t[^1] == ')' && Balanced(t[1..^1])) t = t[1..^1].Trim();
        return t switch { "now()" or "current_timestamp" or "getdate()" or "sysdatetime()" or "systimestamp" => "current_timestamp", "true" => "1", "false" => "0", _ => t };
    }

    private static bool Balanced(string s)
    {
        var d = 0;
        foreach (var c in s) { if (c == '(') d++; else if (c == ')' && --d < 0) return false; }
        return d == 0;
    }
}
