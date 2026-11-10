using Meridian.Core.Analysis;
using Meridian.Core.Libraries;
using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Core.Samples;
using Meridian.Core.Validation;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>Shared model libraries ("used projects", docs/LIBRARIES.md): the overlay in ModelRepository and the project file.</summary>
public class LibraryTests
{
    private static readonly Guid PartsProject = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>A "parts library" project: a "Standard parts" package with a block, a requirement, a relationship and a diagram.</summary>
    private static (ModelRepository Model, Element Package, Element Wheel, Element Load) PartsLibrary(string wheelDoc = "18 inch")
    {
        var m = new ModelRepository("Parts Library");
        var pkg = m.Create(m.Root, ElementKind.Package, "Standard parts");
        var wheel = m.Create(pkg, ElementKind.Block, "Wheel");
        m.Set(wheel, FieldKeys.Documentation, wheelDoc);
        var hub = m.Create(pkg, ElementKind.Block, "Hub");
        m.Connect(ElementKind.Composition, wheel, hub);
        var load = m.Create(pkg, ElementKind.Requirement, "Max load");
        m.Set(load, FieldKeys.Text, "A wheel shall carry 500 kg.");
        var d = m.CreateDiagram(pkg, DiagramType.BlockDefinition, "Parts");
        m.AddNode(d, wheel, new Bounds(10, 10, 120, 60));
        m.AddNode(d, hub, new Bounds(200, 10, 120, 60));
        m.Create(m.Root, ElementKind.Package, "Internal");   // not shared
        return (m, pkg, wheel, load);
    }

    private static LibraryInfo Info(string package, string pin = "1", string mount = "", string usage = "u1", Guid? project = null) =>
        new(usage, project ?? PartsProject, "Parts Library", package, LibraryPinKind.Version, pin, LibraryPins.Label(LibraryPinKind.Version, pin), mount,
            Resolved: "v" + pin);

    private static LibraryPayload Payload(ModelRepository source, Element package, string pin = "1", string mount = "") =>
        new(Info(package.Id, pin, mount), LibraryContent.Extract(source, package.Id)!);

    /// <summary>A vehicle project using the parts library: a part typed by Wheel, a satisfy to Max load and Wheel on a BDD.</summary>
    private static (ModelRepository Model, string Json, Element Part, Relationship Satisfy) UsingProject(LibraryPayload lib)
    {
        var project = SampleModels.Vehicle();
        var m = ProjectFile.Deserialize(ProjectFile.Serialize(project), [lib]);
        var wheel = m.OfKind(ElementKind.Block).Single(b => b.Name == "Wheel");
        var load = m.OfKind(ElementKind.Requirement).Single(r => r.Name == "Max load");
        var vehicle = m.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle");
        var part = m.Create(vehicle, ElementKind.Property, "frontLeft");
        m.Set(part, FieldKeys.Type, wheel.Id);
        var sat = m.Connect(ElementKind.Satisfy, vehicle, load);
        var bdd = m.Diagrams.First(d => !m.IsLibraryElement(d) && d.DiagramType == DiagramType.BlockDefinition);
        m.AddNode(bdd, wheel, new Bounds(600, 400, 120, 60), showRelationships: false);
        return (m, ProjectFile.Serialize(m), part, sat);
    }

