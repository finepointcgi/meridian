using System.IO.Compression;
using System.Text;

namespace Meridian.Tests;

/// <summary>
/// Hand-written, MagicDraw-2021x-shaped project content: UML 2.5 XMI with SysML 1.6 / StandardProfile / MagicDraw profile
/// applications as siblings of uml:Model, diagrams under xmi:Extension/modelExtension/ownedDiagram, and diagram symbols
/// (mdElement / geometry / mdOwnedViews) either in separate zip entries (.mdzip) or inline (.mdxml).
/// </summary>
internal static class MagicDrawFixtures
{
    internal const string Header = """
        <?xml version='1.0' encoding='UTF-8'?>
        <xmi:XMI xmlns:uml='http://www.omg.org/spec/UML/20131001' xmlns:xmi='http://www.omg.org/spec/XMI/20131001'
                 xmlns:sysml='http://www.omg.org/spec/SysML/20181001/SysML'
                 xmlns:StandardProfile='http://www.omg.org/spec/UML/20131001/StandardProfile'
                 xmlns:MagicDraw_Profile='http://www.omg.org/spec/UML/20131001/MagicDrawProfile'
                 xmlns:MD_Customization_for_SysML__additional_stereotypes='http://www.magicdraw.com/spec/Customization/180/SysML'
                 xmlns:MyProfile='http://www.magicdraw.com/schemas/MyProfile.xmi'>
          <xmi:Documentation><xmi:exporter>MagicDraw Clean XMI Exporter</xmi:exporter><xmi:exporterVersion>2021x v1</xmi:exporterVersion></xmi:Documentation>
        """;

    /// <summary>An ownedDiagram extension. With <paramref name="inline"/> the symbols are embedded; otherwise a binaryObject points at zip entry <paramref name="binaryId"/>.</summary>
    internal static string Diagram(string id, string name, string owner, string type, string umlType, string binaryId, string? inline)
    {
        var contents = inline != null
            ? $"<diagramContents contentHash='h' exporterName='MagicDraw UML' exporterVersion='2021x' xmi:id='{id}_c'>{inline}</diagramContents>"
            : $"<diagramContents contentHash='h' exporterName='MagicDraw UML' exporterVersion='2021x' xmi:id='{id}_c'><binaryObject streamContentID='{binaryId}' xmi:id='{id}_b' xsi:type='binary:StreamIdentifierBinaryObject'/></diagramContents>";
        return $"""
            <xmi:Extension extender='MagicDraw UML 2021x'>
              <modelExtension>
                <ownedDiagram xmi:type='uml:Diagram' xmi:id='{id}' name='{name}' visibility='public' context='{owner}' ownerOfDiagram='{owner}'>
                  <xmi:Extension extender='MagicDraw UML 2021x'>
                    <diagramRepresentation>
                      <diagram:DiagramRepresentationObject ID='{id}_r' initialFrameSizeSet='true' requiredFeature='com.nomagic.magicdraw.plugins.impl.sysml#SysML;MD_customization_for_SysML.mdzip;UML_Standard_Profile.mdzip'
                          type='{type}' umlType='{umlType}' xmi:id='{id}_ro' xmi:version='2.0'
                          xmlns:binary='http://www.nomagic.com/ns/cameo/client/binary/1.0' xmlns:diagram='http://www.nomagic.com/ns/magicdraw/core/diagram/1.0'
                          xmlns:xmi='http://www.omg.org/XMI' xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance'>
                        {contents}
                      </diagram:DiagramRepresentationObject>
                    </diagramRepresentation>
                  </xmi:Extension>
                </ownedDiagram>
              </modelExtension>
            </xmi:Extension>
            """;
    }

    internal static string Shape(string id, string cls, string element, string geometry, string nested = "") =>
        $"<mdElement elementClass='{cls}' xmi:id='{id}'><elementID xmi:idref='{element}'/><geometry>{geometry}</geometry>" +
        (nested.Length > 0 ? $"<mdOwnedViews>{nested}</mdOwnedViews>" : "") + "</mdElement>";

    internal static string Path(string id, string cls, string element, string first, string second, string geometry) =>
        $"<mdElement elementClass='{cls}' xmi:id='{id}'><elementID xmi:idref='{element}'/><linkFirstEndID xmi:idref='{first}'/><linkSecondEndID xmi:idref='{second}'/><geometry>{geometry}</geometry></mdElement>";

