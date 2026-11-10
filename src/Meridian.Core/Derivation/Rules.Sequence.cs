using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Derivation;

/// <summary>
/// Activity → sequence: swimlanes become lifelines; each hand-over between actions in different swimlanes becomes a message
/// named after the action it hands over to; decisions, loops and forks become alt, loop and par fragments.
/// </summary>
internal static class ActivityToSequence
{
    public const double LifelineGap = 240, FirstMessageY = 110, MessageGap = 50;

    /// <summary>A message the activity implies: key, sender and receiver swimlanes, the action it hands over to.</summary>
    internal sealed record Planned(string Key, Element FromLane, Element ToLane, Element Action, ActivityGraph.Handover? Handover);

    /// <summary>Extra room on the sequence diagram: above nested fragment tags, above operand separators, per empty operand, below nested fragments.</summary>
    public const double HeaderGap = 22, SeparatorGap = 24, EmptyOperand = 40, FooterGap = 12;

    /// <summary>One operand the derivation plans: its item key, guard and messages (in diagram order once regrouped).</summary>
    internal sealed record OperandPlan(string Key, string Guard, List<Planned> Messages);

    /// <summary>An alt, par or loop fragment the derivation plans, with its operands.</summary>
    internal sealed class FragmentPlan
    {
        public required string Key { get; init; }
        public required string Operator { get; init; }
        public required List<OperandPlan> Operands { get; init; }
        public required int Order { get; init; }
        public IEnumerable<Planned> All => Operands.SelectMany(o => o.Messages);
        private HashSet<string>? _keys;
        private HashSet<string> Keys => _keys ??= All.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        public int First(IReadOnlyDictionary<string, int> index) => All.Min(p => index[p.Key]);
        public int Last(IReadOnlyDictionary<string, int> index) => All.Max(p => index[p.Key]);

        /// <summary>Does this fragment enclose <paramref name="x"/> (its messages are a subset; for equal sets, the one planned first is outside)?</summary>
        public bool Contains(FragmentPlan x) => x.Keys.IsSubsetOf(Keys) && (x.Keys.Count < Keys.Count || Order < x.Order);
    }

    /// <summary>
    /// Decisions become alt fragments with one operand per outgoing branch (guarded by the branch's guard, "else" when it has
    /// none), forks become par fragments with one operand per branch, back edges become loop fragments. An operand holds the
    /// messages that leave the decision or fork along its branch and those sent from actions only that branch reaches.
    /// </summary>
    internal static List<FragmentPlan> PlanFragments(ModelRepository m, ActivityGraph g, Element activity, IReadOnlyList<Planned> messages)
    {
        var list = new List<FragmentPlan>();
        foreach (var node in activity.OwnedElements.Where(e => e.Kind is ElementKind.DecisionNode or ElementKind.ForkNode).OrderBy(e => g.Rank.GetValueOrDefault(e, int.MaxValue)))
        {
            var outs = m.Outgoing(node).Where(r => r.Kind is ElementKind.ControlFlow or ElementKind.ObjectFlow && !g.BackFlows.Contains(r)).ToList();
            if (outs.Count < 2) continue;   // a merge or a join
            var fork = node.Kind == ElementKind.ForkNode;
            var regions = outs.Select(f => g.Reachable(m, f.Target, node)).ToList();
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            var operands = new List<OperandPlan>();
            for (var i = 0; i < outs.Count; i++)
            {
                var only = regions[i].Where(x => !regions.Where((_, j) => j != i).Any(r => r.Contains(x))).ToHashSet();
                var msgs = messages.Where(p => p.Handover is { Back: false } h && (h.Flows.Contains(outs[i]) || only.Contains(h.From)) && assigned.Add(p.Key)).ToList();
                var guard = outs[i].Get(FieldKeys.Guard);
                operands.Add(new OperandPlan($"fragment:{node.Id}:operand:{outs[i].Id}", fork ? guard : guard.Length > 0 ? guard : "else", msgs));
            }
            if (operands.All(x => x.Messages.Count == 0)) continue;
            if (fork) operands.RemoveAll(x => x.Messages.Count == 0);
            var position = messages.Select((p, i) => (p.Key, i)).ToDictionary(t => t.Key, t => t.i);
            operands = operands.OrderBy(x => x.Messages.Count == 0 ? int.MaxValue : x.Messages.Min(p => position[p.Key])).ToList();
            list.Add(new FragmentPlan { Key = $"fragment:{node.Id}", Operator = fork ? "par" : "alt", Operands = operands, Order = list.Count });
        }
        foreach (var (from, to, guard) in g.BackEdges)
        {
            var lo = g.Rank.GetValueOrDefault(to);
            var hi = g.Rank.GetValueOrDefault(from);
            var covered = messages.Where(p => p.Handover is { } h && g.Rank.GetValueOrDefault(h.From) >= lo && g.Rank.GetValueOrDefault(h.From) <= hi).ToList();
            if (covered.Count == 0) continue;
            var key = $"loop:{from.Id}>{to.Id}";
            list.Add(new FragmentPlan { Key = key, Operator = "loop", Operands = [new OperandPlan(key + ":operand", guard, covered)], Order = list.Count });
        }
        return list;
    }

