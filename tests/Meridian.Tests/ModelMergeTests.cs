using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>Three-way model merge: combining independent edits, detecting conflicts and resolving them.</summary>
public class ModelMergeTests
{
    // A small base: Root ▸ Structure (Engine, Wheel, Chassis) ▸ Archive; Engine → Wheel dependency; a BDD showing Engine and Wheel.
    private static ModelRepository Base()
    {
        var m = new ModelRepository("Car");
        var structure = m.Create(m.Root, ElementKind.Package, "Structure");
        var archive = m.Create(m.Root, ElementKind.Package, "Archive");
        var engine = m.Create(structure, ElementKind.Block, "Engine");
        var wheel = m.Create(structure, ElementKind.Block, "Wheel");
        m.Create(structure, ElementKind.Block, "Chassis");
        m.Set(engine, FieldKeys.Documentation, "Combustion engine");
        m.Create(archive, ElementKind.Block, "OldPart");
        var dep = m.Connect(ElementKind.Dependency, engine, wheel);
        var d = m.CreateDiagram(structure, DiagramType.BlockDefinition, "Car BDD");
        m.AddNode(d, engine, new Bounds(10, 10, 120, 60));
        m.AddNode(d, wheel, new Bounds(200, 10, 120, 60));
        Assert.NotNull(d.EdgeFor(dep));
        return m;
    }

    private static ModelRepository Clone(ModelRepository m) => ProjectFile.Deserialize(ProjectFile.Serialize(m));

    private static (ModelRepository B, ModelRepository O, ModelRepository T) Three()
    {
        var b = Base();
        return (b, Clone(b), Clone(b));
    }

    private static Element Named(ModelRepository m, string name) => m.AllElements.Single(e => e.Name == name);
    private static Diagram Bdd(ModelRepository m) => m.Diagrams.Single(d => d.Name == "Car BDD");
    private static DiagramNode NodeOf(ModelRepository m, string name) => Bdd(m).NodeFor(Named(m, name))!;

    [Fact]
    public void Identical_Sides_Merge_To_The_Same_Model_Without_Conflicts()
    {
        var (b, o, t) = Three();
        var r = ModelMerge.Merge(b, o, t);
        Assert.False(r.HasConflicts);
        Assert.Empty(ModelDiff.Compare(b, r.Model));
        Assert.Equal(ProjectFile.Serialize(b), ProjectFile.Serialize(r.Model));
    }

    [Fact]
    public void Independent_Changes_From_Both_Sides_Are_Combined()
    {
        var (b, o, t) = Three();
        o.Rename(Named(o, "Engine"), "Motor");
        o.Create(Named(o, "Structure"), ElementKind.Block, "Battery");
        t.Set(Named(t, "Wheel"), FieldKeys.Documentation, "Alloy");
        t.Create(Named(t, "Structure"), ElementKind.Block, "Seat");
        t.Delete(Named(t, "OldPart"));
        // Same change on both sides is not a conflict.
        o.Set(Named(o, "Chassis"), FieldKeys.IsAbstract, "true");
        t.Set(Named(t, "Chassis"), FieldKeys.IsAbstract, "true");

        var r = ModelMerge.Merge(b, o, t);
        Assert.False(r.HasConflicts);
        var m = r.Model;
        Assert.Equal("Combustion engine", Named(m, "Motor").Documentation);
        Assert.Equal("Alloy", Named(m, "Wheel").Get(FieldKeys.Documentation));
        Assert.True(Named(m, "Chassis").IsAbstract);
        Assert.DoesNotContain(m.AllElements, e => e.Name == "OldPart");
        var structure = Named(m, "Structure");
        Assert.Contains(structure.OwnedElements, e => e.Name == "Battery");
        Assert.Contains(structure.OwnedElements, e => e.Name == "Seat");
        // Theirs' addition lands after its predecessor, ours' after its own.
        var names = structure.OwnedElements.Select(e => e.Name).ToList();
        Assert.True(names.IndexOf("Battery") > names.IndexOf("Chassis"));
        Assert.True(names.IndexOf("Seat") > names.IndexOf("Chassis"));
        // Result is a valid, serializable model.
        Assert.Equal(m.AllElements.Count(), ProjectFile.Deserialize(ProjectFile.Serialize(m)).AllElements.Count());
    }

