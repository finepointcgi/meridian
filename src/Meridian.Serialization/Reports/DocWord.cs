using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using QuestPDF.Fluent;
using Q = QuestPDF.Infrastructure;
using Document = DocumentFormat.OpenXml.Wordprocessing.Document;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using SVG = DocumentFormat.OpenXml.Office2019.Drawing.SVG;

namespace Meridian.Serialization.Reports;

/// <summary>
/// Writes a <see cref="ResolvedDocument"/> as Word .docx with real Word structure: numbered "heading N" styles, a TOC
/// field, "Table of Figures" fields, captions built from SEQ / STYLEREF fields, bookmarks with REF / PAGEREF
/// cross-reference fields, PAGE / NUMPAGES in the footer, landscape sections, and diagrams as SVG with a PNG fallback.
/// Field results are pre-filled, and the document asks Word to update fields on open so page numbers are exact.
/// </summary>
public sealed class DocWord
{
    private const int MarginTw = 1080;   // 0.75 in
    private readonly ResolvedDocument _doc;
    private readonly IProgress<ReportProgress>? _progress;
    private readonly CancellationToken _ct;
    private MainDocumentPart _main = null!;
    private Body _body = null!;
    private string _headerId = "", _footerId = "";
    private int _bookmarkId, _drawingId = 1, _nextNumId = 3;
    private readonly int _pageW, _pageH;
    private bool _landscape;
    private readonly Dictionary<string, (string PngRel, string SvgRel)> _images = new();

    private DocWord(ResolvedDocument doc, IProgress<ReportProgress>? progress, CancellationToken ct)
    {
        _doc = doc; _progress = progress; _ct = ct;
        var a4 = string.Equals(doc.Template.PageSize, "A4", StringComparison.OrdinalIgnoreCase);
        (_pageW, _pageH) = a4 ? (11906, 16838) : (12240, 15840);
    }

