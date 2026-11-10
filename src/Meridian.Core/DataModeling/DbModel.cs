using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.DataModeling;

/// <summary>
/// Queries and edits of data models: databases, schemas, subject areas (groups), tables, columns and keys. Every edit goes
/// through <see cref="ModelRepository"/>, so it is undoable and replicates; multi-step edits are one undo step.
/// </summary>
public static class DbModel
{
    // ------------------------------------------------------------------ roles

    public static string RoleOf(Element e) => e.Kind == ElementKind.Package ? e.Get(DbFields.Role) : "";
    public static bool IsDatabase(Element e) => RoleOf(e) == DbFields.RoleDatabase;
    public static bool IsSchema(Element e) => RoleOf(e) == DbFields.RoleSchema;
    public static bool IsGroup(Element e) => RoleOf(e) == DbFields.RoleGroup;
    public static bool IsView(Element e) => e.Kind == ElementKind.Table && e.Get(FieldKeys.Subtype) is DbFields.View or DbFields.MaterializedView;

    /// <summary>Display name of a data-model element's kind ("Schema", "View", "Unique constraint" …), or null for other elements.</summary>
    public static string? KindName(Element e) => e.Kind switch
    {
        ElementKind.Package when IsDatabase(e) => "Database",
        ElementKind.Package when IsSchema(e) => "Schema",
        ElementKind.Package when IsGroup(e) => "Subject area",
        ElementKind.Table when e.Get(FieldKeys.Subtype) == DbFields.View => "View",
        ElementKind.Table when e.Get(FieldKeys.Subtype) == DbFields.MaterializedView => "Materialized view",
        ElementKind.DbConstraint => e.Get(FieldKeys.Subtype) == DbFields.CheckConstraint ? "Check constraint" : "Unique constraint",
        ElementKind.Routine => e.Get(FieldKeys.Subtype) == DbFields.Procedure ? "Stored procedure" : "Function",
        _ => null,
    };

    /// <summary>True when the model has any table, database or schema (the Database ribbon group shows).</summary>
    public static bool HasDataModel(ModelRepository m) =>
        m.OfKind(ElementKind.Table).Any() || m.OfKind(ElementKind.Package).Any(p => RoleOf(p).Length > 0);

    public static IEnumerable<Element> Databases(ModelRepository m) => m.OfKind(ElementKind.Package).Where(IsDatabase);
    public static IEnumerable<Element> Schemas(ModelRepository m) => m.OfKind(ElementKind.Package).Where(IsSchema);

    /// <summary>Every subject area, by name.</summary>
    public static IReadOnlyList<Element> Groups(ModelRepository m) =>
        m.OfKind(ElementKind.Package).Where(IsGroup).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public static IEnumerable<Element> Tables(ModelRepository m) => m.OfKind(ElementKind.Table);

    /// <summary>Tables (and views) under <paramref name="scope"/>; for a subject area, its members.</summary>
    public static IEnumerable<Element> TablesIn(ModelRepository m, Element scope) =>
        IsGroup(scope) ? Members(m, scope)
        : scope.Kind == ElementKind.Table ? [scope]
        : scope.Descendants().Where(e => e.Kind == ElementKind.Table);

    public static Element? SchemaOf(Element e) => e.AncestorsAndSelf().Skip(e.Kind == ElementKind.Package ? 0 : 1).FirstOrDefault(IsSchema);
    public static Element? DatabaseOf(Element e) => e.AncestorsAndSelf().FirstOrDefault(IsDatabase);

    public static IEnumerable<Element> Columns(Element table) => table.OwnedElements.Where(c => c.Kind == ElementKind.Column);
    public static IEnumerable<Element> Indexes(Element table) => table.OwnedElements.Where(c => c.Kind == ElementKind.DbIndex);
    public static IEnumerable<Element> Constraints(Element table) => table.OwnedElements.Where(c => c.Kind == ElementKind.DbConstraint);
    public static IEnumerable<Element> Triggers(Element table) => table.OwnedElements.Where(c => c.Kind == ElementKind.Trigger);

