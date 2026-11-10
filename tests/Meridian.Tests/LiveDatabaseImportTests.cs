using Meridian.Core.DataModeling;
using Meridian.Core.Model;
using Meridian.Data.Databases;
using Meridian.Serialization.Database;
using Meridian.Tests.StoreContract;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Meridian.Tests;

/// <summary>Reads schemas from real databases: a SQLite file, and a throwaway PostgreSQL container (Testcontainers, random port).</summary>
public class LiveSqliteImportTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"meridian-live-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_file); } catch (IOException) { }
    }

    private async Task Exec(string sql)
    {
        await using var c = new SqliteConnection($"Data Source={_file};Pooling=False");
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Sqlite_File_Is_Read_Read_Only_And_Refresh_Finds_What_Changed()
    {
        await Exec(DatabaseScripts.Sqlite);
        var catalog = await LiveSchemaReader.ReadAsync(LiveProviders.Sqlite, $"Data Source={_file}", null, TimeSpan.FromSeconds(10), CancellationToken.None);
        var book = catalog.FindTable("main", "book")!;
        Assert.Equal(["id"], book.PrimaryKey);
        Assert.Equal("author", book.ForeignKeys.Single().RefTable);
        Assert.Contains(book.Indexes, i => i.Name == "book_isbn" && i.Unique);
        Assert.Equal(DbFields.View, catalog.FindTable("main", "cheap_books")!.Kind);
        Assert.Equal("book_ai", book.Triggers.Single().Name);
        Assert.Equal("sqlite", LiveSchemaReader.Endpoint(LiveProviders.Sqlite, $"Data Source={_file}").Provider);

        var m = new ModelRepository("M");
        var r = DbImport.Import(m, m.Root, catalog, new DbImportOptions { Source = "connection:test" });
        Assert.Equal(4, m.OfKind(ElementKind.Table).Count());   // 3 tables and a view

        await Exec("ALTER TABLE author ADD COLUMN country TEXT; CREATE TABLE publisher (id INTEGER PRIMARY KEY, name TEXT NOT NULL); DROP VIEW cheap_books;");
        var fresh = await LiveSchemaReader.ReadAsync(LiveProviders.Sqlite, $"Data Source={_file}", null, TimeSpan.FromSeconds(10), CancellationToken.None);
        var plan = DbRefresh.Plan(m, r.Database, fresh);
        Assert.Contains(plan.Changes, c => c.Kind == DbChangeKind.Added && c.Type == DbObjectType.Column && c.Name == "country");
        Assert.Contains(plan.Changes, c => c.Kind == DbChangeKind.Added && c.Type == DbObjectType.Table && c.Name == "publisher");
        Assert.Contains(plan.Changes, c => c.Kind == DbChangeKind.Removed && c.Type == DbObjectType.View && c.Name == "cheap_books");
        Assert.Equal(3, plan.Changes.Count);

        // Read-only: the reader cannot have written anything.
        var again = await LiveSchemaReader.ReadAsync(LiveProviders.Sqlite, $"Data Source={_file}", null, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Empty(DbDiff.Compare(fresh, again));
    }

    [Fact]
    public async Task Missing_File_And_Unsupported_Provider_Give_Clear_Errors()
    {
        var ex = await Assert.ThrowsAsync<DatabaseImportException>(() => LiveSchemaReader.ReadAsync(LiveProviders.Sqlite, "Data Source=/nope/missing.db", null, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Contains("does not exist", ex.Message);
        await Assert.ThrowsAsync<DatabaseImportException>(() => LiveSchemaReader.ReadAsync("sqlserver", "Server=x", null, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_Stops_The_Read()
    {
        await Exec(DatabaseScripts.Sqlite);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LiveSchemaReader.ReadAsync(LiveProviders.Sqlite, $"Data Source={_file}", null, TimeSpan.FromSeconds(10), cts.Token));
    }
}

public class LivePostgresImportTests(PostgresServerFixture server) : IClassFixture<PostgresServerFixture>
{
    [SkippableFact]
    public async Task Postgres_Catalog_Matches_The_Ddl_It_Was_Built_From()
    {
        Skip.If(server.SkipReason != null, server.SkipReason);
        var cs = await server.CreateDatabaseAsync();
        await using (var c = new NpgsqlConnection(cs))
        {
            await c.OpenAsync();
            // The fixture script without the parts a plain server lacks (extension, role) and without the data statements.
            var script = DatabaseScripts.Postgres.Replace("CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";", "")
                .Replace("GRANT SELECT ON ALL TABLES IN SCHEMA sales TO reporting;", "");
            await using var cmd = new NpgsqlCommand(script, c);
            await cmd.ExecuteNonQueryAsync();
        }
        var live = await LiveSchemaReader.ReadAsync(LiveProviders.PostgreSql, cs, ["public", "sales", "inventory"], TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal("postgresql", live.Dialect);
        var order = live.FindTable("sales", "order")!;
        Assert.Equal("serial", order.FindColumn("id")!.Type);
        Assert.Equal("customer", order.ForeignKeys.Single().RefTable);
        Assert.Equal("public", order.ForeignKeys.Single().RefSchema);
        Assert.Equal("RESTRICT", order.ForeignKeys.Single().OnDelete);
        Assert.Equal("CASCADE", order.ForeignKeys.Single().OnUpdate);
        Assert.Equal("always", live.FindTable("public", "customer")!.FindColumn("id")!.Identity);
        Assert.Equal("Store credit in EUR", live.FindTable("public", "customer")!.FindColumn("credit")!.Comment);
        Assert.Contains(order.Indexes, i => i.Name == "order_open_idx" && i.Where != null);
        Assert.Equal("order_touch", order.Triggers.Single().Name);
        Assert.Equal(["new", "paid", "shipped", "cancelled"], live.FindSchema("sales")!.Types.Single().Values);
        Assert.Equal("invoice_no", live.FindSchema("sales")!.Sequences.Single().Name);   // the serial's own sequence is not listed
        Assert.Contains(live.FindSchema("sales")!.Routines, r => r.Name == "touch");
        Assert.Equal(DbFields.MaterializedView, live.FindTable("sales", "daily_totals")!.Kind);

        // A model imported from the DDL and refreshed from the live database: no structural differences.
        var m = new ModelRepository("M");
        var r = DbImport.Import(m, m.Root, DdlParser.Parse(DatabaseScripts.Postgres, "postgresql", live.Name));
        var plan = DbRefresh.Plan(m, r.Database, live);
        // (View columns gain the types the server derived; the script did not state them.)
        var structural = plan.Changes.Where(c => c.Type is DbObjectType.Table or DbObjectType.Column or DbObjectType.PrimaryKey or DbObjectType.ForeignKey && c.Table is not ("open_orders" or "daily_totals")).ToList();
        Assert.True(structural.Count == 0, string.Join("\n", structural.Select(c => c.Describe())));
    }

    [SkippableFact]
    public async Task Unreachable_Server_Times_Out_With_A_Message_Without_The_Password()
    {
        Skip.If(server.SkipReason != null, server.SkipReason);
        var ex = await Assert.ThrowsAsync<DatabaseImportException>(() => LiveSchemaReader.ReadAsync(LiveProviders.PostgreSql,
            "Host=127.0.0.1;Port=1;Username=u;Password=s3cret;Database=x", null, TimeSpan.FromSeconds(3), CancellationToken.None));
        Assert.DoesNotContain("s3cret", ex.Message);
    }
}
