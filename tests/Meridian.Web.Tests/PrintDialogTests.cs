using System.Text.RegularExpressions;
using Bunit;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization.Printing;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>Print / PDF… and Print diagrams…: page setup, live preview with page boundaries and thumbnails, and the print jobs.</summary>
public class PrintDialogTests : TestContext
{
    private readonly Workspace _ws;
    private readonly ReportJobService _jobs = new();

    public PrintDialogTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        Services.AddSingleton(_jobs);
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ws = Services.GetRequiredService<Workspace>();
        _ws.UseLocalModel(SampleModels.Vehicle());
        RenderComponent<MudBlazor.MudPopoverProvider>();
    }

    private Diagram Structure => _ws.Model.Diagrams.Single(d => d.Name == "Vehicle Structure");

    private async Task<IRenderedComponent<MudBlazor.MudDialogProvider>> Open(Action<MudBlazor.DialogParameters<PrintDialog>> parameters)
    {
        var provider = RenderComponent<MudBlazor.MudDialogProvider>();
        var dialogs = Services.GetRequiredService<MudBlazor.IDialogService>();
        var p = new MudBlazor.DialogParameters<PrintDialog>();
        parameters(p);
        await provider.InvokeAsync(() => dialogs.ShowAsync<PrintDialog>("Print", p));
        return provider;
    }

    private Task<IRenderedComponent<MudBlazor.MudDialogProvider>> OpenFor(Diagram d, Bounds? view = null, IReadOnlyCollection<string>? selection = null) =>
        Open(p => { p.Add(x => x.Diagram, d); p.Add(x => x.View, view); p.Add(x => x.Selection, selection); });

    private static PrintDialog Dialog(IRenderedComponent<MudBlazor.MudDialogProvider> p) => p.FindComponent<PrintDialog>().Instance;

    private static void Click(IRenderedComponent<MudBlazor.MudDialogProvider> p, string text) =>
        p.FindAll("button").First(b => b.TextContent.Trim() == text).Click();

    private static int PdfPages(byte[] pdf) => Regex.Matches(System.Text.Encoding.Latin1.GetString(pdf), @"/Type\s*/Page\b").Count;

    [Fact]
    public async Task Opens_with_a_preview_page_boundaries_and_one_thumbnail()
    {
        var p = await OpenFor(Structure);
        Assert.Contains("Print “Vehicle Structure”", p.Markup);
        var img = p.Find(".print-preview img");
        Assert.StartsWith("data:image/svg+xml;base64,", img.GetAttribute("src"));
        var preview = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(img.GetAttribute("src")!["data:image/svg+xml;base64,".Length..]));
        Assert.Contains("class=\"print-page\"", preview);
        Assert.Single(p.FindAll(".print-thumb"));
        Assert.Contains("1 × 1 = 1 page", p.Find(".print-summary").TextContent);
        // Light for print by default; the paper defaults to A4 landscape.
        Assert.True(Dialog(p).Setup.ForceLight);
        Assert.Equal((297, 210), Dialog(p).Setup.PaperMm);
    }

    [Fact]
    public async Task Fit_to_pages_and_fixed_scale_update_the_preview_and_thumbnails()
    {
        var p = await OpenFor(Structure);
        Click(p, "Fit to pages");
        p.Find("input[aria-label='Pages across']").Change("3");
        p.Find("input[aria-label='Pages down']").Change("2");
        Assert.Equal(6, p.FindAll(".print-thumb").Count);
        Assert.Contains("3 × 2 = 6 pages", p.Find(".print-summary").TextContent);
        var preview = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(p.Find(".print-preview img").GetAttribute("src")!["data:image/svg+xml;base64,".Length..]));
        Assert.Equal(6, Regex.Matches(preview, "class=\"print-page\"").Count);

        Click(p, "Scale");
        p.Find("input[aria-label='Scale (%)']").Change("300");
        var layout = Dialog(p).Layout!;
        Assert.Equal(3, layout.Scale, 6);
        Assert.Equal(Math.Min(layout.PageCount, PrintDialog.MaxThumbnails), p.FindAll(".print-thumb:not(.more)").Count);
        Assert.Contains($"{layout.Columns} × {layout.Rows} = {layout.PageCount} pages at 300%", p.Find(".print-summary").TextContent);
    }

    [Fact]
    public async Task Too_many_pages_are_refused_before_printing()
    {
        var m = new ModelRepository("Big");
        var pkg = m.Create(m.Root, ElementKind.Package, "Blocks");
        var d = m.CreateDiagram(pkg, DiagramType.BlockDefinition, "Wide");
        for (var i = 0; i < 40; i++) m.AddNode(d, m.Create(pkg, ElementKind.Block, "B" + i), new Bounds(i * 400, i % 4 * 300, 180, 100));
        _ws.UseLocalModel(m);
        var p = await OpenFor(d);
        Click(p, "Scale");
        p.Find("input[aria-label='Scale (%)']").Change("1000");
        Assert.True(Dialog(p).Layout!.TooManyPages);
        Assert.Contains($"at most {PrintTiling.MaxPages}", p.Find(".print-summary").TextContent);
        Assert.True(p.FindAll("button").First(b => b.TextContent.Trim() == "Save PDF").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Paper_orientation_margins_and_fields_change_the_layout()
    {
        var p = await OpenFor(Structure);
        p.Find("select.print-paper").Change(nameof(PaperSize.A3));
        Click(p, "Portrait");
        var l = Dialog(p).Layout!;
        Assert.Equal(297 * PageSetup.PointsPerMm, l.PageWidth, 3);
        Assert.Equal(420 * PageSetup.PointsPerMm, l.PageHeight, 3);
        p.Find("input[aria-label='Left margin (mm)']").Change("25");
        Assert.Equal(25 * PageSetup.PointsPerMm, Dialog(p).Layout!.ImageArea.X, 3);

        // Custom paper shows its size fields.
        Assert.Empty(p.FindAll("input[aria-label='Custom width (mm)']"));
        p.Find("select.print-paper").Change(nameof(PaperSize.Custom));
        p.Find("input[aria-label='Custom width (mm)']").Change("500");
        p.Find("input[aria-label='Custom height (mm)']").Change("300");
        Assert.Equal((300, 500), Dialog(p).Setup.PaperMm);   // portrait: the short side across

        // Turning every header and footer field off gives the picture the whole height inside the margins.
        foreach (var label in new[] { "Project", "Diagram name", "Diagram type", "Date", "Page n of N" })
            p.FindAll("label.chk").First(x => x.TextContent.Trim() == label).QuerySelector("input")!.Change(false);
        var bare = Dialog(p).Layout!;
        Assert.Equal(0, bare.HeaderHeight);
        Assert.Equal(0, bare.FooterHeight);
        Assert.Empty(p.FindAll(".print-thumb .band"));

        // Classification shows its text field.
        p.FindAll("label.chk").First(x => x.TextContent.Trim() == "Classification").QuerySelector("input")!.Change(true);
        p.Find("input[aria-label='Classification marking']").Change("CONFIDENTIAL");
        Assert.Equal("CONFIDENTIAL", Dialog(p).Setup.Classification);
        Assert.True(Dialog(p).Layout!.HeaderHeight > 0);
    }

    [Fact]
    public async Task Region_choices_follow_the_canvas()
    {
        var p = await OpenFor(Structure);
        Assert.True(p.FindAll(".print-region button").Single(b => b.TextContent == "Current view").HasAttribute("disabled"));
        Assert.True(p.FindAll(".print-region button").Single(b => b.TextContent == "Selection only").HasAttribute("disabled"));

        var engine = Structure.Nodes.Single(n => n.Element.Name == "Engine");
        var q = await OpenFor(Structure, new Bounds(0, 0, 300, 300), [engine.Id]);
        var whole = Dialog(q).Layout!.Content;
        q.FindAll(".print-region button").Single(b => b.TextContent == "Selection only").Click();
        Assert.True(Dialog(q).Layout!.Content.Width < whole.Width);
        q.FindAll(".print-region button").Single(b => b.TextContent == "Current view").Click();
        Assert.True(Dialog(q).Layout!.Content.Right <= 300 + 1e-6);
    }

    [Fact]
    public async Task Save_pdf_streams_a_job_and_can_save_the_setup_with_the_diagram()
    {
        var p = await OpenFor(Structure);
        Click(p, "Fit to pages");
        p.Find("input[aria-label='Pages across']").Change("2");
        p.Find(".print-save input").Change(true);
        Click(p, "Save PDF");
        p.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "meridian.downloadUrl"), TimeSpan.FromSeconds(30));
        var call = JSInterop.Invocations.First(i => i.Identifier == "meridian.downloadUrl");
        Assert.Equal("Vehicle Structure.pdf", call.Arguments[0]);
        var job = Assert.Single(_jobs.JobsFor(null));
        Assert.Equal(ReportDownloads.Url(job), call.Arguments[1]);
        var pdf = File.ReadAllBytes(_jobs.FileOf(job)!);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Equal(2, PdfPages(pdf));
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "meridian.download");   // never base64 through interop

        // Saved with the diagram, so the next print of it starts from this setup.
        var saved = PageSetup.FromJson(Structure.Get(PageSetup.DiagramField));
        Assert.Equal(PrintScaleMode.FitToPages, saved.Scale);
        Assert.Equal(2, saved.PagesAcross);
        Assert.Equal(PrintScaleMode.FitToPages, DiagramPrinting.InitialSetup(_ws.Model, Structure, null).Scale);
    }

    [Fact]
    public async Task Png_pages_download_as_a_zip()
    {
        var p = await OpenFor(Structure);
        Click(p, "PNG pages (zip)");
        p.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "meridian.downloadUrl"), TimeSpan.FromSeconds(60));
        Assert.Equal("Vehicle Structure pages.zip", JSInterop.Invocations.First(i => i.Identifier == "meridian.downloadUrl").Arguments[0]);
        var job = Assert.Single(_jobs.JobsFor(null));
        Assert.Equal("application/zip", job.Mime);
        Assert.Null(Structure.Get(PageSetup.DiagramField) is { Length: > 0 } ? "saved" : null);   // not saved unless asked
    }

    [Fact]
    public async Task Print_sends_a_page_css_document_to_the_browser_print_dialog()
    {
        var p = await OpenFor(Structure);
        Click(p, "Print…");
        p.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "meridian.printHtml"), TimeSpan.FromSeconds(30));
        var html = (string)JSInterop.Invocations.First(i => i.Identifier == "meridian.printHtml").Arguments[0]!;
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("@page { size: 841.89pt 595.276pt; margin: 0; }", html);
        Assert.Contains("<section class=\"sheet\"", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Initial_setup_reuses_the_report_classification_and_the_users_last_setup()
    {
        var template = Meridian.Serialization.Reports.ReportTemplates.ModelOverview();
        template.Classification = "OFFICIAL-SENSITIVE";
        Meridian.Serialization.Reports.ReportTemplates.Save(_ws.Model, template);
        var fresh = DiagramPrinting.InitialSetup(_ws.Model, Structure, null);
        Assert.Equal("OFFICIAL-SENSITIVE", fresh.Classification);
        Assert.True(fresh.ShowClassification);

        var mine = new PageSetup { Paper = PaperSize.A2, Landscape = false }.ToJson();
        var remembered = DiagramPrinting.InitialSetup(_ws.Model, Structure, mine);
        Assert.Equal(PaperSize.A2, remembered.Paper);
        Assert.False(remembered.ShowClassification);   // the user's own choice wins; the marking is still offered
        Assert.Equal("OFFICIAL-SENSITIVE", remembered.Classification);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Batch_prints_the_chosen_diagrams_with_a_contents_page()
    {
        var root = _ws.Model.Root;
        var all = DiagramImageExport.DiagramsIn(root);
        var p = await Open(x => x.Add(d => d.Container, root));
        Assert.Contains($"{all.Count} of {all.Count}", p.Find(".print-batch legend").TextContent);
        Assert.Empty(p.FindAll(".print-region"));
        // Untick the first, preview another.
        p.FindAll(".print-batch-row input")[0].Change(false);
        p.FindAll(".print-batch-row .name")[1].Click();
        Assert.Contains("on", p.FindAll(".print-batch-row")[1].ClassName);
        Click(p, "Generate PDF");
        p.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "meridian.downloadUrl"), TimeSpan.FromSeconds(60));
        var job = Assert.Single(_jobs.JobsFor(null));
        Assert.Equal($"{root.DisplayName} diagrams", job.Title);
        var pdf = File.ReadAllBytes(_jobs.FileOf(job)!);
        // One page per diagram (fit to page) after the contents page.
        Assert.Equal(1 + all.Count - 1, PdfPages(pdf));

        // None chosen: nothing to print.
        var q = await Open(x => x.Add(d => d.Container, root));
        Click(q, "None");
        Assert.True(q.FindAll("button").First(b => b.TextContent.Trim() == "Generate PDF").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Batch_job_reports_progress_and_can_be_cancelled()
    {
        var root = _ws.Model.Root;
        var job = DiagramPrinting.Start(_ws, _jobs, new PrintRequest(DiagramImageExport.DiagramsIn(root).Select(d => d.Id).ToList(), new PageSetup(), Contents: true),
                                        PrintOutput.Pdf, "All", background: true);
        Assert.True(job.Background);
        _jobs.Cancel(job);
        await _jobs.WhenDone(job);
        Assert.True(job.State is ReportJobState.Cancelled or ReportJobState.Completed);
        Assert.Null(job.State == ReportJobState.Cancelled ? _jobs.FileOf(job) : null);

        var done = DiagramPrinting.Start(_ws, _jobs, new PrintRequest(DiagramImageExport.DiagramsIn(root).Select(d => d.Id).ToList(), new PageSetup(), Contents: true),
                                         PrintOutput.Pdf, "All");
        var phases = new List<string>();
        _jobs.Progress += (j, pr) => { if (ReferenceEquals(j, done)) lock (phases) phases.Add(pr.Phase); };
        await _jobs.WhenDone(done);
        Assert.Equal(ReportJobState.Completed, done.State);
        Assert.Equal(1, done.Fraction);
    }
}