    /// <summary>
    /// Reorders the messages so that each fragment's operands follow one another, each operand's messages together (outer
    /// fragments first, so nested ones stay inside the operand that holds them). Messages that are in no operand of a fragment
    /// but came after its first message move below it.
    /// </summary>
    internal static List<Planned> Regroup(List<Planned> messages, IReadOnlyList<FragmentPlan> fragments)
    {
        var list = messages.ToList();
        foreach (var f in fragments.OrderByDescending(f => f.All.Count()).ThenBy(f => f.Order))
        {
            var members = f.All.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            var start = list.FindIndex(p => members.Contains(p.Key));
            if (start < 0) continue;
            var position = list.Select((p, i) => (p.Key, i)).ToDictionary(t => t.Key, t => t.i);
            var inside = f.Operands.SelectMany(o => o.Messages.OrderBy(p => position[p.Key])).ToList();
            list = [.. list.Take(start), .. inside, .. list.Skip(start).Where(p => !members.Contains(p.Key))];
        }
        var final = list.Select((p, i) => (p.Key, i)).ToDictionary(t => t.Key, t => t.i);
        foreach (var f in fragments)
            foreach (var op in f.Operands) op.Messages.Sort((a, c) => final[a.Key].CompareTo(final[c.Key]));
        return list;
    }

    internal static List<Planned> Messages(ModelRepository m, ActivityGraph g)
    {
        var list = new List<Planned>();
        // The actor that starts the activity: a message from its lane to the first action's lane.
        var actorLane = g.Partitions.FirstOrDefault(p => m.Find(p.Get(FieldKeys.Represents))?.Kind == ElementKind.Actor);
        if (actorLane != null && g.FirstAction is { } first && g.LaneOf.TryGetValue(first, out var firstLane) && !ReferenceEquals(firstLane, actorLane))
            list.Add(new Planned("msg:start", actorLane, firstLane, first, null));
        foreach (var h in g.Handovers)
            if (g.LaneOf.TryGetValue(h.From, out var a) && g.LaneOf.TryGetValue(h.To, out var b) && !ReferenceEquals(a, b))
                list.Add(new Planned($"msg:{h.From.Id}>{h.To.Id}", a, b, h.To, h));
        // In the order the messages are received: by the run order of the action handed over to (after its sender for a loop
        // back), the opening message from the actor first.
        double When(Planned p) => p.Handover is { Back: true } h ? g.Rank.GetValueOrDefault(h.From) + 0.5 : p.Handover == null ? -1 : g.Rank.GetValueOrDefault(p.Action);
        return list.DistinctBy(p => p.Key).Select((p, i) => (p, i)).OrderBy(t => When(t.p))
                   .ThenBy(t => t.p.Handover is { } h ? g.Rank.GetValueOrDefault(h.From) : -1).ThenBy(t => t.i).Select(t => t.p).ToList();
    }

    public static string Digest(ModelRepository m, Element activity) => new ActivityGraph(m, activity).Digest();

