namespace Meridian.Tests.StoreContract;

/// <summary>The store contract against SQLite (a temporary database file per test).</summary>
public sealed class SqliteStoreContractTests : StoreContract
{
    protected override Task<IStoreHarness> NewDatabaseAsync() => Task.FromResult<IStoreHarness>(new SqliteHarness());
}

/// <summary>
/// The store contract against PostgreSQL: one throwaway container for this class (Testcontainers, random port), a new database per test.
/// Skipped with a reason when Docker is not available.
/// </summary>
public sealed class PostgresStoreContractTests(PostgresServerFixture server) : StoreContract, IClassFixture<PostgresServerFixture>
{
    protected override async Task<IStoreHarness> NewDatabaseAsync()
    {
        Skip.If(server.SkipReason is not null, server.SkipReason);
        return new PostgresHarness(await server.CreateDatabaseAsync());
    }

    /// <summary>
    /// Deleting a project cascades through versions, reviews, comments and so on; without an index on the referencing
    /// column PostgreSQL scans the whole child table once per deleted parent row. (Foreign keys to users are exempt:
    /// accounts are disabled, never deleted.)
    /// </summary>
    [SkippableFact]
    public async Task Every_Foreign_Key_Has_An_Index()
    {
        await using var h = await OpenAsync();
        var missing = await h.ScalarRawAsync("""
            select string_agg(c.conrelid::regclass::text || '.' || a.attname, ', ' order by 1)
            from pg_constraint c
            join pg_attribute a on a.attrelid = c.conrelid and a.attnum = c.conkey[1]
            where c.contype = 'f' and c.confrelid <> 'users'::regclass
              and not exists (select 1 from pg_index i where i.indrelid = c.conrelid and i.indkey[0] = c.conkey[1])
            """);
        Assert.True(missing is null, $"Foreign keys without an index: {missing}");
    }

    [SkippableFact]
    public async Task Timestamps_Are_Stored_With_Time_Zone()
    {
        await using var h = await OpenAsync();
        var naive = await h.ScalarRawAsync("""
            select string_agg(table_name || '.' || column_name, ', ' order by 1) from information_schema.columns
            where table_schema = 'public' and data_type = 'timestamp without time zone'
            """);
        Assert.True(naive is null, $"timestamp without time zone: {naive}");
    }
}
