using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Samples;

/// <summary>A starter project offered by the "New project" gallery.</summary>
/// <param name="Build">Builds the model; the argument is the project name chosen by the user.</param>
public sealed record ProjectTemplate(string Id, string Name, string Description, string Category, Func<string, ModelRepository> Build);

/// <summary>A starter diagram offered by "New diagram from template".</summary>
/// <param name="Create">Creates the diagram (and the elements it shows) in the given package and returns it.</param>
public sealed record DiagramTemplate(string Id, string Name, string Description, DiagramType Type, Func<ModelRepository, Element, Diagram> Create);

/// <summary>
/// Starter templates for new projects and new diagrams, defined in code so they always match the metamodel.
/// Every template builds plain model content (elements, relationships, diagrams), so it renders, exports and
/// replicates like anything a user draws.
/// </summary>
public static class Templates
{
    public static readonly IReadOnlyList<ProjectTemplate> Projects =
    [
        new("blank", "Blank project", "Empty Structure, Requirements and Behavior packages with one block definition diagram.", "Basic", Blank),
        new("sysml", "SysML system", "System skeleton: stakeholder requirements, a system block with subsystems (BDD + IBD), use cases and satisfy links.", "SysML", SysmlSystem),
        new("vehicle", "Vehicle sample", "The complete worked example: requirements, structure, behavior, parametrics, verification.", "SysML", _ => SampleModels.Vehicle()),
        new("bpmn", "BPMN process", "An order-handling process with events, tasks, a gateway and sequence flows.", "Business", name => Single(name, "Processes", "process")),
        new("archimate", "ArchiMate layered view", "Business, application and technology layers wired with serving and realization relations.", "Enterprise", name => Single(name, "Architecture", "archimate-layered")),
        new("database", "Database ER", "Customer, order, order line and product tables with keys and foreign keys.", "Data", name => Single(name, "Data model", "er")),
        new("whiteboard", "Whiteboard / brainstorm", "A free-form board with a central idea, branches and sticky notes.", "Basic", name => Single(name, "Ideas", "mindmap")),
        new("uaf", "UAF skeleton", "UAF profile plus strategic, operational and resource packages with a capability view.", "Enterprise", Uaf),
        new("cubesat", "CubeSat mission", "Aerospace sample: 3U Earth-observation CubeSat with mission requirements, EPS/ADCS/COMMS/OBC/payload, power and data IBD, power and mass budgets, mode state machine and test cases.", "Aerospace", CubeSatSample.Create),
        new("acc", "Adaptive cruise control", "Automotive sample: ISO 26262-style safety goals and safety requirements with ASIL tags, functional architecture with swimlane allocation to ECUs, ECU network IBD and ACC modes.", "Automotive", AdaptiveCruiseSample.Create),
        new("infusion-pump", "Infusion pump", "Medical sample: ISO 14971 hazard analysis table, risk controls, IEC 62304 software items and safety classes, pump state machine and traceability to tests.", "Medical", InfusionPumpSample.Create),
        new("parking-brake", "Electric parking brake (safety)", "Safety & reliability sample: FMEA with RPN and action priority on the controller, actuator, sensors and supply; mitigating requirements and tests; a fault tree with AND, OR and 2-of-3 gates and minimal cut sets.", "Safety", ParkingBrakeSample.Create),
    ];