    /// <summary>The block (or actor) a swimlane's lifeline is typed by: what it represents, a block of the same name, or a new block.</summary>
    internal static ItemRef LaneType(ModelRepository m, PlanBuilder b, Element lane, Element package)
    {
        if (m.Find(lane.Get(FieldKeys.Represents)) is { } rep && Metamodel.IsClassifier(rep.Kind)) return rep;
        var name = lane.Name.Length > 0 ? char.ToUpperInvariant(lane.Name[0]) + lane.Name[1..] : "System";
        if (m.OfKind(ElementKind.Block).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) is { } same) return same;
        var key = $"block:{lane.Id}";
        if (b.Plan[key] == null)
            b.Element(key, ElementKind.Block, package, name, null, (FieldKeys.Documentation, $"Created for the swimlane “{lane.DisplayName}” of {lane.Owner?.DisplayName}."));
        return ItemRef.To(key);
    }

    public static void Build(ModelRepository m, PlanBuilder b, DerivationOptions o)
    {
        var activity = b.Plan.Source;
        var g = new ActivityGraph(m, activity);
        var package = AutoDiagrammer.NearestPackage(activity) ?? m.Root;
        if (g.Partitions.Count == 0)
            b.Warn($"“{activity.Name}” has no swimlanes, so there is one lifeline and no messages. Add swimlanes (partitions) for the actors and blocks, then update.");
        var unassigned = g.Actions.Where(a => !g.LaneOf.ContainsKey(a)).ToList();
        if (g.Partitions.Count > 0 && unassigned.Count > 0)
            b.Warn($"{unassigned.Count} action{(unassigned.Count == 1 ? " is" : "s are")} not in a swimlane ({string.Join(", ", unassigned.Take(4).Select(a => a.DisplayName))}); their hand-overs are skipped.");

        var owner = activity.Owner is { } ao && Metamodel.CanOwn(ao.Kind, ElementKind.Interaction) ? ao : package;
        var interaction = b.Element("interaction", ElementKind.Interaction, owner, $"{activity.Name} sequence", null,
            (FieldKeys.Documentation, $"Derived from the activity {activity.DisplayName}."));
        var ia = ItemRef.To(interaction.Key);
        b.Rel("trace", ElementKind.Trace, ia, activity);

        var messages = Messages(m, g);
        var lanes = g.Partitions.Count > 0 ? g.Partitions.ToList() : [];
        var height = FirstMessageY + Math.Max(1, messages.Count) * MessageGap + 50;
        var lifelineX = new Dictionary<Element, double>();
        var types = new Dictionary<Element, ItemRef>();
        if (lanes.Count == 0)
        {
            var only = b.Element("lifeline:system", ElementKind.Lifeline, ia, "system", new PlannedShape("diagram", new Bounds(40, 20, 150, height)));
            var sys = m.OfKind(ElementKind.Block).FirstOrDefault(x => string.Equals(x.Name, "System", StringComparison.OrdinalIgnoreCase)) is { } s ? (ItemRef)s
                : ItemRef.To(b.Element("block:system", ElementKind.Block, package, "System").Key);
            PlanBuilder.SetRef(only, FieldKeys.Type, sys);
        }
        for (var i = 0; i < lanes.Count; i++)
        {
            var lane = lanes[i];
            var type = LaneType(m, b, lane, package);
            types[lane] = type;
            var x = 40 + i * LifelineGap;
            lifelineX[lane] = x + 75;
            var ll = b.Element($"lifeline:{lane.Id}", ElementKind.Lifeline, ia, LifelineName(lane.Name), new PlannedShape("diagram", new Bounds(x, 20, 150, height)));
            PlanBuilder.SetRef(ll, FieldKeys.Type, type);
        }

        // Fragments first (decisions → alt with one operand per branch, forks → par with one operand per branch, back edges → loop),
        // because the messages are then ordered so that each operand's messages are together, and laid out with room for them.
        var fragments = PlanFragments(m, g, activity, messages);
        messages = Regroup(messages, fragments);
        var index = messages.Select((p, i) => (p.Key, i)).ToDictionary(t => t.Key, t => t.i);
        List<FragmentPlan> Inner(FragmentPlan f, Func<FragmentPlan, int> end) =>
            fragments.Where(x => !ReferenceEquals(x, f) && f.Contains(x) && end(x) == end(f)).ToList();

        // Message heights: room above a message for the tags of the fragments starting there (one is already allowed for) and
        // for operand separators; below a fragment's last message for its empty operands.
        var y = new Dictionary<string, double>();
        var cursor = FirstMessageY;
        for (var i = 0; i < messages.Count; i++)
        {
            var starting = fragments.Count(f => f.First(index) == i);
            cursor += Math.Max(0, starting - 1) * HeaderGap;
            cursor += fragments.Sum(f => f.Operands.Skip(1).Count(op => op.Messages.Count > 0 && index[op.Messages[0].Key] == i)) * SeparatorGap;
            y[messages[i].Key] = cursor;
            cursor += MessageGap;
            var ending = fragments.Where(f => f.Last(index) == i).ToList();
            cursor += ending.Sum(f => f.Operands.Count(op => op.Messages.Count == 0)) * EmptyOperand + Math.Max(0, ending.Count - 1) * FooterGap;
        }
        var tall = Math.Max(FirstMessageY + Math.Max(1, messages.Count) * MessageGap, cursor) + 50;
        foreach (var ll in b.Plan.Items.Where(i => i.Kind == ElementKind.Lifeline && i.Shape?.Bounds != null))
            ll.Shape = ll.Shape! with { Bounds = ll.Shape.Bounds!.Value with { Height = tall } };

        // Messages in the order the activity runs.
        for (var i = 0; i < messages.Count; i++)
        {
            var p = messages[i];
            string? n = null;
            var named = o.MessageNames != null && o.MessageNames.TryGetValue(p.Key, out n) && n.Length > 0;
            var name = named ? n! : DeriveText.Identifier(p.Action.Name);
            var msg = b.Rel(p.Key, ElementKind.Message, ItemRef.To($"lifeline:{p.FromLane.Id}"), ItemRef.To($"lifeline:{p.ToLane.Id}"), name,
                (FieldKeys.MessageSort, "synchCall"));
            msg.MessageY = y[p.Key];
            if (named) msg.Note = "named by AI";
        }
        if (o.MessageNames is { Count: > 0 }) b.Plan.AiNote = o.AiNote ?? "Message names written by AI";

        // The fragments around their messages; nested ones a little inside the ones that hold them.
        foreach (var f in fragments)
        {
            var involved = f.All.SelectMany(p => new[] { p.FromLane, p.ToLane }).Distinct().Where(lifelineX.ContainsKey).ToList();
            if (involved.Count == 0) continue;
            var inset = fragments.Count(x => !ReferenceEquals(x, f) && x.Contains(f)) * 12;
            var x0 = involved.Min(l => lifelineX[l]) - 90 + inset;
            var x1 = involved.Max(l => lifelineX[l]) + 90 - inset;
            var firstY = y[messages[f.First(index)].Key];
            var lastY = y[messages[f.Last(index)].Key];
            var innerEnd = Inner(f, x => x.Last(index));
            var empties = f.Operands.Count(op => op.Messages.Count == 0);
            var y0 = firstY - 34 - Inner(f, x => x.First(index)).Count * HeaderGap;
            var emptyAt = lastY + 22 + innerEnd.Sum(x => x.Operands.Count(op => op.Messages.Count == 0)) * EmptyOperand + innerEnd.Count * FooterGap;
            var y1 = emptyAt + empties * EmptyOperand;
            var item = b.Element(f.Key, ElementKind.CombinedFragment, ia, null, new PlannedShape("diagram", new Bounds(x0, y0, x1 - x0, y1 - y0)),
                (FieldKeys.Operator, f.Operator), (FieldKeys.Guard, f.Operands.Count == 1 ? f.Operands[0].Guard : ""));
            if (f.Operands.Count < 2) continue;
            item.Separators = new List<(string, double)>();
            for (var k = 0; k < f.Operands.Count; k++)
            {
                var op = f.Operands[k];
                b.Element(op.Key, ElementKind.InteractionOperand, ItemRef.To(f.Key), null, null, (FieldKeys.Guard, op.Guard));
                if (k == 0) continue;
                double sepY;
                if (op.Messages.Count > 0) sepY = y[op.Messages[0].Key] - 40;
                else { sepY = emptyAt; emptyAt += EmptyOperand; }
                item.Separators.Add((op.Key, Math.Round(sepY - y0)));
            }
        }
        if (fragments.Count > 0)
            b.Warn("Fragments are placed around the messages of each decision branch, fork branch or loop, one operand per branch; check where each operand ends.");
        if (lanes.Count > 0 && messages.Count == 0)
            b.Warn("No action hands over to an action in another swimlane, so the sequence has no messages.");

        // Allocate each action to its swimlane's block.
        foreach (var a in g.Actions)
        {
            if (!g.LaneOf.TryGetValue(a, out var lane) || !types.TryGetValue(lane, out var t)) continue;
            if (t.Element is { } te && (te.Kind == ElementKind.Actor || m.Outgoing(a, ElementKind.Allocate).Any(r => ReferenceEquals(r.Target, te) && !DeriveFields.IsDerived(r)))) continue;
            b.Rel($"allocate:{a.Id}", ElementKind.Allocate, a, t);
        }

        b.Diagram("diagram", DiagramType.Sequence, ia, interaction.Name, autoLayout: false,
            $"The interaction between {string.Join(", ", lanes.Select(l => l.DisplayName))} as the activity {activity.DisplayName} runs.");
    }

    internal static string LifelineName(string laneName) =>
        laneName.Length == 0 ? "system" : char.ToLowerInvariant(laneName[0]) + laneName[1..];
}
