using System.Text;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Derivation;

/// <summary>Use case → activity: the use case's steps become actions in swimlanes for its actors and the system.</summary>
internal static class UseCaseToActivity
{
    public const double LaneWidth = 240, RowHeight = 90, Top = 20, FirstRow = 74;

    public static IReadOnlyList<Element> Actors(ModelRepository m, Element uc) =>
        m.RelationshipsOf(uc).Where(r => r.Kind == ElementKind.Association)
         .Select(r => ReferenceEquals(r.Source, uc) ? r.Target : r.Source).Where(e => e.Kind == ElementKind.Actor).Distinct().ToList();

    /// <summary>The block drawn as the system boundary around the use case on a use case diagram, if any.</summary>
    public static Element? Subject(ModelRepository m, Element uc)
    {
        foreach (var d in m.Diagrams.Where(d => d.DiagramType == DiagramType.UseCase))
            if (d.NodeFor(uc) is { } n)
            {
                var box = (n.ParentId is { } p ? d.FindNode(p) : null)
                          ?? d.Nodes.Where(b => b.Element.Kind is ElementKind.Block or ElementKind.Component && b.Bounds.Contains(n.Bounds.Center))
                                    .OrderBy(b => b.Bounds.Width * b.Bounds.Height).FirstOrDefault();
                if (box?.Element.Kind is ElementKind.Block or ElementKind.Component) return box.Element;
            }
        return null;
    }

    public static string Digest(ModelRepository m, Element uc)
    {
        var sb = new StringBuilder().Append(uc.Name).Append('\n').Append(uc.Documentation).Append('\n');
        foreach (var a in Actors(m, uc).OrderBy(a => a.Id, StringComparer.Ordinal)) sb.Append("actor ").Append(a.Id).Append(' ').Append(a.Name).Append('\n');
        sb.Append("subject ").Append(Subject(m, uc)?.Id);
        return sb.ToString();
    }

    public static void Build(ModelRepository m, PlanBuilder b, DerivationOptions o)
    {
        var uc = b.Plan.Source;
        var actors = Actors(m, uc);
        var subject = Subject(m, uc);
        var steps = o.Steps is { Count: > 0 } given ? UseCaseSteps.Parse(string.Join("\n", given.Select((s, i) => $"{i + 1}. {s}")), actors.Select(a => a.Name).ToList())
                                                    : UseCaseSteps.Parse(uc.Documentation, actors.Select(a => a.Name).ToList());
        if (steps.Count == 0)
        {
            steps = [new UseCaseStep("1", StepKind.Action, uc.Name, null, null)];
            b.Warn(uc.Documentation.Length == 0
                ? $"“{uc.Name}” has no description, so the activity is a skeleton (start → {uc.Name} → end). Write its steps in the use case's documentation (one per line) and update, or fill in with AI."
                : $"No steps were found in the description of “{uc.Name}”, so the activity is a skeleton.");
        }
        if (o.Steps is { Count: > 0 }) b.Plan.AiNote = o.AiNote ?? "Steps written by AI";

        var activity = b.Element("activity", ElementKind.Activity, uc, uc.Name, null,
            (FieldKeys.Documentation, $"Derived from the use case {uc.DisplayName}."));
        var act = ItemRef.To(activity.Key);

        // Swimlanes: one per actor, then the system (the use case's subject block when it has one).
        var lanes = new List<(string Key, string Name, Element? Represents)>();
        foreach (var a in actors) lanes.Add(($"lane:{a.Id}", a.Name, a));
        lanes.Add(("lane:system", subject?.Name ?? "System", subject));
        int LaneIndex(string? actor) => actor == null ? lanes.Count - 1 : Math.Max(0, lanes.FindIndex(l => string.Equals(l.Name, actor, StringComparison.OrdinalIgnoreCase)));

        // Nodes in order (one row each), then the flows between them.
        var nodes = new List<(string Key, ElementKind Kind, string? Name, int Lane)>();
        var flows = new List<(string From, string To, string Guard)>();
        nodes.Add(("initial", ElementKind.InitialNode, null, LaneIndex(steps[0].Actor)));
        var tails = new List<(string Key, string Guard)> { ("initial", "") };
        var finals = new List<(string Key, string Guard)>();
        void Into(string key)
        {
            foreach (var (k, g) in tails) flows.Add((k, key, g));
            tails = [(key, "")];
        }
        foreach (var s in steps)
        {
            var lane = LaneIndex(s.Actor);
            switch (s.Kind)
            {
                case StepKind.If:
                {
                    var prevLane = nodes[^1].Lane;
                    var d = $"decision:{s.Id}";
                    nodes.Add((d, ElementKind.DecisionNode, null, prevLane));
                    Into(d);
                    var a = $"step:{s.Id}";
                    nodes.Add((a, ElementKind.Action, s.Text, lane));
                    flows.Add((d, a, s.Guard ?? ""));
                    tails = [(a, ""), (d, "else")];
                    break;
                }
                case StepKind.Alternate:
                {
                    var prevLane = nodes[^1].Lane;
                    var d = $"decision:{s.Id}";
                    nodes.Add((d, ElementKind.DecisionNode, null, prevLane));
                    Into(d);
                    var a = $"step:{s.Id}";
                    nodes.Add((a, ElementKind.Action, s.Text, lane));
                    flows.Add((d, a, s.Guard ?? "alternate"));
                    finals.Add((a, ""));
                    tails = [(d, "else")];
                    break;
                }
                default:
                {
                    var a = $"step:{s.Id}";
                    nodes.Add((a, ElementKind.Action, s.Text, lane));
                    Into(a);
                    break;
                }
            }
        }
        nodes.Add(("final", ElementKind.ActivityFinalNode, null, nodes[^1].Lane));
        foreach (var (k, g) in tails.Concat(finals)) flows.Add((k, "final", g));

        // Layout: lanes side by side, one row per node.
        var height = FirstRow + nodes.Count * RowHeight;
        for (var i = 0; i < lanes.Count; i++)
        {
            var (key, name, rep) = lanes[i];
            var lane = b.Element(key, ElementKind.Partition, act, name, new PlannedShape("diagram", new Bounds(40 + i * LaneWidth, Top, LaneWidth, height)));
            lane.Fields[FieldKeys.Represents] = rep?.Id ?? "";
        }
        for (var r = 0; r < nodes.Count; r++)
        {
            var (key, kind, name, lane) = nodes[r];
            var (w, h) = Metamodel.DefaultSize(kind);
            if (kind == ElementKind.Action) w = Math.Clamp(Math.Ceiling(((name?.Length ?? 0) * 7.0 + 30) / 10) * 10, 130, LaneWidth - 30);
            var cx = 40 + lane * LaneWidth + LaneWidth / 2;
            var cy = Top + FirstRow + r * RowHeight;
            var bounds = new Bounds(Math.Round(cx - w / 2), Math.Round(cy - h / 2), w, h);
            var item = b.Element(key, kind, act, name, new PlannedShape("diagram", bounds, lanes[lane].Key));
            if (o.Steps is { Count: > 0 } && kind == ElementKind.Action) item.Note = "written by AI";
        }
        foreach (var (from, to, guard) in flows.Distinct())
            b.Rel($"flow:{from}>{to}", ElementKind.ControlFlow, ItemRef.To(from), ItemRef.To(to), null, (FieldKeys.Guard, guard));

        b.Diagram("diagram", DiagramType.Activity, act, uc.Name, autoLayout: false,
            $"The flow of the use case {uc.DisplayName}: its steps, in swimlanes for {string.Join(", ", lanes.Select(l => l.Name))}.");
    }
}
