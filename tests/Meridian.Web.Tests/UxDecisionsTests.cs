using System.Text.RegularExpressions;
using Bunit;
using Meridian.Core.CodeSync;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.CodeSync;
using Meridian.Web.Components.Mobile;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>
/// The "decisions needed" of docs/UX-AUDIT-0.7.md as built in 0.7.1: the merged ribbon tabs, the Export ▸ submenu,
/// verification as a view tab, read-only phone lists, the user-guide links and singular counts. The browser-level checks are
/// in tests/Meridian.E2E/UxDecisionsTests.cs.
/// </summary>
public sealed class UxDecisionsTests : TestContext, IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-uxdecisions-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;
    private CollaborationHub _hub = default!;
    private readonly CodeSyncEvents _events = new();

    public UxDecisionsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        _hub = new CollaborationHub(_db, NullLogger<CollaborationHub>.Instance);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Meridian.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("Meridian.slnx not found above " + AppContext.BaseDirectory);
    }

    private static string Markup(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), "src", "Meridian.Web", "Components", relative));

    // ---------------------------------------------------------------- 1. ribbon

    [Fact]
    public void Review_and_Extend_are_merged_into_Traceability_and_Generate()
    {
        var all = string.Join("\n", Directory.GetFiles(Path.Combine(RepoRoot(), "src", "Meridian.Web", "Components"), "*.razor", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("data-tab=\"review\"", all);
        Assert.DoesNotContain("data-tab=\"extend\"", all);

        // The buttons live in the trace and generate groups now.
        var wb = Markup("Workbench/Workbench.razor");
        var trace = Group(wb, "trace");
        Assert.Contains("Request review", trace);
        Assert.Contains("ToggleDock(ReviewsDock)", trace);
        var generate = Group(wb, "generate");
        Assert.Contains("<PluginsMenu", generate);
        Assert.Contains("Script", Markup("Workbench/PluginsMenu.razor"));
        Assert.DoesNotContain("rgroup", Markup("Workbench/PluginsMenu.razor"));   // buttons only, inside the Generate group

        // A tab id remembered from 0.7.0 maps to the tab its buttons moved to; the others are unchanged.
        Assert.Equal("trace", Workbench.CurrentRibbonTabId("review"));
        Assert.Equal("generate", Workbench.CurrentRibbonTabId("extend"));
        Assert.Equal("simulate", Workbench.CurrentRibbonTabId("simulate"));
        Assert.DoesNotContain(Workbench.TourTargetTabs.Values, t => t is "review" or "extend");
        Assert.DoesNotContain(TourChapters.All.SelectMany(c => c.Steps), s => s.Html.Contains("Review ▸", StringComparison.Ordinal) || s.Html.Contains("Extend ▸", StringComparison.Ordinal));
    }

    private static string Group(string markup, string tab)
    {
        var start = markup.IndexOf($"data-tab=\"{tab}\">", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no {tab} group");
        var next = markup.IndexOf("<div class=\"rgroup", start, StringComparison.Ordinal);
        return markup[start..(next < 0 ? markup.Length : next)];
    }

    // ---------------------------------------------------------------- 2. Export ▸

    [Fact]
    public void Project_menu_exports_are_one_Export_submenu_with_keyboard_support()
    {
        var wb = Markup("Workbench/Workbench.razor");
        Assert.DoesNotContain("Class=\"menu-heading\">Export<", wb);
        var sub = Regex.Match(wb, "<MudMenu Label=\"Export\"[^>]*>(.*?)</MudMenu>", RegexOptions.Singleline);
        Assert.True(sub.Success, "no Export submenu");
        foreach (var item in new[] { "Download .meridian file", "Export XMI 2.5.1", "Export SysML v2 text", "Export diagram as SVG", "Export diagram as PNG…", "Copy diagram as image", "Export diagram as Mermaid (.mmd)", "Copy diagram as Mermaid" })
            Assert.Contains(item, sub.Groups[1].Value);
        // meridian.js gives kbd-menu lists arrow keys, → / ← for the submenu and Enter.
        Assert.Contains("kbd-menu kbd-submenu", sub.Value);
        Assert.Contains("ListClass=\"project-menu kbd-menu\"", wb);
        var js = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Meridian.Web", "wwwroot", "js", "meridian.js"));
        Assert.Contains(".mud-list.kbd-menu", js);
    }

    // ---------------------------------------------------------------- 3. verification tab

    [Fact]
    public void Verification_results_are_a_view_tab_that_opens_once()
    {
        var ws = new Workspace();
        ws.UseLocalModel(SampleModels.Vehicle());
        var tab = ws.OpenView(ViewTabKind.Verification);
        Assert.Equal("Verification", tab.Title);
        Assert.Same(tab, ws.OpenView(ViewTabKind.Verification));
        Assert.Single(ws.OpenViews, v => v.Kind == ViewTabKind.Verification);

        var wb = Markup("Workbench/Workbench.razor");
        Assert.DoesNotContain("case VerificationDock:", wb);   // no longer a dock panel
        Assert.Contains("data-testid=\"dock-verification\"", wb);   // the dock keeps a shortcut that opens the tab
        Assert.Contains("<VerificationPanel />", Regex.Match(wb, @"ViewTabKind\.Verification\)\s*\{(.*?)\}", RegexOptions.Singleline).Value);
    }

    // ---------------------------------------------------------------- 4. phone

    private (Workspace Ws, MobileUiState Ui) Phone(Workspace? ws = null)
    {
        Services.AddScoped<MobileUiState>();
        Services.AddSingleton<IMeridianStore>(_db);
        Services.AddSingleton(_hub);
        Services.AddSingleton(new MeridianHostInfo(true, Path.GetTempPath(), null));
        Services.AddScoped<ReviewService>();
        if (ws != null) Services.AddSingleton(ws); else Services.AddScoped<Workspace>();
        var w = Services.GetRequiredService<Workspace>();
        var ui = Services.GetRequiredService<MobileUiState>();
        ui.SetViewport(390);
        return (w, ui);
    }

    [Fact]
    public async Task Phone_verification_lists_status_requirement_value_and_margin_read_only()
    {
        var (ws, ui) = Phone();
        ws.UseLocalModel(SampleModels.Vehicle());
        await ws.Verification.RunAsync(null, ws.Verification.Options());
        ui.Show(MobileUiState.View.Verification);

        var cut = RenderComponent<MobileVerification>();
        var cards = cut.FindAll(".m-vfy");
        Assert.Equal(3, cards.Count);
        var range = cut.Find(".m-vfy[data-status=fail]");
        Assert.Contains("REQ-003", range.TextContent);
        Assert.Contains("Range", range.TextContent);
        Assert.Contains("550 km vs 600 km", range.TextContent);
        Assert.Contains("margin -50 km", range.TextContent);
        Assert.Contains("read-only", cut.Markup);
        Assert.Empty(cut.FindAll("input, select, [data-testid=verify-run]"));

        cut.FindAll(".m-chip").First(c => c.TextContent.StartsWith("Fail", StringComparison.Ordinal)).Click();
        Assert.Single(cut.FindAll(".m-vfy"));
        cut.FindAll(".m-chip").First(c => c.TextContent.StartsWith("Not run", StringComparison.Ordinal)).Click();
        Assert.Contains("No results match", cut.Markup);

        // Tapping a result shows the requirement's sheet.
        cut.FindAll(".m-chip")[0].Click();
        cut.Find(".m-vfy[data-status=fail] .m-card-main").Click();
        Assert.Equal("Range", ui.SheetElement?.Name);
    }

    [Fact]
    public void Phone_account_menu_opens_the_read_only_lists()
    {
        var (ws, ui) = Phone();
        ws.UseLocalModel(SampleModels.Vehicle());
        RenderComponent<MudBlazor.MudPopoverProvider>();
        Assert.Equal([MobileUiState.View.Verification, MobileUiState.View.CodeChanges],
            Enum.GetValues<MobileUiState.View>().Except(MobileBottomNav.Tabs.Select(t => t.View)));
        var top = Markup("Mobile/MobileTopBar.razor");
        Assert.Contains("Ui.Show(MobileUiState.View.Verification)", top);
        Assert.Contains("Ui.Show(MobileUiState.View.CodeChanges)", top);
        Assert.Contains("OpenGuide", top);

        ui.Show(MobileUiState.View.Verification);
        var shell = RenderComponent<MobileShell>();
        Assert.Single(shell.FindAll("[data-testid=m-verification]"));
        Assert.Equal(3, shell.FindAll(".m-vfy[data-status=none]").Count);   // criteria, not verified yet
        Assert.Empty(shell.FindAll(".m-bottomnav .m-tab.on"));   // no bottom tab for this view
    }

    private sealed class Reader : ICodeReader
    {
        public string Language => "C#";
        public bool Handles(string path) => path.EndsWith(".cs", StringComparison.Ordinal);
        public CodeModel Read(IReadOnlyList<CodeFile> files, CancellationToken ct = default) => new("C#", [], []);
    }

    private sealed class AddBattery : ICodeModelApplier
    {
        public CodeSyncChanges Apply(ModelRepository model, CodeModel code, CodeSyncOptions options)
        {
            var structure = model.OfKind(ElementKind.Package).First(p => p.Name == "Structure");
            var battery = model.Create(structure, ElementKind.Block, "Battery");
            model.Set(battery, CodeFieldKeys.Symbol, "Sat.Battery");
            var engine = model.AllElements.First(e => e.Name == "Engine" && e is not Diagram);
            model.Set(engine, FieldKeys.Documentation, "Changed by code");
            return new CodeSyncChanges([battery.Id], [engine.Id], [], []);
        }
    }

    [Fact]
    public async Task Phone_code_changes_list_each_change_kind_with_an_accept_elsewhere_note()
    {
        var owner = await _db.CreateUserAsync("ann", "Ann", "correct horse");
        var project = await _db.CreateProjectAsync("Vehicle", ProjectFile.Serialize(SampleModels.Vehicle()), owner.Id);
        var review = new CodeSyncReviewService(_db, _hub, _events, NullLogger<CodeSyncReviewService>.Instance);
        var runner = new CodeSyncRunner(_db, _hub, [new Reader()], new AddBattery(), _events, NullLogger<CodeSyncRunner>.Instance);
        await runner.RunAsync(new CodeSyncRequest(project, Guid.NewGuid(), "acme/sat", "main", "01c0ffee" + new string('0', 32), "https://git.example/acme/sat/commit/01",
            "Dana Dev", "Add battery", [new CodeFile("src/Battery.cs", "class Battery {}")], IsFullSnapshot: true));

        var ws = new Workspace { User = owner };
        Assert.True(await ws.JoinAsync(_hub, project, ProjectRole.Owner, null));
        Services.AddSingleton(_events);
        Services.AddSingleton(review);
        Services.AddScoped<CodeSyncState>();
        var (_, ui) = Phone(ws);
        await Services.GetRequiredService<CodeSyncState>().ReloadAsync();
        ui.Show(MobileUiState.View.CodeChanges);

        var cut = RenderComponent<MobileCodeChanges>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".m-chg").Count));
        Assert.Contains("Added", cut.Find(".m-chg[data-kind=added]").TextContent);
        Assert.Contains("Battery", cut.Find(".m-chg[data-kind=added]").TextContent);
        Assert.Contains("Changed", cut.Find(".m-chg[data-kind=changed]").TextContent);
        Assert.Contains("2 changes from 1 push", cut.Markup);
        Assert.Contains("Accept or reject code changes on a larger screen", cut.Markup);
        Assert.Empty(cut.FindAll("input[type=checkbox], .chg-accept, .chg-reject"));
        ws.Dispose();
    }

    // ---------------------------------------------------------------- 5. guide links

    [Fact]
    public void Learn_more_links_point_at_guide_pages()
    {
        var link = RenderComponent<GuideLink>(p => p.Add(x => x.Page, "verification"));
        var a = link.Find("a");
        Assert.Equal("/guide/docs/verification.html", a.GetAttribute("href"));
        Assert.True(a.HasAttribute("data-guide"));
        Assert.Equal("Learn more", a.TextContent);

        Assert.Contains("<GuideLink Page=\"migration\">", Markup("Workbench/EmptyProjectHint.razor"));
        Assert.Contains("<GuideLink Page=\"verification\">", Markup("Workbench/VerificationPanel.razor"));
        Assert.Contains("data-guide href=\\\"/guide/docs/migration.html\\\"", File.ReadAllText(Path.Combine(RepoRoot(), "src", "Meridian.Web", "Components", "Workbench", "TourChapters.cs")));
        Assert.Contains("data-testid=\"help-guide\"", Markup("Workbench/Workbench.razor"));
        Assert.Contains("data-testid=\"login-guide\"", Markup("Pages/Login.razor"));
    }

    // ---------------------------------------------------------------- 6. plurals

    [Theory]
    [InlineData(0, "diagram", null, "0 diagrams")]
    [InlineData(1, "diagram", null, "1 diagram")]
    [InlineData(2, "diagram", null, "2 diagrams")]
    [InlineData(1, "relationship", null, "1 relationship")]
    [InlineData(1, "push", "pushes", "1 push")]
    [InlineData(3, "push", "pushes", "3 pushes")]
    [InlineData(1000, "element", null, "1000 elements")]
    public void Plural_picks_the_noun_for_the_count(long n, string singular, string? plural, string expected) =>
        Assert.Equal(expected, Plural.Of(n, singular, plural));
}
