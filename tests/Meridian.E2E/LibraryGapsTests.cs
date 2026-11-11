using Meridian.Core.Libraries;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Libraries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>
/// Used projects (docs/LIBRARIES.md) beyond the basics: accepting an update keeps the open diagram tabs, "Open in source project"
/// shows the pinned version read-only with "Go to latest", and on a phone the model list shows the library (read-only, with its
/// pin) and the update review is a full-screen sheet without horizontal scrolling.
/// </summary>
public sealed class LibraryGapsTests(ServerFixture f) : E2ETest(f)
{
    private IMeridianStore Db => F.App.Services.GetRequiredService<IMeridianStore>();

    /// <summary>A "Parts" library (v1 committed through the store), optionally already at v2 on its main line.</summary>
    private async Task<(Guid Project, string PackageId, string WheelId)> CreateLibraryAsync(bool withV2, bool withDiagram = true)
    {
        var alice = (await Db.FindUserAsync(F.Alice.Username))!;
        var m = new ModelRepository("Parts");
        var pkg = m.Create(m.Root, ElementKind.Package, "Standard parts");
        var wheel = m.Create(pkg, ElementKind.Block, "Wheel");
        m.Set(wheel, FieldKeys.Documentation, "18 inch alloy wheel");
        if (withDiagram) m.AddNode(m.CreateDiagram(pkg, DiagramType.BlockDefinition, "Parts"), wheel, new Bounds(40, 40, 140, 70));
        var json = ProjectFile.Serialize(m);
        var id = await Db.CreateProjectAsync("Parts " + Guid.NewGuid().ToString("N")[..6], json, alice.Id);
        await Db.CommitVersionAsync(id, "Standard parts v1", json, alice.Id);
        if (withV2) await CommitV2Async(id, wheel.Id);
        return (id, pkg.Id, wheel.Id);
    }

    private async Task CommitV2Async(Guid project, string wheelId)
    {
        var alice = (await Db.FindUserAsync(F.Alice.Username))!;
        var m = ProjectFile.Deserialize((await Db.LoadProjectAsync(project))!.Value.ModelJson);
        m.Set(m.Find(wheelId)!, FieldKeys.Documentation, "19 inch alloy wheel");
        var json = ProjectFile.Serialize(m);
        await Db.SaveProjectAsync(project, json, alice.Id);
        await Db.CommitVersionAsync(project, "Bigger wheel", json, alice.Id);
    }

    /// <summary>A new Vehicle sample project (through the UI) that uses the library's package at v1 (added through the service).</summary>
    private async Task<string> VehicleUsingAsync(IPage p, Guid library, string packageId)
    {
        var name = "Vehicle " + Guid.NewGuid().ToString("N")[..6];
        await p.NewProjectAsync("Vehicle sample", name);
        var alice = (await Db.FindUserAsync(F.Alice.Username))!;
        var vehicle = (await Db.ListProjectsAsync(alice.Id)).First(x => x.Name == name).Id;
        await F.App.Services.GetRequiredService<LibraryService>().AddAsync(vehicle, library, packageId, LibraryPinKind.Version, "1", null, alice.Id);
        await Expect(p.Tree().Locator("[data-testid=lib-badge]")).ToContainTextAsync("v1");   // the open tab got the rebuilt model
        return name;
    }

    private static async Task AssertNoHorizontalScrollAsync(IPage p)
    {
        var overflow = await p.EvaluateAsync<double>("() => Math.max(document.documentElement.scrollWidth, document.body.scrollWidth) - window.innerWidth");
        Assert.True(overflow <= 0, $"the page scrolls sideways by {overflow}px");
    }

    [E2EFact]
    public async Task Accepting_an_update_with_two_tabs_open_keeps_both_and_the_active_one()
    {
        var (library, packageId, wheelId) = await CreateLibraryAsync(withV2: false);
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await VehicleUsingAsync(p, library, packageId);

        await Expect(p.Tabs()).ToHaveCountAsync(2);   // the sample opens its requirements and structure diagrams
        var names = await p.Tabs().AllInnerTextsAsync();
        await p.Tabs().First.ClickAsync();
        await Expect(p.Tabs().First).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"));

        await CommitV2Async(library, wheelId);
        await p.RibbonAsync("Used projects");   // check for updates
        await p.Locator("[data-testid=lib-updates-badge]").ClickAsync();
        var review = p.MudDialog();
        await Expect(review.Locator("[data-testid=lib-review]")).ToContainTextAsync("1 changed");
        await review.Locator("button[data-testid=lib-accept]").ClickAsync();
        await Expect(p.Tree().Locator("[data-testid=lib-badge]")).ToContainTextAsync("v2");

        await Expect(p.Tabs()).ToHaveCountAsync(2);
        Assert.Equal(names, await p.Tabs().AllInnerTextsAsync());
        await Expect(p.Tabs().First).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"));
        s.AssertNoErrors();
    }

