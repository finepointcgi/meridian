using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization;
using Meridian.Serialization.DrawIo;

namespace Meridian.Tests;

/// <summary>Cloud and network icon libraries: whiteboard shapes, group frames and the icon of a deployment node.</summary>
public class IconLibraryTests
{
    private static (ModelRepository M, Diagram D, Element Fn, Element Vpc) Board()
    {
        var m = new ModelRepository("Cloud");
        var d = m.CreateDiagram(m.Root, DiagramType.Whiteboard, "Architecture");
        var vpc = m.Create(m.Root, ElementKind.Shape, "Prod VPC");
        m.Set(vpc, FieldKeys.Subtype, "icon.vnet");
        var (vw, vh) = Notation.DefaultSizeOf(vpc);
        var frame = m.AddNode(d, vpc, new Bounds(0, 0, vw, vh));
        var fn = m.Create(m.Root, ElementKind.Shape, "Resize images");
        m.Set(fn, FieldKeys.Subtype, "icon.function");
        m.Set(fn, IconLibrary.ProviderField, "AWS Lambda");
        var (w, h) = Notation.DefaultSizeOf(fn);
        m.AddNode(d, fn, new Bounds(40, 60, w, h), parent: frame);
        var db = m.Create(m.Root, ElementKind.Shape, "Orders");
        m.Set(db, FieldKeys.Subtype, "icon.database");
        m.AddNode(d, db, new Bounds(400, 60, w, h));
        var l = m.Connect(ElementKind.Line, fn, db);
        m.AddEdge(d, l);
        return (m, d, fn, vpc);
    }

    [Fact]
    public void Library_Has_Two_Sections_Of_Distinct_Original_Line_Icons()
    {
        Assert.InRange(IconLibrary.All.Count, 28, 40);
        Assert.Equal(IconLibrary.All.Count, IconLibrary.All.Select(i => i.Key).Distinct().Count());
        Assert.Equal(IconLibrary.All.Count, IconLibrary.All.Select(i => i.Path).Distinct().Count());
        Assert.All(IconLibrary.All, i => Assert.Contains(i.Section, new[] { IconLibrary.CloudSection, IconLibrary.NetworkSection }));
        Assert.All(IconLibrary.All, i => Assert.Matches("^[MmLlHhVvCcSsQqTtAaZz0-9 .,\\-]+$", i.Path));
        // Generic names, no trademarks.
        Assert.DoesNotContain(IconLibrary.All, i => i.Name.Contains("AWS") || i.Name.Contains("Azure") || i.Name.Contains("Kubernetes") || i.Name.Contains("Lambda"));
        Assert.Equal(["region", "zone", "vnet", "subnet", "cluster"], IconLibrary.All.Where(i => i.Container).Select(i => i.Key));
    }

    [Fact]
    public void Whiteboard_Palette_Offers_Cloud_And_Network_Sections()
    {
        var sections = IconLibrary.PaletteSections(DiagramType.Whiteboard);
        Assert.Equal([IconLibrary.CloudSection, IconLibrary.NetworkSection], sections.Select(s => s.Title));
        Assert.Equal(IconLibrary.All.Count, sections.Sum(s => s.Items.Count));
        var db = sections[0].Items.Single(i => i.Subtype == "icon.database");
        Assert.Equal(ElementKind.Shape, db.Kind);
        Assert.Equal("Database", db.Title);
        Assert.Empty(IconLibrary.PaletteSections(DiagramType.BlockDefinition));
        // The shape type picker (inspector, format bar) lists them after the plain shapes.
        Assert.Contains("icon.router", Subtypes.For(ElementKind.Shape));
        Assert.Equal("Router", Subtypes.Display("icon.router"));
        Assert.Contains(IconLibrary.ProviderField, Metamodel.EditableFields(ElementKind.Shape));
        Assert.Contains(IconLibrary.IconField, Metamodel.EditableFields(ElementKind.Device));
    }

    [Theory]
    [InlineData("lambda", "icon.function")]
    [InlineData("s3", "icon.objectstore")]
    [InlineData("bucket", "icon.objectstore")]
    [InlineData("vm", "icon.server")]
    [InlineData("ec2", "icon.server")]
    [InlineData("kubernetes", "icon.cluster")]
    [InlineData("vpc", "icon.vnet")]
    [InlineData("kafka", "icon.stream")]
    [InlineData("AWS Lambda", "icon.function")]
    [InlineData("database", "icon.database")]
    public void Search_Finds_Icons_By_Name_And_Synonym(string query, string subtype)
    {
        var all = IconLibrary.PaletteSections(DiagramType.Whiteboard).SelectMany(s => s.Items).ToList();
        var hits = all.Where(i => IconLibrary.Matches(i, query)).Select(i => i.Subtype).ToList();
        Assert.Contains(subtype, hits);
        Assert.True(hits.Count < all.Count / 2, $"'{query}' matched too much: {string.Join(", ", hits)}");
    }

