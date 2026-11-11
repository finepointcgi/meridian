using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Safety;

namespace Meridian.Core.Samples;

/// <summary>
/// Safety &amp; reliability sample: an electric parking brake with an FMEA (failure modes on the controller, actuator,
/// sensors and power supply, rated S/O/D with mitigating requirements and verifying tests) and a fault tree for
/// "Loss of parking brake hold" with AND, OR and 2-of-3 vote gates, a repeated basic event and probabilities.
/// <para>Deliberate gap: failure mode <c>Wheel speed signal stuck</c> is high risk with no mitigating requirement (SAF-003).</para>
/// </summary>
public static class ParkingBrakeSample
{
    public const string Name = "Electric Parking Brake";

    public static ModelRepository Create(string? name = null)
    {
        var m = new ModelRepository(string.IsNullOrWhiteSpace(name) ? Name : name.Trim());
        var k = new SampleKit(m);
        m.Set(m.Root, FieldKeys.Documentation,
            "Electric parking brake (EPB). Worked safety example: FMEA with RPN and AIAG-VDA action priority, mitigating requirements, " +
            "verifying tests and a fault tree with minimal cut sets. Ratings and probabilities are illustrative.");

        var reqPkg = k.Pkg(m.Root, "Requirements");
        var arch = k.Pkg(m.Root, "Architecture", "EPB hardware and software.");
        var safety = k.Pkg(m.Root, "Safety Analysis", "FMEA (failure modes on the architecture) and fault trees.");
        var verification = k.Pkg(m.Root, "Verification");

        // ------------------------------------------------------------ requirements
        var hold = k.Req(reqPkg, "EPB-001", "Hold on slope", "The EPB shall hold the parked vehicle on a 30 % slope.");
        var watchdog = k.Req(reqPkg, "EPB-010", "Controller watchdog", "The brake controller shall be supervised by an independent watchdog that keeps the brake applied on a reset.");
        var stall = k.Req(reqPkg, "EPB-011", "Stall current detection", "The controller shall detect actuator stall from motor current within 50 ms and report a fault.");
        var backup = k.Req(reqPkg, "EPB-012", "Backup supply", "The actuator shall be powered from a backup supply when the main supply drops below 9 V.");

        // ------------------------------------------------------------ architecture
        var system = k.Block(arch, "EPB System");
        var controller = k.Block(arch, "Brake Controller", "ECU running the EPB software.");
        var actuator = k.Block(arch, "Motor Actuator", "Caliper-integrated motor on gear unit.");
        var sensor = k.Block(arch, "Wheel Speed Sensor");
        var supply = k.Block(arch, "Power Supply");
        k.Part(system, "ecu", controller);
        k.Part(system, "actuator", actuator, "2");
        k.Part(system, "wss", sensor, "3");
        k.Part(system, "psu", supply);
        m.Connect(ElementKind.Satisfy, system, hold);
        m.Connect(ElementKind.Satisfy, controller, watchdog);
        m.Connect(ElementKind.Satisfy, controller, stall);
        m.Connect(ElementKind.Satisfy, supply, backup);

        // ------------------------------------------------------------ FMEA
        Element Fm(Element item, string name, string function, string effect, string cause, int s, int o, int d, string? actions, string? probability)
        {
            var fm = m.Create(item, ElementKind.FailureMode, name);
            m.Set(fm, SafetyFields.Function, function);
            m.Set(fm, SafetyFields.Effect, effect);
            m.Set(fm, SafetyFields.Cause, cause);
            m.Set(fm, SafetyFields.Severity, s.ToString(System.Globalization.CultureInfo.InvariantCulture));
            m.Set(fm, SafetyFields.Occurrence, o.ToString(System.Globalization.CultureInfo.InvariantCulture));
            m.Set(fm, SafetyFields.Detection, d.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (actions != null) { m.Set(fm, SafetyFields.Actions, actions); m.Set(fm, SafetyFields.ActionStatus, "in progress"); }
            if (probability != null) m.Set(fm, SafetyFields.Probability, probability);
            return fm;
        }
        var hang = Fm(controller, "Software hang", "Command brake apply / release", "Brake cannot be applied or released on command", "Task overrun, watchdog misconfigured", 9, 3, 4, "Independent watchdog with safe state", "2e-5");
        var motorStall = Fm(actuator, "Motor stall", "Generate clamp force", "Insufficient clamp force: vehicle may roll away", "Gear wear, corrosion", 8, 4, 5, "Stall current monitoring; end-of-line clamp force test", "1e-4");
        var stuck = Fm(sensor, "Wheel speed signal stuck", "Detect standstill", "Standstill misdetected: auto-apply inhibited", "Connector corrosion, sensor IC fault", 7, 5, 7, null, "5e-5");
        var under = Fm(supply, "Undervoltage", "Supply actuator current", "Actuator cannot reach clamp force", "Battery aged, cold crank", 6, 3, 3, null, "3e-4");
        m.Connect(ElementKind.Mitigate, watchdog, hang);
        m.Connect(ElementKind.Mitigate, stall, motorStall);
        m.Connect(ElementKind.Mitigate, backup, under);

        var tWatchdog = k.Test(verification, "Watchdog reset test", "pass", "Force a controller reset while parked; the brake stays applied.", watchdog);
        var tStall = k.Test(verification, "Stall injection test", "pass", "Block the actuator; the stall fault is reported within 50 ms.", stall);
        m.Connect(ElementKind.Verify, tStall, motorStall);

        // ------------------------------------------------------------ fault tree
        Element Ev(string name, string kind, Element? represents = null, string? probability = null)
        {
            var e = m.Create(safety, ElementKind.FaultTreeEvent, name);
            m.Set(e, FieldKeys.Subtype, kind);
            if (represents != null) m.Set(e, SafetyFields.Ref, represents.Id);
            if (probability != null) m.Set(e, SafetyFields.Probability, probability);
            return e;
        }
        Element Gate(string kind, string? kOfN = null)
        {
            var g = m.Create(safety, ElementKind.FaultTreeGate);
            m.Set(g, FieldKeys.Subtype, kind);
            if (kOfN != null) m.Set(g, SafetyFields.K, kOfN);
            return g;
        }
        void Link(Element parent, params Element[] children) { foreach (var c in children) m.Connect(ElementKind.FaultTreeLink, parent, c, safety); }

        var top = Ev("Loss of parking brake hold", SafetyFields.Top, system);
        var gTop = Gate(SafetyFields.Or);
        var eCmd = Ev("Controller commands release", SafetyFields.Intermediate, controller);
        var gCmd = Gate(SafetyFields.Or);
        var ePower = Ev("Loss of actuator power", SafetyFields.Intermediate, supply);
        var gPower = Gate(SafetyFields.And);
        var eStand = Ev("Standstill not detected", SafetyFields.Intermediate);
        var gStand = Gate(SafetyFields.Or);
        var gVote = Gate(SafetyFields.Vote, "2");
        var bStall = Ev("Motor stall", SafetyFields.Basic, motorStall);
        var bHang = Ev("Software hang", SafetyFields.Basic, hang);
        var bSpurious = Ev("Spurious release request", SafetyFields.Undeveloped, probability: "1e-5");
        var bUnder = Ev("Undervoltage", SafetyFields.Basic, under);
        var bBackup = Ev("Backup supply fails", SafetyFields.Basic, probability: "1e-3");
        var s1 = Ev("WSS 1 stuck", SafetyFields.Basic, stuck);
        var s2 = Ev("WSS 2 stuck", SafetyFields.Basic, stuck);
        var s3 = Ev("WSS 3 stuck", SafetyFields.Basic, stuck);
        Link(top, gTop);
        Link(gTop, bStall, eCmd, ePower, eStand);
        Link(eCmd, gCmd); Link(gCmd, bHang, bSpurious);
        Link(ePower, gPower); Link(gPower, bUnder, bBackup);
        Link(eStand, gStand); Link(gStand, gVote, bUnder);   // undervoltage also blinds the sensors: a repeated event
        Link(gVote, s1, s2, s3);

        // ------------------------------------------------------------ diagrams
        var bdd = m.CreateDiagram(arch, DiagramType.BlockDefinition, "EPB architecture");
        k.Place(bdd, system, 300, 40);
        k.Row(bdd, 40, 260, 40, 200, controller, actuator, sensor, supply);
        k.ShowEdges(bdd);

        var req = m.CreateDiagram(safety, DiagramType.Requirement, "Mitigations");
        k.Row(req, 40, 40, 40, 220, watchdog, stall, backup);
        k.Row(req, 40, 260, 40, 220, hang, motorStall, under, stuck);
        k.Row(req, 40, 480, 40, 200, tWatchdog, tStall);
        k.ShowEdges(req, ElementKind.Mitigate, ElementKind.Verify);

        var fta = m.CreateDiagram(safety, DiagramType.FaultTree, "Loss of hold fta");
        foreach (var e in new[] { top, gTop, bStall, eCmd, ePower, eStand, gCmd, gPower, gStand, bHang, bSpurious, bUnder, bBackup, gVote, s1, s2, s3 })
        {
            var (w, h) = Notation.DefaultSizeOf(e);
            m.AddNode(fta, e, new Bounds(0, 0, w, h));
        }
        k.ShowEdges(fta, ElementKind.FaultTreeLink);
        m.SetBounds(fta, FaultTreeLayout.Tidy(fta), "Arrange");

        m.History.Clear();
        return m;
    }
}
