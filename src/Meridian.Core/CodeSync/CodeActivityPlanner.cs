using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.CodeSync;

/// <summary>
/// A method body → an activity: initial node, an action per call (pointing at the called operation when it resolves),
/// one action per run of other simple statements, decisions and merges for if / switch / try-catch, a decision with a
/// back edge for loops, activity finals for returns and «exception» finals for throws. Laid out top to bottom: each
/// decision's branches side by side below it, their merge below the longest branch.
/// </summary>
internal sealed class CodeActivityPlanner
{
    public const double RowHeight = 80, Gap = 40, Top = 30, Left = 40;

    /// <summary>Field on a call action: id of the called operation (a CallOperationAction's operation).</summary>
    public const string OperationField = "operation";

    private readonly CodeBehaviourIndex _index;
    private readonly Func<string, Element?> _operation;
    private readonly BehaviourPlan _plan;
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

    private CodeActivityPlanner(CodeBehaviourIndex index, Func<string, Element?> operation, BehaviourPlan plan)
    {
        _index = index;
        _operation = operation;
        _plan = plan;
    }

    public static BehaviourPlan Plan(CodeBehaviourIndex index, CodeMethod method, Func<string, Element?> operation)
    {
        var plan = new BehaviourPlan
        {
            Symbol = "act:" + method.Symbol, Kind = ElementKind.Activity, DiagramType = DiagramType.Activity,
            Name = $"{method.Type.Name}.{method.Member.Name}",
        };
        plan.Documentation = $"The control flow of {method.Type.Name}.{method.Member.Name}, from its code ({method.Type.File}).";
        var p = new CodeActivityPlanner(index, operation, plan);
        p.Build(method);
        return plan;
    }

