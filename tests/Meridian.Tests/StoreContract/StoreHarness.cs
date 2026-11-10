using System.Data.Common;
using System.Globalization;
using Meridian.Data;
using Microsoft.Data.Sqlite;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Meridian.Tests.StoreContract;

/// <summary>One empty database for one test, with its store and raw SQL access (for schema-level assertions).</summary>
public interface IStoreHarness : IAsyncDisposable
{
    IMeridianStore Store { get; }
    string Provider { get; }

    /// <summary>Opens a second, independent store on the same database (as another server process would).</summary>
    IMeridianStore OpenAnotherStore();

    /// <summary>Runs SQL with positional parameters ($1, $2 …). Guids, timestamps and booleans are converted for the provider.</summary>
    Task<int> ExecRawAsync(string sql, params object?[] args);
    Task<object?> ScalarRawAsync(string sql, params object?[] args);

    /// <summary>Wraps a parameter placeholder so a JSON text parameter can be inserted into a model column.</summary>
    string JsonParam(string placeholder);

    /// <summary>The exact schema that Meridian 0.1.0 created (reconstructed from git history at tag 0.1.0, commit 771abbb).</summary>
    string V010Schema { get; }
}

// ===================================================================== SQLite

public sealed class SqliteHarness : IStoreHarness
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-contract-{Guid.NewGuid():N}.db");
    private readonly List<SqliteStore> _stores = [];

    public SqliteHarness() => Store = OpenAnotherStore();

    public IMeridianStore Store { get; }
    public string Provider => "sqlite";

    public IMeridianStore OpenAnotherStore()
    {
        var s = new SqliteStore(_path);
        _stores.Add(s);
        return s;
    }

    private async Task<SqliteCommand> CommandAsync(SqliteConnection c, string sql, object?[] args)
    {
        await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue("$" + (i + 1), args[i] switch
            {
                null => DBNull.Value,
                Guid g => g.ToString("D"),
                DateTime t => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                bool b => b ? 1 : 0,
                var v => v,
            });
        return cmd;
    }

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _path, ForeignKeys = true, Pooling = false }.ToString();

    public async Task<int> ExecRawAsync(string sql, params object?[] args)
    {
        await using var c = new SqliteConnection(ConnectionString);
        await using var cmd = await CommandAsync(c, sql, args);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<object?> ScalarRawAsync(string sql, params object?[] args)
    {
        await using var c = new SqliteConnection(ConnectionString);
        await using var cmd = await CommandAsync(c, sql, args);
        return await cmd.ExecuteScalarAsync();
    }

    public string JsonParam(string placeholder) => placeholder;

    public string V010Schema => """
        create table if not exists users (
            id text primary key,
            username text not null unique,
            display_name text not null,
            password_hash text not null,
            color text not null,
            created_at text not null);

        create table if not exists projects (
            id text primary key,
            name text not null,
            model text not null,
            revision integer not null default 0,
            created_by text references users(id) on delete set null,
            updated_by text references users(id) on delete set null,
            created_at text not null,
            updated_at text not null);

        create table if not exists project_members (
            project_id text not null references projects(id) on delete cascade,
            user_id text not null references users(id) on delete cascade,
            role integer not null,
            primary key (project_id, user_id));

        create table if not exists project_versions (
            id integer primary key autoincrement,
            project_id text not null references projects(id) on delete cascade,
            number integer not null,
            message text not null,
            model text not null,
            author text references users(id) on delete set null,
            created_at text not null,
            unique (project_id, number));

        create table if not exists comments (
            id integer primary key autoincrement,
            project_id text not null references projects(id) on delete cascade,
            element_id text not null,
            author text references users(id) on delete set null,
            body text not null,
            resolved integer not null default 0,
            created_at text not null);

        create index if not exists comments_project_element on comments(project_id, element_id);
        create index if not exists members_user on project_members(user_id);
        """;

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}

// ===================================================================== PostgreSQL

/// <summary>
/// One throwaway PostgreSQL server (Testcontainers, random host port) shared by a test class; every test gets its own database.
/// Without Docker the tests are skipped, unless MERIDIAN_REQUIRE_POSTGRES=1 (CI), in which case they fail.
/// </summary>
public sealed class PostgresServerFixture : IAsyncLifetime
{
    /// <summary>The server image; MERIDIAN_POSTGRES_IMAGE overrides it (e.g. to check an older major version).</summary>
    public static string Image => Environment.GetEnvironmentVariable("MERIDIAN_POSTGRES_IMAGE") is { Length: > 0 } image ? image : "postgres:17-alpine";
    private PostgreSqlContainer? _container;
    private int _databases;

    public string? SkipReason { get; private set; }

    public static bool Required => Environment.GetEnvironmentVariable("MERIDIAN_REQUIRE_POSTGRES") is "1" or "true";

