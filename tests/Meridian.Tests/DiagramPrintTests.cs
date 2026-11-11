using System.IO.Compression;
using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Core.Samples;
using Meridian.Serialization;
using Meridian.Serialization.Printing;
using UglyToad.PdfPig;

namespace Meridian.Tests;

/// <summary>Diagram printing: tiling math, header/footer layout, and the PDF / PNG / HTML outputs.</summary>
public class DiagramPrintTests
{
    private const double Pt = PageSetup.PointsPerMm;

    private static PageSetup A4Landscape(Action<PageSetup>? change = null)
    {
        var s = new PageSetup { Paper = PaperSize.A4, Landscape = true };
        change?.Invoke(s);
        return s.Normalize();
    }

    private static PageSetup NoBands(PageSetup s)
    {
        s.ShowProject = s.ShowDiagramName = s.ShowDiagramType = s.ShowDate = s.ShowPageNumbers = false;
        return s;
    }

    // ============================================================ tiling math

    [Fact]
    public void Paper_sizes_follow_the_orientation()
    {
        Assert.Equal((297, 210), A4Landscape().PaperMm);
        Assert.Equal((841, 1189), new PageSetup { Paper = PaperSize.A0, Landscape = false }.PaperMm);
        Assert.Equal((431.8, 279.4), new PageSetup { Paper = PaperSize.Tabloid, Landscape = true }.PaperMm);
        Assert.Equal((300, 500), new PageSetup { Paper = PaperSize.Custom, CustomWidthMm = 500, CustomHeightMm = 300, Landscape = false }.PaperMm);
    }

    [Fact]
    public void Margins_and_bands_shrink_the_image_area()
    {
        var bare = NoBands(A4Landscape(s => { s.MarginLeftMm = 20; s.MarginRightMm = 10; s.MarginTopMm = 15; s.MarginBottomMm = 5; }));
        var l = PrintTiling.Compute(bare, new Bounds(0, 0, 100, 100));
        Assert.Equal(297 * Pt, l.PageWidth, 3);
        Assert.Equal(210 * Pt, l.PageHeight, 3);
        Assert.Equal(20 * Pt, l.ImageArea.X, 3);
        Assert.Equal(15 * Pt, l.ImageArea.Y, 3);
        Assert.Equal((297 - 30) * Pt, l.ImageArea.Width, 3);
        Assert.Equal((210 - 20) * Pt, l.ImageArea.Height, 3);
        Assert.Equal(0, l.HeaderHeight);
        Assert.Equal(0, l.FooterHeight);

        // A header and a footer each take a band (and a gap) from the height, never the width.
        var banded = A4Landscape(s => { s.MarginLeftMm = 20; s.MarginRightMm = 10; s.MarginTopMm = 15; s.MarginBottomMm = 5; });
        var b = PrintTiling.Compute(banded, new Bounds(0, 0, 100, 100));
        var band = PrintTiling.BandHeight + PrintTiling.BandGap;
        Assert.Equal(l.ImageArea.Width, b.ImageArea.Width, 3);
        Assert.Equal(l.ImageArea.Height - 2 * band, b.ImageArea.Height, 3);
        Assert.Equal(l.ImageArea.Y + band, b.ImageArea.Y, 3);
    }

    [Fact]
    public void Fit_to_one_page_scales_to_the_tighter_side()
    {
        var s = NoBands(A4Landscape());
        var wide = PrintTiling.Compute(s, new Bounds(0, 0, 4000, 500));
        Assert.Equal(1, wide.PageCount);
        Assert.Equal(wide.ImageArea.Width / (4000 * PrintTiling.PointsPerPx), wide.Scale, 6);
        var tall = PrintTiling.Compute(s, new Bounds(0, 0, 500, 4000));
        Assert.Equal(tall.ImageArea.Height / (4000 * PrintTiling.PointsPerPx), tall.Scale, 6);
        // A small diagram is enlarged to fill the page, and centred on it.
        var small = PrintTiling.Compute(s, new Bounds(100, 100, 200, 100));
        Assert.True(small.Scale > 1);
        Assert.Equal(small.Content.Center.X, small.Tiles[0].Region.Center.X, 6);
        Assert.Equal(small.Content.Center.Y, small.Tiles[0].Region.Center.Y, 6);
    }

