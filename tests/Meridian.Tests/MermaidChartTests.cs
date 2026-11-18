using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization.Mermaid;

namespace Meridian.Tests;

/// <summary>Ishikawa (fishbone), radar and treemap charts drawn on whiteboards, and written back as Mermaid.</summary>
public class MermaidChartTests
{
    private static (ModelRepository M, MermaidImportResult R) Import(string text)
    {
        var m = new ModelRepository("Test");
        var r = MermaidImporter.Import(m, m.Root, text);
        return (m, r);
    }

    private static string Role(Element e) => e.Get(MermaidChartFields.RoleField);

    private static List<Element> WithRole(Diagram d, string role) => d.Nodes.Select(n => n.Element).Where(e => Role(e) == role).ToList();

    private static Bounds BoundsOf(Diagram d, Element e) => d.NodeFor(e)!.Bounds;

    // ================================================================== Ishikawa

    private const string Fishbone = """
        ishikawa-beta
            Blurry Photo
            Process
                Out of focus
                Shutter speed too slow
            User
                Shaky hands
            Equipment
                LENS
                    Inappropriate lens
                    Dirty lens
                SENSOR
                    Dirty sensor
            Environment
                Too dark
        """;

    [Fact]
    public void Ishikawa_Becomes_A_Fishbone_Drawing()
    {
        var (m, r) = Import(Fishbone);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.Whiteboard, d.DiagramType);
        Assert.Equal("Blurry Photo", d.Name);   // Mermaid titles the chart with its effect
        var head = Assert.Single(WithRole(d, "effect"));
        Assert.Equal("Blurry Photo", head.Name);
        var cats = WithRole(d, "category").OrderBy(c => c.Get(MermaidChartFields.OrderField)).Select(c => c.Name).ToList();
        Assert.Equal(["Process", "User", "Equipment", "Environment"], cats);
        Assert.Equal(9, WithRole(d, "cause").Count);

        // The head is right of everything; categories alternate above and below the spine.
        var hb = BoundsOf(d, head);
        Assert.All(WithRole(d, "category").Concat(WithRole(d, "cause")), e => Assert.True(BoundsOf(d, e).Right < hb.X));
        var spineY = hb.Center.Y;
        var byName = WithRole(d, "category").ToDictionary(c => c.Name);
        Assert.True(BoundsOf(d, byName["Process"]).Bottom < spineY);
        Assert.True(BoundsOf(d, byName["User"]).Y > spineY);
        Assert.True(BoundsOf(d, byName["Equipment"]).Bottom < spineY);

        // Sub-causes hang off their cause; lines are plain whiteboard lines.
        var lens = WithRole(d, "cause").Single(c => c.Name == "LENS");
        Assert.Equal(byName["Equipment"].Id, lens.Get(MermaidChartFields.ParentField));
        Assert.Contains(WithRole(d, "cause"), c => c.Name == "Dirty lens" && c.Get(MermaidChartFields.ParentField) == lens.Id);
        Assert.All(m.Relationships, x => Assert.Equal(ElementKind.Line, x.Kind));
        Assert.True(d.Edges.Count() > 15);
        Assert.Empty(r.Warnings);

