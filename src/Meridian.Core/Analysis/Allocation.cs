using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

/// <summary>Functional-to-physical allocation derived from activity swimlanes.</summary>
public static class Allocation
{
    /// <summary>
    /// For every swimlane that represents a block/actor, makes sure each action drawn inside it is allocated
    /// to that block (and removes swimlane-derived allocations to other lanes' blocks). One undo step.
    /// Returns (created, removed).
    /// </summary>
    public static (int Created, int Removed) SyncFromSwimlanes(ModelRepository model, Diagram d)
    {
        var lanes = d.Nodes.Where(n => n.Element.Kind == ElementKind.Partition && model.Find(n.Element.Get(FieldKeys.Represents)) != null).ToList();
        var laneTargets = lanes.Select(l => model.Find(l.Element.Get(FieldKeys.Represents))!).ToHashSet();
        int created = 0, removed = 0;
        using (model.History.Transaction("Allocate actions to swimlanes"))
        {
            foreach (var actionNode in d.Nodes.Where(n => n.Element.Kind is ElementKind.Action or ElementKind.ObjectNode))
            {
                var c = actionNode.Bounds.Center;
                var lane = lanes.FirstOrDefault(l => l.Bounds.Contains(c));
                var target = lane != null ? model.Find(lane.Element.Get(FieldKeys.Represents)) : null;
                foreach (var stale in model.Outgoing(actionNode.Element, ElementKind.Allocate).Where(r => laneTargets.Contains(r.Target) && !ReferenceEquals(r.Target, target)).ToList())
                {
                    model.Delete(stale);
                    removed++;
                }
                if (target != null && !model.Outgoing(actionNode.Element, ElementKind.Allocate).Any(r => ReferenceEquals(r.Target, target)))
                {
                    model.Connect(ElementKind.Allocate, actionNode.Element, target);
                    created++;
                }
            }
        }
        return (created, removed);
    }
}
