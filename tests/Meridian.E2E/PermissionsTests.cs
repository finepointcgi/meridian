using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>Per-package permissions (docs/PERMISSIONS.md) with two browsers: Alice restricts a package, Bob can't edit it, then can't see it.</summary>
public sealed class PermissionsTests(ServerFixture f) : E2ETest(f)
{
    private static async Task SelectAsync(IPage p, ILocator dialog, string testId, string option)
    {
        await Expect(p.Locator(".mud-popover-open .mud-list-item")).ToHaveCountAsync(0);   // the previous list has closed
        await dialog.Locator($"div.mud-input:has(input[data-testid={testId}])").ClickAsync();
        await p.Locator(".mud-popover-open .mud-list-item").Filter(new() { HasTextString = option }).First.ClickAsync();
    }

    /// <summary>Alice: right-click the package ▸ Permissions… ▸ grant Bob <paramref name="level"/>.</summary>
    private async Task GrantAsync(IPage a, string package, string level)
    {
        await a.Tree().GetByText(package, new() { Exact = true }).First.ClickAsync(new() { Button = MouseButton.Right });
        await a.Locator(".ctx-menu .ctx-item").Filter(new() { HasTextString = "Permissions…" }).ClickAsync();   // the menu fits on the screen (long lists are submenus)
        var dlg = a.MudDialog();
        await Expect(dlg.Locator("[data-testid=perm-mine]")).ToContainTextAsync("manage");
        await SelectAsync(a, dlg, "perm-principal", F.Bob.DisplayName);
        await SelectAsync(a, dlg, "perm-level", level);
        await dlg.Locator("button[data-testid=perm-grant]").ClickAsync();
        await Expect(dlg.Locator("[data-testid=perm-rule]").Filter(new() { HasTextString = F.Bob.DisplayName })).ToHaveCountAsync(1);
        await dlg.Locator("button[data-testid=perm-done]").ClickAsync();
    }

    private async Task<string> ApiAsync(TestUser user, string path)
    {
        using var http = new HttpClient { BaseAddress = new Uri(F.App.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user.Username}:{user.Password}")));
        var response = await http.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [E2EFact]
    public async Task Alice_restricts_a_package_and_Bob_can_not_edit_it_then_can_not_see_it()
    {
        var alice = await OpenAsync(F.Alice);
        var a = alice.Page;
        var project = "Perm " + Guid.NewGuid().ToString("N")[..6];
        await a.NewProjectAsync("Vehicle sample", project);
        await a.Locator("header.titlebar button.rbtn", new() { HasTextString = "Share" }).ClickAsync();
        var share = a.MudDialog();
        await share.GetByPlaceholder("Username").FillAsync(F.Bob.Username);
        await share.GetByRole(AriaRole.Button, new() { Name = "Invite" }).ClickAsync();
        await Expect(share.GetByText(F.Bob.DisplayName)).ToBeVisibleAsync();
        await share.GetByRole(AriaRole.Button, new() { Name = "Done" }).ClickAsync();

        // Structure becomes read-only for Bob.
        await GrantAsync(a, "Structure", "Read");
        await Expect(a.Tree().Locator("[data-testid=perm-badge]")).ToHaveCountAsync(1);

        var bob = await OpenAsync(F.Bob);
        var b = bob.Page;
        await b.ProjectMenuAsync("Projects…");
        await b.MudDialog().GetByText(project).First.ClickAsync();
        await bob.WaitForWorkbenchAsync(project);
        await Expect(b.Tree().Locator("[data-testid=perm-badge]")).ToHaveCountAsync(1);
        await Expect(b.Locator("[data-testid=diagram-read-only]")).ToContainTextAsync("Structure");   // the structure BDD is the active diagram
        await Expect(b.Locator(".toolbox")).ToBeHiddenAsync();

        // Bob's rename of a block in it is refused by the server and put back.
        await b.Tree().GetByText("FuelTank", new() { Exact = true }).First.ClickAsync();
        await Expect(b.Locator("[data-testid=inspector-read-only]")).ToBeVisibleAsync();
        await b.Tree().GetByText("FuelTank", new() { Exact = true }).First.ClickAsync(new() { Button = MouseButton.Right });
        await b.Locator(".ctx-menu .ctx-item").Filter(new() { HasTextString = "Rename…" }).DispatchEventAsync("click");
        var prompt = b.MudDialog();
        await prompt.Locator("input").First.FillAsync("Hacked tank");
        await prompt.GetByRole(AriaRole.Button, new() { Name = "OK" }).ClickAsync();
        await Expect(b.StatusMessage()).ToContainTextAsync("“Structure” is read-only for you");
        await Expect(b.Tree().GetByText("FuelTank", new() { Exact = true })).ToHaveCountAsync(1);
        await Expect(a.Tree().GetByText("Hacked tank", new() { Exact = true })).ToHaveCountAsync(0);

        // Then hidden: Bob's tree, diagrams and API lose its contents at once (placeholders where blocks were shown).
        await GrantAsync(a, "Structure", "None");
        await Expect(b.Tree().GetByText("FuelTank", new() { Exact = true })).ToHaveCountAsync(0);
        await Expect(b.Tree().GetByText("Structure", new() { Exact = true })).ToHaveCountAsync(1);
        await b.Locator(".tabstrip .tab").Filter(new() { HasTextString = "Vehicle Requirements" }).First.ClickAsync();
        await Expect(b.Locator(".diagram-host svg.main .redacted-shape")).ToHaveCountAsync(3);
        var html = await b.ContentAsync();
        foreach (var secret in new[] { "FuelTank", "BrakeSystem", "FuelInterface" }) Assert.DoesNotContain(secret, html);
        await Expect(a.Tree().GetByText("FuelTank", new() { Exact = true })).ToHaveCountAsync(1);   // Alice (owner) is unaffected

        // The REST API filters too.
        using var projects = JsonDocument.Parse(await ApiAsync(F.Bob, "/api/projects"));
        var id = projects.RootElement.EnumerateArray().First(p => p.GetProperty("name").GetString() == project).GetProperty("@id").GetString();
        Assert.DoesNotContain("FuelTank", await ApiAsync(F.Bob, $"/api/projects/{id}/commits/head/elements"));
        Assert.Contains("FuelTank", await ApiAsync(F.Alice, $"/api/projects/{id}/commits/head/elements"));
    }
}
