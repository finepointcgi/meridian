using Meridian.Core.DataModeling;
using Meridian.Core.Model;
using Meridian.Serialization.Database;

namespace Meridian.Tests;

/// <summary>Routine and trigger bodies (DDL import, generation, migration), rename detection and drops of constraints with generated names.</summary>
public class DatabaseBodiesTests
{
    private const string PgScript = """
        CREATE TABLE public.account (id integer PRIMARY KEY, balance numeric(12,2) NOT NULL DEFAULT 0, updated_at timestamptz);
        CREATE OR REPLACE FUNCTION public.add_interest(p_rate numeric) RETURNS integer
            LANGUAGE plpgsql STABLE SECURITY DEFINER AS $fn$
        DECLARE n integer;
        BEGIN
            -- a comment; with a semicolon
            UPDATE account SET balance = balance * (1 + p_rate);
            GET DIAGNOSTICS n = ROW_COUNT;
            RETURN n;
        END;
        $fn$;
        CREATE FUNCTION public.touch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN NEW.updated_at := now(); RETURN NEW; END; $$;
        CREATE PROCEDURE public.reset_all() LANGUAGE sql BEGIN ATOMIC UPDATE account SET balance = 0; END;
        CREATE TRIGGER account_touch BEFORE UPDATE ON public.account FOR EACH ROW WHEN (OLD.balance IS DISTINCT FROM NEW.balance) EXECUTE FUNCTION public.touch();
        """;

    private const string SqlServerScript = """
        CREATE TABLE dbo.Account (Id int NOT NULL PRIMARY KEY, Balance decimal(12,2) NOT NULL CONSTRAINT DF_Account_Balance DEFAULT (0), UpdatedAt datetime2 NULL)
        GO
        CREATE PROCEDURE dbo.AddInterest @Rate decimal(5,4), @Count int OUTPUT
        WITH RECOMPILE
        AS
        BEGIN
            SET NOCOUNT ON;
            UPDATE dbo.Account SET Balance = Balance * (1 + @Rate);
            SET @Count = @@ROWCOUNT;
        END
        GO
        CREATE FUNCTION dbo.Total() RETURNS decimal(18,2) WITH SCHEMABINDING AS
        BEGIN
            RETURN (SELECT SUM(Balance) FROM dbo.Account);
        END
        GO
        CREATE TRIGGER dbo.trg_Account_Touch ON dbo.Account AFTER UPDATE AS
        BEGIN
            SET NOCOUNT ON;
            UPDATE a SET UpdatedAt = sysutcdatetime() FROM dbo.Account a JOIN inserted i ON i.Id = a.Id;
            -- more than forty tokens of body: the whole batch is kept
            IF EXISTS (SELECT 1 FROM inserted WHERE Balance < 0) BEGIN RAISERROR('negative', 16, 1); ROLLBACK TRANSACTION; END
        END
        GO
        """;

    private const string MySqlScript = """
        CREATE TABLE account (id int NOT NULL PRIMARY KEY, balance decimal(12,2) NOT NULL DEFAULT 0, updated_at datetime NULL) ENGINE=InnoDB;
        DELIMITER $$
        CREATE PROCEDURE add_interest(IN p_rate DECIMAL(5,4))
        MODIFIES SQL DATA
        BEGIN
          DECLARE n INT DEFAULT 0;
          UPDATE account SET balance = balance * (1 + p_rate);
          IF ROW_COUNT() > 0 THEN SET n = 1; END IF;
        END$$
        CREATE FUNCTION total() RETURNS decimal(18,2) DETERMINISTIC READS SQL DATA
        RETURN (SELECT SUM(balance) FROM account)$$
        CREATE TRIGGER account_bu BEFORE UPDATE ON account FOR EACH ROW
        BEGIN
          SET NEW.updated_at = NOW();
          INSERT INTO account_log (account_id) VALUES (NEW.id);
        END$$
        DELIMITER ;
        CREATE TRIGGER account_ai AFTER INSERT ON account FOR EACH ROW INSERT INTO account_log (account_id) VALUES (NEW.id);
        """;

