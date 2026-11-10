using Bunit;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>Multi-selection on the diagram canvas: shapes and connectors together, bulk formatting, bulk linking, the bulk inspector.</summary>
public class MultiSelectTests : TestContext
{
    public MultiSelectTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        Services.AddSingleton<ImageCache>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>A use case diagram: an actor, three use cases (UC1 associated with the actor) and a requirement.</summary>
    private sealed record Fixture(ModelRepository M, Diagram D, DiagramNode Actor, DiagramNode[] UseCases, DiagramEdge Edge, Element Requirement);

    private static Fixture Build()
    {
        var m = new ModelRepository("Shop");
        var d = m.CreateDiagram(m.Root, DiagramType.UseCase, "Use cases");
        var actor = m.AddNode(d, m.Create(m.Root, ElementKind.Actor, "Customer"), new Bounds(0, 100, 60, 90));
        var ucs = new[] { "Browse", "Order", "Pay" }
            .Select((n, i) => m.AddNode(d, m.Create(m.Root, ElementKind.UseCase, n), new Bounds(300, 20 + i * 120, 140, 60))).ToArray();
        var edge = m.AddEdge(d, m.Connect(ElementKind.Association, actor.Element, ucs[0].Element))!;
        var req = m.Create(m.Root, ElementKind.Requirement, "Checkout");
        m.History.Clear();
        return new Fixture(m, d, actor, ucs, edge, req);
    }

    private (Workspace Ws, DiagramController C) Open(Fixture f)
    {
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(f.M);
        var c = ws.Open(f.D);
        c.SetZoom(1);
        return (ws, c);
    }

    private static (double X, double Y) Screen(DiagramController c, Pt p) { var s = c.ToScreen(p); return (s.X, s.Y); }

    private static void Click(DiagramController c, Pt at, Mods mods = Mods.None, int button = 0)
    {
        var (x, y) = Screen(c, at);
        c.PointerDown(x, y, button, mods, 1);
        c.PointerUp(mods);
    }

    private static Pt EdgeMid(DiagramController c, DiagramEdge e) => Notation.Midpoint(Notation.RouteIn(c.Diagram, e, c.BoundsOf));

    [Fact]
    public void Shift_And_Cmd_Click_Add_And_Remove_Shapes_And_Connectors()
    {
        var f = Build();
        var (ws, c) = Open(f);

        Click(c, f.UseCases[1].Bounds.Center);
        Click(c, f.UseCases[2].Bounds.Center, Mods.Shift);
        Click(c, EdgeMid(c, f.Edge), Mods.Meta);
        Assert.Equal(3, c.SelectionCount);
        Assert.Contains(f.Edge, c.SelectedEdges);
        Assert.Null(c.SelectedEdge);             // not "the one connector" while shapes are selected too
        Assert.Null(ws.Selected);                // the inspector shows the bulk panel instead

        Click(c, EdgeMid(c, f.Edge), Mods.Ctrl);  // toggles back out
        Assert.Equal(2, c.SelectionCount);
        Assert.Empty(c.SelectedEdges);
        Click(c, f.UseCases[1].Bounds.Center, Mods.Shift);
        Assert.Equal(f.UseCases[2].Element, ws.Selected);

        // Right-clicking part of a multi-selection keeps it, so the context menu acts on all of it.
        Click(c, f.UseCases[1].Bounds.Center, Mods.Shift);
        Click(c, EdgeMid(c, f.Edge), Mods.Shift);
        Click(c, f.UseCases[2].Bounds.Center, button: 2);
        Assert.Equal(3, c.SelectionCount);

        Assert.True(c.Key("Escape", Mods.None));
        Assert.Equal(0, c.SelectionCount);
        Assert.True(c.Key("a", Mods.Meta));
        Assert.Equal(f.D.Nodes.Count + f.D.Edges.Count, c.SelectionCount);
    }

    [Fact]
    public void Rubber_Band_Selects_Shapes_And_Connectors_Inside_The_Box()
    {
        var f = Build();
        var (_, c) = Open(f);
        var (x0, y0) = Screen(c, new Pt(-20, 0));
        var (x1, y1) = Screen(c, new Pt(470, 220));   // actor, Browse, Order and the association; not Pay
        c.PointerDown(x0, y0, 0, Mods.None, 1);
        c.PointerMove(x1, y1);
        c.PointerUp(Mods.None);

        Assert.Equal(3, c.SelectedNodes.Count);
        Assert.DoesNotContain(f.UseCases[2], c.SelectedNodes);
        Assert.Contains(f.Edge, c.SelectedEdges);

        // Shift + rubber band adds to the selection.
        (x0, y0) = Screen(c, new Pt(280, 250));
        (x1, y1) = Screen(c, new Pt(470, 330));
        c.PointerDown(x0, y0, 0, Mods.Shift, 1);
        c.PointerMove(x1, y1);
        c.PointerUp(Mods.Shift);
        Assert.Equal(5, c.SelectionCount);
    }