    [Fact]
    public void Library_Elements_Resolve_References_With_Their_Original_Ids()
    {
        var (src, pkg, wheel, load) = PartsLibrary();
        var (m, _, part, sat) = UsingProject(Payload(src, pkg));

        var mount = Assert.Single(m.Libraries);
        Assert.Same(m.Root, mount.MountPoint);                       // default mount point: the model root
        Assert.Equal(pkg.Id, mount.Root.Id);
        Assert.Equal(wheel.Id, m.Find(wheel.Id)!.Id);                 // original ids
        Assert.True(m.IsLibraryElement(m.Find(wheel.Id)!));
        Assert.True(m.IsLibraryRoot(mount.Root));
        Assert.False(m.IsLibraryElement(part));
        Assert.Equal("Wheel", m.TypeDisplayName(part));
        Assert.Same(m.Find(load.Id), sat.Target);
        Assert.Contains(sat, m.RelationshipsOf(m.Find(load.Id)!));
        Assert.Contains(m.OfKind(ElementKind.Composition), r => m.IsLibraryElement(r));   // library relationships load too
        Assert.Single(m.Diagrams, d => m.IsLibraryElement(d));                          // and its diagrams
        Assert.Equal("Parts Library · v1", mount.Info.Badge);
        Assert.DoesNotContain(m.AllElements, e => e.Name == "Internal");                // only the shared package
        Assert.DoesNotContain(m.OwnElements, m.IsLibraryElement);
    }

    [Fact]
    public void Library_Mounts_Below_Its_Mount_Point()
    {
        var (src, pkg, _, _) = PartsLibrary();
        var vehicle = SampleModels.Vehicle();
        var structure = vehicle.OfKind(ElementKind.Package).Single(p => p.Name == "Structure");
        var m = ProjectFile.Deserialize(ProjectFile.Serialize(vehicle), [Payload(src, pkg, mount: structure.Id)]);
        Assert.Equal("Structure", m.Libraries[0].MountPoint.Name);
        Assert.Equal("Structure::Standard parts::Wheel", m.OfKind(ElementKind.Block).Single(b => b.Name == "Wheel").QualifiedName);
        // Deleting the package that shows a library would take the library with it.
        var ex = Assert.Throws<InvalidOperationException>(() => m.Delete(m.Libraries[0].MountPoint));
        Assert.Contains("Parts Library", ex.Message);
        Assert.Contains("Structure", ProjectFile.Serialize(m));

        // A mount point that is gone falls back to the model root.
        var gone = ProjectFile.Deserialize(ProjectFile.Serialize(vehicle), [Payload(src, pkg, mount: "_nope")]);
        Assert.Same(gone.Root, gone.Libraries[0].MountPoint);
    }

    [Fact]
    public void Library_Elements_Are_Read_Only()
    {
        var (src, pkg, wheelSrc, _) = PartsLibrary();
        var (m, _, part, _) = UsingProject(Payload(src, pkg));
        var wheel = m.Find(wheelSrc.Id)!;
        var libPkg = m.Find(pkg.Id)!;
        var libDiagram = m.Diagrams.Single(d => m.IsLibraryElement(d));

        var ex = Assert.Throws<ReadOnlyElementException>(() => m.Rename(wheel, "Tyre"));
        Assert.Contains("Parts Library", ex.Message);
        Assert.Contains("Open it in the source project", ex.Message);
        Assert.Throws<ReadOnlyElementException>(() => m.Delete(wheel));
        Assert.Throws<ReadOnlyElementException>(() => m.Delete(libPkg));
        Assert.Throws<ReadOnlyElementException>(() => m.Create(libPkg, ElementKind.Block, "Sneaky"));
        Assert.Throws<ReadOnlyElementException>(() => m.Move(wheel, m.Root));
        Assert.Throws<ReadOnlyElementException>(() => m.Move(part, libPkg));
        Assert.Throws<ReadOnlyElementException>(() => m.SetBounds(libDiagram, new Dictionary<DiagramNode, Bounds> { [libDiagram.Nodes[0]] = new(0, 0, 1, 1) }));
        Assert.Throws<ReadOnlyElementException>(() => m.CreateDiagram(libPkg, DiagramType.BlockDefinition));
        Assert.Equal("Wheel", wheel.Name);

        // Relationships to library elements belong to the project, even when the library element is the source.
        var vehicle = m.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle");
        var gen = m.Connect(ElementKind.Dependency, wheel, vehicle);
        Assert.False(m.IsLibraryElement(gen.Owner!));

        // The server's single write check refuses operations on library elements, for owners too; replicas ignore them.
        Assert.NotNull(PermissionCheck.Check(m, AccessView.Full, new SetFieldOp(wheel.Id, FieldKeys.Name, "X")));
        Assert.NotNull(PermissionCheck.Check(m, AccessView.Full, new AttachOp(libPkg.Id, -1, ElementSnap.Of(new Element(ElementKind.Block)))));
        Assert.NotNull(PermissionCheck.Check(m, AccessView.Full, new NodeBoundsOp(libDiagram.Id, libDiagram.Nodes[0].Id, new Bounds(0, 0, 5, 5))));
        Assert.Null(PermissionCheck.Check(m, AccessView.Full, new SetFieldOp(part.Id, FieldKeys.Name, "frontRight")));
        m.ApplyRemote(new SetFieldOp(wheel.Id, FieldKeys.Name, "Hacked"));
        Assert.Equal("Wheel", wheel.Name);
    }

