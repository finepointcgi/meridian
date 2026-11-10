using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>Per-package permissions in the core: resolution, the write check, redaction and what filtered views receive.</summary>
public sealed class PackagePermissionTests
{
    private static readonly Guid Bob = Guid.NewGuid(), Team1 = Guid.NewGuid(), Team2 = Guid.NewGuid();

    /// <summary>
    /// Model ▸ Public (Pub, a part "reactor" typed by Reactor, an association Pub→Reactor, a dependency Pub→Lib, a BDD showing
    /// Pub, Reactor and Lib) ▸ Secret (Reactor, Inner ▸ Core, a BDD) ▸ Library (Lib).
    /// </summary>
    private sealed class World
    {
        public readonly ModelRepository M = new("Plant");
        public readonly Element Public, Secret, Inner, Library, Pub, Reactor, Core, Lib, Part;
        public readonly Relationship Assoc, Dep;
        public readonly Diagram Bdd, SecretBdd;

        public World()
        {
            Public = M.Create(M.Root, ElementKind.Package, "Public");
            Secret = M.Create(M.Root, ElementKind.Package, "Secret");
            Library = M.Create(M.Root, ElementKind.Package, "Library");
            Pub = M.Create(Public, ElementKind.Block, "Pub");
            Reactor = M.Create(Secret, ElementKind.Block, "ReactorCoreXJ9");
            M.Set(Reactor, FieldKeys.Documentation, "TOP SECRET design notes");
            Inner = M.Create(Secret, ElementKind.Package, "Inner");
            Core = M.Create(Inner, ElementKind.Block, "Core");
            Lib = M.Create(Library, ElementKind.Block, "Lib");
            Part = M.Create(Pub, ElementKind.Property, "reactor");
            M.Set(Part, FieldKeys.Type, Reactor.Id);
            Assoc = M.Connect(ElementKind.Association, Pub, Reactor, Public);
            Dep = M.Connect(ElementKind.Dependency, Pub, Lib, Public);
            Bdd = M.CreateDiagram(Public, DiagramType.BlockDefinition, "Overview");
            M.AddNode(Bdd, Pub, new Bounds(0, 0, 120, 60));
            M.AddNode(Bdd, Reactor, new Bounds(200, 0, 120, 60));
            M.AddNode(Bdd, Lib, new Bounds(400, 0, 120, 60));
            M.AddEdge(Bdd, Assoc);
            M.AddEdge(Bdd, Dep);
            SecretBdd = M.CreateDiagram(Secret, DiagramType.BlockDefinition, "Secret plan");
            M.AddNode(SecretBdd, Reactor, new Bounds(0, 0, 120, 60));
        }

        /// <summary>Bob (an editor) with Secret hidden and Library read-only.</summary>
        public AccessView BobView(params PermissionRule[] extra) => AccessView.For(
            [new(Secret.Id, PrincipalKind.User, Bob, PackageAccess.None), new(Library.Id, PrincipalKind.User, Bob, PackageAccess.Read), .. extra],
            Bob, [], PackageAccess.Edit, PackageAccess.Manage);
    }

    // ================================================================= resolution

    [Fact]
    public void Nearest_Rule_Wins_Down_The_Containment_Tree()
    {
        var w = new World();
        var v = AccessView.For([new(w.Secret.Id, PrincipalKind.User, Bob, PackageAccess.Read), new(w.Inner.Id, PrincipalKind.User, Bob, PackageAccess.Manage)],
            Bob, [], PackageAccess.Edit, PackageAccess.Manage);
        Assert.Equal(PackageAccess.Edit, v.Level(w.Pub));        // no rule: the role's default
        Assert.Equal(PackageAccess.Read, v.Level(w.Secret));
        Assert.Equal(PackageAccess.Read, v.Level(w.Reactor));    // inherited
        Assert.Equal(PackageAccess.Manage, v.Level(w.Core));     // the nearer rule wins
        Assert.False(v.CanEdit(w.Reactor));
        Assert.True(v.CanEdit(w.Core));
        Assert.True(v.CanManage(w.Inner));
        Assert.False(v.CanManage(w.Secret));
        Assert.False(v.HidesAnything);
    }

