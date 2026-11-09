using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Validation;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace Meridian.Serialization;

public enum ReportSection { TitlePage, Summary, Requirements, Traceability, Diagrams, Elements, Validation }

/// <summary>A report template: which sections, in which order, and which diagrams/elements to include.</summary>
public sealed record ReportTemplate(
    string Title,
    IReadOnlyList<ReportSection> Sections,
    IReadOnlyCollection<DiagramType>? DiagramTypes = null,
    IReadOnlyCollection<ElementKind>? ElementKinds = null,
    string? Author = null)
{
    public static ReportTemplate Full(string title) => new(title, Enum.GetValues<ReportSection>());

    public static readonly IReadOnlyDictionary<string, ReportSection[]> Presets = new Dictionary<string, ReportSection[]>
    {
        ["System design description"] = [ReportSection.TitlePage, ReportSection.Summary, ReportSection.Diagrams, ReportSection.Elements],
        ["Requirements specification"] = [ReportSection.TitlePage, ReportSection.Summary, ReportSection.Requirements, ReportSection.Traceability],
        ["Verification & validation report"] = [ReportSection.TitlePage, ReportSection.Requirements, ReportSection.Traceability, ReportSection.Validation],
        ["Full model report"] = Enum.GetValues<ReportSection>(),
    };

    internal IEnumerable<Diagram> SelectDiagrams(ModelRepository m) =>
        m.Diagrams.Where(d => DiagramTypes is null || DiagramTypes.Count == 0 || DiagramTypes.Contains(d.DiagramType)).OrderBy(d => d.QualifiedName);

    internal IEnumerable<Element> SelectElements(ModelRepository m) =>
        m.AllElements.Where(e => e is not Relationship && e is not Diagram && e.Kind is not (ElementKind.Model or ElementKind.Package)
                                 && (ElementKinds is null || ElementKinds.Count == 0 ? Metamodel.IsClassifier(e.Kind) && e.Kind != ElementKind.Requirement : ElementKinds.Contains(e.Kind)))
         .OrderBy(e => e.QualifiedName);
}

/// <summary>Word (.docx) and PDF documents generated from a <see cref="ReportTemplate"/>.</summary>
public static class DocumentReports
{
    static DocumentReports() => QuestPDF.Settings.License = LicenseType.Community;

    private static string Ts(DateTime d) => d.ToString("MMMM d, yyyy");

    /// <summary>Rasterises an SVG diagram to PNG (via QuestPDF's Skia backend) for formats that need bitmaps.</summary>
    public static (byte[] Png, double Width, double Height) DiagramPng(ModelRepository m, Diagram d, int dpi = 150)
    {
        var svg = SvgRenderer.Render(m, d);
        var (w, h) = SvgSize(svg);
        var images = QuestPDF.Fluent.Document.Create(c => c.Page(p =>
        {
            p.Size(new QuestPDF.Helpers.PageSize((float)w, (float)h));
            p.Margin(0);
            p.PageColor(Colors.White);
            p.Content().Svg(svg);
        })).GenerateImages(new ImageGenerationSettings { RasterDpi = dpi, ImageFormat = ImageFormat.Png });
        return (images.First(), w, h);
    }

