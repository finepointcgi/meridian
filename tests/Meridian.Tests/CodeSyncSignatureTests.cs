using Meridian.Core.CodeSync;
using Meridian.Core.Model;
using Meridian.Serialization.CodeSync;

namespace Meridian.Tests;

/// <summary>Method parameters, overloads, abstract / static flags and member docs, and the migration of the single
/// "Type.Method()" operations that earlier syncs made.</summary>
public class CodeSyncSignatureTests
{
    private static readonly CodeSyncOptions Options = new() { RepoId = "repo-1", Commit = "c1" };

    private const string Eps = """
        namespace Sat.Power;

        public class Cell { }

        /// <summary>Power system.</summary>
        public abstract class Eps
        {
            /// <summary>Maximum load.</summary>
            public static int MaxLoad = 10;
            /// <summary>Charges from a source.</summary>
            public bool Charge(double watts) => true;
            public bool Charge(double watts, int minutes) => true;
            public bool Charge(Cell cell, ref int budget, out string log, params Cell[] more) { log = ""; return true; }
            public abstract void Reset();
            public static Eps Create() => null!;
            public void Idle() { }
        }

        public static class Util { public static void Log(string s) { } }
        """;

    private static CodeModel Read(string src) => new CSharpCodeReader().Read([new CodeFile("Eps.cs", src)]);

    private static Element S(ModelRepository m, string symbol) =>
        m.AllElements.SingleOrDefault(e => e.Get(CodeFieldKeys.Symbol) == symbol) ?? throw new Xunit.Sdk.XunitException($"no element for {symbol}");

    [Fact]
    public void Csharp_reader_lists_each_overload_with_parameters_flags_and_docs()
    {
        var m = Read(Eps);
        var eps = Assert.Single(m.Types, t => t.Symbol == "Sat.Power.Eps");
        Assert.True(eps.IsAbstract);
        Assert.True(Assert.Single(m.Types, t => t.Symbol == "Sat.Power.Util").IsStatic);
        Assert.False(Assert.Single(m.Types, t => t.Symbol == "Sat.Power.Util").IsAbstract);
        var charges = eps.Members.Where(x => x.Name == "Charge").ToList();
        Assert.Equal(3, charges.Count);
        Assert.Equal("Charges from a source.", charges[0].Doc);
        Assert.Equal(11, charges[0].Line);
        var third = charges[2].Parameters;
        Assert.Equal(
            [("cell", "Cell", "Sat.Power.Cell", false, CodeParameterDirection.In), ("budget", "int", null, false, CodeParameterDirection.InOut),
             ("log", "string", null, false, CodeParameterDirection.Out), ("more", "Cell[]", "Sat.Power.Cell", true, CodeParameterDirection.In)],
            third.Select(p => (p.Name, p.TypeName, p.TypeSymbol, p.IsCollection, p.Direction)));
        var maxLoad = eps.Members.Single(x => x.Name == "MaxLoad");
        Assert.Equal((true, "Maximum load."), (maxLoad.IsStatic, maxLoad.Doc));
        Assert.True(eps.Members.Single(x => x.Name == "Reset").IsAbstract);
        Assert.True(eps.Members.Single(x => x.Name == "Create").IsStatic);
    }

    [Fact]
    public void Overloads_become_separate_operations_with_signatures_flags_and_docs()
    {
        var m = new ModelRepository("M");
        var changes = CodeModelMapper.Apply(m, Read(Eps), Options);
        Assert.Empty(changes.Warnings);
        Assert.Equal("(watts : double) : bool", S(m, "Sat.Power.Eps.Charge(double)").Get(FieldKeys.Signature));
        Assert.Equal("(watts : double, minutes : int) : bool", S(m, "Sat.Power.Eps.Charge(double,int)").Get(FieldKeys.Signature));
        Assert.Equal("(cell : Cell, inout budget : int, out log : string, more : Cell[]) : bool",
            S(m, "Sat.Power.Eps.Charge(Cell,int,string,Cell[])").Get(FieldKeys.Signature));
        Assert.Equal("Charges from a source.", S(m, "Sat.Power.Eps.Charge(double)").Documentation);
        Assert.Equal("11", S(m, "Sat.Power.Eps.Charge(double)").Get(CodeFieldKeys.Line));
        Assert.Equal("true", S(m, "Sat.Power.Eps.Reset()").Get(FieldKeys.IsAbstract));
        Assert.Equal("", S(m, "Sat.Power.Eps.Reset()").Get(FieldKeys.Signature));
        Assert.Equal("true", S(m, "Sat.Power.Eps.Create()").Get(CodeModelMapper.IsStaticKey));
        Assert.Equal("true", S(m, "Sat.Power.Eps.MaxLoad").Get(CodeModelMapper.IsStaticKey));
        Assert.Equal("Maximum load.", S(m, "Sat.Power.Eps.MaxLoad").Documentation);
        Assert.Equal("true", S(m, "Sat.Power.Eps").Get(FieldKeys.IsAbstract));
        Assert.True(S(m, "Sat.Power.Eps").IsAbstract);
        Assert.Equal(6, S(m, "Sat.Power.Eps").OwnedElements.Count(e => e.Kind == ElementKind.Operation));

        Assert.True(CodeModelMapper.Apply(m, Read(Eps), Options with { Commit = "c2" }).IsEmpty);
    }

