using Meridian.Core.Model;

namespace Meridian.Core.CodeSync;

/// <summary>
/// An entry method → an interaction: a caller lifeline sends the entry call to the method's object; its body is followed
/// depth first into the scanned types it calls (up to <see cref="CodeBehaviourLimits.SequenceDepth"/> levels and
/// <see cref="CodeBehaviourLimits.MaxMessages"/> messages). Each object gets a lifeline named after the field, parameter
/// or local that holds it ("runner : CodeSyncRunner"), static calls go to a lifeline of the type. Calls are synchronous
/// messages (asynchronous when awaited), with a reply when the method returns a value; if / else and switch become alt
/// fragments (opt for a lone if), loops loop fragments and catch clauses break fragments.
/// </summary>
internal sealed class CodeSequencePlanner
{
    public const double LifelineGap = 220, LifelineWidth = 150, FirstMessageY = 110, MessageGap = 40, HeaderGap = 30, OperandGap = 14;

    private readonly CodeBehaviourIndex _index;
    private readonly Func<string, Element?> _typeElement;
    private readonly CodeBehaviourLimits _limits;
    private readonly BehaviourPlan _plan;
    private readonly List<string> _lifelines = [];
    private readonly Dictionary<string, PlannedNode> _lifelineNodes = new(StringComparer.Ordinal);
    private readonly List<OpenFragment> _fragments = [];
    private double _y = FirstMessageY;
    private int _messages;
    private bool _capped;
    private readonly SortedSet<string> _depthCut = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _recursion = new(StringComparer.Ordinal);

    private sealed class OpenFragment
    {
        public required PlannedNode Node { get; init; }
        public double Top { get; init; }
        public double Bottom { get; set; }
        public int Depth { get; init; }
        public HashSet<string> Lifelines { get; } = new(StringComparer.Ordinal);
    }

    private readonly Stack<OpenFragment> _open = new();

    private CodeSequencePlanner(CodeBehaviourIndex index, Func<string, Element?> typeElement, CodeBehaviourLimits limits, BehaviourPlan plan)
    {
        _index = index;
        _typeElement = typeElement;
        _limits = limits;
        _plan = plan;
    }

    public static BehaviourPlan Plan(CodeBehaviourIndex index, CodeMethod method, Func<string, Element?> typeElement, CodeBehaviourLimits limits)
    {
        var plan = new BehaviourPlan
        {
            Symbol = "seq:" + method.Symbol, Kind = ElementKind.Interaction, DiagramType = DiagramType.Sequence,
            Name = $"{method.Type.Name}.{method.Member.Name} sequence",
        };
        new CodeSequencePlanner(index, typeElement, limits, plan).Build(method);
        return plan;
    }

    private void Build(CodeMethod method)
    {
        var caller = Lifeline("ll:caller", "caller", null);
        var self = Lifeline("ll:self:" + method.Type.Symbol, Camel(method.Type.Name), method.Type.Symbol);
        var args = string.Join(", ", method.Member.Parameters.Where(p => p.Direction != CodeParameterDirection.Return).Select(p => p.Name));
        Message("msg:entry", caller, self, $"{method.Member.Name}({args})", "synchCall");
        var body = _index.WithBody(method) ?? method;
        Walk(body.Member.Body?.Statements ?? [], self, 1, [method.Symbol], new KeyScope("m"));
        if (!method.IsVoid) Message("msg:entry/reply", self, caller, Type(method.Member.TypeName), "reply");

        var notes = new List<string>();
        if (_capped) notes.Add($"Stopped at {_limits.MaxMessages} messages; later calls are not shown.");
        if (_depthCut.Count > 0) notes.Add($"Calls deeper than {_limits.SequenceDepth} levels are not followed ({string.Join(", ", _depthCut.Take(4))}{(_depthCut.Count > 4 ? ", …" : "")}).");
        if (_recursion.Count > 0) notes.Add($"Recursive calls are shown once ({string.Join(", ", _recursion.Take(4))}).");
        if (method.Member.Body is { Omitted: > 0 } b) notes.Add($"+{b.Omitted} more statements of the method were not read.");
        _plan.Documentation = $"Calls made by {method.Type.Name}.{method.Member.Name} into the scanned code, followed {_limits.SequenceDepth} levels deep ({method.Type.File}).";
        var height = _y + 40;
        if (notes.Count > 0)
        {
            var note = new PlannedNode { Key = "note", Kind = ElementKind.Comment, Name = "", Bounds = new Bounds(40, height + 10, 420, 30 + 18 * notes.Count) };
            note.Fields[FieldKeys.Body] = string.Join("\n", notes);
            _plan.Nodes.Add(note);
        }
        foreach (var ll in _lifelineNodes.Values) ll.Bounds = ll.Bounds!.Value with { Height = height - 20 };
        foreach (var f in _fragments)
        {
            var xs = f.Lifelines.Select(k => _lifelineNodes[k].Bounds!.Value).ToList();
            if (xs.Count == 0) continue;
            var inset = f.Depth * 12;
            var x0 = xs.Min(b => b.X + b.Width / 2) - 95 + inset;
            var x1 = xs.Max(b => b.X + b.Width / 2) + 95 - inset;
            f.Node.Bounds = new Bounds(x0, f.Top, Math.Max(200, x1 - x0), f.Bottom - f.Top);
        }
    }