    private static (double W, double H) SvgSize(string svg)
    {
        double Attr(string name)
        {
            var i = svg.IndexOf($" {name}=\"", StringComparison.Ordinal);
            if (i < 0) return 400;
            var start = i + name.Length + 3;
            var end = svg.IndexOf('"', start);
            return double.TryParse(svg[start..end], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 400;
        }
        return (Math.Max(50, Attr("width")), Math.Max(50, Attr("height")));
    }

    // =============================================================== PDF

    public static byte[] Pdf(ModelRepository m, ReportTemplate t)
    {
        var coverage = Traceability.Coverage(m);
        var issues = new ModelValidator().Validate(m);
        return QuestPDF.Fluent.Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(x => x.FontSize(10));
                page.Header().Text(t.Title).FontSize(9).FontColor(Colors.Grey.Darken1);
                page.Footer().AlignRight().Text(x => { x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
                page.Content().Column(col =>
                {
                    col.Spacing(8);
                    foreach (var section in t.Sections)
                    {
                        switch (section)
                        {
                            case ReportSection.TitlePage:
                                col.Item().PaddingTop(160).Text(t.Title).FontSize(28).Bold();
                                col.Item().Text(m.Root.Name).FontSize(16).FontColor(Colors.Grey.Darken2);
                                col.Item().Text($"{Ts(DateTime.Now)}{(t.Author is { Length: > 0 } a ? " · " + a : "")}").FontColor(Colors.Grey.Darken1);
                                if (!string.IsNullOrWhiteSpace(m.Root.Documentation)) col.Item().PaddingTop(20).Text(m.Root.Documentation);
                                col.Item().PageBreak();
                                break;
                            case ReportSection.Summary:
                                col.Item().Text("Summary").FontSize(18).Bold();
                                col.Item().Table(table =>
                                {
                                    table.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(1); });
                                    void Row(string k, string v) { table.Cell().Padding(3).Text(k); table.Cell().Padding(3).AlignRight().Text(v).Bold(); }
                                    Row("Elements", m.AllElements.Count(e => e is not Relationship && e is not Diagram).ToString());
                                    Row("Relationships", m.Relationships.Count().ToString());
                                    Row("Diagrams", m.Diagrams.Count().ToString());
                                    Row("Requirements satisfied", $"{coverage.Satisfied}/{coverage.Total}");
                                    Row("Requirements verified", $"{coverage.Verified}/{coverage.Total}");
                                    Row("Validation errors / warnings", $"{issues.Count(i => i.Severity == Severity.Error)} / {issues.Count(i => i.Severity == Severity.Warning)}");
                                });
                                break;
                            case ReportSection.Requirements:
                                col.Item().PaddingTop(10).Text("Requirements").FontSize(18).Bold();
                                col.Item().Table(table =>
                                {
                                    table.ColumnsDefinition(c => { c.ConstantColumn(60); c.RelativeColumn(2); c.RelativeColumn(4); c.RelativeColumn(2); });
                                    table.Header(h => { foreach (var s in new[] { "Id", "Name", "Text", "Satisfied by" }) h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text(s).Bold(); });
                                    foreach (var r in m.OfKind(ElementKind.Requirement).OrderBy(r => r.Get(FieldKeys.RequirementId)))
                                    {
                                        table.Cell().BorderBottom(0.5f).Padding(3).Text(r.Get(FieldKeys.RequirementId));
                                        table.Cell().BorderBottom(0.5f).Padding(3).Text(r.Name);
                                        table.Cell().BorderBottom(0.5f).Padding(3).Text(r.Get(FieldKeys.Text));
                                        table.Cell().BorderBottom(0.5f).Padding(3).Text(string.Join(", ", m.Incoming(r, ElementKind.Satisfy).Select(x => x.Source.Name)));
                                    }
                                });
                                break;
                            case ReportSection.Traceability:
                                col.Item().PaddingTop(10).Text("Traceability").FontSize(18).Bold();
                                foreach (var r in m.OfKind(ElementKind.Requirement).OrderBy(r => r.Get(FieldKeys.RequirementId)))
                                {
                                    var links = m.RelationshipsOf(r).Select(x => $"{(ReferenceEquals(x.Target, r) ? "←" : "→")} «{Metamodel.Style(x.Kind).Keyword ?? Metamodel.DisplayName(x.Kind)}» {(ReferenceEquals(x.Target, r) ? x.Source : x.Target).Name}").ToList();
                                    col.Item().Text(text =>
                                    {
                                        text.Span($"{r.Get(FieldKeys.RequirementId)} {r.Name}: ").Bold();
                                        text.Span(links.Count == 0 ? "no links" : string.Join("; ", links));
                                    });
                                }
                                break;
                            case ReportSection.Diagrams:
                                col.Item().PaddingTop(10).Text("Diagrams").FontSize(18).Bold();
                                foreach (var d in t.SelectDiagrams(m))
                                {
                                    col.Item().Text($"{d.Name} ({Metamodel.DisplayName(d.DiagramType)})").FontSize(13).Bold();
                                    if (!string.IsNullOrWhiteSpace(d.Documentation)) col.Item().Text(d.Documentation);
                                    col.Item().MaxHeight(600).Svg(SvgRenderer.Render(m, d)).FitArea();
                                }
                                break;
                            case ReportSection.Elements:
                                col.Item().PaddingTop(10).Text("Element specifications").FontSize(18).Bold();
                                foreach (var e in t.SelectElements(m))
                                {
                                    col.Item().Text($"{Metamodel.DisplayName(e.Kind)} {e.Name}").FontSize(12).Bold();
                                    if (!string.IsNullOrWhiteSpace(e.Documentation)) col.Item().Text(e.Documentation);
                                    var features = e.OwnedElements.Where(c => c.Kind is ElementKind.Property or ElementKind.Port or ElementKind.Operation).ToList();
                                    foreach (var f in features)
                                        col.Item().PaddingLeft(12).Text(f.Kind == ElementKind.Operation ? f.Name + f.Get(FieldKeys.Signature) : Notation.FeatureLine(m, f)).FontFamily("Courier");
                                }
                                break;
                            case ReportSection.Validation:
                                col.Item().PaddingTop(10).Text("Validation").FontSize(18).Bold();
                                if (issues.Count == 0) col.Item().Text("No issues found.");
                                foreach (var i in issues.Take(300))
                                    col.Item().Text($"{i.Severity.ToString().ToUpperInvariant()} {i.RuleId} — {i.Message} ({i.Element.QualifiedName})")
                                        .FontColor(i.Severity == Severity.Error ? Colors.Red.Darken2 : i.Severity == Severity.Warning ? Colors.Orange.Darken3 : Colors.Blue.Darken2);
                                break;
                        }
                    }
                });
            });
        }).GeneratePdf();
    }

    // =============================================================== Word

    public static byte[] Docx(ModelRepository m, ReportTemplate t)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new Body());
            AddStyles(main);
            var body = main.Document.Body!;
            var coverage = Traceability.Coverage(m);
            var issues = new ModelValidator().Validate(m);
            uint imageId = 1;

            Paragraph P(string text, string? style = null, bool bold = false)
            {
                var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
                if (bold) run.RunProperties = new RunProperties(new Bold());
                var p = new Paragraph(run);
                if (style != null) p.ParagraphProperties = new ParagraphProperties(new ParagraphStyleId { Val = style });
                return p;
            }
            Table Grid(IEnumerable<string> header, IEnumerable<IEnumerable<string>> rows)
            {
                var table = new Table(new TableProperties(
                    new TableBorders(new TopBorder { Val = BorderValues.Single, Size = 4 }, new BottomBorder { Val = BorderValues.Single, Size = 4 },
                        new InsideHorizontalBorder { Val = BorderValues.Single, Size = 2 }),
                    new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }));
                table.Append(new TableRow(header.Select(h => new TableCell(new TableCellProperties(new Shading { Fill = "E8EDF5", Val = ShadingPatternValues.Clear }), P(h, bold: true)))));
                foreach (var r in rows) table.Append(new TableRow(r.Select(c => new TableCell(P(c)))));
                return table;
            }

            foreach (var section in t.Sections)
            {
                switch (section)
                {
                    case ReportSection.TitlePage:
                        body.Append(P(t.Title, "Title"));
                        body.Append(P(m.Root.Name, "Subtitle"));
                        body.Append(P($"{Ts(DateTime.Now)}{(t.Author is { Length: > 0 } a ? " · " + a : "")}"));
                        if (!string.IsNullOrWhiteSpace(m.Root.Documentation)) body.Append(P(m.Root.Documentation));
                        body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                        break;
                    case ReportSection.Summary:
                        body.Append(P("Summary", "Heading1"));
                        body.Append(Grid(["Measure", "Value"],
                        [
                            ["Elements", m.AllElements.Count(e => e is not Relationship && e is not Diagram).ToString()],
                            ["Relationships", m.Relationships.Count().ToString()],
                            ["Diagrams", m.Diagrams.Count().ToString()],
                            ["Requirements satisfied", $"{coverage.Satisfied}/{coverage.Total}"],
                            ["Requirements verified", $"{coverage.Verified}/{coverage.Total}"],
                            ["Validation errors / warnings", $"{issues.Count(i => i.Severity == Severity.Error)} / {issues.Count(i => i.Severity == Severity.Warning)}"],
                        ]));
                        break;
                    case ReportSection.Requirements:
                        body.Append(P("Requirements", "Heading1"));
                        body.Append(Grid(["Id", "Name", "Text", "Satisfied by", "Verified by"],
                            m.OfKind(ElementKind.Requirement).OrderBy(r => r.Get(FieldKeys.RequirementId)).Select(r => (IEnumerable<string>)
                            [
                                r.Get(FieldKeys.RequirementId), r.Name, r.Get(FieldKeys.Text),
                                string.Join(", ", m.Incoming(r, ElementKind.Satisfy).Select(x => x.Source.Name)),
                                string.Join(", ", m.Incoming(r, ElementKind.Verify).Select(x => x.Source.Name)),
                            ])));
                        break;
                    case ReportSection.Traceability:
                        body.Append(P("Traceability", "Heading1"));
                        foreach (var r in m.OfKind(ElementKind.Requirement).OrderBy(r => r.Get(FieldKeys.RequirementId)))
                        {
                            var links = m.RelationshipsOf(r).Select(x => $"{(ReferenceEquals(x.Target, r) ? "←" : "→")} «{Metamodel.Style(x.Kind).Keyword ?? Metamodel.DisplayName(x.Kind)}» {(ReferenceEquals(x.Target, r) ? x.Source : x.Target).Name}").ToList();
                            body.Append(P($"{r.Get(FieldKeys.RequirementId)} {r.Name}: {(links.Count == 0 ? "no links" : string.Join("; ", links))}"));
                        }
                        break;
                    case ReportSection.Diagrams:
                        body.Append(P("Diagrams", "Heading1"));
                        foreach (var d in t.SelectDiagrams(m))
                        {
                            body.Append(P($"{d.Name} ({Metamodel.DisplayName(d.DiagramType)})", "Heading2"));
                            if (!string.IsNullOrWhiteSpace(d.Documentation)) body.Append(P(d.Documentation));
                            var (png, w, h) = DiagramPng(m, d);
                            body.Append(Image(main, png, w, h, imageId++, d.Name));
                        }
                        break;
                    case ReportSection.Elements:
                        body.Append(P("Element specifications", "Heading1"));
                        foreach (var e in t.SelectElements(m))
                        {
                            body.Append(P($"{Metamodel.DisplayName(e.Kind)} {e.Name}", "Heading2"));
                            if (!string.IsNullOrWhiteSpace(e.Documentation)) body.Append(P(e.Documentation));
                            var features = e.OwnedElements.Where(c => c.Kind is ElementKind.Property or ElementKind.Port or ElementKind.Operation).ToList();
                            if (features.Count > 0)
                                body.Append(Grid(["Feature", "Kind", "Description"], features.Select(f => (IEnumerable<string>)
                                    [f.Kind == ElementKind.Operation ? f.Name + f.Get(FieldKeys.Signature) : Notation.FeatureLine(m, f), Metamodel.DisplayName(f.Kind), f.Documentation])));
                        }
                        break;
                    case ReportSection.Validation:
                        body.Append(P("Validation", "Heading1"));
                        if (issues.Count == 0) body.Append(P("No issues found."));
                        else body.Append(Grid(["Severity", "Rule", "Element", "Message"], issues.Take(500).Select(i => (IEnumerable<string>)[i.Severity.ToString(), i.RuleId, i.Element.QualifiedName, i.Message])));
                        break;
                }
            }
            body.Append(new SectionProperties(new DocumentFormat.OpenXml.Wordprocessing.PageSize { Width = 11906, Height = 16838 }, new PageMargin { Top = 1134, Bottom = 1134, Left = 1134, Right = 1134 }));
            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        Style S(string id, string name, int sizeHalfPts, bool bold, string color = "1D2330", int spaceBefore = 240) => new(
            new StyleName { Val = name }, new BasedOn { Val = "Normal" }, new NextParagraphStyle { Val = "Normal" }, new PrimaryStyle(),
            new StyleParagraphProperties(new SpacingBetweenLines { Before = spaceBefore.ToString(), After = "120" }, new KeepNext()),
            new StyleRunProperties(bold ? new Bold() : null!, new DocumentFormat.OpenXml.Wordprocessing.Color { Val = color }, new FontSize { Val = sizeHalfPts.ToString() }))
        { Type = StyleValues.Paragraph, StyleId = id };
        part.Styles = new Styles(
            new DocDefaults(new RunPropertiesDefault(new RunPropertiesBaseStyle(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }, new FontSize { Val = "21" }))),
            new Style(new StyleName { Val = "Normal" }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
            S("Title", "Title", 56, true, "1F2A3C", 2400), S("Subtitle", "Subtitle", 32, false, "4C586D", 120),
            S("Heading1", "heading 1", 32, true, "2F6FEC"), S("Heading2", "heading 2", 26, true, "1D2330", 180));
        part.Styles.Save();
    }

    private static Paragraph Image(MainDocumentPart main, byte[] png, double widthPt, double heightPt, uint id, string name)
    {
        var imagePart = main.AddImagePart(ImagePartType.Png);
        using (var s = new MemoryStream(png)) imagePart.FeedData(s);
        var relId = main.GetIdOfPart(imagePart);
        // Fit within the text width (≈ 6.7 in) keeping aspect ratio. 1 pt = 12700 EMU.
        const double maxWidthEmu = 6.7 * 914400;
        var cx = widthPt * 12700; var cy = heightPt * 12700;
        if (cx > maxWidthEmu) { cy *= maxWidthEmu / cx; cx = maxWidthEmu; }
        var (lcx, lcy) = ((long)cx, (long)cy);
        var inline = new DW.Inline(
            new DW.Extent { Cx = lcx, Cy = lcy },
            new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
            new DW.DocProperties { Id = id, Name = name },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = id, Name = name + ".png" }, new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = relId }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = lcx, Cy = lcy }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        { DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0 };
        return new Paragraph(new Run(new Drawing(inline)));
    }
}
