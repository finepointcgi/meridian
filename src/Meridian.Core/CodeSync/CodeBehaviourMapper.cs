using System.Globalization;
using System.Text;
using Meridian.Core.Derivation;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.CodeSync;

/// <summary>What generating behaviour from code did: element ids added and changed, and warnings.</summary>
public sealed record CodeBehaviourChanges(IReadOnlyList<string> Added, IReadOnlyList<string> Changed, IReadOnlyList<string> Warnings)
{
    /// <summary>Generated behaviours removed because they no longer meet the "Entry points" rules (and nobody touched them).</summary>
    public int TrivialRemoved { get; init; }
}

/// <summary>
/// Behaviour from code: activities and interactions (with their diagrams) generated from method bodies
/// (<see cref="CodeMember.Body"/>). They live in a "Behaviour" package inside the namespace package, carry
/// <c>code.symbol</c> = <c>act:Type.Method(int)</c> / <c>seq:Type.Method(int)</c> and <c>specification</c> = the
/// operation's id, and are updated in place on every sync: contents are diffed by stable keys per statement path, so
/// unchanged statements keep their elements, and the diagram keeps the positions people gave its shapes. Which methods
/// get them follows <see cref="CodeSyncOptions.Behaviour"/>.
/// </summary>
public static class CodeBehaviourMapper
{
    /// <summary>Role of an element made by behaviour from code: "package", "activity", "sequence", "node", "diagram".</summary>
    public const string RoleKey = "code.behaviour";
    /// <summary>Stable key of a node, flow or message inside a generated behaviour.</summary>
    public const string NodeKey = "code.key";
    /// <summary>Hash of what the behaviour says (ignoring layout and lines); unchanged means nothing to do.</summary>
    public const string HashKey = "code.hash";
    /// <summary>Hash of where the diagram's shapes were last put; a different current layout means someone moved them.</summary>
    public const string LayoutKey = "code.layout";
    /// <summary><see cref="LayoutKey"/> of a generated diagram on which someone moved shapes: its layout is kept for good
    /// (and the behaviour is never cleaned up as untouched).</summary>
    public const string MovedLayout = "moved";
    /// <summary>On a generated activity or interaction: id of the operation it is the method of (UML Behavior.specification).</summary>
    public const string SpecificationKey = "specification";
    public const string PackageName = "Behaviour";
    public const string ActivityPrefix = "act:";
    public const string SequencePrefix = "seq:";
    /// <summary>On a generated behaviour: "demand" when someone asked for it ("Generate … from code"); such behaviours are
    /// kept up to date but never cleaned up for not meeting the "Entry points" rules.</summary>
    public const string OriginKey = "code.origin";
    /// <summary>On a sequence generated on demand: the depth it follows calls to (kept by later syncs).</summary>
    public const string DepthKey = "code.depth";