    /// <summary>What the reader produced before parameters existed: one member per method name, no parameters.</summary>
    private static CodeModel Legacy(CodeModel current) => current with
    {
        Types = current.Types.Select(t => t with
        {
            Members = t.Members.Where(x => x.Kind != CodeMemberKind.Method).Concat(
                t.Members.Where(x => x.Kind == CodeMemberKind.Method).GroupBy(x => x.Name).Select(g => g.First() with { Parameters = [], Doc = null, IsStatic = false, IsAbstract = false, Line = 0 })).ToList(),
        }).ToList(),
    };

    [Fact]
    public void Operations_from_earlier_syncs_are_updated_in_place()
    {
        var m = new ModelRepository("M");
        var old = CodeModelMapper.Apply(m, Legacy(Read(Eps)), Options);
        Assert.Empty(old.Warnings);
        var charge = S(m, "Sat.Power.Eps.Charge()");
        Assert.Equal("() : bool", charge.Get(FieldKeys.Signature));
        var idle = S(m, "Sat.Power.Eps.Idle()");
        // Someone documented and stereotyped the old operation by hand; that has to survive.
        m.Set(charge, FieldKeys.Stereotypes, "command");

        var sync = CodeModelMapper.Apply(m, Read(Eps), Options with { Commit = "c2" });
        Assert.Empty(sync.Removed);
        Assert.Same(charge, S(m, "Sat.Power.Eps.Charge(double)"));   // the first overload took over the element
        Assert.Equal("(watts : double) : bool", charge.Get(FieldKeys.Signature));
        Assert.Equal("command", charge.Get(FieldKeys.Stereotypes));
        Assert.Contains(charge.Id, sync.Changed);
        Assert.Contains(S(m, "Sat.Power.Eps.Charge(double,int)").Id, sync.Added);
        Assert.Contains(S(m, "Sat.Power.Eps.Charge(Cell,int,string,Cell[])").Id, sync.Added);
        Assert.Same(idle, S(m, "Sat.Power.Eps.Idle()"));   // already right: untouched
        Assert.DoesNotContain(idle.Id, sync.Changed);
        Assert.DoesNotContain(m.AllElements, e => e.Get(CodeFieldKeys.Symbol) == "Sat.Power.Eps.Charge()");
        Assert.Equal(3, S(m, "Sat.Power.Eps").OwnedElements.Count(e => e.Name == "Charge"));

        Assert.True(CodeModelMapper.Apply(m, Read(Eps), Options with { Commit = "c3" }).IsEmpty);
    }

    [Fact]
    public void A_zero_parameter_overload_keeps_the_old_operation()
    {
        const string src = "namespace N; public class A { public void Go() { } public void Go(int speed) { } }";
        var m = new ModelRepository("M");
        CodeModelMapper.Apply(m, Legacy(Read(src)), Options);
        var go = S(m, "N.A.Go()");
        var sync = CodeModelMapper.Apply(m, Read(src), Options with { Commit = "c2" });
        Assert.Same(go, S(m, "N.A.Go()"));
        Assert.Contains(S(m, "N.A.Go(int)").Id, sync.Added);
        Assert.Empty(sync.Removed);
    }

