using Meridian.Core.Libraries;
using Npgsql;

namespace Meridian.Data;

/// <summary>Used projects / shared model libraries (PostgreSQL).</summary>
public sealed partial class PostgresStore
{
    // =============================================================== schema (project usages migration block)
    // Runs inside MigrateAsync's transaction under the schema advisory lock, so concurrent server starts are serialised.
    // used_project_id has no foreign key on purpose: deleting a used project keeps the usage (reported as unavailable).

    private static async Task MigrateUsagesAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand("""
            create table if not exists project_usages (
                id uuid primary key,
                project_id uuid not null references projects(id) on delete cascade,
                used_project_id uuid not null,
                root_package_id text not null,
                pin_kind text not null check (pin_kind in ('version', 'branch', 'baseline')),
                pin_value text not null,
                mount_point_id text,
                added_by uuid references users(id) on delete set null,
                added_at timestamptz not null default now(),
                unique (project_id, used_project_id, root_package_id));

            create index if not exists project_usages_used on project_usages(used_project_id);
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private const string UsageColumns = """
        select g.id, g.project_id, g.used_project_id, coalesce(p.name, ''), g.root_package_id, g.pin_kind, g.pin_value, g.mount_point_id,
               g.added_by, coalesce(u.display_name, ''), g.added_at
        from project_usages g left join projects p on p.id = g.used_project_id left join users u on u.id = g.added_by
        """;

    private async Task<IReadOnlyList<ProjectUsage>> QueryUsagesAsync(string where, params object?[] args)
    {
        await using var cmd = IxCmd(UsageColumns + where + " order by g.added_at, g.id", args);
        var list = new List<ProjectUsage>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new ProjectUsage(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetString(4), LibraryPins.Kind(r.GetString(5)),
                r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetGuid(8), r.GetString(9),
                DateTime.SpecifyKind(r.GetDateTime(10), DateTimeKind.Utc)));
        return list;
    }

    public Task<IReadOnlyList<ProjectUsage>> ListProjectUsagesAsync(Guid projectId) => QueryUsagesAsync(" where g.project_id = $1", projectId);

    public Task<IReadOnlyList<ProjectUsage>> ListProjectUsersAsync(Guid usedProjectId) => QueryUsagesAsync(" where g.used_project_id = $1", usedProjectId);

    public async Task<ProjectUsage?> GetProjectUsageAsync(Guid usageId) => (await QueryUsagesAsync(" where g.id = $1", usageId)).FirstOrDefault();

    public async Task<Guid> AddProjectUsageAsync(Guid projectId, Guid usedProjectId, string rootPackageId, LibraryPinKind pinKind, string pinValue,
                                                 string? mountPointId, Guid? addedBy)
    {
        ProjectUsageRules.Check(projectId, usedProjectId, rootPackageId, pinValue, mountPointId);
        var id = Guid.NewGuid();
        try
        {
            await IxExecAsync("""
                insert into project_usages(id, project_id, used_project_id, root_package_id, pin_kind, pin_value, mount_point_id, added_by)
                values ($1,$2,$3,$4,$5,$6,$7,$8)
                """, id, projectId, usedProjectId, rootPackageId, LibraryPins.Code(pinKind), pinValue.Trim(),
                string.IsNullOrEmpty(mountPointId) ? null : mountPointId, addedBy);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException("This project already uses that package.");
        }
        return id;
    }

    public async Task UpdateProjectUsageAsync(Guid usageId, LibraryPinKind pinKind, string pinValue, string? mountPointId)
    {
        ProjectUsageRules.CheckPin(pinValue);
        await IxExecAsync("update project_usages set pin_kind = $2, pin_value = $3, mount_point_id = $4 where id = $1",
            usageId, LibraryPins.Code(pinKind), pinValue.Trim(), string.IsNullOrEmpty(mountPointId) ? null : mountPointId);
    }

    public Task RemoveProjectUsageAsync(Guid usageId) => IxExecAsync("delete from project_usages where id = $1", usageId);
}