    [Fact]
    public void Library_Content_Is_Not_Saved_But_References_Survive_Save_And_Load()
    {
        var (src, pkg, wheel, load) = PartsLibrary();
        var lib = Payload(src, pkg);
        var (m, json, part, sat) = UsingProject(lib);

        Assert.DoesNotContain("\"Standard parts\"", json);
        Assert.DoesNotContain("Max load", json);
        Assert.Contains(wheel.Id, json);   // the part's type and the shape still name it
        Assert.Contains(load.Id, json);

        // Loaded without its libraries (e.g. by someone who can't read them): references are kept as placeholders.
        var bare = ProjectFile.Deserialize(json);
        var bareSat = (Relationship)bare.Find(sat.Id)!;
        Assert.True(MissingReference.Is(bareSat.Target));
        Assert.Equal(load.Id, bareSat.Target.Id);
        Assert.Equal(json, ProjectFile.Serialize(bare));   // nothing lost

        // And with them again, everything resolves.
        var again = ProjectFile.Deserialize(json, [lib]);
        Assert.Same(again.Find(load.Id), ((Relationship)again.Find(sat.Id)!).Target);
        Assert.Equal(json, ProjectFile.Serialize(again));

        // In memory (tabs, copies) the libraries travel along and mount again.
        var copy = ProjectFile.Copy(m);
        Assert.Single(copy.Libraries);
        Assert.Equal(m.Count, copy.Count);
        Assert.Equal(json, ProjectFile.Serialize(copy));
        Assert.Equal(m.Count, ProjectFile.Deserialize(ProjectFile.WithLibraries(json, [lib])).Count);
        Assert.Equal(part.TypeRef, copy.Find(part.Id)!.TypeRef);
    }

    [Fact]
    public void Missing_Library_Element_After_An_Update_Is_A_Placeholder_And_A_Validation_Error()
    {
        var (src, pkg, _, load) = PartsLibrary();
        var (_, json, _, sat) = UsingProject(Payload(src, pkg));

        src.Delete(load);   // v2 of the library no longer has the requirement
        var v2 = Payload(src, pkg, "2");
        var m = ProjectFile.Deserialize(json, [v2]);
        var r = (Relationship)m.Find(sat.Id)!;
        Assert.True(MissingReference.Is(r.Target));
        Assert.Contains(m.MissingReferences, e => e.Id == load.Id);
        var issues = new ModelValidator().Validate(m);
        Assert.Contains(issues, i => i.RuleId == "LIB-001" && ReferenceEquals(i.Element, r));
        Assert.DoesNotContain(issues, i => i.RuleId == "MER-003" && ReferenceEquals(i.Element, r));
        Assert.Equal(json, ProjectFile.Serialize(m));   // the reference is kept until someone fixes it

        // A shape of a removed library element stays on the diagram as a placeholder.
        src.Delete(src.OfKind(ElementKind.Block).Single(b => b.Name == "Wheel"));
        var v3 = ProjectFile.Deserialize(json, [Payload(src, pkg, "3")]);
        Assert.Contains(v3.Diagrams.SelectMany(d => d.Nodes), n => MissingReference.Is(n.Element));
        Assert.Contains(new ModelValidator().Validate(v3), i => i.RuleId == "LIB-001" && i.Element is Diagram);
        Assert.Equal(json, ProjectFile.Serialize(v3));
    }

