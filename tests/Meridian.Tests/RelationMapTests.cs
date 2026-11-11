using System.Diagnostics;
using Meridian.Core.Analysis;
using Meridian.Core.CodeSync;
using Meridian.Core.Libraries;
using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Core.Samples;
using Meridian.Serialization;
using Meridian.Serialization.Reports;
using Xunit.Abstractions;

namespace Meridian.Tests;

/// <summary>Relation maps (Core/Analysis/RelationMap.cs): graph building, filters, caps and stubs, layout, saving, diagrams and reports.</summary>
public class RelationMapTests(ITestOutputHelper output)
{
    private static Element Req(ModelRepository m, string id) => m.OfKind(ElementKind.Requirement).Single(r => r.Get(FieldKeys.RequirementId) == id);
    private static Element Block(ModelRepository m, string name) => m.OfKind(ElementKind.Block).Single(b => b.Name == name);
    private static HashSet<string> Names(RelationMapGraph g) => g.Nodes.Where(n => !n.IsStub).Select(n => n.Element!.Name).ToHashSet();

    private static RelationMapSettings Map(Element root, Action<RelationMapSettings>? set = null)
    {
        var s = new RelationMapSettings { RootId = root.Id, RootName = root.Name };
        set?.Invoke(s);
        return s;
    }

    [Fact]
    public void Requirement_Map_Follows_Its_Traceability_Level_By_Level()
    {
        var m = SampleModels.Vehicle();
        var accel = Req(m, "REQ-002");
        var settings = RelationMapSettings.For(accel);
        Assert.Equal("Traceability", settings.Preset);
        settings.Depth = 1;

        var one = RelationMaps.Build(m, settings);
        var names = Names(one);
        Assert.Contains("Acceleration", names);
        Assert.Contains("Vehicle", names);              // «satisfy»
        Assert.Contains("Acceleration Test", names);    // «verify»
        Assert.Contains("Engine Power", names);         // «deriveReqt»
        Assert.DoesNotContain("Engine", names);         // two levels away
        Assert.All(one.Nodes, n => Assert.InRange(n.Depth, 0, 1));
        Assert.True(one.Root!.IsRoot);
        Assert.Equal(accel, one.Root.Element);

        settings.Depth = 2;
        var two = RelationMaps.Build(m, settings);
        Assert.Contains("Engine", Names(two));          // Engine «satisfy» Engine Power
        Assert.DoesNotContain("FuelTank", Names(two));  // compositions are not traceability
        Assert.True(two.ElementCount > one.ElementCount);

        // Edges carry the relationship kind and its «stereotype» label.
        var satisfy = two.Edges.Single(e => e.Relationship is { Kind: ElementKind.Satisfy } r && r.Source.Name == "Vehicle" && r.Target == accel);
        Assert.Equal("Satisfy", satisfy.KindName);
        Assert.Equal("«satisfy»", satisfy.Label);
        Assert.Contains(two.Edges, e => e.Label == "«verify»");
        Assert.Contains(two.Edges, e => e.Label == "«deriveReqt»");
        Assert.All(two.Edges.Where(e => e.Relationship != null), e => Assert.True(ReferenceEquals(e.Source.Element, e.Relationship!.Source) && ReferenceEquals(e.Target.Element, e.Relationship.Target)));

        // "All" also follows structure.
        settings.RelationshipKinds = RelationMapPresets.Kinds("All").ToList();
        Assert.Contains("FuelTank", Names(RelationMaps.Build(m, settings)));
    }

