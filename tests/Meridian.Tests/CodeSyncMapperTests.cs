using Meridian.Core.CodeSync;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization.CodeSync;

namespace Meridian.Tests;

public class CodeSyncMapperTests
{
    // The package tree as the code has it (no collapsed root namespace, no derived package dependencies): those have tests of their own.
    // Code identifiers as names and every usage dependency: these tests are about the mapping itself (readable names,
    // subsystems and the default of leaving usage dependencies out have tests of their own in CodeSyncArchitectureTests).
    private static CodeSyncOptions Options(string commit = "c1") => new()
    {
        RepoId = "repo-1", Commit = commit, RootPackage = "Software::Code", CollapseRootNamespace = false, PackageDependencies = false,
        ReadableNames = false, IncludeUsageDependencies = true,
    };

    private static CodeSyncChanges Sync(ModelRepository m, IReadOnlyList<CodeFile> files, string commit = "c1", CodeSyncOptions? options = null) =>
        CodeModelMapper.Apply(m, new CSharpCodeReader().Read(files), (options ?? Options()) with { Commit = commit });

    private static Element? Sym(ModelRepository m, string symbol) =>
        m.AllElements.SingleOrDefault(e => e.Get(CodeFieldKeys.Symbol) == symbol && m.Contains(e));

    private static Element S(ModelRepository m, string symbol) => Sym(m, symbol) ?? throw new Xunit.Sdk.XunitException($"no element for {symbol}");

    private static IReadOnlyList<CodeFile> Replace(IReadOnlyList<CodeFile> files, string path, Func<string, string> edit) =>
        files.Select(f => f.Path == path ? f with { Content = edit(f.Content) } : f).ToList();

    [Fact]
    public void Elements_from_submodule_code_carry_the_submodule_without_a_bump_counting_as_a_change()
    {
        var m = new ModelRepository("Mission");
        var files = new List<CodeFile>
        {
            new("src/Eps.cs", "namespace Sat; public class Eps { public Battery Main { get; set; } }"),
            new("libs/core/Battery.cs", "namespace Sat; public class Battery { public double Capacity { get; set; } }"),
        };
        var v1 = Options() with { Submodules = [new CodeSubmodulePin("libs/core", "1111111111111111111111111111111111111111")] };
        Sync(m, files, options: v1);
        Assert.Equal("libs/core@1111111111111111111111111111111111111111", S(m, "Sat.Battery").Get(CodeFieldKeys.Submodule));
        Assert.Equal("libs/core@1111111111111111111111111111111111111111", S(m, "Sat.Battery.Capacity").Get(CodeFieldKeys.Submodule));
        Assert.Equal("", S(m, "Sat.Eps").Get(CodeFieldKeys.Submodule));

        var v2 = Options() with { Submodules = [new CodeSubmodulePin("libs/core", "2222222222222222222222222222222222222222")] };
        var bump = Sync(m, files, "c2", v2);
        Assert.True(bump.IsEmpty);
        Assert.Equal("libs/core@2222222222222222222222222222222222222222", S(m, "Sat.Battery").Get(CodeFieldKeys.Submodule));

        // The file moves out of the submodule (vendored into the repository): the field goes.
        Sync(m, [files[0], new("src/Battery.cs", files[1].Content)], "c3", v2);
        Assert.Equal("", S(m, "Sat.Battery").Get(CodeFieldKeys.Submodule));
    }

