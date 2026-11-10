using Meridian.Core.Analysis;
using Meridian.Core.Model;

namespace Meridian.Core.Samples;

/// <summary>
/// Automotive sample: adaptive cruise control (ACC) with an ISO 26262-flavoured safety concept. Safety goals with
/// ASIL tags are refined into functional and technical safety requirements (deriveReqt), the functional architecture is
/// an activity whose swimlanes allocate each function to an ECU, the physical architecture is an IBD of ECUs connected
/// over the vehicle network, and the ACC modes are a state machine.
/// <para>
/// The ASIL ratings are illustrative, not the result of a real hazard analysis and risk assessment.
/// Deliberate gap: <c>ACC-007 Stop and Go</c> is proposed and has no satisfying element or test yet.
/// </para>
/// </summary>
public static class AdaptiveCruiseSample
{
    public const string Name = "Adaptive Cruise Control";

    public static ModelRepository Create(string? name = null)
    {
        var m = new ModelRepository(string.IsNullOrWhiteSpace(name) ? Name : name.Trim());
        var k = new SampleKit(m);
        m.Set(m.Root, FieldKeys.Documentation,
            "Adaptive cruise control for a passenger car. Worked automotive example: ISO 26262-style safety goals and safety " +
            "requirements with ASIL tags, functional architecture, allocation to ECUs and verification. ASIL ratings are illustrative.");

        // ------------------------------------------------------------ profile
        var profile = m.Create(m.Root, ElementKind.Profile, "ISO 26262");
        m.Set(profile, FieldKeys.Documentation, "Stereotypes for the safety concept: safety goals, functional and technical safety requirements, ECUs.");
        const string Asil = "QM, A, B, C, D";
        k.Stereotype(profile, "SafetyGoal", "Requirement", "#f8cecc", ("ASIL", "Enum", Asil), ("safeState", "String", null), ("FTTI", "String", null));
        k.Stereotype(profile, "FunctionalSafetyRequirement", "Requirement", "#ffe6cc", ("ASIL", "Enum", Asil));
        k.Stereotype(profile, "TechnicalSafetyRequirement", "Requirement", "#fff2cc", ("ASIL", "Enum", Asil));
        k.Stereotype(profile, "ECU", "Block", "#d5e8d4", ("supplier", "String", null), ("asilCapability", "Enum", Asil));

        var safetyPkg = k.Pkg(m.Root, "Safety Concept", "Safety goals (from the HARA), functional safety requirements and technical safety requirements.");
        var reqPkg = k.Pkg(m.Root, "Functional Requirements", "Customer-level ACC requirements (QM).");
        var funcPkg = k.Pkg(m.Root, "Functional Architecture", "ACC functions as activities; swimlanes allocate them to ECUs.");
        var physPkg = k.Pkg(m.Root, "Physical Architecture", "ECUs, sensors and the in-vehicle network.");
        var ifPkg = k.Pkg(physPkg, "Signals", "Interface blocks for the signals exchanged on CAN-FD.");
        var behavior = k.Pkg(m.Root, "Operation", "Driver use cases and the ACC mode state machine.");
        var verification = k.Pkg(m.Root, "Verification", "Unit, integration, HIL and vehicle tests.");

        // ------------------------------------------------------------ functional requirements (QM)
        Element Fr(string id, string name, string text, string method, params (string, string)[] extra) =>
            k.Req(reqPkg, id, name, text, [(ReqFields.Status, "approved"), (ReqFields.Priority, "high"), (ReqFields.VerificationMethod, method), .. extra]);
        var setSpeed = Fr("ACC-001", "Set Speed Range", "The ACC shall maintain a driver-selected set speed between 30 and 180 km/h.", "test");
        var timeGap = Fr("ACC-002", "Time Gap", "The ACC shall maintain a driver-selectable time gap of 1.0, 1.5, 2.0 or 2.5 s to the target vehicle.", "test");
        var range = Fr("ACC-003", "Target Detection Range", "The ACC shall detect vehicles in the ego lane up to 150 m ahead.", "test");
        var overrideReq = Fr("ACC-004", "Driver Override", "Pressing the brake pedal shall deactivate the ACC within 100 ms.", "test");
        var decel = Fr("ACC-005", "Comfort Deceleration Limit", "ACC-commanded deceleration shall not exceed 3.5 m/s2 (ISO 15622).", "test");
        var hmi = Fr("ACC-006", "Status Display", "The instrument cluster shall display the ACC state, the set speed and the selected time gap.", "inspection",
            (ReqFields.Priority, "medium"));
        var stopGo = k.Req(reqPkg, "ACC-007", "Stop and Go",
            "The ACC shall bring the vehicle to a standstill behind a stopped target and resume within 3 s after driver confirmation.",
            (ReqFields.Status, "proposed"), (ReqFields.Priority, "low"), (ReqFields.VerificationMethod, "test"),
            (ReqFields.Rationale, "Planned for the next model year. Deliberately left unsatisfied and unverified in this sample."));

        // ------------------------------------------------------------ safety goals and safety requirements
        Element Safety(string stereo, string id, string name, string text, string asil, params (string, string)[] tags)
        {
            var r = k.Req(safetyPkg, id, name, text, (ReqFields.Status, "approved"), (ReqFields.Priority, "critical"),
                (ReqFields.VerificationMethod, "test"), (ReqFields.CustomPrefix + "Safety level", "ASIL " + asil));
            m.Set(r, FieldKeys.Stereotypes, stereo);
            k.Tag(r, "ASIL", asil);
            foreach (var (t, v) in tags) k.Tag(r, t, v);
            return r;
        }
        var sg1 = Safety("SafetyGoal", "SG-01", "Avoid Unintended Acceleration",
            "Unintended vehicle acceleration above 2 m/s2 for longer than 200 ms shall be prevented.", "B", ("safeState", "ACC off, zero torque request"), ("FTTI", "200 ms"));
        var sg2 = Safety("SafetyGoal", "SG-02", "Avoid Unintended Braking",
            "Unintended ACC-commanded deceleration above 4 m/s2 shall be prevented.", "B", ("safeState", "ACC off, brake request released"), ("FTTI", "150 ms"));
        var sg3 = Safety("SafetyGoal", "SG-03", "Indicate Loss of Target Detection",
            "A loss of target detection shall be indicated to the driver within 300 ms.", "A", ("safeState", "ACC off, driver warned"), ("FTTI", "300 ms"));
        var fsr1 = Safety("FunctionalSafetyRequirement", "FSR-01", "Limit Acceleration Request", "The ACC function shall limit its acceleration request to 2 m/s2.", "B");
        var fsr2 = Safety("FunctionalSafetyRequirement", "FSR-02", "Plausibilise Target",
            "The ACC function shall confirm the selected target with an independent camera object before requesting more than 2 m/s2 deceleration.", "B");
        var fsr3 = Safety("FunctionalSafetyRequirement", "FSR-03", "Independent Deceleration Monitor",
            "The brake system shall independently limit ACC deceleration requests to 4 m/s2.", "B");
        var fsr4 = Safety("FunctionalSafetyRequirement", "FSR-04", "Detect Sensor Faults",
            "Radar blindness or loss of communication shall be detected within 200 ms; the ACC shall then enter the Fault mode and warn the driver.", "A");
        var tsr1 = Safety("TechnicalSafetyRequirement", "TSR-01", "End-to-End Protection",
            "Object list messages from the radar shall be protected by a CRC and an alive counter (AUTOSAR E2E profile 5).", "A");
        var tsr2 = Safety("TechnicalSafetyRequirement", "TSR-02", "Torque Request Range Check",
            "The engine ECU shall reject ACC torque requests that correspond to more than 2 m/s2 acceleration.", "B");
        var tsr3 = Safety("TechnicalSafetyRequirement", "TSR-03", "Brake Monitor Cycle Time",
            "The brake ECU deceleration monitor shall execute every 10 ms.", "B");
        foreach (var (child, parent) in new[] { (fsr1, sg1), (fsr2, sg1), (fsr2, sg2), (fsr3, sg2), (fsr4, sg3), (tsr1, fsr4), (tsr2, fsr1), (tsr3, fsr3) })
            m.Connect(ElementKind.DeriveReqt, child, parent);

        // ------------------------------------------------------------ physical architecture
        var speedIf = k.Block(ifPkg, "ObjectList", "Tracked objects: distance, relative speed, lateral offset, quality.", kind: ElementKind.InterfaceBlock);
        var torqueIf = k.Block(ifPkg, "TorqueRequest", "Requested wheel torque with E2E protection.", kind: ElementKind.InterfaceBlock);
        var decelIf = k.Block(ifPkg, "DecelRequest", "Requested deceleration with E2E protection.", kind: ElementKind.InterfaceBlock);
        var pedalIf = k.Block(ifPkg, "PedalStatus", "Brake and accelerator pedal switches.", kind: ElementKind.InterfaceBlock);
        var hmiIf = k.Block(ifPkg, "AccStatus", "ACC mode, set speed, time gap and warnings for the cluster.", kind: ElementKind.InterfaceBlock);

        Element Ecu(string name, string doc, string asil, string supplier)
        {
            var b = k.Block(physPkg, name, doc, "ECU");
            k.Tag(b, "asilCapability", asil);
            k.Tag(b, "supplier", supplier);
            return b;
        }
        var acc = k.Block(physPkg, "ACC System", "The ACC item: sensors, the ADAS controller and the actuating ECUs.");
        var radar = Ecu("Radar Sensor", "77 GHz long-range radar with object tracking.", "B", "Tier 1 A");
        var camera = Ecu("Front Camera", "Mono camera with vehicle classification.", "B", "Tier 1 B");
        var adas = Ecu("ADAS ECU", "Domain controller running sensor fusion, target selection and the longitudinal controller.", "D", "In-house");
        var engine = Ecu("Engine ECU", "Powertrain controller; executes torque requests.", "C", "Tier 1 C");
        var brake = Ecu("Brake ECU", "ESC unit; executes deceleration requests and reads the brake pedal.", "D", "Tier 1 D");
        var cluster = Ecu("Instrument Cluster", "Driver display.", "QM", "Tier 1 E");
        var driver = m.Create(physPkg, ElementKind.Actor, "Driver");
        k.Value(adas, "cycleTime", value: "20", unit: "ms");
        k.Value(adas, "maxAccel", value: "2", unit: "m/s2");
        k.Value(adas, "maxDecel", value: "3.5", unit: "m/s2");
        k.Value(radar, "range", value: "160", unit: "m");
        k.Value(radar, "updateRate", value: "20", unit: "Hz");
        k.Value(brake, "monitorPeriod", value: "10", unit: "ms");

        var pRadar = k.Part(acc, "radar", radar); var pCamera = k.Part(acc, "camera", camera); var pAdas = k.Part(acc, "adas", adas);
        var pEngine = k.Part(acc, "engine", engine); var pBrake = k.Part(acc, "brake", brake); var pCluster = k.Part(acc, "cluster", cluster);
        foreach (var e in new[] { radar, camera, adas, engine, brake, cluster }) m.Connect(ElementKind.Composition, acc, e);

        var radarOut = k.Port(radar, "objects", speedIf, "out");
        var cameraOut = k.Port(camera, "objects", speedIf, "out");
        var adasRadarIn = k.Port(adas, "radarIn", speedIf, "in");
        var adasCameraIn = k.Port(adas, "cameraIn", speedIf, "in");
        var adasTorqueOut = k.Port(adas, "torqueOut", torqueIf, "out");
        var adasDecelOut = k.Port(adas, "decelOut", decelIf, "out");
        var adasPedalIn = k.Port(adas, "pedalIn", pedalIf, "in");
        var adasHmiOut = k.Port(adas, "hmiOut", hmiIf, "out");
        var engineIn = k.Port(engine, "torqueIn", torqueIf, "in");
        var brakeIn = k.Port(brake, "decelIn", decelIf, "in");
        var brakePedalOut = k.Port(brake, "pedalOut", pedalIf, "out");
        var clusterIn = k.Port(cluster, "statusIn", hmiIf, "in");
        var wires = new[]
        {
            k.Wire(acc, pRadar, radarOut, pAdas, adasRadarIn, "Radar objects"),
            k.Wire(acc, pCamera, cameraOut, pAdas, adasCameraIn, "Camera objects"),
            k.Wire(acc, pAdas, adasTorqueOut, pEngine, engineIn, "Torque request"),
            k.Wire(acc, pAdas, adasDecelOut, pBrake, brakeIn, "Deceleration request"),
            k.Wire(acc, pBrake, brakePedalOut, pAdas, adasPedalIn, "Pedal status"),
            k.Wire(acc, pAdas, adasHmiOut, pCluster, clusterIn, "ACC status"),
        };

        // ------------------------------------------------------------ functional architecture
        var loop = m.Create(funcPkg, ElementKind.Activity, "ACC Control Loop");
        m.Set(loop, FieldKeys.Documentation, "One 20 ms control cycle. Swimlanes allocate each function to the ECU that runs it (Allocate relationships).");
        Element Lane(Element act, string name, Element rep)
        {
            var l = m.Create(act, ElementKind.Partition, name);
            m.Set(l, FieldKeys.Represents, rep.Id);
            return l;
        }
        var lanes = new[] { Lane(loop, "sensing", radar), Lane(loop, "vision", camera), Lane(loop, "control", adas), Lane(loop, "powertrain", engine), Lane(loop, "braking", brake), Lane(loop, "display", cluster) };
        var i0 = m.Create(loop, ElementKind.InitialNode);
        var fork = m.Create(loop, ElementKind.ForkNode);
        var detect = k.Action(loop, "Detect Objects", "distance = 60; closing = 2", "5ms");
        var classify = k.Action(loop, "Classify Objects", "confirmed = 1", "8ms");
        var join = m.Create(loop, ElementKind.ForkNode);
        var fuse = k.Action(loop, "Fuse Objects", null, "2ms");
        var select = k.Action(loop, "Select Target", "gap = distance / 25", "1ms");
        var compute = k.Action(loop, "Compute Acceleration", "accel = (gap - 1.8) * 1.5", "1ms");
        var limit = k.Action(loop, "Limit Request", "accel = max(min(accel, 2), -3.5)", "1ms");
        var dec = m.Create(loop, ElementKind.DecisionNode);
        var torque = k.Action(loop, "Apply Torque", null, "5ms");
        var monitor = k.Action(loop, "Monitor Deceleration", null, "1ms");
        var applyBrakes = k.Action(loop, "Apply Brakes", null, "5ms");
        var merge = m.Create(loop, ElementKind.DecisionNode);
        var display = k.Action(loop, "Display Status", null, "10ms");
        var f0 = m.Create(loop, ElementKind.ActivityFinalNode);
        var loopFlows = new[]
        {
            k.Flow(i0, fork), k.Flow(fork, detect), k.Flow(fork, classify), k.Flow(detect, join), k.Flow(classify, join),
            k.Flow(join, fuse), k.Flow(fuse, select), k.Flow(select, compute), k.Flow(compute, limit), k.Flow(limit, dec),
            k.Flow(dec, torque, "accel >= 0"), k.Flow(dec, monitor, "else"), k.Flow(monitor, applyBrakes),
            k.Flow(torque, merge), k.Flow(applyBrakes, merge), k.Flow(merge, display), k.Flow(display, f0),
        };

        var overrideAct = m.Create(funcPkg, ElementKind.Activity, "Driver Override");
        m.Set(overrideAct, FieldKeys.Documentation, "Brake pedal pressed while ACC is active: ACC hands control back to the driver.");
        var oLanes = new[] { Lane(overrideAct, "braking", brake), Lane(overrideAct, "control", adas), Lane(overrideAct, "display", cluster) };
        var o0 = m.Create(overrideAct, ElementKind.InitialNode);
        var pedal = k.Action(overrideAct, "Detect Brake Pedal", "pedal = 1", "10ms");
        var deactivate = k.Action(overrideAct, "Deactivate ACC", "accel = 0; active = 0", "20ms");
        var release = k.Action(overrideAct, "Release Requests", null, "10ms");
        var warn = k.Action(overrideAct, "Show Standby", null, "50ms");
        var of = m.Create(overrideAct, ElementKind.ActivityFinalNode);
        var oFlows = new[] { k.Flow(o0, pedal), k.Flow(pedal, deactivate), k.Flow(deactivate, release), k.Flow(release, warn), k.Flow(warn, of) };

        // ------------------------------------------------------------ modes and use cases
        var modes = m.Create(acc, ElementKind.StateMachine, "ACC Modes");
        var s0 = m.Create(modes, ElementKind.InitialState);
        var off = k.State(modes, "Off", entry: "active = 0; speed = 50");
        var standby = k.State(modes, "Standby", entry: "active = 0; accel = 0");
        var active = k.State(modes, "Active", entry: "active = 1", doActivity: "cycles = cycles + 1");
        var overrideSt = k.State(modes, "Override", entry: "accel = 0");
        var fault = k.State(modes, "Fault", entry: "active = 0; accel = 0; warning = 1");
        var transitions = new[]
        {
            k.Go(s0, off),
            k.Go(off, standby, "accSwitchOn"),
            k.Go(standby, active, "setPressed", guard: "speed >= 30"),
            k.Go(active, overrideSt, "acceleratorPressed"),
            k.Go(overrideSt, active, "acceleratorReleased"),
            k.Go(active, standby, "brakePressed"),
            k.Go(standby, off, "accSwitchOff"),
            k.Go(active, fault, "sensorFault"),
            k.Go(fault, off, "ignitionOff"),
        };

        var ucActivate = m.Create(behavior, ElementKind.UseCase, "Activate ACC");
        var ucSpeed = m.Create(behavior, ElementKind.UseCase, "Set Speed");
        var ucGap = m.Create(behavior, ElementKind.UseCase, "Adjust Time Gap");
        var ucOverride = m.Create(behavior, ElementKind.UseCase, "Override ACC");
        foreach (var uc in new[] { ucActivate, ucGap, ucOverride }) m.Connect(ElementKind.Association, driver, uc);
        m.Connect(ElementKind.Include, ucActivate, ucSpeed);

        // ------------------------------------------------------------ satisfy
        foreach (var (by, req) in new[]
        {
            (adas, setSpeed), (adas, timeGap), (radar, range), (adas, overrideReq), (brake, decel), (cluster, hmi),
            (adas, fsr1), (adas, fsr2), (camera, fsr2), (brake, fsr3), (radar, fsr4), (modes, fsr4),
            (radar, tsr1), (engine, tsr2), (brake, tsr3),
        })
            m.Connect(ElementKind.Satisfy, by, req);
        m.Connect(ElementKind.Refine, ucOverride, overrideReq);

        // ------------------------------------------------------------ verification
        var tests = new[]
        {
            k.Test(verification, "Set Speed HIL Test", "pass", "HIL: set speeds 30, 80, 130, 180 km/h on a flat road model.", setSpeed),
            k.Test(verification, "Gap Control Track Test", "pass", "Proving ground: follow a target at each time-gap setting.", timeGap),
            k.Test(verification, "Radar Range Test", "pass", "Corner reflector at 50 to 170 m.", range),
            k.Test(verification, "Driver Override Test", "pass", "Vehicle test: measured deactivation 62 ms after brake switch.", overrideReq),
            k.Test(verification, "Deceleration Limit Test", "pass", "HIL: cut-in scenarios, maximum commanded deceleration 3.4 m/s2.", decel),
            k.Test(verification, "HMI Review", "pass", "Inspection of cluster screens against the HMI specification.", hmi),
            k.Test(verification, "Acceleration Limiter Unit Test", "pass", "Unit test of the request limiter with MC/DC coverage.", fsr1),
            k.Test(verification, "Fusion Fault Injection", "inconclusive", "SIL: camera dropouts during braking; two scenarios still under review.", fsr2),
            k.Test(verification, "Brake Monitor HIL Test", "pass", "Inject 6 m/s2 requests; the monitor clamps to 4 m/s2 within one 10 ms cycle.", fsr3, tsr3),
            k.Test(verification, "Sensor Fault Injection", "pass", "Radar blockage and CAN timeout injection; Fault mode entered after 140 ms.", fsr4),
            k.Test(verification, "E2E Communication Test", "pass", "Corrupted CRC and frozen alive counter on the object list.", tsr1),
            k.Test(verification, "Torque Plausibility Test", "pass", "Engine ECU rejects out-of-range ACC torque requests.", tsr2),
            k.Test(verification, "Vehicle Safety Validation", "pass", "ISO 26262-4 safety validation of the safety goals in the vehicle (fault injection drives).", sg1, sg2, sg3),
        };

        // ================================================================ diagrams
        var dSafety = m.CreateDiagram(safetyPkg, DiagramType.Requirement, "Safety Concept");
        foreach (var r in new[] { sg1, sg2, sg3, fsr1, fsr2, fsr3, fsr4, tsr1, tsr2, tsr3 }) k.Place(dSafety, r, 0, 0, 250);
        m.Set(dSafety, FieldKeys.Legend, "true");
        k.Arrange(dSafety, 40, 70);

        var dAlloc = m.CreateDiagram(safetyPkg, DiagramType.Requirement, "Safety Requirement Allocation");
        var y = k.Row(dAlloc, 40, 40, 30, 240, fsr1, fsr2, fsr3, fsr4);
        y = k.Row(dAlloc, 40, y + 40, 30, 240, tsr1, tsr2, tsr3);
        k.Row(dAlloc, 40, y + 90, 40, 190, radar, camera, adas, engine, brake);
        k.ShowEdges(dAlloc, ElementKind.Satisfy);

        var dFr = m.CreateDiagram(reqPkg, DiagramType.Requirement, "ACC Requirements");
        y = k.Row(dFr, 40, 40, 30, 240, setSpeed, timeGap, range, overrideReq);
        y = k.Row(dFr, 40, y + 40, 30, 240, decel, hmi, stopGo);
        y = k.Row(dFr, 40, y + 90, 40, 190, radar, adas, brake, cluster);
        k.Place(dFr, ucOverride, 1000, y - 70);
        k.ShowEdges(dFr, ElementKind.Satisfy, ElementKind.Refine);

        var dVer = m.CreateDiagram(verification, DiagramType.Requirement, "Verification Plan");
        y = k.Grid(dVer, 40, 40, 5, 230, 30, 40, [setSpeed, timeGap, range, overrideReq, decel, hmi, stopGo, sg1, sg2, sg3, fsr1, fsr2, fsr3, fsr4, tsr1, tsr2, tsr3]);
        k.Grid(dVer, 40, y + 90, 5, 230, 30, 40, tests);
        k.ShowEdges(dVer, ElementKind.Verify);

        var dBdd = m.CreateDiagram(physPkg, DiagramType.BlockDefinition, "ECU Architecture");
        foreach (var b in new[] { acc, radar, camera, adas, engine, brake, cluster }) k.Place(dBdd, b, 0, 0, 200);
        m.Set(dBdd, FieldKeys.Legend, "true");
        k.Arrange(dBdd, 40, 80);

        var dSig = m.CreateDiagram(ifPkg, DiagramType.BlockDefinition, "Signals");
        k.Row(dSig, 40, 40, 40, 200, speedIf, torqueIf, decelIf, pedalIf, hmiIf);

        var dIbd = m.CreateDiagram(acc, DiagramType.InternalBlock, "ACC Network");
        var nRadar = k.Place(dIbd, pRadar, 60, 60, 190, 80, edges: false);
        var nCamera = k.Place(dIbd, pCamera, 60, 260, 190, 80, edges: false);
        var nAdas = k.Place(dIbd, pAdas, 400, 120, 220, 200, edges: false);
        var nEngine = k.Place(dIbd, pEngine, 780, 40, 190, 80, edges: false);
        var nBrake = k.Place(dIbd, pBrake, 780, 190, 190, 90, edges: false);
        var nCluster = k.Place(dIbd, pCluster, 780, 350, 190, 80, edges: false);
        k.PortOn(dIbd, nRadar, radarOut, "R");
        k.PortOn(dIbd, nCamera, cameraOut, "R");
        k.BorderAt(dIbd, nAdas, adasRadarIn, "L", 160);
        k.BorderAt(dIbd, nAdas, adasCameraIn, "L", 280);
        k.BorderAt(dIbd, nAdas, adasTorqueOut, "R", 140);
        k.BorderAt(dIbd, nAdas, adasDecelOut, "R", 220);
        k.BorderAt(dIbd, nAdas, adasPedalIn, "R", 260);
        k.BorderAt(dIbd, nAdas, adasHmiOut, "R", 300);
        k.PortOn(dIbd, nEngine, engineIn, "L");
        k.PortOn(dIbd, nBrake, brakeIn, "L", 0.33);
        k.PortOn(dIbd, nBrake, brakePedalOut, "L", 0.78);
        k.PortOn(dIbd, nCluster, clusterIn, "L");
        foreach (var w in wires) m.AddEdge(dIbd, w);

        // Control loop: six vertical swimlanes, one per ECU.
        var dLoop = m.CreateDiagram(loop, DiagramType.Activity, "ACC Control Loop");
        for (var i = 0; i < lanes.Length; i++) k.Place(dLoop, lanes[i], 40 + i * 200, 20, 200, 900, edges: false);
        double Cx(int lane) => 40 + lane * 200 + 100;
        void At(Element e, int lane, double yy, double w = 150, double h = 0)
        {
            var (dw, dh) = Language.Notation.DefaultSizeOf(e);
            var ww = e.Kind == ElementKind.Action ? w : dw;
            k.Place(dLoop, e, Cx(lane) - ww / 2, yy, ww, h);
        }
        At(i0, 2, 60);
        k.Place(dLoop, fork, 120, 120, 440, 8);
        At(detect, 0, 160); At(classify, 1, 160);
        k.Place(dLoop, join, 120, 260, 440, 8);
        At(fuse, 2, 300); At(select, 2, 390, h: 70); At(compute, 2, 490, h: 70); At(limit, 2, 590, h: 70);
        At(dec, 2, 690);
        At(torque, 3, 680); At(monitor, 4, 680); At(applyBrakes, 4, 770);
        At(merge, 3, 790);
        At(display, 5, 780);
        At(f0, 5, 870);
        foreach (var f in loopFlows) m.AddEdge(dLoop, f);
        Allocation.SyncFromSwimlanes(m, dLoop);

        var dOverride = m.CreateDiagram(overrideAct, DiagramType.Activity, "Driver Override");
        for (var i = 0; i < oLanes.Length; i++) k.Place(dOverride, oLanes[i], 40 + i * 220, 20, 220, 520, edges: false);
        k.Place(dOverride, o0, 136, 60);
        k.Place(dOverride, pedal, 85, 120);
        k.Place(dOverride, deactivate, 305, 220);
        k.Place(dOverride, release, 305, 320);
        k.Place(dOverride, warn, 525, 320);
        k.Place(dOverride, of, 576, 440);
        foreach (var f in oFlows) m.AddEdge(dOverride, f);
        Allocation.SyncFromSwimlanes(m, dOverride);

        var dStm = m.CreateDiagram(modes, DiagramType.StateMachine, "ACC Modes");
        k.Place(dStm, s0, 40, 74);
        k.Place(dStm, off, 120, 50, 150, 76);
        k.Place(dStm, standby, 400, 50, 170, 76);
        k.Place(dStm, active, 400, 240, 190, 90);
        k.Place(dStm, overrideSt, 720, 240, 150, 76);
        k.Place(dStm, fault, 120, 240, 180, 90);
        foreach (var t in transitions) m.AddEdge(dStm, t);

        var dUc = m.CreateDiagram(behavior, DiagramType.UseCase, "Driver Use Cases");
        k.Place(dUc, driver, 40, 150);
        k.Place(dUc, ucActivate, 240, 40);
        k.Place(dUc, ucGap, 240, 160);
        k.Place(dUc, ucOverride, 240, 280);
        k.Place(dUc, ucSpeed, 500, 40);

        // ------------------------------------------------------------ saved views
        var views = SavedViews.DefaultOwner(m);
        SavedViews.SaveTable(m, new TableDefinition
        {
            Query = "kind:Requirement where tag:ASIL is not empty",
            Columns = [new("reqId"), new("name"), new("stereotype"), new("tag:ASIL", "ASIL"), new("derivedFrom"), new("satisfiedBy"), new("verifiedBy")],
            SortKey = "tag:ASIL", SortDescending = true,
        }, "Safety requirements by ASIL", owner: views);
        SavedViews.SaveTable(m, new TableDefinition
        {
            Query = "kind:Action",
            Columns = [new("name"), new("owner", "Activity"), new("allocatedTo", "ECU"), new("field:duration", "Duration")],
            SortKey = "owner",
        }, "Function allocation", owner: views);
        SavedViews.SaveTable(m, new TableDefinition
        {
            Query = "kind:Block stereotype:ECU",
            Columns = [new("name"), new("tag:asilCapability", "ASIL capability"), new("tag:supplier", "Supplier"), new("satisfies"), new("allocatedFrom")],
            SortKey = "name",
        }, "ECU overview", owner: views);
        SavedViews.SaveQuery(m, "kind:Requirement where not has:in:verify", "Unverified requirements", owner: views);
        SavedViews.SaveRule(m, "kind:Requirement where tag:ASIL is not empty and not has:in:verify", "Safety requirement verification", "warning",
            "{reqId} (ASIL {tag:ASIL}) has no verifying test case", owner: views);
        SavedViews.SaveRule(m, "kind:Action where not has:allocate", "Unallocated functions", "warning",
            "Function '{name}' in {owner} is not allocated to an ECU", owner: views);

        m.History.Clear();
        return m;
    }
}
