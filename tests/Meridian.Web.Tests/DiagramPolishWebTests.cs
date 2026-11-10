using Bunit;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>Canvas: containers growing while children are dragged, connector routing styles, UAF palette entries.</summary>
public class DiagramPolishWebTests : TestContext
{
    public DiagramPolishWebTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        Services.AddSingleton<ImageCache>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private (Workspace Ws, DiagramController C) Open(ModelRepository m, Diagram d)
    {
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(m);
        var c = ws.Open(d);
        c.SetZoom(1);
        return (ws, c);
    }

    private static (double X, double Y) Screen(DiagramController c, double x, double y) { var p = c.ToScreen(new Pt(x, y)); return (p.X, p.Y); }

    [Fact]
    public void Dragging_A_Child_Out_Grows_Its_Container_Live_And_On_Drop()
    {
        var m = new ModelRepository("Pkg");
        var d = m.CreateDiagram(m.Root, DiagramType.Package, "Packages");
        var outer = m.Create(m.Root, ElementKind.Package, "Outer");
        var frame = m.AddNode(d, outer, new Bounds(0, 0, 300, 220));
        var child = m.AddNode(d, m.Create(outer, ElementKind.Package, "Inner"), new Bounds(40, 60, 120, 80), parent: frame);
        var (_, c) = Open(m, d);

        var (sx, sy) = Screen(c, 100, 100);
        c.PointerDown(sx, sy, 0, Mods.None, 1);
        var (tx, ty) = Screen(c, 400, 300);
        c.PointerMove(tx, ty);
        Assert.True(c.BoundsOf(frame).Right >= c.BoundsOf(child).Right, "container grows while dragging");
        c.PointerUp(Mods.None);

        Assert.True(frame.Bounds.Right >= child.Bounds.Right + ContainerLayout.Padding - 0.01);
        Assert.True(frame.Bounds.Bottom >= child.Bounds.Bottom + ContainerLayout.Padding - 0.01);
        m.History.Undo();   // one step: move and growth together
        Assert.Equal(new Bounds(0, 0, 300, 220), frame.Bounds);
        Assert.Equal(new Bounds(40, 60, 120, 80), child.Bounds);

        c.Select(outer);
        Assert.True(c.SelectionHasContainer);
        c.FitSelectionToContents();
        Assert.Equal(child.Bounds.Right + ContainerLayout.Padding, frame.Bounds.Right, 3);
    }

    [Fact]
    public void Connector_Style_Can_Be_Changed_Per_Connector_And_Clears_Manual_Bends()
    {
        var m = new ModelRepository("Styles");
        var d = m.CreateDiagram(m.Root, DiagramType.BlockDefinition, "Blocks");
        var a = m.AddNode(d, m.Create(m.Root, ElementKind.Block, "A"), new Bounds(0, 0, 120, 60));
        var b = m.AddNode(d, m.Create(m.Root, ElementKind.Block, "B"), new Bounds(300, 200, 120, 60));
        var e = m.AddEdge(d, m.Connect(ElementKind.Dependency, a.Element, b.Element))!;
        m.SetWaypoints(d, e, [new Pt(150, -50)]);
        var (_, c) = Open(m, d);
        c.Select(e.Relationship);
        Assert.Null(c.SelectedRouting);   // manual bends

        c.SetSelectedRouting(EdgeRouting.Curved);
        Assert.Empty(e.Waypoints);
        Assert.Equal(EdgeRouting.Curved, c.SelectedRouting);
        c.SetSelectedRouting(EdgeRouting.Orthogonal);   // the diagram default: no override kept
        Assert.False(e.Style.ContainsKey(EdgeRouter.EdgeKey));
        // Clicking on the routed path selects the connector.
        var pts = Notation.RouteIn(d, e, c.BoundsOf);
        var mid = Notation.Midpoint(pts);
        Assert.Same(e, c.HitEdge(mid));
    }

    [Fact]
    public void Uaf_Palette_Entries_Create_Stereotyped_Elements_And_Add_The_Profile()
    {
        var m = new ModelRepository("Enterprise");
        var d = m.CreateDiagram(m.Root, DiagramType.UafPersonnel, "Org");
        var (_, c) = Open(m, d);
        var post = Metamodel.PaletteItems(DiagramType.UafPersonnel).First(i => i.Stereotype == "Post");
        c.InsertItem(post);
        var created = d.Nodes.Single().Element;
        Assert.Equal(["Post"], created.Stereotypes);
        Assert.Contains(m.OfKind(ElementKind.Stereotype), s => s.Name == "Post");

        // Palette tiles of the same kind are told apart by their stereotype.
        var org = Metamodel.PaletteItems(DiagramType.UafPersonnel).First(i => i.Stereotype == "Organization");
        c.SetTool(org);
        Assert.True(c.IsTool(org));
        Assert.False(c.IsTool(post));
    }

    [Fact]
    public void Canvas_Renders_New_Diagram_Types()
    {
        var m = new ModelRepository("Views");
        var pkg = m.Create(m.Root, ElementKind.Package, "P");
        var d = Meridian.Core.Samples.Templates.CreateDiagram(m, pkg, "communication");
        var (_, c) = Open(m, d);
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = RenderComponent<DiagramView>(p => p.Add(x => x.C, c));
        Assert.Contains("1: placeOrder(cart)", cut.Markup);
    }
}
