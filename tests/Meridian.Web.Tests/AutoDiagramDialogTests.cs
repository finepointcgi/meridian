using Bunit;
using Meridian.Core.Language;
using Meridian.Core.Samples;
using Meridian.Serialization.Reports;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

public class AutoDiagramDialogTests : TestContext
{
    private readonly Workspace _ws;

    public AutoDiagramDialogTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ws = Services.GetRequiredService<Workspace>();
        _ws.UseLocalModel(CubeSatSample.Create());
        RenderComponent<MudPopoverProvider>();
    }

    [Fact]
    public async Task Dialog_Lists_The_Plan_And_Returns_The_Checked_Diagrams()
    {
        var provider = RenderComponent<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        IDialogReference? reference = null;
        await provider.InvokeAsync(async () => reference = await dialogs.ShowAsync<AutoDiagramDialog>("Auto-diagram"));
        var expected = AutoDiagrammer.Plan(_ws.Model);
        var rows = provider.FindAll(".ad-row");
        Assert.Equal(expected.Diagrams.Count, rows.Count);
        Assert.Contains("Package overview", rows[0].TextContent);
        Assert.Contains("new", rows[0].QuerySelector(".ad-status")!.TextContent);

        // Untick the last one, then create.
        rows[^1].QuerySelector("input")!.Change(false);
        provider.Find("button.ad-create").Click();
        var result = await reference!.Result;
        var answer = Assert.IsType<AutoDiagramDialog.Answer>(result!.Data);
        Assert.Equal(expected.Diagrams.Count - 1, answer.Plan!.Diagrams.Count(p => p.Selected));
    }

    [Fact]
    public async Task Report_Dialog_Can_Open_With_The_Sdd_Selected()
    {
        var provider = RenderComponent<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        await provider.InvokeAsync(() => dialogs.ShowAsync<ReportDialog>("Generate document",
            new DialogParameters<ReportDialog> { { x => x.Preselect, ReportTemplates.Sdd().Name } }));
        Assert.Contains("SDD-001", provider.Markup);
        Assert.DoesNotContain("AI narrative", provider.Markup);   // no assistant configured: no narrative option
    }
}
