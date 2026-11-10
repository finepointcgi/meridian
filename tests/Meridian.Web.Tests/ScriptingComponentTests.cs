using Bunit;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Scripting;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

public class ScriptingComponentTests : TestContext
{
    private void Setup(bool localMode, string? enabled = null)
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Meridian:Scripting:Enabled"] = enabled,
            ["Meridian:Plugins:Enabled"] = "false",
        }).Build();
        var host = new MeridianHostInfo(localMode, Path.GetTempPath(), null);
        Services.AddSingleton(host);
        Services.AddMeridianScripting(config, host);
        RenderComponent<MudBlazor.MudPopoverProvider>();
    }

    [Fact]
    public void Settings_Default_On_Locally_And_Off_On_A_Server()
    {
        ScriptingSettings S(bool local, string? flag) => ScriptingSettings.From(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Meridian:Scripting:Enabled"] = flag }).Build(),
            new MeridianHostInfo(local, "/data", null));
        Assert.True(S(true, null).ConsoleEnabled);
        Assert.False(S(true, "false").ConsoleEnabled);
        Assert.False(S(false, null).ConsoleEnabled);
        Assert.True(S(false, "true").ConsoleEnabled);
        Assert.Equal(Path.Combine("/data", "plugins"), S(true, null).PluginsPath);
        Assert.Null(S(false, "true").PluginsPath);
    }

    [Fact]
    public void Server_Mode_Without_Opt_In_Shows_Why_The_Console_Is_Off()
    {
        Setup(localMode: false);
        var cut = RenderComponent<ScriptPanel>();
        Assert.Contains("disabled on this server", cut.Markup);
        Assert.Empty(cut.FindAll("textarea"));
    }

    [Fact]
    public void Server_Mode_Opt_In_Still_Requires_An_Open_Project_Owned_By_The_User()
    {
        Setup(localMode: false, enabled: "true");
        var cut = RenderComponent<ScriptPanel>();
        Assert.Contains("Open a project first", cut.Markup);
    }

    [Fact]
    public void Local_Mode_Runs_The_Script_With_Ctrl_Enter_And_Shows_Line_Numbered_Errors()
    {
        Setup(localMode: true);
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(SampleModels.Vehicle());
        var cut = RenderComponent<ScriptPanel>();
        Assert.Contains("not a sandbox", cut.Markup);

        cut.Find("textarea").Input("Log(\"hello \" + Model.OfKind(\"Block\").Count);\nreturn 42;");
        cut.Find("textarea").KeyDown(new KeyboardEventArgs { Key = "Enter", CtrlKey = true });
        cut.WaitForAssertion(() => Assert.Contains("⇒ 42", cut.Markup), TimeSpan.FromSeconds(30));
        Assert.Contains($"hello {ws.Model.OfKind(ElementKind.Block).Count()}", cut.Markup);
        Assert.Contains(ws.Output, l => l.Contains("hello"));

        cut.Find("textarea").Input("var x = 1;\nx +;");
        cut.Find("textarea").KeyDown(new KeyboardEventArgs { Key = "Enter", MetaKey = true });
        cut.WaitForAssertion(() => Assert.Contains("Line 2", cut.Markup), TimeSpan.FromSeconds(30));
        Assert.NotEmpty(cut.FindAll(".script-gutter .err"));
    }

    [Fact]
    public void Saving_A_Script_Stores_It_In_The_Model()
    {
        Setup(localMode: true);
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(new ModelRepository("M"));
        var cut = RenderComponent<ScriptPanel>();
        cut.Find("input.script-name").Input("My script");
        cut.Find("textarea").Input("Log(1);");
        cut.FindAll("button").First(b => b.TextContent.Contains("Save")).Click();
        var saved = Assert.Single(ScriptLibrary.List(ws.Model));
        Assert.Equal(("My script", "Log(1);"), (saved.Name, saved.Code));
        Assert.Contains(cut.FindAll("option"), o => o.TextContent == "My script");
    }
}
