using System.Globalization;
using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Tests;

/// <summary>
/// UML state machine semantics in the simulators (<see cref="StateMachineEngine"/>): choice, junction, fork / join with
/// orthogonal regions, shallow / deep history, entry / exit points, terminate, nested composites; plus central buffer and
/// data store nodes in activities.
/// </summary>
public class StateMachineSemanticsTests
{
    private sealed class Sm
    {
        public ModelRepository M { get; } = new("SM");
        public Element Context { get; }
        public Element Machine { get; }

        public Sm(params (string Name, double Value)[] variables)
        {
            Context = M.Create(M.Root, ElementKind.Block, "Controller");
            foreach (var (name, value) in variables)
                M.Set(M.Create(Context, ElementKind.Property, name), FieldKeys.DefaultValue, value.ToString(CultureInfo.InvariantCulture));
            Machine = M.Create(Context, ElementKind.StateMachine, "Machine");
        }

        public Element S(string name, Element? owner = null, string? entry = null, string? exit = null)
        {
            var s = M.Create(owner ?? Machine, ElementKind.State, name);
            if (entry != null) M.Set(s, FieldKeys.Entry, entry);
            if (exit != null) M.Set(s, FieldKeys.Exit, exit);
            return s;
        }

        public Element Init(Element? owner = null) => M.Create(owner ?? Machine, ElementKind.InitialState);
        public Element Final(Element? owner = null) => M.Create(owner ?? Machine, ElementKind.FinalState);

        public Element P(string kind, string name, Element? owner = null)
        {
            var e = M.Create(owner ?? Machine, ElementKind.Pseudostate, name);
            M.Set(e, FieldKeys.Subtype, kind);
            return e;
        }

        public Relationship T(Element from, Element to, string? trigger = null, string? guard = null, string? effect = null)
        {
            var r = M.Connect(ElementKind.Transition, from, to);
            if (trigger != null) M.Rename(r, trigger);
            if (guard != null) M.Set(r, FieldKeys.Guard, guard);
            if (effect != null) M.Set(r, FieldKeys.Effect, effect);
            return r;
        }

        public StateMachineSimulator Start()
        {
            var sim = new StateMachineSimulator(M, Machine);
            sim.Start();
            return sim;
        }
    }

    private static string[] Leaves(StateMachineSimulator sim) => sim.ActiveLeaves.Select(e => e.Name).ToArray();

    // ================================================================== choice and junction

    private static (Sm Sm, Element High, Element Low) Branch(string kind, bool withElse = true)
    {
        var sm = new Sm(("x", 0));
        var a = sm.S("A");
        sm.T(sm.Init(), a);
        var p = sm.P(kind, "Pick");
        sm.T(a, p, "go", effect: "x = 5");
        var high = sm.S("High");
        var low = sm.S("Low");
        sm.T(p, high, guard: "x > 3");
        if (withElse) sm.T(p, low, guard: "else");
        else sm.T(p, low, guard: "x < -3");
        return (sm, high, low);
    }

    [Fact]
    public void Choice_Evaluates_Its_Guards_When_Reached_After_Earlier_Effects()
    {
        var (sm, _, _) = Branch(Pseudostates.Choice);
        var sim = sm.Start();
        Assert.True(sim.Send("go"));
        Assert.Equal(["High"], Leaves(sim));            // the effect x = 5 ran before the choice looked at x
        Assert.Contains(sim.Log, l => l.Contains("choice 'Pick' → High [x > 3]"));
    }

    [Fact]
    public void Choice_Takes_Else_When_No_Other_Guard_Holds()
    {
        var sm = new Sm(("x", 0));
        var a = sm.S("A");
        sm.T(sm.Init(), a);
        var c = sm.P(Pseudostates.Choice, "Pick");
        sm.T(a, c, "go", effect: "x = 1");
        sm.T(c, sm.S("High"), guard: "x > 3");
        sm.T(c, sm.S("Low"), guard: "[else]");
        var sim = sm.Start();
        sim.Send("go");
        Assert.Equal(["Low"], Leaves(sim));
    }

