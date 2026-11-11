using System.Text.RegularExpressions;
using Meridian.Core.DataModeling;
using Meridian.Core.Model;
using Meridian.Data.Databases;
using Meridian.Serialization.Database;
using Microsoft.Data.SqlClient;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Meridian.Tests.StoreContract;
using Testcontainers.MsSql;
using Testcontainers.Oracle;

namespace Meridian.Tests;

/// <summary>A throwaway SQL Server in Docker (Testcontainers, random port); skipped when Docker or the image is unavailable.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public static string Image => Environment.GetEnvironmentVariable("MERIDIAN_SQLSERVER_IMAGE") is { Length: > 0 } i ? i : "mcr.microsoft.com/mssql/server:2022-latest";
    private MsSqlContainer? _container;
    public string? SkipReason { get; private set; }
    private int _databases;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new MsSqlBuilder(Image).Build();
            await _container.StartAsync();
        }
        catch (Exception ex) when (Environment.GetEnvironmentVariable("MERIDIAN_REQUIRE_SQLSERVER") is not ("1" or "true"))
        {
            SkipReason = $"SQL Server live tests skipped: could not start {Image} ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}).";
            _container = null;
        }
    }

    public async Task<string> CreateDatabaseAsync()
    {
        if (_container is null) throw new InvalidOperationException(SkipReason);
        var name = $"meridian_{Interlocked.Increment(ref _databases)}_{Guid.NewGuid():N}"[..30];
        var b = new SqlConnectionStringBuilder(_container.GetConnectionString()) { TrustServerCertificate = true, Pooling = false };
        await using (var c = new SqlConnection(b.ConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new SqlCommand($"create database [{name}]", c);
            await cmd.ExecuteNonQueryAsync();
        }
        b.InitialCatalog = name;
        return b.ConnectionString;
    }

    /// <summary>Runs a T-SQL script batch by batch (split on GO lines).</summary>
    public static async Task ExecAsync(string cs, string script)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Where(b => b.Trim().Length > 0 && !b.Trim().Split('\n').All(l => l.TrimStart().StartsWith("--", StringComparison.Ordinal))))
        {
            await using var cmd = new SqlCommand(batch, c);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (_container != null) await _container.DisposeAsync();
    }
}

/// <summary>A throwaway Oracle Database Free (gvenzl/oracle-free) in Docker; skipped when Docker or the image is unavailable.</summary>
public sealed class OracleFixture : IAsyncLifetime
{
    public static string Image => Environment.GetEnvironmentVariable("MERIDIAN_ORACLE_IMAGE") is { Length: > 0 } i ? i : "gvenzl/oracle-free:23-slim-faststart";
    private OracleContainer? _container;
    public string? SkipReason { get; private set; }
    public string ConnectionString => _container?.GetConnectionString() ?? throw new InvalidOperationException(SkipReason);

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("MERIDIAN_SKIP_ORACLE") is "1" or "true") { SkipReason = "Oracle live tests skipped (MERIDIAN_SKIP_ORACLE)."; return; }
        try
        {
            _container = new OracleBuilder(Image).Build();
            await _container.StartAsync();
        }
        catch (Exception ex) when (Environment.GetEnvironmentVariable("MERIDIAN_REQUIRE_ORACLE") is not ("1" or "true"))
        {
            SkipReason = $"Oracle live tests skipped: could not start {Image} ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}).";
            _container = null;
        }
    }

    /// <summary>Runs SQL statements and PL/SQL blocks separated by "/" lines (or a plain statement per ";").</summary>
    public async Task ExecAsync(string script)
    {
        await using var c = new OracleConnection(ConnectionString);
        await c.OpenAsync();
        foreach (var block in Regex.Split(script, @"^\s*/\s*$", RegexOptions.Multiline))
        {
            var text = string.Join("\n", block.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal))).Trim();
            if (text.Length == 0) continue;
            var plsql = Regex.IsMatch(text, @"^(CREATE\s+(OR\s+REPLACE\s+)?(PROCEDURE|FUNCTION|TRIGGER|PACKAGE)|DECLARE|BEGIN)\b", RegexOptions.IgnoreCase);
            foreach (var stmt in plsql ? [text] : text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                await using var cmd = new OracleCommand(stmt, c);
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_container != null) await _container.DisposeAsync();
    }
}