    public static readonly IReadOnlyList<DiagramTemplate> Diagrams =
    [
        new("flowchart", "Flowchart", "Start, steps, a decision and an end, connected with arrows.", DiagramType.Whiteboard, Flowchart),
        new("mindmap", "Mind map", "A central idea with four branches and sticky notes.", DiagramType.Whiteboard, MindMap),
        new("orgchart", "Org chart", "People as cards in a reporting tree. Import your own from a spreadsheet with Import ▸ Org chart from spreadsheet.", DiagramType.Whiteboard, OrgChartSample),
        new("process", "BPMN process", "Start event, tasks, an exclusive gateway and end events.", DiagramType.Bpmn, Process),
        new("er", "ER diagram", "Customer / order / product tables with foreign keys.", DiagramType.Database, ErDiagram),
        new("req-tree", "Requirement tree", "A parent requirement refined into three children.", DiagramType.Requirement, RequirementTree),
        new("context", "System context", "A system block, its users and external systems.", DiagramType.BlockDefinition, SystemContext),
        new("archimate-layered", "ArchiMate layered view", "Business, application and technology layers.", DiagramType.ArchiMate, ArchiLayered),
        new("timing", "Timing diagram", "Two lifelines changing state over time, with a duration and a time constraint.", DiagramType.Timing, TimingSample),
        new("interaction-overview", "Interaction overview", "A flow of interaction uses and an inline sequence with a decision.", DiagramType.InteractionOverview, InteractionOverviewSample),
        new("communication", "Communication diagram", "Three lifelines linked by numbered messages.", DiagramType.Communication, CommunicationSample),
        .. UafViewpoints.All.Select(v => new DiagramTemplate("uaf-" + v.Code.ToLowerInvariant(), v.Name, UafDescription(v), v.Type, (m, pkg) => UafView(m, pkg, v))),
    ];

    public static ProjectTemplate? FindProject(string id) => Projects.FirstOrDefault(t => t.Id == id);
    public static DiagramTemplate? FindDiagram(string id) => Diagrams.FirstOrDefault(t => t.Id == id);

    /// <summary>Builds a project from a template, names its root and clears the undo history.</summary>
    public static ModelRepository CreateProject(string templateId, string name)
    {
        var t = FindProject(templateId) ?? throw new ArgumentException($"Unknown template '{templateId}'", nameof(templateId));
        var m = t.Build(name);
        if (!string.IsNullOrWhiteSpace(name)) ModelRepository.SetFieldForLoad(m.Root, FieldKeys.Name, name.Trim());
        m.History.Clear();
        return m;
    }

    /// <summary>Creates a diagram from a template inside <paramref name="context"/> (or its nearest package). One undo step.</summary>
    public static Diagram CreateDiagram(ModelRepository m, Element context, string templateId)
    {
        var t = FindDiagram(templateId) ?? throw new ArgumentException($"Unknown template '{templateId}'", nameof(templateId));
        var owner = context.AncestorsAndSelf().FirstOrDefault(a => a.Kind is ElementKind.Package or ElementKind.Model) ?? m.Root;
        using (m.History.Transaction($"New {t.Name}"))
            return t.Create(m, owner);
    }

    // ================================================================ projects

    private static ModelRepository Blank(string name)
    {
        var m = new ModelRepository(name);
        var structure = m.Create(m.Root, ElementKind.Package, "Structure");
        m.Create(m.Root, ElementKind.Package, "Requirements");
        m.Create(m.Root, ElementKind.Package, "Behavior");
        m.CreateDiagram(structure, DiagramType.BlockDefinition, "System Structure");
        return m;
    }

    private static ModelRepository Single(string name, string package, string diagramTemplate)
    {
        var m = new ModelRepository(name);
        var pkg = m.Create(m.Root, ElementKind.Package, package);
        FindDiagram(diagramTemplate)!.Create(m, pkg);
        return m;
    }

