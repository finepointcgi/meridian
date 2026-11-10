using System.Text.Json;
using Meridian.Assistant.Engine;
using Meridian.Core.Derivation;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;

namespace Meridian.Tests;

public class DiagramDerivationTests
{
    private const string Steps = """
        1. Driver presses the start button.
        2. System checks the brake pedal.
        3. If the brake is not pressed, System shows a warning.
        4. System starts the engine.
        Alternate: engine fails to start - System reports a fault.
        5. Driver releases the parking brake.
        """;

    /// <summary>A small model: a driver, a use case with steps, an engine controller block shown as the use case's subject.</summary>
    private static (ModelRepository M, Element UseCase, Element Driver) StartModel(string? steps = Steps)
    {
        var m = new ModelRepository("Car");
        var pkg = m.Create(m.Root, ElementKind.Package, "Behavior");
        var driver = m.Create(pkg, ElementKind.Actor, "Driver");
        var uc = m.Create(pkg, ElementKind.UseCase, "Start Car");
        if (steps != null) m.Set(uc, FieldKeys.Documentation, steps);
        m.Connect(ElementKind.Association, driver, uc);
        m.History.Clear();
        return (m, uc, driver);
    }

    private static DerivationResult Derive(ModelRepository m, DerivationKind k, Element source, Element? target = null, DerivationOptions? o = null) =>
        DiagramDerivation.Apply(m, DiagramDerivation.Plan(m, k, source, target, o));

    private static Element Named(ModelRepository m, ElementKind k, string name) => m.OfKind(k).Single(e => e.Name == name);

    // ================================================================== steps

    [Fact]
    public void Steps_Are_Parsed_From_Numbered_Lines_With_Decisions_And_Alternates()
    {
        var steps = UseCaseSteps.Parse(Steps, ["Driver"]);
        Assert.Equal(6, steps.Count);
        Assert.Equal(("Driver", "Presses the start button"), (steps[0].Actor, steps[0].Text));
        Assert.Equal((null as string, "Checks the brake pedal"), (steps[1].Actor, steps[1].Text));
        Assert.Equal(StepKind.If, steps[2].Kind);
        Assert.Equal("the brake is not pressed", steps[2].Guard);
        Assert.Equal("Shows a warning", steps[2].Text);
        Assert.Equal(StepKind.Alternate, steps[4].Kind);
        Assert.Equal("engine fails to start", steps[4].Guard);
        Assert.Equal("Reports a fault", steps[4].Text);
        Assert.Equal("Driver", steps[5].Actor);

        // Without a list, sentences are steps.
        var prose = UseCaseSteps.Parse("The driver turns the key. The system cranks the engine.", ["Driver"]);
        Assert.Equal(["Turns the key", "Cranks the engine"], prose.Select(s => s.Text));
        Assert.Equal("Driver", prose[0].Actor);
    }

    // ================================================================== requirements → use cases