    [Fact]
    public void Fit_to_N_by_M_prints_exactly_that_many_pages_covering_the_content()
    {
        var content = new Bounds(-50, 20, 1600, 700);
        var l = PrintTiling.Compute(A4Landscape(s => { s.Scale = PrintScaleMode.FitToPages; s.PagesAcross = 3; s.PagesDown = 2; }), content);
        Assert.Equal(3, l.Columns);
        Assert.Equal(2, l.Rows);
        Assert.Equal(6, l.PageCount);
        // Row-major order, adjacent tiles, and the grid contains the content.
        Assert.Equal((1, 0), (l.Tiles[1].Column, l.Tiles[1].Row));
        Assert.Equal((0, 1), (l.Tiles[3].Column, l.Tiles[3].Row));
        Assert.Equal(l.Tiles[0].Region.Right, l.Tiles[1].Region.X, 6);
        Assert.Equal(l.Tiles[0].Region.Bottom, l.Tiles[3].Region.Y, 6);
        Assert.True(l.Grid.X <= content.X + 1e-6 && l.Grid.Right >= content.Right - 1e-6);
        Assert.True(l.Grid.Y <= content.Y + 1e-6 && l.Grid.Bottom >= content.Bottom - 1e-6);
        // One side fits exactly (that is the scale's limit).
        Assert.True(Math.Abs(l.Grid.Width - content.Width) < 1e-6 || Math.Abs(l.Grid.Height - content.Height) < 1e-6);
    }

    [Fact]
    public void Overlap_repeats_a_strip_of_each_neighbour()
    {
        var l = PrintTiling.Compute(A4Landscape(s => { s.Scale = PrintScaleMode.FitToPages; s.PagesAcross = 2; s.PagesDown = 2; s.OverlapMm = 10; }), new Bounds(0, 0, 2000, 1400));
        Assert.Equal(10 * Pt, l.Overlap, 6);
        var overlapUnits = l.Overlap * l.UnitsPerPoint;
        Assert.Equal(overlapUnits, l.Tiles[0].Region.Right - l.Tiles[1].Region.X, 6);
        Assert.Equal(overlapUnits, l.Tiles[0].Region.Bottom - l.Tiles[2].Region.Y, 6);
        // The span of two overlapping pages is two pages less one overlap.
        Assert.Equal(2 * l.ImageArea.Width - l.Overlap, PrintTiling.Span(2, l.ImageArea.Width, l.Overlap), 6);
        // Never more than a third of a page.
        var huge = PrintTiling.Compute(A4Landscape(s => s.OverlapMm = 50), new Bounds(0, 0, 100, 100));
        Assert.True(huge.Overlap <= Math.Min(huge.ImageArea.Width, huge.ImageArea.Height) / 3 + 1e-9);
    }

    [Theory]
    [InlineData(1000, 0, 1)]     // 750 pt fits one 785 pt page
    [InlineData(1100, 0, 2)]     // 825 pt
    [InlineData(3000, 0, 3)]     // 2,250 pt
    [InlineData(3000, 10, 3)]    // (2250 − 28) / (785 − 28) = 2.94
    [InlineData(3100, 10, 4)]    // (2325 − 28) / 757 = 3.03
    public void Fixed_scale_computes_the_page_count(double widthPx, double overlapMm, int across)
    {
        var s = NoBands(A4Landscape(x => { x.Scale = PrintScaleMode.Fixed; x.ScalePercent = 100; x.OverlapMm = overlapMm; }));
        var l = PrintTiling.Compute(s, new Bounds(0, 0, widthPx, 100));
        Assert.Equal(across, l.Columns);
        Assert.Equal(1, l.Rows);
        Assert.Equal(1, l.Scale, 9);
        Assert.Equal(across, PrintTiling.PagesFor(widthPx * PrintTiling.PointsPerPx, l.ImageArea.Width, l.Overlap));
    }

