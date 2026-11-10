using System.Text.RegularExpressions;
using Meridian.Core.Analysis;

namespace Meridian.Core.DataModeling;

public enum DbChangeKind { Added, Changed, Removed }

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
        _ => $"Changed {Label}: " + string.Join("; ", Fields.Select(f => $"{f.Key} {Show(f.Before)} → {Show(f.After)}")),
    };

    private static string Show(string? s) => string.IsNullOrEmpty(s) ? "(none)" : s.Length > 60 ? s[..57] + "…" : s;
    private static string Qualified(string schema, string name) => schema.Length > 0 ? $"{schema}.{name}" : name;
}

/// <summary>Compares two catalogs object by object (names compared without case; types and defaults compared in a canonical form).</summary>
public static partial class DbDiff
{
    public static IReadOnlyList<DbChange> Compare(DbCatalog before, DbCatalog after)
    {
        var list = new List<DbChange>();
        var schemas = before.Schemas.Select(s => s.Name).Concat(after.Schemas.Select(s => s.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
                var ta = a.FindTable(name);
                var tb = b.FindTable(name);
                var path = DbPaths.Table(sn, name);
                var type = (tb ?? ta)!.IsView ? DbObjectType.View : DbObjectType.Table;
                if (ta == null) { list.Add(new(DbChangeKind.Added, type, sn, null, tb!.Name, path, [], null, tb)); continue; }
                if (tb == null) { list.Add(new(DbChangeKind.Removed, type, sn, null, ta.Name, path, [], ta, null)); continue; }
                CompareTable(list, sn, path, ta, tb, type);
            }
            Pair(list, a.Routines, b.Routines, t => t.Name, (x, y) => RoutineFields(x, y), DbObjectType.Routine, sn, null, t => DbPaths.Routine(sn, t.Name));
        }
        return list;
    }

