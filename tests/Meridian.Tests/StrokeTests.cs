using Meridian.Core.Model;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>Freehand pen and highlighter strokes: simplification, storage, rendering, persistence, replication, undo.</summary>
public class StrokeTests
{
    [Fact]
    public void Simplify_Drops_Points_On_Straight_Runs_And_Keeps_Corners()
    {
        var line = Enumerable.Range(0, 101).Select(i => new Pt(i, 0.01 * (i % 2))).ToList();
        Assert.Equal([new Pt(0, 0), new Pt(100, 0)], Strokes.Simplify(line, 0.5));

        var corner = Enumerable.Range(0, 51).Select(i => new Pt(i, 0)).Concat(Enumerable.Range(1, 50).Select(i => new Pt(50, i))).ToList();
        var s = Strokes.Simplify(corner, 0.5);
        Assert.Equal([new Pt(0, 0), new Pt(50, 0), new Pt(50, 50)], s);

        // Repeats collapse; one point stays one point.
        Assert.Single(Strokes.Simplify([new Pt(3, 4), new Pt(3, 4), new Pt(3, 4)], 1));
        Assert.Empty(Strokes.Simplify([new Pt(double.NaN, 1)], 1));
    }

    [Fact]
    public void Simplify_Caps_Very_Long_Strokes()
    {
        var zigzag = Enumerable.Range(0, 5_000).Select(i => new Pt(i, i % 2 * 50)).ToList();
        var s = Strokes.Simplify(zigzag, 0.1);
        Assert.Equal(Strokes.MaxPoints, s.Count);
        Assert.Equal(zigzag[0], s[0]);
        Assert.Equal(zigzag[^1], s[^1]);
    }

    [Fact]
    public void Points_Are_Stored_Relative_To_The_Shape_So_Moves_And_Resizes_Follow()
    {
        var pts = new[] { new Pt(10, 10), new Pt(50, 30), new Pt(90, 10) };
        var (b, enc) = Strokes.Encode(pts, 5);
        Assert.Equal(new Bounds(5, 4, 90, 32), b);   // at least MinBox tall
        var back = Strokes.Decode(enc, b);
        for (var i = 0; i < pts.Length; i++) { Assert.Equal(pts[i].X, back[i].X, 2); Assert.Equal(pts[i].Y, back[i].Y, 2); }

        var moved = Strokes.Decode(enc, b with { X = 105 });
        Assert.Equal(110, moved[0].X, 2);
        var wide = Strokes.Decode(enc, b with { Width = 180 });
        Assert.Equal(175, wide[2].X, 1);

        // Junk in the field is ignored, never thrown.
        Assert.Single(Strokes.Decode("x,y 0.5,0.5 1e400,0 ,", new Bounds(0, 0, 10, 10)));
    }

    [Fact]
    public void Smooth_Path_Is_Cubic_Bezier()
    {
        Assert.Equal("", Strokes.SmoothPath([]));
        Assert.StartsWith("M1,2 L", Strokes.SmoothPath([new Pt(1, 2)]));
        Assert.Equal("M0,0 L10,0", Strokes.SmoothPath([new Pt(0, 0), new Pt(10, 0)]));
        var p = Strokes.SmoothPath([new Pt(0, 0), new Pt(10, 10), new Pt(20, 0)]);
        Assert.StartsWith("M0,0 C", p);
        Assert.Equal(2, p.Count(c => c == 'C'));
        Assert.EndsWith("20,0", p);
    }

    [Fact]
    public void Width_Follows_The_Format_Bar()
    {
        Assert.Equal(Strokes.DefaultPenWidth, Strokes.WidthOf(new Dictionary<string, string>(), false));
        Assert.Equal(3, Strokes.WidthOf(new Dictionary<string, string> { ["width"] = "3" }, false));
        Assert.Equal(12, Strokes.WidthOf(new Dictionary<string, string> { ["width"] = "3" }, true));
        Assert.Equal(14, Strokes.WidthOf(new Dictionary<string, string>(), true));
    }

