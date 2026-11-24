using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization.Reports;
using UglyToad.PdfPig;

namespace Meridian.Tests;

/// <summary>
/// Streamed report generation (docs/PERFORMANCE.md, "Reports"): the numbering pass + streaming pass must produce exactly
/// what the single-pass, in-memory pipeline produced before; plus cancellation, progress, the size estimate and warning,
/// scope, split per package, hidden-empty matrices, linear numbering and the diagram cache.
/// </summary>
[Collection(TimingCollection.Name)]
public class ReportStreamingTests
{
    private static readonly ResolveOptions Fixed = new() { Date = new DateTime(2026, 1, 2) };

    /// <summary>Deterministic (ids included), with packages, requirements, diagrams and two big diagrams: ~1,200 elements.</summary>
    private static ModelRepository Small() => LargeModelGenerator.Generate(new LargeModelOptions { Scale = 0.02, UseCases = 10, BreakdownDepth = 3, LargeDiagrams = 2 });

    /// <summary>The golden hashes were taken when diagrams drew paths below shapes; diagrams now draw them on top by default
    /// (EdgeRouter.LinesOnTop, covered by LineOrderTests). These tests compare the report pipelines, so they keep the old order.</summary>
    private static ModelRepository LinesBelow(ModelRepository m)
    {
        foreach (var d in m.AllElements.OfType<Diagram>().ToList()) m.Set(d, Meridian.Core.Language.EdgeRouter.LinesBelowKey, "true");
        return m;
    }

    /// <summary>The built-ins as they were at a847aca, when the golden hashes below were taken: matrices without hidden empty
    /// rows and columns, the VCRM table without the "Criteria" column (added later with several criteria per requirement), and
    /// the SDD without its "Deployment view" section (added with deployment diagrams; DeploymentTests covers it).</summary>
    private static DocTemplate AsBefore(DocTemplate t)
    {
        t.Sections.RemoveAll(s => s.Ref == "deployment");
        foreach (var s in t.AllSections())
            foreach (var b in Flatten(s.Blocks))
            {
                if (b is TraceMatrixBlock m) m.HideEmpty = false;
                if (b is TableBlock table) table.Columns.RemoveAll(c => c.Key == "verificationCriteria");
            }
        return t;
    }

    private static IEnumerable<DocBlock> Flatten(IEnumerable<DocBlock> blocks)
    {
        foreach (var b in blocks)
        {
            yield return b;
            if (b is ForEachBlock f) foreach (var x in Flatten(f.Blocks)) yield return x;
            if (b is IfBlock i) { foreach (var x in Flatten(i.Blocks)) yield return x; foreach (var x in Flatten(i.Else)) yield return x; }
        }
    }

    private static DocTemplate Builtin(string name) => AsBefore(ReportTemplates.BuiltIn.Single(t => t.Name == name));

    // ================================================================== same output as before streaming

    /// <summary>
    /// Hashes of the output of the pre-streaming code (commit a847aca: one pass into memory, HTML built in a StringBuilder,
    /// Word through the SDK's package DOM), for the deterministic ~1,200-element model. Word parts are compared as XML with
    /// namespace declarations dropped and relationship ids blanked (the old package got random ids from the SDK).
    /// </summary>
    public static TheoryData<string, string, string> Goldens => new()
    {
        { "System Requirements Specification", "5b5eb26708502f6c", "9c05c2ad4aebf7af" },
        { "Interface Control Document", "28cce40ecda30853", "f968cb14431db556" },
        { "System Design Description", "06e62c0ed25c1a18", "7de661aa162454b3" },
        { "Verification Cross-Reference Matrix / Test Report", "721a989a55bddc54", "8dd799f599d1948f" },
        { "Model Overview", "fcca4d17f97edc41", "fa4d480d4fcdcd39" },
        { "Data Dictionary", "5b3c13ce7faa9dd4", "fec5d57c7c5b168f" },
    };

    [Theory]
    [MemberData(nameof(Goldens))]
    public void Streamed_Html_And_Word_Match_The_Pre_Streaming_Output(string template, string htmlHash, string wordHash)
    {
        var html = Encoding.UTF8.GetString(ReportEngine.Generate(LinesBelow(Small()), Builtin(template), ReportFormat.Html, options: Fixed));
        Assert.Equal(htmlHash, Hash(Ids(html)));
        Assert.Equal(wordHash, Hash(WordXml(ReportEngine.Generate(LinesBelow(Small()), Builtin(template), ReportFormat.Docx, options: Fixed))));
    }

