using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;

namespace Meridian.Tests;

public class AutoDiagrammerTests
{
    public static TheoryData<string> Samples => new() { "Vehicle", "CubeSat", "AdaptiveCruise", "InfusionPump" };

    private static ModelRepository Sample(string name) => name switch
    {
        "Vehicle" => SampleModels.Vehicle(),
        "CubeSat" => CubeSatSample.Create(),
        "AdaptiveCruise" => AdaptiveCruiseSample.Create(),
        _ => InfusionPumpSample.Create(),
    };

    /// <summary>Every shape is legal for the diagram, containers hold their children, siblings never overlap.</summary>
    private static void AssertTidy(ModelRepository m, Diagram d)
    {
        Assert.True(Metamodel.IsValidDiagramContext(d.DiagramType, d.Owner!.Kind), $"{d.Name}: bad owner {d.Owner.Kind}");
        Assert.NotEmpty(d.Nodes);
        Assert.Equal(EdgeRouting.Orthogonal, EdgeRouter.Parse(d.Get(EdgeRouter.DiagramKey)));
        foreach (var n in d.Nodes.Where(n => n.ParentId == null && !Notation.IsBorderShape(n.Element)))
            Assert.Null(AutoDiagrammer.CheckShape(d.DiagramType, d.Owner, n.Element));
        Bounds B(DiagramNode n) => Notation.EffectiveBounds(m, n, d.DiagramType);
        foreach (var group in d.Nodes.Where(n => !Notation.IsBorderShape(n.Element)).GroupBy(n => n.ParentId))
        {
            var list = group.ToList();
            for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count; j++)
                    Assert.False(B(list[i]).Intersects(B(list[j])), $"{d.Name}: {list[i].Element.Name} overlaps {list[j].Element.Name}");
            if (group.Key is { } pid)
            {
                var parent = B(d.FindNode(pid)!);
                foreach (var c in list)
                    Assert.True(c.Bounds.X >= parent.X && c.Bounds.Right <= parent.Right && c.Bounds.Y >= parent.Y && c.Bounds.Bottom <= parent.Bottom,
                        $"{d.Name}: {c.Element.Name} is outside its container");
            }
        }
        // Ports sit on the border of their part.
        foreach (var p in d.Nodes.Where(n => n.ParentId != null && Notation.IsBorderShape(n.Element)))
        {
            var part = d.FindNode(p.ParentId!)!.Bounds;
            var c = p.Bounds.Center;
            Assert.True(Math.Abs(c.X - part.X) < 1 || Math.Abs(c.X - part.Right) < 1 || Math.Abs(c.Y - part.Y) < 1 || Math.Abs(c.Y - part.Bottom) < 1,
                $"{d.Name}: port {p.Element.Name} is not on its part's border");
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Samples_Get_Valid_Tidy_Diagrams_And_A_Second_Run_Plans_Nothing(string sample)
    {
        var m = Sample(sample);
        var before = m.Diagrams.Count();
        var plan = AutoDiagrammer.Plan(m);
        Assert.False(plan.IsEmpty);
        var overview = plan.Diagrams[0];
        Assert.Equal(DiagramType.Package, overview.Type);
        Assert.Equal("Package overview", overview.Name);
        Assert.Same(m.Root, overview.Owner);
        Assert.All(plan.Diagrams, p => Assert.InRange(p.NodeCount, 1, 200));
        Assert.All(plan.Diagrams.Where(p => p.Type != DiagramType.InternalBlock), p => Assert.True(p.Elements.Count <= AutoDiagrammer.DefaultMaxNodes, $"{p.Name}: {p.Elements.Count}"));
        Assert.All(plan.Diagrams, p => Assert.Equal(AutoDiagramStatus.New, p.Status));

        var result = AutoDiagrammer.Apply(m, plan);
        Assert.Equal(plan.Diagrams.Count, result.Created.Count);
        Assert.Equal(before + plan.Diagrams.Count, m.Diagrams.Count());
        foreach (var d in result.Created)
        {
            AssertTidy(m, d);
            Assert.False(string.IsNullOrWhiteSpace(d.Documentation));   // the one-line purpose
            Assert.NotEmpty(d.Get(AutoDiagrammer.MarkerKey));
        }
        Assert.NotNull(result.Overview);
        Assert.Contains("Auto-diagram", result.Summary);

        // Idempotent: nothing new, nothing planned.
        Assert.True(AutoDiagrammer.Plan(m).IsEmpty, string.Join(", ", AutoDiagrammer.Plan(m).Diagrams.Select(p => p.Name)));

        // One undo step removes everything (diagrams and derived dependencies).
        var relationships = m.Relationships.Count();
        m.History.Undo();
        Assert.Equal(before, m.Diagrams.Count());
        Assert.Equal(relationships - result.Dependencies, m.Relationships.Count());
    }

    [Fact]
    public void Package_Overview_Nests_Packages_And_Draws_Derived_Dependencies()
    {
        var m = CubeSatSample.Create();
        var plan = AutoDiagrammer.Plan(m);
        Assert.NotEmpty(plan.Dependencies);
        var result = AutoDiagrammer.Apply(m, plan);
        var d = result.Overview!;
        var packages = m.Root.Descendants().Where(e => e.Kind == ElementKind.Package && e.Name != "Report Templates").ToList();
        Assert.All(packages, p => Assert.True(m.Diagrams.Any(x => x.DiagramType == DiagramType.Package && x.Shows(p)), p.Name));
        // Nested packages sit inside their owner's folder.
        foreach (var n in d.Nodes.Where(n => n.ParentId != null))
            Assert.True(d.FindNode(n.ParentId!)!.Element.IsAncestorOf(n.Element));
        var uses = m.Relationships.Where(r => r.Kind == ElementKind.Dependency && r.Stereotypes.Contains("use")).ToList();
        Assert.Equal(result.Dependencies, uses.Count);
        Assert.All(uses, u => Assert.True(u.Source.Kind == ElementKind.Package && u.Target.Kind == ElementKind.Package));
        Assert.Contains(d.Edges, e => uses.Contains(e.Relationship));

        // It renders: tabbed folders with the names, dependency arrows labelled «use».
        var svg = SvgRenderer.Render(m, d);
        Assert.StartsWith("<svg", svg);
        foreach (var n in d.Nodes) Assert.Contains(System.Net.WebUtility.HtmlEncode(n.Element.Name), svg);
        Assert.Contains("«use»", System.Net.WebUtility.HtmlDecode(svg));
        Assert.Contains(" Q", svg);   // the rounded folder tab
    }

    [Fact]
    public void Ibd_Puts_Ports_On_Parts_And_Draws_Connectors()
    {
        var m = CubeSatSample.Create();
        var sat = m.OfKind(ElementKind.Block).Single(b => b.Name == "CubeSat");
        var plan = new PlannedDiagram
        {
            Type = DiagramType.InternalBlock, Owner = sat, Name = "CubeSat – internal structure",
            Elements = AutoDiagrammer.DefaultContent(m, DiagramType.InternalBlock, sat),
        };
        var d = AutoDiagrammer.Build(m, plan);
        AssertTidy(m, d);
        Assert.Contains(d.Nodes, n => n.Element.Kind == ElementKind.Port && n.ParentId != null);
        Assert.Contains(d.Edges, e => e.Relationship.Kind == ElementKind.Connector);
        Assert.All(d.Edges.Where(e => e.Relationship.Kind == ElementKind.Connector), e =>
            Assert.True(e.Source.Element.Kind == ElementKind.Port || e.Source.Element.Kind == ElementKind.Property));
    }

    [Fact]
    public void Large_Packages_Are_Split_And_Scope_And_Regenerate_Work()
    {
        var m = new ModelRepository("Big");
        var pkg = m.Create(m.Root, ElementKind.Package, "Parts");
        var other = m.Create(m.Root, ElementKind.Package, "Other");
        m.Create(other, ElementKind.Block, "Lonely");
        Element? prev = null;
        for (var i = 0; i < 60; i++)
        {
            var b = m.Create(pkg, ElementKind.Block, $"Block{i:00}");
            if (prev != null && i % 3 != 0) m.Connect(ElementKind.Generalization, b, prev);
            prev = b;
        }
        var plan = AutoDiagrammer.Plan(m, new AutoDiagramOptions { Scope = pkg });
        var bdds = plan.Diagrams.Where(p => p.Type == DiagramType.BlockDefinition).ToList();
        Assert.Equal(3, bdds.Count);
        Assert.All(bdds, p => Assert.True(p.NodeCount <= 25));
        Assert.Equal("Parts – structure (1/3)", bdds[0].Name);
        Assert.All(plan.Diagrams, p => Assert.True(ReferenceEquals(p.Owner, pkg) || pkg.IsAncestorOf(p.Owner)));
        Assert.DoesNotContain(plan.Diagrams, p => p.Elements.Any(e => e.Name == "Lonely"));
        var result = AutoDiagrammer.Apply(m, plan);
        foreach (var d in result.Created) AssertTidy(m, d);

        // A new block joins the existing auto diagram that has room instead of making another one.
        m.Create(pkg, ElementKind.Block, "Late");
        var again = AutoDiagrammer.Plan(m, new AutoDiagramOptions { Scope = pkg });
        var update = Assert.Single(again.Diagrams);
        Assert.Equal(AutoDiagramStatus.Update, update.Status);
        Assert.Contains(update.Elements, e => e.Name == "Late");

        // Regenerate rebuilds Meridian's own diagrams in place.
        var regen = AutoDiagrammer.Plan(m, new AutoDiagramOptions { Scope = pkg, Regenerate = true });
        Assert.All(regen.Diagrams.Where(p => p.Type == DiagramType.BlockDefinition), p => Assert.Equal(AutoDiagramStatus.Update, p.Status));
        var count = m.Diagrams.Count();
        AutoDiagrammer.Apply(m, regen);
        Assert.Equal(count, m.Diagrams.Count());
        Assert.All(m.Diagrams, d => AssertTidy(m, d));
    }

    [Fact]
    public void Behaviors_Without_Diagrams_Get_A_Layered_Flow()
    {
        var m = new ModelRepository("Flow");
        var act = m.Create(m.Root, ElementKind.Activity, "Brew");
        var init = m.Create(act, ElementKind.InitialNode);
        var a = m.Create(act, ElementKind.Action, "Heat water");
        var dec = m.Create(act, ElementKind.DecisionNode);
        var b = m.Create(act, ElementKind.Action, "Pour");
        var fin = m.Create(act, ElementKind.ActivityFinalNode);
        m.Connect(ElementKind.ControlFlow, init, a);
        m.Connect(ElementKind.ControlFlow, a, dec);
        m.Connect(ElementKind.ControlFlow, dec, a);   // a loop
        m.Connect(ElementKind.ControlFlow, dec, b);
        m.Connect(ElementKind.ControlFlow, b, fin);
        var plan = AutoDiagrammer.Plan(m);
        var p = Assert.Single(plan.Diagrams);
        Assert.Equal(DiagramType.Activity, p.Type);
        Assert.Equal("Brew – flow", p.Name);
        var d = AutoDiagrammer.Apply(m, plan).Created.Single();
        AssertTidy(m, d);
        Assert.Equal(5, d.Edges.Count);
        double Y(Element e) => d.NodeFor(e)!.Bounds.Y;
        Assert.True(Y(init) < Y(a) && Y(a) < Y(dec) && Y(dec) < Y(b) && Y(b) < Y(fin));
    }
}
