using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>Timing, interaction overview, communication and UAF viewpoint diagrams; containers fitting their contents.</summary>
public class InteractionDiagramTests
{
    private static (ModelRepository M, Diagram D) FromTemplate(string id)
    {
        var m = new ModelRepository("Diagrams");
        var pkg = m.Create(m.Root, ElementKind.Package, "Views");
        var d = Templates.CreateDiagram(m, pkg, id);
        return (m, d);
    }

    [Fact]
    public void Timing_Diagram_Has_Palette_Notation_And_Renders_State_Timelines()
    {
        Assert.Equal([ElementKind.TimingLifeline, ElementKind.DurationConstraint, ElementKind.TimeConstraint, ElementKind.Comment], Metamodel.PaletteNodes(DiagramType.Timing));
        Assert.True(Metamodel.IsValidDiagramContext(DiagramType.Timing, ElementKind.Interaction));
        Assert.False(Metamodel.IsValidDiagramContext(DiagramType.Timing, ElementKind.Package));
        Assert.Equal(NodeShape.Timeline, Metamodel.Shape(ElementKind.TimingLifeline));
        Assert.Equal("timing", Metamodel.DiagramAbbreviation(DiagramType.Timing));

        var (m, d) = FromTemplate("timing");
        Assert.Equal(DiagramType.Timing, d.DiagramType);
        var door = d.Nodes.Single(n => n.Element.Name == "Door").Element;
        Assert.Equal(["Closed", "Opening", "Open"], InteractionNotation.StatesOf(door));
        Assert.Equal([(0.0, "Closed"), (6.0, "Opening"), (9.0, "Open"), (18.0, "Closed")], InteractionNotation.TimelineOf(door));
        var (t0, t1) = InteractionNotation.TimeRange(d);
        Assert.Equal(0, t0);
        Assert.True(t1 > 18);

        var svg = SvgRenderer.Render(m, d);
        Assert.Contains(">Opening<", svg);        // state labels
        Assert.Contains("{0..3 s}", svg);         // duration constraint
        Assert.Contains("{t = 6 s}", svg);        // time constraint
        Assert.Contains("time (s)", svg);         // time axis
        Assert.Contains("stroke-width=\"2\" stroke-linejoin=\"round\"", svg);   // the state step line
    }

    [Fact]
    public void Interaction_Overview_Flows_Between_Interaction_Uses_And_Inline_Sequences()
    {
        Assert.Contains(ElementKind.InteractionUse, Metamodel.PaletteNodes(DiagramType.InteractionOverview));
        Assert.Contains(ElementKind.InlineInteraction, Metamodel.PaletteNodes(DiagramType.InteractionOverview));
        Assert.Equal([ElementKind.ControlFlow], Metamodel.PaletteEdges(DiagramType.InteractionOverview));
        Assert.Null(Metamodel.CheckEnds(ElementKind.ControlFlow, ElementKind.InteractionUse, ElementKind.InlineInteraction));
        Assert.True(Metamodel.CanOwn(ElementKind.Activity, ElementKind.InteractionUse));

        var (m, d) = FromTemplate("interaction-overview");
        Assert.Equal(6, d.Edges.Count);
        var pay = d.Nodes.Single(n => n.Element.Kind == ElementKind.InlineInteraction).Element;
        Assert.Equal(["Shopper", "Shop", "Bank"], InteractionNotation.InlineParticipants(pay));
        Assert.Equal(4, InteractionNotation.InlineMessages(pay).Count);

        var svg = SvgRenderer.Render(m, d);
        Assert.Contains(">ref<", svg);
        Assert.Contains(">sd Pay<", svg);
        Assert.Contains(">authorize<", svg);
        Assert.Contains("[ok]", svg);
    }

