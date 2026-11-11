using System.IO.Compression;
using System.Text;
using Meridian.Core.Model;

namespace Meridian.Serialization.Reports;

public enum ReportFormat { Pdf, Docx, Html }

/// <summary>How a report is split by top-level package (<see cref="DocResolver.SplitPackages"/>).</summary>
public enum ReportSplit
{
    /// <summary>One document.</summary>
    None,
    /// <summary>A zip with one document per package.</summary>
    ZipPerPackage,
    /// <summary>One document with a part (level-1 heading) per package; the table of contents lists them all.</summary>
    PartPerPackage,
}

/// <summary>
/// A report after its numbering pass: what it will contain (<see cref="Stats"/>, the size estimate) and the headings,
/// captions and cross-reference targets the streamed writing pass needs. Made by <see cref="ReportEngine.Prepare"/>.
/// </summary>
public sealed class PreparedReport
{
    internal PreparedReport(ModelRepository model, DocTemplate template, ResolveOptions options, ResolvedDocument index)
    {
        Model = model; Template = template; Options = options; Index = index;
    }

    public ModelRepository Model { get; }
    public DocTemplate Template { get; }
    public ResolveOptions Options { get; }
    public ResolvedDocument Index { get; }
    public ReportStats Stats => Index.Stats;
    public long EstimatedPages => Index.Stats.EstimatedPages;
    public string Title => Index.Title;
}

/// <summary>
/// Entry point for template-driven document generation. Typical UI use: take a <see cref="Snapshot"/> on the UI thread,
/// then <see cref="Prepare"/> and <see cref="Write(PreparedReport, ReportFormat, Stream, IProgress{ReportProgress}?, CancellationToken)"/>
/// on a worker with progress and cancellation.
/// <para>Reports are streamed: a numbering pass counts and numbers everything without keeping it, then the writing pass
/// resolves the template again and hands each paragraph, table and figure to the writer as it is made. Memory is bounded
/// by the biggest single table, not by the document, and nothing is ever one giant string.</para>
/// </summary>
public static class ReportEngine
{
    /// <summary>Above this many estimated pages the UI warns before generating and offers to split or narrow the report.</summary>
    public const long WarnPages = 2_000;
    /// <summary>Reports estimated above this many pages, or visiting more than <see cref="BackgroundElements"/> elements,
    /// run as background jobs; smaller ones are generated and downloaded straight away.</summary>
    public const long BackgroundPages = 150;
    public const long BackgroundElements = 20_000;

    public static bool ShouldWarn(ReportStats s) => s.EstimatedPages > WarnPages;
    public static bool ShouldRunInBackground(ReportStats s) => s.EstimatedPages > BackgroundPages || s.Elements > BackgroundElements;

    /// <summary>A private copy of the model, so generation on a worker thread never races with edits.</summary>
    public static ModelRepository Snapshot(ModelRepository m) => ProjectFile.Copy(m);

    public static string Extension(ReportFormat f) => f switch { ReportFormat.Pdf => "pdf", ReportFormat.Docx => "docx", _ => "html" };

    public static string MimeType(ReportFormat f) => f switch
    {
        ReportFormat.Pdf => "application/pdf",
        ReportFormat.Docx => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "text/html",
    };

    /// <summary>File name and media type of a report's output (a zip when split per package).</summary>
    public static (string Extension, string Mime) Output(ReportFormat f, ReportSplit split) =>
        split == ReportSplit.ZipPerPackage ? ("zip", "application/zip") : (Extension(f), MimeType(f));

    // ================================================================== streaming pipeline

    /// <summary>The numbering pass: counts, numbers and collects cross-reference targets (no diagrams are rendered).
    /// Cheap enough to run before deciding whether to warn or to generate in the background.</summary>
    public static PreparedReport Prepare(ModelRepository m, DocTemplate t, ResolveOptions? options = null,
                                         IProgress<ReportProgress>? progress = null, CancellationToken ct = default)
    {
        // Both passes must see the same date and share rendered diagrams.
        var o = Copy(options ?? new ResolveOptions(), date: options?.Date ?? DateTime.Now, images: options?.Images ?? new DiagramImageCache());
        var index = DocResolver.Index(m, t, o, progress, ct);
        return new PreparedReport(m, t, o, index);
    }