    private static ModelRepository SysmlSystem(string name)
    {
        var m = new ModelRepository(name);
        var reqPkg = m.Create(m.Root, ElementKind.Package, "Requirements");
        var structure = m.Create(m.Root, ElementKind.Package, "Structure");
        var behavior = m.Create(m.Root, ElementKind.Package, "Behavior");

        var top = Req(m, reqPkg, "Mission", "The system shall perform its mission in the intended environment.");
        var perf = Req(m, top, "Performance", "The system shall meet its performance targets.");
        var safe = Req(m, top, "Safety", "The system shall fail safe on loss of power.");
        var iface = Req(m, top, "Interfaces", "The system shall exchange data with external systems over a standard interface.");

        var system = m.Create(structure, ElementKind.Block, "System");
        var subA = m.Create(structure, ElementKind.Block, "Subsystem A");
        var subB = m.Create(structure, ElementKind.Block, "Subsystem B");
        var ext = m.Create(structure, ElementKind.InterfaceBlock, "External Interface");
        var operatorActor = m.Create(structure, ElementKind.Actor, "Operator");
        var a = m.Create(system, ElementKind.Property, "a"); m.Set(a, FieldKeys.Type, subA.Id);
        var b = m.Create(system, ElementKind.Property, "b"); m.Set(b, FieldKeys.Type, subB.Id);
        var pa = m.Create(subA, ElementKind.Port, "out"); m.Set(pa, FieldKeys.Type, ext.Id);
        var pb = m.Create(subB, ElementKind.Port, "in"); m.Set(pb, FieldKeys.Type, ext.Id);
        m.Connect(ElementKind.Composition, system, subA);
        m.Connect(ElementKind.Composition, system, subB);
        m.Connect(ElementKind.Satisfy, system, perf);
        m.Connect(ElementKind.Satisfy, subA, safe);
        m.Connect(ElementKind.Satisfy, ext, iface);

        var operate = m.Create(behavior, ElementKind.UseCase, "Operate System");
        var maintain = m.Create(behavior, ElementKind.UseCase, "Maintain System");
        m.Connect(ElementKind.Association, operatorActor, operate);
        m.Connect(ElementKind.Association, operatorActor, maintain);

        var req = m.CreateDiagram(reqPkg, DiagramType.Requirement, "Stakeholder Requirements");
        Place(m, req, top, 300, 30); Place(m, req, perf, 40, 230); Place(m, req, safe, 300, 230); Place(m, req, iface, 560, 230);
        Place(m, req, system, 40, 440, 160, 70); Place(m, req, subA, 300, 440, 160, 70); Place(m, req, ext, 560, 440, 180, 70);

        var bdd = m.CreateDiagram(structure, DiagramType.BlockDefinition, "System Structure");
        Place(m, bdd, system, 260, 30, 220, 130); Place(m, bdd, subA, 80, 260, 200, 110); Place(m, bdd, subB, 460, 260, 200, 110);
        Place(m, bdd, ext, 260, 440, 200, 80); Place(m, bdd, operatorActor, 40, 50);

        var ibd = m.CreateDiagram(system, DiagramType.InternalBlock, "System Internals");
        var na = m.AddNode(ibd, a, new Bounds(60, 70, 170, 80), showRelationships: false);
        var nb = m.AddNode(ibd, b, new Bounds(380, 70, 170, 80), showRelationships: false);
        var (pw, ph) = Metamodel.DefaultSize(ElementKind.Port);
        m.AddNode(ibd, pa, Notation.SnapToBorder(new Bounds(230 - pw / 2, 110 - ph / 2, pw, ph), na.Bounds), false, na);
        m.AddNode(ibd, pb, Notation.SnapToBorder(new Bounds(380 - pw / 2, 110 - ph / 2, pw, ph), nb.Bounds), false, nb);
        var conn = m.Connect(ElementKind.Connector, pa, pb, system);
        m.Set(conn, FieldKeys.SourcePart, a.Id);
        m.Set(conn, FieldKeys.TargetPart, b.Id);
        m.AddEdge(ibd, conn);

        var uc = m.CreateDiagram(behavior, DiagramType.UseCase, "Operational Use Cases");
        Place(m, uc, operatorActor, 40, 120); Place(m, uc, operate, 240, 60); Place(m, uc, maintain, 240, 200);
        return m;
    }

