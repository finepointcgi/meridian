using Meridian.Core.CodeSync;
using Meridian.Core.CodeSync.Conformance;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Validation;
using Meridian.Serialization.CodeSync;

namespace Meridian.Tests;

/// <summary>
/// Fixes found by syncing Meridian's own repository: overloads are not duplicate names (MER-002), the common root
/// namespace is collapsed, a package overview with code-derived «use» dependencies, and IBDs only for blocks with real
/// internal structure.
/// </summary>
public class CodeSyncImportFixesTests
{
    // Code identifiers as names: these tests are about the package tree and diagrams, not the readable names (CodeSyncReadableTests).
    private static CodeSyncOptions Options(string root = "Meridian", string commit = "c1") => new() { RepoId = "repo-1", Commit = commit, RootPackage = root, ReadableNames = false };

    private static CodeSyncChanges Sync(ModelRepository m, IReadOnlyList<CodeFile> files, CodeSyncOptions options) =>
        CodeModelMapper.Apply(m, new CSharpCodeReader().Read(files), options);

    private static Element S(ModelRepository m, string symbol) =>
        m.AllElements.SingleOrDefault(e => e.Get(CodeFieldKeys.Symbol) == symbol && m.Contains(e)) ?? throw new Xunit.Sdk.XunitException($"no element for {symbol}");

    private static Element? Sym(ModelRepository m, string symbol) =>
        m.AllElements.SingleOrDefault(e => e.Get(CodeFieldKeys.Symbol) == symbol && m.Contains(e));

    /// <summary>Meridian-shaped code: one root namespace "Meridian" with Core, Data and Web below it.</summary>
    private static readonly IReadOnlyList<CodeFile> MeridianLike =
    [
        new("src/Core/Model.cs", """
            namespace Meridian.Core
            {
                public class Element { public string Name { get; set; } }
                public class ModelRepository
                {
                    public Element Root { get; set; }
                    public Element Create(string name) => null;
                    public Element Create(string name, int kind) => null;
                    public Element Create(Element owner, string name) => null;
                    public void Save(Meridian.Data.Store store) { }
                }
            }
            namespace Meridian.Core.Validation
            {
                public class ModelValidator { public void Validate(Meridian.Core.ModelRepository m) { } }
            }
            """),
        new("src/Data/Store.cs", """
            namespace Meridian.Data
            {
                public class Store { public void Load(Meridian.Core.ModelRepository m) { } }
            }
            """),
        new("src/Web/App.cs", """
            namespace Meridian.Web
            {
                public class Workspace
                {
                    public Meridian.Core.ModelRepository Model { get; set; }
                    public Meridian.Data.Store Store { get; set; }
                }
            }
            """),
        new("src/Meridian.cs", """
            namespace Meridian
            {
                public class Version { public string Text { get; set; } }
            }
            """),
    ];

    // ================================================================ MER-002 and overloads

    [Fact]
    public void Overloads_are_not_duplicate_names_but_identical_parameter_lists_are()
    {
        var m = new ModelRepository("M");
        var b = m.Create(m.Root, ElementKind.Block, "Repo");
        Element Op(string sig)
        {
            var o = m.Create(b, ElementKind.Operation, "Create");
            if (sig.Length > 0) m.Set(o, FieldKeys.Signature, sig);
            return o;
        }
        Op("");
        Op("(name : String)");
        Op("(name : String, kind : Integer) : Element");
        Op("(out name : String)");
        Op("(map : Dictionary<String, List<Integer>>)");
        Assert.DoesNotContain(new ModelValidator().Validate(m), i => i.RuleId == "MER-002");

        // Same parameter types: names and the return type do not tell overloads apart.
        var dup = Op("(other : String) : Boolean");
        var issue = Assert.Single(new ModelValidator().Validate(m), i => i.RuleId == "MER-002");
        Assert.Same(dup, issue.Element);
        Assert.Contains("Create(String)", issue.Message);

        // Other kinds still need unique names.
        m.Create(b, ElementKind.Property, "size");
        m.Create(b, ElementKind.Property, "size");
        Assert.Equal(2, new ModelValidator().Validate(m).Count(i => i.RuleId == "MER-002"));
    }