    [Fact]
    public void Same_Field_Changed_Differently_Is_A_Field_Conflict_With_Ours_Theirs_And_Custom()
    {
        var (b, o, t) = Three();
        o.Set(Named(o, "Engine"), FieldKeys.Documentation, "Electric motor");
        t.Set(Named(t, "Engine"), FieldKeys.Documentation, "Hybrid engine");
        o.Rename(Named(o, "Wheel"), "Rim");   // non-conflicting, still merged

        var r = ModelMerge.Merge(b, o, t);
        var c = Assert.Single(r.Conflicts);
        Assert.Equal(MergeConflictKind.Field, c.Kind);
        Assert.Equal(FieldKeys.Documentation, c.Subject);
        Assert.Equal("Combustion engine", c.Base);
        Assert.Equal("Electric motor", c.Ours);
        Assert.Equal("Hybrid engine", c.Theirs);
        Assert.True(c.AllowsCustom);
        Assert.Equal(MergeChoice.Ours, c.Choice);
        Assert.Equal("Electric motor", Named(r.Model, "Engine").Documentation);
        Assert.NotNull(r.Model.AllElements.SingleOrDefault(e => e.Name == "Rim"));

        c.Choice = MergeChoice.Theirs;
        Assert.Equal("Hybrid engine", Named(r.Resolve(), "Engine").Documentation);

        c.Choice = MergeChoice.Custom;
        c.CustomValue = "Engine (electric or hybrid)";
        var custom = r.Resolve();
        Assert.Equal("Engine (electric or hybrid)", Named(custom, "Engine").Documentation);
        Assert.NotNull(custom.AllElements.SingleOrDefault(e => e.Name == "Rim"));
    }

    [Fact]
    public void Field_Added_On_One_Side_And_Removed_On_The_Other_Conflicts_Only_When_Both_Changed()
    {
        var (b, o, t) = Three();
        o.Set(Named(o, "Engine"), FieldKeys.Documentation, null);       // removed
        t.Set(Named(t, "Wheel"), FieldKeys.Multiplicity, "4");          // unrelated
        var r = ModelMerge.Merge(b, o, t);
        Assert.False(r.HasConflicts);
        Assert.Equal("", Named(r.Model, "Engine").Documentation);

        t.Set(Named(t, "Engine"), FieldKeys.Documentation, "Diesel");
        r = ModelMerge.Merge(b, o, t);
        var c = Assert.Single(r.Conflicts);
        Assert.Null(c.Ours);
        Assert.Equal("Diesel", c.Theirs);
    }

    [Fact]
    public void Delete_Versus_Modify_Is_A_Conflict_And_Either_Side_Can_Win()
    {
        var (b, o, t) = Three();
        o.Delete(Named(o, "Chassis"));
        t.Set(Named(t, "Chassis"), FieldKeys.Documentation, "Steel frame");

        var r = ModelMerge.Merge(b, o, t);
        var c = Assert.Single(r.Conflicts);
        Assert.Equal(MergeConflictKind.DeleteModify, c.Kind);
        Assert.Equal("deleted", c.Ours);
        Assert.StartsWith("kept", c.Theirs);
        Assert.DoesNotContain(r.Model.AllElements, e => e.Name == "Chassis");   // default: ours

        c.Choice = MergeChoice.Theirs;
        var kept = r.Resolve();
        Assert.Equal("Steel frame", Named(kept, "Chassis").Get(FieldKeys.Documentation));
        Assert.Equal("Structure", Named(kept, "Chassis").Owner!.Name);
    }

    [Fact]
    public void Theirs_Delete_Versus_Our_Edit_Keeps_Ours_By_Default()
    {
        var (b, o, t) = Three();
        o.Rename(Named(o, "Chassis"), "Frame");
        t.Delete(Named(t, "Chassis"));
        var r = ModelMerge.Merge(b, o, t);
        var c = Assert.Single(r.Conflicts);
        Assert.Equal("deleted", c.Theirs);
        Assert.NotNull(r.Model.AllElements.SingleOrDefault(e => e.Name == "Frame"));
        c.Choice = MergeChoice.Theirs;
        Assert.DoesNotContain(r.Resolve().AllElements, e => e.Name is "Frame" or "Chassis");
    }