public class LiveSqlServerImportTests(SqlServerFixture server) : IClassFixture<SqlServerFixture>
{
    private const string Schema = """
        CREATE SCHEMA sales
        GO
        CREATE TYPE dbo.Email FROM nvarchar(320) NOT NULL
        GO
        CREATE TABLE dbo.Customers (
            Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
            Email dbo.Email,
            Credit decimal(12,2) NOT NULL DEFAULT (0),
            Status nvarchar(20) NOT NULL CONSTRAINT DF_Customers_Status DEFAULT (N'new'),
            Code char(4) COLLATE Latin1_General_BIN NULL,
            CONSTRAINT UQ_Customers_Email UNIQUE (Email),
            CONSTRAINT CK_Customers_Credit CHECK (Credit >= 0)
        )
        GO
        CREATE TABLE sales.Orders (
            Id bigint IDENTITY(1,1) PRIMARY KEY,
            CustomerId int NOT NULL REFERENCES dbo.Customers (Id) ON DELETE CASCADE,
            Amount decimal(18,2) NOT NULL,
            Tax AS (Amount * 0.2),
            Note nvarchar(max) NULL
        )
        GO
        CREATE INDEX IX_Orders_Big ON sales.Orders (Amount DESC) WHERE Amount > 1000
        GO
        CREATE SEQUENCE sales.InvoiceNo AS bigint START WITH 1000 INCREMENT BY 10
        GO
        CREATE VIEW sales.BigOrders AS SELECT o.Id, o.Amount FROM sales.Orders o WHERE o.Amount > 1000
        GO
        CREATE PROCEDURE sales.PlaceOrder @CustomerId int, @Amount decimal(18,2)
        AS
        BEGIN
            SET NOCOUNT ON;
            INSERT INTO sales.Orders (CustomerId, Amount) VALUES (@CustomerId, @Amount);
        END
        GO
        CREATE FUNCTION dbo.OrderCount(@CustomerId int) RETURNS int AS
        BEGIN
            RETURN (SELECT COUNT(*) FROM sales.Orders WHERE CustomerId = @CustomerId);
        END
        GO
        CREATE TRIGGER sales.trg_Orders_Audit ON sales.Orders AFTER INSERT, UPDATE AS
        BEGIN
            SET NOCOUNT ON;
            UPDATE dbo.Customers SET Credit = Credit WHERE Id IN (SELECT CustomerId FROM inserted);
        END
        GO
        EXEC sp_addextendedproperty N'MS_Description', N'Registered customers', N'SCHEMA', N'dbo', N'TABLE', N'Customers'
        GO
        EXEC sp_addextendedproperty N'MS_Description', N'Store credit in EUR', N'SCHEMA', N'dbo', N'TABLE', N'Customers', N'COLUMN', N'Credit'
        GO
        """;

    private Task<DbCatalog> Read(string cs) => LiveSchemaReader.ReadAsync(LiveProviders.SqlServer, cs, null, TimeSpan.FromSeconds(60), CancellationToken.None);