    private const string OracleScript = """
        CREATE TABLE account (id NUMBER(10) PRIMARY KEY, balance NUMBER(12,2) DEFAULT 0 NOT NULL, updated_at DATE);
        CREATE OR REPLACE PROCEDURE add_interest (p_rate IN NUMBER)
        AUTHID DEFINER
        IS
          n NUMBER := 0;
        BEGIN
          UPDATE account SET balance = balance * (1 + p_rate);
          n := SQL%ROWCOUNT;
        END add_interest;
        /
        CREATE OR REPLACE FUNCTION total RETURN NUMBER IS
          v NUMBER;
        BEGIN
          SELECT SUM(balance) INTO v FROM account;
          RETURN v;
        END;
        /
        CREATE OR REPLACE TRIGGER account_bu BEFORE UPDATE ON account FOR EACH ROW
        WHEN (new.balance <> old.balance)
        BEGIN
          :new.updated_at := SYSDATE;
        END;
        /
        """;

    private const string SqliteScript = """
        CREATE TABLE account (id INTEGER PRIMARY KEY, balance REAL NOT NULL DEFAULT 0, updated_at TEXT);
        CREATE TRIGGER account_bu AFTER UPDATE OF balance ON account FOR EACH ROW WHEN new.balance <> old.balance
        BEGIN
          UPDATE account SET updated_at = datetime('now') WHERE id = new.id;
          SELECT CASE WHEN new.balance < 0 THEN RAISE(ABORT, 'negative') END;
        END;
        """;

    public static TheoryData<string> Dialects => new() { "postgresql", "sqlserver", "mysql", "oracle", "sqlite" };

    private static string Script(string d) => d switch
    {
        "sqlserver" => SqlServerScript, "mysql" => MySqlScript, "oracle" => OracleScript, "sqlite" => SqliteScript, _ => PgScript,
    };

    private static (string Body, string? Options, string? Language) Routine(DbCatalog c, string name)
    {
        var r = c.Schemas.SelectMany(s => s.Routines).Single(x => DbCatalog.Eq(x.Name, name));
        return (r.Body!, r.Options, r.Language);
    }

    [Fact]
    public void Postgres_Keeps_Dollar_Quoted_And_Sql_Standard_Bodies_Options_And_Trigger_When()
    {
        var c = DdlParser.Parse(PgScript, "postgresql");
        var (body, options, lang) = Routine(c, "add_interest");
        Assert.StartsWith("DECLARE n integer;", body.Trim());
        Assert.Contains("-- a comment; with a semicolon", body);
        Assert.EndsWith("END;", body.Trim());
        Assert.Equal("plpgsql", lang);
        Assert.Equal("STABLE SECURITY DEFINER", options);
        var proc = c.Schemas.SelectMany(s => s.Routines).Single(r => r.Name == "reset_all");
        Assert.Equal(DbFields.Procedure, proc.Kind);
        Assert.Equal("BEGIN ATOMIC UPDATE account SET balance = 0; END", proc.Body);
        var tg = c.FindTable("public", "account")!.Triggers.Single();
        Assert.Equal("WHEN (OLD.balance IS DISTINCT FROM NEW.balance) EXECUTE FUNCTION public.touch()", tg.Action);
        Assert.Equal("ROW", tg.Level);
        Assert.Empty(c.Warnings);
    }

