using Meridian.Core.Analysis;
using Meridian.Core.Model;

namespace Meridian.Core.Samples;

/// <summary>
/// Medical sample: a large-volume infusion pump with ISO 14971 risk management and IEC 62304 software items.
/// Hazards are blocks with the «Hazard» stereotype; the "Hazard analysis" generic table lists them with severity,
/// probability, risk before and after control and the risk-control requirements that trace to them. Risk controls
/// derive software requirements, which are satisfied by software items with a safety class. The pump's operating
/// states are a state machine, and every requirement traces to a test.
/// <para>
/// Deliberate gaps: hazard <c>H-07 Alarm not heard</c> has no risk control yet (flagged by the
/// "Hazard without risk control" rule), and <c>PUMP-005 EMR Integration</c> is proposed with no design or test.
/// </para>
/// </summary>
public static class InfusionPumpSample
{
    public const string Name = "Infusion Pump";

    public static ModelRepository Create(string? name = null)
    {
        var m = new ModelRepository(string.IsNullOrWhiteSpace(name) ? Name : name.Trim());
        var k = new SampleKit(m);
        m.Set(m.Root, FieldKeys.Documentation,
            "Large-volume infusion pump. Worked medical-device example: ISO 14971 hazard analysis with risk controls, IEC 62304 " +
            "software items and safety classes, operating states and traceability to tests. Clinical values are illustrative.");

        // ------------------------------------------------------------ profile
        var profile = m.Create(m.Root, ElementKind.Profile, "Medical Device");
        m.Set(profile, FieldKeys.Documentation, "ISO 14971 risk management and IEC 62304 software lifecycle stereotypes.");
        const string Levels = "acceptable, ALARP, unacceptable, TBD";
        var stHazard = k.Stereotype(profile, "Hazard", "Block", "#f8cecc",
            ("hazardId", "String", null), ("hazardousSituation", "String", null), ("harm", "String", null),
            ("severity", "Enum", "negligible, minor, serious, critical, catastrophic"),
            ("probability", "Enum", "improbable, remote, occasional, probable, frequent"),
            ("initialRisk", "Enum", Levels), ("residualRisk", "Enum", Levels));
        var stControl = k.Stereotype(profile, "RiskControl", "Requirement", "#d5e8d4",
            ("controlType", "Enum", "inherent safety by design, protective measure, information for safety"));
        var stSoftware = k.Stereotype(profile, "SoftwareItem", "Block", "#dae8fc", ("safetyClass", "Enum", "A, B, C"));

        var riskPkg = k.Pkg(m.Root, "Risk Management", "ISO 14971 hazard analysis: hazards, hazardous situations, harms and risk estimates.");
        var reqPkg = k.Pkg(m.Root, "Requirements");
        var sysReqPkg = k.Pkg(reqPkg, "System Requirements");
        var rcPkg = k.Pkg(reqPkg, "Risk Controls", "Risk-control measures from the hazard analysis (ISO 14971 clause 7).");
        var swReqPkg = k.Pkg(reqPkg, "Software Requirements", "IEC 62304 clause 5.2 software requirements derived from risk controls.");
        var archPkg = k.Pkg(m.Root, "Architecture", "Hardware and disposables.");
        var swPkg = k.Pkg(m.Root, "Software", "IEC 62304 software items with their safety class.");
        var behavior = k.Pkg(m.Root, "Use", "Clinical use cases, the programming workflow and the pump states.");
        var verification = k.Pkg(m.Root, "Verification", "Bench tests, software tests and reviews.");

        // ------------------------------------------------------------ hazards
        Element Hazard(string id, string name, string situation, string harm, string severity, string probability, string initial, string residual)
        {
            var h = k.Block(riskPkg, name, null, "Hazard");
            k.Tag(h, "hazardId", id); k.Tag(h, "hazardousSituation", situation); k.Tag(h, "harm", harm);
            k.Tag(h, "severity", severity); k.Tag(h, "probability", probability);
            k.Tag(h, "initialRisk", initial); k.Tag(h, "residualRisk", residual);
            return h;
        }
        var h1 = Hazard("H-01", "Free-Flow Over-Infusion", "Uncontrolled gravity flow when the set is removed from the pump", "Drug overdose", "critical", "occasional", "unacceptable", "acceptable");
        var h2 = Hazard("H-02", "Undetected Occlusion", "Downstream occlusion stops delivery without an alarm", "Delay of therapy", "serious", "probable", "unacceptable", "acceptable");
        var h3 = Hazard("H-03", "Air Embolism", "Air in the line is delivered to the patient", "Air embolism", "catastrophic", "remote", "unacceptable", "ALARP");
        var h4 = Hazard("H-04", "Programming Error", "Wrong rate or dose entered by the user", "Drug overdose", "critical", "probable", "unacceptable", "ALARP");
        var h5 = Hazard("H-05", "Motor Runaway", "Software or motor-driver fault drives the pump continuously", "Drug overdose", "critical", "remote", "unacceptable", "acceptable");
        var h6 = Hazard("H-06", "Therapy Interruption", "Battery depleted during patient transport", "Delay of therapy", "serious", "occasional", "ALARP", "acceptable");
        var h7 = Hazard("H-07", "Alarm Not Heard", "Alarm volume too low in a noisy ward", "Delay of therapy", "serious", "occasional", "ALARP", "TBD");
        var hazards = new[] { h1, h2, h3, h4, h5, h6, h7 };

        // ------------------------------------------------------------ requirements
        Element Sys(string id, string name, string text, string method, params (string, string)[] extra) =>
            k.Req(sysReqPkg, id, name, text, [(ReqFields.Status, "approved"), (ReqFields.Priority, "high"), (ReqFields.VerificationMethod, method), .. extra]);
        var rateRange = Sys("PUMP-001", "Flow Rate Range", "The pump shall deliver programmed rates from 0.1 to 999 mL/h.", "test");
        var accuracy = Sys("PUMP-002", "Flow Accuracy", "The delivered volume shall be within 5 % of the programmed volume (IEC 60601-2-24 trumpet curve).", "test");
        var runtime = Sys("PUMP-003", "Battery Runtime", "The pump shall run for at least 4 h on battery at 25 mL/h.", "test");
        var alarms = Sys("PUMP-004", "Alarm Signals", "Alarm signals shall follow the IEC 60601-1-8 priorities (high, medium, low).", "test");
        var emr = k.Req(sysReqPkg, "PUMP-005", "EMR Integration", "The pump shall receive infusion orders from the hospital EMR over Wi-Fi.",
            (ReqFields.Status, "proposed"), (ReqFields.Priority, "low"), (ReqFields.VerificationMethod, "test"),
            (ReqFields.Rationale, "Planned for release 2; needs a cybersecurity risk assessment first. Deliberately left unsatisfied and unverified."));

        Element Rc(string id, string name, string text, string controlType, string method, params Element[] mitigates)
        {
            var r = k.Req(rcPkg, id, name, text, (ReqFields.Status, "approved"), (ReqFields.Priority, "critical"), (ReqFields.VerificationMethod, method), (ReqFields.Risk, "high"));
            m.Set(r, FieldKeys.Stereotypes, "RiskControl");
            k.Tag(r, "controlType", controlType);
            foreach (var h in mitigates) m.Connect(ElementKind.Trace, r, h);
            return r;
        }
        var rc1 = Rc("RC-001", "Anti-Free-Flow Clamp", "The administration set shall include an anti-free-flow clamp that closes automatically when the set is removed from the pump.",
            "inherent safety by design", "test", h1);
        var rc2 = Rc("RC-002", "Occlusion Detection", "The pump shall detect a downstream occlusion within 2 min at 25 mL/h and raise a high-priority alarm.",
            "protective measure", "test", h2);
        var rc3 = Rc("RC-003", "Air-in-Line Detection", "The pump shall stop the infusion and raise an alarm when a single air bubble of 50 uL or more is detected.",
            "protective measure", "test", h3);
        var rc4 = Rc("RC-004", "Drug Library Hard Limits", "The pump shall reject programmed rates or doses outside the hard limits of the drug library.",
            "protective measure", "test", h4);
        var rc5 = Rc("RC-005", "Independent Motor Watchdog", "An independent hardware watchdog shall stop the motor within 1 s when the motor control software stops refreshing it.",
            "protective measure", "test", h5);
        var rc6 = Rc("RC-006", "Low Battery Alarm", "The pump shall raise a low-battery alarm at least 30 min before the battery is depleted.",
            "protective measure", "test", h6);
        var rc7 = Rc("RC-007", "Double-Check Instructions", "The instructions for use shall describe an independent double-check of rate and volume before an infusion is started.",
            "information for safety", "inspection", h4);
        var riskControls = new[] { rc1, rc2, rc3, rc4, rc5, rc6, rc7 };

        Element Swr(string id, string name, string text, string swClass, Element from)
        {
            var r = k.Req(swReqPkg, id, name, text, (ReqFields.Status, "approved"), (ReqFields.Priority, "high"), (ReqFields.VerificationMethod, "test"),
                (ReqFields.CustomPrefix + "Software safety class", swClass));
            m.Connect(ElementKind.DeriveReqt, r, from);
            return r;
        }
        var swr1 = Swr("SWR-001", "Occlusion Algorithm", "The infusion control software shall compare the downstream pressure against the occlusion threshold every 100 ms.", "C", rc2);
        var swr2 = Swr("SWR-002", "Limit Check", "The drug library software shall check every programmed rate and dose against its hard and soft limits before the infusion starts.", "B", rc4);
        var swr3 = Swr("SWR-003", "Watchdog Refresh", "The motor control task shall refresh the hardware watchdog every 100 ms, and only while its self-checks pass.", "C", rc5);
        var swr4 = Swr("SWR-004", "Air Bubble Stop", "The infusion control software shall stop the motor within 200 ms of an air-in-line detector event.", "C", rc3);

        // ------------------------------------------------------------ architecture
        var pump = k.Block(archPkg, "Infusion Pump", "Volumetric large-volume pump for hospital use.");
        var mech = k.Block(archPkg, "Pumping Mechanism", "Linear peristaltic mechanism driven by a stepper motor.");
        var occl = k.Block(archPkg, "Occlusion Sensor", "Downstream pressure sensor.");
        var air = k.Block(archPkg, "Air Detector", "Ultrasonic air-in-line detector.");
        var ui = k.Block(archPkg, "User Interface", "Touch display and keypad.");
        var psu = k.Block(archPkg, "Power Supply", "Mains supply with a Li-ion backup battery.");
        var alarmUnit = k.Block(archPkg, "Alarm Unit", "Speaker and alarm LEDs.");
        var monitor = k.Block(archPkg, "Safety Monitor", "Independent hardware watchdog that can cut motor power.");
        var controller = k.Block(archPkg, "Main Controller", "Processor board hosting the pump software.");
        var set = k.Block(archPkg, "Administration Set", "Disposable tubing set with an anti-free-flow clamp.");
        var labeling = k.Block(archPkg, "Labeling", "Instructions for use and on-device labels.");
        var nurse = m.Create(archPkg, ElementKind.Actor, "Nurse");
        var biomed = m.Create(archPkg, ElementKind.Actor, "Biomedical Engineer");
        k.Value(mech, "maxRate", value: "999", unit: "mL/h");
        k.Value(mech, "accuracy", value: "5", unit: "%");
        k.Value(psu, "batteryRuntime", value: "6", unit: "h");
        k.Value(occl, "threshold", value: "600", unit: "mmHg");
        k.Value(air, "bubbleLimit", value: "50", unit: "uL");
        foreach (var part in new[] { mech, occl, air, ui, psu, alarmUnit, monitor, controller })
            m.Connect(ElementKind.Composition, pump, part);
        foreach (var (partName, type) in new[] { ("mechanism", mech), ("occlusionSensor", occl), ("airDetector", air), ("ui", ui), ("power", psu), ("alarm", alarmUnit), ("monitor", monitor), ("controller", controller) })
            k.Part(pump, partName, type);
        m.Connect(ElementKind.Association, pump, set);

        Element Sw(string name, string swClass, string doc)
        {
            var b = k.Block(swPkg, name, doc, "SoftwareItem");
            k.Tag(b, "safetyClass", swClass);
            return b;
        }
        var swControl = Sw("Infusion Control SW", "C", "Motor control, occlusion and air-in-line supervision.");
        var swAlarm = Sw("Alarm Manager SW", "C", "Alarm prioritisation and annunciation (IEC 60601-1-8).");
        var swLibrary = Sw("Drug Library SW", "B", "Drug entries with soft and hard limits.");
        var swUi = Sw("UI SW", "B", "Programming screens and status display.");
        foreach (var s in new[] { swControl, swAlarm, swLibrary, swUi }) m.Connect(ElementKind.Composition, controller, s);

        // ------------------------------------------------------------ satisfy
        foreach (var (by, req) in new[]
        {
            (mech, rateRange), (swControl, rateRange), (mech, accuracy), (psu, runtime), (swAlarm, alarms), (alarmUnit, alarms),
            (set, rc1), (occl, rc2), (swControl, rc2), (air, rc3), (swLibrary, rc4), (monitor, rc5), (psu, rc6), (swAlarm, rc6), (labeling, rc7),
            (swControl, swr1), (swLibrary, swr2), (swControl, swr3), (swControl, swr4),
        })
            m.Connect(ElementKind.Satisfy, by, req);

        // ------------------------------------------------------------ verification
        var tests = new[]
        {
            k.Test(verification, "Flow Accuracy Test", "pass", "Gravimetric trumpet-curve test at 1, 25 and 999 mL/h (IEC 60601-2-24).", rateRange, accuracy),
            k.Test(verification, "Battery Runtime Test", "pass", "Run on battery at 25 mL/h until depletion; low-battery alarm 42 min before shutdown.", runtime, rc6),
            k.Test(verification, "Alarm System Test", "pass", "Measure alarm priorities, sound pressure and escalation (IEC 60601-1-8).", alarms),
            k.Test(verification, "Free-Flow Bench Test", "pass", "Remove the loaded set 50 times; no free flow observed.", rc1),
            k.Test(verification, "Occlusion Detection Test", "pass", "Clamp the downstream line at 25 mL/h; alarm after 71 s.", rc2, swr1),
            k.Test(verification, "Air-in-Line Test", "pass", "Inject 50 uL bubbles; infusion stops in 120 ms.", rc3, swr4),
            k.Test(verification, "Drug Library Limit Test", "pass", "Program rates above the hard limit for every library entry.", rc4, swr2),
            k.Test(verification, "Watchdog Fault Injection", "fail", "Halt the motor task: motor stopped after 1.3 s (limit 1 s). CAPA opened.", rc5, swr3),
            k.Test(verification, "IFU Review", "pass", "Usability engineering review of the instructions for use.", rc7),
        };

        // ------------------------------------------------------------ behaviour
        var states = m.Create(pump, ElementKind.StateMachine, "Pump States");
        var s0 = m.Create(states, ElementKind.InitialState);
        var off = k.State(states, "Off");
        var selfTest = k.State(states, "Self Test", entry: "selfTestOk = 1");
        var idle = k.State(states, "Idle", entry: "rate = 0");
        var programming = k.State(states, "Programming", entry: "withinLimits = 1; vtbi = 100");
        var ready = k.State(states, "Ready");
        var infusing = k.State(states, "Infusing", entry: "rate = 25", doActivity: "vtbi = vtbi - rate");
        var paused = k.State(states, "Paused", entry: "rate = 0");
        var alarm = k.State(states, "Alarm", entry: "rate = 0; alarm = 1", exit: "alarm = 0");
        var kvo = k.State(states, "KVO", entry: "rate = 1");
        var fault = k.State(states, "Fault", entry: "rate = 0; alarm = 1");
        var transitions = new[]
        {
            k.Go(s0, off),
            k.Go(off, selfTest, "powerOn"),
            k.Go(selfTest, idle, "after(5s)", guard: "selfTestOk == 1"),
            k.Go(selfTest, fault, "selfTestFailed"),
            k.Go(idle, programming, "program"),
            k.Go(programming, idle, "cancel"),
            k.Go(programming, ready, "confirm", guard: "withinLimits == 1"),
            k.Go(ready, infusing, "start"),
            k.Go(infusing, paused, "pause"),
            k.Go(paused, infusing, "resume"),
            k.Go(infusing, alarm, "occlusion"),
            k.Go(alarm, paused, "acknowledge"),
            k.Go(infusing, kvo, "when(vtbi <= 0)"),
            k.Go(kvo, idle, "stop"),
            k.Go(infusing, fault, "watchdogTimeout"),
            k.Go(fault, off, "powerOff"),
            k.Go(idle, off, "powerOff"),
        };
        var airTransition = k.Go(infusing, alarm, "airInLine");
        m.Connect(ElementKind.Satisfy, states, alarms);

        var program = m.Create(behavior, ElementKind.Activity, "Program Infusion");
        m.Set(program, FieldKeys.Documentation, "Programming workflow with the drug library limit check (RC-004) and the double-check from the IFU (RC-007).");
        Element Lane(string name, Element rep)
        {
            var l = m.Create(program, ElementKind.Partition, name);
            m.Set(l, FieldKeys.Represents, rep.Id);
            return l;
        }
        var lanes = new[] { Lane("clinician", nurse), Lane("screens", swUi), Lane("library", swLibrary), Lane("control", swControl) };
        var p0 = m.Create(program, ElementKind.InitialNode);
        var selectDrug = k.Action(program, "Select Drug", null, "20s");
        var enterRate = k.Action(program, "Enter Rate and Volume", "rate = 25; vtbi = 100", "30s");
        var showSummary = k.Action(program, "Show Summary", null, "1s");
        var check = k.Action(program, "Check Limits", "withinLimits = rate <= 500", "100ms");
        var dec = m.Create(program, ElementKind.DecisionNode);
        var reject = k.Action(program, "Reject and Explain", null, "1s");
        var doubleCheck = k.Action(program, "Independent Double-Check", null, "60s");
        var start = k.Action(program, "Start Motor", "running = 1", "200ms");
        var pf = m.Create(program, ElementKind.ActivityFinalNode);
        var flows = new[]
        {
            k.Flow(p0, selectDrug), k.Flow(selectDrug, enterRate), k.Flow(enterRate, check), k.Flow(check, dec),
            k.Flow(dec, showSummary, "withinLimits == 1"), k.Flow(dec, reject, "else"), k.Flow(reject, enterRate),
            k.Flow(showSummary, doubleCheck), k.Flow(doubleCheck, start), k.Flow(start, pf),
        };

        var ucProgram = m.Create(behavior, ElementKind.UseCase, "Program Infusion");
        var ucAlarm = m.Create(behavior, ElementKind.UseCase, "Respond to Alarm");
        var ucBag = m.Create(behavior, ElementKind.UseCase, "Change Bag");
        var ucLibrary = m.Create(behavior, ElementKind.UseCase, "Update Drug Library");
        foreach (var uc in new[] { ucProgram, ucAlarm, ucBag }) m.Connect(ElementKind.Association, nurse, uc);
        m.Connect(ElementKind.Association, biomed, ucLibrary);
        m.Connect(ElementKind.Refine, program, rc4);

        // ================================================================ diagrams
        var dRisk = m.CreateDiagram(riskPkg, DiagramType.Requirement, "Hazards and Risk Controls");
        var y = k.Row(dRisk, 40, 40, 30, 200, hazards);
        y = k.Row(dRisk, 40, y + 90, 30, 200, riskControls);
        k.Grid(dRisk, 40, y + 90, 5, 230, 30, 40, tests.Where(t => riskControls.Any(r => m.Outgoing(t, ElementKind.Verify).Any(v => ReferenceEquals(v.Target, r)))));
        m.Set(dRisk, FieldKeys.Legend, "true");
        k.ShowEdges(dRisk, ElementKind.Trace, ElementKind.Verify);

        var dSys = m.CreateDiagram(sysReqPkg, DiagramType.Requirement, "System Requirements");
        y = k.Row(dSys, 40, 40, 30, 240, rateRange, accuracy, runtime, alarms, emr);
        k.Row(dSys, 40, y + 90, 40, 190, mech, psu, alarmUnit, swControl, swAlarm);
        k.ShowEdges(dSys, ElementKind.Satisfy);

        var dSw = m.CreateDiagram(swReqPkg, DiagramType.Requirement, "Software Requirements");
        y = k.Row(dSw, 40, 40, 30, 240, rc2, rc3, rc4, rc5);
        y = k.Row(dSw, 40, y + 80, 30, 240, swr1, swr4, swr2, swr3);
        k.Row(dSw, 40, y + 90, 60, 200, swControl, swLibrary);
        k.ShowEdges(dSw, ElementKind.DeriveReqt, ElementKind.Satisfy);

        var dVer = m.CreateDiagram(verification, DiagramType.Requirement, "Verification Traceability");
        y = k.Grid(dVer, 40, 40, 6, 220, 30, 40, [rateRange, accuracy, runtime, alarms, emr, .. riskControls, swr1, swr2, swr3, swr4]);
        k.Grid(dVer, 40, y + 90, 5, 230, 30, 40, tests);
        k.ShowEdges(dVer, ElementKind.Verify);

        var dBdd = m.CreateDiagram(archPkg, DiagramType.BlockDefinition, "Pump Architecture");
        foreach (var b in new[] { pump, mech, occl, air, ui, psu, alarmUnit, monitor, controller }) k.Place(dBdd, b, 0, 0, 180);
        k.Arrange(dBdd, 30, 80);
        var bottom = dBdd.Nodes.Max(n => n.Bounds.Bottom);
        k.Place(dBdd, set, 40, bottom + 60, 200);
        k.Place(dBdd, labeling, 300, bottom + 60, 200);

        var dSwArch = m.CreateDiagram(swPkg, DiagramType.BlockDefinition, "Software Items");
        foreach (var b in new[] { controller, swControl, swAlarm, swLibrary, swUi }) k.Place(dSwArch, b, 0, 0, 200);
        m.Set(dSwArch, FieldKeys.Legend, "true");
        k.Arrange(dSwArch, 40, 80);

        var dProfile = m.CreateDiagram(profile, DiagramType.Package, "Medical Device Profile");
        k.Row(dProfile, 40, 40, 40, 260, stHazard, stControl, stSoftware);

        var dStm = m.CreateDiagram(states, DiagramType.StateMachine, "Pump States");
        k.Place(dStm, s0, 40, 74);
        k.Place(dStm, off, 120, 60, 130, 56);
        k.Place(dStm, selfTest, 340, 50, 170, 76);
        k.Place(dStm, idle, 600, 50, 150, 76);
        k.Place(dStm, programming, 860, 50, 220, 76);
        k.Place(dStm, ready, 900, 230, 150, 56);
        k.Place(dStm, infusing, 580, 220, 220, 90);
        k.Place(dStm, paused, 580, 420, 150, 76);
        k.Place(dStm, alarm, 860, 410, 200, 90);
        k.Place(dStm, kvo, 340, 230, 150, 76);
        k.Place(dStm, fault, 120, 400, 200, 90);
        foreach (var t in transitions) m.AddEdge(dStm, t);
        var airEdge = m.AddEdge(dStm, airTransition)!;
        m.SetWaypoints(dStm, airEdge, [new Pt(820, 380)]);

        var dAct = m.CreateDiagram(program, DiagramType.Activity, "Program Infusion");
        for (var i = 0; i < lanes.Length; i++) k.Place(dAct, lanes[i], 40 + i * 220, 20, 220, 660, edges: false);
        double Cx(int lane) => 40 + lane * 220 + 110;
        void At(Element e, int lane, double yy, double h = 0)
        {
            var (dw, _) = Language.Notation.DefaultSizeOf(e);
            var w = e.Kind == ElementKind.Action ? 160 : dw;
            k.Place(dAct, e, Cx(lane) - w / 2, yy, w, h);
        }
        At(p0, 0, 60); At(selectDrug, 0, 120); At(enterRate, 0, 220, 70);
        At(check, 2, 220, 70); At(dec, 2, 340);
        At(reject, 1, 330); At(showSummary, 1, 430); At(doubleCheck, 0, 430, 70);
        At(start, 3, 530, 70); At(pf, 3, 630);
        foreach (var f in flows) m.AddEdge(dAct, f);
        Allocation.SyncFromSwimlanes(m, dAct);

        var dUc = m.CreateDiagram(behavior, DiagramType.UseCase, "Clinical Use");
        k.Place(dUc, nurse, 40, 140);
        k.Place(dUc, biomed, 40, 400);
        k.Place(dUc, ucProgram, 260, 40);
        k.Place(dUc, ucAlarm, 260, 160);
        k.Place(dUc, ucBag, 260, 280);
        k.Place(dUc, ucLibrary, 260, 410);

        // ------------------------------------------------------------ saved views
        var views = SavedViews.DefaultOwner(m);
        SavedViews.SaveTable(m, new TableDefinition
        {
            Query = "kind:Block stereotype:Hazard",
            Columns =
            [
                new("tag:hazardId", "ID"), new("name", "Hazard"), new("tag:hazardousSituation", "Hazardous situation"), new("tag:harm", "Harm"),
                new("tag:severity", "Severity"), new("tag:probability", "Probability"), new("tag:initialRisk", "Initial risk"),
                new("tracedFrom", "Risk controls"), new("tag:residualRisk", "Residual risk"),
            ],
            SortKey = "tag:hazardId",
        }, "Hazard analysis", owner: views);
        SavedViews.SaveTable(m, new TableDefinition
        {
            Query = "kind:Requirement stereotype:RiskControl",
            Columns = [new("reqId"), new("name"), new("tag:controlType", "Control type"), new("tracesTo", "Hazards"), new("satisfiedBy"), new("verifiedBy")],
            SortKey = "reqId",
        }, "Risk control verification", owner: views);
        SavedViews.SaveTable(m, new TableDefinition
        {
            Query = "kind:Block stereotype:SoftwareItem",
            Columns = [new("name"), new("tag:safetyClass", "Safety class"), new("satisfies"), new("allocatedFrom")],
            SortKey = "name",
        }, "Software items", owner: views);
        SavedViews.SaveTable(m, new TableDefinition
        {
            Query = "kind:TestCase",
            Columns = [new("name"), new("field:verdict", "Verdict"), new("verifies"), new("documentation")],
            SortKey = "name",
        }, "Test results", owner: views);
        SavedViews.SaveQuery(m, "kind:Requirement where not has:in:verify", "Unverified requirements", owner: views);
        SavedViews.SaveRule(m, "kind:Block stereotype:Hazard where not has:in:trace", "Hazard without risk control", "warning",
            "Hazard {tag:hazardId} {name} has no risk control (residual risk {tag:residualRisk})", owner: views);
        SavedViews.SaveRule(m, "kind:Block stereotype:SoftwareItem where tag:safetyClass is empty", "Software safety class", "error",
            "Software item {name} has no IEC 62304 safety class", owner: views);

        m.History.Clear();
        return m;
    }
}