    [Fact]
    public void Direction_And_Kind_Filters()
    {
        var m = SampleModels.Vehicle();
        var vehicle = Block(m, "Vehicle");
        var outgoing = RelationMaps.Build(m, Map(vehicle, s => { s.Depth = 1; s.Direction = RelationDirection.Outgoing; }));
        Assert.Contains("Acceleration", Names(outgoing));   // Vehicle «satisfy» → Acceleration
        Assert.Contains("Engine", Names(outgoing));         // Vehicle ◆→ Engine
        var incoming = RelationMaps.Build(m, Map(vehicle, s => { s.Depth = 1; s.Direction = RelationDirection.Incoming; }));
        Assert.DoesNotContain("Acceleration", Names(incoming));
        Assert.DoesNotContain("Engine", Names(incoming));

        var structure = RelationMaps.Build(m, Map(vehicle, s => { s.Depth = 1; s.RelationshipKinds = RelationMapPresets.Structure.ToList(); }));
        Assert.Contains("Engine", Names(structure));
        Assert.DoesNotContain("Acceleration", Names(structure));
        Assert.All(structure.Edges.Where(e => e.Relationship != null), e => Assert.Contains(e.Relationship!.Kind, RelationMapPresets.Structure));

        var accel = Req(m, "REQ-002");
        var blocksOnly = RelationMaps.Build(m, Map(accel, s => { s.Depth = 2; s.ElementKinds = [ElementKind.Block]; }));
        Assert.Contains("Vehicle", Names(blocksOnly));
        Assert.DoesNotContain("Acceleration Test", Names(blocksOnly));
        Assert.Contains("Acceleration", Names(blocksOnly));   // the root always shows
        Assert.All(blocksOnly.Nodes.Where(n => !n.IsRoot), n => Assert.Equal(ElementKind.Block, n.Element!.Kind));
    }

    [Fact]
    public void Containment_Owned_And_Owner()
    {
        var m = SampleModels.Vehicle();
        var vehicle = Block(m, "Vehicle");
        var plain = RelationMaps.Build(m, Map(vehicle, s => s.Depth = 1));
        Assert.DoesNotContain("mass", Names(plain));
        Assert.DoesNotContain("Structure", Names(plain));

        var owned = RelationMaps.Build(m, Map(vehicle, s => { s.Depth = 1; s.IncludeOwned = true; s.IncludeOwner = true; }));
        Assert.Contains("mass", Names(owned));
        Assert.Contains("Structure", Names(owned));
        Assert.Contains(owned.Edges, e => e.Containment && e.Source.Element == vehicle && e.Target.Element!.Name == "mass");
        Assert.Contains(owned.Edges, e => e.Containment && e.Source.Element!.Name == "Structure" && e.Target.Element == vehicle);
        Assert.Equal("owns", owned.Edges.First(e => e.Containment).Label);
    }

    /// <summary>A hub block satisfying <paramref name="count"/> requirements.</summary>
    private static (ModelRepository M, Element Hub) Hub(int count)
    {
        var m = new ModelRepository("Hub");
        var pkg = m.Create(m.Root, ElementKind.Package, "P");
        var hub = m.Create(pkg, ElementKind.Block, "Hub");
        for (var i = 0; i < count; i++) m.Connect(ElementKind.Satisfy, hub, m.Create(pkg, ElementKind.Requirement, $"R{i:0000}"));
        m.History.Clear();
        return (m, hub);
    }

    [Fact]
    public void Fan_Out_And_Node_Cap_Leave_More_Stubs_That_Expand_On_Demand()
    {
        var (m, hub) = Hub(1000);
        var s = Map(hub, x => { x.Depth = 1; x.FanOut = 40; });
        var g = RelationMaps.Build(m, s);
        Assert.Equal(41, g.ElementCount);
        var stub = Assert.Single(g.Nodes, n => n.IsStub);
        Assert.Equal(960, stub.MoreCount);
        Assert.Equal("+960 more", stub.Name);
        Assert.Same(g.Root, stub.Parent);
        Assert.Contains(g.Edges, e => e.Target == stub && e.Relationship == null && !e.Containment);
        // Neighbours come in a stable order: R0000 … R0039.
        Assert.Equal(Enumerable.Range(0, 40).Select(i => $"R{i:0000}"), g.Root!.Children.Where(c => !c.IsStub).Select(c => c.Element!.Name));

        // A click on the stub shows the next batch.
        s.More[hub.Id] = RelationMapSettings.StubStep;
        var more = RelationMaps.Build(m, s);
        Assert.Equal(81, more.ElementCount);
        Assert.Equal(920, more.Nodes.Single(n => n.IsStub).MoreCount);

        // The global cap.
        var capped = RelationMaps.Build(m, Map(hub, x => { x.Depth = 1; x.FanOut = 1000; x.MaxNodes = 100; }));
        Assert.True(capped.Capped);
        Assert.Equal(100, capped.ElementCount);
        Assert.Equal(901, capped.Nodes.Single(n => n.IsStub).MoreCount);
    }

