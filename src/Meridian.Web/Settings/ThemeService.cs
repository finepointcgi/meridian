using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MudBlazor;

namespace Meridian.Web.Settings;

/// <summary>
/// The one source of truth for the signed-in user's preferences and the theme derived from them (per circuit). The
/// workbench, admin console, mobile shell and dialogs read their CSS classes and accent tokens here, MainLayout reads
/// the MudBlazor theme, and the diagram canvas and exports read <see cref="SvgTheme"/>. Per-user preferences are saved in
/// the store (they follow the user across devices); density and font size are per device (a cookie); "Match system"
/// follows the browser's prefers-color-scheme live.
/// </summary>
public sealed class ThemeService
{
    private readonly IMeridianStore _db;
    private readonly Workspace _ws;
    private readonly IServiceProvider _services;
    private Guid? _userId;
    private bool _favoritesHooked;

    public ThemeService(IMeridianStore db, Workspace ws, IServiceProvider services)
    {
        _db = db;
        _ws = ws;
        _services = services;
        Sync();
    }

    public UserPreferences Prefs { get; private set; } = new();
    public DevicePreferences Device { get; private set; } = new();
    /// <summary>What the browser reports for prefers-color-scheme (dark until it says otherwise).</summary>
    public bool SystemDark { get; private set; } = true;
    public bool Loaded { get; private set; }
    public Guid? UserId => _userId;

    /// <summary>Raised after any change that affects rendering.</summary>
    public event Action? Changed;

    // ================================================================ derived theme

    public bool IsDark => Prefs.Theme == ThemeMode.Dark || (Prefs.Theme == ThemeMode.System && SystemDark);

    /// <summary>The diagram canvas (and default export background) is dark.</summary>
    public bool CanvasDark => IsDark && !Prefs.CanvasAlwaysLight;

    public SvgTheme SvgTheme => CanvasDark ? SvgTheme.Dark : SvgTheme.Light;

    public ExportBackground ExportBackground => CanvasDark ? ExportBackground.Dark : ExportBackground.Light;

    /// <summary>Classes for an <c>.app</c> root: "dark"/"light" plus density, grid, canvas and motion modifiers.</summary>
    public string AppClass => ClassesFor(IsDark, Prefs, Device, prefix: "");

    /// <summary>The same modifiers for &lt;body&gt; (dialogs and popovers live outside <c>.app</c>).</summary>
    public string BodyClass => ClassesFor(IsDark, Prefs, Device, prefix: "m-");

    /// <summary>Accent tokens and UI scale as inline CSS custom properties.</summary>
    public string AppStyle => StyleFor(Prefs.Accent, IsDark, Device);

    public MudTheme MudTheme => MudThemeFor(Prefs.Accent);

    public static string ClassesFor(bool dark, UserPreferences p, DevicePreferences d, string prefix)
    {
        var c = new List<string> { prefix == "" ? (dark ? "dark" : "light") : prefix + "theme-" + (dark ? "dark" : "light") };
        if (d.Density == Density.Compact) c.Add(prefix + "compact");
        if (d.FontScale != FontScale.Normal) c.Add(prefix + "font-" + d.FontScale.ToString().ToLowerInvariant());
        if (!p.ShowGrid) c.Add(prefix + "grid-off");
        else if (p.GridStyle == GridStyle.Lines) c.Add(prefix + "grid-lines");
        if (dark && p.CanvasAlwaysLight) c.Add(prefix + "canvas-light");
        if (p.ReducedMotion) c.Add(prefix + "reduce-motion");
        // Automatic adds nothing: settings.js marks <html> "software-render" when the browser has no GPU (app.css).
        if (d.Effects == VisualEffects.Reduced) c.Add(prefix + "reduce-effects");
        else if (d.Effects == VisualEffects.Full) c.Add(prefix + "full-effects");
        return string.Join(' ', c);
    }

    /// <summary>The panel colours accent text must stay readable on (WCAG AA, 4.5:1).</summary>
    public const string LightPanel = "#ffffff", DarkPanel = "#131b2b";

    public sealed record AccentTokens(string Accent, string Text, string Strong, string StrongEnd, string OnStrong, string Rgb);

