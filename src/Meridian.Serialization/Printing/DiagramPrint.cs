using System.IO.Compression;
using System.Net;
using System.Text;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization.Reports;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Meridian.Serialization.Printing;

/// <summary>One diagram of a print job: how to draw it (drawn again if the batch cache evicted it), its layout and header texts.</summary>
public sealed record PrintItem(string Key, Func<PrintSource> Source, PrintLayout Layout, PrintContext Context);

/// <summary>
/// Prints diagrams across one or many pages: a vector multi-page PDF (QuestPDF; every page draws the diagram's SVG behind a
/// different viewBox, so text stays selectable), a zip of page PNGs, or an HTML page with <c>@page</c> CSS for the
/// browser's print dialog. A batch PDF can start with a contents page.
/// </summary>
public static class DiagramPrint
{
    static DiagramPrint()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable = false;
    }

    private const string Grey = "#5F6B7A";
    private const string Rule = "#B7C0CF";
    private const string Accent = "#1F3B70";

    /// <summary>A print item for one diagram, drawn now (its size decides the layout) and kept in <paramref name="cache"/>.</summary>
    public static PrintItem Item(ModelRepository model, Diagram d, PageSetup setup, PrintContext context, PrintSourceCache? cache = null,
                                 SvgTheme? theme = null, Bounds? view = null, IReadOnlyCollection<string>? selection = null)
    {
        cache ??= new PrintSourceCache();
        Func<PrintSource> create = () => PrintSource.Create(model, d, setup, theme, view, selection);
        var source = cache.Get(d.Id, create);
        return new PrintItem(d.Id, () => cache.Get(d.Id, create), PrintTiling.Compute(setup, source.Content), context);
    }

    /// <summary>Header-field texts for a diagram: project, name, type, package path, version and today's date.</summary>
    public static PrintContext ContextFor(ModelRepository model, Diagram d, string? project = null, string? version = null) => new(
        project ?? model.Root.Name,
        d.Name,
        Metamodel.DisplayName(d.DiagramType),
        string.Join(" ▸ ", d.AncestorsAndSelf().Skip(1).Reverse().Select(a => a.DisplayName)),
        version ?? "",
        PrintContext.Today());

    public static int TotalPages(IEnumerable<PrintItem> items) => items.Sum(i => i.Layout.PageCount);

    // ================================================================ PDF

    public static byte[] Pdf(IReadOnlyList<PrintItem> items, PageSetup setup, bool contents = false, string? title = null)
    {
        using var ms = new MemoryStream();
        WritePdf(items, setup, ms, contents, title);
        return ms.ToArray();
    }

    /// <summary>Writes the PDF to <paramref name="output"/>: one page per tile, after a contents page when asked for.</summary>
    public static void WritePdf(IReadOnlyList<PrintItem> items, PageSetup setup, Stream output, bool contents = false, string? title = null,
                                IProgress<ReportProgress>? progress = null, CancellationToken ct = default)
    {
        var tooMany = items.FirstOrDefault(i => i.Layout.TooManyPages);
        if (tooMany != null)
            throw new InvalidOperationException($"“{tooMany.Context.DiagramName}” would print on {tooMany.Layout.TotalPages:N0} pages (at most {PrintTiling.MaxPages}). Choose a smaller scale or fit it to fewer pages.");
        progress?.Report(new ReportProgress("Laying out pages", 0.05, 0, items.Count));
        var document = Compose(items, setup, contents, title, progress, ct);
        Run(() => document.GeneratePdf(output), ct);
        progress?.Report(new ReportProgress("PDF written", 1, items.Count, items.Count));
    }

    /// <summary>QuestPDF wraps exceptions thrown while drawing: a cancelled job stays "cancelled".</summary>
    private static void Run(Action generate, CancellationToken ct)
    {
        try { generate(); }
        catch (Exception ex) when (ex is not OperationCanceledException && ct.IsCancellationRequested) { throw new OperationCanceledException("Printing was cancelled.", ex, ct); }
    }

    private static IDocument Compose(IReadOnlyList<PrintItem> items, PageSetup setup, bool contents, string? title, IProgress<ReportProgress>? progress, CancellationToken ct)
    {
        var total = Math.Max(1, TotalPages(items));
        var drawn = 0;
        if (items.Count == 0) return Document.Create(c => c.Page(p => { p.Size(PageSizes.A4); p.Content().Text("No diagrams to print."); }));
        return Document.Create(container =>
            {
                if (contents) ContentsPage(container, items, setup, title);
                for (var i = 0; i < items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = items[i];
                    var anchor = "diagram-" + i;
                    var index = i;
                    foreach (var tile in item.Layout.Tiles)
                    {
                        var t = tile;
                        container.Page(page => TilePage(page, item, t, setup, t.Index == 0 ? anchor : null, () =>
                        {
                            ct.ThrowIfCancellationRequested();
                            var n = Interlocked.Increment(ref drawn);
                            progress?.Report(new ReportProgress($"Drawing “{item.Context.DiagramName}”", 0.05 + 0.9 * Math.Min(1, (double)n / (2 * total)), index + 1, items.Count));
                        }));
                    }
                }
            });
    }

    private static PageSize SizeOf(PrintLayout l) => new((float)l.PageWidth, (float)l.PageHeight, Unit.Point);

    private static void TilePage(PageDescriptor page, PrintItem item, PrintTile tile, PageSetup setup, string? anchor, Action drawing)
    {
        var l = item.Layout;
        var area = l.ImageArea;
        page.Size(SizeOf(l));
        page.PageColor(Colors.White);
        page.MarginLeft((float)area.X);
        page.MarginRight((float)(l.PageWidth - area.Right));
        page.MarginTop((float)(area.Y - l.HeaderHeight));
        page.MarginBottom((float)(l.PageHeight - area.Bottom - l.FooterHeight));
        page.DefaultTextStyle(x => x.FontSize(7.5f).FontColor(Grey));
        var text = PrintHeaderFooter.Compose(setup, item.Context, 1, 1);
        if (l.HeaderHeight > 0)
            page.Header().Height((float)l.HeaderHeight).PaddingBottom((float)PrintTiling.BandGap).BorderBottom(0.5f).BorderColor(Rule).Row(r =>
            {
                r.RelativeItem().AlignLeft().AlignBottom().Text(text.HeaderLeft).ClampLines(1);
                r.RelativeItem().AlignCenter().AlignBottom().Text(text.HeaderCenter).FontSize(9).Bold().FontColor("#1D2330").ClampLines(1);
                r.RelativeItem().AlignRight().AlignBottom().Text(text.HeaderRight).SemiBold().FontColor("#1D2330").ClampLines(1);
            });
        var body = page.Content();
        if (anchor != null) body = body.Section(anchor);
        body.Layers(layers =>
        {
            layers.PrimaryLayer().Svg(size =>
            {
                drawing();
                return item.Source().Svg(tile.Region, size.Width, size.Height);
            });
            if (setup.RegistrationMarks || l.Overlap > 0)
                layers.Layer().Svg(size => MarksSvg(l, tile, setup.RegistrationMarks, size.Width, size.Height));
        });
        if (l.FooterHeight > 0)
            page.Footer().Height((float)l.FooterHeight).PaddingTop((float)PrintTiling.BandGap).BorderTop(0.5f).BorderColor(Rule).Row(r =>
            {
                r.RelativeItem().AlignLeft().Text(text.FooterLeft).ClampLines(1);
                r.RelativeItem().AlignCenter().Text(text.FooterCenter).FontSize(text.HeaderCenter.Length > 0 ? 9 : 7.5f).Bold().FontColor("#1D2330").ClampLines(1);
                r.RelativeItem().AlignRight().Text(t =>
                {
                    if (!setup.ShowPageNumbers) return;
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages();
                });
            });
    }

    private static void ContentsPage(IDocumentContainer container, IReadOnlyList<PrintItem> items, PageSetup setup, string? title)
    {
        var first = items[0].Layout;
        container.Page(page =>
        {
            page.Size(SizeOf(first));
            page.PageColor(Colors.White);
            page.Margin((float)Math.Max(36, first.ImageArea.X));
            page.DefaultTextStyle(x => x.FontSize(10).FontColor("#1D2330"));
            var marking = PrintHeaderFooter.Marking(setup);
            if (marking.Length > 0) page.Header().AlignCenter().Text(marking).FontSize(9).Bold();
            page.Content().PaddingVertical(12).Column(col =>
            {
                col.Spacing(4);
                col.Item().Text(title ?? "Diagrams").FontSize(18).Bold().FontColor(Accent);
                col.Item().PaddingBottom(8).Text($"{items[0].Context.Project}  ·  {items.Count} diagram{(items.Count == 1 ? "" : "s")}  ·  {items[0].Context.Date}").FontColor(Grey);
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(cd => { cd.ConstantColumn(28); cd.RelativeColumn(3); cd.RelativeColumn(2); cd.RelativeColumn(3); cd.ConstantColumn(40); cd.ConstantColumn(40); });
                    table.Header(h =>
                    {
                        foreach (var head in new[] { "#", "Diagram", "Type", "Package", "Pages", "Page" })
                            h.Cell().BorderBottom(0.75f).BorderColor(Rule).PaddingVertical(3).Text(head).SemiBold().FontSize(9);
                    });
                    for (var i = 0; i < items.Count; i++)
                    {
                        var it = items[i];
                        var anchor = "diagram-" + i;
                        table.Cell().PaddingVertical(2).Text((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).FontSize(9);
                        table.Cell().PaddingVertical(2).Text(t => t.SectionLink(it.Context.DiagramName, anchor).FontColor(Accent));
                        table.Cell().PaddingVertical(2).Text(it.Context.DiagramType).FontSize(9);
                        table.Cell().PaddingVertical(2).Text(it.Context.PackagePath).FontSize(9).FontColor(Grey);
                        table.Cell().PaddingVertical(2).AlignRight().Text(it.Layout.PageCount.ToString(System.Globalization.CultureInfo.InvariantCulture)).FontSize(9);
                        table.Cell().PaddingVertical(2).AlignRight().Text(t => t.BeginPageNumberOfSection(anchor));
                    }
                });
            });
            if (setup.ShowPageNumbers)
                page.Footer().AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(7.5f).FontColor(Grey));
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages();
                });
        });
    }

    /// <summary>Overlap guides (dashed, where the neighbouring tile repeats this one) and registration marks, in points over the image area.</summary>
    public static string MarksSvg(PrintLayout l, PrintTile tile, bool registration, double width, double height)
    {
        var w = l.ImageArea.Width;
        var h = l.ImageArea.Height;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {PrintSource.F(w)} {PrintSource.F(h)}\" width=\"{PrintSource.F(width)}\" height=\"{PrintSource.F(height)}\" font-family=\"Helvetica, Arial, sans-serif\">");
        const string guide = "stroke=\"#9AA5B4\" stroke-width=\"0.5\" stroke-dasharray=\"3,2\" fill=\"none\"";
        var o = l.Overlap;
        if (o > 0)
        {
            if (tile.Column > 0) sb.Append($"<line x1=\"{PrintSource.F(o)}\" y1=\"0\" x2=\"{PrintSource.F(o)}\" y2=\"{PrintSource.F(h)}\" {guide}/>");
            if (tile.Column < l.Columns - 1) sb.Append($"<line x1=\"{PrintSource.F(w - o)}\" y1=\"0\" x2=\"{PrintSource.F(w - o)}\" y2=\"{PrintSource.F(h)}\" {guide}/>");
            if (tile.Row > 0) sb.Append($"<line x1=\"0\" y1=\"{PrintSource.F(o)}\" x2=\"{PrintSource.F(w)}\" y2=\"{PrintSource.F(o)}\" {guide}/>");
            if (tile.Row < l.Rows - 1) sb.Append($"<line x1=\"0\" y1=\"{PrintSource.F(h - o)}\" x2=\"{PrintSource.F(w)}\" y2=\"{PrintSource.F(h - o)}\" {guide}/>");
        }
        if (registration)
        {
            const double inset = 9, r = 4.5, arm = 7;
            foreach (var (x, y) in new[] { (inset, inset), (w - inset, inset), (inset, h - inset), (w - inset, h - inset) })
                sb.Append($"<g stroke=\"#1D2330\" stroke-width=\"0.4\" fill=\"none\"><circle cx=\"{PrintSource.F(x)}\" cy=\"{PrintSource.F(y)}\" r=\"{PrintSource.F(r)}\"/>" +
                          $"<line x1=\"{PrintSource.F(x - arm)}\" y1=\"{PrintSource.F(y)}\" x2=\"{PrintSource.F(x + arm)}\" y2=\"{PrintSource.F(y)}\"/>" +
                          $"<line x1=\"{PrintSource.F(x)}\" y1=\"{PrintSource.F(y - arm)}\" x2=\"{PrintSource.F(x)}\" y2=\"{PrintSource.F(y + arm)}\"/></g>");
            sb.Append($"<text x=\"{PrintSource.F(w - inset - arm - 3)}\" y=\"{PrintSource.F(h - inset + 2)}\" text-anchor=\"end\" font-size=\"6\" fill=\"#5F6B7A\">{WebUtility.HtmlEncode(tile.Label)}</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    // ================================================================ PNG pages (zip)

    /// <summary>Largest side of a page picture, in pixels (an A0 sheet at 300 dpi would be 14,000 px and 800 MB of pixels).</summary>
    public const int MaxPixels = 7000;

    /// <summary>The resolution a page is rasterised at: <paramref name="dpi"/>, lowered so the larger side stays within <see cref="MaxPixels"/>.</summary>
    public static int EffectiveDpi(PrintLayout l, int dpi) =>
        (int)Math.Max(36, Math.Min(Math.Clamp(dpi, 36, 600), Math.Floor(MaxPixels / (Math.Max(l.PageWidth, l.PageHeight) / 72))));

    /// <summary>A zip with one PNG per page, named after the diagram and the page; pages are rasterised one at a time to disk.</summary>
    public static void WritePngZip(IReadOnlyList<PrintItem> items, PageSetup setup, Stream output, int dpi = 150,
                                   IProgress<ReportProgress>? progress = null, CancellationToken ct = default)
    {
        var temp = Path.Combine(Path.GetTempPath(), "meridian-print-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = items[i];
                if (item.Layout.TooManyPages) throw new InvalidOperationException($"“{item.Context.DiagramName}” would print on {item.Layout.TotalPages:N0} pages (at most {PrintTiling.MaxPages}).");
                progress?.Report(new ReportProgress($"Rasterising “{item.Context.DiagramName}”", 0.05 + 0.9 * i / items.Count, i + 1, items.Count));
                var document = Compose([item], setup, contents: false, title: null, progress: null, ct);
                var settings = new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = EffectiveDpi(item.Layout, dpi) };
                var prefix = Path.Combine(temp, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Run(() => document.GenerateImages(n => $"{prefix}-{n}.png", settings), ct);
                var name = SafeName(item.Context.DiagramName);
                for (var k = 2; !used.Add(name); k++) name = $"{SafeName(item.Context.DiagramName)} ({k})";
                for (var p = 0; p < item.Layout.PageCount; p++)
                {
                    var file = $"{prefix}-{p}.png";
                    if (!File.Exists(file)) continue;
                    var entry = zip.CreateEntry(item.Layout.PageCount == 1 ? name + ".png" : $"{name} - page {p + 1} of {item.Layout.PageCount}.png", CompressionLevel.NoCompression);
                    using (var target = entry.Open())
                    using (var source = File.OpenRead(file))
                        source.CopyTo(target);
                    File.Delete(file);
                }
            }
            progress?.Report(new ReportProgress("Zip written", 1, items.Count, items.Count));
        }
        finally
        {
            try { Directory.Delete(temp, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp folder; the OS cleans it */ }
        }
    }

    public static string SafeName(string? s)
    {
        var cleaned = string.Join("_", (s ?? "").Split(Path.GetInvalidFileNameChars().Append('/').Append('\\').ToArray(), StringSplitOptions.RemoveEmptyEntries)).Trim().Trim('.');
        return cleaned.Length > 0 ? cleaned : "diagram";
    }

    // ================================================================ HTML for the browser's print dialog

    /// <summary>
    /// A self-contained HTML page for the browser's print dialog: <c>@page</c> sets the paper size and no margins (the sheet
    /// draws its own), each sheet shows one tile, and every diagram's body is written once and referenced by every tile.
    /// No scripts.
    /// </summary>
    public static string Html(IReadOnlyList<PrintItem> items, PageSetup setup, string title)
    {
        var sb = new StringBuilder();
        var first = items.Count > 0 ? items[0].Layout : PrintTiling.Compute(setup, new Bounds(0, 0, 400, 300));
        var total = TotalPages(items);
        string F(double v) => PrintSource.F(v);
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>").Append(WebUtility.HtmlEncode(title)).Append("</title><style>");
        sb.Append($"@page {{ size: {F(first.PageWidth)}pt {F(first.PageHeight)}pt; margin: 0; }}");
        sb.Append("html,body{margin:0;padding:0;background:#e9ecf1;font-family:Helvetica,Arial,sans-serif;-webkit-print-color-adjust:exact;print-color-adjust:exact}");
        sb.Append(".sheet{position:relative;background:#fff;overflow:hidden;margin:16px auto;box-shadow:0 1px 4px rgba(0,0,0,.25);break-after:page;page-break-after:always}");
        sb.Append(".sheet:last-child{break-after:auto;page-break-after:auto}");
        sb.Append(".band{position:absolute;display:flex;gap:8pt;font-size:7.5pt;color:#5F6B7A;align-items:flex-end}");
        sb.Append(".band span{flex:1;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.band .c{text-align:center;font-weight:700;color:#1D2330;font-size:9pt}.band .r{text-align:right}");
        sb.Append(".band.h{border-bottom:.5pt solid #B7C0CF;padding-bottom:2pt}.band.f{border-top:.5pt solid #B7C0CF;padding-top:2pt;align-items:flex-start}");
        sb.Append(".band.h .r{color:#1D2330;font-weight:600}.tile{position:absolute;display:block}");
        sb.Append("@media print{html,body{background:#fff}.sheet{margin:0;box-shadow:none}}");
        sb.Append("</style></head><body>");
        // Each diagram's body once, referenced by its tiles.
        sb.Append("<svg width=\"0\" height=\"0\" style=\"position:absolute\" aria-hidden=\"true\"><defs>");
        var sources = new List<PrintSource>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var src = items[i].Source();
            sources.Add(src);
            sb.Append($"<g id=\"mprint-{i}\"{src.RootAttributes}>").Append(src.Inner).Append("</g>");
        }
        sb.Append("</defs></svg>");
        var page = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var l = item.Layout;
            var a = l.ImageArea;
            foreach (var tile in l.Tiles)
            {
                page++;
                var text = PrintHeaderFooter.Compose(setup, item.Context, page, total);
                sb.Append($"<section class=\"sheet\" style=\"width:{F(l.PageWidth)}pt;height:{F(l.PageHeight)}pt\">");
                if (l.HeaderHeight > 0)
                    Band(sb, "h", a.X, a.Y - l.HeaderHeight, a.Width, PrintTiling.BandHeight, text.HeaderLeft, text.HeaderCenter, text.HeaderRight);
                var r = tile.Region;
                sb.Append($"<svg class=\"tile\" xmlns=\"http://www.w3.org/2000/svg\" style=\"left:{F(a.X)}pt;top:{F(a.Y)}pt;width:{F(a.Width)}pt;height:{F(a.Height)}pt\" viewBox=\"{F(r.X)} {F(r.Y)} {F(r.Width)} {F(r.Height)}\"><use href=\"#mprint-{i}\"/></svg>");
                if (setup.RegistrationMarks || l.Overlap > 0)
                    sb.Append(MarksSvg(l, tile, setup.RegistrationMarks, a.Width, a.Height).Replace("<svg ", $"<svg class=\"tile\" style=\"left:{F(a.X)}pt;top:{F(a.Y)}pt;width:{F(a.Width)}pt;height:{F(a.Height)}pt\" ", StringComparison.Ordinal));
                if (l.FooterHeight > 0)
                    Band(sb, "f", a.X, a.Bottom + PrintTiling.BandGap, a.Width, PrintTiling.BandHeight, text.FooterLeft, text.FooterCenter, text.FooterRight);
                sb.Append("</section>");
            }
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void Band(StringBuilder sb, string kind, double x, double y, double w, double h, string left, string center, string right)
    {
        string F(double v) => PrintSource.F(v);
        sb.Append($"<div class=\"band {kind}\" style=\"left:{F(x)}pt;top:{F(y)}pt;width:{F(w)}pt;height:{F(h - 2)}pt\">")
          .Append("<span>").Append(WebUtility.HtmlEncode(left)).Append("</span>")
          .Append("<span class=\"c\">").Append(WebUtility.HtmlEncode(center)).Append("</span>")
          .Append("<span class=\"r\">").Append(WebUtility.HtmlEncode(right)).Append("</span></div>");
    }

    // ================================================================ preview

    /// <summary>The printed region with the page boundaries drawn over it (numbered), for the print preview.</summary>
    public static string PreviewSvg(PrintSource source, PrintLayout layout)
    {
        var grid = layout.Grid;
        var c = source.Content;
        var x0 = Math.Min(grid.X, c.X);
        var y0 = Math.Min(grid.Y, c.Y);
        var x1 = Math.Max(grid.Right, c.Right);
        var y1 = Math.Max(grid.Bottom, c.Bottom);
        var pad = Math.Max(x1 - x0, y1 - y0) * 0.015;
        var box = new Bounds(x0 - pad, y0 - pad, x1 - x0 + 2 * pad, y1 - y0 + 2 * pad);
        var sb = new StringBuilder();
        string F(double v) => PrintSource.F(v);
        // Labels are sized against the whole picture so they read at any zoom.
        var label = Math.Max(box.Width, box.Height) / 45;
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\"{source.RootAttributes} viewBox=\"{F(box.X)} {F(box.Y)} {F(box.Width)} {F(box.Height)}\" width=\"{F(box.Width)}\" height=\"{F(box.Height)}\">");
        sb.Append($"<rect x=\"{F(box.X)}\" y=\"{F(box.Y)}\" width=\"{F(box.Width)}\" height=\"{F(box.Height)}\" fill=\"#d9dee6\"/>");
        foreach (var t in layout.Tiles)
            sb.Append($"<rect x=\"{F(t.Region.X)}\" y=\"{F(t.Region.Y)}\" width=\"{F(t.Region.Width)}\" height=\"{F(t.Region.Height)}\" fill=\"#ffffff\"/>");
        sb.Append(source.Inner);
        foreach (var t in layout.Tiles)
        {
            var r = t.Region;
            sb.Append($"<g class=\"print-page\"><rect x=\"{F(r.X)}\" y=\"{F(r.Y)}\" width=\"{F(r.Width)}\" height=\"{F(r.Height)}\" fill=\"none\" stroke=\"#2f6fec\" stroke-width=\"{F(label / 9)}\" stroke-dasharray=\"{F(label / 2)},{F(label / 3)}\"/>");
            sb.Append($"<rect x=\"{F(r.X)}\" y=\"{F(r.Y)}\" width=\"{F(label * 1.6)}\" height=\"{F(label * 1.4)}\" fill=\"#2f6fec\"/>");
            sb.Append($"<text x=\"{F(r.X + label * 0.8)}\" y=\"{F(r.Y + label * 1.05)}\" font-size=\"{F(label)}\" text-anchor=\"middle\" fill=\"#ffffff\" font-weight=\"700\">{t.Index + 1}</text></g>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }
}
