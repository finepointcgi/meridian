using System.Diagnostics;
using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Meridian.Benchmarks;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;

// Usage:
//   dotnet run -c Release --project benchmarks/Meridian.Benchmarks -- --quick [--scale 1.0]
//       one Stopwatch pass over every scenario, printed as a table (fast; good for before/after comparisons)
//   dotnet run -c Release --project benchmarks/Meridian.Benchmarks -- [BenchmarkDotNet args, e.g. --filter *Validate*]
//       the full BenchmarkDotNet suite (LargeModelBenchmarks)
if (args.Length == 2 && args[0] == "--dump")
{
    // Writes the generated model as .meridian (handy for loading it into the app, or comparing serializer output).
    var m = Meridian.Core.Samples.LargeModelGenerator.Generate();
    ProjectFile.Save(m, args[1]);
    return;
}
if (args.Length == 2 && args[0] == "--code-sync")
{
    // Code sync of a folder's C# code: reading with and without method bodies, mapping with and without behaviour diagrams.
    CodeSyncBench.Run(args[1]);
    return;
}
if (args.Contains("--quick"))
{
    var scale = args.SkipWhile(a => a != "--scale").Skip(1).Select(s => double.Parse(s, CultureInfo.InvariantCulture)).FirstOrDefault(1.0);
    var only = args.SkipWhile(a => a != "--only").Skip(1).FirstOrDefault();
    var repeat = args.SkipWhile(a => a != "--repeat").Skip(1).Select(int.Parse).FirstOrDefault(3);
    // --elements N: a Cameo-scale project (LargeModelOptions.Cameo) instead of the scaled default mix, plus the store, session,
    // report, derivation and diagram scenarios. --postgres also runs the store scenarios against a throwaway Testcontainers server.
    var elements = args.SkipWhile(a => a != "--elements").Skip(1).Select(int.Parse).FirstOrDefault(0);
    await QuickRunner.RunAsync(scale, only, repeat, elements, args.Contains("--postgres"));
    return;
}
BenchmarkSwitcher.FromAssembly(typeof(Scenarios).Assembly).Run(args);

