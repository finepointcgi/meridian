using System.Diagnostics;
using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Core.Validation;
using Meridian.Serialization;

namespace Meridian.Tests;

public class ScaleTests
{
    /// <summary>~10,000 elements: blocks with properties, requirements, satisfy links, and a 400-shape diagram.</summary>
    private static ModelRepository BigModel()
    {
        var m = new ModelRepository("Big");
        var reqs = m.Create(m.Root, ElementKind.Package, "Requirements");
        var structure = m.Create(m.Root, ElementKind.Package, "Structure");
        var d = m.CreateDiagram(structure, DiagramType.BlockDefinition, "Big BDD");
        var requirements = new List<Element>();
        for (var i = 0; i < 1000; i++)
        {
            var r = m.Create(reqs, ElementKind.Requirement, $"Requirement {i}");
            m.Set(r, FieldKeys.Text, $"The system shall do thing {i}.");
            requirements.Add(r);
        }
        for (var i = 0; i < 1500; i++)
        {
            var b = m.Create(structure, ElementKind.Block, $"Block{i}");
            for (var p = 0; p < 4; p++) { var prop = m.Create(b, ElementKind.Property, $"p{p}"); m.Set(prop, FieldKeys.Type, "Real"); }
            m.Connect(ElementKind.Satisfy, b, requirements[i % requirements.Count]);
            if (i < 400) m.AddNode(d, b, new Bounds(i % 20 * 220, i / 20 * 160, 180, 120), showRelationships: false);
        }
        m.History.Clear();
        return m;
    }

    [Fact]
    public void Ten_Thousand_Elements_Stay_Fast()
    {
        var sw = Stopwatch.StartNew();
        var m = BigModel();
        var build = sw.Elapsed; sw.Restart();
        Assert.True(m.AllElements.Count() > 9_000);

        var issues = new ModelValidator().Validate(m);
        var validate = sw.Elapsed; sw.Restart();
        var json = ProjectFile.Serialize(m);
        var back = ProjectFile.Deserialize(json);
        var roundTrip = sw.Elapsed; sw.Restart();
        var d = back.Diagrams.Single();
        var sb = new System.Text.StringBuilder();
        SvgRenderer.RenderBody(sb, back, d, n => n.Bounds, null, null, new Bounds(0, 0, 1600, 900));
        var culledRender = sw.Elapsed;

        Assert.True(validate.TotalSeconds < 5, $"validation took {validate}");
        Assert.True(roundTrip.TotalSeconds < 5, $"save/load took {roundTrip}");
        Assert.True(culledRender.TotalSeconds < 2, $"render took {culledRender}");
        Assert.True(sb.Length < 400_000, "viewport culling should limit the markup");
        _ = (build, issues);
    }

    // ------------------------------------------------------------------ 60,000 elements (LargeModelGenerator)

    private static readonly Lazy<ModelRepository> Large = new(() => LargeModelGenerator.Generate());

    /// <summary>Runs <paramref name="work"/> and fails when it takes longer than <paramref name="limitMs"/> (thresholds are generous: ~10× a laptop).</summary>
    private static T Within<T>(int limitMs, string what, Func<T> work)
    {
        var sw = Stopwatch.StartNew();
        var result = work();
        Assert.True(sw.ElapsedMilliseconds < limitMs, $"{what} took {sw.ElapsedMilliseconds} ms (limit {limitMs} ms)");
        return result;
    }

    [Fact]
    public void Generator_Is_Deterministic_And_Realistically_Sized()
    {
        var small = new LargeModelOptions { Scale = 0.05 };
        var a = ProjectFile.Serialize(LargeModelGenerator.Generate(small));
        var b = ProjectFile.Serialize(LargeModelGenerator.Generate(small));
        Assert.Equal(a, b);

        var m = Large.Value;
        Assert.InRange(m.Count, 55_000, 70_000);
        Assert.Equal(5_000, m.OfKind(ElementKind.Requirement).Count());
        Assert.Equal(300, m.Diagrams.Count());
        Assert.All(m.Diagrams.Take(20), d => Assert.NotEmpty(d.Nodes));
        foreach (var k in new[] { ElementKind.Satisfy, ElementKind.Verify, ElementKind.DeriveReqt, ElementKind.Trace, ElementKind.Port,
                                  ElementKind.Activity, ElementKind.StateMachine, ElementKind.Stereotype, ElementKind.ValidationRule })
            Assert.NotEmpty(m.OfKind(k));
        Assert.Contains(m.AllElements, e => e.TaggedValues.Any());
        Assert.NotEmpty(SuspectLinks.All(m));
        Assert.DoesNotContain(new ModelValidator().Validate(m), i => i.RuleId is "MER-003" or "MER-005");   // well-formed
    }