    private static ModelRepository Uaf(string name)
    {
        var m = new ModelRepository(name);
        var profile = Profiles.AddUaf(m, m.Root);
        var strategic = m.Create(m.Root, ElementKind.Package, "Strategic");
        var operational = m.Create(m.Root, ElementKind.Package, "Operational");
        var resources = m.Create(m.Root, ElementKind.Package, "Resources");
        m.Create(m.Root, ElementKind.Package, "Services");

        Element Stereo(Element owner, ElementKind kind, string name, string stereotype)
        {
            var e = m.Create(owner, kind, name);
            if (profile.OwnedElements.Any(s => s.Name == stereotype)) m.Set(e, FieldKeys.Stereotypes, stereotype);
            return e;
        }
        var cap = Stereo(strategic, ElementKind.Block, "Situational Awareness", "Capability");
        var cap1 = Stereo(strategic, ElementKind.Block, "Sensing", "Capability");
        var cap2 = Stereo(strategic, ElementKind.Block, "Information Sharing", "Capability");
        m.Connect(ElementKind.Composition, cap, cap1);
        m.Connect(ElementKind.Composition, cap, cap2);
        var performer = Stereo(operational, ElementKind.Block, "Command Post", "Operational Performer");
        var sensor = Stereo(resources, ElementKind.Block, "Sensor Suite", "Resource Performer");
        m.Connect(ElementKind.Allocate, cap1, sensor);
        m.Connect(ElementKind.Allocate, cap2, performer);

        var view = m.CreateDiagram(strategic, DiagramType.BlockDefinition, "Capability Taxonomy");
        Place(m, view, cap, 240, 30, 220, 80); Place(m, view, cap1, 60, 220, 200, 80); Place(m, view, cap2, 420, 220, 200, 80);
        Place(m, view, sensor, 60, 400, 200, 80); Place(m, view, performer, 420, 400, 200, 80);
        return m;
    }

    // ================================================================ diagrams

    private static Diagram Flowchart(ModelRepository m, Element pkg)
    {
        var d = m.CreateDiagram(pkg, DiagramType.Whiteboard, "Flowchart");
        var start = Shape(m, pkg, d, "ellipse", "Start", 230, 20, 120, 60);
        var step1 = Shape(m, pkg, d, "rounded", "Collect input", 220, 120, 140, 60);
        var check = Shape(m, pkg, d, "diamond", "Valid?", 235, 220, 110, 90);
        var fix = Shape(m, pkg, d, "rounded", "Ask again", 440, 235, 130, 60);
        var step2 = Shape(m, pkg, d, "rounded", "Process", 220, 360, 140, 60);
        var end = Shape(m, pkg, d, "ellipse", "End", 230, 470, 120, 60);
        Line(m, d, start, step1); Line(m, d, step1, check); Line(m, d, check, step2, "yes"); Line(m, d, check, fix, "no");
        Line(m, d, fix, step1); Line(m, d, step2, end);
        return d;
    }

    private static Diagram MindMap(ModelRepository m, Element pkg)
    {
        var d = m.CreateDiagram(pkg, DiagramType.Whiteboard, "Brainstorm");
        var center = Shape(m, pkg, d, "cloud", "Big idea", 300, 200, 180, 110);
        SetFill(m, d, center, "#dae8fc");
        var colors = new[] { "#d5e8d4", "#ffe6cc", "#fff2cc", "#e1d5e7" };
        var spots = new (string Label, double X, double Y)[] { ("Goals", 60, 60), ("Users", 560, 60), ("Risks", 60, 380), ("Next steps", 560, 380) };
        for (var i = 0; i < spots.Length; i++)
        {
            var (label, x, y) = spots[i];
            var branch = Shape(m, pkg, d, "rounded", label, x, y, 150, 60);
            SetFill(m, d, branch, colors[i]);
            Line(m, d, center, branch, sub: "line");
        }
        foreach (var (text, x, y) in new[] { ("Write ideas on sticky notes", 300, 40), ("Hover a shape and drag an arrow to connect", 300, 400) })
        {
            var note = m.Create(pkg, ElementKind.Comment);
            m.Set(note, FieldKeys.Body, text);
            m.AddNode(d, note, new Bounds(x, y, 190, 70));
        }
        return d;
    }

    private static Diagram OrgChartSample(ModelRepository m, Element pkg)
    {
        OrgPerson P(string name, string title, string dept, string? manager = null) => new(name, title, dept, manager);
        var tree = OrgChart.Resolve(
        [
            P("Alex Morgan", "Chief Executive", "Leadership"),
            P("Sam Lee", "Head of Engineering", "Engineering", "Alex Morgan"),
            P("Priya Shah", "Head of Product", "Product", "Alex Morgan"),
            P("Jordan Diaz", "Head of Operations", "Operations", "Alex Morgan"),
            P("Chris Wu", "Systems Engineer", "Engineering", "Sam Lee"),
            P("Taylor Kim", "Software Engineer", "Engineering", "Sam Lee"),
            P("Robin Park", "Product Manager", "Product", "Priya Shah"),
            P("Casey Nolan", "Operations Lead", "Operations", "Jordan Diaz"),
        ]);
        return OrgChart.Build(m, pkg, tree, "Org chart").Diagram;
    }