    [Fact]
    public void Nodes_Expand_Beyond_The_Depth_And_Collapse_Within_It()
    {
        var m = SampleModels.Vehicle();
        var accel = Req(m, "REQ-002");
        var s = RelationMapSettings.For(accel);
        s.Depth = 1;
        var g = RelationMaps.Build(m, s);
        var enginePower = g.Nodes.Single(n => n.Element?.Name == "Engine Power");
        Assert.False(enginePower.Explored);
        Assert.Equal(1, enginePower.Hidden);   // Engine, behind it

        s.Expanded.Add(enginePower.Id);
        var expanded = RelationMaps.Build(m, s);
        var engine = expanded.Nodes.Single(n => n.Element?.Name == "Engine");
        Assert.Equal(2, engine.Depth);
        Assert.Equal("Engine Power", engine.Parent!.Element!.Name);
        Assert.True(expanded.Find(enginePower.Id)!.Explored);

        s.Depth = 2;
        s.Expanded.Clear();
        s.Collapsed.Add(enginePower.Id);
        var collapsed = RelationMaps.Build(m, s);
        Assert.DoesNotContain("Engine", Names(collapsed));
        Assert.Equal(1, collapsed.Find(enginePower.Id)!.Hidden);
    }

    [Fact]
    public void Building_Is_Deterministic_Across_Runs_And_Reloads()
    {
        var m = SampleModels.Vehicle();
        var vehicle = Block(m, "Vehicle");
        foreach (var layout in Enum.GetValues<RelationMapLayout>())
        {
            var s = Map(vehicle, x => { x.Depth = 3; x.IncludeOwned = true; x.Layout = layout; });
            string Fingerprint(RelationMapGraph g) => string.Join("|", g.Nodes.Select(n => $"{n.Id}@{n.X},{n.Y},{n.W}")) + "#" +
                string.Join("|", g.Edges.Select(e => $"{e.Source.Id}>{e.Target.Id}:{e.Relationship?.Id}"));
            var a = RelationMaps.Build(m, s);
            var b = RelationMaps.Build(m, s.Clone());
            var reloaded = ProjectFile.Deserialize(ProjectFile.Serialize(m));
            var c = RelationMaps.Build(reloaded, s.Clone());
            Assert.Equal(Fingerprint(a), Fingerprint(b));
            Assert.Equal(Fingerprint(a), Fingerprint(c));
            Assert.Equal(RelationMapSvg.Render(a), RelationMapSvg.Render(c));
            // Laid out without overlapping boxes, everything at positive coordinates.
            Assert.All(a.Nodes, n => Assert.True(n.X >= 0 && n.Y >= 0 && n.X + n.W <= a.Width && n.Y + n.H <= a.Height));
            if (layout != RelationMapLayout.Radial)
                foreach (var x in a.Nodes)
                    foreach (var y in a.Nodes.Where(y => !ReferenceEquals(x, y)))
                        Assert.False(x.X < y.X + y.W && y.X < x.X + x.W && x.Y < y.Y + y.H && y.Y < x.Y + x.H, $"{x.Name} overlaps {y.Name} ({layout})");
        }
    }

    [Fact]
    public void Hidden_Elements_And_Relationships_Never_Appear()
    {
        var m = SampleModels.Vehicle();
        var accel = Req(m, "REQ-002");
        var structure = m.Root.OwnedElements.Single(e => e.Name == "Structure");
        var owner = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var view = AccessView.For([new(structure.Id, PrincipalKind.User, bob, PackageAccess.None)], bob, [], PackageAccess.Edit, PackageAccess.Manage);
        Assert.True(view.IsHidden(Block(m, "Vehicle")));
        var s = RelationMapSettings.For(accel);
        s.RelationshipKinds.Clear();
        s.Depth = 3;
        s.IncludeOwner = true;
        var g = RelationMaps.Build(m, s, view.IsHidden);
        Assert.DoesNotContain(g.Nodes, n => n.Element is { } e && view.IsHidden(e));
        Assert.DoesNotContain(g.Edges, e => e.Relationship is { } r && view.IsHidden(r));
        Assert.DoesNotContain("Vehicle", Names(g));
        Assert.Contains("Acceleration Test", Names(g));
        Assert.DoesNotContain(">Vehicle<", RelationMapSvg.Render(g));
        // A hidden root gives an empty map.
        Assert.Null(RelationMaps.Build(m, Map(Block(m, "Vehicle")), view.IsHidden).Root);
        _ = owner;
    }

