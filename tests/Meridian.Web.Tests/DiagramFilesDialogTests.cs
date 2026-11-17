using System.Text;
using Bunit;
using Meridian.Core.Model;
using Meridian.Serialization.DiagramFiles;
using Meridian.Serialization.Mermaid;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

public class DiagramFilesDialogTests : TestContext
{
    private readonly Workspace _ws;

    public DiagramFilesDialogTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ws = Services.GetRequiredService<Workspace>();
        _ws.UseLocalModel(new ModelRepository("Files"));
        RenderComponent<MudPopoverProvider>();
    }

    private static DiagramSourceFile F(string path, string text) => new(path, Encoding.UTF8.GetBytes(text));

    private const string DrawIo = """
        <mxfile><diagram name="Board"><mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/>
        <mxCell id="a" value="One" style="rounded=0;" vertex="1" parent="1"><mxGeometry x="0" y="0" width="80" height="40" as="geometry"/></mxCell>
        <mxCell id="b" value="Two" style="ellipse;" vertex="1" parent="1"><mxGeometry x="200" y="0" width="80" height="40" as="geometry"/></mxCell>
        <mxCell id="e" style="endArrow=classic;" edge="1" parent="1" source="a" target="b"><mxGeometry relative="1" as="geometry"/></mxCell>
        </root></mxGraphModel></diagram></mxfile>
        """;

    private static readonly DiagramSourceFile[] Files =
    [
        F("docs/flows/a.mmd", "flowchart LR\n A --> B"),
        F("docs/board.drawio", DrawIo),
        F("docs/notes.md", "# nothing here"),
    ];

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Reference)> Show(DiagramFolderOptions options)
    {
        var provider = RenderComponent<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        IDialogReference? reference = null;
        await provider.InvokeAsync(async () => reference = await dialogs.ShowAsync<DiagramFilesDialog>("Import",
            new DialogParameters<DiagramFilesDialog>
            {
                { x => x.Files, Files }, { x => x.Target, _ws.Model.Root }, { x => x.Options, options }, { x => x.Title, "Import folder docs" }, { x => x.Skipped, 2 },
            }));
        return (provider, reference!);
    }

    [Fact]
    public async Task The_Preview_Lists_Each_File_And_Import_Returns_The_Options()
    {
        var (provider, reference) = await Show(new DiagramFolderOptions());
        var files = provider.FindAll(".df-file");
        Assert.Equal(2, files.Count);   // the Markdown file without diagrams is left out
        Assert.Contains("docs/board.drawio", files[0].TextContent);
        Assert.Contains("Whiteboard drawing", files[0].TextContent);
        Assert.Contains("Activity diagram", files[1].TextContent);
        Assert.Contains("2 other files", provider.Markup);
        Assert.Contains("Import 2 diagrams", provider.Find("button.df-import").TextContent);
        Assert.Empty(provider.FindAll("input.df-replace"));   // nothing imported before

        provider.Find("input.df-packages").Change(false);
        provider.Find("input.df-match").Change(true);
        provider.Find("button.df-import").Click();
        var options = Assert.IsType<DiagramFolderOptions>((await reference.Result)!.Data);
        Assert.False(options.PackagePerFolder);
        Assert.Equal(MermaidMatchMode.MatchByName, options.Match);
    }

    [Fact]
    public async Task Files_Imported_Before_Are_Marked_And_Replaced()
    {
        var first = DiagramFilesImport.Apply(_ws, Files, new DiagramFolderOptions(), "docs");
        Assert.NotNull(first);
        Assert.Equal(2, first!.Diagrams.Count);
        var options = DiagramFilesImport.DefaultOptions(_ws, Files, _ws.Model.Root);
        Assert.Equal(MermaidMatchMode.MatchByName, options.Match);

        var (provider, _) = await Show(options);
        Assert.Equal(2, provider.FindAll(".df-again").Count);
        Assert.Contains("replaces earlier import", provider.Find(".df-again").TextContent);
        Assert.NotNull(provider.Find("input.df-replace"));

        var again = DiagramFilesImport.Apply(_ws, Files, options, "docs");
        Assert.Equal(2, again!.Replaced);
        Assert.Equal(2, _ws.Model.Diagrams.Count());
    }
}
