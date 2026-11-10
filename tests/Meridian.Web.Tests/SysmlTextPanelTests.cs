using Bunit;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

public class SysmlTextPanelTests : TestContext
{
    public SysmlTextPanelTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public async Task Edits_Apply_To_The_Model_And_Errors_Block_Apply()
    {
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(SampleModels.Vehicle());
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var state = new SysmlTextPanel.EditorState();
        var structure = ws.Model.Root.OwnedElements.Single(e => e.Name == "Structure");
        state.RequestScope(structure);
        var cut = RenderComponent<SysmlTextPanel>(p => p.Add(x => x.State, state));
        Assert.Contains("part def Vehicle", state.Text);
        Assert.DoesNotContain("package Requirements", state.Text);   // scoped to the Structure package
        Assert.Contains("In sync", cut.Markup);

        // A syntax error is shown and nothing is applied.
        await cut.InvokeAsync(() => cut.Instance.OnEdit(state.Text.Replace("part def BrakeSystem", "part def BrakeSystem :>")));
        Assert.Contains("1 error", cut.Markup);
        var count = ws.Model.AllElements.Count();
        await cut.InvokeAsync(() => cut.Instance.OnApplyShortcut(state.Text));
        Assert.Equal(count, ws.Model.AllElements.Count());
        Assert.Contains("Not applied", cut.Markup);

        // A valid edit applies as one undo step and the buffer becomes the canonical text again.
        var edited = state.Baseline.Replace("part def BrakeSystem", "part def Brakes");
        await cut.InvokeAsync(() => cut.Instance.OnEdit(edited));
        Assert.Contains("Modified", cut.Markup);
        cut.Find("button.sx-btn.primary").Click();
        Assert.Contains(ws.Model.OfKind(ElementKind.Block), b => b.Name == "Brakes");
        Assert.False(state.Dirty);
        Assert.Contains("Applied 1 change", cut.Find(".sx-summary").TextContent);

        // A change made elsewhere refreshes the clean editor.
        await cut.InvokeAsync(() => ws.Model.History.Undo());
        cut.WaitForAssertion(() => Assert.Contains("part def BrakeSystem", state.Text), TimeSpan.FromSeconds(3));

        // With unapplied edits, a model change raises the "model changed" notice instead.
        await cut.InvokeAsync(() => cut.Instance.OnEdit(state.Text + "\n// note"));
        await cut.InvokeAsync(() => ws.Model.Rename(ws.Model.OfKind(ElementKind.Block).Single(b => b.Name == "Engine"), "Motor"));
        cut.WaitForAssertion(() => Assert.Contains("The model changed", cut.Markup), TimeSpan.FromSeconds(3));
        Assert.DoesNotContain("Motor", state.Text);
    }
}
