namespace Meridian.Core.DataModeling;

/// <summary>
/// A database schema independent of the model: what a DDL script, a live connection, a .dacpac or the model itself
/// describes. Imports build one and apply it to the model; DDL generation, migration scripts and refresh reviews compare
/// two of them. Names keep the case of their source; comparisons ignore case.
/// </summary>
public sealed class DbCatalog
{
    public string Name { get; set; } = "";
    /// <summary>postgresql, sqlserver, mysql, sqlite or oracle (empty when unknown).</summary>
    public string Dialect { get; set; } = "";
    public List<DbSchemaDef> Schemas { get; } = new();
    /// <summary>Statements or objects that were skipped, with the reason.</summary>
    public List<string> Warnings { get; } = new();

    public DbSchemaDef Schema(string name)
    {
        var s = Schemas.FirstOrDefault(x => Eq(x.Name, name));
        if (s == null) Schemas.Add(s = new DbSchemaDef { Name = name });
        return s;
    }

    public DbSchemaDef? FindSchema(string name) => Schemas.FirstOrDefault(x => Eq(x.Name, name));

    public IEnumerable<DbTableDef> AllTables => Schemas.SelectMany(s => s.Tables);

    public DbTableDef? FindTable(string? schema, string table)
    {
        if (schema != null && FindSchema(schema)?.FindTable(table) is { } t) return t;
        return Schemas.Select(s => s.FindTable(table)).FirstOrDefault(x => x != null);
    }

    public static bool Eq(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    public int ObjectCount => Schemas.Sum(s => s.Tables.Count + s.Sequences.Count + s.Types.Count + s.Routines.Count);
}

public sealed class DbSchemaDef
{
    public string Name { get; set; } = "";
    public string? Comment { get; set; }
    public List<DbTableDef> Tables { get; } = new();
    public List<DbSequenceDef> Sequences { get; } = new();
    public List<DbTypeDef> Types { get; } = new();
    public List<DbRoutineDef> Routines { get; } = new();

    public DbTableDef? FindTable(string name) => Tables.FirstOrDefault(t => DbCatalog.Eq(t.Name, name));

    public DbTableDef Table(string name)
    {
        var t = FindTable(name);
        if (t == null) Tables.Add(t = new DbTableDef { Name = name });
        return t;
    }
}

public sealed class DbTableDef
{
    public string Name { get; set; } = "";
    /// <summary>Id of the model element it was read from (model extracts only): same id + another name = a rename.</summary>
    public string? Id { get; set; }
    /// <summary>"" (table), "view" or "materializedView".</summary>
    public string Kind { get; set; } = "";
    public string? Comment { get; set; }
    /// <summary>Views: the query.</summary>
    public string? Definition { get; set; }
    public List<DbColumnDef> Columns { get; } = new();
    public string? PrimaryKeyName { get; set; }
    public List<string> PrimaryKey { get; } = new();
    public List<DbForeignKeyDef> ForeignKeys { get; } = new();
    public List<DbIndexDef> Indexes { get; } = new();
    public List<DbConstraintDef> Constraints { get; } = new();
    public List<DbTriggerDef> Triggers { get; } = new();

    public bool IsView => Kind is DbFields.View or DbFields.MaterializedView;

    public DbColumnDef? FindColumn(string name) => Columns.FirstOrDefault(c => DbCatalog.Eq(c.Name, name));

