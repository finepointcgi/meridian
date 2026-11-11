using System.Data.Common;
using System.Text.RegularExpressions;
using Meridian.Core.DataModeling;
using Meridian.Serialization.Database;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace Meridian.Data.Databases;

/// <summary>Database engines Meridian can read a schema from over a live connection.</summary>
public static class LiveProviders
{
    public const string PostgreSql = "postgresql";
    public const string MySql = "mysql";
    public const string Sqlite = "sqlite";
    public const string SqlServer = "sqlserver";
    public const string Oracle = "oracle";

    public static readonly IReadOnlyList<string> All = [PostgreSql, SqlServer, MySql, Oracle, Sqlite];

    public static string Display(string p) => p switch
    {
        MySql => "MySQL / MariaDB", Sqlite => "SQLite (file)", SqlServer => "SQL Server / Azure SQL", Oracle => "Oracle", _ => "PostgreSQL",
    };
}

/// <summary>Where a connection string points: a host and port to check against the outbound guard, or a local file.</summary>
public sealed record DatabaseEndpoint(string Provider, string? Host, int Port, string? File, string Database);

/// <summary>
/// Reads a live database's schema into a <see cref="DbCatalog"/> from its catalog views (pg_catalog for PostgreSQL,
/// sys.* for SQL Server, information_schema for MySQL / MariaDB, ALL_* for Oracle, sqlite_master for SQLite). Read-only:
/// every query runs in a read-only transaction where the engine has one (SQL Server: only SELECTs, in a transaction that
/// is rolled back; SQLite opens the file read-only), and nothing is ever written. Timeouts apply to the connection and to
/// every command, and the whole read honours the cancellation token.
/// </summary>
public static partial class LiveSchemaReader
{
    /// <summary>Parses the endpoint out of a connection string (for the outbound guard and for display).</summary>
    public static DatabaseEndpoint Endpoint(string provider, string connectionString)
    {
        switch (provider)
        {
            case LiveProviders.PostgreSql:
            {
                var b = new NpgsqlConnectionStringBuilder(connectionString);
                return new DatabaseEndpoint(provider, b.Host, b.Port == 0 ? 5432 : b.Port, null, b.Database ?? "");
            }
            case LiveProviders.MySql:
            {
                var b = new MySqlConnectionStringBuilder(connectionString);
                return new DatabaseEndpoint(provider, b.Server, (int)(b.Port == 0 ? 3306 : b.Port), null, b.Database ?? "");
            }
            case LiveProviders.Sqlite:
            {
                var b = new SqliteConnectionStringBuilder(connectionString);
                return new DatabaseEndpoint(provider, null, 0, b.DataSource, Path.GetFileNameWithoutExtension(b.DataSource));
            }
            case LiveProviders.SqlServer:
            {
                var b = new SqlConnectionStringBuilder(connectionString);
                var (host, port, _) = SqlServerSource(b.DataSource);
                // A failover partner is a second host: listed with the first, so a team server refuses it like any multi-host string.
                if (b.FailoverPartner is { Length: > 0 } partner) host = $"{host},{SqlServerSource(partner).Host}";
                return new DatabaseEndpoint(provider, host, port, null, b.InitialCatalog ?? "");
            }
            case LiveProviders.Oracle:
            {
                var b = new OracleConnectionStringBuilder(connectionString);
                var (host, port, service) = OracleSource(b.DataSource);
                return new DatabaseEndpoint(provider, host, port, null, service ?? b.DataSource ?? "");
            }
            default:
                throw new NotSupportedException($"Live connections to {provider} are not supported; import a DDL script instead.");
        }
    }

