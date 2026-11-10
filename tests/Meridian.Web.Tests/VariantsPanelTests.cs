using Bunit;
using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

public class VariantsPanelTests : TestContext
{
    public VariantsPanelTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        RenderComponent<MudBlazor.MudPopoverProvider>();
    }

    private Workspace Ws(ModelRepository m)
    {
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(m);
        return ws;
    }

    [Fact]
    public void Variants_Tab_Shows_Tree_Validity_And_Resolution()
    {
        Ws(ProductLineSample.Create());
        var cut = RenderComponent<VariantsTab>();
        Assert.Equal(12, cut.FindAll(".var-feature").Count);
        Assert.Contains("36 valid products", cut.Markup);
        Assert.Contains("Valid configuration", cut.Markup);   // "Eco" is selected first
        Assert.Contains("FuelTank", cut.Markup);
    }

    [Fact]
    public void Ticking_A_Feature_Updates_The_Configuration_With_Propagation()
    {
        var ws = Ws(ProductLineSample.Create());
        var cut = RenderComponent<VariantsTab>();
        var eco = ws.Model.OfKind(ElementKind.Configuration).Single(c => c.Name == "Eco");
        var row = cut.FindAll(".var-feature").First(r => r.TextContent.Contains("Combustion"));
        row.QuerySelector("input")!.Change(true);
        var selected = Variability.Selected(ws.Model, eco).Select(f => f.Name).ToList();
        Assert.Contains("Combustion", selected);
        Assert.DoesNotContain("Electric", selected);              // alternative sibling dropped
        Assert.Empty(Variability.Validate(ws.Model, eco));
        cut.WaitForAssertion(() => Assert.Contains("Valid configuration", cut.Markup));
    }

    [Fact]
    public void Variants_Tab_Offers_The_Example_On_An_Empty_Model()
    {
        var ws = Ws(new ModelRepository("Empty"));
        var cut = RenderComponent<VariantsTab>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Add example product line")).Click();
        Assert.Equal(12, ws.Model.OfKind(ElementKind.Feature).Count());
        cut.WaitForAssertion(() => Assert.Equal(12, cut.FindAll(".var-feature").Count));
    }

    [Fact]
    public void Trade_Study_Tab_Ranks_And_Charts()
    {
        var m = ProductLineSample.Create();
        var configs = m.OfKind(ElementKind.Configuration).ToList();
        TradeStudies.Create(m, m.Root, "Pick", new TradeStudyDefinition
        {
            Alternatives = configs.Select(c => c.Id).ToList(),
            Criteria = [new TradeCriterion { Name = "cost", Direction = "minimize", Source = "tag" }, new TradeCriterion { Name = "range", Source = "tag" }],
        });
        Ws(m);
        var cut = RenderComponent<TradeStudyTab>();
        Assert.Contains("<svg class=\"chart\"", cut.Markup);
        Assert.Contains("Sensitivity", cut.Markup);
        Assert.Equal(2, cut.FindAll(".trade-criteria tbody tr").Count);
        cut.FindAll("button").First(b => b.TextContent.Contains("Add criterion")).Click();
        Assert.Equal(3, TradeStudyDefinition.Parse(m.OfKind(ElementKind.TradeStudy).Single()).Criteria.Count);
    }

    [Fact]
    public void Simulation_Panel_Has_Timed_Monte_Carlo_And_Fmi_Tabs()
    {
        Ws(SampleModels.Vehicle());
        var cut = RenderComponent<SimulationPanel>();
        Assert.Contains("Timed (discrete event)", cut.Markup);
        Assert.Contains("Monte Carlo", cut.Markup);
        Assert.Contains("FMI import", cut.Markup);
    }

    [Fact]
    public void Timed_Tab_Runs_And_Draws_A_Timeline()
    {
        var m = new ModelRepository("T");
        var act = m.Create(m.Root, ElementKind.Activity, "Job");
        var i = m.Create(act, ElementKind.InitialNode);
        var a = m.Create(act, ElementKind.Action, "Work");
        m.Set(a, ExtFieldKeys.Duration, "4s");
        var f = m.Create(act, ElementKind.ActivityFinalNode);
        m.Connect(ElementKind.ControlFlow, i, a);
        m.Connect(ElementKind.ControlFlow, a, f);
        Ws(m);
        var cut = RenderComponent<TimedSimTab>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Start")).Click();
        cut.FindAll("button").First(b => b.TextContent.Contains("Run to completion")).Click();
        cut.WaitForAssertion(() => Assert.Contains("t = 4 s", cut.Markup));
        Assert.Contains("<svg class=\"chart\"", cut.Markup);
        Assert.Contains("Work", cut.Markup);
    }

    [Fact]
    public void Monte_Carlo_Tab_Shows_Statistics()
    {
        var m = SampleModels.Vehicle();
        var vehicle = m.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle");
        m.Set(vehicle.OwnedElements.Single(p => p.Name == "mass"), ExtFieldKeys.Distribution, "normal(1450, 100)");
        Ws(m);
        var cut = RenderComponent<MonteCarloTab>();
        Assert.Contains("mass ~ normal(1450, 100)", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent.Contains("Run")).Click();
        cut.WaitForAssertion(() => Assert.Contains("Std deviation", cut.Markup), TimeSpan.FromSeconds(20));
        Assert.Contains("P5 / P50 / P95", cut.Markup);
    }
}