    [Fact]
    public void Requirements_Become_Use_Cases_With_Actors_And_Refine_Links()
    {
        var m = new ModelRepository("Ops");
        var reqs = m.Create(m.Root, ElementKind.Package, "Requirements");
        Element Req(string name, string text)
        {
            var r = m.Create(reqs, ElementKind.Requirement, name);
            m.Set(r, FieldKeys.Text, text);
            return r;
        }
        var schedule = Req("Scheduling", "The system shall allow the operator to schedule imaging passes.");
        var schedule2 = Req("Rescheduling", "The ground segment shall let the operator schedule imaging passes from the web.");
        var report = Req("Reporting", "The system shall generate daily reports.");
        m.Set(report, "attr:Stakeholder", "Mission Manager");
        var speed = Req("Speed", "The system shall respond within 2 s.");
        m.History.Clear();

        var plan = DiagramDerivation.Plan(m, DerivationKind.UseCasesFromRequirements, reqs);
        Assert.False(plan.IsUpdate);
        var ucs = plan.Items.Where(i => i.Kind == ElementKind.UseCase).ToList();
        Assert.Contains(ucs, u => u.Name == "Schedule Imaging Passes" && u.Selected);
        Assert.Contains(ucs, u => u.Name == "Generate Daily Reports" && u.Selected);
        var perf = Assert.Single(ucs, u => u.Name == "Respond");
        Assert.False(perf.Selected);   // a performance requirement: listed, not ticked

        var r = DiagramDerivation.Apply(m, plan);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.UseCase, d.DiagramType);
        var uc = Named(m, ElementKind.UseCase, "Schedule Imaging Passes");
        Assert.Equal([schedule, schedule2], m.Outgoing(uc, ElementKind.Refine).Select(x => x.Target).OrderBy(x => x.Name == "Rescheduling"));
        Assert.Contains(m.Incoming(uc, ElementKind.Association), a => a.Source.Name == "Operator");
        Assert.Contains(m.Incoming(Named(m, ElementKind.UseCase, "Generate Daily Reports"), ElementKind.Association), a => a.Source.Name == "Mission Manager");
        Assert.DoesNotContain(m.OfKind(ElementKind.UseCase), u => u.Name == "Respond");
        Assert.Empty(m.Incoming(speed, ElementKind.Refine));
        Assert.True(d.Shows(uc));
        Assert.Equal(reqs.Id, uc.Get(DeriveFields.Source));
        Assert.Equal("usecases-from-requirements", uc.Get(DeriveFields.Kind));
        Assert.Equal(plan.Fingerprint, d.Get(DeriveFields.Fingerprint));

