using Meridian.Core.Language;
using Meridian.Core.Analysis;
using Meridian.Core.CodeGen;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Core.Validation;

namespace Meridian.Tests;

public class RepositoryTests
{
    [Fact]
    public void Create_And_Undo_Redo()
    {
        var m = new ModelRepository();
        var pkg = m.Create(m.Root, ElementKind.Package, "P");
        var b = m.Create(pkg, ElementKind.Block);
        Assert.Equal("Block1", b.Name);
        Assert.True(m.Contains(b));
        m.History.Undo();
        Assert.False(m.Contains(b));
        m.History.Redo();
        Assert.True(m.Contains(b));
        Assert.Same(pkg, b.Owner);
    }

    [Fact]
    public void Ownership_Rules_Are_Enforced()
    {
        var m = new ModelRepository();
        var b = m.Create(m.Root, ElementKind.Block, "B");
        Assert.Throws<InvalidOperationException>(() => m.Create(b, ElementKind.Package));
        Assert.Throws<InvalidOperationException>(() => m.Create(m.Root, ElementKind.Property));
    }

    [Fact]
    public void Relationship_Ends_Are_Checked()
    {
        var m = new ModelRepository();
        var a = m.Create(m.Root, ElementKind.Block, "A");
        var b = m.Create(m.Root, ElementKind.Block, "B");
        Assert.Throws<InvalidOperationException>(() => m.Connect(ElementKind.Satisfy, a, b));
        var r = m.Create(m.Root, ElementKind.Requirement, "R");
        var s = m.Connect(ElementKind.Satisfy, a, r);
        Assert.Contains(s, m.Incoming(r, ElementKind.Satisfy));
    }

    [Fact]
    public void Delete_Cascades_Relationships_And_Views_And_Undoes_Fully()
    {
        var m = SampleModels.Vehicle();
        var engine = m.AllElements.Single(e => e.Name == "Engine" && e.Kind == ElementKind.Block);
        var relCount = m.Relationships.Count();
        var touching = m.RelationshipsOf(engine).Count;
        var nodeCount = m.Diagrams.Sum(d => d.Nodes.Count);
        var edgeCount = m.Diagrams.Sum(d => d.Edges.Count);
        var subtree = engine.Descendants().Count();

        m.Delete(engine);

        Assert.False(m.Contains(engine));
        Assert.True(m.Relationships.Count() < relCount - touching + 1);
        Assert.DoesNotContain(m.Diagrams.SelectMany(d => d.Nodes), n => ReferenceEquals(n.Element, engine));
        Assert.All(m.Diagrams.SelectMany(d => d.Edges), e => Assert.True(m.Contains(e.Relationship)));

        m.History.Undo();
        Assert.True(m.Contains(engine));
        Assert.Equal(relCount, m.Relationships.Count());
        Assert.Equal(nodeCount, m.Diagrams.Sum(d => d.Nodes.Count));
        Assert.Equal(edgeCount, m.Diagrams.Sum(d => d.Edges.Count));
        Assert.Equal(subtree, engine.Descendants().Count());

        m.History.Redo();
        Assert.False(m.Contains(engine));
    }

    [Fact]
    public void Transactions_Group_Into_One_Undo()
    {
        var m = new ModelRepository();
        using (m.History.Transaction("batch"))
        {
            m.Create(m.Root, ElementKind.Block, "A");
            m.Create(m.Root, ElementKind.Block, "B");
        }
        Assert.Equal(2, m.Root.OwnedElements.Count);
        m.History.Undo();
        Assert.Empty(m.Root.OwnedElements);
    }

    [Fact]
    public void Dirty_Tracking_Follows_Save_Point()
    {
        var m = new ModelRepository();
        Assert.False(m.History.IsDirty);
        m.Create(m.Root, ElementKind.Block, "A");
        Assert.True(m.History.IsDirty);
        m.History.MarkSaved();
        Assert.False(m.History.IsDirty);
        m.History.Undo();
        Assert.True(m.History.IsDirty);
        m.History.Redo();
        Assert.False(m.History.IsDirty);
    }

    [Fact]
    public void Move_Prevents_Cycles()
    {
        var m = new ModelRepository();
        var p1 = m.Create(m.Root, ElementKind.Package, "P1");
        var p2 = m.Create(p1, ElementKind.Package, "P2");
        Assert.Throws<InvalidOperationException>(() => m.Move(p1, p2));
        var b = m.Create(p1, ElementKind.Block, "B");
        m.Move(b, p2);
        Assert.Same(p2, b.Owner);
        m.History.Undo();
        Assert.Same(p1, b.Owner);
    }

