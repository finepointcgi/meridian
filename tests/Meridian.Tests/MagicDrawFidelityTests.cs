using System.Text;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>Fidelity accounting of the MagicDraw / Cameo importer: what the file held versus what the import kept.</summary>
public sealed class MagicDrawFidelityTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("meridian-md-fidelity-");

    public void Dispose()
    {
        try { _dir.Delete(true); } catch (IOException) { }
    }

    private const string Head = """
        <?xml version='1.0' encoding='UTF-8'?>
        <xmi:XMI xmlns:uml='http://www.omg.org/spec/UML/20131001' xmlns:xmi='http://www.omg.org/spec/XMI/20131001'
                 xmlns:sysml='http://www.omg.org/spec/SysML/20181001/SysML'
                 xmlns:MagicDraw_Profile='http://www.omg.org/spec/UML/20131001/MagicDrawProfile'
                 xmlns:MyProfile='http://www.magicdraw.com/schemas/MyProfile.xmi'>
          <uml:Model xmi:type='uml:Model' xmi:id='_root' name='Small'>
        """;

    private MagicDrawImportReport ImportXmi(string body, string stereotypes = "")
    {
        var path = Path.Combine(_dir.FullName, Guid.NewGuid().ToString("N") + ".mdxml");
        File.WriteAllText(path, Head + body + "</uml:Model>" + stereotypes + "</xmi:XMI>");
        return MagicDrawImporter.Import(path).Report;
    }

    [Fact]
    public void Fixture_Losses_Are_Counted_By_Uml_Type()
    {
        var (_, r) = MagicDrawImporter.Import(MagicDrawFixtures.WriteMdzip(_dir.FullName));

        Assert.False(r.IsLossless);
        var lost = r.Losses.ToDictionary(l => l.Type, l => l.Tally.NotImported);
        Assert.DoesNotContain("InstanceSpecification", lost.Keys);   // imported as instances since 0.10
        Assert.Equal(1, lost["Frobnicator"]);
        Assert.Equal(1, lost["Message"]);                  // the lost message
        Assert.Equal(1, lost["Dependency"]);               // its supplier is not in the project
        Assert.Equal(3, lost.Count);
        // Structured activity nodes (as actions, contents flattened into the activity) and opaque behaviors now come across.
        Assert.Equal(1, r.SourceTypes["StructuredActivityNode"].Imported);
        Assert.Equal(1, r.SourceTypes["OpaqueBehavior"].Imported);
        Assert.DoesNotContain("InteractionConstraint", r.SourceTypes.Keys);   // operand guards are read by the fragment
        Assert.DoesNotContain("Class", lost.Keys);          // blocks all came across
        Assert.DoesNotContain("Comment", lost.Keys);        // documentation comments are folded into the element, not lost
        Assert.Equal(r.ElementsInFile, r.ElementsKept + r.ElementsNotImported);
        Assert.InRange(r.ElementCoverage, 0.9, 0.99);

        // The unreadable "Generic Table" diagram is the only diagram not imported.
        Assert.Equal(r.Diagrams + 1, r.DiagramsInFile);
        Assert.Equal(r.StereotypeApplications, r.StereotypeApplicationsKept);
        Assert.True(r.TagValuesKept >= 2);   // «Critical» level and reviewers

        Assert.Contains(r.Lines("drone.mdzip"), l => l.StartsWith("  Fidelity: Kept "));
        var md = r.ToMarkdown("drone.mdzip");
        Assert.Contains("| Model elements |", md);
        Assert.Contains("| uml:Frobnicator | 1 | 0 | 0 | 1 |", md);
    }

    [Fact]
    public void A_Fully_Supported_Model_Is_Reported_Lossless()
    {
        var r = ImportXmi("""
            <packagedElement xmi:type='uml:Package' xmi:id='_p' name='Structure'>
              <packagedElement xmi:type='uml:Class' xmi:id='_a' name='A'>
                <ownedComment xmi:type='uml:Comment' xmi:id='_doc' body='Documentation of A'><annotatedElement xmi:idref='_a'/></ownedComment>
                <ownedAttribute xmi:type='uml:Property' xmi:id='_ab' name='b' type='_b' aggregation='composite'>
                  <lowerValue xmi:type='uml:LiteralInteger' xmi:id='_lo'/>
                  <upperValue xmi:type='uml:LiteralUnlimitedNatural' xmi:id='_hi' value='*'/>
                </ownedAttribute>
              </packagedElement>
              <packagedElement xmi:type='uml:Class' xmi:id='_b' name='B'/>
            </packagedElement>
            """,
            "<sysml:Block xmi:id='_a_st' base_Class='_a'/><sysml:Block xmi:id='_b_st' base_Class='_b'/>" +
            "<MagicDraw_Profile:auxiliaryResource xmi:id='_aux' base_Element='_p'/>");

        Assert.True(r.IsLossless, string.Join("\n", r.Lines("small.mdxml")));
        Assert.Equal(1.0, r.ElementCoverage);
        Assert.Equal(1, r.SourceTypes["Comment"].Folded);
        Assert.Equal(2, r.StereotypeApplications);   // the tool profile's application is not counted
        Assert.StartsWith("Nothing lost", r.FidelitySummary);
    }

    [Fact]
    public void Structured_Nodes_And_Opaque_Behaviors_Are_Accounted_For()
    {
        var r = ImportXmi("""
            <packagedElement xmi:type='uml:Class' xmi:id='_a' name='A'>
              <ownedAttribute xmi:type='uml:Property' xmi:id='_a_x' name='x'/>
            </packagedElement>
            <packagedElement xmi:type='uml:InstanceSpecification' xmi:id='_i' name='a1' classifier='_a'>
              <ownedComment xmi:type='uml:Comment' xmi:id='_i_doc' body='The first A'><annotatedElement xmi:idref='_i'/></ownedComment>
              <slot xmi:type='uml:Slot' xmi:id='_i_s' definingFeature='_a_x'><value xmi:type='uml:LiteralInteger' xmi:id='_i_v' value='3'/></slot>
            </packagedElement>
            <packagedElement xmi:type='uml:FunctionBehavior' xmi:id='_fb' name='square'><body>x * x</body></packagedElement>
            <packagedElement xmi:type='uml:Activity' xmi:id='_act' name='Work'>
              <node xmi:type='uml:LoopNode' xmi:id='_loop' name='Repeat' isTestedFirst='true' test='_t' bodyPart='_b'>
                <loopVariableInput xmi:type='uml:InputPin' xmi:id='_lvi' name='n'/>
                <node xmi:type='uml:OpaqueAction' xmi:id='_t' name='More?'/>
                <node xmi:type='uml:OpaqueAction' xmi:id='_b' name='Step'/>
                <edge xmi:type='uml:ControlFlow' xmi:id='_tb' source='_t' target='_b'/>
              </node>
              <node xmi:type='uml:ConditionalNode' xmi:id='_cond' name='Choose'>
                <clause xmi:type='uml:Clause' xmi:id='_cl1' test='_c1t' body='_c1b'/>
                <node xmi:type='uml:OpaqueAction' xmi:id='_c1t' name='Is hot'/>
                <node xmi:type='uml:OpaqueAction' xmi:id='_c1b' name='Cool'/>
              </node>
              <node xmi:type='uml:ExpansionRegion' xmi:id='_er' name='Each' mode='parallel'>
                <inputElement xmi:idref='_en'/>
                <node xmi:type='uml:CallBehaviorAction' xmi:id='_sq' name='Square it' behavior='_fb'/>
              </node>
              <node xmi:type='uml:ExpansionNode' xmi:id='_en' name='items' regionAsInput='_er'/>
              <edge xmi:type='uml:ControlFlow' xmi:id='_f1' source='_loop' target='_cond'/>
            </packagedElement>
            """);

        Assert.True(r.IsLossless, string.Join("\n", r.Lines("small.mdxml")));
        Assert.Equal(1, r.SourceTypes["Clause"].Folded);
        Assert.Equal(1, r.SourceTypes["LoopNode"].Imported);
        Assert.Equal(1, r.SourceTypes["ExpansionNode"].Imported);
        Assert.Equal(1, r.SourceTypes["FunctionBehavior"].Imported);
        Assert.Equal(3, r.Warnings.Count(w => w.Contains("flattened")));
        Assert.Empty(r.Skipped);
    }

    [Fact]
    public void Structured_Node_Details_Are_Kept_As_Tags()
    {
        var path = Path.Combine(_dir.FullName, "loop.mdxml");
        File.WriteAllText(path, Head + """
            <packagedElement xmi:type='uml:Activity' xmi:id='_act' name='Work'>
              <node xmi:type='uml:LoopNode' xmi:id='_loop' name='Repeat' isTestedFirst='true' test='_t' bodyPart='_b'>
                <node xmi:type='uml:OpaqueAction' xmi:id='_t' name='More?'/>
                <node xmi:type='uml:OpaqueAction' xmi:id='_b' name='Step'/>
              </node>
              <node xmi:type='uml:ConditionalNode' xmi:id='_cond' name='Choose'>
                <clause xmi:type='uml:Clause' xmi:id='_cl1' test='_c1t' body='_c1b'/>
                <node xmi:type='uml:OpaqueAction' xmi:id='_c1t' name='Is hot'/>
                <node xmi:type='uml:OpaqueAction' xmi:id='_c1b' name='Cool'/>
              </node>
            </packagedElement>
            """ + "</uml:Model></xmi:XMI>");
        var m = MagicDrawImporter.Import(path).Model;
        var loop = m.Find("_loop")!;
        Assert.Contains("loopNode", loop.Stereotypes);
        Assert.Equal("More?", loop.Get(Meridian.Core.Model.FieldKeys.TagPrefix + "test"));
        Assert.Equal("Step", loop.Get(Meridian.Core.Model.FieldKeys.TagPrefix + "bodyPart"));
        Assert.Equal("true", loop.Get(Meridian.Core.Model.FieldKeys.TagPrefix + "isTestedFirst"));
        Assert.Same(m.Find("_act"), m.Find("_b")!.Owner);
        var cond = m.Find("_cond")!;
        Assert.Equal("Is hot", cond.Get(Meridian.Core.Model.FieldKeys.TagPrefix + "clause1.test"));
        Assert.Equal("Cool", cond.Get(Meridian.Core.Model.FieldKeys.TagPrefix + "clause1.body"));
    }

    [Fact]
    public void Stereotypes_On_Dropped_Elements_And_Structured_Tag_Values_Are_Reported()
    {
        var r = ImportXmi("""
            <packagedElement xmi:type='uml:Class' xmi:id='_a' name='A'/>
            <packagedElement xmi:type='uml:Frobnicator' xmi:id='_i' name='a1'/>
            """,
            "<sysml:Block xmi:id='_a_st' base_Class='_a'/>" +
            "<MyProfile:Config xmi:id='_cfg' base_Element='_i' variant='X'/>" +
            "<MyProfile:Rated xmi:id='_rt' base_Class='_a' owner='Ana'><limit><value xmi:type='uml:LiteralReal' value='4.5'/></limit></MyProfile:Rated>");

        Assert.False(r.IsLossless);
        Assert.Equal(1, r.StereotypesLost["Config"]);
        Assert.Equal(1, r.TagValuesLost["limit"]);
        Assert.Equal(2, r.StereotypeApplicationsKept);
        Assert.Contains("1 tag values could not be copied", r.FidelitySummary);
        var md = r.ToMarkdown("small.mdxml");
        Assert.Contains("- «Config»: 1", md);
        Assert.Contains("- limit: 1", md);
    }

    /// <summary>
    /// Corpus check against real Cameo / MagicDraw projects, which cannot live in the repository. Point
    /// <c>MERIDIAN_CAMEO_CORPUS</c> at a folder of .mdzip / .mdxml files; the test imports each one, round-trips it through
    /// the .meridian format and writes <c>fidelity-report.md</c> into the folder. Without the variable it does nothing.
    /// Fails when a file cannot be read, does not round-trip, or keeps less than <c>MERIDIAN_CAMEO_MIN_COVERAGE</c>
    /// (default 0.9) of its model elements.
    /// </summary>
    [Fact]
    public void Corpus_Of_Real_Projects_Imports_With_Recorded_Fidelity()
    {
        var folder = Environment.GetEnvironmentVariable("MERIDIAN_CAMEO_CORPUS");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        var min = double.TryParse(Environment.GetEnvironmentVariable("MERIDIAN_CAMEO_MIN_COVERAGE"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 0.9;

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(MagicDrawImporter.IsSupported).Order(StringComparer.Ordinal).ToList();
        var summary = new StringBuilder("# Cameo import fidelity\n\n| File | Elements | Diagrams | Stereotypes | Round trip | Seconds |\n| --- | ---: | ---: | ---: | --- | ---: |\n");
        var details = new StringBuilder();
        var failures = new List<string>();
        foreach (var file in files)
        {
            var name = Path.GetRelativePath(folder, file);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var (model, r) = MagicDrawImporter.Import(file);
                var again = ProjectFile.Deserialize(ProjectFile.Serialize(model));
                var roundTrip = again.Count == model.Count ? "ok" : $"{model.Count} → {again.Count}";
                summary.AppendLine($"| {name} | {r.ElementCoverage:P1} ({r.ElementsKept}/{r.ElementsInFile}) | {r.Diagrams}/{r.DiagramsInFile} | " +
                                   $"{r.StereotypeApplicationsKept}/{r.StereotypeApplications} | {roundTrip} | {sw.Elapsed.TotalSeconds:0.0} |");
                details.AppendLine(r.ToMarkdown(name));
                if (roundTrip != "ok") failures.Add($"{name}: round trip changed the element count ({roundTrip})");
                if (r.ElementCoverage < min) failures.Add($"{name}: kept {r.ElementCoverage:P1} of model elements, below {min:P0}");
            }
            catch (Exception ex)
            {
                summary.AppendLine($"| {name} | failed: {ex.GetType().Name} | | | | {sw.Elapsed.TotalSeconds:0.0} |");
                failures.Add($"{name}: {ex.Message}");
            }
        }
        File.WriteAllText(Path.Combine(folder, "fidelity-report.md"), summary + "\n" + details);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
