using System.Net;
using System.Text;

namespace Meridian.Serialization.Reports;

/// <summary>Writes a <see cref="ResolvedDocument"/> as one self-contained HTML file (inline SVG diagrams, linked TOC,
/// lists of figures / tables, cross-reference links, classification banners and print styles).</summary>
public static class DocHtml
{
    private static string E(string s) => WebUtility.HtmlEncode(s);

    public static string Write(ResolvedDocument doc, IProgress<ReportProgress>? progress = null, CancellationToken ct = default, bool preview = false)
    {
        using var sw = new StringWriter(new StringBuilder(64 * 1024), System.Globalization.CultureInfo.InvariantCulture);
        Write(doc, sw, progress, ct, preview);
        return sw.ToString();
    }

    /// <summary>Streams the document to <paramref name="output"/> (flushed, not closed).</summary>
    public static void Write(ResolvedDocument doc, Stream output, IProgress<ReportProgress>? progress = null, CancellationToken ct = default, bool preview = false)
    {
        using var w = HtmlSink.Utf8(output);
        Write(doc, w, progress, ct, preview);
    }

    /// <summary>Streams the document to <paramref name="writer"/>; nothing is buffered beyond the writer's own buffer.</summary>
    public static void Write(ResolvedDocument doc, TextWriter writer, IProgress<ReportProgress>? progress = null, CancellationToken ct = default, bool preview = false)
    {
        var sb = new HtmlSink(writer);
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>").Append(E(doc.Title)).Append("</title><style>").Append(Css).Append("</style></head><body>");
        if (doc.Classification.Length > 0) sb.Append("<div class=\"banner top\">").Append(E(doc.Classification)).Append("</div>");
        sb.Append("<main>");
        var landscape = false;
        for (var i = 0; i < doc.Nodes.Count; i++)
        {
            if (i % 200 == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ReportProgress("Writing HTML", (double)i / doc.Nodes.Count));
            }
            var n = doc.Nodes[i];
            switch (n)
            {
                case TitlePageNode: TitlePage(sb, doc); break;
                case RevisionHistoryNode:
                    sb.Append("<section class=\"front\"><h2 class=\"front\">Revision History</h2>");
                    Table(sb, new TableNode(null, ["Revision", "Date", "Description", "Author"], DocPdf.RevisionRows(doc)), doc);
                    sb.Append("</section>");
                    break;
                case ListingNode l: Listing(sb, l.Kind, doc); break;
                case PageBreakNode: sb.Append("<div class=\"pb\"></div>"); break;
                case OrientationNode o:
                    if (landscape) sb.Append("</div>");
                    landscape = o.Landscape;
                    if (landscape) sb.Append("<div class=\"landscape\">");
                    break;
                case HeadingNode h:
                    var tag = "h" + Math.Clamp(h.Level + 1, 2, 6);
                    sb.Append('<').Append(tag).Append(" id=\"").Append(h.Anchor).Append('"').Append(h.PageBreakBefore ? " class=\"pbb\"" : "").Append('>');
                    if (h.Number.Length > 0) sb.Append("<span class=\"num\">").Append(E(h.Number)).Append("</span> ");
                    sb.Append(E(h.Text)).Append("</").Append(tag).Append('>');
                    break;
                case ParagraphNode p: Paragraph(sb, p, doc, doc.Nodes, ref i); break;
                case TableNode t: Table(sb, t, doc); break;
                case FigureNode f:
                    sb.Append("<figure id=\"").Append(f.Caption.Anchor).Append("\"><div class=\"diagram\">").Append(StripXmlDecl(f.Svg)).Append("</div>");
                    Caption(sb, f.Caption, "figcaption");
                    sb.Append("</figure>");
                    break;
            }
        }
        if (landscape) sb.Append("</div>");
        sb.Append("</main>");
        if (doc.Classification.Length > 0) sb.Append("<div class=\"banner bottom\">").Append(E(doc.Classification)).Append("</div>");
        if (preview && doc.Warnings.Count > 0)
        {
            sb.Append("<aside class=\"warnings\"><b>Template warnings</b><ul>");
            foreach (var w in doc.Warnings.Distinct()) sb.Append("<li>").Append(E(w)).Append("</li>");
            sb.Append("</ul></aside>");
        }
        sb.Append("</body></html>");
        writer.Flush();
    }

    private static string StripXmlDecl(string svg) => svg.StartsWith("<?xml", StringComparison.Ordinal) ? svg[(svg.IndexOf("?>", StringComparison.Ordinal) + 2)..] : svg;

    private static void TitlePage(HtmlSink sb, ResolvedDocument doc)
    {
        sb.Append("<section class=\"title\">");
        if (doc.Organization.Length > 0) sb.Append("<div class=\"org\">").Append(E(doc.Organization)).Append("</div>");
        sb.Append("<h1>").Append(E(doc.Title)).Append("</h1>");
        if (doc.Subtitle.Length > 0) sb.Append("<div class=\"subtitle\">").Append(E(doc.Subtitle)).Append("</div>");
        sb.Append("<table class=\"props\">");
        void Row(string k, string v) { if (v.Length > 0) sb.Append("<tr><th>").Append(k).Append("</th><td>").Append(E(v)).Append("</td></tr>"); }
        Row("Document number", doc.DocumentNumber);
        Row("Revision", doc.Revision);
        Row("Date", doc.Date);
        Row("Classification", doc.Classification);
        Row("Prepared by", doc.Author);
        Row("Model", doc.ModelName);
        sb.Append("</table>");
        for (var i = 0; i < doc.TitleNotes.Count; i++) if (doc.TitleNotes[i] is ParagraphNode p) Paragraph(sb, p, doc, doc.TitleNotes, ref i);
        sb.Append("</section>");
    }

    private static void Listing(HtmlSink sb, ListingKind kind, ResolvedDocument doc)
    {
        sb.Append("<nav class=\"front ").Append(kind.ToString().ToLowerInvariant()).Append("\"><h2 class=\"front\">")
          .Append(kind switch { ListingKind.Contents => "Table of Contents", ListingKind.Figures => "List of Figures", _ => "List of Tables" }).Append("</h2>");
        var entries = kind switch
        {
            ListingKind.Contents => doc.Headings.Where(h => h.InToc && h.Level <= doc.Template.TocDepth).Select(h => (h.Display, h.Anchor, h.Level)),
            ListingKind.Figures => doc.Figures.Select(f => (f.Display, f.Anchor, Level: 1)),
            _ => doc.Tables.Select(f => (f.Display, f.Anchor, Level: 1)),
        };
        var any = false;
        foreach (var (text, anchor, level) in entries)
        {
            any = true;
            sb.Append("<a class=\"l").Append(level).Append("\" href=\"#").Append(anchor).Append("\">").Append(E(text)).Append("</a>");
        }
        if (!any) sb.Append("<p><i>None.</i></p>");
        sb.Append("</nav>");
    }

    private static void Inlines(HtmlSink sb, IEnumerable<Inline> inlines, ResolvedDocument doc)
    {
        foreach (var i in inlines)
        {
            switch (i)
            {
                case TextRun r:
                    var t = E(r.Text);
                    if (r.Code) t = "<code>" + t + "</code>";
                    if (r.Italic) t = "<em>" + t + "</em>";
                    if (r.Bold) t = "<strong>" + t + "</strong>";
                    sb.Append(t);
                    break;
                case XRefRun x when doc.Targets.TryGetValue(x.Name, out var target):
                    sb.Append("<a class=\"xref\" href=\"#").Append(target.Anchor).Append("\">")
                      .Append(E(x.Part switch { XRefPart.Title => target.Title, XRefPart.Page => target.Label, _ => target.Label })).Append("</a>");
                    break;
                case XRefRun x:
                    sb.Append("<span class=\"missing\">").Append(E(doc.XRefText(x))).Append("</span>");
                    break;
            }
        }
    }

    private static void Paragraph(HtmlSink sb, ParagraphNode p, ResolvedDocument doc, IReadOnlyList<DocNode> list, ref int index)
    {
        if (p.Kind is ParaKind.Bullet or ParaKind.Numbered)
        {
            // Group consecutive list items of one kind into a single list.
            var tag = p.Kind == ParaKind.Bullet ? "ul" : "ol";
            sb.Append('<').Append(tag).Append(p.Kind == ParaKind.Numbered && p.ListNumber > 1 ? $" start=\"{p.ListNumber}\"" : "").Append('>');
            sb.Append("<li").Append(p.Indent > 0 ? $" style=\"margin-left:{p.Indent * 1.5}em\"" : "").Append('>');
            Inlines(sb, p.Inlines, doc);
            sb.Append("</li>");
            while (index + 1 < list.Count && list[index + 1] is ParagraphNode next && next.Kind == p.Kind && !(next.Kind == ParaKind.Numbered && next.ListNumber == 1))
            {
                index++;
                sb.Append("<li").Append(next.Indent > 0 ? $" style=\"margin-left:{next.Indent * 1.5}em\"" : "").Append('>');
                Inlines(sb, next.Inlines, doc);
                sb.Append("</li>");
            }
            sb.Append("</").Append(tag).Append('>');
            return;
        }
        var (open, close) = p.Kind switch
        {
            ParaKind.Quote => ("<blockquote>", "</blockquote>"),
            ParaKind.Code => ("<pre>", "</pre>"),
            ParaKind.Subheading => ("<p class=\"sub\">", "</p>"),
            _ => ("<p>", "</p>"),
        };
        sb.Append(open);
        Inlines(sb, p.Inlines, doc);
        sb.Append(close);
    }

    private static void Caption(HtmlSink sb, Caption c, string tag)
    {
        sb.Append('<').Append(tag).Append(" class=\"caption\"><b>").Append(E(c.Label)).Append("</b>");
        if (c.Text.Length > 0) sb.Append(": ").Append(E(c.Text));
        sb.Append("</").Append(tag).Append('>');
    }

    private static void Table(HtmlSink sb, TableNode t, ResolvedDocument doc)
    {
        sb.Append("<div class=\"tablewrap\"").Append(t.Caption != null ? $" id=\"{t.Caption.Anchor}\"" : "").Append('>');
        sb.Append("<table").Append(t.KeyValue ? " class=\"kv\"" : t.Compact ? " class=\"compact\"" : "").Append('>');
        if (t.Caption != null) Caption(sb, t.Caption, "caption");
        if (t.Widths is { Count: > 0 } w && w.Count == t.Headers.Count)
        {
            var total = w.Sum();
            sb.Append("<colgroup>");
            foreach (var x in w) sb.Append("<col style=\"width:").Append((100 * x / total).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)).Append("%\">");
            sb.Append("</colgroup>");
        }
        if (!t.KeyValue)
        {
            sb.Append("<thead><tr>");
            foreach (var h in t.Headers) sb.Append("<th>").Append(E(h)).Append("</th>");
            sb.Append("</tr></thead>");
        }
        sb.Append("<tbody>");
        foreach (var row in t.Rows)
        {
            sb.Append("<tr>");
            for (var i = 0; i < Math.Max(1, t.Headers.Count); i++)
            {
                var cell = i < row.Count ? row[i] : "";
                var td = t.KeyValue && i == 0 ? "th" : "td";
                sb.Append('<').Append(td).Append(t.Matrix && i > 0 ? " class=\"c\"" : "").Append('>').Append(E(cell).Replace("\n", "<br>")).Append("</").Append(td).Append('>');
            }
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></div>");
    }

    private const string Css = """
        :root{--ink:#1d2330;--muted:#5b6578;--rule:#cfd6e2;--head:#e8edf5;--accent:#1f3b70;--bg:#fff}
        @media (prefers-color-scheme: dark){:root:not([data-theme="light"]){--ink:#e3e7ef;--muted:#a3adbf;--rule:#3a4354;--head:#253047;--accent:#9db8f0;--bg:#141821}
          :root:not([data-theme="light"]) .diagram{background:#fff}}
        *{box-sizing:border-box}
        body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.5 -apple-system,"Segoe UI",Helvetica,Arial,sans-serif}
        main{max-width:960px;margin:0 auto;padding:24px 16px 48px}
        .banner{text-align:center;font-weight:700;font-size:12px;letter-spacing:.08em;padding:3px 0;background:var(--head);position:sticky;z-index:2}
        .banner.top{top:0}.banner.bottom{bottom:0}
        .title{padding:12vh 0 6vh}.title h1{font-size:34px;color:var(--accent);margin:8px 0}.org,.subtitle{color:var(--muted);font-size:18px}
        table.props{width:auto;margin-top:32px}table.props th{background:none;border:none;color:var(--muted);padding-right:24px}table.props td{border:none}
        h2,h3,h4,h5,h6{color:var(--accent);line-height:1.25;scroll-margin-top:32px}h2{font-size:24px;margin-top:40px;border-bottom:1px solid var(--rule);padding-bottom:4px}
        h3{font-size:19px;margin-top:28px}h4{font-size:16px}h5,h6{font-size:15px;color:var(--ink)}
        .num{color:var(--muted);margin-right:.35em}
        nav.front a{display:block;color:inherit;text-decoration:none;padding:1px 0}nav.front a:hover{text-decoration:underline}
        nav.front .l1{font-weight:600;margin-top:6px}nav.front .l2{padding-left:1.5em}nav.front .l3{padding-left:3em}nav.front .l4,nav.front .l5,nav.front .l6{padding-left:4.5em}
        .tablewrap{overflow-x:auto;margin:12px 0}
        table{border-collapse:collapse;width:100%;font-size:13.5px}th,td{border:1px solid var(--rule);padding:4px 7px;text-align:left;vertical-align:top}
        thead th{background:var(--head)}table.kv th{background:var(--head);width:28%}table.compact{font-size:12.5px}td.c{text-align:center}
        caption,figcaption{caption-side:top;text-align:center;font-size:13.5px;padding:6px 0}
        figure{margin:20px 0}.diagram{overflow-x:auto;text-align:center;border:1px solid var(--rule);border-radius:6px;padding:8px;background:#fff}
        .diagram svg{max-width:100%;height:auto}
        blockquote{margin:8px 0;padding-left:12px;border-left:3px solid var(--rule);color:var(--muted);font-style:italic}
        pre{background:var(--head);padding:8px;overflow-x:auto;font-size:13px}code{font-size:.92em}
        .sub{font-weight:600;margin-bottom:4px}a.xref{color:var(--accent)}.missing{color:#c62828}
        .landscape{border-left:3px solid var(--head);padding-left:12px}
        .warnings{max-width:960px;margin:0 auto 24px;padding:12px 16px;border:1px solid #e0b252;background:#fff8e6;color:#5c4400}
        @media print{.banner{position:fixed}.pb{break-after:page}.pbb{break-before:page}.title{break-after:page}nav.front{break-after:page}
          main{max-width:none}thead{display:table-header-group}tr,figure{break-inside:avoid}}
        """;
}
