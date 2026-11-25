using System.Diagnostics;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization;
using Xunit.Abstractions;

namespace Meridian.Tests;

/// <summary>Obstacle-avoiding orthogonal connector routing, routing styles and line jumps.</summary>
public class EdgeRouterTests(ITestOutputHelper output)
{
    private static (ModelRepository M, Diagram D) Bdd(string routing = "orthogonal")
    {
        var m = new ModelRepository("Routing");
        var d = m.CreateDiagram(m.Root, DiagramType.BlockDefinition, "Routes");
        m.Set(d, EdgeRouter.DiagramKey, routing);
        return (m, d);
    }

    private static DiagramNode Block(ModelRepository m, Diagram d, string name, double x, double y, double w = 120, double h = 60) =>
        m.AddNode(d, m.Create(m.Root, ElementKind.Block, name), new Bounds(x, y, w, h), showRelationships: false);

    private static DiagramEdge Link(ModelRepository m, Diagram d, DiagramNode a, DiagramNode b) =>
        m.AddEdge(d, m.Connect(ElementKind.Dependency, a.Element, b.Element))!;

    private static void AssertOrthogonal(IReadOnlyList<Pt> pts)
    {
        Assert.True(pts.Count >= 2);
        for (var i = 1; i < pts.Count; i++)
            Assert.True(Math.Abs(pts[i - 1].X - pts[i].X) < 0.01 || Math.Abs(pts[i - 1].Y - pts[i].Y) < 0.01,
                $"segment {pts[i - 1]} -> {pts[i]} is not axis-aligned");
    }

    /// <summary>Does an axis-aligned segment pass through the interior of a rectangle?</summary>
    private static bool Crosses(Pt a, Pt b, Bounds r)
    {
        if (Math.Abs(a.X - b.X) < 0.01)
            return a.X > r.X && a.X < r.Right && Math.Max(a.Y, b.Y) > r.Y && Math.Min(a.Y, b.Y) < r.Bottom;
        return a.Y > r.Y && a.Y < r.Bottom && Math.Max(a.X, b.X) > r.X && Math.Min(a.X, b.X) < r.Right;
    }

    private static void AssertAvoids(IReadOnlyList<Pt> pts, IEnumerable<Bounds> obstacles)
    {
        foreach (var o in obstacles)
            for (var i = 1; i < pts.Count; i++)
                Assert.False(Crosses(pts[i - 1], pts[i], o), $"segment {pts[i - 1]} -> {pts[i]} crosses obstacle {o}");
    }

    [Fact]
    public void New_Diagrams_Default_To_Routed_Connectors_And_Whiteboards_To_Straight()
    {
        var m = new ModelRepository("Defaults");
        Assert.Equal("orthogonal", m.CreateDiagram(m.Root, DiagramType.BlockDefinition).Get(EdgeRouter.DiagramKey));
        Assert.Equal("orthogonal", m.CreateDiagram(m.Root, DiagramType.Class).Get(EdgeRouter.DiagramKey));
        Assert.Equal("straight", m.CreateDiagram(m.Root, DiagramType.Whiteboard).Get(EdgeRouter.DiagramKey));
        // A diagram without the field (from an older project) keeps the classic look.
        var old = new Diagram(DiagramType.BlockDefinition);
        Assert.Equal(EdgeRouting.Legacy, EdgeRouter.Parse(old.Get(EdgeRouter.DiagramKey)));
    }

    [Fact]
    public void Existing_Diagrams_Without_A_Style_Keep_Their_Classic_Routes()
    {
        var (m, d) = Bdd("");
        var a = Block(m, d, "A", 0, 0); var b = Block(m, d, "B", 400, 300);
        var e = Link(m, d, a, b);
        Assert.Equal(Notation.Orthogonal(a.Bounds, b.Bounds), Notation.RouteIn(d, e));
    }

    [Fact]
    public void Routes_Around_An_Obstacle_Orthogonally()
    {
        var (m, d) = Bdd();
        var a = Block(m, d, "A", 0, 100);
        var wall = Block(m, d, "Wall", 200, 40, 80, 180);
        var b = Block(m, d, "B", 400, 100);
        var e = Link(m, d, a, b);
        var pts = Notation.RouteIn(d, e);
        AssertOrthogonal(pts);
        AssertAvoids(pts, [wall.Bounds]);
        // Leaves A on its right side and enters B on its left.
        Assert.Equal(a.Bounds.Right, pts[0].X, 3);
        Assert.Equal(b.Bounds.X, pts[^1].X, 3);
    }