        var p = MermaidImporter.Preview(Fishbone);
        Assert.Equal("Whiteboard drawing (fishbone)", p.Diagrams[0].Becomes);
        Assert.Equal("4 categories · 9 causes", p.Diagrams[0].Counts);
    }

    [Fact]
    public void Ishikawa_Exports_Back_With_Edited_Labels()
    {
        var (m, r) = Import(Fishbone);
        var d = r.Diagrams[0];
        Assert.True(MermaidExporter.CanExport(d));
        var dark = d.Nodes.Select(n => n.Element).Single(e => e.Name == "Too dark");
        m.Rename(dark, "Low light");
        var text = MermaidExporter.Export(m, d);
        Assert.Contains("ishikawa-beta\n    Blurry Photo\n    Process\n        Out of focus\n", text);
        Assert.Contains("    Equipment\n        LENS\n            Inappropriate lens\n            Dirty lens\n        SENSOR\n", text);
        Assert.Contains("        Low light\n", text);

        var (m2, r2) = Import(text);
        var d2 = Assert.Single(r2.Diagrams);
        Assert.Equal(9, WithRole(d2, "cause").Count);
        Assert.Equal(4, WithRole(d2, "category").Count);
    }

    [Fact]
    public void Ishikawa_With_The_Effect_Indented_More_Than_The_Causes()
    {
        var (_, r) = Import("ishikawa\n        Late delivery\n  Supplier\n    Strike\n  Weather\n");
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(["Supplier", "Weather"], WithRole(d, "category").Select(c => c.Name).Order().ToArray());
        Assert.Single(WithRole(d, "cause"));
    }

    // ================================================================== radar

    private const string Grades = """
        ---
        title: "Grades"
        ---
        radar-beta
          axis m["Math"], s["Science"], e["English"]
          axis h["History"], g["Geography"], a["Art"]
          curve alice["Alice"]{85, 90, 80, 70, 75, 90}
          curve bob["Bob"]{
            m: 70, s: 75, e: 85,
            h: 80, g: 90, a: 95 }
          max 100
          min 0
        """;

    [Fact]
    public void Radar_Becomes_Rings_Axes_Curves_And_A_Legend()
    {
        var (m, r) = Import(Grades);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal("Grades", d.Name);
        Assert.Equal(DiagramType.Whiteboard, d.DiagramType);
        Assert.Equal(["Math", "Science", "English", "History", "Geography", "Art"],
            WithRole(d, "axis").OrderBy(a => int.Parse(a.Get(MermaidChartFields.OrderField))).Select(a => a.Name).ToArray());
        Assert.Equal(5, WithRole(d, "ring").Count);
        Assert.Equal(["Alice", "Bob"], WithRole(d, "curve").Select(c => c.Name).ToArray());
        var points = WithRole(d, "point");
        Assert.Equal(12, points.Count);
        Assert.Equal("95", points.Single(p => p.Get(MermaidChartFields.ParentField) == "bob" && p.Get(MermaidChartFields.AxisField) == "a").Get(MermaidChartFields.ValueTag));

        // A value of 100 (the max) sits on the outer ring, straight up for the first axis.
        var centre = BoundsOf(d, Assert.Single(WithRole(d, "centre"))).Center;
        var mathAlice = BoundsOf(d, points.Single(p => p.Get(MermaidChartFields.ParentField) == "alice" && p.Get(MermaidChartFields.AxisField) == "m")).Center;
        Assert.Equal(centre.X, mathAlice.X, 0);
        Assert.Equal(200 * 0.85, centre.Y - mathAlice.Y, 0);
        // Each curve is closed: six sides.
        Assert.Equal(6, d.Edges.Count(e => points.Contains(e.Source.Element) && e.Source.Element.Get(MermaidChartFields.ParentField) == "alice"));
        Assert.Empty(r.Warnings);
        Assert.Equal("6 axes · 2 curves", MermaidImporter.Preview(Grades).Diagrams[0].Counts);
    }

    [Fact]
    public void Radar_Exports_Back_With_Edited_Values()
    {
        var (m, r) = Import(Grades);
        var d = r.Diagrams[0];
        var art = WithRole(d, "point").Single(p => p.Get(MermaidChartFields.ParentField) == "alice" && p.Get(MermaidChartFields.AxisField) == "a");
        m.Set(art, MermaidChartFields.ValueTag, "60");
        m.Rename(WithRole(d, "curve").Single(c => c.Name == "Bob"), "Robert");
        var text = MermaidExporter.Export(m, d);
        Assert.Contains("radar-beta\n", text);
        Assert.Contains("axis m[\"Math\"], s[\"Science\"], e[\"English\"], h[\"History\"], g[\"Geography\"], a[\"Art\"]", text);
        Assert.Contains("curve alice[\"Alice\"]{85, 90, 80, 70, 75, 60}", text);
        Assert.Contains("curve bob[\"Robert\"]{70, 75, 85, 80, 90, 95}", text);
        Assert.Contains("max 100\n", text);

        var (_, r2) = Import(text);
        var d2 = Assert.Single(r2.Diagrams);
        Assert.Equal(12, WithRole(d2, "point").Count);
        Assert.Equal("Robert", WithRole(d2, "curve")[1].Name);
    }

    [Fact]
    public void Radar_Problems_Are_Warnings()
    {
        var (_, r) = Import("""
            radar-beta
              axis x, y, z
              curve c1{1, 2}
              curve c2{x: 3, w: 4}
              curve broken
              graticule polygon
              ticks 3
              colour red
            """);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(3, WithRole(d, "ring").Count);
        Assert.Contains(r.Warnings, w => w.Contains("c1") && w.Contains("no value for 1 axis"));
        Assert.Contains(r.Warnings, w => w.Contains("“w”, which is not an axis"));
        Assert.Contains(r.Warnings, w => w.Contains("“broken” is not a curve"));
        Assert.Contains(r.Warnings, w => w.Contains("polygon grid"));
        Assert.Contains(r.Warnings, w => w.Contains("“colour red” is not a radar statement"));
        Assert.Equal(["x", "y", "z"], WithRole(d, "axis").Select(a => a.Name).ToArray());
    }

    // ================================================================== treemap

    private const string Budget = """
        treemap-beta
        "Products"
            "Electronics"
                "Phones": 50
                "Computers": 30
            "Clothing": 20:::accent
        "Services"
            "Support": 15
            "Training": 5
        classDef accent fill:#ff9900,stroke:#333;
        """;

    [Fact]
    public void Treemap_Becomes_Nested_Rectangles_Sized_By_Value()
    {
        var (m, r) = Import(Budget);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.Whiteboard, d.DiagramType);
        var leaves = WithRole(d, "leaf").ToDictionary(l => MermaidChartFields.LeafText(l).Label);
        Assert.Equal(5, leaves.Count);
        Assert.Equal("Phones\n50", leaves["Phones"].Name);
        Assert.Equal(3, WithRole(d, "section").Count);

        double Area(Element e) => BoundsOf(d, e).Width * BoundsOf(d, e).Height;
        Assert.True(Area(leaves["Phones"]) > Area(leaves["Computers"]));
        Assert.True(Area(leaves["Support"]) > 2.5 * Area(leaves["Training"]));
        // Products (100) takes about five sixths of the chart: its frame is far bigger than Services' (20).
        var frames = WithRole(d, "frame").OrderByDescending(Area).ToList();
        Assert.Equal(3, frames.Count);

        // Nesting: every leaf lies inside its section's frame; no two leaves overlap.
        var electronics = WithRole(d, "section").Single(s => s.Name == "Electronics");
        Assert.Equal(electronics.Id, leaves["Phones"].Get(MermaidChartFields.ParentField));
        var all = leaves.Values.Select(l => BoundsOf(d, l)).ToList();
        for (var i = 0; i < all.Count; i++)
            for (var j = i + 1; j < all.Count; j++)
                Assert.False(all[i].X < all[j].Right - 0.5 && all[j].X < all[i].Right - 0.5 && all[i].Y < all[j].Bottom - 0.5 && all[j].Y < all[i].Bottom - 0.5);
        var products = BoundsOf(d, frames[0]);
        Assert.All(new[] { "Phones", "Computers", "Clothing" }, n => Assert.True(BoundsOf(d, leaves[n]).X >= products.X && BoundsOf(d, leaves[n]).Right <= products.Right + 0.5));

        // classDef colours apply.
        Assert.Equal("#ff9900", d.NodeFor(leaves["Clothing"])!.Style["fill"]);
        Assert.Empty(r.Warnings);
        Assert.Equal("5 values · 3 sections", MermaidImporter.Preview(Budget).Diagrams[0].Counts);
    }

    [Fact]
    public void Treemap_Exports_Back_With_Edited_Values()
    {
        var (m, r) = Import(Budget);
        var d = r.Diagrams[0];
        m.Rename(WithRole(d, "leaf").Single(l => l.Name.StartsWith("Training")), "Courses\n12");
        var text = MermaidExporter.Export(m, d);
        Assert.Contains("treemap-beta\n\"Products\"\n    \"Electronics\"\n        \"Phones\": 50\n        \"Computers\": 30\n    \"Clothing\": 20\n\"Services\"\n", text);
        Assert.Contains("    \"Courses\": 12\n", text);
        var (_, r2) = Import(text);
        Assert.Equal(5, WithRole(Assert.Single(r2.Diagrams), "leaf").Count);
    }

    [Fact]
    public void Treemap_Problems_Are_Warnings()
    {
        var (_, r) = Import("""
            treemap
            title Spend
            "A": 10
            Unquoted: 4
            "B": 1,500
            "C": abc
            "D": 0
            """);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal("Spend", d.Name);
        Assert.Contains(r.Warnings, w => w.Contains("“Unquoted: 4” is not a treemap item"));
        Assert.Contains(r.Warnings, w => w.Contains("no positive value"));
        Assert.Contains(r.Warnings, w => w.Contains("“\"C\": abc” is not a treemap item"));
        Assert.Contains(WithRole(d, "leaf"), l => l.Name == "B\n1,500");
    }

    [Fact]
    public void Squarified_Layout_Fills_The_Area()
    {
        var items = new[] { 6.0, 6, 4, 3, 2, 2, 1 };
        var rects = MermaidChartFields.Squarify(items, new Bounds(0, 0, 600, 400));
        Assert.Equal(600 * 400, rects.Sum(b => b.Width * b.Height), 1);
        for (var i = 0; i < items.Length; i++) Assert.Equal(600 * 400 * items[i] / 24, rects[i].Width * rects[i].Height, 1);
        // Squarified: no sliver thinner than 1:4.
        Assert.All(rects, b => Assert.True(Math.Max(b.Width, b.Height) / Math.Min(b.Width, b.Height) < 4));
    }

    [Fact]
    public void Charts_Never_Crash_On_Bad_Input()
    {
        foreach (var text in new[] { "ishikawa-beta\n", "radar-beta\n  curve x{", "treemap-beta\n\"a\"\n    \"b\":\n", "radar-beta\n axis\n curve c{}" })
        {
            var (_, r) = Import(text);
            Assert.NotNull(r);
        }
    }
}