    [Fact]
    public void SqlServer_Keeps_Everything_After_As_Including_Long_Trigger_Batches()
    {
        var c = DdlParser.Parse(SqlServerScript, "sqlserver");
        var (body, options, _) = Routine(c, "AddInterest");
        Assert.StartsWith("BEGIN", body);
        Assert.Contains("SET @Count = @@ROWCOUNT;", body);
        Assert.EndsWith("END", body);
        Assert.Equal("WITH RECOMPILE", options);
        Assert.Equal("(@Rate decimal(5,4), @Count int OUTPUT)", c.Schemas.SelectMany(s => s.Routines).Single(r => r.Name == "AddInterest").Signature);
        Assert.Equal("WITH SCHEMABINDING", Routine(c, "Total").Options);
        var tg = c.FindTable("dbo", "Account")!.Triggers.Single();
        Assert.Contains("ROLLBACK TRANSACTION; END", tg.Action);
        Assert.Contains("-- more than forty tokens", tg.Action);
        Assert.Equal("UPDATE", tg.Events);
        Assert.Equal("DF_Account_Balance", c.FindTable("dbo", "Account")!.FindColumn("Balance")!.DefaultName);
    }

    [Fact]
    public void MySql_Delimiter_Blocks_And_Single_Statement_Bodies()
    {
        var c = DdlParser.Parse(MySqlScript, "mysql");
        var (body, options, _) = Routine(c, "add_interest");
        Assert.StartsWith("BEGIN", body);
        Assert.Contains("IF ROW_COUNT() > 0 THEN SET n = 1; END IF;", body);
        Assert.EndsWith("END", body);
        Assert.Equal("MODIFIES SQL DATA", options);
        var total = Routine(c, "total");
        Assert.Equal("RETURN (SELECT SUM(balance) FROM account)", total.Body);
        Assert.Equal("DETERMINISTIC READS SQL DATA", total.Options);
        var table = c.FindTable(null, "account")!;
        Assert.Contains("INSERT INTO account_log", table.Triggers.Single(t => t.Name == "account_bu").Action);
        // A body that starts with INSERT is the body, not another event.
        var ai = table.Triggers.Single(t => t.Name == "account_ai");
        Assert.Equal("INSERT", ai.Events);
        Assert.Equal("INSERT INTO account_log (account_id) VALUES (NEW.id)", ai.Action);
    }

    [Fact]
    public void Oracle_Slash_Blocks_Keep_Declarations_And_Trigger_When()
    {
        var c = DdlParser.Parse(OracleScript, "oracle");
        var (body, options, _) = Routine(c, "add_interest");
        Assert.StartsWith("n NUMBER := 0;", body);
        Assert.EndsWith("END add_interest;", body);
        Assert.Equal("AUTHID DEFINER", options);
        Assert.Equal("NUMBER", Routine(c, "total").Body.Contains("RETURN v;") ? c.Schemas.SelectMany(s => s.Routines).Single(r => r.Name == "total").Returns : null);
        var tg = c.AllTables.Single().Triggers.Single();
        Assert.StartsWith("WHEN (new.balance <> old.balance)", tg.Action);
        Assert.EndsWith("END;", tg.Action);
    }

