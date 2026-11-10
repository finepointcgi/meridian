using System.Security.Claims;
using Meridian.Data;

namespace Meridian.Web.Admin;

/// <summary>
/// Maps an OpenID Connect sign-in to a Meridian account: existing links (issuer + subject) sign straight in; a first
/// sign-in provisions a new account and records the link. Accounts are never matched by username or e-mail, so an
/// identity provider cannot take over an existing local account.
/// </summary>
public sealed class SsoProvisioner(IMeridianStore db, IAuditLog audit, AdminSettings settings, AdminAccess access)
{
    public sealed record Outcome(UserAccount? User, string? Error, bool Provisioned = false);

    public async Task<Outcome> ResolveAsync(SsoProviderOptions provider, ClaimsPrincipal external, string? ip)
    {
        var subject = external.FindFirstValue("sub") ?? external.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(subject)) return new(null, "The identity provider did not return a subject (sub) claim.");
        var issuer = external.FindFirstValue("iss") ?? external.FindFirst("sub")?.Issuer ?? provider.Authority;
        var m = provider.ClaimMappings;
        var email = Claim(external, m.Email) ?? Claim(external, "email");
        var login = new ExternalLogin(provider.Name, issuer, subject, email);

        var user = await db.FindExternalLoginAsync(issuer, subject);
        var provisioned = false;
        if (user is null)
        {
            if (!provider.AutoProvision)
            {
                await audit.RecordAsync(AuditActions.SsoDenied, null, target: email ?? subject, details: new { provider = provider.Name, issuer, reason = "auto-provisioning is off" }, ip: ip, actorName: email ?? subject);
                return new(null, "Your account has not been set up in Meridian yet. Ask an administrator for access.");
            }
            var username = Claim(external, m.Username) ?? Claim(external, m.UsernameFallback) ?? email ?? Claim(external, "preferred_username") ?? subject;
            var display = Claim(external, m.DisplayName) ?? Claim(external, "name")
                ?? string.Join(' ', new[] { Claim(external, "given_name"), Claim(external, "family_name") }.Where(s => !string.IsNullOrWhiteSpace(s)));
            user = await db.CreateExternalUserAsync(username, display, login);
            provisioned = true;
            await db.EnsureAdminAsync();
            await audit.RecordAsync(AuditActions.SsoProvision, user, target: user.Username, details: new { provider = provider.Name, issuer, subject, email }, ip: ip);
        }

        var state = await db.GetUserAsync(user.Id);
        if (state is { Disabled: true })
        {
            await audit.RecordAsync(AuditActions.SsoDenied, user, target: user.Username, details: new { provider = provider.Name, reason = "account disabled" }, ip: ip);
            return new(null, "This account has been disabled. Contact your administrator.");
        }

        var makeAdmin = settings.Security.Admins.Contains(user.Username)
            || (m.AdminClaim is { Length: > 0 } claim && m.AdminValue is { Length: > 0 } value
                && external.FindAll(claim).Any(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase)));
        if (makeAdmin && state is { IsAdmin: false })
        {
            await db.SetUserAdminAsync(user.Id, true);
            access.Invalidate(user.Id);
            await audit.RecordAsync(AuditActions.UserAdminGrant, null, target: user.Username, details: new { reason = "configured administrator", provider = provider.Name }, actorName: "system");
        }
        await audit.RecordAsync(AuditActions.SsoLogin, user, target: user.Username, details: new { provider = provider.Name }, ip: ip);
        return new(user, null, provisioned);
    }

    private static string? Claim(ClaimsPrincipal p, string? type) =>
        string.IsNullOrEmpty(type) ? null : p.FindFirstValue(type) is { Length: > 0 } v ? v : null;
}
