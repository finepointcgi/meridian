using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>Paths are drawn above shapes by default (so a line passing a block is never hidden); "linesBelow" keeps the old order.</summary>
public class LineOrderTests
{
    private static (ModelRepository M, Diagram D, DiagramNode A, DiagramEdge E) Build()
    {
        var m = new ModelRepository("M");
        var a = m.Create(m.Root, ElementKind.Block, "Engine");
        var b = m.Create(m.Root, ElementKind.Block, "Tank");
        var c = m.Create(m.Root, ElementKind.Block, "Pump");
        var d = m.CreateDiagram(m.Root, DiagramType.BlockDefinition, "Structure");
        var na = m.AddNode(d, a, new Bounds(40, 40, 160, 90));
        m.AddNode(d, b, new Bounds(400, 40, 160, 90));
        m.AddNode(d, c, new Bounds(220, 30, 140, 110));   // sits between the two, across the line
        var e = m.AddEdge(d, m.Connect(ElementKind.Association, a, b))!;
        return (m, d, na, e);
    }

    [Fact]
    public void Lines_Are_Drawn_Above_Shapes_By_Default()
    {
        var (m, d, node, edge) = Build();
        Assert.True(EdgeRouter.LinesOnTop(d));
        var svg = SvgRenderer.Render(m, d);
        Assert.True(svg.IndexOf($"data-edge=\"{edge.Id}\"", StringComparison.Ordinal) > svg.IndexOf($"data-node=\"{node.Id}\"", StringComparison.Ordinal),
            "the path comes after (above) the shapes");
    }

    [Fact]
    public void The_Diagram_Can_Keep_Lines_Below_Shapes()
    {
        var (m, d, node, edge) = Build();
        m.Set(d, EdgeRouter.LinesBelowKey, "true");
        Assert.False(EdgeRouter.LinesOnTop(d));
        var svg = SvgRenderer.Render(m, d);
        Assert.True(svg.IndexOf($"data-edge=\"{edge.Id}\"", StringComparison.Ordinal) < svg.IndexOf($"data-node=\"{node.Id}\"", StringComparison.Ordinal),
            "the path comes before (below) the shapes");
    }
}