    [Fact]
    public void Bulk_Colour_Change_Applies_To_Every_Selected_Item_In_One_Undo_Step()
    {
        var f = Build();
        var (_, c) = Open(f);
        f.M.SetNodeStyle(f.D, f.UseCases[0], "stroke", "#4f8cff");
        f.M.History.Clear();
        c.SelectElements([f.UseCases[0].Element, f.UseCases[1].Element, f.Edge.Relationship]);

        Assert.True(c.IsMixedStyle("stroke"));
        Assert.Null(c.SelectionStyle("stroke"));

        c.SetSelectionStyle("stroke", "#ff6b6b");
        c.SetSelectionStyle("dash", "dashed");
        c.SetSelectionStyle("width", "3");
        Assert.Equal("#ff6b6b", f.UseCases[0].Style["stroke"]);
        Assert.Equal("#ff6b6b", f.UseCases[1].Style["stroke"]);
        Assert.Equal("#ff6b6b", f.Edge.Style["stroke"]);
        Assert.Equal("dashed", f.Edge.Style["dash"]);
        Assert.False(c.IsMixedStyle("stroke"));
        Assert.Equal("#ff6b6b", c.SelectionStyle("stroke"));

        // Fill is for shapes only; the connector is untouched and the value isn't "mixed" because of it.
        c.SetSelectionStyle("fill", "#d5e8d4");
        Assert.False(f.Edge.Style.ContainsKey("fill"));
        Assert.Equal("#d5e8d4", c.SelectionStyle("fill"));

        f.M.History.Undo();   // fill
        f.M.History.Undo();   // width
        f.M.History.Undo();   // dash
        f.M.History.Undo();   // stroke: one step for all three items
        Assert.Equal("#4f8cff", f.UseCases[0].Style["stroke"]);
        Assert.False(f.UseCases[1].Style.ContainsKey("stroke"));
        Assert.Empty(f.Edge.Style);
        Assert.False(f.M.History.CanUndo);

        // The renderer draws the connector's colour, pattern and width.
        f.M.History.Redo(); f.M.History.Redo(); f.M.History.Redo();
        var svg = Meridian.Serialization.SvgRenderer.Render(f.M, f.D);
        Assert.Contains("stroke=\"#ff6b6b\" stroke-width=\"3\" stroke-dasharray=\"6,4\"", svg);
    }

    [Fact]
    public void Bulk_Link_Connects_Every_Selected_Use_Case_To_An_Actor_In_One_Undo_Step()
    {
        var f = Build();
        var (_, c) = Open(f);
        var other = f.M.Create(f.M.Root, ElementKind.Actor, "Clerk");   // not on the diagram yet
        f.M.History.Clear();
        c.SelectElements(f.UseCases.Select(n => n.Element));

        var options = c.BulkLinkOptions(other);
        Assert.Contains(options, o => o.Kind == ElementKind.Association);
        Assert.DoesNotContain(options, o => o.Kind is ElementKind.Include or ElementKind.Satisfy);
        Assert.Contains(c.BulkLinkCandidates(null), x => ReferenceEquals(x, other));

        var created = c.BulkLink(ElementKind.Association, other);
        Assert.Equal(3, created.Count);
        Assert.All(created, r => Assert.Same(other, r.Target));
        Assert.All(created, r => Assert.NotNull(f.D.EdgeFor(r)));
        Assert.NotNull(f.D.NodeFor(other));             // the target was put on the diagram beside the selection
        Assert.Equal(3, c.SelectionCount);              // selection kept

        f.M.History.Undo();
        Assert.Empty(f.M.RelationshipsOf(other));
        Assert.Null(f.D.NodeFor(other));
        Assert.False(f.M.History.CanUndo);

        // Linking again skips pairs that are already linked (UC1 is already associated with Customer).
        f.M.History.Redo();
        var again = c.BulkLink(ElementKind.Association, f.Actor.Element);
        Assert.Equal(2, again.Count);
    }

    [Fact]
    public void Include_And_Trace_To_Requirement_Pick_Valid_Directions()
    {
        var f = Build();
        var (_, c) = Open(f);
        var login = f.M.Create(f.M.Root, ElementKind.UseCase, "Log in");
        c.SelectElements([f.UseCases[1].Element, f.UseCases[2].Element]);

        var include = c.BulkLinkOptions(login).Single(o => o.Kind == ElementKind.Include);
        Assert.True(include.SelectionIsSource);
        c.BulkLink(ElementKind.Include, login);
        Assert.Equal(2, f.M.Incoming(login, ElementKind.Include).Count());

        Assert.DoesNotContain(c.BulkLinkOptions(f.Requirement), o => o.Kind is ElementKind.Association or ElementKind.Composition);
        var traced = c.TraceToRequirement(f.Requirement);
        Assert.Equal(2, traced.Count);
        Assert.All(traced, r => Assert.Equal(ElementKind.Satisfy, r.Kind));
        Assert.All(traced, r => Assert.Same(f.Requirement, r.Target));
    }

