using Npgsql;
using NpgsqlTypes;

namespace Meridian.Data;

/// <summary>
/// PostgreSQL system of record. Models are stored as JSON documents (jsonb) in their native project format;
/// every version snapshot is a full document, so history, diff and restore need no replay.
/// </summary>
public sealed partial class PostgresStore : IMeridianStore
{
    private readonly NpgsqlDataSource _ds;

    public PostgresStore(string connectionString)
    {
        _ds = NpgsqlDataSource.Create(connectionString);
    }

    // =============================================================== schema

    public async Task MigrateAsync()
    {
        await using var cmd = _ds.CreateCommand("""
            create table if not exists users (
                id uuid primary key,
                username text not null unique,
                display_name text not null,
                password_hash text not null,
                color text not null,
                created_at timestamptz not null default now());

            create table if not exists projects (
                id uuid primary key,
                name text not null,
                model jsonb not null,
                revision bigint not null default 0,
                created_by uuid references users(id) on delete set null,
                updated_by uuid references users(id) on delete set null,
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now());

            create table if not exists project_members (
                project_id uuid not null references projects(id) on delete cascade,
                user_id uuid not null references users(id) on delete cascade,
                role smallint not null,
                primary key (project_id, user_id));

            create table if not exists project_versions (
                id bigserial primary key,
                project_id uuid not null references projects(id) on delete cascade,
                number int not null,
                message text not null,
                model jsonb not null,
                author uuid references users(id) on delete set null,
                created_at timestamptz not null default now(),
                unique (project_id, number));

            create table if not exists comments (
                id bigserial primary key,
                project_id uuid not null references projects(id) on delete cascade,
                element_id text not null,
                author uuid references users(id) on delete set null,
                body text not null,
                resolved boolean not null default false,
                created_at timestamptz not null default now());

            create index if not exists comments_project_element on comments(project_id, element_id);
            create index if not exists members_user on project_members(user_id);
            """);
        await cmd.ExecuteNonQueryAsync();
        await MigrateReviewsAsync();
        await MigrateIntegrationsAsync();   // integrations, webhooks and API tokens (PostgresStore.Integrations.cs)
    }

    // =============================================================== users

    public async Task<UserAccount> CreateUserAsync(string username, string displayName, string password)
    {
        username = Accounts.NormalizeUsername(username);
        Accounts.CheckPassword(password);
        var count = await ScalarAsync<long>("select count(*) from users");
        var user = new UserAccount(Guid.NewGuid(), username, string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim(), Accounts.Palette[count % Accounts.Palette.Length]);
        await using var cmd = _ds.CreateCommand("insert into users(id, username, display_name, password_hash, color) values ($1,$2,$3,$4,$5)");
        cmd.Parameters.Add(new() { Value = user.Id });
        cmd.Parameters.Add(new() { Value = user.Username });
        cmd.Parameters.Add(new() { Value = user.DisplayName });
        cmd.Parameters.Add(new() { Value = Accounts.HashPassword(password) });
        cmd.Parameters.Add(new() { Value = user.Color });
        try { await cmd.ExecuteNonQueryAsync(); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException("That username is taken.");
        }
        return user;
    }

