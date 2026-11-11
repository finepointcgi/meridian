using Meridian.Core.CodeSync;
using Meridian.Data;
using Meridian.Web.CodeSync.Conformance;
using Meridian.Web.CodeSync.Hosts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>
/// Architecture checks end to end: a push goes through the app's code-sync runner in-process (the scripted reader returns
/// code where Engine calls BrakeSystem, which the Vehicle sample does not connect), the check that follows the run stores
/// the code, and "Check architecture" (⌘K) lists the violation and draws it on the diagram.
/// </summary>
public sealed class ArchitectureTests(ServerFixture f) : E2ETest(f)
{
    [E2EFact]
    public async Task A_violating_push_is_listed_in_the_architecture_tab_and_drawn_on_the_diagram()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        var name = "Architecture " + Guid.NewGuid().ToString("N")[..6];
        await p.NewProjectAsync("Vehicle sample", name);

        var db = F.App.Services.GetRequiredService<IMeridianStore>();
        var alice = (await db.FindUserAsync(F.Alice.Username))!;
        var project = (await db.ListProjectsAsync(alice.Id)).Single(x => x.Name == name).Id;
        // A connected repository (statuses off: no git host in tests).
        var connection = Guid.NewGuid();
        var config = new CodeRepoConfig { Host = GitHostKind.GitHub, Owner = "acme", Repo = "car", PostStatus = false };
        await db.SaveIntegrationAsync(new IntegrationConnection(connection, project, CodeRepoKinds.CodeRepo, "acme/car", config.ToJson(), null, DateTime.UtcNow));

        var path = $"src/{Guid.NewGuid():N}/Engine.cs";
        CodeSyncE2E.Code[path] = new CodeModel("C#",
        [
            new CodeType("Car.Engine", "Engine", "Car", CodeTypeKind.Class, path, 7, null, [], [], [], ["Car.BrakeSystem", "Car.FuelTank"])
            {
                // Engine creates a BrakeSystem on line 15 and calls it on line 22.
                DependencyLocations =
                [
                    new CodeDependencyUse("Car.BrakeSystem", path, 15, CodeDependencyKind.New),
                    new CodeDependencyUse("Car.BrakeSystem", path, 22, CodeDependencyKind.Call),
                    new CodeDependencyUse("Car.FuelTank", path, 30, CodeDependencyKind.Field),
                ],
            },
            new CodeType("Car.BrakeSystem", "BrakeSystem", "Car", CodeTypeKind.Class, "src/BrakeSystem.cs", 3, null, [], [], [], []),
            new CodeType("Car.FuelTank", "FuelTank", "Car", CodeTypeKind.Class, "src/FuelTank.cs", 3, null, [], [], [], []),
        ], []);
        try
        {
            var outcome = await F.App.Services.GetRequiredService<ICodeSyncRunner>().RunAsync(new CodeSyncRequest(
                project, connection, "acme/car", "main", "89abcdef0123456789abcdef0123456789abcdef", "https://github.com/acme/car/commit/89abcdef0123456789abcdef0123456789abcdef",
                "Dana Dev", "Brake from the engine", [new CodeFile(path, "class Engine {}")], IsFullSnapshot: true));
            Assert.Equal(CodeSyncStatus.NoChanges, outcome.Status);
            var checks = F.App.Services.GetRequiredService<ArchitectureCheckService>();
            await checks.Idle;
            Assert.Equal("1 violation", (await checks.Store.FindAsync(project, connection))!.Summary);
        }
        finally { CodeSyncE2E.Code.TryRemove(path, out _); }

        // ⌘K ▸ Check architecture
        await p.Locator(".cmdk").ClickAsync();
        await p.Locator(".cmdp-input input").FillAsync("Check architecture");
        await p.Keyboard.PressAsync("Enter");

        var tab = p.ViewTab();
        var row = tab.Locator(".arch-row[data-rule=dependency]");
        await Expect(row).ToHaveCountAsync(1);
        await Expect(row).ToContainTextAsync("Engine uses BrakeSystem");
        await Expect(row.Locator("a.loc")).ToHaveAttributeAsync("href", $"https://github.com/acme/car/blob/89abcdef0123456789abcdef0123456789abcdef/{path}#L7");
        // The violation links the exact lines that break the rule.
        var uses = row.Locator(".arch-uses a.use");
        await Expect(uses).ToHaveCountAsync(2);
        await Expect(uses.First).ToHaveAttributeAsync("href", $"https://github.com/acme/car/blob/89abcdef0123456789abcdef0123456789abcdef/{path}#L15");
        await Expect(uses.First).ToHaveTextAsync("line 15 (new)");
        await Expect(uses.Nth(1)).ToHaveAttributeAsync("href", $"https://github.com/acme/car/blob/89abcdef0123456789abcdef0123456789abcdef/{path}#L22");
        await Expect(tab.Locator("h3.arch-rule")).ToContainTextAsync("Dependency not allowed");
        await p.RibbonTabAsync("code");
        await Expect(p.Locator("nav.ribbon button.arch-ribbon")).ToBeVisibleAsync();

        // "Show on diagram": the red dashed edge between Engine and BrakeSystem.
        await row.GetByRole(AriaRole.Button, new() { Name = "Show on diagram" }).ClickAsync();
        await Expect(p.Canvas().Locator("g.arch-violation line")).ToHaveCountAsync(1);
        if (Environment.GetEnvironmentVariable("MERIDIAN_E2E_SCREENSHOTS") is { Length: > 0 } shots)
            await p.ScreenshotAsync(new() { Path = Path.Combine(shots, "architecture-violation.png") });
    }
}
