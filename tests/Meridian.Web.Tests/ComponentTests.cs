using Bunit;
using Bunit.TestDoubles;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Web.Components.Pages;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

public class ComponentTests : TestContext
{
    public ComponentTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        Services.AddSingleton(new Meridian.Web.Admin.AdminSettings(new(), new(), new()));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Context_Menu_Renders_Entries_And_Runs_The_Chosen_One()
    {
        var ran = "";
        var closed = false;
        var cut = RenderComponent<ContextMenu>(p => p
            .Add(x => x.Entries, new[] { MenuEntry.Title("Edit"), MenuEntry.Do("Rename", () => ran = "rename", "F2"), MenuEntry.Sep(), MenuEntry.Do("Delete", () => ran = "delete") })
            .Add(x => x.X, 100).Add(x => x.Y, 50)
            .Add(x => x.OnClose, () => closed = true));
        Assert.Equal(2, cut.FindAll(".ctx-item").Count);
        Assert.Contains("F2", cut.Find(".ctx-item .key").TextContent);
        cut.FindAll(".ctx-item")[1].Click();
        Assert.Equal("delete", ran);
        Assert.True(closed);
    }

    [Fact]
    public void Requirements_Panel_Shows_Coverage_And_Status()
    {
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(SampleModels.Vehicle());
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = RenderComponent<RequirementsSide>();
        var rows = cut.FindAll(".req-table tbody tr");
        Assert.Equal(ws.Model.OfKind(ElementKind.Requirement).Count(), rows.Count);
        Assert.Contains(rows, r => r.TextContent.Contains("REQ-002") && r.TextContent.Contains("Acceleration"));
        Assert.Contains("4/4", cut.Markup);   // all leaf requirements satisfied in the sample
    }

    [Fact]
    public void Requirements_Panel_Selects_The_Clicked_Requirement()
    {
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(SampleModels.Vehicle());
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = RenderComponent<RequirementsSide>();
        cut.FindAll(".req-table tbody tr").First(r => r.TextContent.Contains("REQ-004")).Click();
        Assert.Equal("Braking", ws.Selected?.Name);
        Assert.Contains("sel", cut.FindAll(".req-table tbody tr").First(r => r.TextContent.Contains("REQ-004")).ClassName);
    }

    [Fact]
    public void Kind_Icon_Uses_Kind_Colour()
    {
        var cut = RenderComponent<KindIcon>(p => p.Add(x => x.Kind, ElementKind.Requirement));
        Assert.Contains(UiKinds.Accent(ElementKind.Requirement), cut.Markup);
    }

    [Fact]
    public void Login_Page_Switches_Between_Sign_In_And_Register_And_Shows_Errors()
    {
        var nav = Services.GetRequiredService<FakeNavigationManager>();
        nav.NavigateTo("/login?error=Wrong%20username%20or%20password.");
        var signIn = RenderComponent<Login>();
        Assert.Contains("/account/login", signIn.Find("form").GetAttribute("action"));
        Assert.Contains("Wrong username", signIn.Find(".auth-error").TextContent);
        nav.NavigateTo("/login?mode=register");
        var register = RenderComponent<Login>();
        Assert.Contains("/account/register", register.Find("form").GetAttribute("action"));
        Assert.NotNull(register.Find("input[name=display]"));
    }
}
