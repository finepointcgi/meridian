using Meridian.Data.History;
using Meridian.Serialization.History;
using Npgsql;
using NpgsqlTypes;

namespace Meridian.Data;

/// <summary>Element history: the per-element change index (PostgreSQL). See IMeridianStore and docs-site version control.</summary>
public sealed partial class PostgresStore
{
    // Runs inside MigrateAsync's transaction under the schema advisory lock, so concurrent server starts are serialised.
    // author / created_at copy project_versions' columns (the SQLite store needs them off its big version rows; kept alike here).
    private static async Task MigrateElementChangesAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand("""
            create table if not exists element_change_versions (
                version_id bigint primary key references project_versions(id) on delete cascade,
                project_id uuid not null references projects(id) on delete cascade,
                parent_version_id bigint,
                kind smallint not null default 0,
                merge_source text,
                kind_recorded boolean not null default false,
                indexed boolean not null default false,
                change_count int not null default 0,
                indexed_at timestamptz,
                author uuid,
                created_at timestamptz);

            create table if not exists element_changes (
                version_id bigint not null references project_versions(id) on delete cascade,
                seq int not null,
                project_id uuid not null,
                element_id text not null,
                change smallint not null,
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
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<HistoryVersion>> ListHistoryVersionsAsync(Guid projectId)
    {
        await using var cmd = _ds.CreateCommand("""
            select v.id, v.number, v.message, v.author, coalesce(u.display_name, 'unknown'), v.created_at, vb.branch_id, coalesce(vb.branch_name, 'main'),
                   coalesce(m.indexed, false), m.parent_version_id, coalesce(m.kind, 0), m.merge_source, coalesce(m.kind_recorded, false)
            from project_versions v left join users u on u.id = v.author left join version_branches vb on vb.version_id = v.id
            left join element_change_versions m on m.version_id = v.id
            where v.project_id = $1 order by v.number
            """);
        cmd.Parameters.Add(P(projectId, NpgsqlDbType.Uuid));
        var list = new List<HistoryVersion>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new HistoryVersion(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.GetString(4), r.GetDateTime(5),
                r.IsDBNull(6) ? null : r.GetGuid(6), r.GetString(7), r.GetBoolean(8), r.IsDBNull(9) ? null : r.GetInt64(9),
                (HistoryVersionKind)r.GetInt16(10), r.IsDBNull(11) ? null : r.GetString(11), r.GetBoolean(12)));
        return list;
    }

    public async Task SetVersionKindAsync(Guid projectId, long versionId, HistoryVersionKind kind, string? mergeSource)
    {
        await using var cmd = _ds.CreateCommand("""
            insert into element_change_versions(version_id, project_id, kind, merge_source, kind_recorded, author, created_at)
            select id, project_id, $3, $4, true, author, created_at from project_versions where id = $1 and project_id = $2
            on conflict (version_id) do update set kind = excluded.kind, merge_source = excluded.merge_source, kind_recorded = true
            """);
        cmd.Parameters.Add(P(versionId, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(P(projectId, NpgsqlDbType.Uuid));
        cmd.Parameters.Add(P((short)kind, NpgsqlDbType.Smallint));
        cmd.Parameters.Add(P(mergeSource, NpgsqlDbType.Text));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> SaveElementChangesAsync(Guid projectId, long versionId, long? parentVersionId, HistoryVersionKind kind, string? mergeSource,
                                                    IReadOnlyList<ElementChange> changes)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        // Two servers indexing the same version wait for each other here; the second one then finds it indexed.
        await using (var lk = new NpgsqlCommand("select pg_advisory_xact_lock(7262013, hashtext($1::text))", conn, tx))
        {
            lk.Parameters.Add(P(versionId.ToString(System.Globalization.CultureInfo.InvariantCulture), NpgsqlDbType.Text));
            await lk.ExecuteNonQueryAsync();
        }
        await using (var q = new NpgsqlCommand("""
            select exists(select 1 from project_versions where id = $1 and project_id = $2),
                   coalesce((select indexed from element_change_versions where version_id = $1), false)
            """, conn, tx))
        {
            q.Parameters.Add(P(versionId, NpgsqlDbType.Bigint));
            q.Parameters.Add(P(projectId, NpgsqlDbType.Uuid));
            await using var r = await q.ExecuteReaderAsync();
            await r.ReadAsync();
            if (!r.GetBoolean(0) || r.GetBoolean(1)) return false;
        }
        await using (var del = new NpgsqlCommand("delete from element_changes where version_id = $1", conn, tx))
        {
            del.Parameters.Add(P(versionId, NpgsqlDbType.Bigint));
            await del.ExecuteNonQueryAsync();
        }
        if (changes.Count > 0)
        {
            await using var copy = await conn.BeginBinaryImportAsync("""
                copy element_changes(version_id, seq, project_id, element_id, change, fields, element_kind, label, owner_id, source_id, target_id, diagram_id, diagram_name)
                from stdin (format binary)
                """);
            for (var i = 0; i < changes.Count; i++)
            {
                var x = changes[i];
                await copy.StartRowAsync();
                await copy.WriteAsync(versionId, NpgsqlDbType.Bigint);
                await copy.WriteAsync(i, NpgsqlDbType.Integer);
                await copy.WriteAsync(projectId, NpgsqlDbType.Uuid);
                await copy.WriteAsync(x.ElementId, NpgsqlDbType.Text);
                await copy.WriteAsync((short)x.Kind, NpgsqlDbType.Smallint);
                await copy.WriteAsync(ElementChangeCodes.JoinFields(x.Fields), NpgsqlDbType.Text);
                await copy.WriteAsync(x.ElementKind, NpgsqlDbType.Text);
                await copy.WriteAsync(ElementChangeCodes.Clip(x.Label), NpgsqlDbType.Text);
                await WriteOptAsync(copy, x.OwnerId);
                await WriteOptAsync(copy, x.SourceId);
                await WriteOptAsync(copy, x.TargetId);
                await WriteOptAsync(copy, x.DiagramId);
                await WriteOptAsync(copy, x.DiagramName is null ? null : ElementChangeCodes.Clip(x.DiagramName));
            }
            await copy.CompleteAsync();
        }
        await using (var meta = new NpgsqlCommand("""
            insert into element_change_versions(version_id, project_id, parent_version_id, kind, merge_source, kind_recorded, indexed, change_count, indexed_at,
                                                author, created_at)
            select $1, $2, $3, $4, $5, false, true, $6, now(), author, created_at from project_versions where id = $1
            on conflict (version_id) do update set parent_version_id = excluded.parent_version_id, indexed = true, change_count = excluded.change_count,
                indexed_at = excluded.indexed_at, author = excluded.author, created_at = excluded.created_at,
                kind = case when element_change_versions.kind_recorded then element_change_versions.kind else excluded.kind end,
                merge_source = case when element_change_versions.kind_recorded then element_change_versions.merge_source else excluded.merge_source end
            """, conn, tx))
        {
            meta.Parameters.Add(P(versionId, NpgsqlDbType.Bigint));
            meta.Parameters.Add(P(projectId, NpgsqlDbType.Uuid));
            meta.Parameters.Add(P(parentVersionId, NpgsqlDbType.Bigint));
            meta.Parameters.Add(P((short)kind, NpgsqlDbType.Smallint));
            meta.Parameters.Add(P(mergeSource, NpgsqlDbType.Text));
            meta.Parameters.Add(P(changes.Count, NpgsqlDbType.Integer));
            await meta.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return true;
    }

    private static Task WriteOptAsync(NpgsqlBinaryImporter copy, string? value) =>
        value is null ? copy.WriteNullAsync() : copy.WriteAsync(value, NpgsqlDbType.Text);

    public async Task<IReadOnlyList<ElementChangeRow>> ListElementChangesAsync(Guid projectId, string elementId, bool includeConnected)
    {
        const string Cols = "select version_id, element_id, change, fields, element_kind, label, owner_id, source_id, target_id, diagram_id, diagram_name, seq from element_changes ";
        var sql = Cols + "where project_id = $1 and element_id = $2";
        if (includeConnected)
            sql += " union all " + Cols + "where project_id = $1 and source_id = $2 and element_id <> $2"
                   + " union all " + Cols + "where project_id = $1 and target_id = $2 and element_id <> $2 and coalesce(source_id, '') <> $2";
        await using var cmd = _ds.CreateCommand(sql + " order by 1 desc, 12");
        cmd.Parameters.Add(P(projectId, NpgsqlDbType.Uuid));
        cmd.Parameters.Add(P(elementId, NpgsqlDbType.Text));
        var list = new List<ElementChangeRow>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new ElementChangeRow(r.GetInt64(0), r.GetString(1), (ElementChangeKind)r.GetInt16(2), ElementChangeCodes.SplitFields(r.GetString(3)),
                r.GetString(4), r.GetString(5), Opt(r, 6), Opt(r, 7), Opt(r, 8), Opt(r, 9), Opt(r, 10)));
        return list;
    }

    private static string? Opt(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public async Task ResetElementChangesAsync(Guid projectId)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        foreach (var sql in new[]
                 {
                     "delete from element_changes where project_id = $1",
                     "update element_change_versions set indexed = false, change_count = 0, parent_version_id = null, indexed_at = null where project_id = $1",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.Add(P(projectId, NpgsqlDbType.Uuid));
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }
}
