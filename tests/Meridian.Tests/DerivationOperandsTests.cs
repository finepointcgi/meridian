using Meridian.Core.Derivation;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization.Mermaid;

namespace Meridian.Tests;

/// <summary>Derivation with real operands, choice pseudostates and text-matched unnumbered steps.</summary>
public class DerivationOperandsTests
{
    private static DerivationResult Derive(ModelRepository m, DerivationKind k, Element source, Element? target = null) =>
        DiagramDerivation.Apply(m, DiagramDerivation.Plan(m, k, source, target));

    /// <summary>An activity with swimlanes for a driver (actor), the vehicle and the engine (blocks); actions are allocated to them.</summary>
    private sealed class Act
    {
        public ModelRepository M { get; } = new("Car");
        public Element Activity { get; }
        public Dictionary<string, Element> Lanes { get; } = new();
        public Dictionary<string, Element> Nodes { get; } = new();

        public Act()
        {
            var pkg = M.Create(M.Root, ElementKind.Package, "Behavior");
            Activity = M.Create(pkg, ElementKind.Activity, "Start");
            foreach (var (name, kind) in new[] { ("Driver", ElementKind.Actor), ("Vehicle", ElementKind.Block), ("Engine", ElementKind.Block) })
            {
                var type = M.Create(pkg, kind, name);
                var lane = M.Create(Activity, ElementKind.Partition, name);
                M.Set(lane, FieldKeys.Represents, type.Id);
                Lanes[name] = type;
            }
        }

        public Element Node(string key, ElementKind kind, string? lane = null)
        {
            var e = M.Create(Activity, kind, kind == ElementKind.Action ? key : null);
            if (lane != null) M.Connect(ElementKind.Allocate, e, Lanes[lane]);
            return Nodes[key] = e;
        }

        public Relationship Flow(string from, string to, string guard = "")
        {
            var f = M.Connect(ElementKind.ControlFlow, Nodes[from], Nodes[to]);
            if (guard.Length > 0) M.Set(f, FieldKeys.Guard, guard);
            return f;
        }
    }

    private static (Element Fragment, DiagramNode Node, Diagram D) OnlyFragment(DerivationResult r, string op)
    {
        var d = r.Diagrams.Single();
        var n = d.Nodes.Single(x => x.Element.Kind == ElementKind.CombinedFragment && x.Element.Get(FieldKeys.Operator) == op);
        return (n.Element, n, d);
    }

    private static string? OperandGuard(Diagram d, DiagramNode f, string message)
    {
        var y = Notation.MessageY(d.Edges.Single(e => e.Relationship.Name == message));
        if (y < f.Bounds.Y || y > f.Bounds.Bottom) return null;
        return InteractionOperands.At(f, f.Bounds, y).Operand?.Get(FieldKeys.Guard);
    }

    [Fact]
    public void A_Decision_Becomes_An_Alt_With_One_Guarded_Operand_Per_Branch()
    {
        var a = new Act();
        a.Node("init", ElementKind.InitialNode);
        a.Node("Press start", ElementKind.Action, "Driver");
        a.Node("d", ElementKind.DecisionNode);
        a.Node("Crank engine", ElementKind.Action, "Engine");
        a.Node("Show warning", ElementKind.Action, "Vehicle");
        a.Node("Drive", ElementKind.Action, "Driver");
        a.Node("end", ElementKind.ActivityFinalNode);
        a.Flow("init", "Press start");
        a.Flow("Press start", "d");
        a.Flow("d", "Crank engine", "brake pressed");
        a.Flow("d", "Show warning");
        a.Flow("Crank engine", "Drive");
        a.Flow("Show warning", "Drive");
        a.Flow("Drive", "end");

        var r = Derive(a.M, DerivationKind.SequenceFromActivity, a.Activity);
        var (alt, node, d) = OnlyFragment(r, "alt");
        Assert.Equal(["brake pressed", "else"], InteractionOperands.Guards(alt));
        Assert.Equal("brake pressed", OperandGuard(d, node, "crankEngine"));
        Assert.Equal("else", OperandGuard(d, node, "showWarning"));
        // Each branch's hand-over to the driver is in its own operand: the messages of a branch stay together.
        var drives = d.Edges.Where(e => e.Relationship.Name == "drive").Select(e => InteractionOperands.At(node, node.Bounds, Notation.MessageY(e)).Index).Order().ToList();
        Assert.Equal([0, 1], drives);
        var ys = d.Edges.Where(e => e.Relationship.Kind == ElementKind.Message).OrderBy(e => Notation.MessageY(e)).Select(e => e.Relationship.Name).ToList();
        Assert.Equal(["crankEngine", "drive", "showWarning", "drive"], ys);
        Assert.Contains(r.Plan.Warnings, w => w.Contains("one operand per branch"));
    }

