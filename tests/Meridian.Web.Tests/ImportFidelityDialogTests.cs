using Bunit;
using Meridian.Serialization;
using Meridian.Web.Components.Workbench;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>The dialog shown after a Cameo import that lost something.</summary>
public class ImportFidelityDialogTests : TestContext
{
    public ImportFidelityDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        RenderComponent<MudPopoverProvider>();
    }

    private static MagicDrawImportReport LossyReport()
    {
        var dir = Directory.CreateTempSubdirectory("meridian-fid-web-");
        try
        {
            var path = Path.Combine(dir.FullName, "small.mdxml");
            File.WriteAllText(path, """
                <?xml version='1.0' encoding='UTF-8'?>
                <xmi:XMI xmlns:uml='http://www.omg.org/spec/UML/20131001' xmlns:xmi='http://www.omg.org/spec/XMI/20131001'
                         xmlns:MyProfile='http://www.magicdraw.com/schemas/MyProfile.xmi'>
                  <uml:Model xmi:type='uml:Model' xmi:id='_root' name='Small'>
                    <packagedElement xmi:type='uml:Class' xmi:id='_a' name='A'/>
                    <packagedElement xmi:type='uml:Frobnicator' xmi:id='_i' name='a1'/>
                  </uml:Model>
                  <MyProfile:Config xmi:id='_cfg' base_Element='_i'/>
                </xmi:XMI>
                """);
            return MagicDrawImporter.Import(path).Report;
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task Shows_Coverage_Losses_And_Offers_The_Report()
    {
        var report = LossyReport();
        var provider = RenderComponent<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        Task<bool>? shown = null;
        await provider.InvokeAsync(() => { shown = ImportFidelityDialog.ShowAsync(dialogs, report, "small.mdxml"); });   // Action: do not await the dialog here

        provider.WaitForAssertion(() => Assert.Contains("Kept 50% of model elements (1 of 2)", provider.Find("[data-testid=fidelity-summary]").TextContent));
        Assert.Contains("Frobnicator", provider.Find("[data-testid=fidelity-losses]").TextContent);
        Assert.Contains("«Config» ×1", provider.Markup);

        provider.Find("[data-testid=fidelity-download]").Click();
        Assert.True(await shown!.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Ok_Does_Not_Ask_For_The_Report()
    {
        var provider = RenderComponent<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        Task<bool>? shown = null;
        var report = LossyReport();
        await provider.InvokeAsync(() => { shown = ImportFidelityDialog.ShowAsync(dialogs, report, "small.mdxml"); });
        provider.WaitForElement("[data-testid=fidelity-ok]");
        provider.Find("[data-testid=fidelity-ok]").Click();
        Assert.False(await shown!.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