    [Fact]
    public void User_Rules_Beat_Team_Rules_And_The_Most_Permissive_Team_Rule_Wins()
    {
        var w = new World();
        PermissionRule[] rules =
        [
            new(w.Secret.Id, PrincipalKind.Team, Team1, PackageAccess.None),
            new(w.Secret.Id, PrincipalKind.Team, Team2, PackageAccess.Edit),
            new(w.Library.Id, PrincipalKind.Team, Team1, PackageAccess.Edit),
            new(w.Library.Id, PrincipalKind.User, Bob, PackageAccess.Read),
            new(w.Public.Id, PrincipalKind.Team, Guid.NewGuid(), PackageAccess.None),   // a team Bob isn't in
        ];
        var v = AccessView.For(rules, Bob, [Team1, Team2], PackageAccess.Edit, PackageAccess.Manage);
        Assert.Equal(PackageAccess.Edit, v.Level(w.Reactor));
        Assert.Equal(PackageAccess.Read, v.Level(w.Lib));
        Assert.Equal(PackageAccess.Edit, v.Level(w.Pub));
        Assert.Equal(PackageAccess.None, AccessView.For(rules, Bob, [Team1], PackageAccess.Edit, PackageAccess.Manage).Level(w.Secret));
        Assert.Equal(PackageAccess.Edit, AccessView.For(rules, Guid.NewGuid(), [], PackageAccess.Edit, PackageAccess.Manage).Level(w.Lib));
    }

    [Fact]
    public void Owners_Always_Manage_And_Viewers_Never_Edit()
    {
        var w = new World();
        PermissionRule[] rules = [new(w.Public.Id, PrincipalKind.User, Bob, PackageAccess.None)];
        var owner = AccessView.For(rules, Bob, [], PackageAccess.Edit, PackageAccess.Manage, owner: true);
        Assert.Same(AccessView.Full, owner);
        Assert.False(owner.IsHidden(w.Pub));
        Assert.True(owner.CanManage(w.Public));

        var viewer = AccessView.For([new(w.Library.Id, PrincipalKind.User, Bob, PackageAccess.Manage)], Bob, [], PackageAccess.Read, PackageAccess.Read);
        Assert.Equal(PackageAccess.Read, viewer.Level(w.Lib));   // capped
        Assert.Equal(PackageAccess.Read, viewer.Level(w.Pub));
        Assert.False(viewer.CanEdit(w.Lib));
    }

    [Fact]
    public void None_Hides_The_Contents_But_Not_The_Package_And_Nested_Grants_Cannot_Reveal()
    {
        var w = new World();
        var v = w.BobView(new PermissionRule(w.Inner.Id, PrincipalKind.User, Bob, PackageAccess.Edit));
        Assert.True(v.HidesAnything);
        Assert.False(v.IsHidden(w.Secret));      // listed (its name is part of the visible root)
        Assert.True(v.IsHidden(w.Reactor));
        Assert.True(v.IsHidden(w.Inner));
        Assert.True(v.IsHidden(w.Core));         // the nested "edit" grant doesn't reveal it
        Assert.True(v.IsHidden(w.SecretBdd));
        Assert.True(v.IsHidden(w.Assoc));        // a relationship with a hidden end
        Assert.False(v.IsHidden(w.Dep));
        Assert.False(v.IsHidden(w.Part));
        Assert.False(v.CanSeeContents(w.Secret));
        Assert.False(v.CanEdit(w.Secret));       // can't rename or delete what you can't see into
        Assert.Same(w.Secret, v.ProtectingPackage(w.Core));
        Assert.DoesNotContain("Core", v.DeniedMessage(w.Core));
        Assert.Contains("“Secret”", v.DeniedMessage(w.Core));
    }

    [Fact]
    public void Relationships_With_A_Read_Only_End_Are_Read_Only()
    {
        var w = new World();
        var v = w.BobView();
        Assert.True(v.CanEdit(w.Pub));
        Assert.False(v.CanEdit(w.Dep));
        Assert.Contains("“Library” is read-only", v.DeniedMessage(w.Dep));
        Assert.True(AccessView.For([], Bob, [], PackageAccess.Edit, PackageAccess.Manage).CanEdit(w.Dep));
    }

    // ================================================================= the write check

    private static ElementSnap NewBlockSnap(string name)
    {
        var scratch = new ModelRepository();
        return ElementSnap.Of(scratch.Create(scratch.Root, ElementKind.Block, name));
    }