    [Fact]
    public void Library_Issues_Are_Not_Reported_In_The_Using_Project()
    {
        var (src, pkg, _, _) = PartsLibrary();
        src.Create(src.Find(pkg.Id)!, ElementKind.Requirement, "Untexted");   // a warning in the library's own project
        var (m, _, _, _) = UsingProject(Payload(src, pkg));
        Assert.DoesNotContain(new ModelValidator().Validate(m), i => m.IsLibraryElement(i.Element));
    }

    [Fact]
    public void Diamond_Duplicates_And_Id_Collisions_Load_Once()
    {
        var (src, pkg, wheel, _) = PartsLibrary();
        var a = Payload(src, pkg);
        var b = a with { Info = a.Info with { UsageId = "u2" } };
        var m = ProjectFile.Deserialize(ProjectFile.Serialize(SampleModels.Vehicle()), [a, b]);
        Assert.Single(m.Libraries);
        Assert.Single(m.AllElements, e => e.Id == wheel.Id);
    }

    [Fact]
    public async Task Transitive_Libraries_Load_Breadth_First_With_Cycles_And_Conflicting_Pins_Reported()
    {
        // project P uses A; A uses B and C; B uses C at another pin (conflict) and P (cycle); C uses A (cycle).
        Guid P = Guid.NewGuid(), A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();
        var uses = new Dictionary<Guid, LibraryRequest[]>
        {
            [A] = [new("ab", B, "_b", LibraryPinKind.Version, "1", "_a"), new("ac", C, "_c", LibraryPinKind.Version, "1", "_a")],
            [B] = [new("bc", C, "_c", LibraryPinKind.Version, "2", "_b"), new("bp", P, "_p", LibraryPinKind.Version, "1", "_b")],
            [C] = [new("ca", A, "_a", LibraryPinKind.Version, "1", "_c")],
        };
        var names = new Dictionary<Guid, string> { [A] = "A", [B] = "B", [C] = "C", [P] = "P" };
        var fetched = new List<string>();
        var result = await LibraryResolver.ResolveAsync(P, [new LibraryRequest("pa", A, "_a", LibraryPinKind.Version, "1", "")], r =>
        {
            fetched.Add(names[r.SourceProjectId] + r.PinValue);
            var info = new LibraryInfo(r.UsageId, r.SourceProjectId, names[r.SourceProjectId], r.RootPackageId, r.PinKind, r.PinValue,
                                       LibraryPins.Label(r.PinKind, r.PinValue), r.MountId, r.ViaUsageId);
            return Task.FromResult<LibraryFetch?>(new LibraryFetch(info, $$$"""{"id":"{{{r.RootPackageId}}}","kind":"Package","fields":{"name":"{{{names[r.SourceProjectId]}}}"}}""",
                uses.GetValueOrDefault(r.SourceProjectId, [])));
        });

        Assert.Equal(["A", "B", "C"], result.Payloads.Select(p => p.Info.SourceProjectName));
        Assert.Equal(["A1", "B1", "C1"], fetched);   // C at pin 2 and the cycles are never fetched
        Assert.Equal("pa", result.Payloads[1].Info.ViaUsageId);
        Assert.Equal("pa/ab", result.Payloads[1].Info.UsageId);
        Assert.Contains(result.Warnings, w => w.Contains("v1") && w.Contains("v2") && w.Contains("first"));
        Assert.Contains(result.Warnings, w => w.Contains("uses this project itself"));
        Assert.Contains(result.Warnings, w => w.Contains("cycle"));

        // Nested libraries mount where their own project mounted them when that package is loaded (here: inside A).
        var m = ProjectFile.Deserialize(ProjectFile.Serialize(new ModelRepository("P")), result.Payloads);
        Assert.Equal(3, m.Libraries.Count);
        Assert.Equal("A", m.Find("_b")!.Owner!.Name);
        Assert.Same(m.Libraries[0], m.LibraryOf(m.Find("_a")));
        Assert.Same(m.Libraries[1], m.LibraryOf(m.Find("_b")));
    }