    [Fact]
    public void A_changed_parameter_type_keeps_the_operation_and_a_removed_overload_goes()
    {
        var m = new ModelRepository("M");
        CodeModelMapper.Apply(m, Read("namespace N; public class A { public void Go(int s) { } public void Go(int s, int t) { } }"), Options);
        var go = S(m, "N.A.Go(int)");
        var two = S(m, "N.A.Go(int,int)");

        var retyped = CodeModelMapper.Apply(m, Read("namespace N; public class A { public void Go(long s) { } public void Go(int s, int t) { } }"), Options with { Commit = "c2" });
        Assert.Same(go, S(m, "N.A.Go(long)"));
        Assert.Equal("(s : long)", go.Get(FieldKeys.Signature));
        Assert.Equal([go.Id], retyped.Changed);
        Assert.Empty(retyped.Added);
        Assert.Empty(retyped.Removed);

        var dropped = CodeModelMapper.Apply(m, Read("namespace N; public class A { public void Go(long s) { } }"), Options with { Commit = "c3" });
        Assert.Equal([two.Id], dropped.Removed);
        Assert.False(m.Contains(two));
    }

    [Fact]
    public void Signatures_and_symbols_follow_the_model_notation()
    {
        var method = new CodeMember("Move", CodeMemberKind.Method, "void", null, false, "public")
        {
            Parameters = [new("to", "List<Point>"), new("speed", ""), new("result", "bool", Direction: CodeParameterDirection.Return)],
        };
        Assert.Equal("(to : List<Point>, speed) : bool", CodeModelMapper.Signature(method));
        Assert.Equal("T.Move(List<Point>,?)", CodeModelMapper.OperationSymbol("T", method));
        Assert.Null(CodeModelMapper.Signature(method with { Parameters = [] }));
        Assert.Equal("() : int", CodeModelMapper.Signature(method with { Parameters = [], TypeName = "int" }));
        Assert.Null(CodeModelMapper.Signature(method with { Parameters = [], TypeName = "None" }));
    }

    [Fact]
    public void The_auto_reader_merges_every_language_by_extension()
    {
        var files = new List<CodeFile> { new("svc/Eps.cs", Eps) };
        files.AddRange(CodeSyncTypeScriptTests.GroundStation);
        files.AddRange(CodeSyncJavaTests.Rover);
        files.AddRange(CodeSyncPythonTests.Lander);
        var auto = new MultiLanguageCodeReader();
        Assert.Equal("auto", auto.Language);
        Assert.True(auto.Handles("a.cs") && auto.Handles("a.ts") && auto.Handles("A.java") && auto.Handles("a.py"));
        Assert.False(auto.Handles("README.md"));
        var m = auto.Read(files);
        Assert.Equal("csharp+typescript+java+python", m.Language);
        Assert.Contains(m.Types, t => t.Symbol == "Sat.Power.Eps");
        Assert.Contains(m.Types, t => t.Symbol == "src.station.Station");
        Assert.Contains(m.Types, t => t.Symbol == "com.acme.rover.Rover");
        Assert.Contains(m.Types, t => t.Symbol == "lander.core.Rover");
        Assert.Equal(m.Types.Select(t => t.Symbol).Order(StringComparer.Ordinal), m.Types.Select(t => t.Symbol));
        Assert.Equal(CodeSyncTestText.Describe(m), CodeSyncTestText.Describe(auto.Read(files.AsEnumerable().Reverse().ToList())));
        Assert.Equal("typescript", auto.Read(CodeSyncTypeScriptTests.GroundStation).Language);

        var model = new ModelRepository("All");
        Assert.Empty(CodeModelMapper.Apply(model, m, Options).Warnings);
        Assert.True(CodeModelMapper.Apply(model, auto.Read(files), Options with { Commit = "c2" }).IsEmpty);
    }

    [Fact]
    public void Readers_are_found_by_language_name()
    {
        Assert.IsType<MultiLanguageCodeReader>(CodeReaders.For("auto"));
        Assert.IsType<CSharpCodeReader>(CodeReaders.For("csharp"));
        Assert.IsType<TypeScriptCodeReader>(CodeReaders.For("TypeScript"));
        Assert.IsType<JavaCodeReader>(CodeReaders.For("java"));
        Assert.IsType<PythonCodeReader>(CodeReaders.For("python"));
        Assert.Null(CodeReaders.For("cobol"));
        Assert.True(CodeReaders.Include("auto")("src/a.py"));
        Assert.False(CodeReaders.Include("java")("src/a.py"));
        Assert.False(CodeReaders.Include("cobol")("src/a.cs"));
    }
}