namespace Meridian.Benchmarks
{
    public static class QuickRunner
    {
        /// <summary>Times every scenario (best of <paramref name="repeat"/> runs after a warm-up); <paramref name="only"/> filters by name.</summary>
        public static async Task RunAsync(double scale, string? only = null, int repeat = 3, int elements = 0, bool postgres = false)
        {
            var sw = Stopwatch.StartNew();
            var s = new Scenarios(elements > 0 ? LargeModelOptions.Cameo(elements) : new LargeModelOptions { Scale = scale });
            Console.WriteLine($"Model: {s.Model.AllElements.Count():N0} elements, {s.Model.Relationships.Count():N0} relationships, " +
                              $"{s.Model.Diagrams.Count():N0} diagrams, {s.Requirements.Count:N0} requirements; .meridian {s.Json.Length / 1024 / 1024.0:0.0} MB, managed heap {GC.GetTotalMemory(true) / 1024 / 1024:N0} MB " +
                              $"(setup {sw.Elapsed.TotalSeconds:0.0} s)");
            Console.WriteLine();
            Console.WriteLine($"{"Scenario",-34} {"ms",10}");
            Console.WriteLine(new string('-', 46));

            bool Skip(string name) => only != null && !name.Contains(only, StringComparison.OrdinalIgnoreCase);
            void Time(string name, Func<int> work)
            {
                if (Skip(name)) return;
                work();   // JIT / warm-up
                var best = double.MaxValue;
                var r = 0;
                for (var i = 0; i < repeat; i++)
                {
                    var t = Stopwatch.StartNew();
                    r = work();
                    best = Math.Min(best, t.Elapsed.TotalMilliseconds);
                }
                Console.WriteLine($"{name,-34} {best,10:0.0}   ({r:N0})");
            }
            // Runs once on a fresh copy of the model (the copy is made outside the timed region).
            void TimeOnce(string name, Func<ModelRepository, int> work)
            {
                if (Skip(name)) return;
                var copy = ProjectFile.Deserialize(s.Json);
                var t = Stopwatch.StartNew();
                var r = work(copy);
                Console.WriteLine($"{name,-34} {t.Elapsed.TotalMilliseconds,10:0.0}   ({r:N0})");
            }

            Time("Generate model", s.Generate);
            Time("Save .meridian (serialize)", s.Save);
            Time("Load .meridian (deserialize)", s.Load);
            Time("Load + 10% library (mounted)", s.LoadWithLibrary);
            Time("Save, library mounted", s.SaveWithLibrary);
            Time("Validate, library mounted", s.ValidateWithLibrary);
            TimeOnce("Apply 2.6k remote ops (replica)", s.ApplyOps);
            TimeOnce("500 edits + undo all + redo all", m => s.UndoRedo(m));
            TimeOnce("Delete subsystem + undo", s.DeleteUndo);
            Time("Find x100k", s.Find);
            Time("OfKind x150", s.OfKind);
            Time("Relationship lookups (all reqs)", s.Relationships);
            Time("Qualified names (all elements)", s.QualifiedNames);
            Time("Validate (full suite)", s.Validate);
            Time("Suspect links", s.SuspectLinks);
            Time("Query rules", s.QueryRuleCheck);
            Time("Traceability table build", s.TraceTable);
            Time("Traceability chains", s.TraceChains);
            Time("Query evaluation (2 queries)", s.Query);
            Time("Generic table (requirements)", s.Table);
            var (ours, theirs) = s.Branches();
            Time("ModelDiff", () => s.Diff(ours));
            Time("Three-way ModelMerge", () => s.Merge(ours, theirs));
            Time("XMI export", s.XmiExport);
            Time("XMI import", s.XmiImport);
            Time("SysML v2 export", s.SysmlExport);
            Time("SysML v2 parse", s.SysmlParse);
            TimeOnce("Workspace.Health (+200 edits)", m => s.Health(m));
            if (elements == 0) return;

            Console.WriteLine();
            await using var pg = postgres ? await BenchPostgres.StartAsync() : null;
            await CameoScenarios.RunAsync(s, Time, TimeOnce, Skip, pg?.ConnectionString);
        }
    }

    [MemoryDiagnoser]
    [SimpleJob(launchCount: 1, warmupCount: 1, iterationCount: 5)]
    public class LargeModelBenchmarks
    {
        private Scenarios _s = null!;
        private ModelRepository _ours = null!, _theirs = null!;

        [Params(1.0)] public double Scale { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _s = new Scenarios(Scale);
            (_ours, _theirs) = _s.Branches();
        }

        [Benchmark] public int Save() => _s.Save();
        [Benchmark] public int Load() => _s.Load();
        [Benchmark] public int ApplyRemoteOps() => _s.ApplyOps(ProjectFile.Deserialize(_s.Json));
        [Benchmark] public int UndoRedo() => _s.UndoRedo(ProjectFile.Deserialize(_s.Json));
        [Benchmark] public int Find() => _s.Find();
        [Benchmark] public int OfKind() => _s.OfKind();
        [Benchmark] public int Relationships() => _s.Relationships();
        [Benchmark] public int Validate() => _s.Validate();
        [Benchmark] public int TraceTable() => _s.TraceTable();
        [Benchmark] public int Query() => _s.Query();
        [Benchmark] public int Diff() => _s.Diff(_ours);
        [Benchmark] public int Merge() => _s.Merge(_ours, _theirs);
        [Benchmark] public int XmiExport() => _s.XmiExport();
        [Benchmark] public int XmiImport() => _s.XmiImport();
        [Benchmark] public int SysmlExport() => _s.SysmlExport();
        [Benchmark] public int SysmlParse() => _s.SysmlParse();
        [Benchmark] public int Health() => _s.Health(ProjectFile.Deserialize(_s.Json));
    }
}
