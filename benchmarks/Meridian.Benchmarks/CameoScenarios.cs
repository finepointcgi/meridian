using System.Diagnostics;
using System.Text;
using Meridian.Core.CodeSync;
using Meridian.Core.Derivation;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Serialization.Reports;
using Meridian.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meridian.Benchmarks;

/// <summary>
/// The Cameo-scale scenarios that need more than the core model: stores (SQLite, optionally PostgreSQL), version storage,
/// the collaboration session with many clients, reports, the auto-diagrammer, code sync, derivation, the ⌘K offers and
/// opening a 500-shape diagram. Timings are printed by <see cref="QuickRunner"/>.
/// </summary>
public static class CameoScenarios
{
    public static async Task RunAsync(Scenarios s, Action<string, Func<int>> time, Action<string, Func<ModelRepository, int>> timeOnce, Func<string, bool> skip,
                                      string? postgres)
    {
        var m = s.Model;

        // ---------------------------------------------------------------- memory of one replica (what every open tab holds)
        if (!skip("replica"))
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var before = GC.GetTotalMemory(true);
            var replica = ProjectFile.Deserialize(s.Json);
            var after = GC.GetTotalMemory(true);
            Line("Replica memory (one deserialized model)", 0, $"({(after - before) / 1024.0 / 1024:N0} MB for {replica.Count:N0} elements)");
            GC.KeepAlive(replica);
        }

        // ---------------------------------------------------------------- diagrams
        var biggest = m.Diagrams.OrderByDescending(d => d.Nodes.Count).First();
        Console.WriteLine($"  (largest diagram: {biggest.Nodes.Count} shapes, {biggest.Edges.Count} paths)");
        time("Open 500-shape diagram (controller)", () => { var ws = new Workspace(); ws.UseLocalModel(m); ws.Open(biggest); return ws.Active!.Diagram.Nodes.Count; });
        time("Render 500-shape diagram (SVG, all)", () => SvgRenderer.Render(m, biggest).Length);
        time("Render 500-shape diagram (viewport)", () =>
        {
            var sb = new StringBuilder();
            SvgRenderer.RenderBody(sb, m, biggest, n => n.Bounds, null, null, new Bounds(0, 0, 1600, 900));
            return sb.Length;
        });

        // ---------------------------------------------------------------- search, palette, derivation
        time("Model search (\"telemetry\")", () => m.Search("telemetry").Count());
        var sources = m.AllElements.Where(e => e.Kind is ElementKind.UseCase or ElementKind.Activity or ElementKind.Interaction).Take(200).ToList();
        time("⌘K derivation offers (200 sources)", () => sources.Sum(e => DiagramDerivation.Offers(m, e).Count));
        time("⌘K element entries (sorted)", () => m.AllElements.Where(e => e is not Relationship && e is not Diagram && e.Kind != ElementKind.Model && !string.IsNullOrEmpty(e.Name))
                                                         .OrderBy(e => e.Name).Count());
        if (m.OfKind(ElementKind.UseCase).FirstOrDefault() is { } uc)
            timeOnce("Derive activity from use case", copy =>
            {
                var src = copy.Find(uc.Id)!;
                var plan = DiagramDerivation.Plan(copy, DerivationKind.ActivityFromUseCase, src);
                return DiagramDerivation.Apply(copy, plan).CreatedIds.Count;
            });
        time("Derivation stale check (all diagrams)", () => m.Diagrams.Count(d => DiagramDerivation.IsStale(m, d)));

        // ---------------------------------------------------------------- reports, auto diagrams, code sync
        time("HTML report (HtmlReport)", () => (int)Bench.HtmlReport(m));
        time("SRS template report (HTML)", () => (int)Bench.Template(m, ReportTemplates.Srs(), ReportFormat.Html));
        // Single runs with peak managed heap (above the pre-run baseline) and total allocations.
        if (!skip("report"))
        {
            Measure("Report peak: HTML report", () => Bench.HtmlReport(m));
            foreach (var (label, t) in new[] { ("SRS", ReportTemplates.Srs()), ("SDD", ReportTemplates.Sdd()) })
                foreach (var f in new[] { ReportFormat.Html, ReportFormat.Docx, ReportFormat.Pdf })
                {
                    if (label == "SRS" && f != ReportFormat.Html) continue;
                    Measure($"Report peak: {label} ({f})", () => Bench.Template(m, t, f));
                }
            // The whole 16k × blocks matrix, cell by cell (what the 2 GB string limit used to stop): streaming alone.
            Measure("Report peak: SDD (Html, full matrix)", () => Bench.Template(m, ReportTemplates.Sdd(), ReportFormat.Html, new ResolveOptions { MaxMatrixCells = int.MaxValue }));
        }
        time("AutoDiagrammer plan (whole model)", () => AutoDiagrammer.Plan(m).Diagrams.Count);
        timeOnce("Code-sync apply (300 types)", copy => CodeModelMapper.Apply(copy, SyntheticCode(300), new CodeSyncOptions { RepoId = "gh:bench", Commit = "c1", RootPackage = "Flight Software" }).Added.Count);

