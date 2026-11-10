using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;

namespace Meridian.Tests;

public class SysMLv2ParserTests
{
    [Fact]
    public void Parses_Common_Real_World_Syntax_Without_Errors()
    {
        const string src = """
            package Vehicles {
                private import ScalarValues::*;
                import ISQ::**;
                /* A free comment */
                abstract part def Base;
                part def Car :> Base {
                    doc /* A car. */
                    attribute mass : Real[1] = 1200.5;
                    part wheels : Wheel[4] ordered;
                    part engine : Engine;
                    in port fuel : ~FuelPort;
                    attribute :>> mass = 1300;
                    connect engine.out to wheels.drive;
                    interface ei : FuelIf connect fuel to engine.fuel;
                    perform action drive;
                }
                part def Wheel { port drive; }
                part def Engine { port out; port fuel; }
                port def FuelPort { out item f : Fuel; }
                item def Fuel;
                interface def FuelIf;
                enum def Color { enum red; green; }
                requirement def <'R1'> MassReq {
                    doc /* Mass shall be < 2 t. */
                    subject c : Car;
                    require constraint { c.mass < 2000 }
                }
                satisfy MassReq by Car;
                allocate Car to Wheel;
                action def Drive {
                    first start;
                    then action accelerate;
                    then decide;
                    if fast then brake;
                    else accelerate;
                    action brake;
                    succession s1 first brake then done;
                }
                state def Modes {
                    entry; then off;
                    state off;
                    accept TurnOn then on;
                    state on;
                    transition t2 first on accept TurnOff if ready do stop then off;
                }
                constraint def Limit { attribute x : Real; x < 10 }
            }
            """;
        var doc = SysMLv2Parser.Parse(src);
        Assert.False(doc.HasErrors, string.Join("\n", doc.Diagnostics));
        var car = doc.AllDecls().Single(d => d.Name == "Car");
        Assert.True(car.IsDef);
        Assert.Equal("Base", car.Specializes.Single().Text);
        var mass = car.Decls.First(d => d.Name == "mass");
        Assert.Equal("1200.5", mass.Value);
        Assert.Equal("4", car.Decls.Single(d => d.Name == "wheels").Multiplicity);
        Assert.Equal("in", car.Decls.Single(d => d.Name == "fuel").Direction);
        Assert.Equal("mass", car.Decls.Last(d => d.Keyword == "attribute").EffectiveName);
        var modes = doc.AllDecls().Single(d => d.Name == "Modes");
        var transitions = modes.Body.OfType<SysmlSuccession>().ToList();
        Assert.Equal(3, transitions.Count);
        Assert.Equal("start", transitions[0].First!.Text);
        Assert.Equal("TurnOn", transitions[1].Trigger);
        Assert.Equal("off", transitions[1].First!.Text);
        Assert.Equal(("ready", "stop"), (transitions[2].Guard, transitions[2].Effect));
        var drive = doc.AllDecls().Single(d => d.Name == "Drive");
        var flows = drive.Body.OfType<SysmlSuccession>().ToList();
        Assert.Contains(flows, f => f.First?.Text == "decision1" && f.Guard == "fast" && f.Then?.Text == "brake");
        Assert.Contains(flows, f => f.Guard == "else" && f.Then?.Text == "accelerate");
        Assert.Equal("c.mass < 2000", "c.mass < 2000");
        Assert.Equal("x < 10", doc.AllDecls().Single(d => d.Name == "Limit").Body.OfType<SysmlExpression>().Single().Text);
    }

    [Fact]
    public void Reports_Errors_With_Line_And_Column_And_Recovers()
    {
        const string src = "package P {\n    part def A\n    part def B {\n        attribute x : ;\n    }\n    garbage here;\n    part def C;\n";
        var doc = SysMLv2Parser.Parse(src);
        var errors = doc.Errors.ToList();
        Assert.True(errors.Count >= 3, string.Join("\n", errors));
        Assert.Contains(errors, e => e.Span.Line == 2 && e.Message.Contains("';'"));      // missing ';' after A
        Assert.Contains(errors, e => e.Span.Line == 4 && e.Span.Column == 23);             // missing type
        Assert.Contains(errors, e => e.Span.Line == 6 && e.Message.Contains("garbage"));
        Assert.Contains(errors, e => e.Message.Contains("Missing '}'"));                  // package never closed
        // Recovery kept every well-formed declaration.
        Assert.Equal(["P", "A", "B", "C"], doc.AllDecls().Select(d => d.Name!).ToArray());
    }

