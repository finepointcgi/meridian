using System.IO.Compression;
using System.Text;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization.DiagramFiles;
using Meridian.Serialization.DrawIo;
using Meridian.Serialization.Mermaid;

namespace Meridian.Tests;

public class DrawIoTests
{
    private static string File(params string[] pages) => "<mxfile host=\"test\">" + string.Concat(pages) + "</mxfile>";

    private static string Page(string name, string cells) =>
        $"<diagram id=\"{name}\" name=\"{name}\"><mxGraphModel><root><mxCell id=\"0\"/><mxCell id=\"1\" parent=\"0\"/>{cells}</root></mxGraphModel></diagram>";

    private static string V(string id, string value, string style, double x, double y, double w, double h, string parent = "1") =>
        $"<mxCell id=\"{id}\" value=\"{System.Security.SecurityElement.Escape(value)}\" style=\"{style}\" vertex=\"1\" parent=\"{parent}\"><mxGeometry x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" as=\"geometry\"/></mxCell>";

    private static string E(string id, string source, string target, string style = "endArrow=classic;html=1;", string value = "", string extra = "") =>
        $"<mxCell id=\"{id}\" value=\"{System.Security.SecurityElement.Escape(value)}\" style=\"{style}\" edge=\"1\" parent=\"1\" source=\"{source}\" target=\"{target}\"><mxGeometry relative=\"1\" as=\"geometry\">{extra}</mxGeometry></mxCell>";

    private static (ModelRepository M, MermaidImportResult R) Import(string xml, DrawIoImportOptions? options = null, string fileName = "test.drawio")
    {
        var m = new ModelRepository("Test");
        var r = DrawIoImporter.Import(m, m.Root, fileName, DrawIoFile.ReadText(xml), options);
        return (m, r);
    }

    private static Element Named(ModelRepository m, string name) => m.AllElements.Single(e => e.Name == name && e is not Diagram);

    // ================================================================== drawings

    private static readonly string Flow = File(Page("Ordering",
        V("a", "Start", "ellipse;whiteSpace=wrap;html=1;fillColor=#dae8fc;strokeColor=#6c8ebf;", 200, 40, 120, 60) +
        V("b", "Check<br>stock", "rhombus;whiteSpace=wrap;html=1;", 210, 160, 100, 80) +
        V("c", "Ship it", "rounded=1;whiteSpace=wrap;html=1;", 200, 300, 120, 60) +
        V("d", "A note", "text;html=1;", 400, 40, 80, 30) +
        V("db", "Orders", "shape=cylinder3;whiteSpace=wrap;html=1;", 400, 160, 60, 80) +
        E("e1", "a", "b") +
        E("e2", "b", "c", value: "yes") +
        E("e3", "b", "db", "endArrow=none;dashed=1;html=1;") +
        E("e4", "c", "nowhere")));