    [Fact]
    public void Repeated_Labels_Inside_Activities_And_Interactions_Are_Not_Duplicates()
    {
        var m = new ModelRepository("M");
        var act = m.Create(m.Root, ElementKind.Activity, "Run");
        m.Create(act, ElementKind.ActivityFinalNode, "return Fail()");
        m.Create(act, ElementKind.ActivityFinalNode, "return Fail()");
        m.Create(act, ElementKind.Action, "Charge()");
        m.Create(act, ElementKind.Action, "Charge()");
        var seq = m.Create(m.Root, ElementKind.Interaction, "Flow");
        var frag = m.Create(seq, ElementKind.CombinedFragment, "alt");
        m.Create(frag, ElementKind.InteractionOperand, "");
        m.Create(seq, ElementKind.Lifeline, "b : Battery");
        m.Create(seq, ElementKind.Lifeline, "b : Battery");
        Assert.DoesNotContain(new ModelValidator().Validate(m), i => i.RuleId == "MER-002");
    }

    [Theory]
    [InlineData("", "()")]
    [InlineData("()", "()")]
    [InlineData("(a : int, b : string) : bool", "(int,string)")]
    [InlineData("(a : int,b:string)", "(int,string)")]
    [InlineData("(out a : int, inout b : string, in c : x)", "(out int,inout string,x)")]
    [InlineData("(m : Dictionary<string, List<int>>, f : Func<(int, int), bool>)", "(Dictionary<string,List<int>>,Func<(int,int),bool>)")]
    [InlineData("(t : Sat::Power::Battery)", "(Sat::Power::Battery)")]
    [InlineData("(self, x)", "(?,?)")]
    public void Overload_keys_keep_parameter_directions_and_types(string signature, string key)
    {
        var m = new ModelRepository("M");
        var op = m.Create(m.Create(m.Root, ElementKind.Block, "B"), ElementKind.Operation, "F");
        if (signature.Length > 0) m.Set(op, FieldKeys.Signature, signature);
        Assert.Equal(key, ModelValidator.OverloadKey(op));
    }

    [Fact]
    public void Code_sync_overloads_and_the_rest_of_a_synced_model_pass_MER_002()
    {
        var m = new ModelRepository("Meridian");
        Sync(m, MeridianLike, Options());
        Assert.Equal(3, S(m, "Meridian.Core.ModelRepository").OwnedElements.Count(e => e.Name == "Create"));
        Assert.DoesNotContain(new ModelValidator().Validate(m), i => i.RuleId == "MER-002");
    }

    // ================================================================ collapsing the common root namespace

    [Fact]
    public void A_root_namespace_named_like_the_model_package_is_collapsed()
    {
        var m = new ModelRepository("Project");
        var changes = Sync(m, MeridianLike, Options());
        Assert.Empty(changes.Warnings);
        var root = CodeModelMapper.FindRootPackage(m, "Meridian")!;
        Assert.Null(Sym(m, "ns:Meridian"));
        Assert.Equal(["Core", "Data", "Web"], root.OwnedElements.Where(e => e.Kind == ElementKind.Package).Select(e => e.Name).Order());
        Assert.Same(root, S(m, "ns:Meridian.Core").Owner);
        Assert.Same(S(m, "ns:Meridian.Core"), S(m, "ns:Meridian.Core.Validation").Owner);
        Assert.Same(root, S(m, "Meridian.Version").Owner);   // a type of the collapsed namespace itself
        Assert.Equal("Meridian::Core::ModelRepository", S(m, "Meridian.Core.ModelRepository").QualifiedName.Replace("Project::", ""));
        Assert.True(Sync(m, MeridianLike, Options(commit: "c2")).IsEmpty);
    }