    [Fact]
    public void First_sync_builds_packages_blocks_properties_and_relationships()
    {
        var m = new ModelRepository("Mission");
        var changes = Sync(m, CubeSatCode.Files);
        Assert.Empty(changes.Removed);
        Assert.Empty(changes.Changed);
        Assert.Empty(changes.Warnings);

        var code = m.Root.OwnedElements.Single(e => e.Name == "Software").OwnedElements.Single(e => e.Name == "Code");
        Assert.Equal(ElementKind.Package, code.Kind);
        Assert.Contains(code.Id, changes.Added);
        var sat = S(m, "ns:Sat");
        Assert.Same(code, sat.Owner);
        Assert.Equal(["Comms", "Power", "Thermal"], sat.OwnedElements.Where(e => e.Kind == ElementKind.Package).Select(e => e.Name).Order());
        var power = S(m, "ns:Sat.Power");
        Assert.Same(sat, power.Owner);

        var eps = S(m, "Sat.Power.Eps");
        Assert.Equal((ElementKind.Block, "Eps", "Electrical power system."), (eps.Kind, eps.Name, eps.Documentation));
        Assert.Same(power, eps.Owner);
        Assert.Equal(ElementKind.InterfaceBlock, S(m, "Sat.Power.IPowerSource").Kind);
        Assert.Equal(ElementKind.InterfaceBlock, S(m, "Sat.ISubsystem").Kind);
        Assert.Equal(ElementKind.Block, S(m, "Sat.HealthReport").Kind);
        var mode = S(m, "Sat.Mode");
        Assert.Equal(ElementKind.Enumeration, mode.Kind);
        Assert.Equal(["Safe", "Nominal", "Science"], mode.OwnedElements.Select(e => e.Name));
        Assert.All(mode.OwnedElements, l => Assert.Equal(ElementKind.EnumerationLiteral, l.Kind));

        // Parts, references and values.
        var battery = S(m, "Sat.Power.Battery");
        var panel = S(m, "Sat.Power.SolarPanel");
        var pBattery = S(m, "Sat.Power.Eps.Battery");
        Assert.Equal((ElementKind.Property, battery.Id, "1"), (pBattery.Kind, pBattery.TypeRef, pBattery.Multiplicity));
        Assert.Equal((panel.Id, "*"), (S(m, "Sat.Power.Eps.Panels").TypeRef, S(m, "Sat.Power.Eps.Panels").Multiplicity));
        var backup = S(m, "Sat.Power.Eps.Backup");
        Assert.Equal((S(m, "Sat.Power.IPowerSource").Id, "0..1"), (backup.TypeRef, backup.Multiplicity));
        Assert.Equal("double", S(m, "Sat.Power.Battery.Capacity").TypeRef);
        Assert.Equal(mode.Id, S(m, "Sat.Satellite.Mode").TypeRef);
        var output = S(m, "Sat.Power.Battery.Output()");
        Assert.Equal((ElementKind.Operation, "() : double", "public"), (output.Kind, output.Get(FieldKeys.Signature), output.Get(FieldKeys.Visibility)));
        Assert.Equal(["Battery", "Panels"], AutoDiagrammer.Parts(m, eps).Select(p => p.Name));

        Relationship R(string symbol) => Assert.IsType<Relationship>(S(m, symbol));
        var comp = R("comp:Sat.Power.Eps->Sat.Power.Battery");
        Assert.Equal((ElementKind.Composition, eps, battery, "1"), (comp.Kind, comp.Source, comp.Target, comp.Multiplicity));
        Assert.Equal("*", R("comp:Sat.Power.Eps->Sat.Power.SolarPanel").Multiplicity);
        Assert.Equal(ElementKind.Association, R("ref:Sat.Power.Eps->Sat.Power.IPowerSource").Kind);
        Assert.Equal(ElementKind.Realization, R("real:Sat.Power.Battery->Sat.Power.IPowerSource").Kind);
        Assert.Equal(ElementKind.Realization, R("real:Sat.Comms.Radio->Sat.ISubsystem").Kind);
        Assert.Equal(ElementKind.Dependency, R("dep:Sat.Satellite->Sat.HealthReport").Kind);
        Assert.Equal(ElementKind.Dependency, R("dep:Sat.Comms.Radio->Sat.Comms.PacketQueue").Kind);
        Assert.Null(Sym(m, "dep:Sat.Power.Eps->Sat.Power.Battery"));   // already a part: no duplicate dependency

        // Every element from code carries the code keys.
        foreach (var e in m.AllElements.Where(e => e.Get(CodeFieldKeys.Symbol).Length > 0))
        {
            Assert.Equal("repo-1", e.Get(CodeFieldKeys.Repo));
            Assert.Equal("c1", e.Get(CodeFieldKeys.Commit));
            if (!e.Get(CodeFieldKeys.Symbol).StartsWith("ns:")) Assert.NotEmpty(e.Get(CodeFieldKeys.File));
            Assert.Contains(e.Id, changes.Added);
        }
        Assert.Equal("src/Power/Power.cs", battery.Get(CodeFieldKeys.File));
        Assert.Equal("16", battery.Get(CodeFieldKeys.Line));
    }

