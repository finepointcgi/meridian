using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

public sealed class SettingsTests(ServerFixture f) : E2ETest(f)
{
    private ILocator App(IPage p) => p.Locator("div.app").First;
    private ILocator Dialog(IPage p) => p.Locator(".settings-dialog");

    [E2EFact]
    public async Task Theme_and_accent_apply_live_survive_a_reload_and_match_system_follows_the_os()
    {
        // A fresh account, so the other tests' users keep the default theme.
        var s = await OpenAsync(null, "/login?mode=register", waitForWorkbench: false);
        var name = "theme" + RandomNumberGenerator.GetHexString(4, lowercase: true);
        await s.Page.GetByLabel("Username").FillAsync(name);
        await s.Page.GetByLabel("Display name").FillAsync("Theo Theme");
        await s.Page.GetByLabel("Password").FillAsync("Pw-" + RandomNumberGenerator.GetHexString(20, lowercase: true));
        await s.Page.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();
        await s.WaitForWorkbenchAsync();
        await Expect(App(s.Page)).ToHaveClassAsync(new Regex(@"\bdark\b"));

        // ⌘, / Ctrl+, opens Settings on Appearance.
        await s.Page.Keyboard.PressAsync("Control+Comma");
        await Expect(Dialog(s.Page)).ToBeVisibleAsync();
        await Expect(Dialog(s.Page).Locator(".settings-tab.on")).ToHaveTextAsync("Appearance");

        // Light and a new accent apply at once, with no Save button.
        await Dialog(s.Page).Locator(".seg[data-name='theme'] button", new() { HasTextString = "Light" }).ClickAsync();
        await Expect(App(s.Page)).ToHaveClassAsync(new Regex(@"\blight\b"));
        await Expect(s.Page.Locator("body")).ToHaveClassAsync(new Regex(@"\bm-theme-light\b"));
        await Dialog(s.Page).Locator("button.accent-swatch[aria-label='Violet accent']").ClickAsync();
        await Expect(App(s.Page)).ToHaveAttributeAsync("style", new Regex("--accent-raw: #8b5cf6"));
        Assert.Equal("rgb(255, 255, 255)", await Dialog(s.Page).EvaluateAsync<string>("e => getComputedStyle(e).backgroundColor"));   // the dialog is light too

        // Search narrows the list.
        await Dialog(s.Page).Locator("input.settings-search").FillAsync("snap");
        await Expect(Dialog(s.Page).Locator(".settings-row")).ToHaveCountAsync(1);
        await s.Page.Keyboard.PressAsync("Escape");
        await Expect(Dialog(s.Page)).ToBeHiddenAsync();

        // Reload: the server renders the stored theme before the first paint.
        var html = await (await s.Page.Context.APIRequest.GetAsync(s.BaseUrl + "/")).TextAsync();
        Assert.Matches(@"<body class=""m-theme-light[^""]*"" style=""[^""]*--accent-raw: #8b5cf6", html);
        await s.Page.ReloadAsync();
        await s.WaitForWorkbenchAsync();
        await Expect(App(s.Page)).ToHaveClassAsync(new Regex(@"\blight\b"));
        await Expect(App(s.Page)).ToHaveAttributeAsync("style", new Regex("--accent-raw: #8b5cf6"));

        // Match system follows the operating system's colour scheme live.
        await s.Page.Locator("header.titlebar .tb-settings").ClickAsync();
        await Dialog(s.Page).Locator(".seg[data-name='theme'] button", new() { HasTextString = "Match system" }).ClickAsync();
        await s.Page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
        await Expect(App(s.Page)).ToHaveClassAsync(new Regex(@"\bdark\b"));
        await Expect(s.Page.Locator("body")).ToHaveClassAsync(new Regex(@"\bm-theme-dark\b"));
        await s.Page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
        await Expect(App(s.Page)).ToHaveClassAsync(new Regex(@"\blight\b"));
        await Expect(Dialog(s.Page).Locator(".seg[data-name='theme'] button[aria-checked='true']")).ToHaveTextAsync("Match system");

        // The title-bar sun/moon still toggles, and becomes an explicit theme.
        await s.Page.Keyboard.PressAsync("Escape");
        await s.Page.Locator("header.titlebar .tb-theme").ClickAsync();
        await Expect(App(s.Page)).ToHaveClassAsync(new Regex(@"\bdark\b"));
    }
}
