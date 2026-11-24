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
/// UI responsiveness with a 60,000-element model (LargeModelGenerator): the model tree and the traceability panel must
/// render and re-render quickly. Thresholds are generous (~10× a laptop) so they only catch regressions in kind.
/// </summary>
[Collection(TimingCollection.Name)]
public class LargeModelUiTests : TestContext
{
    private static readonly Lazy<string> LargeJson = new(() => ProjectFile.Serialize(LargeModelGenerator.Generate()));

    private readonly Workspace _ws;
    private readonly ITestOutputHelper _out;

    public LargeModelUiTests(ITestOutputHelper output)
    {
        _out = output;
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ws = Services.GetRequiredService<Workspace>();
        _ws.UseLocalModel(ProjectFile.Deserialize(LargeJson.Value));
        RenderComponent<MudBlazor.MudPopoverProvider>();
    }

    private long Time(string what, int limitMs, Action work)
    {
        var sw = Stopwatch.StartNew();
        work();
        _out.WriteLine($"{what}: {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < limitMs, $"{what} took {sw.ElapsedMilliseconds} ms (limit {limitMs} ms)");
        return sw.ElapsedMilliseconds;
    }

    [Fact]
    public void Model_Tree_Renders_And_Updates_Quickly()
    {
        IRenderedComponent<ModelTree>? cut = null;
        Time("tree first render", 5_000, () => cut = RenderComponent<ModelTree>());
        var rows = cut!.FindAll(".mtree-row");
        Assert.InRange(rows.Count, 2, 200);   // root + top-level packages; nothing below a collapsed branch

        // Selecting a deeply nested element reveals it (its ancestors expand).
        var deep = _ws.Model.OfKind(ElementKind.Port).First();
        Time("select deep element", 5_000, () => _ws.Select(deep));
        cut!.WaitForAssertion(() => Assert.Contains(cut.FindAll(".mtree-row.sel"), r => r.TextContent.Contains(deep.Name)));

        // Collapse / expand through the chevron.
        var toggles = cut.FindAll(".mtree-row .mtree-toggle.open");
        Assert.NotEmpty(toggles);
        Time("collapse branch", 5_000, () => toggles[^1].Click());

        // Edits re-render only what changed; 100 renames stay fast.
        var target = _ws.Model.OfKind(ElementKind.Block).First();
        Time("100 edits with tree mounted", 10_000, () =>
        {
            for (var i = 0; i < 100; i++) _ws.Model.Set(target, FieldKeys.Documentation, "edit " + i);
        });
        Time("100 selections with tree mounted", 10_000, () =>
        {
            foreach (var b in _ws.Model.OfKind(ElementKind.Requirement).Take(100)) _ws.Select(b);
        });
    }

    [Fact]
    public void Traceability_Panel_Renders_And_Pages_Quickly()
    {
        IRenderedComponent<TraceabilityPanel>? cut = null;
        Time("traceability first render", 10_000, () => cut = RenderComponent<TraceabilityPanel>());
        Assert.InRange(cut!.FindAll("table.tt tbody tr").Count, 1, TraceabilityPanel.PageSize);
        Assert.Contains("of 5000 requirements", cut.Markup);

        Time("next page", 5_000, () => cut.Find(".pager button[title='Next page']").Click());
        Assert.Contains("201–400", cut.Find(".pager").TextContent);

        Time("100 selections with panel mounted", 10_000, () =>
        {
            foreach (var r in _ws.Model.OfKind(ElementKind.Requirement).Take(100)) _ws.Select(r);
        });
        Time("filter not verified", 5_000, () => cut.FindAll(".chip").Single(c => c.TextContent.Contains("Not verified")).Click());
    }

    [Fact]
    public void Workspace_Health_Is_Cached_By_Model_Version()
    {
        var first = _ws.Health;
        Time("200 selections reading health", 2_000, () =>
        {
            foreach (var r in _ws.Model.OfKind(ElementKind.Requirement).Take(200)) { _ws.Select(r); _ = _ws.Health; }
        });
        Assert.Equal(first, _ws.Health);
    }
}