    [SkippableFact]
    public async Task SqlServer_Catalog_Views_Give_Tables_Keys_Bodies_And_Comments()
    {
        Skip.If(server.SkipReason != null, server.SkipReason);
        var cs = await server.CreateDatabaseAsync();
        await SqlServerFixture.ExecAsync(cs, Schema);
        var live = await Read(cs);
        Assert.Equal("sqlserver", live.Dialect);

        var customers = live.FindTable("dbo", "Customers")!;
        Assert.Equal(["Id"], customers.PrimaryKey);
        Assert.Equal("PK_Customers", customers.PrimaryKeyName);
        Assert.Equal("always", customers.FindColumn("Id")!.Identity);
        Assert.Equal("Email", customers.FindColumn("Email")!.Type);
        Assert.Equal("decimal(12,2)", customers.FindColumn("Credit")!.Type);
        Assert.Equal("((0))", customers.FindColumn("Credit")!.Default);
        Assert.StartsWith("DF__Customer", customers.FindColumn("Credit")!.DefaultName);   // the generated name, read from the catalog
        Assert.Equal("DF_Customers_Status", customers.FindColumn("Status")!.DefaultName);
        Assert.Equal("Latin1_General_BIN", customers.FindColumn("Code")!.Collation);
        Assert.Equal("Registered customers", customers.Comment);
        Assert.Equal("Store credit in EUR", customers.FindColumn("Credit")!.Comment);
        Assert.Contains(customers.Constraints, c => c.Name == "UQ_Customers_Email" && c.Columns.SequenceEqual(["Email"]));
        Assert.Contains(customers.Constraints, c => c.Name == "CK_Customers_Credit" && c.Expression == "[Credit]>=(0)");

        var orders = live.FindTable("sales", "Orders")!;
        var fk = orders.ForeignKeys.Single();
        Assert.StartsWith("FK__Orders__Custome", fk.Name);
        Assert.Equal(("dbo", "Customers", "CASCADE"), (fk.RefSchema, fk.RefTable, fk.OnDelete));
        Assert.Equal("computed", orders.FindColumn("Tax")!.Type);
        Assert.Equal("([Amount]*(0.2))", orders.FindColumn("Tax")!.Generated);
        Assert.Equal("nvarchar(max)", orders.FindColumn("Note")!.Type);
        var ix = orders.Indexes.Single();
        Assert.Equal(("IX_Orders_Big", "[Amount]>(1000)"), (ix.Name, ix.Where));
        var tg = orders.Triggers.Single();
        Assert.Equal(("trg_Orders_Audit", "AFTER", "INSERT, UPDATE"), (tg.Name, tg.Timing, tg.Events));
        Assert.Contains("WHERE Id IN (SELECT CustomerId FROM inserted);", tg.Action);

        Assert.Equal(DbFields.View, live.FindTable("sales", "BigOrders")!.Kind);
        Assert.Equal("SELECT o.Id, o.Amount FROM sales.Orders o WHERE o.Amount > 1000", live.FindTable("sales", "BigOrders")!.Definition);
        var seq = live.FindSchema("sales")!.Sequences.Single();
        Assert.Equal(("InvoiceNo", "1000", "10"), (seq.Name, seq.Start, seq.Increment));
        Assert.Equal("nvarchar(320)", live.FindSchema("dbo")!.Types.Single().BaseType);
        var proc = live.FindSchema("sales")!.Routines.Single();
        Assert.Equal((DbFields.Procedure, "(@CustomerId int, @Amount decimal(18,2))"), (proc.Kind, proc.Signature));
        Assert.Contains("INSERT INTO sales.Orders (CustomerId, Amount) VALUES (@CustomerId, @Amount);", proc.Body);
        var fn = live.FindSchema("dbo")!.Routines.Single();
        Assert.Equal(("int", DbFields.Function), (fn.Returns, fn.Kind));

        // Nothing was written: a second read is identical.
        Assert.Empty(DbDiff.Compare(live, await Read(cs)));

        // The bodies survive import → model → DDL → a fresh database → read again.
        var m = new ModelRepository("M");
        var r = DbImport.Import(m, m.Root, live);
        var ddl = DdlWriter.Generate(DbExtract.FromModel(m, r.Database).Catalog, "sqlserver");
        var copy = await server.CreateDatabaseAsync();
        await SqlServerFixture.ExecAsync(copy, ddl);
        var again = await Read(copy);
        Assert.Equal(DbDiff.Sql(proc.Body), DbDiff.Sql(again.FindSchema("sales")!.Routines.Single().Body));
        Assert.Equal(DbDiff.Sql(tg.Action), DbDiff.Sql(again.FindTable("sales", "Orders")!.Triggers.Single().Action));
    }