    [Fact]
    public void Operations_On_Protected_Packages_Are_Rejected_With_A_Reason()
    {
        var w = new World();
        var v = w.BobView();
        var m = w.M;
        // Allowed: Bob's own package.
        Assert.Null(PermissionCheck.Check(m, v, new SetFieldOp(w.Pub.Id, FieldKeys.Name, "Pub2")));
        Assert.Null(PermissionCheck.Check(m, v, new AttachOp(w.Public.Id, -1, NewBlockSnap("New"))));
        Assert.Null(PermissionCheck.Check(m, v, new NodeBoundsOp(w.Bdd.Id, w.Bdd.Nodes[0].Id, new Bounds(5, 5, 100, 50))));

        // Read-only and hidden packages.
        Assert.Contains("“Library” is read-only", PermissionCheck.Check(m, v, new SetFieldOp(w.Lib.Id, FieldKeys.Name, "x")));
        Assert.Contains("Library", PermissionCheck.Check(m, v, new AttachOp(w.Library.Id, -1, NewBlockSnap("Sneaky"))));
        Assert.Contains("Secret", PermissionCheck.Check(m, v, new SetFieldOp(w.Reactor.Id, FieldKeys.Name, "x")));
        Assert.Contains("Secret", PermissionCheck.Check(m, v, new AttachOp(w.Secret.Id, -1, NewBlockSnap("Plant"))));
        Assert.NotNull(PermissionCheck.Check(m, v, new DetachOp(w.Lib.Id)));
        Assert.NotNull(PermissionCheck.Check(m, v, new SetFieldOp(w.Secret.Id, FieldKeys.Name, "Renamed")));   // the hidden package itself

        // Moves into and out of a protected package.
        Assert.Contains("Library", PermissionCheck.Check(m, v, new MoveOp(w.Pub.Id, w.Library.Id, -1)));
        Assert.NotNull(PermissionCheck.Check(m, v, new MoveOp(w.Lib.Id, w.Public.Id, -1)));
        Assert.NotNull(PermissionCheck.Check(m, v, new MoveOp(w.Pub.Id, w.Secret.Id, -1)));

        // Deleting a package that contains a protected one.
        var v2 = AccessView.For([new(w.Inner.Id, PrincipalKind.User, Bob, PackageAccess.Read)], Bob, [], PackageAccess.Edit, PackageAccess.Manage);
        Assert.Contains("“Inner” is read-only", PermissionCheck.Check(m, v2, new DetachOp(w.Secret.Id)));
        Assert.Null(PermissionCheck.Check(m, v2, new DetachOp(w.Reactor.Id)));

        // Relationships whose source or target is protected.
        var scratch = new ModelRepository();
        var a = scratch.Create(scratch.Root, ElementKind.Block, "A");
        var rel = new Relationship(ElementKind.Dependency, a, a, Element.NewId());
        var snap = ElementSnap.Of(rel) with { SourceId = w.Pub.Id, TargetId = w.Lib.Id };
        Assert.Contains("Library", PermissionCheck.Check(m, v, new AttachOp(w.Public.Id, -1, snap)));
        Assert.Null(PermissionCheck.Check(m, v, new AttachOp(w.Public.Id, -1, snap with { TargetId = w.Part.Id })));
        Assert.NotNull(PermissionCheck.Check(m, v, new SetFieldOp(w.Dep.Id, FieldKeys.Name, "uses")));

        // Diagrams owned by a protected package, and showing hidden elements.
        var libBdd = m.CreateDiagram(w.Library, DiagramType.BlockDefinition, "Lib BDD");
        Assert.NotNull(PermissionCheck.Check(m, v, new NodeInsertOp(libBdd.Id, -1, Element.NewId(), w.Pub.Id, new Bounds(0, 0, 50, 50))));
        Assert.NotNull(PermissionCheck.Check(m, v, new NodeInsertOp(w.Bdd.Id, -1, Element.NewId(), w.Core.Id, new Bounds(0, 0, 50, 50))));
        Assert.NotNull(PermissionCheck.Check(m, v, new EdgeInsertOp(w.Bdd.Id, -1, Element.NewId(), w.Assoc.Id, w.Bdd.Nodes[0].Id, w.Bdd.Nodes[1].Id, [])));
    }