    [Fact]
    public void Second_sync_with_the_same_code_changes_nothing()
    {
        var m = new ModelRepository();
        Sync(m, CubeSatCode.Files);
        var undo = m.History.UndoDescription;
        var count = m.Count;
        var again = Sync(m, CubeSatCode.Files, "c2");
        Assert.True(again.IsEmpty, string.Join(", ", again.Changed.Select(id => m.Find(id)?.ToString())));
        Assert.Equal(count, m.Count);
        Assert.Equal(undo, m.History.UndoDescription);   // no undo step at all
        Assert.Equal("c1", S(m, "Sat.Power.Battery").Get(CodeFieldKeys.Commit));
    }

    [Fact]
    public void Renaming_a_member_replaces_the_property_and_stamps_the_commit()
    {
        var m = new ModelRepository();
        Sync(m, CubeSatCode.Files);
        var old = S(m, "Sat.Power.Battery.Capacity");
        var files = Replace(CubeSatCode.Files, "src/Power/Power.cs", s => s.Replace("Capacity", "CapacityWh"));
        var changes = Sync(m, files, "c2");
        Assert.Contains(old.Id, changes.Removed);
        Assert.False(m.Contains(old));
        var renamed = S(m, "Sat.Power.Battery.CapacityWh");
        Assert.Equal(["CapacityWh"], changes.Added.Select(id => m.Find(id)!.Name));
        Assert.Equal("c2", renamed.Get(CodeFieldKeys.Commit));
        Assert.Same(S(m, "Sat.Power.Battery"), renamed.Owner);

        // A doc comment edit updates the block in place.
        files = Replace(files, "src/Power/Power.cs", s => s.Replace("Li-ion battery pack.", "Li-ion battery pack, 40 Wh."));
        changes = Sync(m, files, "c3");
        var battery = S(m, "Sat.Power.Battery");
        Assert.Equal([battery.Id], changes.Changed);
        Assert.Equal(("Li-ion battery pack, 40 Wh.", "c3"), (battery.Documentation, battery.Get(CodeFieldKeys.Commit)));
    }