    [Fact]
    public void Scale_and_page_count_are_inverse()
    {
        var content = new Bounds(0, 0, 2400, 1700);
        var s = A4Landscape(x => x.OverlapMm = 5);
        foreach (var (across, down) in new[] { (1, 1), (2, 1), (2, 2), (3, 2), (4, 3) })
        {
            var scale = PrintTiling.ScaleFor(s, content, across, down);
            var fixedSetup = s.Clone();
            fixedSetup.Scale = PrintScaleMode.Fixed;
            fixedSetup.ScalePercent = scale * 100;
            var l = PrintTiling.Compute(fixedSetup.Normalize(), content);
            Assert.True(l.Columns <= across && l.Rows <= down, $"{across}×{down} at {scale:P}: {l.Columns}×{l.Rows}");
            // A hair larger needs more pages in at least one direction.
            fixedSetup.ScalePercent = scale * 100 * 1.02;
            var bigger = PrintTiling.Compute(fixedSetup.Normalize(), content);
            Assert.True(bigger.Columns > across || bigger.Rows > down);
        }
    }

    [Fact]
    public void Huge_fixed_scales_are_capped_and_refused()
    {
        var l = PrintTiling.Compute(A4Landscape(s => { s.Scale = PrintScaleMode.Fixed; s.ScalePercent = 1000; }), new Bounds(0, 0, 20000, 20000));
        Assert.True(l.TooManyPages);
        Assert.True(l.TotalPages > PrintTiling.MaxPages);
        Assert.True(l.Tiles.Count <= PrintTiling.MaxPages + 1);
        var item = new PrintItem("x", () => new PrintSource("", l.Content, ""), l, Context());
        var ex = Assert.Throws<InvalidOperationException>(() => DiagramPrint.Pdf([item], new PageSetup()));
        Assert.Contains("smaller scale", ex.Message);
    }

    [Fact]
    public void Page_setup_round_trips_and_repairs_bad_values()
    {
        var s = new PageSetup
        {
            Paper = PaperSize.A2, Landscape = false, Scale = PrintScaleMode.Fixed, ScalePercent = 75, OverlapMm = 8, RegistrationMarks = true,
            ShowClassification = true, Classification = "CONFIDENTIAL", CustomText = "Draft", Region = PrintRegion.Selection, Legend = true,
        };
        var back = PageSetup.FromJson(s.ToJson());
        Assert.Equal(s.ToJson(), back.ToJson());
        Assert.Contains("\"paper\":\"a2\"", s.ToJson());

        var bad = PageSetup.FromJson("{\"scalePercent\": 99999, \"pagesAcross\": -3, \"marginLeftMm\": 900, \"overlapMm\": -1, \"paper\": \"nope\"}");
        Assert.Equal(PaperSize.A4, bad.Paper);   // an unreadable document gives the defaults
        var clamped = PageSetup.FromJson("{\"scalePercent\": 99999, \"pagesAcross\": -3, \"marginLeftMm\": 900, \"overlapMm\": -1}");
        Assert.Equal(PageSetup.MaxScalePercent, clamped.ScalePercent);
        Assert.Equal(1, clamped.PagesAcross);
        Assert.True(clamped.MarginLeftMm <= 297 * 0.3 + 1e-9);
        Assert.Equal(0, clamped.OverlapMm);
        Assert.Equal(new PageSetup().ToJson(), PageSetup.FromJson("not json").ToJson());
    }

    // ============================================================ header and footer

    private static PrintContext Context(string name = "Vehicle Structure") =>
        new("Vehicle sample", name, "Block Definition Diagram", "Vehicle ▸ Structure", "main · r42", "2026-11-11");