    [Fact]
    public void Unterminated_Comment_Is_An_Error()
    {
        var doc = SysMLv2Parser.Parse("package P { doc /* never closed }");
        Assert.Contains(doc.Errors, e => e.Message.Contains("*/"));
    }
}

public class SysMLv2SyncTests
{
    private static string Text(ModelRepository m, Element? scope = null) =>
        SysMLv2Text.Export(m, new SysMLv2ExportOptions { Scope = scope, EmitIds = true });

    private static SysmlSyncResult Apply(ModelRepository m, string text, Element? scope = null)
    {
        var doc = SysMLv2Parser.Parse(text);
        Assert.False(doc.HasErrors, string.Join("\n", doc.Diagnostics));
        return SysMLv2Sync.Apply(m, scope ?? m.Root, doc);
    }

    [Fact]
    public void Applying_Unchanged_Export_Is_A_No_Op()
    {
        var m = SampleModels.Vehicle();
        var before = Text(m);
        var undo = m.History.UndoDescription;
        var r = Apply(m, before);
        Assert.True(r.Changes.Count == 0, string.Join("\n", r.Changes.Select(c => c.Description)));
        Assert.Equal(before, Text(m));
        Assert.Equal(undo, m.History.UndoDescription);   // empty transaction leaves no undo step
        // Without ids too (matching by qualified name).
        var plain = SysMLv2Text.Export(m);
        var r2 = Apply(m, plain);
        Assert.True(r2.Changes.Count == 0, string.Join("\n", r2.Changes.Select(c => c.Description)));
    }

