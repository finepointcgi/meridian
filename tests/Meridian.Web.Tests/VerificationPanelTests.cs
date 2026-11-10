using Bunit;
using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

public class VerificationPanelTests : TestContext
{
    public VerificationPanelTests()
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

    private static Element Req(ModelRepository m, string id) => m.OfKind(ElementKind.Requirement).Single(r => r.Get(FieldKeys.RequirementId) == id);

    [Fact]
    public void Lists_Criteria_Then_Verifies_With_Summary_And_Drill_Down()
    {
        var ws = Ws(SampleModels.Vehicle());
        var cut = RenderComponent<VerificationPanel>();
        Assert.Equal(3, cut.FindAll("tr.row").Count);
        Assert.Equal(3, cut.FindAll(".verdict.none").Count);

        cut.Find("[data-testid=verify-run]").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr.row .verdict.pass").Count), TimeSpan.FromSeconds(10));
        Assert.Single(cut.FindAll("tr.row .verdict.fail"));
        var summary = cut.Find("[data-testid=verify-summary]").TextContent;
        Assert.Contains("2 pass", summary);
        Assert.Contains("1 fail", summary);
        Assert.Equal("pass", Req(ws.Model, "REQ-002").Get(VerificationKeys.Status));
        cut.WaitForAssertion(() => Assert.Contains("Verified 3 requirements", ws.Status));

        // Drill down into a row.
        cut.FindAll("tr.row").First(r => r.TextContent.Contains("REQ-003")).Click();
        var detail = cut.Find("tr.detail").TextContent;
        Assert.Contains("550 km", detail);
        Assert.Contains("Model version", detail);
        Assert.Contains("Parametric (FuelTank)", detail);

        // Filter by status.
        cut.FindAll(".vfy-summary > span").First(s => s.TextContent.Contains("fail")).Click();
        Assert.Single(cut.FindAll("tr.row"));
    }

    [Fact]
    public void Results_Turn_Stale_When_The_Model_Changes()
    {
        var ws = Ws(SampleModels.Vehicle());
        var cut = RenderComponent<VerificationPanel>();
        cut.Find("[data-testid=verify-run]").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr.row .verdict.pass").Count), TimeSpan.FromSeconds(10));
        var cap = ws.Model.OfKind(ElementKind.Property).Single(p => p.Name == "capacity");
        ws.Model.Set(cap, FieldKeys.DefaultValue, "70");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tr.row .verdict.stale")));
        Assert.Contains("1 stale", cut.Find("[data-testid=verify-summary]").TextContent);
    }

    [Fact]
    public void Editing_A_Criterion_Validates_It()
    {
        var ws = Ws(SampleModels.Vehicle());
        var cut = RenderComponent<VerificationPanel>();
        var input = cut.FindAll("tr.row").First(r => r.TextContent.Contains("REQ-002")).QuerySelector("input.vfy-crit")!;
        input.Change("accelTime <");
        Assert.Equal("accelTime <", Req(ws.Model, "REQ-002").Get(VerificationKeys.Criterion));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".vfy-err")));
    }

    [Fact]
    public void A_Run_Can_Be_Created_Configured_And_Run()
    {
        var ws = Ws(SampleModels.Vehicle());
        ws.Select(Req(ws.Model, "REQ-002"));
        var cut = RenderComponent<VerificationPanel>();
        cut.FindAll("button").First(b => b.TextContent.Contains("New run")).Click();
        var run = Assert.Single(Verification.Runs(ws.Model));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=verify-run-config]")));
        cut.Find("[data-testid=verify-run-config] input[placeholder^='mass']").Change("mass = 1.7 t");
        Assert.Equal("mass = 1.7 t", run.Get(VerificationKeys.Inputs));
        var vehicle = ws.Model.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle");
        cut.FindAll("[data-testid=verify-run-config] select")[1].Change(vehicle.Id);
        Assert.Equal(vehicle.Id, run.Get(VerificationKeys.Subject));

        Assert.Contains("Run “", cut.Find("[data-testid=verify-run]").TextContent);
        cut.Find("[data-testid=verify-run]").Click();
        cut.WaitForAssertion(() => Assert.Equal("fail", run.Get(ReqFields.Verdict)), TimeSpan.FromSeconds(10));
        Assert.Single(cut.FindAll("tr.row"));
        Assert.Equal("8.433 s", Req(ws.Model, "REQ-002").Get(VerificationKeys.Value));
    }

    [Fact]
    public void Requirements_Table_Shows_Criterion_And_Result()
    {
        var ws = Ws(SampleModels.Vehicle());
        Verification.RunAll(ws.Model, new VerificationOptions { Clone = VerificationView.Copy });
        var cut = RenderComponent<RequirementsPanel>();
        var results = cut.FindAll("[data-testid=req-result]");
        Assert.Contains(results, r => r.TextContent.Contains("pass") && r.TextContent.Contains("7.192 s"));
        Assert.Contains(results, r => r.TextContent.Contains("fail"));
        Assert.Contains(cut.FindAll("input"), i => i.GetAttribute("value") == "accelTime < 8 s");
    }

    [Fact]
    public void Diagram_Overlay_Colours_Requirements_By_Result()
    {
        var ws = Ws(SampleModels.Vehicle());
        var d = ws.Model.Diagrams.Single(x => x.Name == "Vehicle Requirements");
        Assert.Equal("", ws.Verification.Overlay(d, n => n.Bounds, 1));
        Verification.RunAll(ws.Model, new VerificationOptions { Clone = VerificationView.Copy });
        var svg = ws.Verification.Overlay(d, n => n.Bounds, 1);
        Assert.Equal(2, CountOf(svg, "vfy-mark pass"));
        Assert.Equal(1, CountOf(svg, "vfy-mark fail"));
        ws.Verification.SetShowOnDiagrams(false);
        Assert.Equal("", ws.Verification.Overlay(d, n => n.Bounds, 1));
    }

    private static int CountOf(string s, string what) => (s.Length - s.Replace(what, "").Length) / what.Length;
}