    public static byte[] Write(ResolvedDocument doc, IProgress<ReportProgress>? progress = null, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        using (var w = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            new DocWord(doc, progress, ct).Build(w);
        return ms.ToArray();
    }

    private void Build(WordprocessingDocument w)
    {
        _main = w.AddMainDocumentPart();
        _main.Document = new Document(new Body());
        _body = _main.Document.Body!;
        AddStyles();
        AddNumbering();
        var settings = _main.AddNewPart<DocumentSettingsPart>();
        settings.Settings = new Settings(new UpdateFieldsOnOpen { Val = true });
        AddHeaderFooter();

        var nodes = _doc.Nodes;
        for (var i = 0; i < nodes.Count; i++)
        {
            if (i % 50 == 0)
            {
                _ct.ThrowIfCancellationRequested();
                _progress?.Report(new ReportProgress("Writing Word document", (double)i / nodes.Count));
            }
            Node(nodes[i], i > 0 ? nodes[i - 1] : null);
        }
        _body.Append(SectionProps(_landscape));
        _main.Document.Save();
    }

    // ================================================================== nodes

    private void Node(DocNode n, DocNode? prev)
    {
        switch (n)
        {
            case TitlePageNode: TitlePage(); break;
            case RevisionHistoryNode:
                _body.Append(Para("Revision History", "TOCHeading"));
                _body.Append(Table(new TableNode(null, ["Revision", "Date", "Description", "Author"], DocPdf.RevisionRows(_doc), [0.8, 1.2, 4, 1.5])));
                break;
            case ListingNode l: Listing(l.Kind); break;
            case PageBreakNode: _body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page }))); break;
            case OrientationNode o:
                // A section break: the paragraph's sectPr describes the section that ends here.
                _body.Append(new Paragraph(new ParagraphProperties(SectionProps(_landscape))));
                _landscape = o.Landscape;
                break;
            case HeadingNode h: Heading(h, prev is OrientationNode or PageBreakNode); break;
            case ParagraphNode p: _body.Append(Paragraph(p)); break;
            case TableNode t:
                if (t.Caption != null) _body.Append(CaptionPara(t.Caption, keepNext: true));
                _body.Append(Table(t));
                _body.Append(new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "0", Line = "120", LineRule = LineSpacingRuleValues.Exact })));
                break;
            case FigureNode f:
                _body.Append(Figure(f));
                _body.Append(CaptionPara(f.Caption, keepNext: false));
                break;
        }
    }

    private void Heading(HeadingNode h, bool atPageTop)
    {
        var level = Math.Clamp(h.Level, 1, 6);
        var ppr = new ParagraphProperties(new ParagraphStyleId { Val = "Heading" + level });
        if (h.PageBreakBefore && !atPageTop) ppr.Append(new PageBreakBefore());
        if (h.Number.Length == 0) ppr.Append(new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 0 }));
        var id = (++_bookmarkId).ToString(CultureInfo.InvariantCulture);
        _body.Append(new Paragraph(ppr,
            new BookmarkStart { Name = h.Anchor, Id = id },
            TextRun(h.Text),
            new BookmarkEnd { Id = id }));
    }

    private Paragraph CaptionPara(Caption c, bool keepNext)
    {
        var ppr = new ParagraphProperties(new ParagraphStyleId { Val = "Caption" });
        if (keepNext) ppr.Append(new KeepNext());
        var p = new Paragraph(ppr);
        var id = (++_bookmarkId).ToString(CultureInfo.InvariantCulture);
        p.Append(new BookmarkStart { Name = c.Anchor, Id = id });
        p.Append(TextRun(c.Kind + " "));
        if (c.Chapter > 0)
        {
            p.Append(Field(" STYLEREF 1 \\s ", c.Chapter.ToString(CultureInfo.InvariantCulture)));
            p.Append(TextRun("-"));
            p.Append(Field($" SEQ {c.Kind} \\* ARABIC \\s 1 ", c.Seq.ToString(CultureInfo.InvariantCulture)));
        }
        else p.Append(Field($" SEQ {c.Kind} \\* ARABIC ", c.Seq.ToString(CultureInfo.InvariantCulture)));
        p.Append(new BookmarkEnd { Id = id });
        if (c.Text.Length > 0) p.Append(TextRun(": " + c.Text));
        return p;
    }

    private void Listing(ListingKind kind)
    {
        var (title, instr, style) = kind switch
        {
            ListingKind.Contents => ("Table of Contents", $" TOC \\o \"1-{Math.Clamp(_doc.Template.TocDepth, 1, 6)}\" \\h \\z \\u ", "TOC"),
            ListingKind.Figures => ("List of Figures", " TOC \\h \\z \\c \"Figure\" ", "TableofFigures"),
            _ => ("List of Tables", " TOC \\h \\z \\c \"Table\" ", "TableofFigures"),
        };
        _body.Append(Para(title, "TOCHeading"));
        var entries = kind switch
        {
            ListingKind.Contents => _doc.Headings.Where(h => h.InToc && h.Level <= _doc.Template.TocDepth).Select(h => (h.Display, h.Anchor, Style: "TOC" + Math.Min(h.Level, 6))),
            ListingKind.Figures => _doc.Figures.Select(f => (f.Display, f.Anchor, Style: style)),
            _ => _doc.Tables.Select(f => (f.Display, f.Anchor, Style: style)),
        };
        var list = entries.ToList();
        // The field spans several paragraphs: begin + instruction + separate in the first, end in the last.
        var first = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = list.Count > 0 ? list[0].Style : style }));
        first.Append(new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
                     new Run(new FieldCode(instr) { Space = SpaceProcessingModeValues.Preserve }),
                     new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }));
        var paras = new List<Paragraph>();
        for (var i = 0; i < list.Count; i++)
        {
            var p = i == 0 ? first : new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = list[i].Style }));
            var link = new Hyperlink { Anchor = list[i].Anchor, History = true };
            link.Append(TextRun(list[i].Display), new Run(new TabChar()));
            foreach (var r in Field($" PAGEREF {list[i].Anchor} \\h ", "")) link.Append(r);
            p.Append(link);
            paras.Add(p);
        }
        if (paras.Count == 0) { first.Append(TextRun("No entries.")); paras.Add(first); }
        paras[^1].Append(new Run(new FieldChar { FieldCharType = FieldCharValues.End }));
        foreach (var p in paras) _body.Append(p);
    }

    private void TitlePage()
    {
        _body.Append(new Paragraph(new ParagraphProperties(new SpacingBetweenLines { Before = "2400" })));
        if (_doc.Organization.Length > 0) _body.Append(Para(_doc.Organization, "Subtitle"));
        _body.Append(Para(_doc.Title, "Title"));
        if (_doc.Subtitle.Length > 0) _body.Append(Para(_doc.Subtitle, "Subtitle"));
        var rows = new List<IReadOnlyList<string>>();
        void Row(string k, string v) { if (v.Length > 0) rows.Add([k, v]); }
        Row("Document number", _doc.DocumentNumber);
        Row("Revision", _doc.Revision);
        Row("Date", _doc.Date);
        Row("Classification", _doc.Classification);
        Row("Prepared by", _doc.Author);
        Row("Model", _doc.ModelName);
        _body.Append(Table(new TableNode(null, ["", ""], rows, [1, 3], KeyValue: true)));
        foreach (var n in _doc.TitleNotes) if (n is ParagraphNode p) _body.Append(Paragraph(p));
    }

    // ================================================================== text

    private static Run TextRun(string text, bool bold = false, bool italic = false, bool code = false)
    {
        var run = new Run();
        if (bold || italic || code)
        {
            var rp = new RunProperties();
            if (code) rp.Append(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas", ComplexScript = "Consolas" });
            if (bold) rp.Append(new Bold());
            if (italic) rp.Append(new Italic());
            run.Append(rp);
        }
        run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return run;
    }

    /// <summary>A complex field (begin / instruction / separate / cached result / end).</summary>
    private static IEnumerable<Run> Field(string instruction, string result)
    {
        yield return new Run(new FieldChar { FieldCharType = FieldCharValues.Begin });
        yield return new Run(new FieldCode(instruction) { Space = SpaceProcessingModeValues.Preserve });
        yield return new Run(new FieldChar { FieldCharType = FieldCharValues.Separate });
        yield return new Run(new Text(result) { Space = SpaceProcessingModeValues.Preserve });
        yield return new Run(new FieldChar { FieldCharType = FieldCharValues.End });
    }

    private static Paragraph Para(string text, string? style = null, bool bold = false)
    {
        var p = new Paragraph();
        if (style != null) p.Append(new ParagraphProperties(new ParagraphStyleId { Val = style }));
        p.Append(TextRun(text, bold));
        return p;
    }

    private Paragraph Paragraph(ParagraphNode n)
    {
        var p = new Paragraph();
        var ppr = new ParagraphProperties();
        switch (n.Kind)
        {
            case ParaKind.Bullet:
                ppr.Append(new ParagraphStyleId { Val = "ListBullet" },
                           new NumberingProperties(new NumberingLevelReference { Val = n.Indent }, new NumberingId { Val = 2 }));
                break;
            case ParaKind.Numbered:
                if (n.ListNumber <= 1) _nextNumId++;
                ppr.Append(new ParagraphStyleId { Val = "ListNumber" },
                           new NumberingProperties(new NumberingLevelReference { Val = n.Indent }, new NumberingId { Val = EnsureNumberedList(_nextNumId) }));
                break;
            case ParaKind.Quote: ppr.Append(new ParagraphStyleId { Val = "Quote" }); break;
            case ParaKind.Code: ppr.Append(new ParagraphStyleId { Val = "Code" }); break;
            case ParaKind.Subheading: ppr.Append(new ParagraphStyleId { Val = "Subheading" }); break;
        }
        if (ppr.HasChildren) p.Append(ppr);
        foreach (var i in n.Inlines)
        {
            switch (i)
            {
                case TextRun r: p.Append(TextRun(r.Text, r.Bold, r.Italic, r.Code)); break;
                case XRefRun x when _doc.Targets.TryGetValue(x.Name, out var t):
                    switch (x.Part)
                    {
                        case XRefPart.Page: p.Append(Field($" PAGEREF {t.Anchor} \\h ", "")); break;
                        case XRefPart.Title: p.Append(new Hyperlink(TextRun(t.Title)) { Anchor = t.Anchor }); break;
                        default:
                            if (t.Kind == TargetKind.Section && t.Label.StartsWith("Section ", StringComparison.Ordinal))
                            {
                                p.Append(TextRun("Section "));
                                p.Append(Field($" REF {t.Anchor} \\r \\h ", t.Label["Section ".Length..]));
                            }
                            else if (t.Kind == TargetKind.Section) p.Append(Field($" REF {t.Anchor} \\h ", t.Title));
                            else p.Append(Field($" REF {t.Anchor} \\h ", t.Label));
                            break;
                    }
                    break;
                case XRefRun x: p.Append(TextRun(_doc.XRefText(x), italic: true)); break;
            }
        }
        return p;
    }

    private readonly HashSet<int> _createdNums = new();

    private int EnsureNumberedList(int numId)
    {
        if (_createdNums.Add(numId))
        {
            var numbering = _main.NumberingDefinitionsPart!.Numbering!;
            numbering.Append(new NumberingInstance(new AbstractNumId { Val = 2 },
                new LevelOverride(new StartOverrideNumberingValue { Val = 1 }) { LevelIndex = 0 }) { NumberID = numId });
        }
        return numId;
    }

    // ================================================================== tables and figures

    private int ContentWidthTw => (_landscape ? _pageH : _pageW) - 2 * MarginTw;
    private int ContentHeightTw => (_landscape ? _pageW : _pageH) - 2 * MarginTw - 1200;

    private Table Table(TableNode t)
    {
        var cols = Math.Max(1, t.Headers.Count);
        var weights = Enumerable.Range(0, cols).Select(i => t.Widths != null && i < t.Widths.Count ? Math.Max(0.2, t.Widths[i]) : 1.0).ToList();
        var total = weights.Sum();
        var widths = weights.Select(w => (int)(ContentWidthTw * w / total)).ToList();
        const string border = "B7C0CF";
        var table = new Table(new TableProperties(
            new TableWidth { Width = ContentWidthTw.ToString(CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Dxa },
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4, Color = border }, new LeftBorder { Val = BorderValues.Single, Size = 4, Color = border },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Color = border }, new RightBorder { Val = BorderValues.Single, Size = 4, Color = border },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = border }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = border }),
            new TableLayout { Type = TableLayoutValues.Fixed },
            new TableCellMarginDefault(new TopMargin { Width = "30", Type = TableWidthUnitValues.Dxa }, new TableCellLeftMargin { Width = 80, Type = TableWidthValues.Dxa },
                                       new BottomMargin { Width = "30", Type = TableWidthUnitValues.Dxa }, new TableCellRightMargin { Width = 80, Type = TableWidthValues.Dxa })));
        table.Append(new TableGrid(widths.Select(w => new GridColumn { Width = w.ToString(CultureInfo.InvariantCulture) })));
        var style = t.Compact || cols > 8 ? "TableTextSmall" : "TableText";

        TableCell Cell(string text, int col, bool header, bool keyCol)
        {
            var tcp = new TableCellProperties(new TableCellWidth { Width = widths[col].ToString(CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Dxa });
            if (header || keyCol) tcp.Append(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = header ? "E8EDF5" : "F6F8FB" });
            var p = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = style }));
            if (t.Matrix && col > 0) p.ParagraphProperties!.Append(new Justification { Val = JustificationValues.Center });
            var lines = text.ReplaceLineEndings("\n").Split('\n');
            var run = new Run();
            if (header || keyCol) run.Append(new RunProperties(new Bold()));
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) run.Append(new Break());
                run.Append(new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
            }
            p.Append(run);
            return new TableCell(tcp, p);
        }

        if (!t.KeyValue)
            table.Append(new TableRow([new TableRowProperties(new TableHeader(), new CantSplit()), .. t.Headers.Select((h, i) => Cell(h, i, true, false))]));
        foreach (var row in t.Rows)
            table.Append(new TableRow([new TableRowProperties(new CantSplit()), .. Enumerable.Range(0, cols).Select(i => Cell(i < row.Count ? row[i] : "", i, false, t.KeyValue && i == 0))]));
        return table;
    }

    private Paragraph Figure(FigureNode f)
    {
        if (!_images.TryGetValue(f.Key, out var rels))
        {
            var png = Rasterize(f.Svg, f.Width, f.Height);
            var pngPart = _main.AddImagePart(ImagePartType.Png);
            using (var s = new MemoryStream(png)) pngPart.FeedData(s);
            var svgPart = _main.AddImagePart(ImagePartType.Svg);
            using (var s = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(f.Svg))) svgPart.FeedData(s);
            _images[f.Key] = rels = (_main.GetIdOfPart(pngPart), _main.GetIdOfPart(svgPart));
        }
        // px → EMU (9525 per px), then fit the text width and the height budget (1 twip = 635 EMU).
        double cx = f.Width * 9525, cy = f.Height * 9525;
        double maxW = ContentWidthTw * 635.0, maxH = ContentHeightTw * 635.0 * f.MaxHeightFraction;
        var scale = Math.Min(1, Math.Min(maxW / cx, maxH / cy));
        long lcx = (long)(cx * scale), lcy = (long)(cy * scale);
        var id = (uint)_drawingId++;
        var blip = new A.Blip(new A.BlipExtensionList(new A.BlipExtension(new SVG.SVGBlip { Embed = rels.SvgRel }) { Uri = "{96DAC541-7B7A-43D3-8B79-37D633B846F1}" }))
            { Embed = rels.PngRel };
        var inline = new DW.Inline(
            new DW.Extent { Cx = lcx, Cy = lcy },
            new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
            new DW.DocProperties { Id = id, Name = "Figure " + f.Caption.Number, Description = f.Caption.Display },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = id, Name = $"diagram{id}.svg" }, new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(blip, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = lcx, Cy = lcy }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        { DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0 };
        return new Paragraph(new ParagraphProperties(new KeepNext(), new Justification { Val = JustificationValues.Center }), new Run(new Drawing(inline)));
    }

    /// <summary>PNG fallback for Word versions without SVG support (rendered by QuestPDF's Skia backend).</summary>
    internal static byte[] Rasterize(string svg, double w, double h, int dpi = 110)
    {
        QuestPDF.Settings.License = Q.LicenseType.Community;
        var images = QuestPDF.Fluent.Document.Create(c => c.Page(p =>
        {
            p.Size(new QuestPDF.Helpers.PageSize((float)(w * 0.75), (float)(h * 0.75)));
            p.Margin(0);
            p.PageColor(QuestPDF.Helpers.Colors.White);
            p.Content().Svg(svg).FitArea();
        })).GenerateImages(new Q.ImageGenerationSettings { RasterDpi = dpi, ImageFormat = Q.ImageFormat.Png });
        return images.First();
    }

    // ================================================================== package parts

    private SectionProperties SectionProps(bool landscape)
    {
        var sp = new SectionProperties(
            new HeaderReference { Type = HeaderFooterValues.Default, Id = _headerId },
            new FooterReference { Type = HeaderFooterValues.Default, Id = _footerId },
            new SectionType { Val = SectionMarkValues.NextPage },
            landscape
                ? new PageSize { Width = (uint)_pageH, Height = (uint)_pageW, Orient = PageOrientationValues.Landscape }
                : new PageSize { Width = (uint)_pageW, Height = (uint)_pageH },
            new PageMargin { Top = MarginTw, Bottom = MarginTw, Left = (uint)MarginTw, Right = (uint)MarginTw, Header = 500, Footer = 500, Gutter = 0 });
        return sp;
    }

    private void AddHeaderFooter()
    {
        static Run PTab(AbsolutePositionTabAlignmentValues align) =>
            new(new PositionalTab { Alignment = align, RelativeTo = AbsolutePositionTabPositioningBaseValues.Margin, Leader = AbsolutePositionTabLeaderCharValues.None });

        var header = _main.AddNewPart<HeaderPart>();
        header.Header = new Header(new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Header" }),
            TextRun(_doc.HeaderLeft), PTab(AbsolutePositionTabAlignmentValues.Center), TextRun(_doc.Classification, bold: true),
            PTab(AbsolutePositionTabAlignmentValues.Right), TextRun(_doc.Title)));
        header.Header.Save();
        _headerId = _main.GetIdOfPart(header);

        var footer = _main.AddNewPart<FooterPart>();
        var fp = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Footer" }),
            TextRun(_doc.Date), PTab(AbsolutePositionTabAlignmentValues.Center), TextRun(_doc.Classification, bold: true),
            PTab(AbsolutePositionTabAlignmentValues.Right), TextRun("Page "));
        foreach (var r in Field(" PAGE ", "1")) fp.Append(r);
        fp.Append(TextRun(" of "));
        foreach (var r in Field(" NUMPAGES ", "1")) fp.Append(r);
        footer.Footer = new Footer(fp);
        footer.Footer.Save();
        _footerId = _main.GetIdOfPart(footer);
    }

    private void AddNumbering()
    {
        var part = _main.AddNewPart<NumberingDefinitionsPart>();
        Level HeadingLevel(int i) => new(
            new StartNumberingValue { Val = 1 },
            new NumberingFormat { Val = NumberFormatValues.Decimal },
            new ParagraphStyleIdInLevel { Val = "Heading" + (i + 1) },
            new LevelText { Val = string.Join(".", Enumerable.Range(1, i + 1).Select(n => "%" + n)) },
            new LevelJustification { Val = LevelJustificationValues.Left },
            new PreviousParagraphProperties(new Indentation { Left = (432 + 144 * i).ToString(CultureInfo.InvariantCulture), Hanging = (432 + 144 * i).ToString(CultureInfo.InvariantCulture) }))
        { LevelIndex = i };
        Level ListLevel(int i, bool bullet) => new(
            new StartNumberingValue { Val = 1 },
            new NumberingFormat { Val = bullet ? NumberFormatValues.Bullet : NumberFormatValues.Decimal },
            new LevelText { Val = bullet ? (i % 2 == 0 ? "•" : "–") : $"%{i + 1}." },
            new LevelJustification { Val = LevelJustificationValues.Left },
            new PreviousParagraphProperties(new Indentation { Left = (360 + 360 * i).ToString(CultureInfo.InvariantCulture), Hanging = "360" }))
        { LevelIndex = i };

        part.Numbering = new Numbering(
            new AbstractNum(new MultiLevelType { Val = MultiLevelValues.Multilevel }, HeadingLevel(0), HeadingLevel(1), HeadingLevel(2), HeadingLevel(3), HeadingLevel(4), HeadingLevel(5)) { AbstractNumberId = 0 },
            new AbstractNum(new MultiLevelType { Val = MultiLevelValues.HybridMultilevel }, ListLevel(0, true), ListLevel(1, true), ListLevel(2, true), ListLevel(3, true)) { AbstractNumberId = 1 },
            new AbstractNum(new MultiLevelType { Val = MultiLevelValues.HybridMultilevel }, ListLevel(0, false), ListLevel(1, false), ListLevel(2, false), ListLevel(3, false)) { AbstractNumberId = 2 },
            new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 1 },
            new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 2 });
    }

    private void AddStyles()
    {
        var part = _main.AddNewPart<StyleDefinitionsPart>();
        Style P(string id, string name, int halfPts, bool bold = false, string color = "1D2330", int before = 0, int after = 120,
                bool keepNext = false, string basedOn = "Normal", int? outline = null, bool italic = false, string? font = null, int? numLevel = null,
                bool tabs = false, string? shade = null, int indent = 0)
        {
            var ppr = new StyleParagraphProperties();
            if (keepNext) ppr.Append(new KeepNext(), new KeepLines());
            if (numLevel is { } lvl) ppr.Append(new NumberingProperties(new NumberingLevelReference { Val = lvl }, new NumberingId { Val = 1 }));
            if (shade != null) ppr.Append(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = shade });
            if (tabs) ppr.Append(new Tabs(new TabStop { Val = TabStopValues.Right, Leader = TabStopLeaderCharValues.Dot, Position = 9350 }));
            ppr.Append(new SpacingBetweenLines { Before = before.ToString(CultureInfo.InvariantCulture), After = after.ToString(CultureInfo.InvariantCulture) });
            if (indent > 0) ppr.Append(new Indentation { Left = indent.ToString(CultureInfo.InvariantCulture) });
            if (outline is { } o) ppr.Append(new OutlineLevel { Val = o });
            var rpr = new StyleRunProperties();
            if (font != null) rpr.Append(new RunFonts { Ascii = font, HighAnsi = font, ComplexScript = font });
            if (bold) rpr.Append(new Bold());
            if (italic) rpr.Append(new Italic());
            rpr.Append(new Color { Val = color }, new FontSize { Val = halfPts.ToString(CultureInfo.InvariantCulture) });
            var s = new Style(new StyleName { Val = name });
            if (id != "Normal") s.Append(new BasedOn { Val = basedOn }, new NextParagraphStyle { Val = "Normal" });
            s.Append(new PrimaryStyle(), ppr, rpr);
            s.Type = StyleValues.Paragraph;
            s.StyleId = id;
            if (id == "Normal") s.Default = true;
            return s;
        }

        var styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri", EastAsia = "Calibri" },
                    new FontSize { Val = "21" }, new Languages { Val = "en-US" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { After = "120", Line = "264", LineRule = LineSpacingRuleValues.Auto }))),
            P("Normal", "Normal", 21),
            P("Title", "Title", 56, bold: true, color: "1F3B70", after: 240),
            P("Subtitle", "Subtitle", 32, color: "4C586D", after: 200),
            P("Heading1", "heading 1", 32, true, "1F3B70", 360, 120, true, outline: 0, numLevel: 0),
            P("Heading2", "heading 2", 27, true, "1F3B70", 240, 100, true, outline: 1, numLevel: 1),
            P("Heading3", "heading 3", 24, true, "1D2330", 200, 80, true, outline: 2, numLevel: 2),
            P("Heading4", "heading 4", 22, true, "1D2330", 160, 60, true, outline: 3, numLevel: 3),
            P("Heading5", "heading 5", 21, true, "1D2330", 120, 60, true, outline: 4, numLevel: 4),
            P("Heading6", "heading 6", 21, true, "1D2330", 120, 60, true, outline: 5, italic: true, numLevel: 5),
            P("TOCHeading", "TOC Heading", 32, true, "1F3B70", 240, 200, true),
            P("TOC1", "toc 1", 21, true, after: 40, tabs: true),
            P("TOC2", "toc 2", 21, after: 30, tabs: true, indent: 220),
            P("TOC3", "toc 3", 20, after: 20, tabs: true, indent: 440),
            P("TOC4", "toc 4", 20, after: 20, tabs: true, indent: 660),
            P("TOC5", "toc 5", 20, after: 20, tabs: true, indent: 880),
            P("TOC6", "toc 6", 20, after: 20, tabs: true, indent: 1100),
            P("TableofFigures", "table of figures", 21, after: 40, tabs: true),
            P("Caption", "caption", 18, true, "1D2330", 60, 160),
            P("TableText", "Table Text", 18, after: 0),
            P("TableTextSmall", "Table Text Small", 16, after: 0),
            P("ListBullet", "List Bullet", 21, after: 60),
            P("ListNumber", "List Number", 21, after: 60),
            P("Quote", "Quote", 21, italic: true, color: "4C586D", indent: 576),
            P("Code", "Code", 18, font: "Consolas", after: 0, shade: "F3F5F9"),
            P("Subheading", "Subheading", 22, true, keepNext: true, before: 120, after: 60),
            P("Header", "header", 16, color: "4C586D", after: 0),
            P("Footer", "footer", 16, color: "4C586D", after: 0));
        part.Styles = styles;
        part.Styles.Save();
    }
}
