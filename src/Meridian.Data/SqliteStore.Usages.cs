using Meridian.Core.Libraries;
using Microsoft.Data.Sqlite;

namespace Meridian.Data;

/// <summary>Used projects / shared model libraries (SQLite).</summary>
public sealed partial class SqliteStore
{
    // =============================================================== schema (project usages migration block)
    // "create … if not exists" is atomic in SQLite, so processes migrating at the same moment can't race here. Columns added to
    // this table later must use the pragma check + duplicate-column catch of MigrateAdminAsync.
    // used_project_id has no foreign key on purpose: deleting a used project keeps the usage, which then reports the library
    // as unavailable (and references to it as missing) instead of silently dropping it.

    private Task MigrateUsagesAsync() => ExecAsync("""
        create table if not exists project_usages (
            id text primary key,
            project_id text not null references projects(id) on delete cascade,
            used_project_id text not null,
            root_package_id text not null,
            pin_kind text not null check (pin_kind in ('version', 'branch', 'baseline')),
            pin_value text not null,
            mount_point_id text,
            added_by text references users(id) on delete set null,
            added_at text not null,
            unique (project_id, used_project_id, root_package_id));

        create index if not exists project_usages_used on project_usages(used_project_id);
        """);

    private const string UsageColumns = """
        select g.id, g.project_id, g.used_project_id, coalesce(p.name, ''), g.root_package_id, g.pin_kind, g.pin_value, g.mount_point_id,
               g.added_by, coalesce(u.display_name, ''), g.added_at
        from project_usages g left join projects p on p.id = g.used_project_id left join users u on u.id = g.added_by
        """;

    private static ProjectUsage ReadUsage(SqliteDataReader r) => new(Id(r, 0), Id(r, 1), Id(r, 2), r.GetString(3), r.GetString(4),
        LibraryPins.Kind(r.GetString(5)), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : Id(r, 8), r.GetString(9), Time(r, 10));

    private async Task<IReadOnlyList<ProjectUsage>> QueryUsagesAsync(string where, params object?[] args)
    {
        await using var cmd = await CmdAsync(UsageColumns + where + " order by g.added_at, g.id", args);
        var list = new List<ProjectUsage>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(ReadUsage(r));
        return list;
    }

    public Task<IReadOnlyList<ProjectUsage>> ListProjectUsagesAsync(Guid projectId) => QueryUsagesAsync(" where g.project_id = $1", S(projectId));

    public Task<IReadOnlyList<ProjectUsage>> ListProjectUsersAsync(Guid usedProjectId) => QueryUsagesAsync(" where g.used_project_id = $1", S(usedProjectId));

    public async Task<ProjectUsage?> GetProjectUsageAsync(Guid usageId) => (await QueryUsagesAsync(" where g.id = $1", S(usageId))).FirstOrDefault();

    public async Task<Guid> AddProjectUsageAsync(Guid projectId, Guid usedProjectId, string rootPackageId, LibraryPinKind pinKind, string pinValue,
                                                 string? mountPointId, Guid? addedBy)
    {
        ProjectUsageRules.Check(projectId, usedProjectId, rootPackageId, pinValue, mountPointId);
        var id = Guid.NewGuid();
        try
        {
            await ExecAsync("""
                insert into project_usages(id, project_id, used_project_id, root_package_id, pin_kind, pin_value, mount_point_id, added_by, added_at)
                values ($1,$2,$3,$4,$5,$6,$7,$8,$9)
                """, S(id), S(projectId), S(usedProjectId), rootPackageId, LibraryPins.Code(pinKind), pinValue.Trim(),
                string.IsNullOrEmpty(mountPointId) ? null : mountPointId, addedBy is { } a ? S(a) : null, Now());
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This project already uses that package.");
        }
        return id;
    }

    public async Task UpdateProjectUsageAsync(Guid usageId, LibraryPinKind pinKind, string pinValue, string? mountPointId)
    {
        ProjectUsageRules.CheckPin(pinValue);
        await ExecAsync("update project_usages set pin_kind = $2, pin_value = $3, mount_point_id = $4 where id = $1",
            S(usageId), LibraryPins.Code(pinKind), pinValue.Trim(), string.IsNullOrEmpty(mountPointId) ? null : mountPointId);
    }

    public Task RemoveProjectUsageAsync(Guid usageId) => ExecAsync("delete from project_usages where id = $1", S(usageId));
}