    [Fact]
    public void A_common_root_namespace_is_collapsed_unless_types_live_outside_it_or_the_option_is_off()
    {
        // All types share "Sat", the package is "Code": collapsed too.
        var m = new ModelRepository("M");
        Sync(m, CubeSatCode.Files, Options("Code"));
        Assert.Null(Sym(m, "ns:Sat"));
        Assert.Equal("Code", S(m, "ns:Sat.Power").Owner!.Name);

        // A type in the global namespace keeps "Sat" (its name is not the package's).
        var m2 = new ModelRepository("M");
        Sync(m2, [.. CubeSatCode.Files, new("src/Global.cs", "public class Loose { }")], Options("Code"));
        Assert.Same(S(m2, "ns:Sat"), S(m2, "ns:Sat.Power").Owner);

        // One outlier beside a namespace named like the model package that holds most types: still collapsed, the
        // outlier keeps its own package.
        var m3 = new ModelRepository("M");
        Sync(m3, [.. MeridianLike, new("src/Other.cs", "namespace Tools { public class Cli { } }")], Options());
        Assert.Equal("Meridian", S(m3, "ns:Meridian.Core").Owner!.Name);
        Assert.Null(Sym(m3, "ns:Meridian"));
        Assert.Equal("Meridian", S(m3, "ns:Tools").Owner!.Name);

        // Two root namespaces of similar size: nothing to collapse.
        var m5 = new ModelRepository("M");
        Sync(m5, [.. MeridianLike, new("src/Other.cs", "namespace Tools { public class A { } public class B { } public class C { } public class D { } public class E { } public class F { } }")], Options());
        Assert.Same(S(m5, "ns:Meridian"), S(m5, "ns:Meridian.Core").Owner);

        // Off: the namespace tree as is.
        var m4 = new ModelRepository("M");
        Sync(m4, MeridianLike, Options() with { CollapseRootNamespace = false });
        Assert.Equal("Meridian", S(m4, "ns:Meridian").Owner!.Name);
        Assert.Same(S(m4, "ns:Meridian"), S(m4, "ns:Meridian.Core").Owner);
    }

    [Fact]
    public void A_project_synced_before_collapsing_migrates_by_moving_not_recreating()
    {
        var m = new ModelRepository("Project");
        var old = Options() with { CollapseRootNamespace = false };
        Sync(m, MeridianLike, old);
        var oldRoot = S(m, "ns:Meridian");
        var ids = m.AllElements.Where(e => e.Get(CodeFieldKeys.Symbol).Length > 0 && !ReferenceEquals(e, oldRoot)).ToDictionary(e => e.Get(CodeFieldKeys.Symbol), e => e.Id);
        // Content people added to the old package: a diagram (auto-made, marked) and a hand-made note block.
        var bdd = m.CreateDiagram(oldRoot, DiagramType.BlockDefinition, "Meridian – structure");
        m.Set(bdd, AutoDiagrammer.MarkerKey, AutoDiagrammer.KeyFor(DiagramType.BlockDefinition, oldRoot));
        var note = m.Create(oldRoot, ElementKind.Block, "Hand made");
        var core = S(m, "ns:Meridian.Core");
        var req = m.Create(m.Root, ElementKind.Requirement, "Keep the model");
        m.Connect(ElementKind.Satisfy, core, req);

        var changes = Sync(m, MeridianLike, Options(commit: "c2"));
        var root = CodeModelMapper.FindRootPackage(m, "Meridian")!;
        Assert.Empty(changes.Added);
        Assert.Equal([oldRoot.Id], changes.Removed);   // only the collapsed package goes
        Assert.False(m.Contains(oldRoot));
        foreach (var (symbol, id) in ids)
            Assert.Equal(id, S(m, symbol).Id);   // every element kept its id
        Assert.Same(root, core.Owner);
        Assert.Same(root, S(m, "Meridian.Version").Owner);
        Assert.Same(root, bdd.Owner);
        Assert.Equal(AutoDiagrammer.KeyFor(DiagramType.BlockDefinition, root), bdd.Get(AutoDiagrammer.MarkerKey));
        Assert.Same(root, note.Owner);
        Assert.Contains(core.Id, changes.Changed);

        // Settled: nothing more to do, and switching back moves things back without duplicates.
        Assert.True(Sync(m, MeridianLike, Options(commit: "c3")).IsEmpty);
        var back = Sync(m, MeridianLike, old with { Commit = "c4" });
        Assert.Equal([S(m, "ns:Meridian").Id], back.Added);
        Assert.Same(S(m, "ns:Meridian"), core.Owner);
        Assert.Single(m.AllElements, e => e.Get(CodeFieldKeys.Symbol) == "ns:Meridian.Core");
    }

