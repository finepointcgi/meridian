using Meridian.Core.Model;

namespace Meridian.Core.DataModeling;

/// <summary>
/// Field keys of the data-modelling metamodel. Everything is stored in the ordinary field bag of existing kinds (plus the
/// appended <see cref="ElementKind.DbIndex"/>, <see cref="ElementKind.DbConstraint"/>, <see cref="ElementKind.Sequence"/>,
/// <see cref="ElementKind.Routine"/> and <see cref="ElementKind.Trigger"/>), so project files stay backward compatible:
/// <list type="bullet">
/// <item>Database / catalog, schema and subject area (group): a <see cref="ElementKind.Package"/> with <see cref="Role"/>.</item>
/// <item>Table, view, materialized view: a <see cref="ElementKind.Table"/> with subtype "" / "view" / "materializedView".</item>
/// <item>Column: <see cref="ElementKind.Column"/> with the existing sqlType / pk / nullable / unique / defaultValue fields.</item>
/// <item>Foreign key: the existing <see cref="ElementKind.ForeignKey"/> relationship (fkColumns, refColumns, on delete / update).</item>
/// <item>Enum type: <see cref="ElementKind.Enumeration"/> with literals; domain: <see cref="ElementKind.DataType"/> with a base type.</item>
/// </list>
/// </summary>
public static class DbFields
{
    /// <summary>Packages: "database", "schema" or "subjectArea".</summary>
    public const string Role = "db.role";
    public const string RoleDatabase = "database";
    public const string RoleSchema = "schema";
    public const string RoleGroup = "subjectArea";

    /// <summary>Database packages: the dialect the model targets (postgresql, sqlserver, mysql, sqlite, oracle).</summary>
    public const string Dialect = "db.dialect";

    /// <summary>Where a database was imported from ("ddl:orders.sql", "connection:&lt;id&gt;", "dacpac:x.dacpac").</summary>
    public const string Source = "db.source";

    /// <summary>The match key for re-imports: "database.schema.table[.column]" as the source named it.</summary>
    public const string QualifiedName = "db.qualifiedName";

    /// <summary>The comment the source had at the last import (documentation is only replaced while it still equals this).</summary>
    public const string Comment = "db.comment";

    /// <summary>Tables (and views): comma separated ids of the subject areas the table belongs to.</summary>
    public const string Groups = "db.groups";

    /// <summary>Tables: name of the primary key constraint.</summary>
    public const string PkName = "db.pkName";

    /// <summary>Views: the SELECT of the view; routines: the body (see <see cref="DbRoutineDef.Body"/>); triggers: the action.</summary>
    public const string Definition = "db.definition";

    /// <summary>Columns: identity / auto increment: "always", "byDefault" or "autoIncrement".</summary>
    public const string Identity = "db.identity";

    /// <summary>Columns: generated (computed) column expression.</summary>
    public const string Generated = "db.generated";

    public const string Collation = "db.collation";

    /// <summary>Indexes and constraints: comma separated column names (expressions allowed for indexes).</summary>
    public const string Columns = "db.columns";

    /// <summary>Check constraints and domains: the condition; indexes: the WHERE of a partial index.</summary>
    public const string Expression = "db.expression";

    /// <summary>Indexes: access method (btree, hash, gin, clustered …).</summary>
    public const string Method = "db.method";

    /// <summary>Foreign keys: referential actions ("CASCADE", "SET NULL", "SET DEFAULT", "RESTRICT", "NO ACTION").</summary>
    public const string OnDelete = "db.onDelete";
    public const string OnUpdate = "db.onUpdate";

    // Sequences
    public const string Start = "db.start";
    public const string Increment = "db.increment";
    public const string MinValue = "db.min";
    public const string MaxValue = "db.max";
    public const string Cycle = "db.cycle";

    /// <summary>Routines: return type; domains: base type.</summary>
    public const string Returns = "db.returns";
    public const string BaseType = "db.baseType";
    public const string Language = "db.language";
    /// <summary>Routines: characteristics written between the signature and the body (STABLE, SECURITY DEFINER, DETERMINISTIC …).</summary>
    public const string Options = "db.options";

    /// <summary>Columns: the name of the default's constraint (SQL Server), so migrations can drop it.</summary>
    public const string DefaultName = "db.defaultName";

    // Triggers
    public const string Timing = "db.timing";     // BEFORE / AFTER / INSTEAD OF
    public const string Events = "db.events";     // INSERT, UPDATE, DELETE
    public const string Level = "db.level";       // ROW / STATEMENT

    /// <summary>Database diagrams and table shapes (node style): "all" (default), "keys" or "none".</summary>
    public const string ShowColumns = "db.showColumns";

