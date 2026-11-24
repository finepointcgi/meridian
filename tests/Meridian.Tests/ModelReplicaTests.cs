using Meridian.Core.Libraries;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>
/// <see cref="ModelRepository.CreateReplica"/>: the in-memory replica a joining tab gets instead of parsing a snapshot. It must be
/// indistinguishable from a parsed copy, and fully independent of the model it was copied from although it shares field data.
/// </summary>
public class ModelReplicaTests
{
    /// <summary>The replica is what <see cref="ProjectFile.Copy"/> gives: same content, index order, libraries and placeholders.</summary>
    private static void AssertLikeParsedCopy(ModelRepository source, ModelRepository replica)
    {
        var parsed = ProjectFile.Copy(source);
        Assert.Equal(ProjectFile.Serialize(parsed, includeLibraries: true), ProjectFile.Serialize(replica, includeLibraries: true));
        Assert.Equal(ProjectFile.Serialize(source), ProjectFile.Serialize(replica));
        Assert.Equal(parsed.Count, replica.Count);
        Assert.Equal(parsed.AllElements.Select(e => e.Id), replica.AllElements.Select(e => e.Id));
        foreach (var kind in Enum.GetValues<ElementKind>())
            Assert.Equal(parsed.OfKind(kind).Select(e => e.Id), replica.OfKind(kind).Select(e => e.Id));
        foreach (var e in parsed.AllElements)
        {
            var r = replica.Find(e.Id)!;
            Assert.Equal(e.Owner?.Id, r.Owner?.Id);
            Assert.Equal(parsed.RelationshipsOf(e).Select(x => x.Id), replica.RelationshipsOf(r).Select(x => x.Id));
            Assert.Equal(parsed.IsLibraryElement(e), replica.IsLibraryElement(r));
            Assert.Equal(parsed.IsLibraryRoot(e), replica.IsLibraryRoot(r));
            Assert.Equal(parsed.LibraryOf(e)?.Info, replica.LibraryOf(r)?.Info);
        }
        Assert.Equal(parsed.Libraries.Select(m => (m.Info, m.Root.Id, m.MountPoint.Id)), replica.Libraries.Select(m => (m.Info, m.Root.Id, m.MountPoint.Id)));
        Assert.Equal(parsed.MissingReferences.Select(e => e.Id).Order(), replica.MissingReferences.Select(e => e.Id).Order());

        // Nothing in the replica points into the source.
        var sourceObjects = new HashSet<Element>(source.AllElements.Concat(source.MissingReferences), ReferenceEqualityComparer.Instance);
        foreach (var e in replica.AllElements)
        {
            Assert.DoesNotContain(e, sourceObjects);
            if (e is Relationship rel)
            {
                Assert.True(replica.Contains(rel.Source) || replica.MissingReferences.Contains(rel.Source));
                Assert.True(replica.Contains(rel.Target) || replica.MissingReferences.Contains(rel.Target));
            }
            if (e is Diagram d)
                foreach (var n in d.Nodes) Assert.DoesNotContain(n.Element, sourceObjects);
        }
        Assert.False(replica.History.CanUndo);
    }

    [Fact]
    public void Replica_Of_A_Large_Model_Is_Like_A_Parsed_Copy()
    {
        var m = LargeModelGenerator.Generate(LargeModelOptions.Cameo(6000));
        AssertLikeParsedCopy(m, m.CreateReplica());
        AssertLikeParsedCopy(m, m.CreateReplica().CreateReplica());   // a replica of a replica too
    }

    [Fact]
    public void Replica_Of_An_Edited_Model_Is_Like_A_Parsed_Copy()
    {
        var m = ProjectFile.Copy(SampleModels.Vehicle());
        var structure = m.Root.OwnedElements.Single(e => e.Name == "Structure");
        var battery = m.Create(structure, ElementKind.Block, "Battery");
        m.Connect(ElementKind.Composition, m.OfKind(ElementKind.Block).Single(e => e.Name == "Vehicle"), battery);
        m.Delete(m.OfKind(ElementKind.Block).Single(e => e.Name == "Engine"));
        m.Move(battery, m.Root.OwnedElements.Single(e => e.Name == "Behavior"), 0);
        AssertLikeParsedCopy(m, m.CreateReplica());
    }

    [Fact]
    public void Replica_With_Used_Projects_And_Missing_References_Is_Like_A_Parsed_Copy()
    {
        var (src, pkg, _, load) = LibraryTests.PartsLibrary();
        var (_, json, _, _) = LibraryTests.UsingProject(LibraryTests.Payload(src, pkg));
        src.Delete(load);   // v2 of the library lost a referenced requirement: the project keeps a placeholder
        var m = ProjectFile.Deserialize(json, [LibraryTests.Payload(src, pkg, "2")]);
        Assert.NotEmpty(m.MissingReferences);
        Assert.Single(m.Libraries);

        var replica = m.CreateReplica();
        AssertLikeParsedCopy(m, replica);
        var wheel = replica.OfKind(ElementKind.Block).Single(b => b.Name == "Wheel");
        Assert.Throws<ReadOnlyElementException>(() => replica.Set(wheel, FieldKeys.Documentation, "changed"));   // still read-only
    }

