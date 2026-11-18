using System.Text;
using Bunit;
using Meridian.Core.Model;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>The "Org chart from spreadsheet" import dialog.</summary>
public class OrgChartWebTests : TestContext
{
    public OrgChartWebTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        Services.AddSingleton<ImageCache>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public async Task Org_Chart_Dialog_Shows_Columns_People_And_Warnings()
    {
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(new ModelRepository("Org"));
        var csv = "Name,Title,Reports to,Department\nAda,CEO,,Leadership\nGrace,CTO,Ada,Engineering\nAlan,Engineer,Nobody,Engineering\n";
        var provider = RenderComponent<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        IDialogReference? reference = null;
        await provider.InvokeAsync(async () => reference = await dialogs.ShowAsync<OrgChartDialog>("Org chart",
            new DialogParameters<OrgChartDialog> { { x => x.Target, ws.Model.Root }, { x => x.FileName, "people.csv" }, { x => x.Bytes, Encoding.UTF8.GetBytes(csv) } }));
        Assert.Equal("3", provider.Find(".oc-people").TextContent);
        Assert.Equal("1", provider.Find(".oc-warning-count").TextContent);
        Assert.Contains("Reports to", provider.Find(".oc-col[data-col=Manager]").TextContent);
        Assert.Contains("Nobody", provider.Find(".oc-warnings").TextContent);
        provider.Find("button.oc-import").Click();
        var result = await reference!.Result;
        var choice = Assert.IsType<OrgChartDialog.Choice>(result!.Data);
        Assert.Equal("people.csv", choice.Source);
        Assert.Equal("people", choice.Name);
        Assert.Equal(3, choice.Sheet.Tree.People.Count);
    }
}
