using System.Text.RegularExpressions;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Services;
using Meridian.Web.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Web.Tests;

public sealed class ThemeServiceTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "meridian-theme-" + Guid.NewGuid().ToString("N"));
    private SqliteStore _db = default!;
    private UserAccount _ann = default!;

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(Path.Combine(_dir, "m.db"));
        await _db.MigrateAsync();
        _ann = await _db.CreateUserAsync("ann", "Ann", "password-1");
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    private (ThemeService Theme, Workspace Ws) New()
    {
        var ws = new Workspace();
        return (new ThemeService(_db, ws, new ServiceCollection().BuildServiceProvider()), ws);
    }

    private async Task<(ThemeService Theme, Workspace Ws)> SignedInAsync()
    {
        var t = New();
        await t.Theme.LoadForAsync(_ann.Id);
        return t;
    }

    [Fact]
    public async Task Defaults_Are_Dark_Blue_And_Comfortable()
    {
        var (theme, ws) = await SignedInAsync();
        Assert.True(theme.IsDark);
        Assert.True(ws.DarkMode);
        Assert.Equal("dark", theme.AppClass);
        Assert.Equal("m-theme-dark", theme.BodyClass);
        Assert.Same(SvgTheme.Dark, theme.SvgTheme);
        Assert.Equal(ExportBackground.Dark, theme.ExportBackground);
        Assert.Equal(AccentPresets.Default, theme.Prefs.Accent);
    }

    [Fact]
    public async Task Match_System_Follows_The_Browser_Live()
    {
        var (theme, ws) = await SignedInAsync();
        await theme.UpdateAsync(p => p.Theme = ThemeMode.System);
        var changes = 0;
        theme.Changed += () => changes++;
        theme.SetSystemDark(false);
        Assert.False(theme.IsDark);
        Assert.False(ws.DarkMode);
        Assert.StartsWith("light", theme.AppClass);
        theme.SetSystemDark(false);   // no change, no event
        theme.SetSystemDark(true);
        Assert.True(theme.IsDark);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Title_Bar_Toggle_Flips_And_Saves_An_Explicit_Theme()
    {
        var (theme, _) = await SignedInAsync();
        await theme.UpdateAsync(p => p.Theme = ThemeMode.System);
        theme.SetSystemDark(true);
        await theme.ToggleDarkAsync();
        Assert.Equal(ThemeMode.Light, theme.Prefs.Theme);
        await theme.ToggleDarkAsync();
        Assert.Equal(ThemeMode.Dark, theme.Prefs.Theme);
        Assert.Equal(ThemeMode.Dark, UserPreferences.FromJson(await _db.GetUserPreferencesAsync(_ann.Id)).Theme);
    }

    [Fact]
    public async Task Preferences_Follow_The_User_To_Another_Device()
    {
        var (theme, _) = await SignedInAsync();
        await theme.UpdateAsync(p => { p.Theme = ThemeMode.Light; p.Accent = "#E5484D"; p.CanvasAlwaysLight = true; p.ShowGrid = false; });
        theme.SetDevice(new DevicePreferences(Density.Compact));

        var (other, otherWs) = await SignedInAsync();   // another browser: same account, its own device settings
        Assert.Equal(ThemeMode.Light, other.Prefs.Theme);
        Assert.Equal("#e5484d", other.Prefs.Accent);
        Assert.Equal(Density.Comfortable, other.Device.Density);
        Assert.Contains("grid-off", other.AppClass);
        Assert.False(otherWs.DarkMode);
    }

    [Fact]
    public async Task Always_Light_Canvas_Keeps_Diagrams_White_In_Dark_Mode()
    {
        var (theme, ws) = await SignedInAsync();
        await theme.UpdateAsync(p => p.CanvasAlwaysLight = true);
        Assert.True(theme.IsDark);
        Assert.Same(SvgTheme.Light, theme.SvgTheme);
        Assert.Equal(ExportBackground.Light, theme.ExportBackground);
        Assert.Contains("canvas-light", theme.AppClass);
        Assert.True(ws.Preferences.CanvasAlwaysLight);   // DiagramView reads it through the workspace
    }

    [Fact]
    public async Task Snapshot_Restores_Without_The_Database()
    {
        var (theme, _) = await SignedInAsync();
        await theme.UpdateAsync(p => p.Theme = ThemeMode.Light);
        theme.SetDevice(new DevicePreferences(Density.Compact, FontScale.Small));
        var snapshot = theme.TakeSnapshot();

        var (restored, ws) = New();
        restored.Restore(snapshot);
        Assert.Equal(theme.AppClass, restored.AppClass);
        Assert.Equal(theme.AppStyle, restored.AppStyle);
        Assert.Equal(_ann.Id, restored.UserId);
        Assert.False(ws.DarkMode);
    }

    [Fact]
    public async Task Legacy_Browser_Settings_Migrate_Once()
    {
        var (theme, _) = await SignedInAsync();
        var project = Guid.NewGuid();
        Assert.True(await theme.MigrateLegacyAsync(new LegacyBrowserSettings(true, project.ToString(), "light")));
        Assert.True(theme.Prefs.TourSeen);
        Assert.Equal(project, theme.Prefs.LastProjectId);
        Assert.Equal(ThemeMode.Light, theme.Prefs.Theme);
        Assert.False(await theme.MigrateLegacyAsync(new LegacyBrowserSettings(false, null, "dark")));   // only the first time
        var stored = UserPreferences.FromJson(await _db.GetUserPreferencesAsync(_ann.Id));
        Assert.True(stored.MigratedLocalStorage);
        Assert.True(stored.TourSeen);
    }

    [Fact]
    public async Task Favourites_Are_Saved_With_The_Preferences()
    {
        var (_, ws) = await SignedInAsync();
        ws.UseLocalModel(SampleModels.Vehicle());
        var block = ws.Model.OfKind(ElementKind.Block).First();
        ws.ToggleFavorite(block);
        await Task.Delay(50);
        Assert.Equal([block.Id], UserPreferences.FromJson(await _db.GetUserPreferencesAsync(_ann.Id)).Favorites);
        var (_, otherWs) = await SignedInAsync();
        Assert.Contains(block.Id, otherWs.Favorites);
    }

    [Fact]
    public void Unreadable_Or_Hostile_Documents_Fall_Back_To_Defaults()
    {
        Assert.Equal(ThemeMode.Dark, UserPreferences.FromJson("not json").Theme);
        var p = UserPreferences.FromJson("""{"theme":"light","accent":"red;}body{display:none","assistantModel":"gpt-x","favorites":[""]}""");
        Assert.Equal(ThemeMode.Light, p.Theme);
        Assert.Equal(AccentPresets.Default, p.Accent);
        Assert.Equal(AssistantModels.Default, p.AssistantModel);
        Assert.Empty(p.Favorites);
        Assert.Equal(new DevicePreferences(), DevicePreferences.FromCookie("<script>"));
        Assert.Equal(new DevicePreferences(Density.Compact, FontScale.Large), DevicePreferences.FromCookie(new DevicePreferences(Density.Compact, FontScale.Large).ToCookie()));
    }

    [Theory]
    [MemberData(nameof(Accents))]
    public void Every_Accent_Yields_Readable_Tokens(string accent)
    {
        foreach (var dark in new[] { false, true })
        {
            var t = ThemeService.TokensFor(accent, dark);
            Assert.True(ColorMath.Contrast(t.Text, dark ? ThemeService.DarkPanel : ThemeService.LightPanel) >= 4.5, $"{accent} text ({dark})");
            Assert.True(ColorMath.Contrast("#ffffff", t.Strong) >= 4.5, $"white on {accent} button");
            var mud = ThemeService.MudThemeFor(accent);
            Assert.True(ColorMath.Contrast(mud.PaletteLight.PrimaryContrastText.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex), mud.PaletteLight.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex)) >= 4.5);
            Assert.True(ColorMath.Contrast(mud.PaletteDark.PrimaryContrastText.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex), mud.PaletteDark.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex)) >= 4.5);
        }
    }

    public static IEnumerable<object[]> Accents() => AccentPresets.All.Select(a => new object[] { a.Color }).Append(["#ffff00"]).Append(["#101010"]);

    [Fact]
    public void Workspace_Replaces_The_Tab_When_Diagrams_Do_Not_Open_As_Tabs()
    {
        var ws = new Workspace();
        ws.UseLocalModel(SampleModels.Vehicle());
        var diagrams = ws.Model.Diagrams.Take(3).ToList();
        ws.Open(diagrams[0]);
        ws.Open(diagrams[1]);
        Assert.Equal(2, ws.OpenDiagrams.Count);
        ws.Preferences = new UserPreferences { OpenDiagramsAsTabs = false };
        ws.Open(diagrams[2]);
        Assert.Equal(2, ws.OpenDiagrams.Count);
        Assert.Same(diagrams[2], ws.Active!.Diagram);
        Assert.DoesNotContain(ws.OpenDiagrams, c => ReferenceEquals(c.Diagram, diagrams[1]));
    }

    [Fact]
    public void Snap_To_Grid_Can_Be_Turned_Off()
    {
        var ws = new Workspace();
        ws.UseLocalModel(SampleModels.Vehicle());
        var d = ws.Model.Diagrams.First(x => x.Nodes.Count > 0);
        var c = ws.Open(d);
        var node = d.Nodes[0];
        c.Select(node.Element);
        c.Key("ArrowRight", Mods.None);
        Assert.Equal(0, node.Bounds.X % DiagramController.Grid);   // nudges move by the grid either way
        ws.Preferences = new UserPreferences { SnapToGrid = false };
        var p = c.ToScreen(new Pt(node.Bounds.X + 5, node.Bounds.Y + 5));
        c.PointerDown(p.X, p.Y, 0, Mods.None, 1);
        c.PointerMove(p.X + 13, p.Y + 7);
        c.PointerUp(Mods.None);
        Assert.NotEqual(0, node.Bounds.X % DiagramController.Grid);
    }

    [Fact]
    public void Confirm_Before_Delete_Asks_And_Respects_Cancel()
    {
        var ws = new Workspace();
        ws.UseLocalModel(SampleModels.Vehicle());
        var d = ws.Model.Diagrams.First(x => x.Nodes.Count > 0);
        var c = ws.Open(d);
        var element = d.Nodes[0].Element;
        c.Select(element);
        var asked = 0;
        ws.Confirm = (_, _) => { asked++; return Task.FromResult(false); };
        ws.Preferences = new UserPreferences { ConfirmDeleteFromModel = true };
        c.DeleteSelectionFromModel();
        Assert.Equal(1, asked);
        Assert.True(ws.Model.Contains(element));
        ws.Confirm = (_, _) => Task.FromResult(true);
        c.Select(element);
        c.DeleteSelectionFromModel();
        Assert.False(ws.Model.Contains(element));
    }

    [Fact]
    public void Diagram_Defaults_Map_To_Routing_Values()
    {
        Assert.Null(new UserPreferences().ConnectorRoutingValue);
        Assert.Equal("curved", new UserPreferences { ConnectorStyle = ConnectorDefault.Curved }.ConnectorRoutingValue);
        Assert.Equal(EdgeRouting.Straight, EdgeRouter.Parse(new UserPreferences { ConnectorStyle = ConnectorDefault.Straight }.ConnectorRoutingValue));
    }
}