    [Fact]
    public void Aligned_Shapes_Get_A_Straight_Segment()
    {
        var (m, d) = Bdd();
        var a = Block(m, d, "A", 0, 0); var b = Block(m, d, "B", 0, 300);
        var pts = Notation.RouteIn(d, Link(m, d, a, b));
        Assert.Equal(2, pts.Count);
        Assert.Equal(pts[0].X, pts[1].X, 3);
    }

    [Fact]
    public void Connectors_Sharing_A_Side_Are_Spread_Along_It()
    {
        var (m, d) = Bdd();
        var hub = Block(m, d, "Hub", 200, 0, 200, 60);
        var kids = Enumerable.Range(0, 3).Select(i => Block(m, d, "K" + i, i * 220, 260)).ToList();
        var starts = kids.Select(k => Notation.RouteIn(d, Link(m, d, hub, k))[0]).ToList();
        Assert.All(starts, p => Assert.Equal(hub.Bounds.Bottom, p.Y, 3));
        Assert.Equal(3, starts.Select(p => Math.Round(p.X)).Distinct().Count());
        // Ordered like their targets, so they don't cross near the hub.
        Assert.True(starts[0].X < starts[1].X && starts[1].X < starts[2].X);
    }

    [Fact]
    public void Routing_Is_Deterministic()
    {
        IReadOnlyList<Pt> Build()
        {
            var (m, d) = Bdd();
            var a = Block(m, d, "A", 0, 0);
            Block(m, d, "X", 160, 40, 100, 200);
            Block(m, d, "Y", 300, -60, 80, 120);
            var b = Block(m, d, "B", 460, 160);
            return Notation.RouteIn(d, Link(m, d, a, b));
        }
        Assert.Equal(Build(), Build());
    }

    [Fact]
    public void Only_Affected_Connectors_Are_Rerouted_When_A_Shape_Moves()
    {
        var (m, d) = Bdd();
        var a = Block(m, d, "A", 0, 0); var b = Block(m, d, "B", 0, 300);
        var c = Block(m, d, "C", 2000, 0); var e2 = Block(m, d, "E", 2000, 300);
        Link(m, d, a, b); Link(m, d, c, e2);
        EdgeRouter.RouteAll(d, n => n.Bounds);
        Assert.Equal(2, EdgeRouter.LastRerouteCount(d));
        EdgeRouter.RouteAll(d, n => n.Bounds);
        Assert.Equal(0, EdgeRouter.LastRerouteCount(d));
        // Dragging A (preview bounds) touches only the A-B connector.
        EdgeRouter.RouteAll(d, n => ReferenceEquals(n, a) ? n.Bounds.Offset(40, 0) : n.Bounds);
        Assert.Equal(1, EdgeRouter.LastRerouteCount(d));
    }

    [Fact]
    public void Routes_Three_Hundred_Shapes_Quickly_And_Rerouting_A_Drag_Is_Incremental()
    {
        var (m, d) = Bdd();
        var nodes = new List<DiagramNode>();
        for (var r = 0; r < 15; r++)
            for (var c = 0; c < 20; c++)
                nodes.Add(Block(m, d, $"N{r}_{c}", c * 200, r * 150));
        var rnd = new Random(7);
        var edges = new List<DiagramEdge>();
        for (var i = 0; i < nodes.Count; i++)
        {
            if (i % 20 < 19) edges.Add(Link(m, d, nodes[i], nodes[i + 1]));
            if (i + 20 < nodes.Count && rnd.Next(2) == 0) edges.Add(Link(m, d, nodes[i], nodes[i + 20]));
            if (i + 42 < nodes.Count && rnd.Next(6) == 0) edges.Add(Link(m, d, nodes[i], nodes[i + 42]));   // diagonal: must detour
        }

        var sw = Stopwatch.StartNew();
        var routes = EdgeRouter.RouteAll(d, n => n.Bounds);
        var full = sw.Elapsed.TotalMilliseconds;
        Assert.Equal(edges.Count, routes.Count);
        foreach (var e in edges)
        {
            var pts = routes[e];
            AssertOrthogonal(pts);
            AssertAvoids(pts, nodes.Where(n => !ReferenceEquals(n, e.Source) && !ReferenceEquals(n, e.Target)).Select(n => n.Bounds));
        }
        Assert.True(full < 3000, $"full routing took {full:0} ms");

        // A drag step: one shape moves, a handful of connectors are recomputed.
        var moved = nodes[150];
        var times = new List<double>();
        for (var step = 1; step <= 10; step++)
        {
            var dx = step * 3;
            sw.Restart();
            EdgeRouter.RouteAll(d, n => ReferenceEquals(n, moved) ? n.Bounds.Offset(dx, 0) : n.Bounds);
            times.Add(sw.Elapsed.TotalMilliseconds);
            Assert.True(EdgeRouter.LastRerouteCount(d) < 40, $"{EdgeRouter.LastRerouteCount(d)} connectors rerouted for one moved shape");
        }
        times.Sort();
        output.WriteLine($"full: {full:0.0} ms for {edges.Count} connectors; drag step median {times[times.Count / 2]:0.00} ms, max {times[^1]:0.00} ms");
        Assert.True(times[times.Count / 2] < 50, $"median incremental reroute took {times[times.Count / 2]:0.0} ms (full: {full:0} ms)");
    }

