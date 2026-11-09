using Bunit;
using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

public class TraceabilityPanelTests : TestContext
{
    private readonly Workspace _ws;

    public TraceabilityPanelTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ws = Services.GetRequiredService<Workspace>();
        _ws.UseLocalModel(SampleModels.Vehicle());
        RenderComponent<MudBlazor.MudPopoverProvider>();
    }

    private static string IdOf(AngleSharp.Dom.IElement row) => row.QuerySelector("td")!.TextContent.Trim();

    private Element Req(string id) => _ws.Model.OfKind(ElementKind.Requirement).Single(r => r.Get(FieldKeys.RequirementId) == id);

    [Fact]
    public void Table_Shows_A_Row_Per_Requirement_With_Gaps_And_Coverage_Bars()
    {
        var cut = RenderComponent<TraceabilityPanel>();
        var rows = cut.FindAll("table.tt tbody tr");
        Assert.Equal(_ws.Model.OfKind(ElementKind.Requirement).Count(), rows.Count);
        var range = rows.Single(r => IdOf(r) == "REQ-003");
        Assert.Contains("not verified", range.TextContent);
        Assert.NotEmpty(range.QuerySelectorAll("td.gap"));
        Assert.Contains("Acceleration Test", rows.Single(r => IdOf(r) == "REQ-002").TextContent);
        Assert.NotEmpty(cut.FindAll(".tt-coverage .cov"));
    }

    [Fact]
    public void Filters_And_Search_Narrow_The_Rows()
    {
        var cut = RenderComponent<TraceabilityPanel>();
        cut.FindAll(".chip").Single(c => c.TextContent.Contains("Not verified")).Click();
        var rows = cut.FindAll("table.tt tbody tr");
        Assert.Contains(rows, r => IdOf(r) == "REQ-003");
        Assert.DoesNotContain(rows, r => IdOf(r) == "REQ-002");
        cut.Find(".tt-search input").Input("600 km");
        Assert.Single(cut.FindAll("table.tt tbody tr"));
    }

    [Fact]
    public void Suspect_Links_Are_Marked_And_Can_Be_Cleared_From_The_Table()
    {
        _ws.Model.Set(Req("REQ-004"), FieldKeys.Text, "The vehicle shall stop from 100 km/h within 36 m.");
        var cut = RenderComponent<TraceabilityPanel>();
        Assert.Equal(2, cut.FindAll(".tt-pill.suspect").Count);
        cut.Find(".tt-pill.suspect .sus").Click();
        Assert.Single(cut.FindAll(".tt-pill.suspect"));
        cut.FindAll("button").Single(b => b.TextContent.Contains("suspect")).Click();
        Assert.Empty(cut.FindAll(".tt-pill.suspect"));
        Assert.Empty(SuspectLinks.All(_ws.Model));
        _ws.Model.History.Undo();
        Assert.Single(SuspectLinks.All(_ws.Model));
    }

    [Fact]
    public void Removing_A_Link_From_A_Cell_Is_Undoable()
    {
        var cut = RenderComponent<TraceabilityPanel>();
        var before = _ws.Model.Relationships.Count();
        var row = cut.FindAll("table.tt tbody tr").Single(r => IdOf(r) == "REQ-004");
        row.QuerySelector(".tt-pill .rm")!.Click();
        Assert.Equal(before - 1, _ws.Model.Relationships.Count());
        _ws.Model.History.Undo();
        Assert.Equal(before, _ws.Model.Relationships.Count());
    }

    [Fact]
    public void Multi_Level_And_Orphan_Modes_Render()
    {
        _ws.Model.Create(_ws.Model.Root, ElementKind.Block, "Unused Block");
        var cut = RenderComponent<TraceabilityPanel>();
        cut.FindAll(".seg button").Single(b => b.TextContent.Contains("Multi-level")).Click();
        Assert.Contains("REQ-001", cut.Find("table.tt").TextContent);
        Assert.NotEmpty(cut.FindAll("tr.gaprow"));
        cut.FindAll(".seg button").Single(b => b.TextContent.Contains("Orphan")).Click();
        Assert.Contains("Unused Block", cut.Find("table.tt").TextContent);
    }

    [Fact]
    public void Inspector_Section_Edits_Attributes_And_Shows_Suspect_Links()
    {
        var req = Req("REQ-002");
        _ws.Model.Set(req, ReqFields.Status, "approved");
        _ws.Model.Set(req, ReqFields.CustomPrefix + "ASIL", "B");
        _ws.Model.Rename(req, "Fast acceleration");
        var cut = RenderComponent<RequirementInspectorSection>(p => p.Add(x => x.E, req));
        Assert.Contains("Requirement attributes", cut.Markup);
        Assert.Contains("ASIL", cut.Markup);
        Assert.Equal(SuspectLinks.Of(_ws.Model, req).Count(), cut.FindAll(".suspect-row").Count);
        cut.FindAll(".suspect-row button").First().Click();
        Assert.Equal(SuspectLinks.Of(_ws.Model, req).Count(), cut.FindAll(".suspect-row").Count);
        cut.Find(".suspect-title .link").Click();   // Clear all
        Assert.Empty(SuspectLinks.Of(_ws.Model, req));
        Assert.Empty(cut.FindAll(".suspect-row"));
    }

    [Fact]
    public void Import_Preview_Dialog_Lists_Changes_With_Checkboxes()
    {
        var diff = new SheetDiff(
        [
            new SheetRowChange { Kind = SheetChangeKind.Added, Id = "REQ-100", Name = "Noise", Accept = true, Fields = [new SheetFieldChange(FieldKeys.Text, "Text", "", "Quiet")] },
            new SheetRowChange { Kind = SheetChangeKind.Removed, Id = "REQ-003", Name = "Range", Existing = Req("REQ-003") },
        ], 3, []);
        var provider = RenderComponent<MudBlazor.MudDialogProvider>();
        var dialogs = Services.GetRequiredService<MudBlazor.IDialogService>();
        provider.InvokeAsync(() => dialogs.ShowAsync<SheetImportDialog>("Import", new MudBlazor.DialogParameters<SheetImportDialog> { { x => x.Diff, diff }, { x => x.FileName, "r.xlsx" } }));
        var rows = provider.FindAll("table.diff tbody tr");
        Assert.Equal(2, rows.Count);
        Assert.Contains("off", rows[1].ClassName);
        provider.FindAll("table.diff input[type=checkbox]")[1].Change(true);
        Assert.True(diff.Rows[1].Accept);
        Assert.Contains("Apply 2 changes", provider.Markup);
    }
}