    [Fact]
    public void Junction_Chooses_Its_Branch_Statically_Before_The_Effects_Run()
    {
        var (sm, _, _) = Branch(Pseudostates.Junction);
        var sim = sm.Start();
        Assert.True(sim.Send("go"));
        Assert.Equal(["Low"], Leaves(sim));             // x was 0 when the compound transition was selected
        Assert.Equal(5, sim.Variables["x"]);
    }

    [Fact]
    public void A_Transition_Whose_Junction_Has_No_Enabled_Branch_Is_Not_Enabled()
    {
        var (sm, _, _) = Branch(Pseudostates.Junction, withElse: false);
        var sim = sm.Start();
        Assert.Empty(sim.EnabledEvents);
        Assert.False(sim.Send("go"));
        Assert.Equal(["A"], Leaves(sim));
        Assert.Null(sim.Error);
        Assert.False(sim.Finished);
        Assert.Equal(0, sim.Variables["x"]);            // nothing fired, so the effect did not run
    }

    [Fact]
    public void A_Choice_With_No_True_Guard_And_No_Else_Stops_With_A_Clear_Error_Instead_Of_Hanging()
    {
        var (sm, _, _) = Branch(Pseudostates.Choice, withElse: false);
        // x = 5 would go High, so make the effect miss both branches.
        var go = sm.M.OfKind(ElementKind.Transition).Cast<Relationship>().Single(t => t.Name == "go");
        sm.M.Set(go, FieldKeys.Effect, "x = 1");
        var sim = sm.Start();
        Assert.True(sim.Send("go"));
        Assert.True(sim.Finished);
        Assert.NotNull(sim.Error);
        Assert.Contains("choice 'Pick'", sim.Error);
        Assert.Contains("no outgoing guard is true and there is no [else] branch", sim.Error);
        Assert.Contains("[x > 3]", sim.Error);
        Assert.Contains(sim.Log, l => l.StartsWith("✖", StringComparison.Ordinal));
        Assert.Equal(0, sim.Run());                     // nothing more to do: no endless loop
        Assert.False(sim.Send("go"));

        // The timed simulator reports the same error and ends.
        var des = new DiscreteEventSimulation(sm.M, [sm.Machine]);
        des.Start();
        des.RunUntil(1);
        des.Inject("go");
        des.RunToCompletion();
        Assert.True(des.Finished);
        Assert.Contains(des.Trace, t => t.Kind == TraceKind.Warning && t.Text.Contains("no outgoing guard is true"));
        Assert.NotNull(des.Machines.Single().Error);
    }

    [Fact]
    public void A_Loop_Of_Choices_Is_Reported_Not_Followed_Forever()
    {
        var sm = new Sm();
        var a = sm.S("A");
        sm.T(sm.Init(), a);
        var c1 = sm.P(Pseudostates.Choice, "C1");
        var c2 = sm.P(Pseudostates.Choice, "C2");
        sm.T(a, c1, "go");
        sm.T(c1, c2);
        sm.T(c2, c1);
        var sim = sm.Start();
        sim.Send("go");
        Assert.True(sim.Finished);
        Assert.Contains("does not reach a state", sim.Error);
    }

    // ================================================================== fork, join and orthogonal regions

    private static (Sm Sm, Element Par, Element A1, Element A2, Element B1, Element Join, Element Done) ForkJoin(string aTrigger = "a", string bTrigger = "b")
    {
        var sm = new Sm();
        var idle = sm.S("Idle");
        sm.T(sm.Init(), idle);
        var fork = sm.P(Pseudostates.Fork, "Split");
        sm.T(idle, fork, "start");
        var par = sm.S("Par");
        var a1 = sm.S("A1", par);
        var a2 = sm.S("A2", par);
        var b1 = sm.S("B1", par);
        sm.T(fork, a1);
        sm.T(fork, b1);
        sm.T(a1, a2, aTrigger);
        var join = sm.P(Pseudostates.Join, "Merge");
        sm.T(a2, join);
        sm.T(b1, join, bTrigger);
        var done = sm.S("Done");
        sm.T(join, done);
        return (sm, par, a1, a2, b1, join, done);
    }

