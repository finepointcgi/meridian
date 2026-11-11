using System.Collections.Concurrent;
using Meridian.Core.CodeSync;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Web.CodeSync.Hosts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>
/// Test-only code-sync seams for the shared E2E server: a reader for *.cs files and a scripted mapper. Tests trigger a
/// sync by calling the app's <see cref="ICodeSyncRunner"/> in-process (no git host, no webhook, no network).
/// </summary>
public static class CodeSyncE2E
{
    public static readonly ConcurrentQueue<Func<ModelRepository, CodeSyncChanges>> Script = new();

    /// <summary>What the reader returns for a push whose first file has this path (default: no types).</summary>
    public static readonly ConcurrentDictionary<string, CodeModel> Code = new(StringComparer.Ordinal);

    /// <summary>
    /// Connects <paramref name="repo"/> to the project with "Review the first import too" on, so even its first sync waits
    /// on the code-sync branch (most tests here are about the review; a first import otherwise goes straight into main).
    /// Statuses and previews are off: there is no git host in tests.
    /// </summary>
    public static async Task<Guid> ReviewFirstAsync(IServiceProvider services, Guid project, string repo)
    {
        var id = Guid.NewGuid();
        var parts = repo.Split('/');
        var config = new CodeRepoConfig { Owner = parts[0], Repo = parts[1], PostStatus = false, PreviewPullRequests = false, ReviewFirstImport = true };
        await services.GetRequiredService<IMeridianStore>().SaveIntegrationAsync(new IntegrationConnection(id, project, CodeRepoKinds.CodeRepo, repo, config.ToJson(), null, DateTime.UtcNow));
        return id;
    }

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<ICodeReader, Reader>();
        services.AddSingleton<ICodeModelApplier, Applier>();
    }

    private sealed class Reader : ICodeReader
    {
        public string Language => "C#";
        public bool Handles(string path) => path.EndsWith(".cs", StringComparison.Ordinal);
        public CodeModel Read(IReadOnlyList<CodeFile> files, CancellationToken ct = default) =>
            files.Count > 0 && Code.TryGetValue(files[0].Path, out var code) ? code : new("C#", [], []);
    }

    private sealed class Applier : ICodeModelApplier
    {
        public CodeSyncChanges Apply(ModelRepository model, CodeModel code, CodeSyncOptions options) =>
            Script.TryDequeue(out var step) ? step(model) : new CodeSyncChanges([], [], [], []);
    }
}

public sealed class CodeSyncTests(ServerFixture f) : E2ETest(f)
{
    [E2EFact]
    public async Task A_push_lands_on_a_review_branch_and_is_accepted_into_main()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        var name = "Code sync " + Guid.NewGuid().ToString("N")[..6];
        await p.NewProjectAsync("Vehicle sample", name);

        var db = F.App.Services.GetRequiredService<IMeridianStore>();
        var alice = (await db.FindUserAsync(F.Alice.Username))!;
        var project = (await db.ListProjectsAsync(alice.Id)).Single(x => x.Name == name).Id;

        // A push (through the runner, as the git host integration would after fetching the files).
        CodeSyncE2E.Script.Enqueue(m =>
        {
            var structure = m.OfKind(ElementKind.Package).First(x => x.Name == "Structure");
            var battery = m.Create(structure, ElementKind.Block, "Battery");
            return new CodeSyncChanges([battery.Id], [], [], []);
        });
        var outcome = await F.App.Services.GetRequiredService<ICodeSyncRunner>().RunAsync(new CodeSyncRequest(
            project, await CodeSyncE2E.ReviewFirstAsync(F.App.Services, project, "acme/sat"), "acme/sat", "main", "0123456789abcdef0123456789abcdef01234567", "https://git.example/acme/sat/commit/0123456",
            "Dana Dev", "Add battery", [new CodeFile("src/Battery.cs", "class Battery {}")], IsFullSnapshot: true));
        Assert.Equal(CodeSyncStatus.AwaitingReview, outcome.Status);