    [Fact]
    public void Stereotypes_And_Shared_Fields_Edit_The_Whole_Selection()
    {
        var f = Build();
        var (_, c) = Open(f);
        f.M.Set(f.UseCases[0].Element, FieldKeys.Stereotypes, "core");
        f.M.History.Clear();
        c.SelectElements(f.UseCases.Select(n => n.Element));

        var (all, some) = c.SelectionStereotypes();
        Assert.Empty(all);
        Assert.Equal(["core"], some);

        c.ApplyStereotype("«business»");
        Assert.All(f.UseCases, n => Assert.Contains("business", n.Element.Stereotypes));
        c.RemoveStereotype("core");
        Assert.All(f.UseCases, n => Assert.DoesNotContain("core", n.Element.Stereotypes));

        Assert.Equal(ElementKind.UseCase, c.CommonKind);
        Assert.Contains(FieldKeys.Documentation, c.SharedFields());
        Assert.DoesNotContain(FieldKeys.Name, c.SharedFields());
        c.SetSharedField(FieldKeys.Documentation, "Customer facing");
        Assert.Equal("Customer facing", c.SharedValue(FieldKeys.Documentation));

        f.M.History.Undo();
        Assert.Equal("", c.SharedValue(FieldKeys.Documentation));   // every one back to empty
        f.M.History.Undo();
        Assert.Equal("core", f.UseCases[0].Element.Get(FieldKeys.Stereotypes).Split(',')[0].Trim());
    }

    [Fact]
    public void Touch_Long_Press_Adds_To_The_Selection_It_Had_Before_The_Press()
    {
        var f = Build();
        var (_, c) = Open(f);
        c.SelectElements([f.UseCases[0].Element]);
        // The finger goes down first (the canvas selects what is under it), then the long press fires.
        var (x, y) = Screen(c, f.UseCases[1].Bounds.Center);
        c.PointerDown(x, y, 0, Mods.None, 1);
        Assert.True(c.LongPressToggle(x, y));
        c.PointerUp(Mods.None);
        Assert.Equal(2, c.SelectionCount);
        Assert.Equal(new Bounds(300, 140, 140, 60), f.UseCases[1].Bounds);   // not moved

        var (ex, ey) = Screen(c, EdgeMid(c, f.Edge));
        c.PointerDown(ex, ey, 0, Mods.None, 1);
        Assert.True(c.LongPressToggle(ex, ey));
        c.PointerUp(Mods.None);
        Assert.Equal(3, c.SelectionCount);
        Assert.Contains(f.Edge, c.SelectedEdges);
        Assert.False(c.LongPressToggle(-500, -500));
    }

    [Fact]
    public void Remove_And_Delete_Act_On_Shapes_And_Connectors_Together()
    {
        var f = Build();
        var (_, c) = Open(f);
        c.SelectElements([f.UseCases[2].Element, f.Edge.Relationship]);
        c.RemoveSelectionFromDiagram();
        Assert.Null(f.D.NodeFor(f.UseCases[2].Element));
        Assert.Empty(f.D.Edges);
        Assert.True(f.M.Contains(f.Edge.Relationship));
        f.M.History.Undo();
        Assert.Single(f.D.Edges);
        Assert.Equal(4, f.D.Nodes.Count);
    }

    [Fact]
    public void Canvas_Highlights_Every_Selected_Connector_And_Shows_The_Count()
    {
        var f = Build();
        var e2 = f.M.AddEdge(f.D, f.M.Connect(ElementKind.Association, f.Actor.Element, f.UseCases[1].Element))!;
        var (_, c) = Open(f);
        c.SelectElements([f.Edge.Relationship, e2.Relationship, f.UseCases[2].Element]);
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = RenderComponent<DiagramView>(p => p.Add(x => x.C, c));
        Assert.Equal(2, cut.FindAll("polyline.edge-sel").Count);
        Assert.Contains("3 selected", cut.Find("[data-testid=selection-count]").TextContent);
    }

    [Fact]
    public void Inspector_Shows_N_Selected_With_Mixed_Values_And_Applies_Swatches_To_All()
    {
        var f = Build();
        var (ws, c) = Open(f);
        f.M.SetNodeStyle(f.D, f.UseCases[0], "fill", "#ffe6cc");
        c.SelectElements([f.UseCases[0].Element, f.UseCases[1].Element, f.Edge.Relationship]);
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = RenderComponent<Inspector>();

        Assert.Equal("3 selected", cut.Find("[data-testid=bulk-count]").TextContent.Trim());
        Assert.Contains("2 use cases, 1 connector", cut.Markup);
        Assert.Contains("Mixed", cut.Find("[data-testid=bulk-fill]").TextContent);
        Assert.Contains("Link to…", cut.Markup);

        f.M.History.Clear();
        cut.FindAll("[data-testid=bulk-stroke] + .swatches .swatch").First(s => s.GetAttribute("data-color") == "#ff6b6b").Click();
        Assert.All(new[] { f.UseCases[0].Style, f.UseCases[1].Style, f.Edge.Style }, s => Assert.Equal("#ff6b6b", s["stroke"]));
        f.M.History.Undo();
        Assert.False(f.M.History.CanUndo);
        Assert.All(new[] { f.UseCases[0].Style, f.UseCases[1].Style, f.Edge.Style }, s => Assert.False(s.ContainsKey("stroke")));
        Assert.Null(ws.Selected);
    }
}
