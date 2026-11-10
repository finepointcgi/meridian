using Meridian.Core.Analysis;
using Meridian.Core.Model;

namespace Meridian.Core.Samples;

/// <summary>
/// Aerospace sample: a 3U Earth-observation CubeSat. Mission requirements, the EPS / ADCS / COMMS / OBC / payload
/// subsystems (BDD), an IBD with power and data ports and item flows, power and mass budgets as parametrics (with
/// Monte Carlo distributions), the operating-mode state machine, a timed imaging activity with swimlanes, a ground
/// pass sequence, a battery trade study and verification test cases.
/// <para>
/// Deliberate gap: <c>SAT-004 Disposal</c> is neither satisfied nor verified yet (status "proposed"), so the
/// validator, the traceability views and the "Unverified requirements" query show one open item.
/// </para>
/// </summary>
public static class CubeSatSample
{
    public const string Name = "CubeSat Mission";

    public static ModelRepository Create(string? name = null)
    {
        var m = new ModelRepository(string.IsNullOrWhiteSpace(name) ? Name : name.Trim());
        var k = new SampleKit(m);
        m.Set(m.Root, FieldKeys.Documentation,
            "3U Earth-observation CubeSat in a 500 km sun-synchronous orbit. Worked aerospace example: requirements, subsystems, " +
            "interfaces, power and mass budgets, operating modes and verification.");

        var reqPkg = k.Pkg(m.Root, "Mission Requirements", "Stakeholder and system requirements, grouped by area.");
        var structure = k.Pkg(m.Root, "Architecture", "Spacecraft structure: subsystems, parts and value properties.");
        var interfaces = k.Pkg(m.Root, "Interfaces", "Interface blocks typing the ports of the subsystems.");
        var analysis = k.Pkg(m.Root, "Budgets", "Constraint blocks for the power and mass budgets.");
        var behavior = k.Pkg(m.Root, "Operations", "Use cases, operating modes, the imaging activity and the ground pass.");
        var trades = k.Pkg(m.Root, "Trade Studies", "Design alternatives and their evaluation.");
        var verification = k.Pkg(m.Root, "Verification", "Test cases, analyses and inspections with their last verdict.");

        // ------------------------------------------------------------ requirements
        const string Attr = ReqFields.CustomPrefix;
        var mission = k.Req(reqPkg, "SAT-001", "Mission",
            "The CubeSat shall provide multispectral imagery of agricultural regions from a 500 km sun-synchronous orbit for at least 2 years.",
            (ReqFields.Priority, "high"), (ReqFields.Status, "approved"), (ReqFields.Owner, "Mission lead"));
        var gsd = k.Req(mission, "SAT-002", "Imaging Resolution", "The payload shall achieve a ground sample distance of 30 m or better from 500 km altitude.",
            (ReqFields.Priority, "high"), (ReqFields.Status, "approved"), (ReqFields.VerificationMethod, "test"), (Attr + "Area", "Payload"));
        var lifetime = k.Req(mission, "SAT-003", "Mission Lifetime", "The spacecraft shall operate for at least 2 years in orbit.",
            (ReqFields.Priority, "high"), (ReqFields.Status, "approved"), (ReqFields.VerificationMethod, "analysis"), (Attr + "Area", "System"));
        var disposal = k.Req(mission, "SAT-004", "Disposal", "The spacecraft shall re-enter the atmosphere within 25 years after the end of the mission.",
            (ReqFields.Priority, "medium"), (ReqFields.Status, "proposed"), (ReqFields.VerificationMethod, "analysis"), (ReqFields.Risk, "high"),
            (ReqFields.Rationale, "Open item: depends on the final orbit altitude and on the drag-sail trade. Deliberately left unsatisfied and unverified in this sample."),
            (Attr + "Area", "System"));
        var power = k.Req(reqPkg, "SAT-010", "Power", "The electrical power subsystem shall supply every subsystem in all operating modes.",
            (ReqFields.Priority, "high"), (ReqFields.Status, "approved"));
        var margin = k.Req(power, "SAT-011", "Orbit-Average Power Margin",
            "The EPS shall provide an orbit-average power margin of at least 10 % over the total consumption in nominal mode.",
            (ReqFields.Priority, "high"), (ReqFields.Status, "approved"), (ReqFields.VerificationMethod, "analysis"), (Attr + "Area", "EPS"));
        var dod = k.Req(power, "SAT-012", "Battery Depth of Discharge", "The battery depth of discharge shall not exceed 30 % during eclipse.",
            (ReqFields.Priority, "medium"), (ReqFields.Status, "approved"), (ReqFields.VerificationMethod, "test"), (Attr + "Area", "EPS"));
        var pointing = k.Req(reqPkg, "SAT-020", "Pointing Accuracy", "The ADCS shall point the payload boresight with an accuracy of 0.5 deg (3 sigma) or better.",
            (ReqFields.Priority, "high"), (ReqFields.Status, "approved"), (ReqFields.VerificationMethod, "test"), (Attr + "Area", "ADCS"));
        var detumble = k.Req(reqPkg, "SAT-021", "Detumbling", "The ADCS shall reduce the body rates below 0.5 deg/s within 3 orbits after separation.",
            (ReqFields.Priority, "medium"), (ReqFields.Status, "approved"), (ReqFields.VerificationMethod, "analysis"), (Attr + "Area", "ADCS"));
        var downlink = k.Req(reqPkg, "SAT-030", "Downlink Rate", "The communication subsystem shall downlink payload data at 1 Mbit/s or more in S-band.",
            (ReqFields.Priority, "high"), (ReqFields.Status, "approved"), (ReqFields.VerificationMethod, "test"), (Attr + "Area", "COMMS"));
        var uplink = k.Req(reqPkg, "SAT-031", "Telecommand Uplink", "The communication subsystem shall receive telecommands over UHF at 9.6 kbit/s.",
            (ReqFields.Priority, "high"), (ReqFields.Status, "approved"), (ReqFields.VerificationMethod, "test"), (Attr + "Area", "COMMS"));
        var storage = k.Req(reqPkg, "SAT-040", "Onboard Storage", "The on-board computer shall store at least 8 GB of payload data.",
            (ReqFields.Priority, "medium"), (ReqFields.Status, "approved"), (ReqFields.VerificationMethod, "test"), (Attr + "Area", "OBC"));
        var massReq = k.Req(reqPkg, "SAT-050", "Mass Limit", "The total spacecraft mass shall not exceed 4.0 kg (3U CubeSat standard).",
            (ReqFields.Priority, "critical"), (ReqFields.Status, "approved"), (ReqFields.VerificationMethod, "inspection"), (Attr + "Area", "System"));
        var safeMode = k.Req(reqPkg, "SAT-060", "Safe Mode Entry",
            "The spacecraft shall enter safe mode within 10 s when the battery state of charge falls below 20 %.",
            (ReqFields.Priority, "critical"), (ReqFields.Status, "approved"), (ReqFields.VerificationMethod, "test"), (ReqFields.Risk, "medium"), (Attr + "Area", "OBC"));

        // ------------------------------------------------------------ interfaces
        var powerBus = k.Block(interfaces, "PowerBus", "Regulated 3.3 V / 5 V and unregulated battery bus.", kind: ElementKind.InterfaceBlock);
        k.Value(powerBus, "voltage", value: "5", unit: "V");
        var dataBus = k.Block(interfaces, "DataBus", "CAN 2.0B housekeeping and command bus; LVDS for payload data.", kind: ElementKind.InterfaceBlock);
        k.Value(dataBus, "bitRate", value: "1000", unit: "kbit/s");
        var rfLink = k.Block(interfaces, "RFLink", "S-band downlink and UHF uplink to the ground station.", kind: ElementKind.InterfaceBlock);
        k.Value(rfLink, "frequency", value: "2245", unit: "MHz");

        // ------------------------------------------------------------ structure
        var sat = k.Block(structure, "CubeSat", "The 3U spacecraft (system of interest).");
        var eps = k.Block(structure, "EPS", "Electrical power subsystem: deployable solar array, Li-ion battery and power distribution.");
        var solar = k.Block(structure, "SolarArray", "Two deployable 3U panels with triple-junction cells.");
        var battery = k.Block(structure, "Battery", "4S Li-ion battery pack with heaters.");
        var adcs = k.Block(structure, "ADCS", "Attitude determination and control: sun sensors, star tracker, reaction wheels, magnetorquers.");
        var comms = k.Block(structure, "COMMS", "UHF telecommand receiver and S-band telemetry / payload transmitter.");
        var obc = k.Block(structure, "OBC", "On-board computer: flight software, mode management and payload data storage.");
        var payload = k.Block(structure, "Payload", "Multispectral push-broom imager.");
        var frame = k.Block(structure, "Frame", "3U aluminium structure, harness and thermal hardware.");
        var station = k.Block(structure, "GroundStation", "Mission ground segment with S-band and UHF antennas.");
        var operatorActor = m.Create(structure, ElementKind.Actor, "Mission Operator");

        var pEps = k.Part(sat, "eps", eps); var pAdcs = k.Part(sat, "adcs", adcs); var pComms = k.Part(sat, "comms", comms);
        var pObc = k.Part(sat, "obc", obc); var pPayload = k.Part(sat, "payload", payload); var pFrame = k.Part(sat, "frame", frame);
        k.Part(eps, "solarArray", solar, "2");
        k.Part(eps, "battery", battery);
        foreach (var (whole, part) in new[] { (sat, eps), (sat, adcs), (sat, comms), (sat, obc), (sat, payload), (sat, frame), (eps, solar), (eps, battery) })
            m.Connect(ElementKind.Composition, whole, part);

        k.Value(solar, "peakPower", value: "4.2", unit: "W");
        k.Value(battery, "capacity", value: "40", unit: "Wh");
        k.Value(battery, "cycleLife", "Integer", "5000");
        k.Value(adcs, "pointingAccuracy", value: "0.3", unit: "deg");
        k.Value(adcs, "slewRate", value: "1.5", unit: "deg/s");
        k.Value(comms, "downlinkRate", value: "2", unit: "Mbit/s");
        k.Value(comms, "uplinkRate", value: "9.6", unit: "kbit/s");
        k.Value(obc, "storage", value: "16", unit: "GB");
        k.Value(payload, "gsd", value: "25", unit: "m");
        k.Value(payload, "swath", value: "40", unit: "km");
        k.Value(frame, "units", "Integer", "3");

        // Ports (typed by interface blocks) and the operations OBC offers to the ground.
        var epsPowerOut = k.Port(eps, "powerOut", powerBus, "out");
        var epsData = k.Port(eps, "data", dataBus, "inout");
        var obcPowerIn = k.Port(obc, "powerIn", powerBus, "in");
        var obcData = k.Port(obc, "dataBus", dataBus, "inout");
        var adcsPowerIn = k.Port(adcs, "powerIn", powerBus, "in");
        var adcsData = k.Port(adcs, "data", dataBus, "inout");
        var commsPowerIn = k.Port(comms, "powerIn", powerBus, "in");
        var commsData = k.Port(comms, "data", dataBus, "inout");
        k.Port(comms, "antenna", rfLink, "inout");
        var payloadPowerIn = k.Port(payload, "powerIn", powerBus, "in");
        var payloadData = k.Port(payload, "data", dataBus, "inout");
        var op = m.Create(obc, ElementKind.Operation, "enterSafeMode");
        m.Set(op, FieldKeys.Signature, "() : Boolean");

        // ------------------------------------------------------------ budgets (parametrics)
        var (consumption, cp) = k.Constraint(analysis, "PowerConsumption", "total = eps + adcs + comms + obc + payload", "eps", "adcs", "comms", "obc", "payload", "total");
        var (marginEq, mp) = k.Constraint(analysis, "PowerMargin", "margin = (solar * sun - total) / total * 100", "solar", "sun", "total", "margin");
        var (massRollup, mr) = k.Constraint(analysis, "MassRollup", "total = eps + adcs + comms + obc + payload + frame", "eps", "adcs", "comms", "obc", "payload", "frame", "total");

        var vPowEps = k.Value(sat, "powerEps", value: "0.3", unit: "W");
        var vPowAdcs = k.Value(sat, "powerAdcs", value: "1.2", unit: "W", distribution: "normal(1.2, 0.1)");
        var vPowComms = k.Value(sat, "powerComms", value: "1.0", unit: "W", distribution: "normal(1.0, 0.15)");
        var vPowObc = k.Value(sat, "powerObc", value: "0.4", unit: "W");
        var vPowPayload = k.Value(sat, "powerPayload", value: "1.5", unit: "W", distribution: "uniform(1.2, 1.8)");
        var vSolar = k.Value(sat, "solarPower", value: "8.4", unit: "W");
        var vSun = k.Value(sat, "sunFraction", value: "0.62", distribution: "uniform(0.58, 0.66)");
        var vTotal = k.Value(sat, "totalPower", unit: "W");
        var vMargin = k.Value(sat, "powerMargin", unit: "%");
        var vMassEps = k.Value(sat, "massEps", value: "0.62", unit: "kg");
        var vMassAdcs = k.Value(sat, "massAdcs", value: "0.45", unit: "kg");
        var vMassComms = k.Value(sat, "massComms", value: "0.25", unit: "kg");
        var vMassObc = k.Value(sat, "massObc", value: "0.15", unit: "kg");
        var vMassPayload = k.Value(sat, "massPayload", value: "0.9", unit: "kg", distribution: "triangular(0.85, 0.9, 1.0)");
        var vMassFrame = k.Value(sat, "massFrame", value: "1.1", unit: "kg");
        var vMassTotal = k.Value(sat, "totalMass", unit: "kg");

        var uConsumption = k.Part(sat, "consumptionEq", consumption);
        var uMargin = k.Part(sat, "marginEq", marginEq);
        var uMass = k.Part(sat, "massEq", massRollup);
        var powerBindings = new[]
        {
            k.Bind(sat, vPowEps, uConsumption, cp["eps"]), k.Bind(sat, vPowAdcs, uConsumption, cp["adcs"]), k.Bind(sat, vPowComms, uConsumption, cp["comms"]),
            k.Bind(sat, vPowObc, uConsumption, cp["obc"]), k.Bind(sat, vPowPayload, uConsumption, cp["payload"]), k.Bind(sat, vTotal, uConsumption, cp["total"]),
            k.Bind(sat, vTotal, uMargin, mp["total"]), k.Bind(sat, vSolar, uMargin, mp["solar"]), k.Bind(sat, vSun, uMargin, mp["sun"]), k.Bind(sat, vMargin, uMargin, mp["margin"]),
        };
        var massBindings = new[]
        {
            k.Bind(sat, vMassEps, uMass, mr["eps"]), k.Bind(sat, vMassAdcs, uMass, mr["adcs"]), k.Bind(sat, vMassComms, uMass, mr["comms"]),
            k.Bind(sat, vMassObc, uMass, mr["obc"]), k.Bind(sat, vMassPayload, uMass, mr["payload"]), k.Bind(sat, vMassFrame, uMass, mr["frame"]),
            k.Bind(sat, vMassTotal, uMass, mr["total"]),
        };

        // ------------------------------------------------------------ IBD connectors
        var wires = new[]
        {
            k.Wire(sat, pEps, epsPowerOut, pObc, obcPowerIn, "Power"),
            k.Wire(sat, pEps, epsPowerOut, pAdcs, adcsPowerIn, "Power"),
            k.Wire(sat, pEps, epsPowerOut, pComms, commsPowerIn, "Power"),
            k.Wire(sat, pEps, epsPowerOut, pPayload, payloadPowerIn, "Power"),
            k.Wire(sat, pEps, epsData, pObc, obcData, "Housekeeping"),
            k.Wire(sat, pObc, obcData, pAdcs, adcsData, "Attitude commands"),
            k.Wire(sat, pObc, obcData, pComms, commsData, "Telemetry"),
            k.Wire(sat, pPayload, payloadData, pObc, obcData, "Image data"),
        };

        // ------------------------------------------------------------ operating modes
        var modes = m.Create(sat, ElementKind.StateMachine, "Operating Modes");
        var s0 = m.Create(modes, ElementKind.InitialState);
        var deploy = k.State(modes, "Deployment", entry: "soc = 80; payloadPower = 0");
        var tumble = k.State(modes, "Detumbling", entry: "rate = 8", exit: "rate = 0.3");
        var commission = k.State(modes, "Commissioning", doActivity: "checks = checks + 1");
        var nominal = k.State(modes, "Nominal");
        var n0 = m.Create(nominal, ElementKind.InitialState);
        var idle = k.State(nominal, "Idle", entry: "payloadPower = 0");
        var imaging = k.State(nominal, "Imaging", entry: "payloadPower = 1.5; soc = soc - 2");
        var downlinkSt = k.State(nominal, "Downlink", entry: "soc = soc - 3");
        var safe = k.State(modes, "Safe", entry: "payloadPower = 0; beacon = 1");
        var eol = k.State(modes, "End of Life", entry: "transmitter = 0");
        var fin = m.Create(modes, ElementKind.FinalState);
        var tInit = k.Go(s0, deploy);
        var tDeploy = k.Go(deploy, tumble, "after(30 min)");
        var tTumble = k.Go(tumble, commission, "after(4 h)");
        var tComm = k.Go(commission, nominal, "commissioningComplete");
        var tN0 = k.Go(n0, idle);
        var tImg = k.Go(idle, imaging, "targetInView");
        var tImgDone = k.Go(imaging, idle, "after(6 min)");
        var tPass = k.Go(idle, downlinkSt, "stationPass");
        var tPassDone = k.Go(downlinkSt, idle, "after(8 min)");
        var tSafe = k.Go(nominal, safe, "when(soc < 20)", effect: "alarm = 1");
        var tResume = k.Go(safe, nominal, "resumeNominal", guard: "soc > 60");
        var tEol = k.Go(nominal, eol, "endOfMission");
        var tFin = k.Go(eol, fin, "after(1 h)");
        m.Connect(ElementKind.Satisfy, modes, safeMode);

        // ------------------------------------------------------------ imaging activity (timed, swimlanes = allocation)
        var imagingAct = m.Create(behavior, ElementKind.Activity, "Acquire Image");
        m.Set(imagingAct, FieldKeys.Documentation, "One imaging pass. Durations drive the timed simulation; swimlanes allocate each action to a subsystem.");
        var laneAdcs = m.Create(imagingAct, ElementKind.Partition, "attitude"); m.Set(laneAdcs, FieldKeys.Represents, adcs.Id);
        var lanePayload = m.Create(imagingAct, ElementKind.Partition, "imager"); m.Set(lanePayload, FieldKeys.Represents, payload.Id);
        var laneObc = m.Create(imagingAct, ElementKind.Partition, "computer"); m.Set(laneObc, FieldKeys.Represents, obc.Id);
        var ai = m.Create(imagingAct, ElementKind.InitialNode);
        var slew = k.Action(imagingAct, "Slew to Target", "pointingError = 0.3", "90s");
        var settle = k.Action(imagingAct, "Settle Attitude", null, "20s");
        var capture = k.Action(imagingAct, "Capture Scene", "images = images + 1", "30s");
        var compress = k.Action(imagingAct, "Compress Image", null, "20s");
        var store = k.Action(imagingAct, "Store Image", "stored = stored + 0.4", "5s");
        var af = m.Create(imagingAct, ElementKind.ActivityFinalNode);
        var actFlows = new[] { k.Flow(ai, slew), k.Flow(slew, settle), k.Flow(settle, capture), k.Flow(capture, compress), k.Flow(compress, store), k.Flow(store, af) };

        // ------------------------------------------------------------ use cases and ground pass
        var ucTask = m.Create(behavior, ElementKind.UseCase, "Task Imaging");
        var ucDownlink = m.Create(behavior, ElementKind.UseCase, "Downlink Data");
        var ucHealth = m.Create(behavior, ElementKind.UseCase, "Monitor Health");
        var ucUplink = m.Create(behavior, ElementKind.UseCase, "Uplink Commands");
        m.Connect(ElementKind.Association, operatorActor, ucTask);
        m.Connect(ElementKind.Association, operatorActor, ucDownlink);
        m.Connect(ElementKind.Association, operatorActor, ucHealth);
        m.Connect(ElementKind.Include, ucTask, ucUplink);

        var pass = m.Create(behavior, ElementKind.Interaction, "Ground Pass");
        var llGs = m.Create(pass, ElementKind.Lifeline, "ground"); m.Set(llGs, FieldKeys.Type, station.Id);
        var llComms = m.Create(pass, ElementKind.Lifeline, "comms"); m.Set(llComms, FieldKeys.Type, comms.Id);
        var llObc = m.Create(pass, ElementKind.Lifeline, "obc"); m.Set(llObc, FieldKeys.Type, obc.Id);
        Relationship Msg(Element a, Element b, string name, string sort = "synchCall")
        {
            var x = m.Connect(ElementKind.Message, a, b);
            m.Set(x, FieldKeys.Name, name);
            m.Set(x, FieldKeys.MessageSort, sort);
            return x;
        }
        var messages = new[]
        {
            Msg(llGs, llComms, "acquisitionOfSignal", "asynchCall"),
            Msg(llComms, llObc, "passStarted", "asynchCall"),
            Msg(llGs, llComms, "uplinkSchedule"),
            Msg(llComms, llObc, "storeSchedule"),
            Msg(llObc, llComms, "ack", "reply"),
            Msg(llObc, llComms, "imageData", "asynchCall"),
            Msg(llComms, llGs, "downlink", "asynchCall"),
        };
        var loop = m.Create(pass, ElementKind.CombinedFragment);
        m.Set(loop, FieldKeys.Operator, "loop");
        m.Set(loop, FieldKeys.Guard, "stored images");

        // ------------------------------------------------------------ satisfy
        m.Connect(ElementKind.Satisfy, payload, gsd);
        m.Connect(ElementKind.Satisfy, sat, lifetime);
        m.Connect(ElementKind.Satisfy, eps, margin);
        m.Connect(ElementKind.Satisfy, uMargin, margin);
        m.Connect(ElementKind.Satisfy, battery, dod);
        m.Connect(ElementKind.Satisfy, adcs, pointing);
        m.Connect(ElementKind.Satisfy, adcs, detumble);
        m.Connect(ElementKind.Satisfy, comms, downlink);
        m.Connect(ElementKind.Satisfy, comms, uplink);
        m.Connect(ElementKind.Satisfy, obc, storage);
        m.Connect(ElementKind.Satisfy, sat, massReq);
        m.Connect(ElementKind.Satisfy, uMass, massReq);

        // ------------------------------------------------------------ verification
        var tests = new[]
        {
            k.Test(verification, "Payload GSD Test", "pass", "Imaging of a calibrated target in the optical stimulator; measured GSD 24.6 m.", gsd),
            k.Test(verification, "Lifetime Analysis", "pass", "Radiation dose, battery cycling and reaction-wheel wear analysis for 2 years.", lifetime),
            k.Test(verification, "Power Budget Analysis", "pass", "Parametric solve of the power budget (see the Power Budget diagram) plus a Monte Carlo run.", margin),
            k.Test(verification, "Battery Eclipse Cycling Test", "pass", "Thermal vacuum test with 36 min eclipse cycles; worst depth of discharge 22 %.", dod),
            k.Test(verification, "ADCS Hardware-in-the-Loop Test", "pass", "Closed-loop pointing on the air-bearing table.", pointing),
            k.Test(verification, "Detumble Simulation", "pass", "B-dot controller simulation from 8 deg/s initial rates.", detumble),
            k.Test(verification, "RF End-to-End Test", "fail", "Downlink reached 0.8 Mbit/s: antenna matching network being reworked. Uplink passed.", downlink, uplink),
            k.Test(verification, "OBC Functional Test", "pass", "Flight software regression suite including mass-memory write / read.", storage),
            k.Test(verification, "Mass Properties Inspection", "pass", "Weighing of the integrated flight model.", massReq),
            k.Test(verification, "Safe Mode Transition Test", "", "Inject a low state-of-charge telemetry value and time the transition to Safe.", safeMode),
        };

        // ------------------------------------------------------------ trade study: battery
        var optA = k.Block(trades, "Battery 18650 Li-ion", "Four COTS 18650 cells in a 4S pack.");
        k.Value(optA, "energy", value: "40", unit: "Wh"); k.Value(optA, "mass", value: "0.27", unit: "kg"); k.Value(optA, "cost", value: "3500", unit: "EUR"); k.Value(optA, "cycleLife", value: "5000");
        var optB = k.Block(trades, "Battery LiPo Pouch", "Two lithium-polymer pouch cells; lighter, shorter cycle life.");
        k.Value(optB, "energy", value: "36", unit: "Wh"); k.Value(optB, "mass", value: "0.2", unit: "kg"); k.Value(optB, "cost", value: "2800", unit: "EUR"); k.Value(optB, "cycleLife", value: "2500");
        var optC = k.Block(trades, "Battery Space-Qualified", "Space-qualified battery module with flight heritage.");
        k.Value(optC, "energy", value: "45", unit: "Wh"); k.Value(optC, "mass", value: "0.35", unit: "kg"); k.Value(optC, "cost", value: "9000", unit: "EUR"); k.Value(optC, "cycleLife", value: "8000");
        var study = m.Create(trades, ElementKind.TradeStudy, "Battery Selection");
        m.Set(study, FieldKeys.Documentation, "Weighted-sum comparison of three battery options. Open it in the Variants panel to see scores and weight sensitivity.");
        new TradeStudyDefinition
        {
            Alternatives = [optA.Id, optB.Id, optC.Id],
            Criteria =
            [
                new TradeCriterion { Name = "energy", Weight = 3, Direction = "maximize" },
                new TradeCriterion { Name = "mass", Weight = 2, Direction = "minimize" },
                new TradeCriterion { Name = "cost", Weight = 2, Direction = "minimize" },
                new TradeCriterion { Name = "cycleLife", Weight = 2, Direction = "maximize", Normalization = "utility", Utility = "2000:0, 5000:0.9, 8000:1" },
            ],
        }.Save(m, study);

        // ================================================================ diagrams
        // Requirements overview: the requirement tree, two parents with their children.
        var dReq = m.CreateDiagram(reqPkg, DiagramType.Requirement, "Mission Requirements");
        var y = k.Row(dReq, 40, 40, 40, 260, mission, power);
        y = k.Row(dReq, 40, y + 60, 30, 240, gsd, lifetime, disposal, margin, dod);
        k.Row(dReq, 40, y + 60, 30, 240, pointing, detumble, downlink, uplink, storage, massReq, safeMode);

        // Satisfaction: requirements on top, the design elements that satisfy them underneath.
        var dSat = m.CreateDiagram(reqPkg, DiagramType.Requirement, "Requirement Satisfaction");
        y = k.Row(dSat, 40, 40, 30, 230, gsd, lifetime, margin, dod, pointing, detumble);
        y = k.Row(dSat, 40, y + 40, 30, 230, downlink, uplink, storage, massReq, safeMode, disposal);
        k.Row(dSat, 40, y + 90, 40, 170, payload, sat, eps, battery, adcs, comms, obc);
        k.ShowEdges(dSat, ElementKind.Satisfy);

        // Verification plan: requirements and the test cases verifying them.
        var dVer = m.CreateDiagram(verification, DiagramType.Requirement, "Verification Plan");
        y = k.Row(dVer, 40, 40, 30, 230, gsd, lifetime, margin, dod, pointing, detumble);
        y = k.Row(dVer, 40, y + 40, 30, 230, downlink, uplink, storage, massReq, safeMode, disposal);
        k.Grid(dVer, 40, y + 90, 5, 220, 40, 40, tests);
        k.ShowEdges(dVer, ElementKind.Verify);

        // Architecture BDD, arranged automatically (whole above parts).
        var dBdd = m.CreateDiagram(structure, DiagramType.BlockDefinition, "Spacecraft Architecture");
        foreach (var b in new[] { sat, eps, adcs, comms, obc, payload, frame, solar, battery }) k.Place(dBdd, b, 0, 0, 200);
        k.Arrange(dBdd);

        var dIf = m.CreateDiagram(interfaces, DiagramType.BlockDefinition, "Interfaces");
        k.Row(dIf, 40, 40, 50, 200, powerBus, dataBus, rfLink);

        // Context: the spacecraft, the ground station and the operator.
        var dCtx = m.CreateDiagram(structure, DiagramType.BlockDefinition, "Mission Context");
        k.Place(dCtx, operatorActor, 60, 60);
        k.Place(dCtx, station, 200, 40, 200);
        k.Place(dCtx, sat, 480, 40, 220);
        var link = m.Connect(ElementKind.Association, station, sat);
        m.Set(link, FieldKeys.Name, "RF link");
        m.AddEdge(dCtx, link);

        // IBD: EPS feeds every subsystem; OBC is the data hub.
        var dIbd = m.CreateDiagram(sat, DiagramType.InternalBlock, "Power and Data Flow");
        var nEps = k.Place(dIbd, pEps, 60, 60, 200, 110, edges: false);
        var nObc = k.Place(dIbd, pObc, 60, 360, 200, 110, edges: false);
        var nAdcs = k.Place(dIbd, pAdcs, 560, 40, 200, 110, edges: false);
        var nComms = k.Place(dIbd, pComms, 560, 230, 200, 110, edges: false);
        var nPay = k.Place(dIbd, pPayload, 560, 420, 200, 110, edges: false);
        k.Place(dIbd, pFrame, 60, 580, 200, 70, edges: false);
        k.PortOn(dIbd, nEps, epsPowerOut, "R", 0.5);
        k.PortOn(dIbd, nEps, epsData, "B", 0.3);
        k.PortOn(dIbd, nObc, obcPowerIn, "T", 0.8);
        k.PortOn(dIbd, nObc, obcData, "R", 0.6);
        foreach (var (n, pw, da) in new[] { (nAdcs, adcsPowerIn, adcsData), (nComms, commsPowerIn, commsData), (nPay, payloadPowerIn, payloadData) })
        {
            k.PortOn(dIbd, n, pw, "L", 0.5);
            k.PortOn(dIbd, n, da, "L", 0.8);
        }
        foreach (var w in wires) m.AddEdge(dIbd, w);

        // Power budget parametric diagram.
        var dPow = m.CreateDiagram(sat, DiagramType.Parametric, "Power Budget");
        var powInputs = new[] { vPowEps, vPowAdcs, vPowComms, vPowObc, vPowPayload };
        for (var i = 0; i < powInputs.Length; i++) k.Place(dPow, powInputs[i], 40, 110 + i * 70, 190, 50, edges: false);
        var nCons = k.Place(dPow, uConsumption, 320, 30, 340, 470, edges: false);
        for (var i = 0; i < 5; i++) k.BorderAt(dPow, nCons, cp[new[] { "eps", "adcs", "comms", "obc", "payload" }[i]], "L", 135 + i * 70);
        k.BorderAt(dPow, nCons, cp["total"], "R", 275);
        k.Place(dPow, vTotal, 750, 250, 180, 50, edges: false);
        var nMargin = k.Place(dPow, uMargin, 1020, 200, 350, 270, edges: false);
        k.BorderAt(dPow, nMargin, mp["total"], "L", 275);
        k.BorderAt(dPow, nMargin, mp["solar"], "L", 345);
        k.BorderAt(dPow, nMargin, mp["sun"], "L", 415);
        k.BorderAt(dPow, nMargin, mp["margin"], "R", 345);
        k.Place(dPow, vSolar, 750, 320, 180, 50, edges: false);
        k.Place(dPow, vSun, 750, 390, 180, 50, edges: false);
        k.Place(dPow, vMargin, 1460, 320, 180, 50, edges: false);
        foreach (var b in powerBindings) m.AddEdge(dPow, b);

        // Mass budget parametric diagram.
        var dMass = m.CreateDiagram(sat, DiagramType.Parametric, "Mass Budget");
        var massInputs = new[] { vMassEps, vMassAdcs, vMassComms, vMassObc, vMassPayload, vMassFrame };
        var massParams = new[] { "eps", "adcs", "comms", "obc", "payload", "frame" };
        for (var i = 0; i < massInputs.Length; i++) k.Place(dMass, massInputs[i], 40, 110 + i * 70, 190, 50, edges: false);
        var nMass = k.Place(dMass, uMass, 320, 30, 420, 540, edges: false);
        for (var i = 0; i < massParams.Length; i++) k.BorderAt(dMass, nMass, mr[massParams[i]], "L", 135 + i * 70);
        k.BorderAt(dMass, nMass, mr["total"], "R", 310);
        k.Place(dMass, vMassTotal, 830, 285, 180, 50, edges: false);
        foreach (var b in massBindings) m.AddEdge(dMass, b);

        // Operating modes.
        var dStm = m.CreateDiagram(modes, DiagramType.StateMachine, "Operating Modes");
        k.Place(dStm, s0, 40, 74);
        k.Place(dStm, deploy, 120, 50, 170, 76);
        k.Place(dStm, tumble, 380, 50, 170, 76);
        k.Place(dStm, commission, 640, 50, 190, 76);
        var nNom = k.Place(dStm, nominal, 330, 220, 520, 280);
        k.Place(dStm, n0, 360, 300, 0, 0, parent: nNom);
        k.Place(dStm, idle, 430, 280, 150, 70, parent: nNom);
        k.Place(dStm, imaging, 660, 250, 160, 76, parent: nNom);
        k.Place(dStm, downlinkSt, 660, 390, 160, 76, parent: nNom);
        k.Place(dStm, safe, 40, 320, 190, 76);
        k.Place(dStm, eol, 930, 320, 170, 70);
        k.Place(dStm, fin, 1001, 470);
        foreach (var t in new[] { tInit, tDeploy, tTumble, tComm, tN0, tImg, tImgDone, tPass, tPassDone, tSafe, tResume, tEol, tFin }) m.AddEdge(dStm, t);

        // Timed imaging activity with swimlanes; actions are allocated to the lane's subsystem.
        var dAct = m.CreateDiagram(imagingAct, DiagramType.Activity, "Acquire Image");
        k.Place(dAct, laneAdcs, 40, 20, 240, 560, edges: false);
        k.Place(dAct, lanePayload, 280, 20, 240, 560, edges: false);
        k.Place(dAct, laneObc, 520, 20, 240, 560, edges: false);
        k.Place(dAct, ai, 146, 70);
        k.Place(dAct, slew, 95, 130, 130, 70);
        k.Place(dAct, settle, 95, 240);
        k.Place(dAct, capture, 335, 240, 130, 70);
        k.Place(dAct, compress, 575, 240);
        k.Place(dAct, store, 575, 350, 130, 70);
        k.Place(dAct, af, 626, 480);
        foreach (var f in actFlows) m.AddEdge(dAct, f);
        Allocation.SyncFromSwimlanes(m, dAct);

        var dUc = m.CreateDiagram(behavior, DiagramType.UseCase, "Mission Operations");
        k.Place(dUc, operatorActor, 40, 150);
        k.Place(dUc, ucTask, 240, 40);
        k.Place(dUc, ucDownlink, 240, 160);
        k.Place(dUc, ucHealth, 240, 280);
        k.Place(dUc, ucUplink, 500, 40);

        var dSeq = m.CreateDiagram(pass, DiagramType.Sequence, "Ground Pass");
        k.Place(dSeq, llGs, 40, 20, 150, 470);
        k.Place(dSeq, llComms, 300, 20, 150, 470);
        k.Place(dSeq, llObc, 560, 20, 150, 470);
        k.Place(dSeq, loop, 330, 350, 380, 110);
        var my = 100.0;
        foreach (var msg in messages)
        {
            var edge = m.AddEdge(dSeq, msg)!;
            m.SetWaypoints(dSeq, edge, [new Pt(0, my)]);
            my += 44;
        }

        // Trade study alternatives.
        var dTrade = m.CreateDiagram(trades, DiagramType.BlockDefinition, "Battery Options");
        k.Row(dTrade, 40, 40, 40, 220, optA, optB, optC);

        // ------------------------------------------------------------ saved views
        var views = SavedViews.DefaultOwner(m);
        SavedViews.SaveTable(m, new TableDefinition
        {
            Query = "kind:Requirement",
            Columns = [new("reqId"), new("name"), new("attr:Area", "Area"), new("field:status", "Status"), new("satisfiedBy"), new("verifiedBy")],
            SortKey = "reqId",
        }, "Requirement status", owner: views);
        SavedViews.SaveTable(m, new TableDefinition
        {
            Query = "kind:TestCase",
            Columns = [new("name"), new("field:verdict", "Verdict"), new("verifies"), new("documentation")],
            SortKey = "name",
        }, "Verification status", owner: views);
        SavedViews.SaveTable(m, new TableDefinition
        {
            Query = "kind:Block in:Architecture",
            Columns = [new("name"), new("satisfies"), new("allocatedFrom"), new("documentation")],
            SortKey = "name",
        }, "Subsystems", owner: views);
        SavedViews.SaveQuery(m, "kind:Requirement where not has:in:verify", "Unverified requirements", owner: views);
        SavedViews.SaveQuery(m, "kind:TestCase where field:verdict = fail", "Failed tests", owner: views);
        SavedViews.SaveRule(m, "kind:Block where gsd > 30", "Payload resolution", "warning",
            "{name}: ground sample distance {gsd} m exceeds the 30 m target of SAT-002", owner: views);

        m.History.Clear();
        return m;
    }
}
