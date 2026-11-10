using Npgsql;
using NpgsqlTypes;

namespace Meridian.Data;

/// <summary>Branches, branch commits and baselines (PostgreSQL).</summary>
public sealed partial class PostgresStore
{
    private async Task MigrateBranchingAsync()
    {
        await using var cmd = _ds.CreateCommand("""
            create table if not exists project_branches (
                id uuid primary key,
                project_id uuid not null references projects(id) on delete cascade,
                name text not null,
                model jsonb not null,
                base_model jsonb not null,
                base_version_id bigint,
                revision bigint not null default 0,
                created_by uuid references users(id) on delete set null,
                updated_by uuid references users(id) on delete set null,
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now(),
                unique (project_id, name));

            create table if not exists version_branches (
                version_id bigint primary key references project_versions(id) on delete cascade,
                branch_id uuid not null,
                branch_name text not null);

            create table if not exists baselines (
                id bigserial primary key,
                project_id uuid not null references projects(id) on delete cascade,
                version_id bigint not null references project_versions(id) on delete cascade,
                name text not null,
                description text not null default '',
                created_by uuid references users(id) on delete set null,
                created_at timestamptz not null default now(),
                unique (project_id, name));

            create index if not exists branches_project on project_branches(project_id);
            create index if not exists version_branches_branch on version_branches(branch_id);
            """);
        await cmd.ExecuteNonQueryAsync();
    }


    public async Task<Guid> CreateBranchAsync(Guid projectId, string name, string headModelJson, string baseModelJson, long? baseVersionId, Guid? creator)
    {
        name = Branching.NormalizeBranchName(name);
        var id = Guid.NewGuid();
        await using var cmd = _ds.CreateCommand("""
            insert into project_branches(id, project_id, name, model, base_model, base_version_id, created_by, updated_by)
            values ($1,$2,$3,$4,$5,$6,$7,$7)
            """);
        cmd.Parameters.Add(P(id, NpgsqlDbType.Uuid));
        cmd.Parameters.Add(P(projectId, NpgsqlDbType.Uuid));
        cmd.Parameters.Add(P(name, NpgsqlDbType.Text));
        cmd.Parameters.Add(P(headModelJson, NpgsqlDbType.Jsonb));
        cmd.Parameters.Add(P(baseModelJson, NpgsqlDbType.Jsonb));
        cmd.Parameters.Add(P(baseVersionId, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(P(creator, NpgsqlDbType.Uuid));
        try { await cmd.ExecuteNonQueryAsync(); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException($"A branch named “{name}” already exists.");
        }
        return id;
    }

    private const string BranchSelect = """
        select b.id, b.project_id, b.name, b.base_version_id, v.number, coalesce(u.display_name, 'unknown'), b.created_at, b.updated_at, b.revision
        from project_branches b left join users u on u.id = b.created_by left join project_versions v on v.id = b.base_version_id
        """;

    private static BranchInfo ReadBranch(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2),
        r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetString(5), r.GetDateTime(6), r.GetDateTime(7), r.GetInt64(8));

    public async Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(Guid projectId)
    {
        await using var cmd = _ds.CreateCommand(BranchSelect + " where b.project_id = $1 order by b.name");
        cmd.Parameters.Add(P(projectId, NpgsqlDbType.Uuid));
        var list = new List<BranchInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(ReadBranch(r));
        return list;
    }

    public async Task<BranchInfo?> GetBranchAsync(Guid branchId)
    {
        await using var cmd = _ds.CreateCommand(BranchSelect + " where b.id = $1");
        cmd.Parameters.Add(P(branchId, NpgsqlDbType.Uuid));
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadBranch(r) : null;
    }

    public async Task<BranchModels?> LoadBranchAsync(Guid branchId)
    {
        await using var cmd = _ds.CreateCommand("select project_id, name, model::text, base_model::text, base_version_id, revision from project_branches where id = $1");
        cmd.Parameters.Add(P(branchId, NpgsqlDbType.Uuid));
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync()
            ? new BranchModels(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4), r.GetInt64(5))
            : null;
    }