    /// <summary>
    /// Reads the schema. <paramref name="schemas"/> limits it (empty: every non-system schema). <paramref name="pinHost"/>, when
    /// given, replaces the host by the address the caller checked (so a DNS answer can't change between check and connect).
    /// </summary>
    public static async Task<DbCatalog> ReadAsync(string provider, string connectionString, IReadOnlyCollection<string>? schemas, TimeSpan timeout,
        CancellationToken ct, string? pinHost = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var seconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
        try
        {
            switch (provider)
            {
                case LiveProviders.PostgreSql:
                {
                    var b = new NpgsqlConnectionStringBuilder(connectionString) { Timeout = Math.Min(seconds, 1024), CommandTimeout = seconds, Pooling = false, ApplicationName = "Meridian schema import" };
                    if (pinHost != null) b.Host = pinHost;
                    await using var c = new NpgsqlConnection(b.ConnectionString);
                    await c.OpenAsync(cts.Token);
                    return await new PostgresSchema(c, seconds).ReadAsync(b.Database ?? "postgres", schemas, cts.Token);
                }
                case LiveProviders.MySql:
                {
                    var b = new MySqlConnectionStringBuilder(connectionString) { ConnectionTimeout = (uint)seconds, DefaultCommandTimeout = (uint)seconds, Pooling = false };
                    if (pinHost != null) b.Server = pinHost;
                    await using var c = new MySqlConnection(b.ConnectionString);
                    await c.OpenAsync(cts.Token);
                    return await new MySqlSchema(c, seconds).ReadAsync(b.Database ?? "", schemas, cts.Token);
                }
                case LiveProviders.SqlServer:
                {
                    // Managed networking on every platform (the native Windows SNI library is not shipped).
                    AppContext.SetSwitch("Switch.Microsoft.Data.SqlClient.UseManagedNetworkingOnWindows", true);
                    var b = new SqlConnectionStringBuilder(connectionString)
                    {
                        ConnectTimeout = seconds, CommandTimeout = seconds, Pooling = false, ApplicationName = "Meridian schema import", ApplicationIntent = ApplicationIntent.ReadOnly,
                        ConnectRetryCount = 0,
                    };
                    if (pinHost != null)
                    {
                        var (host, port, instance) = SqlServerSource(b.DataSource);
                        // The certificate is still checked against the name the user gave (the address was checked just now).
                        if (string.IsNullOrEmpty(b.HostNameInCertificate) && host != null && !System.Net.IPAddress.TryParse(host.Trim('[', ']'), out _)) b.HostNameInCertificate = host;
                        b.DataSource = $"tcp:{(pinHost.Contains(':') ? $"[{pinHost}]" : pinHost)}{(instance != null ? "\\" + instance : "")}{(port > 0 && instance == null ? $",{port}" : "")}";
                    }
                    await using var c = new SqlConnection(b.ConnectionString);
                    await c.OpenAsync(cts.Token);
                    return await new SqlServerSchema(c, seconds).ReadAsync(schemas, cts.Token);
                }
                case LiveProviders.Oracle:
                {
                    var b = new OracleConnectionStringBuilder(connectionString) { ConnectionTimeout = seconds, Pooling = false };
                    if (pinHost != null) b.DataSource = OracleWithHost(b.DataSource, pinHost);
                    await using var c = new OracleConnection(b.ConnectionString);
                    await c.OpenAsync(cts.Token);
                    return await new OracleSchema(c, seconds).ReadAsync(schemas, cts.Token);
                }
                case LiveProviders.Sqlite:
                {
                    var b = new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = seconds };
                    if (!b.DataSource.Equals(":memory:", StringComparison.Ordinal) && !File.Exists(b.DataSource) && !b.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                        throw new DatabaseImportException($"The SQLite file {b.DataSource} does not exist.");
                    await using var c = new SqliteConnection(b.ConnectionString);
                    await c.OpenAsync(cts.Token);
                    return await SqliteSchema.ReadAsync(c, Path.GetFileNameWithoutExtension(b.DataSource), cts.Token);
                }
                default:
                    throw new DatabaseImportException($"Live connections to {provider} are not supported; import a DDL script instead.");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DatabaseImportException($"Reading the schema took longer than {seconds} seconds and was stopped.");
        }
        catch (Exception ex) when (ex is DbException or ArgumentException or InvalidOperationException or System.Net.Sockets.SocketException or TimeoutException)
        {
            throw new DatabaseImportException(Describe(ex));
        }
    }

    /// <summary>
    /// SQL Server's Data Source: "host", "host,port", "tcp:host,port" or "host\instance". Named pipes, shared memory and
    /// LocalDB have no host (a team server refuses them).
    /// </summary>
    internal static (string? Host, int Port, string? Instance) SqlServerSource(string? dataSource)
    {
        var ds = (dataSource ?? "").Trim();
        if (ds.Length == 0 || ds.StartsWith("np:", StringComparison.OrdinalIgnoreCase) || ds.StartsWith("lpc:", StringComparison.OrdinalIgnoreCase)
            || ds.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase) || ds.StartsWith(@"\\", StringComparison.Ordinal)) return (null, 0, null);
        if (ds.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) ds = ds[4..];
        var port = 1433;
        var comma = ds.LastIndexOf(',');
        if (comma > 0 && int.TryParse(ds[(comma + 1)..].Trim(), out var p)) { port = p; ds = ds[..comma]; }
        string? instance = null;
        var slash = ds.IndexOf('\\');
        if (slash > 0) { instance = ds[(slash + 1)..]; ds = ds[..slash]; if (comma < 0) port = 0; }
        if (ds is "." or "(local)") ds = "localhost";
        return (ds.Trim(), port, instance);
    }