public class VariantEditorTests : TestContext
{
    public VariantEditorTests()
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

    private static Element F(ModelRepository m, string name) => m.OfKind(ElementKind.Feature).Single(f => f.Name == name);

    [Fact]
    public void Feature_Model_Editor_Edits_The_Tree_And_Constraints()
    {
        var ws = Ws(ProductLineSample.Create());
        var cut = RenderComponent<FeatureModelEditor>();
        Assert.Equal(12, cut.FindAll(".fm-row").Count);
        Assert.Contains("36 valid products", cut.Find("[data-testid=fm-facts]").TextContent);
        Assert.Contains("core: Car, Powertrain, Infotainment", cut.Find("[data-testid=fm-facts]").TextContent);

        // Add a child to Comfort, rename it, make it mandatory... then optional again.
        var comfort = cut.FindAll(".fm-row").Single(r => r.GetAttribute("data-feature") == "Comfort");
        comfort.QuerySelector("button[title^='Add a child']")!.Click();
        Assert.Equal(13, ws.Model.OfKind(ElementKind.Feature).Count());
        var child = ws.Model.OfKind(ElementKind.Feature).Single(f => f.Owner == F(ws.Model, "Comfort") && f.Name is not ("HeatedSeats" or "Sunroof"));
        Assert.Equal("optional", child.Get(ExtFieldKeys.FeatureKind));
        cut.WaitForAssertion(() => Assert.Equal(13, cut.FindAll(".fm-row").Count));
        var initial = child.Name;
        cut.FindAll(".fm-row").Single(r => r.GetAttribute("data-feature") == initial).QuerySelector("input.fm-name")!.Change("MassageSeats");
        Assert.Equal("MassageSeats", child.Name);
        cut.FindAll(".fm-row").Single(r => r.GetAttribute("data-feature") == "MassageSeats").QuerySelector("select")!.Change("or");
        Assert.Equal("or", child.Get(ExtFieldKeys.FeatureKind));

        // A cross-tree constraint: Sunroof excludes MassageSeats.
        var selects = cut.FindAll("select.dv-select");
        selects[0].Change(F(ws.Model, "Sunroof").Id);
        cut.FindAll("select.dv-select")[1].Change("excludes");
        cut.FindAll("select.dv-select")[2].Change(child.Id);
        cut.FindAll("button").First(b => b.TextContent.Contains("Add constraint")).Click();
        var rel = Assert.Single(ws.Model.Relationships, r => r.Kind == ElementKind.Excludes && r.Target == child);
        cut.WaitForAssertion(() => Assert.Contains("excludes MassageSeats", cut.Markup));
        cut.FindAll(".fm-cons").First(c => c.TextContent.Contains("MassageSeats")).QuerySelector("button")!.Click();
        Assert.False(ws.Model.Contains(rel));
    }