    /// <summary>The depth stored on a sequence generated on demand, if any.</summary>
    public static int? StoredDepth(Element? behaviour) =>
        behaviour != null && int.TryParse(behaviour.Get(DepthKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)
            ? Math.Clamp(d, 1, CodeBehaviourLimits.MaxSequenceDepth) : null;

    public static bool IsBehaviourSymbol(string symbol) =>
        symbol.StartsWith(ActivityPrefix, StringComparison.Ordinal) || symbol.StartsWith(SequencePrefix, StringComparison.Ordinal);

    /// <summary>An element made by behaviour from code (package, behaviour, node, diagram).</summary>
    public static bool IsGenerated(Element e) => e.Get(RoleKey).Length > 0;

    /// <summary>The generated activity or interaction of an operation, if any.</summary>
    public static Element? Find(ModelRepository m, Element operation, bool sequence)
    {
        var symbol = (sequence ? SequencePrefix : ActivityPrefix) + operation.Get(CodeFieldKeys.Symbol);
        var repo = operation.Get(CodeFieldKeys.Repo);
        return m.AllElements.FirstOrDefault(e => e.Kind is ElementKind.Activity or ElementKind.Interaction
            && e.Get(CodeFieldKeys.Symbol) == symbol && e.Get(CodeFieldKeys.Repo) == repo);
    }

    // ================================================================ sync

    /// <summary>
    /// The behaviour pass of a sync (inside its undo step, called by <see cref="CodeModelMapper"/>): keeps every generated
    /// behaviour up to date (removing those whose method is gone, on a full snapshot), and with "Entry points" or "All
    /// public methods" adds new ones within the caps. Nothing happens when the setting is Off.
    /// </summary>
    public static CodeBehaviourChanges Sync(ModelRepository m, CodeModel code, CodeSyncOptions o)
    {
        if (o.Behaviour == CodeBehaviourMode.Off) return new([], [], []);
        var run = new Run(m, code, o);
        run.Sync();
        return run.Result();
    }

    /// <summary>
    /// "Generate activity / sequence from code" for one operation, from a stored code model, as one undo step. Returns the
    /// behaviour's diagram, or null with a reason when the operation's code is not in <paramref name="code"/>.
    /// </summary>
    /// <remarks>A sequence follows calls <paramref name="depth"/> levels deep (1 to <see cref="CodeBehaviourLimits.MaxSequenceDepth"/>;
    /// null keeps the depth it was generated with, else the default) with the longer on-demand message cap; the depth is
    /// kept on the interaction (<see cref="DepthKey"/>) so later syncs keep it. Behaviours made here are marked
    /// <see cref="OriginKey"/> = "demand", so the clean-up of "Entry points" never takes them.</remarks>
    public static (Diagram? Diagram, string Message) Generate(ModelRepository m, CodeModel code, Element operation, bool sequence, CodeSyncOptions o, int? depth = null)
    {
        var symbol = operation.Get(CodeFieldKeys.Symbol);
        var run = new Run(m, code, o);
        if (run.Index.Method(symbol) is not { } method) return (null, $"{operation.DisplayName} is not in the last synced code; sync the repository first.");
        if ((run.Index.WithBody(method) ?? method).Member.Body is null) return (null, $"{operation.DisplayName} has no body in the code (abstract, interface or not read).");
        Element? behaviour;
        using (m.History.Transaction(sequence ? $"Generate sequence of {operation.DisplayName}" : $"Generate activity of {operation.DisplayName}"))
        {
            behaviour = run.Ensure(method, operation, sequence, depth ?? StoredDepth(Find(m, operation, sequence)) ?? o.BehaviourLimits.SequenceDepth);
            if (behaviour != null && behaviour.Get(OriginKey) != "demand") m.Set(behaviour, OriginKey, "demand");
        }
        var d = behaviour?.OwnedElements.OfType<Diagram>().FirstOrDefault();
        return (d, d is null ? "Nothing was generated." : $"{(sequence ? "Sequence" : "Activity")} of {operation.DisplayName} generated from code.");
    }

    /// <summary>
    /// Whether a mode picks <paramref name="cm"/> for an activity (or, with <paramref name="sequence"/>, a sequence), before
    /// the caps. "Entry points": not tests, generated or sample code (<see cref="CodeBehaviourIndex.IsGeneratedOrSample"/>)
    /// or builders (<see cref="CodeBehaviourIndex.IsBuilder"/>); activities for public methods with a branch or loop (or
    /// <see cref="CodeBehaviourLimits.MinCallsForActivity"/> calls inside try / using / lock blocks), sequences for entry
    /// points that call scanned types. "All public methods": every public method (that calls a scanned type, for sequences).
    /// </summary>
    public static bool Qualifies(CodeBehaviourIndex index, CodeMethod cm, CodeBehaviourMode mode, CodeBehaviourLimits limits, bool sequence)
    {
        if (cm.Member.Body is not { } body || body.Statements.Count == 0 || CodeBehaviourIndex.IsTest(cm.Type, cm.Member)) return false;
        var scannedCalls = CodeBodies.Walk(body.Statements).Any(s => s.ReceiverType != null && (s.HasCall || s.Kind == CodeStatementKind.Call));
        if (mode == CodeBehaviourMode.AllPublic) return cm.Member.Visibility == "public" && (!sequence || scannedCalls);
        if (mode != CodeBehaviourMode.EntryPoints) return false;
        if (CodeBehaviourIndex.IsGeneratedOrSample(cm.Type, cm.Member) || CodeBehaviourIndex.IsBuilder(body, limits)) return false;
        if (sequence) return scannedCalls && index.EntryReason(cm) != null;
        if (cm.Member.Visibility != "public") return false;
        var (branches, loops, calls) = CodeBodies.Shape(body.Statements);
        return branches + loops > 0
               || calls >= limits.MinCallsForActivity && CodeBodies.Walk(body.Statements).Any(s => s.Kind is CodeStatementKind.Try or CodeStatementKind.Using or CodeStatementKind.Lock);
    }

    /// <summary>The methods a mode picks, with the caps applied (new ones only; existing behaviours are always kept).</summary>
    public static (List<CodeMethod> Activities, List<CodeMethod> Sequences, string? Summary) Select(
        CodeBehaviourIndex index, CodeBehaviourMode mode, CodeBehaviourLimits limits, Func<CodeMethod, bool>? eligible = null,
        ISet<string>? existingActivities = null, ISet<string>? existingSequences = null)
    {
        if (mode is not (CodeBehaviourMode.EntryPoints or CodeBehaviourMode.AllPublic)) return ([], [], null);
        existingActivities ??= new HashSet<string>();
        existingSequences ??= new HashSet<string>();
        var acts = new List<(CodeMethod M, int Score)>();
        var seqs = new List<(CodeMethod M, int Score)>();
        foreach (var cm in index.Methods.OrderBy(x => x.Symbol, StringComparer.Ordinal))
        {
            if (cm.Member.Body is not { } body || body.Statements.Count == 0 || eligible?.Invoke(cm) == false) continue;
            var (branches, loops, calls) = CodeBodies.Shape(body.Statements);
            var score = branches * 3 + loops * 3 + calls;
            if (Qualifies(index, cm, mode, limits, sequence: false)) acts.Add((cm, score));
            if (Qualifies(index, cm, mode, limits, sequence: true)) seqs.Add((cm, score));
        }
        var perNsAct = mode == CodeBehaviourMode.EntryPoints ? limits.ActivitiesPerNamespace : int.MaxValue;
        var perNsSeq = mode == CodeBehaviourMode.EntryPoints ? limits.SequencesPerNamespace : int.MaxValue;
        List<(CodeMethod M, int Score, bool Seq)> Capped(List<(CodeMethod M, int Score)> list, int perNs, ISet<string> existing, bool seq) =>
            list.GroupBy(x => x.M.Type.Namespace, StringComparer.Ordinal)
                .SelectMany(g =>
                {
                    var have = g.Count(x => existing.Contains(x.M.Symbol));
                    return g.Where(x => !existing.Contains(x.M.Symbol)).OrderByDescending(x => x.Score).ThenBy(x => x.M.Symbol, StringComparer.Ordinal)
                        .Take(Math.Max(0, perNs == int.MaxValue ? int.MaxValue : perNs - have));
                })
                .Select(x => (x.M, x.Score, seq)).ToList();
        var newActs = Capped(acts, perNsAct, existingActivities, false);
        var newSeqs = Capped(seqs, perNsSeq, existingSequences, true);
        var room = Math.Max(0, limits.MaxTotal - existingActivities.Count - existingSequences.Count);
        var chosen = newActs.Concat(newSeqs).OrderByDescending(x => x.Score).ThenBy(x => x.Seq).ThenBy(x => x.M.Symbol, StringComparer.Ordinal).Take(room).ToList();
        var a = chosen.Where(x => !x.Seq).Select(x => x.M).OrderBy(x => x.Symbol, StringComparer.Ordinal).ToList();
        var s = chosen.Where(x => x.Seq).Select(x => x.M).OrderBy(x => x.Symbol, StringComparer.Ordinal).ToList();
        var skippedA = acts.Count(x => !existingActivities.Contains(x.M.Symbol)) - a.Count;
        var skippedS = seqs.Count(x => !existingSequences.Contains(x.M.Symbol)) - s.Count;
        string? summary = null;
        if (skippedA + skippedS > 0)
            summary = $"Behaviour diagrams: {a.Count} new activit{(a.Count == 1 ? "y" : "ies")} and {s.Count} new sequence{(s.Count == 1 ? "" : "s")}; "
                      + $"{skippedA} activit{(skippedA == 1 ? "y" : "ies")} and {skippedS} sequence{(skippedS == 1 ? "" : "s")} skipped by the caps"
                      + (mode == CodeBehaviourMode.EntryPoints ? $" ({limits.ActivitiesPerNamespace} activities and {limits.SequencesPerNamespace} sequences per namespace, {limits.MaxTotal} in total)" : $" ({limits.MaxTotal} in total)")
                      + ". Use “Generate activity from code” on an operation for the others.";
        return (a, s, summary);
    }

    private sealed class Run(ModelRepository m, CodeModel code, CodeSyncOptions o)
    {
        public CodeBehaviourIndex Index { get; } = new(code);
        private readonly List<string> _added = [];
        private readonly List<string> _changed = [];
        private readonly List<string> _warnings = [];
        private Dictionary<string, Element>? _symbols;

        private int _trivialRemoved;

        public CodeBehaviourChanges Result() =>
            new(_added.Where(id => m.Find(id) != null).Distinct().ToList(),
                _changed.Where(id => m.Find(id) != null && !_added.Contains(id)).Distinct().ToList(), _warnings) { TrivialRemoved = _trivialRemoved };

        /// <summary>Elements of this repository by code symbol.</summary>
        private Dictionary<string, Element> Symbols()
        {
            if (_symbols != null) return _symbols;
            _symbols = new Dictionary<string, Element>(StringComparer.Ordinal);
            foreach (var e in m.Root.Descendants())
                if (e.Get(CodeFieldKeys.Repo) == o.RepoId && e.Get(CodeFieldKeys.Symbol) is { Length: > 0 } s) _symbols.TryAdd(s, e);
            return _symbols;
        }

        private Element? Operation(string symbol) => Symbols().TryGetValue(symbol, out var e) && e.Kind == ElementKind.Operation ? e : null;
        private Element? TypeElement(string symbol) => Symbols().TryGetValue(symbol, out var e) && e is not Relationship ? e : null;

        public void Sync()
        {
            var existing = Symbols().Where(kv => IsBehaviourSymbol(kv.Key) && kv.Value.Kind is ElementKind.Activity or ElementKind.Interaction)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            var acts = new HashSet<string>(StringComparer.Ordinal);
            var seqs = new HashSet<string>(StringComparer.Ordinal);
            var inCode = code.Types.Select(t => t.Symbol).ToHashSet(StringComparer.Ordinal);
            CodeSyncDiagrams.References? references = null;
            foreach (var (symbol, e) in existing)
            {
                var sequence = symbol.StartsWith(SequencePrefix, StringComparison.Ordinal);
                var opSymbol = symbol[(sequence ? SequencePrefix : ActivityPrefix).Length..];
                if (Index.Method(opSymbol) is { } method && Operation(opSymbol) is { } op)
                {
                    // Made by "Entry points" before its rules got stricter (sample code, builders, straight runs of calls):
                    // removed while nobody touched it, kept otherwise.
                    if (o.Behaviour == CodeBehaviourMode.EntryPoints && e.Get(OriginKey) != "demand" && !Qualifies(Index, method, o.Behaviour, o.BehaviourLimits, sequence)
                        && Untouched(e, references ??= new CodeSyncDiagrams.References(m)))
                    {
                        m.Delete(e);
                        _trivialRemoved++;
                        continue;
                    }
                    (sequence ? seqs : acts).Add(opSymbol);
                    Ensure(method, op, sequence, StoredDepth(e));
                    continue;
                }
                (sequence ? seqs : acts).Add(opSymbol);
                // The method is gone (on a partial push only when its type was read and lacks it).
                var typeSymbol = CodeStep(opSymbol);
                if (!o.RemoveDeleted && !inCode.Contains(typeSymbol)) continue;
                Remove(e);
            }
            var (newActs, newSeqs, summary) = Select(Index, o.Behaviour, o.BehaviourLimits, cm => Operation(cm.Symbol) != null, acts, seqs);
            foreach (var cm in newActs) Ensure(cm, Operation(cm.Symbol)!, false);
            foreach (var cm in newSeqs) Ensure(cm, Operation(cm.Symbol)!, true);
            if (summary != null) _warnings.Add(summary);
            foreach (var pkg in m.Root.Descendants().Where(e => e.Kind == ElementKind.Package && e.Get(RoleKey) == "package" && e.Get(CodeFieldKeys.Repo) == o.RepoId && e.OwnedElements.Count == 0).ToList())
                m.Delete(pkg);
        }

        /// <summary>"Ns.Type" of "Ns.Type.Method(int)".</summary>
        private static string CodeStep(string op)
        {
            var paren = op.IndexOf('(');
            var head = paren < 0 ? op : op[..paren];
            var dot = head.LastIndexOf('.');
            return dot < 0 ? "" : head[..dot];
        }

        private void Remove(Element behaviour)
        {
            var handMade = behaviour.Descendants().Any(x => x is not Diagram && !IsGenerated(x));
            if (handMade || !o.RemoveDeleted)
            {
                if (behaviour.Get(CodeModelMapper.RemovedKey) != "true")
                {
                    m.Set(behaviour, CodeModelMapper.RemovedKey, "true");
                    _changed.Add(behaviour.Id);
                }
                return;
            }
            m.Delete(behaviour);
        }

        /// <summary>The Behaviour package of the namespace package that holds <paramref name="op"/>.</summary>
        private Element Package(Element op)
        {
            var ns = AutoDiagrammer.NearestPackage(op) ?? m.Root;
            var pkg = ns.OwnedElements.FirstOrDefault(c => c.Kind == ElementKind.Package && c.Get(RoleKey) == "package" && c.Get(CodeFieldKeys.Repo) == o.RepoId);
            if (pkg != null) return pkg;
            pkg = m.Create(ns, ElementKind.Package, PackageName);
            m.Set(pkg, RoleKey, "package");
            m.Set(pkg, CodeFieldKeys.Repo, o.RepoId);
            _added.Add(pkg.Id);
            return pkg;
        }

        /// <summary>
        /// A generated behaviour nobody touched: only generated elements, one diagram laid out where code sync last put it
        /// (<see cref="LayoutKey"/>) showing only them, and nothing else in the model refers to it.
        /// </summary>
        private bool Untouched(Element behaviour, CodeSyncDiagrams.References references)
        {
            if (behaviour.Get(CodeModelMapper.RemovedKey).Length > 0) return false;
            var diagrams = behaviour.Descendants().OfType<Diagram>().ToList();
            if (diagrams is not [var d] || d.Get(LayoutKey) is not { Length: > 0 } stored || stored != LayoutHash(d)) return false;
            if (behaviour.Descendants().Any(x => x is not Diagram && !IsGenerated(x))) return false;
            if (d.Nodes.Any(n => !IsGenerated(n.Element) || !behaviour.IsAncestorOf(n.Element))) return false;
            return !references.Mentions(behaviour) && !references.Mentions(d);
        }

        /// <summary>Creates or updates the activity (or interaction) of a method and its diagram. <paramref name="depth"/>:
        /// a sequence generated on demand, with that depth and the on-demand message cap.</summary>
        public Element? Ensure(CodeMethod method, Element op, bool sequence, int? depth = null)
        {
            var limits = sequence && depth is { } dd ? o.BehaviourLimits.ForDemand(dd) : o.BehaviourLimits;
            var plan = sequence
                ? CodeSequencePlanner.Plan(Index, method, TypeElement, limits)
                : CodeActivityPlanner.Plan(Index, Index.WithBody(method) is { } b && b.Member.Body != null && method.Member.Body == null ? b : method, Operation, limits);
            var hash = plan.Hash();
            var symbols = Symbols();
            symbols.TryGetValue(plan.Symbol, out var behaviour);
            if (behaviour != null && !m.Contains(behaviour)) behaviour = null;
            var file = method.Type.File;
            var line = (method.Member.Line > 0 ? method.Member.Line : method.Type.Line).ToString(CultureInfo.InvariantCulture);
            if (behaviour != null && behaviour.Kind != plan.Kind) { m.Delete(behaviour); behaviour = null; }
            if (behaviour == null)
            {
                behaviour = m.Create(Package(op), plan.Kind, plan.Name);
                m.Set(behaviour, CodeFieldKeys.Symbol, plan.Symbol);
                m.Set(behaviour, CodeFieldKeys.Repo, o.RepoId);
                m.Set(behaviour, RoleKey, sequence ? "sequence" : "activity");
                symbols[plan.Symbol] = behaviour;
                _added.Add(behaviour.Id);
            }
            else if (behaviour.Get(HashKey) == hash && behaviour.OwnedElements.OfType<Diagram>().Any())
            {
                // Same behaviour: only the location is kept current (not a change).
                m.Set(behaviour, CodeFieldKeys.File, file);
                m.Set(behaviour, CodeFieldKeys.Line, line);
                if (behaviour.Get(CodeModelMapper.RemovedKey).Length > 0) { m.Set(behaviour, CodeModelMapper.RemovedKey, null); _changed.Add(behaviour.Id); }
                StampDepth(behaviour, sequence, depth);
                return behaviour;
            }
            else _changed.Add(behaviour.Id);

            if (behaviour.Name != plan.Name) m.Rename(behaviour, plan.Name);
            m.Set(behaviour, FieldKeys.Documentation, plan.Documentation);
            m.Set(behaviour, SpecificationKey, op.Id);
            m.Set(behaviour, HashKey, hash);
            m.Set(behaviour, CodeFieldKeys.File, file);
            m.Set(behaviour, CodeFieldKeys.Line, line);
            m.Set(behaviour, CodeFieldKeys.Commit, o.Commit);
            m.Set(behaviour, CodeModelMapper.RemovedKey, null);
            StampDepth(behaviour, sequence, depth);
            Apply(behaviour, plan);
            return behaviour;
        }

        private void StampDepth(Element behaviour, bool sequence, int? depth)
        {
            var want = sequence && depth is { } d ? Math.Clamp(d, 1, CodeBehaviourLimits.MaxSequenceDepth).ToString(CultureInfo.InvariantCulture) : null;
            if (want != null && behaviour.Get(DepthKey) != want) m.Set(behaviour, DepthKey, want);
        }

        private void Stamp(Element e, string key)
        {
            m.Set(e, RoleKey, "node");
            m.Set(e, NodeKey, key);
            m.Set(e, CodeFieldKeys.Repo, o.RepoId);
        }

        private bool Write(Element e, string name, IReadOnlyDictionary<string, string> fields, IEnumerable<string> owned)
        {
            var changed = false;
            if (name.Length > 0 && e.Name != name) { m.Rename(e, name); changed = true; }
            foreach (var k in owned)
            {
                var want = fields.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
                if ((want ?? "") != e.Get(k)) { m.Set(e, k, want); changed = true; }
            }
            return changed;
        }

        private static readonly string[] OwnedFields =
            [FieldKeys.Guard, FieldKeys.Operator, FieldKeys.MessageSort, FieldKeys.Type, FieldKeys.Stereotypes, FieldKeys.Body, CodeActivityPlanner.OperationField, FieldKeys.Behavior];

        /// <summary>The fields a planned node sets: the documentation too when it has one (summary actions list what they hold),
        /// so documentation people write on other generated nodes stays.</summary>
        private static IEnumerable<string> Owned(PlannedNode n) => n.Fields.ContainsKey(FieldKeys.Documentation) ? OwnedFields.Append(FieldKeys.Documentation) : OwnedFields;

        /// <summary>Brings the behaviour's contents and diagram in line with the plan, matching by key.</summary>
        private void Apply(Element behaviour, BehaviourPlan plan)
        {
            var nodes = new Dictionary<string, Element>(StringComparer.Ordinal);
            var edges = new Dictionary<string, Relationship>(StringComparer.Ordinal);
            foreach (var e in behaviour.Descendants().Where(e => e is not Diagram && e is not Relationship))
            {
                if (e.Get(NodeKey) is { Length: > 0 } k) nodes.TryAdd(k, e);
                foreach (var r in m.Outgoing(e)) if (r.Get(NodeKey) is { Length: > 0 } rk) edges.TryAdd(rk, r);
            }

            // Relationships that go, then nodes that go.
            var wantedEdges = plan.Edges.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            var wantedNodes = plan.Nodes.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var (k, r) in edges.Where(kv => !wantedEdges.Contains(kv.Key)).ToList()) { if (m.Contains(r)) m.Delete(r); edges.Remove(k); }
            foreach (var (k, e) in nodes.Where(kv => !wantedNodes.Contains(kv.Key) || plan.Nodes.First(n => n.Key == kv.Key).Kind != kv.Value.Kind).ToList())
            {
                if (m.Contains(e)) m.Delete(e);
                nodes.Remove(k);
            }

            foreach (var n in plan.Nodes)
            {
                var owner = n.OwnerKey != null && nodes.TryGetValue(n.OwnerKey, out var oe) ? oe : behaviour;
                if (nodes.TryGetValue(n.Key, out var e) && m.Contains(e))
                {
                    if (!ReferenceEquals(e.Owner, owner)) m.Move(e, owner);
                    if (Write(e, n.Name, n.Fields, Owned(n))) _changed.Add(e.Id);
                    continue;
                }
                e = m.Create(owner, n.Kind, n.Name.Length > 0 ? n.Name : null);
                Write(e, n.Name, n.Fields, Owned(n));
                Stamp(e, n.Key);
                nodes[n.Key] = e;
                _added.Add(e.Id);
            }
            foreach (var x in plan.Edges)
            {
                if (!nodes.TryGetValue(x.From, out var s) || !nodes.TryGetValue(x.To, out var t)) continue;
                if (edges.TryGetValue(x.Key, out var r) && m.Contains(r))
                {
                    if (ReferenceEquals(r.Source, s) && ReferenceEquals(r.Target, t) && r.Kind == x.Kind)
                    {
                        if (Write(r, x.Name, x.Fields, OwnedFields)) _changed.Add(r.Id);
                        continue;
                    }
                    m.Delete(r);
                }
                r = m.Connect(x.Kind, s, t);
                Write(r, x.Name, x.Fields, OwnedFields);
                Stamp(r, x.Key);
                edges[x.Key] = r;
                _added.Add(r.Id);
            }
            Draw(behaviour, plan, nodes, edges);
        }

        private static string LayoutHash(Diagram d) =>
            DeriveFields.Hash(string.Join("\n", d.Nodes.Select(n => $"{n.Element.Get(NodeKey)} {n.Bounds.X:0.#} {n.Bounds.Y:0.#} {n.Bounds.Width:0.#} {n.Bounds.Height:0.#}")
                .OrderBy(s => s, StringComparer.Ordinal)));

        private void Draw(Element behaviour, BehaviourPlan plan, Dictionary<string, Element> nodes, Dictionary<string, Relationship> edges)
        {
            var d = behaviour.OwnedElements.OfType<Diagram>().FirstOrDefault(x => x.Get(RoleKey) == "diagram") ?? behaviour.OwnedElements.OfType<Diagram>().FirstOrDefault();
            if (d == null)
            {
                d = m.CreateDiagram(behaviour, plan.DiagramType, plan.Name);
                m.Set(d, RoleKey, "diagram");
                m.Set(d, CodeFieldKeys.Repo, o.RepoId);
                _added.Add(d.Id);
            }
            else if (d.Name != plan.Name) m.Rename(d, plan.Name);
            // Shapes keep where people put them; a diagram nobody moved is laid out afresh.
            // Once moved, always moved: the diagram's layout is someone's from then on.
            var moved = d.Get(LayoutKey) is { Length: > 0 } stored && (stored == MovedLayout || stored != LayoutHash(d));
            var bounds = new Dictionary<DiagramNode, Bounds>();
            var fresh = new List<DiagramNode>();
            foreach (var n in plan.Nodes)
            {
                if (n.Bounds is not { } b || !nodes.TryGetValue(n.Key, out var e)) continue;
                if (d.NodeFor(e) is { } node)
                {
                    var keep = moved && n.Kind is not (ElementKind.CombinedFragment or ElementKind.Comment);
                    var want = keep ? (n.Kind == ElementKind.Lifeline ? node.Bounds with { Height = b.Height } : node.Bounds) : b;
                    if (want != node.Bounds) bounds[node] = want;
                }
                else fresh.Add(m.AddNode(d, e, b, showRelationships: false));
            }
            if (bounds.Count > 0) m.SetBounds(d, bounds, "Code sync: behaviour layout");
            foreach (var n in plan.Nodes.Where(n => n.Kind == ElementKind.CombinedFragment))
                if (nodes.TryGetValue(n.Key, out var fe) && d.NodeFor(fe) is { } fn)
                    foreach (var (opKey, offset) in n.Separators)
                        if (nodes.TryGetValue(opKey, out var opEl))
                        {
                            var v = InteractionOperands.Format(offset);
                            if (fn.Style.GetValueOrDefault(InteractionOperands.StyleKey(opEl)) != v) m.SetNodeStyle(d, fn, InteractionOperands.StyleKey(opEl), v);
                        }
            foreach (var x in plan.Edges)
            {
                if (!edges.TryGetValue(x.Key, out var r)) continue;
                var edge = d.EdgeFor(r) ?? m.AddEdge(d, r);
                if (edge != null && x.MessageY is { } y && (edge.Waypoints.Count != 1 || Math.Abs(edge.Waypoints[0].Y - y) > 0.01))
                    m.SetWaypoints(d, edge, [new Pt(0, y)]);
            }
            EdgeRouter.Invalidate(d);
            m.Set(d, LayoutKey, moved ? MovedLayout : LayoutHash(d));
        }
    }
}