    [Fact]
    public void Viewers_And_Owners_Are_Checked_By_Role()
    {
        var w = new World();
        Assert.NotNull(PermissionCheck.Check(w.M, AccessView.ReadAll, new SetFieldOp(w.Pub.Id, FieldKeys.Name, "x")));
        Assert.Null(PermissionCheck.Check(w.M, AccessView.Full, new SetFieldOp(w.Reactor.Id, FieldKeys.Name, "x")));
    }

    [Fact]
    public void Whole_Model_Replacements_Are_Checked_Element_By_Element()
    {
        var w = new World();
        var before = ProjectFile.Deserialize(ProjectFile.Serialize(w.M));
        var mine = ProjectFile.Deserialize(ProjectFile.Serialize(w.M));
        mine.Set(mine.Find(w.Pub.Id)!, FieldKeys.Documentation, "mine");
        mine.Create(mine.Find(w.Public.Id)!, ElementKind.Block, "Added");
        mine.SetBounds(mine.Find(w.Bdd.Id) as Diagram ?? throw new InvalidOperationException(),
            new Dictionary<DiagramNode, Bounds> { [((Diagram)mine.Find(w.Bdd.Id)!).Nodes[0]] = new(9, 9, 120, 60) });
        Assert.Null(PermissionCheck.CheckReplace(before, mine, w.BobView()));

        var theirs = ProjectFile.Deserialize(ProjectFile.Serialize(w.M));
        theirs.Set(theirs.Find(w.Lib.Id)!, FieldKeys.Documentation, "changed");
        Assert.Contains("Library", PermissionCheck.CheckReplace(before, theirs, w.BobView()));

        var deleted = ProjectFile.Deserialize(ProjectFile.Serialize(w.M));
        deleted.Delete(deleted.Find(w.Core.Id)!);
        Assert.Contains("Secret", PermissionCheck.CheckReplace(before, deleted, w.BobView()));

        var moved = ProjectFile.Deserialize(ProjectFile.Serialize(w.M));
        moved.Move(moved.Find(w.Pub.Id)!, moved.Find(w.Library.Id)!);
        Assert.NotNull(PermissionCheck.CheckReplace(before, moved, w.BobView()));
        Assert.Null(PermissionCheck.CheckReplace(before, moved, AccessView.Full));
    }

    // ================================================================= redaction

    [Fact]
    public void Redaction_Removes_Hidden_Elements_And_Keeps_Nameless_Placeholders()
    {
        var w = new World();
        var copy = ProjectFile.Deserialize(ProjectFile.Serialize(w.M));
        var removed = ModelRedactor.Redact(copy, w.BobView());
        Assert.True(removed >= 5);
        Assert.NotNull(copy.Find(w.Secret.Id));
        Assert.Empty(copy.Find(w.Secret.Id)!.OwnedElements);
        foreach (var gone in new Element[] { w.Reactor, w.Inner, w.Core, w.SecretBdd, w.Assoc })
            Assert.Null(copy.Find(gone.Id));
        Assert.NotNull(copy.Find(w.Dep.Id));
        Assert.Equal("", copy.Find(w.Part.Id)!.Get(FieldKeys.Type));   // the reference to the hidden block is cleared

        var bdd = (Diagram)copy.Find(w.Bdd.Id)!;
        Assert.Equal(3, bdd.Nodes.Count);
        var placeholder = bdd.Nodes[1];
        Assert.Equal(w.Bdd.Nodes[1].Id, placeholder.Id);                // same shape, same place
        Assert.Equal(w.Bdd.Nodes[1].Bounds, placeholder.Bounds);
        Assert.True(ModelRedactor.IsPlaceholder(placeholder.Element));
        Assert.Equal("", placeholder.Element.Name);
        Assert.Single(bdd.Edges);                                        // the association to the hidden block is gone
        Assert.Same(copy.Find(w.Dep.Id), bdd.Edges[0].Relationship);

        var json = ProjectFile.Serialize(copy);
        foreach (var secret in new[] { "ReactorCoreXJ9", "TOP SECRET", "Core", "Secret plan", w.Reactor.Id, w.Core.Id, w.Assoc.Id, w.SecretBdd.Id })
            Assert.DoesNotContain(secret, json);

        // Placeholders survive the project format (the filtered model is sent to the browser's session as JSON).
        var again = ProjectFile.Deserialize(json);
        Assert.True(ModelRedactor.IsPlaceholder(((Diagram)again.Find(w.Bdd.Id)!).Nodes[1].Element));
        var svg = SvgRenderer.Render(again, (Diagram)again.Find(w.Bdd.Id)!);
        Assert.Contains("redacted-shape", svg);
        Assert.Contains("Restricted", svg);
        Assert.DoesNotContain("ReactorCoreXJ9", svg);
        Assert.DoesNotContain("block»", svg[svg.IndexOf("redacted-shape", StringComparison.Ordinal)..(svg.IndexOf("redacted-shape", StringComparison.Ordinal) + 400)]);
    }