    [Fact]
    public void Delete_Of_An_Untouched_Element_Merges_Silently()
    {
        var (b, o, t) = Three();
        o.Delete(Named(o, "Chassis"));
        t.Rename(Named(t, "Wheel"), "Tyre");
        var r = ModelMerge.Merge(b, o, t);
        Assert.False(r.HasConflicts);
        Assert.DoesNotContain(r.Model.AllElements, e => e.Name == "Chassis");
        Assert.Contains(r.Model.AllElements, e => e.Name == "Tyre");
    }

    [Fact]
    public void Deleting_A_Package_That_The_Other_Side_Extended_Raises_One_Conflict_On_The_Package()
    {
        var (b, o, t) = Three();
        o.Delete(Named(o, "Archive"));
        t.Create(Named(t, "Archive"), ElementKind.Block, "LegacyPump");
        t.Set(Named(t, "OldPart"), FieldKeys.Documentation, "still used");

        var r = ModelMerge.Merge(b, o, t);
        var c = Assert.Single(r.Conflicts);
        Assert.Equal(MergeConflictKind.DeleteModify, c.Kind);
        Assert.Equal("Archive", c.ElementName);
        Assert.DoesNotContain(r.Model.AllElements, e => e.Name is "Archive" or "LegacyPump" or "OldPart");

        c.Choice = MergeChoice.Theirs;
        var kept = r.Resolve();
        var archive = Named(kept, "Archive");
        Assert.Equal(["OldPart", "LegacyPump"], archive.OwnedElements.Select(e => e.Name).ToArray());
        Assert.Equal("still used", Named(kept, "OldPart").Documentation);
    }

    [Fact]
    public void A_New_Relationship_To_An_Element_Deleted_On_The_Other_Side_Conflicts()
    {
        var (b, o, t) = Three();
        o.Delete(Named(o, "Chassis"));
        t.Connect(ElementKind.Dependency, Named(t, "Wheel"), Named(t, "Chassis"));

        var r = ModelMerge.Merge(b, o, t);
        var c = Assert.Single(r.Conflicts);
        Assert.Equal(MergeConflictKind.DeleteModify, c.Kind);
        Assert.Contains("referencing", c.Theirs);
        // Ours wins: the chassis is gone and so is the dangling dependency.
        Assert.Single(r.Model.Relationships);
        c.Choice = MergeChoice.Theirs;
        Assert.Equal(2, r.Resolve().Relationships.Count());
    }

    [Fact]
    public void Relationships_Whose_End_Was_Deleted_Are_Dropped()
    {
        var (b, o, t) = Three();
        t.Delete(Named(t, "Wheel"));   // cascades the Engine → Wheel dependency and its shape on theirs
        var r = ModelMerge.Merge(b, o, t);
        Assert.False(r.HasConflicts);
        Assert.Empty(r.Model.Relationships);
        Assert.Single(Bdd(r.Model).Nodes);
        Assert.Empty(Bdd(r.Model).Edges);
    }

    [Fact]
    public void Move_On_One_Side_Is_Applied()
    {
        var (b, o, t) = Three();
        t.Move(Named(t, "Chassis"), Named(t, "Archive"));
        o.Rename(Named(o, "Engine"), "Motor");
        var r = ModelMerge.Merge(b, o, t);
        Assert.False(r.HasConflicts);
        Assert.Equal("Archive", Named(r.Model, "Chassis").Owner!.Name);
    }

    [Fact]
    public void Moves_To_Different_Owners_Conflict()
    {
        var (b, o, t) = Three();
        o.Move(Named(o, "Chassis"), Named(o, "Archive"));
        t.Move(Named(t, "Chassis"), t.Root);
        var r = ModelMerge.Merge(b, o, t);
        var c = Assert.Single(r.Conflicts);
        Assert.Equal(MergeConflictKind.Move, c.Kind);
        Assert.Equal("Archive", Named(r.Model, "Chassis").Owner!.Name);
        c.Choice = MergeChoice.Theirs;
        var resolved = r.Resolve();
        Assert.Same(resolved.Root, Named(resolved, "Chassis").Owner);
    }

