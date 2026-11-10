using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Samples;

/// <summary>Size knobs for <see cref="LargeModelGenerator"/>. <see cref="Scale"/> multiplies every count.</summary>
public sealed record LargeModelOptions
{
    public int Seed { get; init; } = 20251109;
    public double Scale { get; init; } = 1.0;
    public int Requirements { get; init; } = 5_000;
    public int Subsystems { get; init; } = 20;
    public int BlocksPerSubsystem { get; init; } = 80;
    public int InterfaceBlocks { get; init; } = 120;
    public int TestCases { get; init; } = 2_500;
    public int Activities { get; init; } = 600;
    public int StateMachines { get; init; } = 250;
    public int Diagrams { get; init; } = 300;

    internal int N(int count) => Math.Max(1, (int)Math.Round(count * Scale));
}

/// <summary>
/// Builds a realistic, deterministic synthetic model for scale testing: nested packages, requirements with
/// derive / trace / satisfy / verify links (some of them suspect), blocks with value properties, parts, ports and
/// operations, activities, state machines, diagrams with shapes and paths, a profile with stereotypes and tagged
/// values, and a few query-based validation rules. With the default options it has about 60,000 elements.
/// The same options always produce the same model (ids included).
/// </summary>
public static class LargeModelGenerator
{
    public static ModelRepository Generate(LargeModelOptions? options = null)
    {
        var o = options ?? new LargeModelOptions();
        var g = new Builder(o.Seed);
        var root = new Element(ElementKind.Model, g.Id());
        root.SetRaw(FieldKeys.Name, "Large System");
        root.SetRaw(FieldKeys.Documentation, "Synthetic model generated for performance testing.");

        // ---------------------------------------------------------------- profile
        var profile = g.Add(root, ElementKind.Profile, "Program Profile");
        var stereotypes = new[] { ("Critical", "Requirement, Block"), ("Safety", "Requirement"), ("COTS", "Block"), ("Software", "Block"), ("Hardware", "Block"), ("Automated", "Activity, TestCase") };
        foreach (var (name, meta) in stereotypes)
        {
            var s = g.Add(profile, ElementKind.Stereotype, name);
            s.SetRaw(FieldKeys.Metaclass, meta);
            s.SetRaw(FieldKeys.Color, "#" + g.Rng.Next(0x808080, 0xffffff).ToString("x6"));
            var t = g.Add(s, ElementKind.TagDefinition, "level");
            t.SetRaw(FieldKeys.TagType, "Integer");
            var t2 = g.Add(s, ElementKind.TagDefinition, "owner");
            _ = t2;
        }

        // ---------------------------------------------------------------- value types
        var types = g.Add(root, ElementKind.Package, "Types");
        var valueTypes = new List<Element>();
        foreach (var vt in new[] { "Mass", "Power", "Length", "Time", "Voltage", "Current", "Temperature", "Pressure", "Speed", "Force", "Energy", "Frequency" })
        {
            var v = g.Add(types, ElementKind.ValueType, vt);
            v.SetRaw(FieldKeys.Documentation, $"{vt} quantity");
            valueTypes.Add(v);
        }

        // ---------------------------------------------------------------- requirements
        var reqRoot = g.Add(root, ElementKind.Package, "Requirements");
        var requirements = new List<Element>();
        var reqCount = o.N(o.Requirements);
        var reqPkgs = new List<Element>();
        for (var a = 0; a < 10; a++)
        {
            var area = g.Add(reqRoot, ElementKind.Package, $"Area {a + 1:00}");
            for (var b = 0; b < 5; b++) reqPkgs.Add(g.Add(area, ElementKind.Package, $"Area {a + 1:00}.{b + 1}"));
        }
        Element? parentReq = null;
        for (var i = 0; i < reqCount; i++)
        {
            var pkg = reqPkgs[i * reqPkgs.Count / reqCount];
            // Every 8th requirement is a parent; the next three are contained in it.
            var owner = i % 8 is 1 or 2 or 3 && parentReq != null && parentReq.Owner == pkg ? parentReq : pkg;
            var r = g.Add(owner, ElementKind.Requirement, $"{Words.Pick(g.Rng, Words.Subjects)} {Words.Pick(g.Rng, Words.Qualities)} {i + 1}");
            r.SetRaw(FieldKeys.RequirementId, RequirementIds.Format(ReqFields.DefaultIdPrefix, i + 1));
            r.SetRaw(FieldKeys.Text, $"The {Words.Pick(g.Rng, Words.Subjects).ToLowerInvariant()} shall {Words.Pick(g.Rng, Words.Verbs)} within {g.Rng.Next(1, 500)} {Words.Pick(g.Rng, Words.Units)} under {Words.Pick(g.Rng, Words.Conditions)}.");
            r.SetRaw(ReqFields.Priority, ReqFields.Priorities[g.Rng.Next(ReqFields.Priorities.Length)]);
            r.SetRaw(ReqFields.Status, ReqFields.Statuses[g.Rng.Next(ReqFields.Statuses.Length)]);
            r.SetRaw(ReqFields.VerificationMethod, ReqFields.VerificationMethods[g.Rng.Next(ReqFields.VerificationMethods.Length)]);
            if (g.Rng.Next(4) == 0) r.SetRaw(ReqFields.Risk, ReqFields.Risks[g.Rng.Next(ReqFields.Risks.Length)]);
            if (g.Rng.Next(6) == 0)
            {
                r.SetRaw(FieldKeys.Stereotypes, g.Rng.Next(2) == 0 ? "Critical" : "Safety, Critical");
                r.SetRaw(FieldKeys.TagPrefix + "level", g.Rng.Next(1, 5).ToString());
            }
            if (i % 8 == 0) parentReq = r;
            requirements.Add(r);
        }
        // derive (child-level refinement between areas) and trace
        for (var i = 0; i < reqCount * 3 / 10; i++)
        {
            var s = requirements[g.Rng.Next(reqCount)];
            var t = requirements[g.Rng.Next(reqCount)];
            if (!ReferenceEquals(s, t)) g.Connect(root, ElementKind.DeriveReqt, s, t);
        }
        for (var i = 0; i < reqCount / 10; i++)
        {
            var s = requirements[g.Rng.Next(reqCount)];
            var t = requirements[g.Rng.Next(reqCount)];
            if (!ReferenceEquals(s, t)) g.Connect(root, ElementKind.Trace, s, t);
        }

        // ---------------------------------------------------------------- structure
        var structure = g.Add(root, ElementKind.Package, "Structure");
        var ifPkg = g.Add(structure, ElementKind.Package, "Interfaces");
        var interfaces = new List<Element>();
        for (var i = 0; i < o.N(o.InterfaceBlocks); i++)
        {
            var ib = g.Add(ifPkg, ElementKind.InterfaceBlock, $"I{Words.Pick(g.Rng, Words.Signals)}{i + 1}");
            for (var p = 0; p < 2; p++)
            {
                var f = g.Add(ib, ElementKind.Property, $"item{p + 1}");
                f.SetRaw(FieldKeys.Type, valueTypes[g.Rng.Next(valueTypes.Count)].Id);
                f.SetRaw(FieldKeys.Multiplicity, "1");
            }
            interfaces.Add(ib);
        }
        var blocks = new List<Element>();
        var blockPkgs = new List<Element>();
        var subsystems = o.N(o.Subsystems);
        var perSub = Math.Max(4, o.BlocksPerSubsystem);
        for (var s = 0; s < subsystems; s++)
        {
            var sub = g.Add(structure, ElementKind.Package, $"{Words.Subsystems[s % Words.Subsystems.Length]}{(s >= Words.Subsystems.Length ? " " + (s / Words.Subsystems.Length + 1) : "")}");
            for (var k = 0; k < 4; k++)
            {
                var pkg = g.Add(sub, ElementKind.Package, $"{sub.Name} {Words.Layers[k]}");
                blockPkgs.Add(pkg);
                for (var b = 0; b < perSub / 4; b++)
                {
                    var blk = g.Add(pkg, ElementKind.Block, $"{Words.Pick(g.Rng, Words.Components)} {s + 1}.{k + 1}.{b + 1}");
                    blk.SetRaw(FieldKeys.Documentation, $"{blk.Name} of the {sub.Name} subsystem.");
                    if (g.Rng.Next(5) == 0)
                    {
                        blk.SetRaw(FieldKeys.Stereotypes, Words.Pick(g.Rng, ["COTS", "Software", "Hardware", "Critical"]));
                        blk.SetRaw(FieldKeys.TagPrefix + "owner", Words.Pick(g.Rng, Words.Teams));
                    }
                    for (var v = 0; v < 6; v++)
                    {
                        var vp = g.Add(blk, ElementKind.Property, Words.ValueNames[v]);
                        vp.SetRaw(FieldKeys.Type, v % 2 == 0 ? "Real" : valueTypes[g.Rng.Next(valueTypes.Count)].Id);
                        vp.SetRaw(FieldKeys.Multiplicity, "1");
                        vp.SetRaw(FieldKeys.DefaultValue, (g.Rng.NextDouble() * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    for (var p = 0; p < 2; p++)
                    {
                        var port = g.Add(blk, ElementKind.Port, p == 0 ? "in" : "out");
                        port.SetRaw(FieldKeys.Type, interfaces[g.Rng.Next(interfaces.Count)].Id);
                        port.SetRaw(FieldKeys.Direction, p == 0 ? "in" : "out");
                        port.SetRaw(FieldKeys.Multiplicity, "1");
                    }
                    var op = g.Add(blk, ElementKind.Operation, "reset");
                    op.SetRaw(FieldKeys.Signature, "() : Boolean");
                    blocks.Add(blk);
                }
            }
        }
        // Parts (typed by blocks defined later, so there are no containment cycles) with composition links.
        for (var i = 0; i < blocks.Count; i++)
        {
            for (var p = 0; p < 3 && i + 1 < blocks.Count; p++)
            {
                var partType = blocks[g.Rng.Next(i + 1, blocks.Count)];
                var part = g.Add(blocks[i], ElementKind.Property, $"part{p + 1}");
                part.SetRaw(FieldKeys.Type, partType.Id);
                part.SetRaw(FieldKeys.Multiplicity, "1");
                if (p < 2)
                {
                    var c = g.Connect(root, ElementKind.Composition, blocks[i], partType);
                    c.SetRaw(FieldKeys.Multiplicity, "1");
                }
            }
            if (i > 0 && g.Rng.Next(10) == 0) g.Connect(root, ElementKind.Generalization, blocks[i], blocks[g.Rng.Next(0, i)]);
        }
        // satisfy: every leaf requirement except ~5 % is satisfied by one or two blocks (some via part properties).
        foreach (var r in requirements)
        {
            if (g.Rng.Next(20) == 0) continue;
            var n = g.Rng.Next(1, 3);
            for (var k = 0; k < n; k++) g.Connect(root, ElementKind.Satisfy, blocks[g.Rng.Next(blocks.Count)], r);
        }

        // ---------------------------------------------------------------- verification
        var verification = g.Add(root, ElementKind.Package, "Verification");
        var testPkgs = Enumerable.Range(0, 10).Select(i => g.Add(verification, ElementKind.Package, $"Test Campaign {i + 1}")).ToList();
        var testCount = o.N(o.TestCases);
        for (var i = 0; i < testCount; i++)
        {
            var tc = g.Add(testPkgs[i * testPkgs.Count / testCount], ElementKind.TestCase, $"TC {i + 1:0000} {Words.Pick(g.Rng, Words.Qualities)}");
            tc.SetRaw(ReqFields.Verdict, ReqFields.Verdicts[g.Rng.Next(ReqFields.Verdicts.Length)]);
            if (g.Rng.Next(4) == 0) tc.SetRaw(FieldKeys.Stereotypes, "Automated");
            var n = g.Rng.Next(1, 3);
            for (var k = 0; k < n; k++) g.Connect(root, ElementKind.Verify, tc, requirements[g.Rng.Next(reqCount)]);
        }

        // ---------------------------------------------------------------- behaviour
        var behavior = g.Add(root, ElementKind.Package, "Behavior");
        var actPkgs = Enumerable.Range(0, 10).Select(i => g.Add(behavior, ElementKind.Package, $"Functions {i + 1}")).ToList();
        var activities = new List<Element>();
        var actCount = o.N(o.Activities);
        for (var i = 0; i < actCount; i++)
        {
            var act = g.Add(actPkgs[i * actPkgs.Count / actCount], ElementKind.Activity, $"{Words.Pick(g.Rng, Words.Verbs)} {Words.Pick(g.Rng, Words.Signals)} {i + 1}");
            var init = g.Add(act, ElementKind.InitialNode, null);
            var prev = init;
            var actions = new List<Element>();
            for (var k = 0; k < 6; k++)
            {
                var a = g.Add(act, ElementKind.Action, $"Step {k + 1}");
                if (g.Rng.Next(3) == 0) a.SetRaw(FieldKeys.Body, $"x = x + {k}");
                g.Connect(root, ElementKind.ControlFlow, prev, a);
                actions.Add(a);
                prev = a;
            }
            var dec = g.Add(act, ElementKind.DecisionNode, null);
            var fin = g.Add(act, ElementKind.ActivityFinalNode, null);
            g.Connect(root, ElementKind.ControlFlow, prev, dec);
            var ok = g.Connect(root, ElementKind.ControlFlow, dec, fin);
            ok.SetRaw(FieldKeys.Guard, "done");
            var again = g.Connect(root, ElementKind.ControlFlow, dec, actions[0]);
            again.SetRaw(FieldKeys.Guard, "else");
            if (g.Rng.Next(3) == 0) g.Connect(root, ElementKind.Allocate, act, blocks[g.Rng.Next(blocks.Count)]);
            activities.Add(act);
        }
        var stateMachines = new List<Element>();
        for (var i = 0; i < o.N(o.StateMachines); i++)
        {
            var host = blocks[(i * 7) % blocks.Count];
            var sm = g.Add(host, ElementKind.StateMachine, $"{host.Name} Modes");
            var init = g.Add(sm, ElementKind.InitialState, null);
            var states = Enumerable.Range(0, 5).Select(k => g.Add(sm, ElementKind.State, Words.States[k])).ToList();
            states[1].SetRaw(FieldKeys.Entry, "power = 1");
            g.Connect(root, ElementKind.Transition, init, states[0]);
            for (var k = 0; k < 5; k++)
            {
                var t = g.Connect(root, ElementKind.Transition, states[k], states[(k + 1) % 5]);
                t.SetRaw(FieldKeys.Name, $"e{k + 1}");
            }
            g.Connect(root, ElementKind.Transition, states[3], states[1]).SetRaw(FieldKeys.Guard, "retry");
            stateMachines.Add(sm);
        }

        // ---------------------------------------------------------------- saved views and query rules
        var views = g.Add(root, ElementKind.Package, "Views");
        var rule1 = g.Add(views, ElementKind.ValidationRule, "USR-REQ");
        rule1.SetRaw(ViewFields.Query, "kind:Requirement where priority = critical and no:in:verify");
        rule1.SetRaw(ViewFields.Severity, "warning");
        rule1.SetRaw(ViewFields.Message, "Critical requirement {name} is not verified");
        var rule2 = g.Add(views, ElementKind.ValidationRule, "USR-DOC");
        rule2.SetRaw(ViewFields.Query, "kind:Block stereotype:Critical where documentation is empty");
        rule2.SetRaw(ViewFields.Severity, "error");
        rule2.SetRaw(ViewFields.Message, "{name} needs documentation");
        var q = g.Add(views, ElementKind.QueryView, "Unsatisfied high priority");
        q.SetRaw(ViewFields.Query, "kind:Requirement where priority = high and no:in:satisfy");
        var tv = g.Add(views, ElementKind.TableView, "All requirements");
        tv.SetRaw(ViewFields.Query, "kind:Requirement");

        // ---------------------------------------------------------------- diagrams
        var diagramCount = o.N(o.Diagrams);
        var made = 0;
        var di = 0;
        while (made < diagramCount)
        {
            switch (di++ % 4)
            {
                case 0:   // BDD of a block package
                {
                    var pkg = blockPkgs[g.Rng.Next(blockPkgs.Count)];
                    var shown = pkg.Children.Where(c => c.Kind == ElementKind.Block).Take(30).ToList();
                    g.Diagram(pkg, DiagramType.BlockDefinition, $"{pkg.Name} BDD {made + 1}", shown);
                    break;
                }
                case 1:   // requirement diagram of a requirement package
                {
                    var pkg = reqPkgs[g.Rng.Next(reqPkgs.Count)];
                    var shown = pkg.Children.Where(c => c.Kind == ElementKind.Requirement).Take(25).ToList();
                    shown.AddRange(shown.SelectMany(r => g.IncidentOf(r)).Where(x => x.Kind == ElementKind.Satisfy).Select(x => x.Source).Distinct().Take(10).ToList());
                    g.Diagram(pkg, DiagramType.Requirement, $"{pkg.Name} Requirements {made + 1}", shown);
                    break;
                }
                case 2:   // activity diagram
                {
                    var act = activities[g.Rng.Next(activities.Count)];
                    if (act.Children.Any(c => c is Diagram)) { g.Diagram(actPkgs[0], DiagramType.Package, $"Overview {made + 1}", actPkgs[0].Children.Where(c => c.Kind == ElementKind.Activity).Take(20).ToList()); break; }
                    g.Diagram(act, DiagramType.Activity, $"{act.Name} AD", act.Children.Where(c => c is not Relationship).ToList());
                    break;
                }
                default:  // state machine diagram
                {
                    var sm = stateMachines[g.Rng.Next(stateMachines.Count)];
                    if (sm.Children.Any(c => c is Diagram)) { g.Diagram(testPkgs[made % testPkgs.Count], DiagramType.Requirement, $"Verification {made + 1}", testPkgs[made % testPkgs.Count].Children.Take(20).ToList()); break; }
                    g.Diagram(sm, DiagramType.StateMachine, $"{sm.Name} SMD", sm.Children.Where(c => c is not Relationship).ToList());
                    break;
                }
            }
            made++;
        }

        var model = new ModelRepository(root);

        // A handful of requirements changed after their links were reviewed: their trace links are suspect.
        foreach (var r in requirements.Where((_, i) => i % 97 == 0))
        {
            var fp = SuspectLinks.Fingerprint(r);
            foreach (var rel in model.RelationshipsOf(r))
            {
                if (!SuspectLinks.IsTraceKind(rel.Kind)) continue;
                rel.SetRaw(ReferenceEquals(rel.Source, r) ? ReqFields.SourceFingerprint : ReqFields.TargetFingerprint, fp);
            }
            r.SetRaw(FieldKeys.Text, r.Get(FieldKeys.Text) + " (revised)");
        }
        return model;
    }

    private sealed class Builder(int seed)
    {
        public Random Rng { get; } = new(seed);
        private readonly Dictionary<Element, List<Relationship>> _incident = new(ReferenceEqualityComparer.Instance);

        /// <summary>Deterministic ids in the same shape as <see cref="Element.NewId"/>.</summary>
        public string Id()
        {
            Span<byte> b = stackalloc byte[16];
            Rng.NextBytes(b);
            return "_" + Convert.ToHexString(b).ToLowerInvariant();
        }

        public Element Add(Element owner, ElementKind kind, string? name)
        {
            var e = new Element(kind, Id());
            if (name != null) e.SetRaw(FieldKeys.Name, name);
            Attach(owner, e);
            return e;
        }

        private static void Attach(Element owner, Element e)
        {
            if (!Metamodel.CanOwn(owner.Kind, e.Kind)) throw new InvalidOperationException($"{owner.Kind} cannot own {e.Kind}");
            e.Owner = owner;
            owner.Children.Add(e);
        }

        public IReadOnlyList<Relationship> IncidentOf(Element e) => _incident.TryGetValue(e, out var l) ? l : [];

        /// <summary>Same ownership rules as <see cref="ModelRepository.Connect"/>.</summary>
        public Relationship Connect(Element root, ElementKind kind, Element source, Element target)
        {
            var r = new Relationship(kind, source, target, Id());
            Element owner;
            if (kind is ElementKind.Transition or ElementKind.ControlFlow or ElementKind.ObjectFlow or ElementKind.Connector or ElementKind.Message)
                owner = source.Owner ?? root;
            else
                owner = (source.Owner ?? root).AncestorsAndSelf().FirstOrDefault(a => Metamodel.CanOwn(a.Kind, kind)) ?? root;
            Attach(owner, r);
            Track(source, r);
            if (!ReferenceEquals(source, target)) Track(target, r);
            return r;
        }

        private void Track(Element e, Relationship r)
        {
            if (!_incident.TryGetValue(e, out var l)) _incident[e] = l = new List<Relationship>();
            l.Add(r);
        }

        /// <summary>A diagram owned by <paramref name="owner"/> showing <paramref name="shown"/> on a grid, with every relationship between them.</summary>
        public void Diagram(Element owner, DiagramType type, string name, IReadOnlyList<Element> shown)
        {
            var d = new Diagram(type, Id());
            d.SetRaw(FieldKeys.Name, name);
            Attach(owner, d);
            var nodes = new Dictionary<Element, DiagramNode>(ReferenceEqualityComparer.Instance);
            var cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(shown.Count)));
            foreach (var e in shown)
            {
                if (nodes.ContainsKey(e)) continue;
                var i = nodes.Count;
                var n = new DiagramNode(e, new Bounds(40 + i % cols * 240, 40 + i / cols * 170, 180, 110), Id());
                d.NodeList.Add(n);
                nodes[e] = n;
            }
            var seen = new HashSet<Relationship>(ReferenceEqualityComparer.Instance);
            foreach (var (e, n) in nodes)
                foreach (var r in IncidentOf(e))
                    if (seen.Add(r) && nodes.TryGetValue(r.Source, out var s) && nodes.TryGetValue(r.Target, out var t))
                        d.EdgeList.Add(new DiagramEdge(r, s, t, Id()));
        }
    }

    private static class Words
    {
        public static T Pick<T>(Random rng, T[] items) => items[rng.Next(items.Length)];
        public static readonly string[] Subjects = ["Vehicle", "Controller", "Sensor", "Actuator", "Battery", "Network", "Display", "Operator", "Gateway", "Antenna", "Pump", "Valve"];
        public static readonly string[] Qualities = ["Latency", "Availability", "Accuracy", "Throughput", "Mass", "Power", "Safety", "Security", "Reliability", "Range", "Startup", "Recovery"];
        public static readonly string[] Verbs = ["respond", "acquire", "transmit", "store", "regulate", "monitor", "calibrate", "isolate", "report", "recover"];
        public static readonly string[] Units = ["ms", "s", "m", "kg", "W", "V", "%"];
        public static readonly string[] Conditions = ["nominal load", "peak load", "degraded mode", "cold start", "low battery", "maintenance mode"];
        public static readonly string[] Signals = ["Telemetry", "Command", "Power", "Data", "Status", "Fuel", "Coolant", "Video", "Clock"];
        public static readonly string[] Subsystems = ["Propulsion", "Avionics", "Power", "Thermal", "Communications", "Payload", "Structures", "Guidance", "Navigation", "Ground Segment", "Software", "Sensors"];
        public static readonly string[] Layers = ["Logical", "Physical", "Interfaces", "Support"];
        public static readonly string[] Components = ["Controller", "Unit", "Module", "Assembly", "Processor", "Harness", "Converter", "Bus", "Monitor", "Driver"];
        public static readonly string[] ValueNames = ["mass", "power", "length", "mtbf", "voltage", "temperature"];
        public static readonly string[] Teams = ["alpha", "bravo", "charlie", "delta"];
        public static readonly string[] States = ["Off", "Standby", "Operating", "Fault", "Maintenance"];
    }
}