        // The badge appears on main; the review lists the new block.
        var badge = p.Locator("[data-testid=code-changes-badge]");
        await Expect(badge).ToContainTextAsync("Code changes (1)");
        await badge.ClickAsync();
        var review = p.ViewTab();
        await Expect(review.Locator(".chg-run")).ToContainTextAsync("Add battery");
        var row = review.Locator(".chg-row[data-kind=added]");
        await Expect(row).ToContainTextAsync("Battery");
        await Expect(p.Tree().GetByText("Battery", new() { Exact = true })).ToHaveCountAsync(0);

        // "Show on diagram": the new block only exists on the review branch, so it opens there, highlighted.
        await row.GetByRole(AriaRole.Button, new() { Name = "Show on diagram" }).ClickAsync();
        await Expect(p.Locator(".vc-branch .name")).ToHaveTextAsync("code-sync/acme/sat");
        await Expect(p.Canvas().Locator("rect.chg-outline.chg-added")).ToHaveCountAsync(1);
        await Expect(p.Canvas().Locator(".chg-legend")).ToContainTextAsync("Added 1");
        if (Environment.GetEnvironmentVariable("MERIDIAN_E2E_SCREENSHOTS") is { Length: > 0 } shots)
            await p.ScreenshotAsync(new() { Path = Path.Combine(shots, "code-sync-highlight.png") });

