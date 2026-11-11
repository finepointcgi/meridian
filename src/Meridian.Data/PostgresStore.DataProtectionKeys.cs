using Npgsql;

namespace Meridian.Data;

/// <summary>The Data Protection key ring (PostgreSQL), shared by every server instance on the database.</summary>
public sealed partial class PostgresStore : IDataProtectionKeyStore
{
    // Runs inside MigrateAsync's transaction under the schema advisory lock, so concurrent server starts are serialised. The
    // table is also created on first use (under the same lock), because the key ring can be read before MigrateAsync runs.
    private const string DataProtectionKeysDdl = """
        create table if not exists data_protection_keys (
            friendly_name text primary key,
            xml text not null,
            created_at timestamptz not null default now());
        """;

    private volatile bool _keysTableReady;

    private static async Task MigrateDataProtectionKeysAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand(DataProtectionKeysDdl, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private NpgsqlConnection OpenKeysConnection()
    {
        var conn = _ds.OpenConnection();
        if (_keysTableReady) return conn;
        try
        {
            using (var tx = conn.BeginTransaction())
            {
                using (var lk = new NpgsqlCommand("select pg_advisory_xact_lock(7262011)", conn, tx)) lk.ExecuteNonQuery();   // the schema lock
                using (var ddl = new NpgsqlCommand(DataProtectionKeysDdl, conn, tx)) ddl.ExecuteNonQuery();
                tx.Commit();
            }
            _keysTableReady = true;
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    public IReadOnlyList<DataProtectionKeyRecord> ListDataProtectionKeys()
    {
        using var conn = OpenKeysConnection();
        using var cmd = new NpgsqlCommand("select friendly_name, xml from data_protection_keys order by created_at, friendly_name", conn);
        using var r = cmd.ExecuteReader();
        var list = new List<DataProtectionKeyRecord>();
        while (r.Read()) list.Add(new DataProtectionKeyRecord(r.GetString(0), r.GetString(1)));
        return list;
    }

    public void StoreDataProtectionKey(string name, string xml) => WriteKey(name, xml, """
        insert into data_protection_keys(friendly_name, xml) values ($1, $2)
        on conflict (friendly_name) do update set xml = excluded.xml
        """);

    public bool AddDataProtectionKeyIfMissing(string name, string xml) => WriteKey(name, xml, """
        insert into data_protection_keys(friendly_name, xml) values ($1, $2)
        on conflict (friendly_name) do nothing
        """) > 0;

    private int WriteKey(string name, string xml, string sql)
    {
        using var conn = OpenKeysConnection();
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new() { Value = name });
        cmd.Parameters.Add(new() { Value = xml });
        return cmd.ExecuteNonQuery();
    }
}