    [Fact]
    public void A_Fork_Becomes_A_Par_With_One_Operand_Per_Branch()
    {
        var a = new Act();
        a.Node("init", ElementKind.InitialNode);
        a.Node("Turn key", ElementKind.Action, "Driver");
        a.Node("fork", ElementKind.ForkNode);
        a.Node("Check doors", ElementKind.Action, "Vehicle");
        a.Node("Prime fuel", ElementKind.Action, "Engine");
        a.Node("join", ElementKind.ForkNode);
        a.Node("Go", ElementKind.Action, "Driver");
        a.Flow("init", "Turn key");
        a.Flow("Turn key", "fork");
        a.Flow("fork", "Check doors");
        a.Flow("fork", "Prime fuel");
        a.Flow("Check doors", "join");
        a.Flow("Prime fuel", "join");
        a.Flow("join", "Go");

        var r = Derive(a.M, DerivationKind.SequenceFromActivity, a.Activity);
        var (par, node, d) = OnlyFragment(r, "par");
        var ops = InteractionOperands.Of(par);
        Assert.Equal(2, ops.Count);
        Assert.All(ops, o => Assert.Equal("", o.Get(FieldKeys.Guard)));
        var first = InteractionOperands.At(node, node.Bounds, Notation.MessageY(d.Edges.Single(e => e.Relationship.Name == "checkDoors"))).Index;
        var second = InteractionOperands.At(node, node.Bounds, Notation.MessageY(d.Edges.Single(e => e.Relationship.Name == "primeFuel"))).Index;
        Assert.NotEqual(first, second);
        // Derived again with nothing changed: nothing to update (operands and separators are stable).
        Assert.False(DiagramDerivation.Plan(a.M, DerivationKind.SequenceFromActivity, a.Activity).HasChanges);
    }

    [Fact]
    public void State_Machine_Uses_Replies_For_Handling_States_And_A_Choice_For_Branches_With_The_Same_Event()
    {
        var m = new ModelRepository("Shop");
        var r = MermaidImporter.Import(m, m.Root, """
            sequenceDiagram
                participant C as Client
                participant S as Server
                participant B as Bank
                C->>S: login
                S-->>C: welcome
                alt card ok
                    C->>S: pay
                    S->>B: charge
                else card bad
                    C->>S: pay
                    S-->>C: declined
                end
                C-)S: logout
            """);
        var ia = m.OfKind(ElementKind.Interaction).Single();
        var server = m.Create(m.Root, ElementKind.Block, "Server");
        m.Set(m.OfKind(ElementKind.Lifeline).Single(l => l.Name == "Server"), FieldKeys.Type, server.Id);

        var result = Derive(m, DerivationKind.StateMachineFromSequence, ia, server);
        var sm = server.OwnedElements.Single(e => e.Kind == ElementKind.StateMachine);
        var states = sm.OwnedElements.Where(e => e.Kind == ElementKind.State).Select(s => s.Name).ToList();
        Assert.Equal("Idle", states[0]);
        Assert.Contains("Handling login", states);
        Assert.Contains("Handling pay", states);
        Assert.Contains("After pay", states);
        Assert.Contains("After logout", states);
        var choice = Assert.Single(sm.OwnedElements, e => e.Kind == ElementKind.Pseudostate);
        Assert.Equal(Pseudostates.Choice, Pseudostates.KindOf(choice));
        var ts = m.Relationships.Where(t => t.Kind == ElementKind.Transition && t.Owner == sm).ToList();
        Transition(ts, "Idle", "Handling login", "login");
        Transition(ts, "Handling login", "Idle", "");
        var into = Assert.Single(ts, t => ReferenceEquals(t.Target, choice));
        Assert.Equal(("Idle", "pay"), (into.Source.Name, into.Name));
        var ok = Assert.Single(ts, t => ReferenceEquals(t.Source, choice) && t.Target.Name == "After pay");
        Assert.Equal(("card ok", "charge()", ""), (ok.Get(FieldKeys.Guard), ok.Get(FieldKeys.Effect), ok.Name));
        var bad = Assert.Single(ts, t => ReferenceEquals(t.Source, choice) && t.Target.Name == "Handling pay");
        Assert.Equal("card bad", bad.Get(FieldKeys.Guard));
        Transition(ts, "Handling pay", "Idle", "");
        // After the alternatives, logout is taken from either place the block can be in.
        Assert.Equal(2, ts.Count(t => t.Name == "logout"));
        Assert.Contains(result.Plan.Warnings, w => w.Contains("replies"));
    }

    private static void Transition(IEnumerable<Relationship> ts, string from, string to, string trigger) =>
        Assert.Contains(ts, t => t.Source.Name == from && t.Target.Name == to && t.Name == trigger);

