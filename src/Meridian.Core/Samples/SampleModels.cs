using Meridian.Core.Model;

namespace Meridian.Core.Samples;

/// <summary>A small but complete SysML model used for first-run demo and tests.</summary>
public static class SampleModels
{
    public static ModelRepository Vehicle()
    {
        var m = new ModelRepository("Vehicle System");
        m.Set(m.Root, FieldKeys.Documentation, "Example MBSE model of a passenger vehicle.");

        var reqPkg = m.Create(m.Root, ElementKind.Package, "Requirements");
        var structure = m.Create(m.Root, ElementKind.Package, "Structure");
        var behavior = m.Create(m.Root, ElementKind.Package, "Behavior");
        var verification = m.Create(m.Root, ElementKind.Package, "Verification");

        // Requirements
        var perf = Req(m, reqPkg, "REQ-001", "Performance", "The vehicle shall meet the performance targets below.");
        var accel = Req(m, perf, "REQ-002", "Acceleration", "The vehicle shall accelerate from 0 to 100 km/h in less than 8 s.");
        var range = Req(m, perf, "REQ-003", "Range", "The vehicle shall travel at least 600 km on a full tank.");
        var safety = Req(m, reqPkg, "REQ-004", "Braking", "The vehicle shall stop from 100 km/h within 40 m.");
        var engineReq = Req(m, reqPkg, "REQ-005", "Engine Power", "The engine shall deliver at least 150 kW.");

        // Structure
        var real = m.Create(structure, ElementKind.ValueType, "Power");
        m.Set(real, FieldKeys.Documentation, "Power in kW");
        var vehicle = m.Create(structure, ElementKind.Block, "Vehicle");
        var engine = m.Create(structure, ElementKind.Block, "Engine");
        var tank = m.Create(structure, ElementKind.Block, "FuelTank");
        var brakes = m.Create(structure, ElementKind.Block, "BrakeSystem");
        var fuelIf = m.Create(structure, ElementKind.InterfaceBlock, "FuelInterface");
        var driver = m.Create(structure, ElementKind.Actor, "Driver");

        var mass = m.Create(vehicle, ElementKind.Property, "mass");
        m.Set(mass, FieldKeys.Type, "Real");
        m.Set(mass, FieldKeys.DefaultValue, "1450");
        var eng = m.Create(vehicle, ElementKind.Property, "engine");
        m.Set(eng, FieldKeys.Type, engine.Id);
        var tnk = m.Create(vehicle, ElementKind.Property, "tank");
        m.Set(tnk, FieldKeys.Type, tank.Id);
        var brk = m.Create(vehicle, ElementKind.Property, "brakes");
        m.Set(brk, FieldKeys.Type, brakes.Id);

        var power = m.Create(engine, ElementKind.Property, "maxPower");
        m.Set(power, FieldKeys.Type, real.Id);
        m.Set(power, FieldKeys.DefaultValue, "160");
        var engIn = m.Create(engine, ElementKind.Port, "fuelIn");
        m.Set(engIn, FieldKeys.Type, fuelIf.Id);
        m.Set(engIn, FieldKeys.Direction, "in");
        var tankOut = m.Create(tank, ElementKind.Port, "fuelOut");
        m.Set(tankOut, FieldKeys.Type, fuelIf.Id);
        m.Set(tankOut, FieldKeys.Direction, "out");
        var cap = m.Create(tank, ElementKind.Property, "capacity");
        m.Set(cap, FieldKeys.Type, "Real");
        m.Set(cap, FieldKeys.DefaultValue, "55");
        var start = m.Create(engine, ElementKind.Operation, "start");
        m.Set(start, FieldKeys.Signature, "() : Boolean");

        m.Connect(ElementKind.Composition, vehicle, engine);
        m.Connect(ElementKind.Composition, vehicle, tank);
        m.Connect(ElementKind.Composition, vehicle, brakes);
        m.Connect(ElementKind.Satisfy, vehicle, accel);
        m.Connect(ElementKind.Satisfy, tank, range);
        m.Connect(ElementKind.Satisfy, brakes, safety);
        m.Connect(ElementKind.Satisfy, engine, engineReq);
        m.Connect(ElementKind.DeriveReqt, engineReq, accel);

        // Parametrics: acceleration time and drive power derived from value properties.
        var accelCb = m.Create(structure, ElementKind.ConstraintBlock, "AccelerationEquation");
        m.Set(accelCb, FieldKeys.Body, "time = (100 / 3.6) * mass / force");
        var pTime = m.Create(accelCb, ElementKind.Property, "time"); m.Set(pTime, FieldKeys.Type, "Real");
        var pMass = m.Create(accelCb, ElementKind.Property, "mass"); m.Set(pMass, FieldKeys.Type, "Real");
        var pForce = m.Create(accelCb, ElementKind.Property, "force"); m.Set(pForce, FieldKeys.Type, "Real");
        var powerCb = m.Create(structure, ElementKind.ConstraintBlock, "PowerEquation");
        m.Set(powerCb, FieldKeys.Body, "power = force * speed / 1000");
        var qPower = m.Create(powerCb, ElementKind.Property, "power"); m.Set(qPower, FieldKeys.Type, "Real");
        var qForce = m.Create(powerCb, ElementKind.Property, "force"); m.Set(qForce, FieldKeys.Type, "Real");
        var qSpeed = m.Create(powerCb, ElementKind.Property, "speed"); m.Set(qSpeed, FieldKeys.Type, "Real");
        var driveForce = m.Create(vehicle, ElementKind.Property, "driveForce"); m.Set(driveForce, FieldKeys.Type, "Real"); m.Set(driveForce, FieldKeys.DefaultValue, "5600");
        var topSpeed = m.Create(vehicle, ElementKind.Property, "topSpeed"); m.Set(topSpeed, FieldKeys.Type, "Real"); m.Set(topSpeed, FieldKeys.DefaultValue, "50");
        var accelTime = m.Create(vehicle, ElementKind.Property, "accelTime"); m.Set(accelTime, FieldKeys.Type, "Real");
        var requiredPower = m.Create(vehicle, ElementKind.Property, "requiredPower"); m.Set(requiredPower, FieldKeys.Type, "Real");
        var accelUsage = m.Create(vehicle, ElementKind.Property, "accel"); m.Set(accelUsage, FieldKeys.Type, accelCb.Id);
        var powerUsage = m.Create(vehicle, ElementKind.Property, "power"); m.Set(powerUsage, FieldKeys.Type, powerCb.Id);
        Relationship Bind(Element value, Element param, Element usage)
        {
            var b = m.Connect(ElementKind.Binding, value, param, vehicle);
            m.Set(b, FieldKeys.TargetPart, usage.Id);
            return b;
        }
        var bindings = new[]
        {
            Bind(accelTime, pTime, accelUsage), Bind(mass, pMass, accelUsage), Bind(driveForce, pForce, accelUsage),
            Bind(requiredPower, qPower, powerUsage), Bind(driveForce, qForce, powerUsage), Bind(topSpeed, qSpeed, powerUsage),
        };
        m.Connect(ElementKind.Satisfy, accelUsage, accel);

        // IBD connector
        var ibdConnPartA = m.Create(vehicle, ElementKind.Port, "fuelFiller");
        m.Set(ibdConnPartA, FieldKeys.Type, fuelIf.Id);
        m.Connect(ElementKind.Connector, tnk, eng);

        // Use cases
        var drive = m.Create(behavior, ElementKind.UseCase, "Drive Vehicle");
        var refuel = m.Create(behavior, ElementKind.UseCase, "Refuel");
        var startUc = m.Create(behavior, ElementKind.UseCase, "Start Engine");
        m.Connect(ElementKind.Association, driver, drive);
        m.Connect(ElementKind.Association, driver, refuel);
        m.Connect(ElementKind.Include, drive, startUc);

        // Activity
        var act = m.Create(behavior, ElementKind.Activity, "Start Vehicle");
        var i0 = m.Create(act, ElementKind.InitialNode);
        var a1 = m.Create(act, ElementKind.Action, "Insert Key");
        var a2 = m.Create(act, ElementKind.Action, "Crank Engine");
        m.Set(a2, FieldKeys.Body, "attempts = attempts + 1; started = attempts >= 2");
        var dec = m.Create(act, ElementKind.DecisionNode);
        var a3 = m.Create(act, ElementKind.Action, "Idle");
        m.Set(a3, FieldKeys.Body, "rpm = 800");
        var f0 = m.Create(act, ElementKind.ActivityFinalNode);
        m.Connect(ElementKind.ControlFlow, i0, a1);
        m.Connect(ElementKind.ControlFlow, a1, a2);
        m.Connect(ElementKind.ControlFlow, a2, dec);
        var ok = m.Connect(ElementKind.ControlFlow, dec, a3);
        m.Set(ok, FieldKeys.Guard, "started");
        var retry = m.Connect(ElementKind.ControlFlow, dec, a2);
        m.Set(retry, FieldKeys.Guard, "else");
        m.Connect(ElementKind.ControlFlow, a3, f0);

        // State machine
        var sm = m.Create(engine, ElementKind.StateMachine, "Engine States");
        var si = m.Create(sm, ElementKind.InitialState);
        var off = m.Create(sm, ElementKind.State, "Off");
        var idle = m.Create(sm, ElementKind.State, "Idling");
        m.Set(idle, FieldKeys.Entry, "rpm = 800");
        var throttle = m.Create(engine, ElementKind.Property, "throttle"); m.Set(throttle, FieldKeys.Type, "Real"); m.Set(throttle, FieldKeys.DefaultValue, "0");
        var run = m.Create(sm, ElementKind.State, "Running");
        m.Set(run, FieldKeys.Entry, "rpm = 800 + throttle * 5200");
        m.Connect(ElementKind.Transition, si, off);
        var t1 = m.Connect(ElementKind.Transition, off, idle); m.Set(t1, FieldKeys.Name, "ignition");
        var t2 = m.Connect(ElementKind.Transition, idle, run); m.Set(t2, FieldKeys.Guard, "throttle > 0");
        var t3 = m.Connect(ElementKind.Transition, run, idle); m.Set(t3, FieldKeys.Guard, "throttle == 0");
        var t4 = m.Connect(ElementKind.Transition, idle, off); m.Set(t4, FieldKeys.Name, "keyOff");

        // Interaction (sequence)
        var seq = m.Create(behavior, ElementKind.Interaction, "Start Engine Sequence");
        var llDriver = m.Create(seq, ElementKind.Lifeline, "driver");
        m.Set(llDriver, FieldKeys.Type, driver.Id);
        var llVehicle = m.Create(seq, ElementKind.Lifeline, "vehicle");
        m.Set(llVehicle, FieldKeys.Type, vehicle.Id);
        var llEngine = m.Create(seq, ElementKind.Lifeline, "engine");
        m.Set(llEngine, FieldKeys.Type, engine.Id);
        var msg1 = m.Connect(ElementKind.Message, llDriver, llVehicle); m.Set(msg1, FieldKeys.Name, "pressStart");
        var msg2 = m.Connect(ElementKind.Message, llVehicle, llEngine); m.Set(msg2, FieldKeys.Name, "start"); m.Set(msg2, FieldKeys.Signature, "() : Boolean");
        var msg3 = m.Connect(ElementKind.Message, llEngine, llEngine); m.Set(msg3, FieldKeys.Name, "selfTest");
        var msg4 = m.Connect(ElementKind.Message, llEngine, llVehicle); m.Set(msg4, FieldKeys.Name, "started"); m.Set(msg4, FieldKeys.MessageSort, "reply");
        var msg5 = m.Connect(ElementKind.Message, llVehicle, llDriver); m.Set(msg5, FieldKeys.Name, "showReady"); m.Set(msg5, FieldKeys.MessageSort, "asynchCall");

        var optFragment = m.Create(seq, ElementKind.CombinedFragment);
        m.Set(optFragment, FieldKeys.Operator, "opt");
        m.Set(optFragment, FieldKeys.Guard, "engine cold");

        // Verification
        var tc = m.Create(verification, ElementKind.TestCase, "Acceleration Test");
        m.Connect(ElementKind.Verify, tc, accel);
        var tc2 = m.Create(verification, ElementKind.TestCase, "Brake Test");
        m.Connect(ElementKind.Verify, tc2, safety);

        // Diagrams
        var req = m.CreateDiagram(reqPkg, DiagramType.Requirement, "Vehicle Requirements");
        Place(m, req, perf, 40, 40); Place(m, req, accel, 40, 230); Place(m, req, range, 300, 230);
        Place(m, req, safety, 560, 40); Place(m, req, engineReq, 40, 420); Place(m, req, vehicle, 330, 430, 160, 70);
        Place(m, req, tank, 600, 430, 160, 70); Place(m, req, brakes, 820, 230, 160, 70);

        var bdd = m.CreateDiagram(structure, DiagramType.BlockDefinition, "Vehicle Structure");
        Place(m, bdd, vehicle, 300, 30, 230, 150);
        Place(m, bdd, engine, 40, 300, 230, 170);
        Place(m, bdd, tank, 320, 300, 200, 150);
        Place(m, bdd, brakes, 570, 300, 180, 80);
        Place(m, bdd, fuelIf, 40, 540, 180, 70);
        Place(m, bdd, real, 600, 60, 140, 70);

        var ibd = m.CreateDiagram(vehicle, DiagramType.InternalBlock, "Vehicle Internal");
        Place(m, ibd, eng, 60, 60, 170, 70);
        Place(m, ibd, tnk, 380, 60, 170, 70);
        Place(m, ibd, brk, 220, 220, 170, 70);

        var uc = m.CreateDiagram(behavior, DiagramType.UseCase, "Driver Use Cases");
        Place(m, uc, driver, 40, 120); Place(m, uc, drive, 220, 60); Place(m, uc, refuel, 220, 200); Place(m, uc, startUc, 480, 60);

        var ad = m.CreateDiagram(act, DiagramType.Activity, "Start Vehicle");
        Place(m, ad, i0, 126, 20); Place(m, ad, a1, 75, 90); Place(m, ad, a2, 75, 190); Place(m, ad, dec, 120, 290);
        Place(m, ad, a3, 75, 380); Place(m, ad, f0, 126, 480);

        var stm = m.CreateDiagram(sm, DiagramType.StateMachine, "Engine States");
        Place(m, stm, si, 40, 76); Place(m, stm, off, 130, 60); Place(m, stm, idle, 360, 60); Place(m, stm, run, 360, 230);

        var par = m.CreateDiagram(vehicle, DiagramType.Parametric, "Vehicle Performance");
        var nMass = m.AddNode(par, mass, new Bounds(40, 60, 170, 50), showRelationships: false);
        var nForce = m.AddNode(par, driveForce, new Bounds(40, 180, 170, 50), showRelationships: false);
        var nSpeed = m.AddNode(par, topSpeed, new Bounds(40, 300, 170, 50), showRelationships: false);
        var nTime = m.AddNode(par, accelTime, new Bounds(600, 90, 170, 50), showRelationships: false);
        var nPow = m.AddNode(par, requiredPower, new Bounds(600, 270, 170, 50), showRelationships: false);
        var nAccel = m.AddNode(par, accelUsage, new Bounds(300, 60, 220, 110), showRelationships: false);
        var nPowU = m.AddNode(par, powerUsage, new Bounds(300, 230, 220, 110), showRelationships: false);
        m.AddNode(par, pMass, new Bounds(292, 82, 16, 16), false, nAccel);
        m.AddNode(par, pForce, new Bounds(292, 132, 16, 16), false, nAccel);
        m.AddNode(par, pTime, new Bounds(512, 107, 16, 16), false, nAccel);
        m.AddNode(par, qForce, new Bounds(292, 252, 16, 16), false, nPowU);
        m.AddNode(par, qSpeed, new Bounds(292, 302, 16, 16), false, nPowU);
        m.AddNode(par, qPower, new Bounds(512, 287, 16, 16), false, nPowU);
        foreach (var b in bindings) m.AddEdge(par, b);

        var sd = m.CreateDiagram(seq, DiagramType.Sequence, "Start Engine");
        Place(m, sd, llDriver, 40, 20); Place(m, sd, llVehicle, 280, 20); Place(m, sd, llEngine, 520, 20);
        Place(m, sd, optFragment, 470, 164, 260, 80);
        var y = 100.0;
        foreach (var msg in new[] { msg1, msg2, msg3, msg4, msg5 })
        {
            var edge = m.AddEdge(sd, (Relationship)msg)!;
            m.SetWaypoints(sd, edge, [new Pt(0, y)]);
            y += ReferenceEquals(msg.Source, msg.Target) ? 64 : 48;
        }

        m.History.Clear();
        return m;
    }

    private static Element Req(ModelRepository m, Element owner, string id, string name, string text)
    {
        var r = m.Create(owner, ElementKind.Requirement, name);
        m.Set(r, FieldKeys.RequirementId, id);
        m.Set(r, FieldKeys.Text, text);
        return r;
    }

    private static void Place(ModelRepository m, Diagram d, Element e, double x, double y, double w = 0, double h = 0)
    {
        var (dw, dh) = Language.Notation.DefaultSizeOf(e);
        m.AddNode(d, e, new Bounds(x, y, w > 0 ? w : dw, h > 0 ? h : dh));
    }
}