    [Fact]
    public void Communication_Diagram_Shows_Lifeline_Boxes_And_Numbered_Messages()
    {
        Assert.Equal([ElementKind.CommunicationLink], Metamodel.PaletteEdges(DiagramType.Communication));
        Assert.Null(Metamodel.CheckEnds(ElementKind.CommunicationLink, ElementKind.CommunicationLifeline, ElementKind.CommunicationLifeline));
        Assert.NotNull(Metamodel.CheckEnds(ElementKind.CommunicationLink, ElementKind.Block, ElementKind.CommunicationLifeline));
        Assert.True(Metamodel.IsRelationship(ElementKind.CommunicationLink));

        var (m, d) = FromTemplate("communication");
        Assert.Equal(EdgeRouting.Straight, EdgeRouter.StyleOf(d, d.Edges[0]));
        var link = d.Edges.First().Relationship;
        Assert.Equal([("1: placeOrder(cart)", true), ("1.3: confirmation", false)], InteractionNotation.LinkMessages(link));

        var svg = SvgRenderer.Render(m, d);
        Assert.Contains(">:Storefront<", svg);
        Assert.Contains(">1: placeOrder(cart)<", svg);
        Assert.Contains(">1.2: reserved<", svg);
    }

    [Fact]
    public void Every_Uaf_Viewpoint_Is_A_Diagram_Type_With_A_Stereotyped_Palette_And_Template()
    {
        Assert.Equal(9, UafViewpoints.All.Count);
        foreach (var v in UafViewpoints.All)
        {
            var items = Metamodel.PaletteItems(v.Type);
            Assert.All(items.Where(i => i.Kind != ElementKind.Comment), i => Assert.NotNull(i.Stereotype));
            // Every stereotype a palette applies is defined by the UAF profile.
            Assert.All(v.Elements, e => Assert.Contains(Profiles.UafStereotypes, s => s.Name == e.Stereotype));
            Assert.All(v.Links.Where(l => l.Stereotype != null), l => Assert.Contains(Profiles.UafStereotypes, s => s.Name == l.Stereotype));
            Assert.All(Metamodel.PaletteLinks(v.Type), l => Assert.True(Metamodel.IsRelationship(l.Kind)));
            Assert.StartsWith("uaf ", Metamodel.DiagramAbbreviation(v.Type));

            var (m, d) = FromTemplate("uaf-" + v.Code.ToLowerInvariant());
            Assert.Equal(v.Type, d.DiagramType);
            Assert.True(d.Nodes.Count >= 3);
            Assert.True(d.Edges.Count >= 2);
            Assert.Contains(m.OfKind(ElementKind.Profile), p => p.Name == "UAF");
            Assert.All(d.Nodes, n => Assert.NotEmpty(n.Element.Stereotypes));
            var svg = SvgRenderer.Render(m, d);
            Assert.Contains(System.Net.WebUtility.HtmlEncode("«"), svg);
            // Stereotype colour from the profile tints the header.
            var color = Profiles.UafStereotypes.First(s => s.Name == v.Elements[0].Stereotype).Color;
            Assert.Contains(color, svg);
        }
    }

    [Fact]
    public void Missing_Uaf_Stereotypes_Are_Added_To_An_Existing_Profile()
    {
        var m = new ModelRepository("Old UAF");
        var profile = m.Create(m.Root, ElementKind.Profile, "UAF");
        m.Create(profile, ElementKind.Stereotype, "Capability");
        Profiles.EnsureUafStereotype(m, "Project");
        Assert.Contains(profile.OwnedElements, s => s.Name == "Project");
        Assert.Single(m.OfKind(ElementKind.Profile));
    }

