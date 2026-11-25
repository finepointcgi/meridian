using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.CodeSync;

/// <summary>
/// A method body → an activity: initial node, an action per call (pointing at the called operation when it resolves),
/// one action per run of other simple statements, decisions and merges for if / switch / try-catch, a decision with a
/// back edge for loops, activity finals for returns and «exception» finals for throws. Laid out top to bottom: each
/// decision's branches side by side below it, their merge below the longest branch.
/// To stay readable, a run of <see cref="CodeBehaviourLimits.FoldRun"/> or more plain statements is one summary action
/// ("12 calls: AddBlock, Connect, …", the list in its documentation), and an activity that would still draw more than
/// <see cref="CodeBehaviourLimits.MaxActivityShapes"/> shapes and flows is simplified step by step: shorter runs fold,
/// then blocks nested deeper than 3, 2, 1 levels become one action each, and last the tail of the method; the decisions,
/// loops and try blocks that remain keep the control flow, and a note says what was folded.
/// </summary>
internal sealed class CodeActivityPlanner
{
    public const double RowHeight = 80, Gap = 40, Top = 30, Left = 40;

    /// <summary>Field on a call action: id of the called operation (a CallOperationAction's operation).</summary>
    public const string OperationField = "operation";

    /// <summary>Words in the documentation of an activity that was simplified to fit the shape cap.</summary>
    public const string SimplifiedNote = "Simplified to fit";

    /// <summary>The simplification steps: fold runs of this many (0 = the limits' FoldRun), summarize blocks nested this
    /// deep, summarize the tail of the method.</summary>
    private static readonly (int Fold, int Nest, bool Tail)[] Steps =
        [(0, int.MaxValue, false), (2, int.MaxValue, false), (2, 3, false), (2, 2, false), (2, 1, false), (2, 0, false), (2, 0, true)];

    private readonly CodeBehaviourIndex _index;
    private readonly Func<string, Element?> _operation;
    private readonly BehaviourPlan _plan;
    private readonly CodeBehaviourLimits _limits;
    private readonly int _foldRun;
    private readonly int _nestCap;
    private readonly bool _tail;
    private int _nest;
    private int _foldedRuns, _foldedBlocks, _foldedTail;
    private readonly Dictionary<string, (double W, double H)> _size = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _edgeCount = new(StringComparer.Ordinal);

    private record struct Tail(string From, string Guard);

    private abstract class Region;
    private sealed class NodeRegion(string key) : Region { public string Key { get; } = key; }
    private sealed class SeqRegion : Region { public List<Region> Items { get; } = []; }
    private sealed class BranchRegion(string decision) : Region
    {
        public string Decision { get; } = decision;
        public List<SeqRegion> Branches { get; } = [];
        public string? Merge { get; set; }
    }
    private sealed class LoopRegion(string decision, SeqRegion body, string? head) : Region
    {
        public string Decision { get; } = decision;
        public SeqRegion Body { get; } = body;
        /// <summary>do-while: the merge node above the body (the decision is below it).</summary>
        public string? Head { get; } = head;
    }

    private sealed class LoopContext(string target)
    {
        /// <summary>Where continue goes.</summary>
        public string Continue { get; } = target;
        public List<Tail> Breaks { get; } = [];
    }

    private CodeActivityPlanner(CodeBehaviourIndex index, Func<string, Element?> operation, BehaviourPlan plan, CodeBehaviourLimits limits,
                                (int Fold, int Nest, bool Tail) step)
    {
        _index = index;
        _operation = operation;
        _plan = plan;
        _limits = limits;
        _foldRun = Math.Max(2, step.Fold > 0 ? step.Fold : limits.FoldRun);
        _nestCap = step.Nest;
        _tail = step.Tail;
    }

