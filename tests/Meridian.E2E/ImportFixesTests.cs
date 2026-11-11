using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Web.CodeSync.Hosts;
using Microsoft.Extensions.DependencyInjection;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>
/// UI fixes from syncing Meridian's own repository: ⌘K "Connect a code repository" opens the Code repositories tab, the
/// "Review" link of a synced repository opens Code changes, and explorer search keeps to the active filter chip.
/// </summary>
public sealed class ImportFixesTests(ServerFixture f) : E2ETest(f)
{
    [E2EFact]
    public async Task Palette_connect_a_code_repository_opens_the_code_repositories_tab_and_review_opens_code_changes()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        var name = "Import fixes " + Guid.NewGuid().ToString("N")[..6];
        await p.NewProjectAsync("Vehicle sample", name);

        var db = F.App.Services.GetRequiredService<IMeridianStore>();
        var alice = (await db.FindUserAsync(F.Alice.Username))!;
        var project = (await db.ListProjectsAsync(alice.Id)).Single(x => x.Name == name).Id;
        var config = new CodeRepoConfig
        {
            Host = GitHostKind.GitHub, Owner = "acme", Repo = "car", PostStatus = false, PreviewPullRequests = false,
            LastSync = new() { At = DateTime.UtcNow, Commit = "89abcdef0123", Status = "awaiting-review", Summary = "1 added", ReviewUrl = $"/?project={project}&panel=code-changes" },
        };
        await db.SaveIntegrationAsync(new IntegrationConnection(Guid.NewGuid(), project, CodeRepoKinds.CodeRepo, "acme/car", config.ToJson(), null, DateTime.UtcNow));

        // ⌘K ▸ Connect a code repository…
        await p.Locator(".cmdk").ClickAsync();
        await p.Locator(".cmdp-input input").FillAsync("Connect a code repository");
        await p.Keyboard.PressAsync("Enter");
        var dialog = p.MudDialog();
        await Expect(dialog.Locator(".mud-tab.mud-tab-active")).ToHaveTextAsync("Code repositories");
        await Expect(dialog).ToContainTextAsync("Changes to review");

        // The token label asks only for what these settings need.
        await dialog.Locator("button.code-repo-edit").ClickAsync();
        await Expect(dialog.GetByLabel("Access token (Contents: read)")).ToHaveCountAsync(1);
        await dialog.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();

        // "Review" closes Integrations and opens Code changes, like the title-bar badge.
        await dialog.Locator("a.code-repo-review").ClickAsync();
        await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);
        await Expect(p.Tabs().Filter(new() { HasTextString = "Code changes" })).ToHaveCountAsync(1);
    }

    [E2EFact]
    public async Task Explorer_search_keeps_to_the_diagrams_filter()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Search filter " + Guid.NewGuid().ToString("N")[..6]);

        var sample = Meridian.Core.Samples.SampleModels.Vehicle();
        var diagrams = Math.Min(150, sample.Search("Vehicle").Count(e => e.Kind == ElementKind.Diagram));
        Assert.True(diagrams > 0);

        await p.Locator(".chips .chip").Filter(new() { HasTextString = "Diagrams" }).ClickAsync();
        await p.GetByPlaceholder("Search elements…").FillAsync("Vehicle");
        await Expect(p.Tree().Locator(".section-label").First).ToContainTextAsync($"{diagrams} result{(diagrams == 1 ? "" : "s")} in Diagrams");

        // Back to All: the blocks and requirements named Vehicle are found again.
        await p.Locator(".chips .chip").Filter(new() { HasTextString = "All" }).ClickAsync();
        await Expect(p.Tree().Locator(".section-label").First).Not.ToContainTextAsync("in Diagrams");
    }
}