    internal static string Contents(string diagramId, string frameElement, string views) =>
        $"<mdElement elementClass='DiagramPresentationElement' xmi:id='{diagramId}_dpe'><elementID xmi:idref='{diagramId}'/><zoomFactor>1.0</zoomFactor><mdOwnedViews>" +
        Shape(diagramId + "_frame", "DiagramFrame", frameElement, "0, 0, 1200, 900", views) + "</mdOwnedViews></mdElement>";

    internal static string BinaryEntry(string body) =>
        "<?xml version='1.0' encoding='UTF-8'?><diagram:DiagramContents xmlns:xmi='http://www.omg.org/XMI' xmlns:diagram='http://www.nomagic.com/ns/magicdraw/core/diagram/1.0'>" + body + "</diagram:DiagramContents>";

    // ------------------------------------------------------------------ diagram symbol sets

    public static string BddViews =>
        Contents("_bdd", "_pkgS",
            Shape("_vDrone", "Class", "_drone", "100, 80, 220, 160") +
            Shape("_vRotor", "Class", "_rotor", "420, 320, 180, 100") +
            Shape("_vVehicle", "Class", "_vehicle", "100, 10, 160, 50") +
            Shape("_vNote", "TextBox", "_nothere", "700, 10, 50, 50") +
            Path("_vA1", "Association", "_assoc1", "_vRotor", "_vDrone", "420, 360; 250, 360; 250, 240; 240, 200; ") +
            Path("_vG1", "Generalization", "_gen1", "_vDrone", "_vVehicle", "180, 80; 180, 60; "));

    public static string IbdViews =>
        Contents("_ibd", "_drone",
            Shape("_vRotors", "Part", "_p_rotors", "200, 150, 200, 120", Shape("_vRp", "Port", "_rp", "392, 200, 16, 16")) +
            Shape("_vPort1", "Port", "_port1", "-8, 200, 16, 16") +
            Shape("_vEq", "ConstraintProperty", "_p_eq", "600, 400, 200, 100", Shape("_vCbm", "ConstraintParameter", "_cb_m", "592, 440, 16, 16")) +
            Shape("_vMass", "Part", "_p_mass", "300, 600, 150, 50") +
            Path("_vConn", "Connector", "_conn1", "_vRp", "_vPort1", "400, 208; 200, 208; 0, 208; ") +
            Path("_vBind", "BindingConnector", "_bind1", "_vMass", "_vCbm", "450, 620; 520, 620; 520, 448; 600, 448; "));

    public static string ActViews =>
        Contents("_actd", "_act1",
            Shape("_vLane", "Swimlane", "_part1", "40, 40, 600, 800") +
            Shape("_vIn", "InitialNode", "_in", "100, 80, 30, 30") +
            Shape("_vA1", "CallBehaviorAction", "_a1", "80, 160, 140, 60", Shape("_vPin1", "InputPin", "_pin1", "72, 180, 16, 16")) +
            Shape("_vDec", "DecisionNode", "_dec", "130, 260, 40, 40") +
            Shape("_vA2", "OpaqueAction", "_a2", "80, 360, 140, 60", Shape("_vPin2", "OutputPin", "_pin2", "212, 380, 16, 16")) +
            Shape("_vFin", "ActivityFinalNode", "_fin", "300, 270, 30, 30") +
            Path("_vCf1", "ControlFlow", "_cf1", "_vIn", "_vA1", "115, 110; 115, 160; ") +
            Path("_vCf3", "ControlFlow", "_cf3", "_vDec", "_vA2", "150, 300; 150, 360; "));

    public static string StmViews =>
        Contents("_stmd", "_sm",
            Shape("_vPs", "Pseudostate", "_ps", "40, 40, 20, 20") +
            Shape("_vIdle", "State", "_idle", "120, 30, 120, 60") +
            Shape("_vFly", "State", "_flying", "320, 30, 120, 60") +
            Path("_vT2", "Transition", "_t2", "_vIdle", "_vFly", "240, 60; 320, 60; "));

    public static string SeqViews =>
        Contents("_seqd", "_int",
            Shape("_vL1", "Lifeline", "_ll1", "60, 40, 150, 400") +
            Shape("_vL2", "Lifeline", "_ll2", "320, 40, 150, 400", Shape("_vExec", "BehaviorExecutionSpecification", "_exec1", "390, 120, 10, 60")) +
            Shape("_vCF", "CombinedFragment", "_cfrag", "30, 200, 480, 120") +
            Path("_vM1", "Message", "_m1", "_vExec", "_vL1", "395, 130; 135, 130; ") +
            Path("_vM2", "Message", "_m2", "_vL2", "_vL1", "395, 250; 135, 250; "));

