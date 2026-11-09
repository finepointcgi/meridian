using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;

namespace Meridian.Tests;

public class ReplicationTests
{
    private static (ModelRepository A, ModelRepository B) Pair()
    {
        var json = ProjectFile.Serialize(SampleModels.Vehicle());
        var a = ProjectFile.Deserialize(json);
        var b = ProjectFile.Deserialize(json);
        a.OpEmitted += op => b.ApplyRemote(op);
        return (a, b);
    }

    [Fact]
    public void Replica_Converges_Through_Edits_Deletes_Undo_And_Redo()
    {
        var (a, b) = Pair();
        var structure = a.Root.OwnedElements.Single(e => e.Name == "Structure");
        var engine = a.OfKind(ElementKind.Block).Single(e => e.Name == "Engine");
        var bdd = a.Diagrams.Single(d => d.DiagramType == DiagramType.BlockDefinition);

        var battery = a.Create(structure, ElementKind.Block, "Battery");
        a.Create(battery, ElementKind.Property, "voltage");
        a.AddNode(bdd, battery, new Bounds(800, 300, 160, 90));
        a.Connect(ElementKind.Composition, a.OfKind(ElementKind.Block).Single(e => e.Name == "Vehicle"), battery);
        a.Set(engine, FieldKeys.Documentation, "Combustion engine");
        a.SetBounds(bdd, new Dictionary<DiagramNode, Bounds> { [bdd.NodeFor(engine)!] = new Bounds(10, 400, 230, 170) });
        var edge = bdd.Edges.First();
        a.SetWaypoints(bdd, edge, [new Pt(100, 100), new Pt(200, 100)]);
        a.Move(battery, a.Root.OwnedElements.Single(e => e.Name == "Behavior"));
        a.Delete(engine);                // cascades: relationships, views, state machine diagram
        Assert.Equal(ProjectFile.Serialize(a), ProjectFile.Serialize(b));

        a.History.Undo();                // engine and everything comes back
        Assert.Equal(ProjectFile.Serialize(a), ProjectFile.Serialize(b));
        a.History.Undo();
        a.History.Undo();
        a.History.Redo();
        Assert.Equal(ProjectFile.Serialize(a), ProjectFile.Serialize(b));

        var map = a.Duplicate([battery], structure);
        Assert.NotNull(b.Find(map[battery].Id));
        Assert.Equal(ProjectFile.Serialize(a), ProjectFile.Serialize(b));
    }

    [Fact]
    public void Remote_Delete_Makes_Local_Undo_Harmless()
    {
        var (a, b) = Pair();
        b.OpEmitted += op => a.ApplyRemote(op);
        var pkg = a.Root.OwnedElements.First();
        var x = a.Create(pkg, ElementKind.Block, "X");
        var xb = b.Find(x.Id)!;
        b.Set(xb, FieldKeys.Name, "Renamed by B");
        Assert.Equal("Renamed by B", x.Name);
        b.Delete(xb);
        Assert.False(a.Contains(x));
        a.History.Undo();                // undoing the create of an element B already deleted is a no-op
        a.History.Redo();                // re-adding it replicates to B again
        Assert.True(b.Find(x.Id) != null);
        Assert.Equal(ProjectFile.Serialize(a), ProjectFile.Serialize(b));
    }

    [Fact]
    public void Read_Only_Refuses_Edits()
    {
        var m = SampleModels.Vehicle();
        m.History.ReadOnly = true;
        Assert.Throws<InvalidOperationException>(() => m.Create(m.Root, ElementKind.Package));
        Assert.False(m.History.CanUndo);
    }
}