    [Fact]
    public void Straight_And_Curved_Styles_Per_Connector()
    {
        var (m, d) = Bdd();
        var a = Block(m, d, "A", 0, 0); var b = Block(m, d, "B", 300, 300);
        var e = Link(m, d, a, b);
        m.SetEdgeStyle(d, e, EdgeRouter.EdgeKey, "straight");
        var straight = Notation.RouteIn(d, e);
        Assert.Equal(2, straight.Count);
        Assert.NotEqual(straight[0].X, straight[1].X);

        m.SetEdgeStyle(d, e, EdgeRouter.EdgeKey, "curved");
        var curved = Notation.RouteIn(d, e);
        Assert.True(curved.Count > 6);
        Assert.Contains(Enumerable.Range(1, curved.Count - 1), i => Math.Abs(curved[i - 1].X - curved[i].X) > 0.01 && Math.Abs(curved[i - 1].Y - curved[i].Y) > 0.01);

        // "classic" on a connector overrides a routed diagram.
        m.SetEdgeStyle(d, e, EdgeRouter.EdgeKey, "classic");
        Assert.Equal(EdgeRouting.Legacy, EdgeRouter.StyleOf(d, e));
    }

    [Fact]
    public void Manual_Waypoints_Win_Over_Routing()
    {
        var (m, d) = Bdd();
        var a = Block(m, d, "A", 0, 0); var b = Block(m, d, "B", 400, 0);
        var e = Link(m, d, a, b);
        m.SetWaypoints(d, e, [new Pt(200, -100)]);
        var pts = Notation.RouteIn(d, e);
        Assert.Contains(new Pt(200, -100), pts);
    }

    [Fact]
    public void Line_Jumps_Hop_Over_Earlier_Crossing_Connectors()
    {
        var hops = EdgeRouter.Hops([new Pt(0, 50), new Pt(200, 50)], [(new Pt(100, 0), new Pt(100, 100))]);
        Assert.Single(hops);
        Assert.Equal(new Pt(100, 50), hops[0].At);
        // Touching at an end is not a crossing.
        Assert.Empty(EdgeRouter.Hops([new Pt(0, 50), new Pt(100, 50)], [(new Pt(100, 0), new Pt(100, 100))]));

        var (m, d) = Bdd();
        m.Set(d, EdgeRouter.LineJumpsKey, "true");
        var a = Block(m, d, "A", 0, 100); var b = Block(m, d, "B", 400, 100);
        var c = Block(m, d, "C", 200, -100, 60, 40); var e = Block(m, d, "E", 200, 300, 60, 40);
        Link(m, d, c, e);
        Link(m, d, a, b);
        var svg = SvgRenderer.Render(m, d);
        Assert.Contains(" A5,5 0 0 ", svg);
    }

    [Fact]
    public void Svg_Export_Uses_The_Routed_Paths()
    {
        var (m, d) = Bdd();
        var a = Block(m, d, "A", 0, 100);
        Block(m, d, "Wall", 200, 40, 80, 180);
        var b = Block(m, d, "B", 400, 100);
        var e = Link(m, d, a, b);
        var svg = SvgRenderer.Render(m, d);
        Assert.Contains(SvgRenderer.Points(Notation.RouteIn(d, e)), svg);
    }

