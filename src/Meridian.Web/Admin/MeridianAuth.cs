using System.Collections.Concurrent;
using System.Security.Claims;
using Meridian.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Meridian.Web.Admin;

/// <summary>The Meridian session cookie's identity, shared by password and single sign-on.</summary>
public static class MeridianAuth
{
    public static ClaimsPrincipal Principal(UserAccount user, string? method = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new("display_name", user.DisplayName),
            new("color", user.Color),
        };
        if (method != null) claims.Add(new Claim("amr_meridian", method));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static Task SignInAsync(HttpContext http, UserAccount user, string? method = null) =>
        http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, Principal(user, method), new AuthenticationProperties { IsPersistent = true });

    public static Guid? UserId(ClaimsPrincipal p) => Guid.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static UserAccount? User(ClaimsPrincipal p) => UserId(p) is { } id
        ? new UserAccount(id, p.Identity?.Name ?? "", p.FindFirstValue("display_name") ?? p.Identity?.Name ?? "", p.FindFirstValue("color") ?? "#4f8cff")
        : null;

    public static string? Ip(HttpContext? http) => http?.Connection.RemoteIpAddress?.ToString();
}

/// <summary>
/// Live server-role and account-state checks. Admin rights and disabled accounts take effect immediately (no re-login),
/// with a few seconds of caching so every request doesn't hit the database.
/// </summary>
public sealed class AdminAccess(IMeridianStore db, MeridianHostInfo host)
{
    private readonly ConcurrentDictionary<Guid, (AdminUser? User, DateTime At)> _cache = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    public async Task<AdminUser?> StateAsync(Guid userId, bool fresh = false)
    {
        if (!fresh && _cache.TryGetValue(userId, out var c) && DateTime.UtcNow - c.At < Ttl) return c.User;
        var u = await db.GetUserAsync(userId);
        _cache[userId] = (u, DateTime.UtcNow);
        return u;
    }

    public void Invalidate(Guid userId) => _cache.TryRemove(userId, out _);

    /// <summary>Server administrators only exist on team servers; the desktop app's local mode has no admin console.</summary>
    public async Task<bool> IsAdminAsync(ClaimsPrincipal principal)
    {
        if (host.LocalMode || principal.Identity?.IsAuthenticated != true || MeridianAuth.UserId(principal) is not { } id) return false;
        return await StateAsync(id) is { IsAdmin: true, Disabled: false };
    }
}
