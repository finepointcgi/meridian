using System.Text;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization.DiagramFiles;
using Meridian.Serialization.DrawIo;
using Meridian.Serialization.Gliffy;
using Meridian.Serialization.Mermaid;

namespace Meridian.Tests;

/// <summary>Gliffy (.gliffy JSON) import, through the draw.io importer. The fixtures in Gliffy/ are hand-written.</summary>
public class GliffyTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Gliffy", name));

    private static (ModelRepository M, MermaidImportResult R) Import(string name, DrawIoImportOptions? options = null)
    {
        var m = new ModelRepository("Test");
        var r = DrawIoImporter.Import(m, m.Root, name, DrawIoFile.Read(name, Fixture(name)), options);
        return (m, r);
    }

    private static Element Named(ModelRepository m, string name) => m.AllElements.Single(e => e.Name == name && e is not Diagram);

    [Fact]
    public void Gliffy_Files_Are_Recognised_Everywhere_Draw_Io_Files_Are()
    {
        Assert.True(GliffyFile.IsGliffyFile("Architecture.GLIFFY"));
        Assert.True(DrawIoFile.IsDrawIoFile("docs/shop.gliffy"));
        Assert.Equal(DiagramFileFormat.DrawIo, DiagramFolderImport.FormatOf("docs/shop.gliffy"));
        Assert.Contains(".gliffy", DiagramFolderImport.Extensions);
        Assert.True(GliffyFile.LooksLikeGliffy(Encoding.UTF8.GetString(Fixture("flowchart.gliffy"))));
        Assert.False(GliffyFile.LooksLikeGliffy("<mxfile/>"));
        Assert.Equal("shop", DrawIoImporter.BaseName("shop.gliffy"));
    }

    [Fact]
    public void Flowchart_With_Start_And_End_Becomes_An_Activity()
    {
        var (m, r) = Import("flowchart.gliffy");
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.Activity, d.DiagramType);
        Assert.Equal("flowchart", d.Name);
        Assert.Contains("Gliffy", d.Documentation);
        Assert.Contains(m.AllElements, e => e.Kind == ElementKind.InitialNode);
        Assert.Contains(m.AllElements, e => e.Kind == ElementKind.ActivityFinalNode);
        Assert.Contains(m.AllElements, e => e.Kind == ElementKind.DecisionNode);
        Assert.Equal(ElementKind.Action, Named(m, "Receive order").Kind);
        Assert.Equal(ElementKind.Action, Named(m, "Ship parcel").Kind);
        var flows = m.Relationships.Where(x => x.Kind == ElementKind.ControlFlow).ToList();
        Assert.Equal(6, flows.Count);
        Assert.Contains(flows, f => f.Get(FieldKeys.Guard) == "yes" && f.Target.Name == "Ship parcel");
        Assert.Contains(flows, f => f.Get(FieldKeys.Guard) == "no" && f.Target.Name == "Back-order");
        Assert.DoesNotContain(r.Warnings, w => w.Contains("not attached"));

        var preview = DrawIoImporter.Preview("flowchart.gliffy", DrawIoFile.Read("flowchart.gliffy", Fixture("flowchart.gliffy")));
        Assert.Equal("Activity diagram", Assert.Single(preview).Becomes);
        Assert.Equal("6 shapes · 6 lines", preview[0].Counts);
    }

    [Fact]
    public void Flowchart_Stays_A_Drawing_When_Recognise_Uml_Is_Off()
    {
        var (m, r) = Import("flowchart.gliffy", new DrawIoImportOptions { ModelElements = false });
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.Whiteboard, d.DiagramType);
        Assert.Equal("diamond", Named(m, "In stock?").Get(FieldKeys.Subtype));
        Assert.Equal("#FFF2CC", d.NodeFor(Named(m, "In stock?"))!.Style["fill"]);
        Assert.Equal(new Bounds(40, 40, 120, 50), d.NodeFor(Named(m, "Start"))!.Bounds);
        Assert.Equal(6, d.Edges.Count());
        Assert.Contains(m.Relationships, x => x.Kind == ElementKind.Line && x.Name == "yes");
        // The line from Back-order to End keeps its bend.
        var bent = d.Edges.Single(e => e.Source.Element.Name == "Back-order");
        Assert.Equal(new Pt(300, 535), bent.Waypoints.Single());   // Gliffy (460, 515), moved like every shape
    }

    [Fact]
    public void Uml_Class_Diagram_Becomes_A_Bdd_With_Members_And_Relationships()
    {
        var (m, r) = Import("class-diagram.gliffy");
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.BlockDefinition, d.DiagramType);
        var vehicle = Named(m, "Vehicle");
        Assert.Contains(vehicle.OwnedElements, p => p.Name == "maxSpeed");
        Assert.Contains(vehicle.OwnedElements, p => p.Kind == ElementKind.Operation && p.Name == "start");
        Assert.Contains(m.AllElements, e => e.Name == "Drivable" && e.Kind is ElementKind.InterfaceBlock or ElementKind.Interface);
        var car = Named(m, "Car");
        Assert.Contains(m.Relationships, x => x.Kind == ElementKind.Generalization && x.Source == car && x.Target == vehicle);
        Assert.Contains(m.Relationships, x => x.Kind == ElementKind.Realization && x.Source == car && x.Target.Name == "Drivable");
        // The filled diamond at Vehicle's end: Vehicle is made of an Engine.
        Assert.Contains(vehicle.OwnedElements, p => p.Kind == ElementKind.Property && m.ResolveType(p)?.Name == "Engine");
        // Shapes keep the place they had in Gliffy.
        Assert.Equal(40, d.NodeFor(vehicle)!.Bounds.X);
        Assert.Equal(400, d.NodeFor(Named(m, "Engine"))!.Bounds.X);
    }

    [Fact]
    public void Grouped_Shapes_Lines_And_Layers_Become_A_Whiteboard()
    {
        var (m, r) = Import("grouped-shapes.gliffy");
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.Whiteboard, d.DiagramType);
        Assert.Equal("grouped-shapes", d.Name);

        // Shapes inside the group keep their page position (group at 100,80; drawing moved to start at 40,40 from 100,20).
        var web = Named(m, "Web server");
        var cache = Named(m, "Cache");
        Assert.Equal(new Bounds(40, 100, 140, 60), d.NodeFor(web)!.Bounds);
        Assert.Equal(new Bounds(40, 200, 140, 60), d.NodeFor(cache)!.Bounds);
        Assert.Equal("ellipse", cache.Get(FieldKeys.Subtype));
        Assert.Equal("#DAE8FC", d.NodeFor(web)!.Style["fill"]);
        Assert.Equal("#6C8EBF", d.NodeFor(web)!.Style["stroke"]);
        var db = Named(m, "Orders DB");
        Assert.Equal("cylinder", db.Get(FieldKeys.Subtype));
        Assert.Equal("#d79b00", d.NodeFor(db)!.Style["stroke"]);
        Assert.Equal("text", Named(m, "Shop architecture").Get(FieldKeys.Subtype));
        Assert.Equal("rounded", Named(m, "Rotated").Get(FieldKeys.Subtype));
        Assert.Equal("rectangle", Named(m, "Router").Get(FieldKeys.Subtype));
        Assert.DoesNotContain(m.AllElements, e => e.Name == "Secret");

        // The line attached at both ends is drawn with its label, colour and bends; the dangling one is left out.
        var edge = Assert.Single(d.Edges);
        Assert.Equal(web, edge.Source.Element);
        Assert.Equal(db, edge.Target.Element);
        Assert.Equal("SQL", edge.Relationship.Name);
        Assert.Equal("#6C8EBF", edge.Style["stroke"]);
        Assert.Equal(2, edge.Waypoints.Count);
        Assert.Contains(r.Warnings, w => w.Contains("Gliffy page") && w.Contains("1 line is not attached to shapes at both ends"));
        Assert.Contains(r.Warnings, w => w.Contains("1 hidden object was left out"));
        Assert.Contains(r.Warnings, w => w.Contains("1 rotated shape was placed without the rotation"));
        Assert.Contains(r.Warnings, w => w.Contains("1 library icon became"));
    }

    [Fact]
    public void Malformed_Json_Gives_A_Clean_Error()
    {
        var ex = Assert.Throws<FormatException>(() => DrawIoFile.Read("broken.gliffy", Encoding.UTF8.GetBytes("{\"contentType\": \"application/gliffy+json\", \"stage\": {\"objects\": [")));
        Assert.StartsWith("the Gliffy file is not valid JSON", ex.Message);
        Assert.Throws<FormatException>(() => GliffyFile.ReadText("[1, 2, 3]"));
        Assert.Contains("no stage or pages", Assert.Throws<FormatException>(() => GliffyFile.ReadText("{\"version\": \"1.3\"}")).Message);

        // Through the folder import, a broken file is reported and the others still import.
        var m = new ModelRepository("Test");
        var result = DiagramFolderImport.Import(m, m.Root,
        [
            new DiagramSourceFile("docs/broken.gliffy", Encoding.UTF8.GetBytes("{ not json")),
            new DiagramSourceFile("docs/shop.gliffy", Fixture("grouped-shapes.gliffy")),
        ]);
        Assert.Single(result.Diagrams);
        Assert.Contains(result.Warnings, w => w.Contains("broken.gliffy") && w.Contains("not valid JSON"));
    }

    [Fact]
    public void Odd_But_Valid_Input_Never_Crashes()
    {
        foreach (var json in new[]
        {
            "{\"stage\": {}}",
            "{\"stage\": {\"objects\": [{\"id\": 1}]}}",
            "{\"stage\": {\"objects\": [{\"id\": 1, \"graphic\": {\"type\": \"Line\", \"Line\": {\"controlPath\": \"oops\"}}}]}}",
            "{\"stage\": {\"objects\": [{\"id\": \"a\", \"x\": \"12\", \"width\": 1e400, \"graphic\": {\"type\": \"Shape\", \"Shape\": {\"fillColor\": 5}}, \"children\": [null, 3]}]}}",
            "{\"stage\": {\"objects\": [{\"id\": 1, \"uid\": \"com.gliffy.shape.basic.basic_v1.default.group\", \"children\": [{\"id\": 1, \"width\": 10, \"height\": 10, \"graphic\": {\"type\": \"Shape\", \"Shape\": {}}}]}]}}",
        })
        {
            var m = new ModelRepository("Test");
            var r = DrawIoImporter.Import(m, m.Root, "odd.gliffy", GliffyFile.ReadText(json));
            Assert.NotNull(r);
        }
    }

    [Fact]
    public void Pages_Become_One_Diagram_Each_And_Sequence_Lifelines_Are_Recognised()
    {
        const string json = """
            {"contentType":"application/gliffy+json","version":"1.3","pages":[
              {"title":"Login","scene":{"objects":[
                {"x":40,"y":20,"id":1,"width":100,"height":300,"uid":"com.gliffy.shape.uml.uml_v2.sequence.lifeline","order":0,"graphic":null,
                 "children":[{"x":0,"y":0,"id":2,"width":100,"height":40,"graphic":{"type":"Shape","Shape":{"tid":"com.gliffy.stencil.rectangle.basic_v1","fillColor":"#FFFFFF","strokeColor":"#000000"}},
                   "children":[{"x":0,"y":0,"id":3,"width":100,"height":40,"graphic":{"type":"Text","Text":{"html":"<p>User</p>"}}}]}]},
                {"x":240,"y":20,"id":4,"width":100,"height":300,"uid":"com.gliffy.shape.uml.uml_v2.sequence.lifeline","order":1,"graphic":null,
                 "children":[{"x":0,"y":0,"id":5,"width":100,"height":40,"graphic":{"type":"Shape","Shape":{"tid":"com.gliffy.stencil.rectangle.basic_v1","fillColor":"#FFFFFF","strokeColor":"#000000"}},
                   "children":[{"x":0,"y":0,"id":6,"width":100,"height":40,"graphic":{"type":"Text","Text":{"html":"<p>Server</p>"}}}]}]},
                {"x":90,"y":100,"id":7,"width":100,"height":100,"uid":"com.gliffy.shape.uml.uml_v2.sequence.message","order":2,
                 "constraints":{"startConstraint":{"type":"StartPositionConstraint","StartPositionConstraint":{"nodeId":1,"px":0.5,"py":0.3}},
                                "endConstraint":{"type":"EndPositionConstraint","EndPositionConstraint":{"nodeId":4,"px":0.5,"py":0.3}}},
                 "graphic":{"type":"Line","Line":{"strokeColor":"#000000","dashStyle":null,"startArrow":0,"endArrow":2,"controlPath":[[0,0],[200,0]]}},
                 "children":[{"x":80,"y":-20,"id":8,"width":60,"height":16,"graphic":{"type":"Text","Text":{"html":"<p>login()</p>"}}}]},
                {"x":290,"y":160,"id":9,"width":100,"height":100,"uid":"com.gliffy.shape.uml.uml_v2.sequence.return_message","order":3,
                 "constraints":{"startConstraint":{"type":"StartPositionConstraint","StartPositionConstraint":{"nodeId":5,"px":0.5,"py":0.5}},
                                "endConstraint":{"type":"EndPositionConstraint","EndPositionConstraint":{"nodeId":2,"px":0.5,"py":0.5}}},
                 "graphic":{"type":"Line","Line":{"strokeColor":"#000000","dashStyle":"4.0,4.0","startArrow":0,"endArrow":1,"controlPath":[[0,0],[-200,0]]}},
                 "children":[{"x":80,"y":-20,"id":10,"width":60,"height":16,"graphic":{"type":"Text","Text":{"html":"<p>token</p>"}}}]}
              ]}},
              {"title":"Notes","scene":{"objects":[
                {"x":0,"y":0,"id":1,"width":120,"height":60,"uid":"com.gliffy.shape.basic.basic_v1.default.rectangle","order":0,
                 "graphic":{"type":"Shape","Shape":{"tid":"com.gliffy.stencil.rectangle.basic_v1","fillColor":"#FFFFFF","strokeColor":"#000000"}},
                 "children":[{"x":0,"y":0,"id":2,"width":120,"height":60,"graphic":{"type":"Text","Text":{"html":"<p>Remember</p>"}}}]}]}}
            ]}
            """;
        var pages = GliffyFile.ReadText(json);
        Assert.Equal(["Login", "Notes"], pages.Select(p => p.Name).ToArray());
        var m = new ModelRepository("Test");
        var r = DrawIoImporter.Import(m, m.Root, "auth.gliffy", pages);
        Assert.Equal(2, r.Diagrams.Count);
        var seq = r.Diagrams.Single(d => d.Name == "Login");
        Assert.Equal(DiagramType.Sequence, seq.DiagramType);
        var messages = m.Relationships.Where(x => x.Kind == ElementKind.Message).ToList();
        Assert.Equal(["login()", "token"], messages.Select(x => x.Name).ToArray());
        Assert.Equal("reply", messages[1].Get(FieldKeys.MessageSort));
        Assert.Equal(DiagramType.Whiteboard, r.Diagrams.Single(d => d.Name == "Notes").DiagramType);
    }

    [Fact]
    public void Open_As_New_Project_Names_It_After_The_File()
    {
        var (model, report) = DrawIoImporter.ImportAsNewProject("Shop.gliffy", Fixture("grouped-shapes.gliffy"));
        Assert.Equal("Shop", model.Root.Name);
        Assert.Single(model.Diagrams);
        Assert.NotEmpty(report.Warnings);
    }
}