    private static string Camel(string name)
    {
        var n = name.Split('<')[0];
        return n.Length == 0 ? "self" : char.ToLowerInvariant(n[0]) + n[1..];
    }

    private static string Type(string t) => CodeBodies.Clip(t, 40);

    private string Lifeline(string key, string name, string? typeSymbol)
    {
        key = KeyScope.Stored(key);
        if (_lifelineNodes.ContainsKey(key)) return key;
        var n = new PlannedNode { Key = key, Kind = ElementKind.Lifeline, Name = CodeBodies.Clip(name, 40), Bounds = new Bounds(40 + _lifelines.Count * LifelineGap, 20, LifelineWidth, 300) };
        if (typeSymbol != null && _typeElement(typeSymbol) is { } te) n.Fields[FieldKeys.Type] = te.Id;
        _plan.Nodes.Add(n);
        _lifelines.Add(key);
        _lifelineNodes[key] = n;
        return key;
    }

    private void Message(string key, string from, string to, string name, string sort)
    {
        var e = new PlannedEdge { Key = KeyScope.Stored(key), Kind = ElementKind.Message, From = from, To = to, Name = CodeBodies.Clip(name, 60), MessageY = _y };
        e.Fields[FieldKeys.MessageSort] = sort;
        _plan.Edges.Add(e);
        _messages++;
        _y += MessageGap;
        foreach (var f in _open) { f.Lifelines.Add(from); f.Lifelines.Add(to); }
    }

    /// <summary>The lifeline a call goes to: the caller's own for this / base / plain calls, the type's for static
    /// calls, else one per receiver name and type.</summary>
    private string Target(CodeStatement call, string current, CodeMethod callee)
    {
        var receiver = call.Receiver ?? "";
        if (receiver is "" or "this" or "base" or "self" or "super") return current;
        var type = callee.Type;
        if (call.IsStatic || receiver == type.Name || receiver.EndsWith("." + type.Name, StringComparison.Ordinal))
            return Lifeline("ll:static:" + type.Symbol, type.Name.Split('<')[0], type.Symbol);
        var name = receiver;
        foreach (var prefix in new[] { "this.", "self.", "this?.", "base." })
            if (name.StartsWith(prefix, StringComparison.Ordinal)) name = name[prefix.Length..];
        if (name.Contains('(') || name.Contains('[') || name == "?") name = Camel(type.Name);
        else
        {
            name = name[(name.LastIndexOf('.') + 1)..].TrimStart('_');
            if (name.StartsWith("m_", StringComparison.Ordinal)) name = name[2..];
            if (name.Length == 0) name = Camel(type.Name);
        }
        return Lifeline($"ll:{name}:{call.ReceiverType}", name, call.ReceiverType);
    }

    private void Walk(IReadOnlyList<CodeStatement> statements, string current, int depth, List<string> stack, KeyScope scope)
    {
        foreach (var s in statements)
        {
            if (_capped) return;
            var key = scope.Next(KeyScope.For(s));
            var calls = new KeyScope(key + "/c");
            foreach (var c in s.Calls) Call(c, current, depth, stack, calls.Next(c.CallLabel));
            switch (s.Kind)
            {
                case CodeStatementKind.If:
                {
                    var arms = new List<(string, IReadOnlyList<CodeStatement>)>();
                    var cur = s;
                    while (true)
                    {
                        arms.Add((cur.Text ?? "", cur.Body));
                        if (cur.Else is [{ Kind: CodeStatementKind.If } next])
                        {
                            foreach (var c in next.Calls) Call(c, current, depth, stack, calls.Next(c.CallLabel));
                            cur = next;
                            continue;
                        }
                        if (cur.Else.Count > 0) arms.Add(("else", cur.Else));
                        break;
                    }
                    Fragment(key, arms.Count == 1 ? "opt" : "alt", arms, current, depth, stack);
                    break;
                }
                case CodeStatementKind.Switch:
                    Fragment(key, "alt", s.Branches.Select(b => (b.Label.Length == 0 ? "default" : b.Label, b.Body)).ToList(), current, depth, stack);
                    break;
                case CodeStatementKind.Loop:
                    Fragment(key, "loop", [(s.Text ?? "", s.Body)], current, depth, stack);
                    break;
                case CodeStatementKind.Try:
                    Walk(s.Body, current, depth, stack, new KeyScope(key + "/try"));
                    foreach (var b in s.Branches)
                        Fragment(key + "/" + b.Label, "break", [(b.Label.Length == 0 ? "catch" : "catch " + b.Label, b.Body)], current, depth, stack);
                    Walk(s.Finally, current, depth, stack, new KeyScope(key + "/finally"));
                    break;
                case CodeStatementKind.Using or CodeStatementKind.Lock:
                    Walk(s.Body, current, depth, stack, new KeyScope(key));
                    break;
            }
            if (s.HasCall) Call(s, current, depth, stack, key);
            if (s.Kind is CodeStatementKind.Return or CodeStatementKind.Throw) return;
        }
    }

