using System.Xml.Linq;
using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Core.Validation;
using Meridian.Serialization;
using Meridian.Web.Services;

namespace Meridian.Benchmarks;

/// <summary>
/// The measured operations, shared by the BenchmarkDotNet suite and the quick Stopwatch runner. Each scenario works on a
/// model produced by <see cref="LargeModelGenerator"/> and returns something so the work cannot be optimised away.
/// </summary>
public sealed class Scenarios
{
    public Scenarios(double scale = 1.0) : this(new LargeModelOptions { Scale = scale }) { }

    public Scenarios(LargeModelOptions options)
    {
        Options = options;
        Model = LargeModelGenerator.Generate(Options);
        Json = ProjectFile.Serialize(Model);
        Requirements = Model.OfKind(ElementKind.Requirement).ToList();
        Ids = Model.AllElements.Select(e => e.Id).ToArray();
        Xmi = XmiExchange.ToXml(Model).ToString(SaveOptions.DisableFormatting);
        Sysml = SysMLv2Text.Export(Model);
        Ops = RecordOps(ProjectFile.Deserialize(Json));
    }

    public LargeModelOptions Options { get; }
    public ModelRepository Model { get; }
    public string Json { get; }
    public string Xmi { get; }
    public string Sysml { get; }
    public IReadOnlyList<Element> Requirements { get; }
    public string[] Ids { get; }
    public IReadOnlyList<ModelOp> Ops { get; }

    public int Generate() => LargeModelGenerator.Generate(Options).AllElements.Count();

    // Used projects (docs/LIBRARIES.md): the largest top-level package of a model a tenth of the size, mounted read-only.
    private Meridian.Core.Libraries.LibraryPayload? _library;
    private ModelRepository? _withLibrary;

    public Meridian.Core.Libraries.LibraryPayload Library => _library ??= MakeLibrary();

    private Meridian.Core.Libraries.LibraryPayload MakeLibrary()
    {
        var source = LargeModelGenerator.Generate(new LargeModelOptions { Scale = Math.Max(0.01, Options.Scale * 0.1), Seed = 99 });
        var pkg = source.Root.OwnedElements.Where(e => e.Kind == ElementKind.Package).MaxBy(p => p.Descendants().Count())!;
        var info = new Meridian.Core.Libraries.LibraryInfo("bench", Guid.NewGuid(), "Bench library", pkg.Id, Meridian.Core.Libraries.LibraryPinKind.Version, "1", "v1", "");
        return new(info, LibraryContent.Extract(source, pkg.Id)!);
    }

    public int LoadWithLibrary() => ProjectFile.Deserialize(Json, [Library]).Count;
    public int SaveWithLibrary() => ProjectFile.Serialize(_withLibrary ??= ProjectFile.Deserialize(Json, [Library])).Length;
    public int ValidateWithLibrary() => new Meridian.Core.Validation.ModelValidator().Validate(_withLibrary ??= ProjectFile.Deserialize(Json, [Library])).Count;
    public int Save() => ProjectFile.Serialize(Model).Length;
    public int Load() => ProjectFile.Deserialize(Json).AllElements.Count();

    /// <summary>A realistic editing burst (renames, new elements, moves, shape moves) recorded as replication ops.</summary>
    private static List<ModelOp> RecordOps(ModelRepository m)
    {
        var ops = new List<ModelOp>();
        m.OpEmitted += ops.Add;
        var rng = new Random(7);
        var all = m.AllElements.Where(e => e.Owner != null && e is not Relationship && e is not Diagram).ToList();
        var pkgs = m.OfKind(ElementKind.Package).ToList();
        for (var i = 0; i < 2_000; i++) m.Set(all[rng.Next(all.Count)], FieldKeys.Documentation, "edit " + i);
        for (var i = 0; i < 300; i++) m.Create(pkgs[rng.Next(pkgs.Count)], ElementKind.Block, "New block " + i);
        var blocks = m.OfKind(ElementKind.Block).ToList();
        for (var i = 0; i < 100; i++)
        {
            var b = blocks[rng.Next(blocks.Count)];
            var to = pkgs[rng.Next(pkgs.Count)];
            if (b.Owner != to) m.Move(b, to);
        }
        foreach (var d in m.Diagrams.Take(50))
        foreach (var n in d.Nodes.Take(4))
            m.SetBounds(d, new Dictionary<DiagramNode, Bounds> { [n] = n.Bounds.Offset(10, 10) });
        m.OpEmitted -= ops.Add;
        return ops;
    }

    /// <summary>Applies the recorded op burst to a fresh replica (what every collaborator does).</summary>
    public int ApplyOps(ModelRepository replica)
    {
        var changes = 0;
        replica.Changed += (_, _) => changes++;
        foreach (var op in Ops) replica.ApplyRemote(op);
        return changes;
    }

