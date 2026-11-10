using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>Images, hyperlinks, layers and templates: persisted, replicated, undoable and exported.</summary>
public class DiagramExtrasTests
{
    // 1x1 transparent PNG
    private const string Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    private static (ModelRepository M, Diagram D, DiagramNode A, DiagramNode B, DiagramEdge E) Board()
    {
        var m = new ModelRepository("Board");
        var d = m.CreateDiagram(m.Root, DiagramType.Whiteboard, "Ideas");
        DiagramNode Shape(string name, double x)
        {
            var e = m.Create(m.Root, ElementKind.Shape, name);
            m.Set(e, FieldKeys.Subtype, "rectangle");
            return m.AddNode(d, e, new Bounds(x, 40, 120, 60));
        }
        var a = Shape("Alpha", 40);
        var b = Shape("Beta", 300);
        var l = m.Connect(ElementKind.Line, a.Element, b.Element);
        var edge = m.AddEdge(d, l)!;
        return (m, d, a, b, edge);
    }

    [Fact]
    public void Image_Data_Uris_Are_Validated()
    {
        Assert.True(DiagramExtras.IsSafeImageDataUri(Png));
        Assert.False(DiagramExtras.IsSafeImageDataUri("data:text/html;base64,PHNjcmlwdD4="));
        Assert.False(DiagramExtras.IsSafeImageDataUri("data:image/png;base64,abc\"onload=\"x"));
        Assert.False(DiagramExtras.IsSafeImageDataUri("javascript:alert(1)"));
        Assert.False(DiagramExtras.IsSafeImageDataUri("data:image/png;base64," + new string('A', DiagramExtras.MaxImageDataUriLength)));
        Assert.Equal("image/png", DiagramExtras.DecodeImage(Png)!.Value.Mime);
        Assert.Equal((320, 160), DiagramExtras.FitImageSize(1000, 500));
        Assert.Equal((100, 50), DiagramExtras.FitImageSize(100, 50));
    }

    [Fact]
    public void Images_Persist_Replicate_Undo_And_Export()
    {
        var (m, d, _, _, _) = Board();
        var replica = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        m.OpEmitted += replica.ApplyRemote;

        var node = m.AddImage(d, m.Root, Png, new Bounds(500, 40, 200, 100), "Logo");
        Assert.True(DiagramExtras.IsImage(node));
        Assert.Throws<InvalidOperationException>(() => m.AddImage(d, m.Root, "data:text/plain;base64,aGk=", new Bounds(0, 0, 10, 10)));

        var svg = SvgRenderer.Render(m, d);
        Assert.Contains("<image href=\"" + Png + "\"", svg);
        Assert.Contains("Logo", svg);

        // The editor swaps the inline data for a short cached URL.
        var sb = new System.Text.StringBuilder();
        SvgRenderer.RenderBody(sb, m, d, n => n.Bounds, theme: SvgTheme.Light with { ImageHref = _ => "/_img/abc" });
        Assert.Contains("href=\"/_img/abc\"", sb.ToString());
        Assert.DoesNotContain("base64", sb.ToString());

        var rd = replica.Diagrams.Single();
        Assert.Equal(Png, rd.Nodes.Single(n => DiagramExtras.IsImage(n)).Element.Get(DiagramExtras.ImageField));

        var back = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        Assert.Equal(Png, back.Diagrams.Single().Nodes.Single(DiagramExtras.IsImage).Element.Get(DiagramExtras.ImageField));

        m.History.Undo();
        Assert.DoesNotContain(d.Nodes, DiagramExtras.IsImage);
        Assert.DoesNotContain(rd.Nodes, DiagramExtras.IsImage);
    }

    [Fact]
    public void Links_Are_Normalized_Rendered_And_Exported_As_Anchors()
    {
        Assert.Equal("https://example.com/", DiagramExtras.NormalizeLink("example.com"));
        Assert.Equal("http://x.org/a?b=1", DiagramExtras.NormalizeLink(" http://x.org/a?b=1 "));
        Assert.Null(DiagramExtras.NormalizeLink("javascript:alert(1)"));
        Assert.Null(DiagramExtras.NormalizeLink("file:///etc/passwd"));
        Assert.Equal("element:_abc", DiagramExtras.NormalizeLink("element:_abc"));
        Assert.Equal("_abc", DiagramExtras.InternalTarget("element:_abc"));

        var (m, d, a, b, _) = Board();
        var replica = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        m.OpEmitted += replica.ApplyRemote;
        m.SetLink(d, [a], "example.com/docs");
        m.SetLink(d, [b], DiagramExtras.InternalLink(a.Element));
        Assert.Throws<InvalidOperationException>(() => m.SetLink(d, [a], "javascript:void(0)"));

        Assert.Equal("https://example.com/docs", DiagramExtras.LinkOf(a));
        Assert.Equal("https://example.com/docs", DiagramExtras.LinkOf(replica.Diagrams.Single().FindNode(a.Id)!));

        var svg = SvgRenderer.Render(m, d);
        Assert.Contains("<a href=\"https://example.com/docs\" target=\"_blank\" rel=\"noopener noreferrer\">", svg);
        Assert.Equal(2, CountOf(svg, "class=\"link-badge\""));
        Assert.DoesNotContain("href=\"element:", svg);   // internal links only work inside Meridian

        var sb = new System.Text.StringBuilder();
        SvgRenderer.RenderBody(sb, m, d, n => n.Bounds);
        Assert.DoesNotContain("<a href", sb.ToString());  // the canvas must not navigate on click

        var back = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        Assert.Equal("element:" + a.Element.Id, DiagramExtras.LinkOf(back.Diagrams.Single().FindNode(b.Id)!));

        m.History.Undo();
        Assert.Null(DiagramExtras.LinkOf(b));
    }