    [Fact]
    public void Moving_a_class_to_another_namespace_moves_the_block_and_drops_empty_packages()
    {
        var m = new ModelRepository();
        Sync(m, CubeSatCode.Files);
        var heater = S(m, "Sat.Thermal.Heater");
        var watts = S(m, "Sat.Thermal.Heater.Watts");
        var comp = S(m, "comp:Sat.Thermal.ThermalControl->Sat.Thermal.Heater");
        var power = S(m, "ns:Sat.Power");
        var req = m.Create(m.Root, ElementKind.Requirement, "Survival heating");
        var satisfy = m.Connect(ElementKind.Satisfy, heater, req);

        // Heater moves from Sat.Thermal to Sat.Power: same element, new symbol, new owner; members and relationships follow.
        var files = Replace(CubeSatCode.Files, "src/Thermal/Thermal.cs", s => s.Replace("public class Heater { public double Watts { get; set; } }", "")
            .Replace("List<Heater>", "List<Sat.Power.Heater>"));
        files = Replace(files, "src/Power/Power.cs", s => s.Replace("public interface IPowerSource", "public class Heater { public double Watts { get; set; } }\n    public interface IPowerSource"));
        var changes = Sync(m, files, "c2");
        Assert.Same(heater, S(m, "Sat.Power.Heater"));
        Assert.Same(power, heater.Owner);
        Assert.Same(watts, S(m, "Sat.Power.Heater.Watts"));
        Assert.Same(comp, S(m, "comp:Sat.Thermal.ThermalControl->Sat.Power.Heater"));
        Assert.True(m.Contains(satisfy));
        Assert.Contains(heater.Id, changes.Changed);
        Assert.Empty(changes.Added);
        Assert.Empty(changes.Removed);
        Assert.Equal("c2", heater.Get(CodeFieldKeys.Commit));
        Assert.True(Sync(m, files, "c3").IsEmpty);

        // A whole namespace renamed: its blocks move to the new package and the emptied package is removed.
        var commsPkg = S(m, "ns:Sat.Comms");
        var radio = S(m, "Sat.Comms.Radio");
        files = Replace(files, "src/Comms/Radio.cs", s => s.Replace("namespace Sat.Comms;", "namespace Sat.Radio;"));
        files = Replace(files, "src/Satellite.cs", s => s.Replace("using Sat.Comms;", "").Replace("public Radio Radio", "public Sat.Radio.Radio Radio")
            .Replace("new Packet(", "new Sat.Radio.Packet("));
        var names = m.AllElements.ToDictionary(e => e.Id, e => e.Get(CodeFieldKeys.Symbol));
        changes = Sync(m, files, "c4");
        Assert.False(m.Contains(commsPkg));
        Assert.Equal([commsPkg.Get(CodeFieldKeys.Symbol)], changes.Removed.Select(id => names[id]));
        Assert.Same(radio, S(m, "Sat.Radio.Radio"));
        Assert.Same(S(m, "ns:Sat.Radio"), radio.Owner);

        // A type whose owner was changed by hand is put back in its namespace package.
        m.Move(radio, power);
        changes = Sync(m, files, "c5");
        Assert.Same(S(m, "ns:Sat.Radio"), radio.Owner);
        Assert.Equal([radio.Id], changes.Changed);
    }

    [Fact]
    public void Deleting_a_class_removes_its_block_or_marks_it()
    {
        var m = new ModelRepository();
        Sync(m, CubeSatCode.Files);
        var antenna = S(m, "Sat.Comms.Antenna");
        var gain = S(m, "Sat.Comms.Antenna.Gain");
        var comp = S(m, "comp:Sat.Comms.Radio->Sat.Comms.Antenna");
        var files = Replace(CubeSatCode.Files, "src/Comms/Radio.cs", s => s.Replace("public class Antenna { public double Gain { get; set; } }", ""));

        var keep = m.Count;
        var marked = Sync(m, files, "c2", Options() with { RemoveDeleted = false });
        Assert.Equal(keep, m.Count);
        Assert.Empty(marked.Removed);
        Assert.Equal("true", antenna.Get(CodeModelMapper.RemovedKey));
        Assert.Contains(antenna.Id, marked.Changed);
        Assert.Contains(gain.Id, marked.Changed);

        var removed = Sync(m, files, "c3");
        Assert.False(m.Contains(antenna));
        Assert.False(m.Contains(gain));
        Assert.False(m.Contains(comp));
        Assert.Contains(antenna.Id, removed.Removed);
        Assert.Contains(comp.Id, removed.Removed);
        // The member that used it is now a plain value property with the unresolved type name.
        Assert.Equal("Antenna", S(m, "Sat.Comms.Radio.Antenna").TypeRef);

        // Putting the class back clears nothing stale and recreates it.
        var back = Sync(m, CubeSatCode.Files, "c4");
        Assert.NotNull(Sym(m, "Sat.Comms.Antenna"));
        Assert.Equal(S(m, "Sat.Comms.Antenna").Id, S(m, "Sat.Comms.Radio.Antenna").TypeRef);
        Assert.Contains(S(m, "Sat.Comms.Antenna").Id, back.Added);
    }

