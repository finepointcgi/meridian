using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.DataModeling;

public sealed class DbImportOptions
{
    /// <summary>Name of the database package (default: the catalog's name).</summary>
    public string? DatabaseName { get; init; }
    /// <summary>Where the import came from (stored on the database package, e.g. "ddl:orders.sql").</summary>
    public string? Source { get; init; }
    /// <summary>Create a database diagram per schema (laid out automatically).</summary>
    public bool DiagramPerSchema { get; init; } = true;
}

public sealed record DbImportResult(Element Database, int Schemas, int Tables, int Columns, int ForeignKeys, int Others, IReadOnlyList<Diagram> Diagrams, IReadOnlyList<string> Warnings)
{
    public string Summary => $"{Tables} tables, {Columns} columns, {ForeignKeys} foreign keys" + (Others > 0 ? $", {Others} other objects" : "")
        + $" in {Schemas} schema{(Schemas == 1 ? "" : "s")}" + (Warnings.Count > 0 ? $"; {Warnings.Count} warning{(Warnings.Count == 1 ? "" : "s")}" : "");
}

/// <summary>Writes catalog objects into the model (first imports and the "added" / "changed" items of a refresh).</summary>
public sealed class DbModelWriter(ModelRepository m, Element database, string catalogName)
{
    private readonly Dictionary<string, Element> _schemas = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; } = new();
    public int Columns { get; private set; }
    public int ForeignKeys { get; private set; }
    public int Others { get; private set; }

    public string Key(params string[] parts) => string.Join(".", parts.Prepend(catalogName).Where(p => p.Length > 0));

    /// <summary>The schema package for <paramref name="name"/> (created when missing).</summary>
    public Element Schema(string name, string? comment = null)
    {
        if (_schemas.TryGetValue(name, out var s) && m.Contains(s)) return s;
        s = database.OwnedElements.FirstOrDefault(e => DbModel.IsSchema(e) && (DbCatalog.Eq(DbExtract.LastSegment(e.Get(DbFields.QualifiedName)), name) || DbCatalog.Eq(e.Name, name)));
        if (s == null)
        {
            s = DbModel.CreateSchema(m, database, name.Length == 0 ? "default" : name);
            m.Set(s, DbFields.QualifiedName, Key(name));
            Comment(s, comment);
        }
        return _schemas[name] = s;
    }

    public void Comment(Element e, string? comment)
    {
        var old = e.Get(DbFields.Comment);
        comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if ((comment ?? "") == old) return;
        // Documentation written in Meridian is never overwritten: only an empty one, or one still equal to the last imported comment.
        if (e.Documentation.Length == 0 || e.Documentation == old) m.Set(e, FieldKeys.Documentation, comment);
        m.Set(e, DbFields.Comment, comment);
    }

    public Element CreateTable(Element schema, string schemaName, DbTableDef def)
    {
        var t = m.Create(schema, ElementKind.Table, def.Name);
        m.Set(t, DbFields.QualifiedName, Key(schemaName, def.Name));
        UpdateTable(t, def);
        foreach (var c in def.Columns) CreateColumn(t, schemaName, def, c);
        SetPrimaryKey(t, def);
        foreach (var ix in def.Indexes) CreateIndex(t, schemaName, def, ix);
        foreach (var ck in def.Constraints) CreateConstraint(t, schemaName, def, ck);
        foreach (var tg in def.Triggers) CreateTrigger(t, schemaName, def, tg);
        return t;
    }

    public void UpdateTable(Element t, DbTableDef def)
    {
        Set(t, FieldKeys.Subtype, def.Kind);
        Set(t, DbFields.Definition, def.Definition);
        Set(t, DbFields.PkName, def.PrimaryKeyName);
        Comment(t, def.Comment);
    }

    public Element CreateColumn(Element table, string schemaName, DbTableDef tdef, DbColumnDef def)
    {
        var c = m.Create(table, ElementKind.Column, def.Name);
        m.Set(c, DbFields.QualifiedName, Key(schemaName, tdef.Name, def.Name));
        UpdateColumn(c, def, tdef.IsPrimaryKey(def.Name));
        Columns++;
        return c;
    }

    public void UpdateColumn(Element c, DbColumnDef def, bool pk)
    {
        Set(c, FieldKeys.SqlType, def.Type);
        Set(c, FieldKeys.Nullable, def.Nullable && !pk ? "true" : null);
        Set(c, FieldKeys.Unique, def.Unique ? "true" : null);
        Set(c, FieldKeys.DefaultValue, def.Default);
        Set(c, DbFields.Identity, def.Identity);
        Set(c, DbFields.Generated, def.Generated);
        Set(c, DbFields.Collation, def.Collation);
        Comment(c, def.Comment);
    }

    public void SetPrimaryKey(Element t, DbTableDef def)
    {
        foreach (var c in DbModel.Columns(t))
        {
            var pk = def.IsPrimaryKey(c.Name) || def.IsPrimaryKey(DbExtract.LastSegment(c.Get(DbFields.QualifiedName)));
            Set(c, FieldKeys.PrimaryKey, pk ? "true" : null);
            if (pk) Set(c, FieldKeys.Nullable, null);
        }
        Set(t, DbFields.PkName, def.PrimaryKeyName);
    }

    public Relationship? CreateForeignKey(Element source, Element target, Element owner, DbForeignKeyDef def, string sourceKey)
    {
        var r = m.Connect(ElementKind.ForeignKey, source, target, owner);
        UpdateForeignKey(r, def);
        m.Set(r, DbFields.QualifiedName, $"{sourceKey}.{(string.IsNullOrEmpty(def.Name) ? def.Signature : def.Name)}");
        ForeignKeys++;
        return r;
    }

    public void UpdateForeignKey(Relationship r, DbForeignKeyDef def)
    {
        Set(r, FieldKeys.Name, def.Name);
        Set(r, FieldKeys.FkColumns, DbModel.JoinList(def.Columns));
        Set(r, FieldKeys.RefColumns, DbModel.JoinList(def.RefColumns));
        Set(r, DbFields.OnDelete, def.OnDelete);
        Set(r, DbFields.OnUpdate, def.OnUpdate);
    }

    public Element CreateIndex(Element t, string schemaName, DbTableDef tdef, DbIndexDef def)
    {
        var e = m.Create(t, ElementKind.DbIndex, def.Name);
        m.Set(e, DbFields.QualifiedName, Key(schemaName, tdef.Name, def.Name));
        UpdateIndex(e, def);
        Others++;
        return e;
    }

    public void UpdateIndex(Element e, DbIndexDef def)
    {
        Set(e, DbFields.Columns, DbModel.JoinList(def.Columns));
        Set(e, FieldKeys.Unique, def.Unique ? "true" : null);
        Set(e, DbFields.Method, def.Method);
        Set(e, DbFields.Expression, def.Where);
    }

    public Element CreateConstraint(Element t, string schemaName, DbTableDef tdef, DbConstraintDef def)
    {
        var e = m.Create(t, ElementKind.DbConstraint, def.Name);
        m.Set(e, DbFields.QualifiedName, Key(schemaName, tdef.Name, def.Name));
        UpdateConstraint(e, def);
        Others++;
        return e;
    }

    public void UpdateConstraint(Element e, DbConstraintDef def)
    {
        Set(e, FieldKeys.Subtype, def.Type);
        Set(e, DbFields.Columns, DbModel.JoinList(def.Columns));
        Set(e, DbFields.Expression, def.Expression);
    }

    public Element CreateTrigger(Element t, string schemaName, DbTableDef tdef, DbTriggerDef def)
    {
        var e = m.Create(t, ElementKind.Trigger, def.Name);
        m.Set(e, DbFields.QualifiedName, Key(schemaName, tdef.Name, def.Name));
        UpdateTrigger(e, def);
        Others++;
        return e;
    }

    public void UpdateTrigger(Element e, DbTriggerDef def)
    {
        Set(e, DbFields.Timing, def.Timing);
        Set(e, DbFields.Events, def.Events);
        Set(e, DbFields.Level, def.Level);
        Set(e, DbFields.Definition, def.Action);
    }

    public Element CreateSequence(Element schema, string schemaName, DbSequenceDef def)
    {
        var e = m.Create(schema, ElementKind.Sequence, def.Name);
        m.Set(e, DbFields.QualifiedName, Key(schemaName, def.Name));
        UpdateSequence(e, def);
        Others++;
        return e;
    }

    public void UpdateSequence(Element e, DbSequenceDef def)
    {
        Set(e, FieldKeys.SqlType, def.Type);
        Set(e, DbFields.Start, def.Start);
        Set(e, DbFields.Increment, def.Increment);
        Set(e, DbFields.MinValue, def.Min);
        Set(e, DbFields.MaxValue, def.Max);
        Set(e, DbFields.Cycle, def.Cycle ? "true" : null);
    }

    public Element CreateType(Element schema, string schemaName, DbTypeDef def)
    {
        var e = m.Create(schema, def.Kind == "enum" ? ElementKind.Enumeration : ElementKind.DataType, def.Name);
        m.Set(e, DbFields.QualifiedName, Key(schemaName, def.Name));
        UpdateType(e, def);
        Others++;
        return e;
    }

    public void UpdateType(Element e, DbTypeDef def)
    {
        if (e.Kind == ElementKind.Enumeration)
        {
            var lits = e.OwnedElements.Where(l => l.Kind == ElementKind.EnumerationLiteral).ToList();
            if (!lits.Select(l => l.Name).SequenceEqual(def.Values, StringComparer.Ordinal))
            {
                m.Delete(lits);
                foreach (var v in def.Values) m.Create(e, ElementKind.EnumerationLiteral, v);
            }
        }
        else
        {
            Set(e, DbFields.BaseType, def.BaseType);
            Set(e, DbFields.Expression, def.Check);
            Set(e, FieldKeys.Nullable, def.NotNull ? "false" : null);
        }
        Comment(e, def.Comment);
    }

    public Element CreateRoutine(Element schema, string schemaName, DbRoutineDef def)
    {
        var e = m.Create(schema, ElementKind.Routine, def.Name);
        m.Set(e, DbFields.QualifiedName, Key(schemaName, def.Name));
        UpdateRoutine(e, def);
        Others++;
        return e;
    }

    public void UpdateRoutine(Element e, DbRoutineDef def)
    {
        Set(e, FieldKeys.Subtype, def.Kind);
        Set(e, FieldKeys.Signature, def.Signature);
        Set(e, DbFields.Returns, def.Returns);
        Set(e, DbFields.Language, def.Language);
        Comment(e, def.Comment);
    }

    private void Set(Element e, string key, string? value)
    {
        value = string.IsNullOrWhiteSpace(value) ? null : value;
        if (e.Get(key) != (value ?? "")) m.Set(e, key, value);
    }
}