    [Fact]
    public void Fork_Enters_Both_Regions_And_Join_Waits_For_All_Incoming_Transitions()
    {
        var (sm, par, _, _, _, join, _) = ForkJoin();
        var sim = sm.Start();
        Assert.Equal(2, sim.Engine.Regions(par).Count);  // A1–A2 and B1 are orthogonal (fork targets, join sources)
        sim.Send("start");
        Assert.Equal(["A1", "B1"], Leaves(sim));
        Assert.Contains(sim.Active, e => e.Name == "Par");
        Assert.Equal("Par.A1, Par.B1", sim.Configuration);
        Assert.Equal(["a", "b"], sim.EnabledEvents.Select(t => t.Name).Order());

        sim.Send("a");
        Assert.Equal(["A2", "B1"], Leaves(sim));
        Assert.True(sim.Step());                        // A2's completion transition arrives at the join
        Assert.Equal(["B1"], Leaves(sim));
        Assert.Contains(join, sim.Active);              // the waiting join is highlighted
        Assert.Contains(sim.Log, l => l.Contains("join 'Merge' waits (1/2)"));
        Assert.False(sim.Step());                       // nothing else to do until b

        sim.Send("b");
        Assert.Equal(["Done"], Leaves(sim));
        Assert.DoesNotContain(par, sim.Active);
        Assert.DoesNotContain(join, sim.Active);
        Assert.Contains(sim.Log, l => l.Contains("join 'Merge' complete (2/2)"));
    }

    [Fact]
    public void Leaving_The_Composite_Discards_Partial_Join_Arrivals()
    {
        var (sm, par, _, _, _, join, _) = ForkJoin();
        var idle = sm.M.OfKind(ElementKind.State).Single(s => s.Name == "Idle");
        sm.T(par, idle, "abort");
        var sim = sm.Start();
        sim.Send("start");
        sim.Send("a");
        sim.Step();
        Assert.Contains(join, sim.Active);
        sim.Send("abort");
        Assert.Equal(["Idle"], Leaves(sim));
        Assert.DoesNotContain(join, sim.Active);
        // A fresh round needs both arrivals again.
        sim.Send("start");
        sim.Send("b");
        Assert.Equal(["A1"], Leaves(sim));
        Assert.Contains(sim.Log, l => l.Contains("join 'Merge' waits (1/2)"));
    }

    [Fact]
    public void Two_Initial_States_Make_Orthogonal_Regions_That_React_To_One_Event_And_Complete_Together()
    {
        var sm = new Sm(("n", 0));
        var on = sm.S("On", entry: "n = n + 1");
        sm.T(sm.Init(), on);
        var x1 = sm.S("X1", on);
        var x2 = sm.S("X2", on);
        var y1 = sm.S("Y1", on);
        var y2 = sm.S("Y2", on);
        sm.T(sm.Init(on), x1);
        sm.T(sm.Init(on), y1);
        sm.T(x1, x2, "tick");
        sm.T(y1, y2, "tick");
        sm.T(x2, sm.Final(on), "stop");
        sm.T(y2, sm.Final(on), "stop");
        var off = sm.S("Off");
        sm.T(on, off);                                   // completion transition of On

        var sim = sm.Start();
        Assert.Equal(["X1", "Y1"], Leaves(sim));
        Assert.True(sim.Send("tick"));                   // one event, a transition in each region
        Assert.Equal(["X2", "Y2"], Leaves(sim));
        Assert.False(sim.Step());                        // On is not complete yet
        sim.Send("stop");
        Assert.Contains(sim.Log, l => l.Contains("On completed"));
        Assert.True(sim.Step());
        Assert.Equal(["Off"], Leaves(sim));
        Assert.Equal(1, sim.Variables["n"]);
    }