    [Fact]
    public void A_Moved_Element_Edited_Elsewhere_Is_A_Conflict_That_Keeps_Both_Edits()
    {
        var (b, o, t) = Three();
        o.Move(Named(o, "Chassis"), Named(o, "Archive"));
        t.Set(Named(t, "Chassis"), FieldKeys.Documentation, "Aluminium");
        var r = ModelMerge.Merge(b, o, t);
        var c = Assert.Single(r.Conflicts);
        Assert.Equal(MergeConflictKind.MoveEdit, c.Kind);
        var merged = Named(r.Model, "Chassis");
        Assert.Equal("Archive", merged.Owner!.Name);
        Assert.Equal("Aluminium", merged.Documentation);
        c.Choice = MergeChoice.Theirs;
        var other = Named(r.Resolve(), "Chassis");
        Assert.Equal("Structure", other.Owner!.Name);
        Assert.Equal("Aluminium", other.Documentation);
    }

    [Fact]
    public void Crossing_Moves_Never_Produce_A_Containment_Cycle()
    {
        var (b, o, t) = Three();
        o.Move(Named(o, "Archive"), Named(o, "Structure"));
        t.Move(Named(t, "Structure"), Named(t, "Archive"));
        var r = ModelMerge.Merge(b, o, t);
        Assert.False(r.HasConflicts);
        foreach (var e in r.Model.AllElements) Assert.Same(r.Model.Root, e.AncestorsAndSelf().Last());
        Assert.Equal(b.AllElements.Count(), r.Model.AllElements.Count());
    }

    [Fact]
    public void Element_Moved_Into_A_Package_Deleted_On_The_Other_Side_Conflicts_And_Falls_Back()
    {
        var (b, o, t) = Three();
        o.Delete(Named(o, "Archive"));
        t.Move(Named(t, "Chassis"), Named(t, "Archive"));
        var r = ModelMerge.Merge(b, o, t);
        var c = Assert.Single(r.Conflicts);
        Assert.Equal("Archive", c.ElementName);
        // Ours: the archive is gone; the chassis returns to where it was instead of disappearing.
        Assert.Equal("Structure", Named(r.Model, "Chassis").Owner!.Name);
    }

    [Fact]
    public void Diagram_Shapes_Merge_Per_Property_And_Conflict_When_Both_Move_A_Shape()
    {
        var (b, o, t) = Three();
        o.SetBounds(Bdd(o), new Dictionary<DiagramNode, Bounds> { [NodeOf(o, "Engine")] = new Bounds(50, 50, 120, 60) });
        t.SetNodeStyle(Bdd(t), NodeOf(t, "Engine"), "fill", "#ff0000");
        t.SetBounds(Bdd(t), new Dictionary<DiagramNode, Bounds> { [NodeOf(t, "Wheel")] = new Bounds(300, 300, 120, 60) });
        var r = ModelMerge.Merge(b, o, t);
        Assert.False(r.HasConflicts);
        Assert.Equal(new Bounds(50, 50, 120, 60), NodeOf(r.Model, "Engine").Bounds);
        Assert.Equal("#ff0000", NodeOf(r.Model, "Engine").Style["fill"]);
        Assert.Equal(new Bounds(300, 300, 120, 60), NodeOf(r.Model, "Wheel").Bounds);

        t.SetBounds(Bdd(t), new Dictionary<DiagramNode, Bounds> { [NodeOf(t, "Engine")] = new Bounds(400, 0, 120, 60) });
        r = ModelMerge.Merge(b, o, t);
        var c = Assert.Single(r.Conflicts);
        Assert.Equal(MergeConflictKind.DiagramNode, c.Kind);
        Assert.Equal(new Bounds(50, 50, 120, 60), NodeOf(r.Model, "Engine").Bounds);
        Assert.Equal("#ff0000", NodeOf(r.Model, "Engine").Style["fill"]);   // non-conflicting style still merged
        c.Choice = MergeChoice.Theirs;
        Assert.Equal(new Bounds(400, 0, 120, 60), NodeOf(r.Resolve(), "Engine").Bounds);
    }

