using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>The desktop app's private local server: signed in only through the launch token.</summary>
[Collection("local")]
public sealed class LocalModeTests : IAsyncLifetime
{
    private MeridianApp _app = default!;
    private IPlaywright _pw = default!;
    private IBrowser _browser = default!;

    public async Task InitializeAsync()
    {
        if (BrowserSupport.MissingReason != null && !BrowserSupport.Required) return;
        _app = await MeridianApp.StartAsync(localMode: true);
        _pw = await Playwright.CreateAsync();
        _browser = await _pw.Chromium.LaunchAsync();
    }

    public async Task DisposeAsync()
    {
        if (_browser != null) await _browser.CloseAsync();
        _pw?.Dispose();
        if (_app != null) await _app.DisposeAsync();
    }

    [E2EFact]
    public async Task Launch_token_signs_in_and_opens_the_sample_without_server_features()
    {
        await using var s = await BrowserSession.CreateAsync(_browser, _app.BaseUrl, null);
        await s.Page.GotoAsync("/?launch=" + _app.LaunchToken);
        await Expect(s.Page).ToHaveURLAsync(_app.BaseUrl + "/");   // the token is stripped from the address bar
        await s.WaitForWorkbenchAsync("Vehicle System (sample)");
        await Expect(s.Page.Tree().GetByText("Vehicle Requirements", new() { Exact = true })).ToBeVisibleAsync();
        // Local mode has no team features: no notification bell, no Share button.
        await Expect(s.Page.Locator("header.titlebar button.rbtn", new() { HasTextString = "Share" })).ToHaveCountAsync(0);
        s.AssertNoErrors();
    }

    [E2EFact]
    public async Task Without_the_token_the_workspace_is_private()
    {
        await using var s = await BrowserSession.CreateAsync(_browser, _app.BaseUrl, null);
        s.IgnoreErrors.Add(e => e.Contains("403"));   // the deliberate refusal is logged by Chromium as a failed resource
        var wrong = await s.Page.GotoAsync("/?launch=wrong-token");
        Assert.Equal(403, wrong!.Status);
        await Expect(s.Page.Locator("body")).ToContainTextAsync("private to the desktop app");
        var login = await s.Page.GotoAsync("/login");
        Assert.Equal(403, login!.Status);
        s.AssertNoErrors();
    }
}