    /// <summary>The writing pass: streams the document to <paramref name="output"/> (which needn't be seekable).</summary>
    public static void Write(PreparedReport r, ReportFormat format, Stream output, IProgress<ReportProgress>? progress = null, CancellationToken ct = default)
    {
        var counters = new ReportCounters();
        var o = Copy(r.Options, counters: counters);
        var (from, to) = format switch { ReportFormat.Pdf => (0.0, 0.7), ReportFormat.Docx => (0.0, 0.8), _ => (0.0, 0.98) };
        var nodes = Reporting(DocResolver.Stream(r.Model, r.Template, r.Index, o, ct), r.Index.Stats.Nodes, counters, progress,
                              format switch { ReportFormat.Pdf => "Composing PDF", ReportFormat.Docx => "Writing Word document", _ => "Writing HTML" }, from, to);
        switch (format)
        {
            case ReportFormat.Pdf:
                DocPdf.Write(r.Index, nodes, output, progress == null ? null : new LayoutOnly(progress), ct);
                break;
            case ReportFormat.Docx:
                DocWord.Write(r.Index, nodes, output, null, ct, progress == null ? null : new Scaled(progress, 0.8, 0.99));
                break;
            default:
                using (var w = new StreamWriter(output, new UTF8Encoding(false), 64 * 1024, leaveOpen: true))
                    DocHtml.Write(r.Index, nodes, w, null, ct);
                break;
        }
        progress?.Report(new ReportProgress("Done", 1, counters.Sections, counters.Sections, counters.Elements));
    }

    /// <summary>
    /// Generates a report into <paramref name="output"/>: one document, one with a part per top-level package, or a zip of
    /// one document per package (<see cref="ReportSplit"/>). <paramref name="prepared"/> is reused when given (it must be
    /// <see cref="Prepare"/>'s result for the same arguments, as the size check before generating already made it).
    /// </summary>
    public static void Generate(ModelRepository m, DocTemplate t, ReportFormat format, Stream output, ReportSplit split = ReportSplit.None,
                                ResolveOptions? options = null, IProgress<ReportProgress>? progress = null, CancellationToken ct = default,
                                PreparedReport? prepared = null)
    {
        options ??= new ResolveOptions();
        if (split == ReportSplit.ZipPerPackage) { WriteZip(m, t, format, output, options, progress, ct); return; }
        if (split == ReportSplit.PartPerPackage && !options.SplitByPackage) options = Copy(options, splitByPackage: true);
        prepared ??= Prepare(m, t, options, progress == null ? null : new Scaled(progress, 0, 0.2), ct);
        ct.ThrowIfCancellationRequested();
        Write(prepared, format, output, progress == null ? null : new Scaled(progress, 0.2, 1), ct);
    }

    private static void WriteZip(ModelRepository m, DocTemplate t, ReportFormat format, Stream output, ResolveOptions options,
                                 IProgress<ReportProgress>? progress, CancellationToken ct)
    {
        var packages = DocResolver.SplitPackages(m, options.Scope);
        var images = options.Images ?? new DiagramImageCache();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        for (var i = 0; i < packages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var p = packages[i];
            var part = t.Clone();
            part.Subtitle = (part.Subtitle.Length > 0 ? part.Subtitle + " – " : "") + p.Name;
            var o = Copy(options, scope: [p.Id], images: images, splitByPackage: false);
            var scaled = progress == null ? null : new Scaled(new Relabel(progress, $"{p.Name} ({i + 1} of {packages.Count})", i + 1, packages.Count),
                                                             (double)i / packages.Count, (double)(i + 1) / packages.Count);
            var name = FileName($"{i + 1:00} {p.Name}");
            while (!names.Add(name)) name += "_";
            // PDF and Word are compressed already.
            var entry = zip.CreateEntry(name + "." + Extension(format), format == ReportFormat.Html ? CompressionLevel.Fastest : CompressionLevel.NoCompression);
            using var s = entry.Open();
            Generate(m, part, format, s, ReportSplit.None, o, scaled, ct);
        }
        if (packages.Count == 0)
        {
            using var s = zip.CreateEntry("README.txt").Open();
            s.Write(Encoding.UTF8.GetBytes("The model (or the chosen scope) has no packages to split the report by."));
        }
        progress?.Report(new ReportProgress("Done", 1, packages.Count, packages.Count));
    }

