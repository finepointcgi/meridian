using Bunit;
using Meridian.Core.Model;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>The explorer's search respects the active filter chip (All / Diagrams / Requirements / Structure / Behavior).</summary>
public class ModelTreeSearchFilterTests : TestContext
{
    private readonly Workspace _ws;

    public ModelTreeSearchFilterTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ws = Services.GetRequiredService<Workspace>();
        var m = new ModelRepository("M");
        var pkg = m.Create(m.Root, ElementKind.Package, "Overview package");
        var block = m.Create(pkg, ElementKind.Block, "Overview block");
        m.Create(block, ElementKind.Operation, "Overview");
        m.Create(block, ElementKind.Operation, "OverviewOf");
        m.CreateDiagram(pkg, DiagramType.BlockDefinition, "Overview diagram");
        m.Create(pkg, ElementKind.Requirement, "Overview requirement");
        _ws.UseLocalModel(m);
        RenderComponent<MudBlazor.MudPopoverProvider>();
    }

    private static void Chip(IRenderedComponent<ModelTree> cut, string name) =>
        cut.FindAll(".chips .chip").First(c => c.TextContent.Trim() == name).Click();

    [Fact]
    public void Search_Results_Only_Show_What_The_Active_Filter_Lets_Through()
    {
        var cut = RenderComponent<ModelTree>();
        cut.Find("input").Input("Overview");
        cut.WaitForAssertion(() => Assert.Contains("6 results", cut.Markup));

        Chip(cut, "Diagrams");
        cut.WaitForAssertion(() => Assert.Contains("1 result in Diagrams", cut.Markup));
        Assert.Contains("Overview diagram", cut.Markup);
        Assert.DoesNotContain("OverviewOf", cut.Markup);
        Assert.DoesNotContain("Overview block", cut.Markup);

        Chip(cut, "Requirements");
        cut.WaitForAssertion(() => Assert.Contains("1 result in Requirements", cut.Markup));
        Assert.Contains("Overview requirement", cut.Markup);
        Assert.DoesNotContain("Overview diagram", cut.Markup);

        Chip(cut, "Structure");
        cut.WaitForAssertion(() => Assert.Contains("1 result in Structure", cut.Markup));
        Assert.Contains("Overview block", cut.Markup);

        Chip(cut, "Behavior");
        cut.WaitForAssertion(() => Assert.Contains("0 results in Behavior", cut.Markup));

        Chip(cut, "All");
        cut.WaitForAssertion(() => Assert.Contains("6 results", cut.Markup));
    }

    [Fact]
    public void Typing_With_A_Filter_Already_On_Searches_Within_It()
    {
        var cut = RenderComponent<ModelTree>();
        Chip(cut, "Diagrams");
        cut.Find("input").Input("Overview");
        cut.WaitForAssertion(() => Assert.Contains("1 result in Diagrams", cut.Markup));
        Assert.DoesNotContain("OverviewOf", cut.Markup);
    }
}
