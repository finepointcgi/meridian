using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;
using Meridian.Core.Validation;

namespace Meridian.Tests;

/// <summary>General drawing: free shapes and plain lines, usable on a whiteboard or any other diagram.</summary>
public class WhiteboardTests
{
    private static (ModelRepository M, Diagram D, Element A, Element B, Relationship L) Board()
    {
        var m = new ModelRepository("Sketch");
        var d = m.CreateDiagram(m.Root, DiagramType.Whiteboard, "Ideas");
        Element Shape(string sub, string name, double x)
        {
            var e = m.Create(m.Root, ElementKind.Shape, name);
            m.Set(e, FieldKeys.Subtype, sub);
            var (w, h) = Notation.DefaultSizeOf(e);
            m.AddNode(d, e, new Bounds(x, 80, w, h));
            return e;
        }
        var a = Shape("cylinder", "Orders DB", 40);
        var b = Shape("cloud", "Internet", 300);
        var l = m.Connect(ElementKind.Line, a, b);
        m.Set(l, FieldKeys.Subtype, "dashedArrow");
        m.AddEdge(d, l);
        return (m, d, a, b, l);
    }

    [Fact]
    public void Shapes_And_Lines_Use_Subtype_Notation()
    {
        var (_, _, a, b, l) = Board();
        Assert.Equal(NodeShape.Rectangle, Notation.ShapeOf(a));
        Assert.Equal(NodeShape.Ellipse, Notation.ShapeOf(b));
        Assert.Equal(Subtypes.ShapeSize("cylinder"), Notation.DefaultSizeOf(a));
        var style = Notation.StyleOf(l);
        Assert.True(style.Dashed);
        Assert.Equal(ArrowHead.Filled, style.TargetHead);
        Assert.Contains(FieldKeys.Subtype, Metamodel.EditableFields(ElementKind.Shape));
    }

    [Fact]
    public void Whiteboard_Round_Trips_And_Renders()
    {
        var (m, d, _, _, _) = Board();
        var back = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        var d2 = back.Diagrams.Single();
        Assert.Equal(DiagramType.Whiteboard, d2.DiagramType);
        Assert.Equal(2, d2.Nodes.Count);
        Assert.Equal("dashedArrow", d2.Edges.Single().Relationship.Get(FieldKeys.Subtype));

        var svg = SvgRenderer.Render(back, d2);
        Assert.Contains("Orders DB", svg);
        Assert.Contains("stroke-dasharray", svg);
        Assert.DoesNotContain(new ModelValidator().Validate(back), i => i.Element.Kind is ElementKind.Shape or ElementKind.Line);
    }

    [Fact]
    public void General_Shapes_Are_Offered_And_Allowed_Everywhere()
    {
        Assert.Equal(Subtypes.Shapes.Count, Metamodel.GeneralItems.Count);
        Assert.All(Metamodel.PaletteItems(DiagramType.Whiteboard).Where(i => i.Kind != ElementKind.Comment), i => Assert.Equal(ElementKind.Shape, i.Kind));
        // A free shape created from inside an activity lands in the nearest package, not the activity.
        var m = SampleModels.Vehicle();
        var activity = m.OfKind(ElementKind.Activity).First();
        Assert.True(Metamodel.CanOwn(m.DefaultOwnerFor(activity, ElementKind.Shape).Kind, ElementKind.Shape));
        Assert.Null(Metamodel.CheckEnds(ElementKind.Line, ElementKind.Block, ElementKind.Shape));
    }

    [Fact]
    public void Xmi_And_SysML_Exports_Tolerate_General_Shapes()
    {
        var (m, _, _, _, _) = Board();
        var (back, _) = XmiExchange.Import(XmiExchange.ToXml(m));
        Assert.Equal(2, back.OfKind(ElementKind.Shape).Count());
        Assert.NotNull(SysMLv2Text.Export(m));
    }

    [Fact]
    public void Shape_And_Connector_Formatting_Persists_Replicates_And_Undoes()
    {
        var (m, d, a, _, l) = Board();
        var replica = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        m.OpEmitted += op => replica.ApplyRemote(op);

        var edge = d.EdgeFor(l)!;
        m.SetEdgeStyle(d, edge, "stroke", "#ff6b6b");
        m.SetEdgeStyle(d, edge, "width", "3");
        m.SetNodeStyle(d, d.NodeFor(a)!, "fill", "#dae8fc");

        var svg = SvgRenderer.Render(m, d);
        Assert.Contains("stroke=\"#ff6b6b\" stroke-width=\"3\"", svg);
        Assert.Contains("#dae8fc", svg);

        var back = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        Assert.Equal("3", back.Diagrams.Single().Edges.Single().Style["width"]);
        Assert.Equal("#ff6b6b", replica.Diagrams.Single().Edges.Single().Style["stroke"]);

        m.History.Undo();
        m.History.Undo();
        Assert.False(edge.Style.ContainsKey("width"));
        Assert.False(replica.Diagrams.Single().Edges.Single().Style.ContainsKey("width"));
    }
}