    private static Diagram Process(ModelRepository m, Element pkg)
    {
        var d = m.CreateDiagram(pkg, DiagramType.Bpmn, "Order Handling");
        Element Node(string sub, string name, double x, double y)
        {
            var e = m.Create(pkg, ElementKind.BpmnNode, name);
            m.Set(e, FieldKeys.Subtype, sub);
            var (w, h) = Notation.DefaultSizeOf(e);
            m.AddNode(d, e, new Bounds(x, y - h / 2, w, h));
            return e;
        }
        var start = Node("startEvent", "Order received", 40, 160);
        var check = Node("userTask", "Check order", 140, 160);
        var gw = Node("exclusiveGateway", "In stock?", 320, 160);
        var ship = Node("serviceTask", "Ship order", 440, 90);
        var back = Node("userTask", "Back-order items", 440, 230);
        var done = Node("endEvent", "Order shipped", 640, 90);
        var later = Node("endEvent", "Customer notified", 640, 230);
        foreach (var (s, t, label) in new[] { (start, check, ""), (check, gw, ""), (gw, ship, "yes"), (gw, back, "no"), (ship, done, ""), (back, later, "") })
        {
            var f = m.Connect(ElementKind.BpmnFlow, s, t, pkg);
            m.Set(f, FieldKeys.Subtype, "sequence");
            if (label.Length > 0) m.Set(f, FieldKeys.Name, label);
            m.AddEdge(d, f);
        }
        return d;
    }

    private static Diagram ErDiagram(ModelRepository m, Element pkg)
    {
        var d = m.CreateDiagram(pkg, DiagramType.Database, "Orders ER");
        Element Table(string name, double x, double y, params (string Col, string Type, bool Pk)[] cols)
        {
            var t = m.Create(pkg, ElementKind.Table, name);
            foreach (var (col, type, pk) in cols)
            {
                var c = m.Create(t, ElementKind.Column, col);
                m.Set(c, FieldKeys.SqlType, type);
                if (pk) m.Set(c, FieldKeys.PrimaryKey, "true");
            }
            var (w, h) = Notation.DefaultSizeOf(t);
            m.AddNode(d, t, new Bounds(x, y, Math.Max(w, 200), Math.Max(h, 40 + cols.Length * 18)), showRelationships: false);
            return t;
        }
        var customer = Table("customer", 40, 40, ("id", "integer", true), ("name", "varchar(120)", false), ("email", "varchar(200)", false));
        var order = Table("order", 340, 40, ("id", "integer", true), ("customer_id", "integer", false), ("placed_at", "timestamp", false));
        var product = Table("product", 40, 300, ("id", "integer", true), ("title", "varchar(200)", false), ("price", "numeric(10,2)", false));
        var line = Table("order_line", 340, 300, ("order_id", "integer", true), ("product_id", "integer", true), ("quantity", "integer", false));
        foreach (var (from, to, col) in new[] { (order, customer, "customer_id"), (line, order, "order_id"), (line, product, "product_id") })
        {
            var fk = m.Connect(ElementKind.ForeignKey, from, to, pkg);
            m.Set(fk, FieldKeys.FkColumns, col);
            m.Set(fk, FieldKeys.RefColumns, "id");
            m.AddEdge(d, fk);
        }
        return d;
    }

    private static Diagram RequirementTree(ModelRepository m, Element pkg)
    {
        var d = m.CreateDiagram(pkg, DiagramType.Requirement, "Requirement Tree");
        var parent = Req(m, pkg, "Top-level requirement", "The system shall …");
        Place(m, d, parent, 300, 30);
        var x = 40.0;
        foreach (var (name, text) in new[] { ("Functional", "The system shall perform …"), ("Performance", "The system shall respond within …"), ("Constraint", "The system shall comply with …") })
        {
            var child = Req(m, pkg, name, text);
            m.Connect(ElementKind.DeriveReqt, child, parent);
            Place(m, d, child, x, 260);
            x += 260;
        }
        return d;
    }