    [Fact]
    public void Header_and_footer_slots_follow_the_fields()
    {
        var s = new PageSetup { ShowPackagePath = true, ShowVersion = true, ShowClassification = true, Classification = "SECRET", CustomText = "For review" };
        var t = PrintHeaderFooter.Compose(s, Context(), 2, 5);
        Assert.Equal("Vehicle sample  ·  Vehicle ▸ Structure", t.HeaderLeft);
        Assert.Equal("SECRET", t.HeaderCenter);
        Assert.Equal("Vehicle Structure (Block Definition Diagram)", t.HeaderRight);
        Assert.Equal("2026-11-11  ·  main · r42", t.FooterLeft);
        Assert.Equal("SECRET  ·  For review", t.FooterCenter);
        Assert.Equal("Page 2 of 5", t.FooterRight);

        var bare = NoBands(new PageSetup());
        var none = PrintHeaderFooter.Compose(bare, Context(), 1, 1);
        Assert.False(none.HasHeader);
        Assert.False(none.HasFooter);
        Assert.False(PrintHeaderFooter.HasHeader(bare));
        Assert.False(PrintHeaderFooter.HasFooter(bare));

        var typeOnly = PrintHeaderFooter.Compose(new PageSetup { ShowDiagramName = false, ShowProject = false }, Context(), 1, 1);
        Assert.Equal("Block Definition Diagram", typeOnly.HeaderRight);
        Assert.Equal("", typeOnly.HeaderLeft);
        // A classification without the field turned on isn't printed.
        Assert.Equal("", PrintHeaderFooter.Compose(new PageSetup { Classification = "SECRET" }, Context(), 1, 1).HeaderCenter);
    }

    [Fact]
    public void Context_names_the_package_path_and_type()
    {
        var m = SampleModels.Vehicle();
        var d = m.Diagrams.Single(x => x.Name == "Vehicle Structure");
        var c = DiagramPrint.ContextFor(m, d, version: "main");
        Assert.Equal(m.Root.Name, c.Project);
        Assert.Equal("Vehicle Structure", c.DiagramName);
        Assert.Equal(Meridian.Core.Language.Metamodel.DisplayName(DiagramType.BlockDefinition), c.DiagramType);
        Assert.EndsWith(d.Owner!.DisplayName, c.PackagePath);
        Assert.Equal("main", c.Version);
    }

    // ============================================================ outputs

    private static (ModelRepository M, Diagram D) Vehicle()
    {
        var m = SampleModels.Vehicle();
        return (m, m.Diagrams.Single(x => x.Name == "Vehicle Structure"));
    }

    private static byte[] PrintPdf(ModelRepository m, Diagram d, PageSetup s, IReadOnlyCollection<string>? selection = null, Bounds? view = null) =>
        DiagramPrint.Pdf([DiagramPrint.Item(m, d, s, DiagramPrint.ContextFor(m, d), selection: selection, view: view)], s);

    private static List<(double W, double H, string Text)> Pages(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return doc.GetPages().Select(p => (p.Width, p.Height, string.Join(" ", p.GetWords().Select(w => w.Text)))).ToList();
    }

    [Fact]
    public void Pdf_has_one_page_per_tile_at_the_paper_size_with_vector_text()
    {
        var (m, d) = Vehicle();
        var pages = Pages(PrintPdf(m, d, A4Landscape(s => { s.Scale = PrintScaleMode.FitToPages; s.PagesAcross = 2; s.PagesDown = 1; })));
        Assert.Equal(2, pages.Count);
        foreach (var (w, h, _) in pages)
        {
            Assert.Equal(297 * Pt, w, tolerance: 1.0);
            Assert.Equal(210 * Pt, h, tolerance: 1.0);
        }
        // The diagram's text is real (selectable) text in the PDF, not pixels: names of shapes on both halves.
        var all = string.Join(" ", pages.Select(p => p.Text));
        Assert.Contains("Engine", all);
        Assert.Contains("Vehicle", all);
        Assert.Contains("Page 1 of 2", pages[0].Text);
        Assert.Contains("Page 2 of 2", pages[1].Text);
        Assert.Contains("Vehicle Structure", pages[0].Text);
    }

    [Theory]
    [InlineData(PaperSize.A3, false, 297, 420)]
    [InlineData(PaperSize.A0, true, 1189, 841)]
    [InlineData(PaperSize.Letter, false, 215.9, 279.4)]
    [InlineData(PaperSize.Tabloid, true, 431.8, 279.4)]
    public void Pdf_page_size_follows_the_paper(PaperSize paper, bool landscape, double wMm, double hMm)
    {
        var (m, d) = Vehicle();
        var pages = Pages(PrintPdf(m, d, new PageSetup { Paper = paper, Landscape = landscape }.Normalize()));
        var page = Assert.Single(pages);
        Assert.Equal(wMm * Pt, page.W, tolerance: 1.0);
        Assert.Equal(hMm * Pt, page.H, tolerance: 1.0);
    }