        // One undo step removes everything.
        m.History.Undo();
        Assert.Empty(m.OfKind(ElementKind.UseCase));
        Assert.Empty(m.Diagrams);
    }

    [Fact]
    public void Requirements_Without_An_Identifiable_Actor_Get_A_Placeholder_User()
    {
        var m = SampleModels.Vehicle();
        var reqPkg = m.Root.OwnedElements.Single(e => e.Name == "Requirements");
        var plan = DiagramDerivation.Plan(m, DerivationKind.UseCasesFromRequirements, reqPkg);
        foreach (var i in plan.Items.Where(i => i.Kind == ElementKind.UseCase)) i.Selected = true;
        DiagramDerivation.Apply(m, plan);
        var user = Named(m, ElementKind.Actor, "User");
        Assert.NotEmpty(m.Outgoing(user, ElementKind.Association));
        Assert.Contains(m.OfKind(ElementKind.UseCase), u => u.Name == "Accelerate");
    }

    // ================================================================== use case → activity

    [Fact]
    public void Use_Case_Steps_Become_An_Activity_With_Swimlanes_Decisions_And_One_Undo_Step()
    {
        var (m, uc, driver) = StartModel();
        var r = Derive(m, DerivationKind.ActivityFromUseCase, uc);

        var act = Assert.Single(uc.OwnedElements, e => e.Kind == ElementKind.Activity);   // the use case's behaviour
        Assert.Equal("Start Car", act.Name);
        var lanes = act.OwnedElements.Where(e => e.Kind == ElementKind.Partition).ToList();
        Assert.Equal(["Driver", "System"], lanes.Select(l => l.Name));
        Assert.Equal(driver.Id, lanes[0].Get(FieldKeys.Represents));
        var actions = act.OwnedElements.Where(e => e.Kind == ElementKind.Action).Select(a => a.Name).ToList();
        Assert.Equal(["Presses the start button", "Checks the brake pedal", "Shows a warning", "Starts the engine", "Reports a fault", "Releases the parking brake"], actions);
        Assert.Equal(2, act.OwnedElements.Count(e => e.Kind == ElementKind.DecisionNode));
        Assert.Single(act.OwnedElements, e => e.Kind == ElementKind.InitialNode);
        Assert.Single(act.OwnedElements, e => e.Kind == ElementKind.ActivityFinalNode);
        var guards = m.Relationships.Where(x => x.Kind == ElementKind.ControlFlow).Select(x => x.Get(FieldKeys.Guard)).Where(g => g.Length > 0).ToList();
        Assert.Contains("the brake is not pressed", guards);
        Assert.Contains("engine fails to start", guards);
        Assert.Contains("else", guards);

        // The diagram: actions nested in their lanes, flows drawn.
        var d = Assert.Single(r.Diagrams);
        Assert.Same(act, d.Owner);
        var press = d.Nodes.Single(n => n.Element.Name == "Presses the start button");
        Assert.Equal("Driver", d.ParentOf(press)!.Element.Name);
        Assert.Equal("System", d.ParentOf(d.Nodes.Single(n => n.Element.Name == "Starts the engine"))!.Element.Name);
        Assert.Equal(m.Relationships.Count(x => x.Kind == ElementKind.ControlFlow), d.Edges.Count);
        foreach (var e in act.Descendants().Append(act).Append(d))
        {
            Assert.Equal(uc.Id, e.Get(DeriveFields.Source));
            Assert.Equal("activity-from-usecase", e.Get(DeriveFields.Kind));
            Assert.NotEmpty(e.Get(DeriveFields.Key));
            Assert.NotEmpty(e.Get(DeriveFields.Fingerprint));
        }
        Assert.Contains("⌘Z", r.Summary);

        Assert.True(m.History.CanUndo);
        m.History.Undo();
        Assert.Empty(uc.OwnedElements);
        Assert.False(m.History.CanUndo);
    }

    [Fact]
    public void A_Use_Case_Without_Steps_Gets_A_Skeleton_And_A_Warning()
    {
        var m = SampleModels.Vehicle();
        var drive = Named(m, ElementKind.UseCase, "Drive Vehicle");
        var plan = DiagramDerivation.Plan(m, DerivationKind.ActivityFromUseCase, drive);
        Assert.Contains(plan.Warnings, w => w.Contains("skeleton"));
        DiagramDerivation.Apply(m, plan);
        var act = drive.OwnedElements.Single(e => e.Kind == ElementKind.Activity);
        Assert.Equal(["Drive Vehicle"], act.OwnedElements.Where(e => e.Kind == ElementKind.Action).Select(a => a.Name));
        Assert.Equal(["Driver", "System"], act.OwnedElements.Where(e => e.Kind == ElementKind.Partition).Select(p => p.Name));
    }

    // ================================================================== activity → sequence

    [Fact]
    public void Activity_Swimlanes_Become_Lifelines_And_Hand_Overs_Become_Messages()
    {
        var m = CubeSatSample.Create();
        var act = Named(m, ElementKind.Activity, "Acquire Image");
        var r = Derive(m, DerivationKind.SequenceFromActivity, act);

        var ia = Named(m, ElementKind.Interaction, "Acquire Image sequence");
        var lifelines = ia.OwnedElements.Where(e => e.Kind == ElementKind.Lifeline).ToList();
        Assert.Equal(["attitude", "imager", "computer"], lifelines.Select(l => l.Name));
        foreach (var (ll, lane) in lifelines.Zip(act.OwnedElements.Where(e => e.Kind == ElementKind.Partition)))
            Assert.Equal(lane.Get(FieldKeys.Represents), ll.TypeRef);
        // Settle (attitude) → Capture (imager) → Compress (computer); Slew → Settle stays inside one lane.
        var messages = m.Relationships.Where(x => x.Kind == ElementKind.Message && ReferenceEquals(x.Source.Owner, ia)).Select(x => x.Name).ToList();
        Assert.Equal(["captureScene", "compressImage"], messages);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.Sequence, d.DiagramType);
        Assert.Equal(3, d.Nodes.Count(n => n.Element.Kind == ElementKind.Lifeline));
        Assert.Equal(2, d.Edges.Count);
        Assert.True(Notation.MessageY(d.Edges[0]) < Notation.MessageY(d.Edges[1]));
        // Every action allocated to its lane's block (already allocated in the sample: no duplicates).
        foreach (var a in act.OwnedElements.Where(e => e.Kind == ElementKind.Action))
            Assert.Single(m.Outgoing(a, ElementKind.Allocate));
        Assert.Contains(m.Outgoing(ia, ElementKind.Trace), t => ReferenceEquals(t.Target, act));
    }

    [Fact]
    public void An_Activity_Without_Swimlanes_Warns()
    {
        var m = SampleModels.Vehicle();
        var plan = DiagramDerivation.Plan(m, DerivationKind.SequenceFromActivity, Named(m, ElementKind.Activity, "Start Vehicle"));
        Assert.Contains(plan.Warnings, w => w.Contains("no swimlanes"));
        Assert.Single(plan.Items, i => i.Kind == ElementKind.Lifeline);
    }

    // ================================================================== the full chain

    [Fact]
    public void Full_Chain_Use_Case_To_Activity_To_Sequence_To_Structure_To_State_Machine()
    {
        var (m, uc, driver) = StartModel();
        Derive(m, DerivationKind.ActivityFromUseCase, uc);
        var act = uc.OwnedElements.Single(e => e.Kind == ElementKind.Activity);

        // Activity → sequence: Driver and System lifelines; the If step is an alt fragment, the alternate flow too.
        var seq = Derive(m, DerivationKind.SequenceFromActivity, act);
        var ia = m.OfKind(ElementKind.Interaction).Single();
        Assert.Same(uc, ia.Owner);
        var lls = ia.OwnedElements.Where(e => e.Kind == ElementKind.Lifeline).ToList();
        Assert.Equal(["driver", "system"], lls.Select(l => l.Name));
        Assert.Equal(driver.Id, lls[0].TypeRef);
        var system = Named(m, ElementKind.Block, "System");   // created for the lane that represents nothing
        Assert.Equal(system.Id, lls[1].TypeRef);
        var msgs = m.Relationships.Where(x => x.Kind == ElementKind.Message).ToList();
        Assert.Contains(msgs, x => x.Name == "checksTheBrakePedal" && ReferenceEquals(x.Source, lls[0]) && ReferenceEquals(x.Target, lls[1]));
        Assert.Contains(msgs, x => x.Name == "releasesTheParkingBrake" && ReferenceEquals(x.Source, lls[1]) && ReferenceEquals(x.Target, lls[0]));
        var frags = ia.OwnedElements.Where(e => e.Kind == ElementKind.CombinedFragment).Select(f => f.Get(FieldKeys.Operator)).ToList();
        Assert.Contains("alt", frags);
        Assert.Contains(m.Relationships, x => x.Kind == ElementKind.Allocate && ReferenceEquals(x.Target, system));
        Assert.Single(seq.Diagrams);

        // Sequence → BDD + IBD.
        var st = Derive(m, DerivationKind.StructureFromSequence, ia);
        Assert.Equal([DiagramType.BlockDefinition, DiagramType.InternalBlock], st.Diagrams.Select(d => d.DiagramType));
        var ctx = Named(m, ElementKind.Block, "Start Car sequence context");
        var parts = ctx.OwnedElements.Where(e => e.Kind == ElementKind.Property).ToList();
        Assert.Equal(["driver", "system"], parts.Select(p => p.Name));
        Assert.Equal(system.Id, parts[1].TypeRef);
        Assert.Contains(system.OwnedElements, o => o.Kind == ElementKind.Operation && o.Name == "checksTheBrakePedal");
        Assert.Contains(driver.OwnedElements, o => o.Kind == ElementKind.Operation && o.Name == "releasesTheParkingBrake");
        Assert.Contains(m.Relationships, x => x.Kind == ElementKind.Trace && x.Source.Kind == ElementKind.Message && x.Target.Kind == ElementKind.Operation);
        Assert.Contains(m.Relationships, x => x.Kind == ElementKind.Connector);
        var ibd = st.Diagrams[1];
        Assert.Same(ctx, ibd.Owner);
        Assert.Equal(2, ibd.Nodes.Count(n => n.Element.Kind == ElementKind.Property));
        Assert.Single(ibd.Edges, e => e.Relationship.Kind == ElementKind.Connector);
        Assert.True(st.Diagrams[0].Shows(system) && st.Diagrams[0].Shows(ctx));

        // Sequence → state machine for the System block: one state per received event.
        var stm = Derive(m, DerivationKind.StateMachineFromSequence, ia, system);
        var sm = Assert.Single(system.OwnedElements, e => e.Kind == ElementKind.StateMachine);
        var states = sm.OwnedElements.Where(e => e.Kind == ElementKind.State).Select(s => s.Name).ToList();
        Assert.Equal("Idle", states[0]);
        Assert.Contains("After checksTheBrakePedal", states);
        Assert.Contains(stm.Plan.Warnings, w => w.Contains("approximate"));
        Assert.Contains(m.Relationships, t => t.Kind == ElementKind.Transition && t.Name == "checksTheBrakePedal");
        Assert.Equal(system.Id, sm.Get(DeriveFields.Target));
        Assert.Equal(DiagramType.StateMachine, stm.Diagrams.Single().DiagramType);

        // The chain from the activity: Requirements (none) → Use case → Activity → Sequence → IBD.
        var chain = DerivationChain.For(m, act);
        Assert.Equal(["Requirements", "Use case", "Activity", "Sequence", "IBD", "State machine"], chain.Select(c => c.Label));
        Assert.Same(uc, chain[1].Element);
        Assert.True(chain[2].Current);
        Assert.Same(ia, chain[3].Element);
        Assert.Same(ctx, chain[4].Element);
        Assert.Same(ibd, chain[4].View);
        Assert.Same(sm, chain[5].Element);
    }

    [Fact]
    public void Structure_From_The_Vehicle_Sequence_Reuses_Blocks_And_Operations()
    {
        var m = SampleModels.Vehicle();
        var ia = Named(m, ElementKind.Interaction, "Start Engine Sequence");
        var engine = Named(m, ElementKind.Block, "Engine");
        var vehicle = Named(m, ElementKind.Block, "Vehicle");
        var blocksBefore = m.OfKind(ElementKind.Block).Count();
        var startOps = engine.OwnedElements.Count(o => o.Kind == ElementKind.Operation && o.Name == "start");
        Derive(m, DerivationKind.StructureFromSequence, ia);
        Assert.Equal(blocksBefore + 1, m.OfKind(ElementKind.Block).Count());   // only the context block
        Assert.Equal(startOps, engine.OwnedElements.Count(o => o.Kind == ElementKind.Operation && o.Name == "start"));
        Assert.Contains(vehicle.OwnedElements, o => o.Kind == ElementKind.Operation && o.Name == "pressStart");
        var showReady = Named(m, ElementKind.Actor, "Driver").OwnedElements.Single(o => o.Name == "showReady");
        Assert.Contains("reception", showReady.Stereotypes);   // asynchronous message
        Assert.DoesNotContain(vehicle.OwnedElements, o => o.Name == "started");   // replies make no operation
        Assert.Contains(engine.OwnedElements, p => p.Kind == ElementKind.Port && p.Name == "vehiclePort");
    }

    [Fact]
    public void State_Machine_From_An_Activity_For_A_Swimlane_Block()
    {
        var m = CubeSatSample.Create();
        var act = Named(m, ElementKind.Activity, "Acquire Image");
        var offers = DiagramDerivation.Offers(m, act);
        var offer = Assert.Single(offers, o => o.Kind == DerivationKind.StateMachineFromActivity && o.Target!.Name == m.Find(act.OwnedElements.First(p => p.Name == "imager").Get(FieldKeys.Represents))!.Name);
        var r = Derive(m, offer.Kind, offer.Source, offer.Target);
        var sm = Assert.Single(offer.Target!.OwnedElements, e => e.Kind == ElementKind.StateMachine && DeriveFields.IsDerived(e));
        Assert.Contains(sm.OwnedElements, s => s.Name == "After captureScene");
        Assert.Single(r.Diagrams);
    }

    // ================================================================== satisfy / verify proposals

    [Fact]
    public void Satisfy_And_Verify_Are_Proposed_Per_Item()
    {
        var m = SampleModels.Vehicle();
        var reqPkg = m.Root.OwnedElements.Single(e => e.Name == "Requirements");
        var range = Named(m, ElementKind.Requirement, "Range");
        var plan = DiagramDerivation.Plan(m, DerivationKind.SatisfyAndVerify, reqPkg);
        Assert.True(plan.ProposalsOnly);
        Assert.Contains(plan.Items, i => i.Kind == ElementKind.Satisfy && plan.NameOf(i.Source) == "Vehicle" && i.Target!.Value.Element == range);
        Assert.Contains(plan.Items, i => i.Kind == ElementKind.TestCase && i.Name == "Verify Range");
        Assert.DoesNotContain(plan.Items, i => i.Kind == ElementKind.TestCase && i.Name == "Verify Acceleration");   // verified already
        // Untick the test case for Range: its «verify» goes too.
        plan.Items.Single(i => i.Name == "Verify Range").Selected = false;
        DiagramDerivation.Apply(m, plan);
        Assert.Empty(m.Incoming(range, ElementKind.Verify));
        Assert.Contains(m.Incoming(range, ElementKind.Satisfy), s => s.Source.Name == "Vehicle");
        Assert.Contains(m.Incoming(Named(m, ElementKind.Requirement, "Engine Power"), ElementKind.Verify), v => v.Source.Name == "Verify Engine Power");
        // Asked again: only what is still missing.
        var again = DiagramDerivation.Plan(m, DerivationKind.SatisfyAndVerify, reqPkg);
        Assert.Single(again.Items, i => i.Kind == ElementKind.TestCase);
        Assert.DoesNotContain(again.Items, i => i.Change == DerivedChange.Remove);
    }

    // ================================================================== idempotence and update from source

    [Fact]
    public void Deriving_Twice_Offers_An_Update_Rather_Than_A_Duplicate()
    {
        var (m, uc, _) = StartModel();
        Derive(m, DerivationKind.ActivityFromUseCase, uc);
        var count = m.Count;
        Assert.Contains(DiagramDerivation.Offers(m, uc), o => o.Exists && o.Label.StartsWith("Update"));
        var again = DiagramDerivation.Plan(m, DerivationKind.ActivityFromUseCase, uc);
        Assert.True(again.IsUpdate);
        Assert.False(again.HasChanges);
        Assert.Equal("Up to date: nothing to change", again.Summary);
        DiagramDerivation.Apply(m, again);
        Assert.Equal(count, m.Count);
        Assert.Single(uc.OwnedElements, e => e.Kind == ElementKind.Activity);
        Assert.False(DiagramDerivation.IsStale(m, DiagramDerivation.ExistingDiagrams(m, DerivationKind.ActivityFromUseCase, uc).Single()));
    }

    [Fact]
    public void Update_From_Source_Adds_Changes_And_Removes_But_Leaves_User_Work_Alone()
    {
        var (m, uc, _) = StartModel("""
            1. Driver presses the start button.
            2. System checks the brake pedal.
            3. System starts the engine.
            4. System shows the dashboard.
            """);
        var first = Derive(m, DerivationKind.ActivityFromUseCase, uc);
        var d = first.Diagrams.Single();
        var act = uc.OwnedElements.Single(e => e.Kind == ElementKind.Activity);
        Assert.False(DiagramDerivation.IsStale(m, d));

        // The user moves a node, adds an action of their own and edits step 4's action name.
        var startNode = d.Nodes.Single(n => n.Element.Name == "Starts the engine");
        var moved = startNode.Bounds.Offset(0, 7);
        m.SetBounds(d, new Dictionary<DiagramNode, Bounds> { [startNode] = moved });
        var mine = m.Create(act, ElementKind.Action, "My own check");
        m.AddNode(d, mine, new Bounds(600, 600, 130, 56));
        var dash = act.OwnedElements.Single(e => e.Name == "Shows the dashboard");

        // The source changes: step 2 reworded, step 4 removed, a new step 5.
        m.Set(uc, FieldKeys.Documentation, """
            1. Driver presses the start button.
            2. System verifies the brake pedal.
            3. System starts the engine.
            5. Driver drives away.
            """);
        Assert.True(DiagramDerivation.IsStale(m, d));
        var plan = DiagramDerivation.Plan(m, DerivationKind.ActivityFromUseCase, uc);
        Assert.True(plan.IsUpdate);
        var changed = Assert.Single(plan.Items, i => i.Change == DerivedChange.Change && i.Kind == ElementKind.Action);
        Assert.Equal("Verifies the brake pedal", changed.Name);
        Assert.Contains(FieldKeys.Name, changed.ChangedFields);
        var removed = Assert.Single(plan.Items, i => i.Change == DerivedChange.Remove && i.Kind == ElementKind.Action);
        Assert.Same(dash, removed.Existing);
        Assert.True(removed.Selected);
        var added = Assert.Single(plan.Items, i => i.Change == DerivedChange.Add && i.Kind == ElementKind.Action);
        Assert.Equal("Drives away", added.Name);
        Assert.DoesNotContain(plan.Items, i => ReferenceEquals(i.Existing, mine));

        var historyBefore = m.History.UndoDescription;
        var r = DiagramDerivation.Apply(m, plan);
        Assert.Equal((1, 1), (r.Changed, r.Removed > 0 ? 1 : 0));
        Assert.Contains(act.OwnedElements, e => e.Name == "Verifies the brake pedal");
        Assert.DoesNotContain(act.OwnedElements, e => e.Name == "Shows the dashboard");
        var drives = act.OwnedElements.Single(e => e.Name == "Drives away");
        Assert.Contains(mine, act.OwnedElements);                       // user-added element untouched
        Assert.Equal(moved, startNode.Bounds);                           // user-moved node not moved
        Assert.True(d.Shows(drives));
        Assert.Equal("Driver", d.ParentOf(d.NodeFor(drives)!)!.Element.Name);
        Assert.False(DiagramDerivation.IsStale(m, d));

        // One undo step puts it all back.
        m.History.Undo();
        Assert.Equal(historyBefore, m.History.UndoDescription);
        Assert.Contains(act.OwnedElements, e => e.Name == "Checks the brake pedal");
        Assert.Contains(dash, act.OwnedElements);
        Assert.DoesNotContain(act.OwnedElements, e => e.Name == "Drives away");
    }

    [Fact]
    public void Derived_Items_The_User_Edited_Are_Offered_But_Not_Ticked()
    {
        var (m, uc, _) = StartModel("1. System checks the brake pedal.\n2. System starts the engine.");
        Derive(m, DerivationKind.ActivityFromUseCase, uc);
        var act = uc.OwnedElements.Single(e => e.Kind == ElementKind.Activity);
        var start = act.OwnedElements.Single(e => e.Name == "Starts the engine");
        m.Set(start, FieldKeys.Documentation, "My note");
        m.Set(uc, FieldKeys.Documentation, "1. System checks the brake pedal.");
        var plan = DiagramDerivation.Plan(m, DerivationKind.ActivityFromUseCase, uc);
        var gone = Assert.Single(plan.Items, i => i.Change == DerivedChange.Remove && ReferenceEquals(i.Existing, start));
        Assert.False(gone.Selected);
        Assert.Contains("edited", gone.Note);
        DiagramDerivation.Apply(m, plan);
        Assert.Contains(start, act.OwnedElements);   // never auto-deleted
    }

    // ================================================================== AI fill-in (fake, no network)

    private sealed class FakeAssistant(DerivationAiResult answer) : IDerivationAssistant
    {
        public string Name => "Fake";
        public DerivationAiRequest? Asked { get; private set; }
        public Task<DerivationAiResult> FillInAsync(DerivationAiRequest request, CancellationToken ct = default)
        {
            Asked = request;
            return Task.FromResult(answer);
        }
    }

    [Fact]
    public async Task Ai_Fill_In_Writes_Steps_That_Are_Validated_And_Previewed()
    {
        var (m, uc, _) = StartModel("The driver wants the car running quickly; sometimes the battery is flat.");
        var fake = new FakeAssistant(new DerivationAiResult(true, null,
            ["1. Driver presses start {now}", "If the battery is flat, System asks for a jump start", "System starts the engine", "  "], new Dictionary<string, string>()));
        var request = DerivationAi.Request(m, DerivationKind.ActivityFromUseCase, uc)!;
        var answer = await fake.FillInAsync(request);
        Assert.Equal(["Driver"], fake.Asked!.Actors);
        var options = DerivationAi.Validate(request, answer, fake.Name, out var error);
        Assert.Null(error);
        Assert.Equal(3, options!.Steps!.Count);
        Assert.DoesNotContain(options.Steps, s => s.Contains('{'));
        var plan = DiagramDerivation.Plan(m, DerivationKind.ActivityFromUseCase, uc, null, options);
        Assert.Equal("Steps written by Fake", plan.AiNote);
        Assert.Contains(plan.Items, i => i.Name == "Asks for a jump start" && i.Note == "written by AI");
        Assert.Contains(plan.Items, i => i.Kind == ElementKind.DecisionNode);

        var bad = DerivationAi.Validate(request, DerivationAiResult.Fail("offline"), "Fake", out error);
        Assert.Null(bad);
        Assert.Equal("offline", error);
    }

    [Fact]
    public void Ai_Message_Names_Only_Apply_To_Messages_That_Were_Asked_About()
    {
        var m = CubeSatSample.Create();
        var act = Named(m, ElementKind.Activity, "Acquire Image");
        var request = DerivationAi.Request(m, DerivationKind.SequenceFromActivity, act)!;
        Assert.Equal(2, request.Messages.Count);
        var names = new Dictionary<string, string> { [request.Messages[0].Key] = "Start capture!", ["msg:unknown"] = "x" };
        var options = DerivationAi.Validate(request, new DerivationAiResult(true, null, [], names), "Fake", out _)!;
        Assert.Equal(["startCapture"], options.MessageNames!.Values);
        var plan = DiagramDerivation.Plan(m, DerivationKind.SequenceFromActivity, act, null, options);
        Assert.Contains(plan.Items, i => i.Kind == ElementKind.Message && i.Name == "startCapture" && i.Note == "named by AI");
    }

    [Fact]
    public async Task Derivation_Writer_Fills_In_Steps_Through_A_Local_Model_Provider()
    {
        var (m, uc, _) = StartModel("The driver starts the car; if the battery is flat it asks for help.");
        var args = JsonSerializer.Serialize(new { steps = new[] { "Driver turns the key", "If the battery is flat, System asks for help" }, messages = Array.Empty<object>() });
        var fake = new FakeOpenAiHandler().Chat(new OpenAiSse().ToolCall(0, "call_d", DerivationWriter.ToolName, args).End("tool_calls"));
        var writer = new DerivationWriter(fake.Provider());
        var request = DerivationAi.Request(m, DerivationKind.ActivityFromUseCase, uc)!;
        var result = await writer.FillInAsync(request);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, result.Steps.Count);
        Assert.Contains(DerivationWriter.ToolName, fake.Requests[0].Body);
        Assert.Contains("battery is flat", fake.Requests[0].Body);   // the description is in the prompt
        var options = DerivationAi.Validate(request, result, writer.Name, out _)!;
        var plan = DiagramDerivation.Plan(m, DerivationKind.ActivityFromUseCase, uc, null, options);
        Assert.Contains(plan.Items, i => i.Name == "Turns the key");
    }

    // ================================================================== offers

    [Fact]
    public void Offers_List_Only_Valid_Derivations()
    {
        var m = SampleModels.Vehicle();
        Assert.Equal([DerivationKind.ActivityFromUseCase], DiagramDerivation.Offers(m, Named(m, ElementKind.UseCase, "Refuel")).Select(o => o.Kind));
        var seqOffers = DiagramDerivation.Offers(m, Named(m, ElementKind.Interaction, "Start Engine Sequence")).Select(o => o.Kind).ToList();
        Assert.Contains(DerivationKind.StructureFromSequence, seqOffers);
        Assert.Contains(DerivationKind.StateMachineFromSequence, seqOffers);
        var reqOffers = DiagramDerivation.Offers(m, m.Root.OwnedElements.Single(e => e.Name == "Requirements")).Select(o => o.Kind).ToList();
        Assert.Equal([DerivationKind.UseCasesFromRequirements, DerivationKind.SatisfyAndVerify], reqOffers);
        var sd = m.Diagrams.Single(d => d.DiagramType == DiagramType.Sequence);
        Assert.Contains(DiagramDerivation.Offers(m, sd), o => o.Kind == DerivationKind.StructureFromSequence);
        Assert.Empty(DiagramDerivation.Offers(m, Named(m, ElementKind.ValueType, "Power")));
        Assert.Contains(DiagramDerivation.Offers(m, Named(m, ElementKind.Block, "Engine")), o => o.Kind == DerivationKind.StateMachineFromSequence);
    }
}