    [SkippableFact]
    public async Task Migration_Scripts_Drop_Generated_Names_And_Rename_Against_A_Real_Server()
    {
        Skip.If(server.SkipReason != null, server.SkipReason);
        var cs = await server.CreateDatabaseAsync();
        const string before = """
            CREATE TABLE customer (id int PRIMARY KEY, credit int DEFAULT 0, status varchar(20) DEFAULT 'new', note varchar(50))
            GO
            CREATE TABLE orders (id int PRIMARY KEY, customer_id int REFERENCES customer(id), amount int)
            GO
            """;
        await SqlServerFixture.ExecAsync(cs, before);
        // The model knows no constraint names (it came from the script): unnamed FK, default and primary key.
        var model = DdlParser.Parse(before, "sqlserver", "shop");
        var after = DdlParser.Parse("""
            CREATE TABLE customer (id int NOT NULL, credit int DEFAULT 5, remarks varchar(50), CONSTRAINT pk_customer PRIMARY KEY (id))
            GO
            CREATE TABLE orders (id int PRIMARY KEY, customer_id int, amount int)
            GO
            """, "sqlserver", "shop");
        var hint = Assert.Single(DbDiff.SuggestRenames(model, after));
        Assert.Equal(("note", "remarks"), (hint.OldName, hint.NewName));
        var script = MigrationWriter.Generate(model, after, "sqlserver", renames: [hint]);
        await SqlServerFixture.ExecAsync(cs, script.Sql);

        var live = await Read(cs);
        Assert.Empty(live.FindTable("dbo", "orders")!.ForeignKeys);
        var customer = live.FindTable("dbo", "customer")!;
        Assert.Equal("((5))", customer.FindColumn("credit")!.Default);
        Assert.Null(customer.FindColumn("status"));
        Assert.NotNull(customer.FindColumn("remarks"));
        // The script brought the database to the model.
        var rest = DbDiff.Compare(live, after).Where(c => c.Type is not DbObjectType.Column || c.Fields.Any(f => f.Key != "default")).ToList();
        Assert.True(rest.Count == 0, string.Join("\n", rest.Select(c => c.Describe())) + "\n" + script.Sql);
    }

    [SkippableFact]
    public async Task Wrong_Password_Fails_Without_Echoing_It()
    {
        Skip.If(server.SkipReason != null, server.SkipReason);
        var cs = new SqlConnectionStringBuilder(await server.CreateDatabaseAsync()) { Password = "Wr0ng-s3cret!" }.ConnectionString;
        var ex = await Assert.ThrowsAsync<DatabaseImportException>(() => Read(cs));
        Assert.DoesNotContain("Wr0ng-s3cret!", ex.Message);
    }

    [Fact]
    public void Endpoints_Parse_Data_Source_Forms()
    {
        Assert.Equal(("db.example.com", 1433), Ep("Server=db.example.com;Database=shop;User ID=u;Password=p"));
        Assert.Equal(("db.example.com", 14330), Ep("Data Source=tcp:db.example.com,14330;Initial Catalog=shop"));
        Assert.Equal(("db", 0), Ep(@"Server=db\SQLEXPRESS;Database=x"));
        Assert.Equal(("a,b", 1433), Ep("Server=a;Failover Partner=b;Database=x"));
        Assert.Null(LiveSchemaReader.Endpoint(LiveProviders.SqlServer, "Server=np:\\\\.\\pipe\\sql\\query;Database=x").Host);
        Assert.Null(LiveSchemaReader.Endpoint(LiveProviders.SqlServer, "Server=(localdb)\\MSSQLLocalDB;Database=x").Host);
        Assert.Equal("shop", LiveSchemaReader.Endpoint(LiveProviders.SqlServer, "Server=db;Database=shop").Database);

        static (string?, int) Ep(string cs) { var e = LiveSchemaReader.Endpoint(LiveProviders.SqlServer, cs); return (e.Host, e.Port); }
    }
}

