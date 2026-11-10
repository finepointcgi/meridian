using Meridian.Core.Model;

namespace Meridian.Core.DataModeling;

/// <summary>What re-importing a source would change in a database already in the model.</summary>
public sealed class DbRefreshPlan
{
    public required Element Database { get; init; }
    public required DbCatalog Incoming { get; init; }
    public required DbExtract Current { get; init; }
    public required IReadOnlyList<DbChange> Changes { get; init; }

    public bool IsEmpty => Changes.Count == 0;

    /// <summary>The model element a change concerns (null for additions): the element itself, else its table.</summary>
    public Element? ElementOf(DbChange c) =>
        Current.Index.TryGetValue(c.Path, out var e) ? e
        : c.Table != null && Current.Index.TryGetValue(DbPaths.Table(c.Schema, c.Table), out var t) ? t : null;
}

public sealed record DbRefreshResult(int Applied, IReadOnlyList<string> AddedIds, IReadOnlyList<string> ChangedIds, IReadOnlyList<string> Warnings);

/// <summary>
/// Refresh / re-import: compares a database in the model with a fresh catalog of its source and applies the changes the
/// user accepts as ONE undo step. Matching uses the names the source had (<see cref="DbFields.QualifiedName"/>), so
/// elements renamed in Meridian still match. Documentation written in Meridian, subject areas and diagram layout are kept.
/// </summary>
public static class DbRefresh
{
    public static DbRefreshPlan Plan(ModelRepository m, Element database, DbCatalog incoming)
    {
        var current = DbExtract.FromModel(m, database, options: new DbExtractOptions { SourceNames = true });
        var changes = DbDiff.Compare(current.Catalog, incoming);
        return new DbRefreshPlan { Database = database, Incoming = incoming, Current = current, Changes = changes };
    }

