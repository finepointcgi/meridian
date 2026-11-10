using System.Collections.Concurrent;
using Meridian.Core.CodeSync;
using Meridian.Core.Model;
using Meridian.Data;
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

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<ICodeReader, Reader>();
        services.AddSingleton<ICodeModelApplier, Applier>();
    }

    private sealed class Reader : ICodeReader
    {
        public string Language => "C#";
        public bool Handles(string path) => path.EndsWith(".cs", StringComparison.Ordinal);
        public CodeModel Read(IReadOnlyList<CodeFile> files, CancellationToken ct = default) => new("C#", [], []);
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
            project, Guid.NewGuid(), "acme/sat", "main", "0123456789abcdef0123456789abcdef01234567", "https://git.example/acme/sat/commit/0123456",
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
}