public class LiveOracleImportTests(OracleFixture server) : IClassFixture<OracleFixture>
{
    [SkippableFact]
    public async Task Oracle_Dictionary_Views_Give_Tables_Keys_Bodies_And_A_Working_Lookup_Drop()
    {
        Skip.If(server.SkipReason != null, server.SkipReason);
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        string N(string n) => $"{n}_{suffix}";
        await server.ExecAsync($"""
            CREATE TABLE {N("dept")} (id NUMBER(4) CONSTRAINT {N("dept_pk")} PRIMARY KEY, name VARCHAR2(30 CHAR) NOT NULL);
            CREATE TABLE {N("emp")} (
                id NUMBER(6) GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                dept_id NUMBER(4) REFERENCES {N("dept")} (id) ON DELETE CASCADE,
                salary NUMBER(8,2) DEFAULT 0 CONSTRAINT {N("emp_sal")} CHECK (salary >= 0),
                bonus NUMBER(8,2) GENERATED ALWAYS AS (salary * 0.1) VIRTUAL,
                hired DATE DEFAULT SYSDATE NOT NULL);
            CREATE INDEX {N("emp_sal_ix")} ON {N("emp")} (salary);
            COMMENT ON TABLE {N("emp")} IS 'Employees';
            COMMENT ON COLUMN {N("emp")}.salary IS 'Monthly';
            CREATE SEQUENCE {N("emp_seq")} START WITH 10 INCREMENT BY 5;
            CREATE VIEW {N("rich")} AS SELECT id, salary FROM {N("emp")} WHERE salary > 1000
            /
            CREATE OR REPLACE PROCEDURE {N("raise")} (p_pct IN NUMBER) IS
              n NUMBER := 0;
            BEGIN
              UPDATE {N("emp")} SET salary = salary * (1 + p_pct / 100);
              n := SQL%ROWCOUNT;
            END;
            /
            CREATE OR REPLACE TRIGGER {N("emp_bi")} BEFORE INSERT OR UPDATE ON {N("emp")} FOR EACH ROW
            BEGIN
              :new.salary := NVL(:new.salary, 0);
            END;
            /
            """);
        var live = await LiveSchemaReader.ReadAsync(LiveProviders.Oracle, server.ConnectionString, null, TimeSpan.FromSeconds(120), CancellationToken.None);
        Assert.Equal("oracle", live.Dialect);
        var emp = live.FindTable(null, N("emp"))!;
        Assert.Equal("byDefault", emp.FindColumn("ID")!.Identity);
        Assert.Equal("number(8,2)", emp.FindColumn("SALARY")!.Type);
        Assert.Equal("Monthly", emp.FindColumn("SALARY")!.Comment);
        Assert.Equal("Employees", emp.Comment);
        Assert.Contains("\"SALARY\"*0.1", emp.FindColumn("BONUS")!.Generated);
        Assert.Equal("SYSDATE", emp.FindColumn("HIRED")!.Default);
        Assert.False(emp.FindColumn("HIRED")!.Nullable);
        Assert.Equal("varchar2(30 char)", live.FindTable(null, N("dept"))!.FindColumn("NAME")!.Type);
        var fk = emp.ForeignKeys.Single();
        Assert.Equal((N("DEPT"), "CASCADE"), (fk.RefTable, fk.OnDelete));
        Assert.Contains(emp.Constraints, c => c.Name == N("EMP_SAL") && c.Expression == "salary >= 0");
        Assert.DoesNotContain(emp.Constraints, c => c.Expression?.Contains("IS NOT NULL") == true);
        Assert.Contains(emp.Indexes, i => i.Name == N("EMP_SAL_IX"));
        Assert.Equal(DbFields.View, live.FindTable(null, N("rich"))!.Kind);
        Assert.Equal("5", live.Schemas.SelectMany(s => s.Sequences).Single(q => q.Name == N("EMP_SEQ")).Increment);
        var proc = live.Schemas.SelectMany(s => s.Routines).Single(x => x.Name == N("RAISE"));
        Assert.Contains("n := SQL%ROWCOUNT;", proc.Body);
        var tg = emp.Triggers.Single();
        Assert.Equal(("BEFORE", "INSERT, UPDATE", "ROW"), (tg.Timing, tg.Events, tg.Level));
        Assert.Contains(":new.salary := NVL(:new.salary, 0);", tg.Action);

        // The unnamed foreign key is dropped by a PL/SQL block that looks its SYS_C… name up.
        var model = DdlParser.Parse($"CREATE TABLE {N("emp")} (id NUMBER(6) PRIMARY KEY, dept_id NUMBER(4) REFERENCES {N("dept")} (id));", "oracle", live.Name);
        var target = DdlParser.Parse($"CREATE TABLE {N("emp")} (id NUMBER(6) PRIMARY KEY, dept_id NUMBER(4));", "oracle", live.Name);
        var script = MigrationWriter.Generate(model, target, "oracle");
        Assert.Contains("all_constraints", script.Sql);
        await server.ExecAsync(script.Sql);
        var after = await LiveSchemaReader.ReadAsync(LiveProviders.Oracle, server.ConnectionString, null, TimeSpan.FromSeconds(120), CancellationToken.None);
        Assert.Empty(after.FindTable(null, N("emp"))!.ForeignKeys);
    }

