using Bunit;
using Meridian.Core.Model;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>The inspector's colour rules editor.</summary>
public class ColourRulesSectionTests : TestContext
{
    public ColourRulesSectionTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private (Workspace Ws, Diagram D) Setup()
    {
        var m = new ModelRepository("Car");
        var d = m.CreateDiagram(m.Root, DiagramType.BlockDefinition, "Structure");
        var engine = m.Create(m.Root, ElementKind.Block, "Engine");
        m.Set(engine, FieldKeys.TagPrefix + "ASIL", "D");
        m.AddNode(d, engine, new Bounds(0, 0, 160, 80));
        m.AddNode(d, m.Create(m.Root, ElementKind.Block, "Wheel"), new Bounds(240, 0, 160, 80));
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(m);
        return (ws, d);
    }

    [Fact]
    public void Adds_Edits_Reorders_And_Removes_Rules()
    {
        var (ws, d) = Setup();
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = RenderComponent<ColourRulesSection>(p => p.Add(x => x.E, d));
        Assert.Contains("Colour shapes by their data", cut.Markup);

        cut.Find("[data-testid=colour-rule-add] button, button[data-testid=colour-rule-add]").Click();
        var rules = ColorRules.Of(d);
        Assert.Single(rules);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=colour-rule]")));
        // The new rule opens for editing.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input[data-testid=colour-rule-query]")));

        cut.Find("input[data-testid=colour-rule-query]").Change("where tag:ASIL = D");
        cut.WaitForAssertion(() => Assert.Equal("where tag:ASIL = D", ColorRules.Of(d)[0].Query));
        Assert.Equal(["1"], cut.FindAll(".cr-count").Select(e => e.TextContent.Trim()));

        cut.Find("input[data-testid=colour-rule-label]").Change("Safety critical");
        cut.WaitForAssertion(() => Assert.Equal("Safety critical", ColorRules.Of(d)[0].Label));
        Assert.Contains("Safety critical", cut.Find(".cr-label").TextContent);

        // An invalid condition says so.
        cut.Find("input[data-testid=colour-rule-query]").Change("where (");
        cut.WaitForAssertion(() => Assert.Contains("invalid", cut.Find(".cr-count").TextContent));

        ws.Model.AddColorRule(d, new ColorRule("Blocks", "kind:Block", Fill: "#00ff00"));
        cut.Render();
        Assert.Equal(2, cut.FindAll("[data-testid=colour-rule]").Count);
        cut.FindAll("button[aria-label='Move up']")[1].Click();
        cut.WaitForAssertion(() => Assert.Equal(["Blocks", "Safety critical"], ColorRules.Of(d).Select(r => r.Label)));

        cut.FindAll("button[data-testid=colour-rule-remove], [data-testid=colour-rule-remove] button")[0].Click();
        cut.WaitForAssertion(() => Assert.Equal(["Safety critical"], ColorRules.Of(d).Select(r => r.Label)));
        ws.Model.History.Undo();
        Assert.Equal(2, ColorRules.Of(d).Count);
    }

    [Fact]
    public void Shows_Nothing_For_Other_Elements()
    {
        var (ws, d) = Setup();
        var cut = RenderComponent<ColourRulesSection>(p => p.Add(x => x.E, d.Nodes[0].Element));
        Assert.DoesNotContain("Colour rules", cut.Markup);
        _ = ws;
    }
}
