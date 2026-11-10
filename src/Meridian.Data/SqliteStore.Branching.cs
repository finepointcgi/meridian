using Microsoft.Data.Sqlite;

namespace Meridian.Data;

/// <summary>Branches, branch commits and baselines (SQLite).</summary>
public sealed partial class SqliteStore
{
    private Task MigrateBranchingAsync() => ExecAsync("""
        create table if not exists project_branches (
            id text primary key,
            project_id text not null references projects(id) on delete cascade,
            name text not null,
            model text not null,
            base_model text not null,
            base_version_id integer,
            revision integer not null default 0,
            created_by text references users(id) on delete set null,
            updated_by text references users(id) on delete set null,
            created_at text not null,
            updated_at text not null,
            unique (project_id, name));

        create table if not exists version_branches (
            version_id integer primary key references project_versions(id) on delete cascade,
            branch_id text not null,
            branch_name text not null);

        create table if not exists baselines (
            id integer primary key autoincrement,
            project_id text not null references projects(id) on delete cascade,
            version_id integer not null references project_versions(id) on delete cascade,
            name text not null,
            description text not null default '',
            created_by text references users(id) on delete set null,
            created_at text not null,
            unique (project_id, name));

        create index if not exists branches_project on project_branches(project_id);
        create index if not exists version_branches_branch on version_branches(branch_id);
        """);

