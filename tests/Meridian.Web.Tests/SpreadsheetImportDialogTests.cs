using System.Text;
using Bunit;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization.Spreadsheets;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>The spreadsheet import dialog: source preview, column mapping, presets, review and the result handed to the workbench.</summary>
public class SpreadsheetImportDialogTests : TestContext
{
    private readonly Workspace _ws;

    private const string PartsCsv =
        "Level,Part Number,Name,Description,Qty,Mass,Material,Supplier\n"
        + "1,V-100,Vehicle,,1,,,\n"
        + "2,C-200,Chassis,Frame,1,320 kg,Steel,Acme\n"
        + "3,W-300,Wheel,,4,18 kg,Aluminium,Rimco\n";

    public SpreadsheetImportDialogTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ws = Services.GetRequiredService<Workspace>();
        _ws.UseLocalModel(SampleModels.Vehicle());
        RenderComponent<MudPopoverProvider>();
    }

    private Element Structure => _ws.Model.Root.OwnedElements.Single(p => p.Name == "Structure");

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Dialog)> Open(DialogParameters<SpreadsheetImportDialog> parameters)
    {
        var provider = RenderComponent<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        IDialogReference? reference = null;
        await provider.InvokeAsync(async () => reference = await dialogs.ShowAsync<SpreadsheetImportDialog>("Spreadsheet import", parameters));
        return (provider, reference!);
    }

    [Fact]
    public async Task Parts_List_Preset_Maps_Columns_Reviews_And_Returns_The_Plan()
    {
        var (provider, dialog) = await Open(new DialogParameters<SpreadsheetImportDialog>
        {
            { x => x.Book, SheetReader.ReadText(PartsCsv, ',') }, { x => x.FileName, "parts.csv" },
            { x => x.Preset, "Parts list (BOM)" }, { x => x.Scope, Structure },
        });

        // Opens on the mapping step with every header mapped by the preset.
        var rows = provider.FindAll("tr.ss-col");
        Assert.Equal(8, rows.Count);
        string Role(string column) => provider.Find($"tr.ss-col[data-column='{column}'] select.ss-role").GetAttribute("value")!;
        Assert.Equal(nameof(SheetRole.Level), Role("Level"));
        Assert.Equal(nameof(SheetRole.Key), Role("Part Number"));
        Assert.Equal(nameof(SheetRole.Multiplicity), Role("Qty"));
        Assert.Equal(nameof(SheetRole.Value), Role("Mass"));
        Assert.Equal("Block", provider.Find("select.ss-kind").GetAttribute("value"));
        Assert.Contains("320 kg", provider.Find("tr.ss-col[data-column='Mass'] .ss-sample").TextContent);

        // Ignore the supplier column, then check the changes.
        provider.Find("tr.ss-col[data-column='Supplier'] select.ss-role").Change(nameof(SheetRole.Ignore));
        provider.WaitForAssertion(() => Assert.Contains("ignored", provider.Find("tr.ss-col[data-column='Supplier']").ClassName));
        provider.Find("button.ss-check-btn").Click();
        provider.WaitForAssertion(() => Assert.NotEmpty(provider.FindAll("tr.ss-change")));
        Assert.Contains("2 new", provider.Find("[data-testid=ss-summary]").TextContent);   // Chassis, Wheel (Vehicle matched by name)
        Assert.DoesNotContain("Rimco", provider.Find("[data-testid=ss-review]").TextContent);

        // Untick the wheel, apply.
        var wheel = provider.FindAll("tr.ss-change").Single(r => r.TextContent.Contains("Wheel"));
        wheel.QuerySelector("input[type=checkbox]")!.Change(false);
        provider.Find("button.ss-apply").Click();
        var result = await dialog.Result;
        var plan = Assert.IsType<SheetImportPlan>(result!.Data);
        Assert.False(plan.Rows.Single(r => r.Name == "Wheel").Accept);
        Assert.True(plan.Rows.Single(r => r.Name == "Chassis").Accept);

        var applied = SheetImport.Apply(_ws.Model, plan);
        Assert.Equal(1, applied.Added);
        Assert.Contains(_ws.Model.OfKind(ElementKind.Block), b => b.Name == "Chassis" && b.Get("tag:Part Number") == "C-200");
        Assert.DoesNotContain(_ws.Model.OfKind(ElementKind.Block), b => b.Name == "Wheel");
    }

    [Fact]
    public async Task Source_Step_Previews_Csv_With_Detected_Delimiter_And_Header_Row()
    {
        var csv = "Exported from PLM;;\n;;\nName;Type;Documentation\nPump;;Moves fluid\nValve;;\n";
        var (provider, _) = await Open(new DialogParameters<SpreadsheetImportDialog>
        {
            { x => x.Bytes, Encoding.UTF8.GetBytes(csv) }, { x => x.FileName, "blocks.csv" },
        });
        provider.Find("button.ss-step[data-step='0']").Click();
        Assert.Contains("semicolon", provider.Find("select.ss-delim").TextContent);
        Assert.Equal("3", provider.Find("input.ss-header").GetAttribute("value"));   // the title rows are skipped
        var preview = provider.Find("[data-testid=ss-preview]");
        Assert.Contains("Moves fluid", preview.TextContent);
        Assert.Single(preview.QuerySelectorAll("tr.ss-head"));

        // Mapping guessed from the header names.
        provider.Find("button.ss-next").Click();
        Assert.Equal(nameof(SheetRole.Name), provider.Find("tr.ss-col[data-column='Name'] select.ss-role").GetAttribute("value"));
        Assert.Equal(nameof(SheetRole.Documentation), provider.Find("tr.ss-col[data-column='Documentation'] select.ss-role").GetAttribute("value"));

        // Choosing "comma" re-reads the file as one column.
        provider.Find("button.ss-step[data-step='0']").Click();
        provider.Find("select.ss-delim").Change(",");
        provider.WaitForAssertion(() => Assert.Equal(2, provider.FindAll("[data-testid=ss-preview] thead th").Count));   // "#" and column A
        Assert.Contains("Pump;;Moves fluid", provider.Find("[data-testid=ss-preview]").TextContent);
    }

    [Fact]
    public async Task Errors_Are_Listed_And_Exported_And_Missing_Types_Can_Be_Created()
    {
        string? downloaded = null;
        var csv = "Block,Name,Type,Value,Unit\nVehicle,a,Gizmo,1,\nVehicle,b,Real,2..x,\nVehicle,c,Real,5,kg\n";
        var (provider, _) = await Open(new DialogParameters<SpreadsheetImportDialog>
        {
            { x => x.Book, SheetReader.ReadText(csv, ',') }, { x => x.Preset, "Value properties" },
            { x => x.Download, (Func<string, string, byte[], Task>)((name, _, data) => { downloaded = name + "\n" + Encoding.UTF8.GetString(data); return Task.CompletedTask; }) },
        });
        provider.Find("button.ss-check-btn").Click();
        provider.WaitForAssertion(() => Assert.NotEmpty(provider.FindAll("tr.ss-err")));
        Assert.Contains("Unknown type 'Gizmo'", provider.Markup);
        Assert.Contains("Gizmo", provider.Find(".ss-missing-types").TextContent);

        provider.Find("a.ss-export-errors").Click();
        Assert.NotNull(downloaded);
        Assert.Contains("errors.csv", downloaded);
        Assert.Contains("Row,Key,Column,Message", downloaded);

        provider.Find("a.ss-create-missing").Click();
        provider.WaitForAssertion(() => Assert.DoesNotContain("Unknown type 'Gizmo'", provider.Markup));
        Assert.Contains("new type", provider.Find("[data-testid=ss-review]").TextContent);
    }

    [Fact]
    public async Task Presets_Save_To_The_Project_And_Export_As_Json()
    {
        string? json = null;
        var (provider, _) = await Open(new DialogParameters<SpreadsheetImportDialog>
        {
            { x => x.Book, SheetReader.ReadText(PartsCsv, ',') }, { x => x.Preset, "Parts list (BOM)" },
            { x => x.Download, (Func<string, string, byte[], Task>)((name, _, data) => { if (name.EndsWith(".json")) json = Encoding.UTF8.GetString(data); return Task.CompletedTask; }) },
        });
        provider.Find("input.ss-preset-name").Change("Supplier BOM");
        provider.Find("button.ss-save-preset").Click();
        Assert.Equal("Supplier BOM", Assert.Single(SheetPresets.Project(_ws.Model)).Name);
        provider.WaitForAssertion(() => Assert.Contains("project:Supplier BOM", provider.Find("select.ss-preset").InnerHtml));

        provider.FindAll("button").Single(b => b.TextContent.Contains("Export JSON")).Click();
        Assert.NotNull(json);
        Assert.Equal("Supplier BOM", SheetMapping.FromJson(json!).Name);
    }

    [Fact]
    public async Task Export_With_The_Mapping_Downloads_A_Workbook()
    {
        byte[]? xlsx = null;
        var (provider, _) = await Open(new DialogParameters<SpreadsheetImportDialog>
        {
            { x => x.Preset, "Value properties" },
            { x => x.Download, (Func<string, string, byte[], Task>)((name, _, data) => { xlsx = data; return Task.CompletedTask; }) },
        });
        provider.Find("button.ss-step[data-step='1']").Click();
        provider.Find("button.ss-export-xlsx").Click();
        Assert.NotNull(xlsx);
        var book = SheetReader.Read("x.xlsx", new MemoryStream(xlsx!));
        Assert.Contains(book.Sheets[0].Rows, r => r.Count > 1 && r[1] == "mass");
    }
}