    [Fact]
    public void A_Final_State_Inside_A_Composite_Does_Not_End_The_Machine()
    {
        var sm = new Sm();
        var outer = sm.S("Outer");
        sm.T(sm.Init(), outer);
        var inner = sm.S("Inner", outer);
        sm.T(sm.Init(outer), inner);
        sm.T(inner, sm.Final(outer), "finish");
        var after = sm.S("After");
        sm.T(outer, after);
        sm.T(after, sm.Final(), "end");
        var sim = sm.Start();
        sim.Send("finish");
        Assert.False(sim.Finished);
        Assert.True(sim.Step());
        Assert.Equal(["After"], Leaves(sim));
        sim.Send("end");
        Assert.True(sim.Finished);
        Assert.Null(sim.Error);
        Assert.Contains(sim.Log, l => l.Contains("■ Final state reached"));
    }

    [Fact]
    public void Timed_Fork_And_Join_Wait_For_The_Slower_Region()
    {
        var (sm, _, _, a2, b1, _, done) = ForkJoin(aTrigger: "after(2s)", bTrigger: "after(5s)");
        // A2 leaves for the join 1 s after it is entered.
        var a2ToJoin = sm.M.Outgoing(a2, ElementKind.Transition).Single();
        sm.M.Rename(a2ToJoin, "after(1s)");
        var idle = sm.M.OfKind(ElementKind.State).Single(s => s.Name == "Idle");
        sm.M.Rename(sm.M.Outgoing(idle, ElementKind.Transition).Single(), "after(10ms)");
        sm.T(done, sm.Final(), "after(1s)");
        var sim = new DiscreteEventSimulation(sm.M, [sm.Machine]);
        sim.Start();
        sim.RunUntil(1);
        Assert.Equal(["A1", "B1"], sim.Machines.Single().ActiveLeaves.Select(e => e.Name));
        sim.RunToCompletion();
        Assert.True(sim.Finished);
        var wait = sim.Trace.Single(t => t.Text.Contains("join 'Merge' waits (1/2)"));
        Assert.Equal(3.01, wait.Time, 6);                // A1 2 s + A2 1 s after the fork at 10 ms
        var complete = sim.Trace.Single(t => t.Text.Contains("join 'Merge' complete"));
        Assert.Equal(5.01, complete.Time, 6);            // B1's 5 s decide
        var a1Span = sim.Spans.Single(s => s.Element.Name == "A1");
        var b1Span = sim.Spans.Single(s => s.Element.Name == "B1");
        Assert.Equal((0.01, 2.01), (Math.Round(a1Span.Start, 6), Math.Round(a1Span.End, 6)));
        Assert.Equal((0.01, 5.01), (Math.Round(b1Span.Start, 6), Math.Round(b1Span.End, 6)));
        Assert.NotEqual(a1Span.Row, b1Span.Row);          // one timeline row per orthogonal region
        var doneSpan = sim.Spans.Single(s => s.Element.Name == "Done");
        Assert.Equal(5.01, doneSpan.Start, 6);
        Assert.Equal(6.01, sim.Clock, 6);
        Assert.DoesNotContain(sim.Trace, t => t.Kind == TraceKind.Warning);
        _ = b1;
    }

    // ================================================================== history

    private static (Sm Sm, Element Work) HistoryMachine(string historyKind, bool withDefault = false)
    {
        var sm = new Sm(("entries", 0));
        var work = sm.S("Work", entry: "entries = entries + 1");
        var paused = sm.S("Paused");
        var start = sm.S("Start");
        sm.T(sm.Init(), start);
        sm.T(start, work, "begin");
        var w1 = sm.S("W1", work);
        var w2 = sm.S("W2", work);
        sm.T(sm.Init(work), w1);
        sm.T(w1, w2, "next");
        var w2a = sm.S("W2a", w2);
        var w2b = sm.S("W2b", w2);
        sm.T(sm.Init(w2), w2a);
        sm.T(w2a, w2b, "deeper");
        var h = sm.P(historyKind, "H", work);
        if (withDefault) sm.T(h, w2);
        sm.T(work, paused, "pause");
        sm.T(paused, h, "resume");
        sm.T(start, h, "jump");
        return (sm, work);
    }