    [Fact]
    public void Sqlite_Trigger_Body_With_Case_End_Is_Kept_Whole()
    {
        var c = DdlParser.Parse(SqliteScript, "sqlite");
        var tg = c.AllTables.Single().Triggers.Single();
        Assert.StartsWith("WHEN new.balance <> old.balance", tg.Action);
        Assert.Contains("RAISE(ABORT, 'negative') END;", tg.Action);
        Assert.EndsWith("END", tg.Action);
        Assert.Equal("UPDATE", tg.Events);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Bodies_Round_Trip_Through_The_Model_And_Generated_Ddl(string dialect)
    {
        var (m, r) = DatabaseModelTests.Import(Script(dialect), dialect, "bank");
        var routine = m.OfKind(ElementKind.Routine).FirstOrDefault();
        if (dialect != "sqlite") Assert.False(string.IsNullOrWhiteSpace(routine!.Get(DbFields.Definition)));
        var model = DbExtract.FromModel(m, r.Database).Catalog;
        var ddl = DdlWriter.Generate(model, dialect);
        var again = DdlParser.Parse(ddl, dialect, model.Name);
        var diff = DbDiff.Compare(model, again);
        Assert.True(diff.Count == 0, string.Join("\n", diff.Select(d => d.Describe())) + "\n\n" + ddl);
        foreach (var rd in model.Schemas.SelectMany(s => s.Routines))
            Assert.Equal(rd.Body!.Trim(), again.Schemas.SelectMany(s => s.Routines).Single(x => DbCatalog.Eq(x.Name, rd.Name)).Body!.Trim());
        foreach (var t in model.AllTables)
            foreach (var tg in t.Triggers)
                Assert.Equal(DbDiff.Sql(tg.Action), DbDiff.Sql(again.FindTable(null, t.Name)!.Triggers.Single(x => DbCatalog.Eq(x.Name, tg.Name)).Action));
        Assert.DoesNotContain("not generated", ddl);
    }

    [Fact]
    public void Generated_Ddl_Uses_Each_Dialects_Batch_Syntax()
    {
        string Ddl(string d) { var (m, r) = DatabaseModelTests.Import(Script(d), d, "bank"); return DdlWriter.Generate(DbExtract.FromModel(m, r.Database).Catalog, d); }
        var pg = Ddl("postgresql");
        Assert.Contains("CREATE OR REPLACE FUNCTION add_interest(p_rate numeric) RETURNS integer LANGUAGE plpgsql STABLE SECURITY DEFINER\nAS $body$", pg);
        Assert.Contains("CREATE TRIGGER account_touch BEFORE UPDATE ON account FOR EACH ROW", pg);
        Assert.True(pg.IndexOf("CREATE OR REPLACE FUNCTION touch", StringComparison.Ordinal) < pg.IndexOf("CREATE TRIGGER", StringComparison.Ordinal), "functions before the triggers that call them");
        var ss = Ddl("sqlserver");
        Assert.Contains("CREATE OR ALTER PROCEDURE AddInterest @Rate decimal(5,4), @Count int OUTPUT\nWITH RECOMPILE\nAS\nBEGIN", ss);
        Assert.Contains("CREATE OR ALTER TRIGGER trg_Account_Touch ON Account AFTER UPDATE\nAS\nBEGIN", ss);
        Assert.Contains("END\nGO", ss);
        var my = Ddl("mysql");
        Assert.Contains("DELIMITER $$\nCREATE PROCEDURE add_interest(IN p_rate DECIMAL(5,4)) MODIFIES SQL DATA\nBEGIN", my);
        Assert.Contains("END$$\nDELIMITER ;", my);
        Assert.Contains("CREATE TRIGGER account_ai AFTER INSERT ON account FOR EACH ROW\nINSERT INTO account_log (account_id) VALUES (NEW.id);", my);
        var ora = Ddl("oracle");
        Assert.Contains("CREATE OR REPLACE PROCEDURE add_interest(p_rate IN NUMBER) AUTHID DEFINER IS\n", ora);
        Assert.Contains("CREATE OR REPLACE FUNCTION total RETURN NUMBER IS\n", ora);
        Assert.Contains("END;\n/", ora);
        var lite = Ddl("sqlite");
        Assert.Contains("CREATE TRIGGER account_bu AFTER UPDATE ON account FOR EACH ROW\nWHEN new.balance <> old.balance", lite);
    }

    [Fact]
    public void Bodies_From_Another_Dialect_Are_Listed_Not_Generated()
    {
        var (m, r) = DatabaseModelTests.Import(PgScript, "postgresql", "bank");
        var ddl = DdlWriter.Generate(DbExtract.FromModel(m, r.Database).Catalog, "sqlserver");
        Assert.DoesNotContain("CREATE OR ALTER FUNCTION", ddl);
        Assert.Contains("-- Routines written for PostgreSQL (bodies are not translated):", ddl);
        Assert.Contains("written for PostgreSQL; not generated", ddl);
    }

    [Theory]
    [InlineData("postgresql", "CREATE OR REPLACE FUNCTION add_interest(p_rate numeric)", "DROP TRIGGER IF EXISTS account_touch ON account;")]
    [InlineData("sqlserver", "CREATE OR ALTER PROCEDURE AddInterest @Rate decimal(5,4), @Count int OUTPUT", "CREATE OR ALTER TRIGGER trg_Account_Touch ON Account AFTER UPDATE")]
    [InlineData("mysql", "DROP PROCEDURE IF EXISTS add_interest;\nDELIMITER $$\nCREATE PROCEDURE add_interest", "DROP TRIGGER IF EXISTS account_bu;")]
    [InlineData("oracle", "CREATE OR REPLACE PROCEDURE add_interest(p_rate IN NUMBER) AUTHID DEFINER IS", "CREATE OR REPLACE TRIGGER account_bu BEFORE UPDATE ON account FOR EACH ROW")]
    [InlineData("sqlite", null, "DROP TRIGGER IF EXISTS account_bu;\nCREATE TRIGGER account_bu AFTER UPDATE ON account")]
    public void Migration_Replaces_Changed_Bodies_And_Drops_Removed_Routines(string dialect, string? routineSql, string triggerSql)
    {
        var (m, r) = DatabaseModelTests.Import(Script(dialect), dialect, "bank");
        var before = DbExtract.FromModel(m, r.Database).Catalog;
        foreach (var e in m.OfKind(ElementKind.Routine).Where(x => x.Name is "add_interest" or "AddInterest").ToList())
            m.Set(e, DbFields.Definition, e.Get(DbFields.Definition).Replace("balance", "balance /* changed */", StringComparison.OrdinalIgnoreCase));
        foreach (var e in m.OfKind(ElementKind.Trigger).ToList())
            m.Set(e, DbFields.Definition, "/* changed */ " + e.Get(DbFields.Definition));
        var gone = m.OfKind(ElementKind.Routine).FirstOrDefault(x => x.Name is "total" or "Total" or "reset_all");
        if (gone != null) m.Delete(gone);
        var after = DbExtract.FromModel(m, r.Database).Catalog;
        var changes = DbDiff.Compare(before, after);
        if (dialect != "sqlite") Assert.Contains(changes, c => c.Type == DbObjectType.Routine && c.Kind == DbChangeKind.Changed && c.Fields.Any(f => f.Key == "body"));
        var sql = MigrationWriter.Generate(before, after, dialect).Sql;
        if (routineSql != null) Assert.Contains(routineSql, sql);
        Assert.Contains(triggerSql, sql);
        Assert.Contains("/* changed */", sql);
        if (gone != null)
            Assert.Contains(dialect switch
            {
                "postgresql" => "DROP PROCEDURE IF EXISTS reset_all();",
                "sqlserver" => "DROP FUNCTION IF EXISTS Total;",
                "oracle" => "DROP FUNCTION total;",
                _ => "DROP FUNCTION IF EXISTS total;",
            }, sql);
        Assert.DoesNotContain("not scripted", sql);
    }

    [Fact]
    public void Parse_Module_Reads_Stored_Definitions_Without_Their_Tables()
    {
        var (routine, _, _) = DdlParser.ParseModule("CREATE PROCEDURE dbo.p @a int AS SELECT @a; SELECT 2;", "sqlserver");
        Assert.Equal("SELECT @a; SELECT 2", routine!.Body);   // the last ";" ends the statement
        var (_, trigger, table) = DdlParser.ParseModule("CREATE OR REPLACE TRIGGER \"HR\".\"T1\" BEFORE INSERT OR UPDATE ON \"HR\".\"EMP\" FOR EACH ROW BEGIN :new.x := 1; END;", "oracle");
        Assert.Equal("EMP", table);
        Assert.Equal("INSERT, UPDATE", trigger!.Events);
        Assert.Equal("BEGIN :new.x := 1; END;", trigger.Action);
    }

    [Fact]
    public void Inspector_Field_Lists_Include_Routine_Options_And_Column_Default_Name()
    {
        Assert.Contains(DbFields.Options, DbFields.EditableFields(ElementKind.Routine));
        Assert.Contains(DbFields.Definition, DbFields.EditableFields(ElementKind.Routine));
        Assert.Contains(DbFields.DefaultName, DbFields.EditableFields(ElementKind.Column));
    }
}

public class DatabaseRenameTests
{
    private const string Shop = """
        CREATE TABLE customer (id integer PRIMARY KEY, email varchar(200) NOT NULL, name varchar(100));
        CREATE TABLE orders (id integer PRIMARY KEY, customer_id integer NOT NULL REFERENCES customer(id), amount numeric(10,2) NOT NULL DEFAULT 0, note text);
        CREATE INDEX ix_orders_amount ON orders (amount);
        """;

