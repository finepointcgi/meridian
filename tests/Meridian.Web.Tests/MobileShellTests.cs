using Bunit;
using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Web.Components.Mobile;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>The phone / tablet shell: layout switching, the full-screen views, the element sheet and canvas touch entry points.</summary>
public class MobileShellTests : TestContext
{
    private readonly Workspace _ws;
    private readonly MobileUiState _ui;

    public MobileShellTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        Services.AddScoped<MobileUiState>();
        var db = new SqliteStore(Path.Combine(Path.GetTempPath(), $"meridian-mobile-{Guid.NewGuid():N}.db"));
        Services.AddSingleton<IMeridianStore>(db);
        Services.AddSingleton(new CollaborationHub(db, NullLogger<CollaborationHub>.Instance));
        Services.AddSingleton(new MeridianHostInfo(true, Path.GetTempPath(), null));
        Services.AddScoped<ReviewService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ws = Services.GetRequiredService<Workspace>();
        _ui = Services.GetRequiredService<MobileUiState>();
        _ws.UseLocalModel(SampleModels.Vehicle());
        RenderComponent<MudBlazor.MudPopoverProvider>();
    }

    private IRenderedComponent<MobileShell> Phone()
    {
        var cut = RenderComponent<MobileShell>();
        cut.InvokeAsync(() => cut.Instance.OnViewport(375));
        return cut;
    }

    [Theory]
    [InlineData(375, MobileUiState.Layout.Phone)]
    [InlineData(768, MobileUiState.Layout.Phone)]
    [InlineData(899, MobileUiState.Layout.Phone)]
    [InlineData(900, MobileUiState.Layout.Tablet)]
    [InlineData(1024, MobileUiState.Layout.Tablet)]
    [InlineData(1280, MobileUiState.Layout.Desktop)]
    [InlineData(1920, MobileUiState.Layout.Desktop)]
    public void Breakpoints_Pick_Phone_Tablet_Or_Desktop(int width, MobileUiState.Layout expected) =>
        Assert.Equal(expected, MobileUiState.LayoutFor(width));

    [Fact]
    public void Desktop_Renders_No_Phone_Chrome_And_Phone_Adds_Top_Bar_And_Bottom_Navigation()
    {
        var cut = RenderComponent<MobileShell>();
        Assert.Empty(cut.FindAll(".m-topbar"));
        Assert.Empty(cut.FindAll(".m-bottomnav"));
        Assert.Equal("desktop", cut.Find(".m-shell").GetAttribute("data-layout"));

        cut.InvokeAsync(() => cut.Instance.OnViewport(375));
        Assert.Equal("phone", cut.Find(".m-shell").GetAttribute("data-layout"));
        Assert.Single(cut.FindAll(".m-topbar"));
        var tabs = cut.FindAll(".m-bottomnav .m-tab");
        Assert.Equal(["Model", "Diagram", "Requirements", "Reviews", "Comments"], tabs.Select(t => t.TextContent.Trim()));
        Assert.Contains("on", cut.Find(".m-tab[data-view=Diagram]").ClassList);   // the canvas is the default view
    }

    [Fact]
    public void Every_Bottom_Tab_Opens_Its_Full_Screen_View()
    {
        var cut = Phone();
        foreach (var view in new[] { "Model", "Requirements", "Reviews", "Comments" })
        {
            cut.Find($".m-tab[data-view={view}]").Click();
            Assert.Equal(view.ToLowerInvariant(), cut.Find(".m-shell").GetAttribute("data-view"));
            Assert.Single(cut.FindAll(".m-view"));
            Assert.Equal("page", cut.Find($".m-tab[data-view={view}]").GetAttribute("aria-current"));
            if (view == "Reviews") Assert.NotEmpty(cut.FindAll(".m-reviews .rv"));
        }
        cut.Find(".m-tab[data-view=Diagram]").Click();
        Assert.Empty(cut.FindAll(".m-view"));
        Assert.Single(cut.FindAll(".m-diagram-bar"));
    }

    [Fact]
    public void Model_List_Drills_Into_Packages_And_Back()
    {
        var cut = Phone();
        cut.Find(".m-tab[data-view=Model]").Click();
        var root = _ws.Model.Root;
        var pkg = root.OwnedElements.First(e => e.Kind == ElementKind.Package && e.OwnedElements.Any(c => c is not Relationship));
        Assert.Equal(root.OwnedElements.Count(e => e is not Relationship), cut.FindAll(".m-list .m-row").Count);

        cut.Find($".m-row[data-id='{pkg.Id}'] .m-row-main").Click();
        Assert.Same(pkg, _ui.ModelContainer);
        Assert.Contains(pkg.Name, cut.Find(".m-list-head .m-head-title").TextContent);
        Assert.Equal(pkg.OwnedElements.Count(e => e is not Relationship), cut.FindAll(".m-list .m-row").Count);

        cut.Find(".m-list-head .m-icon-btn").Click();   // back
        Assert.Same(root, _ui.ModelContainer);
    }

    [Fact]
    public void Model_List_Opens_Diagrams_On_The_Canvas_And_Leaves_Show_Their_Sheet()
    {
        var cut = Phone();
        cut.Find(".m-tab[data-view=Model]").Click();
        var diagram = _ws.Model.Diagrams.First();
        cut.InvokeAsync(() => _ui.DrillTo(diagram.Owner));
        cut.Find($".m-row[data-id='{diagram.Id}'] .m-row-main").Click();
        Assert.Same(diagram, _ws.Active?.Diagram);
        Assert.Equal(MobileUiState.View.Diagram, _ui.ActiveView);

        var leaf = _ws.Model.OfKind(ElementKind.Requirement).First(r => !r.OwnedElements.Any(c => c is not Relationship));
        cut.Find(".m-tab[data-view=Model]").Click();
        cut.InvokeAsync(() => _ui.DrillTo(leaf.Owner));
        cut.Find($".m-row[data-id='{leaf.Id}'] .m-row-main").Click();
        Assert.Same(leaf, _ws.Selected);
        Assert.Same(leaf, _ui.SheetElement);
        Assert.Contains(leaf.Name, cut.Find(".m-sheet .m-head-title").TextContent);
    }

    [Fact]
    public void Requirements_View_Shows_Cards_With_Traceability_And_Filters()
    {
        var cut = Phone();
        cut.Find(".m-tab[data-view=Requirements]").Click();
        var all = _ws.Model.OfKind(ElementKind.Requirement).Count();
        Assert.Equal(all, cut.FindAll(".m-card").Count);
        Assert.Empty(cut.FindAll(".m-view table"));   // cards, not wide tables
        Assert.Contains(cut.FindAll(".m-card"), c => c.TextContent.Contains("Satisfied by"));

        var cov = Traceability.Coverage(_ws.Model);
        cut.FindAll(".m-chip").First(c => c.TextContent == "Not verified").Click();
        Assert.Equal(cov.Unverified.Count, cut.FindAll(".m-card").Count);

        cut.FindAll(".m-chip").First(c => c.TextContent == "All").Click();
        cut.Find(".m-search input").Input("REQ-004");
        var card = Assert.Single(cut.FindAll(".m-card"));
        card.QuerySelector(".m-card-main")!.Click();
        Assert.Equal("Braking", _ws.Selected?.Name);
        Assert.NotNull(_ui.SheetElement);
    }

    [Fact]
    public void Sheet_Shows_Properties_And_Traceability_And_Its_Actions_Work()
    {
        var cut = Phone();
        var req = _ws.Model.OfKind(ElementKind.Requirement).First(r => _ws.Model.Incoming(r, ElementKind.Satisfy).Any());
        cut.InvokeAsync(() => { _ws.Select(req); _ui.OpenSheet(req); });

        var sheet = cut.Find(".m-sheet");
        Assert.Contains(req.Name, sheet.TextContent);
        Assert.Contains("Documentation", sheet.TextContent);
        Assert.Contains("Satisfy", cut.Find(".m-trace-list").TextContent);
        Assert.All(cut.FindAll(".m-sheet .m-action"), b => Assert.Equal("BUTTON", b.TagName));

        // Following a trace link moves the sheet to the other end.
        var satisfier = _ws.Model.Incoming(req, ElementKind.Satisfy).First().Source;
        cut.FindAll(".m-trace-row").First(r => r.TextContent.Contains(satisfier.DisplayName)).Click();
        Assert.Same(satisfier, _ui.SheetElement);

        // Show in tree: the model view, drilled into the owner, element highlighted.
        cut.FindAll(".m-sheet .m-action").First(b => b.TextContent.Contains("Show in tree")).Click();
        Assert.Equal(MobileUiState.View.Model, _ui.ActiveView);
        Assert.Same(satisfier.Owner, _ui.ModelContainer);
        Assert.Contains("sel", cut.Find($".m-row[data-id='{satisfier.Id}']").ClassList);
        Assert.Null(_ui.SheetElement);
    }

    [Fact]
    public void Comment_Action_Opens_The_Comments_View_For_The_Element()
    {
        var cut = Phone();
        var block = _ws.Model.OfKind(ElementKind.Block).First();
        cut.InvokeAsync(() => _ui.OpenSheet(block));
        cut.FindAll(".m-sheet .m-action").First(b => b.TextContent.Contains("Comment")).Click();
        Assert.Equal(MobileUiState.View.Comments, _ui.ActiveView);
        Assert.Same(block, _ws.Selected);
        Assert.Contains(block.Name, cut.Find(".m-view .m-head-title").TextContent);

        cut.Find(".m-view .m-list-head .m-icon-btn").Click();   // back to every discussion
        Assert.Null(_ws.Selected);
        Assert.Contains("Discussions", cut.Find(".m-view").TextContent);
    }

    [Fact]
    public void Tap_Selects_The_Shape_Under_The_Finger_And_Empty_Canvas_Clears()
    {
        var cut = Phone();
        _ws.OpenDefaultDiagrams();
        var c = _ws.Active!;
        var node = c.VisibleNodes.First(n => n.Element is not Diagram);
        var center = c.ToScreen(c.BoundsOf(node).Center);

        string? id = null;
        cut.InvokeAsync(() => id = cut.Instance.TouchTap(center.X, center.Y));
        Assert.Equal(node.Element.Id, id);
        Assert.Same(node.Element, _ws.Selected);
        Assert.Same(node.Element, _ui.SheetElement);
        Assert.Single(cut.FindAll(".m-sheet"));

        var empty = c.ToScreen(new Pt(c.ContentBounds().Right + 500, c.ContentBounds().Bottom + 500));
        cut.InvokeAsync(() => id = cut.Instance.TouchTap(empty.X, empty.Y));
        Assert.Null(id);
        Assert.Null(_ws.Selected);
        Assert.Empty(cut.FindAll(".m-sheet"));
    }

    [Fact]
    public void Long_Press_Opens_Comment_Open_Diagram_Show_In_Tree()
    {
        var cut = Phone();
        _ws.OpenDefaultDiagrams();
        var c = _ws.Active!;
        var node = c.VisibleNodes.First(n => n.Element is not Diagram);
        var center = c.ToScreen(c.BoundsOf(node).Center);

        var hit = false;
        cut.InvokeAsync(() => hit = cut.Instance.TouchLongPress(center.X, center.Y));
        Assert.True(hit);
        Assert.Same(node.Element, _ui.MenuElement);
        var items = cut.FindAll(".m-action-sheet .m-menu-item").Select(i => i.TextContent.Trim()).ToList();
        Assert.Contains("Comment", items);
        Assert.Contains(items, i => i.StartsWith("Open diagram"));
        Assert.Contains("Show in tree", items);

        cut.FindAll(".m-action-sheet .m-menu-item").First(i => i.TextContent.Trim() == "Cancel").Click();
        Assert.Null(_ui.MenuElement);
    }

    [Fact]
    public void Pinch_Keeps_The_Point_Under_The_Fingers_And_Clamps_Zoom()
    {
        _ws.OpenDefaultDiagrams();
        var c = _ws.Active!;
        c.ViewWidth = 375;
        c.ViewHeight = 600;
        c.SetZoom(1);
        var anchor = c.ToModel(100, 200);

        MobileShell.ApplyTransform(c, 2, 100, 200, 140, 260);   // zoom ×2 while the fingers move by (40, 60)
        Assert.Equal(2, c.Zoom, 3);
        var after = c.ToScreen(anchor);
        Assert.Equal(140, after.X, 3);
        Assert.Equal(260, after.Y, 3);

        MobileShell.ApplyTransform(c, 1, 0, 0, 25, -10);           // two-finger / one-finger pan
        Assert.Equal(165, c.ToScreen(anchor).X, 3);

        MobileShell.ApplyTransform(c, 50, 100, 100, 100, 100);     // clamped to the desktop range
        Assert.Equal(4, c.Zoom, 3);
    }

    [Fact]
    public void Touch_Mode_Defaults_To_View_Only_And_Viewers_Cannot_Switch_To_Edit()
    {
        var cut = Phone();
        Assert.False(_ui.EditMode);
        Assert.Equal("view", cut.Find(".m-shell").GetAttribute("data-mode"));
        Assert.Equal("true", cut.Find(".m-mode button.on").GetAttribute("aria-pressed"));
        Assert.Contains("View only", cut.Find(".m-mode button.on").TextContent);

        cut.FindAll(".m-mode button")[1].Click();
        Assert.True(_ui.EditMode);
        Assert.Equal("edit", cut.Find(".m-shell").GetAttribute("data-mode"));
    }

    [Fact]
    public void Tablet_Drawers_Toggle_And_The_Scrim_Closes_Them()
    {
        var shell = RenderComponent<MobileShell>();
        var buttons = RenderComponent<TabletDrawers>();
        shell.InvokeAsync(() => shell.Instance.OnViewport(1024));
        Assert.Equal("tablet", shell.Find(".m-shell").GetAttribute("data-layout"));
        Assert.Empty(shell.FindAll(".m-topbar"));

        buttons.FindAll(".t-drawer-btn")[0].Click();
        Assert.Equal("open", shell.Find(".m-shell").GetAttribute("data-left"));
        buttons.FindAll(".t-drawer-btn")[1].Click();       // only one drawer at a time
        Assert.Equal("closed", shell.Find(".m-shell").GetAttribute("data-left"));
        Assert.Equal("open", shell.Find(".m-shell").GetAttribute("data-right"));

        shell.Find(".t-scrim").Click();
        Assert.Equal("closed", shell.Find(".m-shell").GetAttribute("data-right"));
    }

    [Fact]
    public void Touch_Targets_In_The_Phone_Chrome_Are_Buttons_With_Accessible_Names()
    {
        var cut = Phone();
        foreach (var b in cut.FindAll(".m-topbar button, .m-bottomnav button, .m-diagram-bar button"))
            Assert.False(string.IsNullOrWhiteSpace(b.GetAttribute("aria-label") ?? b.TextContent), b.OuterHtml);
    }
}
