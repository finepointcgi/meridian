using Meridian.Data;
using Meridian.Web.Admin;

namespace Meridian.Web.Settings;

/// <summary>
/// The theme of a full page load, rendered into &lt;body&gt; by App.razor before anything is painted: the signed-in
/// user's stored preferences, else (the sign-in page) what this device last used, from the cookies settings.js writes.
/// </summary>
public static class ThemeBootstrap
{
    public static async Task<(bool Dark, string BodyClass, string BodyStyle)> BodyAsync(HttpContext http, IMeridianStore db)
    {
        var device = DevicePreferences.FromCookie(http.Request.Cookies[DevicePreferences.CookieName]);
        var prefs = http.User.Identity?.IsAuthenticated == true && MeridianAuth.UserId(http.User) is { } id
            ? UserPreferences.FromJson(await db.GetUserPreferencesAsync(id))
            : FromCookies(http);
        var dark = prefs.Theme == ThemeMode.Dark || (prefs.Theme == ThemeMode.System && (ThemeService.SystemSchemeIsDark(http) ?? true));
        return (dark, ThemeService.ClassesFor(dark, prefs, device, "m-"), ThemeService.StyleFor(prefs.Accent, dark, device));
    }

    /// <summary>Signed out: the theme and accent this browser last showed.</summary>
    public static UserPreferences FromCookies(HttpContext http) => new UserPreferences
    {
        Theme = http.Request.Cookies["meridian.theme"] switch { "light" => ThemeMode.Light, "system" => ThemeMode.System, _ => ThemeMode.Dark },
        Accent = http.Request.Cookies["meridian.accent"] ?? AccentPresets.Default,
    }.Normalize();
}