    public static BehaviourPlan Plan(CodeBehaviourIndex index, CodeMethod method, Func<string, Element?> operation, CodeBehaviourLimits? limits = null)
    {
        limits ??= new CodeBehaviourLimits();
        BehaviourPlan plan = null!;
        CodeActivityPlanner p = null!;
        var tries = 0;
        foreach (var step in Steps)
        {
            tries++;
            plan = new BehaviourPlan
            {
                Symbol = "act:" + method.Symbol, Kind = ElementKind.Activity, DiagramType = DiagramType.Activity,
                Name = $"{method.Type.Name}.{method.Member.Name}",
            };
            plan.Documentation = $"The control flow of {method.Type.Name}.{method.Member.Name}, from its code ({method.Type.File}).";
            p = new CodeActivityPlanner(index, operation, plan, limits, step);
            p.Build(method);
            if (plan.Nodes.Count + plan.Edges.Count <= limits.MaxActivityShapes) break;
        }
        if (tries > 1) p.Simplified();
        return plan;
    }

    /// <summary>Says what was folded to fit the cap: in the activity's documentation and in a note on the diagram.</summary>
    private void Simplified()
    {
        var what = new List<string>();
        if (_foldedRuns > 0) what.Add($"runs of {_foldRun} or more plain statements are one action each");
        if (_foldedBlocks > 0) what.Add($"{_foldedBlocks} block{(_foldedBlocks == 1 ? "" : "s")} nested {(_nestCap == 1 ? "inside another" : $"{_nestCap} levels deep")} are summarized as one action");
        if (_foldedTail > 0) what.Add($"the last {_foldedTail} statement{(_foldedTail == 1 ? " is" : "s are")} one action");
        if (what.Count == 0) return;
        var text = $"{SimplifiedNote} about {_limits.MaxActivityShapes} shapes: {string.Join("; ", what)}. The summary actions list what they hold in their documentation; “Generate activity from code” shows the same.";
        _plan.Documentation += " " + text;
        var right = _plan.Nodes.Where(n => n.Bounds != null).Select(n => n.Bounds!.Value.Right).DefaultIfEmpty(Left).Max();
        var note = new PlannedNode { Key = "note", Kind = ElementKind.Comment, Bounds = new Bounds(right + Gap, Top, 300, 110) };
        note.Fields[FieldKeys.Body] = text;
        _plan.Nodes.Add(note);
    }

    private int Shapes => _plan.Nodes.Count + _plan.Edges.Count;

    private void Build(CodeMethod method)
    {
        var root = new SeqRegion();
        Node(root, "initial", ElementKind.InitialNode, "");
        var statements = method.Member.Body?.Statements ?? [];
        var tails = Sequence(statements, new KeyScope(""), [new Tail("initial", "")], root, null, top: true);
        if (method.Member.Body is { Omitted: > 0 } b && tails.Count > 0)
        {
            Node(root, "more", ElementKind.Action, $"+{b.Omitted} more");
            Connect(tails, "more");
            tails = [new Tail("more", "")];
        }
        if (tails.Count > 0 || statements.Count == 0)
        {
            Node(root, "final", ElementKind.ActivityFinalNode, "");
            Connect(tails.Count == 0 ? [new Tail("initial", "")] : tails, "final");
        }
        var (w, _) = Measure(root);
        Place(root, Left + w / 2, 0);
    }

    // ================================================================ nodes and flows

    private PlannedNode Node(SeqRegion region, string key, ElementKind kind, string name)
    {
        var n = new PlannedNode { Key = KeyScope.Stored(key), Kind = kind, Name = CodeBodies.Clip(name, 60) };
        _plan.Nodes.Add(n);
        var (w, h) = Metamodel.DefaultSize(kind);
        if (kind == ElementKind.Action) w = Math.Clamp(Math.Ceiling((n.Name.Length * 7.0 + 30) / 10) * 10, 120, 300);
        _size[n.Key] = (w, h);
        region.Items.Add(new NodeRegion(n.Key));
        return n;
    }

