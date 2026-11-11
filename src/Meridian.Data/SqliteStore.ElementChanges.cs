using Meridian.Data.History;
using Meridian.Serialization.History;
using Microsoft.Data.Sqlite;

namespace Meridian.Data;

/// <summary>Element history: the per-element change index (SQLite). See IMeridianStore and docs-site version control.</summary>
public sealed partial class SqliteStore
{
    // element_change_versions: one row per version the index knows about (kind recorded at commit time, and/or indexed).
    // element_changes: one row per changed element of an indexed version; rows of a version are written in one transaction.
    // author and created_at are copied from project_versions: there they sit after the multi-megabyte model column, and SQLite
    // reads a row's overflow pages to reach them, which made listing 100 versions of a 200k-element project take ~250 ms.
    private Task MigrateElementChangesAsync() => ExecAsync("""
        create table if not exists element_change_versions (
            version_id integer primary key references project_versions(id) on delete cascade,
            project_id text not null references projects(id) on delete cascade,
            parent_version_id integer,
            kind integer not null default 0,
            merge_source text,
            kind_recorded integer not null default 0,
            indexed integer not null default 0,
            change_count integer not null default 0,
            indexed_at text,
            author text,
            created_at text);

        create table if not exists element_changes (
            version_id integer not null references project_versions(id) on delete cascade,
            seq integer not null,
            project_id text not null,
            element_id text not null,
            change integer not null,
            fields text not null default '',
            element_kind text not null default '',
            label text not null default '',
            owner_id text,
            source_id text,
            target_id text,
            diagram_id text,
            diagram_name text,
            primary key (version_id, seq));

        create index if not exists element_change_versions_project on element_change_versions(project_id);
        create index if not exists element_changes_element on element_changes(project_id, element_id);
        create index if not exists element_changes_source on element_changes(project_id, source_id) where source_id is not null;
        create index if not exists element_changes_target on element_changes(project_id, target_id) where target_id is not null;
        """);