/// <summary>Automated contrast scan of the CSS design tokens: every text token reads at WCAG AA (4.5:1) on every surface.</summary>
public sealed class ThemeContrastTests
{
    private static string AppCss()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "src", "Meridian.Web", "wwwroot", "app.css");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("app.css not found above " + AppContext.BaseDirectory);
    }

    private static Dictionary<string, string> Tokens(string css, string selectorStart)
    {
        var at = css.IndexOf("\n" + selectorStart, StringComparison.Ordinal);
        Assert.True(at >= 0, $"No block {selectorStart}");
        var open = css.IndexOf('{', at);
        var body = css[(open + 1)..css.IndexOf('}', open)];
        return Regex.Matches(body, @"(--[\w-]+)\s*:\s*([^;]+);").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());
    }

    public static IEnumerable<object[]> Themes() => [["dark"], ["light"]];

    [Theory]
    [MemberData(nameof(Themes))]
    public void Text_Tokens_Meet_WCAG_AA_On_Every_Surface(string mode)
    {
        var css = AppCss();
        var t = Tokens(css, ".app {");
        foreach (var kv in Tokens(css, $".app.{mode}")) t[kv.Key] = kv.Value;
        var bg = ColorMath.Parse(t["--bg"]);
        var surfaces = new[] { "--bg", "--panel-solid", "--panel-2", "--panel-3", "--panel", "--chrome" }
            .ToDictionary(s => s, s => ColorMath.Resolve(t[s], bg));
        var failures = new List<string>();
        foreach (var text in new[] { "--text", "--text-2", "--text-3", "--accent-2", "--danger", "--ok" })
            foreach (var (name, surface) in surfaces)
            {
                var ratio = ColorMath.Contrast(ColorMath.Parse(t[text]), surface);
                if (ratio < 4.5) failures.Add($"{mode}: {text} {t[text]} on {name} {surface.Hex} = {ratio:0.00}");
            }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Body_Carries_The_Same_Token_Sets_For_Dialogs()
    {
        var css = AppCss();
        Assert.Contains(".app.dark, body.m-theme-dark {", css);
        Assert.Contains(".app.light, body.m-theme-light {", css);
    }

    [Fact]
    public void Color_Math_Matches_Known_Ratios()
    {
        Assert.Equal(21, ColorMath.Contrast("#000000", "#ffffff"), 1);
        Assert.Equal(1, ColorMath.Contrast("#4f8cff", "#4f8cff"), 3);
        Assert.InRange(ColorMath.Contrast("#767676", "#ffffff"), 4.5, 4.6);   // the classic AA grey
        Assert.True(ColorMath.Contrast(ColorMath.EnsureContrast("#4f8cff", "#ffffff"), "#ffffff") >= 4.5);
    }
}

