using System.IO.Compression;
using System.Text;
using Meridian.Core.Model;
using Meridian.Serialization.DiagramFiles;
using Meridian.Serialization.DrawIo;
using Meridian.Serialization.Visio;

namespace Meridian.Tests;

public class VisioTests
{
    private const string Ns = "http://schemas.microsoft.com/office/visio/2012/main";
    private const string RNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>A minimal .vsdx: masters by name, then pages (name, shapes XML, connects XML, background?).</summary>
    private static byte[] Vsdx(string[] masters, params (string Name, string Shapes, string Connects, bool Background)[] pages)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
            string Rels(IEnumerable<(string Id, string Target)> rels) =>
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                string.Concat(rels.Select(r => $"<Relationship Id=\"{r.Id}\" Type=\"x\" Target=\"{r.Target}\"/>")) + "</Relationships>";
            Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Add("visio/masters/masters.xml", $"<Masters xmlns=\"{Ns}\" xmlns:r=\"{RNs}\">" +
                string.Concat(masters.Select((m, i) => $"<Master ID=\"{i + 1}\" NameU=\"{m}.{i + 10}\" Name=\"{m}\"><Rel r:id=\"rId{i + 1}\"/></Master>")) + "</Masters>");
            Add("visio/masters/_rels/masters.xml.rels", Rels(masters.Select((_, i) => ($"rId{i + 1}", $"master{i + 1}.xml"))));
            for (var i = 0; i < masters.Length; i++)
                Add($"visio/masters/master{i + 1}.xml", $"<MasterContents xmlns=\"{Ns}\"><Shapes><Shape ID=\"5\" Type=\"Shape\"><Cell N=\"Width\" V=\"1\"/><Cell N=\"Height\" V=\"0.5\"/></Shape></Shapes></MasterContents>");
            Add("visio/pages/pages.xml", $"<Pages xmlns=\"{Ns}\" xmlns:r=\"{RNs}\">" +
                string.Concat(pages.Select((p, i) => $"<Page ID=\"{i}\" Name=\"{p.Name}\"{(p.Background ? " Background=\"1\"" : "")}><PageSheet><Cell N=\"PageWidth\" V=\"11\"/><Cell N=\"PageHeight\" V=\"8.5\"/></PageSheet><Rel r:id=\"rId{i + 1}\"/></Page>")) + "</Pages>");
            Add("visio/pages/_rels/pages.xml.rels", Rels(pages.Select((_, i) => ($"rId{i + 1}", $"page{i + 1}.xml"))));
            for (var i = 0; i < pages.Length; i++)
                Add($"visio/pages/page{i + 1}.xml", $"<PageContents xmlns=\"{Ns}\"><Shapes>{pages[i].Shapes}</Shapes><Connects>{pages[i].Connects}</Connects></PageContents>");
        }
        return ms.ToArray();
    }

    private static string Shape(int id, int master, double x, double y, string text, double w = 1.5, double h = 0.75, string extra = "") =>
        $"<Shape ID=\"{id}\" Type=\"Shape\" Master=\"{master}\"><Cell N=\"PinX\" V=\"{x}\"/><Cell N=\"PinY\" V=\"{y}\"/><Cell N=\"Width\" V=\"{w}\"/><Cell N=\"Height\" V=\"{h}\"/>{extra}<Text>{text}</Text></Shape>";

    private static string Line(int id, int master, double x0, double y0, double x1, double y1, string text = "", string extra = "") =>
        $"<Shape ID=\"{id}\" Type=\"Shape\" Master=\"{master}\"><Cell N=\"BeginX\" V=\"{x0}\"/><Cell N=\"BeginY\" V=\"{y0}\"/><Cell N=\"EndX\" V=\"{x1}\"/><Cell N=\"EndY\" V=\"{y1}\"/>{extra}<Text>{text}</Text></Shape>";

    private static string Glue(int line, int from, int to) =>
        $"<Connect FromSheet=\"{line}\" FromCell=\"BeginX\" ToSheet=\"{from}\" ToCell=\"PinX\"/><Connect FromSheet=\"{line}\" FromCell=\"EndX\" ToSheet=\"{to}\" ToCell=\"PinX\"/>";

    private static (ModelRepository M, Meridian.Serialization.Mermaid.MermaidImportResult R) Import(byte[] bytes)
    {
        var m = new ModelRepository("V");
        return (m, DrawIoImporter.Import(m, m.Root, "system.vsdx", DrawIoFile.Read("system.vsdx", bytes)));
    }

    [Fact]
    public void Use_Case_Stencils_Become_Actors_Use_Cases_And_Associations()
    {
        var bytes = Vsdx(["Actor", "Use Case", "Dynamic connector", "Include"],
            ("Use cases",
                Shape(1, 1, 1, 6, "Customer", 0.6, 1.2) + Shape(2, 2, 4, 6, "Withdraw cash") + Shape(3, 2, 4, 4, "Authenticate") +
                Line(4, 3, 1.3, 6, 3.25, 6) + Line(5, 4, 4, 5.6, 4, 4.4),
                Glue(4, 1, 2) + Glue(5, 2, 3), false),
            ("Background", Shape(9, 2, 1, 1, "Logo"), "", true));
        var (m, r) = Import(bytes);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal("Use cases", d.Name);
        Assert.Equal(DiagramType.UseCase, d.DiagramType);
        Assert.Equal("Customer", Assert.Single(m.OfKind(ElementKind.Actor)).Name);
        Assert.Equal(["Authenticate", "Withdraw cash"], m.OfKind(ElementKind.UseCase).Select(u => u.Name).Order());
        Assert.Contains(m.Relationships, x => x.Kind == ElementKind.Association && x.Source.Name == "Customer");
        Assert.Contains(m.Relationships, x => x.Kind == ElementKind.Include && x.Source.Name == "Withdraw cash" && x.Target.Name == "Authenticate");
        Assert.DoesNotContain(m.AllElements, e => e.Name == "Logo");   // background pages are left out
    }

    [Fact]
    public void Class_Shapes_Become_Blocks_With_Members()
    {
        var cls = (int id, double x, string name, string attrs, string ops) =>
            $"<Shape ID=\"{id}\" Type=\"Group\" Master=\"1\"><Cell N=\"PinX\" V=\"{x}\"/><Cell N=\"PinY\" V=\"5\"/><Cell N=\"Width\" V=\"2\"/><Cell N=\"Height\" V=\"2\"/><Shapes>" +
            $"<Shape ID=\"{id * 10 + 1}\" Type=\"Shape\"><Cell N=\"PinX\" V=\"1\"/><Cell N=\"PinY\" V=\"1.8\"/><Text>{name}</Text></Shape>" +
            $"<Shape ID=\"{id * 10 + 2}\" Type=\"Shape\"><Cell N=\"PinX\" V=\"1\"/><Cell N=\"PinY\" V=\"1.2\"/><Text>{attrs}</Text></Shape>" +
            $"<Shape ID=\"{id * 10 + 3}\" Type=\"Shape\"><Cell N=\"PinX\" V=\"1\"/><Cell N=\"PinY\" V=\"0.4\"/><Text>{ops}</Text></Shape></Shapes></Shape>";
        var bytes = Vsdx(["Class", "Generalization"],
            ("Classes", cls(1, 2, "Vehicle", "+speed : Real\n+mass : Real", "+start()") + cls(2, 6, "Car", "+doors : Integer", "") + Line(3, 2, 5, 5, 3, 5),
                $"<Connect FromSheet=\"3\" FromCell=\"BeginX\" ToSheet=\"21\"/><Connect FromSheet=\"3\" FromCell=\"EndX\" ToSheet=\"1\"/>", false));
        var (m, r) = Import(bytes);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.BlockDefinition, d.DiagramType);
        var vehicle = m.AllElements.Single(e => e.Name == "Vehicle" && e.Kind == ElementKind.Block);
        Assert.Equal(["speed", "mass"], vehicle.OwnedElements.Where(e => e.Kind == ElementKind.Property).Select(p => p.Name));
        Assert.Contains(vehicle.OwnedElements, e => e.Kind == ElementKind.Operation && e.Name == "start");
        Assert.Contains(m.Relationships, x => x.Kind == ElementKind.Generalization && x.Source.Name == "Car" && x.Target == vehicle);   // glued to a part of the class
    }

    [Fact]
    public void Other_Pages_Become_Whiteboard_Drawings_With_Colours()
    {
        var fill = "<Cell N=\"FillForegnd\" V=\"#FFCC00\"/><Cell N=\"LineColor\" V=\"RGB(0,0,255)\"/>";
        var diamond = "<Section N=\"Geometry\" IX=\"0\"><Row T=\"MoveTo\" IX=\"1\"><Cell N=\"X\" V=\"0.75\"/><Cell N=\"Y\" V=\"0\"/></Row><Row T=\"LineTo\" IX=\"2\"><Cell N=\"X\" V=\"1.5\"/><Cell N=\"Y\" V=\"0.375\"/></Row>" +
                      "<Row T=\"LineTo\" IX=\"3\"><Cell N=\"X\" V=\"0.75\"/><Cell N=\"Y\" V=\"0.75\"/></Row><Row T=\"LineTo\" IX=\"4\"><Cell N=\"X\" V=\"0\"/><Cell N=\"Y\" V=\"0.375\"/></Row><Row T=\"LineTo\" IX=\"5\"><Cell N=\"X\" V=\"0.75\"/><Cell N=\"Y\" V=\"0\"/></Row></Section>";
        var bytes = Vsdx(["Process", "Dynamic connector"],
            ("Pipeline",
                Shape(1, 1, 2, 6, "Build", extra: fill) +
                $"<Shape ID=\"2\" Type=\"Shape\"><Cell N=\"PinX\" V=\"5\"/><Cell N=\"PinY\" V=\"6\"/><Cell N=\"Width\" V=\"1.5\"/><Cell N=\"Height\" V=\"0.75\"/>{diamond}<Text>Tests pass?</Text></Shape>" +
                "<Shape ID=\"3\" Type=\"Shape\"><Cell N=\"PinX\" V=\"5\"/><Cell N=\"PinY\" V=\"7.5\"/><Cell N=\"Width\" V=\"3\"/><Cell N=\"Height\" V=\"0.4\"/><Text>Release flow</Text></Shape>" +
                Line(4, 2, 2.75, 6, 4.25, 6, "next", "<Cell N=\"EndArrow\" V=\"13\"/>"),
                Glue(4, 1, 2), false));
        var (m, r) = Import(bytes);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal("Pipeline", d.Name);
        var build = d.Nodes.Single(n => n.Element.Name == "Build");
        Assert.Equal(ElementKind.Shape, build.Element.Kind);
        Assert.Equal("#ffcc00", build.Style.GetValueOrDefault("fill"));
        Assert.Equal("#0000ff", build.Style.GetValueOrDefault("stroke"));
        Assert.Equal(144, build.Bounds.Width, 1.0);   // 1.5 in
        var title = d.Nodes.Single(n => n.Element.Name == "Release flow");
        Assert.Equal(((8.5 - 6 - 0.375) - (8.5 - 7.5 - 0.2)) * 96, build.Bounds.Y - title.Bounds.Y, 1.0);   // Visio measures up from the bottom
        Assert.Equal("diamond", d.Nodes.Single(n => n.Element.Name == "Tests pass?").Element.Get(FieldKeys.Subtype));
        Assert.Equal("text", d.Nodes.Single(n => n.Element.Name == "Release flow").Element.Get(FieldKeys.Subtype));
        var line = Assert.Single(d.Edges);
        Assert.Equal("next", line.Relationship.Name);
    }

    [Fact]
    public void Visio_Files_Are_Diagram_Files_And_Bad_Files_Say_Why()
    {
        Assert.True(DrawIoFile.IsDrawIoFile("a/b.vsdx"));
        Assert.True(DrawIoFile.IsBinary("a/b.vsdx"));
        Assert.Equal(DiagramFileFormat.DrawIo, DiagramFolderImport.FormatOf("docs/system.vsdx"));
        Assert.Equal("system", DrawIoImporter.BaseName("system.vsdx"));
        Assert.Contains(".vsdx", Assert.Throws<FormatException>(() => VisioFile.Read("old.vsd", [1, 2, 3])).Message);
        Assert.Contains("zip", Assert.Throws<FormatException>(() => VisioFile.Read("x.vsdx", Encoding.UTF8.GetBytes("hello"))).Message);
        var onlyBackground = Vsdx([], ("Bg", Shape(1, 0, 1, 1, "x"), "", true));
        Assert.Contains("no foreground pages", Assert.Throws<FormatException>(() => VisioFile.Read("x.vsdx", onlyBackground)).Message);
    }
}
