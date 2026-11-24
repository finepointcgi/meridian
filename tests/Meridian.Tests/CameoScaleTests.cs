using System.Diagnostics;
using System.Text;
using Meridian.Core.Derivation;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>
/// Cameo-scale performance: a fast perf budget in the normal suite (a ~30,000-element model, thresholds far above what a
/// laptop needs, so they only catch regressions in kind), equivalence checks for the optimised paths, and the 200,000-element
/// budgets behind MERIDIAN_HEAVY=1. Numbers and how to run the benchmarks: docs/PERFORMANCE.md.
/// </summary>
[Collection(TimingCollection.Name)]
public class CameoScaleTests
{
    private static readonly Lazy<ModelRepository> Medium = new(() => LargeModelGenerator.Generate(LargeModelOptions.Cameo(30_000)));

    private static T Within<T>(int limitMs, string what, Func<T> work)
    {
        var sw = Stopwatch.StartNew();
        var result = work();
        Assert.True(sw.ElapsedMilliseconds < limitMs, $"{what} took {sw.ElapsedMilliseconds} ms (limit {limitMs} ms)");
        return result;
    }

    [Fact]
    public void Cameo_Generator_Makes_Deep_Packages_Use_Cases_And_Big_Diagrams()
    {
        var m = Medium.Value;
        Assert.InRange(m.Count, 27_000, 36_000);
        Assert.NotEmpty(m.OfKind(ElementKind.UseCase));
        Assert.Contains(m.Diagrams, d => d.Nodes.Count == 500 && d.Edges.Count > 100);
        Assert.Contains(m.Diagrams, d => d.Nodes.Count is >= 50 and < 100);
        Assert.True(m.OfKind(ElementKind.Package).Max(p => p.AncestorsAndSelf().Count()) >= 10, "deep package breakdown");
    }

    /// <summary>Perf budget (normal suite): coarse limits on the paths fixed for Cameo-scale models.</summary>
    [Fact]
    public void Perf_Budget_Medium_Model()
    {
        var m = Medium.Value;
        var json = Within(3_000, "serialize", () => ProjectFile.Serialize(m));
        var back = Within(6_000, "deserialize", () => ProjectFile.Deserialize(json));
        Assert.Equal(m.Count, back.Count);

        var big = back.Diagrams.OrderByDescending(d => d.Nodes.Count).First();
        // The canvas path (viewport-culled); exhaustive routing took ~10 s here in a Debug build before the pruned scoring.
        Within(8_000, "render 500-shape diagram (viewport)", () =>
        {
            var sb = new StringBuilder();
            SvgRenderer.RenderBody(sb, back, big, n => n.Bounds, null, null, new Bounds(0, 0, 1600, 900));
            return sb.Length;
        });
        var sources = back.AllElements.Where(e => e.Kind is ElementKind.UseCase or ElementKind.Activity).Take(200).ToList();
        Within(3_000, "⌘K derivation offers", () => sources.Sum(e => DiagramDerivation.Offers(back, e).Count));
        var victim = back.OfKind(ElementKind.Requirement).First();
        Within(2_000, "delete one element + undo", () => { back.Delete(victim); back.History.Undo(); return 0; });
    }

    [Fact]
    public void Pruned_Orthogonal_Routing_Picks_What_Exhaustive_Scoring_Picks()
    {
        var rng = new Random(5);
        for (var run = 0; run < 300; run++)
        {
            var obstacles = Enumerable.Range(0, rng.Next(0, 120))
                .Select(_ => new Bounds(rng.Next(-200, 2000), rng.Next(-200, 2000), rng.Next(0, 300), rng.Next(0, 200))).ToList();
            var s = new Bounds(rng.Next(0, 1800), rng.Next(0, 1800), 120, 80);
            var t = new Bounds(rng.Next(0, 1800), rng.Next(0, 1800), 120, 80);
            Assert.Equal(Notation.OrthogonalAvoidingReference(s, t, obstacles), Notation.OrthogonalAvoiding(s, t, obstacles));
        }
    }

