using System.Diagnostics;
using Bunit;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Xunit.Abstractions;

namespace Meridian.Web.Tests;

/// <summary>
/// UI timings with a Cameo-scale model (opt-in: MERIDIAN_HEAVY=1; MERIDIAN_HEAVY_ELEMENTS sets the size, default 200,000):
/// what the workbench does when a project opens (replica, default diagrams, tree), revealing a deep element, and opening
/// the 500-shape diagram on the canvas. Prints the numbers recorded in docs/PERFORMANCE.md; limits are generous.
/// </summary>
public class CameoUiTimings : TestContext
{
    private readonly ITestOutputHelper _out;

    public CameoUiTimings(ITestOutputHelper output)
    {
        _out = output;
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        Services.AddSingleton<ImageCache>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private void Time(string what, int limitMs, Action work)
    {
        var sw = Stopwatch.StartNew();
        work();
        _out.WriteLine($"{what}: {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < limitMs, $"{what} took {sw.ElapsedMilliseconds} ms (limit {limitMs} ms)");
    }

    [SkippableFact]
    public void Workbench_Pieces_Open_A_Cameo_Scale_Project()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("MERIDIAN_HEAVY") is "1" or "true", "Set MERIDIAN_HEAVY=1 to run the Cameo-scale UI timings.");
        var elements = int.TryParse(Environment.GetEnvironmentVariable("MERIDIAN_HEAVY_ELEMENTS"), out var n) ? n : 200_000;
        var json = ProjectFile.Serialize(LargeModelGenerator.Generate(LargeModelOptions.Cameo(elements)));
        var ws = Services.GetRequiredService<Workspace>();
        RenderComponent<MudBlazor.MudPopoverProvider>();

        ModelRepository? m = null;
        Time("replica (deserialize)", 10_000, () => m = ProjectFile.Deserialize(json));
        Time("install + open default diagrams", 5_000, () => { ws.UseLocalModel(m!); ws.OpenDefaultDiagrams(); });
        IRenderedComponent<ModelTree>? tree = null;
        Time("model tree first render", 5_000, () => tree = RenderComponent<ModelTree>());
        var deep = m!.OfKind(ElementKind.Block).Where(b => b.Name.Contains("item")).OrderByDescending(b => b.AncestorsAndSelf().Count()).First();
        Time("reveal deepest element in tree", 5_000, () => ws.Select(deep));
        var t = tree!;
        t.WaitForAssertion(() => Assert.Contains(t.FindAll(".mtree-row.sel"), r => r.TextContent.Contains(deep.Name)));
        Time("model search (300 hits)", 2_000, () => _ = m.Search("telemetry").Take(300).Count());

        var big = m.Diagrams.OrderByDescending(d => d.Nodes.Count).First();
        DiagramController? c = null;
        Time("open 500-shape diagram (controller)", 2_000, () => { c = ws.Open(big); c.SetZoom(1); });
        IRenderedComponent<DiagramView>? view = null;
        Time($"canvas first render ({big.Nodes.Count} shapes, {big.Edges.Count} paths)", 10_000, () => view = RenderComponent<DiagramView>(p => p.Add(x => x.C, c!)));
        Time("canvas re-render after a shape move", 10_000, () =>
        {
            m.SetBounds(big, new Dictionary<DiagramNode, Bounds> { [big.Nodes[0]] = big.Nodes[0].Bounds.Offset(20, 0) });
            view!.Render();
        });
    }
}