    [Fact]
    public void Fixed_scale_pdf_has_the_computed_number_of_pages()
    {
        var (m, d) = Vehicle();
        var s = A4Landscape(x => { x.Scale = PrintScaleMode.Fixed; x.ScalePercent = 250; x.OverlapMm = 6; x.RegistrationMarks = true; });
        var item = DiagramPrint.Item(m, d, s, DiagramPrint.ContextFor(m, d));
        Assert.True(item.Layout.PageCount > 2);
        var pages = Pages(DiagramPrint.Pdf([item], s));
        Assert.Equal(item.Layout.PageCount, pages.Count);
        Assert.Contains("Row 1 · Column 1", pages[0].Text);
    }

    [Fact]
    public void Frame_and_legend_are_options()
    {
        var (m, d) = Vehicle();
        var framed = PrintSource.Create(m, d, new PageSetup());
        var bare = PrintSource.Create(m, d, new PageSetup { Frame = false });
        Assert.Contains("[Vehicle Structure]", framed.Inner);
        Assert.DoesNotContain("[Vehicle Structure]", bare.Inner);
        Assert.DoesNotContain("Legend", framed.Inner);
        Assert.Contains("Legend", PrintSource.Create(m, d, new PageSetup { Legend = true }).Inner);
        Assert.NotEqual("true", d.Get(FieldKeys.Legend));   // the diagram itself is unchanged
        // Light by default, the canvas's colours on request.
        Assert.Contains(SvgTheme.Light.Background, framed.Inner);
        Assert.Contains(SvgTheme.Dark.Background, PrintSource.Create(m, d, new PageSetup { ForceLight = false }, SvgTheme.Dark).Inner);
        Assert.DoesNotContain(SvgTheme.Dark.Background, PrintSource.Create(m, d, new PageSetup(), SvgTheme.Dark).Inner);
    }

    [Fact]
    public void Selection_and_current_view_narrow_what_is_printed()
    {
        var (m, d) = Vehicle();
        var engine = d.Nodes.Single(n => n.Element.Name == "Engine");
        var whole = PrintSource.Create(m, d, new PageSetup());
        var selected = PrintSource.Create(m, d, new PageSetup { Region = PrintRegion.Selection }, selection: [engine.Id]);
        Assert.Contains("Engine", selected.Inner);
        Assert.DoesNotContain("FuelTank", selected.Inner.Replace(" ", ""));
        Assert.True(selected.Content.Width < whole.Content.Width);
        // Nothing selected: the whole diagram.
        Assert.Equal(whole.Content, PrintSource.Create(m, d, new PageSetup { Region = PrintRegion.Selection }, selection: []).Content);

        var view = new Bounds(0, 0, 300, 200);
        var shown = PrintSource.Create(m, d, new PageSetup { Region = PrintRegion.CurrentView }, view: view);
        Assert.Equal(Math.Max(view.X, whole.Content.X), shown.Content.X, 6);
        Assert.Equal(300 - Math.Max(view.X, whole.Content.X), shown.Content.Width, 6);
        Assert.Contains("clip-path", shown.Inner);
        // The view is ignored for other regions.
        Assert.Equal(whole.Content, PrintSource.Create(m, d, new PageSetup(), view: view).Content);
    }