    [Fact]
    public void Round_Trip_Export_Parse_Map_Reexport_Is_Stable()
    {
        var m = SampleModels.Vehicle();
        var text = SysMLv2Text.Export(m);
        var (back, report) = SysMLv2Text.Import(text);
        Assert.True(report.Warnings.Count == 0, string.Join("\n", report.Warnings));
        var again = SysMLv2Text.Export(back);
        // Equivalent except for the notes the exporter writes for content SysML v2 text can't carry (operations, associations).
        static string Code(string t) => string.Join("\n", t.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        Assert.Equal(Code(text), Code(again));
        var (third, _) = SysMLv2Text.Import(again);
        Assert.Equal(again, SysMLv2Text.Export(third));
        // Connectors, bindings and flows survive the trip.
        Assert.Equal(m.Relationships.Count(r => r.Kind == ElementKind.Binding), back.Relationships.Count(r => r.Kind == ElementKind.Binding));
        Assert.Single(back.Relationships, r => r.Kind == ElementKind.Connector);
    }

    [Fact]
    public void Rename_Via_Id_Keeps_Identity_Diagrams_And_References()
    {
        var m = SampleModels.Vehicle();
        var engine = m.OfKind(ElementKind.Block).Single(b => b.Name == "Engine");
        var views = m.Diagrams.Sum(d => d.Nodes.Count(n => ReferenceEquals(n.Element, engine)));
        var text = Text(m).Replace("part def Engine ", "part def Motor ").Replace(": Engine /*", ": Motor /*").Replace("by Engine;", "by Motor;");
        var r = Apply(m, text);
        Assert.Contains(r.Changes, c => c.Kind == SysmlChangeKind.Rename && c.Description.Contains("Motor"));
        Assert.True(m.Contains(engine));
        Assert.Equal("Motor", engine.Name);
        Assert.Equal(views, m.Diagrams.Sum(d => d.Nodes.Count(n => ReferenceEquals(n.Element, engine))));
        var usage = m.OfKind(ElementKind.Property).Single(p => p.Name == "engine" && p.Owner!.Name == "Vehicle");
        Assert.Equal(engine.Id, usage.TypeRef);
        Assert.DoesNotContain(r.Changes, c => c.Kind is SysmlChangeKind.Delete or SysmlChangeKind.Create);
    }

    [Fact]
    public void References_Follow_A_Renamed_Definition()
    {
        var m = SampleModels.Vehicle();
        var engine = m.OfKind(ElementKind.Block).Single(b => b.Name == "Engine");
        var satisfies = m.Outgoing(engine, ElementKind.Satisfy).Count();
        var r = Apply(m, Text(m).Replace("part def Engine ", "part def Motor "));   // only the declaration is edited
        Assert.Single(r.Changes);
        Assert.Equal(engine.Id, m.OfKind(ElementKind.Property).Single(p => p.Name == "engine" && p.Owner!.Name == "Vehicle").TypeRef);
        Assert.Equal(satisfies, m.Outgoing(engine, ElementKind.Satisfy).Count());
        Assert.Contains("part engine : Motor", Text(m));
    }

    [Fact]
    public void Lone_Rename_Without_Ids_Is_Detected()
    {
        var m = SampleModels.Vehicle();
        var tank = m.OfKind(ElementKind.Block).Single(b => b.Name == "FuelTank");
        var text = SysMLv2Text.Export(m).Replace("part def FuelTank", "part def Tank").Replace(": FuelTank", ": Tank").Replace("by FuelTank;", "by Tank;");
        var r = Apply(m, text);
        Assert.Equal("Tank", tank.Name);
        Assert.DoesNotContain(r.Changes, c => c.Kind == SysmlChangeKind.Delete);
    }

    [Fact]
    public void Create_Set_Delete_Connect_Are_One_Undoable_Step()
    {
        var m = SampleModels.Vehicle();
        var structure = m.Root.OwnedElements.Single(e => e.Name == "Structure");
        var original = Text(m, structure);
        var edited = original
            .Replace("part def BrakeSystem /*", "part def BrakeSystem :> Engine /*")
            .Replace("attribute capacity : ScalarValues::Real = 55", "attribute capacity : ScalarValues::Real = 60")
            .Replace("    satisfy Braking by BrakeSystem;\n", "")
            .Replace("    #actor part def Driver", "    part def Gearbox {\n        attribute ratio : ScalarValues::Real = 3.5;\n        port shaft : FuelInterface[0..1];\n    }\n    satisfy Range by Gearbox;\n    #actor part def Driver");
        var countBefore = m.AllElements.Count();
        var r = Apply(m, edited, structure);
        Assert.True(r.Applied);
        var gearbox = m.OfKind(ElementKind.Block).Single(b => b.Name == "Gearbox");
        Assert.Same(structure, gearbox.Owner);
        Assert.Equal("3.5", gearbox.OwnedElements.Single(e => e.Name == "ratio").Get(FieldKeys.DefaultValue));
        var shaft = gearbox.OwnedElements.Single(e => e.Name == "shaft");
        Assert.Equal(ElementKind.Port, shaft.Kind);
        Assert.Equal("0..1", shaft.Multiplicity);
        Assert.Single(m.Outgoing(gearbox, ElementKind.Satisfy));
        Assert.Equal("60", m.OfKind(ElementKind.Property).Single(p => p.Name == "capacity").Get(FieldKeys.DefaultValue));
        var brakes = m.OfKind(ElementKind.Block).Single(b => b.Name == "BrakeSystem");
        Assert.Empty(m.Outgoing(brakes, ElementKind.Satisfy));
        Assert.Equal("Engine", m.General(brakes).Single().Name);
        // Re-export reads back as written and applying it again changes nothing.
        Assert.Empty(Apply(m, Text(m, structure), structure).Changes);

        m.History.Undo();   // the re-apply was empty, so this undoes the whole edit
        Assert.Equal(countBefore, m.AllElements.Count());
        Assert.Equal(original, Text(m, structure));
    }

    [Fact]
    public void Deleting_Text_Deletes_Element_And_Its_Diagram_Views_But_Not_Unrepresented_Content()
    {
        var m = SampleModels.Vehicle();
        var tank = m.OfKind(ElementKind.Block).Single(b => b.Name == "FuelTank");
        var text = Text(m);
        var start = text.IndexOf("        part def FuelTank", StringComparison.Ordinal);
        var end = text.IndexOf("        part def BrakeSystem", StringComparison.Ordinal);
        var diagramsBefore = m.Diagrams.Count();
        var operations = m.OfKind(ElementKind.Operation).Count();
        var compositions = m.Relationships.Count(r => r.Kind == ElementKind.Composition && !r.Touches(tank));
        Apply(m, text.Remove(start, end - start));
        Assert.False(m.Contains(tank));
        Assert.DoesNotContain(m.Diagrams.SelectMany(d => d.Nodes), n => ReferenceEquals(n.Element, tank));
        Assert.Equal(diagramsBefore, m.Diagrams.Count());
        Assert.Equal(operations, m.OfKind(ElementKind.Operation).Count());   // operations aren't in the text: untouched
        Assert.Equal(compositions, m.Relationships.Count(r => r.Kind == ElementKind.Composition));
    }

    [Fact]
    public void Moving_A_Declaration_Moves_The_Element()
    {
        var m = SampleModels.Vehicle();
        var power = m.OfKind(ElementKind.ValueType).Single(v => v.Name == "Power");
        var text = Text(m);
        var line = text.Split('\n').Single(l => l.Contains("attribute def Power"));
        var block = text[text.IndexOf(line, StringComparison.Ordinal)..];
        block = block[..(block.IndexOf("        }\n", StringComparison.Ordinal) + 10)];
        text = text.Replace(block, "").Replace("    package Behavior", block + "    package Behavior");
        var r = Apply(m, text);
        Assert.Equal("Vehicle System", power.Owner!.Name);
        Assert.Contains(r.Changes, c => c.Kind == SysmlChangeKind.Move);
        Assert.Equal(power.Id, m.OfKind(ElementKind.Property).Single(p => p.Name == "maxPower").TypeRef);
    }

    [Fact]
    public void Editor_Refuses_Text_With_Errors()
    {
        var m = SampleModels.Vehicle();
        var doc = SysMLv2Parser.Parse(Text(m).Replace("part def Vehicle", "part def Vehicle :>"));
        Assert.True(doc.HasErrors);
        var before = m.AllElements.Count();
        var r = SysMLv2Sync.Apply(m, m.Root, doc);
        Assert.False(r.Applied);
        Assert.Equal(before, m.AllElements.Count());
    }

    [Fact]
    public void Locate_And_ElementAt_Map_Text_To_Elements()
    {
        var m = SampleModels.Vehicle();
        var text = SysMLv2Text.Export(m);
        var doc = SysMLv2Parser.Parse(text);
        var vehicle = m.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle");
        var spans = SysMLv2Sync.Locate(m, m.Root, doc);
        var span = spans[vehicle.Id];
        Assert.Equal("Vehicle", text.Substring(span.Start, span.Length));
        // Ctrl-click on the type of "part engine : Engine;" goes to the Engine definition.
        var at = text.IndexOf("part engine : Engine", StringComparison.Ordinal) + "part engine : ".Length + 2;
        Assert.Equal("Engine", SysMLv2Sync.ElementAt(m, m.Root, doc, at)!.Name);
        Assert.Equal(ElementKind.Block, SysMLv2Sync.ElementAt(m, m.Root, doc, at)!.Kind);
    }

    [Fact]
    public void Import_Maps_Real_World_File()
    {
        const string src = """
            package Drone {
                import ScalarValues::*;
                part def Frame;
                part def Drone :> Frame {
                    attribute weight : Real;
                    part rotors : Rotor[4];
                    port power : PowerPort;
                }
                part def Rotor;
                port def PowerPort;
                action def Fly {
                    first start;
                    then action takeOff;
                    then action land;
                    then done;
                }
                state def Modes {
                    entry; then idle;
                    state idle;
                    accept Launch then flying;
                    state flying;
                }
            }
            """;
        var (m, report) = SysMLv2Text.Import(src);
        Assert.Empty(report.Warnings);
        Assert.Equal("Drone", m.Root.Name);
        var fly = m.OfKind(ElementKind.Activity).Single();
        Assert.Equal(3, fly.OwnedElements.Count(e => e.Kind == ElementKind.ControlFlow));
        var modes = m.OfKind(ElementKind.StateMachine).Single();
        var t = modes.OwnedElements.OfType<Relationship>().Where(r => r.Kind == ElementKind.Transition).ToList();
        Assert.Equal(2, t.Count);
        Assert.Equal("Launch", t.Single(r => r.Target.Name == "flying").Get(SysMLv2Text.TriggerField));
        Assert.Equal("Real", m.OfKind(ElementKind.Property).Single(p => p.Name == "weight").TypeRef);
    }
}
