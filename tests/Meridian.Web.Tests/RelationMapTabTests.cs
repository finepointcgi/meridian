using Bunit;
using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>The relation map view tab (RelationMapTab.razor): its controls, node interaction, diagrams and saving.</summary>
public class RelationMapTabTests : TestContext
{
    public RelationMapTabTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        RenderComponent<MudBlazor.MudPopoverProvider>();
    }

    private Workspace Ws(ModelRepository? m = null)
    {
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(m ?? SampleModels.Vehicle());
        return ws;
    }

    private static Element Req(ModelRepository m, string id) => m.OfKind(ElementKind.Requirement).Single(r => r.Get(FieldKeys.RequirementId) == id);

    private (Workspace Ws, ViewTab Tab, IRenderedComponent<RelationMapTab> Cut) Open(Element? root = null, ModelRepository? m = null)
    {
        var ws = Ws(m);
        root ??= Req(ws.Model, "REQ-002");
        var tab = ws.OpenView(ViewTabKind.RelationMap, null, RelationMapSettings.For(root), forceNew: true);
        var cut = RenderComponent<RelationMapTab>(p => p.Add(x => x.Tab, tab));
        return (ws, tab, cut);
    }

    private static HashSet<string> Names(IRenderedComponent<RelationMapTab> cut) =>
        cut.Instance.Graph.Nodes.Where(n => !n.IsStub).Select(n => n.Element!.Name).ToHashSet();

    [Fact]
    public void Opens_On_A_Requirement_With_Its_Traceability_And_A_Titled_Tab()
    {
        var (ws, tab, cut) = Open();
        Assert.Equal("Relation map: Acceleration", tab.Title);
        Assert.Contains("REQ-002 Acceleration", cut.Find("[data-testid=rm-root]").TextContent);
        Assert.Equal("2", cut.Find("[data-testid=rm-depth]").TextContent);
        Assert.Contains("Traceability", cut.Find("[data-testid=rm-rel-filter]").TextContent);
        var svg = cut.Find("svg.rm-svg").InnerHtml;
        Assert.Contains("data-node=", svg);
        Assert.Contains("Acceleration Test", svg);
        Assert.Contains("«verify»", svg);
        Assert.Contains("element", cut.Find("[data-testid=rm-status]").TextContent);
        Assert.Contains("Engine", Names(cut));
        _ = ws;
    }

    [Fact]
    public void Depth_Buttons_Change_The_Levels_Within_1_To_6()
    {
        var (_, _, cut) = Open();
        cut.Find("[data-testid=rm-depth-dec]").Click();
        Assert.Equal("1", cut.Find("[data-testid=rm-depth]").TextContent);
        Assert.DoesNotContain("Engine", Names(cut));
        Assert.True(cut.Find("[data-testid=rm-depth-dec]").HasAttribute("disabled"));
        for (var i = 0; i < 8; i++) if (!cut.Find("[data-testid=rm-depth-inc]").HasAttribute("disabled")) cut.Find("[data-testid=rm-depth-inc]").Click();
        Assert.Equal("6", cut.Find("[data-testid=rm-depth]").TextContent);
        Assert.Equal(6, cut.Instance.Settings.Depth);
        Assert.True(cut.Find("[data-testid=rm-depth-inc]").HasAttribute("disabled"));
    }

    [Fact]
    public void Relationship_Presets_Checklist_Element_Kinds_Direction_And_Layout()
    {
        var (ws, _, cut) = Open(m: null);
        cut.Find("[data-testid=rm-rel-filter]").Click();
        cut.Find("[data-testid=rm-preset-All]").Click();
        Assert.Empty(cut.Instance.Settings.RelationshipKinds);
        Assert.Contains("All", cut.Find("[data-testid=rm-rel-filter]").TextContent);
        Assert.Contains("FuelTank", Names(cut));   // via Vehicle ◆→ FuelTank

        cut.Find("[data-testid=rm-preset-Traceability]").Click();
        Assert.Equal(RelationMapPresets.Traceability, cut.Instance.Settings.RelationshipKinds);
        Assert.DoesNotContain("FuelTank", Names(cut));

        // Unticking «verify» drops the test case.
        var verify = cut.FindAll("[data-testid=rm-rel-panel] label").Single(l => l.TextContent.Trim() == "Verify").QuerySelector("input")!;
        verify.Change(false);
        Assert.DoesNotContain(ElementKind.Verify, cut.Instance.Settings.RelationshipKinds);
        Assert.DoesNotContain("Acceleration Test", Names(cut));
        Assert.Contains("kinds", cut.Find("[data-testid=rm-rel-filter]").TextContent);

        // Element kinds.
        cut.Find("[data-testid=rm-kind-filter]").Click();
        var blocks = cut.FindAll("[data-testid=rm-kind-panel] label").Single(l => l.TextContent.Trim() == "Block").QuerySelector("input")!;
        blocks.Change(false);
        Assert.DoesNotContain("Vehicle", Names(cut));
        Assert.Contains("Engine Power", Names(cut));

        // Direction: nothing points out of the requirement.
        cut.Find("[data-testid=rm-direction]").Change("Outgoing");
        Assert.Equal(RelationDirection.Outgoing, cut.Instance.Settings.Direction);
        Assert.Equal(1, cut.Instance.Graph.ElementCount);

        // Layout.
        cut.Find("[data-testid=rm-layout-TreeDown]").Click();
        Assert.Equal(RelationMapLayout.TreeDown, cut.Instance.Settings.Layout);
        Assert.Contains("on", cut.Find("[data-testid=rm-layout-TreeDown]").ClassName);
        _ = ws;
    }

    [Fact]
    public void Include_Owned_And_Search_Highlight()
    {
        var m = SampleModels.Vehicle();
        var vehicle = m.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle");
        var (_, _, cut) = Open(vehicle, m);
        Assert.Contains("Vehicle", cut.Find("[data-testid=rm-root]").TextContent);
        Assert.DoesNotContain("mass", Names(cut));
        cut.Find("[data-testid=rm-include]").Click();
        cut.Find("[data-testid=rm-owned]").Change(true);
        Assert.Contains("mass", Names(cut));

        cut.Find("[data-testid=rm-search]").Input("tank");
        Assert.Contains("match", cut.Find("[data-testid=rm-hits]").TextContent);
        Assert.Contains("rm-node hit", cut.Find("svg.rm-svg").InnerHtml);
    }

    [Fact]
    public void Clicking_Selects_Expands_Collapses_And_Re_Centres()
    {
        var (ws, _, cut) = Open();
        var g = cut.Instance.Graph;
        var test = g.Nodes.Single(n => n.Element?.Name == "Acceleration Test");
        cut.InvokeAsync(() => cut.Instance.OnNodeClick(test.Id, false));
        Assert.Same(test.Element, ws.Selected);
        Assert.Contains(test.Id, cut.Instance.SelectedIds);
        Assert.Contains("rm-node sel", cut.Find("svg.rm-svg").InnerHtml);
        var vehicle = g.Nodes.Single(n => n.Element?.Name == "Vehicle");
        cut.InvokeAsync(() => cut.Instance.OnNodeClick(vehicle.Id, true));
        Assert.Equal(2, cut.Instance.SelectedIds.Count);
        Assert.Contains("(2)", cut.Find("[data-testid=rm-create-diagram]").TextContent);
        cut.InvokeAsync(() => cut.Instance.OnBackground());
        Assert.Empty(cut.Instance.SelectedIds);

        // Collapse "Engine Power" (depth 1 of 2): Engine goes; expand it again: Engine is back.
        var power = cut.Instance.Graph.Nodes.Single(n => n.Element?.Name == "Engine Power");
        cut.InvokeAsync(() => cut.Instance.OnNodeToggle(power.Id));
        Assert.DoesNotContain("Engine", Names(cut));
        Assert.Contains(power.Id, cut.Instance.Settings.Collapsed);
        cut.InvokeAsync(() => cut.Instance.OnNodeToggle(power.Id));
        Assert.Contains("Engine", Names(cut));

        // Expand a node at the last level: its neighbours appear one level further.
        cut.InvokeAsync(() => cut.Instance.SetPreset("All"));
        var engine = cut.Instance.Graph.Nodes.First(n => n.Depth == 2 && !n.IsStub && n.Hidden > 0);   // something lies behind it
        cut.InvokeAsync(() => cut.Instance.OnNodeToggle(engine.Id));
        Assert.Contains(engine.Id, cut.Instance.Settings.Expanded);
        Assert.Contains(cut.Instance.Graph.Nodes, n => n.Depth == 3 && n.Parent?.Id == engine.Id);

        // Re-centre (the node context menu entry).
        cut.InvokeAsync(() => cut.Instance.ReCentre(engine.Element!));
        Assert.Equal(engine.Id, cut.Instance.Settings.RootId);
        Assert.Empty(cut.Instance.Settings.Expanded);
        Assert.Contains(engine.Element!.DisplayName, cut.Find("[data-testid=rm-root]").TextContent);
    }

    [Fact]
    public void More_Stubs_Show_The_Next_Batch()
    {
        var m = new ModelRepository("Hub");
        var hub = m.Create(m.Root, ElementKind.Block, "Hub");
        for (var i = 0; i < 120; i++) m.Connect(ElementKind.Satisfy, hub, m.Create(m.Root, ElementKind.Requirement, $"R{i:000}"));
        var (_, _, cut) = Open(hub, m);
        Assert.Contains("+80 more", cut.Find("svg.rm-svg").InnerHtml);
        Assert.Contains($"data-stub=\"{hub.Id}\"", cut.Find("svg.rm-svg").InnerHtml);
        cut.InvokeAsync(() => cut.Instance.OnStub(hub.Id));
        Assert.Contains("+40 more", cut.Find("svg.rm-svg").InnerHtml);
        Assert.Equal(81, cut.Instance.Graph.ElementCount);
    }

    [Fact]
    public async Task Create_Diagram_From_Selection_Opens_A_Real_Diagram()
    {
        var (ws, _, cut) = Open();
        Assert.True(cut.Find("[data-testid=rm-create-diagram]").HasAttribute("disabled"));
        foreach (var name in new[] { "Acceleration", "Vehicle", "Acceleration Test" })
        {
            var id = cut.Instance.Graph.Nodes.Single(n => n.Element?.Name == name).Id;
            await cut.InvokeAsync(() => cut.Instance.OnNodeClick(id, true));
        }
        var before = ws.Model.Diagrams.Count();
        cut.Find("[data-testid=rm-create-diagram]").Click();
        cut.WaitForAssertion(() => Assert.Equal(before + 1, ws.Model.Diagrams.Count()));
        var d = ws.Active!.Diagram;
        Assert.Equal(DiagramType.Requirement, d.DiagramType);
        Assert.Equal("Acceleration relations", d.Name);
        Assert.Equal(3, d.Nodes.Count);
        Assert.Contains(d.Edges, e => e.Relationship.Kind == ElementKind.Satisfy);
        Assert.Contains("Created", ws.Status);
    }

    [Fact]
    public void Saving_Makes_A_Relation_Map_Element_That_Reopens_With_Its_Settings()
    {
        var (ws, tab, cut) = Open();
        cut.Find("[data-testid=rm-depth-inc]").Click();
        cut.Find("[data-testid=rm-layout-TreeRight]").Click();
        var saved = cut.Instance.SaveTo("Acceleration trace", null)!;
        Assert.Equal(ElementKind.RelationMap, saved.Kind);
        Assert.Same(saved, tab.Element);
        Assert.Equal("Acceleration trace", tab.Title);
        var settings = RelationMaps.Load(saved);
        Assert.Equal(3, settings.Depth);
        Assert.Equal(RelationMapLayout.TreeRight, settings.Layout);

        // Reopening the saved element brings its tab forward; deleting it closes the tab.
        var again = ws.OpenView(ViewTabKind.RelationMap, saved, RelationMaps.Load(saved));
        Assert.Same(tab, again);
        ws.Model.Delete(saved);
        Assert.DoesNotContain(tab, ws.OpenViews);
    }

    [Fact]
    public void Export_Svg_And_A_Missing_Root()
    {
        var (ws, _, cut) = Open();
        var svg = cut.Instance.ExportSvg();
        Assert.StartsWith("<svg", svg);
        Assert.Contains("Relation map of Acceleration", svg);
        Assert.DoesNotContain("data-node", svg);
        cut.Find("[data-testid=rm-more]").Click();
        cut.Find("[data-testid=rm-export-svg]").Click();
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "meridian.download" && (string)i.Arguments[0]! == "Relation map of Acceleration.svg");

        ws.Model.Delete(Req(ws.Model, "REQ-002"));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=rm-empty]")));
    }
}