    [Fact]
    public void Hidden_elements_print_as_nameless_placeholders()
    {
        var m = new ModelRepository("Plant");
        var open = m.Create(m.Root, ElementKind.Package, "Open");
        var secret = m.Create(m.Root, ElementKind.Package, "Classified");
        var pump = m.Create(open, ElementKind.Block, "Pump");
        var reactor = m.Create(secret, ElementKind.Block, "ReactorCoreZ9");
        var d = m.CreateDiagram(open, DiagramType.BlockDefinition, "Plant Overview");
        m.AddNode(d, pump, new Bounds(20, 20, 160, 80));
        m.AddNode(d, reactor, new Bounds(260, 20, 160, 80));

        var copy = ProjectFile.Copy(m);
        var bob = Guid.NewGuid();
        var view = AccessView.For([new PermissionRule(secret.Id, PrincipalKind.User, bob, PackageAccess.None)], bob, [], PackageAccess.Edit, PackageAccess.Manage);
        ModelRedactor.Redact(copy, view);
        var printed = copy.Diagrams.Single(x => x.Name == "Plant Overview");
        Assert.Equal(2, printed.Nodes.Count);   // the hidden block's shape stays, as a placeholder

        var s = new PageSetup { Scale = PrintScaleMode.FitToPages, PagesAcross = 2 };
        var pdf = DiagramPrint.Pdf([DiagramPrint.Item(copy, printed, s, DiagramPrint.ContextFor(copy, printed))], s);
        var text = string.Join(" ", Pages(pdf).Select(p => p.Text));
        Assert.Contains("Pump", text);
        Assert.DoesNotContain("ReactorCoreZ9", text);
        Assert.DoesNotContain("ReactorCoreZ9", System.Text.Encoding.Latin1.GetString(pdf));
        var html = DiagramPrint.Html([DiagramPrint.Item(copy, printed, s, DiagramPrint.ContextFor(copy, printed))], s, "Plant");
        Assert.DoesNotContain("ReactorCoreZ9", html);
        Assert.DoesNotContain(reactor.Id, html);
    }

    [Fact]
    public void Batch_pdf_starts_with_a_contents_page()
    {
        var m = SampleModels.Vehicle();
        var s = new PageSetup();
        var cache = new PrintSourceCache();
        var diagrams = m.Diagrams.Take(4).ToList();
        var items = diagrams.Select(d => DiagramPrint.Item(m, d, s, DiagramPrint.ContextFor(m, d), cache)).ToList();
        var progress = new List<Meridian.Serialization.Reports.ReportProgress>();
        using var ms = new MemoryStream();
        DiagramPrint.WritePdf(items, s, ms, contents: true, title: "Vehicle diagrams", progress: new SyncProgress(progress.Add));
        var pages = Pages(ms.ToArray());
        Assert.Equal(1 + DiagramPrint.TotalPages(items), pages.Count);
        Assert.Contains("Vehicle diagrams", pages[0].Text);
        foreach (var d in diagrams) Assert.Contains(d.Name.Split(' ')[0], pages[0].Text);
        Assert.Contains($"Page 1 of {pages.Count}", pages[0].Text);
        Assert.Contains(progress, p => p.Phase.StartsWith("Drawing", StringComparison.Ordinal));
        Assert.Equal(1, progress[^1].Fraction);
    }

    [Fact]
    public void Cancelling_stops_the_pdf()
    {
        var (m, d) = Vehicle();
        var s = new PageSetup();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var ms = new MemoryStream();
        Assert.ThrowsAny<OperationCanceledException>(() => DiagramPrint.WritePdf([DiagramPrint.Item(m, d, s, DiagramPrint.ContextFor(m, d))], s, ms, ct: cts.Token));
    }

    [Fact]
    public void Source_cache_keeps_to_its_budget()
    {
        var cache = new PrintSourceCache(budget: 100);
        var made = 0;
        PrintSource Make(int size) { made++; return new PrintSource(new string('x', size), new Bounds(0, 0, 1, 1), ""); }
        cache.Get("a", () => Make(60));
        cache.Get("b", () => Make(60));   // evicts a
        cache.Get("b", () => Make(60));
        Assert.Equal(2, made);
        cache.Get("a", () => Make(60));   // drawn again
        Assert.Equal(3, made);
        Assert.Equal(3, cache.Renders);
    }