/// <summary>Imports a catalog (from DDL, a live database, a .dacpac or Mermaid) as a new database in the model.</summary>
public static class DbImport
{
    /// <summary>Creates the database package with its schemas, tables, keys and other objects (and the diagrams), as ONE undo step.</summary>
    public static DbImportResult Import(ModelRepository m, Element target, DbCatalog catalog, DbImportOptions? options = null)
    {
        options ??= new DbImportOptions();
        var name = options.DatabaseName is { Length: > 0 } n ? n : catalog.Name is { Length: > 0 } cn ? cn : "Database";
        using (m.History.Transaction($"Import database {name}"))
        {
            var db = DbModel.CreateDatabase(m, target, name, catalog.Dialect.Length > 0 ? catalog.Dialect : null);
            m.Set(db, DbFields.QualifiedName, catalog.Name.Length > 0 ? catalog.Name : name);
            if (options.Source is { Length: > 0 } src) m.Set(db, DbFields.Source, src);
            var w = new DbModelWriter(m, db, db.Get(DbFields.QualifiedName));
            w.Warnings.AddRange(catalog.Warnings);
            var tables = new Dictionary<DbTableDef, Element>(ReferenceEqualityComparer.Instance);
            var byPath = new Dictionary<string, Element>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in catalog.Schemas)
            {
                var sp = w.Schema(s.Name, s.Comment);
                foreach (var ty in s.Types) w.CreateType(sp, s.Name, ty);
                foreach (var sq in s.Sequences) w.CreateSequence(sp, s.Name, sq);
                foreach (var t in s.Tables)
                {
                    var te = w.CreateTable(sp, s.Name, t);
                    tables[t] = te;
                    byPath[DbPaths.Table(s.Name, t.Name)] = te;
                }
                foreach (var r in s.Routines) w.CreateRoutine(sp, s.Name, r);
            }
            foreach (var s in catalog.Schemas)
                foreach (var t in s.Tables)
                    foreach (var fk in t.ForeignKeys)
                    {
                        var target2 = ResolveTable(catalog, byPath, s.Name, fk);
                        if (target2 == null) { w.Warnings.Add($"{s.Name}.{t.Name}: foreign key to {fk.RefTable} skipped (table not found)"); continue; }
                        w.CreateForeignKey(tables[t], target2, tables[t].Owner!, fk, tables[t].Get(DbFields.QualifiedName));
                    }
            var diagrams = new List<Diagram>();
            if (options.DiagramPerSchema)
                foreach (var sp in db.OwnedElements.Where(DbModel.IsSchema).ToList())
                {
                    var ts = sp.OwnedElements.Where(e => e.Kind == ElementKind.Table).ToList();
                    if (ts.Count == 0) continue;
                    diagrams.Add(DbDiagrams.Build(m, sp, $"{sp.Name} – tables", ts));
                }
            return new DbImportResult(db, catalog.Schemas.Count, tables.Count, w.Columns, w.ForeignKeys, w.Others, diagrams, w.Warnings);
        }
    }

    internal static Element? ResolveTable(DbCatalog catalog, Dictionary<string, Element> byPath, string fromSchema, DbForeignKeyDef fk)
    {
        if (byPath.TryGetValue(DbPaths.Table(fk.RefSchema ?? fromSchema, fk.RefTable), out var t)) return t;
        if (fk.RefSchema == null)
            foreach (var s in catalog.Schemas)
                if (byPath.TryGetValue(DbPaths.Table(s.Name, fk.RefTable), out t)) return t;
        return null;
    }
}