    [Fact]
    public void New_Kinds_Round_Trip_Through_Xmi()
    {
        var m = new ModelRepository("Interop");
        var pkg = m.Create(m.Root, ElementKind.Package, "Views");
        foreach (var id in new[] { "timing", "interaction-overview", "communication" }) Templates.CreateDiagram(m, pkg, id);
        var routed = m.Diagrams.First(d => d.DiagramType == DiagramType.InteractionOverview);
        m.Set(routed, EdgeRouter.LineJumpsKey, "true");
        m.SetEdgeStyle(routed, routed.Edges[0], EdgeRouter.EdgeKey, "curved");

        var (back, report) = XmiExchange.Import(XmiExchange.ToXml(m));
        foreach (var kind in new[] { ElementKind.TimingLifeline, ElementKind.DurationConstraint, ElementKind.TimeConstraint, ElementKind.InteractionUse,
                                     ElementKind.InlineInteraction, ElementKind.CommunicationLifeline, ElementKind.CommunicationLink })
            Assert.Equal(m.OfKind(kind).Count(), back.OfKind(kind).Count());

        var door = back.OfKind(ElementKind.TimingLifeline).Single(e => e.Name == "Door");
        Assert.Equal("0 Closed; 6 Opening; 9 Open; 18 Closed", door.Get(InteractionNotation.Timeline));
        Assert.Equal("Closed, Opening, Open", door.Get(InteractionNotation.States));
        Assert.Equal("{0..3 s}", back.OfKind(ElementKind.DurationConstraint).Single().Get(InteractionNotation.Constraint));
        Assert.Contains("Bank -> Shop", back.OfKind(ElementKind.InlineInteraction).Single().Get(InteractionNotation.Sequence));
        Assert.Contains(back.OfKind(ElementKind.CommunicationLink), l => l.Get(InteractionNotation.Messages).Contains("1.1: reserve(items)"));

        foreach (var t in new[] { DiagramType.Timing, DiagramType.InteractionOverview, DiagramType.Communication })
        {
            var orig = m.Diagrams.Single(d => d.DiagramType == t);
            var copy = back.Diagrams.Single(d => d.DiagramType == t);
            Assert.Equal(orig.Nodes.Count, copy.Nodes.Count);
            Assert.Equal(orig.Edges.Count, copy.Edges.Count);
            Assert.Equal(orig.Get(EdgeRouter.DiagramKey), copy.Get(EdgeRouter.DiagramKey));
        }
        var io = back.Diagrams.Single(d => d.DiagramType == DiagramType.InteractionOverview);
        Assert.Equal("true", io.Get(EdgeRouter.LineJumpsKey));
        Assert.Equal(1, io.Edges.Count(e => EdgeRouter.StyleOf(io, e) == EdgeRouting.Curved));
        Assert.Equal("s", back.Diagrams.Single(d => d.DiagramType == DiagramType.Timing).Get(InteractionNotation.TimeUnit));
    }

    [Fact]
    public void Uaf_Elements_Round_Trip_With_Their_Stereotypes()
    {
        var (m, _) = FromTemplate("uaf-pj");
        var (back, _) = XmiExchange.Import(XmiExchange.ToXml(m));
        Assert.Equal(DiagramType.UafProjects, back.Diagrams.Single().DiagramType);
        Assert.Contains(back.AllElements, e => e.Stereotypes.Contains("Project"));
    }

    // ================================================================ containers

    private static (ModelRepository M, Diagram D, DiagramNode Frame, DiagramNode Child) Container()
    {
        var m = new ModelRepository("Containers");
        var d = m.CreateDiagram(m.Root, DiagramType.Package, "Packages");
        var outer = m.Create(m.Root, ElementKind.Package, "Outer");
        var frame = m.AddNode(d, outer, new Bounds(0, 0, 300, 200));
        var inner = m.Create(outer, ElementKind.Package, "Inner");
        var child = m.AddNode(d, inner, new Bounds(40, 60, 120, 80), parent: frame);
        return (m, d, frame, child);
    }

    [Fact]
    public void Containers_Grow_To_Fit_A_Moved_Child()
    {
        var (_, d, frame, child) = Container();
        var moved = child.Bounds.Offset(300, 200);
        var grown = ContainerLayout.GrowToFit(d, [child], n => ReferenceEquals(n, child) ? moved : n.Bounds);
        var fb = grown[frame];
        Assert.True(fb.Right >= moved.Right + ContainerLayout.Padding - 0.01);
        Assert.True(fb.Bottom >= moved.Bottom + ContainerLayout.Padding - 0.01);
        Assert.Equal(0, fb.X);   // only grows, never shrinks
        // Nothing to do when the child is inside.
        Assert.Empty(ContainerLayout.GrowToFit(d, [child], n => n.Bounds));
        // A container being dragged along with its child isn't resized.
        Assert.Empty(ContainerLayout.GrowToFit(d, [child], n => ReferenceEquals(n, child) ? moved : n.Bounds, [frame]));
    }

    [Fact]
    public void Fit_Frame_To_Contents_Wraps_Children_Tightly()
    {
        var (m, d, frame, child) = Container();
        var fit = ContainerLayout.FitToContents(d, n => n.Bounds);
        var (left, top) = ContainerLayout.Header(frame);
        Assert.Equal(new Bounds(child.Bounds.X - left, child.Bounds.Y - top, child.Bounds.Width + left + ContainerLayout.Padding, child.Bounds.Height + top + ContainerLayout.Padding), fit[frame]);
        m.SetBounds(d, fit, "Fit");
        Assert.Empty(ContainerLayout.FitToContents(d, n => n.Bounds));
    }
}