    [Fact]
    public void State_Machine_From_An_Activity_Guards_The_Branches_Of_A_Decision()
    {
        var a = new Act();
        a.Node("init", ElementKind.InitialNode);
        a.Node("Press start", ElementKind.Action, "Driver");
        a.Node("d", ElementKind.DecisionNode);
        a.Node("Crank engine", ElementKind.Action, "Engine");
        a.Node("Stall", ElementKind.Action, "Engine");
        a.Flow("init", "Press start");
        a.Flow("Press start", "d");
        a.Flow("d", "Crank engine", "battery ok");
        a.Flow("d", "Stall", "battery flat");
        var engine = a.Lanes["Engine"];
        Derive(a.M, DerivationKind.StateMachineFromActivity, a.Activity, engine);
        var sm = engine.OwnedElements.Single(e => e.Kind == ElementKind.StateMachine);
        var ts = a.M.Relationships.Where(t => t.Kind == ElementKind.Transition && t.Owner == sm).ToList();
        Assert.Contains(ts, t => t.Source.Name == "Idle" && t.Target.Name == "After crankEngine" && t.Name == "crankEngine" && t.Get(FieldKeys.Guard) == "battery ok");
        Assert.Contains(ts, t => t.Source.Name == "Idle" && t.Target.Name == "After stall" && t.Name == "stall" && t.Get(FieldKeys.Guard) == "battery flat");
    }

    // ================================================================== unnumbered steps

    private static (ModelRepository M, Element UseCase) UseCase(string steps)
    {
        var m = new ModelRepository("Car");
        var pkg = m.Create(m.Root, ElementKind.Package, "Behavior");
        var driver = m.Create(pkg, ElementKind.Actor, "Driver");
        var uc = m.Create(pkg, ElementKind.UseCase, "Start Car");
        m.Set(uc, FieldKeys.Documentation, steps);
        m.Connect(ElementKind.Association, driver, uc);
        return (m, uc);
    }

    [Fact]
    public void Inserting_An_Unnumbered_Step_Is_One_Addition_Not_A_Cascade_Of_Changes()
    {
        var (m, uc) = UseCase("- Driver presses the start button\n- System checks the brake pedal\n- System starts the engine\n- Driver releases the parking brake");
        Derive(m, DerivationKind.ActivityFromUseCase, uc);
        m.Set(uc, FieldKeys.Documentation, "- Driver presses the start button\n- System checks the brake pedal\n- System checks the fuel level\n- System starts the engine\n- Driver releases the parking brake");
        var plan = DiagramDerivation.Plan(m, DerivationKind.ActivityFromUseCase, uc);
        var actions = plan.Items.Where(i => i.Kind == ElementKind.Action).ToList();
        var added = Assert.Single(actions, i => i.Change == DerivedChange.Add);
        Assert.Equal("Checks the fuel level", added.Name);
        Assert.DoesNotContain(actions, i => i.Change is DerivedChange.Change or DerivedChange.Remove);
        Assert.Equal(4, actions.Count(i => i.Change == DerivedChange.Same));

        // Rewording one step is a change of that step; deleting one is one removal.
        DiagramDerivation.Apply(m, plan);
        m.Set(uc, FieldKeys.Documentation, "- Driver presses the start button\n- System checks the fuel level\n- System starts the engines\n- Driver releases the parking brake");
        var next = DiagramDerivation.Plan(m, DerivationKind.ActivityFromUseCase, uc).Items.Where(i => i.Kind == ElementKind.Action).ToList();
        Assert.Equal("Starts the engines", Assert.Single(next, i => i.Change == DerivedChange.Change).Name);
        Assert.Equal("Checks the brake pedal", Assert.Single(next, i => i.Change == DerivedChange.Remove).Existing!.Name);
        Assert.DoesNotContain(next, i => i.Change == DerivedChange.Add);
    }

    [Fact]
    public void Step_Matching_Uses_Text_Similarity_In_Order()
    {
        Assert.Equal(1, UseCaseSteps.Similarity("Starts the engine.", "starts the ENGINE"));
        Assert.True(UseCaseSteps.Similarity("Starts the engine", "Starts the engines") > 0.9);
        Assert.True(UseCaseSteps.Similarity("Starts the engine", "Opens the door") < 0.5);
        var steps = UseCaseSteps.Parse("- New first step\n- Checks the brake\n- Starts the engine", []);
        var keyed = UseCaseSteps.MatchUnnumbered(steps, [("#1", "Checks the brake"), ("#2", "Starts the engine")]);
        Assert.Equal(["#3", "#1", "#2"], keyed.Select(s => s.Id));
        // Numbered steps keep their numbers.
        var numbered = UseCaseSteps.Parse("1. Checks the brake\n2. Starts the engine", []);
        Assert.Equal(["1", "2"], UseCaseSteps.MatchUnnumbered(numbered, [("#1", "Starts the engine")]).Select(s => s.Id));
    }
}