    private void Connect(IEnumerable<Tail> tails, string to)
    {
        to = KeyScope.Stored(to);
        foreach (var t in tails)
        {
            var pair = t.From + ">" + to;
            var n = _edgeCount[pair] = _edgeCount.GetValueOrDefault(pair) + 1;
            var e = new PlannedEdge { Key = KeyScope.Stored("flow:" + pair + (n > 1 ? "#" + n : "")), Kind = ElementKind.ControlFlow, From = t.From, To = to };
            if (t.Guard.Length > 0) e.Fields[FieldKeys.Guard] = CodeBodies.Clip(t.Guard, 60);
            _plan.Edges.Add(e);
        }
    }

    private static List<Tail> One(string key) => [new Tail(KeyScope.Stored(key), "")];

    private static bool IsSimple(CodeStatement s) =>
        !s.HasCall && s.Kind is CodeStatementKind.Assign or CodeStatementKind.Create or CodeStatementKind.Yield
            || s.Kind == CodeStatementKind.Other && s.Text is not ("break" or "continue");

    private static string Label(CodeStatement s) => s.Kind switch
    {
        CodeStatementKind.Assign when s.HasCall => $"{s.Text} = {s.CallLabel}",
        CodeStatementKind.Assign => s.Text ?? "",
        CodeStatementKind.Yield when s.HasCall => $"yield {s.CallLabel}",
        CodeStatementKind.Yield => $"yield {s.Text}",
        CodeStatementKind.Return when s.HasCall => $"return {s.CallLabel}",
        _ when s.HasCall => (s.IsAwait ? "await " : "") + s.CallLabel,
        CodeStatementKind.Create => s.Text ?? $"new {s.Method}",
        _ => s.Text ?? "",
    };

    /// <summary>A statement as a step in words (<see cref="CodePhrases"/>): "Load project from DB", "Set total", "Create order".</summary>
    private static string Words(CodeStatement s) => s.Kind switch
    {
        _ when s.HasCall => CodePhrases.Step(s.Method, s.Receiver),
        CodeStatementKind.Create => CodePhrases.Create(s.Method ?? s.Text),
        CodeStatementKind.Assign => CodePhrases.Set([s.Text]),
        CodeStatementKind.Yield => "Yield " + CodePhrases.Noun(s.Text),
        _ => s.Text ?? "",
    };

    /// <summary>Where a decision comes from in the code ("if (user != null)", line 42), for its documentation.</summary>
    private static string Source(string what, CodeStatement s) => $"{what}{(s.Line > 0 ? $" (line {s.Line})" : "")}";

    // ================================================================ statements

    /// <summary>A statement without control flow of its own: a call, assignment, creation or the like.</summary>
    private static bool IsPlain(CodeStatement s) =>
        s.Body.Count == 0 && s.Else.Count == 0 && s.Branches.Count == 0 && s.Finally.Count == 0
        && (s.Kind is CodeStatementKind.Call or CodeStatementKind.Assign or CodeStatementKind.Create or CodeStatementKind.Yield
            || s.Kind == CodeStatementKind.Other && s.Text is not ("break" or "continue"));

    private static bool IsControl(CodeStatement s) => s.Kind is CodeStatementKind.If or CodeStatementKind.Switch or CodeStatementKind.Loop or CodeStatementKind.Try;

    /// <summary>One line per statement (calls and labels), at most 40, for a summary action's documentation.</summary>
    private static string Listing(IEnumerable<CodeStatement> statements)
    {
        var lines = CodeBodies.Walk(statements).Where(x => x.HasCall || IsPlain(x) || x.Kind is CodeStatementKind.Return or CodeStatementKind.Throw)
            .Select(Label).Where(l => l.Length > 0).ToList();
        return string.Join("\n", lines.Take(40)) + (lines.Count > 40 ? $"\n… (+{lines.Count - 40})" : "");
    }

    /// <summary>"12 steps: add block, connect, …".</summary>
    private static string RunName(IReadOnlyList<CodeStatement> run)
    {
        var names = run.Select(x => CodeNames.Lower(Words(x))).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var head = $"{run.Count} steps: ";
        var list = new System.Text.StringBuilder();
        foreach (var n in names)
        {
            var next = (list.Length > 0 ? ", " : "") + n;
            if (head.Length + list.Length + next.Length > 54) { list.Append(", …"); break; }
            list.Append(next);
        }
        return head + list;
    }

