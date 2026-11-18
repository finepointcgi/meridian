using Bunit;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>Pen, highlighter and eraser gestures on the whiteboard canvas.</summary>
public class InkWebTests : TestContext
{
    public InkWebTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        Services.AddSingleton<ImageCache>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private (Workspace Ws, DiagramController C) Board()
    {
        var m = new ModelRepository("Board");
        var d = m.CreateDiagram(m.Root, DiagramType.Whiteboard, "Ideas");
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(m);
        var c = ws.Open(d);
        c.SetZoom(1);
        return (ws, c);
    }

    private static void Drag(DiagramController c, params Pt[] model)
    {
        var p = c.ToScreen(model[0]);
        c.PointerDown(p.X, p.Y, 0, Mods.None, 1);
        foreach (var q in model.Skip(1)) { var s = c.ToScreen(q); c.PointerMove(s.X, s.Y); }
        c.PointerUp(Mods.None);
    }

    private static Pt[] Wave(double y) => Enumerable.Range(0, 30).Select(i => new Pt(100 + i * 6, y + Math.Sin(i / 4.0) * 20)).ToArray();

    [Fact]
    public void Pen_Drag_Draws_A_Stroke_And_The_Tool_Stays_On()
    {
        var (ws, c) = Board();
        c.SetTool(new PaletteItem(ElementKind.Shape, Strokes.PenSubtype));
        Assert.True(c.InkTool);

        var pts = Wave(100);
        var first = c.ToScreen(pts[0]);
        c.PointerDown(first.X, first.Y, 0, Mods.None, 1);
        Assert.Equal(DiagramController.Gesture.Draw, c.Current);
        foreach (var q in pts.Skip(1)) { var s = c.ToScreen(q); c.PointerMove(s.X, s.Y); }
        Assert.True(c.InkPreview!.Count > 20);
        c.PointerUp(Mods.None);

        var stroke = Assert.Single(c.Diagram.Nodes);
        Assert.True(Strokes.IsStroke(stroke));
        Assert.True(Strokes.PointsOf(stroke, stroke.Bounds).Count < pts.Length);   // simplified
        Assert.Null(c.InkPreview);
        Assert.True(c.InkTool);   // keep drawing

        Drag(c, Wave(200));
        Assert.Equal(2, c.Diagram.Nodes.Count);
        ws.Model.History.Undo();
        Assert.Single(c.Diagram.Nodes);
    }

    [Fact]
    public void Strokes_Are_Picked_Near_The_Ink_Moved_And_Formatted()
    {
        var (ws, c) = Board();
        c.SetTool(new PaletteItem(ElementKind.Shape, Strokes.PenSubtype));
        Drag(c, new Pt(100, 100), new Pt(150, 150), new Pt(200, 200));
        c.SetTool(null);
        var n = c.Diagram.Nodes.Single();

        Assert.Same(n, c.HitNode(new Pt(150, 150)));
        Assert.Null(c.HitNode(new Pt(190, 110)));   // inside the box, far from the ink

        Drag(c, new Pt(150, 150), new Pt(250, 160));
        Assert.Contains(n, c.SelectedNodes);
        Assert.True(Strokes.PointsOf(n, c.BoundsOf(n))[0].X > 190);   // the ink moved with its box

        c.SetSelectionStyle("stroke", "#3fbf7f");
        c.SetSelectionStyle("width", "4.5");
        Assert.Equal("#3fbf7f", n.Style["stroke"]);
        // The next stroke takes the colour and width last chosen.
        c.SetTool(new PaletteItem(ElementKind.Shape, Strokes.PenSubtype));
        Assert.Equal("#3fbf7f", c.InkColor);
        Drag(c, new Pt(400, 400), new Pt(450, 420));
        var next = c.Diagram.Nodes.Last();
        Assert.Equal(("#3fbf7f", "4.5"), (next.Style["stroke"], next.Style["width"]));

        c.SetTool(null);
        c.Select(next.Element);
        c.RemoveSelectionFromDiagram();
        Assert.Single(c.Diagram.Nodes);
        ws.Model.History.Undo();
        Assert.Equal(2, c.Diagram.Nodes.Count);
    }

    [Fact]
    public void Highlighter_Is_Wide_And_Goes_On_The_Active_Layer()
    {
        var (ws, c) = Board();
        var layer = ws.Model.AddLayer(c.Diagram, "Notes");
        c.ActiveLayerId = layer.Id;
        c.SetTool(new PaletteItem(ElementKind.Shape, Strokes.HighlighterSubtype));
        Drag(c, new Pt(100, 100), new Pt(300, 100));
        var n = c.Diagram.Nodes.Single();
        Assert.True(Strokes.IsHighlighter(n.Element));
        Assert.Equal(layer.Id, DiagramExtras.LayerIdOf(n));
        Assert.Equal(14, Strokes.WidthOf(n));
    }

    [Fact]
    public void Eraser_Deletes_Strokes_It_Touches_Only()
    {
        var (ws, c) = Board();
        var shape = ws.Model.Create(ws.Model.Root, ElementKind.Shape, "Box");
        ws.Model.Set(shape, FieldKeys.Subtype, "rectangle");
        ws.Model.AddNode(c.Diagram, shape, new Bounds(500, 500, 120, 60));
        c.SetTool(new PaletteItem(ElementKind.Shape, Strokes.PenSubtype));
        Drag(c, new Pt(100, 100), new Pt(200, 100));
        Drag(c, new Pt(100, 200), new Pt(200, 200));
        Assert.Equal(3, c.Diagram.Nodes.Count);

        c.SetTool(new PaletteItem(ElementKind.Shape, Strokes.EraserTool));
        Drag(c, new Pt(550, 520));                         // a shape: untouched
        Assert.Equal(3, c.Diagram.Nodes.Count);
        Drag(c, new Pt(150, 60), new Pt(150, 100), new Pt(150, 140));   // crosses the first stroke
        Assert.Equal(2, c.Diagram.Nodes.Count);
        Assert.Contains(c.Diagram.Nodes, n => n.Element == shape);
        Assert.DoesNotContain(ws.Model.AllElements, e => Strokes.IsStroke(e) && e.Get(Strokes.PointsField).Length > 0 && c.Diagram.NodeFor(e) == null);

        ws.Model.History.Undo();
        Assert.Equal(3, c.Diagram.Nodes.Count);
    }
}
