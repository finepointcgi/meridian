using Microsoft.Data.Sqlite;

namespace Meridian.Data;

/// <summary>Code-sync run records (SQLite).</summary>
public sealed partial class SqliteStore
{
    // =============================================================== schema (code-sync runs migration block)

    private Task MigrateCodeSyncRunsAsync() => ExecAsync("""
        create table if not exists code_sync_runs (
            id text primary key,
            project_id text not null references projects(id) on delete cascade,
            connection_id text not null,
            branch_id text,
            repo text not null,
            commit_sha text not null,
            commit_url text,
            author text,
            message text,
            status text not null,
            summary text not null default '',
            added integer not null default 0,
            changed integer not null default 0,
            removed integer not null default 0,
            changes text not null default '[]',
            created_at text not null,
            updated_at text not null);

        create index if not exists code_sync_runs_project on code_sync_runs(project_id, created_at);
        """);

    private const string RunSelect = """
        select id, project_id, connection_id, branch_id, repo, commit_sha, commit_url, author, message, status, summary,
               added, changed, removed, changes, created_at, updated_at
        from code_sync_runs
        """;

    private static CodeSyncRunRecord ReadRun(SqliteDataReader r) => new(
        Id(r, 0), Id(r, 1), Id(r, 2), r.IsDBNull(3) ? null : Id(r, 3), r.GetString(4), r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
        CodeSyncRuns.ParseStatus(r.GetString(9)), r.GetString(10), r.GetInt32(11), r.GetInt32(12), r.GetInt32(13), r.GetString(14),
        Time(r, 15), Time(r, 16));

    public Task AddCodeSyncRunAsync(CodeSyncRunRecord run) => ExecAsync("""
        insert into code_sync_runs(id, project_id, connection_id, branch_id, repo, commit_sha, commit_url, author, message, status, summary,
                                   added, changed, removed, changes, created_at, updated_at)
        values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$16)
        """, S(run.Id), S(run.ProjectId), S(run.ConnectionId), run.BranchId is { } b ? S(b) : null, run.Repo, run.Commit, run.CommitUrl,
        run.Author, run.Message, run.Status.ToString(), run.Summary, run.Added, run.Changed, run.Removed,
        CodeSyncRuns.CheckChangesJson(run.ChangesJson), Now());

    public async Task<CodeSyncRunRecord?> GetCodeSyncRunAsync(Guid runId)
    {
        await using var cmd = await CmdAsync(RunSelect + " where id = $1", S(runId));
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadRun(r) : null;
    }

    public async Task<IReadOnlyList<CodeSyncRunRecord>> ListCodeSyncRunsAsync(Guid projectId, int limit = 50)
    {
        // rowid breaks ties between runs recorded within the same clock tick.
        await using var cmd = await CmdAsync(RunSelect + " where project_id = $1 order by created_at desc, rowid desc limit $2", S(projectId), limit);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<CodeSyncRunRecord>();
        while (await r.ReadAsync()) list.Add(ReadRun(r));
        return list;
    }

    public Task UpdateCodeSyncRunAsync(Guid runId, CodeSyncRunStatus status, string summary, string changesJson) => ExecAsync(
        "update code_sync_runs set status = $2, summary = $3, changes = $4, updated_at = $5 where id = $1",
        S(runId), status.ToString(), summary, CodeSyncRuns.CheckChangesJson(changesJson), Now());
}