    [Fact]
    public void Marked_elements_are_unmarked_when_their_code_returns()
    {
        var m = new ModelRepository();
        Sync(m, CubeSatCode.Files);
        var heater = S(m, "Sat.Thermal.Heater");
        var files = Replace(CubeSatCode.Files, "src/Thermal/Thermal.cs", s => s.Replace("public class Heater { public double Watts { get; set; } }", ""));
        Sync(m, files, "c2", Options() with { RemoveDeleted = false });
        Assert.Equal("true", heater.Get(CodeModelMapper.RemovedKey));
        Assert.True(Sync(m, files, "c3", Options() with { RemoveDeleted = false }).IsEmpty);
        var back = Sync(m, CubeSatCode.Files, "c4");
        Assert.Equal("", heater.Get(CodeModelMapper.RemovedKey));
        Assert.Contains(heater.Id, back.Changed);
    }

    [Fact]
    public void User_elements_fields_and_relationships_are_left_alone()
    {
        var m = new ModelRepository();
        Sync(m, CubeSatCode.Files);
        var power = S(m, "ns:Sat.Power");
        var battery = S(m, "Sat.Power.Battery");
        var reqs = m.Create(m.Root, ElementKind.Package, "Requirements");
        var req = m.Create(reqs, ElementKind.Requirement, "Eclipse energy");
        var satisfy = m.Connect(ElementKind.Satisfy, battery, req);
        var mine = m.Create(power, ElementKind.Block, "HandMadeBlock");
        var note = m.Create(battery, ElementKind.Property, "userNote");
        m.Set(battery, FieldKeys.Stereotypes, "hardware");
        m.Set(battery, "tag:supplier", "ACME");
        var diagram = m.CreateDiagram(power, DiagramType.BlockDefinition, "Power by hand");
        m.AddNode(diagram, battery, new Bounds(0, 0, 100, 60));

        var files = Replace(CubeSatCode.Files, "src/Power/Power.cs", s => s.Replace("Li-ion battery pack.", "Li-ion pack."));
        var changes = Sync(m, files, "c2");
        Assert.Equal([battery.Id], changes.Changed);
        foreach (var e in new[] { reqs, req, satisfy, mine, note, diagram }) Assert.True(m.Contains(e));
        Assert.Equal(("hardware", "ACME"), (battery.Get(FieldKeys.Stereotypes), battery.Get("tag:supplier")));
        Assert.Same(battery, satisfy.Source);
        Assert.Single(diagram.Nodes);

        // The class is deleted in code, but the block holds hand-made content: it is kept and marked, never deleted.
        files = Replace(files, "src/Power/Power.cs", s => s.Replace("public Battery Battery { get; } = new();", "")
            .Replace("""
                /// <summary>Li-ion pack.</summary>
                    public class Battery : IPowerSource
                    {
                        public double Capacity { get; set; } = 40;
                        public double StateOfCharge { get; set; }
                        public double Output() => Capacity * StateOfCharge;
                    }
                """, ""));
        files = Replace(files, "src/Satellite.cs", s => s.Replace("Eps.Battery.StateOfCharge", "0"));
        Assert.DoesNotContain("class Battery", files[1].Content);
        changes = Sync(m, files, "c3");
        Assert.True(m.Contains(battery));
        Assert.True(m.Contains(satisfy));
        Assert.True(m.Contains(note));
        Assert.Equal("true", battery.Get(CodeModelMapper.RemovedKey));
        Assert.Contains(changes.Warnings, w => w.Contains("Battery") && w.Contains("marked removed"));
        Assert.Single(diagram.Nodes);
        // Its own code members (no hand-made content) are still removed.
        Assert.Null(Sym(m, "Sat.Power.Battery.Capacity"));
        Assert.True(m.Contains(mine));
    }

    [Fact]
    public void Undo_reverts_the_whole_sync_in_one_step()
    {
        var m = new ModelRepository();
        var before = m.Count;
        Sync(m, CubeSatCode.Files);
        Assert.True(m.Count > before + 40);
        Assert.StartsWith("Code sync", m.History.UndoDescription);
        m.History.Undo();
        Assert.Equal(before, m.Count);
        Assert.False(m.History.CanUndo);
        m.History.Redo();
        Assert.NotNull(Sym(m, "Sat.Power.Battery"));

        var files = Replace(CubeSatCode.Files, "src/Power/Power.cs", s => s.Replace("Capacity", "Energy"));
        var snapshot = m.AllElements.Select(e => e.Id).Order().ToList();
        Sync(m, files, "c2");
        m.History.Undo();
        Assert.Equal(snapshot, m.AllElements.Select(e => e.Id).Order().ToList());
        Assert.NotNull(Sym(m, "Sat.Power.Battery.Capacity"));
    }

