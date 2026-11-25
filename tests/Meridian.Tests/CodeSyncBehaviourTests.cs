using Meridian.Core.CodeSync;
using Meridian.Core.CodeSync.Conformance;
using Meridian.Core.Model;
using Meridian.Serialization.CodeSync;

namespace Meridian.Tests;

/// <summary>Behaviour from code: method bodies read as control-flow trees, and the activities and sequences made from them.</summary>
public class CodeSyncBehaviourTests
{
    internal const string Fleet = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading.Tasks;

        namespace Sat.Power
        {
            public class Battery
            {
                public double Level { get; set; }
                public bool IsLow() => Level < 0.2;
                public void Charge(double watts) { Level += watts; }
                public async Task<double> MeasureAsync() { await Task.Delay(1); return Level; }
            }

            public interface ICharger { void Charge(Battery b); }

            public class SolarCharger : ICharger
            {
                public void Charge(Battery b) { if (b.IsLow()) b.Charge(10); else b.Charge(1); }
            }
        }

        namespace Sat.Control
        {
            using Sat.Power;

            public class PowerController
            {
                private readonly List<Battery> _batteries = new();
                private readonly ICharger _charger = new SolarCharger();
                private Battery Main => _batteries[0];

                public async Task<int> BalanceAsync(int rounds)
                {
                    var charged = 0;
                    for (var i = 0; i < rounds; i++)
                    {
                        foreach (var b in _batteries.Where(x => x.IsLow()))
                        {
                            if (b.Level < 0.05)
                            {
                                _charger.Charge(b);
                                charged++;
                            }
                            else if (b.Level < 0.1) b.Charge(5);
                            else continue;
                        }
                    }
                    try
                    {
                        var level = await Main.MeasureAsync();
                        switch (charged)
                        {
                            case 0: Log("idle"); break;
                            case 1:
                            case 2: Log("some"); break;
                            default: throw new InvalidOperationException("too many");
                        }
                    }
                    catch (TimeoutException ex) { Log(ex.Message); return -1; }
                    finally { Log("done"); }
                    int Twice(int x) => x * 2;
                    return Twice(charged);
                }

                public void Log(string text) { Console.WriteLine(text); }

