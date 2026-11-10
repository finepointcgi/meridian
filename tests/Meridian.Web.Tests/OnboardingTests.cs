using Bunit;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Meridian.Web.Settings;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>The guided tour's chapters, the What's new card and the empty project's one-click starts.</summary>
public sealed class OnboardingTests : TestContext
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "meridian-onboarding-" + Guid.NewGuid().ToString("N"));
    private SqliteStore _db = default!;
    private UserAccount _me = default!;

    public OnboardingTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        Services.AddSingleton<ImageCache>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private ThemeService SignedIn(Action<UserPreferences>? prefs = null)
    {
        _db = new SqliteStore(Path.Combine(_dir, "m.db"));
        _db.MigrateAsync().GetAwaiter().GetResult();
        _me = _db.CreateUserAsync("ann", "Ann", "password-1").GetAwaiter().GetResult();
        Services.AddSingleton<IMeridianStore>(_db);
        Services.AddScoped<ThemeService>();
        var ws = Services.GetRequiredService<Workspace>();
        ws.User = _me;
        var theme = Services.GetRequiredService<ThemeService>();
        theme.LoadForAsync(_me.Id).GetAwaiter().GetResult();
        if (prefs != null) theme.UpdateAsync(prefs).GetAwaiter().GetResult();
        return theme;
    }

    private UserPreferences Stored() => UserPreferences.FromJson(_db.GetUserPreferencesAsync(_me.Id).GetAwaiter().GetResult());

    private Workspace Model(ModelRepository? model = null)
    {
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(model ?? SampleModels.Vehicle());
        return ws;
    }

    private void Viewport(double width, double height = 800) =>
        JSInterop.Setup<OnboardingTour.Rect?>("meridian.viewport").SetResult(new OnboardingTour.Rect(0, 0, width, height));

    // ---------------------------------------------------------------- chapters

    [Fact]
    public void Chapters_Have_Unique_Ids_Steps_And_Known_Whats_New_Links()
    {
        Assert.Equal(TourChapters.Basics, TourChapters.All[0].Id);
        Assert.Equal(TourChapters.All.Length, TourChapters.All.Select(c => c.Id).Distinct().Count());
        Assert.Equal(
            ["The basics", "Generate diagrams", "Create diagrams from diagrams", "Connect your code", "Import Mermaid", "AI assistant providers", "Select many & link", "Settings & themes"],
            TourChapters.All.Select(c => c.Title));
        Assert.All(TourChapters.All, c => Assert.True(c.Steps.Length >= 2, c.Id));
        Assert.All(TourChapters.All.SelectMany(c => c.Steps), s => Assert.False(string.IsNullOrWhiteSpace(s.Html)));
        Assert.All(TourChapters.WhatsNew, w => Assert.NotNull(TourChapters.Find(w.ChapterId)));
    }

    [Fact]
    public async Task Help_Tour_Lists_Every_Chapter_And_Runs_The_One_Picked()
    {
        Model();
        var cut = RenderComponent<OnboardingTour>();
        await cut.InvokeAsync(() => cut.Instance.ShowChaptersAsync());
        var items = cut.FindAll(".tour-chapter");
        Assert.Equal(TourChapters.All.Select(c => c.Id), items.Select(i => i.GetAttribute("data-chapter")));

        cut.Find(".tour-chapter[data-chapter='mermaid']").Click();
        cut.WaitForAssertion(() => Assert.Contains("Import Mermaid · Step 1 of 3", cut.Markup));
        Assert.Equal("mermaid", cut.Instance.CurrentChapter);
        Assert.Contains("Paste Mermaid", cut.Find(".tour-card h3").TextContent);
        cut.Find(".tour-btn.primary").Click();
        cut.Find(".tour-btn.primary").Click();
        cut.WaitForAssertion(() => Assert.Equal("Done", cut.Find(".tour-btn.primary").TextContent.Trim()));
        cut.Find(".tour-btn.primary").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tour")));
    }

    [Fact]
    public async Task A_Chapter_Can_Go_Back_To_The_Chapter_List()
    {
        Model();
        var cut = RenderComponent<OnboardingTour>();
        await cut.InvokeAsync(() => cut.Instance.StartChapterAsync("code"));
        Assert.Contains("Connect your code · Step 1 of 3", cut.Markup);
        cut.Find(".tour-chapters-link").Click();
        Assert.Equal(OnboardingTour.Mode.Chapters, cut.Instance.Current);
        await cut.InvokeAsync(() => cut.Instance.StartChapterAsync("no-such-chapter"));
        Assert.Equal(OnboardingTour.Mode.Chapters, cut.Instance.Current);   // unknown ids fall back to the list
    }

    [Fact]
    public async Task Steps_Spotlight_Their_Target_On_A_Desktop_But_Not_On_A_Phone()
    {
        Model();
        JSInterop.Setup<OnboardingTour.Rect?>("meridian.rectOf", _ => true).SetResult(new OnboardingTour.Rect(100, 50, 80, 30));
        Viewport(1400);
        var cut = RenderComponent<OnboardingTour>();
        await cut.InvokeAsync(() => cut.Instance.StartChapterAsync("generate"));
        Assert.Single(cut.FindAll(".tour-spot"));

        Viewport(390, 844);
        await cut.InvokeAsync(() => cut.Instance.StartChapterAsync("generate"));
        Assert.Empty(cut.FindAll(".tour-spot"));   // a phone shows the card as a bottom sheet over a dimmed screen
        Assert.Single(cut.FindAll(".tour-dim"));
    }

    // ---------------------------------------------------------------- first run and What's new

    [Fact]
    public void First_Visit_Runs_The_Basics_And_Finishing_Marks_The_Tour_And_Whats_New_Seen()
    {
        var theme = SignedIn();
        var ws = Model();
        ws.Open(ws.Model.Diagrams.First());
        var cut = RenderComponent<OnboardingTour>();
        cut.WaitForAssertion(() => Assert.Contains("Step 1 of 7", cut.Markup));
        Assert.Equal(TourChapters.Basics, cut.Instance.CurrentChapter);
        cut.Find(".tour-skip").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tour")));
        Assert.True(theme.Prefs.TourSeen);
        Assert.Equal(TourChapters.WhatsNewVersion, Stored().WhatsNewSeen);   // a new user is not shown "What's new"
    }

    [Fact]
    public void First_Visit_On_A_Phone_Does_Not_Start_The_Desktop_Tour()
    {
        SignedIn();
        Viewport(390, 844);
        var ws = Model();
        ws.Open(ws.Model.Diagrams.First());
        var cut = RenderComponent<OnboardingTour>();
        Assert.Empty(cut.FindAll(".tour"));
        Assert.Equal(OnboardingTour.Mode.Closed, cut.Instance.Current);
    }

    [Fact]
    public void Whats_New_Shows_Once_After_An_Upgrade()
    {
        var theme = SignedIn(p => p.TourSeen = true);   // used an earlier version
        Model();
        var cut = RenderComponent<OnboardingTour>();
        cut.WaitForAssertion(() => Assert.Contains($"What's new in {TourChapters.WhatsNewVersion}", cut.Markup));
        Assert.Equal(TourChapters.WhatsNew.Length, cut.FindAll(".tour-new").Count);
        Assert.Empty(cut.FindAll(".tour-dim"));   // a corner card: the workbench stays usable
        cut.Find(".tour-got-it").Click();
        Assert.Equal(TourChapters.WhatsNewVersion, theme.Prefs.WhatsNewSeen);
        Assert.Equal(TourChapters.WhatsNewVersion, Stored().WhatsNewSeen);

        var again = RenderComponent<OnboardingTour>();
        Assert.Empty(again.FindAll(".tour"));
    }

    [Fact]
    public void Whats_New_Show_Me_Opens_That_Chapter()
    {
        var theme = SignedIn(p => p.TourSeen = true);
        Model();
        var cut = RenderComponent<OnboardingTour>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tour-new")));
        cut.Find(".tour-new[data-chapter='derive'] button").Click();
        cut.WaitForAssertion(() => Assert.Equal("derive", cut.Instance.CurrentChapter));
        Assert.Contains("Create from…", cut.Find(".tour-card h3").TextContent);
        Assert.Equal(TourChapters.WhatsNewVersion, theme.Prefs.WhatsNewSeen);
    }

    [Fact]
    public async Task Whats_New_Is_Not_Shown_Twice_Or_To_A_Browser_That_Opted_Out()
    {
        SignedIn(p => { p.TourSeen = true; p.WhatsNewSeen = TourChapters.WhatsNewVersion; });
        Model();
        Assert.Empty(RenderComponent<OnboardingTour>().FindAll(".tour"));

        await Services.GetRequiredService<ThemeService>().UpdateAsync(p => p.WhatsNewSeen = "0.4");
        JSInterop.Setup<string?>("meridian.getFlag", OnboardingTour.WhatsNewKey).SetResult("1");
        Assert.Empty(RenderComponent<OnboardingTour>().FindAll(".tour"));
    }

    [Fact]
    public void Whats_New_Seen_Survives_Normalisation()
    {
        var p = UserPreferences.FromJson("""{"whatsNewSeen":"0.5","tourSeen":true}""");
        Assert.Equal("0.5", p.WhatsNewSeen);
        Assert.Null(UserPreferences.FromJson($$"""{"whatsNewSeen":"{{new string('x', 40)}}"}""").WhatsNewSeen);
    }

    // ---------------------------------------------------------------- empty project

    [Fact]
    public void An_Empty_Project_Offers_Ways_To_Start()
    {
        Model(new ModelRepository("Empty"));
        string? picked = null;
        var cut = RenderComponent<EmptyProjectHint>(p => p.Add(x => x.OnAction, a => picked = a));
        Assert.Contains("This project is empty", cut.Markup);
        var actions = cut.FindAll(".empty-action").Select(b => b.TextContent.Trim()).ToList();
        Assert.Equal(["Import Mermaid", "Open a Cameo file", "Start from a sample", "Auto-diagram"], actions);
        Assert.True(cut.Find($".empty-action[data-action='{EmptyProjectHint.AutoDiagram}']").HasAttribute("disabled"));   // nothing to draw yet

        cut.Find($".empty-action[data-action='{EmptyProjectHint.Mermaid}']").Click();
        Assert.Equal(EmptyProjectHint.Mermaid, picked);
        cut.Find($".empty-action[data-action='{EmptyProjectHint.Sample}']").Click();
        Assert.Equal(EmptyProjectHint.Sample, picked);
        cut.Find($".empty-action[data-action='{EmptyProjectHint.OpenFile}']").Click();
        Assert.Equal(EmptyProjectHint.OpenFile, picked);
    }

    [Fact]
    public void A_Model_Without_Diagrams_Leads_With_Auto_Diagram()
    {
        var m = new ModelRepository("Undrawn");
        m.Create(m.Root, ElementKind.Block, "Engine");
        Model(m);
        string? picked = null;
        var cut = RenderComponent<EmptyProjectHint>(p => p.Add(x => x.OnAction, a => picked = a));
        Assert.Contains("Nothing is drawn yet", cut.Markup);
        var first = cut.FindAll(".empty-action")[0];
        Assert.Contains("primary", first.ClassName);
        first.Click();
        Assert.Equal(EmptyProjectHint.AutoDiagram, picked);
    }

    [Fact]
    public void A_Project_With_Diagrams_Just_Says_None_Is_Open()
    {
        var ws = Model();
        var cut = RenderComponent<EmptyProjectHint>();
        Assert.Contains("No diagram open", cut.Markup);
        Assert.Empty(cut.FindAll(".empty-action"));

        ws.UseLocalModel(new ModelRepository("Empty"));   // the hint follows the workspace
        cut.WaitForAssertion(() => Assert.Contains("This project is empty", cut.Markup));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