    [Fact]
    public void A_class_that_becomes_an_interface_is_replaced()
    {
        var m = new ModelRepository();
        Sync(m, CubeSatCode.Files);
        var old = S(m, "Sat.Thermal.Heater");
        var files = Replace(CubeSatCode.Files, "src/Thermal/Thermal.cs", s => s.Replace("public class Heater { public double Watts { get; set; } }", "public interface Heater { double Watts { get; } }"));
        var changes = Sync(m, files, "c2");
        Assert.False(m.Contains(old));
        var now = S(m, "Sat.Thermal.Heater");
        Assert.Equal(ElementKind.InterfaceBlock, now.Kind);
        Assert.Contains(now.Id, changes.Added);
        Assert.Equal(ElementKind.Association, S(m, "ref:Sat.Thermal.ThermalControl->Sat.Thermal.Heater").Kind);
        Assert.Null(Sym(m, "comp:Sat.Thermal.ThermalControl->Sat.Thermal.Heater"));
        Assert.True(Sync(m, files, "c3").IsEmpty);
    }

    [Fact]
    public void Non_public_members_are_skipped_unless_asked_but_still_count_as_usage()
    {
        var files = new List<CodeFile>
        {
            new("a.cs", """
                namespace N;
                public class Cache { }
                public class Store { private Cache _cache = new(); internal int Size; public int Count; protected void Flush() { } }
                """),
        };
        var m = new ModelRepository();
        Sync(m, files);
        Assert.Equal(["Count"], S(m, "N.Store").OwnedElements.Where(e => !e.IsRelationship).Select(e => e.Name));
        Assert.Equal(ElementKind.Dependency, S(m, "dep:N.Store->N.Cache").Kind);

        var all = new ModelRepository();
        Sync(all, files, options: Options() with { IncludeNonPublic = true });
        Assert.Equal(["_cache", "Size", "Count", "Flush"], S(all, "N.Store").OwnedElements.Where(e => !e.IsRelationship).Select(e => e.Name));
        Assert.Equal(("private", "package"), (S(all, "N.Store._cache").Get(FieldKeys.Visibility), S(all, "N.Store.Size").Get(FieldKeys.Visibility)));
        Assert.NotNull(Sym(all, "comp:N.Store->N.Cache"));
        Assert.Null(Sym(all, "dep:N.Store->N.Cache"));

        var noDeps = new ModelRepository();
        Sync(noDeps, CubeSatCode.Files, options: Options() with { IncludeUsageDependencies = false });
        Assert.DoesNotContain(noDeps.Relationships, r => r.Kind == ElementKind.Dependency);
    }

    [Fact]
    public void Nested_types_live_in_their_outer_block()
    {
        var m = new ModelRepository();
        Sync(m, [new("a.cs", "namespace N; public class Outer { public Inner In { get; } = new(); public class Inner { } public enum State { A, B } } public interface IHost { public class Nested { } }")]);
        var outer = S(m, "N.Outer");
        Assert.Same(outer, S(m, "N.Outer.Inner").Owner);
        Assert.Same(outer, S(m, "N.Outer.State").Owner);
        Assert.Same(S(m, "ns:N"), S(m, "N.IHost.Nested").Owner);   // interface blocks cannot own blocks
        Assert.NotNull(Sym(m, "comp:N.Outer->N.Outer.Inner"));
    }

