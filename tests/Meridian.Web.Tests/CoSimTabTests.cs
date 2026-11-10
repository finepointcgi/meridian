using System.Text;
using Bunit;
using Meridian.Core.Model;
using Meridian.Serialization;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

public class CoSimTabTests : TestContext
{
    private const string Lag = """
        <fmiModelDescription fmiVersion="2.0" modelName="Lag" guid="{g1}">
          <CoSimulation modelIdentifier="lag"/>
          <ModelVariables>
            <ScalarVariable name="T" valueReference="0" causality="parameter" variability="fixed"><Real start="1" unit="s"/></ScalarVariable>
            <ScalarVariable name="u" valueReference="1" causality="input"><Real start="0" unit="m"/></ScalarVariable>
            <ScalarVariable name="y" valueReference="2" causality="output"><Real unit="m"/></ScalarVariable>
          </ModelVariables>
        </fmiModelDescription>
        """;

    public CoSimTabTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private void Configure(bool local, bool? enabled = null)
    {
        var settings = new Dictionary<string, string?>();
        if (enabled != null) settings["Meridian:Fmi:Enabled"] = enabled.Value ? "true" : "false";
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        Services.AddSingleton(new MeridianHostInfo(local, Path.Combine(Path.GetTempPath(), "meridian-web-tests", Guid.NewGuid().ToString("N")), null));
        RenderComponent<MudBlazor.MudPopoverProvider>();
    }

    private static ModelRepository Model(string secondUnit = "m")
    {
        var m = new ModelRepository("M");
        var lag = FmiImport.CreateBlock(m, m.Root, FmiImport.Read(new MemoryStream(Encoding.UTF8.GetBytes(Lag))));
        var other = FmiImport.CreateBlock(m, m.Root, FmiImport.Read(new MemoryStream(Encoding.UTF8.GetBytes(Lag.Replace("unit=\"m\"", $"unit=\"{secondUnit}\"")))));
        var sys = m.Create(m.Root, ElementKind.Block, "Plant");
        var a = m.Create(sys, ElementKind.Property, "a"); m.Set(a, FieldKeys.Type, lag.Id);
        var b = m.Create(sys, ElementKind.Property, "b"); m.Set(b, FieldKeys.Type, other.Id);
        var r = m.Connect(ElementKind.Connector, lag.OwnedElements.Single(e => e.Name == "y"), other.OwnedElements.Single(e => e.Name == "u"), sys);
        m.Set(r, FieldKeys.SourcePart, a.Id);
        m.Set(r, FieldKeys.TargetPart, b.Id);
        return m;
    }

    [Fact]
    public void Server_Default_Blocks_Running_Fmus()
    {
        Configure(local: false);
        Services.GetRequiredService<Workspace>().UseLocalModel(Model());
        var cut = RenderComponent<CoSimTab>();
        Assert.Contains("turned off on this server", cut.Markup);
        Assert.Contains("a.y → b.u", cut.Markup);
        Assert.Contains("b.T", cut.Markup);   // parameters are listed
        Assert.True(cut.FindAll("button").Single(b => b.TextContent.Contains("Run")).HasAttribute("disabled"));
        Assert.Contains("not a sandbox", cut.Markup);
    }

    [Fact]
    public void Desktop_Mode_Allows_Running_And_Asks_For_Missing_Fmu_Files()
    {
        Configure(local: true);
        Services.GetRequiredService<Workspace>().UseLocalModel(Model());
        var cut = RenderComponent<CoSimTab>();
        Assert.DoesNotContain("turned off on this server", cut.Markup);
        Assert.Contains("Attach .fmu", cut.Markup);
        Assert.False(cut.FindAll("button").Single(b => b.TextContent.Contains("Run")).HasAttribute("disabled"));
    }

    [Fact]
    public void Unit_Mismatch_Is_Shown_And_Prevents_Running()
    {
        Configure(local: true);
        Services.GetRequiredService<Workspace>().UseLocalModel(Model(secondUnit: "s"));
        var cut = RenderComponent<CoSimTab>();
        Assert.Contains("unit mismatch, m → s", cut.Markup);
        Assert.True(cut.FindAll("button").Single(b => b.TextContent.Contains("Run")).HasAttribute("disabled"));
    }

    [Fact]
    public void Simulation_Panel_Has_A_Co_Simulation_Tab()
    {
        Configure(local: false, enabled: true);
        Services.GetRequiredService<Workspace>().UseLocalModel(Model());
        var cut = RenderComponent<SimulationPanel>();
        Assert.Contains("Co-simulation", cut.Markup);
    }

    [Fact]
    public void Chart_Draws_One_Path_Per_Series_With_Hover_And_Threshold()
    {
        var time = Enumerable.Range(0, 2001).Select(i => i * 0.005).ToList();
        var sin = time.Select(Math.Sin).ToList();
        var cos = time.Select(Math.Cos).ToList();
        var svg = CoSimChart.Svg(time, [new("sin", "#111", sin), new("cos", "#222", cos)], 0, 10, hover: 100, threshold: 0.5);
        Assert.Equal(2, svg.Split("<path ").Length - 1);
        Assert.Contains("stroke-dasharray=\"6 4\"", svg);   // threshold line
        Assert.Contains("<circle", svg);                     // hover markers
        Assert.Equal(400, CoSimChart.NearestIndex(time, 2.0001));
        Assert.Equal(5, CoSimChart.TimeAt(CoSimChart.Left + CoSimChart.PlotWidth / 2.0, 0, 10), 6);
    }
}
