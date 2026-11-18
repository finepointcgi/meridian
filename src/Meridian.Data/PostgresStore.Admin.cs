using Npgsql;
using NpgsqlTypes;

namespace Meridian.Data;

/// <summary>Enterprise administration for the PostgreSQL store: server roles, lockout, SSO links, organizations/teams, restore and audit.</summary>
public sealed partial class PostgresStore
{
    // =============================================================== schema (administration block)

    private static async Task MigrateAdminAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand("""
            alter table users add column if not exists is_admin boolean not null default false;
            alter table users add column if not exists disabled boolean not null default false;
            alter table users add column if not exists failed_logins int not null default 0;
            alter table users add column if not exists locked_until timestamptz;
            alter table users add column if not exists last_login_at timestamptz;

            create table if not exists external_logins (
                issuer text not null,
                subject text not null,
                provider text not null,
                user_id uuid not null references users(id) on delete cascade,
                email text,
                created_at timestamptz not null default now(),
                primary key (issuer, subject));

            create table if not exists organizations (
                id uuid primary key,
                name text not null unique,
                created_at timestamptz not null default now());

            create table if not exists organization_members (
                organization_id uuid not null references organizations(id) on delete cascade,
                user_id uuid not null references users(id) on delete cascade,
                primary key (organization_id, user_id));

            create table if not exists teams (
                id uuid primary key,
                organization_id uuid not null references organizations(id) on delete cascade,
                name text not null,
                created_at timestamptz not null default now(),
                unique (organization_id, name));

            create table if not exists team_members (
                team_id uuid not null references teams(id) on delete cascade,
                user_id uuid not null references users(id) on delete cascade,
                primary key (team_id, user_id));

            create table if not exists team_project_roles (
                team_id uuid not null references teams(id) on delete cascade,
                project_id uuid not null references projects(id) on delete cascade,
                role smallint not null,
                primary key (team_id, project_id));

            create table if not exists audit_log (
                id bigserial primary key,
                at timestamptz not null default now(),
                actor_id uuid,
                actor_name text not null,
                action text not null,
                project_id uuid,
                project_name text,
                target text,
                details jsonb,
                ip text);

            create index if not exists audit_log_at on audit_log(at);
            create index if not exists audit_log_action on audit_log(action);
            create index if not exists team_members_user on team_members(user_id);
            create index if not exists external_logins_user on external_logins(user_id);
            create index if not exists team_project_roles_project on team_project_roles(project_id);
            create index if not exists audit_log_project on audit_log(project_id);

            create or replace function meridian_audit_append_only() returns trigger language plpgsql as $fn$
            begin raise exception 'audit_log is append-only'; end $fn$;
            drop trigger if exists audit_log_append_only on audit_log;
            create trigger audit_log_append_only before update or delete on audit_log
                for each row execute function meridian_audit_append_only();
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    // =============================================================== users

    private const string AdminUserColumns = """
        u.id, u.username, u.display_name, u.color, u.is_admin, u.disabled, u.password_hash, u.created_at, u.last_login_at, u.locked_until, u.failed_logins,
        (select string_agg(distinct provider, ',') from external_logins e where e.user_id = u.id)
        """;

    private static AdminUser ReadAdminUser(NpgsqlDataReader r) => new(
        new UserAccount(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3)),
        r.GetBoolean(4), r.GetBoolean(5), r.GetString(6).StartsWith("pbkdf2", StringComparison.Ordinal), r.GetDateTime(7),
        r.IsDBNull(8) ? null : r.GetDateTime(8), r.IsDBNull(9) ? null : r.GetDateTime(9), r.GetInt32(10),
        r.IsDBNull(11) ? [] : r.GetString(11).Split(',', StringSplitOptions.RemoveEmptyEntries));

    private static UserAccount ReadUser(NpgsqlDataReader r, int at = 0) => new(r.GetGuid(at), r.GetString(at + 1), r.GetString(at + 2), r.GetString(at + 3));

    public async Task<AdminUser?> GetUserAsync(Guid userId)
    {
        await using var cmd = Command($"select {AdminUserColumns} from users u where u.id = $1", userId);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadAdminUser(r) : null;
    }

    public async Task<IReadOnlyList<AdminUser>> ListUsersAsync()
    {
        await using var cmd = Command($"select {AdminUserColumns} from users u order by u.username");
        var list = new List<AdminUser>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(ReadAdminUser(r));
        return list;
    }

    public Task SetUserAdminAsync(Guid userId, bool admin) => ExecAsync("update users set is_admin = $2 where id = $1", userId, admin);

    public Task SetUserDisabledAsync(Guid userId, bool disabled) => ExecAsync("update users set disabled = $2 where id = $1", userId, disabled);

    public Task SetPasswordAsync(Guid userId, string password)
    {
        Accounts.CheckPassword(password);
        return ExecAsync("update users set password_hash = $2, failed_logins = 0, locked_until = null where id = $1", userId, Accounts.HashPassword(password));
    }

    public Task UnlockUserAsync(Guid userId) => ExecAsync("update users set failed_logins = 0, locked_until = null where id = $1", userId);

    public async Task<bool> EnsureAdminAsync()
    {
        await using var cmd = Command("""
            update users set is_admin = true
            where not exists (select 1 from users where is_admin)
              and id = (select id from users where username <> 'local' order by created_at, username limit 1)
            """);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<LoginResult> AuthenticateAsync(string username, string password, int maxFailures, TimeSpan lockout)
    {
        UserAccount user;
        string hash;
        bool disabled;
        DateTime? lockedUntil;
        await using (var cmd = Command("select id, username, display_name, color, password_hash, disabled, locked_until from users where username = $1", username.Trim().ToLowerInvariant()))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            if (!await r.ReadAsync())
            {
                Accounts.VerifyPassword(password, SqliteStore.DummyHash.Value); // same cost as a real check
                return new LoginResult(LoginStatus.InvalidCredentials);
            }
            user = ReadUser(r);
            hash = r.GetString(4);
            disabled = r.GetBoolean(5);
            lockedUntil = r.IsDBNull(6) ? null : r.GetDateTime(6);
        }
        if (disabled) return new LoginResult(LoginStatus.Disabled, UserId: user.Id);
        if (lockedUntil > DateTime.UtcNow) return new LoginResult(LoginStatus.LockedOut, LockedUntil: lockedUntil, UserId: user.Id);
        if (Accounts.VerifyPassword(password, hash))
        {
            await ExecAsync("update users set failed_logins = 0, locked_until = null, last_login_at = now() where id = $1", user.Id);
            return new LoginResult(LoginStatus.Success, user, UserId: user.Id);
        }
        int failures;
        await using (var cmd = Command("update users set failed_logins = failed_logins + 1 where id = $1 returning failed_logins", user.Id))
            failures = (int)(await cmd.ExecuteScalarAsync() ?? 0);
        if (maxFailures > 0 && failures >= maxFailures)
        {
            var until = DateTime.UtcNow + lockout;
            await ExecAsync("update users set failed_logins = 0, locked_until = $2 where id = $1", user.Id, until);
            return new LoginResult(LoginStatus.LockedOut, LockedUntil: until, UserId: user.Id);
        }
        return new LoginResult(LoginStatus.InvalidCredentials, UserId: user.Id);
    }

    // =============================================================== single sign-on

    public async Task<UserAccount?> FindExternalLoginAsync(string issuer, string subject)
    {
        await using var cmd = Command("""
            select u.id, u.username, u.display_name, u.color from external_logins e join users u on u.id = e.user_id
            where e.issuer = $1 and e.subject = $2
            """, issuer, subject);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadUser(r) : null;
    }

    public async Task<UserAccount> CreateExternalUserAsync(string username, string displayName, ExternalLogin login)
    {
        var baseName = LoginPolicy.SuggestUsername(username);
        await using var conn = await _ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        async Task<object?> Run(string sql, params object?[] args)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            foreach (var a in args) cmd.Parameters.Add(a as NpgsqlParameter ?? new NpgsqlParameter { Value = a ?? DBNull.Value });
            return await cmd.ExecuteScalarAsync();
        }
        // One provisioning per identity at a time: two concurrent first sign-ins must not create two accounts
        // (the loser's insert into external_logins would be silently skipped, leaving an orphaned, unlinked account).
        await Run("select pg_advisory_xact_lock(hashtext($1 || chr(10) || $2))", login.Issuer, login.Subject);
        if (await Run("select user_id from external_logins where issuer = $1 and subject = $2", login.Issuer, login.Subject) is Guid linked)
        {
            await tx.CommitAsync();
            return (await GetUserAsync(linked))!.User;
        }
        var count = (long)(await Run("select count(*) from users"))!;
        for (var attempt = 1; attempt < 200; attempt++)
        {
            var candidate = attempt == 1 ? baseName : $"{baseName}{attempt}";
            var user = new UserAccount(Guid.NewGuid(), candidate, string.IsNullOrWhiteSpace(displayName) ? candidate : displayName.Trim(), Accounts.Palette[count % Accounts.Palette.Length]);
            if (await Run("insert into users(id, username, display_name, password_hash, color) values ($1,$2,$3,$4,$5) on conflict (username) do nothing returning id",
                    user.Id, user.Username, user.DisplayName, LoginPolicy.NoPassword, user.Color) is null)
                continue;   // taken
            await Run("insert into external_logins(issuer, subject, provider, user_id, email) values ($1,$2,$3,$4,$5)",
                login.Issuer, login.Subject, login.Provider, user.Id, Typed(login.Email, NpgsqlDbType.Text));
            await tx.CommitAsync();
            return user;
        }
        throw new InvalidOperationException("Could not find a free username for this account.");
    }

    public async Task LinkExternalLoginAsync(Guid userId, ExternalLogin login)
    {
        await using var cmd = Command("insert into external_logins(issuer, subject, provider, user_id, email) values ($1,$2,$3,$4,$5) on conflict (issuer, subject) do nothing",
            login.Issuer, login.Subject, login.Provider, userId, Typed(login.Email, NpgsqlDbType.Text));
        await cmd.ExecuteNonQueryAsync();
    }

    // =============================================================== organizations & teams

    public async Task<Guid> CreateOrganizationAsync(string name)
    {
        var id = Guid.NewGuid();
        try { await ExecAsync("insert into organizations(id, name) values ($1,$2)", id, RequireName(name)); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { throw new InvalidOperationException("An organization with that name already exists."); }
        return id;
    }

    public Task DeleteOrganizationAsync(Guid organizationId) => ExecAsync("delete from organizations where id = $1", organizationId);

    public async Task<IReadOnlyList<OrganizationInfo>> ListOrganizationsAsync()
    {
        await using var cmd = Command("""
            select o.id, o.name, (select count(*)::int from organization_members m where m.organization_id = o.id),
                   (select count(*)::int from teams t where t.organization_id = o.id), o.created_at
            from organizations o order by o.name
            """);
        var list = new List<OrganizationInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new OrganizationInfo(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetDateTime(4)));
        return list;
    }

    public Task<IReadOnlyList<UserAccount>> OrganizationMembersAsync(Guid organizationId) => UsersAsync("""
        select u.id, u.username, u.display_name, u.color from organization_members m join users u on u.id = m.user_id
        where m.organization_id = $1 order by u.display_name
        """, organizationId);

    public Task AddOrganizationMemberAsync(Guid organizationId, Guid userId) =>
        ExecAsync("insert into organization_members(organization_id, user_id) values ($1,$2) on conflict do nothing", organizationId, userId);

    public async Task RemoveOrganizationMemberAsync(Guid organizationId, Guid userId)
    {
        await ExecAsync("delete from team_members where user_id = $2 and team_id in (select id from teams where organization_id = $1)", organizationId, userId);
        await ExecAsync("delete from organization_members where organization_id = $1 and user_id = $2", organizationId, userId);
    }

    public async Task<Guid> CreateTeamAsync(Guid organizationId, string name)
    {
        var id = Guid.NewGuid();
        try { await ExecAsync("insert into teams(id, organization_id, name) values ($1,$2,$3)", id, organizationId, RequireName(name)); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { throw new InvalidOperationException("That organization already has a team with that name."); }
        return id;
    }

    public async Task DeleteTeamAsync(Guid teamId)
    {
        await ExecAsync("delete from package_permissions where principal_kind = 'team' and principal_id = $1", teamId);
        await ExecAsync("delete from teams where id = $1", teamId);
    }

    public async Task<IReadOnlyList<TeamInfo>> ListTeamsAsync(Guid organizationId)
    {
        await using var cmd = Command("""
            select t.id, t.organization_id, t.name, (select count(*)::int from team_members m where m.team_id = t.id),
                   (select count(*)::int from team_project_roles g where g.team_id = t.id)
            from teams t where t.organization_id = $1 order by t.name
            """, organizationId);
        var list = new List<TeamInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new TeamInfo(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4)));
        return list;
    }

    public Task<IReadOnlyList<UserAccount>> TeamMembersAsync(Guid teamId) => UsersAsync("""
        select u.id, u.username, u.display_name, u.color from team_members m join users u on u.id = m.user_id
        where m.team_id = $1 order by u.display_name
        """, teamId);

    public async Task AddTeamMemberAsync(Guid teamId, Guid userId)
    {
        await ExecAsync("insert into organization_members(organization_id, user_id) select organization_id, $2 from teams where id = $1 on conflict do nothing", teamId, userId);
        await ExecAsync("insert into team_members(team_id, user_id) values ($1,$2) on conflict do nothing", teamId, userId);
    }

    public Task RemoveTeamMemberAsync(Guid teamId, Guid userId) =>
        ExecAsync("delete from team_members where team_id = $1 and user_id = $2", teamId, userId);

    public async Task<IReadOnlyList<TeamGrant>> TeamGrantsAsync(Guid teamId)
    {
        await using var cmd = Command("select p.id, p.name, g.role from team_project_roles g join projects p on p.id = g.project_id where g.team_id = $1 order by p.name", teamId);
        var list = new List<TeamGrant>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new TeamGrant(r.GetGuid(0), r.GetString(1), (ProjectRole)r.GetInt16(2)));
        return list;
    }

    public Task SetTeamGrantAsync(Guid teamId, Guid projectId, ProjectRole role) =>
        ExecAsync("insert into team_project_roles(team_id, project_id, role) values ($1,$2,$3) on conflict (team_id, project_id) do update set role = excluded.role",
            teamId, projectId, (short)role);

    public Task RemoveTeamGrantAsync(Guid teamId, Guid projectId) =>
        ExecAsync("delete from team_project_roles where team_id = $1 and project_id = $2", teamId, projectId);

    // =============================================================== all projects

    public async Task<IReadOnlyList<AdminProject>> ListAllProjectsAsync()
    {
        await using var cmd = Command("""
            select p.id, p.name,
                   (pg_column_size(p.model) + coalesce((select sum(pg_column_size(v.model)) from project_versions v where v.project_id = p.id), 0))::bigint,
                   (select count(*)::int from project_versions v where v.project_id = p.id),
                   (select count(*)::int from project_members m where m.project_id = p.id),
                   p.created_at, p.updated_at, p.revision,
                   o.id, o.username, o.display_name, o.color
            from projects p
            left join users o on o.id = (select m.user_id from project_members m where m.project_id = p.id and m.role = 2 order by m.user_id limit 1)
            order by lower(p.name)
            """);
        var list = new List<AdminProject>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new AdminProject(r.GetGuid(0), r.GetString(1), r.IsDBNull(8) ? null : ReadUser(r, 8), r.GetInt64(2), r.GetInt32(3), r.GetInt32(4), r.GetDateTime(5), r.GetDateTime(6), r.GetInt64(7)));
        return list;
    }

    public async Task TransferProjectAsync(Guid projectId, Guid newOwnerId)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand($"update project_members set role = {(int)ProjectRole.Editor} where project_id = $1 and role = {(int)ProjectRole.Owner} and user_id <> $2", conn, tx))
        {
            cmd.Parameters.Add(new() { Value = projectId });
            cmd.Parameters.Add(new() { Value = newOwnerId });
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = new NpgsqlCommand($"insert into project_members(project_id, user_id, role) values ($1,$2,{(int)ProjectRole.Owner}) on conflict (project_id, user_id) do update set role = excluded.role", conn, tx))
        {
            cmd.Parameters.Add(new() { Value = projectId });
            cmd.Parameters.Add(new() { Value = newOwnerId });
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task<Guid> RestoreProjectAsync(RestoredProject project)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        async Task<object?> Run(string sql, params object?[] args)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            foreach (var a in args) cmd.Parameters.Add(a as NpgsqlParameter ?? new NpgsqlParameter { Value = a ?? DBNull.Value });
            return await cmd.ExecuteScalarAsync();
        }
        var id = await Run("select 1 from projects where id = $1", project.Id) is null ? project.Id : Guid.NewGuid();
        await Run("insert into projects(id, name, model) values ($1,$2,$3)", id, project.Name.Trim(), Typed(project.ModelJson, NpgsqlDbType.Jsonb));
        foreach (var m in project.Members)
            await Run("insert into project_members(project_id, user_id, role) select $1, $2, $3 where exists (select 1 from users where id = $2) on conflict do nothing",
                id, m.UserId, (short)m.Role);
        foreach (var v in project.Versions)
            await Run("insert into project_versions(project_id, number, message, model, author, created_at) values ($1,$2,$3,$4,(select id from users where id = $5),$6)",
                id, v.Number, v.Message, Typed(v.ModelJson, NpgsqlDbType.Jsonb), Typed(v.AuthorId, NpgsqlDbType.Uuid), DateTime.SpecifyKind(v.CreatedAt.ToUniversalTime(), DateTimeKind.Utc));

        static DateTime T(DateTime t) => DateTime.SpecifyKind(t.ToUniversalTime(), DateTimeKind.Utc);
        static NpgsqlParameter U(Guid? g) => Typed(g, NpgsqlDbType.Uuid);
        // Version numbers → the new version rows (reviews and baselines point at versions by row id).
        var versionIds = new Dictionary<int, long>();
        foreach (var v in project.Versions)
            if (await Run("select id from project_versions where project_id = $1 and number = $2", id, v.Number) is long vid) versionIds[v.Number] = vid;
        NpgsqlParameter VersionId(int? number) => Typed(number is { } n && versionIds.TryGetValue(n, out var vid) ? vid : null, NpgsqlDbType.Bigint);

        var commentIds = new Dictionary<long, long>();
        foreach (var cm in project.Comments ?? [])
            commentIds[cm.Key] = (long)(await Run("insert into comments(project_id, element_id, author, body, resolved, created_at) values ($1,$2,(select id from users where id = $3),$4,$5,$6) returning id",
                id, cm.ElementId, U(cm.AuthorId), cm.Body, cm.Resolved, T(cm.CreatedAt)))!;
        foreach (var cm in project.Comments ?? [])
            if (cm.ParentKey is { } pk && commentIds.TryGetValue(pk, out var parent))
                await Run("insert into comment_replies(comment_id, parent_id) values ($1,$2) on conflict (comment_id) do nothing", commentIds[cm.Key], parent);

        foreach (var r in project.Reviews ?? [])
        {
            var rid = (long)(await Run("""
                insert into reviews(project_id, title, description, author, state, scope_kind, scope_ids, base_version, head_version, apply_status, created_at, updated_at)
                values ($1,$2,$3,(select id from users where id = $4),$5,$6,$7,$8,$9,$10,$11,$12) returning id
                """, id, r.Title, r.Description, U(r.AuthorId), (short)r.State, (short)r.ScopeKind, Typed(ReviewRules.EncodeIds(r.ElementIds), NpgsqlDbType.Jsonb),
                VersionId(r.BaseVersion), VersionId(r.HeadVersion), r.ApplyStatus, T(r.CreatedAt), T(r.UpdatedAt)))!;
            foreach (var rv in r.Reviewers)
                await Run("insert into review_reviewers(review_id, user_id, vote, voted_at) select $1, $2, $3, $4 where exists (select 1 from users where id = $2) on conflict do nothing",
                    rid, rv.UserId, (short)rv.Vote, Typed(rv.VotedAt is { } va ? T(va) : null, NpgsqlDbType.TimestampTz));
            var reviewCommentIds = new Dictionary<long, long>();
            foreach (var rc in r.Comments)   // parents come first: replies are written after the comment they answer
                reviewCommentIds[rc.Key] = (long)(await Run("insert into review_comments(review_id, element_id, parent_id, author, body, created_at) values ($1,$2,$3,(select id from users where id = $4),$5,$6) returning id",
                    rid, Typed(rc.ElementId, NpgsqlDbType.Text), Typed(rc.ParentKey is { } pk && reviewCommentIds.TryGetValue(pk, out var parent) ? parent : null, NpgsqlDbType.Bigint),
                    U(rc.AuthorId), rc.Body, T(rc.CreatedAt)))!;
            foreach (var ck in r.Checks)
                await Run("insert into review_checks(review_id, element_id, user_id, checked_at) select $1, $2, $3, $4 where exists (select 1 from users where id = $3) on conflict do nothing",
                    rid, ck.ElementId, ck.UserId, T(r.UpdatedAt));
        }

        foreach (var b in project.Baselines ?? [])
            if (versionIds.TryGetValue(b.VersionNumber, out var vid))
                await Run("insert into baselines(project_id, version_id, name, description, created_at) values ($1,$2,$3,$4,$5) on conflict (project_id, name) do nothing",
                    id, vid, b.Name, b.Description, T(b.CreatedAt));
        await tx.CommitAsync();
        return id;
    }

    public async Task<IReadOnlyList<VersionSnapshot>> ListVersionSnapshotsAsync(Guid projectId)
    {
        await using var cmd = Command("select number, message, model::text, author, created_at from project_versions where project_id = $1 order by number", projectId);
        var list = new List<VersionSnapshot>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new VersionSnapshot(r.GetInt32(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.GetDateTime(4)));
        return list;
    }

    // =============================================================== audit log

    public async Task AppendAuditAsync(AuditEntry e)
    {
        await using var cmd = Command("insert into audit_log(at, actor_id, actor_name, action, project_id, project_name, target, details, ip) values ($1,$2,$3,$4,$5,$6,$7,$8,$9)",
            DateTime.SpecifyKind(e.At.ToUniversalTime(), DateTimeKind.Utc), Typed(e.ActorId, NpgsqlDbType.Uuid), e.Actor, e.Action, Typed(e.ProjectId, NpgsqlDbType.Uuid),
            Typed(e.ProjectName, NpgsqlDbType.Text), Typed(e.Target, NpgsqlDbType.Text), Typed(e.DetailsJson, NpgsqlDbType.Jsonb), Typed(e.Ip, NpgsqlDbType.Text));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<AuditPage> QueryAuditAsync(AuditQuery q)
    {
        var (where, args) = LoginPolicy.AuditFilter(q, i => "$" + i, g => g, t => DateTime.SpecifyKind(t, DateTimeKind.Utc), "details::text");
        long total;
        await using (var count = Command($"select count(*) from audit_log {where}", args.ToArray()))
            total = (long)(await count.ExecuteScalarAsync() ?? 0L);
        args.Add(Math.Clamp(q.Take, 1, 10_000));
        args.Add(Math.Max(0, q.Skip));
        await using var cmd = Command($"""
            select id, at, actor_id, actor_name, action, project_id, project_name, target, details::text, ip from audit_log {where}
            order by id desc limit ${args.Count - 1} offset ${args.Count}
            """, args.ToArray());
        var list = new List<AuditEntry>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new AuditEntry(r.GetDateTime(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetGuid(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetInt64(0)));
        return new AuditPage(list, (int)total);
    }

    // =============================================================== helpers

    private static NpgsqlParameter Typed(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };

    private NpgsqlCommand Command(string sql, params object?[] args)
    {
        var cmd = _ds.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.Add(a as NpgsqlParameter ?? new NpgsqlParameter { Value = a ?? DBNull.Value });
        return cmd;
    }

    private async Task<IReadOnlyList<UserAccount>> UsersAsync(string sql, params object?[] args)
    {
        await using var cmd = Command(sql, args);
        var list = new List<UserAccount>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(ReadUser(r));
        return list;
    }

    private static string RequireName(string name) =>
        string.IsNullOrWhiteSpace(name) ? throw new InvalidOperationException("Enter a name.") : name.Trim();
}