    [Fact]
    public void Streamed_Output_Matches_The_Pre_Streaming_Output_For_The_Vehicle_Sample()
    {
        // The sample's ids are random: they are normalised. Its SDD and overview SVGs order edges by id, so only Word is compared there.
        (string Name, string? Html, string Word)[] goldens =
        [
            ("System Requirements Specification", "4629bdba0cce0746", "c78c0b7a16c7f100"),
            ("Interface Control Document", "1704296de64a5e52", "747126c3e0a3fb62"),
            ("System Design Description", null, "06c6e137c63d230c"),
            ("Verification Cross-Reference Matrix / Test Report", "c7f892f9c04b86af", "85aac24477eaa787"),
            ("Model Overview", null, "019f10cedf1c3eb7"),
            ("Data Dictionary", "5d43462813fc8ad5", "4b51907dfe286a37"),
        ];
        foreach (var (name, html, word) in goldens)
        {
            if (html != null) Assert.Equal(html, Hash(Ids(Encoding.UTF8.GetString(ReportEngine.Generate(LinesBelow(SampleModels.Vehicle()), Builtin(name), ReportFormat.Html, options: Fixed)))));
            Assert.Equal(word, Hash(WordXml(ReportEngine.Generate(LinesBelow(SampleModels.Vehicle()), Builtin(name), ReportFormat.Docx, options: Fixed))));
        }
    }

    /// <summary>
    /// The current VCRM (with the "Criteria" column): its baseline was computed with the in-memory pipeline
    /// (<see cref="ReportEngine.GenerateBuffered"/>) after the column was added, which also showed the column was the only
    /// change (without it the output still hashes to the a847aca values above).
    /// </summary>
    [Fact]
    public void Current_Vcrm_Matches_Its_In_Memory_Baseline()
    {
        var t = Builtin("Verification Cross-Reference Matrix / Test Report");
        Assert.True(t.AllSections().SelectMany(s => Flatten(s.Blocks)).OfType<TableBlock>().Any(), "the VCRM table");
        foreach (var s in t.AllSections())
            foreach (var b in Flatten(s.Blocks))
                if (b is TableBlock table && table.Caption == "VCRM" && table.Columns.All(c => c.Key != "verificationCriteria"))
                    table.Columns.Insert(6, new("verificationCriteria", "Criteria"));
        (string Model, Func<ModelRepository> Make, string Html, string Word)[] baselines =
        [
            ("small", Small, "386a550535542577", "14778a3f94c53648"),
            ("vehicle", SampleModels.Vehicle, "cab010b2539a51ea", "f808bb62b09ddee9"),
        ];
        foreach (var (model, make, html, word) in baselines)
        {
            Assert.True(html == Hash(Ids(Encoding.UTF8.GetString(ReportEngine.Generate(make(), t, ReportFormat.Html, options: Fixed)))), $"{model}: HTML");
            Assert.True(word == Hash(WordXml(ReportEngine.Generate(make(), t, ReportFormat.Docx, options: Fixed))), $"{model}: Word");
        }
    }

