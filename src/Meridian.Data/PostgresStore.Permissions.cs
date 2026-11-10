using Meridian.Core.Permissions;
using Npgsql;

namespace Meridian.Data;

/// <summary>Per-package permissions (PostgreSQL).</summary>
public sealed partial class PostgresStore
{
    // =============================================================== schema (package permissions migration block)
    // Runs inside MigrateAsync's transaction under the schema advisory lock, so concurrent server starts are serialised.

    private static async Task MigratePermissionsAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand("""
            create table if not exists package_permissions (
                project_id uuid not null references projects(id) on delete cascade,
                element_id text not null,
                principal_kind text not null check (principal_kind in ('user', 'team')),
                principal_id uuid not null,
                level text not null check (level in ('none', 'read', 'edit', 'manage')),
                updated_by uuid references users(id) on delete set null,
                updated_at timestamptz not null default now(),
                primary key (project_id, element_id, principal_kind, principal_id));

            create index if not exists package_permissions_principal on package_permissions(principal_id);
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<PackagePermission>> ListPackagePermissionsAsync(Guid projectId)
    {
        await using var cmd = IxCmd("""
            select p.element_id, p.principal_kind, p.principal_id, p.level, coalesce(u.display_name, t.name, ''), p.updated_at
            from package_permissions p
            left join users u on p.principal_kind = 'user' and u.id = p.principal_id
            left join teams t on p.principal_kind = 'team' and t.id = p.principal_id
            where p.project_id = $1 order by p.element_id, p.principal_kind, 5
            """, projectId);
        var list = new List<PackagePermission>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new PackagePermission(projectId, r.GetString(0), PackagePermissionCodes.Kind(r.GetString(1)), r.GetGuid(2),
                PackagePermissionCodes.Level(r.GetString(3)), r.GetString(4), DateTime.SpecifyKind(r.GetDateTime(5), DateTimeKind.Utc)));
        return list;
    }

    public Task SetPackagePermissionAsync(Guid projectId, string elementId, PrincipalKind kind, Guid principalId, PackageAccess level, Guid? updatedBy) =>
        IxExecAsync("""
            insert into package_permissions(project_id, element_id, principal_kind, principal_id, level, updated_by)
            values ($1,$2,$3,$4,$5,$6)
            on conflict (project_id, element_id, principal_kind, principal_id)
            do update set level = excluded.level, updated_by = excluded.updated_by, updated_at = now()
            """, projectId, PackagePermissionCodes.CheckElementId(elementId), PackagePermissionCodes.Kind(kind), principalId,
            PackagePermissionCodes.Level(level), updatedBy);

    public Task RemovePackagePermissionAsync(Guid projectId, string elementId, PrincipalKind kind, Guid principalId) =>
        IxExecAsync("delete from package_permissions where project_id = $1 and element_id = $2 and principal_kind = $3 and principal_id = $4",
            projectId, elementId, PackagePermissionCodes.Kind(kind), principalId);

    public async Task<IReadOnlyList<Guid>> UserTeamIdsAsync(Guid userId)
    {
        await using var cmd = IxCmd("select team_id from team_members where user_id = $1", userId);
        var list = new List<Guid>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetGuid(0));
        return list;
    }

    public async Task<IReadOnlyList<ProjectTeam>> ProjectTeamsAsync(Guid projectId)
    {
        await using var cmd = IxCmd("""
            select t.id, t.name, g.role from team_project_roles g join teams t on t.id = g.team_id where g.project_id = $1 order by t.name
            """, projectId);
        var list = new List<ProjectTeam>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new ProjectTeam(r.GetGuid(0), r.GetString(1), (ProjectRole)r.GetInt16(2)));
        return list;
    }
}