    [Fact]
    public void Endpoints_Parse_Easy_Connect_And_Descriptors()
    {
        var e = LiveSchemaReader.Endpoint(LiveProviders.Oracle, "Data Source=db.example.com:1522/FREEPDB1;User Id=u;Password=p");
        Assert.Equal(("db.example.com", 1522, "FREEPDB1"), (e.Host, e.Port, e.Database));
        e = LiveSchemaReader.Endpoint(LiveProviders.Oracle, "Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=a.example.com)(PORT=1521))(ADDRESS=(PROTOCOL=TCP)(HOST=b.example.com)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=svc)));User Id=u");
        Assert.Equal(("a.example.com,b.example.com", "svc"), (e.Host, e.Database));
        Assert.Null(LiveSchemaReader.Endpoint(LiveProviders.Oracle, "Data Source=PRODALIAS;User Id=u").Host);   // a TNS alias
    }
}

/// <summary>A throwaway MySQL 8.4 (Testcontainers, random port, root account); skipped when Docker or the image is unavailable.</summary>
public sealed class MySqlFixture : IAsyncLifetime
{
    public static string Image => Environment.GetEnvironmentVariable("MERIDIAN_MYSQL_IMAGE") is { Length: > 0 } i ? i : "mysql:8.4";
    private Testcontainers.MySql.MySqlContainer? _container;
    public string? SkipReason { get; private set; }
    public string ConnectionString => _container?.GetConnectionString() ?? throw new InvalidOperationException(SkipReason);

    public async Task InitializeAsync()
    {
        try
        {
            _container = new Testcontainers.MySql.MySqlBuilder(Image).WithUsername("root").WithPassword(Guid.NewGuid().ToString("N")).Build();
            await _container.StartAsync();
        }
        catch (Exception ex) when (Environment.GetEnvironmentVariable("MERIDIAN_REQUIRE_MYSQL") is not ("1" or "true"))
        {
            SkipReason = $"MySQL live tests skipped: could not start {Image} ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}).";
            _container = null;
        }
    }