    [Fact]
    public void Strokes_Render_Persist_Replicate_And_Undo()
    {
        var m = new ModelRepository("Board");
        var d = m.CreateDiagram(m.Root, DiagramType.Whiteboard, "Ideas");
        var replica = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        m.OpEmitted += replica.ApplyRemote;

        var raw = Enumerable.Range(0, 40).Select(i => new Pt(100 + i * 5, 100 + Math.Sin(i / 5.0) * 30)).ToList();
        var pen = m.AddStroke(d, m.Root, raw, style: new Dictionary<string, string> { ["stroke"] = "#e5484d", ["width"] = "3" });
        var hl = m.AddStroke(d, m.Root, [new Pt(100, 200), new Pt(300, 200)], highlighter: true);
        Assert.True(Strokes.IsStroke(pen));
        Assert.True(Strokes.IsHighlighter(hl.Element));
        Assert.True(pen.Bounds.Width >= 195 && pen.Bounds.Height > 50);
        Assert.True(hl.Bounds.Height > 14);   // a flat line still gets a box as tall as the ink

        var svg = SvgRenderer.Render(m, d);
        Assert.Contains("stroke=\"#e5484d\" stroke-width=\"3\" stroke-linecap=\"round\"", svg);
        Assert.Contains($"stroke=\"{Strokes.HighlighterColor}\" stroke-width=\"14\"", svg);
        Assert.Contains("stroke-opacity=\"0.4\"", svg);
        Assert.Contains(" C", svg);
        // Named for the model explorer, but the name is never drawn on the ink.
        Assert.Equal("Pen stroke", pen.Element.Name);
        Assert.Equal("Highlight", hl.Element.Name);
        Assert.DoesNotContain(">Pen stroke<", svg);
        Assert.DoesNotContain(">Highlight<", svg);

        var rd = replica.Diagrams.Single();
        Assert.Equal(2, rd.Nodes.Count(Strokes.IsStroke));
        Assert.Equal(pen.Element.Get(Strokes.PointsField), rd.Nodes.Single(n => n.Id == pen.Id).Element.Get(Strokes.PointsField));
        Assert.Equal("#e5484d", rd.Nodes.Single(n => n.Id == pen.Id).Style["stroke"]);

        var back = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        Assert.Equal(2, back.Diagrams.Single().Nodes.Count(Strokes.IsStroke));

        m.History.Undo();
        Assert.Single(d.Nodes);
        Assert.Single(rd.Nodes);
    }

    [Fact]
    public void Distance_Measures_From_The_Ink_Not_The_Box()
    {
        var m = new ModelRepository("Board");
        var d = m.CreateDiagram(m.Root, DiagramType.Whiteboard, "Ideas");
        var n = m.AddStroke(d, m.Root, [new Pt(0, 0), new Pt(100, 100)]);
        Assert.True(Strokes.Distance(n, n.Bounds, new Pt(50, 50)) < 1);
        Assert.True(Strokes.Distance(n, n.Bounds, new Pt(90, 5)) > 50);
    }

    [Fact]
    public void Same_Named_Drawing_Shapes_Are_Not_Duplicate_Name_Errors()
    {
        // Strokes, icons and chart decorations are labels on a drawing: two "Pen stroke"s or two "Database" icons are fine.
        var m = new ModelRepository("Board");
        var d = m.CreateDiagram(m.Root, DiagramType.Whiteboard, "Board");
        m.AddStroke(d, m.Root, [new Pt(0, 0), new Pt(50, 50)]);
        m.AddStroke(d, m.Root, [new Pt(100, 0), new Pt(150, 50)]);
        m.Create(m.Root, ElementKind.Shape, "Database");
        m.Create(m.Root, ElementKind.Shape, "Database");
        m.Create(m.Root, ElementKind.Shape, " ");
        m.Create(m.Root, ElementKind.Shape, " ");
        Assert.DoesNotContain(new Meridian.Core.Validation.ModelValidator().Validate(m), i => i.RuleId == "MER-002");
        m.Create(m.Root, ElementKind.Block, "Engine");
        m.Create(m.Root, ElementKind.Block, "Engine");
        Assert.Contains(new Meridian.Core.Validation.ModelValidator().Validate(m), i => i.RuleId == "MER-002");
    }
}