                public static PowerController Create() => new PowerController();
            }
        }
        """;

    private static CodeModel ReadCs(string src) => new CSharpCodeReader().Read([new CodeFile("src/Fleet.cs", src)]);

    private static CodeMember Method(CodeModel m, string type, string name) =>
        m.Types.Single(t => t.Symbol == type).Members.First(x => x.Name == name && x.Kind == CodeMemberKind.Method);

    // ================================================================ C# reader

    [Fact]
    public void CSharp_body_tree_has_loops_branches_try_and_resolved_calls()
    {
        var m = ReadCs(Fleet);
        var body = Method(m, "Sat.Control.PowerController", "BalanceAsync").Body!;
        var s = body.Statements;
        Assert.Equal(CodeStatementKind.Assign, s[0].Kind);
        Assert.Equal("charged", s[0].Text);

        var loop = s[1];
        Assert.Equal(CodeStatementKind.Loop, loop.Kind);
        Assert.Equal(CodeLoopKind.For, loop.Loop);
        var inner = Assert.Single(loop.Body);
        Assert.Equal(CodeLoopKind.ForEach, inner.Loop);
        Assert.Contains("b in _batteries.Where(", inner.Text);
        // The LINQ lambda stays in the statement; its call of a scanned type is listed.
        var linq = Assert.Single(inner.Calls);
        Assert.Equal("IsLow", linq.Method);
        Assert.Equal("Sat.Power.Battery", linq.ReceiverType);

        var ifs = Assert.Single(inner.Body);
        Assert.Equal(CodeStatementKind.If, ifs.Kind);
        Assert.Equal("b.Level < 0.05", ifs.Text);
        var charge = ifs.Body[0];
        Assert.Equal(CodeStatementKind.Call, charge.Kind);
        Assert.Equal("_charger", charge.Receiver);
        Assert.Equal("Sat.Power.ICharger", charge.ReceiverType);
        Assert.Equal("Sat.Power.ICharger.Charge(Battery)", charge.Operation);
        var elseIf = Assert.Single(ifs.Else);
        Assert.Equal(CodeStatementKind.If, elseIf.Kind);
        Assert.Equal("Sat.Power.Battery.Charge(double)", elseIf.Body[0].Operation);
        Assert.Equal("continue", Assert.Single(elseIf.Else).Text);

        var tr = s[2];
        Assert.Equal(CodeStatementKind.Try, tr.Kind);
        var measure = tr.Body[0];
        Assert.True(measure.IsAwait);
        Assert.Equal("Main", measure.Receiver);   // a property of a scanned type
        Assert.Equal("Sat.Power.Battery.MeasureAsync()", measure.Operation);
        var sw = tr.Body[1];
        Assert.Equal(CodeStatementKind.Switch, sw.Kind);
        Assert.Equal(["0", "1, 2", "default"], sw.Branches.Select(b => b.Label));
        Assert.Equal("this", sw.Branches[0].Body.Single().Receiver);   // the trailing break is not a step
        Assert.Equal(CodeStatementKind.Throw, sw.Branches[2].Body.Single().Kind);
        Assert.Equal("InvalidOperationException", sw.Branches[2].Body.Single().Method);
        Assert.Equal("TimeoutException", Assert.Single(tr.Branches).Label);
        Assert.Equal(CodeStatementKind.Return, tr.Branches[0].Body[1].Kind);
        Assert.Single(tr.Finally);

        // The local function is not a statement; its call is (unresolved).
        var ret = s[^1];
        Assert.Equal(CodeStatementKind.Return, ret.Kind);
        Assert.Equal("Twice", ret.Method);
        Assert.Null(ret.ReceiverType);
        Assert.True(ret.Line > tr.Line);

        // Static, expression-bodied and interface implementations.
        var create = Method(m, "Sat.Control.PowerController", "Create").Body!.Statements.Single();
        Assert.Equal(CodeStatementKind.Return, create.Kind);
        Assert.Contains("new PowerController()", create.Text);
        Assert.Null(Method(m, "Sat.Power.ICharger", "Charge").Body);
        Assert.Equal(["Sat.Power.ICharger"], m.Types.Single(t => t.Symbol == "Sat.Power.SolarCharger").Interfaces);
    }

    [Fact]
    public void Body_trees_are_capped_and_clipped()
    {
        var lines = string.Join("\n", Enumerable.Range(0, 400).Select(i => $"        Do{i % 3}();"));
        var src = $$"""
            public class Big
            {
                void Do0() {} void Do1() {} void Do2() {}
                public void Run()
                {
            {{lines}}
                    var text = "{{new string('x', 200)}}";
                }
            }
            """;
        var body = ReadCs(src).Types.Single().Members.Single(x => x.Name == "Run").Body!;
        Assert.Equal(CodeBodies.MaxStatements, CodeBodies.Count(body.Statements));
        Assert.Equal(101, body.Omitted);
        Assert.True(CodeBodies.Clip(new string('a', 200)).Length == CodeBodies.MaxText);
        Assert.EndsWith("…", CodeBodies.Clip(new string('a', 200)));
    }

    [Fact]
    public void Body_trees_are_deterministic_and_survive_the_stored_json()
    {
        var a = ReadCs(Fleet);
        var b = ReadCs(Fleet);
        Assert.Equal(CodeModelJson.Serialize(a), CodeModelJson.Serialize(b));
        var back = CodeModelJson.Unpack(CodeModelJson.Pack(a))!;
        Assert.Equal(CodeModelJson.Serialize(a), CodeModelJson.Serialize(back));
        var body = Method(back, "Sat.Control.PowerController", "BalanceAsync").Body!;
        Assert.Equal(CodeBodies.Count(Method(a, "Sat.Control.PowerController", "BalanceAsync").Body!.Statements), CodeBodies.Count(body.Statements));
        Assert.Empty(body.Statements[0].Branches);   // lists left out of the JSON come back empty

        // Code stored before bodies existed reads back without them.
        var old = """{"Language":"csharp","Types":[{"Symbol":"A","Name":"A","Namespace":"","Kind":0,"File":"a.cs","Line":1,"BaseTypes":[],"Interfaces":[],"Members":[{"Name":"Run","Kind":2,"TypeName":"void","IsCollection":false,"Visibility":"public"}],"Dependencies":[]}],"Warnings":[]}""";
        var m = CodeModelJson.Deserialize(old)!;
        Assert.Null(m.Types[0].Members[0].Body);
        Assert.Empty(m.Types[0].Members[0].Attributes);
        Assert.Empty(m.Types[0].ExternalBases);
    }

    // ================================================================ hand-parsed readers

    [Fact]
    public void TypeScript_body_tree_resolves_fields_parameters_and_locals()
    {
        var m = new TypeScriptCodeReader().Read([
            new CodeFile("src/battery.ts", "export class Battery { level = 0; isLow(): boolean { return this.level < 0.2; } charge(w: number): void { this.level += w } }"),
            new CodeFile("src/controller.ts", """
                import { Battery } from "./battery";
                export class Controller {
                  private main: Battery = new Battery();
                  async balance(spare: Battery, rounds: number): Promise<number> {
                    let charged = 0
                    for (const b of [this.main, spare]) {
                      if (b.isLow()) {
                        b.charge(5);
                        charged++;
                      } else if (rounds > 2) {
                        continue;
                      } else {
                        spare.charge(1);
                      }
                    }
                    const extra = new Battery();
                    try {
                      await extra.charge(1);
                    } catch (e) {
                      console.log(e);
                      throw new Error("failed");
                    } finally {
                      this.log("done");
                    }
                    switch (charged) {
                      case 0: this.log("none"); break;
                      default: return charged;
                    }
                    return -1;
                  }
                  log(text: string) { console.log(text); }
                }
                """)]);
        var body = m.Types.Single(t => t.Name == "Controller").Members.Single(x => x.Name == "balance").Body!;
        var s = body.Statements;
        Assert.Equal(CodeStatementKind.Assign, s[0].Kind);
        Assert.Equal(CodeLoopKind.ForEach, s[1].Loop);
        Assert.Equal("b in [this.main, spare]", s[1].Text);
        var ifs = Assert.Single(s[1].Body);
        Assert.Equal("b.isLow()", ifs.Text);
        Assert.Empty(ifs.Calls);
        Assert.Null(ifs.Body[0].ReceiverType);   // the loop variable's type is not known…
        Assert.Equal("charge", ifs.Body[0].Method);
        Assert.Equal(CodeStatementKind.If, ifs.Else.Single().Kind);
        Assert.Equal("src.Battery", ifs.Else.Single().Else.Single().ReceiverType);   // …a parameter's is
        Assert.Equal(CodeStatementKind.Create, s[2].Kind is CodeStatementKind.Assign ? CodeStatementKind.Create : s[2].Kind);
        var tr = s[3];
        Assert.Equal(CodeStatementKind.Try, tr.Kind);
        Assert.True(tr.Body[0].IsAwait);
        Assert.Equal("src.Battery", tr.Body[0].ReceiverType);   // a local with "new Battery()"
        Assert.Equal("", tr.Branches.Single().Label);
        Assert.Equal("Error", tr.Branches[0].Body[1].Method);
        Assert.Equal("src.Controller", tr.Finally.Single().ReceiverType);
        Assert.Equal(["0", "default"], s[4].Branches.Select(b => b.Label));
        Assert.Single(s[4].Branches[0].Body);   // break dropped
        Assert.Equal(CodeStatementKind.Return, s[^1].Kind);
    }

    [Fact]
    public void Java_body_tree_reads_loops_switch_try_and_synchronized()
    {
        var m = new JavaCodeReader().Read([
            new CodeFile("src/main/java/sat/Battery.java", "package sat; public class Battery { public boolean isLow() { return true; } public void charge(int w) { } }"),
            new CodeFile("src/main/java/sat/Controller.java", """
                package sat;
                import java.util.List;
                public class Controller {
                    private final List<Battery> batteries;
                    private final Battery main = new Battery();
                    @GetMapping("/balance")
                    public int balance(Battery spare) {
                        int charged = 0;
                        for (Battery b : batteries) {
                            if (b.isLow()) { b.charge(5); charged++; }
                        }
                        while (charged < 3) { spare.charge(1); charged++; }
                        do { main.charge(1); } while (main.isLow());
                        synchronized (this) { log("locked"); }
                        try { main.charge(2); } catch (IllegalStateException | IllegalArgumentException e) { return -1; } finally { log("done"); }
                        switch (charged) { case 1: case 2: log("few"); break; default: throw new IllegalStateException("x"); }
                        return charged;
                    }
                    void log(String s) { System.out.println(s); }
                }
                """)]);
        var balance = m.Types.Single(t => t.Name == "Controller").Members.Single(x => x.Name == "balance");
        Assert.Contains("GetMapping", balance.Attributes);
        var s = balance.Body!.Statements;
        Assert.Equal(["Assign", "Loop", "Loop", "Loop", "Lock", "Try", "Switch", "Return"], s.Select(x => x.Kind.ToString()));
        Assert.Equal("b in batteries", s[1].Text);
        Assert.Equal("sat.Battery", s[1].Body.Single().Body[0].ReceiverType);   // "Battery b" of the for-each
        Assert.Equal("sat.Battery", s[2].Body[0].ReceiverType);               // a parameter
        Assert.Equal(CodeLoopKind.Do, s[3].Loop);
        Assert.Equal("sat.Battery", s[3].Body[0].ReceiverType);               // a field
        Assert.Equal("sat.Controller", s[4].Body[0].ReceiverType);            // log(...) is this.log
        Assert.Equal("IllegalStateException | IllegalArgumentException", s[5].Branches.Single().Label);
        Assert.Equal(["1, 2", "default"], s[6].Branches.Select(b => b.Label));
        Assert.Equal("IllegalStateException", s[6].Branches[1].Body.Single().Method);
    }

    [Fact]
    public void Python_body_tree_reads_blocks_by_indentation()
    {
        var m = new PythonCodeReader().Read([
            new CodeFile("lander/battery.py", "class Battery:\n    def is_low(self) -> bool:\n        return True\n    def charge(self, w: int) -> None:\n        pass\n"),
            new CodeFile("lander/controller.py", """
                from lander.battery import Battery