    /// <summary>Applies <paramref name="selected"/> (all changes when null). Child additions under an added table come with the table.</summary>
    public static DbRefreshResult Apply(ModelRepository m, DbRefreshPlan plan, IEnumerable<DbChange>? selected = null)
    {
        var chosen = (selected ?? plan.Changes).ToList();
        var added = new List<string>();
        var changed = new List<string>();
        var db = plan.Database;
        var w = new DbModelWriter(m, db, db.Get(DbFields.QualifiedName) is { Length: > 0 } q ? q : db.Name);
        var index = plan.Current.Index;
        var tables = new Dictionary<string, Element>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, e) in index) if (e.Kind == ElementKind.Table) tables[path] = e;
        var pendingFks = new List<(Element Table, string Schema, DbForeignKeyDef Fk)>();
        var applied = 0;

        Element TableFor(DbChange c)
        {
            var path = DbPaths.Table(c.Schema, c.Table!);
            if (tables.TryGetValue(path, out var t) && m.Contains(t)) return t;
            // The table itself is new but was not selected: bring it in (it is needed for the child).
            var def = plan.Incoming.FindSchema(c.Schema)?.FindTable(c.Table!) ?? throw new InvalidOperationException($"Table {c.Table} not found");
            t = w.CreateTable(w.Schema(c.Schema), c.Schema, def);
            tables[path] = t;
            added.Add(t.Id);
            foreach (var fk in def.ForeignKeys) pendingFks.Add((t, c.Schema, fk));
            return t;
        }

        using (m.History.Transaction($"Refresh {db.Name} ({chosen.Count} change{(chosen.Count == 1 ? "" : "s")})"))
        {
            // Removals first (children before their tables), then additions and changes; foreign keys last.
            foreach (var c in chosen.Where(c => c.Kind == DbChangeKind.Removed).OrderBy(c => c.Type is DbObjectType.Table or DbObjectType.View or DbObjectType.Schema ? 1 : 0))
            {
                if (index.TryGetValue(c.Path, out var e) && m.Contains(e))
                {
                    if (c.Type == DbObjectType.Schema && e.OwnedElements.Any(x => x is not Diagram && !index.Values.Contains(x))) { w.Warnings.Add($"Schema {c.Name} kept: it holds elements added in Meridian"); continue; }
                    m.Delete(e);
                    applied++;
                }
                else if (c.Type == DbObjectType.PrimaryKey && c.Table != null && tables.TryGetValue(DbPaths.Table(c.Schema, c.Table), out var t) && m.Contains(t))
                {
                    w.SetPrimaryKey(t, (DbTableDef)c.After!);
                    changed.Add(t.Id);
                    applied++;
                }
            }
            foreach (var c in chosen.Where(c => c.Kind != DbChangeKind.Removed).OrderBy(Order))
            {
                switch (c.Type)
                {
                    case DbObjectType.Schema:
                    {
                        var s = w.Schema(c.Schema, ((DbSchemaDef)c.After!).Comment);
                        added.Add(s.Id);
                        break;
                    }
                    case DbObjectType.Table or DbObjectType.View:
                    {
                        var def = (DbTableDef)c.After!;
                        var path = DbPaths.Table(c.Schema, def.Name);
                        if (c.Kind == DbChangeKind.Added)
                        {
                            if (tables.ContainsKey(path)) break;   // already brought in for a child
                            var t = w.CreateTable(w.Schema(c.Schema), c.Schema, def);
                            tables[path] = t;
                            added.Add(t.Id);
                            foreach (var fk in def.ForeignKeys) pendingFks.Add((t, c.Schema, fk));
                        }
                        else if (index.TryGetValue(path, out var t))
                        {
                            w.UpdateTable(t, def);
                            changed.Add(t.Id);
                        }
                        break;
                    }
                    case DbObjectType.Column:
                    {
                        var def = (DbColumnDef)c.After!;
                        var table = TableFor(c);
                        var tdef = plan.Incoming.FindSchema(c.Schema)?.FindTable(c.Table!);
                        var pk = tdef?.IsPrimaryKey(def.Name) ?? false;
                        if (c.Kind == DbChangeKind.Added)
                        {
                            if (DbModel.Column(table, def.Name) != null && added.Contains(table.Id)) break;
                            var col = w.CreateColumn(table, c.Schema, tdef ?? new DbTableDef { Name = c.Table! }, def);
                            added.Add(col.Id);
                        }
                        else if (index.TryGetValue(c.Path, out var col))
                        {
                            w.UpdateColumn(col, def, pk || DbModel.IsPk(col));
                            changed.Add(col.Id);
                        }
                        if (!added.Contains(table.Id)) changed.Add(table.Id);
                        break;
                    }
                    case DbObjectType.PrimaryKey:
                    {
                        var t = TableFor(c);
                        w.SetPrimaryKey(t, (DbTableDef)c.After!);
                        changed.Add(t.Id);
                        break;
                    }
                    case DbObjectType.ForeignKey:
                    {
                        var def = (DbForeignKeyDef)c.After!;
                        var t = TableFor(c);
                        if (c.Kind == DbChangeKind.Changed && index.TryGetValue(c.Path, out var old) && old is Relationship r)
                        {
                            if (DbCatalog.Eq(((DbForeignKeyDef)c.Before!).RefTable, def.RefTable)) { w.UpdateForeignKey(r, def); changed.Add(r.Id); break; }
                            m.Delete(r);
                        }
                        if (!added.Contains(t.Id) || !pendingFks.Any(p => ReferenceEquals(p.Fk, def))) pendingFks.Add((t, c.Schema, def));
                        break;
                    }
                    case DbObjectType.Index:
                    {
                        var def = (DbIndexDef)c.After!;
                        var t = TableFor(c);
                        if (c.Kind == DbChangeKind.Added) { if (!added.Contains(t.Id)) added.Add(w.CreateIndex(t, c.Schema, new DbTableDef { Name = c.Table! }, def).Id); }
                        else if (index.TryGetValue(c.Path, out var e)) { w.UpdateIndex(e, def); changed.Add(e.Id); }
                        break;
                    }
                    case DbObjectType.Constraint:
                    {
                        var def = (DbConstraintDef)c.After!;
                        var t = TableFor(c);
                        if (c.Kind == DbChangeKind.Added) { if (!added.Contains(t.Id)) added.Add(w.CreateConstraint(t, c.Schema, new DbTableDef { Name = c.Table! }, def).Id); }
                        else if (index.TryGetValue(c.Path, out var e)) { w.UpdateConstraint(e, def); changed.Add(e.Id); }
                        break;
                    }
                    case DbObjectType.Trigger:
                    {
                        var def = (DbTriggerDef)c.After!;
                        var t = TableFor(c);
                        if (c.Kind == DbChangeKind.Added) { if (!added.Contains(t.Id)) added.Add(w.CreateTrigger(t, c.Schema, new DbTableDef { Name = c.Table! }, def).Id); }
                        else if (index.TryGetValue(c.Path, out var e)) { w.UpdateTrigger(e, def); changed.Add(e.Id); }
                        break;
                    }
                    case DbObjectType.Sequence:
                    {
                        var def = (DbSequenceDef)c.After!;
                        if (c.Kind == DbChangeKind.Added) added.Add(w.CreateSequence(w.Schema(c.Schema), c.Schema, def).Id);
                        else if (index.TryGetValue(c.Path, out var e)) { w.UpdateSequence(e, def); changed.Add(e.Id); }
                        break;
                    }
                    case DbObjectType.Type:
                    {
                        var def = (DbTypeDef)c.After!;
                        if (c.Kind == DbChangeKind.Added) added.Add(w.CreateType(w.Schema(c.Schema), c.Schema, def).Id);
                        else if (index.TryGetValue(c.Path, out var e)) { w.UpdateType(e, def); changed.Add(e.Id); }
                        break;
                    }
                    case DbObjectType.Routine:
                    {
                        var def = (DbRoutineDef)c.After!;
                        if (c.Kind == DbChangeKind.Added) added.Add(w.CreateRoutine(w.Schema(c.Schema), c.Schema, def).Id);
                        else if (index.TryGetValue(c.Path, out var e)) { w.UpdateRoutine(e, def); changed.Add(e.Id); }
                        break;
                    }
                }
                applied++;
            }
            foreach (var (table, schema, fk) in pendingFks)
            {
                var target = DbImport.ResolveTable(plan.Incoming, tables, schema, fk)
                             ?? db.Descendants().FirstOrDefault(e => e.Kind == ElementKind.Table && DbCatalog.Eq(e.Name, fk.RefTable));
                if (target == null) { w.Warnings.Add($"{table.Name}: foreign key to {fk.RefTable} skipped (table not in the model)"); continue; }
                var r = w.CreateForeignKey(table, target, table.Owner!, fk, table.Get(DbFields.QualifiedName));
                if (r != null) added.Add(r.Id);
            }
        }
        return new DbRefreshResult(applied, added.Distinct().ToList(), changed.Distinct().Except(added).ToList(), w.Warnings);
    }

    private static int Order(DbChange c) => c.Type switch
    {
        DbObjectType.Schema => 0,
        DbObjectType.Type or DbObjectType.Sequence => 1,
        DbObjectType.Table or DbObjectType.View => 2,
        DbObjectType.Column => 3,
        DbObjectType.PrimaryKey => 4,
        DbObjectType.ForeignKey => 6,
        _ => 5,
    };
}
