using Meridian.Core.Model;

namespace Meridian.Core.CodeSync;

/// <summary>
/// An entry method → an interaction: a caller lifeline sends the entry call to the method's object; its body is followed
/// depth first into the scanned types it calls (up to <see cref="CodeBehaviourLimits.SequenceDepth"/> levels and
/// <see cref="CodeBehaviourLimits.MaxMessages"/> messages). Each object gets a lifeline named after the field, parameter
/// or local that holds it ("runner : CodeSyncRunner"), static calls go to a lifeline of the type. Calls are synchronous
/// messages (asynchronous when awaited), with a reply when the method returns a value the caller uses; if / else and
/// switch become alt fragments (opt for a lone if), loops loop fragments and catch clauses break fragments.
/// To keep it readable: getters, setters and one-line forwarding methods are not followed, the same call repeated in a
/// loop (or right after itself) is one message, and past the cap a single "… N more calls" note stands for the rest
/// (a fragment the cap would cut goes into that count whole).
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
    /// <summary>Messages up to the end of the body: the cap less the entry's reply.</summary>
    private int _budget;
    /// <summary>Replies still owed to calls being followed (room is kept for them under the cap).</summary>
    private int _reserved;
    /// <summary>Calls left out by the cap.</summary>
    private int _more;
    /// <summary>Calls already shown in each open loop fragment (innermost on top).</summary>
    private readonly Stack<HashSet<string>> _loopSeen = new();
    /// <summary>The last call message, while nothing but its reply came after it.</summary>
    private LastCall? _last;
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

    private sealed class LastCall
    {
        public required string Same { get; init; }
        public required int Edge { get; init; }
        public bool Replied { get; set; }
        public int Count { get; set; } = 1;
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
        _budget = Math.Max(2, _limits.MaxMessages - (method.IsVoid ? 0 : 1));
        var body = _index.WithBody(method) ?? method;
        Walk(body.Member.Body?.Statements ?? [], self, 1, [method.Symbol], new KeyScope("m"));
        if (!method.IsVoid) Message("msg:entry/reply", self, caller, Type(method.Member.TypeName), "reply");

        var notes = new List<string>();
        if (_more > 0) notes.Add($"… {_more} more call{(_more == 1 ? "" : "s")} (stopped at {_limits.MaxMessages} messages).");
        if (_depthCut.Count > 0) notes.Add($"Calls deeper than {_limits.SequenceDepth} level{(_limits.SequenceDepth == 1 ? "" : "s")} are not followed ({string.Join(", ", _depthCut.Take(4))}{(_depthCut.Count > 4 ? ", …" : "")}).");
        if (_recursion.Count > 0) notes.Add($"Recursive calls are shown once ({string.Join(", ", _recursion.Take(4))}).");
        if (method.Member.Body is { Omitted: > 0 } b) notes.Add($"+{b.Omitted} more statements of the method were not read.");
        _plan.Documentation = $"Calls made by {method.Type.Name}.{method.Member.Name} into the scanned code, followed {_limits.SequenceDepth} level{(_limits.SequenceDepth == 1 ? "" : "s")} deep ({method.Type.File}).";
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
            var xs = f.Lifelines.Where(_lifelineNodes.ContainsKey).Select(k => _lifelineNodes[k].Bounds!.Value).ToList();
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
            // Past the cap the walk goes on, only counting the calls it would have shown (without following them).
            var key = scope.Next(KeyScope.For(s));
            var calls = new KeyScope(key + "/c");
            foreach (var c in s.Calls) Call(c, current, depth, stack, calls.Next(c.CallLabel), used: true);
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
                            foreach (var c in next.Calls) Call(c, current, depth, stack, calls.Next(c.CallLabel), used: true);
                            cur = next;
                            continue;
                        }
                        if (cur.Else.Count > 0) arms.Add(("else", cur.Else));
                        break;
                    }
                    Fragment(key, arms.Count == 1 ? "opt" : "alt", arms, current, depth, stack,
                        arms.Select(a => a.Item1 == "else" ? "otherwise" : CodePhrases.Clause(a.Item1)).ToList());
                    break;
                }
                case CodeStatementKind.Switch:
                    Fragment(key, "alt", s.Branches.Select(b => (b.Label.Length == 0 ? "default" : b.Label, b.Body)).ToList(), current, depth, stack,
                        s.Branches.Select(b => $"{CodePhrases.Noun(s.Text)} is {CodePhrases.CaseLabel(b.Label)}").ToList());
                    break;
                case CodeStatementKind.Loop:
                    Fragment(key, "loop", [(s.Text ?? "", s.Body)], current, depth, stack,
                        [CodeNames.Lower(CodePhrases.LoopQuestion(s.Loop, s.Text).TrimEnd('?'))]);
                    break;
                case CodeStatementKind.Try:
                    Walk(s.Body, current, depth, stack, new KeyScope(key + "/try"));
                    foreach (var b in s.Branches)
                        Fragment(key + "/" + b.Label, "break", [(b.Label.Length == 0 ? "catch" : "catch " + b.Label, b.Body)], current, depth, stack,
                            [b.Label.Length == 0 ? "it fails" : "it fails: " + CodePhrases.Fail(b.Label)[6..].Trim()]);
                    Walk(s.Finally, current, depth, stack, new KeyScope(key + "/finally"));
                    break;
                case CodeStatementKind.Using or CodeStatementKind.Lock:
                    Walk(s.Body, current, depth, stack, new KeyScope(key));
                    break;
            }
            // A plain call statement throws its result away; an assignment, return or yield uses it.
            if (s.HasCall) Call(s, current, depth, stack, key, used: s.Kind != CodeStatementKind.Call);
            if (s.Kind is CodeStatementKind.Return or CodeStatementKind.Throw) return;
        }
    }

    /// <param name="shown">The guards in words, by arm (the code guards still key the elements).</param>
    private void Fragment(string key, string op, IReadOnlyList<(string Guard, IReadOnlyList<CodeStatement> Body)> arms, string current, int depth, List<string> stack,
                          IReadOnlyList<string>? shown = null)
    {
        if (_capped)
        {
            // Only counting: nothing more is drawn.
            var counting = new KeyScope(key);
            foreach (var (guard, body) in arms) Walk(body, current, depth, stack, new KeyScope(counting.Next("arm:" + guard)));
            return;
        }
        var startY = _y;
        var startMessages = _messages;
        var startEdges = _plan.Edges.Count;
        var startNodes = _plan.Nodes.Count;
        var startFragments = _fragments.Count;
        var startLifelines = _lifelines.Count;
        var node = new PlannedNode { Key = KeyScope.Stored("frag:" + key), Kind = ElementKind.CombinedFragment };
        node.Fields[FieldKeys.Operator] = op;
        var f = new OpenFragment { Node = node, Top = startY - 6, Depth = _open.Count };
        f.Lifelines.Add(current);
        _open.Push(f);
        if (op == "loop") _loopSeen.Push(new HashSet<string>(StringComparer.Ordinal));
        _y += HeaderGap;
        var operands = new List<(PlannedNode Operand, double Top)>();
        var scope = new KeyScope(key);
        for (var i = 0; i < arms.Count; i++)
        {
            var (guard, body) = arms[i];
            if (i > 0) _y += OperandGap;
            var opNode = new PlannedNode { Key = KeyScope.Stored($"frag:{key}/op{i}"), Kind = ElementKind.InteractionOperand, OwnerKey = node.Key };
            opNode.Fields[FieldKeys.Guard] = CodeBodies.Clip(shown is not null && i < shown.Count && shown[i].Length > 0 ? shown[i] : guard, 60);
            var before = _messages;
            operands.Add((opNode, _y - OperandGap / 2 - 10));
            _last = null;
            Walk(body, current, depth, stack, new KeyScope(scope.Next("arm:" + guard)));
            if (_messages == before) _y += 20;   // room for the empty operand's guard
        }
        if (op == "loop") _loopSeen.Pop();
        _open.Pop();
        _last = null;
        // The cap struck inside: rather than a fragment cut halfway, the whole fragment joins the "… N more calls", when
        // what came before it fills at least half the sequence; otherwise it keeps what fits (or little would be left).
        var cut = _capped && startMessages - 1 >= _budget / 2;
        if (_messages == startMessages || cut)
        {
            if (cut) _more += _plan.Edges.Skip(startEdges).Count(e => e.Fields.GetValueOrDefault(FieldKeys.MessageSort) != "reply");
            _messages -= _plan.Edges.Count - startEdges;
            _y = startY;
            _plan.Edges.RemoveRange(startEdges, _plan.Edges.Count - startEdges);
            _plan.Nodes.RemoveRange(startNodes, _plan.Nodes.Count - startNodes);
            _fragments.RemoveRange(startFragments, _fragments.Count - startFragments);
            foreach (var gone in _lifelines.Skip(startLifelines))
            {
                _lifelineNodes.Remove(gone);
                foreach (var o in _open) o.Lifelines.Remove(gone);
            }
            _lifelines.RemoveRange(startLifelines, _lifelines.Count - startLifelines);
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

    /// <summary>A getter, setter or one-line forwarding method: one statement without control flow. Its call is shown;
    /// what it does inside is not followed.</summary>
    internal static bool IsTrivial(CodeBody body) =>
        body.Omitted == 0 && body.Statements is [var only] && only.Kind is not (CodeStatementKind.If or CodeStatementKind.Switch
            or CodeStatementKind.Loop or CodeStatementKind.Try or CodeStatementKind.Using or CodeStatementKind.Lock);

    private void Call(CodeStatement call, string current, int depth, List<string> stack, string key, bool used)
    {
        if (call.ReceiverType is null || _index.Resolve(call) is not { } callee) return;
        if (_capped) { _more++; return; }
        var target = Target(call, current, callee);
        var reply = used && !callee.IsVoid && target != current;
        // The same call again in a loop is the same message: shown once.
        var same = $"{current}>{target}:{call.Method}";
        if (_loopSeen.Count > 0 && !_loopSeen.Peek().Add(same)) return;
        // The same call right after itself is one message "Method(…) ×n".
        if (_last is { } last && last.Same == same && _plan.Edges.Count - 1 == last.Edge + (last.Replied ? 1 : 0))
        {
            last.Count++;
            _plan.Edges[last.Edge].Name = CodeBodies.Clip($"{call.Method}(…) ×{last.Count}", 60);
            return;
        }
        if (_messages + _reserved + 1 + (reply ? 1 : 0) > _budget)
        {
            _capped = true;
            _more++;
            return;
        }
        var args = call.Arguments is { Length: > 0 } a ? CodeBodies.Clip(a, 30) : "";
        var path = string.Join(">", stack.Skip(1).Select(s => s[(s.LastIndexOf('.', s.IndexOf('(') < 0 ? s.Length - 1 : s.IndexOf('(')) + 1)..])) + "/" + key;
        Message("msg:" + path, current, target, $"{call.Method}({args})", call.IsAwait ? "asynchCall" : "synchCall");
        var mine = new LastCall { Same = same, Edge = _plan.Edges.Count - 1, Replied = reply };
        if (reply) _reserved++;
        _last = null;
        var body = _index.WithBody(callee);
        if (body?.Member.Body is { Statements.Count: > 0 } b && !IsTrivial(b))
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
        if (reply)
        {
            _reserved--;
            Message("msg:" + path + "/reply", target, current, Type(callee.Member.TypeName), "reply");
        }
        // Only a call that nothing but its own reply followed can absorb the next identical one.
        _last = _plan.Edges.Count - 1 == mine.Edge + (reply ? 1 : 0) ? mine : null;
    }
}