    public static string ReqViews =>
        Contents("_reqd", "_pkgR",
            Shape("_vR1", "Requirement", "_req1", "50, 50, 220, 120") +
            Shape("_vR2", "Requirement", "_req2", "400, 50, 220, 120") +
            Path("_vD1", "Abstraction", "_der1", "_vR2", "_vR1", "400, 110; 270, 110; "));

    // ------------------------------------------------------------------ the model

    /// <summary>The project XMI. <paramref name="inline"/> embeds diagram symbols (as an .mdxml export does).</summary>
    public static string Xmi(bool inline, string droneName = "Drone", string extraBlocks = "")
    {
        string D(string id, string name, string owner, string type, string uml, string views) =>
            Diagram(id, name, owner, type, uml, "BINARY-" + id.Trim('_'), inline ? views : null);

        return Header + $"""
          <uml:Model xmi:type='uml:Model' xmi:id='_model' name='Drone Project'>
            <ownedComment xmi:type='uml:Comment' xmi:id='_model_doc' body='Project documentation'>
              <annotatedElement xmi:idref='_model'/>
            </ownedComment>
            <packagedElement xmi:type='uml:Package' xmi:id='_pkgS' name='Structure'>
              <packagedElement xmi:type='uml:Class' xmi:id='_drone' name='{droneName}'>
                <ownedComment xmi:type='uml:Comment' xmi:id='_drone_doc' body='&lt;html&gt;&lt;head&gt;&lt;/head&gt;&lt;body&gt;&lt;p&gt;A small &amp;amp; fast quadcopter&lt;/p&gt;&lt;/body&gt;&lt;/html&gt;'>
                  <annotatedElement xmi:idref='_drone'/>
                </ownedComment>
                <ownedComment xmi:type='uml:Comment' xmi:id='_drone_note' body='Reviewed by systems team'/>
                <generalization xmi:type='uml:Generalization' xmi:id='_gen1' general='_vehicle'/>
                <ownedAttribute xmi:type='uml:Property' xmi:id='_p_rotors' name='rotors' visibility='private' aggregation='composite' type='_rotor' association='_assoc1'>
                  <lowerValue xmi:type='uml:LiteralInteger' xmi:id='_p_rotors_lo' value='4'/>
                  <upperValue xmi:type='uml:LiteralUnlimitedNatural' xmi:id='_p_rotors_hi' value='4'/>
                </ownedAttribute>
                <ownedAttribute xmi:type='uml:Property' xmi:id='_p_ctrl' name='controller' type='_ctrl'>
                  <lowerValue xmi:type='uml:LiteralInteger' xmi:id='_p_ctrl_lo'/>
                  <upperValue xmi:type='uml:LiteralUnlimitedNatural' xmi:id='_p_ctrl_hi' value='1'/>
                </ownedAttribute>
                <ownedAttribute xmi:type='uml:Property' xmi:id='_p_mass' name='mass' aggregation='composite' type='_kg'>
                  <defaultValue xmi:type='uml:LiteralReal' xmi:id='_p_mass_dv' value='1.2'/>
                </ownedAttribute>
                <ownedAttribute xmi:type='uml:Property' xmi:id='_p_speed' name='speed' aggregation='composite'>
                  <type href='http://www.omg.org/spec/UML/20131001/PrimitiveTypes.xmi#Real'/>
                </ownedAttribute>
                <ownedAttribute xmi:type='uml:Property' xmi:id='_p_alt' name='altitude' aggregation='composite'>
                  <type href='SysML.mdzip#SysML_dataType_Length'>
                    <xmi:Extension extender='MagicDraw UML 2021x'><referenceExtension referentPath='SysML::Libraries::UnitAndQuantityKind::length' referentType='DataType'/></xmi:Extension>
                  </type>
                </ownedAttribute>
                <ownedAttribute xmi:type='uml:Property' xmi:id='_p_eq' name='massEq' aggregation='composite' type='_cb'/>
                <ownedAttribute xmi:type='uml:Port' xmi:id='_port1' name='power' aggregation='composite' type='_ifb'/>
                <ownedAttribute xmi:type='uml:Port' xmi:id='_port2' name='sensor' aggregation='composite' isConjugated='true' type='_ifb'/>
                <ownedAttribute xmi:type='uml:Port' xmi:id='_port3' name='fuel' aggregation='composite'/>
                <ownedConnector xmi:type='uml:Connector' xmi:id='_conn1' name='drive'>
                  <end xmi:type='uml:ConnectorEnd' xmi:id='_conn1_e1' role='_rp' partWithPort='_p_rotors'/>
                  <end xmi:type='uml:ConnectorEnd' xmi:id='_conn1_e2' role='_port1'/>
                </ownedConnector>
                <ownedConnector xmi:type='uml:Connector' xmi:id='_bind1'>
                  <end xmi:type='uml:ConnectorEnd' xmi:id='_bind1_e1' role='_p_mass'/>
                  <end xmi:type='uml:ConnectorEnd' xmi:id='_bind1_e2' role='_cb_m' partWithPort='_p_eq'/>
                </ownedConnector>
                <ownedOperation xmi:type='uml:Operation' xmi:id='_op_land' name='land'>
                  <ownedParameter xmi:type='uml:Parameter' xmi:id='_op_land_p1' name='speed'><type href='http://www.omg.org/spec/UML/20131001/PrimitiveTypes.xmi#Real'/></ownedParameter>
                  <ownedParameter xmi:type='uml:Parameter' xmi:id='_op_land_r' direction='return'><type href='http://www.omg.org/spec/UML/20131001/PrimitiveTypes.xmi#Boolean'/></ownedParameter>
                </ownedOperation>
                {D("_ibd", "Drone internals", "_drone", "SysML Internal Block Diagram", "Composite Structure Diagram", IbdViews)}
              </packagedElement>
              <packagedElement xmi:type='uml:Class' xmi:id='_rotor' name='Rotor'>
                <ownedAttribute xmi:type='uml:Port' xmi:id='_rp' name='shaft' aggregation='composite' type='_ifb'/>
              </packagedElement>
              <packagedElement xmi:type='uml:Class' xmi:id='_ctrl' name='Controller'/>
              <packagedElement xmi:type='uml:Class' xmi:id='_vehicle' name='Vehicle' isAbstract='true'/>
              <packagedElement xmi:type='uml:Class' xmi:id='_ifb' name='PowerIF'>
                <ownedAttribute xmi:type='uml:Property' xmi:id='_fp' name='current' aggregation='composite'><type href='http://www.omg.org/spec/UML/20131001/PrimitiveTypes.xmi#Real'/></ownedAttribute>
              </packagedElement>
              <packagedElement xmi:type='uml:DataType' xmi:id='_kg' name='Mass'/>
              <packagedElement xmi:type='uml:Enumeration' xmi:id='_mode' name='Mode'>
                <ownedLiteral xmi:type='uml:EnumerationLiteral' xmi:id='_mode_a' name='Auto'/>
                <ownedLiteral xmi:type='uml:EnumerationLiteral' xmi:id='_mode_m' name='Manual'/>
              </packagedElement>
              <packagedElement xmi:type='uml:Class' xmi:id='_cb' name='MassEq'>
                <ownedRule xmi:type='uml:Constraint' xmi:id='_cb_rule'>
                  <constrainedElement xmi:idref='_cb'/>
                  <specification xmi:type='uml:OpaqueExpression' xmi:id='_cb_spec'><body>m = sum(parts)</body><language>English</language></specification>
                </ownedRule>
                <ownedAttribute xmi:type='uml:Property' xmi:id='_cb_m' name='m' aggregation='composite'><type href='http://www.omg.org/spec/UML/20131001/PrimitiveTypes.xmi#Real'/></ownedAttribute>
              </packagedElement>
              <packagedElement xmi:type='uml:Association' xmi:id='_assoc1' memberEnd='_p_rotors _assoc1_end'>
                <ownedEnd xmi:type='uml:Property' xmi:id='_assoc1_end' type='_drone' association='_assoc1'/>
              </packagedElement>
              <packagedElement xmi:type='uml:InstanceSpecification' xmi:id='_inst' name='drone1' classifier='_drone'>
                <slot xmi:type='uml:Slot' xmi:id='_inst_mass' definingFeature='_p_mass'><value xmi:type='uml:LiteralReal' xmi:id='_inst_mass_v' value='1.4'/></slot>
                <slot xmi:type='uml:Slot' xmi:id='_inst_ctrl' definingFeature='_p_ctrl'><value xmi:type='uml:InstanceValue' xmi:id='_inst_ctrl_v' instance='_inst2'/></slot>
              </packagedElement>
              <packagedElement xmi:type='uml:InstanceSpecification' xmi:id='_inst2' name='fc1'><classifier xmi:idref='_ctrl'/></packagedElement>
              <packagedElement xmi:type='uml:InstanceSpecification' xmi:id='_inst3' name='rotor1' classifier='_rotor'/>
              <packagedElement xmi:type='uml:InstanceSpecification' xmi:id='_lnk' classifier='_assoc1'>
                <slot xmi:type='uml:Slot' xmi:id='_lnk_s1' definingFeature='_assoc1_end'><value xmi:type='uml:InstanceValue' xmi:id='_lnk_s1_v' instance='_inst'/></slot>
                <slot xmi:type='uml:Slot' xmi:id='_lnk_s2' definingFeature='_p_rotors'><value xmi:type='uml:InstanceValue' xmi:id='_lnk_s2_v' instance='_inst3'/></slot>
              </packagedElement>
              <packagedElement xmi:type='uml:Frobnicator' xmi:id='_weird' name='Unknown thing'/>
              {extraBlocks}
              {D("_bdd", "Drone structure", "_pkgS", "SysML Block Definition Diagram", "Class Diagram", BddViews)}
              {D("_tbl", "Parts table", "_pkgS", "Generic Table", "", "")}
            </packagedElement>
            <packagedElement xmi:type='uml:Package' xmi:id='_pkgR' name='Requirements'>
              <packagedElement xmi:type='uml:Class' xmi:id='_req1' name='Endurance'/>
              <packagedElement xmi:type='uml:Class' xmi:id='_req2' name='Wind resistance'/>
              <packagedElement xmi:type='uml:Abstraction' xmi:id='_sat1' client='_drone' supplier='_req1'/>
              <packagedElement xmi:type='uml:Abstraction' xmi:id='_der1'><client xmi:idref='_req2'/><supplier xmi:idref='_req1'/></packagedElement>
              <packagedElement xmi:type='uml:Abstraction' xmi:id='_ver1' client='_tc' supplier='_req1'/>
              <packagedElement xmi:type='uml:Abstraction' xmi:id='_ref1' client='_uc1' supplier='_req2'/>
              <packagedElement xmi:type='uml:Abstraction' xmi:id='_tr1' client='_ctrl' supplier='_req2'/>
              <packagedElement xmi:type='uml:Abstraction' xmi:id='_alloc1' client='_act1' supplier='_drone'/>
              <packagedElement xmi:type='uml:Abstraction' xmi:id='_badsat' client='_req2' supplier='_drone'/>
              <packagedElement xmi:type='uml:Dependency' xmi:id='_dep1' client='_ctrl' supplier='_external_thing'/>
              {D("_reqd", "Drone requirements", "_pkgR", "SysML Requirement Diagram", "Class Diagram", ReqViews)}
            </packagedElement>
            <packagedElement xmi:type='uml:Package' xmi:id='_pkgB' name='Behavior'>
              <packagedElement xmi:type='uml:Activity' xmi:id='_act1' name='Fly Mission'>
                <node xmi:type='uml:InitialNode' xmi:id='_in'/>
                <node xmi:type='uml:CallBehaviorAction' xmi:id='_a1' name='Take off' behavior='_act2'>
                  <argument xmi:type='uml:InputPin' xmi:id='_pin1' name='payload' type='_kg'/>
                </node>
                <node xmi:type='uml:DecisionNode' xmi:id='_dec'/>
                <node xmi:type='uml:OpaqueAction' xmi:id='_a2' name='Cruise'>
                  <body>fly(route)</body>
                  <outputValue xmi:type='uml:OutputPin' xmi:id='_pin2' name='telemetry'/>
                </node>
                <node xmi:type='uml:SendSignalAction' xmi:id='_a3' name='Notify' signal='_sigStart'/>
                <node xmi:type='uml:ActivityFinalNode' xmi:id='_fin'/>
                <node xmi:type='uml:StructuredActivityNode' xmi:id='_san' name='Block of work'/>
                <edge xmi:type='uml:ControlFlow' xmi:id='_cf1' source='_in' target='_a1'>
                  <guard xmi:type='uml:LiteralBoolean' xmi:id='_cf1_g' value='true'/>
                </edge>
                <edge xmi:type='uml:ControlFlow' xmi:id='_cf2' source='_a1' target='_dec'/>
                <edge xmi:type='uml:ControlFlow' xmi:id='_cf3' source='_dec' target='_a2'>
                  <guard xmi:type='uml:OpaqueExpression' xmi:id='_cf3_g'><body>battery &gt; 20</body></guard>
                </edge>
                <edge xmi:type='uml:ControlFlow' xmi:id='_cf4' source='_dec' target='_fin'/>
                <edge xmi:type='uml:ObjectFlow' xmi:id='_of1' source='_pin2' target='_pin1'/>
                <group xmi:type='uml:ActivityPartition' xmi:id='_part1' name='Drone' represents='_drone' node='_a1 _a2'/>
                {D("_actd", "Fly mission", "_act1", "SysML Activity Diagram", "Activity Diagram", ActViews)}
              </packagedElement>
              <packagedElement xmi:type='uml:Activity' xmi:id='_act2' name='Take off'/>
              <packagedElement xmi:type='uml:Activity' xmi:id='_tc' name='Hover test'>
                <node xmi:type='uml:OpaqueAction' xmi:id='_tca' name='Hover 60 s'/>
              </packagedElement>
              <packagedElement xmi:type='uml:Signal' xmi:id='_sigStart' name='Start'/>
              <packagedElement xmi:type='uml:SignalEvent' xmi:id='_evStart' signal='_sigStart'/>
              <packagedElement xmi:type='uml:TimeEvent' xmi:id='_ev30' isRelative='true'>
                <when xmi:type='uml:TimeExpression' xmi:id='_ev30_w'><expr xmi:type='uml:LiteralString' xmi:id='_ev30_e' value='30 min'/></when>
              </packagedElement>
              <packagedElement xmi:type='uml:StateMachine' xmi:id='_sm' name='Drone modes'>
                <region xmi:type='uml:Region' xmi:id='_r1' name=''>
                  <subvertex xmi:type='uml:Pseudostate' xmi:id='_ps'/>
                  <subvertex xmi:type='uml:State' xmi:id='_idle' name='Idle'>
                    <entry xmi:type='uml:Activity' xmi:id='_idle_entry' name='self check'/>
                  </subvertex>
                  <subvertex xmi:type='uml:State' xmi:id='_flying' name='Flying'>
                    <doActivity xmi:type='uml:OpaqueBehavior' xmi:id='_fly_do'><body>hover()</body></doActivity>
                  </subvertex>
                  <subvertex xmi:type='uml:Pseudostate' xmi:id='_ch' kind='choice'/>
                  <subvertex xmi:type='uml:FinalState' xmi:id='_fs'/>
                  <transition xmi:type='uml:Transition' xmi:id='_t1' source='_ps' target='_idle'/>
                  <transition xmi:type='uml:Transition' xmi:id='_t2' source='_idle' target='_flying'>
                    <trigger xmi:type='uml:Trigger' xmi:id='_t2_tr' event='_evStart'/>
                    <guard xmi:type='uml:Constraint' xmi:id='_t2_g'><specification xmi:type='uml:OpaqueExpression' xmi:id='_t2_gs'><body>armed</body></specification></guard>
                    <effect xmi:type='uml:OpaqueBehavior' xmi:id='_t2_fx'><body>spinUp()</body></effect>
                  </transition>
                  <transition xmi:type='uml:Transition' xmi:id='_t3' source='_flying' target='_fs'>
                    <trigger xmi:type='uml:Trigger' xmi:id='_t3_tr' event='_ev30'/>
                  </transition>
                </region>
                {D("_stmd", "Modes", "_sm", "SysML State Machine Diagram", "State Machine Diagram", StmViews)}
              </packagedElement>
              <packagedElement xmi:type='uml:Collaboration' xmi:id='_collab' name='Arm collaboration'>
                <ownedAttribute xmi:type='uml:Property' xmi:id='_llp1' name='pilotSide' type='_drone'/>
                <ownedAttribute xmi:type='uml:Property' xmi:id='_llp2' name='ctl' type='_ctrl'/>
                <ownedBehavior xmi:type='uml:Interaction' xmi:id='_int' name='Arm sequence'>
                  <lifeline xmi:type='uml:Lifeline' xmi:id='_ll1' represents='_llp1' coveredBy='_mos2 _mos4'/>
                  <lifeline xmi:type='uml:Lifeline' xmi:id='_ll2' name='controller' represents='_llp2' coveredBy='_mos1 _mos3'/>
                  <fragment xmi:type='uml:MessageOccurrenceSpecification' xmi:id='_mos1' covered='_ll2' message='_m1'/>
                  <fragment xmi:type='uml:BehaviorExecutionSpecification' xmi:id='_exec1' covered='_ll2' start='_mos1' finish='_mos1'/>
                  <fragment xmi:type='uml:MessageOccurrenceSpecification' xmi:id='_mos2' covered='_ll1' message='_m1'/>
                  <fragment xmi:type='uml:CombinedFragment' xmi:id='_cfrag' interactionOperator='alt'>
                    <operand xmi:type='uml:InteractionOperand' xmi:id='_op1'>
                      <guard xmi:type='uml:InteractionConstraint' xmi:id='_op1_g'><specification xmi:type='uml:OpaqueExpression' xmi:id='_op1_gs'><body>ok</body></specification></guard>
                      <fragment xmi:type='uml:MessageOccurrenceSpecification' xmi:id='_mos3' covered='_ll2' message='_m2'/>
                      <fragment xmi:type='uml:MessageOccurrenceSpecification' xmi:id='_mos4' covered='_ll1' message='_m2'/>
                    </operand>
                    <operand xmi:type='uml:InteractionOperand' xmi:id='_op2'>
                      <guard xmi:type='uml:InteractionConstraint' xmi:id='_op2_g'><specification xmi:type='uml:LiteralString' xmi:id='_op2_gs' value='else'/></guard>
                    </operand>
                  </fragment>
                  <fragment xmi:type='uml:MessageOccurrenceSpecification' xmi:id='_mos5' covered='_ll1' message='_m3'/>
                  <message xmi:type='uml:Message' xmi:id='_m1' name='arm' sendEvent='_mos1' receiveEvent='_mos2'/>
                  <message xmi:type='uml:Message' xmi:id='_m2' name='status' messageSort='asynchSignal' sendEvent='_mos3' receiveEvent='_mos4'/>
                  <message xmi:type='uml:Message' xmi:id='_m3' name='lost' messageKind='lost' sendEvent='_mos5'/>
                  {D("_seqd", "Arm", "_int", "SysML Sequence Diagram", "Sequence Diagram", SeqViews)}
                </ownedBehavior>
              </packagedElement>
              <packagedElement xmi:type='uml:Actor' xmi:id='_actor' name='Pilot'/>
              <packagedElement xmi:type='uml:UseCase' xmi:id='_uc1' name='Fly drone'>
                <include xmi:type='uml:Include' xmi:id='_inc1' addition='_uc2'/>
              </packagedElement>
              <packagedElement xmi:type='uml:UseCase' xmi:id='_uc2' name='Pre-flight check'/>
              <packagedElement xmi:type='uml:Association' xmi:id='_assoc2' memberEnd='_assoc2_a _assoc2_b'>
                <ownedEnd xmi:type='uml:Property' xmi:id='_assoc2_a' type='_actor' association='_assoc2'/>
                <ownedEnd xmi:type='uml:Property' xmi:id='_assoc2_b' type='_uc1' association='_assoc2'/>
              </packagedElement>
              {Diagram("_ucd", "Pilot use cases", "_pkgB", "SysML Use Case Diagram", "Use Case Diagram", "BINARY-missing", null)}
            </packagedElement>
          </uml:Model>
          <sysml:Block xmi:id='_drone_st' base_Class='_drone'/>
          <sysml:Block xmi:id='_rotor_st' base_Class='_rotor'/>
          <sysml:Block xmi:id='_ctrl_st' base_Class='_ctrl'/>
          <sysml:Block xmi:id='_vehicle_st' base_Class='_vehicle'/>
          <sysml:InterfaceBlock xmi:id='_ifb_st' base_Class='_ifb'/>
          <sysml:FlowProperty xmi:id='_fp_st' base_Property='_fp' direction='out'/>
          <sysml:ValueType xmi:id='_kg_st' base_DataType='_kg'/>
          <sysml:ConstraintBlock xmi:id='_cb_st' base_Class='_cb'/>
          <MD_Customization_for_SysML__additional_stereotypes:PartProperty xmi:id='_p_rotors_st' base_Property='_p_rotors'/>
          <MD_Customization_for_SysML__additional_stereotypes:ValueProperty xmi:id='_p_mass_st' base_Property='_p_mass'/>
          <MD_Customization_for_SysML__additional_stereotypes:ConstraintProperty xmi:id='_p_eq_st' base_Property='_p_eq'/>
          <sysml:ProxyPort xmi:id='_port1_st' base_Port='_port1'/>
          <sysml:ProxyPort xmi:id='_rp_st' base_Port='_rp'/>
          <sysml:FullPort xmi:id='_port2_st' base_Port='_port2'/>
          <sysml:FlowPort xmi:id='_port3_st' base_Port='_port3' direction='in'/>
          <sysml:BindingConnector xmi:id='_bind1_st' base_Connector='_bind1'/>
          <sysml:Requirement xmi:id='_req1_st' base_Class='_req1' Id='REQ-1' Text='&lt;html&gt;&lt;body&gt;&lt;p&gt;Fly at least 30 minutes&lt;/p&gt;&lt;/body&gt;&lt;/html&gt;'/>
          <sysml:Requirement xmi:id='_req2_st' base_Class='_req2' Id='REQ-2'><Text>Withstand 10 m/s wind</Text></sysml:Requirement>
          <sysml:Satisfy xmi:id='_sat1_st' base_Abstraction='_sat1'/>
          <sysml:Satisfy xmi:id='_badsat_st' base_Abstraction='_badsat'/>
          <sysml:DeriveReqt xmi:id='_der1_st' base_Abstraction='_der1'/>
          <sysml:Verify xmi:id='_ver1_st' base_Abstraction='_ver1'/>
          <sysml:TestCase xmi:id='_tc_st' base_Behavior='_tc'/>
          <StandardProfile:Refine xmi:id='_ref1_st' base_Abstraction='_ref1'/>
          <StandardProfile:Trace xmi:id='_tr1_st' base_Abstraction='_tr1'/>
          <sysml:Allocate xmi:id='_alloc1_st' base_Abstraction='_alloc1'/>
          <MyProfile:Critical xmi:id='_crit' base_Class='_drone' level='high'><reviewers>Ana</reviewers><reviewers>Bo</reviewers></MyProfile:Critical>
          <MagicDraw_Profile:DiagramInfo xmi:id='_di' base_Diagram='_bdd' Creation_date='2024-01-01'/>
          <xmi:Extension extender='MagicDraw UML 2021x'><filePart name='project.options' type='XML'>ignored</filePart></xmi:Extension>
        </xmi:XMI>
        """;
    }

