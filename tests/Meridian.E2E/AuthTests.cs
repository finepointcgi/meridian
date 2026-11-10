using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

public sealed class AuthTests(ServerFixture f) : E2ETest(f)
{
    [E2EFact]
    public async Task Sign_in_rejects_a_wrong_password_then_opens_the_sample_project()
    {
        var s = await OpenAsync(null, "/", waitForWorkbench: false);
        await Expect(s.Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/login"));
        await Expect(s.Page.GetByRole(AriaRole.Heading, new() { Name = "Welcome back" })).ToBeVisibleAsync();

        await s.Page.GetByLabel("Username").FillAsync(F.Admin.Username);
        await s.Page.GetByLabel("Password").FillAsync("not-the-password");
        await s.Page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await Expect(s.Page.Locator(".auth-error")).ToBeVisibleAsync();

        await s.Page.GetByLabel("Username").FillAsync(F.Admin.Username);
        await s.Page.GetByLabel("Password").FillAsync(F.Admin.Password);
        await s.Page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await s.WaitForWorkbenchAsync("Vehicle System (sample)");

        // A fresh account gets its own sample project. It is in the explorer, and a diagram opens on double-click.
        var tree = s.Page.Tree();
        await Expect(tree.GetByText("Vehicle Requirements", new() { Exact = true })).ToBeVisibleAsync();
        await tree.GetByText("Vehicle Structure", new() { Exact = true }).DblClickAsync();
        await Expect(s.Page.Locator(".tabstrip .tab.active")).ToContainTextAsync("Vehicle Structure");
        await Expect(s.Page.Node("Engine").First).ToBeVisibleAsync();
        var counts = await s.Page.CountsAsync();
        Assert.True(counts.Elements > 20 && counts.Relationships > 10 && counts.Diagrams > 3, $"sample counts {counts}");
    }

    [E2EFact]
    public async Task Registration_rejects_a_taken_username()
    {
        var s = await OpenAsync(null, "/login?mode=register", waitForWorkbench: false);
        await s.Page.GetByLabel("Username").FillAsync(F.Bob.Username);
        await s.Page.GetByLabel("Password").FillAsync("Another-Password-123");
        await s.Page.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();
        await Expect(s.Page.Locator(".auth-error")).ToBeVisibleAsync();
        await Expect(s.Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("mode=register"));
    }

    [E2EFact]
    public async Task Sign_out_returns_to_the_sign_in_page()
    {
        var s = await OpenAsync(F.Bob);
        await s.Page.Locator("header.titlebar .avatar.me").ClickAsync();
        await s.Page.Locator(".mud-popover-open .mud-menu-item").Filter(new() { HasTextString = "Sign out" }).ClickAsync();
        await Expect(s.Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/login"));
        // The old session's cookie no longer reaches the workbench.
        await s.Page.GotoAsync("/");
        await Expect(s.Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/login"));
    }
}
