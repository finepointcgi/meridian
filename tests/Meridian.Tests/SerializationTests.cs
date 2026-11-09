using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;

namespace Meridian.Tests;

public class SerializationTests
{
    private static string Signature(ModelRepository m) =>
        string.Join("\n", m.AllElements.Where(e => e.Kind != ElementKind.Diagram)
            .Select(e => $"{e.Kind}|{e.QualifiedName}|{string.Join(";", e.Fields.Where(f => f.Key != FieldKeys.Type).OrderBy(f => f.Key).Select(f => f.Key + "=" + f.Value))}" +
                         (e is Relationship r ? $"|{r.Source.QualifiedName}->{r.Target.QualifiedName}" : "") +
                         (Meridian.Core.Language.Metamodel.IsTypedFeature(e.Kind) ? $"|type={m.TypeDisplayName(e)}" : ""))
            .OrderBy(s => s, StringComparer.Ordinal));

    [Fact]
    public void Project_File_Round_Trips_Everything()
    {
        var m = SampleModels.Vehicle();
        var json = ProjectFile.Serialize(m);
        var back = ProjectFile.Deserialize(json);
        Assert.Equal(Signature(m), Signature(back));
        Assert.Equal(m.Diagrams.Sum(d => d.Nodes.Count), back.Diagrams.Sum(d => d.Nodes.Count));
        Assert.Equal(m.Diagrams.Sum(d => d.Edges.Count), back.Diagrams.Sum(d => d.Edges.Count));
        Assert.Equal(json, ProjectFile.Serialize(back));
    }

    [Fact]
    public void Xmi_Round_Trips_Semantics()
    {
        var m = SampleModels.Vehicle();
        var xml = XmiExchange.ToXml(m);
        var (back, report) = XmiExchange.Import(xml);
        Assert.Empty(report.Warnings);
        Assert.Equal(Signature(m), Signature(back));
    }

    [Fact]
    public void Xmi_Import_Reads_Foreign_Profile_Prefixes()
    {
        // Shape of an Enterprise Architect / Cameo export: different namespace versions and profile prefixes.
        const string xmi = """
            <xmi:XMI xmlns:xmi="http://www.omg.org/spec/XMI/20131001" xmlns:uml="http://www.omg.org/spec/UML/20131001"
                     xmlns:SysML1.4="http://www.omg.org/spec/SysML/20150709/SysML">
              <uml:Model xmi:type="uml:Model" xmi:id="m1" name="EA Model">
                <packagedElement xmi:type="uml:Package" xmi:id="EAPK_1" name="Pkg">
                  <packagedElement xmi:type="uml:Class" xmi:id="EAID_B" name="Pump">
                    <ownedAttribute xmi:type="uml:Property" xmi:id="EAID_P" name="flow">
                      <type href="http://www.omg.org/spec/UML/20131001/PrimitiveTypes.xmi#Real"/>
                      <lowerValue xmi:type="uml:LiteralInteger" xmi:id="lv" value="0"/>
                      <upperValue xmi:type="uml:LiteralUnlimitedNatural" xmi:id="uv" value="1"/>
                    </ownedAttribute>
                  </packagedElement>
                  <packagedElement xmi:type="uml:Class" xmi:id="EAID_R" name="Flow Rate"/>
                  <packagedElement xmi:type="uml:Abstraction" xmi:id="EAID_S" client="EAID_B" supplier="EAID_R"/>
                </packagedElement>
              </uml:Model>
              <SysML1.4:Block xmi:id="s1" base_Class="EAID_B"/>
              <SysML1.4:Requirement xmi:id="s2" base_Class="EAID_R" id="SYS-9" text="Pump shall deliver 10 l/min"/>
              <SysML1.4:Satisfy xmi:id="s3" base_Abstraction="EAID_S"/>
            </xmi:XMI>
            """;
        var (m, report) = XmiExchange.Import(System.Xml.Linq.XDocument.Parse(xmi));
        Assert.Empty(report.Warnings);
        var pump = m.Find("EAID_B")!;
        Assert.Equal(ElementKind.Block, pump.Kind);
        var req = m.Find("EAID_R")!;
        Assert.Equal(ElementKind.Requirement, req.Kind);
        Assert.Equal("SYS-9", req.Get(FieldKeys.RequirementId));
        Assert.Single(m.Incoming(req, ElementKind.Satisfy));
        var flow = m.Find("EAID_P")!;
        Assert.Equal("Real", flow.TypeRef);
        Assert.Equal("0..1", flow.Multiplicity);
    }

