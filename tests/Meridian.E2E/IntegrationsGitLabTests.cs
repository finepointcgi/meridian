using Meridian.Data;
using Meridian.Web.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>Integrations ▸ Issue trackers: adding a GitLab Issues connection in the real dialog (nothing is contacted).</summary>
public sealed class IntegrationsGitLabTests(ServerFixture f) : E2ETest(f)
{
    [E2EFact]
    public async Task A_GitLab_tracker_is_added_with_setup_help_labels_and_an_encrypted_token()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        var name = "GitLab issues " + Guid.NewGuid().ToString("N")[..6];
        await p.NewProjectAsync("Vehicle sample", name);

        await p.ProjectMenuAsync("Integrations");
        var dlg = p.MudDialog();
        await dlg.GetByRole(AriaRole.Button, new() { Name = "Add connection" }).ClickAsync();
        await dlg.Locator(".mud-select").First.ClickAsync();
        await p.Locator(".mud-popover-open .mud-list-item").Filter(new() { HasTextString = "GitLab Issues" }).ClickAsync();

        await Expect(dlg.Locator(".gitlab-setup")).ToContainTextAsync("project access token");
        await Expect(dlg.GetByLabel("Base URL")).ToHaveAttributeAsync("placeholder", "https://gitlab.com (or https://gitlab.example.com)");
        await dlg.GetByLabel("Base URL").FillAsync("https://gitlab.com");
        await dlg.GetByLabel("Project (group/project path or numeric id)").FillAsync("acme/vehicles/car");
        await dlg.GetByLabel("Labels for new issues (comma-separated, optional)").FillAsync("meridian, requirement");
        await dlg.GetByLabel("Access token (scope: api)").FillAsync("glpat-e2e-not-a-real-token-123");
        await dlg.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        await p.SnackbarAsync("Issue tracker saved");
        await Expect(dlg.Locator(".integration-row").Filter(new() { HasTextString = "GitLab Issues" })).ToContainTextAsync("acme/vehicles/car");

        var db = F.App.Services.GetRequiredService<IMeridianStore>();
        var alice = (await db.FindUserAsync(F.Alice.Username))!;
        var project = (await db.ListProjectsAsync(alice.Id)).Single(x => x.Name == name).Id;
        var row = Assert.Single(await db.ListIntegrationsAsync(project), c => c.Kind == IntegrationKinds.IssueTracker);
        var cfg = IssueTrackerConfig.Parse(row.ConfigJson);
        Assert.Equal((TrackerTypes.GitLab, "acme/vehicles/car", "meridian, requirement"), (cfg.Type, cfg.Project, cfg.Labels));
        Assert.DoesNotContain("glpat-e2e", row.ProtectedSecret);
        Assert.DoesNotContain("glpat-e2e", row.ConfigJson);
    }
}