    [Fact]
    public void Adding_Node_Shows_Existing_Relationships()
    {
        var m = new ModelRepository();
        var a = m.Create(m.Root, ElementKind.Block, "A");
        var b = m.Create(m.Root, ElementKind.Block, "B");
        m.Connect(ElementKind.Generalization, a, b);
        var d = m.CreateDiagram(m.Root, DiagramType.BlockDefinition);
        m.AddNode(d, a, new Bounds(0, 0, 100, 100));
        Assert.Empty(d.Edges);
        m.AddNode(d, b, new Bounds(200, 0, 100, 100));
        Assert.Single(d.Edges);
    }

    [Fact]
    public void Validation_Finds_Problems_And_Sample_Is_Clean_Of_Errors()
    {
        var sample = SampleModels.Vehicle();
        var issues = new ModelValidator().Validate(sample);
        Assert.DoesNotContain(issues, i => i.Severity == Severity.Error);

        var m = new ModelRepository();
        var a = m.Create(m.Root, ElementKind.Block, "A");
        var b = m.Create(m.Root, ElementKind.Block, "B");
        m.Connect(ElementKind.Generalization, a, b);
        m.Connect(ElementKind.Generalization, b, a);
        var dup = m.Create(m.Root, ElementKind.Block, "A");
        var p = m.Create(a, ElementKind.Property, "p");
        m.Set(p, FieldKeys.Multiplicity, "1..x");
        var r1 = m.Create(m.Root, ElementKind.Requirement, "R1");
        var r2 = m.Create(m.Root, ElementKind.Requirement, "R2");
        m.Set(r2, FieldKeys.RequirementId, r1.Get(FieldKeys.RequirementId));

        var found = new ModelValidator().Validate(m).Select(i => i.RuleId).ToHashSet();
        Assert.Contains("MER-002", found);
        Assert.Contains("MER-004", found);
        Assert.Contains("MER-006", found);
        Assert.Contains("REQ-002", found);
        Assert.Contains("REQ-003", found);
        _ = dup;
    }

    [Fact]
    public void Traceability_Matrix_And_Coverage()
    {
        var m = SampleModels.Vehicle();
        var matrix = Traceability.BuildMatrix(m, [ElementKind.Requirement], [ElementKind.Block], [ElementKind.Satisfy]);
        var accel = matrix.Rows.Single(r => r.Name == "Acceleration");
        var vehicle = matrix.Columns.Single(c => c.Name == "Vehicle");
        Assert.False(matrix.Cell(accel, vehicle).IsEmpty);

        var cov = Traceability.Coverage(m);
        Assert.Equal(4, cov.Total);
        Assert.Equal(4, cov.Satisfied);
        Assert.Equal(2, cov.Verified);

        var engine = m.AllElements.Single(e => e.Name == "Engine" && e.Kind == ElementKind.Block);
        var impact = Traceability.Impact(m, m.AllElements.Single(e => e.Name == "FuelInterface"));
        Assert.Contains(impact, i => i.Element.Name == "fuelIn");
        _ = engine;
    }

    [Fact]
    public void CSharp_Generation_Produces_Classes()
    {
        var m = SampleModels.Vehicle();
        var files = CSharpGenerator.Generate(m);
        var vehicle = files.Single(f => f.Key.EndsWith("Vehicle.cs")).Value;
        Assert.Contains("public class Vehicle", vehicle);
        Assert.Contains("public Engine Engine { get; set; }", vehicle);
        Assert.Contains("public double Mass { get; set; } = 1450;", vehicle);
        var engine = files.Single(f => f.Key.EndsWith("Engine.cs")).Value;
        Assert.Contains("public bool Start() => throw new NotImplementedException();", engine);
    }
}

public class RoutingAndSequenceTests
{
    [Fact]
    public void Orthogonal_Routes_Use_Only_Horizontal_And_Vertical_Segments()
    {
        var pts = Notation.Orthogonal(new Bounds(0, 0, 100, 60), new Bounds(300, 200, 100, 60));
        for (var i = 1; i < pts.Count; i++)
            Assert.True(pts[i - 1].X == pts[i].X || pts[i - 1].Y == pts[i].Y, $"segment {i} is diagonal");
        // Overlapping horizontally -> one straight vertical segment
        var straight = Notation.Orthogonal(new Bounds(0, 0, 100, 60), new Bounds(20, 200, 100, 60));
        Assert.Equal(2, straight.Count);
        Assert.Equal(straight[0].X, straight[1].X);
    }