    public bool IsPrimaryKey(string column) => PrimaryKey.Any(p => DbCatalog.Eq(p, column));
}

public sealed class DbColumnDef
{
    public string Name { get; set; } = "";
    /// <summary>Id of the model element it was read from (model extracts only): same id + another name = a rename.</summary>
    public string? Id { get; set; }
    /// <summary>The full type as written, e.g. "varchar(200)" or "numeric(10,2)".</summary>
    public string Type { get; set; } = "";
    public bool Nullable { get; set; } = true;
    public string? Default { get; set; }
    /// <summary>Name of the default's constraint (SQL Server keeps defaults as named constraints), when known.</summary>
    public string? DefaultName { get; set; }
    /// <summary>"always", "byDefault" or "autoIncrement".</summary>
    public string? Identity { get; set; }
    public string? Generated { get; set; }
    public string? Collation { get; set; }
    public string? Comment { get; set; }
    /// <summary>A single-column unique constraint declared on the column.</summary>
    public bool Unique { get; set; }
}

public sealed class DbForeignKeyDef
{
    public string? Name { get; set; }
    public List<string> Columns { get; } = new();
    public string? RefSchema { get; set; }
    public string RefTable { get; set; } = "";
    public List<string> RefColumns { get; } = new();
    public string? OnDelete { get; set; }
    public string? OnUpdate { get; set; }

    /// <summary>Identity of a foreign key when it has no name: its columns and the table it references.</summary>
    public string Signature => $"{string.Join(",", Columns).ToLowerInvariant()}->{RefTable.ToLowerInvariant()}";
}

public sealed class DbIndexDef
{
    public string Name { get; set; } = "";
    public List<string> Columns { get; } = new();
    public bool Unique { get; set; }
    public string? Method { get; set; }
    /// <summary>Partial index condition.</summary>
    public string? Where { get; set; }
}

public sealed class DbConstraintDef
{
    public string Name { get; set; } = "";
    /// <summary>"unique" or "check".</summary>
    public string Type { get; set; } = DbFields.UniqueConstraint;
    public List<string> Columns { get; } = new();
    public string? Expression { get; set; }
}

public sealed class DbSequenceDef
{
    public string Name { get; set; } = "";
    public string? Type { get; set; }
    public string? Start { get; set; }
    public string? Increment { get; set; }
    public string? Min { get; set; }
    public string? Max { get; set; }
    public bool Cycle { get; set; }
}

public sealed class DbTypeDef
{
    public string Name { get; set; } = "";
    /// <summary>"enum" or "domain".</summary>
    public string Kind { get; set; } = "enum";
    public List<string> Values { get; } = new();
    public string? BaseType { get; set; }
    public string? Check { get; set; }
    public bool NotNull { get; set; }
    public string? Comment { get; set; }
}

public sealed class DbRoutineDef
{
    public string Name { get; set; } = "";
    /// <summary>"function" or "procedure".</summary>
    public string Kind { get; set; } = DbFields.Function;
    /// <summary>The parameter list, e.g. "(p_id integer, p_note text)".</summary>
    public string Signature { get; set; } = "()";
    public string? Returns { get; set; }
    public string? Language { get; set; }
    public string? Comment { get; set; }
    /// <summary>
    /// The body as the source wrote it: what follows AS in T-SQL, the dollar-quoted text in PostgreSQL, the BEGIN … END of
    /// MySQL, the declarations and block after IS / AS in Oracle. Null when only the signature is known.
    /// </summary>
    public string? Body { get; set; }
    /// <summary>Characteristics between the signature and the body (e.g. "STABLE SECURITY DEFINER", "DETERMINISTIC READS SQL DATA", "WITH SCHEMABINDING").</summary>
    public string? Options { get; set; }
}

public sealed class DbTriggerDef
{
    public string Name { get; set; } = "";
    /// <summary>BEFORE, AFTER or INSTEAD OF.</summary>
    public string Timing { get; set; } = "AFTER";
    /// <summary>INSERT, UPDATE, DELETE (comma separated).</summary>
    public string Events { get; set; } = "INSERT";
    /// <summary>ROW or STATEMENT.</summary>
    public string? Level { get; set; }
    /// <summary>
    /// The action as the source wrote it, after the header: "EXECUTE FUNCTION audit()" (PostgreSQL, with a WHEN (…) in front
    /// when there is one), the T-SQL batch after AS, MySQL's statement or BEGIN … END, Oracle's and SQLite's [WHEN …] block.
    /// </summary>
    public string? Action { get; set; }
}
