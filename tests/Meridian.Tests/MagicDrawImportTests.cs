using System.Diagnostics;
using System.IO.Compression;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization;

namespace Meridian.Tests;

public sealed class MagicDrawImportTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("meridian-md-test-");

    public void Dispose()
    {
        try { _dir.Delete(true); } catch (IOException) { }
    }

    private (ModelRepository Model, MagicDrawImportReport Report) ImportFixture() =>
        MagicDrawImporter.Import(MagicDrawFixtures.WriteMdzip(_dir.FullName));

    private static Element ById(ModelRepository m, string id) => m.Find(id) ?? throw new Xunit.Sdk.XunitException($"{id} not imported");

    [Fact]
    public void Mdzip_Imports_Blocks_Ports_Properties_And_Stereotypes()
    {
        var (m, report) = ImportFixture();
        Assert.Equal("Drone Project", m.Root.Name);
        Assert.Equal("_model", m.Root.Id);
        Assert.Equal("Project documentation", m.Root.Documentation);

        var drone = ById(m, "_drone");
        Assert.Equal(ElementKind.Block, drone.Kind);
        Assert.Equal("Structure", drone.Owner!.Name);
        Assert.Equal("A small & fast quadcopter", drone.Documentation);           // HTML documentation comment
        Assert.Contains("Critical", drone.Stereotypes);                          // user stereotype + tagged values
        Assert.Equal("high", drone.Get(FieldKeys.TagPrefix + "level"));
        Assert.Equal("Ana, Bo", drone.Get(FieldKeys.TagPrefix + "reviewers"));
        Assert.DoesNotContain(drone.Stereotypes, s => s is "Block" or "PartProperty");
        Assert.Contains(drone.OwnedElements, e => e.Kind == ElementKind.Comment && e.Get(FieldKeys.Body) == "Reviewed by systems team");

        Assert.Equal(ElementKind.InterfaceBlock, ById(m, "_ifb").Kind);
        Assert.Equal(ElementKind.ValueType, ById(m, "_kg").Kind);
        Assert.Equal(ElementKind.ConstraintBlock, ById(m, "_cb").Kind);
        Assert.Equal("m = sum(parts)", ById(m, "_cb").Get(FieldKeys.Body));
        Assert.True(ById(m, "_vehicle").IsAbstract);
        Assert.Equal(["Auto", "Manual"], ById(m, "_mode").OwnedElements.Select(e => e.Name));

        var rotors = ById(m, "_p_rotors");
        Assert.Equal("_rotor", rotors.TypeRef);
        Assert.Equal("4", rotors.Multiplicity);
        Assert.Equal("private", rotors.Get(FieldKeys.Visibility));
        Assert.Equal("composite", rotors.Get(MagicDrawImporter.AggregationField));
        var ctrl = ById(m, "_p_ctrl");
        Assert.Equal("0..1", ctrl.Multiplicity);
        Assert.Equal("none", ctrl.Get(MagicDrawImporter.AggregationField));
        Assert.Equal("_kg", ById(m, "_p_mass").TypeRef);
        Assert.Equal("1.2", ById(m, "_p_mass").Get(FieldKeys.DefaultValue));
        Assert.Equal("Real", ById(m, "_p_speed").TypeRef);
        Assert.Equal("length", ById(m, "_p_alt").TypeRef);                       // library type kept by name
        Assert.Equal("(speed : Real) : Boolean", drone.OwnedElements.Single(e => e.Name == "land").Get(FieldKeys.Signature));

        Assert.Equal("proxy", ById(m, "_port1").Get(FieldKeys.PortKind));
        Assert.Equal("full", ById(m, "_port2").Get(FieldKeys.PortKind));
        Assert.Equal("true", ById(m, "_port2").Get(FieldKeys.TagPrefix + "conjugated"));
        Assert.Equal("in", ById(m, "_port3").Get(FieldKeys.Direction));
        Assert.Contains("flowPort", ById(m, "_port3").Stereotypes);
        Assert.Equal("out", ById(m, "_fp").Get(FieldKeys.Direction));

        var conn = (Relationship)ById(m, "_conn1");
        Assert.Equal(ElementKind.Connector, conn.Kind);
        Assert.Equal("_rp", conn.Source.Id);
        Assert.Equal("_port1", conn.Target.Id);
        Assert.Equal("_p_rotors", conn.Get(FieldKeys.SourcePart));
        var bind = (Relationship)ById(m, "_bind1");
        Assert.Equal(ElementKind.Binding, bind.Kind);
        Assert.Equal("_p_eq", bind.Get(FieldKeys.TargetPart));

        var comp = (Relationship)ById(m, "_assoc1");
        Assert.Equal(ElementKind.Composition, comp.Kind);
        Assert.Same(drone, comp.Source);
        Assert.Equal("_rotor", comp.Target.Id);
        Assert.Equal("4", comp.Multiplicity);
        var gen = (Relationship)ById(m, "_gen1");
        Assert.Equal(ElementKind.Generalization, gen.Kind);
        Assert.Equal("_vehicle", gen.Target.Id);

        Assert.Equal(1, report.Imported["Part property"]);
        Assert.Equal(1, report.Imported["Reference property"]);
        Assert.Equal(1, report.Imported["Constraint property"]);
        Assert.Equal(1, report.Imported["Constraint parameter"]);
        Assert.Equal(2, report.Imported["Value property"]);                  // mass, speed (altitude is typed by a library type)
        Assert.Equal(2, report.Imported["Proxy port"]);
        Assert.Equal(1, report.Imported["Full port"]);
        Assert.Equal(1, report.Imported["Flow port"]);
        Assert.Equal(1, report.Imported["Flow property"]);
        Assert.Equal(1, report.Imported["Binding connector"]);
    }

    [Fact]
    public void Requirements_And_Trace_Relationships_Map_From_Stereotypes()
    {
        var (m, report) = ImportFixture();
        var req1 = ById(m, "_req1");
        Assert.Equal(ElementKind.Requirement, req1.Kind);
        Assert.Equal("REQ-1", req1.Get(FieldKeys.RequirementId));
        Assert.Equal("Fly at least 30 minutes", req1.Get(FieldKeys.Text));
        Assert.Equal("Withstand 10 m/s wind", ById(m, "_req2").Get(FieldKeys.Text));

        void Rel(string id, ElementKind k, string src, string tgt)
        {
            var r = (Relationship)ById(m, id);
            Assert.Equal(k, r.Kind);
            Assert.Equal(src, r.Source.Id);
            Assert.Equal(tgt, r.Target.Id);
        }
        Rel("_sat1", ElementKind.Satisfy, "_drone", "_req1");
        Rel("_der1", ElementKind.DeriveReqt, "_req2", "_req1");
        Rel("_ver1", ElementKind.Verify, "_tc", "_req1");
        Rel("_ref1", ElementKind.Refine, "_uc1", "_req2");
        Rel("_tr1", ElementKind.Trace, "_ctrl", "_req2");
        Rel("_alloc1", ElementKind.Allocate, "_act1", "_drone");
        Rel("_badsat", ElementKind.Trace, "_req2", "_drone");                    // invalid satisfy downgraded, with a warning
        Assert.Contains(report.Warnings, w => w.Contains("_badsat"));
        Assert.Null(m.Find("_dep1"));                                           // supplier outside the project
        Assert.True(report.Skipped["Relationships with unresolved ends"] >= 1);
        Assert.Equal(ElementKind.TestCase, ById(m, "_tc").Kind);
        Assert.Equal("Hover 60 s", ById(m, "_tca").Name);                       // test case's actions live in a nested activity
    }

    [Fact]
    public void Activities_State_Machines_Interactions_And_Use_Cases_Import()
    {
        var (m, report) = ImportFixture();
        var a1 = ById(m, "_a1");
        Assert.Equal(ElementKind.Action, a1.Kind);
        Assert.Equal("_act2", a1.Get(FieldKeys.Behavior));
        Assert.Equal(ElementKind.InputPin, ById(m, "_pin1").Kind);
        Assert.Same(a1, ById(m, "_pin1").Owner);
        Assert.Equal("_kg", ById(m, "_pin1").TypeRef);
        Assert.Equal(ElementKind.OutputPin, ById(m, "_pin2").Kind);
        Assert.Equal("fly(route)", ById(m, "_a2").Get(FieldKeys.Body));
        Assert.Equal("Start", ById(m, "_a3").Get(ExtFieldKeys.SendSignal));
        Assert.Equal(ElementKind.DecisionNode, ById(m, "_dec").Kind);
        Assert.Equal("battery > 20", ById(m, "_cf3").Get(FieldKeys.Guard));
        Assert.Equal("", ById(m, "_cf1").Get(FieldKeys.Guard));                  // default "true" guard dropped
        Assert.Equal(ElementKind.ObjectFlow, ById(m, "_of1").Kind);
        Assert.Equal("_drone", ById(m, "_part1").Get(FieldKeys.Represents));
        Assert.Equal(1, report.Skipped["Structured activity nodes (and their contents)"]);

        Assert.Equal(ElementKind.InitialState, ById(m, "_ps").Kind);
        Assert.Equal("self check", ById(m, "_idle").Get(FieldKeys.Entry));
        Assert.Equal("hover()", ById(m, "_flying").Get(FieldKeys.DoActivity));
        Assert.Equal(ElementKind.FinalState, ById(m, "_fs").Kind);
        Assert.Contains("choice", ById(m, "_ch").Stereotypes);
        var t2 = (Relationship)ById(m, "_t2");
        Assert.Equal(ElementKind.Transition, t2.Kind);
        Assert.Equal("Start", t2.Name);
        Assert.Equal("armed", t2.Get(FieldKeys.Guard));
        Assert.Equal("spinUp()", t2.Get(FieldKeys.Effect));
        Assert.Equal("after(30 min)", ById(m, "_t3").Name);
        Assert.Same(ById(m, "_sm"), t2.Owner);

        var interaction = ById(m, "_int");
        Assert.Equal(ElementKind.Interaction, interaction.Kind);
        Assert.Equal("Behavior", interaction.Owner!.Name);                     // collaboration wrapper is transparent
        var ll1 = ById(m, "_ll1");
        Assert.Equal("pilotSide", ll1.Name);
        Assert.Equal("_drone", ll1.TypeRef);
        var m1 = (Relationship)ById(m, "_m1");
        Assert.Equal("_ll2", m1.Source.Id);
        Assert.Equal("_ll1", m1.Target.Id);
        Assert.Equal("synchCall", m1.Get(FieldKeys.MessageSort));
        Assert.Equal("asynchSignal", ById(m, "_m2").Get(FieldKeys.MessageSort));
        Assert.Null(m.Find("_m3"));
        Assert.Equal(1, report.Skipped["Lost / found messages and messages to gates"]);
        var frag = ById(m, "_cfrag");
        Assert.Equal("alt", frag.Get(FieldKeys.Operator));
        Assert.Equal("ok | else", frag.Get(FieldKeys.Guard));

        Assert.Equal(ElementKind.Actor, ById(m, "_actor").Kind);
        Assert.Equal(ElementKind.Include, ById(m, "_inc1").Kind);
        var a2 = (Relationship)ById(m, "_assoc2");
        Assert.Equal(ElementKind.Association, a2.Kind);
        Assert.Equal("_actor", a2.Source.Id);
        Assert.Equal("_uc1", a2.Target.Id);
    }

    [Fact]
    public void Diagrams_Keep_Type_Bounds_Nesting_And_Waypoints()
    {
        var (m, report) = ImportFixture();
        var bdd = (Diagram)ById(m, "_bdd");
        Assert.Equal(DiagramType.BlockDefinition, bdd.DiagramType);
        Assert.Equal("_pkgS", bdd.Owner!.Id);
        Assert.Equal(new Bounds(100, 80, 220, 160), bdd.NodeFor(ById(m, "_drone"))!.Bounds);
        Assert.Equal(3, bdd.Nodes.Count);                                       // frame and the dangling text box are not shapes
        var comp = bdd.EdgeFor((Relationship)ById(m, "_assoc1"))!;
        Assert.Same(bdd.NodeFor(ById(m, "_drone")), comp.Source);              // drawn rotor→drone in MagicDraw, reversed
        Assert.Equal([new Pt(250, 240), new Pt(250, 360)], comp.Waypoints);
        Assert.NotNull(bdd.EdgeFor((Relationship)ById(m, "_gen1")));

        var ibd = (Diagram)ById(m, "_ibd");
        Assert.Equal(DiagramType.InternalBlock, ibd.DiagramType);
        Assert.Same(ById(m, "_drone"), ibd.Owner);
        var part = ibd.NodeFor(ById(m, "_p_rotors"))!;
        var shaft = ibd.NodeFor(ById(m, "_rp"))!;
        Assert.Equal(part.Id, shaft.ParentId);
        Assert.Null(ibd.NodeFor(ById(m, "_port1"))!.ParentId);
        Assert.DoesNotContain(ibd.Nodes, n => n.Element.Id == "_drone");       // the frame
        var connEdge = ibd.EdgeFor((Relationship)ById(m, "_conn1"))!;
        Assert.Same(shaft, connEdge.Source);
        Assert.Equal([new Pt(200, 208)], connEdge.Waypoints);
        Assert.Equal(ibd.NodeFor(ById(m, "_p_eq"))!.Id, ibd.NodeFor(ById(m, "_cb_m"))!.ParentId);
        Assert.NotNull(ibd.EdgeFor((Relationship)ById(m, "_bind1")));

        var act = (Diagram)ById(m, "_actd");
        Assert.Equal(DiagramType.Activity, act.DiagramType);
        var lane = act.NodeFor(ById(m, "_part1"))!;
        Assert.Equal(lane.Id, act.NodeFor(ById(m, "_a1"))!.ParentId);         // swimlane contents
        Assert.Equal(act.NodeFor(ById(m, "_a1"))!.Id, act.NodeFor(ById(m, "_pin1"))!.ParentId);
        Assert.Equal(2, act.Edges.Count);

        Assert.Equal(DiagramType.StateMachine, ((Diagram)ById(m, "_stmd")).DiagramType);
        Assert.Single(((Diagram)ById(m, "_stmd")).Edges);

        var seq = (Diagram)ById(m, "_seqd");
        Assert.Equal(DiagramType.Sequence, seq.DiagramType);
        var m1 = seq.EdgeFor((Relationship)ById(m, "_m1"))!;
        Assert.Equal("_ll2", m1.Source.Element.Id);                             // drawn from the activation bar on ll2
        Assert.Equal(130, Notation.MessageY(m1));
        Assert.Equal(250, Notation.MessageY(seq.EdgeFor((Relationship)ById(m, "_m2"))!));

        Assert.Equal(DiagramType.Requirement, ((Diagram)ById(m, "_reqd")).DiagramType);
        Assert.Single(((Diagram)ById(m, "_reqd")).Edges);

        Assert.Null(m.Find("_tbl"));
        Assert.Equal(1, report.Skipped["Diagrams of type 'Generic Table'"]);

        // No readable symbols: contents inferred from the owner and auto-laid out, with a warning.
        var uc = (Diagram)ById(m, "_ucd");
        Assert.Equal(DiagramType.UseCase, uc.DiagramType);
        Assert.NotNull(uc.NodeFor(ById(m, "_actor")));
        Assert.NotNull(uc.EdgeFor((Relationship)ById(m, "_assoc2")));
        Assert.Contains(report.Warnings, w => w.Contains("Pilot use cases") && w.Contains("auto-laid out"));
        Assert.True(uc.Nodes.Select(n => (n.Bounds.X, n.Bounds.Y)).Distinct().Count() == uc.Nodes.Count);

        Assert.Equal(7, report.Diagrams);
        Assert.Equal(6, report.DiagramsWithLayout);
        Assert.Equal(1, report.DiagramsAutoLaidOut);
    }

    [Fact]
    public void Report_Counts_Kinds_And_Lists_Skipped_Constructs()
    {
        var (_, report) = ImportFixture();
        Assert.Equal(4, report.Imported["Block"]);
        Assert.Equal(2, report.Imported["Requirement"]);
        Assert.Equal(1, report.Imported["Satisfy"]);
        Assert.Equal(1, report.Imported["Composition"]);
        Assert.Equal(1, report.Skipped["Instance specifications"]);
        Assert.Equal(1, report.Skipped["uml:Frobnicator"]);
        Assert.True(report.Skipped["Project resources (options, proxies, used-project backups)"] >= 3);
        Assert.Contains(report.Warnings, w => w.Contains("choice"));
        var lines = report.Lines("drone.mdzip").ToList();
        Assert.StartsWith("Imported MagicDraw/Cameo project drone.mdzip", lines[0]);
        Assert.Contains(lines, l => l.Contains("Proxy port: 2"));
        Assert.Contains(lines, l => l.Contains("Skipped"));
        Assert.Equal(report.Elements, report.ToImportReport().Elements);
    }

    [Fact]
    public void Mdxml_With_Inline_Diagram_Contents_Imports_The_Same_Model()
    {
        var path = Path.Combine(_dir.FullName, "drone.mdxml");
        File.WriteAllText(path, MagicDrawFixtures.Xmi(inline: true));
        var (m, report) = MagicDrawImporter.Import(path);
        var (z, _) = ImportFixture();
        Assert.Equal(z.AllElements.Select(e => e.Id).Order(), m.AllElements.Select(e => e.Id).Order());
        Assert.Equal(new Bounds(100, 80, 220, 160), ((Diagram)ById(m, "_bdd")).NodeFor(ById(m, "_drone"))!.Bounds);
        Assert.Equal(6, report.DiagramsWithLayout);
    }

    [Fact]
    public void Reimport_Updates_By_Id_Instead_Of_Duplicating()
    {
        var path = MagicDrawFixtures.WriteMdzip(_dir.FullName);
        var (model, _) = MagicDrawImporter.Import(path);
        var count = model.AllElements.Count();

        var again = MagicDrawImporter.ImportInto(model, path);
        Assert.Equal(count, model.AllElements.Count());
        Assert.Equal(0, again.Added);
        Assert.Equal(0, again.Updated - again.Diagrams);                          // only diagrams are replaced
        Assert.True(again.Unchanged > 50);

        var changed = MagicDrawFixtures.WriteMdzip(_dir.FullName, "drone2.mdzip",
            MagicDrawFixtures.Xmi(inline: false, droneName: "Quadcopter",
                extraBlocks: "<packagedElement xmi:type='uml:Class' xmi:id='_battery' name='Battery'/>"));
        var r = MagicDrawImporter.ImportInto(model, changed);
        Assert.Equal(count + 1, model.AllElements.Count());
        Assert.Equal(1, r.Added);
        Assert.Equal("Quadcopter", model.Find("_drone")!.Name);
        Assert.Single(model.AllElements, e => e.Name == "Quadcopter");
        Assert.Equal(ElementKind.Class, model.Find("_battery")!.Kind);
        Assert.Same(model.Find("_drone"), ((Diagram)model.Find("_bdd")!).NodeFor(model.Find("_drone")!)!.Element);

        model.History.Undo();                                                   // a re-import is one undo step
        Assert.Equal("Drone", model.Find("_drone")!.Name);
        Assert.Null(model.Find("_battery"));
    }

    [Fact]
    public void Corrupt_And_Unknown_Input_Is_Reported_Not_Fatal()
    {
        var garbage = Path.Combine(_dir.FullName, "garbage.mdzip");
        File.WriteAllBytes(garbage, [1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Throws<FormatException>(() => MagicDrawImporter.Import(garbage));

        var noModel = Path.Combine(_dir.FullName, "empty.mdzip");
        using (var z = ZipFile.Open(noModel, ZipArchiveMode.Create)) z.CreateEntry("records.properties");
        Assert.Throws<FormatException>(() => MagicDrawImporter.Import(noModel));

        var xmi = MagicDrawFixtures.Xmi(inline: false);
        var truncated = MagicDrawFixtures.WriteMdzip(_dir.FullName, "truncated.mdzip", xmi[..(xmi.Length / 2)]);
        Assert.Throws<FormatException>(() => MagicDrawImporter.Import(truncated));

        var notZip = Path.Combine(_dir.FullName, "plain.mdzip");
        File.WriteAllText(notZip, xmi);
        Assert.Throws<FormatException>(() => MagicDrawImporter.Import(notZip));

        // Unreadable diagram resources: the model still imports, diagrams fall back to auto layout.
        var bins = MagicDrawFixtures.DefaultBinaries.ToDictionary(kv => kv.Key, kv => kv.Value);
        bins["BINARY-bdd"] = "\u0001\u0002 not xml at all";
        bins["BINARY-ibd"] = "<?xml version='1.0'?><unexpected><stuff/></unexpected>";
        var (m, report) = MagicDrawImporter.Import(MagicDrawFixtures.WriteMdzip(_dir.FullName, "badbin.mdzip", binaries: bins));
        Assert.Equal(ElementKind.Block, m.Find("_drone")!.Kind);
        var bdd = (Diagram)m.Find("_bdd")!;
        Assert.NotEmpty(bdd.Nodes);
        Assert.Contains(report.Warnings, w => w.Contains("Drone structure") && w.Contains("not in a readable format"));
        Assert.Contains(report.Warnings, w => w.Contains("Drone internals") && w.Contains("auto-laid out"));

        // Only MagicDraw-specific noise and unknown metaclasses: nothing crashes.
        var odd = MagicDrawFixtures.WriteMdzip(_dir.FullName, "odd.mdzip", """
            <?xml version='1.0' encoding='UTF-8'?>
            <xmi:XMI xmlns:uml='http://www.omg.org/spec/UML/20131001' xmlns:xmi='http://www.omg.org/spec/XMI/20131001' xmlns:Foo='urn:foo'>
              <uml:Model xmi:type='uml:Model' xmi:id='_m' name='Odd'>
                <packagedElement xmi:type='uml:Class' xmi:id='_c' name='C'>
                  <ownedAttribute xmi:type='uml:Property' xmi:id='_p' name='p' type='_nowhere'/>
                  <somethingNew xmi:type='uml:Gizmo' xmi:id='_g'/>
                  <generalization xmi:type='uml:Generalization' xmi:id='_gg' general='_missing'/>
                </packagedElement>
                <packagedElement xmi:type='uml:Class' xmi:id='_c' name='Duplicate id'/>
                <packagedElement xmi:type='uml:Association' xmi:id='_a' memberEnd='_x _y'/>
                <packagedElement xmi:type='uml:Abstraction' xmi:id='_s' client='_c'/>
              </uml:Model>
              <Foo:Bar xmi:id='_fb' base_Class='_c' tag='v'/>
            </xmi:XMI>
            """, new Dictionary<string, string>());
        var (om, orep) = MagicDrawImporter.Import(odd);
        Assert.Equal("Odd", om.Root.Name);
        Assert.Equal(2, om.OfKind(ElementKind.Class).Count());
        Assert.Contains(orep.Warnings, w => w.Contains("Duplicate id"));
        Assert.Equal(1, orep.Skipped["uml:Gizmo"]);
        Assert.Contains("Bar", om.Find("_c")!.Stereotypes);
    }

    [Fact]
    public void Large_Project_Streams_In_Reasonable_Time()
    {
        var path = MagicDrawFixtures.WriteLargeMdzip(_dir.FullName, packages: 20, blocksPerPackage: 500);
        var sw = Stopwatch.StartNew();
        var (m, report) = MagicDrawImporter.Import(path);
        sw.Stop();
        Assert.Equal(10_000, report.Imported["Block"]);
        Assert.Equal(10_000, report.Imported["Reference property"]);
        Assert.Equal(10_000, report.Imported["Value property"]);
        Assert.Equal(20 * 499, m.Relationships.Count(r => r.Kind == ElementKind.Generalization));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"took {sw.Elapsed}");
    }
}