                class Controller:
                    def __init__(self) -> None:
                        self.main = Battery()

                    async def balance(self, spare: Battery, rounds: int) -> int:
                        charged = 0
                        for b in [self.main, spare]:
                            if b.is_low():
                                spare.charge(5)
                            elif rounds > 2:
                                continue
                            else:
                                self.main.charge(1)
                        extra = Battery()
                        try:
                            await extra.charge(1)
                        except (IOError, ValueError) as e:
                            raise RuntimeError("failed")
                        finally:
                            self.log("done")
                        with open("x") as f: f.write("y")
                        while charged < 3: charged += 1
                        match charged:
                            case 0:
                                self.log("none")
                            case _:
                                return charged
                        return -1

                    def log(self, text: str) -> None:
                        print(text)
                """)]);
        var s = m.Types.Single(t => t.Name == "Controller").Members.Single(x => x.Name == "balance").Body!.Statements;
        Assert.Equal(["Assign", "Loop", "Assign", "Try", "Using", "Loop", "Switch", "Return"], s.Select(x => x.Kind.ToString()));
        var ifs = s[1].Body.Single();
        Assert.Equal("b.is_low()", ifs.Text);
        Assert.Equal("lander.battery.Battery", ifs.Body.Single().ReceiverType);   // a parameter
        var elif = ifs.Else.Single();
        Assert.Equal("rounds > 2", elif.Text);
        Assert.Equal("lander.battery.Battery", elif.Else.Single().ReceiverType);   // self.main (an attribute)
        Assert.Equal("lander.battery.Battery", s[3].Body.Single().ReceiverType);    // extra = Battery()
        Assert.True(s[3].Body.Single().IsAwait);
        Assert.Equal("IOError, ValueError", s[3].Branches.Single().Label);
        Assert.Equal("RuntimeError", s[3].Branches[0].Body.Single().Method);
        Assert.Equal("lander.controller.Controller", s[3].Finally.Single().ReceiverType);
        Assert.Single(s[4].Body);   // the single-line with body
        Assert.Equal(["0", "default"], s[6].Branches.Select(b => b.Label));
    }

    // ================================================================ mapping

    private static readonly CodeSyncOptions Options = new() { RepoId = "repo", Commit = "c1", Behaviour = CodeBehaviourMode.OnDemand };

    private static (ModelRepository M, CodeModel Code) Synced(string src, CodeSyncOptions? o = null)
    {
        var model = new ModelRepository();
        var code = ReadCs(src);
        CodeModelMapper.Apply(model, code, o ?? Options);
        return (model, code);
    }

    private static Element Op(ModelRepository m, string symbol) => m.AllElements.Single(e => e.Get(CodeFieldKeys.Symbol) == symbol);

    [Fact]
    public void Activity_has_decisions_loops_guards_and_finals()
    {
        var (m, code) = Synced(Fleet);
        var op = Op(m, "Sat.Control.PowerController.BalanceAsync(int)");
        var (d, msg) = CodeBehaviourMapper.Generate(m, code, op, sequence: false, Options);
        Assert.NotNull(d);
        var act = d!.Owner!;
        Assert.Equal(ElementKind.Activity, act.Kind);
        Assert.Equal("act:Sat.Control.PowerController.BalanceAsync(int)", act.Get(CodeFieldKeys.Symbol));
        Assert.Equal(op.Id, act.Get(CodeBehaviourMapper.SpecificationKey));
        Assert.Equal("Behaviour", act.Owner!.Name);
        Assert.Equal("Control", act.Owner!.Owner!.Name);

        var nodes = act.OwnedElements.Where(e => e is not Diagram and not Relationship).ToList();
        Assert.Single(nodes, n => n.Kind == ElementKind.InitialNode);
        // for, foreach, if/else-if, switch, try-catch decisions and their merges.
        Assert.True(nodes.Count(n => n.Kind == ElementKind.DecisionNode) >= 7);
        var finals = nodes.Where(n => n.Kind == ElementKind.ActivityFinalNode).ToList();
        Assert.Contains(finals, f => f.Stereotypes.Contains("exception") && f.Name == "Fail: invalid operation");
        Assert.True(finals.Count >= 3);   // return -1, return Twice(charged), the throw
        var flows = m.Relationships.Where(r => r.Kind == ElementKind.ControlFlow && ReferenceEquals(r.Source.Owner, act)).ToList();
        var guards = flows.Select(f => f.Get(FieldKeys.Guard)).Where(g => g.Length > 0).ToList();
        Assert.Contains("level is less than 0.05", guards);
        Assert.Contains("level is less than 0.1", guards);
        Assert.Contains("otherwise", guards);
        Assert.Contains("no: timeout", guards);
        Assert.Contains("next one", guards);
        Assert.Contains("all done", guards);
        Assert.Contains("1 or 2", guards);
        // Decisions ask their question in words and keep the code they come from.
        var decisions = nodes.Where(n => n.Kind == ElementKind.DecisionNode && n.Name.Length > 0).ToList();
        Assert.Contains(decisions, n => n.Name == "More batteries?" && n.Documentation.StartsWith("foreach (b in _batteries.Where(", StringComparison.Ordinal));
        Assert.Contains(decisions, n => n.Name == "Is i less than rounds?" && n.Documentation.StartsWith("for (var i = 0; i < rounds; i++)", StringComparison.Ordinal));
        Assert.Contains(decisions, n => n.Name == "What is charged?");
        Assert.Contains(decisions, n => n.Name == "Which case applies?" && n.Documentation.StartsWith("if (b.Level < 0.05) / else if (b.Level < 0.1)", StringComparison.Ordinal));
        Assert.Contains(decisions, n => n.Name == "Did it work?");
        // The call action is a step in words, pointing at the called operation; the call as written is its documentation.
        var charge = nodes.Single(n => n.Documentation == "_charger.Charge()");
        Assert.Equal("Charge charger", charge.Name);
        Assert.Equal(Op(m, "Sat.Power.ICharger.Charge(Battery)").Id, charge.Get(CodeActivityPlanner.OperationField));
        Assert.True(nodes.Count == d.Nodes.Count, string.Join(" | ", nodes.Where(n => d.NodeFor(n) == null).Select(n => $"{n.Kind}:{n.Name}:{n.Get(CodeBehaviourMapper.NodeKey)}")));
        Assert.Equal(flows.Count, d.Edges.Count);
        // Top to bottom: the initial node is above everything.
        var initial = d.NodeFor(nodes.Single(n => n.Kind == ElementKind.InitialNode))!;
        Assert.All(d.Nodes.Where(n => n != initial), n => Assert.True(n.Bounds.Y > initial.Bounds.Y));
        Assert.Contains("generated", msg);
    }

    [Fact]
    public void Sequence_has_lifelines_messages_fragments_and_operands()
    {
        var (m, code) = Synced(Fleet);
        var (d, _) = CodeBehaviourMapper.Generate(m, code, Op(m, "Sat.Control.PowerController.BalanceAsync(int)"), sequence: true, Options);
        var seq = d!.Owner!;
        Assert.Equal(ElementKind.Interaction, seq.Kind);
        var lifelines = seq.OwnedElements.Where(e => e.Kind == ElementKind.Lifeline).Select(l => l.Name).ToList();
        Assert.Equal(["caller", "powerController"], lifelines.Take(2));
        Assert.Subset(lifelines.ToHashSet(), new HashSet<string> { "x", "b", "charger", "Main" });   // "x": the LINQ lambda's battery
        var charger = seq.OwnedElements.Single(e => e.Name == "charger");
        Assert.Equal(Op(m, "Sat.Power.ICharger.Charge(Battery)").Owner!.Id, charger.Get(FieldKeys.Type));

        var messages = m.Relationships.Where(r => r.Kind == ElementKind.Message && ReferenceEquals(r.Source.Owner, seq))
            .Select(r => (r, Y: d.EdgeFor(r)!.Waypoints[0].Y)).OrderBy(x => x.Y).Select(x => x.r).ToList();
        Assert.Equal("BalanceAsync(rounds)", messages[0].Name);
        Assert.Contains(messages, x => x.Name == "MeasureAsync()" && x.Get(FieldKeys.MessageSort) == "asynchCall");
        Assert.Contains(messages, x => x.Get(FieldKeys.MessageSort) == "reply");
        // Followed into the interface's only implementation: SolarCharger.Charge calls the battery.
        var intoImpl = messages.SkipWhile(x => x.Name != "Charge(b)").Skip(1).First();
        Assert.Equal("IsLow()", intoImpl.Name);

        var fragments = seq.OwnedElements.Where(e => e.Kind == ElementKind.CombinedFragment).ToList();
        var ops = fragments.Select(f => f.Get(FieldKeys.Operator)).ToList();
        Assert.Contains("loop", ops);
        Assert.Contains("alt", ops);
        Assert.Contains("break", ops);
        var alt = fragments.First(f => f.Get(FieldKeys.Operator) == "alt" && InteractionOperands.Of(f).Count == 3);
        Assert.Equal(["level is less than 0.05", "level is less than 0.1", "otherwise"], InteractionOperands.Guards(alt));
        Assert.Equal(fragments.Count + lifelines.Count, d.Nodes.Count);
    }

    private const string Chain = """
        public class A { private B b = new(); public void Go() { b.Go(); b.Stop(); } }
        public class B { private C c = new(); public void Go() { c.Go(); c.Log(); } public void Stop() { c.Stop(); } }
        public class C { private D d = new(); public void Go() { d.Go(); d.Log(); } public void Log() { } public void Stop() { d.Go(); } }
        public class D { private E e = new(); public void Go() { e.Go(); e.Go2(); } public void Log() { } }
        public class E { public void Go() { } public void Go2() { } }
        """;

    private static List<string> Lifelines(Diagram d) => d.Owner!.OwnedElements.Where(e => e.Kind == ElementKind.Lifeline).Select(e => e.Name).ToList();

    private static List<Relationship> Messages(ModelRepository m, Diagram d) =>
        m.Relationships.Where(r => r.Kind == ElementKind.Message && ReferenceEquals(r.Source.Owner, d.Owner))
            .OrderBy(r => d.EdgeFor(r)!.Waypoints[0].Y).ToList();

    private static string Note(Diagram d) => d.Owner!.OwnedElements.SingleOrDefault(e => e.Kind == ElementKind.Comment)?.Get(FieldKeys.Body) ?? "";

    [Fact]
    public void Sequences_follow_two_levels_by_default_and_skip_one_line_forwarders()
    {
        Assert.Equal((2, 25), (new CodeBehaviourLimits().SequenceDepth, new CodeBehaviourLimits().MaxMessages));
        var (m, code) = Synced(Chain);
        var (d, _) = CodeBehaviourMapper.Generate(m, code, Op(m, "A.Go()"), true, Options with { BehaviourLimits = new() });
        // A.Go (level 1) -> B.Go (level 2) -> C's calls are shown but not followed; B.Stop forwards in one line: not followed.
        Assert.Equal(["caller", "a", "b", "c"], Lifelines(d!));
        Assert.Equal(["Go()", "Go()", "Go()", "Log()", "Stop()"], Messages(m, d!).Select(r => r.Name));
        Assert.Contains("deeper than 2 levels", Note(d!));
        Assert.DoesNotContain("B.Stop", Note(d!));
    }

    [Fact]
    public void On_demand_sequences_take_the_depth_asked_for_and_keep_it_on_later_syncs()
    {
        var (m, code) = Synced(Chain);
        var (d1, _) = CodeBehaviourMapper.Generate(m, code, Op(m, "A.Go()"), true, Options, depth: 1);
        Assert.Equal(["caller", "a", "b"], Lifelines(d1!));
        Assert.Contains("deeper than 1 level", Note(d1!));

        var (d4, _) = CodeBehaviourMapper.Generate(m, code, Op(m, "A.Go()"), true, Options, depth: 4);
        Assert.Same(d1!.Owner, d4!.Owner);   // the same interaction, regenerated
        Assert.Equal(["caller", "a", "b", "c", "d", "e"], Lifelines(d4));
        Assert.Equal("4", d4.Owner!.Get(CodeBehaviourMapper.DepthKey));
        Assert.Equal("demand", d4.Owner!.Get(CodeBehaviourMapper.OriginKey));
        Assert.Equal(4, CodeBehaviourMapper.StoredDepth(d4.Owner));

        // A later sync brings it up to date at the depth it was made with (nothing changes).
        Assert.True(CodeModelMapper.Apply(m, code, Options with { Commit = "c2" }).IsEmpty);
        Assert.Equal(["caller", "a", "b", "c", "d", "e"], Lifelines(d4));
        // Without a depth, "Generate" keeps the stored one.
        CodeBehaviourMapper.Generate(m, code, Op(m, "A.Go()"), true, Options);
        Assert.Equal(6, Lifelines(d4).Count);
    }

    [Fact]
    public void Replies_only_for_results_that_are_used()
    {
        var (m, code) = Synced("""
            public class S { public int Read() { var x = 1; return x + 1; } public void Write(int v) { var y = v; y++; } }
            public class A
            {
                private S s = new();
                public int Go()
                {
                    s.Read();
                    s.Write(1);
                    var v = s.Read();
                    s.Write(v);
                    if (s.Read() > 2) s.Write(v);
                    return v;
                }
            }
            """);
        var (d, _) = CodeBehaviourMapper.Generate(m, code, Op(m, "A.Go()"), true, Options);
        var names = Messages(m, d!).Select(r => (r.Name, r.Get(FieldKeys.MessageSort))).ToList();
        Assert.Equal([
            ("Go()", "synchCall"),
            ("Read()", "synchCall"),             // result thrown away: no reply
            ("Write(1)", "synchCall"),           // void: no reply
            ("Read()", "synchCall"), ("int", "reply"),   // assigned: reply
            ("Write(v)", "synchCall"),
            ("Read()", "synchCall"), ("int", "reply"),   // in the condition: reply
            ("Write(v)", "synchCall"),
            ("int", "reply"),                    // the entry's own reply
        ], names);
    }

    [Fact]
    public void Repeated_calls_in_a_loop_and_runs_of_the_same_call_are_one_message()
    {
        var (m, code) = Synced("""
            public class B { public void Add(int i) { var x = i; x++; } public void Flush() { var y = 0; y++; } }
            public class A
            {
                private B b = new();
                public void Go(int[] xs)
                {
                    foreach (var x in xs) { b.Add(x); b.Flush(); b.Add(x + 1); b.Add(x + 2); b.Flush(); }
                    b.Add(1); b.Add(2); b.Add(3);
                    b.Flush();
                }
            }
            """);
        var (d, _) = CodeBehaviourMapper.Generate(m, code, Op(m, "A.Go(int[])"), true, Options);
        var seq = d!.Owner!;
        var loop = seq.OwnedElements.Single(e => e.Kind == ElementKind.CombinedFragment);
        Assert.Equal("loop", loop.Get(FieldKeys.Operator));
        var frame = d.NodeFor(loop)!.Bounds;
        var inLoop = Messages(m, d).Where(r => d.EdgeFor(r)!.Waypoints[0].Y is var y && y > frame.Y && y < frame.Bottom).Select(r => r.Name).ToList();
        Assert.Equal(["Add(x)", "Flush()"], inLoop);
        Assert.Equal(["Go(xs)", "Add(x)", "Flush()", "Add(…) ×3", "Flush()"], Messages(m, d).Select(r => r.Name));
    }

    [Fact]
    public void The_message_cap_ends_with_one_note_and_never_cuts_a_fragment()
    {
        var calls = string.Join(" ", Enumerable.Range(1, 15).Select(i => $"b.M{i}();"));
        var guarded = string.Join(" ", Enumerable.Range(16, 20).Select(i => $"b.M{i}();"));
        var methods = string.Join(" ", Enumerable.Range(1, 40).Select(i => $"public void M{i}() {{ }}"));
        var (m, code) = Synced($$"""
            public class B { {{methods}} }
            public class A
            {
                private B b = new();
                public void Go(bool on) { {{calls}} if (on) { {{guarded}} } b.M36(); b.M37(); }
            }
            """);
        var (d, _) = CodeBehaviourMapper.Generate(m, code, Op(m, "A.Go(bool)"), true, Options with { BehaviourLimits = new() }, depth: 2);
        // On demand the cap is 60: everything fits.
        Assert.Equal(1 + 37, Messages(m, d!).Count);
        Assert.Single(d!.Owner!.OwnedElements, e => e.Kind == ElementKind.CombinedFragment);

        // The sync's own cap (25): the 15 calls, then the opt fragment would be cut, so it goes whole into the note.
        var index = new CodeBehaviourIndex(code);
        var plan = CodeSequencePlanner.Plan(index, index.Method("A.Go(bool)")!, _ => null, new CodeBehaviourLimits());
        Assert.Equal(16, plan.Edges.Count);
        Assert.DoesNotContain(plan.Nodes, n => n.Kind == ElementKind.CombinedFragment);
        Assert.Equal("… 22 more calls (stopped at 25 messages).", plan.Nodes.Single(n => n.Kind == ElementKind.Comment).Fields[FieldKeys.Body]);

        // With nothing before it, a fragment keeps what fits rather than leaving the sequence empty.
        var (m3, code3) = Synced($$"""
            public class B { {{methods}} }
            public class A { private B b = new(); public void Go(bool on) { if (on) { {{calls}} {{guarded}} } } }
            """);
        var index3 = new CodeBehaviourIndex(code3);
        var plan3 = CodeSequencePlanner.Plan(index3, index3.Method("A.Go(bool)")!, _ => null, new CodeBehaviourLimits());
        Assert.Equal(25, plan3.Edges.Count);
        Assert.Single(plan3.Nodes, n => n.Kind == ElementKind.CombinedFragment);
        Assert.StartsWith("… 11 more calls", plan3.Nodes.Single(n => n.Kind == ElementKind.Comment).Fields[FieldKeys.Body]);
        Assert.NotNull(m3);
    }

    [Fact]
    public void Recursion_is_shown_once()
    {
        var (m, code) = Synced("public class R { public int F(int n) { if (n > 0) return F(n - 1); return 0; } }");
        var (d, _) = CodeBehaviourMapper.Generate(m, code, Op(m, "R.F(int)"), true, Options);
        Assert.Contains("Recursive calls are shown once", d!.Owner!.OwnedElements.Single(e => e.Kind == ElementKind.Comment).Get(FieldKeys.Body));
    }

    [Fact]
    public void Resync_of_the_same_code_changes_nothing_and_a_changed_body_updates_in_place_keeping_layout()
    {
        var o = Options with { Behaviour = CodeBehaviourMode.EntryPoints };
        var (m, code) = Synced(Fleet, o);
        var act = m.AllElements.Single(e => e.Get(CodeFieldKeys.Symbol) == "act:Sat.Control.PowerController.BalanceAsync(int)");
        var d = act.OwnedElements.OfType<Diagram>().Single();
        Assert.True(CodeModelMapper.Apply(m, code, o).IsEmpty);

        // Someone moves a shape.
        var moved = d.Nodes.First(n => n.Element.Documentation == "_charger.Charge()");
        m.SetBounds(d, new Dictionary<DiagramNode, Bounds> { [moved] = moved.Bounds with { X = moved.Bounds.X + 333 } });
        var where = moved.Bounds;
        var ids = act.OwnedElements.Where(e => e is not Relationship).ToDictionary(e => e.Id);

        var changedSrc = Fleet.Replace("charged++;", "charged++;\n                                Log(\"charged\");");
        var changes = CodeModelMapper.Apply(m, ReadCs(changedSrc), o with { Commit = "c2" });
        Assert.Contains(act.Id, changes.Changed);
        var log = act.OwnedElements.Single(e => e.Name == "Log" && changes.Added.Contains(e.Id));
        Assert.NotNull(d.NodeFor(log));
        Assert.Equal(where, d.NodeFor(m.Find(moved.Element.Id)!)!.Bounds);   // kept where it was put
        Assert.All(ids.Keys, id => Assert.NotNull(m.Find(id)));   // every earlier node kept its element
        Assert.Same(act, m.AllElements.Single(e => e.Get(CodeFieldKeys.Symbol) == "act:Sat.Control.PowerController.BalanceAsync(int)"));
    }

    [Fact]
    public void Entry_points_pick_controllers_and_control_flow_within_the_caps()
    {
        var src = Fleet + """