    public async Task<IReadOnlyList<HistoryVersion>> ListHistoryVersionsAsync(Guid projectId)
    {
        await using var cmd = await CmdAsync("""
            select v.id, v.number, v.message, case when m.created_at is not null then m.author else v.author end, coalesce(u.display_name, 'unknown'),
                   coalesce(m.created_at, v.created_at), vb.branch_id, coalesce(vb.branch_name, 'main'),
                   coalesce(m.indexed, 0), m.parent_version_id, coalesce(m.kind, 0), m.merge_source, coalesce(m.kind_recorded, 0)
            from project_versions v left join element_change_versions m on m.version_id = v.id
            left join users u on u.id = (case when m.created_at is not null then m.author else v.author end)
            left join version_branches vb on vb.version_id = v.id
            where v.project_id = $1 order by v.number
            """, S(projectId));
        var list = new List<HistoryVersion>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new HistoryVersion(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.IsDBNull(3) ? null : Id(r, 3), r.GetString(4), Time(r, 5),
                r.IsDBNull(6) ? null : Id(r, 6), r.GetString(7), r.GetInt64(8) != 0, r.IsDBNull(9) ? null : r.GetInt64(9),
                (HistoryVersionKind)r.GetInt32(10), r.IsDBNull(11) ? null : r.GetString(11), r.GetInt64(12) != 0));
        return list;
    }

    public Task SetVersionKindAsync(Guid projectId, long versionId, HistoryVersionKind kind, string? mergeSource) => ExecAsync("""
        insert into element_change_versions(version_id, project_id, kind, merge_source, kind_recorded, author, created_at)
        select id, project_id, $3, $4, 1, author, created_at from project_versions where id = $1 and project_id = $2
        on conflict (version_id) do update set kind = excluded.kind, merge_source = excluded.merge_source, kind_recorded = 1
        """, versionId, S(projectId), (int)kind, mergeSource);

    public async Task<bool> SaveElementChangesAsync(Guid projectId, long versionId, long? parentVersionId, HistoryVersionKind kind, string? mergeSource,
                                                    IReadOnlyList<ElementChange> changes)
    {
        await using var c = await OpenAsync();
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync();   // BEGIN IMMEDIATE: one writer at a time
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = """
                select (select count(*) from project_versions where id = $1 and project_id = $2),
                       coalesce((select indexed from element_change_versions where version_id = $1), 0)
                """;
            q.Parameters.AddWithValue("$1", versionId);
            q.Parameters.AddWithValue("$2", S(projectId));
            await using var r = await q.ExecuteReaderAsync();
            await r.ReadAsync();
            if (r.GetInt64(0) == 0 || r.GetInt64(1) != 0) return false;   // gone, or indexed by someone else
        }
        await using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "delete from element_changes where version_id = $1";
            del.Parameters.AddWithValue("$1", versionId);
            await del.ExecuteNonQueryAsync();
        }
        if (changes.Count > 0)
        {
            await using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                insert into element_changes(version_id, seq, project_id, element_id, change, fields, element_kind, label, owner_id, source_id, target_id, diagram_id, diagram_name)
                values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)
                """;
            var p = new SqliteParameter[13];
            for (var i = 0; i < p.Length; i++) p[i] = ins.Parameters.Add("$" + (i + 1), SqliteType.Text);
            p[0].SqliteType = p[1].SqliteType = p[4].SqliteType = SqliteType.Integer;
            ins.Prepare();
            p[0].Value = versionId;
            p[2].Value = S(projectId);
            for (var i = 0; i < changes.Count; i++)
            {
                var x = changes[i];
                p[1].Value = i;
                p[3].Value = x.ElementId;
                p[4].Value = (int)x.Kind;
                p[5].Value = ElementChangeCodes.JoinFields(x.Fields);
                p[6].Value = x.ElementKind;
                p[7].Value = ElementChangeCodes.Clip(x.Label);
                p[8].Value = (object?)x.OwnerId ?? DBNull.Value;
                p[9].Value = (object?)x.SourceId ?? DBNull.Value;
                p[10].Value = (object?)x.TargetId ?? DBNull.Value;
                p[11].Value = (object?)x.DiagramId ?? DBNull.Value;
                p[12].Value = x.DiagramName is null ? DBNull.Value : ElementChangeCodes.Clip(x.DiagramName);
                await ins.ExecuteNonQueryAsync();
            }
        }
        await using (var meta = c.CreateCommand())
        {
            meta.Transaction = tx;
            meta.CommandText = """
                insert into element_change_versions(version_id, project_id, parent_version_id, kind, merge_source, kind_recorded, indexed, change_count, indexed_at,
                                                    author, created_at)
                select $1, $2, $3, $4, $5, 0, 1, $6, $7, author, created_at from project_versions where id = $1
                on conflict (version_id) do update set parent_version_id = excluded.parent_version_id, indexed = 1, change_count = excluded.change_count,
                    indexed_at = excluded.indexed_at, author = excluded.author, created_at = excluded.created_at,
                    kind = case when element_change_versions.kind_recorded = 1 then element_change_versions.kind else excluded.kind end,
                    merge_source = case when element_change_versions.kind_recorded = 1 then element_change_versions.merge_source else excluded.merge_source end
                """;
            meta.Parameters.AddWithValue("$1", versionId);
            meta.Parameters.AddWithValue("$2", S(projectId));
            meta.Parameters.AddWithValue("$3", (object?)parentVersionId ?? DBNull.Value);
            meta.Parameters.AddWithValue("$4", (int)kind);
            meta.Parameters.AddWithValue("$5", (object?)mergeSource ?? DBNull.Value);
            meta.Parameters.AddWithValue("$6", changes.Count);
            meta.Parameters.AddWithValue("$7", Now());
            await meta.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return true;
    }

    public async Task<IReadOnlyList<ElementChangeRow>> ListElementChangesAsync(Guid projectId, string elementId, bool includeConnected)
    {
        // Separate index lookups joined with "union all" (an "or" over three columns would scan the project's rows).
        var sql = "select version_id, element_id, change, fields, element_kind, label, owner_id, source_id, target_id, diagram_id, diagram_name, seq "
                  + "from element_changes where project_id = $1 and element_id = $2";
        if (includeConnected)
            sql += " union all select version_id, element_id, change, fields, element_kind, label, owner_id, source_id, target_id, diagram_id, diagram_name, seq "
                   + "from element_changes where project_id = $1 and source_id = $2 and element_id <> $2"
                   + " union all select version_id, element_id, change, fields, element_kind, label, owner_id, source_id, target_id, diagram_id, diagram_name, seq "
                   + "from element_changes where project_id = $1 and target_id = $2 and element_id <> $2 and coalesce(source_id, '') <> $2";
        await using var cmd = await CmdAsync(sql + " order by 1 desc, 12", S(projectId), elementId);
        var list = new List<ElementChangeRow>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new ElementChangeRow(r.GetInt64(0), r.GetString(1), (ElementChangeKind)r.GetInt32(2), ElementChangeCodes.SplitFields(r.GetString(3)),
                r.GetString(4), r.GetString(5), Opt(r, 6), Opt(r, 7), Opt(r, 8), Opt(r, 9), Opt(r, 10)));
        return list;
    }

    private static string? Opt(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public async Task ResetElementChangesAsync(Guid projectId)
    {
        await ExecAsync("delete from element_changes where project_id = $1", S(projectId));
        await ExecAsync("update element_change_versions set indexed = 0, change_count = 0, parent_version_id = null, indexed_at = null where project_id = $1", S(projectId));
    }
}