        // ---------------------------------------------------------------- stores
        if (!skip("store")) await StoreAsync("SQLite", () => Sqlite(), s);
        PostgresConnection = postgres;
        if (postgres != null && !skip("store")) await StoreAsync("PostgreSQL", () => (new PostgresStore(postgres), () => { }), s);

        // ---------------------------------------------------------------- collaboration session
        if (!skip("session")) await SessionAsync(s, 25);
    }

    private static string? PostgresConnection;

    private static (IMeridianStore Store, Action Cleanup) Sqlite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meridian-bench-{Guid.NewGuid():N}.db");
        return (new SqliteStore(path), () =>
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(f); } catch (IOException) { }
        });
    }

    /// <summary>Runs <paramref name="work"/> once and prints wall time, peak managed heap above the starting heap
    /// (sampled every 2 ms) and bytes allocated. Exceptions are printed instead of thrown.</summary>
    private static void Measure(string name, Func<long> work)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var baseline = GC.GetTotalMemory(true);
        var alloc0 = GC.GetTotalAllocatedBytes(true);
        long peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var now = GC.GetTotalMemory(false);
                if (now > Interlocked.Read(ref peak)) Interlocked.Exchange(ref peak, now);
                Thread.Sleep(2);
            }
        }) { IsBackground = true };
        sampler.Start();
        var t = Stopwatch.StartNew();
        string note;
        try
        {
            var bytes = work();
            note = $"output {bytes / 1024.0 / 1024:N1} MB";
        }
        catch (Exception ex) { note = $"FAILED {ex.GetType().Name}: {ex.Message}"; }
        var ms = t.Elapsed.TotalMilliseconds;
        stop.Cancel();
        sampler.Join();
        var alloc = GC.GetTotalAllocatedBytes(true) - alloc0;
        Line(name, ms, $"(peak heap +{(peak - baseline) / 1024.0 / 1024:N0} MB, allocated {alloc / 1024.0 / 1024:N0} MB, {note})");
    }

    private static void Line(string name, double ms, string note = "") => Console.WriteLine($"{name,-40} {ms,10:0.0}   {note}");

    private static async Task<double> Ms(Func<Task> work)
    {
        var t = Stopwatch.StartNew();
        await work();
        return t.Elapsed.TotalMilliseconds;
    }

    private static async Task StoreAsync(string provider, Func<(IMeridianStore, Action)> open, Scenarios s)
    {
        var (store, cleanup) = open();
        try
        {
            await store.MigrateAsync();
            var user = await store.CreateUserAsync("bench" + Guid.NewGuid().ToString("N")[..8], "Bench", "correct horse battery");
            Guid project = default;
            Line($"{provider}: create project", await Ms(async () => project = await store.CreateProjectAsync("Big", s.Json, user.Id)));
            Line($"{provider}: save project (autosave)", await Ms(() => store.SaveProjectAsync(project, s.Json, user.Id)));
            ModelRepository? loaded = null;
            Line($"{provider}: load + deserialize (cold)", await Ms(async () =>
            {
                var (st2, _) = provider == "SQLite" ? (store, (Action)(() => { })) : open();
                var row = await st2.LoadProjectAsync(project);
                loaded = ProjectFile.Deserialize(row!.Value.ModelJson);
            }), $"({loaded?.Count:N0} elements)");
            Line($"{provider}: load + deserialize (warm)", await Ms(async () => loaded = ProjectFile.Deserialize((await store.LoadProjectAsync(project))!.Value.ModelJson)));
            Line($"{provider}: load row only (warm)", await Ms(() => store.LoadProjectAsync(project)));

            var size0 = await DbSizeAsync(store);
            var commit = new List<double>();
            for (var i = 0; i < 5; i++) commit.Add(await Ms(() => store.CommitVersionAsync(project, "v" + i, s.Json, user.Id)));
            var size1 = await DbSizeAsync(store);
            Line($"{provider}: commit version (median of 5)", commit.Order().ElementAt(2),
                 size1 > 0 ? $"(+{(size1 - size0) / 5.0 / 1024 / 1024:0.0} MB per version, JSON {Encoding.UTF8.GetByteCount(s.Json) / 1024.0 / 1024:0.0} MB)" : "");
            var versions = await store.ListVersionsAsync(project);
            Line($"{provider}: load version", await Ms(async () => _ = (await store.LoadVersionAsync(project, versions[0].Id))!.Length));
        }
        finally
        {
            if (store is IAsyncDisposable d) await d.DisposeAsync();
            cleanup();
        }
    }

    private static async Task<long> DbSizeAsync(IMeridianStore store)
    {
        switch (store)
        {
            case SqliteStore:
                // Sum of the database file and its WAL (the bench database is the only one under this prefix).
                var files = Directory.GetFiles(Path.GetTempPath(), "meridian-bench-*.db*");
                return files.Sum(f => new FileInfo(f).Length);
            case PostgresStore when PostgresConnection != null:
                await using (var c = new Npgsql.NpgsqlConnection(PostgresConnection))
                {
                    await c.OpenAsync();
                    await using var cmd = new Npgsql.NpgsqlCommand("checkpoint; select pg_database_size(current_database())", c);
                    return (long)(await cmd.ExecuteScalarAsync())!;
                }
            default:
                return 0;
        }
    }

    /// <summary>A session with <paramref name="clients"/> tabs: join times, op apply + broadcast latency, snapshot cost.</summary>
    private static async Task SessionAsync(Scenarios s, int clients)
    {
        var (store, cleanup) = Sqlite();
        try
        {
            await store.MigrateAsync();
            var user = await store.CreateUserAsync("bench", "Bench", "correct horse battery");
            var project = await store.CreateProjectAsync("Big", s.Json, user.Id);
            var hub = new CollaborationHub(store, NullLogger<CollaborationHub>.Instance);
            var tabs = new List<Workspace>();
            var join = new List<double>();
            for (var i = 0; i < clients; i++)
            {
                var ws = new Workspace { User = user };
                join.Add(await Ms(() => ws.JoinAsync(hub, project, ProjectRole.Owner)));
                tabs.Add(ws);
            }
            Line("Session: first tab join (load + replica)", join[0]);
            Line($"Session: next tab join (median of {clients - 1})", join.Skip(1).Order().ElementAt((clients - 1) / 2));
            var session = tabs[0].Session!;
            var lat = new List<double>();
            var t = Stopwatch.StartNew();
            foreach (var op in s.Ops)
            {
                var o = Stopwatch.GetTimestamp();
                session.Submit(tabs[0].ParticipantId, user.Id, op);
                lat.Add(Stopwatch.GetElapsedTime(o).TotalMilliseconds);
            }
            lat.Sort();
            Line($"Session: submit + relay to {clients - 1} replicas", t.Elapsed.TotalMilliseconds,
                 $"({s.Ops.Count:N0} ops; p50 {lat[lat.Count / 2]:0.000} ms, p95 {lat[lat.Count * 95 / 100]:0.000} ms, max {lat[^1]:0.0} ms)");
            Line("Session: snapshot", await Ms(() => { _ = session.Snapshot().Length; return Task.CompletedTask; }));
            Line("Session: persist", await Ms(session.PersistAsync));
            GC.Collect();
            Line($"Session: managed heap with {clients} tabs", 0, $"({GC.GetTotalMemory(true) / 1024 / 1024:N0} MB)");
            foreach (var ws in tabs) ws.Dispose();
        }
        finally { cleanup(); }
    }

    /// <summary>A C# code model of <paramref name="types"/> classes in ten namespaces, each with fields, a part and a method.</summary>
    public static CodeModel SyntheticCode(int types)
    {
        var list = new List<CodeType>();
        for (var i = 0; i < types; i++)
        {
            var ns = $"Sat.Module{i % 10}";
            var next = i + 1 < types ? $"Sat.Module{(i + 1) % 10}.Unit{i + 1}" : null;
            var members = new List<CodeMember>
            {
                new("Mass", CodeMemberKind.Property, "double", null, false, "public"),
                new("Power", CodeMemberKind.Property, "double", null, false, "public"),
                new("Reset", CodeMemberKind.Method, "bool", null, false, "public"),
            };
            if (next != null) members.Add(new("Child", CodeMemberKind.Field, $"Unit{i + 1}", next, false, "private"));
            list.Add(new CodeType($"{ns}.Unit{i}", $"Unit{i}", ns, CodeTypeKind.Class, $"src/Unit{i}.cs", 1, "A unit.", [], [], members, []));
        }
        return new CodeModel("C#", list, []);
    }
}

/// <summary>A throwaway PostgreSQL server for the store benchmarks (Testcontainers, random host port; never a local server).</summary>
public sealed class BenchPostgres : IAsyncDisposable
{
    private readonly Testcontainers.PostgreSql.PostgreSqlContainer _container;
    private BenchPostgres(Testcontainers.PostgreSql.PostgreSqlContainer c) => _container = c;
    public string ConnectionString => _container.GetConnectionString();

    public static async Task<BenchPostgres> StartAsync()
    {
        var c = new Testcontainers.PostgreSql.PostgreSqlBuilder("postgres:17-alpine").Build();
        await c.StartAsync();
        return new BenchPostgres(c);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