    [Fact]
    public void Library_Elements_Show_With_Their_Badge_And_Code_Synced_Ones_Can_Be_Left_Out()
    {
        var src = new ModelRepository("Parts Library");
        var pkg = src.Create(src.Root, ElementKind.Package, "Standard parts");
        var wheel = src.Create(pkg, ElementKind.Block, "Wheel");
        var info = new LibraryInfo("u1", Guid.NewGuid(), "Parts Library", pkg.Id, LibraryPinKind.Version, "1", LibraryPins.Label(LibraryPinKind.Version, "1"), "", Resolved: "v1");
        var m = ProjectFile.Deserialize(ProjectFile.Serialize(SampleModels.Vehicle()), [new LibraryPayload(info, LibraryContent.Extract(src, pkg.Id)!)]);
        var vehicle = Block(m, "Vehicle");
        var libWheel = m.OfKind(ElementKind.Block).Single(b => b.Name == "Wheel");
        Assert.True(m.IsLibraryElement(libWheel));
        m.Connect(ElementKind.Composition, vehicle, libWheel);
        var code = m.Create(vehicle.Owner!, ElementKind.Block, "Controller");
        m.Set(code, CodeFieldKeys.Symbol, "Car.Controller");
        m.Connect(ElementKind.Dependency, vehicle, code);

        var g = RelationMaps.Build(m, Map(vehicle, s => s.Depth = 1));
        var node = g.Nodes.Single(n => n.Element == libWheel);
        Assert.StartsWith("Parts Library", node.Library);
        Assert.True(g.Nodes.Single(n => n.Element == code).CodeSynced);
        var svg = RelationMapSvg.Render(g);
        Assert.Contains("class=\"rm-lib\"", svg);
        Assert.Contains("read-only", svg);
        Assert.Contains("class=\"rm-code\"", svg);

        var without = RelationMaps.Build(m, Map(vehicle, s => { s.Depth = 1; s.IncludeLibrary = false; s.IncludeCodeSynced = false; }));
        Assert.DoesNotContain("Wheel", Names(without));
        Assert.DoesNotContain("Controller", Names(without));
        Assert.Contains("Engine", Names(without));
    }

    [Fact]
    public void Settings_Save_As_A_Model_Element_And_Survive_The_Project_File()
    {
        var m = SampleModels.Vehicle();
        var accel = Req(m, "REQ-002");
        var s = RelationMapSettings.For(accel);
        s.Depth = 3; s.Layout = RelationMapLayout.TreeDown; s.Direction = RelationDirection.Incoming; s.IncludeOwner = true;
        s.Expanded.Add("x"); s.More["y"] = 40;
        var saved = RelationMaps.Save(m, s, "Acceleration trace");
        Assert.Equal(ElementKind.RelationMap, saved.Kind);
        Assert.Equal(SavedViews.ViewsPackageName, saved.Owner!.Name);
        Assert.Equal("Relation Map", Core.Language.Metamodel.DisplayName(ElementKind.RelationMap));

        var back = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        var again = Assert.Single(RelationMaps.Saved(back));
        var loaded = RelationMaps.Load(again);
        Assert.Equal(s.ToJson(), loaded.ToJson());
        Assert.Equal(RelationMapLayout.TreeDown, loaded.Layout);
        Assert.Equal(40, loaded.More["y"]);

        // Updating keeps the element; undo restores the earlier settings.
        s.Depth = 5;
        Assert.Same(saved, RelationMaps.Save(m, s, "Acceleration trace", saved));
        Assert.Equal(5, RelationMaps.Load(saved).Depth);
        m.History.Undo();
        Assert.Equal(3, RelationMaps.Load(saved).Depth);

        // Garbage gives defaults; numbers are clamped; XMI export copes with the new kind.
        Assert.Equal(2, RelationMapSettings.FromJson("{not json").Depth);
        Assert.Equal(6, RelationMapSettings.FromJson("{\"depth\": 40}").Depth);
        Assert.Contains("Acceleration trace", XmiExchange.ToXml(m).ToString());
    }

