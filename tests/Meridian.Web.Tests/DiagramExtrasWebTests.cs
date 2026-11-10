using Bunit;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>Canvas behaviour of images, links and layers, and the gallery / layers / tour components.</summary>
public class DiagramExtrasWebTests : TestContext
{
    private const string Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    public DiagramExtrasWebTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        Services.AddSingleton<ImageCache>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private (Workspace Ws, DiagramController C, DiagramNode A, DiagramNode B) Board()
    {
        var m = new ModelRepository("Board");
        var d = m.CreateDiagram(m.Root, DiagramType.Whiteboard, "Ideas");
        DiagramNode Shape(string name, double x)
        {
            var e = m.Create(m.Root, ElementKind.Shape, name);
            m.Set(e, FieldKeys.Subtype, "rectangle");
            return m.AddNode(d, e, new Bounds(x, 40, 120, 60));
        }
        var a = Shape("Alpha", 0);
        var b = Shape("Beta", 300);
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(m);
        var c = ws.Open(d);
        c.SetZoom(1);
        return (ws, c, a, b);
    }

    private static (double X, double Y) Screen(DiagramController c, double x, double y) { var p = c.ToScreen(new Pt(x, y)); return (p.X, p.Y); }

    [Fact]
    public void Images_Insert_At_Natural_Size_And_Keep_Aspect_On_Resize()
    {
        var (ws, c, _, _) = Board();
        var node = c.InsertImage(Png, 800, 400)!;
        Assert.Equal((320, 160), (node.Bounds.Width, node.Bounds.Height));
        Assert.Contains(node, c.SelectedNodes);

        // Drag the bottom-right handle: the height follows the width.
        var b = node.Bounds;
        var (hx, hy) = Screen(c, b.Right, b.Bottom);
        c.PointerDown(hx, hy, 0, Mods.None, 1);
        Assert.Equal(DiagramController.Gesture.Resize, c.Current);
        var (tx, ty) = Screen(c, b.Right + 160, b.Bottom + 10);
        c.PointerMove(tx, ty);
        c.PointerUp(Mods.None);
        Assert.Equal(2.0, node.Bounds.Width / node.Bounds.Height, 2);

        // Shift frees it.
        b = node.Bounds;
        (hx, hy) = Screen(c, b.Right, b.Bottom);
        c.PointerDown(hx, hy, 0, Mods.None, 1);
        (tx, ty) = Screen(c, b.Right, b.Bottom + 200);
        c.PointerMove(tx, ty, Mods.Shift);
        c.PointerUp(Mods.Shift);
        Assert.NotEqual(2.0, Math.Round(node.Bounds.Width / node.Bounds.Height, 2));

        Assert.Null(c.InsertImage("data:text/html;base64,PGI+", 10, 10));
        ws.Model.History.Undo(); ws.Model.History.Undo(); ws.Model.History.Undo();
        Assert.DoesNotContain(c.Diagram.Nodes, DiagramExtras.IsImage);
    }

    [Fact]
    public void Keep_Aspect_Anchors_The_Opposite_Corner()
    {
        var (x0, y0, x1, y1) = DiagramController.KeepAspect(new Bounds(0, 0, 200, 100), 0, -100, 40, 200, 100);
        Assert.Equal((200.0, 100.0), (x1, y1));
        Assert.Equal(2.0, (x1 - x0) / (y1 - y0), 3);
    }

    [Fact]
    public void Links_Follow_On_Ctrl_Click_And_Icon()
    {
        var (_, c, a, b) = Board();
        c.Select(a.Element);
        c.SetSelectionLink("example.com");
        Assert.Equal("https://example.com/", c.SelectionLink());

        // A plain click selects; Ctrl-click follows.
        var (sx, sy) = Screen(c, a.Bounds.Center.X, a.Bounds.Center.Y);
        c.PointerDown(sx, sy, 0, Mods.None, 1); c.PointerUp(Mods.None);
        Assert.Null(c.LinkRequest);
        c.PointerDown(sx, sy, 0, Mods.Ctrl, 1); c.PointerUp(Mods.Ctrl);
        Assert.Equal("https://example.com/", c.LinkRequest);
        c.LinkRequest = null;

        // The link icon follows it too.
        var badge = DiagramExtras.LinkBadgeBounds(a.Bounds).Center;
        (sx, sy) = Screen(c, badge.X, badge.Y);
        c.PointerDown(sx, sy, 0, Mods.None, 1); c.PointerUp(Mods.None);
        Assert.Equal("https://example.com/", c.LinkRequest);

        // Internal links navigate in the workspace.
        var ws = Services.GetRequiredService<Workspace>();
        c.Select(b.Element);
        c.SetSelectionLink(DiagramExtras.InternalLink(a.Element));
        DiagramLinks.Navigate(ws, DiagramExtras.InternalTarget(c.SelectionLink())!);
        Assert.Same(a.Element, ws.Selected);
    }