            namespace Sat.Api
            {
                using Sat.Control;
                [System.AttributeUsage(System.AttributeTargets.All)] public class HttpGetAttribute : System.Attribute { }
                [System.AttributeUsage(System.AttributeTargets.All)] public class FactAttribute : System.Attribute { }
                public class PowerApi
                {
                    private PowerController _c = new();
                    [HttpGet] public void Get() { _c.Log("x"); }
                    public void Plain() { }
                }
                public class PowerApiTests
                {
                    [Fact] public void Works() { new PowerApi().Get(); if (1 > 0) { } }
                }
            }
            """;
        var o = Options with { Behaviour = CodeBehaviourMode.EntryPoints };
        var (m, code) = Synced(src, o);
        var symbols = m.AllElements.Select(e => e.Get(CodeFieldKeys.Symbol)).Where(CodeBehaviourMapper.IsBehaviourSymbol).OrderBy(s => s).ToList();
        Assert.Contains("act:Sat.Control.PowerController.BalanceAsync(int)", symbols);
        Assert.Contains("act:Sat.Power.SolarCharger.Charge(Battery)", symbols);
        Assert.Contains("seq:Sat.Api.PowerApi.Get()", symbols);   // [HttpGet]
        Assert.DoesNotContain(symbols, s => s.Contains("Tests", StringComparison.Ordinal));
        Assert.DoesNotContain("act:Sat.Power.Battery.Charge(double)", symbols);   // no control flow
        Assert.DoesNotContain(symbols, s => s.Contains("Plain", StringComparison.Ordinal));

        // Caps: one activity per namespace, two in total.
        var tight = o with { RepoId = "other", BehaviourLimits = new() { ActivitiesPerNamespace = 1, SequencesPerNamespace = 0, MaxTotal = 2 } };
        var m2 = new ModelRepository();
        var result = CodeModelMapper.Apply(m2, code, tight);
        Assert.True(m2.AllElements.Count(e => e.Get(CodeFieldKeys.Symbol).StartsWith("act:", StringComparison.Ordinal)) <= 2);
        Assert.DoesNotContain(m2.AllElements, e => e.Get(CodeFieldKeys.Symbol).StartsWith("seq:", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("skipped by the caps", StringComparison.Ordinal));
    }

    [Fact]
    public void On_demand_and_off_generate_nothing_by_themselves_and_removed_methods_take_their_behaviour()
    {
        var (m, code) = Synced(Fleet);
        Assert.DoesNotContain(m.AllElements, e => e.Kind is ElementKind.Activity or ElementKind.Interaction);
        var op = Op(m, "Sat.Control.PowerController.BalanceAsync(int)");
        CodeBehaviourMapper.Generate(m, code, op, false, Options);
        Assert.Single(m.AllElements, e => e.Kind == ElementKind.Activity);
        Assert.True(CodeModelMapper.Apply(m, code, Options).IsEmpty);   // kept, unchanged

        var without = ReadCs(Fleet.Replace("public async Task<int> BalanceAsync", "private async Task<int> BalanceAsyncGone"));
        CodeModelMapper.Apply(m, without, Options);
        Assert.DoesNotContain(m.AllElements, e => e.Kind == ElementKind.Activity);
        Assert.DoesNotContain(m.AllElements, e => e.Name == "Behaviour");
    }

    [Fact]
    public void Behaviour_elements_stay_off_structure_diagrams()
    {
        var o = Options with { Behaviour = CodeBehaviourMode.EntryPoints };
        var m = new ModelRepository();
        var changes = CodeModelMapper.Apply(m, ReadCs(Fleet), o);
        var refresh = CodeSyncDiagrams.Refresh(m, changes, createDiagrams: true);
        Assert.DoesNotContain(refresh.Created, d => d.Owner?.Name == "Behaviour");
        Assert.DoesNotContain(m.Diagrams.Where(d => !CodeBehaviourMapper.IsGenerated(d)).SelectMany(d => d.Nodes), n => CodeBehaviourMapper.IsGenerated(n.Element));
    }

    // ================================================================ readable activities

    private static BehaviourPlan ActivityPlan(CodeModel code, string symbol, CodeBehaviourLimits? limits = null)
    {
        var index = new CodeBehaviourIndex(code);
        return CodeActivityPlanner.Plan(index, index.Method(symbol)!, _ => null, limits);
    }

    private const string Wiring = "public class B { public void AddBlock() { } public void Connect() { } public void Done() { } public void Check() { } }";

    [Fact]
    public void Runs_of_four_or_more_plain_statements_fold_into_one_action_listing_them()
    {
        var run = string.Join(" ", Enumerable.Range(1, 12).Select(i => i % 2 == 0 ? "b.AddBlock();" : "b.Connect();"));
        var src = $$"""
            {{Wiring}}
            public class A { private B b = new(); public void Go(bool on) { if (on) { b.Done(); b.Check(); b.Done(); } {{run}} } }
            """;
        var plan = ActivityPlan(ReadCs(src), "A.Go(bool)");
        var fold = plan.Nodes.Single(n => n.Name.StartsWith("12 steps", StringComparison.Ordinal));
        Assert.Equal("12 steps: connect b, add block to b", fold.Name);
        Assert.Equal(12, fold.Fields[FieldKeys.Documentation].Split('\n').Length);
        Assert.StartsWith("b.Connect()\nb.AddBlock()", fold.Fields[FieldKeys.Documentation]);
        Assert.Equal(2, plan.Nodes.Count(n => n.Name == "Done b" && n.Fields[FieldKeys.Documentation] == "b.Done()"));   // a run of three stays as it is
        Assert.DoesNotContain(CodeActivityPlanner.SimplifiedNote, plan.Documentation);

        // In the model: the summary action carries the list as its documentation.
        var (m, code) = Synced(src);
        var (d, _) = CodeBehaviourMapper.Generate(m, code, Op(m, "A.Go(bool)"), false, Options);
        var action = d!.Owner!.OwnedElements.Single(e => e.Name.StartsWith("12 steps", StringComparison.Ordinal));
        Assert.Contains("b.AddBlock()", action.Documentation);
    }

    [Fact]
    public void Large_activities_keep_their_control_flow_and_fold_leaf_blocks_to_fit_the_shape_cap()
    {
        var arms = string.Join("\n", Enumerable.Range(1, 4).Select(i => $$"""
            if (n == {{i}})
            {
                for (var k = 0; k < n; k++) { b.AddBlock(); b.Connect(); if (k > 2) { b.Check(); b.Done(); } else b.Done(); }
                b.Done(); b.Check();
            }
            """));
        var code = ReadCs($$"""
            {{Wiring}}
            public class A { private B b = new(); public void Go(int n) { {{arms}} } }
            """);
        var full = ActivityPlan(code, "A.Go(int)", new CodeBehaviourLimits { MaxActivityShapes = 10_000 });
        Assert.True(full.Nodes.Count + full.Edges.Count > 100);

        var plan = ActivityPlan(code, "A.Go(int)");
        Assert.True(plan.Nodes.Count + plan.Edges.Count <= 61, $"{plan.Nodes.Count + plan.Edges.Count} shapes");
        Assert.True(plan.Nodes.Count(n => n.Kind == ElementKind.DecisionNode) >= 4);   // the four ifs (and their merges) stay
        // The loops stay; the ifs inside them are one action each, listing what they hold.
        var inner = plan.Nodes.Where(n => n.Name == "If k is more than 2 … (3 steps)").ToList();
        Assert.Equal(4, inner.Count);
        Assert.Equal("b.Check()\nb.Done()\nb.Done()", inner[0].Fields[FieldKeys.Documentation]);
        Assert.Contains(CodeActivityPlanner.SimplifiedNote, plan.Documentation);
        Assert.Contains("summarized", plan.Nodes.Single(n => n.Kind == ElementKind.Comment).Fields[FieldKeys.Body]);
    }

    [Fact]
    public void Even_a_huge_flat_method_ends_up_near_the_shape_cap()
    {
        var ifs = string.Join(" ", Enumerable.Range(1, 80).Select(i => $"if (n == {i}) {{ b.Done(); b.Check(); }}"));
        var code = ReadCs($$"""
            {{Wiring}}
            public class A { private B b = new(); public void Go(int n) { {{ifs}} } }
            """);
        var plan = ActivityPlan(code, "A.Go(int)");
        Assert.True(plan.Nodes.Count + plan.Edges.Count <= 61, $"{plan.Nodes.Count + plan.Edges.Count} shapes");
        Assert.Contains(plan.Nodes, n => n.Name.StartsWith("… ", StringComparison.Ordinal) && n.Name.EndsWith("more steps", StringComparison.Ordinal));
        Assert.Single(plan.Nodes, n => n.Kind == ElementKind.ActivityFinalNode);
    }

    // ================================================================ what "Entry points" picks

    private static CodeModel ReadFiles(params (string Path, string Source)[] files) =>
        new CSharpCodeReader().Read(files.Select(f => new CodeFile(f.Path, f.Source)).ToList());

    private const string Repo = "namespace App.Data { public class Repo { public void A() { } public void B() { } public void C() { } public void D() { } } }";

    [Fact]
    public void Entry_points_skip_samples_generated_code_builders_and_straight_runs_of_calls()
    {
        var seed = string.Join(" ", Enumerable.Range(0, 10).Select(i => "_r." + "ABCD"[i % 4] + "();"));
        var code = ReadFiles(
            ("src/Data/Repo.cs", Repo),
            ("src/Samples/Demo.cs", "namespace App.Samples { public class Demo { private App.Data.Repo _r = new(); public void Build(bool x) { if (x) _r.A(); else _r.B(); } } }"),
            ("src/Ui/Form1.Designer.cs", "namespace App.Ui { public class Form1 { private App.Data.Repo _r = new(); public void InitializeComponent(bool x) { if (x) _r.A(); } } }"),
            ("src/Ui/Gen.cs", "namespace App.Ui { [System.CodeDom.Compiler.GeneratedCode(\"t\", \"1\")] public class Gen { private App.Data.Repo _r = new(); public void Run(bool x) { if (x) _r.A(); } } }"),
            ("src/Api/SeedController.cs", "namespace App.Api { public class SeedController { private App.Data.Repo _r = new(); public void Seed() { " + seed + " } public void Pick(bool x) { if (x) _r.A(); else _r.B(); } } }"),
            ("src/Core/Steps.cs", "namespace App.Core { public class Steps { private App.Data.Repo _r = new(); "
                + "public void Straight() { _r.A(); _r.B(); _r.C(); _r.D(); _r.A(); } "
                + "public void Guarded() { using (var x = new System.IO.MemoryStream()) { _r.A(); _r.B(); _r.C(); _r.D(); _r.A(); } } } }"));
        var o = Options with { Behaviour = CodeBehaviourMode.EntryPoints };
        var m = new ModelRepository();
        CodeModelMapper.Apply(m, code, o);
        var symbols = m.AllElements.Select(e => e.Get(CodeFieldKeys.Symbol)).Where(CodeBehaviourMapper.IsBehaviourSymbol).OrderBy(x => x).ToList();
        Assert.Contains("act:App.Api.SeedController.Pick(bool)", symbols);
        Assert.Contains("seq:App.Api.SeedController.Pick(bool)", symbols);
        Assert.Contains("act:App.Core.Steps.Guarded()", symbols);   // calls inside a using block: worth a diagram
        Assert.DoesNotContain(symbols, x => x.Contains("Samples", StringComparison.Ordinal));
        Assert.DoesNotContain(symbols, x => x.Contains("Form1", StringComparison.Ordinal));
        Assert.DoesNotContain(symbols, x => x.Contains("Gen.", StringComparison.Ordinal));
        Assert.DoesNotContain(symbols, x => x.Contains("Seed()", StringComparison.Ordinal));   // a builder, even in a controller
        Assert.DoesNotContain("act:App.Core.Steps.Straight()", symbols);   // five calls in a row fold into one action: no diagram

        Assert.True(CodeBehaviourIndex.IsGeneratedOrSample(code.Types.Single(t => t.Name == "Demo")));
        Assert.DoesNotContain(code.Types, t => t.Name == "Form1");   // the reader already leaves designer files out
        Assert.True(CodeBehaviourIndex.IsGeneratedOrSample(code.Types.Single(t => t.Name == "Gen")));
        Assert.False(CodeBehaviourIndex.IsGeneratedOrSample(code.Types.Single(t => t.Name == "Steps")));

        // They stay available on demand.
        var (d, _) = CodeBehaviourMapper.Generate(m, code, Op(m, "App.Samples.Demo.Build(bool)"), false, o);
        Assert.NotNull(d);
        var (seq, _) = CodeBehaviourMapper.Generate(m, code, Op(m, "App.Api.SeedController.Seed()"), true, o);
        Assert.NotNull(seq);
    }

    [Fact]
    public void Behaviours_entry_points_no_longer_pick_are_removed_when_untouched_and_kept_otherwise()
    {
        static (string, string) Type(string folder, string name) =>
            ($"src/{folder}/{name}.cs", $"namespace App.Logic {{ public class {name} {{ private App.Data.Repo _r = new(); public void Run(bool x) {{ if (x) _r.A(); else _r.B(); }} }} }}");
        var o = Options with { Behaviour = CodeBehaviourMode.EntryPoints };
        var m = new ModelRepository();
        var before = ReadFiles(("src/Data/Repo.cs", Repo), Type("Logic", "Plain"), Type("Logic", "Moved"), Type("Logic", "Asked"));
        CodeModelMapper.Apply(m, before, o);
        Element Act(string name) => m.AllElements.Single(e => e.Get(CodeFieldKeys.Symbol) == $"act:App.Logic.{name}.Run(bool)");
        var (plain, moved, asked) = (Act("Plain"), Act("Moved"), Act("Asked"));
        // Someone moves a shape on one; another is regenerated on demand (it is now theirs).
        var d = moved.OwnedElements.OfType<Diagram>().Single();
        m.SetBounds(d, new Dictionary<DiagramNode, Bounds> { [d.Nodes[1]] = d.Nodes[1].Bounds with { X = d.Nodes[1].Bounds.X + 90 } });
        CodeBehaviourMapper.Generate(m, before, Op(m, "App.Logic.Asked.Run(bool)"), false, o);

        // The code moves into a Samples folder: "Entry points" no longer picks these methods.
        var after = ReadFiles(("src/Data/Repo.cs", Repo), Type("Samples", "Plain"), Type("Samples", "Moved"), Type("Samples", "Asked"));
        var changes = CodeModelMapper.Apply(m, after, o with { Commit = "c2" });
        Assert.False(m.Contains(plain));
        Assert.Contains(plain.Id, changes.Removed);
        Assert.True(m.Contains(moved));
        Assert.True(m.Contains(asked));
        // Plain's activity and the three sequences (all untouched, none asked for) went.
        Assert.Equal(4, changes.TrivialDiagramsRemoved);
        Assert.DoesNotContain(m.AllElements, e => e.Get(CodeFieldKeys.Symbol).StartsWith("seq:", StringComparison.Ordinal));
        // And they are not made again.
        var third = CodeModelMapper.Apply(m, after, o with { Commit = "c3" });
        Assert.True(third.IsEmpty, string.Join(" | ", third.Added.Concat(third.Changed).Select(id => m.Find(id)).Select(e => $"{e?.Kind}:{e?.Name}:{e?.Get(CodeFieldKeys.Symbol)}")) + " removed " + third.Removed.Count);
    }
}