    public async Task<UserAccount?> VerifyUserAsync(string username, string password)
    {
        await using var cmd = _ds.CreateCommand("select id, username, display_name, color, password_hash from users where username = $1");
        cmd.Parameters.Add(new() { Value = username.Trim().ToLowerInvariant() });
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return Accounts.VerifyPassword(password, r.GetString(4)) ? new UserAccount(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3)) : null;
    }

    public async Task<UserAccount?> FindUserAsync(string username)
    {
        await using var cmd = _ds.CreateCommand("select id, username, display_name, color from users where username = $1");
        cmd.Parameters.Add(new() { Value = username.Trim().ToLowerInvariant() });
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? new UserAccount(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3)) : null;
    }

    // =============================================================== projects

    public async Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(Guid userId)
    {
        await using var cmd = _ds.CreateCommand("""
            select p.id, p.name, m.role, p.updated_at, p.revision, u.display_name
            from projects p join project_members m on m.project_id = p.id and m.user_id = $1
            left join users u on u.id = p.updated_by
            order by p.updated_at desc
            """);
        cmd.Parameters.Add(new() { Value = userId });
        var list = new List<ProjectSummary>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new ProjectSummary(r.GetGuid(0), r.GetString(1), (ProjectRole)r.GetInt16(2), r.GetDateTime(3), r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    public async Task<Guid> CreateProjectAsync(string name, string modelJson, Guid ownerId)
    {
        var id = Guid.NewGuid();
        await using var conn = await _ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("insert into projects(id, name, model, created_by, updated_by) values ($1,$2,$3,$4,$4)", conn, tx))
        {
            cmd.Parameters.Add(new() { Value = id });
            cmd.Parameters.Add(new() { Value = name.Trim() });
            cmd.Parameters.Add(new() { Value = modelJson, NpgsqlDbType = NpgsqlDbType.Jsonb });
            cmd.Parameters.Add(new() { Value = ownerId });
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = new NpgsqlCommand("insert into project_members(project_id, user_id, role) values ($1,$2,$3)", conn, tx))
        {
            cmd.Parameters.Add(new() { Value = id });
            cmd.Parameters.Add(new() { Value = ownerId });
            cmd.Parameters.Add(new() { Value = (short)ProjectRole.Owner });
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return id;
    }

    public async Task<(string Name, string ModelJson, long Revision)?> LoadProjectAsync(Guid projectId)
    {
        await using var cmd = _ds.CreateCommand("select name, model::text, revision from projects where id = $1");
        cmd.Parameters.Add(new() { Value = projectId });
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? (r.GetString(0), r.GetString(1), r.GetInt64(2)) : null;
    }

    public async Task<long> SaveProjectAsync(Guid projectId, string modelJson, Guid? userId)
    {
        await using var cmd = _ds.CreateCommand("""
            update projects set model = $2, revision = revision + 1, updated_at = now(), updated_by = $3
            where id = $1 returning revision
            """);
        cmd.Parameters.Add(new() { Value = projectId });
        cmd.Parameters.Add(new() { Value = modelJson, NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new() { Value = (object?)userId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    public Task RenameProjectAsync(Guid projectId, string name) =>
        ExecAsync("update projects set name = $2, updated_at = now() where id = $1", projectId, name.Trim());

    public Task DeleteProjectAsync(Guid projectId) => ExecAsync("delete from projects where id = $1", projectId);

    public async Task<ProjectRole?> RoleOfAsync(Guid projectId, Guid userId)
    {
        await using var cmd = _ds.CreateCommand("select role from project_members where project_id = $1 and user_id = $2");
        cmd.Parameters.Add(new() { Value = projectId });
        cmd.Parameters.Add(new() { Value = userId });
        var v = await cmd.ExecuteScalarAsync();
        return v is short s ? (ProjectRole)s : null;
    }

    public async Task<IReadOnlyList<ProjectMember>> MembersAsync(Guid projectId)
    {
        await using var cmd = _ds.CreateCommand("""
            select u.id, u.username, u.display_name, u.color, m.role from project_members m join users u on u.id = m.user_id
            where m.project_id = $1 order by m.role desc, u.display_name
            """);
        cmd.Parameters.Add(new() { Value = projectId });
        var list = new List<ProjectMember>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new ProjectMember(new UserAccount(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3)), (ProjectRole)r.GetInt16(4)));
        return list;
    }

    public Task SetMemberAsync(Guid projectId, Guid userId, ProjectRole role) =>
        ExecAsync("insert into project_members(project_id, user_id, role) values ($1,$2,$3) on conflict (project_id, user_id) do update set role = excluded.role",
            projectId, userId, (short)role);

    public Task RemoveMemberAsync(Guid projectId, Guid userId) =>
        ExecAsync("delete from project_members where project_id = $1 and user_id = $2", projectId, userId);

    // =============================================================== versions

    public async Task<int> CommitVersionAsync(Guid projectId, string message, string modelJson, Guid? author)
    {
        await using var cmd = _ds.CreateCommand("""
            insert into project_versions(project_id, number, message, model, author)
            values ($1, coalesce((select max(number) from project_versions where project_id = $1), 0) + 1, $2, $3, $4)
            returning number
            """);
        cmd.Parameters.Add(new() { Value = projectId });
        cmd.Parameters.Add(new() { Value = message });
        cmd.Parameters.Add(new() { Value = modelJson, NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new() { Value = (object?)author ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    public async Task<IReadOnlyList<VersionInfo>> ListVersionsAsync(Guid projectId)
    {
        await using var cmd = _ds.CreateCommand("""
            select v.id, v.number, v.message, coalesce(u.display_name, 'unknown'), v.created_at
            from project_versions v left join users u on u.id = v.author where v.project_id = $1 order by v.number desc
            """);
        cmd.Parameters.Add(new() { Value = projectId });
        var list = new List<VersionInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new VersionInfo(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetDateTime(4)));
        return list;
    }

    public async Task<string?> LoadVersionAsync(Guid projectId, long versionId)
    {
        await using var cmd = _ds.CreateCommand("select model::text from project_versions where id = $1 and project_id = $2");
        cmd.Parameters.Add(new() { Value = versionId });
        cmd.Parameters.Add(new() { Value = projectId });
        return await cmd.ExecuteScalarAsync() as string;
    }

    // =============================================================== comments

    public async Task<IReadOnlyList<CommentInfo>> ListCommentsAsync(Guid projectId)
    {
        await using var cmd = _ds.CreateCommand("""
            select c.id, c.element_id, u.id, coalesce(u.username, ''), coalesce(u.display_name, 'unknown'), coalesce(u.color, '#888'), c.body, c.resolved, c.created_at
            from comments c left join users u on u.id = c.author where c.project_id = $1 order by c.created_at
            """);
        cmd.Parameters.Add(new() { Value = projectId });
        var list = new List<CommentInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new CommentInfo(r.GetInt64(0), r.GetString(1),
                new UserAccount(r.IsDBNull(2) ? Guid.Empty : r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5)),
                r.GetString(6), r.GetBoolean(7), r.GetDateTime(8)));
        return list;
    }

    public async Task<long> AddCommentAsync(Guid projectId, string elementId, Guid author, string body)
    {
        await using var cmd = _ds.CreateCommand("insert into comments(project_id, element_id, author, body) values ($1,$2,$3,$4) returning id");
        cmd.Parameters.Add(new() { Value = projectId });
        cmd.Parameters.Add(new() { Value = elementId });
        cmd.Parameters.Add(new() { Value = author });
        cmd.Parameters.Add(new() { Value = body });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    public Task SetCommentResolvedAsync(long commentId, bool resolved) =>
        ExecAsync("update comments set resolved = $2 where id = $1", commentId, resolved);

    public Task DeleteCommentAsync(long commentId) => ExecAsync("delete from comments where id = $1", commentId);

    // =============================================================== helpers

    private async Task ExecAsync(string sql, params object[] args)
    {
        await using var cmd = _ds.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.Add(new() { Value = a });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params object[] args)
    {
        await using var cmd = _ds.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.Add(new() { Value = a });
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