    [Fact]
    public void Locked_And_Hidden_Layers_Cannot_Be_Picked()
    {
        var (ws, c, a, b) = Board();
        var top = ws.Model.AddLayer(c.Diagram, "Top");
        c.Select(b.Element);
        c.MoveSelectionToLayer(top.Id);
        Assert.Equal(top.Id, DiagramExtras.LayerIdOf(b));

        ws.Model.UpdateLayer(c.Diagram, DiagramExtras.FindLayer(c.Diagram, top.Id)! with { Locked = true });
        c.ClearInvalidSelection();
        Assert.Empty(c.SelectedNodes);
        Assert.Null(c.HitNode(b.Bounds.Center));
        Assert.Same(a, c.HitNode(a.Bounds.Center));
        c.SelectAll();
        Assert.Equal([a], c.SelectedNodes);

        // Dragging over a locked shape doesn't move it.
        var before = b.Bounds;
        var (sx, sy) = Screen(c, b.Bounds.Center.X, b.Bounds.Center.Y);
        c.PointerDown(sx, sy, 0, Mods.None, 1);
        c.PointerMove(sx + 80, sy + 80);
        c.PointerUp(Mods.None);
        Assert.Equal(before, b.Bounds);

        ws.Model.UpdateLayer(c.Diagram, DiagramExtras.FindLayer(c.Diagram, top.Id)! with { Locked = false, Visible = false });
        Assert.DoesNotContain(b, c.VisibleNodes);
        Assert.Null(c.HitNode(b.Bounds.Center));

        // New shapes go on the active layer.
        c.ActiveLayerId = top.Id;
        var img = c.InsertImage(Png, 10, 10)!;
        Assert.Equal(top.Id, DiagramExtras.LayerIdOf(img));
    }

    [Fact]
    public void Layers_Panel_Adds_Hides_And_Moves()
    {
        var (ws, c, a, _) = Board();
        c.Select(a.Element);
        var cut = RenderComponent<LayersPanel>(p => p.Add(x => x.C, c));
        Assert.Single(cut.FindAll(".layer-row"));
        cut.Find(".layers-head .fmt-link").Click();
        Assert.Equal(2, DiagramExtras.Layers(c.Diagram).Count);
        Assert.Equal(2, cut.FindAll(".layer-row").Count);
        var added = DiagramExtras.Layers(c.Diagram).Last();
        cut.Find(".layers-move select").Change(added.Id);
        Assert.Equal(added.Id, DiagramExtras.LayerIdOf(a));
        cut.FindAll(".layer-row")[0].QuerySelector(".lbtn")!.Click();   // top row = new layer: hide
        Assert.False(DiagramExtras.FindLayer(c.Diagram, added.Id)!.Visible);
        Assert.True(DiagramExtras.IsHidden(c.Diagram, a));
    }

    [Fact]
    public void Template_Gallery_Shows_Every_Template_With_A_Thumbnail()
    {
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var provider = RenderComponent<MudBlazor.MudDialogProvider>();
        var dialogs = Services.GetRequiredService<MudBlazor.IDialogService>();
        _ = provider.InvokeAsync(() => TemplateGallery.ShowAsync(dialogs, forDiagram: false));
        provider.WaitForState(() => provider.FindAll(".tpl-card").Count > 0);
        var cards = provider.FindAll(".tpl-card");
        Assert.Equal(Templates.Projects.Count, cards.Count);
        Assert.All(provider.FindAll(".tpl-thumb img"), img => Assert.StartsWith("data:image/svg+xml;base64,", img.GetAttribute("src")));
    }

    [Fact]
    public void Tour_Starts_And_Steps_Through()
    {
        Services.GetRequiredService<Workspace>().UseLocalModel(SampleModels.Vehicle());
        var cut = RenderComponent<OnboardingTour>();
        cut.InvokeAsync(() => cut.Instance.StartAsync());
        Assert.Contains("Step 1 of", cut.Markup);
        cut.Find(".tour-btn.primary").Click();
        Assert.Contains("Step 2 of", cut.Markup);
        cut.Find(".tour-skip").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tour")));
    }
}