    public async Task<Guid> CreateBranchAsync(Guid projectId, string name, string headModelJson, string baseModelJson, long? baseVersionId, Guid? creator)
    {
        name = Branching.NormalizeBranchName(name);
        var id = Guid.NewGuid();
        var now = Now();
        try
        {
            await ExecAsync("""
                insert into project_branches(id, project_id, name, model, base_model, base_version_id, created_by, updated_by, created_at, updated_at)
                values ($1,$2,$3,$4,$5,$6,$7,$7,$8,$8)
                """, S(id), S(projectId), name, headModelJson, baseModelJson, baseVersionId, creator is { } c ? S(c) : null, now);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"A branch named “{name}” already exists.");
        }
        return id;
    }

    private const string BranchSelect = """
        select b.id, b.project_id, b.name, b.base_version_id, v.number, coalesce(u.display_name, 'unknown'), b.created_at, b.updated_at, b.revision
        from project_branches b left join users u on u.id = b.created_by left join project_versions v on v.id = b.base_version_id
        """;

    private static BranchInfo ReadBranch(SqliteDataReader r) => new(Id(r, 0), Id(r, 1), r.GetString(2),
        r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetString(5), Time(r, 6), Time(r, 7), r.GetInt64(8));

    public async Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(Guid projectId)
    {
        await using var cmd = await CmdAsync(BranchSelect + " where b.project_id = $1 order by b.name", S(projectId));
        var list = new List<BranchInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(ReadBranch(r));
        return list;
    }

    public async Task<BranchInfo?> GetBranchAsync(Guid branchId)
    {
        await using var cmd = await CmdAsync(BranchSelect + " where b.id = $1", S(branchId));
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadBranch(r) : null;
    }

    public async Task<BranchModels?> LoadBranchAsync(Guid branchId)
    {
        await using var cmd = await CmdAsync("select project_id, name, model, base_model, base_version_id, revision from project_branches where id = $1", S(branchId));
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync()
            ? new BranchModels(Id(r, 0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4), r.GetInt64(5))
            : null;
    }

    public async Task<long> SaveBranchAsync(Guid branchId, string modelJson, Guid? userId) =>
        (long)(await ScalarAsync("update project_branches set model = $2, revision = revision + 1, updated_at = $4, updated_by = $3 where id = $1 returning revision",
            S(branchId), modelJson, userId is { } u ? S(u) : null, Now()) ?? 0L);

    public Task SetBranchBaseAsync(Guid branchId, string baseModelJson, long? baseVersionId) =>
        ExecAsync("update project_branches set base_model = $2, base_version_id = $3 where id = $1", S(branchId), baseModelJson, baseVersionId);

    public Task DeleteBranchAsync(Guid branchId) => ExecAsync("delete from project_branches where id = $1", S(branchId));

    public async Task<(long Id, int Number)> CommitBranchVersionAsync(Guid projectId, Guid? branchId, string message, string modelJson, Guid? author)
    {
        await using var c = await OpenAsync();
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync();
        string? branchName = null;
        if (branchId is { } bid)
        {
            await using var q = c.CreateCommand();
            q.Transaction = tx;
            q.CommandText = "select name from project_branches where id = $1 and project_id = $2";
            q.Parameters.AddWithValue("$1", S(bid));
            q.Parameters.AddWithValue("$2", S(projectId));
            branchName = await q.ExecuteScalarAsync() as string ?? throw new InvalidOperationException("That branch no longer exists.");
        }
        long id;
        int number;
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                insert into project_versions(project_id, number, message, model, author, created_at)
                values ($1, coalesce((select max(number) from project_versions where project_id = $1), 0) + 1, $2, $3, $4, $5)
                returning id, number
                """;
            cmd.Parameters.AddWithValue("$1", S(projectId));
            cmd.Parameters.AddWithValue("$2", message);
            cmd.Parameters.AddWithValue("$3", modelJson);
            cmd.Parameters.AddWithValue("$4", author is { } a ? S(a) : DBNull.Value);
            cmd.Parameters.AddWithValue("$5", Now());
            await using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            (id, number) = (r.GetInt64(0), r.GetInt32(1));
        }
        if (branchId is { } b2)
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "insert into version_branches(version_id, branch_id, branch_name) values ($1,$2,$3)";
            cmd.Parameters.AddWithValue("$1", id);
            cmd.Parameters.AddWithValue("$2", S(b2));
            cmd.Parameters.AddWithValue("$3", branchName!);
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return (id, number);
    }

    public async Task<IReadOnlyList<VersionInfo>> ListBranchVersionsAsync(Guid projectId, Guid? branchId)
    {
        await using var cmd = await CmdAsync($"""
            select v.id, v.number, v.message, coalesce(u.display_name, 'unknown'), v.created_at
            from project_versions v left join users u on u.id = v.author left join version_branches vb on vb.version_id = v.id
            where v.project_id = $1 and {(branchId is null ? "vb.branch_id is null" : "vb.branch_id = $2")} order by v.number desc
            """, branchId is { } b ? [S(projectId), S(b)] : [S(projectId)]);
        var list = new List<VersionInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new VersionInfo(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetString(3), Time(r, 4)));
        return list;
    }

    public async Task<long> CreateBaselineAsync(Guid projectId, long versionId, string name, string description, Guid? creator)
    {
        name = Branching.NormalizeBaselineName(name);
        if (await ScalarAsync("select 1 from project_versions where id = $1 and project_id = $2", versionId, S(projectId)) is null)
            throw new InvalidOperationException("That version does not belong to this project.");
        try
        {
            return (long)(await ScalarAsync("insert into baselines(project_id, version_id, name, description, created_by, created_at) values ($1,$2,$3,$4,$5,$6) returning id",
                S(projectId), versionId, name, (description ?? "").Trim(), creator is { } c ? S(c) : null, Now()) ?? 0L);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"A baseline named “{name}” already exists.");
        }
    }

    public async Task<IReadOnlyList<BaselineInfo>> ListBaselinesAsync(Guid projectId)
    {
        await using var cmd = await CmdAsync("""
            select bl.id, bl.name, bl.description, v.id, v.number, v.message, coalesce(vb.branch_name, 'main'), coalesce(u.display_name, 'unknown'), bl.created_at
            from baselines bl join project_versions v on v.id = bl.version_id
            left join version_branches vb on vb.version_id = v.id left join users u on u.id = bl.created_by
            where bl.project_id = $1 order by v.number desc, bl.id desc
            """, S(projectId));
        var list = new List<BaselineInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new BaselineInfo(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt32(4), r.GetString(5), r.GetString(6), r.GetString(7), Time(r, 8)));
        return list;
    }

    public Task DeleteBaselineAsync(long baselineId) => ExecAsync("delete from baselines where id = $1", baselineId);
}