    private static Diagram SystemContext(ModelRepository m, Element pkg)
    {
        var d = m.CreateDiagram(pkg, DiagramType.BlockDefinition, "System Context");
        var context = m.Create(pkg, ElementKind.Block, "Context");
        var system = m.Create(pkg, ElementKind.Block, "System of Interest");
        var ext1 = m.Create(pkg, ElementKind.Block, "External System");
        var env = m.Create(pkg, ElementKind.Block, "Environment");
        var user = m.Create(pkg, ElementKind.Actor, "User");
        m.Connect(ElementKind.Composition, context, system);
        m.Connect(ElementKind.Composition, context, ext1);
        m.Connect(ElementKind.Composition, context, env);
        m.Connect(ElementKind.Association, user, system);
        Place(m, d, context, 260, 20, 200, 80);
        Place(m, d, system, 260, 220, 200, 90);
        Place(m, d, ext1, 520, 220, 180, 80);
        Place(m, d, env, 20, 220, 180, 80);
        Place(m, d, user, 300, 400);
        return d;
    }

    private static Diagram ArchiLayered(ModelRepository m, Element pkg)
    {
        var d = m.CreateDiagram(pkg, DiagramType.ArchiMate, "Layered View");
        Element El(string sub, string name, double x, double y)
        {
            var e = m.Create(pkg, ElementKind.ArchiElement, name);
            m.Set(e, FieldKeys.Subtype, sub);
            m.AddNode(d, e, new Bounds(x, y, 160, 70));
            return e;
        }
        void Rel(string sub, Element s, Element t)
        {
            var r = m.Connect(ElementKind.ArchiRelation, s, t, pkg);
            m.Set(r, FieldKeys.Subtype, sub);
            m.AddEdge(d, r);
        }
        var customer = El("businessActor", "Customer", 40, 20);
        var process = El("businessProcess", "Handle order", 260, 20);
        var bsvc = El("businessService", "Ordering", 480, 20);
        var asvc = El("applicationService", "Order service", 260, 170);
        var app = El("applicationComponent", "Order system", 260, 300);
        var data = El("dataObject", "Order", 480, 300);
        var node = El("node", "App server", 160, 450);
        var db = El("systemSoftware", "Database", 380, 450);
        Rel("serving", bsvc, customer);
        Rel("realization", process, bsvc);
        Rel("serving", asvc, process);
        Rel("realization", app, asvc);
        Rel("access", app, data);
        Rel("serving", node, app);
        Rel("serving", db, app);
        return d;
    }

    // ================================================================ interaction diagrams

    private static Diagram TimingSample(ModelRepository m, Element pkg)
    {
        var interaction = m.Create(pkg, ElementKind.Interaction, "Door Timing");
        var d = m.CreateDiagram(interaction, DiagramType.Timing, "Door Timing");
        m.Set(d, InteractionNotation.TimeUnit, "s");
        Element Lifeline(string name, string states, string timeline, double y)
        {
            var l = m.Create(interaction, ElementKind.TimingLifeline, name);
            m.Set(l, InteractionNotation.States, states);
            m.Set(l, InteractionNotation.Timeline, timeline);
            m.AddNode(d, l, new Bounds(40, y, 640, 130));
            return l;
        }
        Lifeline("Controller", "Idle, Unlocking, Open", "0 Idle; 4 Unlocking; 6 Open; 16 Idle", 40);
        Lifeline("Door", "Closed, Opening, Open", "0 Closed; 6 Opening; 9 Open; 18 Closed", 200);
        // Place the constraints on the time axis: x of a time inside the lifelines' plot area.
        var (t0, t1) = InteractionNotation.TimeRange(d);
        double X(double t) => 40 + InteractionNotation.TimingLabelWidth + 10 + (t - t0) / (t1 - t0) * (640 - InteractionNotation.TimingLabelWidth - 20);
        var dur = m.Create(interaction, ElementKind.DurationConstraint);
        m.Set(dur, InteractionNotation.Constraint, "{0..3 s}");
        m.AddNode(d, dur, new Bounds(X(6), 168, X(9) - X(6), 32));
        var at = m.Create(interaction, ElementKind.TimeConstraint);
        m.Set(at, InteractionNotation.Constraint, "{t = 6 s}");
        m.AddNode(d, at, new Bounds(X(6) - 35, 10, 70, 360));
        return d;
    }

