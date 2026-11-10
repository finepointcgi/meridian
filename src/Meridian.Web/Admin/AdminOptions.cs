namespace Meridian.Web.Admin;

/// <summary>One OpenID Connect identity provider (<c>Meridian:Sso:Providers:N</c>): Entra ID / Azure AD, Okta, Google, Keycloak…</summary>
public sealed class SsoProviderOptions
{
    /// <summary>Short id used in URLs and the audit log, e.g. <c>entra</c>. Letters, digits and '-' only.</summary>
    public string Name { get; set; } = "";
    /// <summary>Button text: "Sign in with {DisplayName}".</summary>
    public string? DisplayName { get; set; }
    /// <summary>Issuer / discovery base, e.g. <c>https://login.microsoftonline.com/{tenant}/v2.0</c>.</summary>
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? ClientSecret { get; set; }
    public string[] Scopes { get; set; } = ["openid", "profile", "email"];
    /// <summary>Which ID-token / userinfo claims fill in the Meridian account.</summary>
    public SsoClaimMappings ClaimMappings { get; set; } = new();
    /// <summary>Create a Meridian account on first sign-in (default). When false, an administrator must link accounts first.</summary>
    public bool AutoProvision { get; set; } = true;
    /// <summary>Only for local identity-provider testing over plain HTTP.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Ready-made defaults for a well-known provider: <c>gitlab</c> (also implied by the name <c>gitlab</c>). Explicit settings win.</summary>
    public string? Preset { get; set; }

    public string Scheme => "oidc-" + Name.ToLowerInvariant();
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName!;

    public const string GitLabPreset = "gitlab";
    public const string GitLabDotCom = "https://gitlab.com";

    /// <summary>
    /// Fills in what a preset implies and the configuration left out. GitLab (gitlab.com or self-managed) is a standard
    /// OpenID Connect provider whose issuer is the instance root: Authority defaults to https://gitlab.com, the scopes are
    /// <c>openid profile email</c>, the button says "GitLab", and the username comes from <c>preferred_username</c>, falling
    /// back to GitLab's <c>nickname</c> claim (both carry the GitLab username).
    /// </summary>
    public void ApplyPreset()
    {
        var preset = Preset?.Trim().ToLowerInvariant() is { Length: > 0 } p ? p
            : string.Equals(Name?.Trim(), GitLabPreset, StringComparison.OrdinalIgnoreCase) ? GitLabPreset : null;
        if (preset != GitLabPreset) return;
        Preset = GitLabPreset;
        if (string.IsNullOrWhiteSpace(Name)) Name = GitLabPreset;
        Authority = string.IsNullOrWhiteSpace(Authority) ? GitLabDotCom : Authority.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(DisplayName) || string.Equals(DisplayName, Name, StringComparison.Ordinal)) DisplayName = "GitLab";
        if (Scopes is not { Length: > 0 }) Scopes = ["openid", "profile", "email"];
        if (string.IsNullOrWhiteSpace(ClaimMappings.Username)) ClaimMappings.Username = "preferred_username";
        ClaimMappings.UsernameFallback ??= "nickname";
        if (string.IsNullOrWhiteSpace(ClaimMappings.DisplayName)) ClaimMappings.DisplayName = "name";
    }
}

public sealed class SsoClaimMappings
{
    public string Username { get; set; } = "preferred_username";
    /// <summary>Claim used when <see cref="Username"/> is missing, before falling back to the e-mail (GitLab preset: <c>nickname</c>).</summary>
    public string? UsernameFallback { get; set; }
    public string DisplayName { get; set; } = "name";
    public string Email { get; set; } = "email";
    /// <summary>Optional claim (e.g. <c>groups</c> or <c>roles</c>) and value that make the user a Meridian administrator.</summary>
    public string? AdminClaim { get; set; }
    public string? AdminValue { get; set; }
}

/// <summary><c>Meridian:Sso</c>.</summary>
public sealed class SsoOptions
{
    public List<SsoProviderOptions> Providers { get; set; } = [];
    /// <summary>Turn off username/password sign-in, registration and HTTP Basic API auth: single sign-on only.</summary>
    public bool DisableLocalPasswords { get; set; }

    public SsoProviderOptions? Find(string name) =>
        Providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary><c>Meridian:Security</c>: sign-in hardening.</summary>
public sealed class SecurityOptions
{
    /// <summary>Wrong passwords in a row before an account is locked (0 = never).</summary>
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    /// <summary>Sign-in attempts allowed per client IP per minute.</summary>
    public int LoginAttemptsPerMinute { get; set; } = 10;
    /// <summary>Allow anyone to create an account from the sign-in page.</summary>
    public bool AllowRegistration { get; set; } = true;
    /// <summary>Usernames that are always server administrators.</summary>
    public string[] Admins { get; set; } = [];
    /// <summary>Honour X-Forwarded-For/-Proto from a reverse proxy (needed for SSO redirect URLs behind TLS termination).</summary>
    public bool TrustForwardedHeaders { get; set; }
}

/// <summary><c>Meridian:Backup</c>: scheduled automatic backups.</summary>
public sealed class BackupOptions
{
    /// <summary>Folder for scheduled backups; empty disables the schedule.</summary>
    public string? Folder { get; set; }
    public double IntervalHours { get; set; } = 24;
    /// <summary>How many scheduled backup files to keep.</summary>
    public int Retain { get; set; } = 7;
    public bool IncludeUsers { get; set; } = true;

    public bool Enabled => !string.IsNullOrWhiteSpace(Folder) && IntervalHours > 0;
}

/// <summary>All enterprise settings, bound once at startup.</summary>
public sealed record AdminSettings(SsoOptions Sso, SecurityOptions Security, BackupOptions Backup)
{
    public static AdminSettings From(IConfiguration c)
    {
        var sso = c.GetSection("Meridian:Sso").Get<SsoOptions>() ?? new SsoOptions();
        foreach (var p in sso.Providers) p.ApplyPreset();
        sso.Providers.RemoveAll(p => string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Authority) || string.IsNullOrWhiteSpace(p.ClientId));
        var security = c.GetSection("Meridian:Security").Get<SecurityOptions>() ?? new SecurityOptions();
        // Also accept Meridian:Admins (array or comma-separated string).
        var admins = c.GetSection("Meridian:Admins").Get<string[]>() ?? (c["Meridian:Admins"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        security.Admins = [.. security.Admins.Concat(admins).Select(a => a.Trim().ToLowerInvariant()).Where(a => a.Length > 0).Distinct()];
        var backup = c.GetSection("Meridian:Backup").Get<BackupOptions>() ?? new BackupOptions();
        return new AdminSettings(sso, security, backup);
    }
}