    /// <summary>A file name made of <paramref name="text"/> (characters no file system accepts become "_").</summary>
    public static string FileName(string text)
    {
        var cleaned = string.Join("_", text.Split(Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).Distinct().ToArray(),
                                                  StringSplitOptions.RemoveEmptyEntries)).Trim();
        return cleaned.Length == 0 ? "report" : cleaned.Length > 120 ? cleaned[..120] : cleaned;
    }

    /// <summary>Generates into memory with the streaming pipeline (fine for small and medium reports).</summary>
    public static byte[] Generate(ModelRepository m, DocTemplate t, ReportFormat format, IProgress<ReportProgress>? progress = null,
                                  CancellationToken ct = default, ResolveOptions? options = null)
    {
        using var ms = new MemoryStream();
        Generate(m, t, format, ms, ReportSplit.None, options, progress, ct);
        return ms.ToArray();
    }

    /// <summary>The single-pass pipeline: the whole document resolved into memory, then written. The streaming pipeline must
    /// produce exactly the same output; tests compare the two.</summary>
    public static byte[] GenerateBuffered(ModelRepository m, DocTemplate t, ReportFormat format, IProgress<ReportProgress>? progress = null,
                                          CancellationToken ct = default, ResolveOptions? options = null)
    {
        // Resolution is 30 % of the bar, writing the rest.
        var resolveProgress = progress == null ? null : new Scaled(progress, 0, 0.3);
        var doc = DocResolver.Resolve(m, t, options, resolveProgress, ct);
        ct.ThrowIfCancellationRequested();
        var write = progress == null ? null : new Scaled(progress, 0.3, 1);
        var bytes = format switch
        {
            ReportFormat.Pdf => DocPdf.Write(doc, write, ct),
            ReportFormat.Docx => DocWord.Write(doc, write, ct),
            _ => Encoding.UTF8.GetBytes(DocHtml.Write(doc, write, ct)),
        };
        progress?.Report(new ReportProgress("Done", 1));
        return bytes;
    }

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

    private static ResolveOptions Copy(ResolveOptions o, DateTime? date = null, DiagramImageCache? images = null, ReportCounters? counters = null,
                                       IReadOnlyCollection<string>? scope = null, bool? splitByPackage = null) => new()
    {
        LoopLimit = o.LoopLimit, RenderDiagrams = o.RenderDiagrams, Date = date ?? o.Date, MaxIterations = o.MaxIterations, MaxNodes = o.MaxNodes,
        Scope = scope ?? o.Scope, ScopeDepth = o.ScopeDepth, SplitByPackage = splitByPackage ?? o.SplitByPackage,
        Images = images ?? o.Images, Counters = counters ?? o.Counters,
    };

    /// <summary>Passes the nodes through, reporting how far the writer has got (nodes written of the numbering pass's total).</summary>
    private static IEnumerable<DocNode> Reporting(IEnumerable<DocNode> nodes, long total, ReportCounters counters, IProgress<ReportProgress>? progress,
                                                  string phase, double from, double to)
    {
        long i = 0;
        total = Math.Max(1, total);
        foreach (var n in nodes)
        {
            if (progress != null && i % 100 == 0)
                progress.Report(new ReportProgress($"{phase}: {i:N0} of {total:N0} blocks", from + (to - from) * Math.Min(1, (double)i / total),
                                                   counters.Section, counters.Sections, counters.Elements));
            i++;
            yield return n;
        }
        progress?.Report(new ReportProgress(phase, to, counters.Section, counters.Sections, counters.Elements));
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

    /// <summary>The PDF writer's own reports, minus composition (which <see cref="Reporting"/> follows): page layout is 70–100 %.</summary>
    private sealed class LayoutOnly(IProgress<ReportProgress> inner) : IProgress<ReportProgress>
    {
        public void Report(ReportProgress value)
        {
            if (value.Phase.StartsWith("Composing", StringComparison.Ordinal)) return;
            inner.Report(value with { Fraction = value.Fraction >= 1 ? 0.99 : 0.7 });
        }
    }

    /// <summary>Prefixes the phase with the document being written (split reports) and reports the document as the section.</summary>
    private sealed class Relabel(IProgress<ReportProgress> inner, string label, int section, int sections) : IProgress<ReportProgress>
    {
        public void Report(ReportProgress value) =>
            inner.Report(value with { Phase = label + ": " + value.Phase, Section = section, Sections = sections });
    }
}
