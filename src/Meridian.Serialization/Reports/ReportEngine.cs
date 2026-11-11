using Meridian.Core.Model;

namespace Meridian.Serialization.Reports;

public enum ReportFormat { Pdf, Docx, Html }

/// <summary>
/// Entry point for template-driven document generation. Typical UI use: take a <see cref="Snapshot"/> on the UI thread,
/// then <see cref="GenerateAsync"/> on a worker with progress and cancellation.
/// </summary>
public static class ReportEngine
{
    /// <summary>A private copy of the model, so generation on a worker thread never races with edits.</summary>
    public static ModelRepository Snapshot(ModelRepository m) => ProjectFile.Copy(m);

    public static string Extension(ReportFormat f) => f switch { ReportFormat.Pdf => "pdf", ReportFormat.Docx => "docx", _ => "html" };

    public static string MimeType(ReportFormat f) => f switch
    {
        ReportFormat.Pdf => "application/pdf",
        ReportFormat.Docx => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "text/html",
    };

    /// <summary>Generates into memory. Prefer the <see cref="Stream"/> overload for whole-model documents.</summary>
    public static byte[] Generate(ModelRepository m, DocTemplate t, ReportFormat format, IProgress<ReportProgress>? progress = null,
                                  CancellationToken ct = default, ResolveOptions? options = null)
    {
        using var ms = new MemoryStream();
        Generate(m, t, format, ms, progress, ct, options);
        return ms.ToArray();
    }

    /// <summary>Generates straight into <paramref name="output"/> (flushed, left open): HTML is written as it is laid out,
    /// so a document is not bounded by the 2 GB string / array limits; the Word body is streamed through a temporary file.
    /// <paramref name="output"/> may be forward-only (an HTTP response).</summary>
    public static void Generate(ModelRepository m, DocTemplate t, ReportFormat format, Stream output, IProgress<ReportProgress>? progress = null,
                                CancellationToken ct = default, ResolveOptions? options = null)
    {
        // Resolution is 30 % of the bar, writing the rest.
        var resolveProgress = progress == null ? null : new Scaled(progress, 0, 0.3);
        var doc = DocResolver.Resolve(m, t, options, resolveProgress, ct);
        ct.ThrowIfCancellationRequested();
        var write = progress == null ? null : new Scaled(progress, 0.3, 1);
        Write(doc, format, output, write, ct);
        progress?.Report(new ReportProgress("Done", 1));
    }

    /// <summary>Lays out an already resolved document in <paramref name="format"/>.</summary>
    public static void Write(ResolvedDocument doc, ReportFormat format, Stream output, IProgress<ReportProgress>? progress = null, CancellationToken ct = default)
    {
        switch (format)
        {
            case ReportFormat.Pdf: DocPdf.Write(doc, output, progress, ct); break;
            case ReportFormat.Docx: DocWord.Write(doc, output, progress, ct); break;
            default: DocHtml.Write(doc, output, progress, ct); break;
        }
        output.Flush();
    }

    /// <summary>A read/write temporary file that is deleted when closed.</summary>
    public static FileStream TempFile() =>
        new(Path.Combine(Path.GetTempPath(), "meridian-" + Guid.NewGuid().ToString("N") + ".tmp"), FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);

    /// <summary>Generates on a thread-pool thread into <paramref name="output"/>.</summary>
    public static Task GenerateAsync(ModelRepository m, DocTemplate t, ReportFormat format, Stream output, IProgress<ReportProgress>? progress = null,
                                     CancellationToken ct = default, ResolveOptions? options = null) =>
        Task.Run(() => Generate(m, t, format, output, progress, ct, options), ct);

    /// <summary>Generates on a thread-pool thread. Pass a <see cref="Snapshot"/> when the live model may change meanwhile.</summary>
    public static Task<byte[]> GenerateAsync(ModelRepository m, DocTemplate t, ReportFormat format, IProgress<ReportProgress>? progress = null,
                                             CancellationToken ct = default, ResolveOptions? options = null) =>
        Task.Run(() => Generate(m, t, format, progress, ct, options), ct);

    /// <summary>Budgets of the live preview, which runs on every edit (and whenever a saved template is opened).</summary>
    public const int PreviewMaxIterations = 5_000, PreviewMaxNodes = 20_000;

    /// <summary>Fast HTML preview for the template editor: loops are capped and diagrams can be stubbed.</summary>
    public static string Preview(ModelRepository m, DocTemplate t, int loopLimit = 6, bool diagrams = true, CancellationToken ct = default)
    {
        var doc = DocResolver.Resolve(m, t, new ResolveOptions { LoopLimit = loopLimit, RenderDiagrams = diagrams, MaxIterations = PreviewMaxIterations, MaxNodes = PreviewMaxNodes }, null, ct);
        return DocHtml.Write(doc, null, ct, preview: true);
    }

    private sealed class Scaled : IProgress<ReportProgress>
    {
        private readonly IProgress<ReportProgress> _inner;
        private readonly double _from, _to;
        private double _last;

        public Scaled(IProgress<ReportProgress> inner, double from, double to) { _inner = inner; _from = from; _to = to; _last = from; }

        public void Report(ReportProgress value)
        {
            // NaN = "still working, no new fraction": keep the last position but update the text.
            if (!double.IsNaN(value.Fraction)) _last = Math.Max(_last, _from + (_to - _from) * Math.Clamp(value.Fraction, 0, 1));
            _inner.Report(value with { Fraction = _last });
        }
    }
}