    /// <summary>A model and its catalog before and after renaming customer → client, orders.amount → total_amount.</summary>
    private static (DbCatalog Before, DbCatalog After) RenamedModel()
    {
        var (m, r) = DatabaseModelTests.Import(Shop, "postgresql", "shop");
        var before = DbExtract.FromModel(m, r.Database).Catalog;
        var customer = m.OfKind(ElementKind.Table).Single(t => t.Name == "customer");
        var amount = m.OfKind(ElementKind.Column).Single(c => c.Name == "amount");
        m.Set(customer, FieldKeys.Name, "client");
        m.Set(amount, FieldKeys.Name, "total_amount");
        return (before, DbExtract.FromModel(m, r.Database).Catalog);
    }

    [Fact]
    public void Same_Id_And_Another_Name_Is_A_Rename_And_Dependent_Keys_Stay()
    {
        var (before, after) = RenamedModel();
        var changes = DbDiff.Compare(before, after);
        Assert.Contains(changes, c => c.Kind == DbChangeKind.Renamed && c.Type == DbObjectType.Table && c.Name == "client" && c.Fields.Single().Before == "customer");
        Assert.Contains(changes, c => c.Kind == DbChangeKind.Renamed && c.Type == DbObjectType.Column && c.Table == "orders" && c.Name == "total_amount");
        // The foreign key to the renamed table and the index on the renamed column follow the renames: not dropped and added.
        Assert.True(changes.All(c => c.Kind == DbChangeKind.Renamed), string.Join("\n", changes.Select(c => c.Describe())));
        Assert.Equal("Renamed column public.orders.total_amount (was amount)", changes.Single(c => c.Type == DbObjectType.Column).Describe());
    }