    [Fact]
    public void A_Plain_Drawing_Becomes_A_Whiteboard_With_Its_Layout_And_Colours()
    {
        var (m, r) = Import(Flow);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.Whiteboard, d.DiagramType);
        Assert.Equal("Ordering", d.Name);
        Assert.Equal(5, d.Nodes.Count());
        var start = d.Nodes.Single(n => n.Element.Name == "Start");
        Assert.Equal("ellipse", start.Element.Get(FieldKeys.Subtype));
        Assert.Equal(new Bounds(40, 40, 120, 60), start.Bounds);        // moved to the top-left margin, sizes kept
        Assert.Equal("#dae8fc", start.Style["fill"]);
        Assert.Equal("diamond", d.Nodes.Single(n => n.Element.Name == "Check\nstock").Element.Get(FieldKeys.Subtype));
        Assert.Equal("text", d.Nodes.Single(n => n.Element.Name == "A note").Element.Get(FieldKeys.Subtype));
        Assert.Equal("cylinder", d.Nodes.Single(n => n.Element.Name == "Orders").Element.Get(FieldKeys.Subtype));
        Assert.Equal(3, d.Edges.Count());
        Assert.Equal("yes", d.Edges.Single(e => e.Relationship.Name == "yes").Relationship.Name);
        Assert.Equal("dashed", d.Edges.Single(e => e.Target.Element.Name == "Orders").Relationship.Get(FieldKeys.Subtype));
        Assert.Contains(r.Warnings, w => w.Contains("not attached"));
    }

    [Fact]
    public void Compressed_Pages_And_Pictures_With_The_Diagram_Inside_Are_Read()
    {
        var model = Flow[(Flow.IndexOf("<mxGraphModel", StringComparison.Ordinal))..Flow.IndexOf("</diagram>", StringComparison.Ordinal)];
        var compressed = File($"<diagram id=\"x\" name=\"Packed\">{Deflate(model)}</diagram>");
        Assert.Equal("Packed", Assert.Single(DrawIoFile.ReadText(compressed)).Name);

        var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" content=\"{System.Security.SecurityElement.Escape(compressed)}\"><rect/></svg>";
        Assert.Equal(5, DGraphCount(DrawIoFile.Read("x.drawio.svg", Encoding.UTF8.GetBytes(svg))[0]));

        var png = Png(Uri.EscapeDataString(compressed));
        Assert.Equal("Packed", Assert.Single(DrawIoFile.Read("x.drawio.png", png)).Name);
        Assert.Throws<FormatException>(() => DrawIoFile.Read("plain.png", Png(null)));
        Assert.Throws<FormatException>(() => DrawIoFile.ReadText("<html/>"));
    }

    private static int DGraphCount(DrawIoPage p) => p.Model.Descendants("mxCell").Count(c => c.Attribute("vertex")?.Value == "1");

    private static string Deflate(string xml)
    {
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(Uri.EscapeDataString(xml));
            z.Write(bytes);
        }
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>A 1×1 PNG, with an "mxfile" tEXt chunk when <paramref name="text"/> is given.</summary>
    private static byte[] Png(string? text)
    {
        var ms = new MemoryStream();
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(len);
            ms.Write(len);
            ms.Write(Encoding.ASCII.GetBytes(type));
            ms.Write(data);
            ms.Write(new byte[4]);   // CRC is not checked by the reader
        }
        Chunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]);
        if (text != null) Chunk("tEXt", [.. Encoding.Latin1.GetBytes("mxfile"), 0, .. Encoding.Latin1.GetBytes(text)]);
        Chunk("IEND", []);
        return ms.ToArray();
    }

    // ================================================================== UML pages

    [Fact]
    public void Actors_And_Ellipses_Become_A_Use_Case_Diagram()
    {
        var xml = File(Page("Page-1",
            V("sys", "Shop", "rounded=0;whiteSpace=wrap;html=1;", 150, 20, 300, 300) +
            V("cust", "Customer", "shape=umlActor;verticalLabelPosition=bottom;verticalAlign=top;html=1;", 40, 100, 30, 60) +
            V("buy", "Buy item", "ellipse;whiteSpace=wrap;html=1;", 200, 60, 140, 70) +
            V("pay", "Pay", "ellipse;whiteSpace=wrap;html=1;", 200, 200, 140, 70) +
            E("a", "cust", "buy", "endArrow=none;html=1;") +
            E("i", "buy", "pay", "endArrow=open;dashed=1;html=1;", "&lt;&lt;include&gt;&gt;")));
        var (m, r) = Import(xml, fileName: "shop.drawio");
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.UseCase, d.DiagramType);
        Assert.Equal("shop", d.Name);   // a single page keeps the file's name when it has draw.io's default one
        Assert.Equal(ElementKind.Actor, Named(m, "Customer").Kind);
        Assert.Equal(ElementKind.UseCase, Named(m, "Buy item").Kind);
        Assert.Single(m.Relationships, x => x.Kind == ElementKind.Association);
        Assert.Single(m.Relationships, x => x.Kind == ElementKind.Include && x.Source.Name == "Buy item" && x.Target.Name == "Pay");
        // The system boundary is kept as an outline, drawn first so it sits behind the use cases.
        var boundary = d.Nodes.First();
        Assert.Equal("Shop", boundary.Element.Name);
        Assert.Equal("none", boundary.Style["fill"]);
    }

    private const string ClassStyle = "swimlane;fontStyle=1;align=center;verticalAlign=top;childLayout=stackLayout;horizontal=1;startSize=26;horizontalStack=0;resizeParent=1;html=1;";
    private const string Row = "text;strokeColor=none;fillColor=none;align=left;verticalAlign=top;spacingLeft=4;html=1;";

    [Fact]
    public void Class_Boxes_Become_Blocks_With_Members_And_Relationships()
    {
        var xml = File(Page("Structure",
            V("car", "Car", ClassStyle, 40, 40, 160, 100) +
            V("r1", "+ speed: Real", Row, 0, 26, 160, 26, "car") +
            V("ln", "", "line;strokeWidth=1;html=1;", 0, 52, 160, 8, "car") +
            V("r2", "+ drive(km: Real): Boolean", Row, 0, 60, 160, 26, "car") +
            V("wheel", "Wheel", ClassStyle, 300, 40, 160, 60) +
            V("w1", "radius: Real<br>- pressure: Real", Row, 0, 26, 160, 40, "wheel") +
            V("veh", "<p><i>«interface»</i><br/><b>Vehicle</b></p><hr size=\"1\"/><p>+ start()</p>", "verticalAlign=top;align=left;overflow=fill;html=1;", 40, 240, 160, 80) +
            E("comp", "car", "wheel", "startArrow=diamondThin;startFill=1;endArrow=none;html=1;") +
            "<mxCell id=\"c4\" value=\"4\" style=\"edgeLabel;html=1;\" vertex=\"1\" connectable=\"0\" parent=\"comp\"><mxGeometry x=\"0.8\" relative=\"1\" as=\"geometry\"/></mxCell>" +
            E("real", "car", "veh", "endArrow=block;endFill=0;dashed=1;html=1;")));
        var (m, r) = Import(xml);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.BlockDefinition, d.DiagramType);
        var car = Named(m, "Car");
        Assert.Equal(ElementKind.Block, car.Kind);
        Assert.Contains(car.OwnedElements, p => p.Kind == ElementKind.Property && p.Name == "speed");
        Assert.Contains(car.OwnedElements, p => p.Kind == ElementKind.Operation && p.Name == "drive");
        var wheel = Named(m, "Wheel");
        Assert.Equal(2, wheel.OwnedElements.Count(p => p.Kind == ElementKind.Property));
        Assert.Equal("private", wheel.OwnedElements.Single(p => p.Name == "pressure").Get(FieldKeys.Visibility));
        Assert.Equal(ElementKind.InterfaceBlock, Named(m, "Vehicle").Kind);
        var comp = Assert.Single(m.Relationships, x => x.Kind == ElementKind.Composition);
        Assert.Same(car, comp.Source);
        Assert.Equal("4", comp.Get(FieldKeys.Multiplicity));
        Assert.Single(m.Relationships, x => x.Kind == ElementKind.Realization && x.Source == car);
        // Blocks stay where they were drawn.
        Assert.Equal(new Pt(40, 40), new Pt(d.NodeFor(car)!.Bounds.X, d.NodeFor(car)!.Bounds.Y));
        Assert.Equal(new Pt(300, 40), new Pt(d.NodeFor(wheel)!.Bounds.X, d.NodeFor(wheel)!.Bounds.Y));
    }

    private const string Start = "ellipse;html=1;shape=startState;fillColor=#000000;strokeColor=#ff0000;";
    private const string End = "ellipse;html=1;shape=endState;fillColor=#000000;strokeColor=#ff0000;";
    private const string StateBox = "rounded=1;whiteSpace=wrap;html=1;arcSize=40;fillColor=#ffffc0;strokeColor=#ff0000;";

    [Fact]
    public void Start_And_End_States_With_Event_Labels_Become_A_State_Machine()
    {
        var xml = File(Page("Door",
            V("s", "", Start, 40, 60, 30, 30) +
            V("closed", "Closed<br>entry / lock", StateBox, 120, 40, 120, 60) +
            V("open", "Open", StateBox, 320, 40, 120, 60) +
            V("e", "", End, 520, 60, 30, 30) +
            E("t0", "s", "closed") +
            E("t1", "closed", "open", value: "push [unlocked] / beep") +
            E("t2", "open", "closed", value: "release") +
            E("t3", "open", "e", value: "break")));
        var (m, r) = Import(xml);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.StateMachine, d.DiagramType);
        var closed = Named(m, "Closed");
        Assert.Equal(ElementKind.State, closed.Kind);
        Assert.Equal("lock", closed.Get(FieldKeys.Entry));
        var push = Assert.Single(m.Relationships, x => x.Kind == ElementKind.Transition && x.Name == "push");
        Assert.Equal("unlocked", push.Get(FieldKeys.Guard));
        Assert.Equal("beep", push.Get(FieldKeys.Effect));
        Assert.Single(m.AllElements, x => x.Kind == ElementKind.InitialState);
        Assert.Single(m.AllElements, x => x.Kind == ElementKind.FinalState);
        Assert.Equal(120, d.NodeFor(closed)!.Bounds.X);
    }

    [Fact]
    public void Start_And_End_With_Decision_Guards_Become_An_Activity()
    {
        var xml = File(Page("Approve",
            V("s", "", Start, 100, 20, 30, 30) +
            V("a", "Review", StateBox, 55, 90, 120, 50) +
            V("q", "OK?", "rhombus;whiteSpace=wrap;html=1;", 75, 180, 80, 60) +
            V("b", "Rework", StateBox, 240, 185, 120, 50) +
            V("e", "", End, 100, 300, 30, 30) +
            E("1", "s", "a") + E("2", "a", "q") + E("3", "q", "e", value: "yes") + E("4", "q", "b", value: "no") + E("5", "b", "a")));
        var (m, r) = Import(xml);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.Activity, d.DiagramType);
        Assert.Equal(ElementKind.Action, Named(m, "Review").Kind);
        Assert.Equal(ElementKind.DecisionNode, Named(m, "OK?").Kind);
        Assert.Contains(m.Relationships, x => x.Kind == ElementKind.ControlFlow && x.Get(FieldKeys.Guard) == "yes");
        Assert.Single(m.AllElements, x => x.Kind == ElementKind.InitialNode);
        Assert.Single(m.AllElements, x => x.Kind == ElementKind.ActivityFinalNode);
    }

    [Fact]
    public void Lifelines_Become_A_Sequence_Diagram_With_Messages_In_Order()
    {
        const string life = "shape=umlLifeline;perimeter=lifelinePerimeter;whiteSpace=wrap;html=1;container=1;size=40;";
        var xml = File(Page("Login",
            V("u", "User", life + "participant=umlActor;", 40, 40, 100, 300) +
            V("s", "Server", life, 240, 40, 100, 300) +
            V("act", "", "html=1;points=[];perimeter=orthogonalPerimeter;", 45, 80, 10, 120, "s") +
            // Written out of order: the later message first.
            E("m2", "act", "u", "html=1;endArrow=open;dashed=1;exitX=0;exitY=0.9;", "token") +
            E("m1", "u", "act", "html=1;endArrow=block;endFill=1;exitX=0.5;exitY=0.2;", "login(name)") +
            V("f", "alt", "shape=umlFrame;whiteSpace=wrap;html=1;", 20, 90, 360, 140)));
        var (m, r) = Import(xml);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.Sequence, d.DiagramType);
        var messages = d.Edges.Where(e => e.Relationship.Kind == ElementKind.Message).OrderBy(e => e.Waypoints[0].Y).Select(e => e.Relationship).ToList();
        Assert.Equal(["login(name)", "token"], messages.Select(x => x.Name));
        Assert.Equal("synchCall", messages[0].Get(FieldKeys.MessageSort));
        Assert.Equal("reply", messages[1].Get(FieldKeys.MessageSort));
        Assert.Single(m.AllElements, x => x.Kind == ElementKind.CombinedFragment);
    }

    [Fact]
    public void Turning_Off_Recognition_Keeps_Uml_Pages_As_Drawings()
    {
        var xml = File(Page("Door", V("s", "", Start, 40, 60, 30, 30) + V("x", "Closed", StateBox, 120, 40, 120, 60) + E("t", "s", "x")));
        var (_, r) = Import(xml, new DrawIoImportOptions { ModelElements = false });
        Assert.Equal(DiagramType.Whiteboard, Assert.Single(r.Diagrams).DiagramType);
    }

    // ================================================================== export

    [Fact]
    public void Exported_Diagrams_Read_Back_As_The_Same_Kinds_Of_Diagram()
    {
        var src = SampleModels.Vehicle();
        var diagrams = src.Diagrams.Where(d => d.Nodes.Any()).ToList();
        var xml = DrawIoExporter.Export(src, diagrams);
        var pages = DrawIoFile.ReadText(xml);
        Assert.Equal(diagrams.Count, pages.Count);
        Assert.Equal(diagrams.Select(d => d.Name), pages.Select(p => p.Name));

        var m = new ModelRepository("Back");
        var r = DrawIoImporter.Import(m, m.Root, "vehicle.drawio", pages);
        foreach (var d in diagrams.Where(d => d.DiagramType is DiagramType.BlockDefinition or DiagramType.UseCase or DiagramType.StateMachine or DiagramType.Sequence or DiagramType.Activity or DiagramType.Requirement
                     or DiagramType.InternalBlock or DiagramType.Parametric))
        {
            var back = r.Diagrams.Single(x => x.Name == d.Name);
            Assert.Equal(d.DiagramType, back.DiagramType);
        }
        // Blocks keep their names and members.
        foreach (var block in src.AllElements.Where(e => e.Kind == ElementKind.Block && diagrams.Any(d => d.DiagramType == DiagramType.BlockDefinition && d.NodeFor(e) != null)))
        {
            var copy = m.AllElements.First(e => e.Kind == ElementKind.Block && e.Name == block.Name);
            Assert.Equal(block.OwnedElements.Where(p => p.Kind == ElementKind.Property).Select(p => p.Name).Order(), copy.OwnedElements.Where(p => p.Kind == ElementKind.Property).Select(p => p.Name).Order());
            Assert.Equal(block.OwnedElements.Where(p => p.Kind == ElementKind.Port).Select(p => p.Name).Order(), copy.OwnedElements.Where(p => p.Kind == ElementKind.Port).Select(p => p.Name).Order());
        }
        // The internal block and parametric diagrams come back on the same block, with their parts, parameters and bindings.
        foreach (var d in diagrams.Where(d => d.DiagramType is DiagramType.InternalBlock or DiagramType.Parametric))
        {
            var back = r.Diagrams.Single(x => x.Name == d.Name);
            Assert.Equal(d.Owner!.Name, back.Owner!.Name);
            Assert.Equal(d.Nodes.Select(n => (n.Element.Name, n.Element.Kind, Parent: d.ParentOf(n)?.Element.Name)).Order(),
                back.Nodes.Select(n => (n.Element.Name, n.Element.Kind, Parent: back.ParentOf(n)?.Element.Name)).Order());
            Assert.Equal(d.Edges.Select(e => (e.Relationship.Kind, e.Source.Element.Name, e.Target.Element.Name, e.Relationship.Get(FieldKeys.TargetPart).Length > 0)).Order(),
                back.Edges.Select(e => (e.Relationship.Kind, e.Source.Element.Name, e.Target.Element.Name, e.Relationship.Get(FieldKeys.TargetPart).Length > 0)).Order());
            foreach (var n in back.Nodes.Where(n => n.Element.Kind == ElementKind.Property))
            {
                var original = d.Nodes.First(o => o.Element.Name == n.Element.Name && d.ParentOf(o)?.Element.Name == back.ParentOf(n)?.Element.Name).Element;
                var originalType = src.Find(original.TypeRef)?.Name ?? original.TypeRef;
                Assert.Equal(originalType, m.Find(n.Element.TypeRef)?.Name ?? n.Element.TypeRef);
            }
        }
    }

    [Fact]
    public void Internal_Block_Diagrams_Keep_Ports_On_Parts_And_Item_Flows()
    {
        var src = Meridian.Core.Samples.CubeSatSample.Create();
        var ibd = src.Diagrams.Single(d => d.Name == "Power and Data Flow");
        var pages = DrawIoFile.ReadText(DrawIoExporter.Export(src, [ibd]));
        Assert.Equal(DrawIoPageKind.InternalBlock, Assert.Single(DrawIoImporter.Preview("sat.drawio", pages)).Kind);

        var m = new ModelRepository("Back");
        var back = Assert.Single(DrawIoImporter.Import(m, m.Root, "sat.drawio", pages).Diagrams);
        Assert.Equal(DiagramType.InternalBlock, back.DiagramType);
        Assert.Equal(ibd.Edges.Count(), back.Edges.Count());
        foreach (var e in back.Edges)
        {
            var r = e.Relationship;
            Assert.Equal(ElementKind.Connector, r.Kind);
            // Each end sits on the port shown on the part the connector names.
            Assert.Equal(r.Get(FieldKeys.SourcePart), back.ParentOf(e.Source)!.Element.Id);
            Assert.Equal(r.Get(FieldKeys.TargetPart), back.ParentOf(e.Target)!.Element.Id);
            Assert.Equal(ElementKind.Port, e.Source.Element.Kind);
            // Ports belong to the part's type.
            Assert.Equal(m.Find(back.ParentOf(e.Source)!.Element.TypeRef), e.Source.Element.Owner);
        }
        Assert.Contains(back.Edges, e => e.Relationship.Get(FieldKeys.ItemFlow) == "Power" && e.Relationship.Get(FieldKeys.FlowDirection) == "forward");

        // Read back into the model it came from (matching names): the same parts and ports, nothing doubled.
        var before = src.AllElements.Count(e => e is not Relationship && e is not Diagram);
        DrawIoImporter.Import(src, src.Root, "sat.drawio", pages, new DrawIoImportOptions { Match = Meridian.Serialization.Mermaid.MermaidMatchMode.MatchByName });
        Assert.Equal(before, src.AllElements.Count(e => e is not Relationship && e is not Diagram));
    }

    [Fact]
    public void Free_Shapes_And_Pictures_Survive_A_Round_Trip()
    {
        var (m, r) = Import(Flow);
        var d = r.Diagrams[0];
        m.AddImage(d, m.Root, "data:image/png;base64,iVBORw0KGgo=", new Bounds(500, 300, 40, 40));
        var xml = DrawIoExporter.Export(m, [d]);
        Assert.Contains("image=data:image/png,iVBORw0KGgo=", xml);
        var (m2, r2) = Import(xml);
        var back = Assert.Single(r2.Diagrams);
        Assert.Equal(DiagramType.Whiteboard, back.DiagramType);
        Assert.Equal(d.Nodes.Count(), back.Nodes.Count());
        Assert.Equal(d.Edges.Count(), back.Edges.Count());
        Assert.Equal("data:image/png;base64,iVBORw0KGgo=", back.Nodes.Single(n => DiagramExtras.IsImage(n)).Element.Get(DiagramExtras.ImageField));
        Assert.Equal(d.Nodes.Select(n => n.Element.Get(FieldKeys.Subtype)).Order(), back.Nodes.Select(n => n.Element.Get(FieldKeys.Subtype)).Order());
    }

    // ================================================================== folders

    private static DiagramSourceFile F(string path, string text) => new(path, Encoding.UTF8.GetBytes(text));

    private static readonly DiagramSourceFile[] Folder =
    [
        F("docs/overview.drawio", Flow),
        F("docs/flows/login.mmd", "flowchart TD\n A[Enter name] --> B{Valid?}\n B -->|yes| C[Welcome]"),
        F("docs/flows/states.mmd", "---\ntitle: Door states\n---\nstateDiagram-v2\n [*] --> Closed\n Closed --> Open : push"),
        F("docs/model/structure.md", "# Structure\n\n```mermaid\nclassDiagram\n class Car\n Car *-- Wheel\n```\n\n```mermaid\nclassDiagram\n class Wheel\n```\n"),
        F("docs/README.md", "# Just text, no diagrams"),
        F("docs/notes.txt", "ignored"),
    ];

    [Fact]
    public void A_Folder_Becomes_Packages_With_A_Diagram_Per_File()
    {
        var m = new ModelRepository("Test");
        var preview = DiagramFolderImport.Preview(Folder);
        Assert.Equal(["docs/flows/login.mmd", "docs/flows/states.mmd", "docs/model/structure.md", "docs/overview.drawio"], preview.Select(p => p.Path));
        Assert.Equal(["login"], preview[0].Diagrams.Select(x => x.Name));
        Assert.Equal(["structure 1", "structure 2"], preview[2].Diagrams.Select(x => x.Name));

        var r = DiagramFolderImport.Import(m, m.Root, Folder);
        Assert.Equal(4, r.Files);
        Assert.Equal(5, r.Diagrams.Count);
        var docs = m.Root.OwnedElements.Single(e => e.Kind == ElementKind.Package && e.Name == "docs");
        var flows = docs.OwnedElements.Single(e => e.Kind == ElementKind.Package && e.Name == "flows");
        Assert.Contains(flows.Descendants().OfType<Diagram>(), d => d.Name == "login" && d.DiagramType == DiagramType.Activity);
        Assert.Contains(flows.Descendants().OfType<Diagram>(), d => d.Name == "Door states");   // a Mermaid title wins over the file name
        Assert.Contains(docs.OwnedElements.OfType<Diagram>(), d => d.Name == "Ordering");
        // Car and Wheel are shared by the two diagrams of structure.md.
        Assert.Single(m.AllElements, e => e.Kind == ElementKind.Block && e.Name == "Wheel");
        Assert.True(m.History.CanUndo);
        m.History.Undo();
        Assert.Empty(m.Diagrams);
    }

    [Fact]
    public void Importing_A_Folder_Again_Replaces_Its_Diagrams()
    {
        var m = new ModelRepository("Test");
        DiagramFolderImport.Import(m, m.Root, Folder);
        var count = m.Diagrams.Count();
        var shapes = m.AllElements.Count(e => e.Kind == ElementKind.Shape);
        var changed = Folder.Select(f => f.Path == "docs/flows/login.mmd" ? F(f.Path, "flowchart TD\n A[Enter name] --> B[Check] --> C[Welcome]") : f).ToList();
        Assert.True(DiagramFolderImport.Preview(changed, model: m).All(p => p.ImportedBefore));

        var r = DiagramFolderImport.Import(m, m.Root, changed, new DiagramFolderOptions { Match = MermaidMatchMode.MatchByName });
        Assert.Equal(5, r.Replaced);
        Assert.Equal(count, m.Diagrams.Count());
        Assert.Equal(shapes, m.AllElements.Count(e => e.Kind == ElementKind.Shape));
        Assert.Single(m.AllElements, e => e.Kind == ElementKind.Block && e.Name == "Car");
        Assert.Single(m.AllElements, e => e.Kind == ElementKind.Activity);
        Assert.Contains(m.AllElements, e => e.Kind == ElementKind.Action && e.Name == "Check");
    }

    [Fact]
    public void A_Folder_Imported_Before_Is_Found_Where_It_Went()
    {
        var m = new ModelRepository("Test");
        var into = m.Create(m.Root, ElementKind.Package, "Imported");
        Assert.Null(DiagramFolderImport.PreviousTarget(m, Folder));
        DiagramFolderImport.Import(m, into, Folder);
        Assert.Same(into, DiagramFolderImport.PreviousTarget(m, Folder));
        Assert.Same(into, DiagramFolderImport.PreviousTarget(m, [Folder[1]]));   // a nested file climbs out of docs/flows

        var flat = new ModelRepository("Flat");
        DiagramFolderImport.Import(flat, flat.Root, Folder, new DiagramFolderOptions { PackagePerFolder = false });
        Assert.Same(flat.Root, DiagramFolderImport.PreviousTarget(flat, Folder));
    }

    [Fact]
    public void Filled_Shapes_Get_Readable_Text()
    {
        var (_, r) = Import(Flow);
        var start = r.Diagrams[0].Nodes.Single(n => n.Element.Name == "Start");
        Assert.Equal("#1f2328", start.Style["text"]);   // light blue fill: dark text, as in draw.io
        Assert.False(r.Diagrams[0].Nodes.Single(n => n.Element.Name == "Ship it").Style.ContainsKey("text"));
    }

    [Fact]
    public void A_Broken_File_Is_Reported_And_The_Rest_Imported()
    {
        var m = new ModelRepository("Test");
        var r = DiagramFolderImport.Import(m, m.Root, [F("a/broken.drawio", "<mxfile><diagram>not base64 at all!</diagram></mxfile>"), F("a/ok.mmd", "flowchart LR\n A --> B")]);
        Assert.Single(r.Diagrams);
        Assert.Contains(r.Warnings, w => w.StartsWith("a/broken.drawio:"));
        Assert.NotNull(DiagramFolderImport.Preview([F("a/broken.drawio", "<mxfile><diagram>???</diagram></mxfile>")]).Single().Error);
    }
}