    [Fact]
    public void Shallow_History_Restores_The_Last_Direct_Substate_With_Default_Entry_Below_It()
    {
        var (sm, work) = HistoryMachine(Pseudostates.ShallowHistory);
        var sim = sm.Start();
        sim.Send("begin");
        sim.Send("next");
        sim.Send("deeper");
        Assert.Equal(["W2b"], Leaves(sim));
        sim.Send("pause");
        Assert.Equal(["Paused"], Leaves(sim));
        Assert.Equal(["W2"], sim.Engine.RecordedHistory(work).Select(e => e.Name));
        sim.Send("resume");
        Assert.Equal(["W2a"], Leaves(sim));             // W2 restored, its own substate by default entry
        Assert.Equal(2, sim.Variables["entries"]);       // Work's entry behavior ran again on re-entry
        Assert.Contains(sim.Log, l => l.Contains("shallow history of Work restores Work.W2"));
    }

    [Fact]
    public void Deep_History_Restores_The_Whole_Nested_Configuration()
    {
        var (sm, work) = HistoryMachine(Pseudostates.DeepHistory);
        var sim = sm.Start();
        sim.Send("begin");
        sim.Send("next");
        sim.Send("deeper");
        sim.Send("pause");
        Assert.Equal(["W2b"], sim.Engine.RecordedHistory(work, deep: true).Select(e => e.Name));
        sim.Send("resume");
        Assert.Equal(["W2b"], Leaves(sim));
        Assert.Equal("Work.W2.W2b", sim.Configuration);
        // History survives several rounds.
        sim.Send("pause");
        sim.Send("resume");
        Assert.Equal(["W2b"], Leaves(sim));
    }

    [Fact]
    public void History_Without_A_Previous_Visit_Uses_Its_Default_Transition_Else_The_Initial_State()
    {
        var (withDefault, _) = HistoryMachine(Pseudostates.ShallowHistory, withDefault: true);
        var sim = withDefault.Start();
        sim.Send("jump");
        Assert.Equal(["W2a"], Leaves(sim));
        Assert.Contains(sim.Log, l => l.Contains("no history of Work yet"));

        var (noDefault, _) = HistoryMachine(Pseudostates.DeepHistory);
        sim = noDefault.Start();
        sim.Send("jump");
        Assert.Equal(["W1"], Leaves(sim));
    }

    // ================================================================== entry and exit points, terminate, nesting

    [Fact]
    public void Entry_And_Exit_Points_Enter_And_Leave_A_Composite_Through_Their_Transitions()
    {
        var sm = new Sm(("inside", 0), ("left", 0));
        var idle = sm.S("Idle");
        sm.T(sm.Init(), idle);
        var c = sm.S("Cycle", entry: "inside = inside + 1", exit: "left = left + 1");
        var first = sm.S("First", c);
        var second = sm.S("Second", c);
        sm.T(sm.Init(c), first);
        sm.T(first, second, "advance");
        var ep = sm.P(Pseudostates.EntryPoint, "resumeAt", c);
        var xp = sm.P(Pseudostates.ExitPoint, "abort", c);
        sm.T(ep, second, effect: "inside = inside + 10");
        sm.T(second, xp, "fail");
        var error = sm.S("Error");
        sm.T(xp, error, effect: "left = left + 10");
        sm.T(idle, ep, "resume");
        sm.T(idle, c, "go");
        sm.T(error, idle, "reset");

        var sim = sm.Start();
        sim.Send("resume");
        Assert.Equal(["Second"], Leaves(sim));          // not First, the default
        Assert.Equal(11, sim.Variables["inside"]);       // Cycle's entry, then the entry point's effect
        sim.Send("fail");
        Assert.Equal(["Error"], Leaves(sim));
        Assert.Equal(11, sim.Variables["left"]);         // Cycle exited (exit behavior), then the exit point's transition
        Assert.DoesNotContain(c, sim.Active);

        sim.Send("reset");
        sim.Send("go");
        Assert.Equal(["First"], Leaves(sim));           // a plain transition still uses the default entry
    }