    private void Fragment(string key, string op, IReadOnlyList<(string Guard, IReadOnlyList<CodeStatement> Body)> arms, string current, int depth, List<string> stack)
    {
        if (_capped) return;
        var startY = _y;
        var startMessages = _messages;
        var startEdges = _plan.Edges.Count;
        var startNodes = _plan.Nodes.Count;
        var startFragments = _fragments.Count;
        var node = new PlannedNode { Key = KeyScope.Stored("frag:" + key), Kind = ElementKind.CombinedFragment };
        node.Fields[FieldKeys.Operator] = op;
        var f = new OpenFragment { Node = node, Top = startY - 6, Depth = _open.Count };
        f.Lifelines.Add(current);
        _open.Push(f);
        _y += HeaderGap;
        var operands = new List<(PlannedNode Operand, double Top)>();
        var scope = new KeyScope(key);
        for (var i = 0; i < arms.Count; i++)
        {
            var (guard, body) = arms[i];
            if (i > 0) _y += OperandGap;
            var opNode = new PlannedNode { Key = KeyScope.Stored($"frag:{key}/op{i}"), Kind = ElementKind.InteractionOperand, OwnerKey = node.Key };
            opNode.Fields[FieldKeys.Guard] = CodeBodies.Clip(guard, 60);
            var before = _messages;
            operands.Add((opNode, _y - OperandGap / 2 - 10));
            Walk(body, current, depth, stack, new KeyScope(scope.Next("arm:" + guard)));
            if (_messages == before) _y += 20;   // room for the empty operand's guard
        }
        _open.Pop();
        if (_messages == startMessages)
        {
            // No messages inside: the fragment is not worth drawing.
            _y = startY;
            _plan.Edges.RemoveRange(startEdges, _plan.Edges.Count - startEdges);
            _plan.Nodes.RemoveRange(startNodes, _plan.Nodes.Count - startNodes);
            _fragments.RemoveRange(startFragments, _fragments.Count - startFragments);
            return;
        }
        foreach (var o in _open) o.Lifelines.UnionWith(f.Lifelines);
        _y += 10;
        f.Bottom = _y;
        if (arms.Count == 1) node.Fields[FieldKeys.Guard] = operands[0].Operand.Fields[FieldKeys.Guard];
        _plan.Nodes.Insert(startNodes, node);
        if (arms.Count > 1)
        {
            var at = startNodes + 1;
            foreach (var (o, top) in operands) _plan.Nodes.Insert(at++, o);
            foreach (var (o, top) in operands.Skip(1)) node.Separators.Add((o.Key, Math.Round(top - f.Top)));
        }
        _fragments.Insert(startFragments, f);
    }

    private void Call(CodeStatement call, string current, int depth, List<string> stack, string key)
    {
        if (_capped || call.ReceiverType is null || _index.Resolve(call) is not { } callee) return;
        if (_messages >= _limits.MaxMessages) { _capped = true; return; }
        var target = Target(call, current, callee);
        var args = call.Arguments is { Length: > 0 } a ? CodeBodies.Clip(a, 30) : "";
        var path = string.Join(">", stack.Skip(1).Select(s => s[(s.LastIndexOf('.', s.IndexOf('(') < 0 ? s.Length - 1 : s.IndexOf('(')) + 1)..])) + "/" + key;
        Message("msg:" + path, current, target, $"{call.Method}({args})", call.IsAwait ? "asynchCall" : "synchCall");
        var body = _index.WithBody(callee);
        if (body?.Member.Body is { Statements.Count: > 0 } b)
        {
            if (stack.Contains(callee.Symbol)) _recursion.Add($"{callee.Type.Name}.{callee.Member.Name}");
            else if (depth >= _limits.SequenceDepth)
            {
                if (CodeBodies.Walk(b.Statements).Any(x => x.ReceiverType != null)) _depthCut.Add($"{callee.Type.Name}.{callee.Member.Name}");
            }
            else
            {
                stack.Add(callee.Symbol);
                Walk(b.Statements, target, depth + 1, stack, new KeyScope(path));
                stack.RemoveAt(stack.Count - 1);
            }
        }
        if (!callee.IsVoid && target != current && !_capped)
            Message("msg:" + path + "/reply", target, current, Type(callee.Member.TypeName), "reply");
    }
}