    [Fact]
    public void Replica_And_Source_Change_Independently_Although_They_Share_Fields()
    {
        var m = ProjectFile.Copy(SampleModels.Vehicle());
        var json = ProjectFile.Serialize(m);
        var replica = m.CreateReplica();
        var second = m.CreateReplica();
        var engine = m.OfKind(ElementKind.Block).Single(e => e.Name == "Engine");
        var engineCopy = replica.Find(engine.Id)!;

        // Fields are copy-on-write in both directions.
        replica.Set(engineCopy, FieldKeys.Documentation, "Electric");
        Assert.Equal("", engine.Documentation);
        m.Rename(engine, "Motor");
        Assert.Equal("Engine", engineCopy.Name);
        Assert.Equal("Electric", engineCopy.Documentation);
        Assert.Equal("Engine", second.Find(engine.Id)!.Name);
        Assert.Equal("", second.Find(engine.Id)!.Documentation);

        // Structure, diagrams and undo are the replica's own.
        var bdd = replica.Diagrams.First(d => d.DiagramType == DiagramType.BlockDefinition);
        var node = bdd.Nodes[0];
        replica.SetBounds(bdd, new Dictionary<DiagramNode, Bounds> { [node] = new Bounds(1, 2, 3, 4) });
        replica.SetNodeStyle(bdd, node, "fill", "#ff0000");
        replica.Create(replica.Root, ElementKind.Package, "Only in the replica");
        replica.Delete(replica.OfKind(ElementKind.Requirement).First());
        replica.History.Undo();
        replica.History.Undo();
        Assert.Equal(json, ProjectFile.Serialize(second));
        var original = m.Find(bdd.Id) as Diagram;
        Assert.NotEqual(new Bounds(1, 2, 3, 4), original!.FindNode(node.Id)!.Bounds);
        Assert.Empty(original.FindNode(node.Id)!.Style);
        Assert.DoesNotContain(m.AllElements, e => e.Name == "Only in the replica");
    }

    [Fact]
    public void Operations_Of_The_Source_Converge_On_The_Replica()
    {
        var m = ProjectFile.Copy(SampleModels.Vehicle());
        var replica = m.CreateReplica();
        m.OpEmitted += replica.ApplyRemote;

        var structure = m.Root.OwnedElements.Single(e => e.Name == "Structure");
        var engine = m.OfKind(ElementKind.Block).Single(e => e.Name == "Engine");
        var bdd = m.Diagrams.Single(d => d.DiagramType == DiagramType.BlockDefinition);
        var battery = m.Create(structure, ElementKind.Block, "Battery");
        m.AddNode(bdd, battery, new Bounds(800, 300, 160, 90));
        m.Set(engine, FieldKeys.Documentation, "Combustion engine");
        m.SetWaypoints(bdd, bdd.Edges.First(), [new Pt(100, 100), new Pt(200, 100)]);
        m.Delete(engine);
        Assert.Equal(ProjectFile.Serialize(m), ProjectFile.Serialize(replica));
        m.History.Undo();
        m.History.Undo();
        Assert.Equal(ProjectFile.Serialize(m), ProjectFile.Serialize(replica));
        Assert.Equal(ProjectFile.Serialize(ProjectFile.Copy(m)), ProjectFile.Serialize(replica.CreateReplica()));
    }

    [Fact]
    public async Task Replicas_Read_While_The_Source_Is_Edited()
    {
        // The session edits its model under its lock while tabs read their replicas on other threads.
        var m = LargeModelGenerator.Generate(LargeModelOptions.Cameo(3000));
        var replicas = Enumerable.Range(0, 4).Select(_ => m.CreateReplica()).ToList();
        var expected = ProjectFile.Serialize(replicas[0]);
        var elements = m.AllElements.Where(e => e.Kind != ElementKind.Model && !m.IsLibraryElement(e)).Take(500).ToList();
        var readers = replicas.Select(r => Task.Run(() =>
        {
            for (var i = 0; i < 5; i++) Assert.Equal(expected, ProjectFile.Serialize(r));
        })).ToList();
        for (var round = 0; round < 20; round++)
            foreach (var e in elements) m.Set(e, FieldKeys.Documentation, $"round {round}");
        await Task.WhenAll(readers);
        Assert.All(replicas, r => Assert.Equal(expected, ProjectFile.Serialize(r)));
    }
}
