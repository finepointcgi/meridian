using System.Diagnostics;
using Meridian.Core.CodeSync;
using Meridian.Core.CodeSync.Conformance;
using Meridian.Core.Model;
using Meridian.Serialization.CodeSync;

namespace Meridian.Benchmarks;

/// <summary>
/// <c>--code-sync &lt;folder&gt;</c>: code sync of every .cs file under a folder (e.g. Meridian's own src), with and without
/// method bodies, and the mapping with and without behaviour diagrams. Best of three runs after a warm-up.
/// </summary>
public static class CodeSyncBench
{
    public static void Run(string folder)
    {
        var files = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Select(p => new CodeFile(Path.GetRelativePath(folder, p).Replace('\\', '/'), File.ReadAllText(p)))
            .Where(f => !CSharpCodeReader.IsGeneratedPath(f.Path)).ToList();
        Console.WriteLine($"{files.Count:N0} files, {files.Sum(f => (long)f.Content.Length) / 1024:N0} KB");

        static (double Ms, T Result) Best<T>(Func<T> work)
        {
            var result = work();
            var best = double.MaxValue;
            for (var i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                result = work();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            return (best, result);
        }

        var (plainMs, plain) = Best(() => new CSharpCodeReader { ReadBodies = false }.Read(files));
        var (bodiesMs, bodies) = Best(() => new CSharpCodeReader().Read(files));
        var methods = bodies.Types.SelectMany(t => t.Members).Count(m => m.Body != null);
        var statements = bodies.Types.SelectMany(t => t.Members).Sum(m => m.Body is { } b ? CodeBodies.Count(b.Statements) : 0);
        Console.WriteLine($"{plain.Types.Count:N0} types; {methods:N0} method bodies, {statements:N0} statements");
        Console.WriteLine($"read without bodies   {plainMs,10:N0} ms   packed {CodeModelJson.Pack(plain).Length / 1024:N0} KB");
        Console.WriteLine($"read with bodies      {bodiesMs,10:N0} ms   packed {CodeModelJson.Pack(bodies).Length / 1024:N0} KB   ({bodiesMs / plainMs:0.00}×)");

        var (mapOff, _) = Best(() => CodeModelMapper.Apply(new ModelRepository(), bodies, new CodeSyncOptions { RepoId = "r", Behaviour = CodeBehaviourMode.OnDemand }));
        CodeSyncChanges? changes = null;
        ModelRepository? model = null;
        var (mapOn, _) = Best(() =>
        {
            model = new ModelRepository();
            return changes = CodeModelMapper.Apply(model, bodies, new CodeSyncOptions { RepoId = "r", Behaviour = CodeBehaviourMode.EntryPoints });
        });
        var acts = model!.AllElements.Count(e => e.Kind == ElementKind.Activity);
        var seqs = model.AllElements.Count(e => e.Kind == ElementKind.Interaction);
        Console.WriteLine($"map (on demand)       {mapOff,10:N0} ms");
        Console.WriteLine($"map (entry points)    {mapOn,10:N0} ms   {acts} activities, {seqs} sequences");
        Console.WriteLine($"sync total            {plainMs + mapOff,10:N0} ms → {bodiesMs + mapOn,10:N0} ms ({(bodiesMs + mapOn) / (plainMs + mapOff):0.00}×)");
        foreach (var w in changes!.Warnings.Where(w => w.StartsWith("Behaviour", StringComparison.Ordinal))) Console.WriteLine(w);
        var (resync, again) = Best(() => CodeModelMapper.Apply(model, bodies, new CodeSyncOptions { RepoId = "r", Behaviour = CodeBehaviourMode.EntryPoints }));
        Console.WriteLine($"re-sync, same code    {resync,10:N0} ms   ({(again.IsEmpty ? "no changes" : "CHANGES")})");
    }
}