    [Fact]
    public void Other_repositories_and_their_elements_are_independent()
    {
        var m = new ModelRepository();
        Sync(m, CubeSatCode.Files);
        var other = CodeModelMapper.Apply(m, new CSharpCodeReader().Read([new("x.cs", "namespace Sat.Power; public class Battery { }")]),
            new CodeSyncOptions { RepoId = "repo-2", Commit = "z", RootPackage = "Ground" });
        Assert.Empty(other.Removed);
        Assert.Equal(2, m.AllElements.Count(e => e.Get(CodeFieldKeys.Symbol) == "Sat.Power.Battery"));
        Assert.True(Sync(m, CubeSatCode.Files).IsEmpty);
    }

    [Fact]
    public void End_to_end_cubesat_code_is_diagrammed_automatically()
    {
        var m = new ModelRepository("CubeSat");
        var code = new CSharpCodeReader().Read(CubeSatCode.Files);
        Assert.Empty(code.Warnings);
        var changes = CodeModelMapper.Apply(m, code, new CodeSyncOptions { RepoId = "gh:cubesat", Commit = "abc123", RootPackage = "Flight Software", CollapseRootNamespace = false });
        Assert.Empty(changes.Warnings);
        var root = m.Root.OwnedElements.Single(e => e.Name == "Flight Software");

        var plan = AutoDiagrammer.Plan(m, new AutoDiagramOptions { Scope = root });
        Assert.Contains(plan.Diagrams, d => d.Type == DiagramType.Package);
        foreach (var ns in new[] { "ns:Sat.Power", "ns:Sat.Comms", "ns:Sat.Thermal", "ns:Sat" })
            Assert.Contains(plan.Diagrams, d => d.Type == DiagramType.BlockDefinition && ReferenceEquals(d.Owner, S(m, ns)));
        foreach (var block in new[] { "Sat.Satellite", "Sat.Power.Eps", "Sat.Comms.Radio", "Sat.Thermal.ThermalControl" })
            Assert.Contains(plan.Diagrams, d => d.Type == DiagramType.InternalBlock && ReferenceEquals(d.Owner, S(m, block)));
        Assert.DoesNotContain(plan.Diagrams, d => d.Type == DiagramType.InternalBlock && ReferenceEquals(d.Owner, S(m, "Sat.Power.Battery")));
        var powerBdd = plan.Diagrams.Single(d => d.Type == DiagramType.BlockDefinition && ReferenceEquals(d.Owner, S(m, "ns:Sat.Power")));
        Assert.Contains(S(m, "Sat.Power.Battery"), powerBdd.Elements);
        Assert.Contains(S(m, "Sat.Power.IPowerSource"), powerBdd.Elements);

        var result = AutoDiagrammer.Apply(m, plan);
        Assert.Equal(plan.Diagrams.Count, result.Created.Count);
        var bdd = result.Created.Single(d => d.DiagramType == DiagramType.BlockDefinition && ReferenceEquals(d.Owner, S(m, "ns:Sat.Power")));
        Assert.Contains(bdd.Edges, x => x.Relationship.Kind == ElementKind.Composition);
        Assert.Contains(bdd.Edges, x => x.Relationship.Kind == ElementKind.Realization);
        var ibd = result.Created.Single(d => d.DiagramType == DiagramType.InternalBlock && ReferenceEquals(d.Owner, S(m, "Sat.Power.Eps")));
        Assert.Equal(["Battery", "Panels"], ibd.Nodes.Select(n => n.Element.Name).Order());

        // A later sync that deletes a shown class keeps every diagram.
        var diagrams = m.Diagrams.Count();
        var files = Replace(CubeSatCode.Files, "src/Thermal/Thermal.cs", s => s.Replace("public class Heater { public double Watts { get; set; } }", "")
            .Replace("public List<Heater> Heaters { get; } = new();", ""));
        var later = CodeModelMapper.Apply(m, new CSharpCodeReader().Read(files), new CodeSyncOptions { RepoId = "gh:cubesat", Commit = "def456", RootPackage = "Flight Software", CollapseRootNamespace = false });
        Assert.Null(Sym(m, "Sat.Thermal.Heater"));
        Assert.Equal(diagrams, m.Diagrams.Count());
        Assert.NotEmpty(later.Removed);
    }
}