/// <summary>
/// The feature stylesheets added in 0.5–0.7 (database, libraries, permissions, verification, multi-select, the SysML
/// editor, the tour and empty states) must not hard-code a text colour that is unreadable in one of the themes: a fixed
/// <c>color: #…</c> in a rule that does not set its own background must reach WCAG AA (4.5:1) on the panel of every
/// theme it applies to (rules under <c>.app.light</c> / <c>.app.dark</c> only count for that theme).
/// </summary>
public sealed class FeatureCssContrastTests
{
    private static string WwwRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "src", "Meridian.Web", "wwwroot");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("wwwroot not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Hard_Coded_Text_Colours_Are_Readable_In_Both_Themes()
    {
        var root = WwwRoot();
        var files = Directory.GetFiles(Path.Combine(root, "css"), "*.css").Append(Path.Combine(root, "extras.css")).ToList();
        Assert.Contains(files, f => f.EndsWith("verification.css", StringComparison.Ordinal));
        var failures = new List<string>();
        var checkedRules = 0;
        foreach (var file in files)
        {
            var css = Regex.Replace(File.ReadAllText(file), @"/\*.*?\*/", "", RegexOptions.Singleline);
            foreach (Match rule in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}"))
            {
                var selector = rule.Groups[1].Value.Trim();
                var body = rule.Groups[2].Value;
                if (Regex.IsMatch(body, @"(^|[;\s])background(-color)?\s*:")) continue;   // its own background: judged with it, not here
                var color = Regex.Match(body, @"(?:^|[;\s])color\s*:\s*(#[0-9a-fA-F]{6}|#[0-9a-fA-F]{3})\b");
                if (!color.Success) continue;
                if (color.Groups[1].Value.ToLowerInvariant() is "#fff" or "#ffffff") continue;   // white is only used on a coloured chip set in the markup
                checkedRules++;
                var themes = selector.Contains(".light", StringComparison.Ordinal) ? new[] { false }
                    : selector.Contains(".dark", StringComparison.Ordinal) ? new[] { true } : new[] { false, true };
                foreach (var dark in themes)
                {
                    // A rule with a .app.light twin only applies to the dark theme.
                    if (!dark && themes.Length == 2 && css.Contains(".app.light " + selector, StringComparison.Ordinal)) continue;
                    var ratio = ColorMath.Contrast(color.Groups[1].Value, dark ? ThemeService.DarkPanel : ThemeService.LightPanel);
                    if (ratio < 4.5) failures.Add($"{Path.GetFileName(file)}: {selector} {color.Groups[1].Value} on the {(dark ? "dark" : "light")} panel = {ratio:0.00}");
                }
            }
        }
        Assert.True(checkedRules > 0);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