    /// <summary>
    /// Derives the accent tokens from the chosen colour: <c>Text</c> is the accent nudged until it reads at 4.5:1 on the
    /// theme's panels, <c>Strong</c> is a fill that white button text reads on at 4.5:1.
    /// </summary>
    public static AccentTokens TokensFor(string accent, bool dark)
    {
        accent = AccentPresets.Normalize(accent);
        var text = ColorMath.EnsureContrast(accent, dark ? DarkPanel : LightPanel);
        var strong = ColorMath.EnsureContrast(accent, "#ffffff");
        var end = ColorMath.Mix(ColorMath.Parse("#000000"), ColorMath.Parse(strong), 0.14).Hex;
        return new(accent, text, strong, end, "#ffffff", ColorMath.Channels(accent));
    }

    public static string StyleFor(string accent, bool dark, DevicePreferences d)
    {
        var t = TokensFor(accent, dark);
        return string.Create(CultureInfo.InvariantCulture,
            $"--accent: {t.Text}; --accent-raw: {t.Accent}; --accent-rgb: {t.Rgb}; --accent-strong: {t.Strong}; " +
            $"--accent-grad: linear-gradient(135deg, {t.Strong} 0%, {t.StrongEnd} 100%); " +
            $"--sel: rgba({t.Rgb}, {(dark ? ".16" : ".12")}); --sel-border: rgba({t.Rgb}, .55); --ui-scale: {d.Scale:0.##};");
    }

    private static readonly ConcurrentDictionary<string, MudTheme> MudThemes = new();

    /// <summary>The MudBlazor theme with the accent as primary (cached per accent).</summary>
    public static MudTheme MudThemeFor(string accent) => MudThemes.GetOrAdd(AccentPresets.Normalize(accent), a =>
    {
        var light = TokensFor(a, dark: false);
        var darkPrimary = TokensFor(a, dark: true).Text;
        var darkOnPrimary = ColorMath.Contrast("#ffffff", darkPrimary) >= 4.5 ? "#ffffff" : "#0a0f19";
        return Themes.Create(light.Strong, "#ffffff", darkPrimary, darkOnPrimary);
    });

    // ================================================================ loading

    public sealed record Snapshot(Guid? UserId, string PrefsJson, string DeviceCookie, bool SystemDark);

    public Snapshot TakeSnapshot() => new(_userId, Prefs.ToJson(), Device.ToCookie(), SystemDark);

    /// <summary>Restores the state the prerender loaded (no database round trip, no theme flash).</summary>
    public void Restore(Snapshot s)
    {
        _userId = s.UserId;
        Prefs = UserPreferences.FromJson(s.PrefsJson);
        Device = DevicePreferences.FromCookie(s.DeviceCookie);
        SystemDark = s.SystemDark;
        Loaded = true;
        Sync();
    }