    [Fact]
    public void Layers_Hide_Lock_Order_Persist_And_Replicate()
    {
        var (m, d, a, b, e) = Board();
        var replica = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        m.OpEmitted += replica.ApplyRemote;

        Assert.Single(DiagramExtras.Layers(d));
        Assert.Same(d, DiagramExtras.VisibleView(d));

        var notes = m.AddLayer(d, "Notes");
        Assert.Equal(["Default", "Notes"], DiagramExtras.Layers(d).Select(l => l.Name));
        m.MoveToLayer(d, [b], [], notes.Id);
        Assert.Equal(notes.Id, DiagramExtras.LayerIdOf(b));

        // Upper layers draw last (on top).
        m.ReorderLayer(d, notes.Id, -1);
        Assert.Equal(["Notes", "Default"], DiagramExtras.Layers(d).Select(l => l.Name));
        Assert.Equal([b, a], DiagramExtras.VisibleView(d).Nodes);
        m.ReorderLayer(d, notes.Id, +1);
        Assert.Equal([a, b], DiagramExtras.VisibleView(d).Nodes);

        m.UpdateLayer(d, DiagramExtras.FindLayer(d, notes.Id)! with { Visible = false });
        Assert.True(DiagramExtras.IsHidden(d, b));
        Assert.True(DiagramExtras.IsHidden(d, e));  // a path to a hidden shape is hidden too
        Assert.Equal([a], DiagramExtras.VisibleView(d).Nodes);
        var svg = SvgRenderer.Render(m, d);
        Assert.Contains("Alpha", svg);
        Assert.DoesNotContain("Beta", svg);

        // The replica and a saved file see the same layers.
        var rd = replica.Diagrams.Single();
        Assert.True(DiagramExtras.IsHidden(rd, rd.FindNode(b.Id)!));
        var back = ProjectFile.Deserialize(ProjectFile.Serialize(m)).Diagrams.Single();
        Assert.True(DiagramExtras.IsHidden(back, back.FindNode(b.Id)!));
        Assert.Equal("Notes", DiagramExtras.Layers(back).Single(l => l.Id == notes.Id).Name);

        m.UpdateLayer(d, DiagramExtras.FindLayer(d, notes.Id)! with { Visible = true, Locked = true, Name = "Locked notes" });
        Assert.True(DiagramExtras.IsLocked(d, b));
        Assert.False(DiagramExtras.IsLocked(d, a));

        m.History.Undo();
        Assert.False(DiagramExtras.IsLocked(d, b));
        Assert.True(DiagramExtras.IsHidden(d, b));

        m.RemoveLayer(d, notes.Id);
        Assert.Equal(DiagramExtras.DefaultLayerId, DiagramExtras.LayerIdOf(b));
        Assert.False(DiagramExtras.IsHidden(d, b));
        Assert.Throws<InvalidOperationException>(() => m.RemoveLayer(d, DiagramExtras.DefaultLayerId));
        Assert.Equal(DiagramExtras.DefaultLayerId, DiagramExtras.LayerIdOf(rd.FindNode(b.Id)!));
    }

    [Fact]
    public void Png_Friendly_Render_Options()
    {
        var (m, d, _, _, _) = Board();
        var light = SvgRenderer.Render(m, d);
        var transparent = SvgRenderer.Render(m, d, transparent: true);
        var dark = SvgRenderer.Render(m, d, theme: SvgTheme.Dark);
        Assert.Contains($"fill=\"{SvgTheme.Light.Background}\"", light);
        Assert.True(transparent.Length < light.Length);
        Assert.Contains(SvgTheme.Dark.Background, dark);
    }

    [Fact]
    public void Every_Project_Template_Builds_Validates_And_Renders()
    {
        foreach (var t in Templates.Projects)
        {
            var m = Templates.CreateProject(t.Id, "My " + t.Name);
            Assert.Equal("My " + t.Name, m.Root.Name);
            Assert.NotEmpty(m.Diagrams);
            Assert.False(m.History.CanUndo);
            foreach (var d in m.Diagrams)
            {
                var svg = SvgRenderer.Render(m, d);
                Assert.StartsWith("<svg", svg);
            }
            if (t.Id != "blank") Assert.Contains(m.Diagrams, d => d.Nodes.Count > 0);
            // Survives a save / load round trip.
            var back = ProjectFile.Deserialize(ProjectFile.Serialize(m));
            Assert.Equal(m.AllElements.Count(), back.AllElements.Count());
        }
    }

    [Fact]
    public void Every_Diagram_Template_Creates_Content_In_One_Undo_Step()
    {
        foreach (var t in Templates.Diagrams)
        {
            var m = new ModelRepository("M");
            var pkg = m.Create(m.Root, ElementKind.Package, "P");
            m.History.Clear();
            var d = Templates.CreateDiagram(m, pkg, t.Id);
            Assert.Equal(t.Type, d.DiagramType);
            Assert.True(d.Nodes.Count >= 3, t.Id);
            Assert.Same(pkg, d.Owner);
            m.History.Undo();
            Assert.Empty(m.Diagrams);
            Assert.Empty(pkg.OwnedElements);
        }
    }

    private static int CountOf(string s, string part)
    {
        var n = 0;
        for (var i = s.IndexOf(part, StringComparison.Ordinal); i >= 0; i = s.IndexOf(part, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }
}
