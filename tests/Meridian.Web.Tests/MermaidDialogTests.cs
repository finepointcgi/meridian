using Bunit;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization.Mermaid;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

public class MermaidDialogTests : TestContext
{
    private readonly Workspace _ws;

    public MermaidDialogTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ws = Services.GetRequiredService<Workspace>();
        _ws.UseLocalModel(SampleModels.Vehicle());
        RenderComponent<MudPopoverProvider>();
    }

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Reference)> Show(string? text = null, string? fileName = null)
    {
        var provider = RenderComponent<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        IDialogReference? reference = null;
        await provider.InvokeAsync(async () => reference = await dialogs.ShowAsync<MermaidDialog>("Paste Mermaid",
            new DialogParameters<MermaidDialog> { { x => x.Text, text }, { x => x.FileName, fileName } }));
        return (provider, reference!);
    }

    [Fact]
    public async Task Typing_Shows_A_Live_Preview_And_Import_Returns_The_Text_And_Options()
    {
        var (provider, reference) = await Show();
        Assert.True(provider.Find("button.mmd-import").HasAttribute("disabled"));   // nothing to import yet

        provider.Find("textarea.mmd-input").Input("flowchart LR\n  A[Start] --> B{Ok?}\n  B -->|yes| C[Done]\n\nsequenceDiagram\n  A->>B: hi\n\npie\n  \"x\" : 1\n");
        var rows = provider.FindAll(".mmd-row");
        Assert.Equal(3, rows.Count);
        Assert.Contains("Activity diagram", rows[0].TextContent);
        Assert.Equal("3 nodes · 2 flows", rows[0].QuerySelector(".mmd-counts")!.TextContent);
        Assert.Contains("Sequence diagram", rows[1].TextContent);
        Assert.Equal("2 lifelines · 1 message", rows[1].QuerySelector(".mmd-counts")!.TextContent);
        Assert.Contains("skipped", rows[2].ClassName);
        Assert.Contains("no SysML equivalent", provider.Find(".mmd-warnings").TextContent);
        Assert.Contains("Import 2 diagrams", provider.Find("button.mmd-import").TextContent);

        provider.Find("input.mmd-match").Change(true);
        provider.Find("button.mmd-import").Click();
        var result = await reference.Result;
        var answer = Assert.IsType<MermaidDialog.Answer>(result!.Data);
        Assert.StartsWith("flowchart LR", answer.Text);
        Assert.Equal(MermaidMatchMode.MatchByName, answer.Options.Match);
    }

    [Fact]
    public async Task A_Markdown_File_Opens_Prefilled_And_Reads_Only_Its_Mermaid_Blocks()
    {
        var (provider, reference) = await Show("# Notes\n\nflowchart TD\n\n```mermaid\nclassDiagram\n  A <|-- B\n```\n", "notes.md");
        Assert.Contains("Import notes.md", provider.Markup);
        var row = Assert.Single(provider.FindAll(".mmd-row"));
        Assert.Contains("Block definition diagram", row.TextContent);
        provider.Find("button.mmd-import").Click();
        var answer = Assert.IsType<MermaidDialog.Answer>((await reference.Result)!.Data);
        Assert.True(answer.Options.Markdown);
    }

    [Fact]
    public void Import_Into_The_Workspace_Is_One_Undo_Step_And_Opens_The_Diagram()
    {
        var before = _ws.Model.AllElements.Count();
        var result = MermaidImport.Apply(_ws, "---\ntitle: Pasted flow\n---\nflowchart TD\n  a[One] --> b[Two] --> c((End))\n", new MermaidImportOptions(), "pasted text");
        Assert.NotNull(result);
        var d = Assert.Single(result!.Diagrams);
        Assert.Equal("Pasted flow", d.Name);
        Assert.Same(d, _ws.Active?.Diagram);
        Assert.Contains(_ws.OpenDiagrams, c => ReferenceEquals(c.Diagram, d));
        Assert.Equal(3, d.Nodes.Count);
        _ws.Model.History.Undo();
        Assert.Equal(before, _ws.Model.AllElements.Count());
    }
}
