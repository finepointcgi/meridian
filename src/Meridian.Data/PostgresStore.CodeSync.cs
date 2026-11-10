using Npgsql;

namespace Meridian.Data;

/// <summary>Code-sync run records (PostgreSQL).</summary>
public sealed partial class PostgresStore
{
    // =============================================================== schema (code-sync runs migration block)

    private static async Task MigrateCodeSyncRunsAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand("""
            create table if not exists code_sync_runs (
                id uuid primary key,
                project_id uuid not null references projects(id) on delete cascade,
                connection_id uuid not null,
                branch_id uuid,
                repo text not null,
                commit_sha text not null,
                commit_url text,
                author text,
                message text,
                status text not null,
                summary text not null default '',
                added int not null default 0,
                changed int not null default 0,
                removed int not null default 0,
                changes jsonb not null default '[]'::jsonb,
                created_at timestamptz not null default clock_timestamp(),
                updated_at timestamptz not null default clock_timestamp());

            create index if not exists code_sync_runs_project on code_sync_runs(project_id, created_at);
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private const string RunSelect = """
        select id, project_id, connection_id, branch_id, repo, commit_sha, commit_url, author, message, status, summary,
               added, changed, removed, changes::text, created_at, updated_at
        from code_sync_runs
        """;

    private static CodeSyncRunRecord ReadRun(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.GetString(4), r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
        CodeSyncRuns.ParseStatus(r.GetString(9)), r.GetString(10), r.GetInt32(11), r.GetInt32(12), r.GetInt32(13), r.GetString(14),
        DateTime.SpecifyKind(r.GetDateTime(15), DateTimeKind.Utc), DateTime.SpecifyKind(r.GetDateTime(16), DateTimeKind.Utc));

    public async Task AddCodeSyncRunAsync(CodeSyncRunRecord run)
    {
        // clock_timestamp(), not now(): runs recorded in one transaction still order by when they happened.
        await using var cmd = IxCmd("""
            insert into code_sync_runs(id, project_id, connection_id, branch_id, repo, commit_sha, commit_url, author, message, status, summary,
                                       added, changed, removed, changes)
            values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15::jsonb)
            """, run.Id, run.ProjectId, run.ConnectionId, run.BranchId, run.Repo, run.Commit, run.CommitUrl, run.Author, run.Message,
            run.Status.ToString(), run.Summary, run.Added, run.Changed, run.Removed, CodeSyncRuns.CheckChangesJson(run.ChangesJson));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<CodeSyncRunRecord?> GetCodeSyncRunAsync(Guid runId)
    {
        await using var cmd = IxCmd(RunSelect + " where id = $1", runId);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadRun(r) : null;
    }

    public async Task<IReadOnlyList<CodeSyncRunRecord>> ListCodeSyncRunsAsync(Guid projectId, int limit = 50)
    {
        await using var cmd = IxCmd(RunSelect + " where project_id = $1 order by created_at desc, id desc limit $2", projectId, limit);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<CodeSyncRunRecord>();
        while (await r.ReadAsync()) list.Add(ReadRun(r));
        return list;
    }

    public async Task UpdateCodeSyncRunAsync(Guid runId, CodeSyncRunStatus status, string summary, string changesJson)
    {
        await using var cmd = IxCmd("update code_sync_runs set status = $2, summary = $3, changes = $4::jsonb, updated_at = clock_timestamp() where id = $1",
            runId, status.ToString(), summary, CodeSyncRuns.CheckChangesJson(changesJson));
        await cmd.ExecuteNonQueryAsync();
    }
}
