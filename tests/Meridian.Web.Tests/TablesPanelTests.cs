using Bunit;
using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

public class TablesPanelTests : TestContext
{
    private readonly Workspace _ws;

    public TablesPanelTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ws = Services.GetRequiredService<Workspace>();
        _ws.UseLocalModel(SampleModels.Vehicle());
        RenderComponent<MudBlazor.MudPopoverProvider>();
    }

    private IRenderedComponent<TablesPanel> Render(TablesPanel.Mode mode, string? viewName = null)
    {
        var view = viewName == null ? null : _ws.Model.AllElements.Single(e => e.Name == viewName);
        return RenderComponent<TablesPanel>(p => p.Add(x => x.Open, new TablesPanel.Request(mode, view)));
    }

    private static List<AngleSharp.Dom.IElement> Rows(IRenderedComponent<TablesPanel> cut) => cut.FindAll(".gt-table tbody tr").ToList();

    [Fact]
    public void Saved_Table_View_Shows_Its_Rows_And_Columns()
    {
        var cut = Render(TablesPanel.Mode.Table, "Block overview");
        Assert.Equal(4, Rows(cut).Count);   // Vehicle, Engine, FuelTank, BrakeSystem
        var headers = cut.FindAll(".gt-table thead tr:first-child th.gt-h").Select(h => h.TextContent.Trim()).ToList();
        Assert.Contains(headers, h => h.StartsWith("Name"));
        Assert.Contains(headers, h => h.StartsWith("Satisfies"));
        Assert.Contains("REQ-002", Rows(cut).Single(r => r.InnerHtml.Contains("value=\"Vehicle\"")).TextContent);
        Assert.Contains("Block overview", cut.Find(".gt-views").TextContent);
    }

    [Fact]
    public void Inline_Edit_Goes_Through_The_Undoable_Repository()
    {
        var cut = Render(TablesPanel.Mode.Table, "Block overview");
        var engine = _ws.Model.AllElements.Single(e => e.Kind == ElementKind.Block && e.Name == "Engine");
        cut.Find($"tr input.gt-cell[value=\"Engine\"]").Change("Motor");
        Assert.Equal("Motor", engine.Name);
        _ws.Model.History.Undo();
        Assert.Equal("Engine", engine.Name);
    }

    [Fact]
    public void Clicking_A_Row_Selects_The_Element_And_Delete_Removes_It()
    {
        var cut = Render(TablesPanel.Mode.Table, "Block overview");
        var row = Rows(cut).Single(r => r.InnerHtml.Contains("value=\"FuelTank\""));
        row.Click();
        Assert.Equal("FuelTank", _ws.Selected?.Name);
        cut.WaitForAssertion(() => Assert.Contains("sel", Rows(cut).Single(r => r.InnerHtml.Contains("value=\"FuelTank\"")).ClassName));

        Rows(cut).Single(r => r.InnerHtml.Contains("value=\"FuelTank\"")).QuerySelector(".gt-del")!.Click();
        Assert.DoesNotContain(_ws.Model.AllElements, e => e.Name == "FuelTank");
        cut.WaitForAssertion(() => Assert.Equal(3, Rows(cut).Count));
        _ws.Model.History.Undo();
        Assert.Contains(_ws.Model.AllElements, e => e.Name == "FuelTank");
    }

    [Fact]
    public void Column_Filters_And_Sorting_Apply()
    {
        var cut = Render(TablesPanel.Mode.Table, "Block overview");
        cut.FindAll(".gt-filter")[0].Input("tank");
        Assert.Single(Rows(cut));
        cut.FindAll(".gt-filter")[0].Input("");
        Assert.Equal(4, Rows(cut).Count);

        // The view sorts by name ascending; clicking the header flips it to descending.
        string FirstName() => Rows(cut)[0].QuerySelector("input.gt-cell")!.GetAttribute("value")!;
        Assert.Equal("BrakeSystem", FirstName());
        cut.FindAll(".gt-hl")[0].Click();
        Assert.Equal("Vehicle", FirstName());
    }

    [Fact]
    public void Row_Kind_Change_Resets_Default_Columns_And_Csv_Exports()
    {
        var cut = Render(TablesPanel.Mode.Table);
        cut.Find("select.gt-select").Change(ElementKind.Requirement.ToString());
        Assert.Equal(_ws.Model.OfKind(ElementKind.Requirement).Count(), Rows(cut).Count);
        Assert.Contains(cut.FindAll("th.gt-h"), h => h.TextContent.Contains("Verified by"));

        cut.Find("input.gt-where").Change("text contains stop");
        Assert.Single(Rows(cut));
        cut.Find("input.gt-where").Change("text contains (");
        Assert.NotNull(cut.Find(".gt-error"));

        cut.Find("input.gt-where").Change("");
        cut.FindAll("button.gt-btn").First(b => b.TextContent.Contains("CSV")).Click();
        var call = JSInterop.VerifyInvoke("meridian.download");
        Assert.Equal("Requirement table.csv", call.Arguments[0]);
    }

    [Fact]
    public void Query_Mode_Opens_A_Saved_Query_And_Lists_Live_Results()
    {
        var cut = Render(TablesPanel.Mode.Query, "Value properties without a value");
        var expected = ModelQuery.Parse("kind:Property where type = Real and defaultValue is empty").Run(_ws.Model);
        Assert.Equal(expected.Count, Rows(cut).Count);
        Assert.Equal(2, cut.FindAll(".gt-cond").Count);   // two builder rows

        // Switch to text and narrow it down: results update live.
        cut.FindAll(".gt-seg.small button")[1].Click();
        cut.Find("textarea.gt-qtext").Input("kind:Property where name = accelTime");
        Assert.Single(Rows(cut));
        cut.Find("textarea.gt-qtext").Input("kind:Property where (");
        Assert.NotNull(cut.Find(".gt-error"));
    }

    [Fact]
    public void Builder_Conditions_Can_Be_Added_And_Opened_As_A_Table()
    {
        var cut = Render(TablesPanel.Mode.Query);
        var blocks = _ws.Model.OfKind(ElementKind.Block).Count();
        Assert.Empty(Rows(cut));   // default: blocks without satisfy — every sample block satisfies something
        cut.FindAll(".gt-cond .gt-del")[0].Click();
        Assert.Equal(blocks, Rows(cut).Count);
        cut.FindAll("button.gt-btn").First(b => b.TextContent.Contains("Value condition")).Click();
        cut.FindAll(".gt-cond input.gt-input")[0].Input("name");
        cut.FindAll(".gt-cond input.gt-input")[1].Input("tank");
        Assert.Single(Rows(cut));
        Assert.Contains("kind:Block where name contains tank", cut.Find(".gt-code").TextContent);

        cut.FindAll("button.gt-btn").First(b => b.TextContent.Contains("Open as table")).Click();
        Assert.Single(Rows(cut));
        Assert.Equal("FuelTank", Rows(cut)[0].QuerySelector("input.gt-cell")!.GetAttribute("value"));
    }
}