    /// <summary>Writes an .mdzip with the model entry, diagram binary entries and the usual MagicDraw side resources.</summary>
    public static string WriteMdzip(string dir, string fileName = "drone.mdzip", string? xmi = null, IReadOnlyDictionary<string, string>? binaries = null)
    {
        var path = System.IO.Path.Combine(dir, fileName);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            var e = zip.CreateEntry(name);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        Add("records.properties", "# MagicDraw records\n");
        Add("PROJECT-f41072fa-d1aa-4e90-b7d9-fdd8862ed8af", "");
        Add("PROJECT-f41072fa.com.nomagic.ci.metamodel.project", "<project/>");
        Add("f41072fa.com.nomagic.magicdraw.core.project.options.commonprojectoptions", "<options/>");
        Add("proxy.local__SysML_profile.mdzip", "<proxy/>");
        Add("f41072fa.com.nomagic.magicdraw.uml_model.model", xmi ?? Xmi(inline: false));
        foreach (var (k, v) in binaries ?? DefaultBinaries) Add(k, v);
        return path;
    }

    public static IReadOnlyDictionary<string, string> DefaultBinaries => new Dictionary<string, string>
    {
        ["BINARY-bdd"] = BinaryEntry(BddViews),
        ["BINARY-ibd"] = BinaryEntry(IbdViews),
        ["BINARY-actd"] = BinaryEntry(ActViews),
        ["BINARY-stmd"] = BinaryEntry(StmViews),
        ["BINARY-seqd"] = BinaryEntry(SeqViews),
        ["BINARY-reqd"] = BinaryEntry(ReqViews),
    };