    [Fact]
    public async Task Unavailable_Libraries_Are_Warnings_Not_Failures()
    {
        var result = await LibraryResolver.ResolveAsync(Guid.NewGuid(), [new LibraryRequest("u", Guid.NewGuid(), "_x", LibraryPinKind.Branch, "main", "")],
            _ => Task.FromResult<LibraryFetch?>(null));
        Assert.Empty(result.Payloads);
        Assert.Contains("not available", Assert.Single(result.Warnings));
    }

    [Fact]
    public void Update_Marks_References_To_Changed_Library_Elements_Suspect_And_Undo_Clears_Them()
    {
        var (src, pkg, wheel, load) = PartsLibrary();
        var v1 = Payload(src, pkg);
        var (_, json, part, sat) = UsingProject(v1);

        src.Set(src.Find(wheel.Id)!, FieldKeys.Documentation, "19 inch");
        src.Set(src.Find(load.Id)!, FieldKeys.Text, "A wheel shall carry 650 kg.");
        var v2 = Payload(src, pkg, "2");

        var before = ProjectFile.LibraryContent(v1);
        var after = ProjectFile.LibraryContent(v2);
        var diffs = ModelDiff.Compare(before, after);
        Assert.Contains(diffs, d => d.Id == wheel.Id && d.Kind == DiffKind.Changed);
        var (changed, removed) = LibraryUpdates.ChangedIds(before, after, diffs);
        Assert.Contains(wheel.Id, changed);
        Assert.Empty(removed);

        var m = ProjectFile.Deserialize(json, [v2]);
        var marks = LibraryUpdates.MarkSuspects(m, "Parts Library", "v1", "v2", changed, removed, new Dictionary<string, string>());
        Assert.Equal(2, marks.Count);
        var p = m.Find(part.Id)!;
        Assert.Contains("Wheel changed in Parts Library (v1 → v2)", SuspectLinks.LibraryChangeOf(p));
        Assert.True(SuspectLinks.IsSuspect((Relationship)m.Find(sat.Id)!));
        Assert.Contains(SuspectLinks.All(m), r => r.Id == sat.Id);
        var issues = new ModelValidator().Validate(m);
        Assert.Contains(issues, i => i.RuleId == "TRC-001" && ReferenceEquals(i.Element, p));
        Assert.Contains(issues, i => i.RuleId == "TRC-001" && i.Element.Id == sat.Id);

        // One undo step; and clearing a suspect link also clears its library mark.
        Assert.StartsWith("Library update", m.History.UndoDescription);
        LibraryUpdates.UnmarkSuspects(m, marks);
        Assert.Null(SuspectLinks.LibraryChangeOf(p));
        Assert.False(SuspectLinks.IsSuspect((Relationship)m.Find(sat.Id)!));
        LibraryUpdates.MarkSuspects(m, "Parts Library", "v1", "v2", changed, removed, new Dictionary<string, string>());
        SuspectLinks.Clear(m, (Relationship)m.Find(sat.Id)!);
        Assert.False(SuspectLinks.IsSuspect((Relationship)m.Find(sat.Id)!));
        SuspectLinks.ClearLibraryChange(m, p);
        Assert.Empty(SuspectLinks.SuspectElements(m));
    }

    [Fact]
    public void References_List_What_Uses_Each_Library()
    {
        var (src, pkg, _, _) = PartsLibrary();
        var (m, _, part, sat) = UsingProject(Payload(src, pkg));
        var refs = LibraryUpdates.References(m, m.Libraries[0]);
        Assert.Contains(refs, r => ReferenceEquals(r.Referrer, part) && r.How == "typed by");
        Assert.Contains(refs, r => ReferenceEquals(r.Referrer, sat) && r.How.Contains("satisfy"));
        Assert.Contains(refs, r => r.Referrer is Diagram && r.How == "shown on diagram");
    }