    [Theory]
    [InlineData("postgresql", "ALTER TABLE customer RENAME TO client;", "ALTER TABLE orders RENAME COLUMN amount TO total_amount;")]
    // The model's schema is PostgreSQL's "public": other dialects qualify the names with it.
    [InlineData("sqlserver", "EXEC sp_rename N'[public].[customer]', N'client';", "EXEC sp_rename N'[public].[orders].[amount]', N'total_amount', N'COLUMN';")]
    [InlineData("mysql", "RENAME TABLE public.customer TO public.client;", "ALTER TABLE public.orders RENAME COLUMN amount TO total_amount;  -- MySQL 8.0+")]
    [InlineData("oracle", "ALTER TABLE \"public\".customer RENAME TO client;", "ALTER TABLE \"public\".orders RENAME COLUMN amount TO total_amount;")]
    [InlineData("sqlite", "ALTER TABLE customer RENAME TO client;", "ALTER TABLE orders RENAME COLUMN amount TO total_amount;")]
    public void Rename_Migrations_Per_Dialect(string dialect, string tableSql, string columnSql)
    {
        var (before, after) = RenamedModel();
        var script = MigrationWriter.Generate(before, after, dialect);
        Assert.Contains(tableSql, script.Sql);
        Assert.Contains(columnSql, script.Sql);
        Assert.DoesNotContain("DROP COLUMN", script.Sql);
        Assert.DoesNotContain("DROP TABLE", script.Sql);
        Assert.DoesNotContain("ADD COLUMN", script.Sql);
        Assert.Empty(script.Warnings);
        Assert.Equal(2, script.Steps);
    }

