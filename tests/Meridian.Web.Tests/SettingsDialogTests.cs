using Bunit;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Meridian.Web.Settings;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

public class SettingsDialogTests : TestContext
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "meridian-settings-" + Guid.NewGuid().ToString("N"));
    private SqliteStore _db = default!;
    private UserAccount _me = default!;

    /// <summary>Shows the Settings dialog through the dialog service, as the workbench does.</summary>
    public static IRenderedComponent<MudDialogProvider> Open(TestContext ctx, SettingsSection? section = null, bool desktop = false)
    {
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var dialogs = ctx.Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<SettingsDialog> { { x => x.Section, section }, { x => x.Desktop, desktop } };
        provider.InvokeAsync(() => dialogs.ShowAsync<SettingsDialog>("Settings", parameters, SettingsDialog.Options)).GetAwaiter().GetResult();
        provider.WaitForAssertion(() => Assert.NotEmpty(provider.FindAll(".settings")));
        return provider;
    }

    private ThemeService Setup(bool localMode = false, bool admin = false)
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _db = new SqliteStore(Path.Combine(_dir, "m.db"));
        _db.MigrateAsync().GetAwaiter().GetResult();
        _me = _db.CreateUserAsync("ann", "Ann", "password-1").GetAwaiter().GetResult();
        Services.AddSingleton<IMeridianStore>(_db);
        Services.AddSingleton(new MeridianHostInfo(localMode, _dir, null));
        Services.AddScoped<ThemeService>();
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(SampleModels.Vehicle());
        ws.User = _me;
        ws.IsServerAdmin = admin;
        var theme = Services.GetRequiredService<ThemeService>();
        theme.LoadForAsync(_me.Id).GetAwaiter().GetResult();
        RenderComponent<MudPopoverProvider>();
        return theme;
    }

    private UserPreferences Stored() => UserPreferences.FromJson(_db.GetUserPreferencesAsync(_me.Id).GetAwaiter().GetResult());

    private static void Choose(IRenderedComponent<MudDialogProvider> d, string group, string option) =>
        d.FindAll($".seg[data-name='{group}'] button").Single(b => b.TextContent.Trim() == option).Click();

    [Fact]
    public void Opens_On_Appearance_With_Sections_For_A_Team_Server_User()
    {
        Setup();
        var d = Open(this);
        var tabs = d.FindAll(".settings-tab").Select(t => t.TextContent.Trim()).ToList();
        Assert.Equal(["Appearance", "Diagrams", "Editor", "AI assistant", "Account"], tabs);   // no Desktop (browser), no Server (not an admin)
        Assert.Contains("on", d.Find(".settings-tab[data-section='Appearance']").ClassName);
        Assert.Contains("Accent colour", d.Markup);
        Assert.Contains("Reset to defaults", d.Markup);
    }

    [Fact]
    public void Theme_And_Accent_Apply_Live_And_Are_Saved_For_The_User()
    {
        var theme = Setup();
        var d = Open(this);
        Assert.True(theme.IsDark);
        Choose(d, "theme", "Light");
        Assert.False(theme.IsDark);
        Assert.Equal("true", d.FindAll(".seg[data-name='theme'] button").Single(b => b.TextContent.Trim() == "Light").GetAttribute("aria-checked"));
        d.Find("button.accent-swatch[aria-label='Violet accent']").Click();
        Assert.Equal("#8b5cf6", theme.Prefs.Accent);
        Assert.Contains("--accent-raw: #8b5cf6", theme.AppStyle);
        var stored = Stored();
        Assert.Equal(ThemeMode.Light, stored.Theme);
        Assert.Equal("#8b5cf6", stored.Accent);
        Assert.DoesNotContain(d.FindAll(".settings-body button"), b => b.TextContent.Contains("Save"));   // no Save button for ordinary settings
    }

    [Fact]
    public void Density_And_Font_Size_Are_Per_Device_And_Not_Stored_For_The_User()
    {
        var theme = Setup();
        var d = Open(this);
        Assert.Contains("This device", d.Find(".settings-row[data-setting='Density']").TextContent);
        Assert.Contains("You", d.Find(".settings-row[data-setting='Theme']").TextContent);
        Choose(d, "density", "Compact");
        Choose(d, "font", "Large");
        Assert.Equal(new DevicePreferences(Density.Compact, FontScale.Large), theme.Device);
        Assert.Contains("compact", theme.AppClass);
        Assert.Contains("m-font-large", theme.BodyClass);
        Assert.Null(_db.GetUserPreferencesAsync(_me.Id).GetAwaiter().GetResult());   // nothing saved server-side
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "meridianSettings.setDevice" && (string)i.Arguments[0]! == "compact.large");
    }

    [Fact]
    public void Search_Filters_Settings_By_Name_Across_Sections()
    {
        Setup();
        var d = Open(this);
        d.Find("input.settings-search").Input("snap");
        var rows = d.FindAll(".settings-row");
        Assert.Single(rows);
        Assert.Contains("Snap to grid", rows[0].TextContent);
        d.Find("input.settings-search").Input("zoom");
        Assert.Contains(d.FindAll(".settings-row"), r => r.TextContent.Contains("Zoom when a diagram opens"));
        Assert.Contains(d.FindAll(".settings-h"), h => h.TextContent == "Diagrams");
        d.Find("input.settings-search").Input("no such setting");
        Assert.Contains("No settings match", d.Markup);
    }

    [Fact]
    public void Reset_To_Defaults_Resets_Only_That_Section()
    {
        var theme = Setup();
        theme.UpdateAsync(p => { p.ConnectorStyle = ConnectorDefault.Curved; p.LineJumps = true; p.Theme = ThemeMode.Light; }).GetAwaiter().GetResult();
        var d = Open(this, SettingsSection.Diagrams);
        Assert.Equal("true", d.FindAll(".seg[data-name='connector'] button").Single(b => b.TextContent.Trim() == "Curved").GetAttribute("aria-checked"));
        d.Find("button.settings-reset").Click();
        Assert.Equal(ConnectorDefault.Automatic, theme.Prefs.ConnectorStyle);
        Assert.False(theme.Prefs.LineJumps);
        Assert.Equal(ThemeMode.Light, theme.Prefs.Theme);   // Appearance untouched
        Assert.Equal(ConnectorDefault.Automatic, Stored().ConnectorStyle);
    }

    [Fact]
    public void Toggles_Change_Diagram_And_Editor_Preferences()
    {
        var theme = Setup();
        var d = Open(this, SettingsSection.Diagrams);
        d.Find("input[data-name='tabs']").Change(false);
        Assert.False(theme.Prefs.OpenDiagramsAsTabs);
        Assert.False(Services.GetRequiredService<Workspace>().Preferences.OpenDiagramsAsTabs);   // the workspace sees it at once
        d.Find(".settings-tab[data-section='Editor']").Click();
        d.Find("input[data-name='confirm-delete']").Change(true);
        Assert.True(Stored().ConfirmDeleteFromModel);
        Assert.Contains("⌘K", d.Find(".settings-keys").TextContent);
    }

    [Fact]
    public void Assistant_Section_Shows_Model_Choice_And_Privacy_Notice()
    {
        var theme = Setup();
        var d = Open(this, SettingsSection.Assistant);
        Assert.Contains(SettingsDialog.PrivacyText, d.Markup);
        Assert.Equal(4, d.FindAll("select.settings-select option").Count);
        d.Find("select.settings-select").Change("claude-sonnet-5-5");
        Assert.Equal("claude-sonnet-5-5", theme.Prefs.AssistantModel);
        d.Find("input[data-name='ai']").Change(false);
        Assert.False(Stored().AssistantEnabled);
        Assert.Empty(d.FindAll("input.ai-key"));   // team server: the administrator configures the key
    }

    [Fact]
    public void Local_Mode_Has_No_Account_Section_And_Admins_See_The_Server_Link()
    {
        Setup(localMode: true, admin: true);
        var d = Open(this);
        var tabs = d.FindAll(".settings-tab").Select(t => t.TextContent.Trim()).ToList();
        Assert.DoesNotContain("Account", tabs);
        Assert.DoesNotContain("Server", tabs);   // local mode has no admin console
        Assert.Contains("This computer", d.Find(".settings-row[data-setting='Density']").TextContent);
    }

    [Fact]
    public void Server_Admins_Get_A_Link_To_The_Admin_Console()
    {
        Setup(admin: true);
        var d = Open(this, SettingsSection.Server);
        Assert.Equal("/admin", d.Find(".settings-body a.settings-btn").GetAttribute("href"));
        Assert.Contains("Server", d.Find(".settings-row").TextContent);
    }

    [Fact]
    public void Show_The_Tour_Again_Clears_The_Seen_Flag()
    {
        var theme = Setup();
        theme.UpdateAsync(p => p.TourSeen = true).GetAwaiter().GetResult();
        var d = Open(this, SettingsSection.Editor);
        d.FindAll("button.settings-btn").Single(b => b.TextContent.Contains("Show the tour again")).Click();
        Assert.False(theme.Prefs.TourSeen);
        Assert.False(Stored().TourSeen);
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "meridianSettings.forgetTour");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
