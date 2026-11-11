using Bunit;
using Meridian.Core.CodeSync;
using Meridian.Web.Components.Workbench;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>"Generate sequence from code" asks how deep to follow calls (1–4) before generating.</summary>
public class SequenceDepthDialogTests : TestContext
{
    public SequenceDepthDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        RenderComponent<MudPopoverProvider>();
    }

    private (IRenderedComponent<MudDialogProvider> Provider, Task<int?> Answer) Ask(int depth)
    {
        var provider = RenderComponent<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        Task<int?> answer = null!;
        provider.InvokeAsync(() => answer = SequenceDepthDialog.AskAsync(dialogs, "Balance(int)", depth));
        provider.WaitForElement("button.sequence-depth-ok");
        return (provider, answer);
    }

    [Fact]
    public async Task Offers_Depths_One_To_Four_Starting_At_The_Given_One_And_Returns_The_Pick()
    {
        var (provider, answer) = Ask(2);
        Assert.Contains("Balance(int)", provider.Markup);
        var radios = provider.FindAll("input[data-testid^=sequence-depth-]");
        Assert.Equal(CodeBehaviourLimits.MaxSequenceDepth, radios.Count);
        Assert.True(provider.Find("input[data-testid=sequence-depth-2]").HasAttribute("checked"));
        Assert.Contains("(default)", provider.FindAll("label.mud-radio")[1].TextContent);

        provider.Find("input[data-testid=sequence-depth-4]").Click();
        provider.Find("button.sequence-depth-ok").Click();
        Assert.Equal(4, await answer);
    }

    [Fact]
    public async Task Cancel_Returns_Nothing_And_A_Stored_Depth_Is_Preselected()
    {
        var (provider, answer) = Ask(3);
        Assert.True(provider.Find("input[data-testid=sequence-depth-3]").HasAttribute("checked"));
        provider.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();
        Assert.Null(await answer);

        var (again, out9) = Ask(9);   // out of range: clamped to the deepest
        Assert.True(again.Find("input[data-testid=sequence-depth-4]").HasAttribute("checked"));
        again.Find("button.sequence-depth-ok").Click();
        Assert.Equal(4, await out9);
    }
}
