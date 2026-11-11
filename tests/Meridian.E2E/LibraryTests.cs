using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>
/// Used projects / shared model libraries (docs/LIBRARIES.md): a "Parts" library project with a "Standard parts" package is used
/// from the Vehicle sample, a part is typed by a library block, the library gets a new version, the update badge and review
/// appear, and after the update the part is suspect.
/// </summary>
public sealed class LibraryTests(ServerFixture f) : E2ETest(f)
{
    private static async Task PickAsync(IPage p, ILocator scope, string testId, string option)
    {
        await Expect(p.Locator(".mud-popover-open .mud-list-item")).ToHaveCountAsync(0);   // the previous list has closed
        await scope.Locator($"div.mud-input:has(input[data-testid={testId}])").ClickAsync();
        await p.Locator(".mud-popover-open .mud-list-item").Filter(new() { HasTextString = option }).First.ClickAsync();
    }

    /// <summary>The library project, written through the store as another team would have (v1 committed).</summary>
    private async Task<(Guid Project, string Name, string WheelId)> CreateLibraryAsync()
    {
        var db = F.App.Services.GetRequiredService<IMeridianStore>();
        var alice = (await db.FindUserAsync(F.Alice.Username))!;
        var m = new ModelRepository("Parts");
        var pkg = m.Create(m.Root, ElementKind.Package, "Standard parts");
        var wheel = m.Create(pkg, ElementKind.Block, "Wheel");
        m.Set(wheel, FieldKeys.Documentation, "18 inch alloy wheel");
        m.Create(pkg, ElementKind.Block, "Brake disc");
        var name = "Parts " + Guid.NewGuid().ToString("N")[..6];
        var json = ProjectFile.Serialize(m);
        var id = await db.CreateProjectAsync(name, json, alice.Id);
        await db.CommitVersionAsync(id, "Standard parts v1", json, alice.Id);
        return (id, name, wheel.Id);
    }

    private async Task CommitV2Async(Guid project, string wheelId)
    {
        var db = F.App.Services.GetRequiredService<IMeridianStore>();
        var alice = (await db.FindUserAsync(F.Alice.Username))!;
        var m = ProjectFile.Deserialize((await db.LoadProjectAsync(project))!.Value.ModelJson);
        m.Set(m.Find(wheelId)!, FieldKeys.Documentation, "19 inch alloy wheel");
        var json = ProjectFile.Serialize(m);
        await db.SaveProjectAsync(project, json, alice.Id);
        await db.CommitVersionAsync(project, "Bigger wheel", json, alice.Id);
    }

    [E2EFact]
    public async Task Vehicle_uses_a_parts_library_types_a_part_by_it_and_reviews_an_update()
    {
        var (library, libraryName, wheelId) = await CreateLibraryAsync();
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Vehicle " + Guid.NewGuid().ToString("N")[..6]);

        // Project ▸ Used projects… ▸ the library's "Standard parts" at v1.
        await p.ProjectMenuAsync("Used projects…");
        var dlg = p.MudDialog();
        await Expect(dlg.Locator("[data-testid=lib-empty]")).ToBeVisibleAsync();
        await PickAsync(p, dlg, "lib-project", libraryName);
        await Expect(dlg.Locator("input[data-testid=lib-package]")).ToHaveValueAsync("Standard parts");
        await Expect(dlg.Locator("input[data-testid=lib-pin]")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("^v1 · "));
        await dlg.Locator("button[data-testid=lib-save]").ClickAsync();
        await Expect(dlg.Locator("[data-testid=lib-usage]")).ToHaveCountAsync(1);
        await Expect(dlg.Locator("[data-testid=lib-usage]")).ToContainTextAsync("v1");
        await dlg.Locator("button[data-testid=lib-close]").ClickAsync();

        // The library shows in the tree with its pin; its elements are read-only.
        await Expect(p.Tree().Locator("[data-testid=lib-badge]")).ToContainTextAsync("v1");
        await p.Tree().Locator("[data-testid=lib-badge]").ClickAsync();
        await p.Tree().GetByText("Wheel", new() { Exact = true }).First.ClickAsync();
        await Expect(p.Inspector().Locator("[data-testid=inspector-library]")).ToContainTextAsync(libraryName);
        await Expect(p.Inspector().Locator("[data-testid=open-in-source]")).ToBeVisibleAsync();

        // A part of the vehicle typed by the library's Wheel block.
        await p.Tree().GetByText("Vehicle", new() { Exact = true }).First.ClickAsync(new() { Button = MouseButton.Right });
        await p.Locator(".ctx-menu .ctx-item").Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex("^Property$") }).DispatchEventAsync("click");
        await Expect(p.Inspector()).ToContainTextAsync("Property · in Vehicle");
        var name = p.Inspector().GetByLabel("Name", new() { Exact = true });
        await name.FillAsync("frontLeft");
        await name.PressAsync("Tab");
        await p.Inspector().Locator(".mud-select").Filter(new() { Has = p.Locator("label", new() { HasTextString = "Type" }) }).First.ClickAsync();
        await p.Locator(".mud-popover-open .mud-list-item").Filter(new() { HasTextString = "Wheel" }).First.ClickAsync();
        await Expect(p.Inspector().GetByText("Standard parts::Wheel")).ToBeVisibleAsync();

        // The library gets a new version: "Check for library updates" shows the badge, and the review the change.
        await CommitV2Async(library, wheelId);
        await p.RibbonAsync("Used projects");
        await Expect(p.Locator("[data-testid=lib-updates-badge]")).ToContainTextAsync("Library update (1)");
        await p.Locator("[data-testid=lib-updates-badge]").ClickAsync();
        var review = p.MudDialog();
        await Expect(review.Locator("[data-testid=lib-review]")).ToContainTextAsync("1 changed");
        await Expect(review.Locator("[data-testid=lib-affected]")).ToContainTextAsync("frontLeft");
        await Expect(review).ToContainTextAsync("19 inch alloy wheel");
        await review.Locator("button[data-testid=lib-accept]").ClickAsync();
        await Expect(p.Locator("[data-testid=lib-updates-badge]")).ToHaveCountAsync(0);
        await Expect(p.Tree().Locator("[data-testid=lib-badge]")).ToContainTextAsync("v2");

        // The part that uses the changed block is now suspect.
        await p.GetByPlaceholder("Search elements…").FillAsync("frontLeft");   // the rebuilt model's tree starts collapsed
        await p.Tree().GetByText("frontLeft : Wheel").First.ClickAsync();
        await Expect(p.Inspector().Locator("[data-testid=inspector-library-suspect]")).ToContainTextAsync("Wheel changed in");
        await p.Inspector().Locator("button[data-testid=clear-library-suspect]").ClickAsync();
        await Expect(p.Inspector().Locator("[data-testid=inspector-library-suspect]")).ToHaveCountAsync(0);
    }
}
