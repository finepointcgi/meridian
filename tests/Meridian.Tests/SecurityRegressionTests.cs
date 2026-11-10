using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>Regression tests for the security review (docs/SECURITY-REVIEW.md). Each test names its finding.</summary>
public class SecurityRegressionTests
{
    private const string AttributeBreakout = "red\" onmouseover=\"alert(1)";

    // ------------------------------------------------------------------ SR-01: stored XSS through diagram SVG

    [Fact]
    public void SR01_Svg_Drops_Untrusted_Colours_And_Escapes_Node_Ids()
    {
        var m = SampleModels.Vehicle();
        var bdd = m.Diagrams.First(d => d.DiagramType == DiagramType.BlockDefinition);
        var node = bdd.Nodes.First();
        foreach (var key in new[] { "fill", "stroke", "text" }) m.SetNodeStyle(bdd, node, key, AttributeBreakout);
        if (bdd.Edges.FirstOrDefault() is { } edge) m.SetEdgeStyle(bdd, edge, "stroke", AttributeBreakout);

        // A stereotype colour is a free-text field.
        var profile = m.Create(m.Root, ElementKind.Profile, "Profile");
        var stereo = m.Create(profile, ElementKind.Stereotype, "Evil");
        m.Set(stereo, FieldKeys.Color, AttributeBreakout);
        m.Set(bdd.Nodes.Last().Element, FieldKeys.Stereotypes, "Evil");

        // Node ids come straight from project files.
        var evilNode = new DiagramNode(m.Create(m.Root, ElementKind.Block, "Injected"), new Bounds(900, 900, 120, 60), "n\"><image href=x onerror=alert(1)>");
        m.AddNodeForLoad(bdd, evilNode);

        // Free-form shapes (whiteboard) take fill / stroke / text colours too.
        var wb = m.CreateDiagram(m.Root, DiagramType.Whiteboard, "Board");
        var shape = m.Create(m.Root, ElementKind.Shape, "Sticky");
        m.Set(shape, FieldKeys.Subtype, "rectangle");
        var shapeNode = m.AddNode(wb, shape, new Bounds(40, 40, 120, 60));
        foreach (var key in new[] { "fill", "stroke", "text" }) m.SetNodeStyle(wb, shapeNode, key, AttributeBreakout);

        foreach (var d in new[] { bdd, wb })
        {
            var exported = SvgRenderer.Render(m, d);
            var live = new StringBuilder();
            SvgRenderer.RenderBody(live, m, d, n => n.Bounds);
            foreach (var svg in new[] { exported, live.ToString() })
            {
                Assert.DoesNotContain("onmouseover", svg);
                Assert.DoesNotContain("\"><image href=x", svg);
                Assert.True(XDocument.Parse(exported) is not null);   // still well-formed
            }
        }

        // Ordinary colours still apply.
        m.SetNodeStyle(wb, shapeNode, "fill", "#ffcc00");
        m.SetNodeStyle(wb, shapeNode, "stroke", "rgb(10, 20, 30)");
        var ok = SvgRenderer.Render(m, wb);
        Assert.Contains("fill=\"#ffcc00\"", ok);
        Assert.Contains("rgb(10, 20, 30)", ok);
    }

    [Theory]
    [InlineData("#abc", true)]
    [InlineData("#a1b2c3d4", true)]
    [InlineData("tomato", true)]
    [InlineData("hsl(120, 50%, 40%)", true)]
    [InlineData("red\" onload=\"x", false)]
    [InlineData("url(javascript:alert(1))", false)]
    [InlineData("expression(alert(1))", false)]
    [InlineData("#fff;background:url(x)", false)]
    public void SR01_SafeColor_Accepts_Only_Plain_Colours(string value, bool safe) =>
        Assert.Equal(safe, SvgRenderer.SafeColor(value) != null);

    [Fact]
    public void SR01_Exported_Link_Anchors_Use_The_Normalised_Url_Only()
    {
        var m = new ModelRepository("Links");
        var d = m.CreateDiagram(m.Root, DiagramType.Whiteboard, "Board");
        var e = m.Create(m.Root, ElementKind.Shape, "Link");
        m.Set(e, FieldKeys.Subtype, "rectangle");
        var n = m.AddNode(d, e, new Bounds(40, 40, 120, 60));
        // Written directly (as a project file or a collaborator's operation could), bypassing SetLink's validation.
        m.SetNodeStyle(d, n, DiagramExtras.LinkStyle, "javascript:/**/alert(1)");
        // The raw value passes IsExternalLink ("https://" is prepended for the check), so it used to be written as the href.
        Assert.True(DiagramExtras.IsExternalLink("javascript:/**/alert(1)"));
        var svg = SvgRenderer.Render(m, d);
        Assert.DoesNotContain("href=\"javascript:", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<a href=\"https://", svg);
    }

    // ------------------------------------------------------------------ SR-02: stored XSS through the HTML report

    [Fact]
    public void SR02_Html_Report_Escapes_Ids_And_Forbids_Scripts()
    {
        var m = SampleModels.Vehicle();
        var pkg = m.Root;
        var evil = new Element(ElementKind.Requirement, "r\"><img src=x onerror=alert(1)>");
        m.AttachForLoad(pkg, evil);
        m.Set(evil, FieldKeys.RequirementId, "REQ-X");
        var html = HtmlReport.Generate(m);
        Assert.DoesNotContain("\"><img src=x", html);
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("default-src 'none'", html);
    }