    [Fact]
    public void Nothing_Is_Redacted_Without_A_None_Rule()
    {
        var w = new World();
        var json = ProjectFile.Serialize(w.M);
        var copy = ProjectFile.Deserialize(json);
        Assert.Equal(0, ModelRedactor.Redact(copy, AccessView.For([new(w.Library.Id, PrincipalKind.User, Bob, PackageAccess.Read)], Bob, [], PackageAccess.Edit, PackageAccess.Manage)));
        Assert.Equal(json, ProjectFile.Serialize(copy));
    }

    // ================================================================= relaying others' operations

    private static RelayDecision Relay(ModelRepository m, AccessView v, ModelOp op)
    {
        var probe = PermissionCheck.Probe(m, v, op);
        m.ApplyRemote(op);
        return PermissionCheck.Decide(m, v, op, probe);
    }

    [Fact]
    public void Filtered_Views_Get_Only_Operations_On_What_They_Can_See()
    {
        var w = new World();
        var v = w.BobView();
        Assert.Equal(RelayDecision.Relay, Relay(w.M, v, new SetFieldOp(w.Pub.Id, FieldKeys.Documentation, "seen")));
        Assert.Equal(RelayDecision.Drop, Relay(w.M, v, new SetFieldOp(w.Reactor.Id, FieldKeys.Documentation, "unseen")));
        Assert.Equal(RelayDecision.Drop, Relay(w.M, v, new AttachOp(w.Inner.Id, -1, NewBlockSnap("Hidden"))));
        Assert.Equal(RelayDecision.Relay, Relay(w.M, v, new AttachOp(w.Public.Id, -1, NewBlockSnap("Shown"))));
        Assert.Equal(RelayDecision.Drop, Relay(w.M, v, new NodeBoundsOp(w.SecretBdd.Id, w.SecretBdd.Nodes[0].Id, new Bounds(1, 1, 50, 50))));
        Assert.Equal(RelayDecision.Relay, Relay(w.M, v, new NodeBoundsOp(w.Bdd.Id, w.Bdd.Nodes[1].Id, new Bounds(1, 1, 50, 50))));   // a placeholder moved
        var assocEdge = w.Bdd.Edges.First(e => ReferenceEquals(e.Relationship, w.Assoc));
        Assert.Equal(RelayDecision.Drop, Relay(w.M, v, new EdgeWaypointsOp(w.Bdd.Id, assocEdge.Id, [new Pt(1, 1)])));

        // Changes of what is visible: a fresh filtered model.
        Assert.Equal(RelayDecision.Resync, Relay(w.M, v, new MoveOp(w.Pub.Id, w.Secret.Id, -1)));
        Assert.Equal(RelayDecision.Resync, Relay(w.M, v, new MoveOp(w.Pub.Id, w.Public.Id, -1)));
        Assert.Equal(RelayDecision.Resync, Relay(w.M, v, new NodeInsertOp(w.Bdd.Id, -1, Element.NewId(), w.Core.Id, new Bounds(0, 200, 80, 40))));
        Assert.Equal(RelayDecision.Resync, Relay(w.M, v, new DetachOp(w.Reactor.Id)));   // its placeholder must go
        Assert.Equal(RelayDecision.Drop, Relay(w.M, v, new DetachOp(w.SecretBdd.Id)));   // hidden, and shown nowhere visible
        Assert.Equal(RelayDecision.Resync, Relay(w.M, v, new DetachOp(w.Inner.Id)));     // Core (inside it) has a placeholder now
        Assert.Equal(RelayDecision.Relay, Relay(w.M, AccessView.Full, new SetFieldOp(w.Pub.Id, FieldKeys.Name, "x")));
    }
}