    [Theory]
    [InlineData("orthogonal")]
    [InlineData("classic")]
    public void While_Dragging_Only_The_Moving_Shapes_Connectors_Are_Routed_Again(string routing)
    {
        var (m, d) = Bdd(routing);
        var a = Block(m, d, "A", 0, 100);
        var b = Block(m, d, "B", 400, 100);
        var c = Block(m, d, "C", 0, 400);
        var e = Block(m, d, "E", 400, 400);
        var ab = Link(m, d, a, b);
        var ce = Link(m, d, c, e);
        var frozen = EdgeRouter.RouteAll(d, n => n.Bounds);

        // A moves right between C and E: its connector follows, C-E keeps its pre-drag path even though A now sits on it.
        Func<DiagramNode, Bounds> dragging = n => ReferenceEquals(n, a) ? new Bounds(200, 380, 120, 60) : n.Bounds;
        var live = EdgeRouter.RouteMoving(d, frozen, new HashSet<DiagramNode> { a }, dragging);
        Assert.Same(frozen[ce], live[ce]);
        Assert.NotEqual(frozen[ab], live[ab]);
        var (start, at) = (live[ab][0], dragging(a));   // leaves A where it is now
        Assert.True(start.X >= at.X - 1 && start.X <= at.Right + 1 && start.Y >= at.Y - 1 && start.Y <= at.Bottom + 1, $"{start} is not on {at}");
        AssertOrthogonal(live[ab]);

        // The diagram's own cache never saw the preview: routing the unchanged diagram again recomputes nothing.
        EdgeRouter.RouteAll(d, n => n.Bounds);
        if (routing == "orthogonal") Assert.Equal(0, EdgeRouter.LastRerouteCount(d));
        // The drop routes everything around A's new place.
        m.SetBounds(d, new Dictionary<DiagramNode, Bounds> { [a] = dragging(a) });
        var dropped = EdgeRouter.RouteAll(d, n => n.Bounds);
        AssertAvoids(dropped[ce], [a.Bounds]);
    }

    [Fact]
    public void Classic_Paths_Are_Reused_Until_A_Shape_Changes_And_Always_Match_A_Fresh_Route()
    {
        var (m, d) = Bdd("classic");
        var a = Block(m, d, "A", 0, 100);
        var wall = Block(m, d, "Wall", 200, 40, 80, 180);
        var b = Block(m, d, "B", 400, 100);
        var c = Block(m, d, "C", 0, 400);
        var ab = Link(m, d, a, b);
        var cb = Link(m, d, c, b);
        void AssertFresh(Dictionary<DiagramEdge, IReadOnlyList<Pt>> routes, Func<DiagramNode, Bounds> bounds)
        {
            foreach (var e in d.Edges) Assert.Equal(Notation.RouteIn(d, e, bounds), routes[e]);
        }

        var first = EdgeRouter.RouteAll(d, n => n.Bounds);
        AssertFresh(first, n => n.Bounds);
        AssertAvoids(first[ab], [wall.Bounds]);
        // Nothing moved: the same paths, not recomputed.
        var again = EdgeRouter.RouteAll(d, n => n.Bounds);
        Assert.Same(first[ab], again[ab]);
        Assert.Same(first[cb], again[cb]);

        // A shape no connector touches moves (a preview, then for real): every path is worked out again around it.
        Func<DiagramNode, Bounds> dragged = n => ReferenceEquals(n, wall) ? n.Bounds.Offset(0, 160) : n.Bounds;
        var moved = EdgeRouter.RouteAll(d, dragged);
        AssertFresh(moved, dragged);
        Assert.NotSame(first[ab], moved[ab]);
        m.SetBounds(d, new Dictionary<DiagramNode, Bounds> { [wall] = wall.Bounds.Offset(0, 160) });
        AssertFresh(EdgeRouter.RouteAll(d, n => n.Bounds), n => n.Bounds);

        // Nesting changes what a path may cross (its own containers aren't obstacles) even when no rectangle moved.
        m.SetBounds(d, new Dictionary<DiagramNode, Bounds> { [wall] = new Bounds(-40, 60, 540, 160) });
        AssertFresh(EdgeRouter.RouteAll(d, n => n.Bounds), n => n.Bounds);
        ModelRepository.SetNodeParentForLoad(a, wall.Id);
        AssertFresh(EdgeRouter.RouteAll(d, n => n.Bounds), n => n.Bounds);
    }
}
