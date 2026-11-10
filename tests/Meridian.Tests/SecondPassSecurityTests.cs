using System.IO.Compression;
using System.Text;
using Meridian.Core.Analysis;
using Meridian.Core.Collaboration;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;
using Meridian.Serialization.Fmi;
using Meridian.Serialization.Reports;

namespace Meridian.Tests;

/// <summary>Regression tests for the second security pass (docs/SECURITY-REVIEW.md, "Second pass"). Each test names its finding.</summary>
public class SecondPassSecurityTests
{
    private const string XmiHead = "<?xml version='1.0' encoding='UTF-8'?><xmi:XMI xmlns:uml='http://www.omg.org/spec/UML/20131001' xmlns:xmi='http://www.omg.org/spec/XMI/20131001'><uml:Model xmi:type='uml:Model' xmi:id='m' name='M'>";
    private const string XmiTail = "</uml:Model></xmi:XMI>";

    private static (ModelRepository, MagicDrawImportReport) ImportMdxml(string xml) =>
        MagicDrawImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "evil.mdxml");

    // ------------------------------------------------------------------ SR-25: MagicDraw import recursion

    [Fact]
    public void SR25_Deeply_Nested_MagicDraw_Packages_Are_Rejected_Not_A_Crash()
    {
        const int depth = 60_000;
        var sb = new StringBuilder(XmiHead);
        for (var i = 0; i < depth; i++) sb.Append("<packagedElement xmi:type='uml:Package' xmi:id='p").Append(i).Append("' name='P").Append(i).Append("'>");
        for (var i = 0; i < depth; i++) sb.Append("</packagedElement>");
        sb.Append(XmiTail);

        var ex = Assert.Throws<FormatException>(() => ImportMdxml(sb.ToString()));
        Assert.Contains("nested", ex.Message);
    }

    [Fact]
    public void SR25_Deeply_Nested_MagicDraw_Elements_Are_Rejected_Not_A_Crash()
    {
        const int depth = 60_000;
        var sb = new StringBuilder(XmiHead).Append("<packagedElement xmi:type='uml:Class' xmi:id='c' name='C'>");
        for (var i = 0; i < depth; i++) sb.Append("<wrapper>");
        for (var i = 0; i < depth; i++) sb.Append("</wrapper>");
        sb.Append("</packagedElement>").Append(XmiTail);

        var ex = Assert.Throws<FormatException>(() => ImportMdxml(sb.ToString()));
        Assert.Contains("nested", ex.Message);
    }

    [Fact]
    public void SR25_Deeply_Nested_Diagram_Views_Are_Skipped_Not_A_Crash()
    {
        var dir = Directory.CreateTempSubdirectory("meridian-sr25-");
        try
        {
            var deep = new StringBuilder("<?xml version='1.0'?>");
            for (var i = 0; i < 60_000; i++) deep.Append("<mdOwnedViews>");
            for (var i = 0; i < 60_000; i++) deep.Append("</mdOwnedViews>");
            var binaries = MagicDrawFixtures.DefaultBinaries.ToDictionary(kv => kv.Key, kv => kv.Value);
            binaries["BINARY-bdd"] = deep.ToString();
            var (_, report) = MagicDrawImporter.Import(MagicDrawFixtures.WriteMdzip(dir.FullName, binaries: binaries));
            Assert.Contains(report.Warnings, w => w.Contains("nested", StringComparison.OrdinalIgnoreCase));
        }
        finally { dir.Delete(recursive: true); }
    }

    // ------------------------------------------------------------------ SR-26: MagicDraw zip bombs

    [Fact]
    public void SR26_MagicDraw_Diagram_Binary_Bomb_Is_Bounded()
    {
        var dir = Directory.CreateTempSubdirectory("meridian-sr26-");
        try
        {
            // A gzip of 200 MB of zeros (about 200 KB on disk) as the diagram's symbol data.
            var bomb = new MemoryStream();
            using (var gz = new GZipStream(bomb, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                var zeros = new byte[1024 * 1024];
                for (var i = 0; i < 200; i++) gz.Write(zeros);
            }
            var path = Path.Combine(dir.FullName, "bomb.mdzip");
            using (var fs = File.Create(path))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(zip.CreateEntry("x.com.nomagic.magicdraw.uml_model.model").Open())) w.Write(MagicDrawFixtures.Xmi(inline: false));
                foreach (var name in MagicDrawFixtures.DefaultBinaries.Keys)
                    using (var s = zip.CreateEntry(name).Open()) s.Write(bomb.ToArray());
            }

            // This thread only: the whole-process counter also sees other tests running in parallel.
            var before = GC.GetAllocatedBytesForCurrentThread();
            var (_, report) = MagicDrawImporter.Import(path);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Contains(report.Warnings, w => w.Contains("larger than", StringComparison.OrdinalIgnoreCase));
            Assert.True(allocated < 6L * 200 * 1024 * 1024, $"allocated {allocated / (1024 * 1024)} MB");
        }
        finally { dir.Delete(recursive: true); }
    }

    // ------------------------------------------------------------------ SR-27: report template loops

    [Fact]
    public void SR27_Exploding_Report_Template_Stops_At_The_Budget()
    {
        var m = SampleModels.Vehicle();
        for (var i = 0; i < 30; i++) m.Create(m.Root, ElementKind.Block, "B" + i);
        // Six nested loops over every block: |blocks|^6 iterations without a budget.
        var json = "{\"name\":\"Bomb\",\"sections\":[{\"title\":\"S\",\"blocks\":[" + Nest(6) + "]}]}";
        static string Nest(int n) => n == 0
            ? "{\"type\":\"text\",\"markdown\":\"x\"}"
            : "{\"type\":\"forEach\",\"query\":\"kind:Block\",\"blocks\":[" + Nest(n - 1) + "]}";
        var t = DocTemplate.FromJson(json);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<InvalidOperationException>(() => DocResolver.Resolve(m, t, new ResolveOptions { RenderDiagrams = false, MaxIterations = 20_000 }));
        Assert.Contains("too large", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));

        // The editor preview has a small budget of its own.
        Assert.Throws<InvalidOperationException>(() => ReportEngine.Preview(m, t, loopLimit: 5, diagrams: false));

        // Ordinary templates are unaffected.
        Assert.NotEmpty(DocResolver.Resolve(m, DocTemplate.FromJson("{\"name\":\"ok\",\"sections\":[{\"title\":\"S\",\"blocks\":[" + Nest(1) + "]}]}"),
            new ResolveOptions { RenderDiagrams = false }).Nodes);
    }

    // ------------------------------------------------------------------ SR-28: shared text operations from the client

    [Fact]
    public void SR28_Overflowing_Text_Operation_Components_Are_Rejected()
    {
        static System.Text.Json.JsonElement J(string s) => System.Text.Json.JsonDocument.Parse(s).RootElement;
        Assert.ThrowsAny<Exception>(() => TextOperation.FromJson(J("[2147483647, 2147483647, 7]")));
        Assert.ThrowsAny<Exception>(() => TextOperation.FromJson(J("[-2147483648, -2147483648, -5]")));
        Assert.ThrowsAny<Exception>(() => TextOperation.FromJson(J("[1.5]")));
        // Ordinary operations still parse.
        var ok = TextOperation.FromJson(J("[3, \"ab\", -2, 1]"));
        Assert.Equal(6, ok.BaseLength);
        Assert.Equal("abcabf".Length, ok.Apply("abcdef").Length + 0);
    }

    [Fact]
    public void SR28_Shared_Text_Document_Bounds_Size_And_History()
    {
        var doc = new SharedTextDocument("hello");
        Assert.Throws<InvalidOperationException>(() => doc.Receive(0, new TextOperation().Retain(5).Insert(new string('x', SharedTextDocument.MaxLength))));
        Assert.Equal("hello", doc.Text);

        for (var i = 0; i < SharedTextDocument.MaxHistory + 50; i++)
            doc.Receive(doc.Revision, new TextOperation().Retain(doc.Text.Length).Insert("."));
        Assert.Equal(SharedTextDocument.MaxHistory + 50, doc.Revision);
        Assert.True(doc.HistoryCount <= SharedTextDocument.MaxHistory);
        // An operation based on a revision that was trimmed away is refused (the client resynchronises).
        Assert.Throws<InvalidOperationException>(() => doc.Receive(1, new TextOperation().Retain(5).Insert("!")));
        // Recent revisions still transform.
        var r = doc.Revision - 3;
        doc.Receive(r, new TextOperation().Retain(5 + r).Insert("!"));
        Assert.Contains("!", doc.Text);
        Assert.Equal(5 + SharedTextDocument.MaxHistory + 50 + 1, doc.Text.Length);
        Assert.InRange(doc.TransformIndex(0, 3), 0, doc.Text.Length);
    }

    // ------------------------------------------------------------------ SR-30: FMU model identifiers

    [Theory]
    [InlineData("../../../../usr/lib/libz")]
    [InlineData("/usr/lib/libz")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public void SR30_Fmu_Model_Identifier_Must_Be_A_Plain_Name(string identifier)
    {
        var dir = Directory.CreateTempSubdirectory("meridian-sr30-");
        try { Assert.Throws<CoSimException>(() => FmuArchive.Binary(dir.FullName, identifier)); }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void SR30_Fmu_Extraction_Rejects_Zip_Slip()
    {
        var dir = Directory.CreateTempSubdirectory("meridian-sr30-");
        try
        {
            var path = Path.Combine(dir.FullName, "evil.fmu");
            using (var fs = File.Create(path))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                using (var w = new StreamWriter(zip.CreateEntry("../../escaped.txt").Open())) w.Write("x");
            Assert.Throws<InvalidDataException>(() => FmuArchive.Extract(path));
        }
        finally { dir.Delete(recursive: true); }
    }
}
