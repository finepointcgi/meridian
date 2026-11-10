using Bunit;
using Meridian.Core.Samples;
using Meridian.Serialization.Reports;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

public class ReportDialogTests : TestContext
{
    private readonly Workspace _ws;

    public ReportDialogTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ws = Services.GetRequiredService<Workspace>();
        _ws.UseLocalModel(SampleModels.Vehicle());
        RenderComponent<MudBlazor.MudPopoverProvider>();
    }

    [Fact]
    public async Task Dialog_Generates_In_The_Background_And_Downloads()
    {
        var provider = RenderComponent<MudBlazor.MudDialogProvider>();
        var dialogs = Services.GetRequiredService<MudBlazor.IDialogService>();
        await provider.InvokeAsync(() => dialogs.ShowAsync<ReportDialog>("Generate document"));
        Assert.Contains("System Requirements Specification", provider.Markup);
        Assert.Contains("SRS-001", provider.Markup);
        provider.FindAll("button").Single(b => b.TextContent.Trim() == "Generate").Click();
        provider.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "meridian.download"), TimeSpan.FromSeconds(30));
        var call = JSInterop.Invocations.First(i => i.Identifier == "meridian.download");
        Assert.EndsWith(".pdf", (string)call.Arguments[0]!);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(Convert.FromBase64String((string)call.Arguments[2]!), 0, 4));
        provider.WaitForAssertion(() => Assert.Contains("Generated", provider.Markup));
    }

    [Fact]
    public async Task Editor_Shows_Section_Tree_Preview_And_Saves_To_Model()
    {
        var provider = RenderComponent<MudBlazor.MudDialogProvider>();
        var dialogs = Services.GetRequiredService<MudBlazor.IDialogService>();
        var template = ReportTemplates.Icd();
        template.Name = "My ICD";
        MudBlazor.IDialogReference? reference = null;
        await provider.InvokeAsync(async () => reference = await dialogs.ShowAsync<ReportTemplateEditor>("Edit",
            new MudBlazor.DialogParameters<ReportTemplateEditor> { { x => x.Template, template } }));
        var rows = provider.FindAll(".rte-row");
        Assert.Contains(rows, r => r.TextContent.Contains("Interface definitions"));
        Assert.Contains(rows, r => r.TextContent.Contains("For each"));
        provider.WaitForAssertion(() => Assert.Contains("Interface Control Document", provider.Find("iframe.rte-frame").GetAttribute("srcdoc") ?? ""), TimeSpan.FromSeconds(20));

        // Select a section and add a subsection: the tree grows.
        provider.FindAll(".rte-row").First(r => r.TextContent.Contains("Ports")).Click();
        provider.Find("button.rte-add-sub").Click();
        Assert.Contains(provider.FindAll(".rte-row"), r => r.TextContent.Contains("New subsection"));

        provider.FindAll("button").Single(b => b.TextContent.Trim() == "Save to model").Click();
        var result = await reference!.Result;
        Assert.False(result!.Canceled);
        var stored = Assert.Single(ReportTemplates.InModel(_ws.Model));
        Assert.Equal("My ICD", stored.Template.Name);
        Assert.Contains(stored.Template.AllSections(), s => s.Title == "New subsection");
    }
}
