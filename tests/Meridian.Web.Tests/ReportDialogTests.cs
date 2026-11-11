using Bunit;
using Meridian.Core.Model;
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
    private readonly ReportJobService _jobs = new();

    public ReportDialogTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        Services.AddSingleton(_jobs);
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
        // The file is streamed from the download endpoint, not passed to JS as base64.
        provider.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "meridian.downloadUrl"), TimeSpan.FromSeconds(30));
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "meridian.download");
        var call = JSInterop.Invocations.First(i => i.Identifier == "meridian.downloadUrl");
        Assert.EndsWith(".pdf", (string)call.Arguments[0]!);
        var job = Assert.Single(_jobs.JobsFor(null));
        Assert.Equal(ReportDownloads.Url(job), (string)call.Arguments[1]!);
        Assert.False(job.Background);   // a small document: the instant flow
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(_jobs.FileOf(job)!), 0, 4));
        provider.WaitForAssertion(() => Assert.Contains("Generated", provider.Markup));
    }

    /// <summary>A model big enough that the overview runs as a background job (~24,000 elements).</summary>
    private void UseBigModel() => _ws.UseLocalModel(LargeModelGenerator.Generate(new LargeModelOptions { Scale = 0.4 }));

    private async Task<IRenderedComponent<MudBlazor.MudDialogProvider>> OpenDialog(string template)
    {
        var provider = RenderComponent<MudBlazor.MudDialogProvider>();
        var dialogs = Services.GetRequiredService<MudBlazor.IDialogService>();
        await provider.InvokeAsync(() => dialogs.ShowAsync<ReportDialog>("Generate document",
            new MudBlazor.DialogParameters<ReportDialog> { { x => x.Preselect, template } }));
        return provider;
    }

    private static void Click(IRenderedComponent<MudBlazor.MudDialogProvider> p, string text) =>
        p.FindAll("button").First(b => b.TextContent.Trim() == text).Click();

    /// <summary>Clicks while late progress updates may still re-render the dialog (a stale handler id is retried).</summary>
    private static void ClickStable(IRenderedComponent<MudBlazor.MudDialogProvider> p, string selector)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { p.Find(selector).Click(); return; }
            catch (Bunit.Rendering.UnknownEventHandlerIdException) when (attempt < 40) { Thread.Sleep(50); }
        }
    }

    [Fact]
    public async Task Big_Document_Runs_In_The_Background_With_Progress_And_Can_Be_Cancelled()
    {
        UseBigModel();
        var provider = await OpenDialog(ReportTemplates.ModelOverview().Name);
        Click(provider, "Generate");
        // Sized first, then started as a background job: progress with sections and elements, and a note that it is safe to close.
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find(".report-background-note")), TimeSpan.FromSeconds(60));
        var job = Assert.Single(_jobs.JobsFor(null));
        Assert.True(job.Background);
        Assert.True(job.EstimatedPages > ReportEngine.BackgroundPages, $"{job.EstimatedPages} pages");
        provider.WaitForAssertion(() =>
        {
            Assert.Matches(@"Section \d+ of \d+ · [\d,]+ elements", provider.Find(".report-progress-detail").TextContent);
            Assert.Contains("blocks", provider.Find(".report-progress-text").TextContent);
        }, TimeSpan.FromSeconds(60));
        Assert.Contains(provider.FindAll("button"), b => b.TextContent.Trim() == "Close");   // it can be left running

        Click(provider, "Cancel generation");
        await _jobs.WhenDone(job).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(ReportJobState.Cancelled, job.State);
        provider.WaitForAssertion(() => Assert.Contains("Generation cancelled.", provider.Markup), TimeSpan.FromSeconds(10));
        Assert.Null(_jobs.FileOf(job));
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "meridian.downloadUrl");
    }

    [Fact]
    public async Task Huge_Document_Warns_First_And_Can_Be_Split_Per_Package()
    {
        UseBigModel();
        // A template whose requirement × block matrix lists every pair: thousands of pages.
        var t = ReportTemplates.Sdd();
        t.Name = "Huge matrix";
        t.Sections = [new DocSection { Title = "Allocation", Blocks = [new TraceMatrixBlock { RowQuery = "kind:Requirement", ColumnQuery = "kind:Block" }] }];
        var saved = ReportTemplates.Save(_ws.Model, t);
        var provider = RenderComponent<MudBlazor.MudDialogProvider>();
        var dialogs = Services.GetRequiredService<MudBlazor.IDialogService>();
        await provider.InvokeAsync(() => dialogs.ShowAsync<ReportDialog>("Generate document"));
        // Pick the saved template.
        var select = provider.FindComponents<MudBlazor.MudSelect<string>>().Single(c => c.Instance.Label == "Template");
        await provider.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync("model:" + saved.Id));
        Click(provider, "Generate");
        provider.WaitForAssertion(() => Assert.NotNull(provider.Find(".report-size-warning")), TimeSpan.FromSeconds(60));
        var warning = provider.Find(".report-size-warning").TextContent;
        Assert.Matches(@"This Huge matrix would be ~[\d,]+ pages", warning);
        Assert.Contains("per top-level package", warning);
        Assert.Empty(_jobs.JobsFor(null));   // nothing generated yet

        ClickStable(provider, "button.report-split-zip");
        provider.WaitForAssertion(() => Assert.Single(_jobs.JobsFor(null)), TimeSpan.FromSeconds(10));
        var job = _jobs.JobsFor(null).Single();
        Assert.EndsWith(".zip", job.FileName);
        Assert.Equal("application/zip", job.Mime);
        _jobs.Cancel(job);
        await _jobs.WhenDone(job).WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Scope_Options_Limit_The_Report_To_A_Package()
    {
        var provider = await OpenDialog(ReportTemplates.ModelOverview().Name);
        Assert.NotNull(provider.Find(".report-scope"));
        Assert.NotNull(provider.Find(".report-split"));
        var scope = provider.FindComponents<MudBlazor.MudSelect<string>>().Single(c => c.Instance.Label == "Report on");
        await provider.InvokeAsync(() => scope.Instance.ValueChanged.InvokeAsync("package"));
        var package = provider.FindComponents<MudBlazor.MudSelect<string>>().Single(c => c.Instance.Label == "Package");
        var behavior = _ws.Model.OfKind(ElementKind.Package).First(p => p.Name == "Behavior");
        await provider.InvokeAsync(() => package.Instance.ValueChanged.InvokeAsync(behavior.Id));
        var format = provider.FindComponent<MudBlazor.MudRadioGroup<ReportFormat>>();
        await provider.InvokeAsync(() => format.Instance.ValueChanged.InvokeAsync(ReportFormat.Html));
        Click(provider, "Generate");
        provider.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "meridian.downloadUrl"), TimeSpan.FromSeconds(30));
        var job = Assert.Single(_jobs.JobsFor(null));
        Assert.EndsWith(".html", job.FileName);
        var text = File.ReadAllText(_jobs.FileOf(job)!);
        Assert.Contains("Driver Use Cases", text);          // a diagram in Behavior
        Assert.DoesNotContain("Vehicle Structure", text);   // a diagram elsewhere
    }

    [Fact]
    public async Task Status_Bar_Follows_A_Background_Job_And_Offers_Download()
    {
        RenderComponent<MudBlazor.MudSnackbarProvider>();
        var status = RenderComponent<ReportJobsStatus>();
        using var gate = new ManualResetEventSlim();
        var job = _jobs.Start("System Design Description", "SDD.pdf", "application/pdf", (s, p, ct) =>
        {
            p.Report(new Meridian.Serialization.Reports.ReportProgress("Composing PDF", 0.4, 2, 7, 1234));
            gate.Wait(ct);
            s.Write("%PDF-1.7"u8);
        }, null, null, background: true, estimatedPages: 9000);
        status.WaitForAssertion(() => Assert.Contains("40 %", status.Find(".report-job").TextContent));
        Assert.Contains("Section 2 of 7", status.Find(".report-job").GetAttribute("title"));
        gate.Set();
        await _jobs.WhenDone(job);
        status.WaitForAssertion(() => Assert.Contains("ready", status.Find(".report-job").TextContent));
        // A notification with a Download action, since no dialog is watching.
        var snackbars = Services.GetRequiredService<MudBlazor.ISnackbar>();
        Assert.Contains(snackbars.ShownSnackbars, s => s.Message != null && s.Message.Contains("SDD.pdf is ready"));
        status.Find(".report-job-download").Click();
        status.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "meridian.downloadUrl" && (string)i.Arguments[1]! == ReportDownloads.Url(job)));
        status.WaitForAssertion(() => Assert.Empty(status.FindAll(".report-job")));   // downloaded: gone from the bar
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

    protected override void Dispose(bool disposing)
    {
        if (disposing) _jobs.Dispose();
        base.Dispose(disposing);
    }
}
