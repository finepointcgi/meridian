using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Meridian.Data;

/// <summary>Enterprise administration for the SQLite store: server roles, lockout, SSO links, organizations/teams, restore and audit.</summary>
public sealed partial class SqliteStore
{
    // =============================================================== schema (administration block)

    private async Task MigrateAdminAsync()
    {
        foreach (var (column, ddl) in new[]
        {
            ("is_admin", "is_admin integer not null default 0"),
            ("disabled", "disabled integer not null default 0"),
            ("failed_logins", "failed_logins integer not null default 0"),
            ("locked_until", "locked_until text"),
            ("last_login_at", "last_login_at text"),
        })
        {
            if (await ScalarAsync("select count(*) from pragma_table_info('users') where name = $1", column) is 0L)
            {
                // Another process migrating at the same moment may add the column between the check and the alter.
                try { await ExecAsync($"alter table users add column {ddl}"); }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }
            }
        }

        await ExecAsync("""
            create table if not exists external_logins (
                issuer text not null,
                subject text not null,
                provider text not null,
                user_id text not null references users(id) on delete cascade,
                email text,
                created_at text not null,
                primary key (issuer, subject));

            create table if not exists organizations (
                id text primary key,
                name text not null unique,
                created_at text not null);

            create table if not exists organization_members (
                organization_id text not null references organizations(id) on delete cascade,
                user_id text not null references users(id) on delete cascade,
                primary key (organization_id, user_id));

            create table if not exists teams (
                id text primary key,
                organization_id text not null references organizations(id) on delete cascade,
                name text not null,
                created_at text not null,
                unique (organization_id, name));

            create table if not exists team_members (
                team_id text not null references teams(id) on delete cascade,
                user_id text not null references users(id) on delete cascade,
                primary key (team_id, user_id));

            create table if not exists team_project_roles (
                team_id text not null references teams(id) on delete cascade,
                project_id text not null references projects(id) on delete cascade,
                role integer not null,
                primary key (team_id, project_id));

            create table if not exists audit_log (
                id integer primary key autoincrement,
                at text not null,
                actor_id text,
                actor_name text not null,
                action text not null,
                project_id text,
                project_name text,
                target text,
                details text,
                ip text);

            create index if not exists audit_log_at on audit_log(at);
            create index if not exists audit_log_action on audit_log(action);
            create index if not exists team_members_user on team_members(user_id);
            create index if not exists external_logins_user on external_logins(user_id);

            create trigger if not exists audit_log_no_update before update on audit_log
            begin select raise(abort, 'audit_log is append-only'); end;
            create trigger if not exists audit_log_no_delete before delete on audit_log
            begin select raise(abort, 'audit_log is append-only'); end;
            """);
    }

    // =============================================================== users

    private const string AdminUserColumns = """
        u.id, u.username, u.display_name, u.color, u.is_admin, u.disabled, u.password_hash, u.created_at, u.last_login_at, u.locked_until, u.failed_logins,
        (select group_concat(provider, ',') from external_logins e where e.user_id = u.id)
        """;

    private static AdminUser ReadAdminUser(SqliteDataReader r) => new(
        new UserAccount(Id(r, 0), r.GetString(1), r.GetString(2), r.GetString(3)),
        r.GetInt64(4) != 0, r.GetInt64(5) != 0, r.GetString(6).StartsWith("pbkdf2", StringComparison.Ordinal), Time(r, 7),
        r.IsDBNull(8) ? null : Time(r, 8), r.IsDBNull(9) ? null : Time(r, 9), r.GetInt32(10),
        r.IsDBNull(11) ? [] : r.GetString(11).Split(',', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList());

    private static UserAccount ReadUser(SqliteDataReader r, int at = 0) => new(Id(r, at), r.GetString(at + 1), r.GetString(at + 2), r.GetString(at + 3));

    public async Task<AdminUser?> GetUserAsync(Guid userId)
    {
        await using var cmd = await CmdAsync($"select {AdminUserColumns} from users u where u.id = $1", S(userId));
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadAdminUser(r) : null;
    }

    public async Task<IReadOnlyList<AdminUser>> ListUsersAsync()
    {
        await using var cmd = await CmdAsync($"select {AdminUserColumns} from users u order by u.username");
        var list = new List<AdminUser>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(ReadAdminUser(r));
        return list;
    }

    public Task SetUserAdminAsync(Guid userId, bool admin) => ExecAsync("update users set is_admin = $2 where id = $1", S(userId), admin ? 1 : 0);

    public Task SetUserDisabledAsync(Guid userId, bool disabled) => ExecAsync("update users set disabled = $2 where id = $1", S(userId), disabled ? 1 : 0);

    public Task SetPasswordAsync(Guid userId, string password)
    {
        Accounts.CheckPassword(password);
        return ExecAsync("update users set password_hash = $2, failed_logins = 0, locked_until = null where id = $1", S(userId), Accounts.HashPassword(password));
    }

    public Task UnlockUserAsync(Guid userId) => ExecAsync("update users set failed_logins = 0, locked_until = null where id = $1", S(userId));

    public async Task<bool> EnsureAdminAsync()
    {
        await using var cmd = await CmdAsync("""
            update users set is_admin = 1
            where not exists (select 1 from users where is_admin = 1)
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
        await using (var cmd = await CmdAsync("select id, username, display_name, color, password_hash, disabled, locked_until from users where username = $1", username.Trim().ToLowerInvariant()))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            if (!await r.ReadAsync())
            {
                Accounts.VerifyPassword(password, DummyHash.Value); // same cost as a real check, so timing doesn't reveal which usernames exist
                return new LoginResult(LoginStatus.InvalidCredentials);
            }
            user = ReadUser(r);
            hash = r.GetString(4);
            disabled = r.GetInt64(5) != 0;
            lockedUntil = r.IsDBNull(6) ? null : Time(r, 6);
        }
        if (disabled) return new LoginResult(LoginStatus.Disabled, UserId: user.Id);
        if (lockedUntil > DateTime.UtcNow) return new LoginResult(LoginStatus.LockedOut, LockedUntil: lockedUntil, UserId: user.Id);
        if (Accounts.VerifyPassword(password, hash))
        {
            await ExecAsync("update users set failed_logins = 0, locked_until = null, last_login_at = $2 where id = $1", S(user.Id), Now());
            return new LoginResult(LoginStatus.Success, user, UserId: user.Id);
        }
        var failures = (long)(await ScalarAsync("update users set failed_logins = failed_logins + 1 where id = $1 returning failed_logins", S(user.Id)) ?? 0L);
        if (maxFailures > 0 && failures >= maxFailures)
        {
            var until = DateTime.UtcNow + lockout;
            await ExecAsync("update users set failed_logins = 0, locked_until = $2 where id = $1", S(user.Id), until.ToString("O", CultureInfo.InvariantCulture));
            return new LoginResult(LoginStatus.LockedOut, LockedUntil: until, UserId: user.Id);
        }
        return new LoginResult(LoginStatus.InvalidCredentials, UserId: user.Id);
    }

    internal static readonly Lazy<string> DummyHash = new(() => Accounts.HashPassword(Guid.NewGuid().ToString()));

    // =============================================================== single sign-on

    public async Task<UserAccount?> FindExternalLoginAsync(string issuer, string subject)
    {
        await using var cmd = await CmdAsync("""
            select u.id, u.username, u.display_name, u.color from external_logins e join users u on u.id = e.user_id
            where e.issuer = $1 and e.subject = $2
            """, issuer, subject);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadUser(r) : null;
    }

    public async Task<UserAccount> CreateExternalUserAsync(string username, string displayName, ExternalLogin login)
    {
        var baseName = LoginPolicy.SuggestUsername(username);
        await using var c = await OpenAsync();
        // An immediate (write) transaction: two concurrent first sign-ins of one identity must not create two accounts.
        await using var tx = c.BeginTransaction(deferred: false);
        async Task<object?> Run(string sql, params object?[] args)
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$" + (i + 1), args[i] ?? DBNull.Value);
            return await cmd.ExecuteScalarAsync();
        }
        if (await Run("select user_id from external_logins where issuer = $1 and subject = $2", login.Issuer, login.Subject) is string linked)
        {
            tx.Commit();
            return (await GetUserAsync(Guid.Parse(linked)))!.User;
        }
        var count = (long)(await Run("select count(*) from users") ?? 0L);
        for (var attempt = 1; attempt < 200; attempt++)
        {
            var candidate = attempt == 1 ? baseName : $"{baseName}{attempt}";
            var user = new UserAccount(Guid.NewGuid(), candidate, string.IsNullOrWhiteSpace(displayName) ? candidate : displayName.Trim(), Accounts.Palette[count % Accounts.Palette.Length]);
            var now = Now();
            if (await Run("insert into users(id, username, display_name, password_hash, color, created_at) values ($1,$2,$3,$4,$5,$6) on conflict (username) do nothing returning id",
                    S(user.Id), user.Username, user.DisplayName, LoginPolicy.NoPassword, user.Color, now) is null)
                continue;   // taken
            await Run("insert into external_logins(issuer, subject, provider, user_id, email, created_at) values ($1,$2,$3,$4,$5,$6)",
                login.Issuer, login.Subject, login.Provider, S(user.Id), login.Email, now);
            tx.Commit();
            return user;
        }
        throw new InvalidOperationException("Could not find a free username for this account.");
    }

    public Task LinkExternalLoginAsync(Guid userId, ExternalLogin login) =>
        ExecAsync("insert into external_logins(issuer, subject, provider, user_id, email, created_at) values ($1,$2,$3,$4,$5,$6) on conflict (issuer, subject) do nothing",
            login.Issuer, login.Subject, login.Provider, S(userId), login.Email, Now());

    // =============================================================== organizations & teams

    public async Task<Guid> CreateOrganizationAsync(string name)
    {
        var id = Guid.NewGuid();
        try { await ExecAsync("insert into organizations(id, name, created_at) values ($1,$2,$3)", S(id), RequireName(name), Now()); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException("An organization with that name already exists."); }
        return id;
    }

    public Task DeleteOrganizationAsync(Guid organizationId) => ExecAsync("delete from organizations where id = $1", S(organizationId));

    public async Task<IReadOnlyList<OrganizationInfo>> ListOrganizationsAsync()
    {
        await using var cmd = await CmdAsync("""
            select o.id, o.name, (select count(*) from organization_members m where m.organization_id = o.id),
                   (select count(*) from teams t where t.organization_id = o.id), o.created_at
            from organizations o order by o.name
            """);
        var list = new List<OrganizationInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new OrganizationInfo(Id(r, 0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), Time(r, 4)));
        return list;
    }

    public Task<IReadOnlyList<UserAccount>> OrganizationMembersAsync(Guid organizationId) => UsersAsync("""
        select u.id, u.username, u.display_name, u.color from organization_members m join users u on u.id = m.user_id
        where m.organization_id = $1 order by u.display_name
        """, S(organizationId));

    public Task AddOrganizationMemberAsync(Guid organizationId, Guid userId) =>
        ExecAsync("insert into organization_members(organization_id, user_id) values ($1,$2) on conflict do nothing", S(organizationId), S(userId));

    public Task RemoveOrganizationMemberAsync(Guid organizationId, Guid userId) =>
        ExecAsync("""
            delete from team_members where user_id = $2 and team_id in (select id from teams where organization_id = $1);
            delete from organization_members where organization_id = $1 and user_id = $2;
            """, S(organizationId), S(userId));

    public async Task<Guid> CreateTeamAsync(Guid organizationId, string name)
    {
        var id = Guid.NewGuid();
        try { await ExecAsync("insert into teams(id, organization_id, name, created_at) values ($1,$2,$3,$4)", S(id), S(organizationId), RequireName(name), Now()); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException("That organization already has a team with that name."); }
        return id;
    }

    public Task DeleteTeamAsync(Guid teamId) => ExecAsync("""
        delete from package_permissions where principal_kind = 'team' and principal_id = $1;
        delete from teams where id = $1;
        """, S(teamId));

    public async Task<IReadOnlyList<TeamInfo>> ListTeamsAsync(Guid organizationId)
    {
        await using var cmd = await CmdAsync("""
            select t.id, t.organization_id, t.name, (select count(*) from team_members m where m.team_id = t.id),
                   (select count(*) from team_project_roles g where g.team_id = t.id)
            from teams t where t.organization_id = $1 order by t.name
            """, S(organizationId));
        var list = new List<TeamInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new TeamInfo(Id(r, 0), Id(r, 1), r.GetString(2), r.GetInt32(3), r.GetInt32(4)));
        return list;
    }

    public Task<IReadOnlyList<UserAccount>> TeamMembersAsync(Guid teamId) => UsersAsync("""
        select u.id, u.username, u.display_name, u.color from team_members m join users u on u.id = m.user_id
        where m.team_id = $1 order by u.display_name
        """, S(teamId));

    public Task AddTeamMemberAsync(Guid teamId, Guid userId) =>
        ExecAsync("""
            insert into organization_members(organization_id, user_id) select organization_id, $2 from teams where id = $1 on conflict do nothing;
            insert into team_members(team_id, user_id) values ($1,$2) on conflict do nothing;
            """, S(teamId), S(userId));

    public Task RemoveTeamMemberAsync(Guid teamId, Guid userId) =>
        ExecAsync("delete from team_members where team_id = $1 and user_id = $2", S(teamId), S(userId));

    public async Task<IReadOnlyList<TeamGrant>> TeamGrantsAsync(Guid teamId)
    {
        await using var cmd = await CmdAsync("""
            select p.id, p.name, g.role from team_project_roles g join projects p on p.id = g.project_id where g.team_id = $1 order by p.name
            """, S(teamId));
        var list = new List<TeamGrant>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new TeamGrant(Id(r, 0), r.GetString(1), (ProjectRole)r.GetInt32(2)));
        return list;
    }

    public Task SetTeamGrantAsync(Guid teamId, Guid projectId, ProjectRole role) =>
        ExecAsync("insert into team_project_roles(team_id, project_id, role) values ($1,$2,$3) on conflict (team_id, project_id) do update set role = excluded.role",
            S(teamId), S(projectId), (int)role);

    public Task RemoveTeamGrantAsync(Guid teamId, Guid projectId) =>
        ExecAsync("delete from team_project_roles where team_id = $1 and project_id = $2", S(teamId), S(projectId));

    // =============================================================== all projects

    public async Task<IReadOnlyList<AdminProject>> ListAllProjectsAsync()
    {
        await using var cmd = await CmdAsync("""
            select p.id, p.name,
                   length(cast(p.model as blob)) + coalesce((select sum(length(cast(v.model as blob))) from project_versions v where v.project_id = p.id), 0),
                   (select count(*) from project_versions v where v.project_id = p.id),
                   (select count(*) from project_members m where m.project_id = p.id),
                   p.created_at, p.updated_at, p.revision,
                   o.id, o.username, o.display_name, o.color
            from projects p
            left join users o on o.id = (select m.user_id from project_members m where m.project_id = p.id and m.role = 2 order by m.user_id limit 1)
            order by lower(p.name)
            """);
        var list = new List<AdminProject>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new AdminProject(Id(r, 0), r.GetString(1), r.IsDBNull(8) ? null : ReadUser(r, 8), r.GetInt64(2), r.GetInt32(3), r.GetInt32(4), Time(r, 5), Time(r, 6), r.GetInt64(7)));
        return list;
    }

    public Task TransferProjectAsync(Guid projectId, Guid newOwnerId) =>
        ExecAsync($"""
            update project_members set role = {(int)ProjectRole.Editor} where project_id = $1 and role = {(int)ProjectRole.Owner} and user_id <> $2;
            insert into project_members(project_id, user_id, role) values ($1, $2, {(int)ProjectRole.Owner})
                on conflict (project_id, user_id) do update set role = excluded.role;
            """, S(projectId), S(newOwnerId));

    public async Task<Guid> RestoreProjectAsync(RestoredProject project)
    {
        var id = await ScalarAsync("select 1 from projects where id = $1", S(project.Id)) is null ? project.Id : Guid.NewGuid();
        await using var c = await OpenAsync();
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync();
        async Task Run(string sql, params object?[] args)
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$" + (i + 1), args[i] ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        var now = Now();
        await Run("insert into projects(id, name, model, created_at, updated_at) values ($1,$2,$3,$4,$4)", S(id), project.Name.Trim(), project.ModelJson, now);
        foreach (var m in project.Members)
            await Run("insert into project_members(project_id, user_id, role) select $1, $2, $3 where exists (select 1 from users where id = $2) on conflict do nothing",
                S(id), S(m.UserId), (int)m.Role);
        foreach (var v in project.Versions)
            await Run("insert into project_versions(project_id, number, message, model, author, created_at) values ($1,$2,$3,$4,(select id from users where id = $5),$6)",
                S(id), v.Number, v.Message, v.ModelJson, v.AuthorId is { } a ? S(a) : null, v.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await tx.CommitAsync();
        return id;
    }

    public async Task<IReadOnlyList<VersionSnapshot>> ListVersionSnapshotsAsync(Guid projectId)
    {
        await using var cmd = await CmdAsync("select number, message, model, author, created_at from project_versions where project_id = $1 order by number", S(projectId));
        var list = new List<VersionSnapshot>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new VersionSnapshot(r.GetInt32(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : Id(r, 3), Time(r, 4)));
        return list;
    }

    // =============================================================== audit log

    public Task AppendAuditAsync(AuditEntry e) =>
        ExecAsync("insert into audit_log(at, actor_id, actor_name, action, project_id, project_name, target, details, ip) values ($1,$2,$3,$4,$5,$6,$7,$8,$9)",
            e.At.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), e.ActorId is { } a ? S(a) : null, e.Actor, e.Action,
            e.ProjectId is { } p ? S(p) : null, e.ProjectName, e.Target, e.DetailsJson, e.Ip);

    public async Task<AuditPage> QueryAuditAsync(AuditQuery q)
    {
        var (where, args) = LoginPolicy.AuditFilter(q, i => "$" + i, g => S(g), t => t.ToString("O", CultureInfo.InvariantCulture));
        var total = (long)(await ScalarAsync($"select count(*) from audit_log {where}", args.ToArray()) ?? 0L);
        args.Add(Math.Clamp(q.Take, 1, 10_000));
        args.Add(Math.Max(0, q.Skip));
        await using var cmd = await CmdAsync($"""
            select id, at, actor_id, actor_name, action, project_id, project_name, target, details, ip from audit_log {where}
            order by id desc limit ${args.Count - 1} offset ${args.Count}
            """, args.ToArray());
        var list = new List<AuditEntry>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new AuditEntry(Time(r, 1), r.IsDBNull(2) ? null : Id(r, 2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : Id(r, 5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetInt64(0)));
        return new AuditPage(list, (int)total);
    }

    // =============================================================== helpers

    private async Task<IReadOnlyList<UserAccount>> UsersAsync(string sql, params object?[] args)
    {
        await using var cmd = await CmdAsync(sql, args);
        var list = new List<UserAccount>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(ReadUser(r));
        return list;
    }

    private static string RequireName(string name) =>
        string.IsNullOrWhiteSpace(name) ? throw new InvalidOperationException("Enter a name.") : name.Trim();
}