    /// <summary>
    /// An existing server to use instead of starting a container (CI services without Docker, e.g. a GitLab CI <c>postgres</c>
    /// service): an Npgsql connection string for a role that may create databases.
    /// </summary>
    public static string? ExistingServer => Environment.GetEnvironmentVariable("MERIDIAN_POSTGRES_CONNECTION") is { Length: > 0 } cs ? cs : null;

    private string? _existing;

    private string ServerConnectionString => _existing ?? _container!.GetConnectionString();

    public async Task InitializeAsync()
    {
        if (ExistingServer is { } existing)
        {
            try
            {
                await using var probe = new NpgsqlConnection(existing);
                await probe.OpenAsync();
                _existing = existing;
            }
            catch (Exception ex) when (!Required)
            {
                SkipReason = $"PostgreSQL contract tests skipped: could not connect with MERIDIAN_POSTGRES_CONNECTION ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}).";
            }
            return;
        }
        try
        {
            _container = new PostgreSqlBuilder(Image)
                .WithCommand("-c", "max_connections=500")
                .Build();
            await _container.StartAsync();
        }
        catch (Exception ex) when (!Required)
        {
            SkipReason = $"PostgreSQL contract tests skipped: could not start {Image} (is Docker running?) ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}). " +
                         "Start Docker, or set MERIDIAN_REQUIRE_POSTGRES=1 to make this a failure.";
            _container = null;
        }
    }

    public async Task<string> CreateDatabaseAsync()
    {
        if (_container is null && _existing is null) throw new InvalidOperationException(SkipReason);
        var name = $"meridian_{Interlocked.Increment(ref _databases)}_{Guid.NewGuid():N}"[..40];
        await using (var admin = new NpgsqlConnection(ServerConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"create database \"{name}\"", admin);
            await cmd.ExecuteNonQueryAsync();
        }
        return new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = name }.ToString();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}

public sealed class PostgresHarness(string connectionString) : IStoreHarness
{
    private readonly List<PostgresStore> _stores = [];
    private PostgresStore? _first;

    public IMeridianStore Store => _first ??= (PostgresStore)OpenAnotherStore();
    public string Provider => "postgres";

    public IMeridianStore OpenAnotherStore()
    {
        var s = new PostgresStore(connectionString);
        _stores.Add(s);
        return s;
    }

    private static NpgsqlCommand Command(NpgsqlConnection c, string sql, object?[] args)
    {
        var cmd = new NpgsqlCommand(sql, c);
        foreach (var a in args) cmd.Parameters.Add(new NpgsqlParameter { Value = a ?? DBNull.Value });
        return cmd;
    }

    public async Task<int> ExecRawAsync(string sql, params object?[] args)
    {
        await using var c = new NpgsqlConnection(connectionString + ";Pooling=false");
        await c.OpenAsync();
        await using var cmd = Command(c, sql, args);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<object?> ScalarRawAsync(string sql, params object?[] args)
    {
        await using var c = new NpgsqlConnection(connectionString + ";Pooling=false");
        await c.OpenAsync();
        await using var cmd = Command(c, sql, args);
        var v = await cmd.ExecuteScalarAsync();
        return v is DBNull ? null : v;
    }

    public string JsonParam(string placeholder) => placeholder + "::jsonb";

    public string V010Schema => """
        create table if not exists users (
            id uuid primary key,
            username text not null unique,
            display_name text not null,
            password_hash text not null,
            color text not null,
            created_at timestamptz not null default now());

        create table if not exists projects (
            id uuid primary key,
            name text not null,
            model jsonb not null,
            revision bigint not null default 0,
            created_by uuid references users(id) on delete set null,
            updated_by uuid references users(id) on delete set null,
            created_at timestamptz not null default now(),
            updated_at timestamptz not null default now());

        create table if not exists project_members (
            project_id uuid not null references projects(id) on delete cascade,
            user_id uuid not null references users(id) on delete cascade,
            role smallint not null,
            primary key (project_id, user_id));

        create table if not exists project_versions (
            id bigserial primary key,
            project_id uuid not null references projects(id) on delete cascade,
            number int not null,
            message text not null,
            model jsonb not null,
            author uuid references users(id) on delete set null,
            created_at timestamptz not null default now(),
            unique (project_id, number));

        create table if not exists comments (
            id bigserial primary key,
            project_id uuid not null references projects(id) on delete cascade,
            element_id text not null,
            author uuid references users(id) on delete set null,
            body text not null,
            resolved boolean not null default false,
            created_at timestamptz not null default now());

        create index if not exists comments_project_element on comments(project_id, element_id);
        create index if not exists members_user on project_members(user_id);
        """;

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _stores) await s.DisposeAsync();
    }
}
