using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Meridian.Serialization.Reports;

/// <summary>Lays a <see cref="ResolvedDocument"/> out as PDF with QuestPDF: vector diagrams, live page numbers in the
/// table of contents, lists of figures / tables and cross-references, landscape page runs.</summary>
public static class DocPdf
{
    static DocPdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable = false;
    }

    private const float Margin = 54;          // 0.75 in
    private const string Accent = "#1F3B70";
    private const string Rule = "#B7C0CF";
    private const string HeaderFill = "#E8EDF5";
    private const string Mono = "Courier";

    public static byte[] Write(ResolvedDocument doc, IProgress<ReportProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new ReportProgress("Composing PDF", 0));
        var document = Compose(doc, ct);
        progress?.Report(new ReportProgress("Laying out pages", 0.5));
        var bytes = document.GeneratePdf();
        progress?.Report(new ReportProgress("PDF written", 1));
        return bytes;
    }

    /// <summary>The QuestPDF document (also usable for page images).</summary>
    public static IDocument Compose(ResolvedDocument doc, CancellationToken ct = default)
    {
        var size = string.Equals(doc.Template.PageSize, "A4", StringComparison.OrdinalIgnoreCase) ? PageSizes.A4 : PageSizes.Letter;
        var svgs = new Dictionary<string, SvgImage>();

        // Split into page runs at orientation changes.
        var runs = new List<(bool Landscape, List<DocNode> Nodes)> { (false, new()) };
        foreach (var n in doc.Nodes)
        {
            if (n is OrientationNode o) { runs.Add((o.Landscape, new())); continue; }
            runs[^1].Nodes.Add(n);
        }
        runs.RemoveAll(r => r.Nodes.Count == 0);

        return Document.Create(container =>
        {
            foreach (var (landscape, nodes) in runs)
            {
                ct.ThrowIfCancellationRequested();
                var pageSize = landscape ? size.Landscape() : size;
                var contentWidth = pageSize.Width - 2 * Margin;
                var contentHeight = pageSize.Height - 2 * Margin - 60;
                container.Page(page =>
                {
                    page.Size(pageSize);
                    page.Margin(Margin);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(x => x.FontSize(10).FontColor("#1D2330").LineHeight(1.25f));
                    page.Header().Element(c => Header(c, doc));
                    page.Footer().Element(c => Footer(c, doc));
                    page.Content().PaddingVertical(10).Column(col =>
                    {
                        col.Spacing(6);
                        for (var i = 0; i < nodes.Count; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            var n = nodes[i];
                            if (n is PageBreakNode) { col.Item().PageBreak(); continue; }
                            if (n is HeadingNode h)
                            {
                                if (h.PageBreakBefore && i > 0 && nodes[i - 1] is not PageBreakNode) col.Item().PageBreak();
                                // Keep a heading with the content that follows it.
                                var group = new List<DocNode> { h };
                                while (i + 1 < nodes.Count && nodes[i + 1] is HeadingNode { PageBreakBefore: false } next) { group.Add(next); i++; }
                                if (i + 1 < nodes.Count && nodes[i + 1] is ParagraphNode or TableNode or FigureNode) group.Add(nodes[++i]);
                                // A figure always fits one page: keep it whole with its heading; otherwise demand some room.
                                var item = group[^1] is FigureNode ? col.Item().PreventPageBreak() : col.Item().EnsureSpace(110);
                                item.Column(g =>
                                {
                                    g.Spacing(6);
                                    foreach (var x in group) Node(g.Item(), x, doc, contentWidth, contentHeight, svgs);
                                });
                                continue;
                            }
                            Node(col.Item(), n, doc, contentWidth, contentHeight, svgs);
                        }
                    });
                });
            }
        });
    }

    private static void Header(IContainer c, ResolvedDocument doc) =>
        c.BorderBottom(0.5f).BorderColor(Rule).PaddingBottom(4).Row(r =>
        {
            r.RelativeItem().AlignLeft().Text(doc.HeaderLeft).FontSize(8).FontColor(Colors.Grey.Darken2);
            r.RelativeItem().AlignCenter().Text(doc.Classification).FontSize(9).Bold();
            r.RelativeItem().AlignRight().Text(doc.Title).FontSize(8).FontColor(Colors.Grey.Darken2);
        });

    private static void Footer(IContainer c, ResolvedDocument doc) =>
        c.BorderTop(0.5f).BorderColor(Rule).PaddingTop(4).Row(r =>
        {
            r.RelativeItem().AlignLeft().Text(doc.Date).FontSize(8).FontColor(Colors.Grey.Darken2);
            r.RelativeItem().AlignCenter().Text(doc.Classification).FontSize(9).Bold();
            r.RelativeItem().AlignRight().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(8).FontColor(Colors.Grey.Darken2));
                t.Span("Page ");
                t.CurrentPageNumber();
                t.Span(" of ");
                t.TotalPages();
            });
        });

    private static void Node(IContainer c, DocNode n, ResolvedDocument doc, float width, float height, Dictionary<string, SvgImage> svgs)
    {
        switch (n)
        {
            case TitlePageNode: TitlePage(c, doc); break;
            case RevisionHistoryNode: RevisionHistory(c, doc); break;
            case ListingNode l: Listing(c, l.Kind, doc); break;
            case HeadingNode h:
            {
                var size = h.Level switch { 1 => 16f, 2 => 13.5f, 3 => 12f, _ => 10.5f };
                c.Section(h.Anchor).PaddingTop(h.Level == 1 ? 12 : 6).Text(t =>
                {
                    var span = t.Span(h.Display).FontSize(size).Bold();
                    if (h.Level <= 2) span.FontColor(Accent);
                });
                break;
            }
            case ParagraphNode p: Paragraph(c, p, doc); break;
            case TableNode t: Table(c, t); break;
            case FigureNode f: Figure(c, f, width, height, svgs); break;
        }
    }

    private static void Inlines(TextDescriptor t, IEnumerable<Inline> inlines, ResolvedDocument doc, bool italic = false, bool code = false)
    {
        foreach (var i in inlines)
        {
            switch (i)
            {
                case TextRun r:
                {
                    var s = t.Span(r.Text);
                    if (r.Bold) s.Bold();
                    if (r.Italic || italic) s.Italic();
                    if (r.Code || code) s.FontFamily(Mono).FontSize(9);
                    break;
                }
                case XRefRun x when doc.Targets.TryGetValue(x.Name, out var target):
                    if (x.Part == XRefPart.Page) t.BeginPageNumberOfSection(target.Anchor);
                    else t.SectionLink(x.Part == XRefPart.Title ? target.Title : target.Label, target.Anchor).FontColor(Accent);
                    break;
                case XRefRun x:
                    t.Span(doc.XRefText(x)).FontColor(Colors.Red.Darken2);
                    break;
            }
        }
    }

    private static void Paragraph(IContainer c, ParagraphNode p, ResolvedDocument doc)
    {
        switch (p.Kind)
        {
            case ParaKind.Bullet or ParaKind.Numbered:
                c.PaddingLeft(10 + 14 * p.Indent).Row(r =>
                {
                    r.ConstantItem(p.Kind == ParaKind.Bullet ? 12 : 20).Text(p.Kind == ParaKind.Bullet ? "•" : p.ListNumber + ".");
                    r.RelativeItem().Text(t => Inlines(t, p.Inlines, doc));
                });
                break;
            case ParaKind.Quote:
                c.BorderLeft(2).BorderColor(Rule).PaddingLeft(8).Text(t => Inlines(t, p.Inlines, doc, italic: true));
                break;
            case ParaKind.Code:
                c.Background("#F3F5F9").PaddingHorizontal(6).Text(t => Inlines(t, p.Inlines, doc, code: true));
                break;
            case ParaKind.Subheading:
                c.PaddingTop(4).Text(t => { t.DefaultTextStyle(s => s.Bold().FontSize(10.5f)); Inlines(t, p.Inlines, doc); });
                break;
            default:
                c.Text(t => Inlines(t, p.Inlines, doc));
                break;
        }
    }

    private static void CaptionText(IContainer c, Caption cap) =>
        c.Section(cap.Anchor).AlignCenter().Text(t =>
        {
            t.Span(cap.Label + (cap.Text.Length > 0 ? ": " : "")).Bold().FontSize(9);
            t.Span(cap.Text).FontSize(9);
        });

    private static void Table(IContainer c, TableNode t)
    {
        var cols = Math.Max(1, t.Headers.Count);
        var font = t.Compact || cols > 8 ? 8f : 9f;
        c.EnsureSpace(90).Column(col =>
        {
            col.Spacing(3);
            if (t.Caption != null) col.Item().Element(x => CaptionText(x, t.Caption));
            col.Item().Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    for (var i = 0; i < cols; i++) cd.RelativeColumn((float)(t.Widths != null && i < t.Widths.Count ? Math.Max(0.2, t.Widths[i]) : 1));
                });
                if (!t.KeyValue)
                    table.Header(h =>
                    {
                        foreach (var head in t.Headers)
                            h.Cell().Background(HeaderFill).Border(0.5f).BorderColor(Rule).Padding(3).Text(head).Bold().FontSize(font);
                    });
                foreach (var row in t.Rows)
                    for (var i = 0; i < cols; i++)
                    {
                        var cell = table.Cell().Border(0.5f).BorderColor(Rule).Padding(3);
                        if (t.KeyValue && i == 0) cell = cell.Background("#F6F8FB");
                        if (t.Matrix && i > 0) cell = cell.AlignCenter();
                        var text = cell.Text(i < row.Count ? row[i] : "").FontSize(font);
                        if (t.KeyValue && i == 0) text.SemiBold();
                    }
            });
        });
    }

    private static void Figure(IContainer c, FigureNode f, float width, float height, Dictionary<string, SvgImage> svgs)
    {
        if (!svgs.TryGetValue(f.Key, out var img)) svgs[f.Key] = img = SvgImage.FromText(f.Svg);
        // SVG user units are CSS px (1/96 in) → points; shrink to fit the text width and the height budget.
        var w = (float)(f.Width * 0.75);
        var h = (float)(f.Height * 0.75);
        var maxH = (float)(height * f.MaxHeightFraction);
        var scale = Math.Min(1f, Math.Min(width / w, maxH / h));
        w *= scale; h *= scale;
        c.PreventPageBreak().Column(col =>
        {
            col.Spacing(4);
            col.Item().AlignCenter().Width(w).Height(h).Svg(img).FitArea();
            col.Item().Element(x => CaptionText(x, f.Caption));
        });
    }

    private static void FrontHeading(IContainer c, string text) =>
        c.PaddingBottom(6).Text(text).FontSize(16).Bold().FontColor(Accent);

    private static void TitlePage(IContainer c, ResolvedDocument doc) =>
        c.PaddingTop(120).Column(col =>
        {
            col.Spacing(10);
            if (doc.Organization.Length > 0) col.Item().Text(doc.Organization).FontSize(13).FontColor(Colors.Grey.Darken2);
            col.Item().Text(doc.Title).FontSize(28).Bold().FontColor(Accent);
            if (doc.Subtitle.Length > 0) col.Item().Text(doc.Subtitle).FontSize(16).FontColor(Colors.Grey.Darken2);
            col.Item().PaddingTop(30).Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.ConstantColumn(140); cd.RelativeColumn(); });
                void Row(string k, string v)
                {
                    if (v.Length == 0) return;
                    t.Cell().PaddingVertical(3).Text(k).SemiBold().FontColor(Colors.Grey.Darken2);
                    t.Cell().PaddingVertical(3).Text(v);
                }
                Row("Document number", doc.DocumentNumber);
                Row("Revision", doc.Revision);
                Row("Date", doc.Date);
                Row("Classification", doc.Classification);
                Row("Prepared by", doc.Author);
                Row("Model", doc.ModelName);
            });
            foreach (var n in doc.TitleNotes)
                if (n is ParagraphNode p) col.Item().Element(x => Paragraph(x, p, doc));
        });

    private static void RevisionHistory(IContainer c, ResolvedDocument doc) =>
        c.Column(col =>
        {
            col.Spacing(6);
            col.Item().Element(x => FrontHeading(x, "Revision History"));
            col.Item().Element(x => Table(x, new TableNode(null, ["Revision", "Date", "Description", "Author"], RevisionRows(doc), [0.8, 1.2, 4, 1.5])));
        });

    internal static IReadOnlyList<IReadOnlyList<string>> RevisionRows(ResolvedDocument doc) =>
        doc.Template.Revisions.Count > 0
            ? doc.Template.Revisions.Select(r => (IReadOnlyList<string>)[r.Revision, r.Date, r.Description, r.Author]).ToList()
            : [[doc.Revision, doc.Date, "Initial release", doc.Author]];

    private static void Listing(IContainer c, ListingKind kind, ResolvedDocument doc) =>
        c.Column(col =>
        {
            col.Spacing(3);
            col.Item().Element(x => FrontHeading(x, kind switch { ListingKind.Contents => "Table of Contents", ListingKind.Figures => "List of Figures", _ => "List of Tables" }));
            var entries = kind switch
            {
                ListingKind.Contents => doc.Headings.Where(h => h.InToc && h.Level <= doc.Template.TocDepth).Select(h => (Text: h.Display, h.Anchor, Indent: (h.Level - 1) * 14f, Bold: h.Level == 1)),
                ListingKind.Figures => doc.Figures.Select(f => (Text: f.Display, f.Anchor, Indent: 0f, Bold: false)),
                _ => doc.Tables.Select(f => (Text: f.Display, f.Anchor, Indent: 0f, Bold: false)),
            };
            var any = false;
            foreach (var e in entries)
            {
                any = true;
                col.Item().PaddingLeft(e.Indent).Row(r =>
                {
                    r.RelativeItem().Text(t =>
                    {
                        var s = t.SectionLink(e.Text, e.Anchor);
                        if (e.Bold) s.SemiBold();
                    });
                    r.ConstantItem(40).AlignRight().Text(t => t.BeginPageNumberOfSection(e.Anchor));
                });
            }
            if (!any) col.Item().Text("None.").Italic();
        });
}