    private void Build(CodeMethod method)
    {
        var root = new SeqRegion();
        Node(root, "initial", ElementKind.InitialNode, "");
        var statements = method.Member.Body?.Statements ?? [];
        var tails = Sequence(statements, new KeyScope(""), [new Tail("initial", "")], root, null);
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

    // ================================================================ statements

    private List<Tail> Sequence(IReadOnlyList<CodeStatement> list, KeyScope scope, List<Tail> tails, SeqRegion region, LoopContext? loop)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (tails.Count == 0) break;   // after return / throw / break: unreachable
            var s = list[i];
            if (IsSimple(s))
            {
                // A run of simple statements is one action named after them.
                var run = new List<CodeStatement> { s };
                while (i + 1 < list.Count && IsSimple(list[i + 1])) run.Add(list[++i]);
                var key = scope.Next("do:" + Label(run[0]));
                var name = run.Count == 1 ? Label(s) : $"{Label(run[0])}; … (+{run.Count - 1})";
                Node(region, key, ElementKind.Action, name);
                Connect(tails, key);
                tails = One(key);
                continue;
            }
            tails = Statement(s, scope, tails, region, loop);
        }
        return tails;
    }

    private List<Tail> Call(CodeStatement s, string key, List<Tail> tails, SeqRegion region)
    {
        var n = Node(region, key, ElementKind.Action, Label(s));
        if (_index.Resolve(s) is { } target && _operation(target.Symbol) is { } op) n.Fields[OperationField] = op.Id;
        Connect(tails, key);
        return One(key);
    }

    private List<Tail> Statement(CodeStatement s, KeyScope scope, List<Tail> tails, SeqRegion region, LoopContext? loop)
    {
        var key = scope.Next(KeyScope.For(s));
        switch (s.Kind)
        {
            case CodeStatementKind.Return:
            {
                if (s.HasCall) tails = Call(s, key + "/call", tails, region);
                var final = key + "/end";
                Node(region, final, ElementKind.ActivityFinalNode, s.HasCall ? "" : s.Text is { Length: > 0 } t ? "return " + t : "");
                Connect(tails, final);
                return [];
            }
            case CodeStatementKind.Throw:
            {
                var n = Node(region, key, ElementKind.ActivityFinalNode, s.Method ?? s.Text ?? "");
                n.Fields[FieldKeys.Stereotypes] = "exception";
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
                if (otherwise != null) arms.Add(("else", otherwise));
                return Branch(key, arms, otherwise == null, tails, region, loop);
            }
            case CodeStatementKind.Switch:
            {
                var arms = s.Branches.Select(b => (b.Label.Length == 0 ? "default" : b.Label, b.Body)).ToList();
                var hasDefault = s.Branches.Any(b => b.Label == "default" || b.Label.Length == 0);
                return Branch(key, arms, !hasDefault, tails, region, loop);
            }
            case CodeStatementKind.Loop when s.Loop == CodeLoopKind.Do:
            {
                var head = key + "/do";
                var dec = key + "/until";
                var body = new SeqRegion();
                var outer = new SeqRegion();
                var headNode = Node(outer, head, ElementKind.DecisionNode, "");
                outer.Items.Clear();
                Connect(tails, head);
                var ctx = new LoopContext(KeyScope.Stored(dec));
                var inner = Sequence(s.Body, new KeyScope(key), One(head), body, ctx);
                var decRegion = new SeqRegion();
                Node(decRegion, dec, ElementKind.DecisionNode, "");
                Connect(inner, dec);
                Connect([new Tail(KeyScope.Stored(dec), s.Text ?? "")], head);
                region.Items.Add(new LoopRegion(KeyScope.Stored(dec), body, headNode.Key));
                return [new Tail(KeyScope.Stored(dec), "else"), .. ctx.Breaks];
            }
            case CodeStatementKind.Loop:
            {
                var tmp = new SeqRegion();
                var dec = Node(tmp, key, ElementKind.DecisionNode, "").Key;
                Connect(tails, dec);
                var body = new SeqRegion();
                var ctx = new LoopContext(dec);
                var inner = Sequence(s.Body, new KeyScope(key), [new Tail(dec, s.Text ?? "")], body, ctx);
                if (inner.Count > 0 && !(inner.Count == 1 && inner[0].From == dec)) Connect(inner, dec);
                region.Items.Add(new LoopRegion(dec, body, null));
                return [new Tail(dec, "else"), .. ctx.Breaks];
            }
            case CodeStatementKind.Try:
            {
                tails = Sequence(s.Body, new KeyScope(key + "/try"), tails, region, loop);
                if (s.Branches.Count > 0 && tails.Count > 0)
                {
                    var arms = new List<(string, IReadOnlyList<CodeStatement>)> { ("ok", []) };
                    arms.AddRange(s.Branches.Select(b => (b.Label.Length == 0 ? "catch" : "catch " + b.Label, b.Body)));
                    tails = Branch(key + "/catch", arms, false, tails, region, loop);
                }
                if (s.Finally.Count > 0 && tails.Count > 0) tails = Sequence(s.Finally, new KeyScope(key + "/finally"), tails, region, loop);
                return tails;
            }
            case CodeStatementKind.Using or CodeStatementKind.Lock:
            {
                Node(region, key, ElementKind.Action, (s.Kind == CodeStatementKind.Using ? "using " : "lock ") + s.Text);
                Connect(tails, key);
                return Sequence(s.Body, new KeyScope(key), One(key), region, loop);
            }
            default:
                if (s.HasCall) return Call(s, key, tails, region);
                Node(region, key, ElementKind.Action, Label(s));
                Connect(tails, key);
                return One(key);
        }
    }

    /// <summary>A decision with one branch per arm (guarded), and a merge when more than one path comes out.</summary>
    private List<Tail> Branch(string key, List<(string Guard, IReadOnlyList<CodeStatement> Body)> arms, bool elsePasses, List<Tail> tails, SeqRegion region, LoopContext? loop)
    {
        var tmp = new SeqRegion();
        var dec = Node(tmp, key, ElementKind.DecisionNode, "").Key;
        Connect(tails, dec);
        var br = new BranchRegion(dec);
        var outs = new List<Tail>();
        var scope = new KeyScope(key);
        foreach (var (guard, body) in arms)
        {
            var seq = new SeqRegion();
            br.Branches.Add(seq);
            outs.AddRange(Sequence(body, new KeyScope(scope.Next("arm:" + guard)), [new Tail(dec, guard)], seq, loop));
        }
        if (elsePasses)
        {
            br.Branches.Add(new SeqRegion());
            outs.Add(new Tail(dec, "else"));
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
