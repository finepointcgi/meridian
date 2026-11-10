using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

public sealed class AdminTests(ServerFixture f) : E2ETest(f)
{
    [E2EFact]
    public async Task Admin_pages_load_for_the_first_registered_account()
    {
        var s = await OpenAsync(F.Admin, "/admin", waitForWorkbench: false);
        var p = s.Page;
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Server administration" })).ToBeVisibleAsync();
        await Expect(p.Locator(".admin-stat").Filter(new() { HasTextString = "Users" }).Locator(".v")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"^[3-9]|\d\d"));

        // Every tab renders its content.
        await Expect(p.Locator(".admin-table").GetByText(F.Alice.Username)).ToBeVisibleAsync();
        await Expect(p.Locator(".admin-table tr").Filter(new() { HasTextString = F.Admin.Username }).Locator(".pill.admin")).ToBeVisibleAsync();
        foreach (var tab in new[] { "Organizations & teams", "Projects", "Audit log", "Backups" })
        {
            await p.Locator(".mud-tab").Filter(new() { HasTextString = tab }).ClickAsync();
            await Expect(p.Locator(".mud-tab.mud-tab-active")).ToContainTextAsync(tab);
            await Expect(p.Locator(".mud-tabs-panels .admin-panel, .mud-tabs-panels .admin-toolbar").First).ToBeVisibleAsync();
        }
        await p.Locator(".mud-tab").Filter(new() { HasTextString = "Audit log" }).ClickAsync();
        await Expect(p.Locator(".mud-tabs-panels").GetByText(F.Bob.Username).First).ToBeVisibleAsync();   // user.register entries

        // The admin-only download endpoints answer for an admin.
        var csv = await p.Context.APIRequest.GetAsync("/admin/api/audit.csv");
        Assert.Equal(200, csv.Status);
        Assert.Contains(F.Alice.Username, await csv.TextAsync());

        // The workbench's account menu links to the admin pages.
        await p.GotoAsync("/");
        await s.WaitForWorkbenchAsync();
        await p.Locator("header.titlebar .avatar.me").ClickAsync();
        await Expect(p.Locator(".mud-popover-open").GetByText("Administration", new() { Exact = false }).First).ToBeVisibleAsync();
    }

    [E2EFact]
    public async Task Admin_pages_are_forbidden_for_a_normal_user()
    {
        var s = await OpenAsync(F.Alice, "/admin", waitForWorkbench: false);
        var p = s.Page;
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Administrators only" })).ToBeVisibleAsync();
        await Expect(p.GetByRole(AriaRole.Heading, new() { Name = "Server administration" })).ToHaveCountAsync(0);
        await Expect(p.Locator(".admin-table")).ToHaveCountAsync(0);

        var csv = await p.Context.APIRequest.GetAsync("/admin/api/audit.csv");
        Assert.Equal(404, csv.Status);
        var backup = await p.Context.APIRequest.GetAsync("/admin/api/backup");
        Assert.Equal(404, backup.Status);

        await p.GotoAsync("/");
        await s.WaitForWorkbenchAsync();
        await p.Locator("header.titlebar .avatar.me").ClickAsync();
        await Expect(p.Locator(".mud-popover-open .mud-menu-item").Filter(new() { HasTextString = "Sign out" })).ToBeVisibleAsync();
        await Expect(p.Locator(".mud-popover-open").GetByText("Administration")).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task Admin_api_requires_sign_in()
    {
        var s = await OpenAsync(null, "/login", waitForWorkbench: false);
        var r = await s.Page.Context.APIRequest.GetAsync("/admin/api/audit.csv", new() { MaxRedirects = 0 });
        Assert.True(r.Status is 302 or 401, $"anonymous got {r.Status}");
    }
}
