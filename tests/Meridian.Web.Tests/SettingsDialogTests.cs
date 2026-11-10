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

    // ---------------------------------------------------------------- Settings ▸ Desktop (Photino bridge)

    private IRenderedComponent<MudDialogProvider> OpenDesktop(SettingsDialog.DesktopState state)
    {
        Setup(localMode: true);
        JSInterop.Setup<SettingsDialog.DesktopState?>("meridianSettings.desktopSettings").SetResult(state);
        var d = Open(this, SettingsSection.Desktop, desktop: true);
        d.WaitForAssertion(() => Assert.Contains("This is Meridian", d.Markup));
        return d;
    }

    private List<object?[]> DesktopCommands() =>
        JSInterop.Invocations.Where(i => i.Identifier == "meridianSettings.desktop").Select(i => i.Arguments.ToArray()).ToList();

    private static void Message(IRenderedComponent<MudDialogProvider> d, string json) =>
        d.InvokeAsync(() => d.FindComponent<SettingsDialog>().Instance.OnDesktopMessage(json)).GetAwaiter().GetResult();

    [Fact]
    public void Desktop_Shows_The_Version_Last_Check_And_Result()
    {
        var d = OpenDesktop(new(false, true, 2, Version: "0.5.0", LastCheck: DateTimeOffset.Now.AddMinutes(-5).ToString("O"),
            LastMessage: "You're up to date (0.5.0).", LastStatus: "UpToDate"));
        Assert.Contains("This is Meridian 0.5.0.", d.Find(".settings-row[data-setting='desktop-updates']").TextContent);
        Assert.Contains("Last checked 5 min ago.", d.Find(".desktop-last-check").TextContent);
        var message = d.Find(".desktop-update-message");
        Assert.Equal("You're up to date (0.5.0).", message.TextContent.Trim());
        Assert.Contains("ok", message.ClassName);
        Assert.Empty(d.FindAll(".desktop-install"));
        Assert.True(d.Find(".switch input[data-name='updates']").HasAttribute("checked"));
        Assert.Contains("Automatically check daily", d.Markup);
        // Listening for the host's messages.
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "meridianSettings.desktopListen");
    }

    [Fact]
    public void Desktop_Check_For_Updates_Asks_The_Host_And_Shows_Its_Answer()
    {
        var d = OpenDesktop(new(false, true, 0, Version: "0.5.0"));
        Assert.Contains("Never checked.", d.Find(".desktop-last-check").TextContent);
        d.Find("button.desktop-check").Click();
        Assert.Contains(DesktopCommands(), a => (string?)a[0] == "checkUpdates");
        Assert.Equal("Checking for updates…", d.Find(".desktop-update-message").TextContent.Trim());
        Assert.True(d.Find("button.desktop-check").HasAttribute("disabled"));

        Message(d, """{"type":"update-status","status":"PrivateRepository","message":"Couldn't reach GitHub: the repository is private — set a token or another update source in Settings.","lastCheck":null,"available":null}""");
        var message = d.Find(".desktop-update-message");
        Assert.StartsWith("Couldn't reach GitHub: the repository is private", message.TextContent.Trim());
        Assert.Contains("bad", message.ClassName);
        Assert.False(d.Find("button.desktop-check").HasAttribute("disabled"));
    }

    [Fact]
    public void Desktop_Install_And_Restart_Shows_Download_Progress()
    {
        var d = OpenDesktop(new(false, true, 0, Version: "0.5.0"));
        Message(d, """{"type":"update-status","status":"Available","message":"0.6.0 is available — Install and restart.","lastCheck":"2026-11-10T09:00:00+00:00","available":"0.6.0"}""");
        var install = d.Find("button.desktop-install");
        Assert.Equal("Install 0.6.0 and restart", install.TextContent.Trim());
        install.Click();
        Assert.Contains(DesktopCommands(), a => (string?)a[0] == "update");
        Message(d, """{"type":"update-progress","percent":42}""");
        Assert.Equal("42", d.Find("progress.desktop-update-progress").GetAttribute("value"));
        Assert.Contains("Downloading… 42%", d.Markup);
        Assert.True(d.Find("button.desktop-install").HasAttribute("disabled"));

        Message(d, """{"type":"update-error","message":"Checksum mismatch for Meridian-0.6.0-macos-arm64.zip: the download was discarded."}""");
        Assert.Empty(d.FindAll("progress.desktop-update-progress"));
        Assert.Contains("Checksum mismatch", d.Find(".desktop-update-message").TextContent);
        Assert.False(d.Find("button.desktop-install").HasAttribute("disabled"));
    }

    [Fact]
    public void Desktop_Update_Source_Switches_To_A_Custom_Manifest_Url()
    {
        var d = OpenDesktop(new(false, true, 0, Version: "0.5.0", UpdateSource: "GitHub"));
        Assert.Equal("true", d.FindAll(".seg[data-name='update-source'] button").Single(b => b.TextContent.Trim() == "GitHub releases").GetAttribute("aria-checked"));
        Assert.True(d.Find("button.desktop-source-save").HasAttribute("disabled"));
        Choose(d, "update-source", "Custom URL");
        d.Find("input.desktop-manifest-url").Input("https://meridian.example.com/api/desktop/latest");
        d.Find("button.desktop-source-save").Click();
        var call = DesktopCommands().Last();
        Assert.Equal("updateSource", call[0]);
        Assert.Contains("https://meridian.example.com/api/desktop/latest", System.Text.Json.JsonSerializer.Serialize(call[1]));
        Assert.Contains("\"Manifest\"", System.Text.Json.JsonSerializer.Serialize(call[1]));
    }

    [Fact]
    public void Desktop_Token_Goes_From_The_Input_To_The_Host_Without_Blazor()
    {
        var d = OpenDesktop(new(false, true, 0, Version: "0.5.0", UpdateSource: "GitHubToken", HasToken: true));
        var input = d.Find("input.desktop-token");
        Assert.Equal("password", input.GetAttribute("type"));
        Assert.Contains("A token is saved", input.GetAttribute("placeholder"));
        Assert.False(input.HasAttribute("value"));   // never bound: the component never sees the token
        Assert.Contains("api.github.com", d.Markup);
        d.Find("button.desktop-token-save").Click();
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "meridianSettings.desktopSaveToken" && (string?)i.Arguments[0] == "desktop-token");
        d.Find("button.desktop-token-remove").Click();
        Assert.Contains(DesktopCommands(), a => (string?)a[0] == "updateToken");
    }

    [Fact]
    public void Desktop_Update_Source_Switches_To_GitLab_Releases_With_An_Optional_Token()
    {
        var d = OpenDesktop(new(false, true, 0, Version: "0.5.0", UpdateSource: "GitHub"));
        Choose(d, "update-source", "GitLab releases");
        d.Find("input.desktop-gitlab-url").Input("https://gitlab.acme.test");
        d.Find("input.desktop-gitlab-project").Input("tools/meridian");
        var token = d.Find("input.desktop-gitlab-token");
        Assert.Equal("password", token.GetAttribute("type"));
        Assert.False(token.HasAttribute("value"));
        d.Find("button.desktop-source-save").Click();
        var call = System.Text.Json.JsonSerializer.Serialize(DesktopCommands().Last()[1]);
        Assert.Contains("\"GitLab\"", call);
        Assert.Contains("https://gitlab.acme.test", call);
        Assert.Contains("tools/meridian", call);

        d.Find("button.desktop-gitlab-token-save").Click();
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "meridianSettings.desktopSaveToken"
            && (string?)i.Arguments[0] == "desktop-gitlab-token" && (string?)i.Arguments[1] == "GitLab");
    }

    [Fact]
    public void Desktop_GitLab_Source_Shows_The_Saved_Project_And_Removes_Its_Token()
    {
        var saved = OpenDesktop(new(false, true, 0, Version: "0.5.0", UpdateSource: "GitLab", GitLabUrl: "https://gitlab.acme.test", GitLabProject: "tools/meridian", HasGitLabToken: true));
        Assert.Equal("tools/meridian", saved.Find("input.desktop-gitlab-project").GetAttribute("value"));
        Assert.True(saved.Find("button.desktop-source-save").HasAttribute("disabled"));
        saved.Find("button.desktop-gitlab-token-remove").Click();
        Assert.Contains(DesktopCommands(), a => (string?)a[0] == "updateToken" && System.Text.Json.JsonSerializer.Serialize(a[1]).Contains("GitLab"));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