    [GeneratedRegex(@"\(\s*HOST\s*=\s*([^)\s]+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex OracleHost();

    [GeneratedRegex(@"\(\s*PORT\s*=\s*(\d+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex OraclePort();

    [GeneratedRegex(@"\(\s*(?:SERVICE_NAME|SID)\s*=\s*([^)\s]+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex OracleService();

    [GeneratedRegex(@"^(?:(?:tcps?)://)?(?<host>\[[^\]]+\]|[^:/\s(]+)(?::(?<port>\d+))?(?:/(?<svc>[^\s:/?]+))?(?:[:/?].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex OracleEasyConnect();

    /// <summary>
    /// Oracle's Data Source: an Easy Connect string ("host[:port]/service") or a connect descriptor ("(DESCRIPTION=(ADDRESS=
    /// (HOST=…)(PORT=…))…)"); several ADDRESS entries come back as a comma separated host list. A TNS alias has no host here
    /// (it is resolved from tnsnames.ora or a directory, which a team server refuses).
    /// </summary>
    internal static (string? Host, int Port, string? Service) OracleSource(string? dataSource)
    {
        var ds = (dataSource ?? "").Trim();
        if (ds.StartsWith('('))
        {
            var hosts = OracleHost().Matches(ds).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var port = OraclePort().Match(ds) is { Success: true } pm ? int.Parse(pm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 1521;
            return (hosts.Count == 0 ? null : string.Join(",", hosts), port, OracleService().Match(ds) is { Success: true } sm ? sm.Groups[1].Value : null);
        }
        if (ds.Contains('/') || ds.Contains(':') || ds.Contains("://", StringComparison.Ordinal))
        {
            var m = OracleEasyConnect().Match(ds);
            if (m.Success)
                return (m.Groups["host"].Value.Trim('[', ']'), m.Groups["port"].Success ? int.Parse(m.Groups["port"].Value, System.Globalization.CultureInfo.InvariantCulture) : 1521,
                    m.Groups["svc"].Success ? m.Groups["svc"].Value : null);
        }
        return (null, 0, null);   // a TNS alias
    }

    /// <summary>The Data Source with its host replaced by the checked address.</summary>
    internal static string OracleWithHost(string dataSource, string address)
    {
        var a = address.Contains(':') ? $"[{address}]" : address;
        if (dataSource.TrimStart().StartsWith('(')) return OracleHost().Replace(dataSource, $"(HOST={a})");
        var m = OracleEasyConnect().Match(dataSource.Trim());
        if (!m.Success) return dataSource;
        var g = m.Groups["host"];
        return dataSource.Trim()[..g.Index] + a + dataSource.Trim()[(g.Index + g.Length)..];
    }

    /// <summary>A message for the user without connection-string details.</summary>
    private static string Describe(Exception ex)
    {
        var msg = ex.Message;
        // Drivers may echo connection string fragments; never pass a password along.
        msg = Regex.Replace(msg, @"(?i)(password|pwd)\s*=\s*[^;]*", "$1=***");
        return $"Could not read the database: {msg}";
    }

    internal static async Task<List<object?[]>> Rows(DbConnection c, DbTransaction? tx, string sql, int timeout, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeout;
        cmd.Transaction = tx;
        // Drivers whose async calls don't watch the token still stop when the command is cancelled.
        await using var reg = ct.Register(() => { try { cmd.Cancel(); } catch (Exception ex) when (ex is DbException or InvalidOperationException) { } });
        foreach (var (name, value) in args)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        var rows = new List<object?[]>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    internal static string? S(object? o) => o switch { null => null, string s => s, _ => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) };
}

public sealed class DatabaseImportException(string message) : Exception(message);

/// <summary>PostgreSQL through pg_catalog.</summary>
internal sealed class PostgresSchema(NpgsqlConnection c, int timeout)
{

    public async Task<DbCatalog> ReadAsync(string database, IReadOnlyCollection<string>? only, CancellationToken ct)
    {
        await using var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using (var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY", c, tx)) await ro.ExecuteNonQueryAsync(ct);
        Task<List<object?[]>> Q(string sql, params (string, object)[] args) => LiveSchemaReader.Rows(c, tx, sql, timeout, ct, args);
        var catalog = new DbCatalog { Name = database, Dialect = "postgresql" };

        var nsRows = await Q("select n.oid, n.nspname, obj_description(n.oid, 'pg_namespace') from pg_namespace n " +
                             "where n.nspname not like 'pg\\_%' and n.nspname <> 'information_schema' order by n.nspname");
        var schemas = nsRows.Select(r => (Oid: Convert.ToUInt32(r[0]), Name: (string)r[1]!, Comment: LiveSchemaReader.S(r[2])))
            .Where(s => only == null || only.Count == 0 || only.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var names = schemas.Select(s => s.Name).ToArray();
        foreach (var s in schemas) catalog.Schema(s.Name).Comment = s.Comment;
        if (names.Length == 0) return catalog;

        // Tables, views, materialized views.
        var rels = await Q("select c.oid, n.nspname, c.relname, c.relkind::text, obj_description(c.oid, 'pg_class'), case when c.relkind in ('v','m') then pg_get_viewdef(c.oid, true) end " +
                           "from pg_class c join pg_namespace n on n.oid = c.relnamespace where c.relkind in ('r','p','v','m') and not c.relispartition and n.nspname = any(@s) order by n.nspname, c.relname", ("s", names));
        var tables = new Dictionary<uint, (string Schema, DbTableDef Table)>();
        foreach (var r in rels)
        {
            var kind = (string)r[3]!;
            var t = catalog.Schema((string)r[1]!).Table((string)r[2]!);
            t.Kind = kind == "v" ? DbFields.View : kind == "m" ? DbFields.MaterializedView : "";
            t.Comment = LiveSchemaReader.S(r[4]);
            t.Definition = LiveSchemaReader.S(r[5])?.Trim().TrimEnd(';');
            tables[Convert.ToUInt32(r[0])] = ((string)r[1]!, t);
        }
        if (tables.Count == 0) return await Rest(catalog, names, Q);
        var oids = tables.Keys.Select(o => (long)o).ToArray();

        // Columns (serial: an integer column whose default takes the next value of a sequence it owns).
        var cols = await Q("select a.attrelid, a.attnum, a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull, pg_get_expr(d.adbin, d.adrelid), " +
                           "a.attidentity::text, a.attgenerated::text, col_description(a.attrelid, a.attnum), case when a.attcollation <> t.typcollation then co.collname end, " +
                           "pg_get_serial_sequence(quote_ident(n.nspname) || '.' || quote_ident(c.relname), a.attname) is not null and a.attidentity = '' " +
                           "from pg_attribute a join pg_class c on c.oid = a.attrelid join pg_namespace n on n.oid = c.relnamespace join pg_type t on t.oid = a.atttypid " +
                           "left join pg_attrdef d on d.adrelid = a.attrelid and d.adnum = a.attnum left join pg_collation co on co.oid = a.attcollation " +
                           "where a.attrelid = any(@o) and a.attnum > 0 and not a.attisdropped order by a.attrelid, a.attnum", ("o", oids));
        var attnames = new Dictionary<(uint, short), string>();
        foreach (var r in cols)
        {
            var (_, t) = tables[Convert.ToUInt32(r[0])];
            var type = (string)r[3]!;
            var def = LiveSchemaReader.S(r[5]);
            var serial = r[10] is true && def != null && def.StartsWith("nextval(", StringComparison.Ordinal);
            if (serial) { type = type switch { "bigint" => "bigserial", "smallint" => "smallserial", _ => "serial" }; def = null; }
            var identity = LiveSchemaReader.S(r[6]) switch { "a" => "always", "d" => "byDefault", _ => null };
            var generated = LiveSchemaReader.S(r[7]) is "s" ? def : null;
            t.Columns.Add(new DbColumnDef
            {
                Name = (string)r[2]!, Type = type, Nullable = r[4] is not true, Default = generated == null ? def : null, Identity = identity, Generated = generated,
                Comment = LiveSchemaReader.S(r[8]), Collation = LiveSchemaReader.S(r[9]),
            });
            attnames[(Convert.ToUInt32(r[0]), Convert.ToInt16(r[1]))] = (string)r[2]!;
        }

        // Keys and constraints. Referenced tables may sit in schemas that are not imported.
        var cons = await Q("select k.conrelid, k.conname, k.contype::text, pg_get_constraintdef(k.oid, true), k.conkey, k.confrelid, k.confkey, k.confdeltype::text, k.confupdtype::text, " +
                           "rn.nspname, rc.relname, pg_get_expr(k.conbin, k.conrelid, true) " +
                           "from pg_constraint k left join pg_class rc on rc.oid = k.confrelid left join pg_namespace rn on rn.oid = rc.relnamespace " +
                           "where k.conrelid = any(@o) and k.contype in ('p','u','f','c') order by k.conrelid, k.conname", ("o", oids));
        var refAtt = new Dictionary<(uint, short), string>(attnames);
        var foreignRefs = cons.Where(r => (string)r[2]! == "f" && !tables.ContainsKey(Convert.ToUInt32(r[5]))).Select(r => (long)Convert.ToUInt32(r[5])).Distinct().ToArray();
        if (foreignRefs.Length > 0)
            foreach (var r in await Q("select attrelid, attnum, attname from pg_attribute where attrelid = any(@o) and attnum > 0", ("o", foreignRefs)))
                refAtt[(Convert.ToUInt32(r[0]), Convert.ToInt16(r[1]))] = (string)r[2]!;
        foreach (var r in cons)
        {
            var rel = Convert.ToUInt32(r[0]);
            var (schema, t) = tables[rel];
            List<string> Cols(object? keys, uint table) => keys is short[] ks ? ks.Select(k => refAtt.TryGetValue((table, k), out var n) ? n : $"#{k}").ToList() : [];
            var name = (string)r[1]!;
            switch ((string)r[2]!)
            {
                case "p":
                    t.PrimaryKey.Clear();
                    t.PrimaryKey.AddRange(Cols(r[4], rel));
                    t.PrimaryKeyName = name;
                    break;
                case "u":
                {
                    var cs = Cols(r[4], rel);
                    var uq = new DbConstraintDef { Name = name, Type = DbFields.UniqueConstraint };
                    uq.Columns.AddRange(cs);
                    t.Constraints.Add(uq);
                    break;
                }
                case "c":
                    t.Constraints.Add(new DbConstraintDef { Name = name, Type = DbFields.CheckConstraint, Expression = LiveSchemaReader.S(r[11]) });
                    break;
                case "f":
                {
                    var target = Convert.ToUInt32(r[5]);
                    var fk = new DbForeignKeyDef
                    {
                        Name = name, RefSchema = LiveSchemaReader.S(r[9]), RefTable = LiveSchemaReader.S(r[10]) ?? "", OnDelete = Action(LiveSchemaReader.S(r[7])), OnUpdate = Action(LiveSchemaReader.S(r[8])),
                    };
                    if (fk.RefSchema == schema) fk.RefSchema = null;
                    fk.Columns.AddRange(Cols(r[4], rel));
                    fk.RefColumns.AddRange(Cols(r[6], target));
                    t.ForeignKeys.Add(fk);
                    break;
                }
            }
        }

        // Indexes that do not back a constraint.
        var ix = await Q("select i.indrelid, ic.relname, i.indisunique, am.amname, pg_get_expr(i.indpred, i.indrelid, true), " +
                         "array(select pg_get_indexdef(i.indexrelid, k, true) from generate_series(1, i.indnkeyatts) k) " +
                         "from pg_index i join pg_class ic on ic.oid = i.indexrelid join pg_am am on am.oid = ic.relam " +
                         "where i.indrelid = any(@o) and not exists (select 1 from pg_constraint k where k.conindid = i.indexrelid and k.conrelid = i.indrelid) order by ic.relname", ("o", oids));
        foreach (var r in ix)
        {
            var (_, t) = tables[Convert.ToUInt32(r[0])];
            var idx = new DbIndexDef { Name = (string)r[1]!, Unique = r[2] is true, Method = (string)r[3]! == "btree" ? null : (string)r[3]!, Where = LiveSchemaReader.S(r[4]) };
            if (r[5] is string[] keys) idx.Columns.AddRange(keys.Select(k => k.Trim('"')));
            t.Indexes.Add(idx);
        }

        // Triggers: pg_get_triggerdef's CREATE TRIGGER, read by the DDL parser (WHEN condition and EXECUTE FUNCTION kept).
        foreach (var r in await Q("select tgrelid, tgname, pg_get_triggerdef(oid, true) from pg_trigger where tgrelid = any(@o) and not tgisinternal order by tgname", ("o", oids)))
        {
            var (_, t) = tables[Convert.ToUInt32(r[0])];
            var tg = DdlParser.ParseModule((string)r[2]!, "postgresql").Trigger ?? new DbTriggerDef();
            tg.Name = (string)r[1]!;
            t.Triggers.Add(tg);
        }
        return await Rest(catalog, names, Q);
    }

    /// <summary>Sequences (not the ones owned by serial / identity columns), enum and domain types, functions and procedures.</summary>
    private static async Task<DbCatalog> Rest(DbCatalog catalog, string[] names, Func<string, (string, object)[], Task<List<object?[]>>> q)
    {
        foreach (var r in await q("select n.nspname, c.relname, format_type(s.seqtypid, null), s.seqstart, s.seqincrement, s.seqmin, s.seqmax, s.seqcycle " +
                                  "from pg_sequence s join pg_class c on c.oid = s.seqrelid join pg_namespace n on n.oid = c.relnamespace " +
                                  "where n.nspname = any(@s) and not exists (select 1 from pg_depend d where d.objid = c.oid and d.classid = 'pg_class'::regclass and d.deptype in ('a','i')) order by 1, 2", [("s", names)]))
        {
            var start = Convert.ToInt64(r[3]);
            var inc = Convert.ToInt64(r[4]);
            catalog.Schema((string)r[0]!).Sequences.Add(new DbSequenceDef
            {
                Name = (string)r[1]!, Type = (string)r[2]! == "bigint" ? null : (string)r[2]!, Start = start == 1 ? null : start.ToString(), Increment = inc == 1 ? null : inc.ToString(), Cycle = r[7] is true,
            });
        }
        foreach (var r in await q("select n.nspname, t.typname, array(select e.enumlabel::text from pg_enum e where e.enumtypid = t.oid order by e.enumsortorder), obj_description(t.oid, 'pg_type') " +
                                  "from pg_type t join pg_namespace n on n.oid = t.typnamespace where t.typtype = 'e' and n.nspname = any(@s) order by 1, 2", [("s", names)]))
        {
            var td = new DbTypeDef { Name = (string)r[1]!, Kind = "enum", Comment = LiveSchemaReader.S(r[3]) };
            if (r[2] is string[] values) td.Values.AddRange(values);
            catalog.Schema((string)r[0]!).Types.Add(td);
        }
        foreach (var r in await q("select n.nspname, t.typname, format_type(t.typbasetype, t.typtypmod), t.typnotnull, " +
                                  "(select pg_get_expr(k.conbin, 0, true) from pg_constraint k where k.contypid = t.oid limit 1), obj_description(t.oid, 'pg_type') " +
                                  "from pg_type t join pg_namespace n on n.oid = t.typnamespace where t.typtype = 'd' and n.nspname = any(@s) order by 1, 2", [("s", names)]))
            catalog.Schema((string)r[0]!).Types.Add(new DbTypeDef { Name = (string)r[1]!, Kind = "domain", BaseType = (string)r[2]!, NotNull = r[3] is true, Check = LiveSchemaReader.S(r[4]), Comment = LiveSchemaReader.S(r[5]) });
        // Functions and procedures: pg_get_functiondef's CREATE OR REPLACE, read by the DDL parser for the body and options.
        foreach (var r in await q("select n.nspname, p.proname, p.prokind::text, pg_get_function_arguments(p.oid), pg_get_function_result(p.oid), l.lanname, obj_description(p.oid, 'pg_proc'), " +
                                  "case when l.lanname <> 'c' then pg_get_functiondef(p.oid) end " +
                                  "from pg_proc p join pg_namespace n on n.oid = p.pronamespace join pg_language l on l.oid = p.prolang " +
                                  "where n.nspname = any(@s) and p.prokind in ('f','p') and not exists (select 1 from pg_depend d where d.objid = p.oid and d.deptype = 'e') order by 1, 2", [("s", names)]))
        {
            var parsed = LiveSchemaReader.S(r[7]) is { } def ? DdlParser.ParseModule(def, "postgresql").Routine : null;
            catalog.Schema((string)r[0]!).Routines.Add(new DbRoutineDef
            {
                Name = (string)r[1]!, Kind = (string)r[2]! == "p" ? DbFields.Procedure : DbFields.Function, Signature = $"({r[3]})", Returns = LiveSchemaReader.S(r[4]),
                Language = LiveSchemaReader.S(r[5]), Comment = LiveSchemaReader.S(r[6]), Body = parsed?.Body, Options = parsed?.Options,
            });
        }
        return catalog;
    }

    private static string? Action(string? code) => code switch { "c" => "CASCADE", "n" => "SET NULL", "d" => "SET DEFAULT", "r" => "RESTRICT", _ => null };
}

/// <summary>SQLite: the schema is the DDL it stores in sqlite_master, read by the DDL parser.</summary>
internal static class SqliteSchema
{
    public static async Task<DbCatalog> ReadAsync(SqliteConnection c, string name, CancellationToken ct)
    {
        var rows = await LiveSchemaReader.Rows(c, null, "select sql from sqlite_master where sql is not null and name not like 'sqlite\\_%' escape '\\' order by case type when 'table' then 0 when 'index' then 1 when 'view' then 2 else 3 end, rowid", 30, ct);
        var script = string.Join(";\n", rows.Select(r => ((string)r[0]!).Trim().TrimEnd(';'))) + ";";
        var catalog = DdlParser.Parse(script, "sqlite", name);
        catalog.Name = name;
        return catalog;
    }
}

/// <summary>MySQL / MariaDB through information_schema.</summary>
internal sealed class MySqlSchema(MySqlConnection c, int timeout)
{
    private static readonly string[] SystemSchemas = ["mysql", "information_schema", "performance_schema", "sys"];

    public async Task<DbCatalog> ReadAsync(string database, IReadOnlyCollection<string>? only, CancellationToken ct)
    {
        await using (var ro = new MySqlCommand("SET SESSION TRANSACTION READ ONLY", c)) await ro.ExecuteNonQueryAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        Task<List<object?[]>> Q(string sql) => LiveSchemaReader.Rows(c, tx, sql, timeout, ct);
        var catalog = new DbCatalog { Name = database.Length > 0 ? database : "mysql", Dialect = "mysql" };
        var schemaRows = await Q("select SCHEMA_NAME from information_schema.SCHEMATA order by SCHEMA_NAME");
        var wanted = schemaRows.Select(r => (string)r[0]!).Where(s => !SystemSchemas.Contains(s, StringComparer.OrdinalIgnoreCase))
            .Where(s => only is { Count: > 0 } ? only.Contains(s, StringComparer.OrdinalIgnoreCase) : database.Length == 0 || s.Equals(database, StringComparison.OrdinalIgnoreCase)).ToList();
        if (wanted.Count == 0) return catalog;
        var list = string.Join(", ", wanted.Select(s => "'" + s.Replace("'", "''") + "'"));
        foreach (var s in wanted) catalog.Schema(s);

        foreach (var r in await Q($"select TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE, TABLE_COMMENT from information_schema.TABLES where TABLE_SCHEMA in ({list}) order by 1, 2"))
        {
            var t = catalog.Schema((string)r[0]!).Table((string)r[1]!);
            t.Kind = (string)r[2]! == "VIEW" ? DbFields.View : "";
            t.Comment = LiveSchemaReader.S(r[3]) is { Length: > 0 } cm && cm != "VIEW" ? cm : null;
        }
        foreach (var r in await Q($"select TABLE_SCHEMA, TABLE_NAME, VIEW_DEFINITION from information_schema.VIEWS where TABLE_SCHEMA in ({list})"))
            if (catalog.FindSchema((string)r[0]!)?.FindTable((string)r[1]!) is { } v) v.Definition = LiveSchemaReader.S(r[2]);
        foreach (var r in await Q($"select TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT, EXTRA, GENERATION_EXPRESSION, COLLATION_NAME, COLUMN_COMMENT " +
                                  $"from information_schema.COLUMNS where TABLE_SCHEMA in ({list}) order by TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION"))
        {
            if (catalog.FindSchema((string)r[0]!)?.FindTable((string)r[1]!) is not { } t) continue;
            var extra = LiveSchemaReader.S(r[6]) ?? "";
            var gen = extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase) ? LiveSchemaReader.S(r[7]) : null;
            t.Columns.Add(new DbColumnDef
            {
                Name = (string)r[2]!, Type = (string)r[3]!, Nullable = (string)r[4]! == "YES", Default = gen == null ? Default(LiveSchemaReader.S(r[5]), (string)r[3]!) : null,
                Identity = extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase) ? "autoIncrement" : null, Generated = gen,
                Comment = LiveSchemaReader.S(r[9]) is { Length: > 0 } cc ? cc : null,
            });
        }
        // Primary keys, unique constraints and foreign keys.
        var keyCols = await Q($"select k.TABLE_SCHEMA, k.TABLE_NAME, k.CONSTRAINT_NAME, c.CONSTRAINT_TYPE, k.COLUMN_NAME, k.REFERENCED_TABLE_SCHEMA, k.REFERENCED_TABLE_NAME, k.REFERENCED_COLUMN_NAME " +
                              $"from information_schema.KEY_COLUMN_USAGE k join information_schema.TABLE_CONSTRAINTS c on c.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA and c.TABLE_NAME = k.TABLE_NAME and c.CONSTRAINT_NAME = k.CONSTRAINT_NAME " +
                              $"where k.TABLE_SCHEMA in ({list}) order by k.TABLE_SCHEMA, k.TABLE_NAME, k.CONSTRAINT_NAME, k.ORDINAL_POSITION");
        var rules = (await Q($"select CONSTRAINT_SCHEMA, CONSTRAINT_NAME, DELETE_RULE, UPDATE_RULE from information_schema.REFERENTIAL_CONSTRAINTS where CONSTRAINT_SCHEMA in ({list})"))
            .ToDictionary(r => ((string)r[0]!, (string)r[1]!), r => (Delete: LiveSchemaReader.S(r[2]), Update: LiveSchemaReader.S(r[3])));
        foreach (var g in keyCols.GroupBy(r => ((string)r[0]!, (string)r[1]!, (string)r[2]!, (string)r[3]!)))
        {
            var (schema, table, name, type) = g.Key;
            if (catalog.FindSchema(schema)?.FindTable(table) is not { } t) continue;
            var cols = g.Select(r => (string)r[4]!).ToList();
            switch (type)
            {
                case "PRIMARY KEY":
                    t.PrimaryKey.Clear();
                    t.PrimaryKey.AddRange(cols);
                    break;
                case "UNIQUE":
                    // MySQL keeps unique keys as indexes; they are read with the indexes below.
                    break;
                case "FOREIGN KEY":
                {
                    var first = g.First();
                    var fk = new DbForeignKeyDef { Name = name, RefSchema = LiveSchemaReader.S(first[5]) is { } rs && rs != schema ? rs : null, RefTable = LiveSchemaReader.S(first[6]) ?? "" };
                    fk.Columns.AddRange(cols);
                    fk.RefColumns.AddRange(g.Select(r => LiveSchemaReader.S(r[7]) ?? ""));
                    if (rules.TryGetValue((schema, name), out var rule))
                    {
                        fk.OnDelete = rule.Delete is "NO ACTION" or "RESTRICT" ? null : rule.Delete;
                        fk.OnUpdate = rule.Update is "NO ACTION" or "RESTRICT" ? null : rule.Update;
                    }
                    t.ForeignKeys.Add(fk);
                    break;
                }
            }
        }
        foreach (var g in (await Q($"select TABLE_SCHEMA, TABLE_NAME, INDEX_NAME, NON_UNIQUE, COLUMN_NAME, INDEX_TYPE from information_schema.STATISTICS " +
                                   $"where TABLE_SCHEMA in ({list}) and INDEX_NAME <> 'PRIMARY' order by TABLE_SCHEMA, TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX")).GroupBy(r => ((string)r[0]!, (string)r[1]!, (string)r[2]!)))
        {
            if (catalog.FindSchema(g.Key.Item1)?.FindTable(g.Key.Item2) is not { } t) continue;
            var first = g.First();
            var method = LiveSchemaReader.S(first[5])?.ToLowerInvariant();
            var ix = new DbIndexDef { Name = g.Key.Item3, Unique = Convert.ToInt64(first[3]) == 0, Method = method is "fulltext" or "spatial" or "hash" ? method : null };
            ix.Columns.AddRange(g.Select(r => LiveSchemaReader.S(r[4]) ?? ""));
            t.Indexes.Add(ix);
        }
        try
        {
            foreach (var r in await Q($"select t.TABLE_SCHEMA, t.TABLE_NAME, c.CONSTRAINT_NAME, c.CHECK_CLAUSE from information_schema.CHECK_CONSTRAINTS c join information_schema.TABLE_CONSTRAINTS t " +
                                      $"on t.CONSTRAINT_SCHEMA = c.CONSTRAINT_SCHEMA and t.CONSTRAINT_NAME = c.CONSTRAINT_NAME and t.CONSTRAINT_TYPE = 'CHECK' where c.CONSTRAINT_SCHEMA in ({list})"))
                if (catalog.FindSchema((string)r[0]!)?.FindTable((string)r[1]!) is { } t)
                    t.Constraints.Add(new DbConstraintDef { Name = (string)r[2]!, Type = DbFields.CheckConstraint, Expression = LiveSchemaReader.S(r[3]) });
        }
        catch (MySqlException) { /* MySQL before 8.0.16 / MariaDB before 10.2 have no CHECK_CONSTRAINTS view */ }
        foreach (var r in await Q($"select TRIGGER_SCHEMA, EVENT_OBJECT_TABLE, TRIGGER_NAME, ACTION_TIMING, EVENT_MANIPULATION, ACTION_ORIENTATION, ACTION_STATEMENT from information_schema.TRIGGERS where TRIGGER_SCHEMA in ({list})"))
            if (catalog.FindSchema((string)r[0]!)?.FindTable((string)r[1]!) is { } t)
                t.Triggers.Add(new DbTriggerDef { Name = (string)r[2]!, Timing = (string)r[3]!, Events = (string)r[4]!, Level = LiveSchemaReader.S(r[5]), Action = LiveSchemaReader.S(r[6])?.Trim() });
        var pars = (await Q($"select SPECIFIC_SCHEMA, SPECIFIC_NAME, PARAMETER_MODE, PARAMETER_NAME, DTD_IDENTIFIER from information_schema.PARAMETERS where SPECIFIC_SCHEMA in ({list}) and ORDINAL_POSITION > 0 order by ORDINAL_POSITION"))
            .GroupBy(r => ((string)r[0]!, (string)r[1]!)).ToDictionary(g => g.Key, g => g.Select(r => $"{(LiveSchemaReader.S(r[2]) is { } mode && mode != "IN" ? mode + " " : "")}{r[3]} {r[4]}").ToList());
        // Routines: the body from ROUTINE_DEFINITION (empty without the privilege to see it), characteristics from their columns.
        foreach (var r in await Q($"select ROUTINE_SCHEMA, ROUTINE_NAME, ROUTINE_TYPE, DTD_IDENTIFIER, ROUTINE_COMMENT, ROUTINE_DEFINITION, IS_DETERMINISTIC, SQL_DATA_ACCESS, SECURITY_TYPE " +
                                  $"from information_schema.ROUTINES where ROUTINE_SCHEMA in ({list}) order by 1, 2"))
        {
            var options = new List<string>();
            if (LiveSchemaReader.S(r[6]) == "YES") options.Add("DETERMINISTIC");
            if (LiveSchemaReader.S(r[7]) is { Length: > 0 } access && access != "CONTAINS SQL") options.Add(access);
            if (LiveSchemaReader.S(r[8]) == "INVOKER") options.Add("SQL SECURITY INVOKER");
            catalog.Schema((string)r[0]!).Routines.Add(new DbRoutineDef
            {
                Name = (string)r[1]!, Kind = (string)r[2]! == "PROCEDURE" ? DbFields.Procedure : DbFields.Function,
                Signature = "(" + string.Join(", ", pars.GetValueOrDefault(((string)r[0]!, (string)r[1]!)) ?? []) + ")",
                Returns = (string)r[2]! == "PROCEDURE" ? null : LiveSchemaReader.S(r[3]), Comment = LiveSchemaReader.S(r[4]) is { Length: > 0 } rc ? rc : null,
                Body = LiveSchemaReader.S(r[5]) is { Length: > 0 } body ? body.Trim() : null, Options = options.Count > 0 ? string.Join(" ", options) : null,
            });
        }
        return catalog;
    }

    /// <summary>information_schema gives defaults unquoted ("active"); quote text defaults so they read like DDL.</summary>
    private static string? Default(string? value, string type)
    {
        if (value == null) return null;
        var t = type.ToLowerInvariant();
        if ((t.Contains("char") || t.Contains("text") || t.StartsWith("enum") || t.StartsWith("set")) && !value.StartsWith('\'')) return "'" + value.Replace("'", "''") + "'";
        return value;
    }
}