/// <summary>Database diagrams laid out automatically (one per schema, one per subject area).</summary>
public static class DbDiagrams
{
    /// <summary>Creates a database diagram in <paramref name="owner"/> showing <paramref name="tables"/>, laid out by the auto-diagrammer.</summary>
    public static Diagram Build(ModelRepository m, Element owner, string name, IReadOnlyList<Element> tables, Diagram? existing = null)
    {
        var planned = new PlannedDiagram
        {
            Type = DiagramType.Database, Owner = owner, Name = name, Elements = tables, Existing = existing,
            Key = AutoDiagrammer.KeyFor(DiagramType.Database, owner),
            Purpose = $"The tables of {owner.DisplayName} with their keys and foreign keys.",
        };
        return AutoDiagrammer.Build(m, planned);
    }

    /// <summary>A diagram of the tables of a subject area, owned by the area itself, filtered to it.</summary>
    public static Diagram BuildForGroup(ModelRepository m, Element group)
    {
        using (m.History.Transaction($"Diagram {group.Name}"))
        {
            var existing = group.OwnedElements.OfType<Diagram>().FirstOrDefault(d => d.DiagramType == DiagramType.Database && d.Get(AutoDiagrammer.MarkerKey) == AutoDiagrammer.KeyFor(DiagramType.Database, group));
            var d = Build(m, group, $"{group.Name} – subject area", DbModel.Members(m, group), existing);
            return d;
        }
    }
}
