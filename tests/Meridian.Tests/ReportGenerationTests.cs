using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization.Reports;
using UglyToad.PdfPig;

namespace Meridian.Tests;

public class ReportGenerationTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>A small template exercising nesting, numbering, captions, cross-references, loops, conditions and landscape.</summary>
    private static DocTemplate TestTemplate() => new()
    {
        Name = "Test", Title = "Test Document", DocumentNumber = "TD-42", Revision = "B", Classification = "CUI",
        Sections =
        [
            new DocSection
            {
                Title = "Introduction", Ref = "intro",
                Blocks = [new TextBlock { Markdown = "See {ref:fig-bdd} on page {page:fig-bdd}, {ref:tbl-reqs} and {ref:details}.\n\n- **bold** item\n- *italic* item" }],
                Sections =
                [
                    new DocSection { Title = "Purpose", Sections = [new DocSection { Title = "Deep" }] },
                    new DocSection { Title = "Conventions" },
                ],
            },
            new DocSection
            {
                Title = "Structure", Ref = "structure",
                Blocks =
                [
                    new DiagramBlock { Diagram = "Vehicle Structure", Ref = "fig-bdd", Caption = "Vehicle structure" },
                    new RequirementTableBlock { Ref = "tbl-reqs", Caption = "All requirements" },
                    new DiagramBlock { DiagramTypes = [DiagramType.Requirement] },
                ],
            },
            new DocSection
            {
                Title = "Details", Ref = "details",
                Blocks =
                [
                    new ForEachBlock
                    {
                        Query = "kind:Block", Variable = "b", Heading = "Block {name}",
                        Blocks =
                        [
                            new IfBlock { Condition = "has:satisfy", Blocks = [new TextBlock { Markdown = "{name} satisfies requirements." }], Else = [new TextBlock { Markdown = "{name} satisfies nothing." }] },
                            new ForEachBlock { Related = "satisfies", Query = "kind:Requirement", Blocks = [new TextBlock { Markdown = "- {b.name} → {reqId}" }] },
                        ],
                    },
                ],
            },
            new DocSection
            {
                Title = "Matrix", Landscape = true,
                Blocks = [new TraceMatrixBlock { Ref = "tbl-matrix" }, new TableBlock { Query = "kind:Port", Columns = [new("owner"), new("name"), new("type")] }],
            },
            new DocSection { Title = "Appendix", Numbered = false, Blocks = [new TextBlock { Markdown = "Matrix is {ref:tbl-matrix}." }] },
        ],
    };

    private static ModelRepository Vehicle()
    {
        var m = SampleModels.Vehicle();
        Assert.Contains(m.Diagrams, d => d.Name == "Vehicle Structure");
        return m;
    }

    /// <summary>The vehicle model plus <paramref name="n"/> synthetic requirements, blocks and test cases.</summary>
    internal static ModelRepository LargeModel(int n)
    {
        var m = SampleModels.Vehicle();
        var pkg = m.Create(m.Root, ElementKind.Package, "Generated");
        var design = m.Create(m.Root, ElementKind.Package, "Generated design");
        var tests = m.Create(m.Root, ElementKind.Package, "Generated tests");
        var blocks = Enumerable.Range(1, 40).Select(i => m.Create(design, ElementKind.Block, $"Unit{i:00}")).ToList();
        var cases = Enumerable.Range(1, 30).Select(i => m.Create(tests, ElementKind.TestCase, $"TC-{i:000}")).ToList();
        string[] pri = ["low", "medium", "high", "critical"];
        for (var i = 1; i <= n; i++)
        {
            var r = m.Create(pkg, ElementKind.Requirement, $"Generated requirement {i}");
            m.Set(r, FieldKeys.RequirementId, $"GEN-{i:0000}");
            m.Set(r, FieldKeys.Text, $"The system shall perform function {i} within {i % 50 + 1} ms under all operating conditions defined in the environment specification, " +
                                     "including degraded modes and recovery after a power interruption.");
            m.Set(r, ReqFields.Priority, pri[i % 4]);
            m.Set(r, ReqFields.Status, "approved");
            m.Set(r, ReqFields.VerificationMethod, i % 3 == 0 ? "analysis" : "test");
            m.Set(r, ReqFields.Rationale, "Derived from the operational concept.");
            if (i % 5 != 0) m.Connect(ElementKind.Satisfy, blocks[i % blocks.Count], r);
            if (i % 4 != 0) m.Connect(ElementKind.Verify, cases[i % cases.Count], r);
        }
        return m;
    }

    private static ResolvedDocument Resolve(DocTemplate t, ModelRepository? m = null) =>
        DocResolver.Resolve(m ?? Vehicle(), t, new ResolveOptions { Date = new DateTime(2026, 1, 2) });

    private static string PdfText(byte[] pdf, out int pages, out bool anyLandscape)
    {
        using var doc = PdfDocument.Open(pdf);
        pages = doc.NumberOfPages;
        anyLandscape = false;
        var sb = new System.Text.StringBuilder();
        foreach (var p in doc.GetPages())
        {
            if (p.Width > p.Height) anyLandscape = true;
            sb.Append(string.Join(" ", p.GetWords().Select(w => w.Text))).Append('\n');
        }
        return sb.ToString();
    }

    private static List<string> PdfPages(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return doc.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))).ToList();
    }

    // ================================================================== model / numbering

    [Fact]
    public void Headings_Are_Numbered_Hierarchically()
    {
        var doc = Resolve(TestTemplate());
        var h = doc.Headings.ToDictionary(x => x.Text, x => x.Number);
        Assert.Equal("1", h["Introduction"]);
        Assert.Equal("1.1", h["Purpose"]);
        Assert.Equal("1.1.1", h["Deep"]);
        Assert.Equal("1.2", h["Conventions"]);
        Assert.Equal("2", h["Structure"]);
        Assert.Equal("3", h["Details"]);
        Assert.Equal("3.1", doc.Headings.First(x => x.Text.StartsWith("Block ")).Number);
        Assert.Equal("4", h["Matrix"]);
        Assert.Equal("", h["Appendix"]);
        Assert.Equal(doc.Headings.Count, doc.Headings.Select(x => x.Anchor).Distinct().Count());
    }

    [Fact]
    public void Captions_Are_Numbered_Per_Chapter_And_References_Resolve()
    {
        var doc = Resolve(TestTemplate());
        Assert.Equal("Figure 2-1", doc.Targets["fig-bdd"].Label);
        Assert.Equal("Table 2-1", doc.Targets["tbl-reqs"].Label);
        Assert.Equal("Table 4-1", doc.Targets["tbl-matrix"].Label);       // reset in chapter 4
        Assert.Equal("Section 3", doc.Targets["details"].Label);
        Assert.Equal(["Figure 2-1", "Figure 2-2"], doc.Figures.Take(2).Select(f => f.Label));
        Assert.Equal("Vehicle structure", doc.Figures[0].Text);
        Assert.Empty(doc.Warnings);

        // The introduction paragraph carries live cross-references.
        var intro = doc.Nodes.OfType<ParagraphNode>().First(p => p.Inlines.OfType<XRefRun>().Any());
        var text = DocMarkdown.PlainText(intro.Inlines, x => doc.XRefText(x, "7"));
        Assert.Equal("See Figure 2-1 on page 7, Table 2-1 and Section 3.", text);

        var sequential = TestTemplate();
        sequential.CaptionNumbering = "sequential";
        var doc2 = Resolve(sequential);
        Assert.Equal("Figure 1", doc2.Targets["fig-bdd"].Label);
        Assert.Equal("Table 2", doc2.Targets["tbl-matrix"].Label);
    }

    [Fact]
    public void Loops_Conditions_And_Variables_Expand()
    {
        var doc = Resolve(TestTemplate());
        var texts = doc.Nodes.OfType<ParagraphNode>().Select(p => DocMarkdown.PlainText(p.Inlines, x => "")).ToList();
        Assert.Contains("Engine satisfies requirements.", texts);
        Assert.Contains("FuelInterface", string.Join("|", doc.Nodes.OfType<TableNode>().SelectMany(t => t.Rows.SelectMany(r => r))));
        Assert.Contains("Engine → REQ-005", texts);        // nested related loop with outer variable
        var blocks = doc.Headings.Where(x => x.Text.StartsWith("Block ")).Select(x => x.Text).ToList();
        Assert.Contains("Block Vehicle", blocks);
        Assert.Contains("Block BrakeSystem", blocks);
        // landscape section is bracketed by orientation changes
        var o = doc.Nodes.OfType<OrientationNode>().Select(x => x.Landscape).ToList();
        Assert.Equal([true, false], o.Take(2));
    }

    [Fact]
    public void Missing_Reference_Is_Reported()
    {
        var t = new DocTemplate { Sections = [new DocSection { Title = "A", Blocks = [new TextBlock { Markdown = "See {ref:nowhere}." }] }] };
        var doc = Resolve(t);
        var p = doc.Nodes.OfType<ParagraphNode>().Single();
        Assert.Contains("[missing reference: nowhere]", DocMarkdown.PlainText(p.Inlines, x => doc.XRefText(x)));
    }

    [Fact]
    public void Markdown_Subset_Parses()
    {
        var nodes = DocMarkdown.Parse("# Head\nPara **b** and *i* and `c`\ncontinued\n\n- one\n- two\n\n1. first\n2. second\n\n> quote\n```\ncode line\n```");
        Assert.Equal(ParaKind.Subheading, ((ParagraphNode)nodes[0]).Kind);
        var para = (ParagraphNode)nodes[1];
        Assert.Contains(para.Inlines.OfType<TextRun>(), r => r.Bold && r.Text == "b");
        Assert.Contains(para.Inlines.OfType<TextRun>(), r => r.Italic && r.Text == "i");
        Assert.Contains(para.Inlines.OfType<TextRun>(), r => r.Code && r.Text == "c");
        Assert.EndsWith("continued", DocMarkdown.PlainText(para.Inlines, _ => ""));
        Assert.Equal(2, nodes.OfType<ParagraphNode>().Count(p => p.Kind == ParaKind.Bullet));
        Assert.Equal([1, 2], nodes.OfType<ParagraphNode>().Where(p => p.Kind == ParaKind.Numbered).Select(p => p.ListNumber));
        Assert.Contains(nodes.OfType<ParagraphNode>(), p => p.Kind == ParaKind.Quote);
        Assert.Contains(nodes.OfType<ParagraphNode>(), p => p.Kind == ParaKind.Code);
    }

    // ================================================================== outputs

    [Fact]
    public void Pdf_Has_Toc_With_All_Headings_Page_Numbers_And_Landscape_Pages()
    {
        var m = Vehicle();
        var pdf = ReportEngine.Generate(m, TestTemplate(), ReportFormat.Pdf);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        var text = PdfText(pdf, out var pages, out var landscape);
        Assert.True(pages >= 5, $"only {pages} pages");
        Assert.True(landscape, "the Matrix section should be landscape");
        Assert.Contains($"Page 1 of {pages}", text);
        Assert.Contains("TD-42 Rev B", text);
        Assert.Contains("CUI", text);

        var pageTexts = PdfPages(pdf);
        var tocPage = pageTexts.First(p => p.Contains("Table of Contents"));
        var doc = Resolve(TestTemplate(), m);
        foreach (var h in doc.Headings.Where(h => h.Level <= 3))
            Assert.Contains(Squash(h.Display), Squash(tocPage));

        // Cross-reference resolves to the caption number and the page the figure is on.
        var figPage = pageTexts.FindIndex(p => p.Contains("Figure 2-1: Vehicle structure") && !p.Contains("List of Figures")) + 1;
        Assert.True(figPage > 0);
        var seePage = pageTexts.First(p => p.Contains("See "));
        Assert.True(seePage.Contains($"See Figure 2-1 on page {figPage}, Table 2-1 and Section 3."), seePage);
        Assert.Contains("List of Figures", text);
        Assert.Contains("Figure 2-1: Vehicle structure", pageTexts.First(p => p.Contains("List of Figures")));
    }

    private static string Squash(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    [Fact]
    public void Docx_Uses_Word_Headings_Toc_Captions_And_Reference_Fields()
    {
        var bytes = ReportEngine.Generate(Vehicle(), TestTemplate(), ReportFormat.Docx);
        using (var ms = new MemoryStream(bytes))
        using (var word = WordprocessingDocument.Open(ms, false))
        {
            var errors = new OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019).Validate(word).ToList();
            Assert.True(errors.Count == 0, string.Join("\n", errors.Take(10).Select(e => $"{e.Path?.XPath}: {e.Description}")));
        }

        using var zip = new ZipArchive(new MemoryStream(bytes));
        XDocument Part(string name) { using var s = zip.GetEntry(name)!.Open(); return XDocument.Load(s); }
        var body = Part("word/document.xml");
        var styles = Part("word/styles.xml");
        var paras = body.Descendants(W + "p").ToList();
        string StyleOf(XElement p) => p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? "";
        string Text(XElement p) => string.Concat(p.Descendants(W + "t").Select(t => t.Value));

        // Real Word heading styles, numbered through the heading list
        Assert.Contains(styles.Descendants(W + "name"), n => n.Attribute(W + "val")?.Value == "heading 1");
        Assert.NotNull(styles.Descendants(W + "style").First(s => s.Attribute(W + "styleId")?.Value == "Heading2").Descendants(W + "numPr").FirstOrDefault());
        var headings = paras.Where(p => StyleOf(p).StartsWith("Heading")).ToList();
        Assert.Contains(headings, p => StyleOf(p) == "Heading1" && Text(p) == "Introduction");
        Assert.Contains(headings, p => StyleOf(p) == "Heading3" && Text(p) == "Deep");
        // Unnumbered heading opts out of the list
        Assert.Equal("0", headings.Single(p => Text(p) == "Appendix").Descendants(W + "numId").Single().Attribute(W + "val")!.Value);

        var instr = body.Descendants(W + "instrText").Select(i => i.Value.Trim()).ToList();
        Assert.Contains(instr, i => i.StartsWith("TOC \\o \"1-3\""));
        Assert.Contains(instr, i => i == "TOC \\h \\z \\c \"Figure\"");
        Assert.Contains(instr, i => i == "TOC \\h \\z \\c \"Table\"");
        Assert.Contains(instr, i => i.StartsWith("SEQ Figure \\* ARABIC \\s 1"));
        Assert.Contains(instr, i => i.StartsWith("SEQ Table"));
        Assert.Contains(instr, i => i.StartsWith("STYLEREF 1 \\s"));

        // TOC entries: one per heading (levels 1-3), each a hyperlink to the heading bookmark
        var doc = Resolve(TestTemplate());
        var tocEntries = paras.Where(p => StyleOf(p).StartsWith("TOC") && StyleOf(p) != "TOCHeading").ToList();
        var expected = doc.Headings.Where(h => h.Level <= 3).ToList();
        Assert.Equal(expected.Count, tocEntries.Count);
        foreach (var h in expected) Assert.Contains(tocEntries, p => Text(p).StartsWith(h.Display));

        // Captions: label + number inside a bookmark that REF fields point at
        var bookmarks = body.Descendants(W + "bookmarkStart").Select(b => b.Attribute(W + "name")!.Value).ToHashSet();
        var captions = paras.Where(p => StyleOf(p) == "Caption").Select(Text).ToList();
        Assert.Contains("Figure 2-1: Vehicle structure", captions);
        Assert.Contains("Table 2-1: All requirements", captions);
        var refs = instr.Where(i => i.StartsWith("REF ") || i.StartsWith("PAGEREF ")).Select(i => i.Split(' ')[1]).ToList();
        Assert.NotEmpty(refs);
        Assert.All(refs, r => Assert.Contains(r, bookmarks));
        var intro = paras.First(p => Text(p).StartsWith("See "));
        Assert.Contains("See Figure 2-1 on page", Text(intro));
        Assert.Contains(intro.Descendants(W + "instrText"), i => i.Value.Trim() == $"REF {doc.Targets["fig-bdd"].Anchor} \\h");

        // Header / footer with document number, classification and page X of Y; landscape section; SVG + PNG
        var footer = zip.Entries.Where(e => e.FullName.StartsWith("word/footer")).Select(e => { using var s = e.Open(); return XDocument.Load(s); }).Single();
        var fi = footer.Descendants(W + "instrText").Select(i => i.Value.Trim()).ToList();
        Assert.Contains("PAGE", fi);
        Assert.Contains("NUMPAGES", fi);
        var header = zip.Entries.Where(e => e.FullName.StartsWith("word/header")).Select(e => { using var s = e.Open(); return XDocument.Load(s); }).Single();
        Assert.Contains("TD-42 Rev B", string.Concat(header.Descendants(W + "t").Select(t => t.Value)));
        Assert.Contains("CUI", string.Concat(header.Descendants(W + "t").Select(t => t.Value)));
        Assert.Contains(body.Descendants(W + "pgSz"), s => s.Attribute(W + "orient")?.Value == "landscape");
        Assert.Contains(zip.Entries, e => e.FullName.EndsWith(".svg"));
        Assert.Contains(zip.Entries, e => e.FullName.EndsWith(".png"));
        Assert.Contains("svgBlip", body.ToString());
        Assert.Contains("updateFields", Part("word/settings.xml").ToString());
    }

    [Fact]
    public void Html_Is_Single_File_With_Linked_Toc()
    {
        var html = System.Text.Encoding.UTF8.GetString(ReportEngine.Generate(Vehicle(), TestTemplate(), ReportFormat.Html));
        Assert.StartsWith("<!doctype html>", html);
        Assert.DoesNotContain("<script src", html);
        Assert.DoesNotContain("<link ", html);
        Assert.Contains("<svg", html);
        var doc = Resolve(TestTemplate());
        foreach (var h in doc.Headings.Where(h => h.Level <= 3))
            Assert.Contains($"href=\"#{h.Anchor}\"", html);
        Assert.Contains("Figure 2-1</a>", html);
        Assert.Contains("<b>Table 2-1</b>: All requirements", html);
        Assert.Contains("class=\"banner top\">CUI", html);
        Assert.EndsWith("</html>", html);
    }

    [Theory]
    [InlineData(ReportFormat.Pdf)]
    [InlineData(ReportFormat.Docx)]
    [InlineData(ReportFormat.Html)]
    public void Every_BuiltIn_Template_Generates(ReportFormat format)
    {
        var m = Vehicle();
        foreach (var t in ReportTemplates.BuiltIn)
        {
            var doc = DocResolver.Resolve(m, t);
            Assert.True(doc.Warnings.Count == 0, t.Name + ": " + string.Join("; ", doc.Warnings));
            Assert.NotEmpty(doc.Headings);
            var bytes = ReportEngine.Generate(m, t, format);
            Assert.True(bytes.Length > 1000, t.Name);
        }
    }

    [Fact]
    public void BuiltIn_Templates_Have_No_Dangling_References()
    {
        var m = LargeModel(20);
        foreach (var t in ReportTemplates.BuiltIn)
        {
            var doc = DocResolver.Resolve(m, t);
            var refs = doc.Nodes.OfType<ParagraphNode>().SelectMany(p => p.Inlines.OfType<XRefRun>()).ToList();
            Assert.All(refs, r => Assert.True(doc.Targets.ContainsKey(r.Name), $"{t.Name}: {{ref:{r.Name}}}"));
        }
    }

    // ================================================================== storage

    [Fact]
    public void Templates_RoundTrip_Through_Json_And_Model()
    {
        foreach (var t in ReportTemplates.BuiltIn)
        {
            var json = t.ToJson();
            var back = DocTemplate.FromJson(json);
            Assert.Equal(json, back.ToJson());
        }
        var m = Vehicle();
        var srs = ReportTemplates.Srs();
        srs.Name = "Our SRS";
        var e = ReportTemplates.Save(m, srs);
        Assert.Equal(ReportTemplates.PackageName, e.Owner!.Name);
        var stored = Assert.Single(ReportTemplates.InModel(m));
        Assert.Equal("Our SRS", stored.Template.Name);
        Assert.Equal(srs.AllSections().Count(), stored.Template.AllSections().Count());
        Assert.IsType<ForEachBlock>(stored.Template.AllSections().First(s => s.Title == "Requirement details").Blocks[0]);

        // The template replicates with the model (project file round trip) and saving is undoable.
        var copy = Meridian.Serialization.ProjectFile.Deserialize(Meridian.Serialization.ProjectFile.Serialize(m));
        Assert.Equal("Our SRS", Assert.Single(ReportTemplates.InModel(copy)).Template.Name);
        m.History.Undo();
        Assert.Empty(ReportTemplates.InModel(m));
    }

    // ================================================================== scale, progress, cancellation

    [Fact]
    public void Large_Model_Generates_Hundreds_Of_Pages_Quickly()
    {
        var m = LargeModel(1200);
        var t = ReportTemplates.Srs();
        var phases = new List<ReportProgress>();
        var progress = new SyncProgress(phases.Add);
        var sw = Stopwatch.StartNew();
        var pdf = ReportEngine.Generate(m, t, ReportFormat.Pdf, progress);
        var pdfTime = sw.Elapsed;
        using (var doc = PdfDocument.Open(pdf))
            Assert.True(doc.NumberOfPages >= 500, $"{doc.NumberOfPages} pages");
        Assert.True(pdfTime < TimeSpan.FromSeconds(60), $"PDF took {pdfTime}");
        Console.WriteLine($"Large SRS: PDF {pdfTime.TotalSeconds:0.0} s");
        Assert.NotEmpty(phases);
        Assert.Equal(1, phases[^1].Fraction);
        Assert.True(phases.Zip(phases.Skip(1)).All(p => p.Second.Fraction >= p.First.Fraction), "progress must not go backwards");

        sw.Restart();
        var docx = ReportEngine.Generate(m, t, ReportFormat.Docx);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"DOCX took {sw.Elapsed}");
        Console.WriteLine($"Large SRS: DOCX {sw.Elapsed.TotalSeconds:0.0} s");
        using var word = WordprocessingDocument.Open(new MemoryStream(docx), false);
        Assert.True(word.MainDocumentPart!.Document!.Body!.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>()
            .Count(p => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value == "Heading3") >= 1200);
    }

    [Fact]
    public async Task Generation_Can_Be_Cancelled()
    {
        var m = LargeModel(300);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReportEngine.GenerateAsync(m, ReportTemplates.Srs(), ReportFormat.Pdf, null, cts.Token));
    }

    [Fact]
    public void Preview_Caps_Loops()
    {
        var m = LargeModel(200);
        var html = ReportEngine.Preview(m, ReportTemplates.Srs(), loopLimit: 3);
        Assert.Contains("GEN-0001", html);
        Assert.True(Regex.Matches(html, "<h4 ").Count < 10);
    }

    [Fact]
    public void Snapshot_Is_Independent_Of_Live_Model()
    {
        var m = Vehicle();
        var snap = ReportEngine.Snapshot(m);
        m.Rename(m.Root, "Renamed");
        Assert.Equal("Vehicle System", snap.Root.Name);
        Assert.Equal(m.AllElements.Count(), snap.AllElements.Count());
    }

    private sealed class SyncProgress(Action<ReportProgress> a) : IProgress<ReportProgress>
    {
        public void Report(ReportProgress value) => a(value);
    }
}