    /// <summary>Loads the signed-in user's preferences (and, while prerendering, this device's cookies). Idempotent.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (Loaded) return;
        Loaded = true;
        if (_services.GetService(typeof(Microsoft.AspNetCore.Http.IHttpContextAccessor)) is Microsoft.AspNetCore.Http.IHttpContextAccessor { HttpContext: { } http })
        {
            Device = DevicePreferences.FromCookie(http.Request.Cookies[DevicePreferences.CookieName]);
            SystemDark = SystemSchemeIsDark(http) ?? true;
        }
        if (_services.GetService(typeof(AuthenticationStateProvider)) is AuthenticationStateProvider auth)
        {
            try
            {
                var user = (await auth.GetAuthenticationStateAsync()).User;
                if (Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) await LoadForAsync(id);
            }
            catch (InvalidOperationException) { /* no authentication in this context (tests) */ }
        }
        Sync();
    }

    /// <summary>Loads a specific user's preferences from the store.</summary>
    public async Task LoadForAsync(Guid userId)
    {
        _userId = userId;
        Prefs = UserPreferences.FromJson(await _db.GetUserPreferencesAsync(userId));
        Loaded = true;
        Sync();
    }

    /// <summary>prefers-color-scheme as the browser told us: the client hint header, else the cookie our script writes.</summary>
    public static bool? SystemSchemeIsDark(Microsoft.AspNetCore.Http.HttpContext http)
    {
        var hint = http.Request.Headers["Sec-CH-Prefers-Color-Scheme"].ToString().Trim('"');
        if (hint is "dark" or "light") return hint == "dark";
        return http.Request.Cookies["meridian.scheme"] switch { "dark" => true, "light" => false, _ => null };
    }

    // ================================================================ changes

    /// <summary>Changes per-user preferences: applied at once (live), then saved for this user.</summary>
    public async Task UpdateAsync(Action<UserPreferences> change)
    {
        change(Prefs);
        Prefs.Normalize();
        Sync();
        Raise();
        await SaveAsync();
    }

    /// <summary>The title-bar sun/moon: flips between light and dark (leaving "Match system" for an explicit theme).</summary>
    public Task ToggleDarkAsync() => UpdateAsync(p => p.Theme = IsDark ? ThemeMode.Light : ThemeMode.Dark);

    public void SetDevice(DevicePreferences device)
    {
        Device = device;
        Raise();
    }

    /// <summary>The browser's colour scheme changed (or was first reported).</summary>
    public void SetSystemDark(bool dark)
    {
        if (SystemDark == dark) return;
        var before = IsDark;
        SystemDark = dark;
        Sync();
        if (before != IsDark) Raise();
    }

    private void Raise()
    {
        Changed?.Invoke();
        _ws.Bump();   // everything that renders from the workspace (workbench, admin, mobile shell, canvas) re-renders
    }

    public async Task SaveAsync()
    {
        if (_userId is not { } id) return;
        try { await _db.SaveUserPreferencesAsync(id, Prefs.ToJson()); }
        catch (InvalidOperationException) { /* e.g. too many favourites: keep working with what is in memory */ }
    }

    /// <summary>
    /// One-time import of settings older versions kept in this browser's localStorage (tour seen, last project).
    /// Returns true if anything changed.
    /// </summary>
    public async Task<bool> MigrateLegacyAsync(LegacyBrowserSettings? legacy)
    {
        if (Prefs.MigratedLocalStorage || legacy is null || _userId is null) return false;
        Prefs.MigratedLocalStorage = true;
        if (legacy.TourSeen) Prefs.TourSeen = true;
        if (Prefs.LastProjectId is null && Guid.TryParse(legacy.LastProject, out var last)) Prefs.LastProjectId = last;
        if (legacy.Theme is "light" or "dark" or "system" && Prefs.Theme == ThemeMode.Dark)
            Prefs.Theme = legacy.Theme switch { "light" => ThemeMode.Light, "system" => ThemeMode.System, _ => ThemeMode.Dark };
        Sync();
        await SaveAsync();
        Raise();
        return true;
    }

    /// <summary>What the browser should apply to &lt;body&gt; and remember in cookies for the next page load.</summary>
    public object BrowserState() => new
    {
        bodyClass = BodyClass,
        style = AppStyle,
        theme = Prefs.Theme.ToString().ToLowerInvariant(),
        dark = IsDark,
        accent = Prefs.Accent,
        device = Device.ToCookie(),
        reducedMotion = Prefs.ReducedMotion,
    };

    /// <summary>Pushes the current theme to the page (body classes, cookies). Safe before the circuit is interactive.</summary>
    public async Task PushAsync(IJSRuntime js)
    {
        try { await js.InvokeVoidAsync("meridianSettings.apply", BrowserState()); }
        catch (Exception e) when (e is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException) { }
    }

    /// <summary>Copies the preferences the workspace consults (diagram defaults, favourites, dark mode for older code).</summary>
    private void Sync()
    {
        _ws.DarkMode = IsDark;
        _ws.Preferences = Prefs;
        _ws.Favorites.Clear();
        foreach (var f in Prefs.Favorites) _ws.Favorites.Add(f);
        if (!_favoritesHooked)
        {
            _favoritesHooked = true;
            _ws.FavoritesChanged += () =>
            {
                Prefs.Favorites = _ws.Favorites.ToList();
                _ = SaveAsync();
            };
        }
    }
}

/// <summary>What settings.js reports when the circuit starts.</summary>
public sealed record BrowserInfo(bool SystemDark, string? Device, LegacyBrowserSettings? Legacy);

/// <summary>Values older versions kept in the browser (read once by settings.js for migration).</summary>
public sealed record LegacyBrowserSettings(bool TourSeen, string? LastProject, string? Theme);