    [Fact]
    public void Search_Still_Matches_Plain_Tiles_By_Title_Only()
    {
        Assert.True(IconLibrary.Matches(new PaletteItem(ElementKind.Shape, "cylinder"), "cyl"));
        Assert.False(IconLibrary.Matches(new PaletteItem(ElementKind.Shape, "cylinder"), "lambda"));
        Assert.True(IconLibrary.Matches(new PaletteItem(ElementKind.Shape, "cylinder"), ""));
    }

    [Fact]
    public void Icons_Render_With_Name_And_Provider_And_Groups_As_Frames()
    {
        var (m, d, fn, vpc) = Board();
        Assert.Equal(NodeShape.Swimlane, Notation.ShapeOf(vpc));        // group frame: shapes created inside nest
        Assert.Equal(NodeShape.Rectangle, Notation.ShapeOf(fn));
        Assert.Equal(IconLibrary.DefaultSize(IconLibrary.Find("vnet")!), Notation.DefaultSizeOf(vpc));
        var svg = SvgRenderer.Render(m, d);
        Assert.Contains("data-icon=\"function\"", svg);
        Assert.Contains("data-icon=\"database\"", svg);
        Assert.Contains("data-icon=\"vnet\"", svg);
        Assert.Contains("AWS Lambda", svg);
        Assert.Contains("Prod VPC", svg);
        Assert.Contains("Resize images", svg);
        // The frame is drawn before what it contains.
        Assert.True(svg.IndexOf("data-icon=\"vnet\"", StringComparison.Ordinal) < svg.IndexOf("data-icon=\"function\"", StringComparison.Ordinal));
        // Line colour applies to the icon; dark theme draws it light.
        d.Nodes.Single(n => ReferenceEquals(n.Element, fn)).Style["stroke"] = "#ff8800";
        Assert.Contains("stroke=\"#ff8800\"", SvgRenderer.Render(m, d));
        Assert.Contains(SvgTheme.Dark.Ink, SvgRenderer.Render(m, d, theme: SvgTheme.Dark));
    }

    [Fact]
    public void Icons_Round_Trip_Through_A_Project_File()
    {
        var (m, _, _, _) = Board();
        var back = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        var d2 = back.Diagrams.Single();
        var fn = back.OfKind(ElementKind.Shape).Single(e => e.Name == "Resize images");
        Assert.Equal("icon.function", fn.Get(FieldKeys.Subtype));
        Assert.Equal("AWS Lambda", fn.Get(IconLibrary.ProviderField));
        var vpcNode = d2.Nodes.Single(n => n.Element.Name == "Prod VPC");
        Assert.Equal(vpcNode.Id, d2.Nodes.Single(n => ReferenceEquals(n.Element, fn)).ParentId);
        Assert.Contains("data-icon=\"function\"", SvgRenderer.Render(back, d2));
    }

    [Fact]
    public void DrawIo_Export_Writes_Icons_As_Pictures_And_Groups_As_Containers()
    {
        var (m, d, _, _) = Board();
        var xml = DrawIoExporter.Export(m, [d]);
        Assert.Contains("image=data:image/svg+xml,", xml);
        Assert.Contains("container=1", xml);
        Assert.Contains("AWS Lambda", xml);
        // Reading it back does not fail and keeps every shape.
        var m2 = new ModelRepository("Back");
        var r = DrawIoImporter.Import(m2, m2.Root, "cloud.drawio", DrawIoFile.ReadText(xml));
        Assert.Equal(d.Nodes.Count(), Assert.Single(r.Diagrams).Nodes.Count());
    }

    [Fact]
    public void Deployment_Nodes_Can_Show_An_Icon()
    {
        var m = new ModelRepository("Deploy");
        var d = m.CreateDiagram(m.Root, DiagramType.Deployment, "Runtime");
        var node = m.Create(m.Root, ElementKind.Node, "App server");
        m.AddNode(d, node, new Bounds(0, 0, 200, 120));
        Assert.DoesNotContain("data-icon=", SvgRenderer.Render(m, d));
        m.Set(node, IconLibrary.IconField, "server");
        Assert.Same(IconLibrary.Find("server"), IconLibrary.Of(node));
        Assert.Contains("data-icon=\"server\"", SvgRenderer.Render(m, d));
        var back = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        Assert.Equal("server", back.OfKind(ElementKind.Node).Single().Get(IconLibrary.IconField));
        // An unknown key is ignored rather than drawn.
        m.Set(node, IconLibrary.IconField, "nope");
        Assert.DoesNotContain("data-icon=", SvgRenderer.Render(m, d));
    }
}
