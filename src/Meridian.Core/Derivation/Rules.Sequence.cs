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
        return list.DistinctBy(p => p.Key).ToList();
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

        // Messages in the order the activity runs.
        var y = new Dictionary<string, double>();
        for (var i = 0; i < messages.Count; i++)
        {
            var p = messages[i];
            string? n = null;
            var named = o.MessageNames != null && o.MessageNames.TryGetValue(p.Key, out n) && n.Length > 0;
            var name = named ? n! : DeriveText.Identifier(p.Action.Name);
            var msg = b.Rel(p.Key, ElementKind.Message, ItemRef.To($"lifeline:{p.FromLane.Id}"), ItemRef.To($"lifeline:{p.ToLane.Id}"), name,
                (FieldKeys.MessageSort, "synchCall"));
            msg.MessageY = y[p.Key] = FirstMessageY + i * MessageGap;
            if (named) msg.Note = "named by AI";
        }
        if (o.MessageNames is { Count: > 0 }) b.Plan.AiNote = o.AiNote ?? "Message names written by AI";

        // Fragments: decisions → alt, back edges → loop, forks → par, around the messages they govern.
        var order = messages.Select((p, i) => (p, i)).ToList();
        void Fragment(string key, string op, string guard, IReadOnlyList<int> covered)
        {
            if (covered.Count == 0) return;
            var involved = covered.SelectMany(i => new[] { messages[i].FromLane, messages[i].ToLane }).Distinct().Where(lifelineX.ContainsKey).ToList();
            if (involved.Count == 0) return;
            var x0 = involved.Min(l => lifelineX[l]) - 90;
            var x1 = involved.Max(l => lifelineX[l]) + 90;
            var y0 = covered.Min(i => y[messages[i].Key]) - 34;
            var y1 = covered.Max(i => y[messages[i].Key]) + 22;
            b.Element(key, ElementKind.CombinedFragment, ia, null, new PlannedShape("diagram", new Bounds(x0, y0, x1 - x0, y1 - y0)),
                (FieldKeys.Operator, op), (FieldKeys.Guard, guard));
        }
        foreach (var node in activity.OwnedElements.Where(e => e.Kind is ElementKind.DecisionNode or ElementKind.ForkNode).OrderBy(e => g.Order.GetValueOrDefault(e, int.MaxValue)))
        {
            var covered = order.Where(t => t.p.Handover is { Back: false } h && h.Via.Contains(node)).Select(t => t.i).ToList();
            var outs = m.Outgoing(node).Where(r => r.Kind is ElementKind.ControlFlow or ElementKind.ObjectFlow).ToList();
            if (node.Kind == ElementKind.ForkNode && outs.Count < 2) continue;   // a join
            var guards = string.Join(" / ", outs.Select(r => r.Get(FieldKeys.Guard)).Where(s => s.Length > 0));
            Fragment($"fragment:{node.Id}", node.Kind == ElementKind.ForkNode ? "par" : "alt", guards, covered);
        }
        foreach (var (from, to, guard) in g.BackEdges)
        {
            var lo = g.Order.GetValueOrDefault(to);
            var hi = g.Order.GetValueOrDefault(from);
            var covered = order.Where(t => t.p.Handover is { } h && g.Order.GetValueOrDefault(h.From) >= lo && g.Order.GetValueOrDefault(h.From) <= hi).Select(t => t.i).ToList();
            Fragment($"loop:{from.Id}>{to.Id}", "loop", guard, covered);
        }
        if (b.Plan.Items.Any(i => i.Kind == ElementKind.CombinedFragment))
            b.Warn("Fragments are placed around the messages that leave each decision, fork or loop; check where each operand ends.");
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
