using System.Collections.Concurrent;
using Meridian.Data;
using Meridian.Web.Admin;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Meridian.Web.Settings;

/// <summary>Registers the Settings page's services and the session checks behind "sign out everywhere" and profile edits.</summary>
public static class SettingsHosting
{
    public static IServiceCollection AddMeridianSettings(this IServiceCollection services, MeridianHostInfo host)
    {
        services.AddScoped<ThemeService>();
        services.AddScoped<AccountSettings>();
        services.AddSingleton<SessionRevocations>();

        // A session issued before "sign out everywhere" stops working on its next request; a changed display name or
        // colour is written into the session cookie so the next page load (and collaborators) see it.
        services.PostConfigure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, o =>
        {
            var previous = o.Events.OnValidatePrincipal;
            o.Events.OnValidatePrincipal = async ctx =>
            {
                await previous(ctx);
                if (ctx.Principal is null || MeridianAuth.UserId(ctx.Principal) is not { } id) return;
                var rs = ctx.HttpContext.RequestServices;
                if (!host.LocalMode && await rs.GetRequiredService<SessionRevocations>().RevokedAtAsync(id) is { } revoked
                    && (ctx.Properties.IssuedUtc is not { } issued || issued.UtcDateTime < revoked.AddSeconds(-1)))
                {
                    ctx.RejectPrincipal();
                    await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    return;
                }
                if (await rs.GetRequiredService<AdminAccess>().StateAsync(id) is { } state && MeridianAuth.User(ctx.Principal) is { } claimed
                    && (claimed.DisplayName != state.User.DisplayName || claimed.Color != state.User.Color))
                {
                    ctx.ReplacePrincipal(MeridianAuth.Principal(state.User, ctx.Principal.FindFirst("amr_meridian")?.Value));
                    ctx.ShouldRenew = true;
                }
            };
        });
        return services;
    }
}

/// <summary>"Sign out everywhere" timestamps, cached for a few seconds so every request doesn't hit the database.</summary>
public sealed class SessionRevocations(IMeridianStore db)
{
    private readonly ConcurrentDictionary<Guid, (DateTime? At, DateTime Read)> _cache = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    public async Task<DateTime?> RevokedAtAsync(Guid userId)
    {
        if (_cache.TryGetValue(userId, out var c) && DateTime.UtcNow - c.Read < Ttl) return c.At;
        var at = await db.UserSessionsRevokedAtAsync(userId);
        _cache[userId] = (at, DateTime.UtcNow);
        return at;
    }

    public async Task RevokeAsync(Guid userId)
    {
        await db.RevokeUserSessionsAsync(userId);
        _cache.TryRemove(userId, out _);
    }
}

/// <summary>Account actions behind Settings ▸ Account (team servers): profile, password, sessions.</summary>
public sealed class AccountSettings(IMeridianStore db, AdminAccess access, SessionRevocations sessions, IAuditLog audit, AdminSettings admin)
{
    public bool PasswordsDisabled => admin.Sso.DisableLocalPasswords;

    public async Task<UserAccount> UpdateProfileAsync(UserAccount me, string displayName, string color)
    {
        var updated = await db.UpdateUserProfileAsync(me.Id, displayName, color);
        access.Invalidate(me.Id);
        return updated;
    }

    /// <summary>Changes the password after checking the current one. Returns null on success, else a message.</summary>
    public async Task<string?> ChangePasswordAsync(UserAccount me, string current, string next, string repeat)
    {
        if (PasswordsDisabled) return "Password sign-in is disabled on this server.";
        if (next != repeat) return "The new passwords don't match.";
        try { Accounts.CheckPassword(next); }
        catch (InvalidOperationException e) { return e.Message; }
        if (await db.VerifyUserAsync(me.Username, current) is null) return "The current password is wrong.";
        await db.SetPasswordAsync(me.Id, next);
        await audit.RecordAsync(AuditActions.UserPasswordReset, me, target: me.Username, details: new { self = true });
        return null;
    }

    public Task SignOutEverywhereAsync(UserAccount me) => sessions.RevokeAsync(me.Id);
}