    private static Diagram InteractionOverviewSample(ModelRepository m, Element pkg)
    {
        var activity = m.Create(pkg, ElementKind.Activity, "Checkout Overview");
        var d = m.CreateDiagram(activity, DiagramType.InteractionOverview, "Checkout Overview");
        Element Node(ElementKind k, string? name, double x, double y, double w = 0, double h = 0)
        {
            var e = m.Create(activity, k, name);
            var (dw, dh) = Metamodel.DefaultSize(k);
            m.AddNode(d, e, new Bounds(x, y, w > 0 ? w : dw, h > 0 ? h : dh), showRelationships: false);
            return e;
        }
        var start = Node(ElementKind.InitialNode, null, 226, 20);
        var login = Node(ElementKind.InteractionUse, "Authenticate", 140, 90);
        m.Set(login, InteractionNotation.RefersTo, "Authenticate");
        var decide = Node(ElementKind.DecisionNode, null, 220, 210);
        var pay = Node(ElementKind.InlineInteraction, "Pay", 100, 300, 280, 170);
        m.Set(pay, InteractionNotation.Sequence, "Shopper -> Shop : checkout\nShop -> Bank : authorize\nBank -> Shop : approved\nShop -> Shopper : receipt");
        var retry = Node(ElementKind.InteractionUse, "Report failure", 440, 195);
        m.Set(retry, InteractionNotation.RefersTo, "Report failure");
        var end = Node(ElementKind.ActivityFinalNode, null, 226, 520);
        foreach (var (a, b, guard) in new[] { (start, login, ""), (login, decide, ""), (decide, pay, "ok"), (decide, retry, "failed"), (pay, end, ""), (retry, end, "") })
        {
            var f = m.Connect(ElementKind.ControlFlow, a, b);
            if (guard.Length > 0) m.Set(f, FieldKeys.Guard, guard);
            m.AddEdge(d, f);
        }
        return d;
    }

    private static Diagram CommunicationSample(ModelRepository m, Element pkg)
    {
        var interaction = m.Create(pkg, ElementKind.Interaction, "Place Order");
        var d = m.CreateDiagram(interaction, DiagramType.Communication, "Place Order");
        Element Lifeline(string name, double x, double y)
        {
            var l = m.Create(interaction, ElementKind.CommunicationLifeline, name);
            m.AddNode(d, l, new Bounds(x, y, 150, 46));
            return l;
        }
        var ui = Lifeline(":Storefront", 40, 60);
        var orders = Lifeline(":OrderService", 380, 60);
        var stock = Lifeline(":Inventory", 380, 300);
        void Link(Element a, Element b, string messages)
        {
            var l = m.Connect(ElementKind.CommunicationLink, a, b, interaction);
            m.Set(l, InteractionNotation.Messages, messages);
            m.AddEdge(d, l);
        }
        Link(ui, orders, "1: placeOrder(cart)\n< 1.3: confirmation");
        Link(orders, stock, "1.1: reserve(items)\n< 1.2: reserved");
        return d;
    }

    // ================================================================ UAF viewpoints

    private static string UafDescription(UafViewpoint v) =>
        $"{v.Domain} domain: " + string.Join(", ", v.Elements.Select(e => e.Stereotype).Distinct()) + ", using the UAF profile.";