    [Fact]
    public void A_type_moved_out_of_the_collapsed_namespace_is_moved_not_recreated()
    {
        var m = new ModelRepository("Project");
        Sync(m, MeridianLike, Options());
        var version = S(m, "Meridian.Version");
        var moved = MeridianLike.Select(f => f.Path == "src/Meridian.cs" ? f with { Content = "namespace Meridian.Core { public class Version { public string Text { get; set; } } }" } : f).ToList();
        var changes = Sync(m, moved, Options(commit: "c2"));
        Assert.Same(version, S(m, "Meridian.Core.Version"));
        Assert.Same(S(m, "ns:Meridian.Core"), version.Owner);
        Assert.DoesNotContain(version.Id, changes.Added);
    }

    // ================================================================ package dependencies and the overview

    [Fact]
    public void Package_dependencies_are_derived_from_code_marked_and_kept_in_step()
    {
        var m = new ModelRepository("Project");
        Sync(m, MeridianLike, Options());
        var core = S(m, "ns:Meridian.Core");
        var data = S(m, "ns:Meridian.Data");
        var web = S(m, "ns:Meridian.Web");

        var coreData = Assert.IsType<Relationship>(S(m, "pkgdep:Meridian.Core->Meridian.Data"), exactMatch: false);
        Assert.Equal((ElementKind.Dependency, core, data, "use"), (coreData.Kind, coreData.Source, coreData.Target, coreData.Get(FieldKeys.Stereotypes)));
        Assert.Equal("repo-1", coreData.Get(CodeFieldKeys.Repo));
        Assert.NotNull(Sym(m, "pkgdep:Meridian.Web->Meridian.Core"));
        Assert.NotNull(Sym(m, "pkgdep:Meridian.Web->Meridian.Data"));
        // Core ⇄ Data is a cycle; Web is outside it. Nested Core.Validation is shown (few packages), but its use of its
        // parent is not a dependency between siblings.
        Assert.Equal("true", coreData.Get(CodeModelMapper.CycleKey));
        Assert.Equal("true", S(m, "pkgdep:Meridian.Data->Meridian.Core").Get(CodeModelMapper.CycleKey));
        Assert.Contains("cycle", coreData.Documentation);
        Assert.Equal("", S(m, "pkgdep:Meridian.Web->Meridian.Core").Get(CodeModelMapper.CycleKey));
        Assert.Null(Sym(m, "pkgdep:Meridian.Core.Validation->Meridian.Core"));
        Assert.Equal(4, m.Relationships.Count(r => r.Get(CodeFieldKeys.Symbol).StartsWith(CodeModelMapper.PackageDependencyPrefix, StringComparison.Ordinal)));
        Assert.Equal([core, data, web, S(m, "ns:Meridian.Core.Validation")], CodeModelMapper.OverviewPackages(CodeModelMapper.FindRootPackage(m, "Meridian")!));

        // The cycle is broken: Core no longer uses Data. The dependency goes, the other loses its cycle mark.
        var fixedCode = MeridianLike.Select(f => f.Path == "src/Core/Model.cs" ? f with { Content = f.Content.Replace("public void Save(Meridian.Data.Store store) { }", "") } : f).ToList();
        var changes = Sync(m, fixedCode, Options(commit: "c2"));
        Assert.Contains(coreData.Id, changes.Removed);
        var dataCore = S(m, "pkgdep:Meridian.Data->Meridian.Core");
        Assert.Contains(dataCore.Id, changes.Changed);
        Assert.Equal("", dataCore.Get(CodeModelMapper.CycleKey));
        Assert.Equal("", dataCore.Documentation);
        Assert.True(Sync(m, fixedCode, Options(commit: "c3")).IsEmpty);

        // Off: they are removed like any other code element that is gone.
        Sync(m, fixedCode, Options(commit: "c4") with { PackageDependencies = false });
        Assert.DoesNotContain(m.Relationships, r => r.Get(CodeFieldKeys.Symbol).StartsWith(CodeModelMapper.PackageDependencyPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Architecture_checks_do_not_treat_derived_package_dependencies_as_hand_made()
    {
        // Two layers made by hand around the synced packages; Web may only use Core.
        var m = new ModelRepository("Project");
        var code = new CSharpCodeReader().Read(MeridianLike);
        CodeModelMapper.Apply(m, code, Options());
        var web = S(m, "ns:Meridian.Web");
        var dep = S(m, "pkgdep:Meridian.Web->Meridian.Data");
        foreach (var p in new[] { web, S(m, "ns:Meridian.Core"), S(m, "ns:Meridian.Data") }) m.Set(p, FieldKeys.Stereotypes, "layer");
        m.Set(web, FieldKeys.TagPrefix + ArchitectureConformance.AllowedTag, "Core");
        var violations = ArchitectureConformance.Check(m, code, new ConformanceOptions { RepoId = "repo-1" }).Violations;
        Assert.Contains(violations, v => v.Rule == ConformanceRule.Layering && v.CodeSymbol == "Meridian.Web.Workspace" && v.TargetSymbol == "Meridian.Data.Store");
        Assert.True(ArchitectureConformance.IsCodeMade(dep));

        // The same dependency drawn by hand as «allowed» is a permitted path: the code-made one never is.
        var allowed = m.Connect(ElementKind.Dependency, web, S(m, "ns:Meridian.Data"));
        m.Set(allowed, FieldKeys.Stereotypes, "allowed");
        Assert.DoesNotContain(ArchitectureConformance.Check(m, code, new ConformanceOptions { RepoId = "repo-1" }).Violations, v => v.Rule == ConformanceRule.Layering);
    }

    [Fact]
    public void First_sync_creates_one_package_overview_and_later_syncs_update_it_in_place()
    {
        var m = new ModelRepository("Project");
        var changes = Sync(m, MeridianLike, Options());
        var root = CodeModelMapper.FindRootPackage(m, "Meridian")!;
        var refresh = CodeSyncDiagrams.Refresh(m, changes, createDiagrams: true, codeRoot: root);
        var overview = Assert.Single(m.Diagrams, d => d.DiagramType == DiagramType.Package);
        Assert.Contains(overview, refresh.Created);
        Assert.Equal("Meridian – package overview", overview.Name);
        Assert.Same(root, overview.Owner);
        Assert.Equal(AutoDiagrammer.KeyFor(DiagramType.Package, root), overview.Get(AutoDiagrammer.MarkerKey));
        Assert.Equal(["Core", "Data", "Validation", "Web"], overview.Nodes.Select(n => n.Element.Name).Order());
        Assert.Equal(4, overview.Edges.Count);
        var cycleEdges = overview.Edges.Where(x => x.Relationship.Get(CodeModelMapper.CycleKey) == "true").ToList();
        Assert.Equal(2, cycleEdges.Count);
        Assert.All(cycleEdges, x => Assert.Equal(CodeSyncDiagrams.CycleStroke, x.Style["stroke"]));
        Assert.All(overview.Edges.Except(cycleEdges), x => Assert.False(x.Style.ContainsKey("stroke")));

        // A new namespace: added to the same overview without moving what is there; its dependency is drawn.
        var positions = overview.Nodes.ToDictionary(n => n.Id, n => n.Bounds);
        var more = MeridianLike.Append(new("src/Cli/Cli.cs", "namespace Meridian.Cli { public class Program { public Meridian.Core.ModelRepository Model { get; set; } } }")).ToList();
        var second = Sync(m, more, Options(commit: "c2"));
        var r2 = CodeSyncDiagrams.Refresh(m, second, createDiagrams: true, codeRoot: root);
        Assert.Single(m.Diagrams, d => d.DiagramType == DiagramType.Package);
        Assert.Contains(overview, r2.Updated);
        Assert.Contains(overview.Nodes, n => n.Element.Name == "Cli");
        foreach (var (id, bounds) in positions) Assert.Equal(bounds, overview.FindNode(id)!.Bounds);
        Assert.Contains(overview.Edges, x => x.Relationship.Get(CodeFieldKeys.Symbol) == "pkgdep:Meridian.Cli->Meridian.Core");

        // The namespace goes again: off the overview.
        var third = Sync(m, MeridianLike, Options(commit: "c3"));
        CodeSyncDiagrams.Refresh(m, third, createDiagrams: true, codeRoot: root);
        Assert.DoesNotContain(overview.Nodes, n => n.Element.Name == "Cli");
        Assert.Single(m.Diagrams, d => d.DiagramType == DiagramType.Package);

        // A later Auto-diagram run recognises it (same marker) instead of adding another overview of the package.
        var plan = AutoDiagrammer.Plan(m, new AutoDiagramOptions { Scope = root, Regenerate = true });
        Assert.Contains(plan.Diagrams, p => p.Type == DiagramType.Package && ReferenceEquals(p.Existing, overview));
    }

    [Fact]
    public void No_package_overview_when_diagrams_are_off_an_existing_project_gets_one_later_and_a_deleted_one_stays_gone()
    {
        var m = new ModelRepository("Project");
        var changes = Sync(m, MeridianLike, Options());
        var root = CodeModelMapper.FindRootPackage(m, "Meridian")!;
        CodeSyncDiagrams.Refresh(m, changes, createDiagrams: false, codeRoot: root);
        Assert.DoesNotContain(m.Diagrams, d => d.DiagramType == DiagramType.Package);

        // A project synced before the overview existed: the next sync makes it, even one that adds no namespace (or
        // changes nothing at all), and only once.
        var edited = MeridianLike.Select(f => f.Path == "src/Meridian.cs" ? f with { Content = f.Content.Replace("public string Text { get; set; }", "public string Text { get; set; } public int Major { get; set; }") } : f).ToList();
        var later = Sync(m, edited, Options(commit: "c2"));
        Assert.DoesNotContain(later.Added, id => m.Find(id) is { } e && CodeModelMapper.IsNamespacePackage(e));
        var made = CodeSyncDiagrams.Refresh(m, later, createDiagrams: true, codeRoot: root);
        var overview = Assert.Single(m.Diagrams, d => d.DiagramType == DiagramType.Package);
        Assert.Contains(overview, made.Created);
        Assert.Equal("true", root.Get(CodeSyncDiagrams.OverviewMadeKey));
        var again = Sync(m, edited, Options(commit: "c3"));
        Assert.True(again.IsEmpty);
        Assert.Empty(CodeSyncDiagrams.Refresh(m, again, createDiagrams: true, codeRoot: root).Created);
        Assert.Single(m.Diagrams, d => d.DiagramType == DiagramType.Package);

        // Someone deletes it: it is not made again.
        m.Delete(overview);
        Assert.Empty(CodeSyncDiagrams.Refresh(m, Sync(m, MeridianLike, Options(commit: "c4")), createDiagrams: true, codeRoot: root).Created);
        Assert.DoesNotContain(m.Diagrams, d => d.DiagramType == DiagramType.Package);
    }

    [Fact]
    public void A_single_namespace_project_gets_no_overview()
    {
        var m = new ModelRepository("Project");
        var files = new List<CodeFile> { new("src/A.cs", "namespace Solo { public class A { } public class B { public A Part { get; set; } } }") };
        var changes = Sync(m, files, Options("Code"));
        CodeSyncDiagrams.Refresh(m, changes, createDiagrams: true, codeRoot: CodeModelMapper.FindRootPackage(m, "Code"));
        Assert.DoesNotContain(m.Diagrams, d => d.DiagramType == DiagramType.Package);
    }

    // ================================================================ trivial internal-structure diagrams of earlier syncs

    private static readonly IReadOnlyList<CodeFile> Eps =
    [
        new("src/Sat.cs", """
            namespace Sat.Power
            {
                public class Battery { public double Charge { get; set; } public void FeedFrom(Panel panel) { } }
                public class Panel { public double Watts { get; set; } }
                public class Eps { public Battery Main { get; set; } = new(); public Panel Wing { get; set; } = new(); }
                public class Pcu { public Battery Main { get; set; } = new(); public Panel Wing { get; set; } = new(); }
                public class Bus { public Battery Main { get; set; } = new(); public Panel Wing { get; set; } = new(); }
            }
            """),
    ];

    /// <summary>Eps, Pcu and Bus lose a part each: their IBDs no longer meet the rule.</summary>
    private static IReadOnlyList<CodeFile> EpsWithOnePart =>
        [new("src/Sat.cs", Eps[0].Content.Replace(" public Panel Wing { get; set; } = new(); }", " }"))];

    private static Diagram Ibd(ModelRepository m, string block) => m.Diagrams.Single(d => d.DiagramType == DiagramType.InternalBlock && d.Owner == S(m, block));

    [Fact]
    public void Trivial_internal_structure_diagrams_of_earlier_syncs_are_removed_unless_someone_touched_them()
    {
        var m = new ModelRepository("Project");
        var first = Sync(m, Eps, Options("Code"));
        CodeSyncDiagrams.Refresh(m, first, createDiagrams: true, codeRoot: CodeModelMapper.FindRootPackage(m, "Code"));
        var (eps, pcu, bus) = (Ibd(m, "Sat.Power.Eps"), Ibd(m, "Sat.Power.Pcu"), Ibd(m, "Sat.Power.Bus"));
        Assert.All([eps, pcu, bus], d => Assert.True(CodeSyncDiagrams.IsUntouched(d)));   // stamped when made

        // Pcu: someone moves a shape. Bus: someone adds a comment to it. Eps: untouched.
        var shape = pcu.Nodes[0];
        m.SetBounds(pcu, new Dictionary<DiagramNode, Bounds> { [shape] = shape.Bounds with { X = shape.Bounds.X + 50 } });
        var note = m.Create(S(m, "Sat.Power.Bus"), ElementKind.Comment);
        m.AddNode(bus, note, new Bounds(400, 40, 160, 60));

        var later = Sync(m, EpsWithOnePart, Options("Code", "c2"));
        Assert.False(m.Contains(eps));
        Assert.Contains(eps.Id, later.Removed);   // reviewable like any removal
        Assert.True(m.Contains(pcu));
        Assert.True(m.Contains(bus));
        Assert.Equal(1, later.TrivialDiagramsRemoved);
    }

    [Fact]
    public void Trivial_internal_structure_diagrams_referenced_elsewhere_or_made_by_hand_stay()
    {
        var m = new ModelRepository("Project");
        var first = Sync(m, Eps, Options("Code"));
        CodeSyncDiagrams.Refresh(m, first, createDiagrams: true, codeRoot: CodeModelMapper.FindRootPackage(m, "Code"));
        var (eps, pcu) = (Ibd(m, "Sat.Power.Eps"), Ibd(m, "Sat.Power.Pcu"));
        // A report template names the Eps diagram; a relation map's settings carry the Pcu diagram's id.
        var templates = m.Create(m.Root, ElementKind.Package, "Report Templates");
        var template = m.Create(templates, ElementKind.Comment);
        m.Set(template, FieldKeys.Body, $"{{{{diagram \"{eps.Name}\"}}}}");
        var map = m.Create(m.Root, ElementKind.RelationMap, "Power");
        m.Set(map, "relationMap", $"{{\"RootId\":\"{pcu.Id}\"}}");
        // Bus's IBD was made by hand with Auto-diagram (not stamped, not in an earlier run): code sync leaves it alone.
        var bus = Ibd(m, "Sat.Power.Bus");
        m.Set(bus, CodeBehaviourMapper.LayoutKey, null);

        var later = Sync(m, EpsWithOnePart, Options("Code", "c2"));
        Assert.True(m.Contains(eps));
        Assert.True(m.Contains(pcu));
        Assert.True(m.Contains(bus));
        Assert.Equal(0, later.TrivialDiagramsRemoved);
    }

    [Fact]
    public void Unstamped_internal_structure_diagrams_from_an_earlier_run_are_judged_by_the_auto_diagrammer_layout()
    {
        var m = new ModelRepository("Project");
        var first = Sync(m, Eps, Options("Code"));
        CodeSyncDiagrams.Refresh(m, first, createDiagrams: true, codeRoot: CodeModelMapper.FindRootPackage(m, "Code"));
        var (eps, pcu) = (Ibd(m, "Sat.Power.Eps"), Ibd(m, "Sat.Power.Pcu"));
        // As the first sync of a project left them: no stamp; the run record lists them.
        foreach (var d in new[] { eps, pcu }) m.Set(d, CodeBehaviourMapper.LayoutKey, null);
        var shape = pcu.Nodes[0];
        m.SetBounds(pcu, new Dictionary<DiagramNode, Bounds> { [shape] = shape.Bounds with { Y = shape.Bounds.Y + 80 } });

        var later = Sync(m, EpsWithOnePart, Options("Code", "c2") with { SyncedDiagrams = new HashSet<string> { eps.Id, pcu.Id } });
        Assert.False(m.Contains(eps));   // where the auto-diagrammer puts it
        Assert.True(m.Contains(pcu));    // moved by someone
        Assert.Equal(2, later.TrivialDiagramsRemoved);   // and Bus's, stamped and untouched
    }

    [Fact]
    public void Large_namespace_trees_show_only_the_top_level_on_the_overview()
    {
        var files = new List<CodeFile>();
        for (var i = 0; i < 6; i++)
            for (var j = 0; j < 8; j++)
                files.Add(new($"src/N{i}/S{j}.cs", $"namespace App.N{i}.S{j} {{ public class C{i}{j} {{ }} }}"));
        var m = new ModelRepository("Project");
        Sync(m, files, Options("App"));
        var shown = CodeModelMapper.OverviewPackages(CodeModelMapper.FindRootPackage(m, "App")!);
        Assert.Equal(["N0", "N1", "N2", "N3", "N4", "N5"], shown.Select(p => p.Name));   // 6 + 48 > 40: top level only
    }

    // ================================================================ which blocks get an IBD

    [Fact]
    public void Only_blocks_whose_parts_work_together_get_an_internal_structure_diagram()
    {
        var files = new List<CodeFile>
        {
            new("src/Sat.cs", """
                using System.Collections.Generic;
                namespace Sat.Power
                {
                    public class Battery { public double Charge { get; set; } }
                    public class Panel { public double Watts { get; set; } public void Feed(Battery b) { } }
                    public interface ISource { }
                    public class Eps { public Battery Main { get; set; } = new(); public List<Panel> Panels { get; set; } = new(); } // 2 parts that interact: IBD
                    public class Charger { public Battery Battery { get; set; } = new(); public ISource Source { get; set; } }      // 1 part + a reference: none
                    public class Logger { public List<string> Lines { get; set; } public Dictionary<string, int> Counts { get; set; } } // values only: none
                    public class Pair { public Battery A { get; set; } = new(); public Battery B { get; set; } = new(); }          // 2 parts of one type, no interaction: none
                    public class Holder { public Battery Main { get; set; } public Panel Wing { get; set; } }                         // references (not created here): none
                }
                namespace Sat.Comms { public class Radio { public double Mhz { get; set; } } }
                """),
        };
        var m = new ModelRepository("Project");
        var changes = Sync(m, files, Options("Code"));
        CodeSyncDiagrams.Refresh(m, changes, createDiagrams: true, codeRoot: CodeModelMapper.FindRootPackage(m, "Code"));
        var ibds = m.Diagrams.Where(d => d.DiagramType == DiagramType.InternalBlock).Select(d => d.Owner!.Name).Order().ToList();
        Assert.Equal(["Eps"], ibds);
        Assert.True(CodeSyncDiagrams.WorthAnIbd(m, S(m, "Sat.Power.Eps")));
        Assert.False(CodeSyncDiagrams.WorthAnIbd(m, S(m, "Sat.Power.Pair")));
        Assert.False(CodeSyncDiagrams.WorthAnIbd(m, S(m, "Sat.Power.Holder")));
        // The connector says why the parts are joined.
        var connector = Assert.Single(S(m, "Sat.Power.Eps").OwnedElements.OfType<Relationship>(), r => r.Kind == ElementKind.Connector);
        Assert.Equal(("Panels", "Main"), (connector.Source.Name, connector.Target.Name));
        Assert.Equal("Derived from code: Panel uses Battery.", connector.Documentation);
        Assert.False(CodeSyncDiagrams.WorthAnIbd(m, S(m, "Sat.Power.Charger")));
        Assert.False(CodeSyncDiagrams.WorthAnIbd(m, S(m, "Sat.Power.Logger")));
    }
}