    [Fact]
    public void Derived_Element_Index_Follows_Edits()
    {
        var m = SampleModels.Vehicle();
        var uc = m.Create(m.Root, ElementKind.UseCase, "Drive");
        m.Set(uc, FieldKeys.Documentation, "1. Driver starts the car\n2. System checks the battery");
        Assert.Empty(DiagramDerivation.Existing(m, DerivationKind.ActivityFromUseCase, uc));
        var result = DiagramDerivation.Apply(m, DiagramDerivation.Plan(m, DerivationKind.ActivityFromUseCase, uc));
        var made = DiagramDerivation.Existing(m, DerivationKind.ActivityFromUseCase, uc).ToList();
        Assert.NotEmpty(made);
        Assert.Equal(m.AllElements.Where(e => e.Get(DeriveFields.Source) == uc.Id && e.Get(DeriveFields.Key).Length > 0
                                              && e.Get(DeriveFields.Kind) == DerivationKinds.Id(DerivationKind.ActivityFromUseCase)), made);
        m.History.Undo();
        Assert.Empty(DiagramDerivation.Existing(m, DerivationKind.ActivityFromUseCase, uc));
        m.History.Redo();
        Assert.Equal(made.Count, DiagramDerivation.Existing(m, DerivationKind.ActivityFromUseCase, uc).Count());
        _ = result;
    }

    [Fact]
    public async Task Sqlite_Versions_Are_Compressed_And_Old_Text_Rows_Still_Load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meridian-vb-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteStore(path);
            await store.MigrateAsync();
            var user = await store.CreateUserAsync("vb", "VB", "correct horse battery");
            var json = ProjectFile.Serialize(Medium.Value);
            var project = await store.CreateProjectAsync("P", json, user.Id);
            await store.CommitVersionAsync(project, "big", json, user.Id);
            await store.CommitVersionAsync(project, "small", "{\"format\":\"meridian\"}", user.Id);
            var versions = await store.ListVersionsAsync(project);
            Assert.Equal(json, await store.LoadVersionAsync(project, versions.Single(v => v.Message == "big").Id));
            Assert.Equal("{\"format\":\"meridian\"}", await store.LoadVersionAsync(project, versions.Single(v => v.Message == "small").Id));

            var blob = (byte[])VersionBlob.Encode(json);
            Assert.True(blob.Length < Encoding.UTF8.GetByteCount(json) / 4, $"{blob.Length} bytes for {json.Length} chars");
            Assert.Equal(json, VersionBlob.Decode(blob));
            Assert.Equal("legacy", VersionBlob.Decode("legacy"));   // rows written as text by earlier releases
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(f); } catch (IOException) { }
        }
    }

    /// <summary>200,000-element budgets (opt-in: MERIDIAN_HEAVY=1). Limits are ~5–10× the M4 Pro numbers in docs/PERFORMANCE.md.</summary>
    [SkippableFact]
    public void Heavy_200k_Budgets()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("MERIDIAN_HEAVY") is "1" or "true", "Set MERIDIAN_HEAVY=1 to run the 200,000-element budgets.");
        var m = LargeModelGenerator.Generate(LargeModelOptions.Cameo(200_000));
        Assert.InRange(m.Count, 190_000, 215_000);
        var json = Within(2_000, "serialize", () => ProjectFile.Serialize(m));
        var back = Within(5_000, "deserialize", () => ProjectFile.Deserialize(json));
        var big = back.Diagrams.OrderByDescending(d => d.Nodes.Count).First();
        Within(3_000, "render 500-shape diagram", () => SvgRenderer.Render(back, big).Length);
        Within(1_000, "⌘K derivation offers", () => back.AllElements.Where(e => e.Kind is ElementKind.UseCase or ElementKind.Activity).Take(200).Sum(e => DiagramDerivation.Offers(back, e).Count));
        Within(15_000, "HTML report", () => HtmlReport.Generate(back).Length);
        var victim = back.OfKind(ElementKind.Requirement).First();
        Within(1_000, "delete one element + undo", () => { back.Delete(victim); back.History.Undo(); return 0; });
    }
}