    [Fact]
    public void Configuration_Editor_Shows_Propagated_States_And_Switches_Alternatives()
    {
        var ws = Ws(ProductLineSample.Create());
        var touring = ws.Model.OfKind(ElementKind.Configuration).Single(c => c.Name == "Touring");
        var cut = RenderComponent<VariantsTab>();
        cut.InvokeAsync(() => cut.FindComponent<MudBlazor.MudSelect<string>>().Instance.ValueChanged.InvokeAsync(touring.Id));
        cut.WaitForAssertion(() => Assert.Contains("blocked", Row(cut, "Electric").OuterHtml));
        var electric = Row(cut, "Electric");
        Assert.Contains("excluded", electric.ClassName);

        electric.QuerySelector("input")!.Change(true);
        var selected = Variability.Selected(ws.Model, touring).Select(f => f.Name).ToList();
        Assert.Contains("Electric", selected);
        Assert.DoesNotContain("Combustion", selected);
        Assert.DoesNotContain("Towing", selected);         // excludes Electric
        Assert.Empty(Variability.Validate(ws.Model, touring));
    }

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<VariantsTab> cut, string feature) =>
        cut.FindAll(".var-feature").Single(r => r.QuerySelector(".link")?.TextContent == feature);

    [Fact]
    public void Show_Variant_Greys_Or_Hides_Absent_Elements()
    {
        var ws = Ws(ProductLineSample.Create());
        var eco = ws.Model.OfKind(ElementKind.Configuration).Single(c => c.Name == "Eco");
        var d = ws.Model.Diagrams.Single(x => x.Name == "Variant structure");
        Assert.Equal("", ws.VariantView.StyleFor(d));

        var cut = RenderComponent<VariantsTab>();
        cut.Find("[data-testid=variant-display]").Change("Grey");
        Assert.True(ws.VariantView.Active);
        Assert.Equal(eco.Id, ws.VariantView.ConfigurationId);
        var tankNode = d.Nodes.Single(n => n.Element.Name == "FuelTank");
        var batteryNode = d.Nodes.Single(n => n.Element.Name == "Battery");
        var css = ws.VariantView.StyleFor(d);
        Assert.Contains(tankNode.Id, css);
        Assert.DoesNotContain(batteryNode.Id, css);
        Assert.Contains("opacity", css);
        Assert.Equal(3, ws.VariantView.CountOut(d));     // FuelTank, NavigationUnit, TowHitch

        ws.VariantView.SetMode(VariantDisplay.Hide);
        Assert.Contains("display:none", ws.VariantView.StyleFor(d));

        // Feature diagrams grey the features the configuration does not select.
        var fm = ws.Model.Diagrams.Single(x => x.DiagramType == DiagramType.FeatureModel);
        Assert.Contains(fm.Nodes.Single(n => n.Element.Name == "Combustion").Id, ws.VariantView.StyleFor(fm));

        ws.VariantView.SetMode(VariantDisplay.Off);
        Assert.Equal("", ws.VariantView.StyleFor(d));
    }

    [Fact]
    public void Legend_Chip_Switches_Configuration_And_Mode()
    {
        var ws = Ws(ProductLineSample.Create());
        var d = ws.Model.Diagrams.Single(x => x.Name == "Variant structure");
        var touring = ws.Model.OfKind(ElementKind.Configuration).Single(c => c.Name == "Touring");
        ws.VariantView.SetMode(VariantDisplay.Grey);
        var cut = RenderComponent<DiagramLegends>(p => p.Add(x => x.Diagram, d));
        Assert.Contains("3 out", cut.Find("[data-testid=variant-chip]").TextContent);
        cut.Find("[data-testid=variant-chip] select").Change(touring.Id);
        Assert.Equal(touring.Id, ws.VariantView.ConfigurationId);
        cut.FindAll("[data-testid=variant-chip] button").First(b => b.TextContent == "Hide").Click();
        Assert.Equal(VariantDisplay.Hide, ws.VariantView.Mode);
    }

    [Fact]
    public void Compare_View_Lists_Differences()
    {
        Ws(ProductLineSample.Create());
        var cut = RenderComponent<VariantCompare>();
        var text = cut.Find("[data-testid=vcmp]").TextContent;
        Assert.Contains("Battery", text);
        Assert.Contains("FuelTank", text);
        Assert.Contains("Combustion", text);
        Assert.Contains("element differences", cut.Find("[data-testid=vcmp-summary]").TextContent);
    }

    [Fact]
    public void Variants_Panel_Has_Editor_Compare_And_Trade_Tabs()
    {
        Ws(ProductLineSample.Create());
        var cut = RenderComponent<VariantsPanel>();
        Assert.Contains("Feature model", cut.Markup);
        Assert.Contains("Compare", cut.Markup);
        Assert.Contains("Trade studies", cut.Markup);
    }
}