    [Fact]
    public void A_Renamed_Column_That_Also_Changed_Is_Renamed_Then_Altered()
    {
        var (m, r) = DatabaseModelTests.Import(Shop, "postgresql", "shop");
        var before = DbExtract.FromModel(m, r.Database).Catalog;
        var note = m.OfKind(ElementKind.Column).Single(c => c.Name == "note");
        m.Set(note, FieldKeys.Name, "remarks");
        m.Set(note, FieldKeys.SqlType, "varchar(500)");
        var sql = MigrationWriter.Generate(before, DbExtract.FromModel(m, r.Database).Catalog, "postgresql").Sql;
        var rename = sql.IndexOf("RENAME COLUMN note TO remarks", StringComparison.Ordinal);
        var alter = sql.IndexOf("ALTER COLUMN remarks TYPE varchar(500)", StringComparison.Ordinal);
        Assert.True(rename >= 0 && alter > rename, sql);
    }

    [Fact]
    public void Catalogs_Without_Ids_Get_Rename_Suggestions_That_Become_Renames_When_Confirmed()
    {
        // The database as it is (no element ids) and the model, where customer became client and amount total_amount.
        var live = DdlParser.Parse(Shop, "postgresql", "shop");
        var (_, after) = RenamedModel();
        var plain = DbDiff.Compare(live, after);
        Assert.Contains(plain, c => c.Kind == DbChangeKind.Removed && c.Type == DbObjectType.Column && c.Name == "amount");

        var hints = DbDiff.SuggestRenames(live, after);
        var table = Assert.Single(hints, h => h.Type == DbObjectType.Table);
        Assert.Equal(("customer", "client"), (table.OldName, table.NewName));
        var column = Assert.Single(hints, h => h.Type == DbObjectType.Column);
        Assert.Equal(("orders", "amount", "total_amount"), (column.Table, column.OldName, column.NewName));
        Assert.True(column.Score >= 0.8, column.Score.ToString());

        var script = MigrationWriter.Generate(live, after, "postgresql", renames: hints);
        Assert.Contains("ALTER TABLE customer RENAME TO client;", script.Sql);
        Assert.Contains("ALTER TABLE orders RENAME COLUMN amount TO total_amount;", script.Sql);
        Assert.DoesNotContain("DROP", script.Sql);
        // Only the column confirmed: the table is dropped and created again.
        var partial = MigrationWriter.Generate(live, after, "postgresql", renames: [column]);
        Assert.Contains("DROP TABLE customer;", partial.Sql);
        Assert.Contains("RENAME COLUMN amount TO total_amount", partial.Sql);
    }

    [Fact]
    public void Suggestions_Need_The_Same_Type_And_Skip_Objects_With_Ids()
    {
        var before = DdlParser.Parse("CREATE TABLE t (id int PRIMARY KEY, a int, b text);", "postgresql");
        var after = DdlParser.Parse("CREATE TABLE t (id int PRIMARY KEY, a int, c date);", "postgresql");
        Assert.Empty(DbDiff.SuggestRenames(before, after));   // b text → c date: not a rename
        var (b2, a2) = RenamedModel();
        Assert.Empty(DbDiff.SuggestRenames(b2, a2));   // both sides carry ids: the ids decide
        Assert.Equal(0.8, DbDiff.NameSimilarity("amount", "total_amount"));
        Assert.Equal(1, DbDiff.NameSimilarity("Customer_Id", "customerid"));
    }
}

public class DatabaseGeneratedNameDropTests
{
    private const string Before = """
        CREATE TABLE customer (id int PRIMARY KEY, credit int DEFAULT 0, status varchar(20) DEFAULT 'new');
        CREATE TABLE orders (id int PRIMARY KEY, customer_id int REFERENCES customer(id), amount int);
        """;

