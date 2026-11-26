using Meridian.Core.CodeSync;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization.CodeSync;

namespace Meridian.Tests;

/// <summary>
/// What makes a synced model explain the architecture: subsystems from projects with ports and connectors, readable
/// names, parts only for what a type owns, test code and private helpers left out, usage dependencies only at the
/// package and subsystem level, and a few diagrams of the key blocks.
/// </summary>
public class CodeSyncArchitectureTests
{
    private const string Sdk = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>""";

    private static readonly IReadOnlyList<CodeFile> Shop =
    [
        new("src/App.Core/App.Core.csproj", Sdk + "<PropertyGroup><Description>The order domain.</Description></PropertyGroup></Project>"),
        new("src/App.Core/Orders.cs", """
            namespace App.Core
            {
                /// <summary>Where orders are kept.</summary>
                public interface IStore { void Save(Order order); }
                public class Order { public Line FirstLine { get; set; } = new(); }
                public class Line { public double Amount { get; set; } }
                public enum Status { Open, Paid }
                public class Ledger { private class Entry { } private Entry _last; }
            }
            """),
        new("src/App.Data/App.Data.csproj", Sdk + """<ItemGroup><ProjectReference Include="..\App.Core\App.Core.csproj" /></ItemGroup></Project>"""),
        new("src/App.Data/SqlStore.cs", "namespace App.Data { public class SqlStore : App.Core.IStore { public void Save(App.Core.Order order) { } } }"),
        new("src/App.Web/App.Web.csproj", Sdk + """<ItemGroup><ProjectReference Include="../App.Core/App.Core.csproj" /><ProjectReference Include="../App.Data/App.Data.csproj" /></ItemGroup></Project>"""),
        new("src/App.Web/OrdersController.cs", """
            namespace App.Web
            {
                public class OrdersController
                {
                    private readonly App.Core.IStore _store;
                    public App.Core.Order Current { get; set; }
                    public void Post() { _store.Save(Current); }
                }
            }
            """),
        new("tests/App.Tests/App.Tests.csproj", Sdk + """<ItemGroup><PackageReference Include="xunit" Version="2.9.0" /></ItemGroup></Project>"""),
        new("tests/App.Tests/OrderTests.cs", "namespace App.Tests { public class OrderTests { [Fact] public void Works() { } } public class FactAttribute : System.Attribute { } }"),
    ];

    private static CodeSyncOptions Options(string commit = "c1") => new() { RepoId = "repo-1", Commit = commit, RootPackage = "App" };

    private static (ModelRepository M, CodeModel Code, CodeSyncChanges Changes) Synced(IReadOnlyList<CodeFile>? files = null, CodeSyncOptions? options = null)
    {
        var m = new ModelRepository("Shop");
        var code = new CSharpCodeReader().Read(files ?? Shop);
        return (m, code, CodeModelMapper.Apply(m, code, options ?? Options()));
    }

    private static Element? Sym(ModelRepository m, string symbol) => m.AllElements.SingleOrDefault(e => e.Get(CodeFieldKeys.Symbol) == symbol && m.Contains(e));

    private static Element S(ModelRepository m, string symbol) => Sym(m, symbol) ?? throw new Xunit.Sdk.XunitException($"no element for {symbol}");

    [Fact]
    public void The_reader_finds_projects_test_projects_references_and_type_visibility()
    {
        var code = new CSharpCodeReader().Read(Shop);
        Assert.Equal(["App.Core", "App.Data", "App.Tests", "App.Web"], code.Modules.Select(x => x.Name));
        var web = code.Modules.Single(x => x.Name == "App.Web");
        Assert.Equal(("src/App.Web", "src/App.Web/App.Web.csproj"), (web.Path, web.File));
        Assert.Equal(["App.Core", "App.Data"], web.References);
        Assert.True(code.Modules.Single(x => x.Name == "App.Tests").IsTest);
        Assert.Equal("The order domain.", code.Modules.Single(x => x.Name == "App.Core").Description);
        Assert.Equal("App.Data", code.Types.Single(t => t.Symbol == "App.Data.SqlStore").Module);
        Assert.Equal("private", code.Types.Single(t => t.Symbol == "App.Core.Ledger.Entry").Visibility);
        Assert.Equal("public", code.Types.Single(t => t.Symbol == "App.Core.Order").Visibility);
        Assert.True(new CSharpCodeReader().Handles("src/App.Web/App.Web.csproj"));
        Assert.False(new CSharpCodeReader().Handles("src/App.Web/obj/App.Web.csproj"));
    }

    [Fact]
    public void Subsystems_get_ports_for_the_interfaces_between_them_and_connectors_for_other_use()
    {
        var (m, _, _) = Synced();
        var system = S(m, "arch:system");
        Assert.Equal(("App", "system"), (system.Name, system.Get(FieldKeys.Stereotypes)));
        Assert.Equal("Architecture", system.Owner!.Name);
        // One subsystem per non-test project, named without the shared prefix; tests are not a subsystem.
        Assert.Equal(["core", "data", "web"], AutoDiagrammer.Parts(m, system).Select(p => p.Name).Order());
        var core = S(m, "arch:subsystem:App.Core");
        Assert.Equal(("Core", "subsystem"), (core.Name, core.Get(FieldKeys.Stereotypes)));
        Assert.StartsWith("The order domain.", core.Documentation);
        Assert.Null(Sym(m, "arch:subsystem:App.Tests"));

        // Web uses IStore, which Data implements: Data provides it, Web requires it, a connector joins the two ports.
        var provided = S(m, "arch:port:App.Data:provides:App.Core.IStore");
        var required = S(m, "arch:port:App.Web:requires:App.Core.IStore");
        Assert.Equal("Store", provided.Name);
        Assert.Equal("Store", required.Name);
        Assert.Contains("needs Store", required.Documentation);
        Assert.Equal(S(m, "App.Core.IStore").Id, provided.Get(FieldKeys.Type));
        var wire = Assert.IsType<Relationship>(S(m, "arch:conn:App.Web->App.Data:App.Core.IStore"));
        Assert.Equal((ElementKind.Connector, required, provided), (wire.Kind, wire.Source, wire.Target));
        Assert.Equal((S(m, "arch:part:App.Web").Id, S(m, "arch:part:App.Data").Id), (wire.Get(FieldKeys.SourcePart), wire.Get(FieldKeys.TargetPart)));

        // Web also uses Order from Core directly: a plain connector, saying through what.
        var uses = Assert.IsType<Relationship>(S(m, "arch:conn:App.Web->App.Core"));
        Assert.Equal((S(m, "arch:part:App.Web"), S(m, "arch:part:App.Core")), (uses.Source, uses.Target));
        Assert.Contains("Order", uses.Documentation);
        Assert.Same(system, uses.Owner);
        // Data implements IStore itself: it does not "require" it.
        Assert.Null(Sym(m, "arch:port:App.Data:requires:App.Core.IStore"));
    }

    [Fact]
    public void Defaults_leave_out_tests_private_helpers_and_type_usage_dependencies_and_use_words_for_names()
    {
        var (m, _, _) = Synced();
        Assert.Null(Sym(m, "App.Tests.OrderTests"));
        Assert.Null(Sym(m, "App.Core.Ledger.Entry"));
        Assert.DoesNotContain(m.AllElements, e => e.Get(CodeFieldKeys.Symbol).StartsWith("dep:", StringComparison.Ordinal));
        // The usage still counts between packages.
        Assert.NotNull(Sym(m, "pkgdep:App.Web->App.Core"));

        Assert.Equal("Orders controller", S(m, "App.Web.OrdersController").Name);
        Assert.Equal("SQL store", S(m, "App.Data.SqlStore").Name);
        Assert.Equal("Store", S(m, "App.Core.IStore").Name);
        Assert.Equal("Current", S(m, "App.Web.OrdersController.Current").Name);   // members keep their code names

        // Options bring them back.
        var (all, _, _) = Synced(options: Options() with { IncludeTests = true, IncludeNonPublic = true, IncludeUsageDependencies = true, ReadableNames = false });
        Assert.NotNull(Sym(all, "App.Tests.OrderTests"));
        Assert.NotNull(Sym(all, "App.Core.Ledger.Entry"));
        Assert.Equal("OrdersController", S(all, "App.Web.OrdersController").Name);
        Assert.NotNull(Sym(all, "arch:subsystem:App.Tests"));
    }

    [Fact]
    public void A_member_is_a_part_only_when_the_type_creates_it_otherwise_a_reference()
    {
        var (m, _, _) = Synced();
        var line = S(m, "App.Core.Order.FirstLine");
        Assert.Equal("", line.Get(FieldKeys.Aggregation));
        Assert.Equal(ElementKind.Composition, S(m, "comp:App.Core.Order->App.Core.Line").Kind);
        var current = S(m, "App.Web.OrdersController.Current");
        Assert.Equal("none", current.Get(FieldKeys.Aggregation));
        Assert.True(AutoDiagrammer.IsReference(current));
        Assert.Equal(ElementKind.Association, S(m, "ref:App.Web.OrdersController->App.Core.Order").Kind);
        Assert.Null(Sym(m, "comp:App.Web.OrdersController->App.Core.Order"));
        Assert.Empty(AutoDiagrammer.Parts(m, S(m, "App.Web.OrdersController")));
    }

    [Fact]
    public void A_name_written_by_the_AI_pass_is_kept_and_a_resync_changes_nothing()
    {
        var (m, code, _) = Synced();
        Assert.True(CodeModelMapper.Apply(m, code, Options("c2")).IsEmpty);
        var controller = S(m, "App.Web.OrdersController");
        m.Set(controller, CodeModelMapper.LabelKey, "Order API");
        m.Rename(controller, "Order API");
        Assert.True(CodeModelMapper.Apply(m, code, Options("c3")).IsEmpty);
        Assert.Equal("Order API", controller.Name);
        Assert.Equal("App.Web.OrdersController", controller.Get(CodeFieldKeys.Symbol));
    }

    [Fact]
    public void Key_blocks_are_the_most_used_public_top_level_types_of_a_package()
    {
        var files = new List<CodeFile>();
        var types = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"public class T{i:00} {{ {(i >= 3 ? "public void Use(Hub a, Hub2 b) { }" : "")} }}"));
        files.Add(new("src/Big.cs", $"namespace Big {{ public class Hub {{ }} public class Hub2 {{ }} public enum Kind {{ A }} {types} }}"));
        var (m, _, _) = Synced(files, Options() with { RootPackage = "Big" });
        var key = m.AllElements.Where(e => e.Get(CodeModelMapper.KeyKey) == "true").Select(e => e.Get(CodeFieldKeys.Symbol)).ToList();
        Assert.Contains("Big.Hub", key);
        Assert.Contains("Big.Hub2", key);
        Assert.DoesNotContain("Big.Kind", key);
        Assert.True(key.Count <= CodeModelMapper.KeyBlocksPerPackage);
    }

    [Fact]
    public void A_first_sync_draws_the_architecture_and_one_compact_structure_diagram_per_package()
    {
        var (m, _, changes) = Synced();
        var result = CodeSyncDiagrams.Refresh(m, changes, createDiagrams: true, codeRoot: CodeModelMapper.FindRootPackage(m, "App"));
        var system = S(m, "arch:system");
        var arch = Assert.Single(result.Created, d => d.DiagramType == DiagramType.InternalBlock && ReferenceEquals(d.Owner, system));
        Assert.Equal("App – system architecture", arch.Name);
        Assert.Equal(3, arch.Nodes.Count(n => n.Element.Kind == ElementKind.Property));
        Assert.Contains(arch.Nodes, n => n.Element.Kind == ElementKind.Port);
        Assert.Equal(m.Relationships.Count(r => r.Kind == ElementKind.Connector && r.Get(CodeFieldKeys.Symbol).StartsWith("arch:conn:", StringComparison.Ordinal)), arch.Edges.Count);
        Assert.Contains(result.Created, d => d.Name == "Subsystems" && d.DiagramType == DiagramType.BlockDefinition);

        var core = Assert.Single(result.Created, d => d.DiagramType == DiagramType.BlockDefinition && d.Owner!.Get(CodeFieldKeys.Symbol) == "ns:App.Core");
        Assert.All(core.Nodes, n => Assert.Equal("none", n.Style[Notation.CompartmentsStyle]));
        Assert.DoesNotContain(core.Nodes, n => n.Element.Kind == ElementKind.Enumeration);
        Assert.DoesNotContain(m.Diagrams, d => d.DiagramType == DiagramType.InternalBlock && !ReferenceEquals(d.Owner, system));   // no parts work together elsewhere
    }

    [Theory]
    [InlineData("tests/Unit/OrderTests.cs", true)]
    [InlineData("src/App.Tests/Helpers.cs", true)]
    [InlineData("web/src/cart.spec.ts", true)]
    [InlineData("pkg/test_orders.py", true)]
    [InlineData("src/main/java/Order.java", false)]
    [InlineData("src/App/Latest.cs", false)]
    [InlineData("src/App/Contest.cs", false)]
    public void Test_paths_are_recognised(string path, bool test) => Assert.Equal(test, CodeSyncNoise.IsTestPath(path));
}
