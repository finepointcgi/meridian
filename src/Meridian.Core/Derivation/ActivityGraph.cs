using System.Text;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Derivation;

/// <summary>
/// Reads an activity the way the activity → sequence and activity → state machine derivations need it: which swimlane each
/// action is in, the order actions run in (depth-first from the initial node), and every hand-over from one action to the
/// next through decisions, forks and merges, with the control nodes passed on the way and whether it loops back.
/// </summary>
internal sealed class ActivityGraph
{
    /// <param name="Flows">The flows taken from <paramref name="From"/> to <paramref name="To"/>, in order.</param>
    public sealed record Handover(Element From, Element To, IReadOnlyList<Element> Via, IReadOnlyList<string> Guards, bool Back, IReadOnlyList<Relationship> Flows);

    public Element Activity { get; }
    public IReadOnlyList<Element> Partitions { get; }
    public IReadOnlyDictionary<Element, Element> LaneOf { get; }
    public IReadOnlyList<Element> Actions { get; }
    public IReadOnlyDictionary<Element, int> Order { get; }

    /// <summary>
    /// The order nodes run in: topological over the forward flows (back edges ignored), ties broken by <see cref="Order"/>. Unlike the
    /// depth-first order, everything on one branch of a decision or fork comes before what follows the merge or join.
    /// </summary>
    public IReadOnlyDictionary<Element, int> Rank { get; }

    /// <summary>Flows that close a loop.</summary>
    public IReadOnlySet<Relationship> BackFlows { get; }
    public IReadOnlyList<Handover> Handovers { get; }

    /// <summary>The first action after the initial node, if any.</summary>
    public Element? FirstAction { get; }

    /// <summary>Back edges found by the depth-first walk: (from, to).</summary>
    public IReadOnlyList<(Element From, Element To, string Guard)> BackEdges { get; }

    private static IEnumerable<Relationship> Flows(ModelRepository m, Element n) =>
        m.Outgoing(n).Where(r => r.Kind is ElementKind.ControlFlow or ElementKind.ObjectFlow);

    public ActivityGraph(ModelRepository m, Element activity)
    {
        Activity = activity;
        Partitions = activity.OwnedElements.Where(e => e.Kind == ElementKind.Partition).ToList();
        LaneOf = Lanes(m, activity, Partitions);
        var nodes = activity.OwnedElements.Where(e => Metamodel.IsActivityNode(e.Kind)).ToList();

        // Depth-first from the initial nodes (then any unreached node), in model order: visit order and back edges.
        var order = new Dictionary<Element, int>();
        var state = new Dictionary<Element, int>();
        var back = new List<(Element, Element, string)>();
        var backSet = new HashSet<Relationship>();
        void Visit(Element n)
        {
            state[n] = 1;
            order[n] = order.Count;
            foreach (var f in Flows(m, n))
            {
                if (!state.TryGetValue(f.Target, out var s)) Visit(f.Target);
                else if (s == 1) { back.Add((n, f.Target, f.Get(FieldKeys.Guard))); backSet.Add(f); }
            }
            state[n] = 2;
        }
        foreach (var start in nodes.Where(n => n.Kind == ElementKind.InitialNode).Concat(nodes))
            if (!state.ContainsKey(start)) Visit(start);
        Order = order;
        BackEdges = back;
        BackFlows = backSet;
        Rank = Topological(m, nodes, order, backSet);
        Actions = nodes.Where(n => n.Kind is ElementKind.Action or ElementKind.InteractionUse).OrderBy(n => order.GetValueOrDefault(n, int.MaxValue)).ToList();

        // Hand-overs: from each action, through control nodes, to the next actions.
        var list = new List<Handover>();
        foreach (var a in Actions) list.AddRange(Next(m, a, backSet));
        Handovers = list.OrderBy(h => order.GetValueOrDefault(h.From)).ThenBy(h => h.Back ? 1 : 0).ThenBy(h => order.GetValueOrDefault(h.To)).ToList();
        var initial = nodes.FirstOrDefault(n => n.Kind == ElementKind.InitialNode);
        FirstAction = initial != null ? Next(m, initial, backSet).Select(h => h.To).FirstOrDefault() : Actions.FirstOrDefault();
    }

    private static IEnumerable<Handover> Next(ModelRepository m, Element from, HashSet<Relationship> backSet)
    {
        var seen = new HashSet<Element>();
        var queue = new Queue<(Element Node, List<Element> Via, List<string> Guards, bool Back, List<Relationship> Path)>();
        foreach (var f in Flows(m, from)) queue.Enqueue((f.Target, [], f.Get(FieldKeys.Guard) is { Length: > 0 } g ? [g] : [], backSet.Contains(f), [f]));
        while (queue.Count > 0)
        {
            var (n, via, guards, isBack, path) = queue.Dequeue();
            if (n.Kind is ElementKind.Action or ElementKind.InteractionUse) { yield return new Handover(from, n, via, guards, isBack, path); continue; }
            if (n.Kind == ElementKind.ActivityFinalNode || !seen.Add(n)) continue;
            foreach (var f in Flows(m, n))
                queue.Enqueue((f.Target, [.. via, n], f.Get(FieldKeys.Guard) is { Length: > 0 } g ? [.. guards, g] : guards, isBack || backSet.Contains(f), [.. path, f]));
        }
    }

