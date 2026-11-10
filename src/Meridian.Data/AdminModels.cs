namespace Meridian.Data;

/// <summary>An account as the administration console sees it: server role, state, lockout and linked SSO providers.</summary>
public sealed record AdminUser(
    UserAccount User, bool IsAdmin, bool Disabled, bool HasPassword, DateTime CreatedAt, DateTime? LastLoginAt,
    DateTime? LockedUntil, int FailedLogins, IReadOnlyList<string> SsoProviders)
{
    public bool IsLockedOut(DateTime utcNow) => LockedUntil is { } until && until > utcNow;
}

public enum LoginStatus { Success, InvalidCredentials, Disabled, LockedOut }

public sealed record LoginResult(LoginStatus Status, UserAccount? User = null, DateTime? LockedUntil = null, Guid? UserId = null);

/// <summary>An identity at an OpenID Connect provider. Issuer + subject is the stable key; e-mail is informational.</summary>
public sealed record ExternalLogin(string Provider, string Issuer, string Subject, string? Email);

public sealed record OrganizationInfo(Guid Id, string Name, int MemberCount, int TeamCount, DateTime CreatedAt);

public sealed record TeamInfo(Guid Id, Guid OrganizationId, string Name, int MemberCount, int ProjectCount);

public sealed record TeamGrant(Guid ProjectId, string ProjectName, ProjectRole Role);

public sealed record AdminProject(
    Guid Id, string Name, UserAccount? Owner, long SizeBytes, int VersionCount, int MemberCount,
    DateTime CreatedAt, DateTime UpdatedAt, long Revision);

/// <summary>A full version snapshot (used by backups and restore).</summary>
public sealed record VersionSnapshot(int Number, string Message, string ModelJson, Guid? AuthorId, DateTime CreatedAt);

public sealed record RestoredMember(Guid UserId, ProjectRole Role);

public sealed record RestoredProject(Guid Id, string Name, string ModelJson, IReadOnlyList<VersionSnapshot> Versions, IReadOnlyList<RestoredMember> Members);

/// <summary>One append-only audit record. <see cref="DetailsJson"/> is a JSON object (or null).</summary>
public sealed record AuditEntry(
    DateTime At, Guid? ActorId, string Actor, string Action, Guid? ProjectId = null, string? ProjectName = null,
    string? Target = null, string? DetailsJson = null, string? Ip = null, long Id = 0);

public sealed record AuditQuery(
    string? Action = null, string? Actor = null, Guid? ProjectId = null, string? Text = null,
    DateTime? From = null, DateTime? To = null, int Skip = 0, int Take = 50);

public sealed record AuditPage(IReadOnlyList<AuditEntry> Items, int Total);

/// <summary>Account-lockout policy used by <see cref="IMeridianStore.VerifyUserAsync"/> (HTTP Basic API and older callers).</summary>
public static class LoginPolicy
{
    public static int MaxFailures { get; set; } = 5;
    public static TimeSpan Lockout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Marker stored instead of a password hash for single-sign-on-only accounts; never verifies.</summary>
    public const string NoPassword = "sso-only";

    /// <summary>Turns an arbitrary identity-provider name into a valid Meridian username (2–40 of letters, digits, '.', '_', '-').</summary>
    public static string SuggestUsername(string? raw)
    {
        raw ??= "";
        var at = raw.IndexOf('@');
        if (at > 0) raw = raw[..at];
        var chars = raw.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) && c < 128 || c is '.' or '_' or '-' ? c : '-').ToArray();
        var s = new string(chars).Trim('-', '.');
        while (s.Contains("--")) s = s.Replace("--", "-");
        if (s.Length > 36) s = s[..36];
        return s.Length < 2 ? "user" : s;
    }

    /// <summary>Builds audit filter SQL shared by both stores (parameters are named by the caller's prefix).</summary>
    internal static (string Where, List<object> Args) AuditFilter(AuditQuery q, Func<int, string> param, Func<Guid, object> id, Func<DateTime, object> time, string detailsText = "details")
    {
        var where = new List<string>();
        var args = new List<object>();
        string P(object v) { args.Add(v); return param(args.Count); }
        if (!string.IsNullOrWhiteSpace(q.Action)) where.Add($"action like {P(q.Action.Trim() + "%")}");
        if (!string.IsNullOrWhiteSpace(q.Actor)) where.Add($"lower(actor_name) like {P("%" + q.Actor.Trim().ToLowerInvariant() + "%")}");
        if (q.ProjectId is { } pid) where.Add($"project_id = {P(id(pid))}");
        if (q.From is { } from) where.Add($"at >= {P(time(from.ToUniversalTime()))}");
        if (q.To is { } to) where.Add($"at < {P(time(to.ToUniversalTime()))}");
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var t = P("%" + q.Text.Trim().ToLowerInvariant() + "%");
            where.Add($"(lower(coalesce(target, '')) like {t} or lower(coalesce(project_name, '')) like {t} or lower(coalesce({detailsText}, '')) like {t} or lower(action) like {t})");
        }
        return (where.Count == 0 ? "" : "where " + string.Join(" and ", where), args);
    }
}