    [Fact]
    public void Hidden_Source_Packages_Stay_Hidden_In_The_Library()
    {
        var (src, pkg, _, _) = PartsLibrary();
        var secret = src.Create(pkg, ElementKind.Package, "Secret");
        src.Create(secret, ElementKind.Block, "Prototype");
        var user = Guid.NewGuid();
        var view = AccessView.For([new PermissionRule(secret.Id, PrincipalKind.User, user, PackageAccess.None)], user, [], PackageAccess.Read, PackageAccess.Read);

        var json = LibraryContent.Extract(src, pkg.Id, view)!;
        Assert.DoesNotContain("Prototype", json);
        Assert.Contains("Wheel", json);
        Assert.Contains("Prototype", LibraryContent.Extract(src, pkg.Id)!);

        // A package hidden as a whole can't be used at all; nor can something that isn't a package.
        var hideAll = AccessView.For([new PermissionRule(pkg.Id, PrincipalKind.User, user, PackageAccess.None)], user, [], PackageAccess.Read, PackageAccess.Read);
        Assert.Null(LibraryContent.Extract(src, pkg.Id, hideAll));
        Assert.Null(LibraryContent.Extract(src, src.OfKind(ElementKind.Block).First().Id));
    }

    [Fact]
    public void Branch_Merges_Keep_References_To_Library_Elements()
    {
        var (src, pkg, wheel, load) = PartsLibrary();
        var lib = Payload(src, pkg);
        var baseJson = ProjectFile.Serialize(SampleModels.Vehicle());
        var branch = ProjectFile.Deserialize(baseJson, [lib]);
        var part = branch.Create(branch.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle"), ElementKind.Property, "frontLeft");
        branch.Set(part, FieldKeys.Type, wheel.Id);
        var sat = branch.Connect(ElementKind.Satisfy, branch.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle"), branch.Find(load.Id)!);
        var json = ProjectFile.Serialize(branch);

        // Stored versions have no library content: the merge sees the references as external ids, not as dangling ends.
        var @base = ProjectFile.Deserialize(baseJson);
        var theirs = ProjectFile.Deserialize(json);
        var ours = ProjectFile.Deserialize(ProjectFile.Serialize(@base));
        var merged = ModelMerge.Merge(@base, ours, theirs).Model;
        var mergedJson = ProjectFile.Serialize(merged);
        Assert.Contains(sat.Id, mergedJson);
        Assert.Equal(wheel.Id, merged.Find(part.Id)!.TypeRef);
        var again = ProjectFile.Deserialize(mergedJson, [lib]);
        Assert.Equal(load.Id, ((Relationship)again.Find(sat.Id)!).Target.Id);
        Assert.True(again.IsLibraryElement(((Relationship)again.Find(sat.Id)!).Target));

        // A model with its libraries mounted merges its own content only.
        var live = ProjectFile.Deserialize(json, [lib]);
        var fromLive = ModelMerge.Merge(@base, ours, live).Model;
        Assert.DoesNotContain("Standard parts", ProjectFile.Serialize(fromLive));
        Assert.Contains(sat.Id, ProjectFile.Serialize(fromLive));
    }

    [Fact]
    public void Models_Without_Libraries_Are_Unchanged()
    {
        var m = SampleModels.Vehicle();
        var json = ProjectFile.Serialize(m);
        Assert.Equal(json, ProjectFile.Serialize(m, includeLibraries: true));
        Assert.Equal(json, ProjectFile.WithLibraries(json, []));
        Assert.False(ProjectFile.Deserialize(json).HasLibraries);
        Assert.Empty(ProjectFile.Deserialize(json).MissingReferences);
    }
}
