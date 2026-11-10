using System.Text.Json;
using Meridian.Data;

namespace Meridian.Web.Admin;

/// <summary>
/// Records who did what, when, to which project. Entries go to the append-only <c>audit_log</c> table; recording
/// never throws, so an audit failure is logged but cannot break the user's action.
/// </summary>
public interface IAuditLog
{
    Task RecordAsync(string action, UserAccount? actor, Guid? projectId = null, string? projectName = null,
        string? target = null, object? details = null, string? ip = null, string? actorName = null);
}

/// <summary>Action names used across the app (prefix-filterable: <c>login.</c>, <c>user.</c>, <c>project.</c>…).</summary>
public static class AuditActions
{
    public const string LoginSuccess = "login.success", LoginFailure = "login.failure", LoginLocked = "login.locked", LoginDisabled = "login.disabled",
        LoginRateLimited = "login.rate-limited", SsoLogin = "sso.login", SsoProvision = "sso.provision", SsoDenied = "sso.denied";
    public const string UserRegister = "user.register", UserCreate = "user.create", UserDisable = "user.disable", UserEnable = "user.enable",
        UserPasswordReset = "user.password-reset", UserUnlock = "user.unlock", UserAdminGrant = "user.admin-grant", UserAdminRevoke = "user.admin-revoke";
    public const string OrgCreate = "org.create", OrgDelete = "org.delete", OrgMemberAdd = "org.member-add", OrgMemberRemove = "org.member-remove",
        TeamCreate = "team.create", TeamDelete = "team.delete", TeamMemberAdd = "team.member-add", TeamMemberRemove = "team.member-remove",
        TeamGrantSet = "team.grant-set", TeamGrantRemove = "team.grant-remove";
    public const string ProjectCreate = "project.create", ProjectDelete = "project.delete", ProjectRename = "project.rename", ProjectTransfer = "project.transfer",
        ProjectExport = "project.export", MemberSet = "project.member-set", MemberRemove = "project.member-remove",
        PermissionSet = "project.permission-set", PermissionRemove = "project.permission-remove";
    public const string VersionCommit = "version.commit", VersionRestore = "version.restore";
    public const string BackupDownload = "backup.download", BackupScheduled = "backup.scheduled", BackupRestore = "backup.restore", AuditExport = "audit.export";

    public static readonly string[] Prefixes = ["login.", "sso.", "user.", "org.", "team.", "project.", "version.", "backup.", "audit."];
}

public sealed class AuditLog(IMeridianStore db, ILogger<AuditLog> log) : IAuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RecordAsync(string action, UserAccount? actor, Guid? projectId = null, string? projectName = null,
        string? target = null, object? details = null, string? ip = null, string? actorName = null)
    {
        var name = actorName ?? (actor is null ? "system" : actor.Username);
        try
        {
            var json = details switch { null => null, string s => JsonSerializer.Serialize(new { note = s }, Json), _ => JsonSerializer.Serialize(details, Json) };
            await db.AppendAuditAsync(new AuditEntry(DateTime.UtcNow, actor?.Id is { } id && id != Guid.Empty ? id : null, name, action, projectId, projectName, target, json, ip));
            log.LogInformation("audit {Action} by {Actor} project={Project} target={Target}", action, name, projectName ?? projectId?.ToString(), target);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not record audit entry {Action} by {Actor}", action, name);
        }
    }

    /// <summary>RFC 4180 CSV of audit entries (newest first).</summary>
    public static string ToCsv(IEnumerable<AuditEntry> entries)
    {
        static string F(string? v)
        {
            v ??= "";
            // Neutralise spreadsheet formula injection, then quote.
            if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
            return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }
        var sb = new System.Text.StringBuilder("id,time_utc,actor,actor_id,action,project,project_id,target,details,ip\r\n");
        foreach (var e in entries)
            sb.Append(string.Join(",", F(e.Id.ToString()), F(e.At.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")), F(e.Actor), F(e.ActorId?.ToString()),
                F(e.Action), F(e.ProjectName), F(e.ProjectId?.ToString()), F(e.Target), F(e.DetailsJson), F(e.Ip))).Append("\r\n");
        return sb.ToString();
    }
}