    [Fact]
    public void Png_zip_has_one_picture_per_page()
    {
        var (m, d) = Vehicle();
        var s = A4Landscape(x => { x.Scale = PrintScaleMode.FitToPages; x.PagesAcross = 2; });
        using var ms = new MemoryStream();
        DiagramPrint.WritePngZip([DiagramPrint.Item(m, d, s, DiagramPrint.ContextFor(m, d))], s, ms, dpi: 50);
        ms.Position = 0;
        using var zip = new ZipArchive(ms);
        Assert.Equal(["Vehicle Structure - page 1 of 2.png", "Vehicle Structure - page 2 of 2.png"], zip.Entries.Select(e => e.FullName).OrderBy(x => x).ToArray());
        using var first = zip.Entries[0].Open();
        var head = new byte[4];
        first.ReadExactly(head);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, head);
        // Big sheets are rasterised at a lower resolution rather than running out of memory.
        var a0 = PrintTiling.Compute(new PageSetup { Paper = PaperSize.A0 }, new Bounds(0, 0, 100, 100));
        Assert.True(DiagramPrint.EffectiveDpi(a0, 300) * a0.PageWidth / 72 <= DiagramPrint.MaxPixels);
    }

    [Fact]
    public void Html_print_page_has_one_sheet_per_tile_and_page_css()
    {
        var (m, d) = Vehicle();
        var s = A4Landscape(x => { x.Scale = PrintScaleMode.FitToPages; x.PagesAcross = 2; x.PagesDown = 2; x.OverlapMm = 5; });
        var html = DiagramPrint.Html([DiagramPrint.Item(m, d, s, DiagramPrint.ContextFor(m, d))], s, "Vehicle Structure");
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("@page { size: 841.89pt 595.276pt; margin: 0; }", html);
        Assert.Equal(4, html.Split("<section class=\"sheet\"").Length - 1);
        Assert.Equal(4, html.Split("<use href=\"#mprint-0\"/>").Length - 1);
        Assert.Contains("Page 4 of 4", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        // The diagram body is written once.
        Assert.Equal(1, html.Split(PrintSource.Create(m, d, s).Inner).Length - 1);
    }

    [Fact]
    public void Preview_draws_numbered_page_boundaries()
    {
        var (m, d) = Vehicle();
        var s = A4Landscape(x => { x.Scale = PrintScaleMode.FitToPages; x.PagesAcross = 3; x.PagesDown = 2; });
        var src = PrintSource.Create(m, d, s);
        var preview = DiagramPrint.PreviewSvg(src, PrintTiling.Compute(s, src.Content));
        Assert.Equal(6, preview.Split("class=\"print-page\"").Length - 1);
        Assert.Contains(">6</text>", preview);
    }

    [Fact]
    public void Large_diagram_on_A0_prints_without_holding_every_tile()
    {
        var m = new ModelRepository("Big");
        var pkg = m.Create(m.Root, ElementKind.Package, "Blocks");
        var d = m.CreateDiagram(pkg, DiagramType.BlockDefinition, "Everything");
        for (var i = 0; i < 400; i++)
            m.AddNode(d, m.Create(pkg, ElementKind.Block, "Block" + i), new Bounds(i % 20 * 220, i / 20 * 140, 180, 100));
        var s = new PageSetup { Paper = PaperSize.A0, Scale = PrintScaleMode.Fixed, ScalePercent = 100 }.Normalize();
        var item = DiagramPrint.Item(m, d, s, DiagramPrint.ContextFor(m, d));
        Assert.InRange(item.Layout.PageCount, 2, 12);
        var before = GC.GetTotalMemory(true);
        var path = Path.GetTempFileName();
        try
        {
            using (var file = File.Create(path)) DiagramPrint.WritePdf([item], s, file);
            Assert.True(GC.GetTotalMemory(false) - before < 1_500_000_000);
            var pages = Pages(File.ReadAllBytes(path));
            Assert.Equal(item.Layout.PageCount, pages.Count);
            Assert.Contains("Block399", string.Join(" ", pages.Select(p => p.Text)));
        }
        finally { File.Delete(path); }
    }

    private sealed class SyncProgress(Action<Meridian.Serialization.Reports.ReportProgress> report) : IProgress<Meridian.Serialization.Reports.ReportProgress>
    {
        public void Report(Meridian.Serialization.Reports.ReportProgress value) => report(value);
    }
}
