using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;

namespace Meridian.Tests;

public class SysmlDepthTests
{
    [Fact]
    public void Swimlanes_Drive_Allocations()
    {
        var m = SampleModels.Vehicle();
        var act = m.OfKind(ElementKind.Activity).Single();
        var ad = m.Diagrams.Single(d => d.DiagramType == DiagramType.Activity);
        var driver = m.OfKind(ElementKind.Actor).Single();
        var engine = m.OfKind(ElementKind.Block).Single(b => b.Name == "Engine");
        var laneDriver = m.Create(act, ElementKind.Partition, "Driver lane");
        m.Set(laneDriver, FieldKeys.Represents, driver.Id);
        var laneEngine = m.Create(act, ElementKind.Partition, "Engine lane");
        m.Set(laneEngine, FieldKeys.Represents, engine.Id);
        m.AddNode(ad, laneDriver, new Bounds(60, 70, 160, 80));      // contains "Insert Key" (75,90)
        m.AddNode(ad, laneEngine, new Bounds(60, 170, 160, 300));    // contains "Crank Engine" and "Idle"

        var (created, removed) = Allocation.SyncFromSwimlanes(m, ad);
        Assert.Equal((3, 0), (created, removed));
        var insertKey = act.OwnedElements.Single(e => e.Name == "Insert Key");
        Assert.Same(driver, m.Outgoing(insertKey, ElementKind.Allocate).Single().Target);

        // Move "Insert Key" into the engine lane: its allocation follows.
        m.SetBounds(ad, new Dictionary<DiagramNode, Bounds> { [ad.NodeFor(insertKey)!] = ad.NodeFor(insertKey)!.Bounds with { Y = 400 } });
        (created, removed) = Allocation.SyncFromSwimlanes(m, ad);
        Assert.Equal((1, 1), (created, removed));
        Assert.Same(engine, m.Outgoing(insertKey, ElementKind.Allocate).Single().Target);
    }

    [Fact]
    public void Nested_Shapes_Persist_Replicate_And_Cascade()
    {
        var json = ProjectFile.Serialize(SampleModels.Vehicle());
        var a = ProjectFile.Deserialize(json);
        var b = ProjectFile.Deserialize(json);
        a.OpEmitted += op => b.ApplyRemote(op);

        var ibd = a.Diagrams.Single(d => d.DiagramType == DiagramType.InternalBlock);
        var engPart = ibd.Nodes.Single(n => n.Element.Name == "engine");
        var engineBlock = a.ResolveType(engPart.Element)!;
        var fuelIn = engineBlock.OwnedElements.Single(e => e.Name == "fuelIn");
        var portNode = a.AddNode(ibd, fuelIn, Notation.SnapToBorder(new Bounds(55, 80, 16, 16), engPart.Bounds), parent: engPart);
        a.SetNodeStyle(ibd, engPart, "fill", "#ff6b6b");

        Assert.Equal(engPart.Id, portNode.ParentId);
        Assert.Equal(ProjectFile.Serialize(a), ProjectFile.Serialize(b));
        var reloaded = ProjectFile.Deserialize(ProjectFile.Serialize(a));
        var rIbd = (Diagram)reloaded.Find(ibd.Id)!;
        Assert.Equal(engPart.Id, rIbd.FindNode(portNode.Id)!.ParentId);
        Assert.Equal("#ff6b6b", rIbd.FindNode(engPart.Id)!.Style["fill"]);

        // Removing the part from the diagram takes the port shape on its border with it.
        a.RemoveFromDiagram(ibd, [engPart]);
        Assert.Null(ibd.FindNode(portNode.Id));
        Assert.Equal(ProjectFile.Serialize(a), ProjectFile.Serialize(b));
        a.History.Undo();
        Assert.NotNull(ibd.FindNode(portNode.Id));
    }

    [Fact]
    public void Connector_Ends_Resolve_Through_The_Right_Part()
    {
        var m = new ModelRepository();
        var pump = m.Create(m.Root, ElementKind.Block, "Pump");
        var port = m.Create(pump, ElementKind.Port, "out");
        var sys = m.Create(m.Root, ElementKind.Block, "System");
        var p1 = m.Create(sys, ElementKind.Property, "p1"); m.Set(p1, FieldKeys.Type, pump.Id);
        var p2 = m.Create(sys, ElementKind.Property, "p2"); m.Set(p2, FieldKeys.Type, pump.Id);
        var ibd = m.CreateDiagram(sys, DiagramType.InternalBlock);
        var n1 = m.AddNode(ibd, p1, new Bounds(0, 0, 120, 60));
        var n2 = m.AddNode(ibd, p2, new Bounds(300, 0, 120, 60));
        var port1 = m.AddNode(ibd, port, new Bounds(112, 22, 16, 16), parent: n1);
        var port2 = m.AddNode(ibd, port, new Bounds(292, 22, 16, 16), parent: n2);
        Assert.NotSame(port1, port2);
        var c = m.Connect(ElementKind.Connector, port, port);
        m.Set(c, FieldKeys.SourcePart, p1.Id);
        m.Set(c, FieldKeys.TargetPart, p2.Id);
        var edge = m.AddEdge(ibd, c)!;
        Assert.Same(port1, edge.Source);
        Assert.Same(port2, edge.Target);
    }
}
