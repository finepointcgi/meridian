using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Meridian.Data;

/// <summary>The Data Protection key ring (SQLite).</summary>
public sealed partial class SqliteStore : IDataProtectionKeyStore
{
    // "create … if not exists" is atomic in SQLite, so processes migrating at the same moment can't race here. The table is
    // also created on first use, because the key ring can be read before MigrateAsync runs (the statement is idempotent).
    private const string DataProtectionKeysDdl = """
        create table if not exists data_protection_keys (
            friendly_name text primary key,
            xml text not null,
            created_at text not null);
        """;

    private volatile bool _keysTableReady;

    private Task MigrateDataProtectionKeysAsync() => ExecAsync(DataProtectionKeysDdl);

    private SqliteConnection OpenKeysConnection()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        if (!_keysTableReady)
        {
            using var ddl = c.CreateCommand();
            ddl.CommandText = DataProtectionKeysDdl;
            ddl.ExecuteNonQuery();
            _keysTableReady = true;
        }
        return c;
    }

    public IReadOnlyList<DataProtectionKeyRecord> ListDataProtectionKeys()
    {
        using var c = OpenKeysConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "select friendly_name, xml from data_protection_keys order by created_at, friendly_name";
        using var r = cmd.ExecuteReader();
        var list = new List<DataProtectionKeyRecord>();
        while (r.Read()) list.Add(new DataProtectionKeyRecord(r.GetString(0), r.GetString(1)));
        return list;
    }

    public void StoreDataProtectionKey(string name, string xml) => WriteKey(name, xml, """
        insert into data_protection_keys(friendly_name, xml, created_at) values ($1, $2, $3)
        on conflict(friendly_name) do update set xml = excluded.xml
        """);

    public bool AddDataProtectionKeyIfMissing(string name, string xml) => WriteKey(name, xml, """
        insert into data_protection_keys(friendly_name, xml, created_at) values ($1, $2, $3)
        on conflict(friendly_name) do nothing
        """) > 0;

    private int WriteKey(string name, string xml, string sql)
    {
        using var c = OpenKeysConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$1", name);
        cmd.Parameters.AddWithValue("$2", xml);
        cmd.Parameters.AddWithValue("$3", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return cmd.ExecuteNonQuery();
    }
}