    public async Task<long> SaveBranchAsync(Guid branchId, string modelJson, Guid? userId)
    {
        await using var cmd = _ds.CreateCommand("""
            update project_branches set model = $2, revision = revision + 1, updated_at = now(), updated_by = $3
            where id = $1 returning revision
            """);
        cmd.Parameters.Add(P(branchId, NpgsqlDbType.Uuid));
        cmd.Parameters.Add(P(modelJson, NpgsqlDbType.Jsonb));
        cmd.Parameters.Add(P(userId, NpgsqlDbType.Uuid));
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    public async Task SetBranchBaseAsync(Guid branchId, string baseModelJson, long? baseVersionId)
    {
        await using var cmd = _ds.CreateCommand("update project_branches set base_model = $2, base_version_id = $3 where id = $1");
        cmd.Parameters.Add(P(branchId, NpgsqlDbType.Uuid));
        cmd.Parameters.Add(P(baseModelJson, NpgsqlDbType.Jsonb));
        cmd.Parameters.Add(P(baseVersionId, NpgsqlDbType.Bigint));
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DeleteBranchAsync(Guid branchId) => ExecAsync("delete from project_branches where id = $1", branchId);

    public async Task<(long Id, int Number)> CommitBranchVersionAsync(Guid projectId, Guid? branchId, string message, string modelJson, Guid? author)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        string? branchName = null;
        if (branchId is { } bid)
        {
            await using var q = new NpgsqlCommand("select name from project_branches where id = $1 and project_id = $2", conn, tx);
            q.Parameters.Add(P(bid, NpgsqlDbType.Uuid));
            q.Parameters.Add(P(projectId, NpgsqlDbType.Uuid));
            branchName = await q.ExecuteScalarAsync() as string ?? throw new InvalidOperationException("That branch no longer exists.");
        }
        // Serialise numbering per project (the unique (project_id, number) constraint backs this up).
        await using (var lk = new NpgsqlCommand("select pg_advisory_xact_lock(hashtext($1::text))", conn, tx))
        {
            lk.Parameters.Add(P(projectId.ToString(), NpgsqlDbType.Text));
            await lk.ExecuteNonQueryAsync();
        }
        long id;
        int number;
        await using (var cmd = new NpgsqlCommand("""
            insert into project_versions(project_id, number, message, model, author)
            values ($1, coalesce((select max(number) from project_versions where project_id = $1), 0) + 1, $2, $3, $4)
            returning id, number
            """, conn, tx))
        {
            cmd.Parameters.Add(P(projectId, NpgsqlDbType.Uuid));
            cmd.Parameters.Add(P(message, NpgsqlDbType.Text));
            cmd.Parameters.Add(P(modelJson, NpgsqlDbType.Jsonb));
            cmd.Parameters.Add(P(author, NpgsqlDbType.Uuid));
            await using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            (id, number) = (r.GetInt64(0), r.GetInt32(1));
        }
        if (branchId is { } b2)
        {
            await using var cmd = new NpgsqlCommand("insert into version_branches(version_id, branch_id, branch_name) values ($1,$2,$3)", conn, tx);
            cmd.Parameters.Add(P(id, NpgsqlDbType.Bigint));
            cmd.Parameters.Add(P(b2, NpgsqlDbType.Uuid));
            cmd.Parameters.Add(P(branchName, NpgsqlDbType.Text));
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return (id, number);
    }

    public async Task<IReadOnlyList<VersionInfo>> ListBranchVersionsAsync(Guid projectId, Guid? branchId)
    {
        await using var cmd = _ds.CreateCommand($"""
            select v.id, v.number, v.message, coalesce(u.display_name, 'unknown'), v.created_at
            from project_versions v left join users u on u.id = v.author left join version_branches vb on vb.version_id = v.id
            where v.project_id = $1 and {(branchId is null ? "vb.branch_id is null" : "vb.branch_id = $2")} order by v.number desc
            """);
        cmd.Parameters.Add(P(projectId, NpgsqlDbType.Uuid));
        if (branchId is { } b) cmd.Parameters.Add(P(b, NpgsqlDbType.Uuid));
        var list = new List<VersionInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new VersionInfo(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetDateTime(4)));
        return list;
    }

    public async Task<long> CreateBaselineAsync(Guid projectId, long versionId, string name, string description, Guid? creator)
    {
        name = Branching.NormalizeBaselineName(name);
        await using (var check = _ds.CreateCommand("select 1 from project_versions where id = $1 and project_id = $2"))
        {
            check.Parameters.Add(P(versionId, NpgsqlDbType.Bigint));
            check.Parameters.Add(P(projectId, NpgsqlDbType.Uuid));
            if (await check.ExecuteScalarAsync() is null) throw new InvalidOperationException("That version does not belong to this project.");
        }
        await using var cmd = _ds.CreateCommand("insert into baselines(project_id, version_id, name, description, created_by) values ($1,$2,$3,$4,$5) returning id");
        cmd.Parameters.Add(P(projectId, NpgsqlDbType.Uuid));
        cmd.Parameters.Add(P(versionId, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(P(name, NpgsqlDbType.Text));
        cmd.Parameters.Add(P((description ?? "").Trim(), NpgsqlDbType.Text));
        cmd.Parameters.Add(P(creator, NpgsqlDbType.Uuid));
        try { return (long)(await cmd.ExecuteScalarAsync() ?? 0L); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException($"A baseline named “{name}” already exists.");
        }
    }

    public async Task<IReadOnlyList<BaselineInfo>> ListBaselinesAsync(Guid projectId)
    {
        await using var cmd = _ds.CreateCommand("""
            select bl.id, bl.name, bl.description, v.id, v.number, v.message, coalesce(vb.branch_name, 'main'), coalesce(u.display_name, 'unknown'), bl.created_at
            from baselines bl join project_versions v on v.id = bl.version_id
            left join version_branches vb on vb.version_id = v.id left join users u on u.id = bl.created_by
            where bl.project_id = $1 order by v.number desc, bl.id desc
            """);
        cmd.Parameters.Add(P(projectId, NpgsqlDbType.Uuid));
        var list = new List<BaselineInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new BaselineInfo(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt32(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetDateTime(8)));
        return list;
    }

    public Task DeleteBaselineAsync(long baselineId) => ExecAsync("delete from baselines where id = $1", baselineId);
}