    /// <summary>Local edits through the undoable API: N renames, undo them all, redo them all.</summary>
    public int UndoRedo(ModelRepository m, int n = 500)
    {
        var rng = new Random(3);
        var all = m.AllElements.Where(e => e.Owner != null && e is not Relationship).ToList();
        for (var i = 0; i < n; i++) m.Set(all[rng.Next(all.Count)], FieldKeys.Documentation, "u" + i);
        while (m.History.CanUndo) m.History.Undo();
        while (m.History.CanRedo) m.History.Redo();
        return all.Count;
    }

    /// <summary>Deleting a whole subsystem (thousands of elements with external links and views) and undoing it.</summary>
    public int DeleteUndo(ModelRepository m)
    {
        var sub = m.OfKind(ElementKind.Package).First(p => p.Owner?.Name == "Structure" && p.Name != "Interfaces");
        m.Delete(sub);
        m.History.Undo();
        return m.AllElements.Count();
    }

    public int Find()
    {
        var hits = 0;
        for (var i = 0; i < 100_000; i++) if (Model.Find(Ids[i % Ids.Length]) != null) hits++;
        return hits;
    }

    public int OfKind()
    {
        var n = 0;
        for (var i = 0; i < 50; i++)
            n += Model.OfKind(ElementKind.Requirement).Count() + Model.OfKind(ElementKind.Block).Count() + Model.Diagrams.Count();
        return n;
    }

    public int Relationships()
    {
        var n = 0;
        foreach (var r in Requirements)
            n += Model.Incoming(r, ElementKind.Satisfy).Count() + Model.Incoming(r, ElementKind.Verify).Count() + Model.Outgoing(r).Count();
        return n + Model.Relationships.Count();
    }

    public int QualifiedNames() => Model.AllElements.Sum(e => e.QualifiedName.Length);

    public int Validate() => new ModelValidator().Validate(Model).Count;
    public int SuspectLinks() => Meridian.Core.Analysis.SuspectLinks.All(Model).Count();
    public int QueryRuleCheck() => new QueryRules().Check(Model).Count();

    public int TraceTable() => TraceabilityTable.Build(Model).Count;
    public int TraceChains() => TraceabilityTable.Chains(Model).Count;

    public int Query() =>
        ModelQuery.Parse("kind:Requirement where priority = high and no:in:satisfy").Run(Model).Count
        + ModelQuery.Parse("kind:Block where mass > 50").Run(Model).Count;

    public int Table() =>
        ModelTables.Build(Model, new TableDefinition { Query = "kind:Requirement", Columns = TableDefinition.DefaultColumns(ElementKind.Requirement) }).Count;

    public int Diff(ModelRepository after) => ModelDiff.Compare(Model, after).Count;

    public int Merge(ModelRepository ours, ModelRepository theirs) => ModelMerge.Merge(Model, ours, theirs).Conflicts.Count;

    /// <summary>Two divergent edited copies of the model, for diff and merge.</summary>
    public (ModelRepository Ours, ModelRepository Theirs) Branches()
    {
        var ours = ProjectFile.Deserialize(Json);
        var theirs = ProjectFile.Deserialize(Json);
        Edit(ours, 11);
        Edit(theirs, 12);
        return (ours, theirs);

        static void Edit(ModelRepository m, int seed)
        {
            var rng = new Random(seed);
            var all = m.AllElements.Where(e => e.Owner != null && e is not Relationship && e is not Diagram).ToList();
            for (var i = 0; i < 500; i++) m.Set(all[rng.Next(all.Count)], FieldKeys.Documentation, $"branch {seed} edit {i}");
            var pkg = m.OfKind(ElementKind.Package).Skip(seed).First();
            for (var i = 0; i < 100; i++) m.Create(pkg, ElementKind.Block, $"B{seed}-{i}");
            foreach (var victim in m.OfKind(ElementKind.TestCase).Skip(seed * 10).Take(20).ToList()) m.Delete(victim);
        }
    }

    public int XmiExport() => XmiExchange.ToXml(Model).ToString(SaveOptions.DisableFormatting).Length;
    public int XmiImport() => XmiExchange.Import(XDocument.Parse(Xmi)).Model.AllElements.Count();

    public int SysmlExport() => SysMLv2Text.Export(Model).Length;
    public int SysmlParse() => SysMLv2Parser.Parse(Sysml).Members.Count;

    /// <summary>The status-bar health figure: first computation, then 200 reads with an edit between each.</summary>
    public int Health(ModelRepository m, int edits = 200)
    {
        var ws = new Workspace();
        ws.UseLocalModel(m);
        var h = ws.Health.Errors;
        var target = m.OfKind(ElementKind.Block).First();
        for (var i = 0; i < edits; i++)
        {
            m.Set(target, FieldKeys.Documentation, "h" + i);
            h += ws.Health.Warnings;
        }
        return h;
    }
}