    /// <summary>Runs statements one by one (a routine is one statement: no DELIMITER needed through the driver).</summary>
    public async Task ExecAsync(params string[] statements)
    {
        await using var c = new MySqlConnector.MySqlConnection(ConnectionString + ";AllowUserVariables=true");
        await c.OpenAsync();
        foreach (var sql in statements)
        {
            await using var cmd = new MySqlConnector.MySqlCommand(sql, c);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (_container != null) await _container.DisposeAsync();
    }
}

public class LiveMySqlImportTests(MySqlFixture server) : IClassFixture<MySqlFixture>
{
    [SkippableFact]
    public async Task MySql_Information_Schema_Gives_Keys_And_Bodies_And_The_Lookup_Drop_Runs()
    {
        Skip.If(server.SkipReason != null, server.SkipReason);
        await server.ExecAsync(
            "CREATE TABLE customer (id int NOT NULL PRIMARY KEY, email varchar(200) NOT NULL, credit decimal(10,2) NOT NULL DEFAULT 0, status enum('new','vip') NOT NULL DEFAULT 'new')",
            "CREATE TABLE orders (id bigint NOT NULL AUTO_INCREMENT PRIMARY KEY, customer_id int, amount decimal(10,2) NOT NULL, FOREIGN KEY (customer_id) REFERENCES customer (id) ON DELETE CASCADE)",
            "CREATE INDEX ix_orders_amount ON orders (amount)",
            """
            CREATE PROCEDURE add_credit(IN p_id INT, IN p_amount DECIMAL(10,2))
            MODIFIES SQL DATA
            BEGIN
              UPDATE customer SET credit = credit + p_amount WHERE id = p_id;
              IF ROW_COUNT() = 0 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'no such customer'; END IF;
            END
            """,
            "CREATE FUNCTION order_total(p_id INT) RETURNS decimal(12,2) DETERMINISTIC READS SQL DATA RETURN (SELECT COALESCE(SUM(amount), 0) FROM orders WHERE customer_id = p_id)",
            "CREATE TRIGGER orders_bi BEFORE INSERT ON orders FOR EACH ROW SET NEW.amount = GREATEST(NEW.amount, 0)");
        var live = await LiveSchemaReader.ReadAsync(LiveProviders.MySql, server.ConnectionString, null, TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.Equal("mysql", live.Dialect);
        var orders = live.AllTables.Single(t => t.Name == "orders");
        Assert.Equal("autoIncrement", orders.FindColumn("id")!.Identity);
        var fk = orders.ForeignKeys.Single();
        Assert.Equal(("customer", "CASCADE"), (fk.RefTable, fk.OnDelete));
        Assert.StartsWith("orders_ibfk_", fk.Name);   // the generated name, from the catalog
        Assert.Contains(orders.Indexes, i => i.Name == "ix_orders_amount");
        Assert.Equal("SET NEW.amount = GREATEST(NEW.amount, 0)", orders.Triggers.Single().Action);
        var routines = live.Schemas.SelectMany(s => s.Routines).ToList();
        var proc = routines.Single(r => r.Name == "add_credit");
        Assert.Equal((DbFields.Procedure, "MODIFIES SQL DATA"), (proc.Kind, proc.Options));
        Assert.Contains("SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'no such customer'; END IF;", proc.Body);
        var fn = routines.Single(r => r.Name == "order_total");
        Assert.Equal(("decimal(12,2)", "DETERMINISTIC READS SQL DATA"), (fn.Returns, fn.Options));
        Assert.StartsWith("RETURN (SELECT COALESCE(SUM(amount), 0)", fn.Body);

        // The model came from a script without the FK's name: the migration looks it up and drops it, and renames a column.
        const string before = "CREATE TABLE customer (id int PRIMARY KEY, email varchar(200) NOT NULL); CREATE TABLE orders (id bigint PRIMARY KEY, customer_id int REFERENCES customer (id), amount decimal(10,2) NOT NULL);";
        var model = DdlParser.Parse(before, "mysql", live.Name);
        var target = DdlParser.Parse(before.Replace("customer_id int REFERENCES customer (id)", "customer_id int").Replace("email varchar", "email_address varchar"), "mysql", live.Name);
        var script = MigrationWriter.Generate(model, target, "mysql", renames: DbDiff.SuggestRenames(model, target));
        Assert.Contains("RENAME COLUMN email TO email_address", script.Sql);
        await server.ExecAsync(script.Sql);
        var after = await LiveSchemaReader.ReadAsync(LiveProviders.MySql, server.ConnectionString, null, TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.Empty(after.AllTables.Single(t => t.Name == "orders").ForeignKeys);
        Assert.NotNull(after.AllTables.Single(t => t.Name == "customer").FindColumn("email_address"));
    }
}

/// <summary>PostgreSQL: bodies from pg_get_functiondef and a lookup drop of an unnamed foreign key, run for real.</summary>
public class LivePostgresBodiesTests(PostgresServerFixture server) : IClassFixture<PostgresServerFixture>
{
    [SkippableFact]
    public async Task Bodies_Are_Read_And_Generated_Names_Are_Looked_Up()
    {
        Skip.If(server.SkipReason != null, server.SkipReason);
        var cs = await server.CreateDatabaseAsync();
        const string ddl = """
            CREATE TABLE customer (id integer PRIMARY KEY, balance numeric(12,2) NOT NULL DEFAULT 0, updated_at timestamptz);
            CREATE TABLE orders (id integer PRIMARY KEY, customer_id integer REFERENCES customer(id), amount integer);
            CREATE FUNCTION touch() RETURNS trigger LANGUAGE plpgsql STABLE AS $$ BEGIN NEW.updated_at := now(); RETURN NEW; END; $$;
            CREATE TRIGGER customer_touch BEFORE UPDATE ON customer FOR EACH ROW WHEN (OLD.balance IS DISTINCT FROM NEW.balance) EXECUTE FUNCTION touch();
            """;
        await using (var c = new NpgsqlConnection(cs)) { await c.OpenAsync(); await using var cmd = new NpgsqlCommand(ddl, c); await cmd.ExecuteNonQueryAsync(); }
        var live = await LiveSchemaReader.ReadAsync(LiveProviders.PostgreSql, cs, null, TimeSpan.FromSeconds(30), CancellationToken.None);
        var fn = live.FindSchema("public")!.Routines.Single();
        Assert.Equal("BEGIN NEW.updated_at := now(); RETURN NEW; END;", fn.Body!.Trim());
        Assert.Equal("STABLE", fn.Options);
        var tg = live.FindTable("public", "customer")!.Triggers.Single();
        Assert.Equal("WHEN (old.balance IS DISTINCT FROM new.balance) EXECUTE FUNCTION touch()", tg.Action);

        // Model from the script (the FK has no name there) → model without the FK and with a renamed column.
        var model = DdlParser.Parse(ddl, "postgresql", live.Name);
        var target = DdlParser.Parse(ddl.Replace("customer_id integer REFERENCES customer(id)", "customer_id integer").Replace("amount integer", "total_amount integer"), "postgresql", live.Name);
        var script = MigrationWriter.Generate(model, target, "postgresql", renames: DbDiff.SuggestRenames(model, target));
        await using (var c = new NpgsqlConnection(cs)) { await c.OpenAsync(); await using var cmd = new NpgsqlCommand(script.Sql, c); await cmd.ExecuteNonQueryAsync(); }
        var after = await LiveSchemaReader.ReadAsync(LiveProviders.PostgreSql, cs, null, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Empty(after.FindTable("public", "orders")!.ForeignKeys);
        Assert.NotNull(after.FindTable("public", "orders")!.FindColumn("total_amount"));
    }
}