    private List<Tail> Sequence(IReadOnlyList<CodeStatement> list, KeyScope scope, List<Tail> tails, SeqRegion region, LoopContext? loop, bool top = false)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (tails.Count == 0) break;   // after return / throw / break: unreachable
            var s = list[i];
            if (top && _tail && Shapes >= _limits.MaxActivityShapes - 6)
            {
                // Last resort: the rest of the method is one action.
                var rest = list.Skip(i).ToList();
                _foldedTail = CodeBodies.Count(rest);
                var key = scope.Next("rest:" + KeyScope.For(s));
                var n = Node(region, key, ElementKind.Action, $"… {_foldedTail} more step{(_foldedTail == 1 ? "" : "s")}");
                n.Fields[FieldKeys.Documentation] = Listing(rest);
                Connect(tails, key);
                tails = One(key);
                break;
            }
            if (IsPlain(s))
            {
                var run = new List<CodeStatement> { s };
                while (i + 1 < list.Count && IsPlain(list[i + 1])) run.Add(list[++i]);
                if (run.Count >= _foldRun)
                {
                    // A long run of plain statements is one summary action, the list in its documentation.
                    var key = scope.Next("fold:" + Label(run[0]));
                    var n = Node(region, key, ElementKind.Action, RunName(run));
                    n.Fields[FieldKeys.Documentation] = Listing(run);
                    Connect(tails, key);
                    tails = One(key);
                    _foldedRuns++;
                    continue;
                }
                tails = Plain(run, scope, tails, region, loop);
                continue;
            }
            tails = Statement(s, scope, tails, region, loop);
        }
        return tails;
    }

    /// <summary>A short run of plain statements: an action per call, one action per run of other simple statements.</summary>
    private List<Tail> Plain(List<CodeStatement> run, KeyScope scope, List<Tail> tails, SeqRegion region, LoopContext? loop)
    {
        for (var i = 0; i < run.Count; i++)
        {
            var s = run[i];
            if (IsSimple(s))
            {
                // A run of simple statements is one action named after them.
                var simple = new List<CodeStatement> { s };
                while (i + 1 < run.Count && IsSimple(run[i + 1])) simple.Add(run[++i]);
                var key = scope.Next("do:" + Label(simple[0]));
                var name = simple.Count == 1 ? Words(s) : simple.All(x => x.Kind == CodeStatementKind.Assign) ? CodePhrases.Set(simple.Select(x => x.Text)) : $"{Words(simple[0])}; … (+{simple.Count - 1})";
                var act = Node(region, key, ElementKind.Action, name);
                act.Fields[FieldKeys.Documentation] = string.Join("\n", simple.Select(Label));
                Connect(tails, key);
                tails = One(key);
                continue;
            }
            tails = Statement(s, scope, tails, region, loop);
        }
        return tails;
    }

    /// <summary>A whole if / switch / loop / try as one action ("if x … (7 statements)"), what it holds in its documentation.</summary>
    private List<Tail> Summary(CodeStatement s, KeyScope scope, List<Tail> tails, SeqRegion region)
    {
        var key = scope.Next("fold:" + KeyScope.For(s));
        var inside = CodeBodies.Count([s]) - 1 - s.Calls.Count;
        var count = $"{inside} step{(inside == 1 ? "" : "s")}";
        var name = s.Kind switch
        {
            CodeStatementKind.If => $"If {CodeBodies.Clip(CodePhrases.Clause(s.Text), 30)} … ({count})",
            CodeStatementKind.Switch => $"Depending on {CodeBodies.Clip(CodePhrases.Noun(s.Text), 24)} ({s.Branches.Count} cases, {count})",
            CodeStatementKind.Loop => $"Repeat: {CodeBodies.Clip(CodePhrases.LoopQuestion(s.Loop, s.Text).TrimEnd('?'), 30)} ({count})",
            _ => $"Try … ({count})",
        };
        var n = Node(region, key, ElementKind.Action, name);
        n.Fields[FieldKeys.Documentation] = Listing([s]);
        Connect(tails, key);
        _foldedBlocks++;
        return One(key);
    }

    private List<Tail> Statement(CodeStatement s, KeyScope scope, List<Tail> tails, SeqRegion region, LoopContext? loop)
    {
        if (!IsControl(s)) return StatementCore(s, scope, tails, region, loop);
        if (_nest >= _nestCap) return Summary(s, scope, tails, region);
        _nest++;
        try { return StatementCore(s, scope, tails, region, loop); }
        finally { _nest--; }
    }

    private List<Tail> Call(CodeStatement s, string key, List<Tail> tails, SeqRegion region)
    {
        var n = Node(region, key, ElementKind.Action, Words(s));
        n.Fields[FieldKeys.Documentation] = Label(s);
        if (_index.Resolve(s) is { } target && _operation(target.Symbol) is { } op) n.Fields[OperationField] = op.Id;
        Connect(tails, key);
        return One(key);
    }

    private List<Tail> StatementCore(CodeStatement s, KeyScope scope, List<Tail> tails, SeqRegion region, LoopContext? loop)
    {
        var key = scope.Next(KeyScope.For(s));
        switch (s.Kind)
        {
            case CodeStatementKind.Return:
            {
                if (s.HasCall) tails = Call(s, key + "/call", tails, region);
                var final = key + "/end";
                Node(region, final, ElementKind.ActivityFinalNode, s.HasCall ? "" : s.Text is { Length: > 0 } t ? CodePhrases.Return(t) : "");
                Connect(tails, final);
                return [];
            }
            case CodeStatementKind.Throw:
            {
                var n = Node(region, key, ElementKind.ActivityFinalNode, CodePhrases.Fail(s.Method ?? s.Text));
                n.Fields[FieldKeys.Stereotypes] = "exception";
                n.Fields[FieldKeys.Documentation] = "throw " + (s.Text ?? s.Method ?? "");
                Connect(tails, key);
                return [];
            }
            case CodeStatementKind.Other when s.Text == "break" && loop != null:
                loop.Breaks.AddRange(tails);
                return [];
            case CodeStatementKind.Other when s.Text == "continue" && loop != null:
                Connect(tails, loop.Continue);
                return [];
            case CodeStatementKind.If:
            {
                var arms = new List<(string Guard, IReadOnlyList<CodeStatement> Body)>();
                var cur = s;
                IReadOnlyList<CodeStatement>? otherwise = null;
                while (true)
                {
                    arms.Add((cur.Text ?? "", cur.Body));
                    if (cur.Else is [{ Kind: CodeStatementKind.If } next]) { cur = next; continue; }
                    otherwise = cur.Else.Count > 0 ? cur.Else : null;
                    break;
                }
                // One condition: the decision asks it, its branches answer yes / no. Several (else-if): each branch says
                // which condition holds, the last "otherwise".
                var single = arms.Count == 1;
                var source = "if " + string.Join(" / else if ", arms.Select(a => $"({a.Guard})"));
                var named = arms.Select(a => (single ? "yes" : CodePhrases.Clause(a.Guard), a.Body)).ToList();
                if (otherwise != null) named.Add((single ? "no" : "otherwise", otherwise));
                // Arm keys stay the code (stable element identity across versions of the phrasing).
                var keys = arms.Select(a => a.Guard).Concat(otherwise != null ? ["else"] : []).ToList();
                return Branch(key, named, otherwise == null, tails, region, loop,
                    single ? CodePhrases.Question(arms[0].Guard) : "Which case applies?", Source(source, s), single ? "no" : "otherwise", keys);
            }
            case CodeStatementKind.Switch:
            {
                var arms = s.Branches.Select(b => (CodePhrases.CaseLabel(b.Label), b.Body)).ToList();
                var hasDefault = s.Branches.Any(b => b.Label == "default" || b.Label.Length == 0);
                return Branch(key, arms, !hasDefault, tails, region, loop, CodePhrases.SwitchQuestion(s.Text), Source($"switch ({s.Text})", s), "otherwise",
                    s.Branches.Select(b => b.Label.Length == 0 ? "default" : b.Label).ToList());
            }
            case CodeStatementKind.Loop when s.Loop == CodeLoopKind.Do:
            {
                var head = key + "/do";
                var dec = key + "/until";
                var body = new SeqRegion();
                var outer = new SeqRegion();
                var headNode = Node(outer, head, ElementKind.DecisionNode, "");   // a merge: unnamed
                outer.Items.Clear();
                Connect(tails, head);
                var ctx = new LoopContext(KeyScope.Stored(dec));
                var inner = Sequence(s.Body, new KeyScope(key), One(head), body, ctx);
                var decRegion = new SeqRegion();
                var until = Node(decRegion, dec, ElementKind.DecisionNode, CodePhrases.LoopQuestion(CodeLoopKind.Do, s.Text));
                until.Fields[FieldKeys.Documentation] = Source($"do … while ({s.Text})", s);
                Connect(inner, dec);
                Connect([new Tail(KeyScope.Stored(dec), "yes, again")], head);
                region.Items.Add(new LoopRegion(KeyScope.Stored(dec), body, headNode.Key));
                return [new Tail(KeyScope.Stored(dec), "no, done"), .. ctx.Breaks];
            }
            case CodeStatementKind.Loop:
            {
                var tmp = new SeqRegion();
                var decNode = Node(tmp, key, ElementKind.DecisionNode, CodePhrases.LoopQuestion(s.Loop, s.Text));
                var keyword = s.Loop switch { CodeLoopKind.ForEach => "foreach", CodeLoopKind.For => "for", _ => "while" };
                decNode.Fields[FieldKeys.Documentation] = Source($"{keyword} ({s.Text})", s);
                var dec = decNode.Key;
                Connect(tails, dec);
                var body = new SeqRegion();
                var ctx = new LoopContext(dec);
                var (more, done) = s.Loop == CodeLoopKind.ForEach ? ("next one", "all done") : ("yes", "no");
                var inner = Sequence(s.Body, new KeyScope(key), [new Tail(dec, more)], body, ctx);
                if (inner.Count > 0 && !(inner.Count == 1 && inner[0].From == dec)) Connect(inner, dec);
                region.Items.Add(new LoopRegion(dec, body, null));
                return [new Tail(dec, done), .. ctx.Breaks];
            }
            case CodeStatementKind.Try:
            {
                tails = Sequence(s.Body, new KeyScope(key + "/try"), tails, region, loop);
                if (s.Branches.Count > 0 && tails.Count > 0)
                {
                    var arms = new List<(string, IReadOnlyList<CodeStatement>)> { ("yes", []) };
                    arms.AddRange(s.Branches.Select(b => (b.Label.Length == 0 ? "no, it failed" : "no: " + CodePhrases.Fail(b.Label)[6..].Trim(), b.Body)));
                    tails = Branch(key + "/catch", arms, false, tails, region, loop, "Did it work?",
                        Source("try … catch " + string.Join(", ", s.Branches.Select(b => b.Label.Length == 0 ? "(any)" : b.Label)), s), "no",
                        ["ok", .. s.Branches.Select(b => b.Label.Length == 0 ? "catch" : "catch " + b.Label)]);
                }
                if (s.Finally.Count > 0 && tails.Count > 0) tails = Sequence(s.Finally, new KeyScope(key + "/finally"), tails, region, loop);
                return tails;
            }
            case CodeStatementKind.Using or CodeStatementKind.Lock:
            {
                var use = Node(region, key, ElementKind.Action, (s.Kind == CodeStatementKind.Using ? "Use " : "Lock ") + CodePhrases.Noun((s.Text ?? "").Split('=')[^1]));
                use.Fields[FieldKeys.Documentation] = (s.Kind == CodeStatementKind.Using ? "using " : "lock ") + s.Text;
                Connect(tails, key);
                return Sequence(s.Body, new KeyScope(key), One(key), region, loop);
            }
            default:
                if (s.HasCall) return Call(s, key, tails, region);
                var plain = Node(region, key, ElementKind.Action, Words(s));
                plain.Fields[FieldKeys.Documentation] = Label(s);
                Connect(tails, key);
                return One(key);
        }
    }

    /// <summary>A decision with one branch per arm (guarded), and a merge when more than one path comes out.</summary>
    private List<Tail> Branch(string key, List<(string Guard, IReadOnlyList<CodeStatement> Body)> arms, bool elsePasses, List<Tail> tails, SeqRegion region, LoopContext? loop,
                              string question, string source, string elseGuard, IReadOnlyList<string> armKeys)
    {
        var armIndex = 0;
        var tmp = new SeqRegion();
        var decNode = Node(tmp, key, ElementKind.DecisionNode, question);
        decNode.Fields[FieldKeys.Documentation] = source;
        var dec = decNode.Key;
        Connect(tails, dec);
        var br = new BranchRegion(dec);
        var outs = new List<Tail>();
        var scope = new KeyScope(key);
        foreach (var (guard, body) in arms)
        {
            var seq = new SeqRegion();
            br.Branches.Add(seq);
            outs.AddRange(Sequence(body, new KeyScope(scope.Next("arm:" + armKeys[armIndex++])), [new Tail(dec, guard)], seq, loop));
        }
        if (elsePasses)
        {
            br.Branches.Add(new SeqRegion());
            outs.Add(new Tail(dec, elseGuard));
        }
        region.Items.Add(br);
        if (outs.Count < 2) return outs;
        var merge = new SeqRegion();
        var m = Node(merge, key + "/merge", ElementKind.DecisionNode, "").Key;
        br.Merge = m;
        Connect(outs, m);
        return One(m);
    }

    // ================================================================ layout

    private (double W, int Rows) Measure(Region r) => r switch
    {
        NodeRegion n => (_size[n.Key].W, 1),
        SeqRegion s => s.Items.Count == 0 ? (60, 0) : (s.Items.Max(i => Measure(i).W), s.Items.Sum(i => Measure(i).Rows)),
        BranchRegion b => (Math.Max(_size[b.Decision].W, b.Branches.Sum(x => Measure(x).W) + Gap * (b.Branches.Count - 1)),
                           1 + b.Branches.Max(x => Measure(x).Rows) + (b.Merge != null ? 1 : 0)),
        LoopRegion l => (Measure(l.Body).W + 2 * Gap, 1 + Measure(l.Body).Rows + (l.Head != null ? 1 : 0)),
        _ => (0, 0),
    };

    private void At(string key, double cx, int row)
    {
        var (w, h) = _size[key];
        var cy = Top + row * RowHeight + RowHeight / 2;
        _plan.Nodes.First(n => n.Key == key).Bounds = new Bounds(Math.Round(cx - w / 2), Math.Round(cy - h / 2), w, h);
    }

    private int Place(Region r, double cx, int row)
    {
        switch (r)
        {
            case NodeRegion n:
                At(n.Key, cx, row);
                return row + 1;
            case SeqRegion s:
                foreach (var i in s.Items) row = Place(i, cx, row);
                return row;
            case BranchRegion b:
            {
                At(b.Decision, cx, row);
                var widths = b.Branches.Select(x => Measure(x).W).ToList();
                var total = widths.Sum() + Gap * (widths.Count - 1);
                var x0 = cx - total / 2;
                var bottom = row + 1;
                for (var i = 0; i < b.Branches.Count; i++)
                {
                    bottom = Math.Max(bottom, Place(b.Branches[i], x0 + widths[i] / 2, row + 1));
                    x0 += widths[i] + Gap;
                }
                if (b.Merge != null) { At(b.Merge, cx, bottom); bottom++; }
                return bottom;
            }
            case LoopRegion l:
                if (l.Head != null)
                {
                    At(l.Head, cx, row);
                    var end = Place(l.Body, cx, row + 1);
                    At(l.Decision, cx, end);
                    return end + 1;
                }
                At(l.Decision, cx, row);
                return Place(l.Body, cx, row + 1);
        }
        return row;
    }
}