    // ------------------------------------------------------------------ SR-03: stack-overflow DoS in parsers

    [Fact]
    public void SR03_Deeply_Nested_SysML_Is_Reported_Not_A_Crash()
    {
        const int depth = 20_000;
        var text = string.Concat(Enumerable.Repeat("package P {\n", depth)) + string.Concat(Enumerable.Repeat("}\n", depth));
        var doc = SysMLv2Parser.Parse(text);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("nested more than"));

        // Reasonable nesting still parses cleanly.
        var ok = SysMLv2Parser.Parse(string.Concat(Enumerable.Repeat("package P {\n", 50)) + string.Concat(Enumerable.Repeat("}\n", 50)));
        Assert.DoesNotContain(ok.Diagnostics, d => d.Message.Contains("nested more than"));
    }

    [Fact]
    public void SR03_Deeply_Nested_Expressions_And_Queries_Throw_Their_Own_Errors()
    {
        const int depth = 50_000;
        var parens = new string('(', depth) + "1" + new string(')', depth);
        Assert.Throws<Expressions.ExpressionException>(() => Expressions.Evaluate(parens, new Dictionary<string, double>()));
        Assert.Throws<Expressions.ExpressionException>(() => Expressions.Evaluate(new string('-', depth) + "1", new Dictionary<string, double>()));
        Assert.Equal(1, Expressions.Evaluate(new string('(', 50) + "1" + new string(')', 50), new Dictionary<string, double>()));

        Assert.Throws<FeatureExpression.FeatureExpressionException>(() => FeatureExpression.Evaluate(new string('(', depth) + "a" + new string(')', depth), _ => true));
        Assert.Throws<FeatureExpression.FeatureExpressionException>(() => FeatureExpression.Evaluate(new string('!', depth) + "a", _ => true));

        Assert.Throws<QueryParseException>(() => ModelQuery.Parse(string.Concat(Enumerable.Repeat("not ", depth)) + "name = x"));
        Assert.Throws<QueryParseException>(() => ModelQuery.Parse(new string('(', depth) + "name = x" + new string(')', depth)));
    }

    [Fact]
    public void SR03_Deeply_Nested_Xmi_Is_Rejected()
    {
        var model = new XElement("{http://www.omg.org/spec/UML/20161101}Model", new XAttribute("name", "Deep"));
        var cur = model;
        for (var i = 0; i < 20_000; i++)
        {
            var next = new XElement("region");
            cur.Add(next);
            cur = next;
        }
        var doc = new XDocument(new XElement("{http://www.omg.org/spec/XMI/20131001}XMI", model));
        Assert.Throws<FormatException>(() => XmiExchange.Import(doc));
    }

    // ------------------------------------------------------------------ SR-07: zip bombs in imports

    private static MemoryStream Zip(string entryName, long bytes)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var s = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
            var chunk = new byte[1 << 20];
            for (long written = 0; written < bytes; written += chunk.Length) s.Write(chunk, 0, (int)Math.Min(chunk.Length, bytes - written));
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void SR07_Fmu_Entries_Are_Read_With_A_Size_Limit()
    {
        using var bomb = Zip("modelDescription.xml", SafeZip.MaxEntryBytes + (1 << 20));
        Assert.True(bomb.Length < 1 << 20);   // tiny on the wire
        Assert.Throws<InvalidDataException>(() => FmiImport.Read(bomb));
    }

    [Fact]
    public void SR07_LimitedStream_Stops_At_The_Limit_Even_If_The_Header_Lies()
    {
        using var zipStream = Zip("a.cs", 3 << 20);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = zip.Entries.Single();
        Assert.Throws<InvalidDataException>(() => SafeZip.ReadText(entry, 1 << 20));
        using var limited = new SafeZip.LimitedStream(new MemoryStream(new byte[5000]), 4096, "x");
        Assert.Throws<InvalidDataException>(() => limited.CopyTo(Stream.Null));
        Assert.Equal(4, SafeZip.ReadText(Zip("b.cs", 4).Let(s => new ZipArchive(s)).Entries.Single()).Length);
    }

    // ------------------------------------------------------------------ SR-09: CSV formula injection

    [Fact]
    public void SR09_Csv_Exports_Neutralise_Formulas_And_Import_Restores_Them()
    {
        var m = new ModelRepository("Reqs");
        var r = m.Create(m.Root, ElementKind.Requirement, "=HYPERLINK(\"http://x\",\"y\")");
        m.Set(r, FieldKeys.RequirementId, "REQ-1");
        m.Set(r, FieldKeys.Text, "-2+3");
        var csv = RequirementsCsv.Export(m);
        Assert.DoesNotContain(",=HYPERLINK", csv);
        Assert.Contains("'=HYPERLINK", csv);
        Assert.Contains("'-2+3", csv);

        var back = new ModelRepository("Back");
        RequirementsCsv.Import(back, back.Root, csv);
        var imported = back.OfKind(ElementKind.Requirement).Single();
        Assert.Equal("=HYPERLINK(\"http://x\",\"y\")", imported.Name);
        Assert.Equal("-2+3", imported.Get(FieldKeys.Text));

        Assert.Equal("'@SUM(A1)", CsvSafety.Neutralize("@SUM(A1)"));
        Assert.Equal("plain", CsvSafety.Neutralize("plain"));
    }
}

internal static class TestExtensions
{
    public static TOut Let<TIn, TOut>(this TIn value, Func<TIn, TOut> f) => f(value);
}