    /// <summary>A large synthetic project: <paramref name="packages"/> × <paramref name="blocksPerPackage"/> blocks with two properties each, plus a generalization chain.</summary>
    public static string WriteLargeMdzip(string dir, int packages, int blocksPerPackage)
    {
        var path = System.IO.Path.Combine(dir, "large.mdzip");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var e = zip.CreateEntry("x.com.nomagic.magicdraw.uml_model.model", CompressionLevel.Fastest);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(Header);
        w.Write("<uml:Model xmi:type='uml:Model' xmi:id='_m' name='Large'>");
        var stereo = new StringBuilder();
        for (var p = 0; p < packages; p++)
        {
            w.Write($"<packagedElement xmi:type='uml:Package' xmi:id='_p{p}' name='P{p}'>");
            for (var b = 0; b < blocksPerPackage; b++)
            {
                var id = $"_b{p}_{b}";
                w.Write($"<packagedElement xmi:type='uml:Class' xmi:id='{id}' name='B{p}_{b}'>");
                if (b > 0) w.Write($"<generalization xmi:type='uml:Generalization' xmi:id='{id}_g' general='_b{p}_{b - 1}'/>");
                w.Write($"<ownedAttribute xmi:type='uml:Property' xmi:id='{id}_a' name='mass' aggregation='composite'><type href='http://www.omg.org/spec/UML/20131001/PrimitiveTypes.xmi#Real'/></ownedAttribute>");
                w.Write($"<ownedAttribute xmi:type='uml:Property' xmi:id='{id}_c' name='next' type='_b{p}_{(b + 1) % blocksPerPackage}'/>");
                w.Write("</packagedElement>");
                stereo.Append($"<sysml:Block xmi:id='{id}_st' base_Class='{id}'/>");
            }
            w.Write("</packagedElement>");
        }
        w.Write("</uml:Model>");
        w.Write(stereo.ToString());
        w.Write("</xmi:XMI>");
        return path;
    }
}