    [Fact]
    public void Sixty_Thousand_Elements_Save_Load_And_Query_Fast()
    {
        var m = Large.Value;
        var json = Within(5_000, "save", () => ProjectFile.Serialize(m));
        var back = Within(8_000, "load", () => ProjectFile.Deserialize(json));
        Assert.Equal(m.Count, back.Count);
        Assert.Equal(json, ProjectFile.Serialize(back));

        var ids = m.AllElements.Select(e => e.Id).ToArray();
        Within(1_000, "100k Find", () => { var n = 0; for (var i = 0; i < 100_000; i++) if (m.Find(ids[i % ids.Length]) != null) n++; return n; });
        Within(500, "OfKind x1000", () => { var n = 0; for (var i = 0; i < 1_000; i++) n += m.OfKind(ElementKind.Requirement).Count(); return n; });
        Within(1_000, "relationship lookups", () => m.OfKind(ElementKind.Requirement).Sum(r => m.Incoming(r, ElementKind.Satisfy).Count() + m.Outgoing(r).Count()));
        Within(1_000, "qualified names", () => m.AllElements.Sum(e => e.QualifiedName.Length));

        Within(5_000, "validation", () => new ModelValidator().Validate(m).Count);
        Within(5_000, "traceability table", () => TraceabilityTable.Build(m).Count);
        Within(5_000, "query", () => ModelQuery.Parse("kind:Requirement where priority = high and no:in:satisfy").Run(m).Count);
        Within(5_000, "generic table", () => ModelTables.Build(m, new TableDefinition { Query = "kind:Requirement", Columns = TableDefinition.DefaultColumns(ElementKind.Requirement) }).Count);
    }

    [Fact]
    public void Sixty_Thousand_Elements_Edit_Undo_And_Replicate_Fast()
    {
        var json = ProjectFile.Serialize(Large.Value);
        var m = ProjectFile.Deserialize(json);
        var replica = ProjectFile.Deserialize(json);
        var ops = new List<ModelOp>();
        m.OpEmitted += ops.Add;
        var pkg = m.OfKind(ElementKind.Package).First(p => p.Name == "Area 01.1");
        Within(5_000, "1000 edits", () =>
        {
            for (var i = 0; i < 500; i++) m.Create(pkg, ElementKind.Requirement, "New " + i);
            foreach (var r in m.OfKind(ElementKind.Requirement).Take(500).ToList()) m.Set(r, FieldKeys.Text, "changed");
            return 0;
        });
        Within(5_000, "apply remote ops", () => { foreach (var op in ops) replica.ApplyRemote(op); return ops.Count; });
        Assert.Equal(m.Count, replica.Count);
        Within(5_000, "undo + redo", () => { while (m.History.CanUndo) m.History.Undo(); while (m.History.CanRedo) m.History.Redo(); return 0; });

        var sub = m.OfKind(ElementKind.Package).First(p => p.Owner?.Name == "Structure" && p.Name != "Interfaces");
        var before = m.Count;
        Within(5_000, "delete subsystem + undo", () => { m.Delete(sub); m.History.Undo(); return 0; });
        Assert.Equal(before, m.Count);
    }

    [Fact]
    public void Sixty_Thousand_Elements_Diff_Merge_And_Exchange_Fast()
    {
        var json = ProjectFile.Serialize(Large.Value);
        var @base = ProjectFile.Deserialize(json);
        var ours = ProjectFile.Deserialize(json);
        var theirs = ProjectFile.Deserialize(json);
        foreach (var e in ours.OfKind(ElementKind.Block).Take(300).ToList()) ours.Set(e, FieldKeys.Documentation, "ours");
        foreach (var e in theirs.OfKind(ElementKind.Block).Skip(200).Take(300).ToList()) theirs.Set(e, FieldKeys.Documentation, "theirs");
        foreach (var t in theirs.OfKind(ElementKind.TestCase).Take(20).ToList()) theirs.Delete(t);

        var diff = Within(10_000, "diff", () => ModelDiff.Compare(@base, ours));
        Assert.Equal(300, diff.Count);
        var merge = Within(15_000, "three-way merge", () => ModelMerge.Merge(@base, ours, theirs));
        Assert.Equal(100, merge.Conflicts.Count);

        var xmi = Within(15_000, "XMI export", () => XmiExchange.ToXml(@base).ToString());
        var (imported, _) = Within(15_000, "XMI import", () => XmiExchange.Import(System.Xml.Linq.XDocument.Parse(xmi)));
        Assert.True(imported.Count > 50_000);

        var text = Within(10_000, "SysML v2 export", () => SysMLv2Text.Export(@base));
        var doc = Within(10_000, "SysML v2 parse", () => SysMLv2Parser.Parse(text));
        Assert.False(doc.HasErrors);
    }
}