    [Fact]
    public void SysMLv2_Text_Round_Trips_Core_Structure()
    {
        var m = SampleModels.Vehicle();
        var text = SysMLv2Text.Export(m);
        Assert.Contains("part def Vehicle", text);
        Assert.Contains("requirement def <'REQ-002'> Acceleration", text);
        Assert.Contains("satisfy Acceleration by Vehicle;", text);

        var (back, report) = SysMLv2Text.Import(text);
        Assert.True(report.Warnings.Count == 0, string.Join("\n", report.Warnings));
        Assert.Equal("Vehicle System", back.Root.Name);
        var vehicle = back.AllElements.Single(e => e.Name == "Vehicle" && e.Kind == ElementKind.Block);
        var engineUsage = vehicle.OwnedElements.Single(e => e.Name == "engine");
        Assert.Equal("Engine", back.TypeDisplayName(engineUsage));
        var accel = back.AllElements.Single(e => e.Name == "Acceleration");
        Assert.Equal("REQ-002", accel.Get(FieldKeys.RequirementId));
        Assert.StartsWith("The vehicle shall accelerate", accel.Get(FieldKeys.Text));
        Assert.Equal(2, back.Incoming(accel, ElementKind.Satisfy).Count());   // Vehicle and the accel constraint property
        Assert.Single(back.Incoming(accel, ElementKind.Verify));
        var sm = back.AllElements.Single(e => e.Kind == ElementKind.StateMachine);
        Assert.Equal(5, sm.OwnedElements.Count(e => e.Kind == ElementKind.Transition));
        var act = back.AllElements.Single(e => e.Kind == ElementKind.Activity);
        Assert.Equal(6, act.OwnedElements.Count(e => e.Kind == ElementKind.ControlFlow));

        // Exporting the re-imported model is stable.
        var (again, _) = SysMLv2Text.Import(SysMLv2Text.Export(back));
        Assert.Equal(back.AllElements.Count(), again.AllElements.Count());
    }

    [Fact]
    public void SysMLv2_Parser_Handles_Handwritten_Text()
    {
        const string src = """
            package Drone {
                // a comment
                part def Frame;
                attribute def Mass;
                part def Drone :> Frame {
                    doc /* A quadcopter. */
                    attribute weight : Mass;
                    part rotors : Rotor[4];
                    in port power : PowerPort;
                }
                part def Rotor;
                port def PowerPort;
                requirement def <'D-1'> Endurance {
                    doc /* Flight time shall exceed 30 min. */
                }
                satisfy Endurance by Drone;
                unknownThing x y z;
            }
            """;
        var (m, report) = SysMLv2Text.Import(src);
        Assert.Single(report.Warnings); // the unknown statement is reported and skipped
        var drone = m.AllElements.Single(e => e.Name == "Drone" && e.Kind == ElementKind.Block);
        Assert.Equal("A quadcopter.", drone.Documentation);
        Assert.Equal("4", drone.OwnedElements.Single(e => e.Name == "rotors").Multiplicity);
        Assert.Equal("in", drone.OwnedElements.Single(e => e.Name == "power").Get(FieldKeys.Direction));
        Assert.Single(m.General(drone));
        Assert.Single(m.Relationships, r => r.Kind == ElementKind.Satisfy);
    }

    [Fact]
    public void Requirements_Csv_Import_Upserts_And_Nests()
    {
        var m = new ModelRepository();
        var pkg = m.Create(m.Root, ElementKind.Package, "Reqs");
        const string csv = "Id,Name,Text,Parent Id\nR-1,Top,\"Top level, with comma\",\nR-2,Child,\"Says \"\"hi\"\"\",R-1\n";
        var (created, updated) = RequirementsCsv.Import(m, pkg, csv);
        Assert.Equal((2, 0), (created, updated));
        var child = m.OfKind(ElementKind.Requirement).Single(r => r.Name == "Child");
        Assert.Equal("Top", child.Owner!.Name);
        Assert.Equal("Says \"hi\"", child.Get(FieldKeys.Text));

        (created, updated) = RequirementsCsv.Import(m, pkg, "Id,Text\nR-1,Changed\n");
        Assert.Equal((0, 1), (created, updated));
        Assert.Equal("Changed", m.OfKind(ElementKind.Requirement).Single(r => r.Name == "Top").Get(FieldKeys.Text));

        var export = RequirementsCsv.Export(m);
        Assert.Contains("R-2,Child,\"Says \"\"hi\"\"\",R-1", export);

        m.History.Undo();
        m.History.Undo();
        Assert.Empty(m.OfKind(ElementKind.Requirement));
    }

    [Fact]
    public void Report_And_Svg_Render()
    {
        var m = SampleModels.Vehicle();
        var html = HtmlReport.Generate(m);
        Assert.Contains("<svg", html);
        Assert.Contains("REQ-002", html);
        foreach (var d in m.Diagrams)
        {
            var svg = SvgRenderer.Render(m, d);
            System.Xml.Linq.XDocument.Parse(svg); // well-formed
        }
    }
}
