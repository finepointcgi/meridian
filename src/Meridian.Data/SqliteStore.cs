using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Meridian.Data;

/// <summary>
/// Single-file SQLite store used by the desktop app's local mode: same schema and behaviour as
/// <see cref="PostgresStore"/>, with ids as text, timestamps as ISO-8601 UTC text and models as JSON text.
/// </summary>
public sealed partial class SqliteStore : IMeridianStore
{
    private readonly string _connectionString;

    public SqliteStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true, Pooling = true }.ToString();
    }

    private static string Now() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    private static DateTime Time(SqliteDataReader r, int i) => DateTime.Parse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    private static Guid Id(SqliteDataReader r, int i) => Guid.Parse(r.GetString(i));
    private static string S(Guid g) => g.ToString("D");

    private async Task<SqliteConnection> OpenAsync()
    {
        var c = new SqliteConnection(_connectionString);
        await c.OpenAsync();
        return c;
    }

    private async Task<SqliteCommand> CmdAsync(string sql, params object?[] args)
    {
        var c = await OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$" + (i + 1), args[i] ?? DBNull.Value);
        cmd.Disposed += (_, _) => c.Dispose();
        return cmd;
    }

    private async Task ExecAsync(string sql, params object?[] args)
    {
        await using var cmd = await CmdAsync(sql, args);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql, params object?[] args)
    {
        await using var cmd = await CmdAsync(sql, args);
        return await cmd.ExecuteScalarAsync();
    }

    // =============================================================== schema

    public async Task MigrateAsync()
    {
        await ExecAsync("""
            pragma journal_mode = wal;

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
            """);
        await MigrateIntegrationsAsync();   // integrations, webhooks and API tokens (SqliteStore.Integrations.cs)
    }

    // =============================================================== users

    public async Task<UserAccount> CreateUserAsync(string username, string displayName, string password)
    {
        username = Accounts.NormalizeUsername(username);
        Accounts.CheckPassword(password);
        var count = (long)(await ScalarAsync("select count(*) from users") ?? 0L);
        var user = new UserAccount(Guid.NewGuid(), username, string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim(), Accounts.Palette[count % Accounts.Palette.Length]);
        try
        {
            await ExecAsync("insert into users(id, username, display_name, password_hash, color, created_at) values ($1,$2,$3,$4,$5,$6)",
                S(user.Id), user.Username, user.DisplayName, Accounts.HashPassword(password), user.Color, Now());
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            throw new InvalidOperationException("That username is taken.");
        }
        return user;
    }

    public async Task<UserAccount?> VerifyUserAsync(string username, string password)
    {
        await using var cmd = await CmdAsync("select id, username, display_name, color, password_hash from users where username = $1", username.Trim().ToLowerInvariant());
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return Accounts.VerifyPassword(password, r.GetString(4)) ? new UserAccount(Id(r, 0), r.GetString(1), r.GetString(2), r.GetString(3)) : null;
    }

    public async Task<UserAccount?> FindUserAsync(string username)
    {
        await using var cmd = await CmdAsync("select id, username, display_name, color from users where username = $1", username.Trim().ToLowerInvariant());
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? new UserAccount(Id(r, 0), r.GetString(1), r.GetString(2), r.GetString(3)) : null;
    }

    // =============================================================== projects

    public async Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(Guid userId)
    {
        await using var cmd = await CmdAsync("""
            select p.id, p.name, m.role, p.updated_at, p.revision, u.display_name
            from projects p join project_members m on m.project_id = p.id and m.user_id = $1
            left join users u on u.id = p.updated_by
            order by p.updated_at desc
            """, S(userId));
        var list = new List<ProjectSummary>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new ProjectSummary(Id(r, 0), r.GetString(1), (ProjectRole)r.GetInt32(2), Time(r, 3), r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    public async Task<Guid> CreateProjectAsync(string name, string modelJson, Guid ownerId)
    {
        var id = Guid.NewGuid();
        await using var c = await OpenAsync();
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync();
        var now = Now();
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "insert into projects(id, name, model, created_by, updated_by, created_at, updated_at) values ($1,$2,$3,$4,$4,$5,$5)";
            cmd.Parameters.AddWithValue("$1", S(id));
            cmd.Parameters.AddWithValue("$2", name.Trim());
            cmd.Parameters.AddWithValue("$3", modelJson);
            cmd.Parameters.AddWithValue("$4", S(ownerId));
            cmd.Parameters.AddWithValue("$5", now);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "insert into project_members(project_id, user_id, role) values ($1,$2,$3)";
            cmd.Parameters.AddWithValue("$1", S(id));
            cmd.Parameters.AddWithValue("$2", S(ownerId));
            cmd.Parameters.AddWithValue("$3", (int)ProjectRole.Owner);
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return id;
    }

    public async Task<(string Name, string ModelJson, long Revision)?> LoadProjectAsync(Guid projectId)
    {
        await using var cmd = await CmdAsync("select name, model, revision from projects where id = $1", S(projectId));
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? (r.GetString(0), r.GetString(1), r.GetInt64(2)) : null;
    }

    public async Task<long> SaveProjectAsync(Guid projectId, string modelJson, Guid? userId) =>
        (long)(await ScalarAsync("update projects set model = $2, revision = revision + 1, updated_at = $4, updated_by = $3 where id = $1 returning revision",
            S(projectId), modelJson, userId is { } u ? S(u) : null, Now()) ?? 0L);

    public Task RenameProjectAsync(Guid projectId, string name) =>
        ExecAsync("update projects set name = $2, updated_at = $3 where id = $1", S(projectId), name.Trim(), Now());

    public Task DeleteProjectAsync(Guid projectId) => ExecAsync("delete from projects where id = $1", S(projectId));

    public async Task<ProjectRole?> RoleOfAsync(Guid projectId, Guid userId) =>
        await ScalarAsync("select role from project_members where project_id = $1 and user_id = $2", S(projectId), S(userId)) is long l ? (ProjectRole)l : null;

    public async Task<IReadOnlyList<ProjectMember>> MembersAsync(Guid projectId)
    {
        await using var cmd = await CmdAsync("""
            select u.id, u.username, u.display_name, u.color, m.role from project_members m join users u on u.id = m.user_id
            where m.project_id = $1 order by m.role desc, u.display_name
            """, S(projectId));
        var list = new List<ProjectMember>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new ProjectMember(new UserAccount(Id(r, 0), r.GetString(1), r.GetString(2), r.GetString(3)), (ProjectRole)r.GetInt32(4)));
        return list;
    }

    public Task SetMemberAsync(Guid projectId, Guid userId, ProjectRole role) =>
        ExecAsync("insert into project_members(project_id, user_id, role) values ($1,$2,$3) on conflict (project_id, user_id) do update set role = excluded.role",
            S(projectId), S(userId), (int)role);

    public Task RemoveMemberAsync(Guid projectId, Guid userId) =>
        ExecAsync("delete from project_members where project_id = $1 and user_id = $2", S(projectId), S(userId));

    // =============================================================== versions

    public async Task<int> CommitVersionAsync(Guid projectId, string message, string modelJson, Guid? author) =>
        (int)(long)(await ScalarAsync("""
            insert into project_versions(project_id, number, message, model, author, created_at)
            values ($1, coalesce((select max(number) from project_versions where project_id = $1), 0) + 1, $2, $3, $4, $5)
            returning number
            """, S(projectId), message, modelJson, author is { } a ? S(a) : null, Now()) ?? 0L);

    public async Task<IReadOnlyList<VersionInfo>> ListVersionsAsync(Guid projectId)
    {
        await using var cmd = await CmdAsync("""
            select v.id, v.number, v.message, coalesce(u.display_name, 'unknown'), v.created_at
            from project_versions v left join users u on u.id = v.author where v.project_id = $1 order by v.number desc
            """, S(projectId));
        var list = new List<VersionInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new VersionInfo(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetString(3), Time(r, 4)));
        return list;
    }

    public async Task<string?> LoadVersionAsync(Guid projectId, long versionId) =>
        await ScalarAsync("select model from project_versions where id = $1 and project_id = $2", versionId, S(projectId)) as string;

    // =============================================================== comments

    public async Task<IReadOnlyList<CommentInfo>> ListCommentsAsync(Guid projectId)
    {
        await using var cmd = await CmdAsync("""
            select c.id, c.element_id, u.id, coalesce(u.username, ''), coalesce(u.display_name, 'unknown'), coalesce(u.color, '#888'), c.body, c.resolved, c.created_at
            from comments c left join users u on u.id = c.author where c.project_id = $1 order by c.created_at
            """, S(projectId));
        var list = new List<CommentInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new CommentInfo(r.GetInt64(0), r.GetString(1),
                new UserAccount(r.IsDBNull(2) ? Guid.Empty : Id(r, 2), r.GetString(3), r.GetString(4), r.GetString(5)),
                r.GetString(6), r.GetInt64(7) != 0, Time(r, 8)));
        return list;
    }

    public async Task<long> AddCommentAsync(Guid projectId, string elementId, Guid author, string body) =>
        (long)(await ScalarAsync("insert into comments(project_id, element_id, author, body, created_at) values ($1,$2,$3,$4,$5) returning id",
            S(projectId), elementId, S(author), body, Now()) ?? 0L);

    public Task SetCommentResolvedAsync(long commentId, bool resolved) =>
        ExecAsync("update comments set resolved = $2 where id = $1", commentId, resolved ? 1 : 0);

    public Task DeleteCommentAsync(long commentId) => ExecAsync("delete from comments where id = $1", commentId);
}