    public static bool IsPk(Element column) => column.Get(FieldKeys.PrimaryKey) == "true";
    public static bool IsNullable(Element column) => column.Get(FieldKeys.Nullable) == "true" && !IsPk(column);
    public static bool IsUnique(Element column) => column.Get(FieldKeys.Unique) == "true";

    public static IReadOnlyList<Element> PrimaryKey(Element table) => Columns(table).Where(IsPk).ToList();

    /// <summary>Names of the columns of <paramref name="table"/> that take part in one of its foreign keys.</summary>
    public static HashSet<string> ForeignKeyColumns(ModelRepository m, Element table)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fk in m.Outgoing(table, ElementKind.ForeignKey))
            foreach (var c in SplitList(fk.Get(FieldKeys.FkColumns))) set.Add(c);
        return set;
    }

    /// <summary>The columns of a foreign key on its source table (names that resolve).</summary>
    public static IReadOnlyList<Element> FkColumns(Relationship fk) =>
        SplitList(fk.Get(FieldKeys.FkColumns)).Select(n => Column(fk.Source, n)).OfType<Element>().ToList();

    /// <summary>The referenced columns of a foreign key (its refColumns, else the target's primary key).</summary>
    public static IReadOnlyList<Element> RefColumns(Relationship fk)
    {
        var names = SplitList(fk.Get(FieldKeys.RefColumns));
        return names.Count > 0 ? names.Select(n => Column(fk.Target, n)).OfType<Element>().ToList() : PrimaryKey(fk.Target);
    }

    public static Element? Column(Element table, string name) =>
        Columns(table).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public static List<string> SplitList(string s) =>
        s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static string JoinList(IEnumerable<string> items) => string.Join(", ", items);

    // ------------------------------------------------------------------ names

    /// <summary>
    /// The match key of a data-model element: its stored <see cref="DbFields.QualifiedName"/>, else database.schema.table[.column]
    /// from where it sits in the model.
    /// </summary>
    public static string KeyOf(Element e)
    {
        var stored = e.Get(DbFields.QualifiedName);
        return stored.Length > 0 ? stored : ComputedKey(e);
    }

    public static string ComputedKey(Element e)
    {
        var parts = new List<string>();
        for (var a = e; a != null; a = a.Owner)
        {
            if (a.Kind == ElementKind.Package && !IsDatabase(a) && !IsSchema(a)) continue;
            if (a.Kind == ElementKind.Model) break;
            parts.Add(a.Name);
            if (IsDatabase(a)) break;
        }
        parts.Reverse();
        return string.Join(".", parts);
    }

    // ------------------------------------------------------------------ creating

    public static Element CreateDatabase(ModelRepository m, Element owner, string name, string? dialect = null)
    {
        using (m.History.Transaction($"New database {name}"))
        {
            var db = m.Create(owner, ElementKind.Package, name);
            m.Set(db, DbFields.Role, DbFields.RoleDatabase);
            if (!string.IsNullOrEmpty(dialect)) m.Set(db, DbFields.Dialect, dialect);
            return db;
        }
    }

    public static Element CreateSchema(ModelRepository m, Element database, string name)
    {
        using (m.History.Transaction($"New schema {name}"))
        {
            var s = m.Create(database, ElementKind.Package, name);
            m.Set(s, DbFields.Role, DbFields.RoleSchema);
            return s;
        }
    }

    /// <summary>A new table with an "id" primary key column, in <paramref name="owner"/> (a schema or any package).</summary>
    public static Element CreateTable(ModelRepository m, Element owner, string? name = null, string? subtype = null, string pkType = "integer")
    {
        using (m.History.Transaction("New table"))
        {
            var t = m.Create(owner, ElementKind.Table, name ?? UniqueName(owner, subtype is DbFields.View or DbFields.MaterializedView ? "new_view" : "new_table"));
            if (!string.IsNullOrEmpty(subtype)) m.Set(t, FieldKeys.Subtype, subtype);
            else
            {
                var id = m.Create(t, ElementKind.Column, "id");
                m.Set(id, FieldKeys.SqlType, pkType);
                m.Set(id, FieldKeys.PrimaryKey, "true");
            }
            return t;
        }
    }

    private static string UniqueName(Element owner, string stem)
    {
        var names = owner.OwnedElements.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(stem)) return stem;
        for (var i = 2; ; i++) if (!names.Contains($"{stem}_{i}")) return $"{stem}_{i}";
    }

    /// <summary>Adds a column (nullable unless it is the first one), named uniquely. One undo step.</summary>
    public static Element AddColumn(ModelRepository m, Element table, string? name = null, string type = "varchar(255)", bool nullable = true)
    {
        using (m.History.Transaction("Add column"))
        {
            var c = m.Create(table, ElementKind.Column, name is { Length: > 0 } n ? n : UniqueName(table, "column"));
            m.Set(c, FieldKeys.SqlType, type);
            if (nullable) m.Set(c, FieldKeys.Nullable, "true");
            return c;
        }
    }

    /// <summary>Marks a column as (part of) the primary key or not. A key column is never nullable.</summary>
    public static void SetPrimaryKey(ModelRepository m, Element column, bool on)
    {
        using (m.History.Transaction(on ? $"Make {column.Name} the primary key" : $"Remove {column.Name} from the primary key"))
        {
            m.Set(column, FieldKeys.PrimaryKey, on ? "true" : null);
            if (on) m.Set(column, FieldKeys.Nullable, null);
        }
    }

    /// <summary>
    /// Fills in a foreign key just drawn between two tables: the referenced columns (the target's primary key, or
    /// <paramref name="targetColumn"/>) and the referencing columns (<paramref name="sourceColumn"/>, else an existing
    /// column named like the target's key, else a new one "&lt;target&gt;_&lt;key&gt;" of the key's type). One undo step.
    /// </summary>
    public static void CompleteForeignKey(ModelRepository m, Relationship fk, Element? sourceColumn = null, Element? targetColumn = null)
    {
        if (fk.Kind != ElementKind.ForeignKey) return;
        using (m.History.Transaction("Complete foreign key"))
        {
            var refs = targetColumn != null ? [targetColumn] : PrimaryKey(fk.Target).ToList();
            if (refs.Count == 0 && Columns(fk.Target).FirstOrDefault() is { } first) refs = [first];
            if (fk.Get(FieldKeys.RefColumns).Length == 0 && refs.Count > 0) m.Set(fk, FieldKeys.RefColumns, JoinList(refs.Select(r => r.Name)));
            if (fk.Get(FieldKeys.FkColumns).Length > 0) return;
            var cols = new List<string>();
            if (sourceColumn != null && refs.Count <= 1) cols.Add(sourceColumn.Name);
            else
            {
                foreach (var r in refs)
                {
                    var stem = ToSnake(fk.Target.Name);
                    var wanted = r.Name.Equals("id", StringComparison.OrdinalIgnoreCase) ? $"{stem}_id" : $"{stem}_{r.Name}";
                    var existing = Column(fk.Source, wanted);
                    if (existing == null && !ReferenceEquals(fk.Source, fk.Target)) existing = Column(fk.Source, r.Name) is { } same && !IsPk(same) ? same : null;
                    if (existing == null)
                    {
                        existing = m.Create(fk.Source, ElementKind.Column, wanted);
                        m.Set(existing, FieldKeys.SqlType, BaseKeyType(r.Get(FieldKeys.SqlType)));
                    }
                    cols.Add(existing.Name);
                }
                if (refs.Count == 0)
                {
                    var name = $"{ToSnake(fk.Target.Name)}_id";
                    var c = Column(fk.Source, name) ?? m.Create(fk.Source, ElementKind.Column, name);
                    if (c.Get(FieldKeys.SqlType).Length == 0) m.Set(c, FieldKeys.SqlType, "integer");
                    cols.Add(c.Name);
                }
            }
            m.Set(fk, FieldKeys.FkColumns, JoinList(cols));
            if (fk.Name.Length == 0) m.Set(fk, FieldKeys.Name, $"fk_{fk.Source.Name}_{fk.Target.Name}".ToLowerInvariant());
        }
    }

    /// <summary>A referencing column has the key's type without identity: serial → integer, bigserial → bigint.</summary>
    public static string BaseKeyType(string type) => type.Trim().ToLowerInvariant() switch
    {
        "" => "integer",
        "serial" or "serial4" => "integer",
        "bigserial" or "serial8" => "bigint",
        "smallserial" or "serial2" => "smallint",
        _ => type,
    };

    public static string ToSnake(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]))) sb.Append('_');
            sb.Append(c is ' ' or '-' ? '_' : char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ subject areas (groups)

    /// <summary>A new subject area in <paramref name="owner"/> (a database package, or any package) with a colour. One undo step.</summary>
    public static Element CreateGroup(ModelRepository m, Element owner, string name, string? color = null)
    {
        using (m.History.Transaction($"New subject area {name}"))
        {
            var g = m.Create(owner, ElementKind.Package, name);
            m.Set(g, DbFields.Role, DbFields.RoleGroup);
            m.Set(g, FieldKeys.Color, color ?? DbFields.GroupColors[Groups(m).Count(x => !ReferenceEquals(x, g)) % DbFields.GroupColors.Count]);
            return g;
        }
    }

    public static IReadOnlyList<string> GroupIds(Element table) => SplitList(table.Get(DbFields.Groups));

    /// <summary>The subject areas a table belongs to (ids that no longer resolve are skipped).</summary>
    public static IReadOnlyList<Element> GroupsOf(ModelRepository m, Element table) =>
        GroupIds(table).Select(m.Find).OfType<Element>().Where(IsGroup).ToList();

    public static IReadOnlyList<Element> Members(ModelRepository m, Element group) =>
        m.OfKind(ElementKind.Table).Where(t => GroupIds(t).Contains(group.Id)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public static bool InGroup(Element table, Element group) => GroupIds(table).Contains(group.Id);

    /// <summary>Adds tables to a subject area (a table may be in several). One undo step; returns how many were added.</summary>
    public static int AddToGroup(ModelRepository m, IEnumerable<Element> tables, Element group)
    {
        if (!IsGroup(group)) throw new InvalidOperationException($"“{group.DisplayName}” is not a subject area.");
        var n = 0;
        using (m.History.Transaction($"Add to {group.Name}"))
            foreach (var t in tables.Where(t => t.Kind == ElementKind.Table).Distinct())
            {
                var ids = GroupIds(t).ToList();
                if (ids.Contains(group.Id)) continue;
                ids.Add(group.Id);
                m.Set(t, DbFields.Groups, string.Join(",", ids));
                n++;
            }
        return n;
    }

    public static int RemoveFromGroup(ModelRepository m, IEnumerable<Element> tables, Element group)
    {
        var n = 0;
        using (m.History.Transaction($"Remove from {group.Name}"))
            foreach (var t in tables.Where(t => t.Kind == ElementKind.Table).Distinct())
            {
                var ids = GroupIds(t).ToList();
                if (!ids.Remove(group.Id)) continue;
                m.Set(t, DbFields.Groups, ids.Count == 0 ? null : string.Join(",", ids));
                n++;
            }
        return n;
    }

    /// <summary>Deletes a subject area and removes it from its tables (the tables stay). One undo step.</summary>
    public static void DeleteGroup(ModelRepository m, Element group)
    {
        using (m.History.Transaction($"Delete subject area {group.Name}"))
        {
            RemoveFromGroup(m, Members(m, group), group);
            foreach (var d in m.Diagrams.Where(d => d.Get(DbFields.GroupFilter) == group.Id).ToList()) m.Set(d, DbFields.GroupFilter, null);
            m.Delete(group);
        }
    }

    /// <summary>The colour of a subject area (a safe default when unset).</summary>
    public static string ColorOf(Element group) => group.Get(FieldKeys.Color) is { Length: > 0 } c ? c : DbFields.GroupColors[0];

    // ------------------------------------------------------------------ diagrams

    /// <summary>Shows only the tables of <paramref name="group"/> on a database diagram (null shows everything). Undoable.</summary>
    public static void FilterDiagram(ModelRepository m, Diagram d, Element? group) => m.Set(d, DbFields.GroupFilter, group?.Id);

    /// <summary>Columns a table shape lists: "all", "keys" (primary and foreign keys) or "none".</summary>
    public static string ShowColumnsOf(DiagramNode n) =>
        n.Style.TryGetValue(DbFields.ShowColumns, out var v) && v is "keys" or "none" ? v : "all";

    /// <summary>Sets what every table on a database diagram lists (and the diagram's default for new shapes). One undo step.</summary>
    public static void SetShowColumns(ModelRepository m, Diagram d, string mode)
    {
        mode = mode is "keys" or "none" ? mode : "all";
        using (m.History.Transaction("Show columns: " + mode))
        {
            m.Set(d, DbFields.ShowColumns, mode == "all" ? null : mode);
            var resized = new Dictionary<DiagramNode, Bounds>();
            foreach (var n in d.Nodes.Where(n => n.Element.Kind == ElementKind.Table).ToList())
            {
                if (ShowColumnsOf(n) != mode) m.SetNodeStyle(d, n, DbFields.ShowColumns, mode == "all" ? null : mode);
                // Shapes fit what they now list (they only ever grow on their own).
                var h = Math.Ceiling(Notation.PreferredHeight(Notation.Content(m, n, d.DiagramType), NodeShape.Rectangle, Notation.LineHeight) + 4);
                if (Math.Abs(n.Bounds.Height - h) > 0.5) resized[n] = n.Bounds with { Height = h };
            }
            if (resized.Count > 0) m.SetBounds(d, resized, "Resize tables");
        }
    }

    /// <summary>Is this node hidden by the diagram's subject-area filter?</summary>
    public static bool FilteredOut(Diagram d, DiagramNode n)
    {
        var g = d.Get(DbFields.GroupFilter);
        return g.Length > 0 && n.Element.Kind == ElementKind.Table && !GroupIds(n.Element).Contains(g);
    }

    /// <summary>Tables shown on <paramref name="d"/>, grouped by the subject areas they belong to (only areas with members on it).</summary>
    public static IReadOnlyList<(Element Group, IReadOnlyList<DiagramNode> Nodes)> GroupFrames(ModelRepository m, Diagram d)
    {
        if (d.DiagramType != DiagramType.Database) return [];
        var byGroup = new Dictionary<string, List<DiagramNode>>(StringComparer.Ordinal);
        foreach (var n in d.Nodes.Where(n => n.Element.Kind == ElementKind.Table))
            foreach (var id in GroupIds(n.Element))
                (byGroup.TryGetValue(id, out var l) ? l : byGroup[id] = new()).Add(n);
        return byGroup.Select(kv => (Group: m.Find(kv.Key), Nodes: (IReadOnlyList<DiagramNode>)kv.Value))
            .Where(x => x.Group is { } g && IsGroup(g)).Select(x => (x.Group!, x.Nodes))
            .OrderBy(x => x.Item1.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The default dialect of a data-model element: its database's, else PostgreSQL.</summary>
    public static string DialectOf(Element e) => DatabaseOf(e)?.Get(DbFields.Dialect) is { Length: > 0 } d ? d : "postgresql";

    /// <summary>Elements a model tree filter for <paramref name="group"/> lists: its tables with their columns' owners.</summary>
    public static IEnumerable<Element> TreeFilter(ModelRepository m, Element group) => Members(m, group);

    /// <summary>Kinds that make up a data model (for tree filters and searches).</summary>
    public static readonly ElementKind[] DataKinds =
        [ElementKind.Table, ElementKind.Sequence, ElementKind.Routine, ElementKind.Trigger, ElementKind.DbIndex, ElementKind.DbConstraint];

    /// <summary>Keyword shown on a table shape: «view», «materialized view», or none.</summary>
    public static string? TableKeyword(Element table) => table.Get(FieldKeys.Subtype) switch
    {
        DbFields.View => "view",
        DbFields.MaterializedView => "materialized view",
        _ => null,
    };

    /// <summary>Does <paramref name="e"/> (or its owner) belong to the database notation, so diagram checks treat it as data?</summary>
    public static bool IsDataElement(Element e) => e.Kind is ElementKind.Table or ElementKind.Column or ElementKind.DbIndex or ElementKind.DbConstraint
        or ElementKind.Sequence or ElementKind.Routine or ElementKind.Trigger || RoleOf(e).Length > 0;
}