    /// <summary>Kahn's algorithm over the forward flows, always taking the ready node visited first by the depth-first walk.</summary>
    private static Dictionary<Element, int> Topological(ModelRepository m, IReadOnlyList<Element> nodes, IReadOnlyDictionary<Element, int> order, HashSet<Relationship> back)
    {
        var set = nodes.ToHashSet();
        var indegree = nodes.ToDictionary(n => n, _ => 0);
        foreach (var n in nodes)
            foreach (var f in Flows(m, n).Where(f => !back.Contains(f) && set.Contains(f.Target)))
                indegree[f.Target]++;
        var ready = new PriorityQueue<Element, int>();
        foreach (var n in nodes.Where(n => indegree[n] == 0)) ready.Enqueue(n, order.GetValueOrDefault(n, int.MaxValue));
        var rank = new Dictionary<Element, int>();
        while (ready.TryDequeue(out var n, out _))
        {
            rank[n] = rank.Count;
            foreach (var f in Flows(m, n).Where(f => !back.Contains(f) && set.Contains(f.Target)))
                if (--indegree[f.Target] == 0) ready.Enqueue(f.Target, order.GetValueOrDefault(f.Target, int.MaxValue));
        }
        foreach (var n in nodes.Where(n => !rank.ContainsKey(n)).OrderBy(n => order.GetValueOrDefault(n, int.MaxValue))) rank[n] = rank.Count;
        return rank;
    }

    /// <summary>
    /// The nodes reachable from <paramref name="start"/> over forward flows without passing through <paramref name="stop"/>
    /// (the decision or fork whose branch this is).
    /// </summary>
    public IReadOnlySet<Element> Reachable(ModelRepository m, Element start, Element stop)
    {
        var seen = new HashSet<Element>();
        var stack = new Stack<Element>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (ReferenceEquals(n, stop) || !seen.Add(n)) continue;
            foreach (var f in Flows(m, n).Where(f => !BackFlows.Contains(f))) stack.Push(f.Target);
        }
        return seen;
    }

    /// <summary>
    /// The swimlane of each action: nesting or containment on the activity's diagrams first, then an «allocate» to the
    /// element a swimlane represents.
    /// </summary>
    public static Dictionary<Element, Element> Lanes(ModelRepository m, Element activity, IReadOnlyList<Element> partitions)
    {
        var result = new Dictionary<Element, Element>();
        if (partitions.Count == 0) return result;
        foreach (var d in m.Diagrams.Where(d => d.Nodes.Any(n => ReferenceEquals(n.Element.Owner, activity))))
        {
            var lanes = d.Nodes.Where(n => n.Element.Kind == ElementKind.Partition && ReferenceEquals(n.Element.Owner, activity)).ToList();
            if (lanes.Count == 0) continue;
            foreach (var n in d.Nodes.Where(n => ReferenceEquals(n.Element.Owner, activity) && Metamodel.IsActivityNode(n.Element.Kind)))
            {
                if (result.ContainsKey(n.Element)) continue;
                DiagramNode? lane = null;
                for (var p = d.ParentOf(n); p != null && lane == null; p = d.ParentOf(p)) if (lanes.Contains(p)) lane = p;
                lane ??= lanes.Where(l => l.Bounds.Contains(n.Bounds.Center)).OrderBy(l => l.Bounds.Width * l.Bounds.Height).FirstOrDefault();
                if (lane != null) result[n.Element] = lane.Element;
            }
        }
        foreach (var a in activity.OwnedElements.Where(e => e.Kind == ElementKind.Action && !result.ContainsKey(e)))
            foreach (var r in m.Outgoing(a, ElementKind.Allocate))
                if (partitions.FirstOrDefault(p => p.Get(FieldKeys.Represents) == r.Target.Id) is { } lane) { result[a] = lane; break; }
        return result;
    }

    /// <summary>Canonical text of everything the derivations read (hashed into the source fingerprint).</summary>
    public string Digest()
    {
        var sb = new StringBuilder();
        sb.Append(Activity.Name).Append('\n');
        foreach (var p in Partitions) sb.Append("P ").Append(p.Id).Append(' ').Append(p.Name).Append(' ').Append(p.Get(FieldKeys.Represents)).Append('\n');
        foreach (var a in Actions) sb.Append("A ").Append(a.Id).Append(' ').Append(a.Name).Append(' ').Append(LaneOf.TryGetValue(a, out var l) ? l.Id : "").Append('\n');
        foreach (var h in Handovers) sb.Append("H ").Append(h.From.Id).Append('>').Append(h.To.Id).Append(' ').Append(string.Join("|", h.Via.Select(v => v.Id)))
                                         .Append(' ').Append(string.Join("|", h.Guards)).Append(h.Back ? " back" : "").Append('\n');
        if (FirstAction != null) sb.Append("F ").Append(FirstAction.Id).Append('\n');
        return sb.ToString();
    }
}