    [Fact]
    public void A_Shape_Removed_On_One_Side_And_Moved_On_The_Other_Conflicts()
    {
        var (b, o, t) = Three();
        o.RemoveFromDiagram(Bdd(o), [NodeOf(o, "Wheel")]);
        t.SetBounds(Bdd(t), new Dictionary<DiagramNode, Bounds> { [NodeOf(t, "Wheel")] = new Bounds(0, 200, 120, 60) });
        var r = ModelMerge.Merge(b, o, t);
        var c = Assert.Single(r.Conflicts);
        Assert.Equal(MergeConflictKind.DiagramNode, c.Kind);
        Assert.Equal("removed", c.Ours);
        Assert.Null(Bdd(r.Model).NodeFor(Named(r.Model, "Wheel")));
        Assert.Empty(Bdd(r.Model).Edges);   // the path loses its end
        c.Choice = MergeChoice.Theirs;
        Assert.Equal(new Bounds(0, 200, 120, 60), NodeOf(r.Resolve(), "Wheel").Bounds);
    }

    [Fact]
    public void Shapes_Added_On_Both_Sides_Are_All_Kept()
    {
        var (b, o, t) = Three();
        o.AddNode(Bdd(o), Named(o, "Chassis"), new Bounds(10, 200, 100, 50));
        var seat = t.Create(Named(t, "Structure"), ElementKind.Block, "Seat");
        t.AddNode(Bdd(t), seat, new Bounds(200, 200, 100, 50));
        var r = ModelMerge.Merge(b, o, t);
        Assert.False(r.HasConflicts);
        Assert.Equal(4, Bdd(r.Model).Nodes.Count);
    }

    [Fact]
    public void Conflict_Keys_Are_Stable_And_Resolve_Reuses_Choices()
    {
        var (b, o, t) = Three();
        o.Set(Named(o, "Engine"), FieldKeys.Documentation, "A");
        t.Set(Named(t, "Engine"), FieldKeys.Documentation, "B");
        o.Set(Named(o, "Wheel"), FieldKeys.Documentation, "C");
        t.Set(Named(t, "Wheel"), FieldKeys.Documentation, "D");
        var r1 = ModelMerge.Merge(b, o, t);
        var r2 = ModelMerge.Merge(b, o, t);
        Assert.Equal(r1.Conflicts.Select(c => c.Key), r2.Conflicts.Select(c => c.Key));
        Assert.Equal(2, r1.Conflicts.Count);
        r1.ChooseAll(MergeChoice.Theirs);
        var m = r1.Resolve();
        Assert.Equal("B", Named(m, "Engine").Documentation);
        Assert.Equal("D", Named(m, "Wheel").Documentation);
    }

    [Fact]
    public void Relationship_Fields_Merge_Like_Element_Fields()
    {
        var (b, o, t) = Three();
        var od = o.Relationships.Single();
        var td = t.Relationships.Single();
        o.ApplyRemote(new SetFieldOp(od.Id, FieldKeys.Name, "uses"));
        t.ApplyRemote(new SetFieldOp(td.Id, FieldKeys.Name, "drives"));
        var r = ModelMerge.Merge(b, o, t);
        var c = Assert.Single(r.Conflicts);
        Assert.Equal(FieldKeys.Name, c.Subject);
        Assert.Equal("uses", r.Model.Relationships.Single().Name);
    }

    [Fact]
    public void Sample_Model_Merges_With_Large_Independent_Edits()
    {
        var b = SampleModels.Vehicle();
        var o = Clone(b);
        var t = Clone(b);
        var blocks = o.OfKind(ElementKind.Block).Take(3).ToList();
        foreach (var e in blocks) o.Set(e, FieldKeys.Documentation, "ours " + e.Name);
        var reqs = t.OfKind(ElementKind.Requirement).Take(3).ToList();
        foreach (var e in reqs) t.Set(e, "text", "theirs " + e.Name);
        var r = ModelMerge.Merge(b, o, t);
        Assert.False(r.HasConflicts);
        Assert.Equal(6, ModelDiff.Compare(b, r.Model).Count);
        Assert.NotNull(ProjectFile.Deserialize(ProjectFile.Serialize(r.Model)));
    }
}
