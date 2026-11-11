using Bunit;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>The Step simulation tab: the active configuration of a state machine with orthogonal regions, highlighting and errors.</summary>
public class SimulationStepTabTests : TestContext
{
    public SimulationStepTabTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        RenderComponent<MudBlazor.MudPopoverProvider>();
    }

    private static ModelRepository ForkJoin(bool brokenChoice = false)
    {
        var m = new ModelRepository("Sim");
        var sm = m.Create(m.Root, ElementKind.StateMachine, "Controller");
        Element S(string name, Element? owner = null) => m.Create(owner ?? sm, ElementKind.State, name);
        Element P(string kind, string name) { var e = m.Create(sm, ElementKind.Pseudostate, name); m.Set(e, FieldKeys.Subtype, kind); return e; }
        Relationship T(Element a, Element b, string? name = null) { var r = m.Connect(ElementKind.Transition, a, b); if (name != null) m.Rename(r, name); return r; }
        var idle = S("Idle");
        T(m.Create(sm, ElementKind.InitialState), idle);
        var fork = P(Pseudostates.Fork, "Split");
        T(idle, fork, "start");
        var op = S("Operating");
        var heat = S("Heating", op);
        var mon = S("Monitoring", op);
        T(fork, heat);
        T(fork, mon);
        var join = P(Pseudostates.Join, "Merge");
        T(heat, join, "warm");
        T(mon, join, "ok");
        T(join, S("Done"));
        if (brokenChoice)
        {
            var c = P(Pseudostates.Choice, "Pick");
            T(idle, c, "pick");
            var guarded = T(c, S("Never"));
            m.Set(guarded, FieldKeys.Guard, "1 > 2");
        }
        return m;
    }

    private IRenderedComponent<SimulationStepTab> Start(ModelRepository m, out Workspace ws)
    {
        ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(m);
        var cut = RenderComponent<SimulationStepTab>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Start").Click();
        return cut;
    }

    private static void Event(IRenderedComponent<SimulationStepTab> cut, string name) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == name).Click();

    [Fact]
    public void Shows_Every_Active_State_Of_Orthogonal_Regions_And_The_Waiting_Join()
    {
        var m = ForkJoin();
        var cut = Start(m, out var ws);
        Assert.Contains("Idle", cut.Find("[data-testid=sim-configuration]").TextContent);
        Event(cut, "start");
        var config = cut.Find("[data-testid=sim-configuration]").TextContent;
        Assert.Contains("Operating › Heating", config);
        Assert.Contains("Operating › Monitoring", config);
        foreach (var name in new[] { "Operating", "Heating", "Monitoring" })
            Assert.Contains(m.AllElements.Single(e => e.Name == name).Id, ws.SimHighlight);   // highlighted on the diagram

        Event(cut, "warm");
        config = cut.Find("[data-testid=sim-configuration]").TextContent;
        Assert.DoesNotContain("Heating", config);
        Assert.Contains("join Merge waiting", config);
        Assert.Contains(m.AllElements.Single(e => e.Name == "Merge").Id, ws.SimHighlight);
        Event(cut, "ok");
        Assert.Contains("Done", cut.Find("[data-testid=sim-configuration]").TextContent);
        Assert.Contains("join 'Merge' complete", cut.Find("pre.output").TextContent);
    }

    [Fact]
    public void A_Choice_Without_An_Enabled_Branch_Shows_The_Error()
    {
        var cut = Start(ForkJoin(brokenChoice: true), out _);
        Event(cut, "pick");
        Assert.Contains("no outgoing guard is true", cut.Find("[data-testid=sim-error]").TextContent);
        Assert.Contains("stopped on an error", cut.Find("[data-testid=sim-status]").TextContent);
    }
}