        // Back to the review (the badge), accept the change into main.
        await badge.ClickAsync();
        row = review.Locator(".chg-row[data-kind=added]");
        await row.Locator("input[type=checkbox]").CheckAsync();
        await review.GetByRole(AriaRole.Button, new() { Name = "Accept selected" }).ClickAsync();
        await p.SnackbarAsync("Accepted 1 code change into main.");
        await Expect(review).ToContainTextAsync("No code changes to review");
        await Expect(badge).ToHaveCountAsync(0);
        await Expect(p.Canvas().Locator(".chg-legend")).ToHaveCountAsync(0);
        Assert.Contains("\"Battery\"", (await db.LoadProjectAsync(project))!.Value.ModelJson);   // merged into main
    }

    [E2EFact]
    public async Task The_first_import_lands_on_main_and_the_next_push_shows_a_banner_on_main()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        var name = "First import " + Guid.NewGuid().ToString("N")[..6];
        await p.NewProjectAsync("Vehicle sample", name);

        var db = F.App.Services.GetRequiredService<IMeridianStore>();
        var alice = (await db.FindUserAsync(F.Alice.Username))!;
        var project = (await db.ListProjectsAsync(alice.Id)).Single(x => x.Name == name).Id;
        // A connected repository with the default settings (statuses off: no git host in tests).
        var connection = Guid.NewGuid();
        var config = new CodeRepoConfig { Owner = "acme", Repo = "sat", PostStatus = false, PreviewPullRequests = false, ConnectedBy = alice.Id };
        await db.SaveIntegrationAsync(new IntegrationConnection(connection, project, CodeRepoKinds.CodeRepo, "acme/sat", config.ToJson(), null, DateTime.UtcNow));
        var runner = F.App.Services.GetRequiredService<ICodeSyncRunner>();
        CodeSyncRequest Push(string commit, string message) => new(project, connection, "acme/sat", "main", commit, null, "Dana Dev", message,
            [new CodeFile("src/Sat.cs", "class Sat {}")], IsFullSnapshot: true) { TriggeredBy = alice.Id };
        Func<ModelRepository, CodeSyncChanges> Block(string block) => m =>
        {
            var structure = m.OfKind(ElementKind.Package).First(x => x.Name == "Structure");
            return new CodeSyncChanges([m.Create(structure, ElementKind.Block, block).Id], [], [], []);
        };

        // The first sync: straight into main, visible at once in the open tab; nothing waits for review.
        CodeSyncE2E.Script.Enqueue(Block("Battery"));
        var first = await runner.RunAsync(Push("0123456789abcdef0123456789abcdef01234567", "Add battery"));
        Assert.Equal(CodeSyncStatus.Accepted, first.Status);
        Assert.StartsWith("Imported into main", first.Summary);
        await Expect(p.Tree().GetByText("Battery", new() { Exact = true })).ToHaveCountAsync(1);
        await Expect(p.Locator(".vc-branch .name")).ToHaveTextAsync("main");
        await Expect(p.Locator("[data-testid=code-review-banner]")).ToHaveCountAsync(0);
        Assert.DoesNotContain(await db.ListBranchesAsync(project), b => b.Name == "code-sync/acme/sat");

        // The next push waits for review: a banner on main says so.
        CodeSyncE2E.Script.Enqueue(Block("Radio"));
        Assert.Equal(CodeSyncStatus.AwaitingReview, (await runner.RunAsync(Push("89abcdef0123456789abcdef0123456789abcdef", "Add radio"))).Status);
        var banner = p.Locator("[data-testid=code-review-banner]");
        await Expect(banner).ToContainTextAsync("1 code change from acme/sat is waiting for review (commit 89abcde)");
        await Expect(p.Tree().GetByText("Radio", new() { Exact = true })).ToHaveCountAsync(0);
        if (Environment.GetEnvironmentVariable("MERIDIAN_E2E_SCREENSHOTS") is { Length: > 0 } shots)
            await p.ScreenshotAsync(new() { Path = Path.Combine(shots, "code-sync-banner.png") });

        // Accept all into main (after the confirmation) brings it in; the banner goes.
        await banner.GetByRole(AriaRole.Button, new() { Name = "Accept all into main" }).ClickAsync();
        await p.MudDialog().GetByRole(AriaRole.Button, new() { Name = "Accept" }).ClickAsync();
        await p.SnackbarAsync("Accepted 1 code change into main.");
        await Expect(banner).ToHaveCountAsync(0);
        await Expect(p.Tree().GetByText("Radio", new() { Exact = true })).ToHaveCountAsync(1);
        Assert.Contains("\"Radio\"", (await db.LoadProjectAsync(project))!.Value.ModelJson);
    }

    [E2EFact]
    public async Task A_submodule_bump_shows_in_what_changed_with_a_compare_link_and_is_accepted()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        var name = "Submodules " + Guid.NewGuid().ToString("N")[..6];
        await p.NewProjectAsync("Vehicle sample", name);

        var db = F.App.Services.GetRequiredService<IMeridianStore>();
        var alice = (await db.FindUserAsync(F.Alice.Username))!;
        var project = (await db.ListProjectsAsync(alice.Id)).Single(x => x.Name == name).Id;
        var runner = F.App.Services.GetRequiredService<ICodeSyncRunner>();
        var connection = Guid.NewGuid();
        const string v1 = "1111111111111111111111111111111111111111", v2 = "2222222222222222222222222222222222222222";
        const string compare = "https://github.com/acme/core/compare/{old}...{new}";
        CodeSyncRequest Push(string commit, string sha) => new(
            project, connection, "acme/sat", "main", commit, null, "Dana Dev", "Bump core",
            [new CodeFile("src/Sat.cs", "class Sat {}"), new CodeFile("libs/core/Battery.cs", "class Battery {}")], IsFullSnapshot: true)
        {
            Submodules = [new CodeSubmodulePin("libs/core", sha, compare)],
        };

        // The first sync pins libs/core: the repository's first import, it goes straight into main.
        CodeSyncE2E.Script.Enqueue(_ => new CodeSyncChanges([], [], [], []));
        Assert.Equal(CodeSyncStatus.Accepted, (await runner.RunAsync(Push("0123456789abcdef0123456789abcdef01234567", v1))).Status);

        // The parent repository bumps libs/core, whose new code adds a block.
        CodeSyncE2E.Script.Enqueue(m =>
        {
            var structure = m.OfKind(ElementKind.Package).First(x => x.Name == "Structure");
            return new CodeSyncChanges([m.Create(structure, ElementKind.Block, "CoreBattery").Id], [], [], []);
        });
        Assert.Equal(CodeSyncStatus.AwaitingReview, (await runner.RunAsync(Push("89abcdef0123456789abcdef0123456789abcdef", v2))).Status);

        var badge = p.Locator("[data-testid=code-changes-badge]");
        await Expect(badge).ToContainTextAsync("Code changes (2)");
        await badge.ClickAsync();
        var review = p.ViewTab();
        var bump = review.Locator(".chg-submodule[data-submodule='libs/core']");
        await Expect(bump).ToContainTextAsync("1111111 → 2222222");
        await Expect(bump.Locator("a.chg-compare")).ToHaveAttributeAsync("href", $"https://github.com/acme/core/compare/{v1}...{v2}");
        await Expect(review.Locator(".chg-row[data-kind=added]")).ToContainTextAsync("CoreBattery");
        await Expect(review.Locator(".chg-row[data-kind=changed]")).ToHaveCountAsync(0);   // the pins are shown as the bump only

        await review.Locator(".chg-select-all").CheckAsync();
        await review.GetByRole(AriaRole.Button, new() { Name = "Accept selected" }).ClickAsync();
        await p.SnackbarAsync("Accepted 2 code changes into main.");
        await Expect(review).ToContainTextAsync("No code changes to review");
        var json = (await db.LoadProjectAsync(project))!.Value.ModelJson;
        Assert.Contains($"libs/core@{v2}", json);
        Assert.Contains("CoreBattery", json);
    }

    [E2EFact]
    public async Task A_pull_request_preview_shows_as_PR_n_and_goes_away_when_merged()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        var name = "PR preview " + Guid.NewGuid().ToString("N")[..6];
        await p.NewProjectAsync("Vehicle sample", name);

        var db = F.App.Services.GetRequiredService<IMeridianStore>();
        var alice = (await db.FindUserAsync(F.Alice.Username))!;
        var project = (await db.ListProjectsAsync(alice.Id)).Single(x => x.Name == name).Id;

        // PR #7 opened: the git host integration syncs its head into the preview branch (in-process here).
        var previews = Assert.IsAssignableFrom<Meridian.Web.CodeSync.Hosts.IPullRequestPreviews>(F.App.Services.GetRequiredService<ICodeSyncRunner>());
        CodeSyncE2E.Script.Enqueue(m =>
        {
            var structure = m.OfKind(ElementKind.Package).First(x => x.Name == "Structure");
            var thruster = m.Create(structure, ElementKind.Block, "Thruster");
            return new CodeSyncChanges([thruster.Id], [], [], []);
        });
        var outcome = await previews.RunPreviewAsync(new CodeSyncRequest(
            project, Guid.NewGuid(), "acme/sat", "feature/thruster", "89abcdef0123456789abcdef0123456789abcdef", null,
            "Cleo Ray", "Add thruster", [new CodeFile("src/Thruster.cs", "class Thruster {}")], IsFullSnapshot: true), 7);
        Assert.Equal(CodeSyncStatus.AwaitingReview, outcome.Status);

        // The badge says PR #7 (no changes to accept on main); the review lists the preview, read-only.
        var badge = p.Locator("[data-testid=code-changes-badge]");
        await Expect(badge).ToContainTextAsync("PR #7");
        await Expect(badge).Not.ToContainTextAsync("Code changes (");
        await badge.ClickAsync();
        var review = p.ViewTab();
        await Expect(review.Locator("h3.chg-preview")).ToContainTextAsync("PR #7");
        await Expect(review.Locator("h3.chg-preview")).ToContainTextAsync("code-sync/acme/sat/pr-7");
        var row = review.Locator(".chg-row[data-kind=added]");
        await Expect(row).ToContainTextAsync("Thruster");
        await Expect(row.Locator("input[type=checkbox]")).ToBeDisabledAsync();
        if (Environment.GetEnvironmentVariable("MERIDIAN_E2E_SCREENSHOTS") is { Length: > 0 } shots)
            await p.ScreenshotAsync(new() { Path = Path.Combine(shots, "code-sync-pr-preview.png") });

        // Merged: the preview branch goes away (the push to main brings the code to review).
        await previews.ClosePreviewAsync(project, "acme/sat", 7, merged: true);
        await Expect(badge).ToHaveCountAsync(0);
        await Expect(review).ToContainTextAsync("No code changes to review");
        Assert.DoesNotContain(await db.ListBranchesAsync(project), b => b.Name == "code-sync/acme/sat/pr-7");
    }
}