    [Fact]
    public void Sample_Sequence_Diagram_Has_Ordered_Horizontal_Messages()
    {
        var m = SampleModels.Vehicle();
        var sd = m.Diagrams.Single(d => d.DiagramType == DiagramType.Sequence);
        Assert.Equal(3, sd.Nodes.Count(n => n.Element.Kind == ElementKind.Lifeline));
        Assert.Single(sd.Nodes, n => n.Element.Kind == ElementKind.CombinedFragment);
        Assert.Equal(5, sd.Edges.Count);
        foreach (var e in sd.Edges.Where(e => !ReferenceEquals(e.Source, e.Target)))
        {
            var r = Notation.Route(e);
            Assert.Equal(r[0].Y, r[^1].Y);
        }
        // Messages in model (creation) order must run top to bottom.
        var messages = sd.Owner!.OwnedElements.OfType<Relationship>().Where(r => r.Kind == ElementKind.Message).ToList();
        var ys = messages.Select(msg => sd.EdgeFor(msg)!.Waypoints[0].Y).ToList();
        Assert.Equal(ys.OrderBy(y => y), ys);
        Assert.Throws<InvalidOperationException>(() => m.Connect(ElementKind.Message, sd.Nodes[0].Element, m.OfKind(ElementKind.Block).First()));
    }
}

public class InteractionAndEditingTests
{
    [Fact]
    public void Activations_Span_From_Call_To_Reply()
    {
        var m = SampleModels.Vehicle();
        var sd = m.Diagrams.Single(d => d.DiagramType == DiagramType.Sequence);
        var acts = Notation.Activations(sd);
        var engine = acts.Single(a => a.Lifeline.Element.Name == "engine");
        Assert.Equal(148, engine.From);
        Assert.Equal(260, engine.To);   // "start" call at 148 until "started" reply at 260
    }

    [Fact]
    public void SysMLv2_Round_Trips_Interactions()
    {
        var m = SampleModels.Vehicle();
        var text = Meridian.Serialization.SysMLv2Text.Export(m);
        Assert.Contains("occurrence def 'Start Engine Sequence'", text);
        Assert.Contains("#reply message started from engine to vehicle;", text);
        var (back, report) = Meridian.Serialization.SysMLv2Text.Import(text);
        Assert.True(report.Warnings.Count == 0, string.Join("\n", report.Warnings));
        var seq = back.OfKind(ElementKind.Interaction).Single();
        Assert.Equal(3, seq.OwnedElements.Count(e => e.Kind == ElementKind.Lifeline));
        var msgs = seq.OwnedElements.OfType<Relationship>().Where(r => r.Kind == ElementKind.Message).ToList();
        Assert.Equal(5, msgs.Count);
        Assert.Equal("reply", msgs.Single(x => x.Name == "started").Get(FieldKeys.MessageSort));
        Assert.Equal("Engine", back.TypeDisplayName(seq.OwnedElements.Single(e => e.Name == "engine")));
    }

    [Fact]
    public void Duplicate_Deep_Copies_And_Remaps_Internal_Links()
    {
        var m = SampleModels.Vehicle();
        var engine = m.OfKind(ElementKind.Block).Single(b => b.Name == "Engine");
        var tank = m.OfKind(ElementKind.Block).Single(b => b.Name == "FuelTank");
        var before = m.AllElements.Count();
        var map = m.Duplicate([engine, tank], engine.Owner!);
        var engineCopy = map[engine];
        Assert.Equal("Engine copy", engineCopy.Name);
        Assert.Equal(engine.OwnedElements.Count(e => e is not Relationship && e is not Diagram), engineCopy.OwnedElements.Count(e => e is not Relationship && e is not Diagram));
        // State machine inside the engine came along with its transitions.
        var smCopy = engineCopy.OwnedElements.Single(e => e.Kind == ElementKind.StateMachine);
        Assert.Equal(5, smCopy.OwnedElements.Count(e => e.Kind == ElementKind.Transition));
        // A relationship between two original elements is not copied unless both ends are copied.
        Assert.Empty(m.Incoming(engineCopy, ElementKind.Composition));
        m.History.Undo();
        Assert.Equal(before, m.AllElements.Count());
    }
}