    [Fact]
    public void Create_Diagram_From_Selection_Makes_A_Laid_Out_Diagram_In_One_Undo_Step()
    {
        var m = SampleModels.Vehicle();
        var accel = Req(m, "REQ-002");
        var g = RelationMaps.Build(m, RelationMapSettings.For(accel));
        var picked = g.Elements.Where(e => e.Name is "Acceleration" or "Vehicle" or "Acceleration Test" or "Engine Power").ToList();
        Assert.Equal(4, picked.Count);
        var before = m.Diagrams.Count();
        var d = RelationMaps.CreateDiagram(m, picked, accel.Owner!.Owner!, "Acceleration relations");
        Assert.Equal(DiagramType.Requirement, d.DiagramType);
        Assert.Equal(4, d.Nodes.Count);
        Assert.Contains(d.Edges, e => e.Relationship.Kind == ElementKind.Satisfy);
        Assert.Contains(d.Edges, e => e.Relationship.Kind == ElementKind.Verify);
        Assert.Contains(d.Edges, e => e.Relationship.Kind == ElementKind.DeriveReqt);
        Assert.Equal(4, d.Nodes.Select(n => (n.Bounds.X, n.Bounds.Y)).Distinct().Count());   // laid out, not stacked
        m.History.Undo();
        Assert.Equal(before, m.Diagrams.Count());
        Assert.Equal(DiagramType.BlockDefinition, RelationMaps.DiagramTypeFor([Block(m, "Vehicle")]));
        Assert.Throws<InvalidOperationException>(() => RelationMaps.CreateDiagram(m, [], m.Root, "x"));
    }

    [Fact]
    public void Svg_Shows_Icons_Names_Kinds_And_Stereotype_Labels()
    {
        var m = SampleModels.Vehicle();
        var g = RelationMaps.Build(m, RelationMapSettings.For(Req(m, "REQ-002")));
        var svg = RelationMapSvg.Render(g, icon: k => $"<path data-icon=\"{k}\"/>");
        Assert.StartsWith("<svg", svg);
        Assert.Contains("REQ-002 Acceleration", svg);
        Assert.Contains(">Requirement<", svg);
        Assert.Contains(">Test Case<", svg);
        Assert.Contains("&#171;satisfy&#187;", svg);
        Assert.Contains("data-icon=\"Block\"", svg);
        Assert.DoesNotContain("data-node", svg);   // the export has no click targets
        var sb = new System.Text.StringBuilder();
        RelationMapSvg.RenderBody(sb, g, SvgTheme.Dark, null, new RelationMapMarks(new HashSet<string> { g.Root!.Id }, null, Interactive: true));
        Assert.Contains($"data-node=\"{g.Root.Id}\"", sb.ToString());
        Assert.Contains("rm-node root sel", sb.ToString());
    }