    [Fact]
    public void Terminate_Ends_The_Run_Without_Exit_Behaviors()
    {
        var sm = new Sm(("left", 0), ("killed", 0));
        var a = sm.S("A", exit: "left = 1");
        sm.T(sm.Init(), a);
        sm.T(a, sm.S("B"), "next");
        sm.T(a, sm.P(Pseudostates.Terminate, "Kill"), "kill", effect: "killed = 1");
        var sim = sm.Start();
        Assert.True(sim.Send("kill"));
        Assert.True(sim.Finished);
        Assert.True(sim.Terminated);
        Assert.Null(sim.Error);
        Assert.Equal(1, sim.Variables["killed"]);
        Assert.Equal(0, sim.Variables["left"]);
        Assert.False(sim.Send("next"));
        Assert.Contains(sim.Log, l => l.Contains("terminated at 'Kill'"));
    }

    [Fact]
    public void Nested_Composites_Enter_Outside_In_Exit_Inside_Out_And_Inner_Transitions_Win()
    {
        var sm = new Sm(("order", 0));
        var outer = sm.S("Outer", entry: "order = order * 10 + 1", exit: "order = order * 10 + 4");
        var mid = sm.S("Mid", outer, entry: "order = order * 10 + 2", exit: "order = order * 10 + 3");
        var leaf = sm.S("Leaf", mid);
        var leaf2 = sm.S("Leaf2", mid);
        sm.T(sm.Init(), outer);
        sm.T(sm.Init(outer), mid);
        sm.T(sm.Init(mid), leaf);
        sm.T(leaf, leaf2, "e");
        var other = sm.S("Other");
        sm.T(outer, other, "e");
        sm.T(outer, other, "out");
        var sim = sm.Start();
        Assert.Equal(12, sim.Variables["order"]);       // Outer then Mid
        Assert.Equal("Outer.Mid.Leaf", sim.Configuration);
        Assert.Equal(["Outer", "Mid", "Leaf"], sim.Active.Select(e => e.Name));
        sim.Send("e");
        Assert.Equal(["Leaf2"], Leaves(sim));           // the inner transition has priority over Outer's
        sim.Send("out");
        Assert.Equal(["Other"], Leaves(sim));
        Assert.Equal(1234, sim.Variables["order"]);      // Mid exited before Outer
        var exits = sim.Log.Where(l => l.StartsWith("← ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["← Leaf", "← Leaf2", "← Mid", "← Outer"], exits);
    }

    [Fact]
    public void Regions_Merge_Unconnected_States_Into_The_Initial_Region_Unless_A_Fork_Separates_Them()
    {
        var sm = new Sm();
        var c = sm.S("C");
        var a = sm.S("A", c);
        var b = sm.S("B", c);
        var loose = sm.S("Loose", c);
        sm.T(sm.Init(c), a);
        sm.T(a, b);
        var engine = new StateMachineEngine(sm.M, sm.Machine, new NullHost());
        Assert.Single(engine.Regions(c));
        Assert.Contains(loose, engine.Regions(c)[0].Members);
        Assert.Empty(engine.Regions(a));                 // a simple state

        var sm2 = new Sm();
        var p = sm2.S("P");
        var x = sm2.S("X", p);
        var y = sm2.S("Y", p);
        var f = sm2.P(Pseudostates.Fork, "F");
        sm2.T(f, x);
        sm2.T(f, y);
        var e2 = new StateMachineEngine(sm2.M, sm2.Machine, new NullHost());
        Assert.Equal(2, e2.Regions(p).Count);
    }

    private sealed class NullHost : IStateMachineHost
    {
        public void Exec(string? statements, string what) { }
        public bool Guard(Relationship transition) => true;
        public void Log(MachineEventKind kind, Element? element, string text) { }
    }

    // ================================================================== activities: central buffer and data store

    private static (ModelRepository M, Element Activity, Element Buffer, Element Store) Buffers()
    {
        var m = new ModelRepository("Buffers");
        var act = m.Create(m.Root, ElementKind.Activity, "Pipeline");
        Element N(ElementKind k, string name, string? subtype = null, string? body = null, string? duration = null)
        {
            var e = m.Create(act, k, name);
            if (subtype != null) m.Set(e, FieldKeys.Subtype, subtype);
            if (body != null) m.Set(e, FieldKeys.Body, body);
            if (duration != null) m.Set(e, ExtFieldKeys.Duration, duration);
            return e;
        }
        void F(Element a, Element b) => m.Connect(ElementKind.ObjectFlow, a, b);
        var init = N(ElementKind.InitialNode, "start");
        var fork = N(ElementKind.ForkNode, "split");
        var p1 = N(ElementKind.Action, "Produce 1", body: "made = made + 1", duration: "1s");
        var p2 = N(ElementKind.Action, "Produce 2", body: "made = made + 1", duration: "2s");
        var buffer = N(ElementKind.ObjectNode, "Parts", Subtypes.CentralBuffer);
        var c1 = N(ElementKind.Action, "Consume 1", body: "c1 = c1 + 1");
        var c2 = N(ElementKind.Action, "Consume 2", body: "c2 = c2 + 1");
        var store = N(ElementKind.ObjectNode, "Log", Subtypes.DataStore);
        var r1 = N(ElementKind.Action, "Read A", body: "ra = ra + 1");
        var r2 = N(ElementKind.Action, "Read B", body: "rb = rb + 1");
        m.Connect(ElementKind.ControlFlow, init, fork);
        m.Connect(ElementKind.ControlFlow, fork, p1);
        m.Connect(ElementKind.ControlFlow, fork, p2);
        F(p1, buffer);
        F(p2, buffer);
        F(buffer, c1);
        F(buffer, c2);
        F(c1, store);
        F(store, r1);
        F(store, r2);
        return (m, act, buffer, store);
    }

    [Fact]
    public void A_Central_Buffer_Gives_Each_Token_To_One_Consumer_And_A_Data_Store_Copies_And_Keeps_Data()
    {
        var (m, act, buffer, store) = Buffers();
        var sim = new ActivitySimulator(m, act);
        sim.Start();
        sim.Run();
        Assert.True(sim.Finished);
        Assert.Equal(2, sim.Variables["made"]);
        Assert.Equal(1, sim.Variables["c1"]);            // two tokens, one to each consumer, not two to each
        Assert.Equal(1, sim.Variables["c2"]);
        Assert.Equal(1, sim.Variables["ra"]);            // every reader gets a copy
        Assert.Equal(1, sim.Variables["rb"]);
        Assert.Equal(0, sim.Held(buffer));
        Assert.Equal(1, sim.Held(store));                // the data store keeps its data
        Assert.Contains(store, sim.Active);
        Assert.Contains(sim.Log, l => l.Contains("data store Log keeps its data"));
    }

    [Fact]
    public void A_Central_Buffer_Holds_Tokens_Until_A_Guard_Lets_Them_Through()
    {
        var (m, act, buffer, _) = Buffers();
        foreach (var f in m.Outgoing(buffer).ToList()) m.Set(f, FieldKeys.Guard, "made > 5");
        var sim = new ActivitySimulator(m, act);
        sim.Start();
        sim.Run();
        Assert.True(sim.Finished);
        Assert.Equal(2, sim.Held(buffer));
        Assert.Contains(buffer, sim.Active);
        Assert.Contains(sim.Log, l => l.Contains("central buffer Parts still holds 2 tokens"));
    }

    [Fact]
    public void Timed_Activities_Buffer_And_Store_Tokens_Too()
    {
        var (m, act, buffer, store) = Buffers();
        var sim = new DiscreteEventSimulation(m, [act]);
        sim.Start();
        sim.RunToCompletion();
        Assert.True(sim.Finished);
        Assert.Equal(2, sim.Clock, 9);
        Assert.Equal(1, sim.Variables["c1"]);
        Assert.Equal(1, sim.Variables["c2"]);
        Assert.Equal(1, sim.Variables["ra"]);
        Assert.Equal(1, sim.Variables["rb"]);
        Assert.Contains(sim.Trace, t => t.Text.Contains("▣ Parts buffers a token"));
        Assert.Contains(sim.Trace, t => t.Text.Contains("data store Log keeps its data"));
        Assert.Contains(store, sim.Active);
        Assert.DoesNotContain(buffer, sim.Active);
    }
}
