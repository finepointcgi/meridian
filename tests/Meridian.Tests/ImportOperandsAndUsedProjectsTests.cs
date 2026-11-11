using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization;
using Microsoft.Data.Sqlite;
using static Meridian.Tests.MagicDrawFixtures;

namespace Meridian.Tests;

/// <summary>
/// The Cameo / MagicDraw, Enterprise Architect and XMI importers read interaction operands (with their separators), every
/// pseudostate kind, datastore / central buffer nodes and both association end multiplicities; the Cameo importer reads
/// used-project metadata. Fixtures are hand-written in each tool's format.
/// </summary>
public sealed class ImportOperandsAndUsedProjectsTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("meridian-import-gaps-");

    public void Dispose()
    {
        try { _dir.Delete(true); } catch (IOException) { }
    }

    // ================================================================== Cameo / MagicDraw

    /// <summary>
    /// Sequence diagram symbols as MagicDraw writes them: the fragment's view holds one InteractionOperand view per operand, each
    /// with its own bounds (the second starts at y = 190, 90 below the fragment's top at 100).
    /// </summary>
    internal static string PaySequenceViews =>
        Contents("_paySd", "_pay",
            Shape("_vShop", "Lifeline", "_llShop", "60, 40, 150, 420") +
            Shape("_vBank", "Lifeline", "_llBank", "320, 40, 150, 420") +
            Shape("_vAlt", "CombinedFragment", "_alt", "30, 100, 480, 200",
                Shape("_vOpOk", "InteractionOperand", "_opOk", "30, 100, 480, 90") +
                Shape("_vOpElse", "InteractionOperand", "_opElse", "30, 190, 480, 110")) +
            Path("_vM1", "Message", "_mCharge", "_vShop", "_vBank", "135, 140; 395, 140; ") +
            Path("_vM2", "Message", "_mRefuse", "_vBank", "_vShop", "395, 240; 135, 240; "));

    internal static string PayXmi(bool inline) => Header + $"""
          <uml:Model xmi:type='uml:Model' xmi:id='_payModel' name='Payments'>
            <packagedElement xmi:type='uml:Package' xmi:id='_payPkg' name='Payments'>
              <packagedElement xmi:type='uml:Class' xmi:id='_shop' name='Shop'>
                <ownedAttribute xmi:type='uml:Property' xmi:id='_shop_orders' name='orders' type='_order' association='_shopOrders'>
                  <lowerValue xmi:type='uml:LiteralInteger' xmi:id='_so_lo'/>
                  <upperValue xmi:type='uml:LiteralUnlimitedNatural' xmi:id='_so_hi' value='*'/>
                </ownedAttribute>
              </packagedElement>
              <packagedElement xmi:type='uml:Class' xmi:id='_order' name='Order'/>
              <packagedElement xmi:type='uml:Association' xmi:id='_shopOrders' memberEnd='_shop_orders _shopOrders_end'>
                <ownedEnd xmi:type='uml:Property' xmi:id='_shopOrders_end' type='_shop' association='_shopOrders'>
                  <lowerValue xmi:type='uml:LiteralInteger' xmi:id='_soe_lo' value='1'/>
                  <upperValue xmi:type='uml:LiteralUnlimitedNatural' xmi:id='_soe_hi' value='1'/>
                </ownedEnd>
              </packagedElement>
              <packagedElement xmi:type='uml:Collaboration' xmi:id='_payCollab' name='Pay collaboration'>
                <ownedAttribute xmi:type='uml:Property' xmi:id='_pShop' name='shop' type='_shop'/>
                <ownedAttribute xmi:type='uml:Property' xmi:id='_pBank' name='bank'/>
                <ownedBehavior xmi:type='uml:Interaction' xmi:id='_pay' name='Pay'>
                  <lifeline xmi:type='uml:Lifeline' xmi:id='_llShop' represents='_pShop'/>
                  <lifeline xmi:type='uml:Lifeline' xmi:id='_llBank' name='bank' represents='_pBank'/>
                  <fragment xmi:type='uml:CombinedFragment' xmi:id='_alt' interactionOperator='alt'>
                    <operand xmi:type='uml:InteractionOperand' xmi:id='_opOk'>
                      <guard xmi:type='uml:InteractionConstraint' xmi:id='_opOk_g'>
                        <specification xmi:type='uml:OpaqueExpression' xmi:id='_opOk_s'><body>card valid</body><language>English</language></specification>
                      </guard>
                      <fragment xmi:type='uml:MessageOccurrenceSpecification' xmi:id='_mos1' covered='_llShop' message='_mCharge'/>
                      <fragment xmi:type='uml:MessageOccurrenceSpecification' xmi:id='_mos2' covered='_llBank' message='_mCharge'/>
                    </operand>
                    <operand xmi:type='uml:InteractionOperand' xmi:id='_opElse'>
                      <guard xmi:type='uml:InteractionConstraint' xmi:id='_opElse_g'>
                        <specification xmi:type='uml:LiteralString' xmi:id='_opElse_s' value='else'/>
                      </guard>
                      <fragment xmi:type='uml:MessageOccurrenceSpecification' xmi:id='_mos3' covered='_llBank' message='_mRefuse'/>
                      <fragment xmi:type='uml:MessageOccurrenceSpecification' xmi:id='_mos4' covered='_llShop' message='_mRefuse'/>
                    </operand>
                  </fragment>
                  <message xmi:type='uml:Message' xmi:id='_mCharge' name='charge' sendEvent='_mos1' receiveEvent='_mos2'/>
                  <message xmi:type='uml:Message' xmi:id='_mRefuse' name='refuse' messageSort='reply' sendEvent='_mos3' receiveEvent='_mos4'/>
                  {Diagram("_paySd", "Pay", "_pay", "SysML Sequence Diagram", "Sequence Diagram", "BINARY-paySd", inline ? PaySequenceViews : null)}
                </ownedBehavior>
              </packagedElement>
              <packagedElement xmi:type='uml:StateMachine' xmi:id='_door' name='Door'>
                <region xmi:type='uml:Region' xmi:id='_doorR'>
                  <subvertex xmi:type='uml:Pseudostate' xmi:id='_init'/>
                  <subvertex xmi:type='uml:State' xmi:id='_open' name='Open'>
                    <connectionPoint xmi:type='uml:Pseudostate' xmi:id='_in' name='in' kind='entryPoint'/>
                    <connectionPoint xmi:type='uml:Pseudostate' xmi:id='_out' name='out' kind='exitPoint'/>
                    <region xmi:type='uml:Region' xmi:id='_openR'>
                      <subvertex xmi:type='uml:Pseudostate' xmi:id='_hist' kind='deepHistory'/>
                    </region>
                  </subvertex>
                  <subvertex xmi:type='uml:Pseudostate' xmi:id='_choice' name='locked?' kind='choice'/>
                  <subvertex xmi:type='uml:Pseudostate' xmi:id='_junction' kind='junction'/>
                  <subvertex xmi:type='uml:Pseudostate' xmi:id='_fork' kind='fork'/>
                  <subvertex xmi:type='uml:Pseudostate' xmi:id='_join' kind='join'/>
                  <subvertex xmi:type='uml:Pseudostate' xmi:id='_shallow' kind='shallowHistory'/>
                  <subvertex xmi:type='uml:Pseudostate' xmi:id='_stop' kind='terminate'/>
                  <subvertex xmi:type='uml:Pseudostate' xmi:id='_odd' name='odd' kind='dynamicChoice'/>
                  <transition xmi:type='uml:Transition' xmi:id='_tIn' source='_init' target='_choice'/>
                  <transition xmi:type='uml:Transition' xmi:id='_tOut' source='_out' target='_stop'/>
                </region>
              </packagedElement>
              <packagedElement xmi:type='uml:Activity' xmi:id='_fulfil' name='Fulfil'>
                <node xmi:type='uml:DataStoreNode' xmi:id='_ordersDb' name='Orders' type='_order'/>
                <node xmi:type='uml:CentralBufferNode' xmi:id='_queue' name='Queue'/>
                <node xmi:type='uml:ActivityParameterNode' xmi:id='_param' name='request'/>
              </packagedElement>
            </packagedElement>
          </uml:Model>
          <sysml:Block xmi:id='_shop_st' base_Class='_shop'/>
          <sysml:Block xmi:id='_order_st' base_Class='_order'/>
        </xmi:XMI>
        """;

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    /// <summary>An .mdzip as later Cameo versions name its parts (uml_umodel), with the diagram symbols in their own entry.</summary>
    internal static string WritePayMdzip(string dir)
    {
        var path = System.IO.Path.Combine(dir, "payments.mdzip");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        AddEntry(zip, "com.nomagic.magicdraw.uml_umodel.model", PayXmi(inline: false));
        AddEntry(zip, "BINARY-paySd", BinaryEntry(PaySequenceViews));
        AddEntry(zip, "com.nomagic.ci.metamodel.project", "<project:Project xmlns:project='http://www.nomagic.com/ns/cameo/client/project/1.0' xmlns:xmi='http://www.omg.org/XMI' xmi:id='_p' id='PROJECT-pay'/>");
        return path;
    }

    [Fact]
    public void Cameo_Operands_Keep_Their_Guards_And_Separators_From_The_Operand_Shapes()
    {
        var (m, report) = MagicDrawImporter.Import(WritePayMdzip(_dir.FullName));
        var alt = m.Find("_alt")!;
        Assert.Equal(["card valid", "else"], InteractionOperands.Guards(alt));
        Assert.Equal(["_opOk", "_opElse"], InteractionOperands.Of(alt).Select(o => o.Id));
        Assert.Equal("", alt.Get(FieldKeys.Guard));
        var d = (Diagram)m.Find("_paySd")!;
        var node = d.NodeFor(alt)!;
        Assert.DoesNotContain(d.Nodes, n => n.Element.Kind == ElementKind.InteractionOperand);   // operands are drawn by the fragment
        Assert.Equal(90, InteractionOperands.StoredOffset(node, m.Find("_opElse")!));
        Assert.Equal([100.0, 190.0], InteractionOperands.Layout(node, node.Bounds).Select(b => b.Top));
        Assert.False(report.Approximated.ContainsKey(MagicDrawImportReport.OperandGeometryGuessed));
        Assert.Equal("PROJECT-pay", report.ProjectId);
        Assert.DoesNotContain(report.Skipped.Keys, k => k.Contains("InteractionConstraint"));   // guards are read, not skipped
        Assert.Contains("operand-sep", SvgRenderer.Render(m, d));
    }

    [Fact]
    public void Cameo_Pseudostates_Object_Nodes_And_Both_Association_Ends_Import()
    {
        var path = System.IO.Path.Combine(_dir.FullName, "payments.mdxml");
        File.WriteAllText(path, PayXmi(inline: true));
        var (m, report) = MagicDrawImporter.Import(path);

        Assert.Equal(ElementKind.InitialState, m.Find("_init")!.Kind);
        var kinds = new Dictionary<string, string>
        {
            ["_in"] = Pseudostates.EntryPoint, ["_out"] = Pseudostates.ExitPoint, ["_hist"] = Pseudostates.DeepHistory, ["_choice"] = Pseudostates.Choice,
            ["_junction"] = Pseudostates.Junction, ["_fork"] = Pseudostates.Fork, ["_join"] = Pseudostates.Join,
            ["_shallow"] = Pseudostates.ShallowHistory, ["_stop"] = Pseudostates.Terminate,
        };
        foreach (var (id, kind) in kinds)
        {
            Assert.Equal(ElementKind.Pseudostate, m.Find(id)!.Kind);
            Assert.Equal(kind, Pseudostates.KindOf(m.Find(id)!));
        }
        Assert.Same(m.Find("_open"), m.Find("_in")!.Owner);       // connection points belong to their state
        Assert.Same(m.Find("_open"), m.Find("_hist")!.Owner);
        Assert.Equal("locked?", m.Find("_choice")!.Name);
        Assert.Equal(ElementKind.Transition, m.Find("_tOut")!.Kind);

        // An unknown kind is kept as a choice, reported as approximated and warned about once.
        Assert.Equal(Pseudostates.Choice, Pseudostates.KindOf(m.Find("_odd")!));
        Assert.Contains(report.Approximated[MagicDrawImportReport.UnknownPseudostate], a => a.Contains("dynamicChoice"));
        Assert.Contains(report.Warnings, w => w.Contains("dynamicChoice"));
        Assert.Contains(report.Lines("payments.mdxml"), l => l.Contains("Approximated") );

        Assert.Equal(Subtypes.DataStore, m.Find("_ordersDb")!.Get(FieldKeys.Subtype));
        Assert.Equal("_order", m.Find("_ordersDb")!.TypeRef);
        Assert.Equal(Subtypes.CentralBuffer, m.Find("_queue")!.Get(FieldKeys.Subtype));
        Assert.Equal("", m.Find("_param")!.Get(FieldKeys.Subtype));

        var assoc = (Relationship)m.Find("_shopOrders")!;
        Assert.Equal(("_shop", "_order"), (assoc.Source.Id, assoc.Target.Id));
        Assert.Equal("*", assoc.Get(FieldKeys.Multiplicity));
        Assert.Equal("1", assoc.Get(FieldKeys.SourceMultiplicity));
    }

    [Fact]
    public void Cameo_Import_Round_Trips_Through_Meridian_Xmi()
    {
        var (m, _) = MagicDrawImporter.Import(WritePayMdzip(_dir.FullName));
        var (back, report) = XmiExchange.Import(XmiExchange.ToXml(m));
        Assert.DoesNotContain(report.Warnings, w => w.StartsWith("Approximated", StringComparison.Ordinal));
        var alt = back.Find("_alt")!;
        Assert.Equal(["card valid", "else"], InteractionOperands.Guards(alt));
        var node = back.Diagrams.SelectMany(d => d.Nodes).Single(n => ReferenceEquals(n.Element, alt));
        Assert.Equal(90, InteractionOperands.StoredOffset(node, back.Find("_opElse")!));

        var path = System.IO.Path.Combine(_dir.FullName, "payments.mdxml");
        File.WriteAllText(path, PayXmi(inline: true));
        var (states, _) = MagicDrawImporter.Import(path);
        var (statesBack, _) = XmiExchange.Import(XmiExchange.ToXml(states));
        Assert.Equal(states.OfKind(ElementKind.Pseudostate).Select(p => (p.Id, Pseudostates.KindOf(p))).Order(),
                     statesBack.OfKind(ElementKind.Pseudostate).Select(p => (p.Id, Pseudostates.KindOf(p))).Order());
        Assert.Equal(Subtypes.DataStore, statesBack.Find("_ordersDb")!.Get(FieldKeys.Subtype));
        Assert.Equal("1", statesBack.Find("_shopOrders")!.Get(FieldKeys.SourceMultiplicity));
    }

    // ================================================================== Cameo used projects

    /// <summary>
    /// Project metadata shaped like a Cameo 2021x <c>com.nomagic.ci.metamodel.project</c> resource: the project's id, a shared package
    /// of its own, and project usages with usedProjectURI, project id properties and mount points naming the mounted shared package.
    /// </summary>
    private const string ProjectMetadata = """
        <?xml version='1.0' encoding='UTF-8'?>
        <project:Project xmi:version='2.0' xmlns:xmi='http://www.omg.org/XMI' xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance'
            xmlns:project='http://www.nomagic.com/ns/cameo/client/project/1.0' xmi:id='_proj' id='PROJECT-a1b2c3d4e5f6'>
          <properties xmi:id='_pp1' key='LOCAL_PROJECT_ID' value='PROJECT-a1b2c3d4e5f6'/>
          <ownedSections xmi:id='_sec1' name='model' featureName='UML2MetamodelFeature'/>
          <sharePoints xmi:id='_sp_own' ID='_sp_own' name='Vehicle API'>
            <object href='com.nomagic.magicdraw.uml_umodel.shared_umodel#_vapi'/>
          </sharePoints>
          <projectUsages xmi:id='_u1' usedProjectURI='local:/Standard%20Parts.mdzip' readonly='true' loadedAutomatically='true'>
            <properties xmi:id='_u1p' key='LOCAL_PROJECT_ID' value='PROJECT-77aa'/>
            <mountPoints xmi:id='_u1m' sharePointID='_sp_std' containmentFeatureID='51' featureName='packagedElement'>
              <mountedPoint href='local:/Standard%20Parts.mdzip?resource=com.nomagic.magicdraw.uml_umodel.shared_umodel#_std'/>
              <mountedOn href='com.nomagic.magicdraw.uml_umodel.model#_vmodel'/>
            </mountPoints>
            <usedProject href='local:/Standard%20Parts.mdzip?resource=com.nomagic.ci.metamodel.project#_stdproj'/>
          </projectUsages>
          <projectUsages xmi:id='_u2' usedProjectURI='twcloud:/1f3a9c2e-7d4b-4c55-9a10-0f2b6b7c8d9e/5d1e0c1a-2b3c-4d5e-8f90-a1b2c3d4e5f6' readonly='true'>
            <mountPoints xmi:id='_u2m'><mountedPoint href='twcloud:/1f3a9c2e-7d4b-4c55-9a10-0f2b6b7c8d9e/5d1e0c1a-2b3c-4d5e-8f90-a1b2c3d4e5f6#_sensors'/></mountPoints>
          </projectUsages>
          <projectUsages xmi:id='_u3' usedProjectURI='twcloud:/9c8b7a65-4321-4fed-b0c1-23456789abcd/1?projectName=Fleet%20Interfaces' readonly='true'/>
          <projectUsages xmi:id='_u4' usedProjectURI='local:/SysML.mdzip' readonly='true'>
            <mountPoints xmi:id='_u4m'><mountedPoint href='local:/SysML.mdzip#_11_5EAPbeta_be00301_1147424179914_458922_958'/></mountPoints>
          </projectUsages>
          <projectUsages xmi:id='_u5' usedProjectURI='local:/C:/Program%20Files/Cameo%20Systems%20Modeler/profiles/Std%20Lib.mdzip' readonly='true'>
            <mountPoints xmi:id='_u5m'><mountedPoint href='#_9_0_be00301_1108053761194_467635_11463'/></mountPoints>
          </projectUsages>
          <projectUsages xmi:id='_u6' usedProjectURI='local:/MD_customization_for_SysML.mdzip' readonly='true'/>
        </project:Project>
        """;

    private const string VehicleWithCopyXmi = """
        <?xml version='1.0' encoding='UTF-8'?>
        <xmi:XMI xmlns:uml='http://www.omg.org/spec/UML/20131001' xmlns:xmi='http://www.omg.org/spec/XMI/20131001'
                 xmlns:sysml='http://www.omg.org/spec/SysML/20181001/SysML'>
          <uml:Model xmi:type='uml:Model' xmi:id='_vmodel' name='Vehicle'>
            <packagedElement xmi:type='uml:Package' xmi:id='_vs' name='Structure'>
              <packagedElement xmi:type='uml:Class' xmi:id='_car' name='Car'>
                <ownedAttribute xmi:type='uml:Property' xmi:id='_fl' name='frontLeft' aggregation='composite' type='_wheel'/>
                <ownedAttribute xmi:type='uml:Property' xmi:id='_ecu' name='ecu' aggregation='composite'>
                  <type href='twcloud:/1f3a9c2e-7d4b-4c55-9a10-0f2b6b7c8d9e/5d1e0c1a-2b3c-4d5e-8f90-a1b2c3d4e5f6#_ecuBlock'/>
                </ownedAttribute>
              </packagedElement>
            </packagedElement>
          </uml:Model>
          <sysml:Block xmi:id='_car_b' base_Class='_car'/>
        </xmi:XMI>
        """;

    /// <summary>The used project's shared package as Cameo keeps it in the using project (the shared model part).</summary>
    private const string SharedPartXmi = """
        <?xml version='1.0' encoding='UTF-8'?>
        <xmi:XMI xmlns:uml='http://www.omg.org/spec/UML/20131001' xmlns:xmi='http://www.omg.org/spec/XMI/20131001'>
          <uml:Model xmi:type='uml:Model' xmi:id='_vmodel' name='Vehicle'>
            <packagedElement xmi:type='uml:Package' xmi:id='_std' name='Standard parts'>
              <packagedElement xmi:type='uml:Class' xmi:id='_wheel' name='Wheel'/>
            </packagedElement>
          </uml:Model>
        </xmi:XMI>
        """;

    private string WriteMultiModuleMdzip()
    {
        var path = System.IO.Path.Combine(_dir.FullName, "vehicle.mdzip");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        AddEntry(zip, "com.nomagic.ci.metamodel.project", ProjectMetadata);
        AddEntry(zip, "com.nomagic.magicdraw.uml_umodel.model", VehicleWithCopyXmi);
        AddEntry(zip, "com.nomagic.magicdraw.uml_umodel.shared_umodel", SharedPartXmi);
        AddEntry(zip, "com.nomagic.magicdraw.core.project.options.commonprojectoptions", "<options/>");
        AddEntry(zip, "proxy.local__SysML_profile.mdzip", "<proxy/>");
        AddEntry(zip, "proxy.local__UML_Standard_Profile.mdzip", "<proxy/>");
        AddEntry(zip, "proxy.local__Standard%20Parts.mdzip", "<proxy/>");
        AddEntry(zip, "proxy.twcloud__1f3a9c2e-7d4b-4c55-9a10-0f2b6b7c8d9e", "<proxy/>");
        return path;
    }

    [Fact]
    public void Used_Projects_Come_From_Project_Usages_With_Uris_Ids_And_Mounted_Shared_Packages()
    {
        var (m, report) = MagicDrawImporter.Import(WriteMultiModuleMdzip());
        Assert.Equal("PROJECT-a1b2c3d4e5f6", report.ProjectId);
        Assert.Equal(["_vapi"], report.SharedPackages);
        Assert.Equal(["MD Customization for SysML", "Std Lib", "SysML", "UML Standard Profile"], report.StandardProfiles.Order(StringComparer.Ordinal));

        var used = report.UsedProjects.ToDictionary(u => u.Name);
        Assert.Equal(["Fleet Interfaces", "Standard Parts", "Teamwork Cloud project 1f3a9c2e"], used.Keys.Order(StringComparer.Ordinal));

        var parts = used["Standard Parts"];
        Assert.Equal((MagicDrawProjectLocation.Local, "PROJECT-77aa", "local:/Standard%20Parts.mdzip", "metadata"), (parts.Location, parts.ProjectId, parts.Uri, parts.Source));
        Assert.Equal(["_std"], parts.SharedPackageIds);
        Assert.Equal(MagicDrawUsageOutcome.Copied, parts.Outcome);                 // its shared package is in the file …
        Assert.NotNull(m.Find("_wheel"));                                         // … and was imported with it
        Assert.Equal("_wheel", m.Find("_fl")!.TypeRef);

        var sensors = used["Teamwork Cloud project 1f3a9c2e"];
        Assert.Equal(MagicDrawProjectLocation.TeamworkCloud, sensors.Location);
        Assert.Equal(MagicDrawUsageOutcome.Referenced, sensors.Outcome);
        Assert.Equal(["_sensors"], sensors.SharedPackageIds);
        Assert.Equal(["_ecuBlock"], sensors.ReferencedIds);                      // an href into the Teamwork Cloud project
        Assert.Equal(MagicDrawUsageOutcome.Referenced, used["Fleet Interfaces"].Outcome);
        Assert.Equal(MagicDrawProjectLocation.TeamworkCloud, used["Fleet Interfaces"].Location);

        var lines = report.Lines("vehicle.mdzip").ToList();
        Assert.Contains(lines, l => l.Contains("Standard Parts (local file, id PROJECT-77aa") && l.Contains("copied: 1 shared package"));
        Assert.Contains(lines, l => l.Contains("Fleet Interfaces (Teamwork Cloud") && l.Contains("found, not in the file"));
        Assert.Contains(lines, l => l.StartsWith("  Standard profiles used", StringComparison.Ordinal) && l.Contains("SysML"));
    }

    [Fact]
    public void Linking_A_Used_Project_Prefers_Its_Shared_Packages_And_Is_Reported()
    {
        var (m, report) = MagicDrawImporter.Import(WriteMultiModuleMdzip());
        var existing = new ModelRepository("Standard Parts");
        foreach (var (id, name) in new[] { ("_other", "Other"), ("_std", "Standard parts") })
        {
            var pkg = new Element(ElementKind.Package, id);
            ModelRepository.SetFieldForLoad(pkg, FieldKeys.Name, name);
            existing.AttachForLoad(existing.Root, pkg);
        }
        var wheel = new Element(ElementKind.Block, "_wheel");
        ModelRepository.SetFieldForLoad(wheel, FieldKeys.Name, "Wheel");
        existing.AttachForLoad(existing.Find("_std")!, wheel);

        var used = report.UsedProjects.Single(u => u.Name == "Standard Parts");
        var packages = MagicDrawImporter.SuggestPackages(existing, used);
        Assert.Equal(["_std"], packages);                                         // the mounted shared package, not every top-level one
        MagicDrawImporter.UseLibraries(m, report, existing, packages, used, "Parts library");
        Assert.Null(m.Find("_wheel"));
        var linked = report.UsedProjects.Single(u => u.Name == "Standard Parts");
        Assert.Equal((MagicDrawUsageOutcome.Linked, "Parts library"), (linked.Outcome, linked.LinkedTo));
        Assert.Contains(report.Lines("vehicle.mdzip"), l => l.Contains("linked: uses the Meridian project “Parts library”"));
    }

    [Theory]
    [InlineData("local:/Standard%20Parts.mdzip", "Standard Parts", MagicDrawProjectLocation.Local)]
    [InlineData("local:/C:/models/Fleet/Power%20Lib.mdzip?resource=com.nomagic.ci.metamodel.project#_x", "Power Lib", MagicDrawProjectLocation.Local)]
    [InlineData("file:/home/me/models/Ground_Segment.mdzip", "Ground_Segment", MagicDrawProjectLocation.Local)]
    [InlineData("Standard%20Parts.mdzip#_wheel", "Standard Parts", MagicDrawProjectLocation.Local)]
    [InlineData("twcloud:/1f3a9c2e-7d4b-4c55-9a10-0f2b6b7c8d9e/5d1e0c1a", "Teamwork Cloud project 1f3a9c2e", MagicDrawProjectLocation.TeamworkCloud)]
    [InlineData("twcloud:/1f3a9c2e/2?projectName=Fleet%20Interfaces", "Fleet Interfaces", MagicDrawProjectLocation.TeamworkCloud)]
    [InlineData("http://www.omg.org/spec/UML/20131001/PrimitiveTypes.xmi#Real", null, MagicDrawProjectLocation.Unknown)]
    public void Used_Project_Uris_Map_To_Display_Names(string uri, string? name, MagicDrawProjectLocation location)
    {
        Assert.Equal((name, location), MagicDrawImporter.UsedProjectName(uri));
    }

    [Fact]
    public void Teamwork_Cloud_References_Are_Not_Mistaken_For_File_Modules()
    {
        Assert.Null(MagicDrawImporter.ModuleOf("twcloud:/1f3a/2#_x"));
        Assert.Equal("Standard Parts", MagicDrawImporter.ModuleOf("local:/Standard%20Parts.mdzip#_x"));
        Assert.Null(MagicDrawImporter.ModuleOf("local:/UML_Standard_Profile.mdzip#_x"));
        Assert.True(MagicDrawImporter.IsStandardModule("MD_customization_for_SysML"));
        Assert.False(MagicDrawImporter.IsStandardModule("Standard Parts"));
    }

    // ================================================================== Enterprise Architect

    private string WriteEaRepository()
    {
        var path = System.IO.Path.Combine(_dir.FullName, "model.qea");
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        void Exec(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        Exec("create table t_package (Package_ID int, Name text, Parent_ID int, Notes text)");
        Exec("create table t_object (Object_ID int, Object_Type text, Name text, Alias text, Note text, Package_ID int, Stereotype text, ParentID int, Classifier int, Abstract text, NType int, ea_guid text, PDATA1 text)");
        Exec("create table t_objectproperties (Object_ID int, Property text, Value text, Notes text)");
        Exec("create table t_attribute (ID int, Object_ID int, Name text, Type text, Classifier text, LowerBound text, UpperBound text, \"Default\" text, Notes text, Scope text, Pos int)");
        Exec("create table t_operation (OperationID int, Object_ID int, Name text, Type text, Notes text, Pos int)");
        Exec("create table t_operationparams (OperationID int, Name text, Type text, Kind text, Pos int)");
        Exec("create table t_connector (Connector_ID int, Connector_Type text, SubType text, Stereotype text, Name text, Start_Object_ID int, End_Object_ID int, SourceCard text, DestCard text, PDATA2 text, SeqNo int)");
        Exec("create table t_diagram (Diagram_ID int, Package_ID int, ParentID int, Diagram_Type text, Name text, StyleEx text)");
        Exec("create table t_diagramobjects (Diagram_ID int, Object_ID int, RectTop int, RectLeft int, RectRight int, RectBottom int, Sequence int)");
        Exec("create table t_diagramlinks (DiagramID int, ConnectorID int, Hidden text)");
        Exec("create table t_xref (XrefID text, Name text, Type text, Visibility text, Namespace text, Requirement text, \"Constraint\" text, Behavior text, Partition text, Description text, Client text, Supplier text, Link text)");
        Exec("insert into t_package values (1,'Model',0,''),(2,'Shop',1,'')");
        Exec("""
            insert into t_object values
            (10,'Class','Shop','','',2,'block',0,0,'0',0,'{O-10}',''),
            (11,'Class','Order','','',2,'block',0,0,'0',0,'{O-11}',''),
            (20,'Interaction','Pay','','',2,'',0,0,'0',0,'{O-20}',''),
            (21,'Sequence','shop','','',2,'',20,10,'0',0,'{O-21}',''),
            (22,'Sequence','bank','','',2,'',20,0,'0',0,'{O-22}',''),
            (23,'InteractionFragment','','','',2,'',20,0,'0',0,'{O-23}',''),
            (24,'InteractionFragment','','','',2,'',20,0,'0',4,'{O-24}',''),
            (30,'StateMachine','Door','','',2,'',0,0,'0',0,'{O-30}',''),
            (31,'StateNode','','','',2,'',30,0,'0',3,'{O-31}',''),
            (32,'State','Open','','',2,'',30,0,'0',0,'{O-32}',''),
            (33,'StateNode','H','','',2,'',32,0,'0',5,'{O-33}',''),
            (34,'StateNode','H*','','',2,'',32,0,'0',15,'{O-34}',''),
            (35,'StateNode','locked?','','',2,'',30,0,'0',11,'{O-35}',''),
            (36,'StateNode','','','',2,'',30,0,'0',12,'{O-36}',''),
            (37,'StateNode','in','','',2,'',32,0,'0',13,'{O-37}',''),
            (38,'StateNode','out','','',2,'',32,0,'0',14,'{O-38}',''),
            (39,'StateNode','j','','',2,'',30,0,'0',10,'{O-39}',''),
            (40,'StateNode','odd','','',2,'',30,0,'0',7,'{O-40}',''),
            (41,'Synchronization','split','','',2,'',30,0,'0',0,'{O-41}',''),
            (42,'Synchronization','merge','','',2,'Join',30,0,'0',0,'{O-42}',''),
            (43,'ExitPoint','abort','','',2,'',32,0,'0',0,'{O-43}',''),
            (44,'StateNode','','','',2,'',30,0,'0',4,'{O-44}',''),
            (50,'Activity','Fulfil','','',2,'',0,0,'0',0,'{O-50}',''),
            (51,'DataStore','Orders','','',2,'',50,0,'0',0,'{O-51}',''),
            (52,'CentralBufferNode','Queue','','',2,'',50,0,'0',0,'{O-52}',''),
            (53,'Object','Archive','','',2,'datastore',50,0,'0',0,'{O-53}',''),
            (54,'Synchronization','','','',2,'',50,0,'0',0,'{O-54}','')
            """);
        // Operands of fragment 23 (alt): 60 px and 140 px high, guards typed with and without brackets.
        Exec("""
            insert into t_xref (XrefID, Name, Type, Visibility, Partition, Description, Client, Supplier) values
            ('{X-1}','Partitions','element property','Public','0','@PAR;Name=[card valid];Size=60;GUID={P-1};@ENDPAR;@PAR;Name=else;Size=140;GUID={P-2};@ENDPAR;','{O-23}','<none>'),
            ('{X-2}','Partitions','element property','Public','0','@PAR;Name=retry < 3;Size=500;GUID={P-3};@ENDPAR;@PAR;Name=;Size=500;GUID={P-4};@ENDPAR;','{O-24}','<none>'),
            ('{X-3}','MOFProps','element property','Public','0','@PROP;@NAME=isActive@ENDNAME;@ENDPROP;','{O-23}','<none>')
            """);
        Exec("""
            insert into t_connector values
            (100,'Association','','','',10,11,'1','0..*','',0),
            (101,'Aggregation','Strong','','',11,10,'1..*','1','',0),
            (102,'Sequence','','','charge',21,22,'','','',1),
            (103,'Sequence','','','refuse',22,21,'','','',2),
            (104,'StateFlow','','','',41,32,'','','',0),
            (105,'StateFlow','','','',41,35,'','','',0),
            (106,'StateFlow','','','',31,32,'','','',0)
            """);
        Exec("insert into t_diagram values (1,2,20,'Sequence','Pay',''),(2,2,30,'Statechart','Door','')");
        Exec("""
            insert into t_diagramobjects values
            (1,21,-40,60,210,-460,1),(1,22,-40,320,470,-460,2),(1,23,-100,30,510,-300,3),(1,24,-320,30,510,-420,4),
            (2,32,-50,100,300,-200,1),(2,41,-250,100,200,-258,2)
            """);
        Exec("insert into t_diagramlinks values (1,103,'0'),(1,102,'0')");
        return path;
    }

    [Fact]
    public void Ea_Fragments_Read_Their_Operator_And_Partitions_As_Operands_With_Separators()
    {
        var (m, report) = EaImporter.Import(WriteEaRepository());
        var frags = m.OfKind(ElementKind.CombinedFragment).ToList();
        var alt = frags.Single(f => f.Get(FieldKeys.Operator) == "alt");
        var loop = frags.Single(f => f.Get(FieldKeys.Operator) == "loop");
        Assert.Equal(["card valid", "else"], InteractionOperands.Guards(alt));
        Assert.Equal(["retry < 3", ""], InteractionOperands.Guards(loop));
        Assert.Equal(ElementKind.Interaction, alt.Owner!.Kind);
        Assert.Equal(2, m.OfKind(ElementKind.Lifeline).Count());

        var d = m.Diagrams.Single(x => x.Name == "Pay");
        Assert.Equal(DiagramType.Sequence, d.DiagramType);
        var altNode = d.NodeFor(alt)!;
        Assert.Equal(new Bounds(30, 100, 480, 200), altNode.Bounds);
        Assert.Equal(60, InteractionOperands.StoredOffset(altNode, InteractionOperands.Of(alt)[1]));
        // The loop's stored heights (500 + 500) do not fit its 100 px shape: spread evenly and reported.
        Assert.Null(InteractionOperands.StoredOffset(d.NodeFor(loop)!, InteractionOperands.Of(loop)[1]));
        Assert.Contains(report.Warnings, w => w.StartsWith("Approximated", StringComparison.Ordinal) && w.Contains("loop fragment"));

        // Messages in sequence-number order, from the lifeline heads down.
        var messages = d.Edges.Where(e => e.Relationship.Kind == ElementKind.Message).OrderBy(e => Notation.MessageY(e)).Select(e => e.Relationship.Name).ToList();
        Assert.Equal(["charge", "refuse"], messages);
    }

    [Fact]
    public void Ea_State_Nodes_Map_By_NType_And_Unconfirmed_Codes_Are_Reported()
    {
        var (m, report) = EaImporter.Import(WriteEaRepository());
        Element ByName(string n) => m.AllElements.Single(e => e.Name == n);
        Assert.Equal(Pseudostates.ShallowHistory, Pseudostates.KindOf(ByName("H")));
        Assert.Equal(Pseudostates.DeepHistory, Pseudostates.KindOf(ByName("H*")));
        Assert.Equal(Pseudostates.Choice, Pseudostates.KindOf(ByName("locked?")));
        Assert.Equal(Pseudostates.EntryPoint, Pseudostates.KindOf(ByName("in")));
        Assert.Equal(Pseudostates.ExitPoint, Pseudostates.KindOf(ByName("out")));
        Assert.Equal(Pseudostates.ExitPoint, Pseudostates.KindOf(ByName("abort")));
        Assert.Equal(Pseudostates.Junction, Pseudostates.KindOf(ByName("j")));
        Assert.Equal(Pseudostates.Choice, Pseudostates.KindOf(ByName("odd")));
        Assert.Equal(Pseudostates.Fork, Pseudostates.KindOf(ByName("split")));          // two outgoing transitions
        Assert.Equal(Pseudostates.Join, Pseudostates.KindOf(ByName("merge")));          // «Join» stereotype
        Assert.Empty(ByName("merge").Stereotypes);
        Assert.Single(m.OfKind(ElementKind.Pseudostate), p => Pseudostates.KindOf(p) == Pseudostates.Terminate);
        Assert.Single(m.OfKind(ElementKind.InitialState));
        Assert.Single(m.OfKind(ElementKind.FinalState));
        Assert.Same(ByName("Open"), ByName("in").Owner);
        Assert.Same(ByName("Open"), ByName("H*").Owner);
        Assert.Single(m.OfKind(ElementKind.ForkNode));                                   // the activity's bar stays a fork node

        Assert.Contains(report.Warnings, w => w.StartsWith("Approximated", StringComparison.Ordinal) && w.Contains("NType 10"));
        Assert.Contains(report.Warnings, w => w.StartsWith("Approximated", StringComparison.Ordinal) && w.Contains("NType 7"));
        Assert.Contains(report.Warnings, w => w.Contains("'split'") && w.Contains("2 outgoing"));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("'H*'"));
    }

    [Fact]
    public void Ea_Object_Nodes_And_Both_Association_Ends_Import()
    {
        var (m, _) = EaImporter.Import(WriteEaRepository());
        Element ByName(string n) => m.AllElements.Single(e => e.Name == n);
        Assert.Equal((ElementKind.ObjectNode, Subtypes.DataStore), (ByName("Orders").Kind, ByName("Orders").Get(FieldKeys.Subtype)));
        Assert.Equal(Subtypes.CentralBuffer, ByName("Queue").Get(FieldKeys.Subtype));
        Assert.Equal(Subtypes.DataStore, ByName("Archive").Get(FieldKeys.Subtype));
        Assert.Empty(ByName("Archive").Stereotypes);

        var assoc = m.Relationships.Single(r => r.Kind == ElementKind.Association);
        Assert.Equal(("Shop", "Order", "0..*", "1"), (assoc.Source.Name, assoc.Target.Name, assoc.Get(FieldKeys.Multiplicity), assoc.Get(FieldKeys.SourceMultiplicity)));
        // A composition is drawn part → whole in EA: the part's card (SourceCard) is the target end in Meridian.
        var comp = m.Relationships.Single(r => r.Kind == ElementKind.Composition);
        Assert.Equal(("Shop", "Order", "1..*", "1"), (comp.Source.Name, comp.Target.Name, comp.Get(FieldKeys.Multiplicity), comp.Get(FieldKeys.SourceMultiplicity)));
    }

    [Fact]
    public void Ea_Import_Round_Trips_Operands_And_Pseudostates_Through_Xmi()
    {
        var (m, _) = EaImporter.Import(WriteEaRepository());
        var (back, report) = XmiExchange.Import(XmiExchange.ToXml(m));
        var alt = back.OfKind(ElementKind.CombinedFragment).Single(f => f.Get(FieldKeys.Operator) == "alt");
        Assert.Equal(["card valid", "else"], InteractionOperands.Guards(alt));
        var node = back.Diagrams.SelectMany(d => d.Nodes).Single(n => ReferenceEquals(n.Element, alt));
        Assert.Equal(60, InteractionOperands.StoredOffset(node, InteractionOperands.Of(alt)[1]));
        Assert.Equal(m.OfKind(ElementKind.Pseudostate).Select(p => Pseudostates.KindOf(p)).Order(), back.OfKind(ElementKind.Pseudostate).Select(p => Pseudostates.KindOf(p)).Order());
        Assert.Contains(report.Warnings, w => w.Contains("loop fragment"));   // still spread evenly, still said
    }

    // ================================================================== XMI from other tools

    [Fact]
    public void Xmi_Reads_Separators_From_Operand_Shapes_And_Keeps_Unknown_Pseudostates_As_Choices()
    {
        const string xmi = """
            <xmi:XMI xmlns:xmi="http://www.omg.org/spec/XMI/20131001" xmlns:uml="http://www.omg.org/spec/UML/20131001"
                     xmlns:umldi="http://www.omg.org/spec/UML/20131001/UMLDI" xmlns:dc="http://www.omg.org/spec/DD/20131001/DC">
              <uml:Model xmi:type="uml:Model" xmi:id="m" name="Tool">
                <packagedElement xmi:type="uml:Interaction" xmi:id="ia" name="Pay">
                  <lifeline xmi:type="uml:Lifeline" xmi:id="l1" name="shop"/>
                  <fragment xmi:type="uml:CombinedFragment" xmi:id="cf" interactionOperator="par">
                    <operand xmi:type="uml:InteractionOperand" xmi:id="o1"/>
                    <operand xmi:type="uml:InteractionOperand" xmi:id="o2"/>
                    <operand xmi:type="uml:InteractionOperand" xmi:id="o3"/>
                  </fragment>
                </packagedElement>
                <packagedElement xmi:type="uml:StateMachine" xmi:id="sm" name="Door">
                  <region xmi:type="uml:Region" xmi:id="r">
                    <subvertex xmi:type="uml:Pseudostate" xmi:id="x" name="weird" kind="dynamicChoice"/>
                    <subvertex xmi:type="uml:Pseudostate" xmi:id="t" kind="terminate"/>
                  </region>
                </packagedElement>
              </uml:Model>
              <umldi:UMLDiagram xmi:id="d" name="Pay" modelElement="ia">
                <ownedElement xmi:type="umldi:UMLShape" xmi:id="s1" modelElement="l1"><bounds xmi:type="dc:Bounds" x="40" y="20" width="120" height="400"/></ownedElement>
                <ownedElement xmi:type="umldi:UMLShape" xmi:id="s2" modelElement="cf"><bounds xmi:type="dc:Bounds" x="20" y="100" width="300" height="240"/></ownedElement>
                <ownedElement xmi:type="umldi:UMLShape" xmi:id="s3" modelElement="o2"><bounds xmi:type="dc:Bounds" x="20" y="170" width="300" height="80"/></ownedElement>
              </umldi:UMLDiagram>
            </xmi:XMI>
            """;
        var (m, report) = XmiExchange.Import(XDocument.Parse(xmi));
        Assert.Equal(Pseudostates.Choice, Pseudostates.KindOf(m.Find("x")!));
        Assert.Equal(Pseudostates.Terminate, Pseudostates.KindOf(m.Find("t")!));
        Assert.Contains(report.Warnings, w => w.StartsWith("Approximated", StringComparison.Ordinal) && w.Contains("dynamicChoice"));

        var d = m.Diagrams.Single();
        d = (Diagram)m.Find(d.Id)!;
        var node = d.NodeFor(m.Find("cf")!)!;
        Assert.DoesNotContain(d.Nodes, n => n.Element.Kind == ElementKind.InteractionOperand);
        Assert.Equal(70, InteractionOperands.StoredOffset(node, m.Find("o2")!));
        Assert.Null(InteractionOperands.StoredOffset(node, m.Find("o3")!));
        Assert.Contains(report.Warnings, w => w.Contains("par fragment: 1 operand separator(s) not in the file"));
        Assert.Equal(["", "", ""], InteractionOperands.Guards(m.Find("cf")!));
    }
}