    /// <summary>A starter view for a UAF domain: the profile (added if missing), a parent element composed of two children and a dependency.</summary>
    private static Diagram UafView(ModelRepository m, Element pkg, UafViewpoint v)
    {
        foreach (var st in v.Elements.Select(e => e.Stereotype).Distinct()) Profiles.EnsureUafStereotype(m, st);
        var d = m.CreateDiagram(pkg, v.Type, v.Name.Replace("UAF ", ""));
        var (pk, ps) = v.Elements[0];
        Element El(ElementKind k, string stereotype, string name, double x, double y)
        {
            var e = m.Create(pkg, k, name);
            m.Set(e, FieldKeys.Stereotypes, stereotype);
            if (k == ElementKind.Requirement) m.Set(e, FieldKeys.Text, $"The enterprise shall achieve {name.ToLowerInvariant()}.");
            var (w, h) = Notation.DefaultSizeOf(e);
            m.AddNode(d, e, new Bounds(x, y, Math.Max(w, 200), h), showRelationships: false);
            return e;
        }
        void Rel(ElementKind k, Element s, Element t, string? stereotype = null)
        {
            if (Metamodel.CheckEnds(k, s.Kind, t.Kind) != null) return;
            var r = m.Connect(k, s, t, pkg);
            if (stereotype != null) m.Set(r, FieldKeys.Stereotypes, stereotype);
            m.AddEdge(d, r);
        }
        // A taxonomy of the domain's main element, plus its second element linked to one of the parts.
        var parent = El(pk, ps, ps, 240, 30);
        var a = El(pk, ps, $"{ps} A", 60, 250);
        var b = El(pk, ps, $"{ps} B", 420, 250);
        Rel(pk == ElementKind.Requirement ? ElementKind.DeriveReqt : ElementKind.Composition, pk == ElementKind.Requirement ? a : parent, pk == ElementKind.Requirement ? parent : a);
        Rel(pk == ElementKind.Requirement ? ElementKind.DeriveReqt : ElementKind.Composition, pk == ElementKind.Requirement ? b : parent, pk == ElementKind.Requirement ? parent : b);
        if (v.Links.FirstOrDefault(l => l.Stereotype != null) is { Stereotype: { } exchange } ex) Rel(ex.Kind, a, b, exchange);
        if (v.Elements.Count > 1)
        {
            var (ck, cs) = v.Elements[1];
            var c = El(ck, cs, cs, 240, 450);
            foreach (var (lk, ls) in v.Links.Where(l => l.Kind is not (ElementKind.Composition or ElementKind.Generalization) && l.Stereotype == null))
            {
                if (Metamodel.CheckEnds(lk, c.Kind, a.Kind) == null) { Rel(lk, c, a, ls); break; }
                if (Metamodel.CheckEnds(lk, a.Kind, c.Kind) == null) { Rel(lk, a, c, ls); break; }
            }
        }
        return d;
    }

    // ================================================================ helpers

    private static Element Req(ModelRepository m, Element owner, string name, string text)
    {
        var r = m.Create(owner, ElementKind.Requirement, name);
        m.Set(r, FieldKeys.Text, text);
        return r;
    }

    private static void Place(ModelRepository m, Diagram d, Element e, double x, double y, double w = 0, double h = 0)
    {
        var (dw, dh) = Notation.DefaultSizeOf(e);
        m.AddNode(d, e, new Bounds(x, y, w > 0 ? w : dw, h > 0 ? h : dh));
    }

    private static Element Shape(ModelRepository m, Element owner, Diagram d, string subtype, string label, double x, double y, double w, double h)
    {
        var e = m.Create(owner, ElementKind.Shape, label);
        m.Set(e, FieldKeys.Subtype, subtype);
        m.AddNode(d, e, new Bounds(x, y, w, h), showRelationships: false);
        return e;
    }

    private static void Line(ModelRepository m, Diagram d, Element a, Element b, string label = "", string sub = "arrow")
    {
        var l = m.Connect(ElementKind.Line, a, b, d.Owner);
        m.Set(l, FieldKeys.Subtype, sub);
        if (label.Length > 0) m.Set(l, FieldKeys.Name, label);
        m.AddEdge(d, l);
    }

    private static void SetFill(ModelRepository m, Diagram d, Element e, string fill)
    {
        if (d.NodeFor(e) is { } n) m.SetNodeStyle(d, n, "fill", fill);
    }
}