    /// <summary>
    /// A report is the same document whatever culture the server runs under: CI and Linux containers run under the invariant
    /// culture, where "P0" writes "100 %", while developers' machines write "100%" (the coverage-by-package table differed).
    /// </summary>
    [Fact]
    public void Reports_Do_Not_Depend_On_The_Servers_Culture()
    {
        var before = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            string Html(CultureInfo c, DocTemplate t)
            {
                (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (c, c);
                return Ids(Encoding.UTF8.GetString(ReportEngine.Generate(Small(), t, ReportFormat.Html, options: Fixed)));
            }
            foreach (var t in new[] { ReportTemplates.Srs(), Builtin("Verification Cross-Reference Matrix / Test Report") })
            {
                var us = Html(new CultureInfo("en-US"), t);
                Assert.True(us == Html(CultureInfo.InvariantCulture, t), $"{t.Name}: en-US and invariant output differ");
                Assert.Contains("(100%)", us);
            }
        }
        finally { (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = before; }
    }

    [Fact]
    public void Streaming_And_Buffered_Pipelines_Produce_The_Same_Documents()
    {
        var m = Small();
        foreach (var t in ReportTemplates.BuiltIn)
        {
            var streamed = Encoding.UTF8.GetString(ReportEngine.Generate(m, t, ReportFormat.Html, options: Fixed));
            var buffered = Encoding.UTF8.GetString(ReportEngine.GenerateBuffered(m, t, ReportFormat.Html, options: Fixed));
            Assert.True(streamed == buffered, $"{t.Name}: HTML differs");
            Assert.True(WordXml(ReportEngine.Generate(m, t, ReportFormat.Docx, options: Fixed)) == WordXml(ReportEngine.GenerateBuffered(m, t, ReportFormat.Docx, options: Fixed)),
                        $"{t.Name}: Word differs");
        }
        foreach (var t in new[] { ReportTemplates.Srs(), ReportTemplates.Sdd() })
        {
            using var a = PdfDocument.Open(ReportEngine.Generate(m, t, ReportFormat.Pdf, options: Fixed));
            using var b = PdfDocument.Open(ReportEngine.GenerateBuffered(m, t, ReportFormat.Pdf, options: Fixed));
            Assert.Equal(b.NumberOfPages, a.NumberOfPages);
            for (var p = 1; p <= a.NumberOfPages; p++) Assert.Equal(b.GetPage(p).Text, a.GetPage(p).Text);
        }
    }

    [Fact]
    public void Word_Package_Streams_To_A_Write_Only_Stream_And_Validates()
    {
        var sink = new WriteOnlyStream();
        ReportEngine.Generate(SampleModels.Vehicle(), ReportTemplates.Sdd(), ReportFormat.Docx, sink);
        using var word = WordprocessingDocument.Open(new MemoryStream(sink.Bytes.ToArray()), false);
        var errors = new OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019).Validate(word).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(10).Select(e => $"{e.Path?.XPath}: {e.Description}")));
        Assert.NotEmpty(word.MainDocumentPart!.ImageParts);
        Assert.NotNull(word.MainDocumentPart.NumberingDefinitionsPart);

        // HTML and PDF need no seeking either.
        foreach (var f in new[] { ReportFormat.Html, ReportFormat.Pdf })
        {
            var s = new WriteOnlyStream();
            ReportEngine.Generate(SampleModels.Vehicle(), ReportTemplates.Srs(), f, s);
            Assert.True(s.Bytes.Length > 1000, f.ToString());
        }
    }

    // ================================================================== cancellation and progress

    [Theory]
    [InlineData(ReportFormat.Html)]
    [InlineData(ReportFormat.Docx)]
    [InlineData(ReportFormat.Pdf)]
    public void Cancelling_Mid_Stream_Stops_Resolution_And_Writing(ReportFormat format)
    {
        var m = ReportGenerationTests.LargeModel(600);
        using var cts = new CancellationTokenSource();
        var reports = 0;
        // Cancel once the writer is under way (a few progress reports into the writing pass).
        var progress = new SyncProgress(p => { if (p.Fraction > 0.25 && ++reports == 3) cts.Cancel(); });
        var sink = new WriteOnlyStream();
        var sw = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => ReportEngine.Generate(m, ReportTemplates.Srs(), format, sink, ReportSplit.None, null, progress, cts.Token));
        Assert.True(cts.IsCancellationRequested);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"cancellation took {sw.Elapsed}");
    }

    [Fact]
    public void Abandoning_A_Node_Stream_Stops_Its_Producer()
    {
        var m = ReportGenerationTests.LargeModel(300);
        var t = ReportTemplates.Srs();
        var prepared = ReportEngine.Prepare(m, t);
        var sw = Stopwatch.StartNew();
        // Take a few nodes and stop: disposing the enumerator must cancel and join the producer thread, not hang.
        var first = DocResolver.Stream(m, t, prepared.Index, prepared.Options).Take(5).ToList();
        Assert.Equal(5, first.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        // An exception in the producer reaches the consumer.
        var bad = new DocTemplate { Sections = [new DocSection { Title = "X", Blocks = [new ForEachBlock { Query = "kind:Requirement", Blocks = [new TextBlock { Markdown = "{name}" }] }] }] };
        var tight = new ResolveOptions { MaxNodes = 50 };
        Assert.Throws<InvalidOperationException>(() => ReportEngine.Prepare(m, bad, tight));
        var loose = ReportEngine.Prepare(m, bad);
        Assert.Throws<InvalidOperationException>(() => DocResolver.Stream(m, bad, loose.Index, tight).Count());
    }

    [Fact]
    public void Progress_Reports_Sections_And_Elements_And_Never_Goes_Back()
    {
        var m = ReportGenerationTests.LargeModel(400);
        foreach (var format in new[] { ReportFormat.Html, ReportFormat.Docx, ReportFormat.Pdf })
        {
            var seen = new List<ReportProgress>();
            ReportEngine.Generate(m, ReportTemplates.Srs(), format, new WriteOnlyStream(), ReportSplit.None, null, new SyncProgress(seen.Add));
            Assert.True(seen.Count > 5, $"{format}: {seen.Count} reports");
            Assert.Equal(1, seen[^1].Fraction);
            Assert.True(seen.Zip(seen.Skip(1)).All(p => p.Second.Fraction >= p.First.Fraction), $"{format}: progress went backwards");
            Assert.Contains(seen, p => p.Sections == ReportTemplates.Srs().Sections.Count && p.Section >= 1);
            Assert.True(seen.Max(p => p.Elements) >= 400, $"{format}: {seen.Max(p => p.Elements)} elements reported");
            Assert.Contains(seen, p => p.Phase.Contains("blocks"));   // "Writing HTML: 1,200 of 4,000 blocks"
        }
    }

    // ================================================================== size estimate and warning

    [Fact]
    public void Estimate_Is_Close_To_The_Real_Page_Count()
    {
        var m = Small();
        foreach (var t in new[] { ReportTemplates.Srs(), ReportTemplates.Sdd(), ReportTemplates.ModelOverview() })
        {
            var prepared = ReportEngine.Prepare(m, t);
            using var pdf = PdfDocument.Open(ReportEngine.Generate(m, t, ReportFormat.Pdf));
            var ratio = (double)prepared.EstimatedPages / pdf.NumberOfPages;
            Assert.True(ratio is > 0.5 and < 2, $"{t.Name}: estimated {prepared.EstimatedPages} pages, real {pdf.NumberOfPages}");
            Assert.Equal(prepared.Stats.Nodes, DocResolver.Resolve(m, t).Nodes.Count);
        }
    }

    [Fact]
    public void Huge_Documents_Are_Flagged_Before_Anything_Is_Written()
    {
        // A requirement × block matrix of every requirement against every block: tens of thousands of mostly empty rows.
        var m = LargeModelGenerator.Generate(new LargeModelOptions { Scale = 0.4 });
        var matrix = new DocTemplate
        {
            Name = "Matrix", Title = "Matrix",
            Sections = [new DocSection { Title = "Allocation", Blocks = [new TraceMatrixBlock { RowQuery = "kind:Requirement", ColumnQuery = "kind:Block", RelationshipKinds = [ElementKind.Satisfy] }] }],
        };
        var sw = Stopwatch.StartNew();
        var full = ReportEngine.Prepare(m, matrix);
        Assert.True(ReportEngine.ShouldWarn(full.Stats), $"{full.EstimatedPages:N0} pages, {full.Stats.TableRows:N0} rows");
        Assert.True(ReportEngine.ShouldRunInBackground(full.Stats));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"sizing took {sw.Elapsed}");

        ((TraceMatrixBlock)matrix.Sections[0].Blocks[0]).HideEmpty = true;
        var trimmed = ReportEngine.Prepare(m, matrix);
        Assert.False(ReportEngine.ShouldWarn(trimmed.Stats), $"{trimmed.EstimatedPages:N0} pages");
        Assert.True(trimmed.Stats.TableRows * 20 < full.Stats.TableRows, $"{trimmed.Stats.TableRows:N0} vs {full.Stats.TableRows:N0} rows");

        // Small documents stay instant.
        var srs = ReportEngine.Prepare(SampleModels.Vehicle(), ReportTemplates.Srs());
        Assert.False(ReportEngine.ShouldWarn(srs.Stats));
        Assert.False(ReportEngine.ShouldRunInBackground(srs.Stats));
    }

    [Fact]
    public void Hidden_Empty_Matrix_Keeps_Every_Link_And_Drops_Only_Empty_Rows_And_Columns()
    {
        var m = new ModelRepository("M");
        var reqs = Enumerable.Range(1, 6).Select(i => m.Create(m.Root, ElementKind.Requirement, $"R{i}")).ToList();
        var blocks = Enumerable.Range(1, 30).Select(i => m.Create(m.Root, ElementKind.Block, $"B{i:00}")).ToList();
        m.Connect(ElementKind.Satisfy, blocks[0], reqs[0], m.Root);
        m.Connect(ElementKind.Satisfy, blocks[25], reqs[2], m.Root);
        m.Connect(ElementKind.Satisfy, blocks[26], reqs[0], m.Root);
        var block = new TraceMatrixBlock { RowQuery = "kind:Requirement", ColumnQuery = "kind:Block", RelationshipKinds = [ElementKind.Satisfy], MaxColumns = 3 };
        var t = new DocTemplate { Title = "T", TitlePage = false, TableOfContents = false, ListOfFigures = false, ListOfTables = false, RevisionHistory = false,
                                  Sections = [new DocSection { Title = "M", Blocks = [block] }] };
        var full = DocResolver.Resolve(m, t).Nodes.OfType<TableNode>().ToList();
        Assert.Equal(10, full.Count);   // 30 columns, 3 per table
        Assert.All(full, x => Assert.Equal(6, x.Rows.Count));

        block.HideEmpty = true;
        var tables = DocResolver.Resolve(m, t).Nodes.OfType<TableNode>().ToList();
        var table = Assert.Single(tables);   // B01, B26, B27 in one table
        Assert.Equal(["", "B01", "B26", "B27"], table.Headers);
        Assert.Equal(2, table.Rows.Count);   // R1 and R3
        Assert.Equal(["R1", "X", "", "X"], table.Rows[0].Select(c => c.Split(' ')[^1]).ToList());
        Assert.Equal(["R3", "", "X", ""], table.Rows[1].Select(c => c.Split(' ')[^1]).ToList());
    }

    // ================================================================== scope and split per package

    private static (ModelRepository M, Element A, Element B) TwoPackages()
    {
        var m = new ModelRepository("Plant");
        var a = m.Create(m.Root, ElementKind.Package, "Alpha");
        var b = m.Create(m.Root, ElementKind.Package, "Bravo");
        var deep = m.Create(m.Create(a, ElementKind.Package, "Alpha inner"), ElementKind.Package, "Alpha deepest");
        m.Create(a, ElementKind.Block, "AlphaPump");
        m.Create(deep, ElementKind.Block, "AlphaValve");
        m.Create(b, ElementKind.Block, "BravoMotor");
        m.Set(m.Create(a, ElementKind.Requirement, "Alpha shall pump"), FieldKeys.RequirementId, "REQ-A1");
        m.Set(m.Create(b, ElementKind.Requirement, "Bravo shall turn"), FieldKeys.RequirementId, "REQ-B1");
        return (m, a, b);
    }

    private static DocTemplate Listing() => new()
    {
        Name = "Listing", Title = "{model.name} listing", Subtitle = "Blocks", RevisionHistory = false, ListOfFigures = false, ListOfTables = false,
        Sections =
        [
            new DocSection { Title = "Blocks", Blocks = [new ForEachBlock { Query = "kind:Block", Heading = "{name}", Blocks = [new FieldsBlock { Keys = ["qualifiedName"] }] }] },
            new DocSection { Title = "Requirements", Blocks = [new RequirementTableBlock(), new StatisticsBlock()] },
        ],
    };

    private static string Html(ModelRepository m, DocTemplate t, ResolveOptions? o = null) => Encoding.UTF8.GetString(ReportEngine.Generate(m, t, ReportFormat.Html, options: o));

    [Fact]
    public void Scope_Limits_A_Report_To_A_Package_Or_Selection_And_Depth()
    {
        var (m, a, _) = TwoPackages();
        var all = Html(m, Listing());
        Assert.Contains("BravoMotor", all);
        Assert.Contains("AlphaValve", all);

        var alpha = Html(m, Listing(), new ResolveOptions { Scope = [a.Id] });
        Assert.Contains("AlphaPump", alpha);
        Assert.Contains("AlphaValve", alpha);
        Assert.Contains("REQ-A1", alpha);
        Assert.DoesNotContain("BravoMotor", alpha);
        Assert.DoesNotContain("REQ-B1", alpha);

        // Depth 1 below the scope: Alpha's own contents and its first sub-package, not "Alpha deepest".
        var shallow = Html(m, Listing(), new ResolveOptions { Scope = [a.Id], ScopeDepth = 1 });
        Assert.Contains("AlphaPump", shallow);
        Assert.DoesNotContain("AlphaValve", shallow);

        // A selection of one element.
        var pump = m.OfKind(ElementKind.Block).Single(e => e.Name == "AlphaPump");
        var one = Html(m, Listing(), new ResolveOptions { Scope = [pump.Id] });
        Assert.Contains("AlphaPump", one);
        Assert.DoesNotContain("AlphaValve", one);
        Assert.DoesNotContain("REQ-A1", one);
    }

    [Fact]
    public void Split_Per_Package_Makes_A_Zip_Of_Documents()
    {
        var (m, _, _) = TwoPackages();
        var ms = new MemoryStream();
        ReportEngine.Generate(m, Listing(), ReportFormat.Html, ms, ReportSplit.ZipPerPackage);
        using var zip = new ZipArchive(new MemoryStream(ms.ToArray()));
        Assert.Equal(["01 Alpha.html", "02 Bravo.html"], zip.Entries.Select(e => e.FullName).ToList());
        string Read(string name) { using var r = new StreamReader(zip.GetEntry(name)!.Open()); return r.ReadToEnd(); }
        var alpha = Read("01 Alpha.html");
        Assert.Contains("Blocks – Alpha", alpha);   // the package in the subtitle
        Assert.Contains("AlphaPump", alpha);
        Assert.DoesNotContain("BravoMotor", alpha);
        var bravo = Read("02 Bravo.html");
        Assert.Contains("BravoMotor", bravo);
        Assert.DoesNotContain("AlphaPump", bravo);

        // Word and PDF parts are complete documents too.
        foreach (var f in new[] { ReportFormat.Docx, ReportFormat.Pdf })
        {
            var z = new MemoryStream();
            ReportEngine.Generate(m, Listing(), f, z, ReportSplit.ZipPerPackage);
            using var archive = new ZipArchive(new MemoryStream(z.ToArray()));
            Assert.Equal(2, archive.Entries.Count);
            Assert.All(archive.Entries, e => Assert.EndsWith("." + ReportEngine.Extension(f), e.FullName));
        }
        Assert.Equal(("zip", "application/zip"), ReportEngine.Output(ReportFormat.Pdf, ReportSplit.ZipPerPackage));
    }

    [Fact]
    public void Split_Per_Package_Into_One_Document_Gives_Each_Package_A_Part_In_The_Contents()
    {
        var (m, _, _) = TwoPackages();
        var ms = new MemoryStream();
        ReportEngine.Generate(m, Listing(), ReportFormat.Html, ms, ReportSplit.PartPerPackage);
        var html = Encoding.UTF8.GetString(ms.ToArray());
        var toc = html[html.IndexOf("Table of Contents", StringComparison.Ordinal)..html.IndexOf("</nav>", StringComparison.Ordinal)];
        Assert.Contains(">1  Alpha<", toc);
        Assert.Contains(">1.1  Blocks<", toc);
        Assert.Contains(">2  Bravo<", toc);
        Assert.Contains(">2.1  Blocks<", toc);
        // Each part only lists its own package.
        var bravoPart = html[html.IndexOf("<span class=\"num\">2</span> Bravo", StringComparison.Ordinal)..];
        Assert.Contains("BravoMotor", bravoPart);
        Assert.DoesNotContain("AlphaPump", bravoPart);
        Assert.Equal(["Alpha", "Bravo"], DocResolver.SplitPackages(m, null).Select(p => p.Name).ToList());
    }

    // ================================================================== numbering and diagrams

    [Fact]
    public void Cross_References_And_Numbering_Scale_Linearly()
    {
        // Every heading's title refers to the next figure: the plain-text references are fixed after resolution, which
        // used to look each heading up in the heading list (quadratic).
        static DocTemplate Many(int n) => new()
        {
            Name = "Many", Title = "Many", RevisionHistory = false,
            Sections = Enumerable.Range(1, n).Select(i => new DocSection
            {
                Title = $"Part {i} (see {{ref:t{i}}})", Ref = $"s{i}",
                Blocks = [new TextBlock { Markdown = $"Table {{ref:t{i}}} and section {{ref:s{i}}}." },
                          new TableBlock { Query = "kind:Block", Caption = $"Blocks {i}", Ref = $"t{i}", Columns = [new("name")] }],
            }).ToList(),
        };
        var m = new ModelRepository("M");
        m.Create(m.Root, ElementKind.Block, "Only");
        TimeSpan Time(int n) { var sw = Stopwatch.StartNew(); ReportEngine.Prepare(m, Many(n)); return sw.Elapsed; }
        Time(500);   // warm up
        var small = Time(2_000);
        var big = Time(16_000);
        Assert.True(big < TimeSpan.FromSeconds(20), $"16,000 sections took {big}");
        Assert.True(big.TotalMilliseconds < Math.Max(1, small.TotalMilliseconds) * 24, $"2,000: {small.TotalMilliseconds:0} ms, 16,000: {big.TotalMilliseconds:0} ms (8× the work)");

        var doc = DocResolver.Resolve(m, Many(3));
        Assert.Equal("Part 2 (see Table 2-1)", doc.Headings[1].Text);
        Assert.Contains(doc.Nodes.OfType<HeadingNode>(), h => h.Text == "Part 3 (see Table 3-1)");
    }

    [Fact]
    public void Each_Diagram_Is_Rendered_Once_Across_Sections_Passes_And_Documents()
    {
        var m = SampleModels.Vehicle();
        var images = new DiagramImageCache();
        var t = ReportTemplates.Sdd();
        var prepared = ReportEngine.Prepare(m, t, new ResolveOptions { Images = images });
        Assert.Equal(0, images.Renders);   // the numbering pass renders nothing
        foreach (var f in new[] { ReportFormat.Html, ReportFormat.Docx, ReportFormat.Pdf })
            ReportEngine.Write(prepared, f, new MemoryStream());
        var distinct = DocResolver.Resolve(m, t).Nodes.OfType<FigureNode>().Select(x => x.Key).Distinct().Count();
        Assert.True(prepared.Stats.FigureCount > distinct, $"{prepared.Stats.FigureCount} figures of {distinct} diagrams: the SDD shows some twice");
        Assert.Equal(distinct, images.Renders);

        // A changed model is a new version: rendered again.
        var d = m.Diagrams.First();
        var before = images.Get(m, d);
        Assert.Same(before, images.Get(m, d));
        m.Rename(d, d.Name + " (renamed)");
        Assert.NotSame(before, images.Get(m, d));
    }

    // ================================================================== helpers

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    private static string Ids(string s) => Regex.Replace(s, "_[0-9a-f]{32}", "_ID");

    private static string WordXml(byte[] docx)
    {
        using var zip = new ZipArchive(new MemoryStream(docx));
        var sb = new StringBuilder();
        foreach (var name in new[] { "word/document.xml", "word/styles.xml", "word/numbering.xml", "word/settings.xml" })
        {
            using var s = zip.GetEntry(name)!.Open();
            var x = XDocument.Load(s);
            foreach (var e in x.Descendants())
            {
                e.Attributes().Where(a => a.IsNamespaceDeclaration).Remove();
                foreach (var a in e.Attributes())
                    if (a.Name.NamespaceName == "http://schemas.openxmlformats.org/officeDocument/2006/relationships") a.Value = "R";
            }
            sb.Append(Ids(x.Root!.ToString(SaveOptions.DisableFormatting))).Append('\n');
        }
        return sb.ToString();
    }

    private sealed class SyncProgress(Action<ReportProgress> a) : IProgress<ReportProgress>
    {
        public void Report(ReportProgress value) => a(value);
    }

    /// <summary>A response body or zip entry: write-only, no seeking, no length.</summary>
    internal sealed class WriteOnlyStream : Stream
    {
        public MemoryStream Bytes { get; } = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Bytes.Write(buffer, offset, count);
    }
}