    private const string After = """
        CREATE TABLE customer (id int PRIMARY KEY, credit int DEFAULT 5);
        CREATE TABLE orders (id int PRIMARY KEY, customer_id int, amount int);
        """;

    private static string Script(string dialect) =>
        MigrationWriter.Generate(DdlParser.Parse(Before, dialect, "shop"), DdlParser.Parse(After, dialect, "shop"), dialect).Sql;

    [Theory]
    [InlineData("postgresql", "FROM pg_constraint k", "EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I', 'orders'::regclass, r.conname);")]
    [InlineData("sqlserver", "FROM sys.foreign_keys f", "IF @meridian_fk1 IS NOT NULL EXEC sp_executesql @meridian_sql1;")]
    [InlineData("mysql", "information_schema.KEY_COLUMN_USAGE", "PREPARE meridian_stmt FROM @meridian_sql;")]
    [InlineData("oracle", "FROM all_constraints c", "EXCEPTION\n    WHEN NO_DATA_FOUND THEN NULL;\nEND;\n/")]
    public void Unnamed_Foreign_Keys_Are_Dropped_By_Looking_Up_Their_Generated_Name(string dialect, string lookup, string drop)
    {
        var sql = Script(dialect);
        Assert.Contains(lookup, sql);
        Assert.Contains(drop, sql);
        Assert.DoesNotContain("drop it by hand", sql);
    }

    [Fact]
    public void Sqlite_Says_What_It_Cannot_Do()
    {
        Assert.Contains("-- SQLite can't drop foreign key", Script("sqlite"));
    }

    [Fact]
    public void SqlServer_Defaults_Are_Dropped_By_Name_Or_Looked_Up_Before_Changing_Or_Dropping_The_Column()
    {
        var sql = Script("sqlserver");
        // credit: unnamed default changed → look it up, drop it, add the new one.
        Assert.Contains("FROM sys.default_constraints dc", sql);
        Assert.Contains("c.name = N'credit'", sql);
        Assert.Contains("ALTER TABLE customer ADD DEFAULT 5 FOR credit;", sql);
        // status: dropped column with a default → its default goes first.
        var df = sql.IndexOf("c.name = N'status'", StringComparison.Ordinal);
        Assert.True(df >= 0 && df < sql.IndexOf("DROP COLUMN status", StringComparison.Ordinal), sql);

        // A default whose name is known (DDL or the live catalog) is dropped by that name.
        var named = MigrationWriter.Generate(
            DdlParser.Parse("CREATE TABLE t (id int PRIMARY KEY, n int CONSTRAINT DF_t_n DEFAULT (0))", "sqlserver"),
            DdlParser.Parse("CREATE TABLE t (id int PRIMARY KEY, n int CONSTRAINT DF_t_n DEFAULT (1))", "sqlserver"), "sqlserver").Sql;
        Assert.Contains("ALTER TABLE t DROP CONSTRAINT DF_t_n;", named);
        Assert.Contains("ALTER TABLE t ADD CONSTRAINT DF_t_n DEFAULT (1) FOR n;", named);
        Assert.DoesNotContain("sys.default_constraints", named);
    }

    [Fact]
    public void Primary_Keys_Without_A_Known_Name()
    {
        var before = DdlParser.Parse("CREATE TABLE t (id int PRIMARY KEY, k int NOT NULL)", "sqlserver");
        var after = DdlParser.Parse("CREATE TABLE t (id int NOT NULL, k int NOT NULL PRIMARY KEY)", "sqlserver");
        Assert.Contains("FROM sys.key_constraints k WHERE k.parent_object_id = OBJECT_ID(N'[dbo].[t]') AND k.type = 'PK'", MigrationWriter.Generate(before, after, "sqlserver").Sql);
        Assert.Contains("t DROP PRIMARY KEY;", MigrationWriter.Generate(before, after, "oracle").Sql);
        Assert.Contains("t DROP CONSTRAINT t_pkey;", MigrationWriter.Generate(before, after, "postgresql").Sql);
    }
}
