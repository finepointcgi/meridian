using Meridian.Core.Model;
using Meridian.Serialization;
using Meridian.Serialization.Printing;
using Meridian.Serialization.Reports;
using Microsoft.JSInterop;

namespace Meridian.Web.Services;

/// <summary>What a print job prints: diagrams (by id), and for one diagram its canvas view and selection.</summary>
public sealed record PrintRequest(IReadOnlyList<string> DiagramIds, PageSetup Setup, bool Contents = false, Bounds? View = null,
                                  IReadOnlyCollection<string>? Selection = null, SvgTheme? Theme = null);

public enum PrintOutput { Pdf, PngZip, Html }

/// <summary>
/// Diagram printing in the web app (Print / PDF… and Print diagrams…): page setup defaults and persistence, and print jobs
/// on <see cref="ReportJobService"/>, so a big PDF is written to a temporary file on a worker thread with progress and
/// cancellation and streamed from <c>GET api/reports/{id}/file</c> (or saved through the desktop app's save-url bridge).
/// Jobs print a snapshot of the user's own model: package permissions already replaced hidden elements by nameless
/// placeholders there, and the snapshot is redacted again for the current access view.
/// </summary>
public static class DiagramPrinting
{
    /// <summary>A batch this big runs as a background job (the status bar follows it).</summary>
    public const int BackgroundDiagrams = 8, BackgroundPages = 40;

    /// <summary>
    /// The setup a print dialog starts from: the one saved with the diagram, else the user's last one, else the defaults; the
    /// classification marking defaults to the one of the model's report templates.
    /// </summary>
    public static PageSetup InitialSetup(ModelRepository model, Diagram? d, string? userJson)
    {
        var saved = d?.Get(PageSetup.DiagramField) is { Length: > 0 } json ? PageSetup.FromJson(json) : null;
        var setup = saved ?? PageSetup.FromJson(userJson);
        if (setup.Classification.Length == 0 && ReportTemplates.InModel(model).Select(t => t.Template.Classification.Trim()).FirstOrDefault(c => c.Length > 0) is { } marking)
        {
            setup.Classification = marking;
            if (string.IsNullOrEmpty(userJson) && saved == null) setup.ShowClassification = true;
        }
        if (setup.Region == PrintRegion.Selection && d == null) setup.Region = PrintRegion.WholeDiagram;
        return setup;
    }

    public static string ProjectName(Workspace ws) => ws.Session?.Name is { Length: > 0 } n ? n : ws.Model.Root.Name;

    public static string VersionText(Workspace ws) => "Branch " + ws.BranchName;

    public static PrintContext ContextFor(Workspace ws, ModelRepository model, Diagram d) =>
        DiagramPrint.ContextFor(model, d, ProjectName(ws), VersionText(ws));

    public static string FileName(string title, PrintOutput output) =>
        DiagramPrint.SafeName(title) + output switch { PrintOutput.PngZip => " pages.zip", PrintOutput.Html => ".html", _ => ".pdf" };

    /// <summary>Starts a print job on a snapshot of the workspace's model (taken now, on the calling thread).</summary>
    public static ReportJob Start(Workspace ws, ReportJobService jobs, PrintRequest request, PrintOutput output, string title, bool? background = null, int dpi = 150)
    {
        var snapshot = ws.RedactForMe(ReportEngine.Snapshot(ws.Model));
        var diagrams = request.DiagramIds.Select(id => snapshot.Find(id) as Diagram).OfType<Diagram>().ToList();
        if (diagrams.Count == 0) throw new InvalidOperationException("There is nothing to print: the diagrams are no longer in the model.");
        var contexts = diagrams.Select(d => ContextFor(ws, snapshot, d)).ToList();
        var setup = request.Setup.Clone();
        var single = diagrams.Count == 1;
        var runInBackground = background ?? diagrams.Count >= BackgroundDiagrams;
        var mime = output switch { PrintOutput.PngZip => "application/zip", PrintOutput.Html => "text/html; charset=utf-8", _ => "application/pdf" };
        return jobs.Start(title, FileName(title, output), mime, (stream, progress, ct) =>
        {
            var cache = new PrintSourceCache();
            var items = new List<PrintItem>(diagrams.Count);
            for (var i = 0; i < diagrams.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(new ReportProgress($"Measuring “{diagrams[i].Name}”", 0.05 * i / diagrams.Count, i + 1, diagrams.Count));
                items.Add(DiagramPrint.Item(snapshot, diagrams[i], setup, contexts[i], cache, request.Theme,
                                            single ? request.View : null, single ? request.Selection : null));
            }
            switch (output)
            {
                case PrintOutput.PngZip: DiagramPrint.WritePngZip(items, setup, stream, dpi, progress, ct); break;
                case PrintOutput.Html:
                {
                    using var w = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 1 << 16, leaveOpen: true);
                    w.Write(DiagramPrint.Html(items, setup, title));
                    break;
                }
                default: DiagramPrint.WritePdf(items, setup, stream, request.Contents && !single, title, progress, ct); break;
            }
        }, ws.User?.Id, ws.Session?.ProjectId, runInBackground, estimatedPages: 0, snapshot, ws.Access);
    }

    /// <summary>Downloads a finished print (the desktop app saves it with its save dialog and, when <paramref name="open"/>, opens it).</summary>
    public static async Task DownloadAsync(IJSRuntime js, ReportJobService jobs, ReportJob job, bool open = false)
    {
        var url = ReportDownloads.Url(job);
        if (await IsDesktopAsync(js)) url += "?ticket=" + Uri.EscapeDataString(jobs.IssueTicket(job));
        await js.InvokeVoidAsync("meridian.downloadUrl", job.FileName, url, open);
    }

    public static async Task<bool> IsDesktopAsync(IJSRuntime js)
    {
        try { return await js.InvokeAsync<bool>("meridian.isDesktop"); }
        catch (JSException) { return false; }
    }

    /// <summary>HTML at most this long goes straight to a hidden frame's print dialog; bigger pages open from the job's file.</summary>
    public const int InlineHtmlLimit = 8_000_000;
}
