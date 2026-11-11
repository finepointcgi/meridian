using System.Diagnostics;
using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Data;
using Meridian.Data.History;
using Meridian.Serialization;
using Meridian.Serialization.History;

namespace Meridian.Benchmarks;

/// <summary>
/// Element history at scale (docs/PERFORMANCE.md, "Element history"): a project with MERIDIAN_HISTORY_VERSIONS (default 100)
/// committed versions, each changing a few dozen elements. Measures commit time, the cost of indexing one commit (in the
/// background, after the commit), the lazy backfill of every version, and opening an element's history once indexed.
/// </summary>
public static class HistoryScenarios
{
    public static async Task RunAsync(string provider, IMeridianStore store, Scenarios s)
    {
        var versions = int.TryParse(Environment.GetEnvironmentVariable("MERIDIAN_HISTORY_VERSIONS"), out var n) && n > 1 ? n : 100;
        await store.MigrateAsync();
        var user = await store.CreateUserAsync("hist" + Guid.NewGuid().ToString("N")[..8], "History", "correct horse battery");
        var project = await store.CreateProjectAsync("History", s.Json, user.Id);
        var model = ProjectFile.Deserialize(s.Json);
        var rng = new Random(42);
        var blocks = model.OfKind(ElementKind.Block).ToList();
        var target = model.OfKind(ElementKind.Requirement).First();
        var indexer = ElementHistoryIndexer.For(store);

        var commit = new List<double>();
        var enqueue = new List<double>();
        var index = new List<double>();
        for (var i = 0; i < versions; i++)
        {
            if (i % 5 == 1) model.Rename(target, $"{target.Name.Split(" #")[0]} #{i}");
            if (i % 7 == 2) model.Set(target, FieldKeys.Text, $"The system shall do thing number {i} within {i * 3} ms.");
            if (i % 10 == 3) model.Connect(ElementKind.Satisfy, blocks[rng.Next(blocks.Count)], target);
            for (var k = 0; k < 25; k++) model.Set(blocks[rng.Next(blocks.Count)], FieldKeys.Documentation, $"Edited in version {i + 1} ({k})");
            var json = ProjectFile.Serialize(model);
            var t = Stopwatch.StartNew();
            var v = await store.CommitBranchVersionAsync(project, null, $"Version {i + 1}", json, user.Id);
            commit.Add(t.Elapsed.TotalMilliseconds);
            t.Restart();
            indexer.VersionCommitted(project, v.Id, json);   // what VersionControl.CommitAsync adds to the commit path
            enqueue.Add(t.Elapsed.TotalMilliseconds);
            t.Restart();
            await indexer.WhenIdleAsync(project);            // the background work (not on the commit path)
            index.Add(t.Elapsed.TotalMilliseconds);
        }
        Line($"{provider}: commit version (median of {versions})", Median(commit), $"(p95 {P95(commit):0})");
        Line($"{provider}: history: commit path addition", Median(enqueue), $"(max {enqueue.Max():0.000} ms: hands the JSON to the indexer)");
        Line($"{provider}: history: index one commit (background)", Median(index), $"(p95 {P95(index):0}; first {index[0]:0}; parse + compare + write)");
        var c = indexer.LastBreakdown;
        Line($"{provider}: history: index one commit, breakdown", indexer.LastVersionCost.TotalMilliseconds,
             $"(load {c.Load.TotalMilliseconds:0}, parse {c.Parse.TotalMilliseconds:0}, compare {c.Compare.TotalMilliseconds:0}, write {c.Write.TotalMilliseconds:0} ms; {c.Rows} rows)");

        var full = await ElementHistoryService.LoadAsync(store, project, null, target, model, AccessView.Full, includeRelationships: true);
        var open = new List<double>();
        for (var i = 0; i < 7; i++)
        {
            var t = Stopwatch.StartNew();
            full = await ElementHistoryService.LoadAsync(store, project, null, target, model, AccessView.Full, includeRelationships: true);
            open.Add(t.Elapsed.TotalMilliseconds);
        }
        Line($"{provider}: history: open (indexed, owner)", Median(open), $"({full.Entries.Count} entries over {versions} versions, with relationships)");

        // A viewer with a hidden package (rows checked against their model).
        var hiddenPkg = model.Root.OwnedElements.First(e => e.Kind == ElementKind.Package && !e.Descendants().Contains(target));
        var view = AccessView.For([new(hiddenPkg.Id, PrincipalKind.User, user.Id, PackageAccess.None)], user.Id, [], PackageAccess.Edit, PackageAccess.Manage);
        var visible = ProjectFile.Deserialize(s.Json);
        ModelRedactor.Redact(visible, view);
        var vt = visible.Find(target.Id) ?? target;
        open.Clear();
        ElementHistory? redacted = null;
        for (var i = 0; i < 7; i++)
        {
            var t = Stopwatch.StartNew();
            redacted = await ElementHistoryService.LoadAsync(store, project, null, vt, visible, view, includeRelationships: true);
            open.Add(t.Elapsed.TotalMilliseconds);
        }
        Line($"{provider}: history: open (viewer, hidden package)", Median(open), $"({redacted!.Entries.Count} entries)");

        var tl = Stopwatch.StartNew();
        var raw = await store.LoadVersionAsync(project, full.Head!.Id);
        Line($"{provider}: history: load one stored version", tl.Elapsed.TotalMilliseconds, $"({raw!.Length / 1024 / 1024} M chars)");
        var reader = new VersionReader(store, project, AccessView.Full);
        var entry = full.Entries.First(e => e.Change == ElementChangeKind.Changed && !e.IsConnected);
        var td = Stopwatch.StartNew();
        var diff = await ElementHistoryService.DiffAsync(reader, entry);
        Line($"{provider}: history: open a diff (2 versions)", td.Elapsed.TotalMilliseconds, $"({diff.Rows.Count} fields)");
        td.Restart();
        var redactedDiff = await ElementHistoryService.DiffAsync(new VersionReader(store, project, view), redacted.Entries.First(e => e.Change == ElementChangeKind.Changed));
        Line($"{provider}: history: open a diff (viewer, redacted)", td.Elapsed.TotalMilliseconds, $"({redactedDiff.Rows.Count} fields)");
        td.Restart();
        model.Set(target, FieldKeys.Text, "Uncommitted text");
        var head = full.Head;
        var unc = await ElementHistoryService.UncommittedAsync(new VersionReader(store, project, AccessView.Full), head, target, model);
        Line($"{provider}: history: uncommitted check", td.Elapsed.TotalMilliseconds, $"({unc?.Fields.Count ?? 0} fields)");

        // Lazy backfill of every version (what the first history request of an existing project starts in the background).
        await store.ResetElementChangesAsync(project);
        var fresh = ElementHistoryIndexer.For(store);
        var tb = Stopwatch.StartNew();
        var marks = new List<string>();
        void Mark(Guid _, IndexProgress p) => marks.Add($"{p.Indexed}@{tb.ElapsedMilliseconds}({fresh.LastBreakdown.Load.TotalMilliseconds:0}/{fresh.LastBreakdown.Parse.TotalMilliseconds:0})");
        fresh.ProgressChanged += Mark;
        await fresh.IndexAllAsync(project);
        fresh.ProgressChanged -= Mark;
        if (Environment.GetEnvironmentVariable("MERIDIAN_HISTORY_TRACE") == "1") Console.WriteLine("  " + string.Join(" ", marks));
        var total = tb.Elapsed.TotalMilliseconds;
        Line($"{provider}: history: backfill {versions} versions", total, $"({total / versions:0} ms per version)");
        var b = fresh.LastBreakdown;
        Line($"{provider}: history: backfill, last version", fresh.LastVersionCost.TotalMilliseconds,
             $"(load {b.Load.TotalMilliseconds:0}, parse {b.Parse.TotalMilliseconds:0}, compare {b.Compare.TotalMilliseconds:0}, write {b.Write.TotalMilliseconds:0} ms; {b.Rows} rows)");

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var heap = GC.GetTotalMemory(true);
        var parse = Stopwatch.StartNew();
        var flat = FlatModel.Parse(s.Json);
        var parsed = parse.Elapsed.TotalMilliseconds;
        var size = (GC.GetTotalMemory(true) - heap) / 1024.0 / 1024;
        Line("History: parse a version (flat reader)", parsed, $"({flat.Count:N0} elements, {size:N0} MB held while cached)");
        GC.KeepAlive(flat);
        parse.Restart();
        _ = ProjectFile.Deserialize(s.Json);
        Line("History: deserialize a version (for comparison)", parse.Elapsed.TotalMilliseconds);
    }

    private static double Median(List<double> x) => x.Order().ElementAt(x.Count / 2);
    private static double P95(List<double> x) => x.Order().ElementAt(Math.Min(x.Count - 1, x.Count * 95 / 100));
    private static void Line(string name, double ms, string note = "") => Console.WriteLine($"{name,-40} {ms,10:0.0}   {note}");
}