    [Fact]
    public void Report_Block_Draws_A_Relation_Map_Figure()
    {
        var m = SampleModels.Vehicle();
        var saved = RelationMaps.Save(m, RelationMapSettings.For(Req(m, "REQ-005")), "Engine power trace");
        var t = new DocTemplate
        {
            TitlePage = false, RevisionHistory = false, TableOfContents = false, ListOfFigures = false, ListOfTables = false,
            Sections =
            [
                new DocSection { Title = "Trace", Blocks = [new RelationMapBlock { Root = "REQ-002", Depth = 1, Preset = "Traceability" }] },
                new DocSection { Title = "Saved", Blocks = [new RelationMapBlock { Map = "Engine power trace", Caption = "Saved map" }] },
                new DocSection { Title = "Loop", Blocks = [new ForEachBlock { Query = "kind:Requirement where reqId = REQ-004", Blocks = [new RelationMapBlock { Depth = 1 }] }] },
                new DocSection { Title = "Missing", Blocks = [new RelationMapBlock { Root = "No such thing" }] },
            ],
        };
        var back = DocTemplate.FromJson(t.ToJson());
        Assert.IsType<RelationMapBlock>(back.Sections[0].Blocks[0]);
        Assert.Equal("Traceability", ((RelationMapBlock)back.Sections[0].Blocks[0]).Preset);

        var doc = DocResolver.Resolve(m, back);
        var figures = doc.Nodes.OfType<FigureNode>().ToList();
        Assert.Equal(3, figures.Count);
        Assert.Contains("Relation map of Acceleration", figures[0].Caption.Display);
        Assert.Contains("Acceleration Test", figures[0].Svg);
        Assert.DoesNotContain(">Engine<", figures[0].Svg);   // depth 1
        Assert.Contains("Saved map", figures[1].Caption.Display);
        Assert.Contains("Engine Power", figures[1].Svg);
        Assert.Contains("Braking", figures[2].Svg);
        Assert.Contains(doc.Warnings, w => w.Contains("No such thing"));
        Assert.True(figures.All(f => f.Width > 0 && f.Height > 0));
        _ = saved;
        // The figure goes through every writer.
        Assert.NotEmpty(DocHtml.Write(doc));
    }

    /// <summary>The 200,000-element model (opt-in: MERIDIAN_HEAVY=1): maps build in milliseconds because only what is shown is visited.</summary>
    [SkippableFact]
    public void Heavy_200k_Relation_Maps_Stay_Fast()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("MERIDIAN_HEAVY") is "1" or "true", "Set MERIDIAN_HEAVY=1 to run the 200,000-element relation maps.");
        var m = LargeModelGenerator.Generate(LargeModelOptions.Cameo(200_000));
        Assert.InRange(m.Count, 190_000, 215_000);
        var busiest = m.OfKind(ElementKind.Requirement).OrderByDescending(r => m.RelationshipsOf(r).Count).First();
        var block = m.OfKind(ElementKind.Block).OrderByDescending(b => m.RelationshipsOf(b).Count).First();
        var package = m.OfKind(ElementKind.Package).OrderByDescending(p => p.OwnedElements.Count).First();
        RelationMaps.Build(m, RelationMapSettings.For(busiest));   // warm up (JIT)
        var cases = new (string Name, RelationMapSettings S)[]
        {
            ("requirement, traceability, depth 6", Set(RelationMapSettings.For(busiest), s => s.Depth = 6)),
            ("requirement, all kinds, depth 6, containment", Set(RelationMapSettings.For(busiest), s => { s.Depth = 6; s.RelationshipKinds.Clear(); s.IncludeOwned = true; s.IncludeOwner = true; })),
            ("block, all kinds, depth 6, radial", Set(Map(block), s => { s.Depth = 6; s.Layout = RelationMapLayout.Radial; })),
            ("largest package, owned, depth 3", Set(Map(package), s => { s.Depth = 3; s.IncludeOwned = true; })),
            ("model root, owned, depth 6, cap 2000", Set(Map(m.Root.OwnedElements.First(e => e.Kind == ElementKind.Package)), s => { s.Depth = 6; s.IncludeOwned = true; s.MaxNodes = 2000; })),
        };
        foreach (var (name, s) in cases)
        {
            var sw = Stopwatch.StartNew();
            var g = RelationMaps.Build(m, s);
            var build = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var svg = RelationMapSvg.Render(g);
            var render = sw.Elapsed.TotalMilliseconds;
            output.WriteLine($"{name}: {g.ElementCount} elements, {g.Nodes.Count(n => n.IsStub)} stubs, {g.Edges.Count} edges — build+layout {build:0.0} ms, SVG {render:0.0} ms ({svg.Length / 1024} KB)");
            Assert.True(g.ElementCount <= s.MaxNodes);
            Assert.True(build < 1_000, $"{name}: build took {build:0} ms");
            Assert.True(render < 1_000, $"{name}: render took {render:0} ms");
        }
    }

    private static RelationMapSettings Set(RelationMapSettings s, Action<RelationMapSettings> a) { a(s); return s; }
}