    private static void CompareTable(List<DbChange> list, string sn, string path, DbTableDef a, DbTableDef b, DbObjectType type)
    {
        var f = new List<FieldChange>();
        Field(f, "kind", Kind(a.Kind), Kind(b.Kind));
        Field(f, "comment", a.Comment, b.Comment);
        Field(f, "definition", Sql(a.Definition), Sql(b.Definition), a.Definition, b.Definition);
        if (f.Count > 0) list.Add(new(DbChangeKind.Changed, type, sn, null, b.Name, path, f, a, b));

        foreach (var cn in a.Columns.Select(c => c.Name).Concat(b.Columns.Select(c => c.Name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var ca = a.FindColumn(cn); var cb = b.FindColumn(cn);
            var cp = DbPaths.Column(path, cn);
            if (ca == null) list.Add(new(DbChangeKind.Added, DbObjectType.Column, sn, b.Name, cb!.Name, cp, [], null, cb));
            else if (cb == null) list.Add(new(DbChangeKind.Removed, DbObjectType.Column, sn, a.Name, ca.Name, cp, [], ca, null));
            else
            {
                var cf = new List<FieldChange>();
                if (DbTypes.Normalize(ca.Type) != DbTypes.Normalize(cb.Type)) cf.Add(new("type", ca.Type, cb.Type));
                var pkA = a.IsPrimaryKey(cn); var pkB = b.IsPrimaryKey(cn);
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

        var pka = string.Join(", ", a.PrimaryKey); var pkb = string.Join(", ", b.PrimaryKey);
        if (!string.Equals(pka, pkb, StringComparison.OrdinalIgnoreCase))
        {
            var kind = pka.Length == 0 ? DbChangeKind.Added : pkb.Length == 0 ? DbChangeKind.Removed : DbChangeKind.Changed;
            list.Add(new(kind, DbObjectType.PrimaryKey, sn, b.Name, "primary key", DbPaths.PrimaryKey(path),
                kind == DbChangeKind.Changed ? [new FieldChange("columns", pka, pkb)] : [], a, b));
        }

        // Foreign keys: by name when both sides have one, else by columns and referenced table.
        var fkA = a.ForeignKeys.ToList(); var fkB = b.ForeignKeys.ToList();
        foreach (var fb in fkB)
        {
            var fa = fkA.FirstOrDefault(x => !string.IsNullOrEmpty(x.Name) && !string.IsNullOrEmpty(fb.Name) && DbCatalog.Eq(x.Name, fb.Name))
                     ?? fkA.FirstOrDefault(x => x.Signature == fb.Signature);
            var fp = DbPaths.ForeignKey(path, fb);
            if (fa == null) { list.Add(new(DbChangeKind.Added, DbObjectType.ForeignKey, sn, b.Name, FkName(fb), fp, [], null, fb)); continue; }
            fkA.Remove(fa);
            var ff = new List<FieldChange>();
            Field(ff, "columns", List(fa.Columns), List(fb.Columns));
            Field(ff, "references", fa.RefTable, fb.RefTable);
            Field(ff, "refColumns", List(fa.RefColumns), List(fb.RefColumns));
            Field(ff, "onDelete", Action(fa.OnDelete), Action(fb.OnDelete));
            Field(ff, "onUpdate", Action(fa.OnUpdate), Action(fb.OnUpdate));
            if (ff.Count > 0) list.Add(new(DbChangeKind.Changed, DbObjectType.ForeignKey, sn, b.Name, FkName(fb), DbPaths.ForeignKey(path, fa), ff, fa, fb));
        }
        foreach (var fa in fkA) list.Add(new(DbChangeKind.Removed, DbObjectType.ForeignKey, sn, a.Name, FkName(fa), DbPaths.ForeignKey(path, fa), [], fa, null));

        Pair(list, a.Indexes, b.Indexes, x => x.Name, (x, y) =>
        {
            var l = new List<FieldChange>();
            Field(l, "columns", List(x.Columns), List(y.Columns));
            if (x.Unique != y.Unique) l.Add(new("unique", x.Unique ? "true" : "false", y.Unique ? "true" : "false"));
            Field(l, "method", Method(x.Method), Method(y.Method));
            Field(l, "where", Sql(x.Where), Sql(y.Where), x.Where, y.Where);
            return l;
        }, DbObjectType.Index, sn, b.Name, x => DbPaths.Index(path, x.Name));
        Pair(list, a.Constraints, b.Constraints, x => x.Name, (x, y) =>
        {
            var l = new List<FieldChange>();
            Field(l, "type", x.Type, y.Type);
            Field(l, "columns", List(x.Columns), List(y.Columns));
            Field(l, "expression", Sql(x.Expression), Sql(y.Expression), x.Expression, y.Expression);
            return l;
        }, DbObjectType.Constraint, sn, b.Name, x => DbPaths.Constraint(path, x.Name));
        Pair(list, a.Triggers, b.Triggers, x => x.Name, (x, y) =>
        {
            var l = new List<FieldChange>();
            Field(l, "timing", x.Timing.ToUpperInvariant(), y.Timing.ToUpperInvariant());
            Field(l, "events", Lower(x.Events), Lower(y.Events));
            Field(l, "action", Sql(x.Action), Sql(y.Action), x.Action, y.Action);
            return l;
        }, DbObjectType.Trigger, sn, b.Name, x => DbPaths.Trigger(path, x.Name));
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
    private static string? Norm(string? t) => string.IsNullOrWhiteSpace(t) ? null : DbTypes.Normalize(t);
    private static string List(IEnumerable<string> items) => string.Join(",", items.Select(i => i.Trim().ToLowerInvariant()));
    private static string? Action(string? a) => string.IsNullOrWhiteSpace(a) || a.Trim().Equals("NO ACTION", StringComparison.OrdinalIgnoreCase) ? null : a.Trim().ToUpperInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    [GeneratedRegex(@"::[\w ]+(\(\d+(,\d+)?\))?(\[\])?")]
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