    [E2EFact]
    public async Task Open_in_source_shows_the_pinned_version_read_only_and_go_to_latest_leaves_it()
    {
        var (library, packageId, _) = await CreateLibraryAsync(withV2: true, withDiagram: false);   // the source is at v2; the vehicle uses v1 (no diagram: the canvas closes)
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await VehicleUsingAsync(p, library, packageId);

        await p.Tree().Locator("[data-testid=lib-badge]").ClickAsync();
        await p.Tree().GetByText("Wheel", new() { Exact = true }).First.ClickAsync();
        await p.Inspector().Locator("[data-testid=open-in-source]").ClickAsync();

        var banner = p.Locator("[data-testid=pinned-view]");
        await Expect(banner).ToContainTextAsync("Viewing version 1 (pinned)");
        await Expect(banner).ToContainTextAsync("read-only");
        await Expect(p.ProjectTitle()).ToHaveTextAsync((await Db.LoadProjectAsync(library))!.Value.Name);
        await Expect(p.Inspector()).ToContainTextAsync("Wheel");
        await Expect(p.Inspector().GetByLabel("Documentation", new() { Exact = true })).ToHaveValueAsync("18 inch alloy wheel");   // v1, not the head

        await banner.Locator("[data-testid=pinned-latest]").ClickAsync();
        await Expect(p.Locator("[data-testid=pinned-view]")).ToHaveCountAsync(0);
        await p.Tree().GetByText("Wheel", new() { Exact = true }).First.ClickAsync();
        await Expect(p.Inspector().GetByLabel("Documentation", new() { Exact = true })).ToHaveValueAsync("19 inch alloy wheel");
        s.AssertNoErrors();
    }

    [E2EFact]
    public async Task Phone_tree_shows_the_library_read_only_and_the_update_review_fits_the_screen()
    {
        var (library, packageId, wheelId) = await CreateLibraryAsync(withV2: false);
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        var vehicle = await VehicleUsingAsync(p, library, packageId);
        await CommitV2Async(library, wheelId);

        await p.SetViewportSizeAsync(390, 844);
        await p.ReloadAsync();   // reopens the project, which checks for library updates
        await Expect(p.Locator(".m-topbar .m-project .name")).ToHaveTextAsync(vehicle, new() { Timeout = 20_000 });

        // The update badge in the top bar.
        await Expect(p.Locator("[data-testid=m-lib-updates] .n")).ToHaveTextAsync("1");

        // The model list: the library's package with its pin, read-only; its content inside.
        await p.Locator(".m-bottomnav .m-tab[data-view=Model]").ClickAsync();
        var badge = p.Locator("[data-testid=m-lib-badge]");
        await Expect(badge).ToContainTextAsync("v1");
        await AssertNoHorizontalScrollAsync(p);
        await p.Locator($"li.m-row[data-id='{packageId}'] .m-row-main").ClickAsync();
        await Expect(p.Locator("[data-testid=m-lib-hint]")).ToContainTextAsync("read-only");
        await Expect(p.Locator("li.m-row.lib", new() { HasTextString = "Wheel" })).ToBeVisibleAsync();
        await AssertNoHorizontalScrollAsync(p);

        // The review is a full-screen sheet that fits the phone.
        await p.Locator("[data-testid=m-lib-updates]").ClickAsync();
        var review = p.MudDialog();
        await Expect(review.Locator("[data-testid=lib-review]")).ToContainTextAsync("1 changed");
        await Ui.EventuallyAsync(async () => await review.BoundingBoxAsync() is { } b && b.X >= 0 && b.X + b.Width <= 390 && b.Width >= 340,
            "the review sheet spans the phone's width (after its opening animation)");
        await AssertNoHorizontalScrollAsync(p);
        await review.Locator("button[data-testid=lib-accept]").ClickAsync();
        await Expect(p.Locator("[data-testid=m-lib-updates]")).ToHaveCountAsync(0);
        await Expect(badge).ToContainTextAsync("v2");
        s.AssertNoErrors();
    }
}