    /// <summary>Database diagrams: id of the subject area whose tables are shown (others are hidden while it is set).</summary>
    public const string GroupFilter = "db.groupFilter";

    /// <summary>Database packages or the model root: naming convention checked by validation ("snake_case", "PascalCase", "camelCase").</summary>
    public const string Naming = "db.naming";

    public static readonly IReadOnlyList<string> Dialects = ["postgresql", "sqlserver", "mysql", "sqlite", "oracle"];

    public static readonly IReadOnlyList<string> ReferentialActions = ["", "CASCADE", "SET NULL", "SET DEFAULT", "RESTRICT", "NO ACTION"];

    public static readonly IReadOnlyList<string> NamingConventions = ["", "snake_case", "PascalCase", "camelCase"];

    /// <summary>Subtypes of a table.</summary>
    public const string View = "view";
    public const string MaterializedView = "materializedView";

    public static readonly IReadOnlyList<string> TableKinds = ["", View, MaterializedView];

    public const string UniqueConstraint = "unique";
    public const string CheckConstraint = "check";
    public static readonly IReadOnlyList<string> ConstraintKinds = [UniqueConstraint, CheckConstraint];

    public const string Procedure = "procedure";
    public const string Function = "function";
    public static readonly IReadOnlyList<string> RoutineKinds = [Function, Procedure];

    /// <summary>The colours offered for subject areas.</summary>
    public static readonly IReadOnlyList<string> GroupColors = ["#4f8cff", "#3fbf7f", "#e0a85a", "#e5c54a", "#9a8cff", "#f08f8f", "#4ec9d6", "#c77dff", "#8a93a3"];

    /// <summary>Fields shown in the inspector's Database section, per kind.</summary>
    public static IEnumerable<string> EditableFields(ElementKind k)
    {
        switch (k)
        {
            case ElementKind.Table:
                yield return Definition; yield return PkName; yield return QualifiedName;
                break;
            case ElementKind.Column:
                yield return Identity; yield return Generated; yield return Collation; yield return DefaultName; yield return QualifiedName;
                break;
            case ElementKind.ForeignKey:
                yield return OnDelete; yield return OnUpdate;
                break;
            case ElementKind.DbIndex:
                yield return Columns; yield return FieldKeys.Unique; yield return Method; yield return Expression;
                break;
            case ElementKind.DbConstraint:
                yield return Columns; yield return Expression;
                break;
            case ElementKind.Sequence:
                yield return FieldKeys.SqlType; yield return Start; yield return Increment; yield return MinValue; yield return MaxValue; yield return Cycle;
                break;
            case ElementKind.Routine:
                yield return FieldKeys.Signature; yield return Returns; yield return Language; yield return Options; yield return Definition;
                break;
            case ElementKind.Trigger:
                yield return Timing; yield return Events; yield return Level; yield return Definition;
                break;
            case ElementKind.DataType:
                yield return BaseType; yield return Expression;
                break;
        }
    }

    public static string? Label(string key) => key switch
    {
        Role => "Data model role",
        Dialect => "Dialect",
        Source => "Imported from",
        QualifiedName => "Database name (match key)",
        Comment => "Database comment",
        Groups => "Subject areas",
        PkName => "Primary key name",
        Definition => "Definition",
        Identity => "Identity",
        Generated => "Generated as",
        Collation => "Collation",
        Columns => "Columns",
        Expression => "Expression",
        Method => "Method",
        OnDelete => "On delete",
        OnUpdate => "On update",
        Start => "Start",
        Increment => "Increment",
        MinValue => "Minimum",
        MaxValue => "Maximum",
        Cycle => "Cycle",
        Returns => "Returns",
        BaseType => "Base type",
        Language => "Language",
        Options => "Options",
        DefaultName => "Default constraint name",
        Timing => "Timing",
        Events => "Events",
        Level => "For each",
        ShowColumns => "Columns shown",
        GroupFilter => "Subject area filter",
        Naming => "Naming convention",
        FieldKeys.SqlType => "Data type",
        _ => null,
    };

    public static string? Help(string key) => key switch
    {
        FieldKeys.SqlType => "e.g. varchar(200), numeric(10,2), timestamptz",
        Columns => "Comma separated, e.g. customer_id, created_at",
        Expression => "e.g. amount > 0",
        Generated => "Expression of a computed column",
        QualifiedName => "Used to match this element when the database is imported again",
        Options => "e.g. STABLE SECURITY DEFINER, DETERMINISTIC, WITH SCHEMABINDING",
        DefaultName => "SQL Server: the DEFAULT constraint's name, used to drop it in migration scripts",
        _ => null,
    };
}
