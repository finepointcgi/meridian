using System.Text.RegularExpressions;
using Meridian.Core.Model;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>Realistic SysML v2 files (SysMLv2Corpus/*.sysml, written for these tests in the style of the public SysML v2 training
/// examples): each parses without errors or warnings, applies to a model, round-trips stably and keeps all of its content.</summary>
public partial class SysMLv2CorpusTests
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "SysMLv2Corpus");

    public static IEnumerable<object[]> Files =>
        Directory.GetFiles(Dir, "*.sysml").Order(StringComparer.Ordinal).Select(f => new object[] { Path.GetFileName(f) });

    private static string Problems(IEnumerable<SysmlDiagnostic> d) => string.Join("\n", d.Where(x => x.Severity != SysmlSeverity.Info));

    [GeneratedRegex(@"'(?:[^'\\]|\\.)*'|[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Word();

    /// <summary>Names and keywords of the text outside comments (comment bodies are compared separately).</summary>
    private static HashSet<string> Words(string text)
    {
        var code = Regex.Replace(text, @"//[^\n]*|/\*[\s\S]*?\*/|""(?:[^""\\]|\\.)*""", " ");
        return Word().Matches(code).Select(m => m.Value.Trim('\'')).ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void Corpus_Is_Present()
    {
        Assert.True(Files.Count() >= 10);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Corpus_File_Parses_Applies_And_Round_Trips(string file)
    {
        var text = File.ReadAllText(Path.Combine(Dir, file));

        // 1) no errors or warnings
        var doc = SysMLv2Parser.Parse(text);
        Assert.True(doc.Diagnostics.All(d => d.Severity == SysmlSeverity.Info), Problems(doc.Diagnostics));

        // 2) applies to a model without warnings
        var model = new ModelRepository("Corpus");
        var result = SysMLv2Sync.Apply(model, model.Root, doc);
        Assert.True(result.Applied);
        Assert.True(result.Diagnostics.All(d => d.Severity == SysmlSeverity.Info), Problems(result.Diagnostics));
        Assert.True(result.ElementsCreated > 3);

        // 3) export → parse → export is stable, and the export itself is clean SysML v2
        var exported = SysMLv2Text.Export(model);
        var reparsed = SysMLv2Parser.Parse(exported);
        Assert.True(reparsed.Diagnostics.All(d => d.Severity == SysmlSeverity.Info), Problems(reparsed.Diagnostics) + "\n" + exported);
        var (again, report) = SysMLv2Text.Import(exported);
        Assert.True(report.Warnings.Count == 0, string.Join("\n", report.Warnings) + "\n" + exported);
        Assert.Equal(exported, SysMLv2Text.Export(again));

        // 4) applying the export back to the same model changes nothing
        var noop = SysMLv2Sync.Apply(model, model.Root, reparsed);
        Assert.True(noop.Changes.Count == 0, string.Join("\n", noop.Changes.Select(c => c.Description)));

        // 5) nothing is lost: every name and keyword survives (apart from long forms the exporter writes as symbols)
        var aliases = new HashSet<string> { "specializes", "subsets", "redefines", "references", "defined", "typed", "by", "default" };
        var missing = Words(text).Except(Words(exported)).Where(w => !aliases.Contains(w)).ToList();
        Assert.True(missing.Count == 0, "Lost: " + string.Join(", ", missing) + "\n" + exported);
        var comments = Regex.Matches(text, @"/\*([\s\S]*?)\*/").Select(m => m.Groups[1].Value.Trim()).ToList();
        foreach (var c in comments) Assert.Contains(c, exported);
    }

    [Fact]
    public void Constructs_Map_Onto_Meridian_Elements()
    {
        var (m, _) = Load("06_Calculations.sysml", "03_Actions.sysml", "05_Requirements.sysml", "01_VehicleParts.sysml", "08_ViewsAndMetadata.sysml",
                          "07_Cases.sysml", "02_Interfaces.sysml");

        // calcs and constraints → constraint blocks with directed parameters and the result expression
        var power = m.OfKind(ElementKind.ConstraintBlock).Single(c => c.Name == "Power");
        Assert.Equal("in", power.OwnedElements.Single(p => p.Name == "whlpwr").Get(FieldKeys.Direction));
        Assert.Equal("whlpwr - drag - friction", power.Get(FieldKeys.Body));
        Assert.Contains("return : PowerValue;", power.Get(SysMLv2Text.UnmappedField));
        Assert.Contains(m.OfKind(ElementKind.ConstraintBlock), c => c.Name == "MassLimit" && c.Get(FieldKeys.Body) == "total <= limit");

        // cases → test cases / use cases; verify relationships
        var test = m.OfKind(ElementKind.TestCase).Single(t => t.Name == "VehicleMassTest");
        Assert.Single(m.Outgoing(test, ElementKind.Verify));
        Assert.Contains(m.OfKind(ElementKind.TestCase), t => t.Name == "FuelEconomyAnalysis");
        var transport = m.OfKind(ElementKind.UseCase).Single(u => u.Name == "ProvideTransportation");
        Assert.Single(m.Outgoing(transport, ElementKind.Include));

        // enumerations with literals and values
        var level = m.OfKind(ElementKind.Enumeration).Single(e => e.Name == "Level");
        Assert.Equal(["low", "medium", "high"], level.OwnedElements.Select(l => l.Name).ToArray());

        // metadata → stereotypes and tagged values
        var vehicle = m.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle" && b.Owner!.Name == "VehicleModel");
        Assert.Contains("Safety", vehicle.Stereotypes);
        Assert.Equal("true", vehicle.Get(FieldKeys.TagPrefix + "isMandatory"));
        Assert.Contains("Security", m.OfKind(ElementKind.Property).Single(p => p.Name == "alarm").Stereotypes);

        // port and feature directions
        var fuelPort = m.OfKind(ElementKind.InterfaceBlock).Single(p => p.Name == "FuelOutPort");
        Assert.Equal("out", fuelPort.OwnedElements.Single(p => p.Name == "fuelSupply").Get(FieldKeys.Direction));

        // perform → allocation of the behavior to the part; satisfy; item flows
        var actionsVehicle = m.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle" && b.Owner!.Name == "VehicleActions");
        Assert.Equal(3, m.Incoming(actionsVehicle, ElementKind.Allocate).Count());
        Assert.NotEmpty(m.Relationships.Where(r => r.Kind == ElementKind.Satisfy));
        var flow = m.Relationships.Single(r => r.Kind == ElementKind.Connector && r.Name == "electricity");
        Assert.Equal("ElectricalPower", m.Find(flow.Get(FieldKeys.ItemFlow))!.Name);

        // action-body statements are action nodes in the flow
        var operate = m.OfKind(ElementKind.Activity).Single(a => a.Name == "Operate");
        Assert.True(operate.OwnedElements.Count(e => e.Kind == ElementKind.Action) >= 8);
        Assert.True(operate.OwnedElements.Count(e => e.Kind == ElementKind.ControlFlow) >= 8);

        // imports are kept on their package and come back on export
        var parts = m.Root.OwnedElements.Single(e => e.Name == "VehicleParts");
        Assert.Contains("private import ISQ::*;", parts.Get(SysMLv2Text.UnmappedHeadField));
    }

    private static (ModelRepository Model, string Text) Load(params string[] files)
    {
        var text = "package Corpus {\n" + string.Join("\n", files.Select(f => File.ReadAllText(Path.Combine(Dir, f)))) + "\n}\n";
        var (m, report) = SysMLv2Text.Import(text);
        Assert.True(report.Warnings.Count == 0, string.Join("\n", report.Warnings));
        return (m, text);
    }

    [Fact]
    public void Standard_Library_Resolves_Names_And_Units()
    {
        var lib = SysMLv2Library.Standard;
        Assert.Empty(lib.Diagnostics);
        Assert.NotNull(lib.Find("ScalarValues::Real"));
        Assert.NotNull(lib.Find("ISQ::mass"));
        Assert.NotNull(lib.Find("ISQ::MassValue"));
        Assert.NotNull(lib.Find("SI::kg"));
        Assert.True(lib.Find("SI::kilogram")!.IsUnit);
        Assert.Contains("Collections", lib.Packages);

        var ok = SysMLv2Parser.Parse("package P { attribute m = 12 [kg]; attribute v = (3 + 4) [m/s]; attribute t = 2 ['°C']; }");
        Assert.Empty(ok.Diagnostics);
        var bad = SysMLv2Parser.Parse("package P { attribute m = 12 [kgg]; part w[2]; }");
        var warning = Assert.Single(bad.Diagnostics);
        Assert.Equal(SysmlSeverity.Warning, warning.Severity);
        Assert.Contains("kgg", warning.Message);

        // library references resolve without warnings; unknown ones are reported
        var (m, report) = SysMLv2Text.Import("""
            package P {
                import ISQ::*;
                import Nowhere::*;
                part def A :> Parts::Part { attribute mass : ISQ::MassValue; attribute x : MassValue; attribute y : Missing; }
            }
            """);
        Assert.Equal(2, report.Warnings.Count);
        Assert.Contains(report.Warnings, w => w.Contains("Nowhere"));
        Assert.Contains(report.Warnings, w => w.Contains("Missing"));
        Assert.Equal("ISQ::MassValue", m.OfKind(ElementKind.Property).Single(p => p.Name == "mass").TypeRef);
    }

    [Fact]
    public void Text_Only_Syntax_Survives_Editing_The_Model()
    {
        const string src = """
            package P {
                private import ScalarValues::*;
                alias R for ScalarValues::Real;
                part def A {
                    attribute x : Real;
                    assert constraint { x > 0 }
                }
            }
            """;
        var (m, report) = SysMLv2Text.Import(src);
        Assert.Empty(report.Warnings);
        m.Rename(m.OfKind(ElementKind.Block).Single(), "B");
        var text = SysMLv2Text.Export(m);
        Assert.Contains("private import ScalarValues::*;", text);
        Assert.Contains("alias R for ScalarValues::Real;", text);
        Assert.Contains("part def B {", text);
        Assert.Contains("assert constraint { x > 0 }", text);
    }
}
